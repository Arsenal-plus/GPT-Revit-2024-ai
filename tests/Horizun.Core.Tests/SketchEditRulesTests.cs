// Horizun Revit MCP - original Horizun code.
// Core/SketchEditRules: the arithmetic behind horizun_transform_elements edit_sketch.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class SketchEditRulesTests
    {
        private static List<SketchPt> Rect(double x0, double y0, double x1, double y1) =>
            new List<SketchPt> { new SketchPt(x0, y0), new SketchPt(x1, y0), new SketchPt(x1, y1), new SketchPt(x0, y1) };

        private static IList<IList<SketchPt>> L(params List<SketchPt>[] loops) => loops.Cast<IList<SketchPt>>().ToList();

        [Fact]
        public void Area_of_a_rectangle_with_a_hole_subtracts_the_hole()
        {
            Assert.Equal(4000.0 * 3000.0, SketchEditRules.NetArea(L(Rect(0, 0, 4000, 3000))), 6);
            Assert.Equal(4000.0 * 3000.0 - 1000.0 * 1000.0,
                SketchEditRules.NetArea(L(Rect(0, 0, 4000, 3000), Rect(1000, 1000, 2000, 2000))), 6);
        }

        [Fact]
        public void Two_islands_are_two_solids_not_an_island_with_a_hole()
        {
            double a = SketchEditRules.NetArea(L(Rect(0, 0, 1000, 1000), Rect(5000, 0, 7000, 1000)));
            Assert.Equal(1000.0 * 1000.0 + 2000.0 * 1000.0, a, 6);
        }

        [Fact]
        public void Island_inside_a_hole_counts_again()
        {
            double a = SketchEditRules.NetArea(L(Rect(0, 0, 10000, 10000), Rect(2000, 2000, 8000, 8000), Rect(4000, 4000, 5000, 5000)));
            Assert.Equal(1e8 - 3.6e7 + 1e6, a, 6);
        }

        [Fact]
        public void Validate_refuses_self_intersection_short_edges_and_degenerate_loops()
        {
            Assert.Null(SketchEditRules.ValidateLoop(Rect(0, 0, 4000, 3000)));
            var bowtie = new List<SketchPt> { new SketchPt(0, 0), new SketchPt(1000, 1000), new SketchPt(1000, 0), new SketchPt(0, 1000) };
            Assert.Contains("intersects itself", SketchEditRules.ValidateLoop(bowtie));
            var shortEdge = new List<SketchPt> { new SketchPt(0, 0), new SketchPt(0.5, 0), new SketchPt(1000, 1000) };
            Assert.Contains("shorter than", SketchEditRules.ValidateLoop(shortEdge));
            var line = new List<SketchPt> { new SketchPt(0, 0), new SketchPt(1000, 0), new SketchPt(2000, 0) };
            Assert.NotNull(SketchEditRules.ValidateLoop(line));
            Assert.NotNull(SketchEditRules.ValidateLoop(new List<SketchPt> { new SketchPt(0, 0), new SketchPt(1, 1) }));
        }

        [Fact]
        public void Normalise_drops_a_repeated_closing_point_only()
        {
            var closed = Rect(0, 0, 10, 10); closed.Add(new SketchPt(0, 0));
            Assert.Equal(4, SketchEditRules.Normalise(closed).Count);
            Assert.Equal(4, SketchEditRules.Normalise(Rect(0, 0, 10, 10)).Count);
        }

        [Fact]
        public void Chain_orders_shuffled_and_reversed_segments_into_loops()
        {
            SketchPt a = new SketchPt(0, 0), b = new SketchPt(1000, 0), c = new SketchPt(1000, 1000), d = new SketchPt(0, 1000);
            SketchSegment S(SketchPt p, SketchPt q) => new SketchSegment(p, q, new SketchPt((p.X + q.X) / 2, (p.Y + q.Y) / 2));
            var segs = new List<SketchSegment> { S(a, b), S(d, c), S(d, a), S(b, c), S(new SketchPt(200, 200), new SketchPt(300, 200)),
                                                 S(new SketchPt(300, 300), new SketchPt(300, 200)), S(new SketchPt(300, 300), new SketchPt(200, 200)) };
            string problem;
            var loops = SketchEditRules.Chain(segs, 0.5, out problem);
            Assert.Null(problem);
            Assert.Equal(2, loops.Count);
            List<SketchPt> v = SketchEditRules.Vertices(segs, loops[0]);
            Assert.True(SketchEditRules.SameLoop(v, Rect(0, 0, 1000, 1000), 0.5));
            Assert.Equal(3, loops[1].Count);
        }

        [Fact]
        public void Chain_refuses_an_open_profile()
        {
            SketchSegment S(double x0, double y0, double x1, double y1) => new SketchSegment(new SketchPt(x0, y0), new SketchPt(x1, y1), new SketchPt((x0 + x1) / 2, (y0 + y1) / 2));
            string problem;
            Assert.Null(SketchEditRules.Chain(new List<SketchSegment> { S(0, 0, 1000, 0), S(1000, 0, 1000, 1000) }, 0.5, out problem));
            Assert.Contains("open", problem);
        }

        [Fact]
        public void SameLoop_accepts_rotation_and_reversal_and_refuses_a_moved_vertex()
        {
            var r = Rect(0, 0, 4000, 3000);
            var rotated = new List<SketchPt> { r[2], r[3], r[0], r[1] };
            var reversed = Enumerable.Reverse(r).ToList();
            Assert.True(SketchEditRules.SameLoop(r, rotated, 0.5));
            Assert.True(SketchEditRules.SameLoop(r, reversed, 0.5));
            Assert.False(SketchEditRules.SameLoop(r, SketchEditRules.MoveVertex(r, 2, new SketchPt(4000, 3001)), 0.5));
            Assert.False(SketchEditRules.SameLoop(r, r.Take(3).ToList(), 0.5));
        }

        [Fact]
        public void MatchLoops_is_order_free_but_each_actual_loop_matches_once()
        {
            var outer = Rect(0, 0, 4000, 3000); var hole = Rect(1000, 1000, 2000, 2000);
            Assert.Null(SketchEditRules.MatchLoops(L(outer, hole), L(hole, outer), 0.5));
            Assert.NotNull(SketchEditRules.MatchLoops(L(outer, hole), L(outer, outer), 0.5));
            Assert.Contains("expected 2", SketchEditRules.MatchLoops(L(outer, hole), L(outer), 0.5));
        }

        [Fact]
        public void FindVertex_needs_exactly_one_match()
        {
            int l, v; string problem;
            var loops = L(Rect(0, 0, 4000, 3000), Rect(1000, 1000, 2000, 2000));
            Assert.True(SketchEditRules.FindVertex(loops, new SketchPt(2000.2, 2000), 0.5, out l, out v, out problem));
            Assert.Equal(1, l); Assert.Equal(2, v);
            Assert.False(SketchEditRules.FindVertex(loops, new SketchPt(5, 5), 0.5, out l, out v, out problem));
            Assert.Contains("no vertex", problem);
            var twins = L(Rect(0, 0, 10, 10), Rect(0, 0, 20, 20));
            Assert.False(SketchEditRules.FindVertex(twins, new SketchPt(0, 0), 0.5, out l, out v, out problem));
            Assert.Contains("2 vertices", problem);
        }

        [Fact]
        public void Moving_a_corner_changes_the_area_by_the_triangle_it_sweeps()
        {
            var r = Rect(0, 0, 4000, 3000);
            var moved = SketchEditRules.MoveVertex(r, 2, new SketchPt(5000, 3000));
            Assert.Equal(4000.0 * 3000.0 + 0.5 * 1000.0 * 3000.0, SketchEditRules.NetArea(L(moved)), 6);
        }

        [Fact]
        public void SameSegment_uses_the_midpoint_to_tell_an_arc_from_a_chord()
        {
            var chord = new SketchSegment(new SketchPt(0, 0), new SketchPt(1000, 0), new SketchPt(500, 0));
            var arc = new SketchSegment(new SketchPt(1000, 0), new SketchPt(0, 0), new SketchPt(500, 200));
            var flipped = new SketchSegment(new SketchPt(1000, 0), new SketchPt(0, 0), new SketchPt(500, 0));
            Assert.False(SketchEditRules.SameSegment(chord, arc, 0.5));
            Assert.True(SketchEditRules.SameSegment(chord, flipped, 0.5));
        }

        [Fact]
        public void Area_agreement_tolerance_is_five_square_centimetres_or_a_tenth_of_a_percent()
        {
            Assert.True(SketchEditRules.AreaAgrees(12.0004, 12.0));
            Assert.False(SketchEditRules.AreaAgrees(12.02, 12.0));
            Assert.True(SketchEditRules.AreaAgrees(1000.9, 1000.0));
        }
    }
}
