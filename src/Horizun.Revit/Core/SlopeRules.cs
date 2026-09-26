// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_mep_routing's slope operation: given the
// network as a tree of nodes and edges (an edge is one segment's horizontal
// run between two logical junctions - a pipe end, an elbow, a tee), computes
// the target elevation of every node by walking OUT from the one node the
// caller holds fixed.
//
// WHY A TREE, NOT A GRAPH WITH CYCLES. A gravity network with a loop has no
// single slope solution (the two paths around the loop would demand two
// different elevations at the point they meet), so ComputeTargets refuses
// with a named node the moment a second edge reaches an already-elevated
// node - it does not pick one arbitrarily.
//
// WHY A TEE "KEEPS ITS MAIN-LINE ELEVATION" FOR FREE. Each node's elevation is
// set exactly once, from the edge that first reaches it while walking the
// tree outward from the fixed node. The main run and every branch off it are
// just different edges leaving that same node - the branch's slope is
// computed from THAT node's own elevation, so the main line's own elevation
// is never touched by what a branch does. No special-casing a "Tees" group.
//
// UNITS. Every length and elevation here is in feet, matching Revit's
// internal units - the command that owns this converts once, at the edges.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SlopeRules
    {
        /// <summary>0.05 percentage points, as a ratio (0.05 / 100). Measured slope
        /// within this of the target counts as verified.</summary>
        public const double SlopeToleranceRatio = 0.0005;

        /// <summary>Below this horizontal run, in feet (about 3 mm), a segment is
        /// treated as vertical/zero-length: it is skipped and its far node inherits
        /// the near node's elevation unchanged rather than dividing by ~zero.</summary>
        public const double MinHorizontalLengthFeet = 0.01;

        public sealed class Edge
        {
            public readonly string Id;
            public readonly string FromNode;
            public readonly string ToNode;
            public readonly double HorizontalLengthFeet;
            public Edge(string id, string fromNode, string toNode, double horizontalLengthFeet)
            { Id = id; FromNode = fromNode; ToNode = toNode; HorizontalLengthFeet = horizontalLengthFeet; }
        }

        public sealed class EdgeResult
        {
            public string EdgeId;
            public string NearNode, FarNode;
            public double NearElevationFeet, FarElevationFeet;
            public double HorizontalLengthFeet;
            public bool Skipped; // vertical/zero-length: no slope applied
        }

        public sealed class TargetResult
        {
            public bool Ok;
            public string Error;
            /// <summary>Node key -> target elevation, feet. Only nodes reached from
            /// the fixed node (the whole tree, when it IS a tree) are present.</summary>
            public Dictionary<string, double> NodeElevationFeet = new Dictionary<string, double>();
            public List<EdgeResult> Edges = new List<EdgeResult>();
        }

        /// <summary>
        /// Walks the network outward from fixedNode (held at fixedElevationFeet) and
        /// returns the target elevation of every other node. fixedEndIsUpstream:
        /// true when the fixed node is the high/upstream end, so elevation DROPS by
        /// slopePercent/100 per horizontal foot moving away from it (a normal gravity
        /// run draining away from where it starts); false when the fixed node is the
        /// downstream/low end held in place and elevation RISES moving away from it
        /// (the same physical slope, referenced from the other end).
        ///
        /// floorElevationFeet + minClearanceFeet, when both are given, refuse the
        /// first node computed below that floor - the network never asks Revit to
        /// route through a slab.
        /// </summary>
        public static TargetResult ComputeTargets(IEnumerable<Edge> edges, string fixedNode, double fixedElevationFeet,
            double slopePercent, bool fixedEndIsUpstream, double? floorElevationFeet, double? minClearanceFeet)
        {
            var r = new TargetResult();
            if (string.IsNullOrEmpty(fixedNode)) { r.Error = "fixed node is required."; return r; }
            if (double.IsNaN(slopePercent) || double.IsInfinity(slopePercent) || slopePercent <= 0)
            { r.Error = "slope_percent must be a positive number."; return r; }
            double ratio = slopePercent / 100.0;
            double minClear = floorElevationFeet.HasValue && minClearanceFeet.HasValue ? floorElevationFeet.Value + minClearanceFeet.Value : double.NaN;
            bool checkFloor = !double.IsNaN(minClear);

            var edgeList = (edges ?? Enumerable.Empty<Edge>()).ToList();
            var byNode = new Dictionary<string, List<Edge>>();
            foreach (Edge e in edgeList)
            {
                if (!byNode.TryGetValue(e.FromNode, out var la)) byNode[e.FromNode] = la = new List<Edge>();
                la.Add(e);
                if (!byNode.TryGetValue(e.ToNode, out var lb)) byNode[e.ToNode] = lb = new List<Edge>();
                lb.Add(e);
            }
            if (!byNode.ContainsKey(fixedNode) && edgeList.Count > 0) { r.Error = "fixed node " + fixedNode + " touches no edge of this network."; return r; }

            r.NodeElevationFeet[fixedNode] = fixedElevationFeet;
            if (checkFloor && fixedElevationFeet < minClear)
            { r.Error = "fixed node " + fixedNode + " is already below the minimum clearance above the floor."; return r; }

            var visitedEdges = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(fixedNode);
            while (queue.Count > 0)
            {
                string node = queue.Dequeue();
                double nodeElev = r.NodeElevationFeet[node];
                if (!byNode.TryGetValue(node, out var incident)) continue;
                foreach (Edge e in incident)
                {
                    if (visitedEdges.Contains(e.Id)) continue;
                    string far = e.FromNode == node ? e.ToNode : e.FromNode;
                    if (r.NodeElevationFeet.ContainsKey(far))
                    {
                        // A second, different-length path reaching an already-elevated node is a
                        // loop: the tree assumption this algorithm relies on does not hold.
                        visitedEdges.Add(e.Id);
                        r.Error = "the network is not a tree: node " + far + " is reached by more than one path (loop through edge " + e.Id + ").";
                        return r;
                    }
                    visitedEdges.Add(e.Id);
                    bool vertical = e.HorizontalLengthFeet < MinHorizontalLengthFeet;
                    double farElev = vertical ? nodeElev : nodeElev + (fixedEndIsUpstream ? -1.0 : 1.0) * ratio * e.HorizontalLengthFeet;
                    if (checkFloor && farElev < minClear)
                    {
                        r.Error = "node " + far + " would land at " + farElev.ToString("G6") + " ft, below the required clearance above the floor (" + minClear.ToString("G6") + " ft). Nothing was written.";
                        return r;
                    }
                    r.NodeElevationFeet[far] = farElev;
                    r.Edges.Add(new EdgeResult
                    {
                        EdgeId = e.Id, NearNode = node, FarNode = far,
                        NearElevationFeet = nodeElev, FarElevationFeet = farElev,
                        HorizontalLengthFeet = e.HorizontalLengthFeet, Skipped = vertical
                    });
                    queue.Enqueue(far);
                }
            }
            r.Ok = true;
            return r;
        }

        /// <summary>Measured slope of a segment (feet drop / horizontal feet), compared to
        /// the target within SlopeToleranceRatio. Vertical/zero-length segments (below
        /// MinHorizontalLengthFeet) are always considered verified - there is no slope to
        /// measure on them.</summary>
        public static bool SlopeWithinTolerance(double horizontalLengthFeet, double nearElevationFeet, double farElevationFeet,
            double expectedSlopePercent)
        {
            if (horizontalLengthFeet < MinHorizontalLengthFeet) return true;
            double measuredRatio = Math.Abs(nearElevationFeet - farElevationFeet) / horizontalLengthFeet;
            double expectedRatio = expectedSlopePercent / 100.0;
            return Math.Abs(measuredRatio - expectedRatio) <= SlopeToleranceRatio;
        }
    }
}
