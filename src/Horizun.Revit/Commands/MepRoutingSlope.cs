// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_mep_routing's "slope" operation. Original Horizun code.
//
// SCOPE: gravity pipe runs (Pipe elements joined by pipe fittings) - drainage
// and vent, not ducts or conduit. The caller names the run by element_ids (the
// pipes; the fittings between them join automatically), or one seed pipe with
// walk=true to walk the connected network through fittings, bounded to
// MaxNetworkElements pipes + fittings. A fitting is OST_PipeFitting by CATEGORY:
// accessories (valves, cleanouts, inline sensors) also expose a MechanicalFitting
// MEPModel (its PartType enum carries ValveNormal, InlineSensor, Damper...), so
// the MEPModel test would walk through them. Accessories, fixtures, equipment and
// unnamed pipes are boundaries: reported as external connections, verified to
// stay connected, and a plan that would MOVE the end they connect to is refused
// (Revit would drag or disconnect an element nobody planned or verified).
//
// THE GRAPH IS THE CONNECTOR GRAPH, NOT COINCIDENT POINTS. A pipe ending in an
// elbow does not share an endpoint with the next pipe - the elbow sits between
// them - so nodes are connector pairs ("j:" + both connectors' keys), open or
// external ends ("open:"), and each fitting's centre ("fit:"). A fitting adds
// one RIGID edge from its centre to each connector: the leg keeps its original
// rise, because Revit moves a fitting as one body (MoveElement), so its
// connectors land at centre target + original offset - exactly where the pipe
// ends are planned. Pipes carry the whole drop at exactly the target slope;
// pipes steeper than 45 degrees are risers and keep their rise (SlopeRules owns
// the arithmetic, the riser rule and the outlet model).
//
// THE FIXED END. fixed_end = 'upstream'/'downstream' (two open ends, flow must
// read), or an element_id with one open end, optionally ':high' (held end is
// the upstream end) or ':low' (held end is the outlet). Bare id: flow decides
// when a connector reports In/Out, else it is REFUSED - holding an end high or
// low gives opposite slopes on every pipe, and the bridge does not guess. Flow
// readings that contradict each other (both open ends In, or both Out) are
// refused with both readings named. Connector.Direction is read element-
// relative: In = flow enters that pipe there (its upstream end) - TO MEASURE
// LIVE on a calculated sanitary system; the live probe uses ':high' so it does
// not depend on it.
//
// WHAT REVIT DOES TO FITTINGS (RevitAPI.xml): there is no SlopeType enum and no
// "slope this run" call for pipes - a slope is two Z's on a straight
// LocationCurve (RBS_PIPE_SLOPE is only documented as "Slope";
// PipeSettings.GetPipeSlopes lists the document's preset slopes).
// BuiltInFailures.AutoRouteFailures carries AttemptToConnectNonSlopingElement-
// ToSlopedPipeWarning and ...Error ("You have specified that sloped pipe be
// drawn..."), posted by the routing tools; the Warning variant does NOT fail a
// commit and the apply transaction sets no failure preprocessor, so a fitting
// that separates is caught ONLY by the post-commit connector re-read, never by
// Revit failing the transaction. Apply (1) moves each fitting vertically by
// its target minus its centre RE-READ at that moment (a fitting joined
// directly to another was already dragged by that one's move), (2) sets every
// pipe's LocationCurve to its target ends, (3) regenerates, and (4) for every
// connector pair that was connected before and is not now, calls ConnectTo
// when the two origins still coincide (within ReconnectToleranceFeet) and
// reports it as "reconnected". Verify re-reads, in the request's units, every
// pipe end against its target (sign and height), every pipe's signed slope,
// every fitting centre, the held end, min_clearance, and every connector pair;
// anything else fails by name and the runner rolls back. Whether Revit
// re-orients elbows to the sloped pipes, and how often (4) runs, is TO MEASURE
// LIVE (pipe-slope.probes.ps1 re-reads connector origins independently).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class MepRoutingCommand
    {
        private const int MaxNetworkElements = 200;
        private const double ReconnectToleranceFeet = 1e-3;
        private const double FixedEndToleranceFeet = 1e-3;
        /// <summary>Pipe ends re-read within this of their planned elevation (about 0.3 mm).</summary>
        private const double ElevationToleranceFeet = 1e-3;

        private sealed class SlopePipe { public ElementId Id; public long IdValue; public string UniqueId, NodeA, NodeB, Downstream; public XYZ A, B; public bool Riser; }
        private sealed class SlopeFitting { public ElementId Id; public long IdValue; public string UniqueId, Node; public XYZ Center; public int ConnectedBefore; public bool Moves; public double LegTolerance; }
        private sealed class SlopeLink { public long OwnerA, OwnerB; public int ConnA, ConnB; public string Key; public bool External; }

        private static string ConnKey(long owner, int conn) => owner.ToString(CultureInfo.InvariantCulture) + "#" + conn.ToString(CultureInfo.InvariantCulture);

        /// <summary>A pipe fitting by category. Not MEPModel: accessories and duct fittings are
        /// MechanicalFitting too, and accessories must stay boundaries.</summary>
        private static bool IsFitting(Element e)
        {
            if (!(e is FamilyInstance)) return false;
            Category cat = Safe(() => e.Category);
            return cat != null && Rid.Value(cat.Id) == (long)BuiltInCategory.OST_PipeFitting;
        }

        private static List<Connector> PhysicalRefs(Connector c)
        {
            var list = new List<Connector>();
            try
            {
                foreach (Connector o in c.AllRefs)
                    if (o != null && o.Owner != null && o.Owner.Id != c.Owner.Id && o.ConnectorType == ConnectorType.End) list.Add(o);
            }
            catch { }
            return list;
        }

        private sealed class SlopePlan : WritePlan
        {
            private readonly List<SlopePipe> _pipes = new List<SlopePipe>();
            private readonly List<SlopeFitting> _fittings = new List<SlopeFitting>();
            private readonly List<SlopeLink> _links = new List<SlopeLink>();
            private readonly Dictionary<string, double> _target = new Dictionary<string, double>();
            private readonly Dictionary<string, long[]> _openNodes = new Dictionary<string, long[]>(); // node -> owner, connector
            private readonly List<JObject> _reconnected = new List<JObject>();
            private double _slopePercent, _fixedZ;
            private string _fixedNode, _outletNode, _directionSource;
            private double? _minAllowed;
            private Units _u; private string _unitName;
            private double U(double feet) => _u.Out(feet);
            /// <summary>Pipes rewritten plus fittings actually moved.</summary>
            public override int Count => _pipes.Count + _fittings.Count(f => f.Moves);

            public static WritePlan Build(Document doc, JObject request, Units u, out string error, out CommandResult refusal)
            {
                error = null; refusal = null;
                JArray ids = request["element_ids"] as JArray;
                if (ids == null || ids.Count == 0) { error = "slope needs element_ids: the run's pipes, or one seed pipe with walk=true."; return null; }
                bool walk = request.Value<bool?>("walk") ?? false;
                double slope = request.Value<double?>("slope_percent") ?? double.NaN;
                if (!(slope > 0) || double.IsInfinity(slope) || slope > 100) { error = "slope_percent must be a number above 0 and at most 100."; return null; }
                if (!SlopeRules.TryParseFixedEnd(request.Value<string>("fixed_end"), out string word, out long heldId, out bool? heldHigh, out error)) return null;
                double? clearance = request["min_clearance"] == null ? (double?)null : request.Value<double>("min_clearance") * u.ToFeet;
                if (clearance.HasValue && (clearance.Value < 0 || double.IsNaN(clearance.Value))) { error = "min_clearance must be zero or positive."; return null; }

                var seedIds = ids.Select(t => t.Value<long>()).Distinct().ToList();
                if (walk && seedIds.Count != 1) { error = "walk=true takes exactly one seed pipe in element_ids."; return null; }
                if (seedIds.Count > MaxNetworkElements) { error = "slope takes at most " + MaxNetworkElements + " elements."; return null; }
                Dictionary<long, Element> set = CollectSet(doc, seedIds, walk, out error);
                if (set == null) return null;

                var p = new SlopePlan { _slopePercent = slope };
                var edges = new List<SlopeRules.Edge>();
                var nodePoint = new Dictionary<string, XYZ>();
                var seenLinks = new HashSet<string>();
                foreach (Element e in set.Values.OrderBy(x => Rid.Value(x.Id)))
                {
                    long eid = Rid.Value(e.Id);
                    foreach (Connector c in MepFacts.Ordered(MepFacts.ManagerOf(e)))
                    {
                        bool connected = Safe(() => (bool?)c.IsConnected).GetValueOrDefault();
                        if (e is Pipe && c.ConnectorType == ConnectorType.Curve && connected)
                        { error = "pipe " + eid + " has a tap (a connection along its length); slope covers runs joined by fittings at pipe ends."; return null; }
                        if (!connected) continue;
                        foreach (Connector o in PhysicalRefs(c))
                        {
                            long oid = Rid.Value(o.Owner.Id);
                            string a = ConnKey(eid, c.Id), b = ConnKey(oid, o.Id);
                            string key = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                            if (!seenLinks.Add(key)) continue;
                            p._links.Add(new SlopeLink { OwnerA = eid, ConnA = c.Id, OwnerB = oid, ConnB = o.Id, Key = key, External = !set.ContainsKey(oid) });
                        }
                    }
                }

                foreach (Element e in set.Values.OrderBy(x => Rid.Value(x.Id)))
                {
                    long eid = Rid.Value(e.Id);
                    if (e is Pipe pipe)
                    {
                        if (!((pipe.Location as LocationCurve)?.Curve is Line line)) { error = "pipe " + eid + " has no straight LocationCurve."; return null; }
                        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                        var sp = new SlopePipe { Id = pipe.Id, IdValue = eid, UniqueId = pipe.UniqueId, A = a, B = b };
                        foreach (Connector c in MepFacts.Ordered(pipe.ConnectorManager).Where(c => c.ConnectorType == ConnectorType.End))
                        {
                            XYZ o = Safe(() => c.Origin); if (o == null) continue;
                            string node = p.NodeOf(eid, c, set);
                            if (o.DistanceTo(a) <= o.DistanceTo(b)) sp.NodeA = node; else sp.NodeB = node;
                            nodePoint[node] = o;
                        }
                        if (sp.NodeA == null || sp.NodeB == null || sp.NodeA == sp.NodeB) { error = "pipe " + eid + " does not expose two end connectors at its two ends."; return null; }
                        p._pipes.Add(sp);
                        edges.Add(new SlopeRules.Edge("p" + eid, sp.NodeA, sp.NodeB, Horizontal(a, b), b.Z - a.Z));
                    }
                    else
                    {
                        var conns = MepFacts.Ordered(MepFacts.ManagerOf(e)).Where(c => c.ConnectorType == ConnectorType.End).ToList();
                        XYZ center = (e.Location as LocationPoint)?.Point;
                        if (center == null && conns.Count > 0)
                            center = conns.Select(c => c.Origin).Aggregate(XYZ.Zero, (s, x) => s + x) / conns.Count;
                        if (center == null) { error = "fitting " + eid + " has neither a location point nor connectors."; return null; }
                        var sf = new SlopeFitting { Id = e.Id, IdValue = eid, UniqueId = e.UniqueId, Node = "fit:" + eid, Center = center,
                            ConnectedBefore = conns.Count(c => Safe(() => (bool?)c.IsConnected).GetValueOrDefault()) };
                        p._fittings.Add(sf);
                        nodePoint[sf.Node] = center;
                        foreach (Connector c in conns)
                        {
                            XYZ o = c.Origin;
                            string node = p.NodeOf(eid, c, set);
                            nodePoint[node] = o;
                            // rigid: the leg keeps its rise, as MoveElement moves the fitting as one body.
                            edges.Add(new SlopeRules.Edge("f" + ConnKey(eid, c.Id), sf.Node, node, Horizontal(center, o), o.Z - center.Z, true));
                            sf.LegTolerance = Math.Max(sf.LegTolerance, slope / 100.0 * Horizontal(center, o));
                        }
                    }
                }
                if (p._pipes.Count == 0) { error = "slope found no pipe to slope."; return null; }

                var degree = new Dictionary<string, int>();
                foreach (var e in edges)
                {
                    degree[e.FromNode] = (degree.TryGetValue(e.FromNode, out int da) ? da : 0) + 1;
                    degree[e.ToNode] = (degree.TryGetValue(e.ToNode, out int db) ? db : 0) + 1;
                }
                var leaves = degree.Where(kv => kv.Value == 1).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

                if (word != null)
                {
                    if (leaves.Count != 2 || !leaves.All(l => p._openNodes.ContainsKey(l))) { error = "fixed_end='" + word + "' needs a run with exactly two open pipe or fitting ends; this one has " + leaves.Count + " end(s). Use '<element_id>:high' or ':low'."; return null; }
                    bool? firstHigh = p.LeafIsHighByFlow(doc, leaves[0]), secondHigh = p.LeafIsHighByFlow(doc, leaves[1]);
                    if (firstHigh.HasValue && secondHigh.HasValue && firstHigh.Value == secondHigh.Value)
                    { error = "flow contradicts itself: both open ends read " + (firstHigh.Value ? "In" : "Out") + " (" + Readable(leaves[0]) + " and " + Readable(leaves[1]) + "), so neither can be named the high end. Use fixed_end='<element_id>:high' or ':low'."; return null; }
                    string high = firstHigh == true || secondHigh == false ? leaves[0] : firstHigh == false || secondHigh == true ? leaves[1] : null;
                    if (high == null) { error = "flow direction is not readable at either open end (the system may not be calculated). Use fixed_end='<element_id>:high' or ':low'."; return null; }
                    string low = leaves[0] == high ? leaves[1] : leaves[0];
                    p._fixedNode = word == "upstream" ? high : low;
                    p._outletNode = low;
                    p._directionSource = "flow";
                }
                else
                {
                    var own = leaves.Where(l => p._openNodes.TryGetValue(l, out long[] oc) && oc[0] == heldId).ToList();
                    if (!set.ContainsKey(heldId)) { error = "fixed_end " + heldId + " is not in the run."; return null; }
                    if (own.Count != 1) { error = "fixed_end " + heldId + " has " + own.Count + " open end(s); name the element at the run's open end (or use upstream/downstream)."; return null; }
                    p._fixedNode = own[0];
                    bool? high = heldHigh;
                    p._directionSource = "caller";
                    if (!high.HasValue)
                    {
                        high = p.LeafIsHighByFlow(doc, p._fixedNode);
                        if (!high.HasValue)
                        { error = "fixed_end=" + heldId + " does not say whether the held end is high or low, and its connector reports no flow direction (the system may not be calculated). Holding it high or low slopes every pipe the opposite way; send '" + heldId + ":high' or '" + heldId + ":low'."; return null; }
                        p._directionSource = "flow";
                    }
                    var others = leaves.Where(l => l != p._fixedNode).ToList();
                    if (p._directionSource == "flow" && others.Count == 1 && p.LeafIsHighByFlow(doc, others[0]) == high)
                    { error = "flow contradicts itself: both open ends read " + (high.Value ? "In" : "Out") + " (" + Readable(p._fixedNode) + " and " + Readable(others[0]) + "). Send '" + heldId + ":high' or '" + heldId + ":low'."; return null; }
                    if (high == false) p._outletNode = p._fixedNode;
                    else
                    {
                        if (others.Count == 1) p._outletNode = others[0];
                        else
                        {
                            var byFlow = others.Where(l => p.LeafIsHighByFlow(doc, l) == false).ToList();
                            if (byFlow.Count != 1) { error = "the run branches (" + others.Count + " other open ends) and flow does not name one outlet. Hold the outlet with fixed_end='<element_id>:low'."; return null; }
                            p._outletNode = byFlow[0];
                        }
                    }
                }

                p._fixedZ = nodePoint[p._fixedNode].Z;
                p._u = u; p._unitName = UnitName(request);
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => l.ProjectElevation).Where(z => z <= p._fixedZ + 1e-6).ToList();
                double? floor = levels.Count == 0 ? (double?)null : levels.Max();
                if (clearance.HasValue && !floor.HasValue) { error = "min_clearance: no level lies at or below the held end, so there is no floor to measure from."; return null; }
                var r = SlopeRules.ComputeTargets(edges, p._fixedNode, p._fixedZ, slope, p._outletNode, clearance.HasValue ? floor : null, clearance);
                if (!r.Ok)
                {
                    error = r.LowestNode != null && r.MinAllowedElevationFeet.HasValue
                        ? Readable(r.LowestNode) + " would land at " + p.U(r.LowestElevationFeet) + " " + p._unitName + ", below the floor plus min_clearance (" + p.U(r.MinAllowedElevationFeet.Value) + " " + p._unitName + ")."
                        : Readable(r.Error);
                    return null;
                }
                foreach (var kv in r.NodeElevationFeet) p._target[kv.Key] = kv.Value;
                p._minAllowed = r.MinAllowedElevationFeet;
                // Default floor check (no min_clearance): a point the re-grade drags from above the
                // floor to below it is refused; a point already below the floor (under a slab) is not.
                if (!clearance.HasValue && floor.HasValue)
                    foreach (var kv in p._target.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                        if (nodePoint.TryGetValue(kv.Key, out XYZ was) && was.Z > floor.Value + ElevationToleranceFeet && kv.Value < floor.Value - 1e-6)
                        { error = Readable(kv.Key) + " would drop from " + p.U(was.Z) + " to " + p.U(kv.Value) + " " + p._unitName + ", through the level at " + p.U(floor.Value) + " below the held end. Hold another end or lower slope_percent."; return null; }
                // An end that connects outside the run must not move: Revit would drag or disconnect
                // an element this plan neither lists nor verifies.
                foreach (SlopeLink l in p._links.Where(x => x.External))
                {
                    string node = "open:" + ConnKey(l.OwnerA, l.ConnA);
                    if (!p._target.TryGetValue(node, out double tz) || !nodePoint.TryGetValue(node, out XYZ now)) continue;
                    if (Math.Abs(tz - now.Z) > ElevationToleranceFeet)
                    { error = "element " + l.OwnerA + " connector " + l.ConnA + " connects to element " + l.OwnerB + ", outside the run, and the slope would move that end by " + p.U(tz - now.Z) + " " + p._unitName + "; Revit would drag or disconnect " + l.OwnerB + " unplanned. Hold that end (fixed_end='" + l.OwnerA + ":high' or ':low'), name " + l.OwnerB + " in the run, or disconnect it first."; return null; }
                }
                var near = r.Edges.ToDictionary(x => x.EdgeId, x => x);
                foreach (SlopePipe sp in p._pipes)
                {
                    SlopeRules.EdgeResult er = near["p" + sp.IdValue];
                    sp.Downstream = er.NearNode; sp.Riser = er.Skipped;
                }
                foreach (SlopeFitting f in p._fittings) f.Moves = Math.Abs(p._target[f.Node] - f.Center.Z) > 1e-9;
                return p;
            }

            private static double Horizontal(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

            private string NodeOf(long owner, Connector c, Dictionary<long, Element> set)
            {
                var inside = Safe(() => (bool?)c.IsConnected).GetValueOrDefault()
                    ? PhysicalRefs(c).Where(o => set.ContainsKey(Rid.Value(o.Owner.Id))).ToList() : new List<Connector>();
                string me = ConnKey(owner, c.Id);
                if (inside.Count == 1)
                {
                    string other = ConnKey(Rid.Value(inside[0].Owner.Id), inside[0].Id);
                    return "j:" + (string.CompareOrdinal(me, other) < 0 ? me + "|" + other : other + "|" + me);
                }
                string node = "open:" + me;
                _openNodes[node] = new[] { owner, (long)c.Id };
                return node;
            }

            /// <summary>Internal node and edge keys in a message read as elements and connectors.</summary>
            private static string Readable(string text)
            {
                if (text == null) return null;
                text = Regex.Replace(text, @"j:(\d+)#(\d+)\|(\d+)#(\d+)", m => "the joint of element " + m.Groups[1].Value + " connector " + m.Groups[2].Value + " and element " + m.Groups[3].Value + " connector " + m.Groups[4].Value);
                text = Regex.Replace(text, @"open:(\d+)#(\d+)", m => "element " + m.Groups[1].Value + " connector " + m.Groups[2].Value);
                text = Regex.Replace(text, @"fit:(\d+)", m => "the centre of fitting " + m.Groups[1].Value);
                text = Regex.Replace(text, @"\bf(\d+)#(\d+)", m => "fitting " + m.Groups[1].Value + " leg " + m.Groups[2].Value);
                text = Regex.Replace(text, @"\bp(\d+)\b", m => "pipe " + m.Groups[1].Value);
                return text;
            }

            /// <summary>true = this open end is the upstream (high) end by flow, false = the outlet end, null = unreadable.
            /// Element-relative: In = flow enters the owner here. TO MEASURE LIVE.</summary>
            private bool? LeafIsHighByFlow(Document doc, string node)
            {
                if (!_openNodes.TryGetValue(node, out long[] oc)) return null;
                Element e = doc.GetElement(Rid.Make(oc[0]));
                Connector c = MepFacts.Ordered(MepFacts.ManagerOf(e)).FirstOrDefault(x => x.Id == (int)oc[1]);
                if (c == null) return null;
                var dir = Safe(() => (FlowDirectionType?)c.Direction);
                if (dir == FlowDirectionType.In) return true;
                if (dir == FlowDirectionType.Out) return false;
                return null;
            }

            private static Dictionary<long, Element> CollectSet(Document doc, List<long> seedIds, bool walk, out string error)
            {
                error = null;
                var set = new Dictionary<long, Element>();
                var queue = new Queue<Element>();
                foreach (long id in seedIds)
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (!(e is Pipe) && !(e != null && IsFitting(e))) { error = "element_ids " + id + " is not a pipe or pipe fitting; slope covers gravity pipe runs only."; return null; }
                    set[id] = e; queue.Enqueue(e);
                }
                // explicit mode: the named elements plus the fittings that join them; walk mode: follow on.
                while (queue.Count > 0)
                {
                    Element cur = queue.Dequeue();
                    foreach (Connector c in MepFacts.Ordered(MepFacts.ManagerOf(cur)))
                    {
                        if (c.ConnectorType != ConnectorType.End || !Safe(() => (bool?)c.IsConnected).GetValueOrDefault()) continue;
                        foreach (Connector o in PhysicalRefs(c))
                        {
                            Element owner = o.Owner; long oid = Rid.Value(owner.Id);
                            if (set.ContainsKey(oid)) continue;
                            bool fitting = IsFitting(owner);
                            if (!(walk ? (owner is Pipe || fitting) : (fitting && cur is Pipe))) continue;
                            if (set.Count >= MaxNetworkElements) { error = "the connected network exceeds " + MaxNetworkElements + " pipes and fittings; name a smaller run with explicit element_ids."; return null; }
                            set[oid] = owner;
                            if (walk) queue.Enqueue(owner);
                        }
                    }
                }
                return set;
            }

            public override void Apply(Document doc)
            {
                _reconnected.Clear();
                foreach (SlopeFitting f in _fittings)
                {
                    // Re-read NOW: a fitting joined directly to another was dragged by that one's move.
                    XYZ now = CentreOf(doc.GetElement(f.Id));
                    if (now == null) throw new InvalidOperationException("fitting " + f.IdValue + " no longer has a location to move");
                    double dz = _target[f.Node] - now.Z;
                    if (Math.Abs(dz) > 1e-9) ElementTransformUtils.MoveElement(doc, f.Id, new XYZ(0, 0, dz));
                }
                foreach (SlopePipe sp in _pipes)
                {
                    var lc = doc.GetElement(sp.Id)?.Location as LocationCurve;
                    if (lc == null) throw new InvalidOperationException("pipe " + sp.IdValue + " no longer has a LocationCurve");
                    lc.Curve = Line.CreateBound(new XYZ(sp.A.X, sp.A.Y, _target[sp.NodeA]), new XYZ(sp.B.X, sp.B.Y, _target[sp.NodeB]));
                }
                doc.Regenerate();
                foreach (SlopeLink l in _links)
                {
                    Connector a = FindConnector(doc, l.OwnerA, l.ConnA), b = FindConnector(doc, l.OwnerB, l.ConnB);
                    if (a == null || b == null || Safe(() => (bool?)a.IsConnectedTo(b)).GetValueOrDefault()) continue;
                    XYZ oa = Safe(() => a.Origin), ob = Safe(() => b.Origin);
                    if (oa == null || ob == null || oa.DistanceTo(ob) > ReconnectToleranceFeet) continue;
                    a.ConnectTo(b);
                    _reconnected.Add(new JObject { ["element_id"] = l.OwnerA, ["connector"] = l.ConnA, ["to_element_id"] = l.OwnerB, ["to_connector"] = l.ConnB });
                }
            }

            private static XYZ CentreOf(Element e)
            {
                if (e == null) return null;
                XYZ p = (e.Location as LocationPoint)?.Point;
                if (p != null) return p;
                var conns = MepFacts.Ordered(MepFacts.ManagerOf(e)).Where(c => c.ConnectorType == ConnectorType.End).ToList();
                return conns.Count == 0 ? null : conns.Select(c => c.Origin).Aggregate(XYZ.Zero, (s, x) => s + x) / conns.Count;
            }

            private static Connector FindConnector(Document doc, long owner, int id)
            {
                Element e = Rid.CanRepresent(owner) ? doc.GetElement(Rid.Make(owner)) : null;
                return e == null ? null : MepFacts.Ordered(MepFacts.ManagerOf(e)).FirstOrDefault(c => c.Id == id);
            }

            public override void ResetAfterRehearsal() => _reconnected.Clear();

            public override PostconditionCheck Verify(Document doc)
            {
                var required = new List<string> { "fixed_end" };
                foreach (SlopePipe sp in _pipes)
                    required.AddRange(new[] { "slope:" + sp.IdValue, "elevation:" + sp.IdValue + ":start", "elevation:" + sp.IdValue + ":end" });
                required.AddRange(_links.Select(l => "connection:" + l.Key));
                foreach (SlopeFitting f in _fittings) required.AddRange(new[] { "fitting:" + f.IdValue, "fitting_centre:" + f.IdValue });
                if (_minAllowed.HasValue) required.Add("min_clearance");
                var check = new PostconditionCheck(required.ToArray());

                long[] held = _openNodes[_fixedNode];
                XYZ heldNow = Safe(() => FindConnector(doc, held[0], (int)held[1])?.Origin);
                if (heldNow == null) check.Unreadable("fixed_end", U(_fixedZ), "the held connector did not re-read");
                else check.Measure("fixed_end", U(_fixedZ), U(heldNow.Z), U(FixedEndToleranceFeet), _unitName, "held connector origin Z, re-read");

                double lowest = double.PositiveInfinity;
                foreach (SlopePipe sp in _pipes)
                {
                    string what = "slope:" + sp.IdValue, ends = "elevation:" + sp.IdValue;
                    if (!((doc.GetElement(sp.Id)?.Location as LocationCurve)?.Curve is Line line))
                    {
                        const string why = "the pipe did not re-read as a straight LocationCurve";
                        check.Unreadable(what, _slopePercent, why); check.Unreadable(ends + ":start", U(_target[sp.NodeA]), why); check.Unreadable(ends + ":end", U(_target[sp.NodeB]), why);
                        continue;
                    }
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                    lowest = Math.Min(lowest, Math.Min(a.Z, b.Z));
                    // Each end against its planned elevation: covers the sign and the absolute height.
                    check.Measure(ends + ":start", U(_target[sp.NodeA]), U(a.Z), U(ElevationToleranceFeet), _unitName, "LocationCurve end 0 Z, re-read");
                    check.Measure(ends + ":end", U(_target[sp.NodeB]), U(b.Z), U(ElevationToleranceFeet), _unitName, "LocationCurve end 1 Z, re-read");
                    double horiz = Horizontal(a, b);
                    if (sp.Riser || horiz < SlopeRules.MinHorizontalLengthFeet) { check.Record(what, "riser: original rise kept", "riser", true); continue; }
                    double zNear = sp.Downstream == sp.NodeA ? a.Z : b.Z, zFar = sp.Downstream == sp.NodeA ? b.Z : a.Z;
                    double measured = (zFar - zNear) / horiz * 100.0; // positive = falls toward the outlet
                    check.Record(what, _slopePercent, Math.Round(measured, 4), SlopeRules.SlopeWithinTolerance(horiz, zNear, zFar, _slopePercent));
                }
                if (_minAllowed.HasValue)
                {
                    if (double.IsInfinity(lowest)) check.Unreadable("min_clearance", U(_minAllowed.Value), "no pipe re-read");
                    else check.Record("min_clearance", U(_minAllowed.Value), U(lowest), lowest >= _minAllowed.Value - ElevationToleranceFeet);
                }
                foreach (SlopeLink l in _links)
                {
                    Connector a = FindConnector(doc, l.OwnerA, l.ConnA), b = FindConnector(doc, l.OwnerB, l.ConnB);
                    if (a == null || b == null) { check.Unreadable("connection:" + l.Key, true, "element " + (a == null ? l.OwnerA : l.OwnerB) + " or its connector no longer exists"); continue; }
                    check.Record("connection:" + l.Key, true, Safe(() => (bool?)a.IsConnectedTo(b)).GetValueOrDefault(), Safe(() => (bool?)a.IsConnectedTo(b)).GetValueOrDefault());
                }
                foreach (SlopeFitting f in _fittings)
                {
                    Element e = doc.GetElement(f.Id);
                    if (e == null) { check.Unreadable("fitting:" + f.IdValue, f.ConnectedBefore, "the fitting no longer exists"); continue; }
                    int now = MepFacts.Ordered(MepFacts.ManagerOf(e)).Count(c => c.ConnectorType == ConnectorType.End && Safe(() => (bool?)c.IsConnected).GetValueOrDefault());
                    check.Record("fitting:" + f.IdValue, f.ConnectedBefore, now, now >= f.ConnectedBefore);
                    XYZ centre = CentreOf(e);
                    // Tolerance: slope x its longest leg, room for Revit re-orienting the fitting to the
                    // sloped pipes (TO MEASURE LIVE); a double move overshoots by a whole pipe's drop.
                    if (centre == null) check.Unreadable("fitting_centre:" + f.IdValue, U(_target[f.Node]), "the fitting's centre did not re-read");
                    else check.Measure("fitting_centre:" + f.IdValue, U(_target[f.Node]), U(centre.Z), U(ElevationToleranceFeet + f.LegTolerance), _unitName, "fitting location point Z, re-read");
                }
                return check;
            }

            public override JToken Report(Document doc, Units u)
            {
                Func<double, double> o = z => u == null ? Math.Round(z, 6) : u.Out(z);
                var rows = new JArray();
                foreach (SlopePipe sp in _pipes)
                {
                    var row = new JObject { ["element_id"] = sp.IdValue };
                    if ((doc.GetElement(sp.Id)?.Location as LocationCurve)?.Curve is Line line)
                    {
                        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                        double horiz = Horizontal(a, b);
                        row["start_elevation"] = o(a.Z); row["end_elevation"] = o(b.Z);
                        row["slope_percent"] = sp.Riser || horiz < SlopeRules.MinHorizontalLengthFeet ? (JToken)"riser" : Math.Round(Math.Abs(a.Z - b.Z) / horiz * 100.0, 4);
                    }
                    rows.Add(row);
                }
                return new JObject
                {
                    ["slope_percent"] = _slopePercent, ["direction_source"] = _directionSource, ["pipes"] = rows,
                    ["fittings"] = new JArray(_fittings.Select(f => (JToken)f.IdValue)),
                    ["reconnected"] = new JArray(_reconnected.Select(x => (JToken)x.DeepClone()))
                };
            }

            public override JObject Describe(Units u)
            {
                long[] held = _openNodes[_fixedNode];
                long[] outlet;
                _openNodes.TryGetValue(_outletNode, out outlet);
                return new JObject
                {
                    ["slope_percent"] = _slopePercent,
                    ["held"] = new JObject { ["element_id"] = held[0], ["connector"] = held[1], ["elevation"] = u.Out(_fixedZ) },
                    ["outlet"] = outlet == null ? (JToken)JValue.CreateNull() : new JObject { ["element_id"] = outlet[0], ["connector"] = outlet[1], ["elevation"] = u.Out(_target[_outletNode]) },
                    ["direction_source"] = _directionSource,
                    ["pipe_count"] = _pipes.Count, ["fitting_count"] = _fittings.Count,
                    ["targets"] = new JArray(_pipes.Select(sp => (JToken)new JObject
                    {
                        ["element_id"] = sp.IdValue, ["start_elevation"] = u.Out(_target[sp.NodeA]), ["end_elevation"] = u.Out(_target[sp.NodeB])
                    })),
                    ["fittings"] = new JArray(_fittings.Select(f => (JToken)new JObject { ["element_id"] = f.IdValue, ["centre_elevation"] = u.Out(_target[f.Node]) })),
                    ["risers_kept"] = new JArray(_pipes.Where(sp => sp.Riser).Select(sp => (JToken)sp.IdValue)),
                    ["external_connections"] = new JArray(_links.Where(l => l.External).Select(l => (JToken)new JObject { ["element_id"] = l.OwnerA, ["to_element_id"] = l.OwnerB })),
                    ["min_allowed_elevation"] = _minAllowed.HasValue ? (JToken)u.Out(_minAllowed.Value) : JValue.CreateNull(),
                    ["method"] = "fittings move rigidly and vertically to their target centre, pipes get new LocationCurve ends (risers steeper than 45 degrees keep their rise); a connector pair that separated but still coincides is reconnected and listed in reconnected."
                };
            }

            private static string Pt(XYZ p) => F6(p.X) + "," + F6(p.Y) + "," + F6(p.Z);
            private static string F6(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

            /// <summary>One Modify row per pipe and per fitting, so the token binds the set and its
            /// geometry: a pipe moved, connected or added between the dry run and the apply is stale_plan.</summary>
            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command)
            {
                var rp = NewResolved(gate, app, command);
                Func<long, string> external = id => string.Join(";", _links.Where(l => l.External && l.OwnerA == id).Select(l => l.Key).OrderBy(k => k, StringComparer.Ordinal));
                foreach (SlopePipe sp in _pipes)
                    rp.Elements.Add(new PlannedElement
                    {
                        UniqueId = sp.UniqueId, ElementId = sp.IdValue, Category = "pipe", Action = PlannedAction.Modify,
                        BeforeValues = new Dictionary<string, string> { ["start"] = Pt(sp.A), ["end"] = Pt(sp.B), ["start_node"] = sp.NodeA, ["end_node"] = sp.NodeB, ["held"] = _fixedNode, ["outlet"] = _outletNode, ["external"] = external(sp.IdValue) },
                        ProposedValues = new Dictionary<string, string> { ["start_z"] = F6(_target[sp.NodeA]), ["end_z"] = F6(_target[sp.NodeB]) }
                    });
                foreach (SlopeFitting f in _fittings)
                    rp.Elements.Add(new PlannedElement
                    {
                        UniqueId = f.UniqueId, ElementId = f.IdValue, Category = "pipe_fitting", Action = PlannedAction.Modify,
                        BeforeValues = new Dictionary<string, string> { ["centre"] = Pt(f.Center), ["connected"] = f.ConnectedBefore.ToString(CultureInfo.InvariantCulture), ["held"] = _fixedNode, ["outlet"] = _outletNode, ["external"] = external(f.IdValue) },
                        ProposedValues = new Dictionary<string, string> { ["centre_z"] = F6(_target[f.Node]) }
                    });
                return rp;
            }
        }
    }
}
