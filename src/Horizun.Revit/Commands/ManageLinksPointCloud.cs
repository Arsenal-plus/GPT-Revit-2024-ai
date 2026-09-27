// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_links add kind=point_cloud and operation=scan_deviation.
//
// ADD. PointCloudType.Create and PointCloudInstance.Create both run inside a
// transaction, so the dry run is a REAL rehearsal (VerifiedModelEdit: create,
// re-read, roll back). The engine is refused by name before anything is touched
// when PointCloudEngineRegistry does not list it. The instance is placed with the
// identity transform: the cloud's own coordinates are read as internal ones.
//
// SCAN_DEVIATION (read-only). For each planar face of the chosen walls, floors and
// columns, a convex slab around the face (two planes parallel to it at +/- band,
// four around its UV rectangle) is the multi-plane filter GetPoints samples with.
// Each returned point that projects INSIDE the face is measured as a signed
// distance to the face plane. The judgement is Core/LinkSurveyRules.cs.
//
// THE COORDINATE FRAME IS MEASURED, NOT ASSUMED. RevitAPI says the filter is in
// model coordinates; whether the returned points are too, or in the cloud's own
// frame, is taken per face from the points themselves: the frame (raw, or through
// the instance's total transform) in which the points actually fall inside the
// filter's volume is the one used. A face where neither holds - or where BOTH do,
// because the transform moves less than the band - is not_measured
// (point_frame_undetermined) rather than measured in a frame that was picked.
//
// THE SLAB IS ASYMMETRIC. Outward it reaches band_mm; inward it stops below half
// the element's thickness behind the face (LinkSurveyRules.InwardBandMm), so the
// element's own opposite face is never measured as this face's deviation.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageLinksCommand
    {
        // ---- add kind=point_cloud ------------------------------------------------
        private static CommandResult AddPointCloud(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_manage_links");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
                return CommandResult.Fail("path is required and must be absolute.");
            if (!System.IO.File.Exists(path))
                return CommandResult.Fail("'" + path + "' does not exist. Nothing was linked.");
            string engine = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            IList<string> engines;
            try { engines = PointCloudEngineRegistry.GetSupportedEngines(); }
            catch (Exception ex) { return CommandResult.Fail("The point cloud engines could not be listed: " + ex.Message + ". Nothing was linked."); }
            if (engines == null || !engines.Any(e => string.Equals(e, engine, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail("point_cloud_engine_unavailable: this Revit session registers no '" + engine +
                    "' engine (registered: " + (engines == null ? "(none)" : string.Join(", ", engines)) + "). Nothing was linked.");
            foreach (PointCloudType existing in new FilteredElementCollector(doc).OfClass(typeof(PointCloudType)).Cast<PointCloudType>())
            {
                // PointCloudType is not an ExternalFileReference (no such ExternalFileReferenceType): GetPath is its file.
                string existingPath = null;
                try
                {
                    ModelPath mp = existing.GetPath();
                    existingPath = mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                    if (!string.IsNullOrEmpty(existingPath)) existingPath = System.IO.Path.GetFullPath(existingPath);
                }
                catch { existingPath = null; }
                if (!string.IsNullOrEmpty(existingPath) &&
                    string.Equals(existingPath, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("'" + path + "' is ALREADY LINKED as point cloud type " + Rid.Value(existing.Id) +
                        ". Nothing was linked.");
            }

            ElementId typeId = ElementId.InvalidElementId, instId = ElementId.InvalidElementId;
            var edit = new ModelEdit
            {
                Tool = "horizun_manage_links",
                Operation = "add",
                Subject = "point_cloud:" + System.IO.Path.GetFullPath(path).ToLowerInvariant(),
                Category = "PointCloudType",
                Action = PlannedAction.Create,
                TokenNote = "the token binds the file path and the engine that reads it.",
                Plan = new JObject
                {
                    ["kind"] = "point_cloud",
                    ["path"] = path,
                    ["engine"] = engine,
                    ["placement"] = "identity transform: the cloud's own coordinates are read as internal coordinates"
                }
            };
            edit.Before["linked"] = "false";
            // Runs twice (rehearsal, apply): the ids of the LAST run are the ones verified and published.
            edit.Apply = d =>
            {
                PointCloudType t = PointCloudType.Create(d, engine, path);
                typeId = t.Id;
                instId = PointCloudInstance.Create(d, t.Id, Transform.Identity).Id;
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("point_cloud_type", "point_cloud_instance", "instance_type", "engine");
                var t = d.GetElement(typeId) as PointCloudType;
                var i = d.GetElement(instId) as PointCloudInstance;
                check.Compare("point_cloud_type", true, t != null);
                check.Compare("point_cloud_instance", true, i != null);
                check.Compare("instance_type", Rid.Value(typeId), i == null ? -1 : Rid.Value(i.GetTypeId()));
                check.Compare("engine", engine, t == null ? null : (Safe(() => t.EngineIdentifier) ?? "").ToLowerInvariant());
                return check;
            };
            edit.Result = d =>
            {
                var t = d.GetElement(typeId) as PointCloudType;
                return new JObject
                {
                    ["kind"] = "point_cloud",
                    ["link_type_id"] = Rid.Value(typeId),
                    ["link_instance_id"] = Rid.Value(instId),
                    ["engine"] = t == null ? null : Safe(() => t.EngineIdentifier),
                    ["found_status"] = t == null ? null : Safe(() => t.FoundStatus.ToString()),
                    ["path"] = path,
                    ["verified"] = t != null && d.GetElement(instId) is PointCloudInstance
                };
            };
            return VerifiedModelEdit.Run(app, gate, request, edit, "path", "kind");
        }

        // ---- scan_deviation (read-only) --------------------------------------------
        private static readonly BuiltInCategory[] ScanCategories =
            { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns };

        private static CommandResult ScanDeviation(UIApplication app, JObject request)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            CommandResult refused = DocumentGate.ReadGuard(doc, request, "horizun_manage_links");
            if (refused != null) return refused;

            long pcId = request.Value<long?>("link_instance_id") ?? -1;
            var pc = Rid.CanRepresent(pcId) ? doc.GetElement(Rid.Make(pcId)) as PointCloudInstance : null;
            if (pc == null)
            {
                var clouds = new FilteredElementCollector(doc).OfClass(typeof(PointCloudInstance)).ToElementIds().Select(Rid.Value).Take(50).ToList();
                return CommandResult.Fail("scan_deviation needs link_instance_id naming a PointCloudInstance. Point clouds here: " +
                    (clouds.Count == 0 ? "(none)" : string.Join(", ", clouds)) + ".");
            }
            // A cloud whose file is not found returns no points: every face would read "the scan did not see it".
            string found = null;
            try { found = (doc.GetElement(pc.GetTypeId()) as PointCloudType)?.FoundStatus.ToString(); } catch { found = null; }
            if (found == "NotFound")
                return CommandResult.Fail("cloud_not_found: the file of point cloud " + pcId + " is not found (PointCloudType.FoundStatus " +
                    "NotFound), so no point can be read and no face measured. Repath or reload it first. Nothing was measured.");
            var idsToken = request["element_ids"] as JArray;
            if (idsToken == null || idsToken.Count == 0)
                return CommandResult.Fail("scan_deviation needs element_ids: the walls, floors and columns whose faces are measured. " +
                    "An empty selection is not read as every element.");
            if (idsToken.Count > LinkSurveyRules.MaxElements)
                return CommandResult.Fail("element_ids is limited to " + LinkSurveyRules.MaxElements + " per call.");
            double tol = request.Value<double?>("tolerance_mm") ?? LinkSurveyRules.DefaultToleranceMm;
            if (!(tol > 0) || tol > 1000) return CommandResult.Fail("tolerance_mm must be > 0 and <= 1000.");
            double bandMm = LinkSurveyRules.BandMm(tol);
            double avg = LinkSurveyRules.AverageDistanceMm / 304.8;
            Transform tr;
            try { tr = pc.GetTotalTransform(); } catch { tr = Transform.Identity; }

            var rows = new JArray();
            var states = new List<string>();
            int facesMeasured = 0, facesNotMeasured = 0, nonPlanarTotal = 0, beyondTotal = 0;
            foreach (JToken tok in idsToken)
            {
                long id = tok.Type == JTokenType.Integer ? tok.Value<long>() : -1;
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                var row = new JObject { ["element_id"] = id };
                rows.Add(row);
                if (e == null) { row["state"] = "not_measured"; row["reason"] = "not_found"; states.Add("not_measured"); continue; }
                ElementId cat = e.Category?.Id;
                if (cat == null || !ScanCategories.Any(b => cat.Equals(new ElementId(b))))
                {
                    row["state"] = "not_measured"; row["reason"] = "category_not_scanned";
                    row["category"] = e.Category?.Name; states.Add("not_measured"); continue;
                }
                row["category"] = e.Category.Name;
                List<PlanarFace> faces = PlanarFaces(e, out int nonPlanar, out string geometryWhy);
                nonPlanarTotal += nonPlanar;
                if (faces.Count == 0 && nonPlanar == 0)
                {
                    row["state"] = "not_measured"; row["reason"] = geometryWhy ?? "no_solid_geometry";
                    row["faces"] = new JArray(); states.Add("not_measured"); continue;
                }
                var faceRows = new JArray();
                var faceStates = new List<string>();
                int index = 0;
                foreach (PlanarFace f in faces.Take(LinkSurveyRules.MaxFacesPerElement))
                {
                    JObject fv = MeasureFace(pc, tr, f, faces, bandMm, avg, tol);
                    fv.AddFirst(new JProperty("face", index++));
                    fv["normal"] = new JArray(Math.Round(f.FaceNormal.X, 4), Math.Round(f.FaceNormal.Y, 4), Math.Round(f.FaceNormal.Z, 4));
                    fv["area_m2"] = Math.Round(f.Area * 0.09290304, 3);
                    string st = fv.Value<string>("state");
                    if (st == "not_measured") facesNotMeasured++; else facesMeasured++;
                    faceStates.Add(st);
                    faceRows.Add(fv);
                }
                // Non-planar faces and faces beyond the per-element limit are not sampled: they count as
                // faces nobody measured, in the element's state and in the totals.
                int beyond = Math.Max(0, faces.Count - LinkSurveyRules.MaxFacesPerElement);
                facesNotMeasured += nonPlanar + beyond;
                beyondTotal += beyond;
                string es = LinkSurveyRules.ElementState(faceStates, nonPlanar + beyond);
                row["state"] = es;
                row["faces"] = faceRows;
                if (nonPlanar > 0) row["non_planar_faces_not_measured"] = nonPlanar;
                if (beyond > 0) row["faces_beyond_limit_not_measured"] = beyond;
                states.Add(es);
            }
            var result = new JObject
            {
                ["operation"] = "scan_deviation",
                ["point_cloud_instance_id"] = pcId,
                ["verdict"] = LinkSurveyRules.Verdict(states),
                ["tolerance_mm"] = tol,
                ["band_mm"] = bandMm,
                ["cloud_found_status"] = found,
                ["min_coverage_share"] = LinkSurveyRules.MinCoverageShare,
                ["average_distance_mm"] = LinkSurveyRules.AverageDistanceMm,
                ["max_points_per_face"] = LinkSurveyRules.MaxPointsPerFace,
                ["min_points_per_face"] = LinkSurveyRules.MinPointsPerFace,
                ["summary"] = new JObject
                {
                    ["elements"] = states.Count,
                    ["ok"] = states.Count(s => s == "ok"),
                    ["deviates"] = states.Count(s => s == "deviates"),
                    ["partially_measured"] = states.Count(s => s == "partially_measured"),
                    ["not_measured"] = states.Count(s => s == "not_measured"),
                    ["faces_measured"] = facesMeasured,
                    ["faces_not_measured"] = facesNotMeasured,
                    ["non_planar_faces"] = nonPlanarTotal,
                    ["faces_beyond_limit"] = beyondTotal
                },
                ["elements"] = rows,
                ["note"] = "A face is judged on the 95th percentile of |distance|. Points farther than band_mm outside a face " +
                           "(or than band_inward_mm inside it, below half the element's thickness) are never sampled, so a " +
                           "deviation beyond them cannot be seen. A face with fewer than min_points_per_face points, or under " +
                           "min_coverage_share of the points its area should return, is not_measured, never ok; so are faces " +
                           "that were not sampled, and an element with any of them is at best partially_measured."
            };
            // Read-only: declared, so a plan step reads "nothing written" instead of an undeclared (uncertain) child.
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            return CommandResult.Ok(result);
        }

        private static JObject MeasureFace(PointCloudInstance pc, Transform tr, PlanarFace f, List<PlanarFace> all, double bandMm,
                                           double avg, double tolMm)
        {
            XYZ n = f.FaceNormal.Normalize();
            double? thicknessFt = ThicknessBehind(f, all);
            double inwardMm = LinkSurveyRules.InwardBandMm(bandMm, thicknessFt * 304.8);
            double band = bandMm / 304.8, inward = inwardMm / 304.8;
            BoundingBoxUV bb = f.GetBoundingBox();
            var corners = new[]
            {
                f.Evaluate(bb.Min), f.Evaluate(new UV(bb.Max.U, bb.Min.V)), f.Evaluate(bb.Max), f.Evaluate(new UV(bb.Min.U, bb.Max.V))
            };
            XYZ centroid = (corners[0] + corners[1] + corners[2] + corners[3]) / 4;
            var planes = new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(n.Negate(), corners[0] + n * band),
                Plane.CreateByNormalAndOrigin(n, corners[0] - n * inward)
            };
            for (int i = 0; i < 4; i++)
            {
                XYZ a = corners[i], b = corners[(i + 1) % 4];
                XYZ edge = b - a;
                if (edge.GetLength() < 1e-6) continue;
                XYZ m = n.CrossProduct(edge).Normalize();
                if ((centroid - a).DotProduct(m) < 0) m = m.Negate();
                planes.Add(Plane.CreateByNormalAndOrigin(m, a));
            }
            List<XYZ> raw;
            try
            {
                PointCloudFilter filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
                PointCollection pts = pc.GetPoints(filter, avg, LinkSurveyRules.MaxPointsPerFace);
                raw = new List<XYZ>();
                foreach (CloudPoint cp in pts) raw.Add(new XYZ(cp.X, cp.Y, cp.Z));
            }
            catch (Exception ex)
            {
                return new JObject { ["points"] = 0, ["state"] = "not_measured", ["reason"] = "points_unreadable", ["note"] = ex.Message };
            }
            // Which frame are the points in? The one where they lie inside the filter's volume - and only that one.
            List<XYZ> moved = tr.IsIdentity ? raw : raw.Select(p => tr.OfPoint(p)).ToList();
            int insideRaw = raw.Count(p => Inside(planes, p)), insideMoved = tr.IsIdentity ? insideRaw : moved.Count(p => Inside(planes, p));
            string frame = LinkSurveyRules.PointFrame(tr.IsIdentity, raw.Count, insideRaw, insideMoved);
            if (frame == null)
                return new JObject
                {
                    ["points"] = raw.Count, ["state"] = "not_measured", ["reason"] = "point_frame_undetermined",
                    ["inside_as_returned"] = insideRaw, ["inside_through_transform"] = insideMoved,
                    ["note"] = "the returned points fall inside the sampled volume in neither frame, or in both (the instance " +
                               "transform moves less than the band): which frame Revit returned cannot be told from this face."
                };
            List<XYZ> use = frame == "instance_transform" ? moved : raw;
            var signed = new List<double>();
            foreach (XYZ p in use)
            {
                if (!Inside(planes, p)) continue;
                if (f.Project(p) == null) continue; // inside the UV rectangle but outside the face's own boundary
                signed.Add((p - corners[0]).DotProduct(n) * 304.8);
            }
            double expected = LinkSurveyRules.ExpectedPoints(f.Area * 0.09290304, LinkSurveyRules.AverageDistanceMm,
                                                             LinkSurveyRules.MaxPointsPerFace);
            JObject v = LinkSurveyRules.FaceVerdict(signed, tolMm, LinkSurveyRules.MinPointsPerFace, expected);
            v["points_returned"] = raw.Count;
            v["point_frame"] = raw.Count == 0 ? null : frame;
            v["band_inward_mm"] = Math.Round(inwardMm, 1);
            if (thicknessFt.HasValue) v["thickness_behind_mm"] = Math.Round(thicknessFt.Value * 304.8, 1);
            return v;
        }

        /// <summary>
        /// Distance to the nearest antiparallel face of the same element behind this one - the material
        /// the slab must not cross. Null when the element has none (the inward band then stays the band).
        /// </summary>
        private static double? ThicknessBehind(PlanarFace f, List<PlanarFace> all)
        {
            XYZ n = f.FaceNormal.Normalize();
            double? best = null;
            foreach (PlanarFace g in all)
            {
                if (ReferenceEquals(g, f) || g.FaceNormal.Normalize().DotProduct(n) > -0.999) continue;
                double d = (f.Origin - g.Origin).DotProduct(n);
                if (d > 1e-6 && (!best.HasValue || d < best.Value)) best = d;
            }
            return best;
        }

        private static bool Inside(List<Plane> planes, XYZ p)
            => planes.All(pl => (p - pl.Origin).DotProduct(pl.Normal) >= -1e-3);

        private static List<PlanarFace> PlanarFaces(Element e, out int nonPlanar, out string why)
        {
            var list = new List<PlanarFace>();
            nonPlanar = 0;
            why = null;
            GeometryElement g;
            try { g = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false }); }
            catch (Exception ex) { why = "geometry_unreadable: " + ex.Message; return list; }
            if (g == null) { why = "geometry_unreadable: the element returned no geometry"; return list; }
            Collect(g, list, ref nonPlanar);
            if (list.Count == 0 && nonPlanar == 0) why = "no_solid_geometry";
            return list;
        }

        private static void Collect(GeometryElement g, List<PlanarFace> list, ref int nonPlanar)
        {
            if (g == null) return;
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > 1e-9)
                {
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf) list.Add(pf); else nonPlanar++;
                }
                else if (o is GeometryInstance gi)
                    Collect(gi.GetInstanceGeometry(), list, ref nonPlanar);
            }
        }
    }
}
