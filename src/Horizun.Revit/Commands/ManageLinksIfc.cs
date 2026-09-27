// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_links add kind=ifc: link an IFC the way Revit's own importer does
// in its IFCImportAction.Link branch (the sequence RevitAPI names under
// RevitLinkType.CreateFromIFC), but with every step aimed at THE HOST:
//   1. Application.OpenIFCDocument with Action=Open, Intent=Reference - the IFC is
//      imported by reference into a NEW document Revit creates for it;
//   2. that document is saved as "<file>.ifc.RVT" and closed;
//   3. RevitLinkType.CreateFromIFC + RevitLinkInstance.Create in the host.
//
// WHY NOT Action=Link. With Link, the importer itself saves the intermediate and
// links it into ImporterIFC.Document - and for OpenIFCDocument that is the throwaway
// document it returns, never the host. Saving that throwaway onto the same
// "<file>.ifc.RVT" would replace the imported model with a model whose only content
// is a link to itself.
//
// WHY THE DRY RUN CANNOT REHEARSE. The importer builds a document and step 2 writes a
// file on disk; there is no transaction around either. So the dry run is a MEASURED
// PREVIEW, and its token binds the intermediate's state on disk (absent, or size and
// last write): an .ifc.RVT that appears, disappears or changes before the apply
// refuses as a changed plan instead of being overwritten unseen.
//
// FAILURES ARE NAMED BY WHERE THEY HAPPENED. ifc_importer_unavailable only when the
// importer assembly is nowhere to be found; ifc_import_failed when it is present and
// Revit refused the file; intermediate_save_failed when the save of step 2 failed;
// link_failed when step 3 was rolled back. None of them touched the host model, and
// every one reports whether the file on disk changed.
//
// THE LINK IS VERIFIED BY ITS CONTENT TOO: type re-read Loaded, instance re-read of
// that type, and the linked document holds DirectShape elements (what a
// Reference-intent import builds from IFC products) and no link to itself.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageLinksCommand
    {
        private static CommandResult AddIfc(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_manage_links");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
                return CommandResult.Fail("path is required and must be absolute.");
            if (!System.IO.File.Exists(path))
                return CommandResult.Fail("'" + path + "' does not exist. Nothing was linked.");
            path = System.IO.Path.GetFullPath(path);
            string rvtPath = path + ".RVT";
            RevitLinkType already = LinkTypeAt(doc, rvtPath);
            if (already != null)
                return CommandResult.Fail("'" + path + "' is ALREADY LINKED (its intermediate '" + rvtPath + "' is type " +
                    Rid.Value(already.Id) + "). Place another instance of that type instead. Nothing was linked.");
            string onDiskBefore = FileState(rvtPath);

            // The intermediate's state is folded into the hash: the token binds it, not only the request.
            var hashed = (JObject)request.DeepClone();
            hashed["intermediate_on_disk"] = onDiskBefore;
            string hash = DocumentGate.PlanHash(hashed, "operation", "path", "kind", "intermediate_on_disk");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["mode"] = "measured_preview",
                    ["would"] = "add",
                    ["kind"] = "ifc",
                    ["path"] = path,
                    ["intermediate_rvt"] = rvtPath,
                    ["intermediate_on_disk"] = onDiskBefore,
                    ["note"] = "OpenIFCDocument imports the IFC by reference into a new document and the apply saves it as the " +
                               "intermediate RVT, outside any transaction, so this preview measured the files only. " +
                               (onDiskBefore != "absent" ? "The existing intermediate RVT will be OVERWRITTEN. " : "") +
                               "A missing importer is refused by name (ifc_importer_unavailable)."
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true,
                    "the token binds the IFC path and the intermediate RVT's state on disk (absent, or size and last write).");
                return CommandResult.Ok(preview);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links", hash);
            if (refusal != null) return refusal;

            // 1-2. Import by reference into a new document, save it as the intermediate, close it.
            string stage = "import", failure = null;
            Document ifcDoc = null;
            try
            {
                var options = new IFCImportOptions { Action = IFCImportAction.Open, Intent = IFCImportIntent.Reference };
                ifcDoc = app.Application.OpenIFCDocument(path, options);
                if (ifcDoc == null) failure = "OpenIFCDocument returned no document";
                else if (ifcDoc.Equals(doc)) { ifcDoc = null; failure = "OpenIFCDocument returned the host document itself"; }
                else
                {
                    stage = "save_intermediate";
                    ifcDoc.SaveAs(rvtPath, new SaveAsOptions { OverwriteExistingFile = true });
                }
            }
            catch (Exception ex) { failure = ex.Message; }
            finally
            {
                try { if (ifcDoc != null && ifcDoc.IsValidObject) ifcDoc.Close(false); } catch { }
            }
            if (failure != null) return IfcFailure(app, stage, failure, path, rvtPath, onDiskBefore);

            // 3. The host links the intermediate.
            RevitLinkType type = null;
            RevitLinkInstance instance = null;
            using (var tx = new Transaction(doc, "Horizun: link IFC"))
            {
                tx.Start();
                try
                {
                    LinkLoadResult r = RevitLinkType.CreateFromIFC(doc, path, rvtPath, false, new RevitLinkOptions(false));
                    if (r == null || !LinkLoadResult.IsCodeSuccess(r.LoadResult))
                        throw new InvalidOperationException("RevitLinkType.CreateFromIFC answered '" +
                            (r == null ? "(null)" : r.LoadResult.ToString()) + "'");
                    type = doc.GetElement(r.ElementId) as RevitLinkType;
                    if (type == null) throw new InvalidOperationException("no link type could be read after CreateFromIFC");
                    instance = RevitLinkInstance.Create(doc, type.Id);
                    Guard.Commit(tx, "Horizun: link IFC");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    return IfcFailure(app, "link", ex.Message, path, rvtPath, onDiskBefore);
                }
            }

            RevitLinkType typeReread = doc.GetElement(type.Id) as RevitLinkType;
            RevitLinkInstance instReread = instance == null ? null : doc.GetElement(instance.Id) as RevitLinkInstance;
            string status = SafeStatus(typeReread);
            Document linked = null;
            try { linked = instReread?.GetLinkDocument(); } catch { }
            int? shapes = null;
            bool selfLink = false;
            if (linked != null)
            {
                try { shapes = new FilteredElementCollector(linked).OfClass(typeof(DirectShape)).GetElementCount(); } catch { }
                selfLink = LinkTypeAt(linked, rvtPath) != null;
            }
            bool verified = typeReread != null && instReread != null && status == "Loaded" && instReread.GetTypeId() == typeReread.Id
                            && shapes > 0 && !selfLink;
            var added = new JObject
            {
                ["operation"] = "add",
                ["kind"] = "ifc",
                ["path"] = path,
                ["intermediate_rvt"] = rvtPath,
                ["intermediate_before"] = onDiskBefore,
                ["intermediate_after"] = FileState(rvtPath),
                ["link_type_id"] = typeReread == null ? null : (JToken)Rid.Value(typeReread.Id),
                ["link_instance_id"] = instReread == null ? null : (JToken)Rid.Value(instReread.Id),
                ["status_after"] = status,
                ["linked_direct_shapes"] = shapes.HasValue ? (JToken)shapes.Value : JValue.CreateNull(),
                ["links_to_itself"] = selfLink,
                ["linked_by"] = "RevitLinkType.CreateFromIFC",
                ["verified"] = verified
            };
            ApplicationOutcome.StampApplied(added, ApplicationOutcome.Committed, 1, verified ? 1 : 0, verified ? 1 : 0, 0,
                                            verified ? 0 : 1, 0);
            if (!verified)
                return CommandResult.FailWithDetail("The IFC link committed but the re-read does not hold: type " +
                    (typeReread == null ? "(gone)" : status) + ", instance " + (instReread == null ? "(gone)" : "present") +
                    ", DirectShapes in the linked model " + (shapes.HasValue ? shapes.Value.ToString() : "(unreadable)") +
                    (selfLink ? ", and the linked model links to itself" : "") + ". Success is not claimed.", added);
            return CommandResult.Ok(added);
        }

        /// <summary>A refusal named by the step that failed. The host model was not written; the disk may have been.</summary>
        private static CommandResult IfcFailure(UIApplication app, string stage, string message, string path, string rvtPath,
                                                string onDiskBefore)
        {
            string version = Safe(() => app.Application.VersionNumber);
            string importer = IfcImporterAssembly(out string lookedIn);
            string reason = stage == "import" ? (importer == null ? "ifc_importer_unavailable" : "ifc_import_failed")
                          : stage == "save_intermediate" ? "intermediate_save_failed" : "link_failed";
            string onDiskAfter = FileState(rvtPath);
            bool diskChanged = !string.Equals(onDiskAfter, onDiskBefore, StringComparison.Ordinal);
            var detail = new JObject
            {
                ["state"] = "refused",
                ["reason"] = reason,
                ["stage"] = stage,
                ["revit_version"] = version,
                ["revit_message"] = message,
                ["path"] = path,
                ["intermediate_rvt"] = rvtPath,
                ["intermediate_before"] = onDiskBefore,
                ["intermediate_after"] = onDiskAfter,
                ["disk_changed"] = diskChanged,
                ["importer_assembly"] = importer,
                ["importer_looked_in"] = lookedIn
            };
            // link_failed opened (and rolled back) a transaction; the earlier stages never opened one.
            ApplicationOutcome.StampApplied(detail, stage == "link" ? ApplicationOutcome.RolledBackStatus : ApplicationOutcome.NotStarted,
                                            1, 0, 0, 0, 1, 0);
            string what = reason == "ifc_importer_unavailable"
                ? "Revit " + version + " has no IFC importer (looked in: " + lookedIn + ")"
                : reason == "ifc_import_failed" ? "Revit " + version + "'s IFC importer refused '" + path + "'"
                : reason == "intermediate_save_failed" ? "the imported IFC could not be saved as '" + rvtPath + "'"
                : "the intermediate '" + rvtPath + "' could not be linked and the host transaction was rolled back";
            return CommandResult.FailWithDetail(reason + ": " + what + " (" + message + "). The host model was not changed" +
                (diskChanged ? "; the intermediate file on disk WAS written (" + onDiskBefore + " -> " + onDiskAfter + ")." : "; nothing on disk changed."),
                detail);
        }

        /// <summary>"absent", or size and last write (UTC ticks) - what the token binds.</summary>
        private static string FileState(string path)
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                return fi.Exists ? fi.Length + " bytes @" + fi.LastWriteTimeUtc.Ticks : "absent";
            }
            catch (Exception ex) { return "unreadable: " + ex.Message; }
        }

        /// <summary>
        /// Where the IFC importer assembly is, or null. It ships beside RevitAPI.dll; the open-source
        /// IFC add-in, when installed, replaces it from an ApplicationPlugins bundle.
        /// </summary>
        private static string IfcImporterAssembly(out string lookedIn)
        {
            var looked = new List<string> { "loaded assemblies" };
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = null, location = null;
                    try { name = a.GetName().Name; location = a.Location; } catch { }
                    if (string.Equals(name, "Revit.IFC.Import", StringComparison.OrdinalIgnoreCase))
                    { lookedIn = looked[0]; return string.IsNullOrEmpty(location) ? "(loaded)" : location; }
                }
                string dir = System.IO.Path.GetDirectoryName(typeof(Document).Assembly.Location);
                string beside = System.IO.Path.Combine(dir ?? "", "Revit.IFC.Import.dll");
                looked.Add(beside);
                if (System.IO.File.Exists(beside)) { lookedIn = string.Join("; ", looked); return beside; }
                string plugins = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                                        "Autodesk", "ApplicationPlugins");
                looked.Add(System.IO.Path.Combine(plugins, "*IFC*"));
                if (System.IO.Directory.Exists(plugins))
                    foreach (string d in System.IO.Directory.GetDirectories(plugins, "*IFC*"))
                    {
                        string hit = System.IO.Directory.GetFiles(d, "Revit.IFC.Import.dll", System.IO.SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) { lookedIn = string.Join("; ", looked); return hit; }
                    }
            }
            catch (Exception ex) { looked.Add("search failed: " + ex.Message); }
            lookedIn = string.Join("; ", looked);
            return null;
        }

        private static RevitLinkType LinkTypeAt(Document doc, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full = System.IO.Path.GetFullPath(path);
            foreach (RevitLinkType t in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                string p = LinkPath(t);
                try { if (p != null && string.Equals(System.IO.Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)) return t; }
                catch (Exception) { /* a cloud path is not a file path and cannot be this one */ }
            }
            return null;
        }
    }
}
