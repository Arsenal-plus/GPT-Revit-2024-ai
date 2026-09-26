// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_mep_routing's slope operation, the Revit-free half: target elevations
// of a gravity tree draining to one outlet, anchored at the node the caller
// holds; a tee's branch draining TOWARD the main whichever end is held; the
// floor-clearance refusal; vertical/zero-length segments keeping their rise;
// loops and disconnected runs refused by name; and the fixed_end grammar.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
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
            // A --10ft--> B --10ft--> C, 2% slope, A held (upstream), outlet C.
            var edges = new List<Edge> { new Edge("s1", "A", "B", 10), new Edge("s2", "B", "C", 10) };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "C", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.0, r.NodeElevationFeet["A"], 6);
            Assert.Equal(-0.2, r.NodeElevationFeet["B"], 6);
            Assert.Equal(-0.4, r.NodeElevationFeet["C"], 6);
        }

        [Fact]
        public void Downstream_fixed_end_rises_going_upstream()
        {
            // Same physical run, the caller holds the outlet C itself.
            var edges = new List<Edge> { new Edge("s1", "A", "B", 10), new Edge("s2", "B", "C", 10) };
            var r = SlopeRules.ComputeTargets(edges, "C", 0.0, 2.0, "C", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.0, r.NodeElevationFeet["C"], 6);
            Assert.Equal(0.2, r.NodeElevationFeet["B"], 6);
            Assert.Equal(0.4, r.NodeElevationFeet["A"], 6);
        }

        [Fact]
        public void Run_with_elbows_counts_the_fitting_legs_as_path_length()
        {
            // Pipe A-P1 (4 ft), elbow E1 as centre node with two 0.25 ft legs, pipe P2-P3 (6 ft),
            // elbow E2 (two 0.25 ft legs), pipe P4-B (5 ft). Outlet B, A held at 100.
            var edges = new List<Edge>
            {
                new Edge("pipe1", "A", "P1", 4), new Edge("e1a", "P1", "E1", 0.25), new Edge("e1b", "E1", "P2", 0.25),
                new Edge("pipe2", "P2", "P3", 6), new Edge("e2a", "P3", "E2", 0.25), new Edge("e2b", "E2", "P4", 0.25),
                new Edge("pipe3", "P4", "B", 5)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 100.0, 1.0, "B", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(100.0 - 0.04, r.NodeElevationFeet["P1"], 6);
            Assert.Equal(100.0 - 0.045, r.NodeElevationFeet["P2"], 6);
            Assert.Equal(100.0 - 0.105, r.NodeElevationFeet["P3"], 6);
            Assert.Equal(100.0 - 0.16, r.NodeElevationFeet["B"], 6);
            // every pipe edge holds exactly the target slope
            foreach (var e in r.Edges)
                Assert.True(SlopeRules.SlopeWithinTolerance(e.HorizontalLengthFeet, e.NearElevationFeet, e.FarElevationFeet, 1.0), e.EdgeId);
        }

        [Fact]
        public void Tee_branch_drains_toward_the_main_and_the_main_keeps_its_line()
        {
            // Main A -> T -> C (outlet C). A branch D -> T (8 ft) joins at the tee centre T.
            var edges = new List<Edge>
            {
                new Edge("main1", "A", "T", 10), new Edge("main2", "T", "C", 10), new Edge("branch", "D", "T", 8)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "C", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(-0.2, r.NodeElevationFeet["T"], 6);
            Assert.Equal(-0.4, r.NodeElevationFeet["C"], 6);              // main line: as if no branch
            Assert.Equal(-0.2 + 0.16, r.NodeElevationFeet["D"], 6);       // branch: RISES away from the tee
            Assert.True(r.NodeElevationFeet["D"] > r.NodeElevationFeet["T"]);
        }

        [Fact]
        public void Holding_the_branch_end_still_drains_everything_to_the_outlet()
        {
            var edges = new List<Edge>
            {
                new Edge("main1", "A", "T", 10), new Edge("main2", "T", "C", 10), new Edge("branch", "D", "T", 5)
            };
            var r = SlopeRules.ComputeTargets(edges, "D", 0.0, 2.0, "C", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(-0.1, r.NodeElevationFeet["T"], 6);
            Assert.Equal(-0.3, r.NodeElevationFeet["C"], 6);
            Assert.Equal(0.1, r.NodeElevationFeet["A"], 6); // main upstream end rises from the tee
        }

        [Fact]
        public void Refuses_when_a_node_would_go_below_the_floor_clearance()
        {
            var edges = new List<Edge> { new Edge("s1", "A", "B", 100) };
            // 2% of 100 ft = 2 ft drop; floor -1.5 ft + 1 ft clearance -> B must stay >= -0.5 ft.
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "B", -1.5, 1.0);
            Assert.False(r.Ok);
            Assert.Contains("node B", r.Error);
            Assert.Contains("min_clearance", r.Error);
            Assert.Empty(r.NodeElevationFeet);

            var ok = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "B", -3.0, 1.0);
            Assert.True(ok.Ok, ok.Error);
            Assert.Equal(-2.0, ok.MinAllowedElevationFeet.Value, 6);
        }

        [Fact]
        public void Vertical_segments_keep_their_rise_and_zero_length_ones_are_skipped()
        {
            // A --10ft--> B, riser B -> V dropping 3 ft (0 horizontal), V --5ft--> C (outlet),
            // plus a zero-length stub C -> Z.
            var edges = new List<Edge>
            {
                new Edge("run1", "A", "B", 10), new Edge("riser", "B", "V", 0.0, -3.0),
                new Edge("run2", "V", "C", 5), new Edge("stub", "C", "Z", 0.0)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "C", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(-0.2, r.NodeElevationFeet["B"], 6);
            Assert.Equal(-3.2, r.NodeElevationFeet["V"], 6); // the riser stays a 3 ft riser
            Assert.Equal(-3.3, r.NodeElevationFeet["C"], 6);
            Assert.Equal(-3.3, r.NodeElevationFeet["Z"], 6);
            Assert.True(r.Edges.Find(e => e.EdgeId == "riser").Skipped);
            Assert.True(r.Edges.Find(e => e.EdgeId == "stub").Skipped);
            Assert.False(r.Edges.Find(e => e.EdgeId == "run1").Skipped);
        }

        [Fact]
        public void A_loop_is_refused_by_name_instead_of_picking_one_path()
        {
            var edges = new List<Edge>
            {
                new Edge("s1", "A", "B", 10), new Edge("s2", "B", "C", 10), new Edge("s3", "C", "A", 10)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "C", null, null);
            Assert.False(r.Ok);
            Assert.Contains("loop", r.Error);
        }

        [Fact]
        public void A_disconnected_run_is_refused()
        {
            var edges = new List<Edge> { new Edge("s1", "A", "B", 10), new Edge("s2", "X", "Y", 10) };
            var r = SlopeRules.ComputeTargets(edges, "A", 0.0, 2.0, "B", null, null);
            Assert.False(r.Ok);
            Assert.Contains("not connected", r.Error);
        }

        [Fact]
        public void Slope_tolerance_accepts_within_005_percentage_points_and_rejects_beyond()
        {
            // near = the outlet-side end, far = the upstream end: far sits higher.
            Assert.True(SlopeRules.SlopeWithinTolerance(100, 0, 2.0, 2.0));
            Assert.True(SlopeRules.SlopeWithinTolerance(100, 0, 2.04, 2.0));
            Assert.False(SlopeRules.SlopeWithinTolerance(100, 0, 2.06, 2.0));
            Assert.True(SlopeRules.SlopeWithinTolerance(0.001, 0, -5, 2.0)); // vertical: nothing to measure
        }

        [Fact]
        public void Slope_tolerance_rejects_a_pipe_draining_backwards_at_the_right_magnitude()
        {
            // The upstream end LOWER than the outlet end by exactly 2 percent: right size, wrong way.
            Assert.False(SlopeRules.SlopeWithinTolerance(100, 0, -2.0, 2.0));
            Assert.False(SlopeRules.SlopeWithinTolerance(100, 5.0, 3.0, 2.0));
        }

        [Fact]
        public void A_riser_with_a_few_millimetres_of_plan_offset_keeps_its_rise()
        {
            // A 3 m (9.84 ft) riser drawn with 5 mm (0.0164 ft) of plan offset: by an absolute
            // plan-length rule it is a sloped pipe and flattens to ~0.0003 ft; by angle it is a riser.
            Assert.True(SlopeRules.IsRiser(0.0164, -9.84));
            Assert.True(SlopeRules.IsRiser(1.0, 1.0));     // a 45 degree offset
            Assert.False(SlopeRules.IsRiser(10.0, 0.5));   // 5 percent: a sloped pipe
            var edges = new List<Edge> { new Edge("run", "A", "B", 10), new Edge("riser", "B", "C", 0.0164, 9.84) };
            var r = SlopeRules.ComputeTargets(edges, "C", 20.0, 2.0, "A", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(20.0 - 9.84, r.NodeElevationFeet["B"], 6);   // the riser kept its 9.84 ft
            Assert.Equal(20.0 - 9.84 - 0.2, r.NodeElevationFeet["A"], 6);
            Assert.True(r.Edges.Single(e => e.EdgeId == "riser").Skipped);
        }

        [Fact]
        public void A_rigid_fitting_leg_keeps_its_original_rise_so_connectors_move_with_the_centre()
        {
            // pipe1 A-P1, elbow legs P1-E and E-P2 (0.5 ft each, flat), pipe2 P2-B.
            var edges = new List<Edge>
            {
                new Edge("pipe1", "A", "P1", 10), new Edge("leg1", "E", "P1", 0.5, 0.0, true),
                new Edge("leg2", "E", "P2", 0.5, 0.0, true), new Edge("pipe2", "P2", "B", 10)
            };
            var r = SlopeRules.ComputeTargets(edges, "A", 10.0, 2.0, "B", null, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(r.NodeElevationFeet["E"], r.NodeElevationFeet["P1"], 9);
            Assert.Equal(r.NodeElevationFeet["E"], r.NodeElevationFeet["P2"], 9);
            Assert.Equal(10.0 - 0.4, r.NodeElevationFeet["B"], 9); // only the pipes carry the drop
        }

        [Fact]
        public void The_floor_refusal_names_the_lowest_node_as_data()
        {
            var edges = new List<Edge> { new Edge("s1", "A", "B", 100) };
            var r = SlopeRules.ComputeTargets(edges, "A", 1.0, 2.0, "B", 0.0, 0.0);
            Assert.False(r.Ok);
            Assert.Equal("B", r.LowestNode);
            Assert.Equal(-1.0, r.LowestElevationFeet, 9);
        }

        [Theory]
        [InlineData("upstream", "upstream", 0L, null)]
        [InlineData(" Downstream ", "downstream", 0L, null)]
        [InlineData("12345", null, 12345L, null)]
        [InlineData("12345:high", null, 12345L, true)]
        [InlineData("12345:LOW", null, 12345L, false)]
        public void Fixed_end_grammar_parses(string raw, string word, long id, bool? high)
        {
            Assert.True(SlopeRules.TryParseFixedEnd(raw, out string w, out long e, out bool? h, out string err), err);
            Assert.Equal(word, w);
            Assert.Equal(id, e);
            Assert.Equal(high, h);
        }

        [Theory]
        [InlineData("")]
        [InlineData("middle")]
        [InlineData("12:up")]
        [InlineData("-5")]
        public void Fixed_end_grammar_refuses(string raw)
        {
            Assert.False(SlopeRules.TryParseFixedEnd(raw, out _, out _, out _, out string err));
            Assert.False(string.IsNullOrEmpty(err));
        }
    }
}
