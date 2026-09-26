// -----------------------------------------------------------------------------
// Horizun Revit MCP - MEP routing: obstacle-avoiding path search and creation.
// Original Horizun code. Partial class: plugs the 'route' operation into the
// shared dry_run -> confirmation_token -> apply pipeline defined in
// MepRoutingCommand.cs (WritePlan, Rehearse, the transaction/rollback wrapper).
//
// WHAT 'route' PROVES, re-read from the model after the commit:
//   - every created segment's two endpoints match the path RouteSearch (Core,
//     Revit-free, unit-tested in RouteSearchTests.cs) returned, within 1 mm.
//   - every elbow placed between two consecutive segments has both its
//     connectors CONNECTED (NewElbowFitting is asked, not trusted).
//   - a spatial check (SpatialCoherence, the same engine horizun_verify_changes
//     uses) against every element the route created finds NO error against a
//     physical host or a loaded link. Any error throws inside Apply(), which
//     the shared wrapper in MepRoutingCommand.cs rolls back and reports by
//     name - this file adds no rollback code of its own.
//
// OBSTACLES. Physical host elements (SpatialCoherence.IsPhysical - excludes
// element types, view-specific and non-Model categories) and elements of every
// LOADED link, both read from a box around start/end inflated by a margin wide
// enough for RouteSearch's own default search box (MarginSteps grid steps),
// so nothing the search could reach through is missed. Each obstacle box is
// the element's own bounding box (world space for links, via GetTotalTransform)
// inflated by clearance_mm + half the run's own size - the search must keep
// clearance_mm of AIR around the new pipe, not around its centreline.
// An unloaded link is not observable (measured elsewhere in this codebase,
// see workset-cerrado-en-vinculo-no-observable) and is skipped, not refused.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class MepRoutingCommand
    {
        private const double MmToFeet = 1.0 / 304.8;

        private sealed class RoutePlan : WritePlan
        {
            private string _kind;
            private ElementId _typeId, _systemTypeId, _levelId;
            private double? _diameter, _width, _height;
            private RouteSearch.Result _result;
            private readonly List<SegRecord> _segments = new List<SegRecord>();
            private readonly List<ElementId> _elbowIds = new List<ElementId>();
            private readonly List<ElementId> _createdIds = new List<ElementId>();

            private struct SegRecord { public ElementId Id; public XYZ Start; public XYZ End; }

            public override int Count => _createdIds.Count;

            public static RoutePlan Build(Document doc, JObject request, Units u, out string error)
            {
                error = null;
                string kind = (request.Value<string>("kind") ?? "").Trim().ToLowerInvariant();
                if (kind != "pipe" && kind != "duct" && kind != "conduit" && kind != "cable_tray")
                { error = "kind must be pipe, duct, conduit or cable_tray."; return null; }

                long typeIdRaw = request.Value<long?>("type_id") ?? -1;
                if (typeIdRaw < 0 || !Rid.CanRepresent(typeIdRaw)) { error = "type_id is required for route."; return null; }
                ElementId typeId = Rid.Make(typeIdRaw);
                if (doc.GetElement(typeId) == null) { error = "type_id " + typeIdRaw + " does not resolve to an element."; return null; }

                ElementId systemTypeId = null;
                if (kind == "pipe" || kind == "duct")
                {
                    long sysRaw = request.Value<long?>("system_type_id") ?? -1;
                    if (sysRaw < 0 || !Rid.CanRepresent(sysRaw)) { error = "system_type_id is required for kind pipe or duct."; return null; }
                    systemTypeId = Rid.Make(sysRaw);
                    if (doc.GetElement(systemTypeId) == null) { error = "system_type_id " + sysRaw + " does not resolve."; return null; }
                }

                long levelRaw = request.Value<long?>("level_id") ?? -1;
                if (levelRaw < 0 || !Rid.CanRepresent(levelRaw)) { error = "level_id is required for route."; return null; }
                ElementId levelId = Rid.Make(levelRaw);
                if (!(doc.GetElement(levelId) is Level)) { error = "level_id " + levelRaw + " is not a Level."; return null; }

                double? diaMm = request.Value<double?>("diameter");
                double? wMm = request.Value<double?>("width"), hMm = request.Value<double?>("height");
                if (diaMm == null && (wMm == null || hMm == null)) { error = "size requires diameter, or width and height, in mm."; return null; }
                if (diaMm != null && (wMm != null || hMm != null)) { error = "give diameter OR width/height, not both."; return null; }
                double? diameterFt = diaMm.HasValue ? diaMm.Value * MmToFeet : (double?)null;
                double? widthFt = wMm.HasValue ? wMm.Value * MmToFeet : (double?)null;
                double? heightFt = hMm.HasValue ? hMm.Value * MmToFeet : (double?)null;
                if ((diameterFt.HasValue && diameterFt.Value <= 0) || (widthFt.HasValue && widthFt.Value <= 0) || (heightFt.HasValue && heightFt.Value <= 0))
                { error = "diameter, width and height must be positive."; return null; }

                XYZ start = ParsePoint(request["start"], "start", ref error);
                if (error != null) return null;
                XYZ end = ParsePoint(request["end"], "end", ref error);
                if (error != null) return null;

                double clearanceMm = request.Value<double?>("clearance_mm") ?? 50.0;
                double gridMm = request.Value<double?>("grid_mm") ?? 100.0;
                if (clearanceMm < 0 || gridMm <= 0) { error = "clearance_mm must be >= 0 and grid_mm must be > 0."; return null; }
                double clearanceFt = clearanceMm * MmToFeet, gridFt = gridMm * MmToFeet;
                int maxNodes = request.Value<int?>("max_nodes") ?? RouteSearch.DefaultMaxNodes;
                if (maxNodes <= 0) { error = "max_nodes must be positive."; return null; }

                double halfRun = diameterFt.HasValue ? diameterFt.Value / 2.0 : Math.Max(widthFt.Value, heightFt.Value) / 2.0;
                double inflate = clearanceFt + halfRun;

                var reqStart = new RouteSearch.Point3(start.X, start.Y, start.Z);
                var reqEnd = new RouteSearch.Point3(end.X, end.Y, end.Z);
                int marginSteps = 6; // RouteSearch's own default when SearchBounds is not given
                double margin = (marginSteps + 6) * gridFt + inflate; // generous superset: the search's own box, plus room
                List<RouteSearch.Box3> obstacles = CollectObstacles(doc, start, end, margin, inflate);

                var searchReq = new RouteSearch.Request
                {
                    Start = reqStart, End = reqEnd, Obstacles = obstacles, GridSize = gridFt, MaxNodes = maxNodes, MarginSteps = marginSteps
                };
                JToken pref = request["preferred_elevation"];
                double? pMinMm = pref?["min_mm"]?.Value<double>(), pMaxMm = pref?["max_mm"]?.Value<double>();
                if (pMinMm.HasValue) searchReq.PreferredMinZ = pMinMm.Value * MmToFeet;
                if (pMaxMm.HasValue) searchReq.PreferredMaxZ = pMaxMm.Value * MmToFeet;

                RouteSearch.Result result = RouteSearch.Find(searchReq);
                if (!result.Found)
                {
                    error = "no_route: " + (result.Reason ?? "no path was found within max_nodes") +
                        (result.BlockingRegion.HasValue ? " (blocking region: " + (result.BlockingRegion.Value.Name ?? "unnamed") + ")" : "");
                    return null;
                }

                return new RoutePlan
                {
                    _kind = kind, _typeId = typeId, _systemTypeId = systemTypeId, _levelId = levelId,
                    _diameter = diameterFt, _width = widthFt, _height = heightFt, _result = result
                };
            }

            private static XYZ ParsePoint(JToken token, string name, ref string error)
            {
                var arr = token as JArray;
                if (arr == null || arr.Count != 3) { error = name + " must be [x, y, z] in mm."; return null; }
                try { return new XYZ(arr[0].Value<double>() * MmToFeet, arr[1].Value<double>() * MmToFeet, arr[2].Value<double>() * MmToFeet); }
                catch (Exception ex) { error = name + " must be three finite numbers: " + ex.Message; return null; }
            }

            /// <summary>Physical hosts and loaded-link elements near the search box, as inflated world-space boxes.</summary>
            private static List<RouteSearch.Box3> CollectObstacles(Document doc, XYZ start, XYZ end, double margin, double inflate)
            {
                var boxes = new List<RouteSearch.Box3>();
                var min = new XYZ(Math.Min(start.X, end.X) - margin, Math.Min(start.Y, end.Y) - margin, Math.Min(start.Z, end.Z) - margin);
                var max = new XYZ(Math.Max(start.X, end.X) + margin, Math.Max(start.Y, end.Y) + margin, Math.Max(start.Z, end.Z) + margin);
                Outline outline;
                try { outline = new Outline(min, max); } catch { return boxes; }
                var bboxFilter = new BoundingBoxIntersectsFilter(outline);

                foreach (Element e in new FilteredElementCollector(doc).WherePasses(bboxFilter).WhereElementIsNotElementType())
                {
                    if (!SpatialCoherence.IsPhysical(e)) continue;
                    AddBox(boxes, e, null, inflate, "host:" + Rid.Value(e.Id) + ":" + (e.Category?.Name ?? e.GetType().Name));
                }

                foreach (Element linkEl in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    var inst = linkEl as RevitLinkInstance;
                    Document linkedDoc = null;
                    try { linkedDoc = inst?.GetLinkDocument(); } catch { }
                    if (linkedDoc == null) continue; // unloaded: not observable, skipped rather than refused
                    Transform t;
                    try { t = inst.GetTotalTransform(); } catch { continue; }
                    XYZ localA, localB;
                    try { Transform inv = t.Inverse; localA = inv.OfPoint(min); localB = inv.OfPoint(max); } catch { continue; }
                    var localMin = new XYZ(Math.Min(localA.X, localB.X), Math.Min(localA.Y, localB.Y), Math.Min(localA.Z, localB.Z));
                    var localMax = new XYZ(Math.Max(localA.X, localB.X), Math.Max(localA.Y, localB.Y), Math.Max(localA.Z, localB.Z));
                    BoundingBoxIntersectsFilter linkFilter;
                    try { linkFilter = new BoundingBoxIntersectsFilter(new Outline(localMin, localMax)); } catch { continue; }
                    foreach (Element e in new FilteredElementCollector(linkedDoc).WherePasses(linkFilter).WhereElementIsNotElementType())
                    {
                        if (!SpatialCoherence.IsPhysical(e)) continue;
                        AddBox(boxes, e, t, inflate, "link:" + (inst.Name ?? "?") + ":" + Rid.Value(e.Id));
                    }
                }
                return boxes;
            }

            private static void AddBox(List<RouteSearch.Box3> boxes, Element e, Transform worldTransform, double inflate, string name)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }
                if (bb == null) return;
                XYZ mn = bb.Min, mx = bb.Max;
                if (worldTransform != null) { mn = worldTransform.OfPoint(bb.Min); mx = worldTransform.OfPoint(bb.Max); }
                double minX = Math.Min(mn.X, mx.X) - inflate, minY = Math.Min(mn.Y, mx.Y) - inflate, minZ = Math.Min(mn.Z, mx.Z) - inflate;
                double maxX = Math.Max(mn.X, mx.X) + inflate, maxY = Math.Max(mn.Y, mx.Y) + inflate, maxZ = Math.Max(mn.Z, mx.Z) + inflate;
                boxes.Add(new RouteSearch.Box3(minX, minY, minZ, maxX, maxY, maxZ, name));
            }

            public override void Apply(Document doc)
            {
                _segments.Clear(); _elbowIds.Clear(); _createdIds.Clear();
                List<RouteSearch.Point3> poly = _result.Polyline;
                var segments = new List<Element>();
                for (int i = 0; i < poly.Count - 1; i++)
                {
                    XYZ a = ToXyz(poly[i]), b = ToXyz(poly[i + 1]);
                    if (a.DistanceTo(b) < MmToFeet) continue; // two waypoints that landed on the same point
                    Element seg;
                    switch (_kind)
                    {
                        case "pipe": seg = Pipe.Create(doc, _systemTypeId, _typeId, _levelId, a, b); break;
                        case "duct": seg = Duct.Create(doc, _systemTypeId, _typeId, _levelId, a, b); break;
                        case "conduit": seg = Conduit.Create(doc, _typeId, a, b, _levelId); break;
                        case "cable_tray": seg = CableTray.Create(doc, _typeId, a, b, _levelId); break;
                        default: throw new InvalidOperationException("unsupported kind '" + _kind + "'");
                    }
                    ApplySize(seg);
                    segments.Add(seg);
                    _segments.Add(new SegRecord { Id = seg.Id, Start = a, End = b });
                    _createdIds.Add(seg.Id);
                }
                for (int i = 0; i < segments.Count - 1; i++)
                {
                    XYZ bendPoint = ToXyz(poly[i + 1]);
                    Connector cA = OpenConnectorNear(segments[i], bendPoint);
                    Connector cB = OpenConnectorNear(segments[i + 1], bendPoint);
                    if (cA == null || cB == null)
                        throw new InvalidOperationException("segment at bend " + i + " has no open connector to fit an elbow (nothing was rolled back yet - the caller's transaction wrapper does that)");
                    FamilyInstance elbow = doc.Create.NewElbowFitting(cA, cB);
                    _elbowIds.Add(elbow.Id);
                    _createdIds.Add(elbow.Id);
                }
                doc.Regenerate();

                SpatialCoherence.Outcome spatial = SpatialCoherence.Check(doc, SpatialCoherence.Subjects(doc, _createdIds));
                if (spatial.Errors > 0)
                {
                    SpatialCoherence.Finding f = spatial.Findings.FirstOrDefault(x => x.Verdict.Severity == "error");
                    throw new InvalidOperationException("the spatial check found " + spatial.Errors + " error(s) against physical elements after routing" +
                        (f != null ? ": " + f.Verdict.Kind + " (" + f.Verdict.Reason + ") between element " + Rid.Value(f.A.Id) + " and " + Rid.Value(f.B.Id) +
                            (f.LinkB != null ? " (in link '" + f.LinkB + "')" : "") : "") + ". Rolled back.");
                }
            }

            private void ApplySize(Element seg)
            {
                try
                {
                    if (_diameter.HasValue)
                    {
                        BuiltInParameter bip = seg is Pipe ? BuiltInParameter.RBS_PIPE_DIAMETER_PARAM
                            : seg is Conduit ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM
                            : BuiltInParameter.RBS_CURVE_DIAMETER_PARAM; // Duct (round) and CableTray fall back here
                        SetParam(seg, bip, _diameter.Value);
                    }
                    else if (_width.HasValue && _height.HasValue)
                    {
                        SetParam(seg, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, _width.Value);
                        SetParam(seg, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, _height.Value);
                    }
                }
                catch { /* best-effort: sizing is not one of route's required postconditions */ }
            }

            private static void SetParam(Element e, BuiltInParameter bip, double value)
            {
                Parameter p = null;
                try { p = e.get_Parameter(bip); } catch { }
                if (p != null && !p.IsReadOnly) { try { p.Set(value); } catch { } }
            }

            /// <summary>The element's own FREE connector nearest to <paramref name="target"/> - the bend point an elbow goes at.</summary>
            private static Connector OpenConnectorNear(Element seg, XYZ target)
            {
                var mep = seg as MEPCurve;
                ConnectorManager mgr = mep?.ConnectorManager;
                if (mgr == null) return null;
                Connector best = null; double bestDist = double.MaxValue;
                foreach (Connector c in mgr.Connectors)
                {
                    if (c.IsConnected) continue;
                    double d = c.Origin.DistanceTo(target);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
                return best;
            }

            private static XYZ ToXyz(RouteSearch.Point3 p) => new XYZ(p.X, p.Y, p.Z);

            public override PostconditionCheck Verify(Document doc)
            {
                var required = new List<string>();
                foreach (SegRecord s in _segments) { required.Add("segment:" + Rid.Value(s.Id) + ":start"); required.Add("segment:" + Rid.Value(s.Id) + ":end"); }
                foreach (ElementId id in _elbowIds) required.Add("elbow:" + Rid.Value(id) + ":connected");
                var check = new PostconditionCheck(required.ToArray());

                foreach (SegRecord s in _segments)
                {
                    Element live = null;
                    try { live = doc.GetElement(s.Id); } catch { }
                    Curve curve = (live?.Location as LocationCurve)?.Curve;
                    XYZ actualStart = null, actualEnd = null;
                    try { if (curve != null) { actualStart = curve.GetEndPoint(0); actualEnd = curve.GetEndPoint(1); } } catch { }
                    bool startOk = actualStart != null && actualStart.DistanceTo(s.Start) <= MmToFeet;
                    bool endOk = actualEnd != null && actualEnd.DistanceTo(s.End) <= MmToFeet;
                    check.Record("segment:" + Rid.Value(s.Id) + ":start", PointJson(s.Start), actualStart == null ? (JToken)JValue.CreateNull() : PointJson(actualStart), startOk);
                    check.Record("segment:" + Rid.Value(s.Id) + ":end", PointJson(s.End), actualEnd == null ? (JToken)JValue.CreateNull() : PointJson(actualEnd), endOk);
                }
                foreach (ElementId id in _elbowIds)
                {
                    bool connected = false;
                    try
                    {
                        var fi = doc.GetElement(id) as FamilyInstance;
                        ConnectorManager mgr = fi?.MEPModel?.ConnectorManager;
                        if (mgr != null) connected = mgr.Connectors.Cast<Connector>().Count(c => c.IsConnected) >= 2;
                    }
                    catch { }
                    check.Record("elbow:" + Rid.Value(id) + ":connected", true, connected, connected);
                }
                return check;
            }

            private static JToken PointJson(XYZ p) => new JArray(Math.Round(p.X * 304.8, 1), Math.Round(p.Y * 304.8, 1), Math.Round(p.Z * 304.8, 1));

            public override JObject Describe(Units u)
            {
                var poly = new JArray(_result.Polyline.Select(p => (JToken)PointJson(ToXyz(p))));
                return new JObject
                {
                    ["kind"] = _kind, ["type_id"] = Rid.Value(_typeId),
                    ["system_type_id"] = _systemTypeId == null ? (JToken)JValue.CreateNull() : Rid.Value(_systemTypeId),
                    ["level_id"] = Rid.Value(_levelId),
                    ["length_mm"] = Math.Round(_result.Length * 304.8, 1), ["bends"] = _result.Bends,
                    ["nodes_expanded"] = _result.NodesExpanded, ["polyline_mm"] = poly
                };
            }

            public override JToken Report(Document doc, Units u)
            {
                return new JObject
                {
                    ["length_mm"] = Math.Round(_result.Length * 304.8, 1), ["bends"] = _result.Bends,
                    ["polyline_mm"] = new JArray(_result.Polyline.Select(p => (JToken)PointJson(ToXyz(p)))),
                    ["segment_ids"] = new JArray(_segments.Select(s => (JToken)Rid.Value(s.Id))),
                    ["elbow_ids"] = new JArray(_elbowIds.Select(id => (JToken)Rid.Value(id)))
                };
            }

            public override void ResetAfterRehearsal() { _segments.Clear(); _elbowIds.Clear(); _createdIds.Clear(); }

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command) => NewResolved(gate, app, command);
        }
    }
}
