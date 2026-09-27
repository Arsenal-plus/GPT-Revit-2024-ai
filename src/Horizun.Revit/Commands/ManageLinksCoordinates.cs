// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_links operation=acquire_coordinates.
//
// Document.AcquireCoordinates runs INSIDE a transaction (RevitAPI lists "The
// document has no open transaction" among its exceptions), so unlike add/unload
// this one CAN rehearse for real: the dry run acquires, re-reads, and rolls back
// through VerifiedModelEdit, and the reply carries Revit's rollback status.
//
// WHAT "DONE" MEANS. Acquiring makes the link's shared coordinates the host's.
// The fact that proves it is the one horizun_federation_check measures: three link
// points taken to shared coordinates through the link AND through the host must
// agree. The same method (FederationCheckCommand.SameSite) is re-run inside the
// transaction and again on the committed model, so "same_site true" is measured,
// not inferred from a call that did not throw. A CAD link has no link document to
// ask; its proof is that the DWG's own coordinates become the host's shared ones
// at three points of the import (the WCS rule RevitAPI documents for DWG links).
//
// publish_coordinates is deliberately not here: it writes into the LINKED file,
// which is a different verification story.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageLinksCommand
    {
        private const double AcquireToleranceMm = 1.0;

        private static CommandResult AcquireCoordinates(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_manage_links");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            long instanceId = request.Value<long?>("link_instance_id") ?? -1;
            if (instanceId < 0)
            {
                // Never guess the source: a type may be placed several times, and the site that
                // becomes the project's is the one instance's position.
                var all = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).ToElementIds()
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                        .Where(i => Safe<bool>(() => i.IsLinked) == true).Select(i => i.Id))
                    .Select(Rid.Value).Take(50).ToList();
                return CommandResult.Fail("acquire_coordinates needs link_instance_id: the instance whose position " +
                    "defines the new shared coordinates. Link instances here: " +
                    (all.Count == 0 ? "(none)" : string.Join(", ", all)) + ". Nothing was written.");
            }
            Element source = Rid.CanRepresent(instanceId) ? doc.GetElement(Rid.Make(instanceId)) : null;
            var rvt = source as RevitLinkInstance;
            var cad = source as ImportInstance;
            if (rvt == null && (cad == null || Safe<bool>(() => cad.IsLinked) != true))
                return CommandResult.Fail("link_instance_id " + instanceId + " is not a Revit link instance nor a LINKED " +
                    "CAD instance (an imported CAD has no link to take coordinates from). Nothing was written.");

            ElementId typeId = source.GetTypeId();
            var siblings = new FilteredElementCollector(doc).OfClass(rvt != null ? typeof(RevitLinkInstance) : typeof(ImportInstance))
                .Where(e => e.GetTypeId() == typeId).Select(e => Rid.Value(e.Id)).OrderBy(x => x).ToList();

            Document linkDoc = null;
            double? deltaBefore = null;
            if (rvt != null)
            {
                linkDoc = rvt.GetLinkDocument();
                if (linkDoc == null)
                    return CommandResult.Fail("link instance " + instanceId + " is not loaded, so its coordinates cannot be " +
                        "read or acquired. reload its type first. Nothing was written.");
                deltaBefore = SiteDelta(doc, rvt, linkDoc, out string why);
                if (deltaBefore == null)
                    return CommandResult.Fail("the link's shared coordinates could not be compared with the host's (" + why +
                        "), so the result could not be verified either. Nothing was written.");
            }
            // The instance is always named here, so a type placed several times is Revit's to refuse: the
            // rehearsal (a real transaction, rolled back) asks it, and its own message is what the caller hears.
            string refusal = LinkSurveyRules.AcquireRefusal(instanceId, siblings, deltaBefore, AcquireToleranceMm, instanceNamed: true);
            if (refusal != null) return CommandResult.Fail(refusal);

            ProjectLocation loc = doc.ActiveProjectLocation;
            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            if (loc == null || pbp == null)
                return CommandResult.Fail("The document has no active project location or project base point to re-read. Nothing was written.");
            XYZ at = pbp.Position;
            JObject positionBefore = SharedPositionJson(loc.GetProjectPosition(at));
            string locationBefore = loc.Name;

            ElementId sourceId = source.Id;
            var edit = new ModelEdit
            {
                Tool = "horizun_manage_links",
                Operation = "acquire_coordinates",
                Subject = Safe(() => loc.UniqueId) ?? "project_location",
                Category = "ProjectLocation",
                Action = PlannedAction.Modify,
                TokenNote = "the token binds the host's shared position at the project base point and the source instance.",
                Warning = "The host's shared coordinates and True North become the link's. Links placed by shared " +
                          "coordinates, coordinate-based exports and survey ties follow; geolocation is overwritten by the link's " +
                          "(RevitAPI: the API always overwrites it, unlike the UI)."
            };
            foreach (var p in positionBefore.Properties()) edit.Before[p.Name] = p.Value.ToString();
            edit.Before["source_instance"] = instanceId.ToString();
            // What decides the result is bound too: where the source instance sits (Revit acquires "based on the
            // position of the linked model instance") and, for an RVT, the link's own site. A link moved or
            // reloaded after the dry run refuses as a changed plan instead of acquiring a site nobody rehearsed.
            Transform placed = source is Instance placedInstance ? placedInstance.GetTotalTransform() : null;
            if (placed != null)
            {
                edit.Before["source_origin_mm"] = Xyz(placed.Origin * 304.8, 1);
                edit.Before["source_basis_x"] = Xyz(placed.BasisX, 6);
            }
            if (deltaBefore.HasValue) edit.Before["same_site_delta_mm"] = Math.Round(deltaBefore.Value, 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (siblings.Count > 1) edit.Before["placements_of_type"] = string.Join(",", siblings);
            edit.Plan = new JObject
            {
                ["source_instance_id"] = instanceId,
                ["source_kind"] = rvt != null ? "rvt" : "cad",
                ["source_title"] = rvt != null ? Safe(() => linkDoc.Title) : SafeName(source),
                ["location_before"] = locationBefore,
                ["project_position_before"] = positionBefore,
                ["same_site_delta_mm_before"] = deltaBefore.HasValue ? (JToken)Math.Round(deltaBefore.Value, 1) : JValue.CreateNull()
            };
            edit.Apply = d => d.AcquireCoordinates(sourceId);
            edit.Verify = d =>
            {
                string what = rvt != null ? "link_same_site_delta_mm" : "cad_wcs_delta_mm";
                var check = new PostconditionCheck(what, "host_shared_position_changed");
                try
                {
                    Element src = d.GetElement(sourceId);
                    double? delta;
                    string why;
                    if (src is RevitLinkInstance r)
                        delta = SiteDelta(d, r, r.GetLinkDocument() ?? linkDoc, out why);
                    else
                        delta = CadWcsDelta(d, src as ImportInstance, out why);
                    if (delta.HasValue)
                        check.Measure(what, 0, delta.Value, AcquireToleranceMm, "mm",
                            rvt != null ? "horizun_federation_check same_site (three link points, shared coordinates both ways)"
                                        : "three import points: host shared position vs the DWG's own coordinates");
                    else check.Unreadable(what, 0, why);
                    ProjectLocation now = d.ActiveProjectLocation;
                    ProjectPosition p = now.GetProjectPosition(at);
                    JObject after = SharedPositionJson(p);
                    bool changed = !JToken.DeepEquals(after, positionBefore);
                    check.Record("host_shared_position_changed", true, changed, changed);
                }
                catch (Exception ex)
                {
                    check.Unreadable(what, 0, ex.Message);
                    check.Unreadable("host_shared_position_changed", true, ex.Message);
                }
                return check;
            };
            edit.Result = d =>
            {
                ProjectLocation now = d.ActiveProjectLocation;
                var o = new JObject
                {
                    ["source_instance_id"] = instanceId,
                    ["location_after"] = now?.Name,
                    ["project_position_before"] = positionBefore,
                    ["project_position_after"] = now == null ? null : SharedPositionJson(now.GetProjectPosition(at))
                };
                if (d.GetElement(sourceId) is RevitLinkInstance r)
                {
                    double? delta = SiteDelta(d, r, r.GetLinkDocument() ?? linkDoc, out string why);
                    o["same_site_delta_mm_after"] = delta.HasValue ? (JToken)Math.Round(delta.Value, 3) : JValue.CreateNull();
                    o["same_site"] = delta.HasValue ? (JToken)(delta.Value <= AcquireToleranceMm) : JValue.CreateNull();
                    if (!delta.HasValue) o["same_site_unreadable"] = why;
                }
                return o;
            };
            return VerifiedModelEdit.Run(app, gate, request, edit, "link_instance_id");
        }

        /// <summary>The same measurement horizun_federation_check publishes as the link's same_site delta.</summary>
        private static double? SiteDelta(Document host, RevitLinkInstance inst, Document linkDoc, out string why)
        {
            var fact = new FederationLinkFact();
            FederationCheckCommand.SameSite(host, inst, linkDoc, fact);
            why = fact.SiteWhyNot;
            return fact.SiteDeltaMm;
        }

        /// <summary>
        /// After acquiring from a DWG, the DWG's WCS is the host's shared system: a point q of the
        /// import (its own coordinates) sits at shared (q.X, q.Y, q.Z). Largest disagreement, mm.
        /// </summary>
        private static double? CadWcsDelta(Document host, ImportInstance inst, out string why)
        {
            why = null;
            try
            {
                ProjectLocation loc = host.ActiveProjectLocation;
                if (inst == null || loc == null) { why = "the import or the project location could not be read."; return null; }
                Transform t = inst.GetTotalTransform();
                double worst = 0;
                foreach (XYZ q in new[] { XYZ.Zero, new XYZ(100, 0, 0), new XYZ(0, 100, 0) })
                {
                    ProjectPosition p = loc.GetProjectPosition(t.OfPoint(q));
                    double dx = p.EastWest - q.X, dy = p.NorthSouth - q.Y, dz = p.Elevation - q.Z;
                    worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz) * 304.8);
                }
                return worst;
            }
            catch (Exception ex) { why = "the CAD link's coordinates could not be compared: " + ex.Message; return null; }
        }

        private static string Xyz(XYZ v, int digits)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return Math.Round(v.X, digits).ToString("R", ci) + "," + Math.Round(v.Y, digits).ToString("R", ci) + "," +
                   Math.Round(v.Z, digits).ToString("R", ci);
        }

        /// <summary>Same units horizun_manage_units base_points takes back (mm, degrees), so a caller can restore.</summary>
        private static JObject SharedPositionJson(ProjectPosition p) => new JObject
        {
            ["east_west"] = Math.Round(p.EastWest * 304.8, 3),
            ["north_south"] = Math.Round(p.NorthSouth * 304.8, 3),
            ["elevation"] = Math.Round(p.Elevation * 304.8, 3),
            ["angle_to_true_north"] = Math.Round(p.Angle * 180 / Math.PI, 6)
        };
    }
}
