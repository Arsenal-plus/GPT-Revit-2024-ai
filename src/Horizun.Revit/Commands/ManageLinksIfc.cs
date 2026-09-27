// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_links add kind=ifc: link an IFC the way Revit does - the IFC is
// imported by reference into an intermediate "<file>.ifc.RVT", and that RVT is
// linked with RevitLinkType.CreateFromIFC (RevitAPI names the Revit.IFC.Import
// Link branch as the reference sequence).
//
// WHY THE DRY RUN CANNOT REHEARSE. Application.OpenIFCDocument runs the IFC
// importer and writes a file on disk; there is no transaction around that. So the
// dry run is a MEASURED PREVIEW (the IFC exists, where the intermediate RVT lands,
// whether one is already there and would be overwritten) and says so.
//
// THE IMPORTER MAY BE MISSING. OpenIFCDocument delegates to the IFC importer
// Revit ships per year; when it is absent or fails, the apply refuses by name
// (ifc_importer_unavailable) with Revit's message, before anything is linked. The
// link itself is verified the way `add` verifies a .rvt: type re-read as Loaded,
// instance re-read and belonging to that type.
// -----------------------------------------------------------------------------
using System;
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
            bool intermediateExists = System.IO.File.Exists(rvtPath);

            string hash = DocumentGate.PlanHash(request, "operation", "path", "kind");
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
                    ["intermediate_exists"] = intermediateExists,
                    ["note"] = "Application.OpenIFCDocument runs the IFC importer and writes the intermediate file outside any " +
                               "transaction, so this preview measured the files only. " +
                               (intermediateExists ? "The existing intermediate RVT will be regenerated. " : "") +
                               "The importer is exercised by the apply and refused by name (ifc_importer_unavailable) if missing."
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, "horizun_manage_links", hash, true,
                    "the token binds the IFC path and whether its intermediate RVT already exists.");
                return CommandResult.Ok(preview);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_manage_links", hash);
            if (refusal != null) return refusal;

            // 1. The importer produces the intermediate RVT.
            string produced;
            Document ifcDoc = null;
            try
            {
                var options = new IFCImportOptions { Action = IFCImportAction.Link };
                ifcDoc = app.Application.OpenIFCDocument(path, options);
                if (ifcDoc == null) throw new InvalidOperationException("OpenIFCDocument returned no document");
                if (ifcDoc.Equals(doc)) throw new InvalidOperationException("the importer returned the host document itself");
                produced = string.IsNullOrEmpty(ifcDoc.PathName) ? null : ifcDoc.PathName;
                if (produced == null)
                {
                    ifcDoc.SaveAs(rvtPath, new SaveAsOptions { OverwriteExistingFile = true });
                    produced = rvtPath;
                }
            }
            catch (Exception ex)
            {
                var detail = new JObject
                {
                    ["state"] = "refused",
                    ["reason"] = "ifc_importer_unavailable",
                    ["revit_version"] = Safe(() => app.Application.VersionNumber),
                    ["path"] = path
                };
                ApplicationOutcome.StampApplied(detail, ApplicationOutcome.RolledBackStatus, 1, 0, 0, 0, 1, 0);
                return CommandResult.FailWithDetail("ifc_importer_unavailable: Revit " + Safe(() => app.Application.VersionNumber) +
                    " could not import '" + path + "' by reference (" + ex.Message + "). Nothing was linked.", detail);
            }
            finally
            {
                try { if (ifcDoc != null && ifcDoc.IsValidObject && !ifcDoc.Equals(doc)) ifcDoc.Close(false); } catch { }
            }

            // 2. The host links the intermediate RVT - unless the importer already did.
            RevitLinkType type = LinkTypeAt(doc, produced);
            RevitLinkInstance instance = null;
            bool importerLinked = type != null;
            using (var tx = new Transaction(doc, "Horizun: link IFC"))
            {
                tx.Start();
                try
                {
                    if (type == null)
                    {
                        LinkLoadResult r = RevitLinkType.CreateFromIFC(doc, path, produced, false, new RevitLinkOptions(false));
                        if (r == null || !LinkLoadResult.IsCodeSuccess(r.LoadResult))
                            throw new InvalidOperationException("RevitLinkType.CreateFromIFC answered '" +
                                (r == null ? "(null)" : r.LoadResult.ToString()) + "'");
                        type = doc.GetElement(r.ElementId) as RevitLinkType;
                    }
                    if (type == null) throw new InvalidOperationException("no link type could be read after CreateFromIFC");
                    if (InstancesOf(doc, type.Id).Count == 0) instance = RevitLinkInstance.Create(doc, type.Id);
                    else instance = doc.GetElement(InstancesOf(doc, type.Id).First()) as RevitLinkInstance;
                    Guard.Commit(tx, "Horizun: link IFC");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    return CommandResult.Fail("The IFC was imported to '" + produced + "' but linking it failed and was rolled " +
                        "back: " + ex.Message + ". The intermediate file stays on disk; nothing was linked.");
                }
            }

            RevitLinkType typeReread = doc.GetElement(type.Id) as RevitLinkType;
            RevitLinkInstance instReread = instance == null ? null : doc.GetElement(instance.Id) as RevitLinkInstance;
            string status = SafeStatus(typeReread);
            bool verified = typeReread != null && instReread != null && status == "Loaded" && instReread.GetTypeId() == typeReread.Id;
            var added = new JObject
            {
                ["operation"] = "add",
                ["kind"] = "ifc",
                ["path"] = path,
                ["intermediate_rvt"] = produced,
                ["link_type_id"] = typeReread == null ? null : (JToken)Rid.Value(typeReread.Id),
                ["link_instance_id"] = instReread == null ? null : (JToken)Rid.Value(instReread.Id),
                ["status_after"] = status,
                ["linked_by"] = importerLinked ? "ifc_importer" : "RevitLinkType.CreateFromIFC",
                ["verified"] = verified
            };
            ApplicationOutcome.StampApplied(added, ApplicationOutcome.Committed, 1, verified ? 1 : 0, verified ? 1 : 0, 0,
                                            verified ? 0 : 1, 0);
            if (!verified)
                return CommandResult.FailWithDetail("The IFC link committed but the re-read does not hold: type " +
                    (typeReread == null ? "(gone)" : status) + ", instance " + (instReread == null ? "(gone)" : "present") +
                    ". Success is not claimed.", added);
            return CommandResult.Ok(added);
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
