// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_mep_routing's slope operation: given the
// network as a tree of nodes and edges (an edge is one segment's run between
// two logical junctions - a pipe end, the centre of an elbow or tee), computes
// the target elevation of every node.
//
// WHY AN OUTLET, NOT JUST A FIXED NODE. Water runs toward ONE point: the
// outlet (the downstream, low end where the run joins the stack or main).
// Every node's height above the outlet is slope x its horizontal path length
// TO the outlet - so a branch leaving a tee RISES moving away from the main and
// drains toward it, whichever end the caller holds still. An earlier version
// dropped elevation moving away from the fixed node on every edge; with the
// upstream end held that made a tee's branch fall away from the main (drain
// backwards). The fixed node only anchors the absolute height:
//   z(n) = z(fixed) + h(n) - h(fixed),  h(n) = slope x path length(n -> outlet).
// Holding the outlet itself is the case fixed == outlet.
//
// WHY A TREE. A network with a loop has no single slope solution (two paths
// to the outlet of different lengths demand two heights at the node where they
// meet), so ComputeTargets refuses with the node named instead of picking one.
//
// RISERS AND RIGID EDGES KEEP THEIR RISE. An edge is a riser when it is steeper
// than SteepRiseRatio (45 degrees) or shorter in plan than MinHorizontalLengthFeet:
// classified by ANGLE, not by an absolute plan length, because a 3 m riser drawn
// with 5 mm of plan offset is still a riser - re-grading it to slope_percent would
// flatten it to a near-level 5 mm stub and drop everything above it by 3 m. A
// riser keeps its ORIGINAL rise and is reported as skipped. A Rigid edge (a
// fitting's leg, centre to connector) always keeps its original rise too: Revit
// moves a fitting as one body, so its connectors cannot take separate slopes.
//
// UNITS. Every length and elevation here is in feet, Revit's internal unit -
// the command that owns this converts once, at the edges.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SlopeRules
    {
        /// <summary>0.05 percentage points, as a ratio (0.05 / 100). Measured slope
        /// within this of the target counts as verified.</summary>
        public const double SlopeToleranceRatio = 0.0005;

        /// <summary>Below this horizontal run, in feet (about 3 mm), an edge is treated
        /// as vertical/zero-length: skipped, keeping its original rise.</summary>
        public const double MinHorizontalLengthFeet = 0.01;

        /// <summary>|rise| / horizontal at or above this (tan 45 degrees) makes an edge a
        /// riser that keeps its original rise instead of being re-graded.</summary>
        public const double SteepRiseRatio = 1.0;

        /// <summary>True when an edge is a riser by angle: steeper than 45 degrees, or
        /// too short in plan to carry a slope.</summary>
        public static bool IsRiser(double horizontalLengthFeet, double riseFeet)
            => horizontalLengthFeet < MinHorizontalLengthFeet || Math.Abs(riseFeet) >= SteepRiseRatio * horizontalLengthFeet * (1 - 1e-9);

        public sealed class Edge
        {
            public readonly string Id;
            public readonly string FromNode;
            public readonly string ToNode;
            public readonly double HorizontalLengthFeet;
            /// <summary>Original Z(ToNode) - Z(FromNode). Kept when the edge is a riser or rigid.</summary>
            public readonly double RiseFeet;
            /// <summary>A fitting leg: always keeps RiseFeet, whatever its angle.</summary>
            public readonly bool Rigid;
            public Edge(string id, string fromNode, string toNode, double horizontalLengthFeet)
                : this(id, fromNode, toNode, horizontalLengthFeet, 0.0, false) { }
            public Edge(string id, string fromNode, string toNode, double horizontalLengthFeet, double riseFeet)
                : this(id, fromNode, toNode, horizontalLengthFeet, riseFeet, false) { }
            public Edge(string id, string fromNode, string toNode, double horizontalLengthFeet, double riseFeet, bool rigid)
            { Id = id; FromNode = fromNode; ToNode = toNode; HorizontalLengthFeet = horizontalLengthFeet; RiseFeet = riseFeet; Rigid = rigid; }
        }

        public sealed class EdgeResult
        {
            public string EdgeId;
            /// <summary>NearNode is the end closer to the outlet (downstream), FarNode the upstream end.</summary>
            public string NearNode, FarNode;
            public double NearElevationFeet, FarElevationFeet;
            public double HorizontalLengthFeet;
            public bool Skipped; // riser or rigid: no slope applied, original rise kept
        }

        public sealed class TargetResult
        {
            public bool Ok;
            public string Error;
            /// <summary>Node key -> target elevation, feet.</summary>
            public Dictionary<string, double> NodeElevationFeet = new Dictionary<string, double>();
            public List<EdgeResult> Edges = new List<EdgeResult>();
            /// <summary>Set when the floor check ran: floor elevation + clearance, feet.</summary>
            public double? MinAllowedElevationFeet;
            /// <summary>Set when the floor check refused: the lowest node and its target, feet,
            /// as data so the caller can name it and format it in its own units.</summary>
            public string LowestNode;
            public double LowestElevationFeet;
        }

        /// <summary>
        /// Target elevations for a gravity tree draining to outletNode, anchored so that
        /// fixedNode stays at fixedElevationFeet. floorElevationFeet + minClearanceFeet, when
        /// both are given, refuse the lowest node that would land below that height, by name.
        /// </summary>
        public static TargetResult ComputeTargets(IEnumerable<Edge> edges, string fixedNode, double fixedElevationFeet,
            double slopePercent, string outletNode, double? floorElevationFeet, double? minClearanceFeet)
        {
            var r = new TargetResult();
            if (string.IsNullOrEmpty(fixedNode)) { r.Error = "fixed node is required."; return r; }
            if (string.IsNullOrEmpty(outletNode)) { r.Error = "outlet node is required."; return r; }
            if (double.IsNaN(slopePercent) || double.IsInfinity(slopePercent) || slopePercent <= 0)
            { r.Error = "slope_percent must be a positive number."; return r; }
            double ratio = slopePercent / 100.0;

            var edgeList = (edges ?? Enumerable.Empty<Edge>()).ToList();
            var byNode = new Dictionary<string, List<Edge>>();
            foreach (Edge e in edgeList)
            {
                if (!byNode.TryGetValue(e.FromNode, out var la)) byNode[e.FromNode] = la = new List<Edge>();
                la.Add(e);
                if (!byNode.TryGetValue(e.ToNode, out var lb)) byNode[e.ToNode] = lb = new List<Edge>();
                lb.Add(e);
            }
            if (edgeList.Count > 0 && !byNode.ContainsKey(outletNode)) { r.Error = "outlet node " + outletNode + " touches no edge of this network."; return r; }
            if (edgeList.Count > 0 && !byNode.ContainsKey(fixedNode)) { r.Error = "fixed node " + fixedNode + " touches no edge of this network."; return r; }

            // h(n): height above the outlet, walking the tree outward FROM the outlet.
            var h = new Dictionary<string, double> { [outletNode] = 0.0 };
            var order = new List<EdgeResult>();
            var visitedEdges = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(outletNode);
            while (queue.Count > 0)
            {
                string node = queue.Dequeue();
                if (!byNode.TryGetValue(node, out var incident)) continue;
                foreach (Edge e in incident)
                {
                    if (!visitedEdges.Add(e.Id)) continue;
                    string far = e.FromNode == node ? e.ToNode : e.FromNode;
                    if (h.ContainsKey(far))
                    {
                        r.Error = "the network is not a tree: node " + far + " is reached by more than one path (loop through edge " + e.Id + ").";
                        return r;
                    }
                    bool vertical = e.Rigid || IsRiser(e.HorizontalLengthFeet, e.RiseFeet);
                    double rise = vertical ? (far == e.ToNode ? e.RiseFeet : -e.RiseFeet) : ratio * e.HorizontalLengthFeet;
                    h[far] = h[node] + rise;
                    order.Add(new EdgeResult { EdgeId = e.Id, NearNode = node, FarNode = far, HorizontalLengthFeet = e.HorizontalLengthFeet, Skipped = vertical });
                    queue.Enqueue(far);
                }
            }
            var unreached = byNode.Keys.Where(n => !h.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (unreached.Count > 0)
            {
                r.Error = "the network is not connected: node " + unreached[0] + " does not reach the outlet " + outletNode +
                    " (" + unreached.Count + " node(s) unreached). Name one connected run.";
                return r;
            }

            double offset = fixedElevationFeet - h[fixedNode];
            foreach (var kv in h) r.NodeElevationFeet[kv.Key] = kv.Value + offset;
            foreach (EdgeResult er in order)
            {
                er.NearElevationFeet = r.NodeElevationFeet[er.NearNode];
                er.FarElevationFeet = r.NodeElevationFeet[er.FarNode];
            }
            r.Edges = order;

            if (floorElevationFeet.HasValue && minClearanceFeet.HasValue)
            {
                double minAllowed = floorElevationFeet.Value + minClearanceFeet.Value;
                r.MinAllowedElevationFeet = minAllowed;
                var lowest = r.NodeElevationFeet.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
                if (lowest.Value < minAllowed - 1e-9)
                {
                    r.LowestNode = lowest.Key; r.LowestElevationFeet = lowest.Value;
                    r.Error = "node " + lowest.Key + " would land at " + lowest.Value.ToString("0.####", CultureInfo.InvariantCulture) +
                        " ft, below the floor plus min_clearance (" + minAllowed.ToString("0.####", CultureInfo.InvariantCulture) + " ft).";
                    r.NodeElevationFeet.Clear(); r.Edges = new List<EdgeResult>();
                    return r;
                }
            }
            r.Ok = true;
            return r;
        }

        /// <summary>Measured slope of a segment compared to the target within
        /// SlopeToleranceRatio, SIGNED: the near (downstream, outlet-side) end must be the
        /// lower one, so a pipe draining backwards at the right magnitude fails.
        /// Vertical/zero-length segments (below MinHorizontalLengthFeet) always pass -
        /// there is no slope to measure on them.</summary>
        public static bool SlopeWithinTolerance(double horizontalLengthFeet, double nearElevationFeet, double farElevationFeet,
            double expectedSlopePercent)
        {
            if (horizontalLengthFeet < MinHorizontalLengthFeet) return true;
            double measuredRatio = (farElevationFeet - nearElevationFeet) / horizontalLengthFeet;
            double expectedRatio = expectedSlopePercent / 100.0;
            return Math.Abs(measuredRatio - expectedRatio) <= SlopeToleranceRatio;
        }

        /// <summary>Parses fixed_end: "upstream", "downstream", "&lt;element_id&gt;",
        /// "&lt;element_id&gt;:high" or "&lt;element_id&gt;:low". Returns false with an error otherwise.
        /// held: null for the bare element form (direction then comes from flow, and the
        /// command refuses when flow does not read); true = held end is the high/upstream end.</summary>
        public static bool TryParseFixedEnd(string raw, out string word, out long elementId, out bool? heldIsHigh, out string error)
        {
            word = null; elementId = 0; heldIsHigh = null; error = null;
            string s = (raw ?? "").Trim().ToLowerInvariant();
            if (s == "upstream" || s == "downstream") { word = s; return true; }
            string idPart = s;
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                string suffix = s.Substring(colon + 1);
                idPart = s.Substring(0, colon);
                if (suffix == "high") heldIsHigh = true;
                else if (suffix == "low") heldIsHigh = false;
                else { error = "fixed_end suffix must be ':high' or ':low'."; return false; }
            }
            if (!long.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out elementId) || elementId <= 0)
            { error = "fixed_end must be 'upstream', 'downstream', or a pipe element_id optionally followed by ':high' or ':low'."; return false; }
            return true;
        }
    }
}
