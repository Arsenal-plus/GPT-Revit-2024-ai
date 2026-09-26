// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_mep_routing's "slope" operation. Original Horizun code.
//
// SCOPE: gravity pipe runs (Pipe elements joined by pipe fittings) - drainage
// and vent, not ducts or conduit. The caller names the run by element_ids (the
// pipes; the fittings between them join automatically), or one seed pipe with
// walk=true to walk the connected network through fittings, bounded to
// MaxNetworkElements pipes + fittings. Accessories, fixtures and equipment are
// never walked through: they are boundaries, reported as external connections
// and verified to stay connected.
//
// THE GRAPH IS THE CONNECTOR GRAPH, NOT COINCIDENT POINTS. A pipe ending in an
// elbow does not share an endpoint with the next pipe - the elbow sits between
// them - so nodes are connector pairs ("j:" + both connectors' keys), open or
// external ends ("open:"), and each fitting's centre ("fit:"). A fitting adds
// one edge from its centre to each connector, so its tangent legs count as path
// length and every pipe keeps exactly the target slope (SlopeRules owns the
// arithmetic and the outlet model).
//
// THE FIXED END. fixed_end = 'upstream'/'downstream' (two open ends, flow must
// read), or an element_id with one open end, optionally ':high' (held end is
// the upstream end) or ':low' (held end is the outlet). Bare id: flow decides
// when a connector reports In/Out, else the held end is assumed high and the
// plan says so (direction_source). Connector.Direction is read element-
// relative: In = flow enters that pipe there (its upstream end) - TO MEASURE
// LIVE on a calculated sanitary system; the live probe uses ':high' so it does
// not depend on it.
//
// WHAT REVIT DOES TO FITTINGS (RevitAPI.xml): there is no SlopeType enum and no
// "slope this run" call for pipes - a slope is two Z's on a straight
// LocationCurve (RBS_PIPE_SLOPE is only documented as "Slope";
// PipeSettings.GetPipeSlopes lists the document's preset slopes).
// AutoRouteFailures.AttemptToConnectNonSlopingElementToSlopedPipe exists, so a
// fitting that cannot slope fails the transaction rather than silently
// disconnecting. Apply therefore (1) moves each fitting vertically to its
// target centre, (2) sets every pipe's LocationCurve to its target ends,
// (3) regenerates, and (4) for every connector pair that was connected before
// and is not now, calls ConnectTo when the two origins still coincide (within
// ReconnectToleranceFeet) and reports it as "reconnected". Pairs that no longer
// coincide are left for Verify to fail by name, and the runner rolls back. How
// often (4) is needed is TO MEASURE LIVE (pipe-slope.probes.ps1).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        private sealed class SlopePipe { public ElementId Id; public long IdValue; public string NodeA, NodeB; public XYZ A, B; }
        private sealed class SlopeFitting { public ElementId Id; public long IdValue; public string Node; public XYZ Center; public int ConnectedBefore; }
        private sealed class SlopeLink { public long OwnerA, OwnerB; public int ConnA, ConnB; public string Key; public bool External; }

        private static string ConnKey(long owner, int conn) => owner.ToString(CultureInfo.InvariantCulture) + "#" + conn.ToString(CultureInfo.InvariantCulture);

        private static bool IsFitting(Element e)
            => e is FamilyInstance fi && Safe(() => fi.MEPModel) is Autodesk.Revit.DB.Mechanical.MechanicalFitting;

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
            public override int Count => _pipes.Count;

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
                        var sp = new SlopePipe { Id = pipe.Id, IdValue = eid, A = a, B = b };
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
                        var sf = new SlopeFitting { Id = e.Id, IdValue = eid, Node = "fit:" + eid, Center = center,
                            ConnectedBefore = conns.Count(c => Safe(() => (bool?)c.IsConnected).GetValueOrDefault()) };
                        p._fittings.Add(sf);
                        nodePoint[sf.Node] = center;
                        foreach (Connector c in conns)
                        {
                            XYZ o = c.Origin;
                            string node = p.NodeOf(eid, c, set);
                            nodePoint[node] = o;
                            edges.Add(new SlopeRules.Edge("f" + ConnKey(eid, c.Id), sf.Node, node, Horizontal(center, o), o.Z - center.Z));
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
                    p._directionSource = high.HasValue ? "caller" : null;
                    if (!high.HasValue)
                    {
                        high = p.LeafIsHighByFlow(doc, p._fixedNode);
                        p._directionSource = high.HasValue ? "flow" : "assumed: the held end is the high end";
                        if (!high.HasValue) high = true;
                    }
                    if (high == false) p._outletNode = p._fixedNode;
                    else
                    {
                        var others = leaves.Where(l => l != p._fixedNode).ToList();
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
                double? floor = null;
                if (clearance.HasValue)
                {
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Select(l => l.ProjectElevation).Where(z => z <= p._fixedZ + 1e-6).ToList();
                    if (levels.Count == 0) { error = "min_clearance: no level lies at or below the held end, so there is no floor to measure from."; return null; }
                    floor = levels.Max();
                }
                var r = SlopeRules.ComputeTargets(edges, p._fixedNode, p._fixedZ, slope, p._outletNode, floor, clearance);
                if (!r.Ok) { error = p.Readable(r.Error); return null; }
                foreach (var kv in r.NodeElevationFeet) p._target[kv.Key] = kv.Value;
                p._minAllowed = r.MinAllowedElevationFeet;
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

            /// <summary>Open-end node names in errors read as "element 123 connector 2".</summary>
            private string Readable(string text)
            {
                foreach (var kv in _openNodes) text = text.Replace(kv.Key, "element " + kv.Value[0] + " connector " + kv.Value[1]);
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
                    double dz = _target[f.Node] - f.Center.Z;
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

            private static Connector FindConnector(Document doc, long owner, int id)
            {
                Element e = Rid.CanRepresent(owner) ? doc.GetElement(Rid.Make(owner)) : null;
                return e == null ? null : MepFacts.Ordered(MepFacts.ManagerOf(e)).FirstOrDefault(c => c.Id == id);
            }

            public override void ResetAfterRehearsal() => _reconnected.Clear();

            public override PostconditionCheck Verify(Document doc)
            {
                var required = new List<string> { "fixed_end" };
                required.AddRange(_pipes.Select(sp => "slope:" + sp.IdValue));
                required.AddRange(_links.Select(l => "connection:" + l.Key));
                required.AddRange(_fittings.Select(f => "fitting:" + f.IdValue));
                var check = new PostconditionCheck(required.ToArray());

                long[] held = _openNodes[_fixedNode];
                XYZ heldNow = Safe(() => FindConnector(doc, held[0], (int)held[1])?.Origin);
                if (heldNow == null) check.Unreadable("fixed_end", Math.Round(_fixedZ, 6), "the held connector did not re-read");
                else check.Record("fixed_end", Math.Round(_fixedZ, 6), Math.Round(heldNow.Z, 6), Math.Abs(heldNow.Z - _fixedZ) <= FixedEndToleranceFeet);

                foreach (SlopePipe sp in _pipes)
                {
                    string what = "slope:" + sp.IdValue;
                    if (!((doc.GetElement(sp.Id)?.Location as LocationCurve)?.Curve is Line line)) { check.Unreadable(what, _slopePercent, "the pipe did not re-read as a straight LocationCurve"); continue; }
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                    double horiz = Horizontal(a, b);
                    if (horiz < SlopeRules.MinHorizontalLengthFeet) { check.Record(what, "vertical: skipped", "vertical", true); continue; }
                    double measured = Math.Abs(a.Z - b.Z) / horiz * 100.0;
                    check.Record(what, _slopePercent, Math.Round(measured, 4), SlopeRules.SlopeWithinTolerance(horiz, a.Z, b.Z, _slopePercent));
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
                        row["slope_percent"] = horiz < SlopeRules.MinHorizontalLengthFeet ? (JToken)"vertical" : Math.Round(Math.Abs(a.Z - b.Z) / horiz * 100.0, 4);
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
                    ["external_connections"] = new JArray(_links.Where(l => l.External).Select(l => (JToken)new JObject { ["element_id"] = l.OwnerA, ["to_element_id"] = l.OwnerB })),
                    ["min_allowed_elevation"] = _minAllowed.HasValue ? (JToken)u.Out(_minAllowed.Value) : JValue.CreateNull(),
                    ["method"] = "fittings move vertically to their target centre, pipes get new LocationCurve ends; a connector pair that separated but still coincides is reconnected and listed in reconnected."
                };
            }

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command) => NewResolved(gate, app, command);
        }
    }
}
