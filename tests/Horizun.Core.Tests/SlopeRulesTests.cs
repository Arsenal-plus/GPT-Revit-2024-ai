// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_mep_routing's slope operation, the Revit-free half: target elevations
// walking a tree from the fixed node, a tee's branch sloping toward the main
// line without the main line ever noticing, the floor-clearance refusal, and
// zero-length/vertical segments skipped instead of producing a NaN slope.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;
using Edge = Horizun.Revit.Core.SlopeRules.Edge;

namespace Horizun.Core.Tests
{
    public class SlopeRulesTests
    {
        [Fact]
        public void Straight_run_drops_by_slope_times_length_from_upstream_fixed_end()
        {
            // A --10ft--> B --10ft--> C, 2% slope, fixed at A (upstream) at elevation 0.
            var edges = new List<Edge> { new Edge("s1", "A", "B", 10), new Edge("s2", "B", "C", 10) };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, fixedEndIsUpstream: true, null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.0, r.NodeElevationFeet["A"], 6);
            Assert.Equal(-0.2, r.NodeElevationFeet["B"], 6);
            Assert.Equal(-0.4, r.NodeElevationFeet["C"], 6);
        }

        [Fact]
        public void Downstream_fixed_end_rises_going_upstream()
        {
            // Same physical run, but the caller holds the downstream (low) end at C.
            var edges = new List<Edge> { new Edge("s1", "A", "B", 10), new Edge("s2", "B", "C", 10) };
            var r = SlopeRules.ComputeTargets(edges, "C", 0.0, 2.0, fixedEndIsUpstream: false, null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.0, r.NodeElevationFeet["C"], 6);
            Assert.Equal(0.2, r.NodeElevationFeet["B"], 6);
            Assert.Equal(0.4, r.NodeElevationFeet["A"], 6);
        }

        [Fact]
        public void Run_with_elbows_is_just_more_edges_and_still_sums_correctly()
        {
            // Two elbows are two extra intermediate nodes (E1, E2); each leg has its own
            // horizontal length, matching what a real elbow-jogged run would report.
            var edges = new List<Edge>
            {
                new Edge("s1", "A", "E1", 4), new Edge("s2", "E1", "E2", 6), new Edge("s3", "E2", "B", 5)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 100.0, 1.0, fixedEndIsUpstream: true, null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(100.0 - 0.04, r.NodeElevationFeet["E1"], 6);
            Assert.Equal(100.0 - 0.04 - 0.06, r.NodeElevationFeet["E2"], 6);
            Assert.Equal(100.0 - 0.04 - 0.06 - 0.05, r.NodeElevationFeet["B"], 6);
        }

        [Fact]
        public void Tee_branch_slopes_toward_the_main_without_touching_the_main_lines_own_elevation()
        {
            // Main line A -> T -> C (20 ft total). A branch leaves T toward D (8 ft).
            // The branch's own slope is computed from T's elevation; C's elevation is
            // whatever the MAIN line's edges say, independent of the branch.
            var edges = new List<Edge>
            {
                new Edge("main1", "A", "T", 10), new Edge("main2", "T", "C", 10), new Edge("branch", "T", "D", 8)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, fixedEndIsUpstream: true, null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(-0.2, r.NodeElevationFeet["T"], 6);
            Assert.Equal(-0.4, r.NodeElevationFeet["C"], 6);   // main line: unaffected by the branch
            Assert.Equal(-0.2 - 0.16, r.NodeElevationFeet["D"], 6); // branch: slopes further from T
        }

        [Fact]
        public void Refuses_when_a_node_would_go_below_the_floor_clearance()
        {
            var edges = new List<Edge> { new Edge("s1", "A", "B", 100) };
            // 2% of 100 ft = 2 ft drop; floor at -1.5 ft with 1 ft clearance means B must
            // stay at or above -0.5 ft, but B lands at -2 ft.
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, fixedEndIsUpstream: true, -1.5, 1.0);
            Assert.False(r.Ok);
            Assert.Contains("B", r.Error);
        }

        [Fact]
        public void Zero_length_or_vertical_segments_are_skipped_not_divided_by_zero()
        {
            // A vertical riser A->V (0 horizontal length) then a normal run V->B (10 ft).
            var edges = new List<Edge> { new Edge("riser", "A", "V", 0.0), new Edge("run", "V", "B", 10) };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, fixedEndIsUpstream: true, null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.0, r.NodeElevationFeet["V"], 6); // riser contributes no slope
            Assert.Equal(-0.2, r.NodeElevationFeet["B"], 6);
            var riserEdge = r.Edges.Find(e => e.EdgeId == "riser");
            Assert.NotNull(riserEdge);
            Assert.True(riserEdge.Skipped);
        }

        [Fact]
        public void A_loop_is_refused_by_name_instead_of_picking_one_path()
        {
            var edges = new List<Edge>
            {
                new Edge("s1", "A", "B", 10), new Edge("s2", "B", "C", 10), new Edge("s3", "C", "A", 10)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, fixedEndIsUpstream: true, null, null);
            Assert.False(r.Ok);
            Assert.Contains("loop", r.Error);
        }

        [Fact]
        public void Slope_tolerance_accepts_within_005_percentage_points_and_rejects_beyond()
        {
            // Target 2%, 100 ft run: exactly-on-target 2 ft drop passes; a drop giving
            // 2.06% (0.06 pp off) is rejected, and 2.04% (0.04 pp off) still passes.
            Assert.True(SlopeRules.SlopeWithinTolerance(100, 0, -2.0, 2.0));
            Assert.True(SlopeRules.SlopeWithinTolerance(100, 0, -2.04, 2.0));
            Assert.False(SlopeRules.SlopeWithinTolerance(100, 0, -2.06, 2.0));
        }
    }
}
