using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Commands
{
    public sealed class ModelSnapshotCommand : ICommand
    {
        public string Name => "horizun_model_snapshot";
        public string Description => "Read explicit elements and room boundaries with per-field availability.";
        public CommandResult Execute(UIApplication app, string json)
        {
            JObject r = VerifiedModelEdit.Parse(json, out CommandResult parse);
            if (r == null) return parse;
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is active.");
            var guard = DocumentGate.ReadGuard(doc, r, Name);
            if (guard != null) return guard;
            if (!(r["element_ids"] is JArray ids) || ids.Count < 1 || ids.Count > 2000 ||
                ids.Any(x => x.Type != JTokenType.Integer || x.Value<long>() <= 0 || !Rid.CanRepresent(x.Value<long>())))
                return CommandResult.Fail("element_ids must contain 1..2000 positive representable integer IDs.");
            var rows = new JArray(); var missing = new JArray(); var errors = new JArray();
            var budget = new GeometryBudget();
            foreach (long id in ids.Values<long>().Distinct())
            {
                Element e = doc.GetElement(Rid.Make(id));
                if (e == null) { missing.Add(id); continue; }
                var row = new JObject { ["id"] = id, ["unique_id"] = e.UniqueId,
                    ["class"] = e.GetType().Name, ["category"] = e.Category?.Name,
                    ["category_id"] = e.Category == null ? null : (JToken)Rid.Value(e.Category.Id),
                    ["type_id"] = Rid.Value(e.GetTypeId()) };
                rows.Add(row);
                try
                {
                    var box = e.get_BoundingBox(null);
                    row["bounding_box"] = box == null ? null : WorldBounds(box);
                    row["bounding_box_status"] = box == null ? "not_available" : "measured";
                    if (e.Location is LocationPoint lp) { row["point"] = Point(lp.Point); row["rotation_radians"] = lp.Rotation; }
                    else if (e.Location is LocationCurve lc) row["curve"] = CurveData(lc.Curve, budget);
                    if (e is FamilyInstance instance) { row["family"] = instance.Symbol?.Family?.Name; row["host_id"] = instance.Host == null ? null : (JToken)Rid.Value(instance.Host.Id); }
                    if (e is Opening opening)
                    {
                        row["host_id"] = opening.Host == null ? null : (JToken)Rid.Value(opening.Host.Id);
                        row["is_rectangular"] = opening.IsRectBoundary;
                        if (opening.IsRectBoundary)
                        {
                            var rect = opening.BoundaryRect;
                            row["boundary"] = rect == null ? null : new JArray(rect.Select(Point));
                            row["boundary_status"] = rect == null ? "not_available" : "measured";
                        }
                        else
                        {
                            var curves = opening.BoundaryCurves;
                            if (curves != null && curves.Size > 2048) budget.Truncated = true;
                            row["boundary"] = curves == null ? null : new JArray(curves.Cast<Curve>().Take(2048).Select(c => CurveData(c, budget)));
                            row["boundary_status"] = curves == null ? "not_available" : "measured";
                        }
                    }
                    if (e is SpatialElement spatial && r.Value<bool?>("include_room_boundaries") != false)
                    {
                        using (var options = new SpatialElementBoundaryOptions())
                        {
                            var loops = spatial.GetBoundarySegments(options);
                            if (loops != null && (loops.Count > 128 || loops.Any(loop => loop.Count > 2048))) budget.Truncated = true;
                            row["boundaries"] = loops == null ? null : new JArray(loops.Take(128).Select(loop =>
                                new JArray(loop.Take(2048).Select(segment => CurveData(segment.GetCurve(), budget)))));
                            row["boundaries_status"] = loops == null || loops.Count == 0 ? "not_enclosed_or_unplaced" : "measured";
                        }
                    }
                }
                catch (Exception ex) { errors.Add(new JObject { ["id"] = id, ["type"] = ex.GetType().Name, ["message"] = ex.Message }); }
            }
            return CommandResult.Ok(new JObject { ["schema"] = "horizun.model-snapshot/1",
                ["document"] = doc.PathName, ["document_title"] = doc.Title, ["revit_build"] = app.Application.VersionBuild,
                ["units"] = "mm", ["coordinate_system"] = "internal_origin", ["elements"] = rows,
                ["missing_element_ids"] = missing, ["errors"] = errors, ["complete"] = missing.Count == 0 && errors.Count == 0 && !budget.Truncated,
                ["geometry_truncated"] = budget.Truncated, ["completeness_scope"] = "Requested element reads completed; inspect each geometry availability status separately.",
                ["note"] = "Unavailable geometry is explicitly marked; null does not mean zero. No model or file was modified." });
        }
        private static JArray Point(XYZ p) => new JArray(p.X * 304.8, p.Y * 304.8, p.Z * 304.8);
        private sealed class GeometryBudget { public int Remaining = 20000; public bool Truncated; }
        private static JObject CurveData(Curve c, GeometryBudget budget)
        {
            if (c == null) return new JObject { ["status"] = "not_available" };
            if (budget.Remaining <= 0) { budget.Truncated = true; return new JObject { ["status"] = "truncated" }; }
            if (!c.IsBound) return new JObject { ["kind"] = c.GetType().Name, ["status"] = "unbounded" };
            IList<XYZ> points = c.Tessellate();
            int count = Math.Min(points.Count, Math.Min(512, budget.Remaining));
            bool truncated = count < points.Count;
            budget.Remaining -= count; budget.Truncated |= truncated;
            return new JObject { ["kind"] = c.GetType().Name, ["start"] = Point(c.GetEndPoint(0)),
                ["end"] = Point(c.GetEndPoint(1)), ["length_mm"] = c.Length * 304.8,
                ["tessellation"] = new JArray(points.Take(count).Select(Point)), ["tessellation_truncated"] = truncated };
        }
        private static JObject WorldBounds(BoundingBoxXYZ box)
        {
            var corners = new List<XYZ>();
            for (int i = 0; i < 8; i++) corners.Add(box.Transform.OfPoint(new XYZ(
                (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                (i & 4) == 0 ? box.Min.Z : box.Max.Z)));
            return new JObject { ["min"] = Point(new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z))),
                ["max"] = Point(new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z))) };
        }
    }
}
