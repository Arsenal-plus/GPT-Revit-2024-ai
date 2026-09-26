// -----------------------------------------------------------------------------
// Horizun Revit MCP - the "hangers" operation of horizun_mep_routing.
// Original Horizun code.
//
// WHAT IT DOES. Places a family type THE CALLER supplies (a non-hosted generic
// model, pipe/duct accessory or specialty equipment - this bridge compiles in no
// organisation's hanger catalogue) along straight pipes, ducts, cable trays and
// conduits: one instance per station, on the run's centreline, turned to the run's
// horizontal direction. Stations come from HangerRules (Core, unit-tested): end
// clearance at both ends, no gap above spacing_mm, none within end_offset_mm of an
// interior fitting (a tap on the run).
//
// THE ROD. For each station a ray goes straight UP from the run's top (centreline
// plus half its outside height) against floors, structural framing and roofs of the
// host document AND of loaded Revit links. The nearest hit within max_rod_mm is the
// support; its distance is the rod length, written to rod_length_parameter when the
// caller names one. A station with nothing above within max_rod_mm is reported as
// no_support_above and NOT placed: a hanger hanging from nothing is a defect this
// operation would otherwise create.
//
// WHAT IS PROVEN after the commit, re-read from the model: every instance exists
// with the requested type, its position (X/Y from its location, Z from its level
// plus the offset that governs it) within 1 mm, its rotation within 0.5 degree, the
// rod parameter within 1 mm, and placed == planned.
//
// NOT MEASURED YET (live probe mep-hangers.probes.ps1 measures it): whether
// ReferenceIntersector honours the ray view's visibility/section box for linked
// structure, and which offset parameter a work-plane-based type exposes after a
// level placement. The code is defensive on both; the verification decides.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class MepRoutingCommand
    {
        private sealed class HangersPlan : WritePlan
        {
            private const double MmPerFoot = 304.8;
            private const double PositionTolFeet = 1.0 / MmPerFoot;          // 1 mm
            private const double RotationTolRad = 0.5 * Math.PI / 180.0;     // 0.5 degree
            private const int MaxStations = 1000;

            private static readonly BuiltInCategory[] HangerCategories =
            {
                BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_SpecialityEquipment
            };

            private static readonly BuiltInCategory[] StructureCategories =
            {
                BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Roofs
            };

            private sealed class Station
            {
                public string Key; public long RunId; public int Index;
                public double Along; public XYZ Point; public double Angle; public Level Level;
                public double RodFeet; public string SupportKind; public long SupportId; public long? LinkInstanceId; public string SupportCategory;
                public ElementId Created;
            }

            private readonly List<Station> _stations = new List<Station>();
            private readonly List<JObject> _runs = new List<JObject>();
            private ElementId _symbolId;
            private string _symbolName, _rodName, _viewName;
            private double _spacingMm, _endOffsetMm, _maxRodMm;
            private int _linksLoaded, _noSupport;

            public override int Count => _stations.Count;

            public static WritePlan Build(UIApplication app, Document doc, JObject request, out string error, out CommandResult refusal)
            {
                error = null; refusal = null;
                string attach = (request.Value<string>("attach") ?? "structure_above").Trim().ToLowerInvariant();
                if (attach != "structure_above") { error = "attach must be structure_above - the only support this operation measures."; return null; }
                if (request["system_id"] != null) { error = "hangers takes element_ids, not system_id: name the runs to support."; return null; }
                double? spacing = request.Value<double?>("spacing_mm"), endOffset = request.Value<double?>("end_offset_mm");
                double maxRod = request.Value<double?>("max_rod_mm") ?? 3000;
                if (spacing == null || !(spacing > 0) || double.IsInfinity(spacing.Value)) { error = "hangers needs spacing_mm > 0 (the maximum distance between supports)."; return null; }
                if (endOffset == null || endOffset < 0 || double.IsInfinity(endOffset.Value)) { error = "hangers needs end_offset_mm >= 0 (the clearance from each run end and each fitting)."; return null; }
                if (!(maxRod > 0) || double.IsInfinity(maxRod)) { error = "max_rod_mm must be positive."; return null; }

                long typeId = request.Value<long?>("hanger_type_id") ?? -1;
                var symbol = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as FamilySymbol : null;
                if (symbol == null) { error = "hanger_type_id " + typeId + " is not a loaded family type."; return null; }
                long cat = symbol.Category == null ? -1 : Rid.Value(symbol.Category.Id);
                if (!HangerCategories.Any(c => (long)(int)c == cat))
                { error = "hanger_type_id " + typeId + " is " + (symbol.Category?.Name ?? "uncategorised") + "; hangers places generic models, pipe/duct accessories or specialty equipment."; return null; }
                FamilyPlacementType placement = symbol.Family.FamilyPlacementType;
                if (placement != FamilyPlacementType.OneLevelBased && placement != FamilyPlacementType.WorkPlaneBased)
                { error = "hanger_type_id " + typeId + " is placed " + placement + "; hangers places non-hosted level-based or work-plane-based families."; return null; }
                string rodName = (request.Value<string>("rod_length_parameter") ?? "").Trim();
                if (rodName.Length == 0) rodName = null;
                if (rodName != null && symbol.LookupParameter(rodName) != null)
                { error = "rod_length_parameter '" + rodName + "' is a TYPE parameter of " + symbol.Name + ": one value per type cannot carry a per-station rod length."; return null; }

                List<MEPCurve> targets = Targets(doc, request, out error);
                if (targets == null) return null;
                View3D view = RayView(doc);
                if (view == null) { error = "no non-template, non-perspective 3D view exists to cast the support rays from; create one first."; return null; }

                var p = new HangersPlan
                {
                    _symbolId = symbol.Id, _symbolName = (symbol.FamilyName ?? "") + ": " + symbol.Name, _rodName = rodName, _viewName = view.Name,
                    _spacingMm = spacing.Value, _endOffsetMm = endOffset.Value, _maxRodMm = maxRod,
                    _linksLoaded = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Count(l => Safe(() => l.GetLinkDocument()) != null)
                };
                var filter = new ElementMulticategoryFilter(StructureCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }).ToList());
                var ray = new ReferenceIntersector(filter, FindReferenceTarget.Face, view) { FindReferencesInRevitLinks = true };
                double spacingFt = spacing.Value / MmPerFoot, offsetFt = endOffset.Value / MmPerFoot, maxRodFt = maxRod / MmPerFoot;

                var outcomes = new List<ActionOutcome>();
                for (int i = 0; i < targets.Count; i++)
                {
                    MEPCurve e = targets[i]; long id = Rid.Value(e.Id);
                    var outcome = new ActionOutcome { Index = i }; outcomes.Add(outcome);
                    string kind = e is Pipe ? "pipe" : e is Duct ? "duct" : e is CableTray ? "cable_tray" : e is Conduit ? "conduit" : null;
                    Line line = (e.Location as LocationCurve)?.Curve as Line;
                    if (kind == null || line == null)
                    { outcome.Error = "element " + id + " is not a straight pipe, duct, cable tray or conduit (flex runs have no straight axis to space supports along)"; outcome.UnsupportedReason = FallbackSignal.ReasonUnsupportedCapability; continue; }
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1); double length = a.DistanceTo(b); XYZ dir = (b - a).Normalize();
                    if (Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y) < 1e-6)
                    { outcome.Error = "element " + id + " is vertical: a riser is supported by riser clamps, not by hangers from the structure above"; outcome.UnsupportedReason = FallbackSignal.ReasonUnsupportedCapability; continue; }
                    Level level = Safe(() => e.ReferenceLevel) ?? doc.GetElement(e.LevelId) as Level;
                    if (level == null) { outcome.Error = "element " + id + " has no reference level to place its hangers on"; continue; }

                    double half = HalfHeight(e), angle = Math.Atan2(dir.Y, dir.X);
                    List<double> fittings = InteriorFittings(e, a, dir, length);
                    List<double> along = HangerRules.ComputeStations(length, offsetFt, spacingFt, fittings);
                    var row = new JObject { ["element_id"] = id, ["kind"] = kind, ["length_mm"] = Mm(length), ["interior_fittings"] = fittings.Count, ["stations"] = along.Count };
                    if (along.Count == 0) row["skipped"] = "shorter than 2 x end_offset_mm: no station keeps the clearance from both ends";
                    var missing = new JArray();
                    for (int k = 0; k < along.Count; k++)
                    {
                        XYZ point = a + dir * along[k];
                        var s = new Station { Key = id + "@" + k, RunId = id, Index = k, Along = along[k], Point = point, Angle = angle, Level = level };
                        if (!FindSupport(doc, ray, point + XYZ.BasisZ * half, maxRodFt, s))
                        { missing.Add(new JObject { ["station"] = k, ["along_mm"] = Mm(along[k]), ["point_mm"] = MmPoint(point) }); p._noSupport++; continue; }
                        p._stations.Add(s);
                    }
                    if (missing.Count > 0) row["no_support_above"] = missing;
                    p._runs.Add(row);
                }
                if (outcomes.Any(o => o.Failed))
                {
                    refusal = FallbackDecision.Refuse(string.Join("; ", outcomes.Where(o => o.Failed).Select(o => o.Error)) + ". Nothing was written.",
                        FallbackDecision.Decide(outcomes, writeStarted: false));
                    return null;
                }
                if (p._stations.Count > MaxStations) { error = p._stations.Count + " stations planned; at most " + MaxStations + " hangers per call - split the runs."; return null; }
                if (p._stations.Count == 0)
                {
                    error = "no station can carry a hanger: " + p._runs.Count(r => r["skipped"] != null) + " run(s) shorter than 2 x end_offset_mm, " +
                            p._noSupport + " station(s) with no floor, framing or roof above within " + maxRod + " mm (ray view '" + view.Name + "').";
                    return null;
                }
                return p;
            }

            public override void Apply(Document doc)
            {
                var symbol = doc.GetElement(_symbolId) as FamilySymbol ?? throw new InvalidOperationException("the hanger type no longer exists");
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                foreach (Station s in _stations)
                    s.Created = doc.Create.NewFamilyInstance(s.Point, symbol, s.Level, StructuralType.NonStructural).Id;
                doc.Regenerate();
                foreach (Station s in _stations)
                {
                    var fi = doc.GetElement(s.Created) as FamilyInstance ?? throw new InvalidOperationException("station " + s.Key + ": Revit did not create the instance");
                    if (!(fi.Location is LocationPoint lp)) throw new InvalidOperationException("station " + s.Key + ": the hanger family has no point location");
                    double rotationBefore = lp.Rotation; XYZ at = lp.Point;
                    var dxy = new XYZ(s.Point.X - at.X, s.Point.Y - at.Y, 0);
                    if (dxy.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, fi.Id, dxy);
                    // THE HEIGHT IS GOVERNED BY AN OFFSET, NOT BY THE LOCATION (same rule as
                    // create_elements' PositionInstance): set the offset where one exists.
                    Level baseLevel = doc.GetElement(fi.LevelId) as Level;
                    Parameter offset = OffsetParameter(fi);
                    if (baseLevel != null && offset != null && !offset.IsReadOnly)
                    {
                        double want = s.Point.Z - baseLevel.ProjectElevation;
                        if (Math.Abs(offset.AsDouble() - want) > 1e-9 && !offset.Set(want))
                            throw new InvalidOperationException("station " + s.Key + ": Revit refused the offset that sets the hanger's height");
                    }
                    else if (Math.Abs(s.Point.Z - at.Z) > 1e-9) ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, s.Point.Z - at.Z));
                    double turn = s.Angle - rotationBefore;
                    if (Math.Abs(turn) > 1e-9) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(s.Point, s.Point + XYZ.BasisZ), turn);
                    if (_rodName != null)
                    {
                        Parameter rod = fi.LookupParameter(_rodName);
                        if (rod == null) throw new InvalidOperationException("the hanger family has no instance parameter named '" + _rodName + "'");
                        if (rod.IsReadOnly || rod.StorageType != StorageType.Double) throw new InvalidOperationException("'" + _rodName + "' is read-only or not a length on the hanger instance");
                        if (!rod.Set(s.RodFeet)) throw new InvalidOperationException("station " + s.Key + ": Revit refused " + _rodName);
                    }
                }
            }

            public override PostconditionCheck Verify(Document doc)
            {
                var required = new List<string> { "count" };
                foreach (Station s in _stations)
                {
                    required.Add("position:" + s.Key); required.Add("rotation:" + s.Key);
                    if (_rodName != null) required.Add("rod:" + s.Key);
                }
                var check = new PostconditionCheck(required.ToArray());
                int placed = 0;
                foreach (Station s in _stations)
                {
                    var fi = s.Created == null ? null : doc.GetElement(s.Created) as FamilyInstance;
                    if (fi == null || fi.GetTypeId() != _symbolId || !(fi.Location is LocationPoint lp))
                    {
                        string why = fi == null ? "the hanger does not exist" : fi.GetTypeId() != _symbolId ? "the instance is not of hanger_type_id" : "the instance has no point location";
                        check.Unreadable("position:" + s.Key, MmPoint(s.Point), why); check.Unreadable("rotation:" + s.Key, Deg(s.Angle), why);
                        if (_rodName != null) check.Unreadable("rod:" + s.Key, Mm(s.RodFeet), why);
                        continue;
                    }
                    placed++;
                    double z = lp.Point.Z;
                    Level baseLevel = doc.GetElement(fi.LevelId) as Level; Parameter offset = OffsetParameter(fi);
                    if (baseLevel != null && offset != null && offset.HasValue) z = baseLevel.ProjectElevation + offset.AsDouble();
                    var found = new XYZ(lp.Point.X, lp.Point.Y, z);
                    check.Record("position:" + s.Key, MmPoint(s.Point), MmPoint(found), found.DistanceTo(s.Point) <= PositionTolFeet);
                    double diff = Math.IEEERemainder(lp.Rotation - s.Angle, 2 * Math.PI);
                    check.Record("rotation:" + s.Key, Deg(s.Angle), Deg(lp.Rotation), Math.Abs(diff) <= RotationTolRad);
                    if (_rodName != null)
                    {
                        Parameter rod = fi.LookupParameter(_rodName);
                        if (rod == null || !rod.HasValue) check.Unreadable("rod:" + s.Key, Mm(s.RodFeet), "'" + _rodName + "' did not re-read");
                        else check.Record("rod:" + s.Key, Mm(s.RodFeet), Mm(rod.AsDouble()), Math.Abs(rod.AsDouble() - s.RodFeet) <= PositionTolFeet);
                    }
                }
                check.Record("count", _stations.Count, placed, placed == _stations.Count);
                return check;
            }

            public override JToken Report(Document doc, Units u) => new JObject
            {
                ["planned"] = _stations.Count,
                ["placed"] = new JArray(_stations.Take(MaxListed).Select(s => Row(s, true))),
                ["listed"] = Math.Min(MaxListed, _stations.Count),
                ["no_support_above"] = _noSupport
            };

            public override void ResetAfterRehearsal() { foreach (Station s in _stations) s.Created = null; }

            public override JObject Describe(Units u) => new JObject
            {
                ["hanger_type"] = new JObject { ["id"] = Rid.Value(_symbolId), ["name"] = _symbolName },
                ["spacing_mm"] = _spacingMm, ["end_offset_mm"] = _endOffsetMm, ["max_rod_mm"] = _maxRodMm,
                ["rod_length_parameter"] = _rodName == null ? (JToken)JValue.CreateNull() : _rodName,
                ["rod_measured_from"] = "the run's top (centreline + half its outside height) up to the underside of the support",
                ["ray_view"] = _viewName, ["loaded_links_searched"] = _linksLoaded,
                ["planned"] = _stations.Count, ["no_support_above"] = _noSupport,
                ["runs"] = new JArray(_runs.Take(MaxListed)),
                ["stations"] = new JArray(_stations.Take(MaxListed).Select(s => Row(s, false)))
            };

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command)
            {
                var rp = NewResolved(gate, app, command);
                foreach (Station s in _stations)
                    rp.Elements.Add(new PlannedElement
                    {
                        Category = "hanger", TypeName = _symbolName, Level = s.Level?.Name, Action = PlannedAction.Create,
                        ProposedValues = new Dictionary<string, string> { ["station"] = s.Key, ["point_mm"] = MmPoint(s.Point).ToString(Newtonsoft.Json.Formatting.None), ["rod_mm"] = R(Mm(s.RodFeet)) }
                    });
                return rp;
            }

            private JObject Row(Station s, bool withId)
            {
                var row = new JObject
                {
                    ["station"] = s.Key, ["run_id"] = s.RunId, ["along_mm"] = Mm(s.Along), ["point_mm"] = MmPoint(s.Point),
                    ["rotation_deg"] = Deg(s.Angle), ["rod_mm"] = Mm(s.RodFeet),
                    ["support"] = new JObject
                    {
                        ["source"] = s.SupportKind, ["element_id"] = s.SupportId, ["category"] = s.SupportCategory,
                        ["link_instance_id"] = s.LinkInstanceId.HasValue ? (JToken)s.LinkInstanceId.Value : JValue.CreateNull()
                    }
                };
                if (withId) row["element_id"] = s.Created == null ? (JToken)JValue.CreateNull() : Rid.Value(s.Created);
                return row;
            }

            /// <summary>Nearest floor, framing or roof straight above `origin` within maxRod, host or loaded link.</summary>
            private static bool FindSupport(Document doc, ReferenceIntersector ray, XYZ origin, double maxRod, Station s)
            {
                IList<ReferenceWithContext> hits;
                try { hits = ray.Find(origin, XYZ.BasisZ); } catch { return false; }
                foreach (ReferenceWithContext h in hits.OrderBy(x => x.Proximity))
                {
                    if (h.Proximity <= 1e-6) continue;
                    if (h.Proximity > maxRod) break;
                    Reference r = h.GetReference(); if (r == null) continue;
                    Element e = doc.GetElement(r.ElementId);
                    if (e is RevitLinkInstance link)
                    {
                        if (r.LinkedElementId == ElementId.InvalidElementId) continue;
                        Element linked = Safe(() => link.GetLinkDocument())?.GetElement(r.LinkedElementId);
                        if (!IsStructure(linked)) continue;
                        s.SupportKind = "linked"; s.SupportId = Rid.Value(linked.Id); s.LinkInstanceId = Rid.Value(link.Id); s.SupportCategory = linked.Category.Name;
                    }
                    else
                    {
                        if (!IsStructure(e)) continue;
                        s.SupportKind = "host"; s.SupportId = Rid.Value(e.Id); s.SupportCategory = e.Category.Name;
                    }
                    s.RodFeet = h.Proximity;
                    return true;
                }
                return false;
            }

            private static bool IsStructure(Element e)
            {
                long cat = e?.Category == null ? -1 : Rid.Value(e.Category.Id);
                return StructureCategories.Any(c => (long)(int)c == cat);
            }

            private static View3D RayView(Document doc)
            {
                List<View3D> views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .Where(v => !v.IsTemplate && !v.IsPerspective).OrderBy(v => Rid.Value(v.Id)).ToList();
                return views.FirstOrDefault(v => !Safe(() => v.IsSectionBoxActive)) ?? views.FirstOrDefault();
            }

            /// <summary>Half the run's outside height: outer diameter for pipes/conduits, height for rectangular sections.</summary>
            private static double HalfHeight(MEPCurve e)
            {
                Parameter outer = e.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER) ?? e.get_Parameter(BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM);
                if (outer != null && outer.HasValue && outer.AsDouble() > 0) return outer.AsDouble() / 2;
                double h = Safe(() => e.Height); if (h > 0) return h / 2;
                double d = Safe(() => e.Diameter); return d > 0 ? d / 2 : 0;
            }

            /// <summary>Distances along the run of connected connectors that are NOT at its ends (taps).</summary>
            private static List<double> InteriorFittings(MEPCurve e, XYZ start, XYZ dir, double length)
            {
                var list = new List<double>();
                foreach (Connector c in MepFacts.Ordered(Safe(() => e.ConnectorManager)))
                {
                    if (!Safe(() => (bool?)c.IsConnected).GetValueOrDefault()) continue;
                    XYZ o = Safe(() => c.Origin); if (o == null) continue;
                    double t = (o - start).DotProduct(dir);
                    if (t > PositionTolFeet && t < length - PositionTolFeet) list.Add(t);
                }
                return list;
            }

            private static Parameter OffsetParameter(FamilyInstance fi)
            {
                Parameter p = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (p != null && !p.IsReadOnly) return p;
                p = fi.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                return p != null && !p.IsReadOnly ? p : null;
            }

            private static double Mm(double feet) => Math.Round(feet * MmPerFoot, 1);
            private static double Deg(double rad) => Math.Round(rad * 180.0 / Math.PI, 2);
            private static JArray MmPoint(XYZ p) => new JArray(Mm(p.X), Mm(p.Y), Mm(p.Z));
        }
    }
}
