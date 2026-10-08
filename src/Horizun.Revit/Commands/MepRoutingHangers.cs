// -----------------------------------------------------------------------------
// Horizun Revit MCP - the "hangers" operation of horizun_mep_routing.
// Original Horizun code.
//
// WHAT IT DOES. Places a family type THE CALLER supplies (a non-hosted generic
// model, pipe/duct accessory or specialty equipment - this bridge compiles in no
// organisation's hanger catalogue) along straight pipes, ducts, cable trays and
// conduits: one instance per station, on the run's centreline, turned to the run's
// horizontal direction. Stations come from HangerRules (Core, unit-tested): end
// clearance at both ends, none inside end_offset_mm of an interior fitting (a tap)
// - end stations included - and no gap above spacing_mm inside a free span. A gap
// that cannot be closed (across a tap's clearance zone wider than the spacing, or
// where a station has nothing above it) is NAMED in gaps_above_spacing, never hidden.
//
// THE COUNT IS BOUNDED BEFORE ANY RAY. Pass 1 is arithmetic only: every run is
// classified and its stations counted against MaxStations; a spacing in the wrong
// unit refuses there, before Revit's UI thread casts a single ray.
//
// THE ROD. For each station a ray goes straight UP from the run's top (centreline
// plus half its outside height) against floors, structural framing and roofs of the
// host document AND of loaded Revit links. ReferenceIntersector returns only what
// its view shows (RevitAPI.xml: hidden elements and elements outside the section box
// are never returned), so the rays are cast in a TEMPORARY isometric view created in
// a transaction that is always rolled back: no template, no filters, no section box,
// the structure and link categories visible, every open user workset visible, the
// run's own phase and a phase filter that hides demolished elements. The nearest hit
// within max_rod_mm is the support; its distance is the rod length, written to
// rod_length_parameter (an instance Length parameter) when the caller names one. A
// station with nothing above is reported as no_support_above and NOT placed; a ray
// that could not be cast refuses the call (no measurement is not "nothing above").
//
// Risers are skipped and reported (they take riser clamps, not hangers); flex runs
// are the only capability gap that grants the Python fallback.
//
// WHAT IS PROVEN after the commit, re-read from the model: every instance exists
// with the requested type, its position (X/Y from its location, Z from its level
// plus the offset that governs it) within 1 mm, its rotation within 0.5 degree, the
// rod parameter within 1 mm, and placed == planned.
//
// NOT MEASURED YET (live probe mep-hangers.probes.ps1 measures what it can): that
// linked structure is hit through the temporary view, and which offset parameter a
// work-plane-based type exposes after a level placement. Closed worksets are not
// loaded at all, so they are listed rather than searched.
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

            private sealed class RunPlan
            {
                public MEPCurve Run; public long Id; public XYZ A; public XYZ Dir; public double Half; public double Angle; public Level Level;
                public HangerStationPlan Stations; public JObject Row;
            }

            private readonly List<Station> _stations = new List<Station>();
            private readonly List<JObject> _runs = new List<JObject>();
            private readonly SortedSet<string> _viewPhases = new SortedSet<string>(), _closedWorksets = new SortedSet<string>();
            private ElementId _symbolId;
            private string _symbolName, _rodName, _phaseFilter;
            private double _spacingMm, _endOffsetMm, _maxRodMm;
            private int _linksLoaded, _noSupport, _risers, _wideGaps, _viewCount;

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
                if (endOffset == null || !(endOffset >= 0) || double.IsInfinity(endOffset.Value)) { error = "hangers needs end_offset_mm >= 0 (the clearance from each run end and each fitting)."; return null; }
                if (!(maxRod > 0) || double.IsInfinity(maxRod)) { error = "max_rod_mm must be positive."; return null; }

                long typeId = request.Value<long?>("hanger_type_id") ?? -1;
                var symbol = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as FamilySymbol : null;
                if (symbol == null) { error = "hanger_type_id " + typeId + " is not a loaded family type."; return null; }
                long cat = symbol.Category == null ? -1 : Rid.Value(symbol.Category.Id);
                if (!HangerCategories.Any(c => (long)(int)c == cat))
                { error = "hanger_type_id " + typeId + " is " + (symbol.Category?.Name ?? "uncategorised") + "; hangers places generic models, pipe/duct accessories or specialty equipment."; return null; }
                // LEVEL-BASED ONLY. MEASURED 2026-09-26 in Revit 2023: a work-plane-based generic
                // model placed through NewFamilyInstance(point, symbol, level) stayed at z=0 - no
                // writable offset and a Z move that did not take - so every station failed its
                // re-read and the rehearsal refused. Refused here, before anything is placed, the
                // same way create_elements refuses a hostless work-plane placement.
                FamilyPlacementType placement = symbol.Family.FamilyPlacementType;
                if (placement != FamilyPlacementType.OneLevelBased)
                { error = "hanger_type_id " + typeId + " (" + symbol.FamilyName + ") is placed " + placement + "; hangers places level-based families only - a work-plane- or face-based family cannot be raised to the run's height here. Use a level-based generic model or accessory type."; return null; }
                string rodName = (request.Value<string>("rod_length_parameter") ?? "").Trim();
                if (rodName.Length == 0) rodName = null;
                if (rodName != null && ParameterResolver.ForWrite(symbol, rodName) != null)
                { error = "rod_length_parameter '" + rodName + "' is a TYPE parameter of " + symbol.Name + ": one value per type cannot carry a per-station rod length."; return null; }

                List<MEPCurve> targets = Targets(doc, request, out error);
                if (targets == null) return null;
                // Targets() sorts by id; a capability gap must name the caller's OWN index.
                var requestIndex = new Dictionary<long, int>();
                if (request["element_ids"] is JArray requested)
                    for (int i = 0; i < requested.Count; i++) { long rid = requested[i].Value<long>(); if (!requestIndex.ContainsKey(rid)) requestIndex[rid] = i; }

                var p = new HangersPlan
                {
                    _symbolId = symbol.Id, _symbolName = (symbol.FamilyName ?? "") + ": " + symbol.Name, _rodName = rodName,
                    _spacingMm = spacing.Value, _endOffsetMm = endOffset.Value, _maxRodMm = maxRod,
                    _linksLoaded = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Count(l => Safe(() => l.GetLinkDocument()) != null)
                };
                double spacingFt = spacing.Value / MmPerFoot, offsetFt = endOffset.Value / MmPerFoot, maxRodFt = maxRod / MmPerFoot;

                // PASS 1 - arithmetic only, no ray: classify every run and count its stations
                // against the budget.
                var outcomes = new List<ActionOutcome>(); var runs = new List<RunPlan>();
                int planned = 0; bool overBudget = false;
                for (int i = 0; i < targets.Count; i++)
                {
                    MEPCurve e = targets[i]; long id = Rid.Value(e.Id);
                    var outcome = new ActionOutcome { Index = requestIndex.TryGetValue(id, out int at) ? at : i }; outcomes.Add(outcome);
                    if (e is FlexPipe || e is FlexDuct)
                    { outcome.Error = "element " + id + " is a flex run: it has no straight axis to space supports along"; outcome.UnsupportedReason = FallbackSignal.ReasonUnsupportedCapability; continue; }
                    string kind = e is Pipe ? "pipe" : e is Duct ? "duct" : e is CableTray ? "cable_tray" : e is Conduit ? "conduit" : null;
                    // A wire or any other MEPCurve is out of scope, not a capability gap: no fallback grant.
                    if (kind == null) { outcome.Error = "element " + id + " is not a pipe, duct, cable tray or conduit; hangers supports those only"; continue; }
                    Line line = (e.Location as LocationCurve)?.Curve as Line;
                    if (line == null) { outcome.Error = "element " + id + " has no straight location line"; continue; }
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1); double length = a.DistanceTo(b); XYZ dir = (b - a).Normalize();
                    var row = new JObject { ["element_id"] = id, ["kind"] = kind, ["length_mm"] = Mm(length) };
                    if (Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y) < 1e-6)
                    {
                        // A riser takes riser clamps, not hangers from above: skipped and reported, and
                        // never a fallback grant (Python placing them would be the same defect).
                        row["stations"] = 0; row["skipped"] = "vertical run (riser): supported by riser clamps, not by hangers from the structure above";
                        p._risers++; p._runs.Add(row); continue;
                    }
                    Level level = Safe(() => e.ReferenceLevel) ?? doc.GetElement(e.LevelId) as Level;
                    if (level == null) { outcome.Error = "element " + id + " has no reference level to place its hangers on"; continue; }

                    List<double> fittings = InteriorFittings(e, a, dir, length);
                    HangerStationPlan sp = HangerRules.Plan(length, offsetFt, spacingFt, fittings, Math.Max(0, MaxStations - planned));
                    if (sp.OverBudget) { overBudget = true; continue; }
                    planned += sp.Stations.Count;
                    row["interior_fittings"] = fittings.Count; row["stations"] = sp.Stations.Count;
                    if (sp.TooShort) row["skipped"] = "shorter than 2 x end_offset_mm: no station keeps the clearance from both ends";
                    else if (sp.NoFreeSpan) row["skipped"] = "every point of the run is within end_offset_mm of an end or a tap fitting";
                    if (sp.StartShift > 1e-9 || sp.EndShift > 1e-9) row["end_stations_moved_mm"] = new JArray(Mm(sp.StartShift), Mm(sp.EndShift));
                    runs.Add(new RunPlan { Run = e, Id = id, A = a, Dir = dir, Half = HalfHeight(e), Angle = Math.Atan2(dir.Y, dir.X), Level = level, Stations = sp, Row = row });
                    p._runs.Add(row);
                }
                if (outcomes.Any(o => o.Failed))
                {
                    refusal = FallbackDecision.Refuse(string.Join("; ", outcomes.Where(o => o.Failed).Select(o => o.Error)) + ". Nothing was written.",
                        FallbackDecision.Decide(outcomes, writeStarted: false));
                    return null;
                }
                if (overBudget)
                { error = "more than " + MaxStations + " stations at spacing_mm " + spacing.Value + " (is it in millimetres?); at most " + MaxStations + " hangers per call - split the runs."; return null; }

                // PASS 2 - the rays, in temporary views this code configures, rolled back after.
                var rayFailures = new List<string>();
                using (var tx = new Transaction(doc, "Horizun: hangers ray view (rolled back)"))
                {
                    try
                    {
                        if (tx.Start() != TransactionStatus.Started) { error = "the temporary ray view could not be created: no transaction could start."; return null; }
                        var views = new Dictionary<long, View3D>();
                        foreach (RunPlan r in runs.Where(x => x.Stations.Stations.Count > 0))
                        {
                            ElementId phaseId = RunPhase(doc, r.Run);
                            long pk = phaseId == null ? -1 : Rid.Value(phaseId);
                            if (!views.TryGetValue(pk, out View3D view))
                            {
                                view = RayView(doc, phaseId, p, out error);
                                if (view == null) return null;
                                views[pk] = view;
                            }
                            var ray = new ReferenceIntersector(new ElementMulticategoryFilter(StructureCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }).ToList()),
                                FindReferenceTarget.Face, view) { FindReferencesInRevitLinks = true };
                            var missing = new JArray(); var supported = new List<double>();
                            List<double> along = r.Stations.Stations;
                            for (int k = 0; k < along.Count; k++)
                            {
                                XYZ point = r.A + r.Dir * along[k];
                                var s = new Station { Key = r.Id + "@" + k, RunId = r.Id, Index = k, Along = along[k], Point = point, Angle = r.Angle, Level = r.Level };
                                int found = FindSupport(doc, ray, point + XYZ.BasisZ * r.Half, maxRodFt, s, out string why);
                                if (found < 0) { rayFailures.Add("station " + s.Key + ": " + why); continue; }
                                if (found == 0) { missing.Add(new JObject { ["station"] = k, ["along_mm"] = Mm(along[k]), ["point_mm"] = MmPoint(point) }); p._noSupport++; continue; }
                                p._stations.Add(s); supported.Add(along[k]);
                            }
                            if (missing.Count > 0) r.Row["no_support_above"] = missing;
                            // Every gap above spacing_mm among the hangers that WILL exist is named with
                            // its cause: a tap's clearance zone, or a station with nothing above it.
                            var wide = new JArray();
                            foreach (double[] g in HangerRules.GapsAbove(supported, spacingFt))
                            {
                                bool zone = r.Stations.WideGaps.Any(w => Math.Abs(w[0] - g[0]) < 1e-6 && Math.Abs(w[1] - g[1]) < 1e-6);
                                wide.Add(new JObject { ["from_mm"] = Mm(g[0]), ["to_mm"] = Mm(g[1]), ["gap_mm"] = Mm(g[1] - g[0]), ["why"] = zone ? "tap_fitting_clearance" : "no_support_above" });
                                p._wideGaps++;
                            }
                            if (wide.Count > 0) r.Row["gaps_above_spacing"] = wide;
                        }
                        p._viewCount = views.Count;
                    }
                    finally { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); }
                }
                if (rayFailures.Count > 0)
                { error = rayFailures.Count + " support ray(s) could not be cast, so there is no measurement to place them on (" + rayFailures[0] + ")."; return null; }
                if (p._stations.Count == 0)
                {
                    error = "no station can carry a hanger: " + p._runs.Count(r => r["skipped"] != null) + " run(s) skipped (shorter than 2 x end_offset_mm, inside tap clearances, or risers), " +
                            p._noSupport + " station(s) with no floor, framing or roof above within " + maxRod + " mm in the temporary ray view" +
                            (p._closedWorksets.Count > 0 ? " (closed worksets not searched: " + string.Join(", ", p._closedWorksets) + ")" : "") + ".";
                    return null;
                }
                return p;
            }

            public override void Apply(Document doc)
            {
                var symbol = doc.GetElement(_symbolId) as FamilySymbol ?? throw new InvalidOperationException("the hanger type no longer exists");
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                foreach (Station s in _stations)
                {
                    // NewFamilyInstance returns null when creation fails (RevitAPI.xml).
                    FamilyInstance created = doc.Create.NewFamilyInstance(s.Point, symbol, s.Level, StructuralType.NonStructural);
                    if (created == null) throw new InvalidOperationException("station " + s.Key + ": Revit did not create the instance");
                    s.Created = created.Id;
                }
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
                        Parameter rod = ParameterResolver.ForWrite(fi, _rodName);
                        if (rod == null) throw new InvalidOperationException("the hanger family has no instance parameter named '" + _rodName + "'");
                        // A Number or Angle parameter would take the feet value and still re-read equal: only a Length is a rod.
                        if (rod.IsReadOnly || rod.StorageType != StorageType.Double || !IsLength(rod))
                            throw new InvalidOperationException("'" + _rodName + "' is read-only or not a Length parameter on the hanger instance");
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
                        Parameter rod = ParameterResolver.ForWrite(fi, _rodName);
                        if (rod == null || !rod.HasValue || !IsLength(rod)) check.Unreadable("rod:" + s.Key, Mm(s.RodFeet), "'" + _rodName + "' did not re-read as a Length");
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
                ["no_support_above"] = _noSupport, ["gaps_above_spacing"] = _wideGaps, ["risers_skipped"] = _risers
            };

            public override void ResetAfterRehearsal() { foreach (Station s in _stations) s.Created = null; }

            public override JObject Describe(Units u) => new JObject
            {
                ["hanger_type"] = new JObject { ["id"] = Rid.Value(_symbolId), ["name"] = _symbolName },
                ["spacing_mm"] = _spacingMm, ["end_offset_mm"] = _endOffsetMm, ["max_rod_mm"] = _maxRodMm,
                ["rod_length_parameter"] = _rodName == null ? (JToken)JValue.CreateNull() : _rodName,
                ["rod_measured_from"] = "the run's top (centreline + half its outside height) up to the underside of the support",
                ["ray_view"] = new JObject
                {
                    ["kind"] = "temporary isometric, rolled back: no template, filters or section box; structure and links visible",
                    ["views"] = _viewCount, ["phases"] = new JArray(_viewPhases), ["phase_filter"] = _phaseFilter ?? "the view type's default",
                    ["closed_worksets_not_searched"] = new JArray(_closedWorksets)
                },
                ["loaded_links_searched"] = _linksLoaded,
                ["planned"] = _stations.Count, ["no_support_above"] = _noSupport, ["gaps_above_spacing"] = _wideGaps, ["risers_skipped"] = _risers,
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

            /// <summary>
            /// Nearest floor, framing or roof straight above `origin` within maxRod, host or
            /// loaded link: 1 found, 0 nothing there, -1 the ray could not be cast (why says so).
            /// </summary>
            private static int FindSupport(Document doc, ReferenceIntersector ray, XYZ origin, double maxRod, Station s, out string why)
            {
                why = null;
                IList<ReferenceWithContext> hits;
                try { hits = ray.Find(origin, XYZ.BasisZ); }
                catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return -1; }
                if (hits == null) { why = "the intersector returned no result list"; return -1; }
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
                    return 1;
                }
                return 0;
            }

            private static bool IsStructure(Element e)
            {
                long cat = e?.Category == null ? -1 : Rid.Value(e.Category.Id);
                return StructureCategories.Any(c => (long)(int)c == cat);
            }

            /// <summary>
            /// A temporary isometric view whose visibility is KNOWN, created inside the caller's
            /// open (always rolled back) transaction: nothing inherited may hide the structure.
            /// </summary>
            private static View3D RayView(Document doc, ElementId phaseId, HangersPlan p, out string error)
            {
                error = null;
                ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft == null) { error = "the model has no 3D view type to build the temporary ray view from."; return null; }
                View3D view = View3D.CreateIsometric(doc, vft.Id);
                if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                foreach (ElementId f in view.GetFilters().ToList()) view.RemoveFilter(f);
                if (view.IsSectionBoxActive) view.IsSectionBoxActive = false;
                try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
                foreach (BuiltInCategory bic in StructureCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }))
                {
                    Category c = Safe(() => Category.GetCategory(doc, bic));
                    if (c != null && view.CanCategoryBeHidden(c.Id) && view.GetCategoryHidden(c.Id)) view.SetCategoryHidden(c.Id, false);
                }
                if (doc.IsWorkshared)
                    foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    {
                        if (!w.IsOpen) { p._closedWorksets.Add(w.Name); continue; } // not loaded: nothing to see
                        if (view.GetWorksetVisibility(w.Id) != WorksetVisibility.Visible) view.SetWorksetVisibility(w.Id, WorksetVisibility.Visible);
                    }
                if (phaseId != null)
                {
                    Parameter vp = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    if (vp != null && !vp.IsReadOnly) vp.Set(phaseId);
                    p._viewPhases.Add(doc.GetElement(phaseId)?.Name ?? Rid.Value(phaseId).ToString());
                }
                PhaseFilter complete = ShowComplete(doc);
                Parameter pf = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                if (complete != null && pf != null && !pf.IsReadOnly && pf.Set(complete.Id)) p._phaseFilter = complete.Name;
                doc.Regenerate();
                return view;
            }

            /// <summary>
            /// The phase filter that shows new and existing but not demolished elements, found by
            /// its presentation settings rather than by its (localised) name.
            /// </summary>
            private static PhaseFilter ShowComplete(Document doc)
            {
                List<PhaseFilter> filters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().ToList();
                bool Shows(PhaseFilter f, ElementOnPhaseStatus st)
                {
                    try { return f.GetPhaseStatusPresentation(st) != PhaseStatusPresentation.DontShow; } catch { return true; }
                }
                return filters.FirstOrDefault(f => Shows(f, ElementOnPhaseStatus.New) && Shows(f, ElementOnPhaseStatus.Existing) && !Shows(f, ElementOnPhaseStatus.Demolished) && !Shows(f, ElementOnPhaseStatus.Temporary))
                    ?? filters.FirstOrDefault(f => Shows(f, ElementOnPhaseStatus.New) && Shows(f, ElementOnPhaseStatus.Existing) && !Shows(f, ElementOnPhaseStatus.Demolished));
            }

            /// <summary>The run's own phase (the structure must exist when the run does); the last phase when it has none.</summary>
            private static ElementId RunPhase(Document doc, Element e)
            {
                ElementId id = Safe(() => e.CreatedPhaseId);
                if (id != null && id != ElementId.InvalidElementId) return id;
                return doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1).Id : null;
            }

            private static bool IsLength(Parameter p)
            {
                string have = Safe(() => p.Definition.GetDataType())?.TypeId, want = SpecTypeId.Length.TypeId;
                return have != null && Unversioned(have) == Unversioned(want);
            }

            private static string Unversioned(string typeId) { int dash = typeId.IndexOf('-'); return dash < 0 ? typeId : typeId.Substring(0, dash); }

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
