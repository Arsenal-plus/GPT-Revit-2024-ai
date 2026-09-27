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
        public void A_loop_that_touches_or_crosses_another_loop_is_named_and_a_clear_one_is_not()
        {
            var outer = Rect(0, 0, 4000, 3000);
            // A hole well inside the boundary is clear of it; a profile of one loop has nothing to cross.
            Assert.Null(SketchEditRules.CrossesOtherLoops(L(outer, Rect(1000, 1000, 2000, 2000)), 1));
            Assert.Null(SketchEditRules.CrossesOtherLoops(L(outer), 0));
            // A hole pushed through the boundary's edge crosses it, and the reason names that loop.
            string crossing = SketchEditRules.CrossesOtherLoops(L(outer, Rect(3500, 1000, 4500, 2000)), 1);
            Assert.NotNull(crossing);
            Assert.Contains("loop 0", crossing);
            // A hole lying ON the boundary's edge only touches it - Revit refuses that as well.
            Assert.NotNull(SketchEditRules.CrossesOtherLoops(L(outer, Rect(3000, 1000, 4000, 2000)), 1));
            // The boundary shrunk onto a hole is refused from the boundary's side too.
            string shrunk = SketchEditRules.CrossesOtherLoops(L(Rect(0, 0, 2000, 3000), Rect(1000, 1000, 2000, 2000)), 0);
            Assert.NotNull(shrunk);
            Assert.Contains("loop 1", shrunk);
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

        private static List<SketchPt> SqLoop(double x, double y, double w, double h) =>
            new List<SketchPt> { new SketchPt(x, y), new SketchPt(x + w, y), new SketchPt(x + w, y + h), new SketchPt(x, y + h) };

        [Fact]
        public void RoleChange_refuses_a_hole_left_outside_and_an_island_swallowed()
        {
            var hole = SqLoop(8000, 8000, 1000, 1000);
            var before = new List<IList<SketchPt>> { SqLoop(0, 0, 10000, 10000), hole };
            // The outer loop stops short of the hole without touching it: the hole would become slab.
            Assert.Contains("loop 1", SketchEditRules.RoleChange(before, new List<IList<SketchPt>> { SqLoop(0, 0, 6000, 6000), hole }));
            Assert.Null(SketchEditRules.RoleChange(before, new List<IList<SketchPt>> { SqLoop(0, 0, 9500, 9500), hole }));
            // ... and NetArea, which reads the same parity, would count it as slab: why the plan refuses first.
            Assert.Equal(6000.0 * 6000 + 1000 * 1000, SketchEditRules.NetArea(new List<IList<SketchPt>> { SqLoop(0, 0, 6000, 6000), hole }), 3);
            var island = SqLoop(20000, 2000, 1000, 1000);
            var two = new List<IList<SketchPt>> { SqLoop(0, 0, 10000, 10000), island };
            Assert.Contains("a solid into a hole", SketchEditRules.RoleChange(two, new List<IList<SketchPt>> { SqLoop(0, 0, 25000, 10000), island }));
        }

        [Fact]
        public void ValidateMovedVertex_holds_the_moved_edges_of_a_loop_with_arcs()
        {
            // A top side tessellated like an arc, with a chord under 1 mm: ValidateLoop's every-edge
            // rule refuses the outline itself, which is why the moved vertex is held on its own.
            var outline = new List<SketchPt> { new SketchPt(0, 0), new SketchPt(4000, 0), new SketchPt(4000, 3000),
                                               new SketchPt(2000, 3000.5), new SketchPt(2000.5, 3000.6), new SketchPt(0, 3000) };
            Assert.NotNull(SketchEditRules.ValidateLoop(outline));
            var fine = outline.ToList(); fine[1] = new SketchPt(4500, -200);
            Assert.Null(SketchEditRules.ValidateMovedVertex(fine, 1));
            var crossing = outline.ToList(); crossing[1] = new SketchPt(1000, 5000);
            Assert.Contains("cross", SketchEditRules.ValidateMovedVertex(crossing, 1));
            var tiny = outline.ToList(); tiny[1] = new SketchPt(0.5, 0);
            Assert.Contains("shorter", SketchEditRules.ValidateMovedVertex(tiny, 1));
        }

        [Fact]
        public void AlignCyclic_carries_each_old_vertex_to_the_nearest_new_one_in_either_direction()
        {
            var old = SqLoop(0, 0, 6000, 4000);
            // The same rectangle narrowed to 5000, sent clockwise from another corner.
            var sent = new List<SketchPt> { new SketchPt(5000, 4000), new SketchPt(5000, 0), new SketchPt(0, 0), new SketchPt(0, 4000) };
            Assert.Equal(new[] { 2, 1, 0, 3 }, SketchEditRules.AlignCyclic(old, sent));
            Assert.Null(SketchEditRules.AlignCyclic(old, sent.Take(3).ToList()));
        }
    }
}
