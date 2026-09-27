// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_framing operation=ceiling, the Revit-free half: the centred-strips
// grid of mains and cross members clipped to a boundary with holes, the
// perimeter, the long/short direction and the hanger stations. Millimetres.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CeilingFramingRulesTests
    {
        private const int Budget = 5000;

        private static List<double[]> Loop(params double[] xy)
        {
            var loop = new List<double[]>();
            for (int i = 0; i + 1 < xy.Length; i += 2) loop.Add(new[] { xy[i], xy[i + 1] });
            return loop;
        }

        private static CeilingFramingInput Ceiling(double mainSpacing, params List<double[]>[] loops)
            => new CeilingFramingInput
            {
                Loops = loops.ToList(), MainSpacing = mainSpacing, MainTypeKey = "main",
                CrossTypeKey = "cross", HangerTypeKey = "rod",
            };

        private static int Count(CeilingFramingPlan plan, string role) => plan.CountByRole().TryGetValue(role, out int n) ? n : 0;

        private static List<FramingMember> Of(CeilingFramingPlan plan, string role) => plan.Members.Where(m => m.Role == role).ToList();

        private static readonly List<double[]> Rect = Loop(0, 0, 6000, 0, 6000, 4000, 0, 4000);

        [Fact]
        public void Rectangle_gets_centred_strips_perimeter_and_hangers()
        {
            var input = Ceiling(1200, Rect);
            input.CrossSpacing = 400;
            input.PerimeterTypeKey = "angle";
            input.HangerSpacing = 1200;
            input.HangerEndOffset = 150;
            CeilingFramingPlan plan = CeilingFramingRules.Plan(input, Budget);
            Assert.Null(plan.Refusal);
            Assert.Equal(0, plan.MainAngleRad, 9);

            List<FramingMember> mains = Of(plan, FramingRoles.Main);
            Assert.Equal(new[] { 200.0, 1400, 2600, 3800 }, mains.Select(m => Math.Round(m.Y0, 6)));
            Assert.All(mains, m => { Assert.Equal(0, m.X0, 6); Assert.Equal(6000, m.X1, 6); Assert.Equal(m.Y0, m.Y1, 6); });

            List<FramingMember> cross = Of(plan, FramingRoles.Cross);
            Assert.Equal(15, cross.Count);
            Assert.Equal(200, cross.Min(c => c.X0), 6);
            Assert.Equal(5800, cross.Max(c => c.X0), 6);
            Assert.All(cross, c => Assert.Equal(4000, c.Length, 6));

            Assert.Equal(4, Count(plan, FramingRoles.Perimeter));
            // 6000 per main, 150 end offset, 1200 spacing: 5700 / 1200 -> 5 intervals, 6 hangers.
            Assert.Equal(24, Count(plan, FramingRoles.Hanger));
            List<FramingMember> first = Of(plan, FramingRoles.Hanger).Where(h => h.Source == 0).ToList();
            Assert.Equal(new[] { 150.0, 1290, 2430, 3570, 4710, 5850 }, first.Select(h => Math.Round(h.X0, 6)));
            Assert.All(first, h => Assert.Equal(200, h.Y0, 6));
        }

        [Fact]
        public void L_shape_mains_are_clipped_to_each_leg()
        {
            var input = Ceiling(1000, Loop(0, 0, 6000, 0, 6000, 3000, 3000, 3000, 3000, 6000, 0, 6000));
            CeilingFramingPlan plan = CeilingFramingRules.Plan(input, Budget);
            Assert.Null(plan.Refusal);
            List<FramingMember> mains = Of(plan, FramingRoles.Main);
            Assert.Equal(6, mains.Count);
            Assert.Equal(3, mains.Count(m => Math.Abs(m.Length - 6000) < 1e-6 && m.Y0 < 3000));
            Assert.Equal(3, mains.Count(m => Math.Abs(m.Length - 3000) < 1e-6 && m.Y0 > 3000));
        }

        [Fact]
        public void A_hole_splits_the_mains_that_cross_it_and_gets_its_own_perimeter()
        {
            var hole = Loop(2000, 1000, 4000, 1000, 4000, 3000, 2000, 3000);
            var input = Ceiling(1000, Rect, hole);
            input.PerimeterTypeKey = "angle";
            input.HangerSpacing = 1200;
            input.HangerEndOffset = 150;
            CeilingFramingPlan plan = CeilingFramingRules.Plan(input, Budget);
            Assert.Null(plan.Refusal);
            List<FramingMember> mains = Of(plan, FramingRoles.Main);
            Assert.Equal(6, mains.Count);
            Assert.Equal(2, mains.Count(m => Math.Abs(m.Y0 - 1500) < 1e-6));
            Assert.DoesNotContain(mains, m => Math.Min(m.X0, m.X1) < 3000 && Math.Max(m.X0, m.X1) > 3000 && m.Y0 > 1000 && m.Y0 < 3000);
            Assert.Equal(8, Count(plan, FramingRoles.Perimeter));
            Assert.DoesNotContain(Of(plan, FramingRoles.Hanger), h => h.X0 > 2000 && h.X0 < 4000 && h.Y0 > 1000 && h.Y0 < 3000);
        }

        [Fact]
        public void Direction_short_runs_the_mains_across_the_long_side()
        {
            var longPlan = CeilingFramingRules.Plan(Ceiling(1200, Rect), Budget);
            Assert.Equal(4, Count(longPlan, FramingRoles.Main));
            Assert.All(Of(longPlan, FramingRoles.Main), m => Assert.Equal(6000, m.Length, 6));

            var input = Ceiling(1200, Rect);
            input.Direction = "short";
            var shortPlan = CeilingFramingRules.Plan(input, Budget);
            List<FramingMember> mains = Of(shortPlan, FramingRoles.Main);
            Assert.Equal(5, mains.Count);
            Assert.All(mains, m => { Assert.Equal(4000, m.Length, 6); Assert.Equal(m.X0, m.X1, 6); });
            Assert.Equal(new[] { 600.0, 1800, 3000, 4200, 5400 }, mains.Select(m => Math.Round(m.X0, 6)).OrderBy(x => x));
        }

        [Fact]
        public void The_long_axis_follows_a_rotated_boundary()
        {
            double a = Math.PI / 6, c = Math.Cos(a), s = Math.Sin(a);
            var pts = new[] { new[] { 0.0, 0 }, new[] { 6000.0, 0 }, new[] { 6000.0, 4000 }, new[] { 0.0, 4000 } };
            var rotated = pts.Select(p => new[] { p[0] * c - p[1] * s, p[0] * s + p[1] * c }).ToList();
            CeilingFramingPlan plan = CeilingFramingRules.Plan(Ceiling(1200, rotated), Budget);
            Assert.Equal(a, plan.MainAngleRad, 9);
            Assert.All(Of(plan, FramingRoles.Main), m => Assert.Equal(6000, m.Length, 6));
            Assert.Equal(4, Count(plan, FramingRoles.Main));

            var input = Ceiling(1200, Rect);
            input.DirectionAngleRad = Math.PI / 2;
            Assert.All(Of(CeilingFramingRules.Plan(input, Budget), FramingRoles.Main), m => Assert.Equal(m.X0, m.X1, 6));
        }

        [Fact]
        public void A_main_shorter_than_one_hanger_spacing_hangs_at_its_two_end_offsets_and_a_stub_at_its_middle()
        {
            var input = Ceiling(1200, Loop(0, 0, 1000, 0, 1000, 600, 0, 600));
            input.HangerSpacing = 1200;
            input.HangerEndOffset = 150;
            CeilingFramingPlan plan = CeilingFramingRules.Plan(input, Budget);
            FramingMember main = Assert.Single(Of(plan, FramingRoles.Main));
            Assert.Equal(300, main.Y0, 6);
            Assert.Equal(new[] { 150.0, 850 }, Of(plan, FramingRoles.Hanger).Select(h => Math.Round(h.X0, 6)));

            var stub = Ceiling(1200, Loop(0, 0, 200, 0, 200, 100, 0, 100));
            stub.HangerSpacing = 1200;
            stub.HangerEndOffset = 150;
            FramingMember hanger = Assert.Single(Of(CeilingFramingRules.Plan(stub, Budget), FramingRoles.Hanger));
            Assert.Equal(100, hanger.X0, 6);
            Assert.Equal(50, hanger.Y0, 6);
        }

        [Fact]
        public void A_line_through_a_vertex_is_counted_once()
        {
            var diamond = Loop(0, -1000, 1000, 0, 0, 1000, -1000, 0);
            var input = Ceiling(2000.0 / 3, diamond);
            input.DirectionAngleRad = 0;
            List<FramingMember> mains = Of(CeilingFramingRules.Plan(input, Budget), FramingRoles.Main);
            Assert.Equal(3, mains.Count);
            FramingMember middle = mains.Single(m => Math.Abs(m.Y0) < 1e-6);
            Assert.Equal(2000, middle.Length, 6);
        }

        [Fact]
        public void Bad_input_refuses_with_a_stable_code()
        {
            Assert.Equal("boundary_needs_three_points", CeilingFramingRules.Plan(Ceiling(1200, Loop(0, 0, 1, 1)), Budget).Refusal);
            Assert.Equal("main_spacing_must_be_positive", CeilingFramingRules.Plan(Ceiling(0, Rect), Budget).Refusal);
            var sideways = Ceiling(1200, Rect);
            sideways.Direction = "diagonal";
            Assert.Equal("unknown_direction", CeilingFramingRules.Plan(sideways, Budget).Refusal);
            // The bound counts members: 1 mm mains in the 6 x 4 m Rect are 4004 members, inside 5000
            // (the spec reader's 10 mm minimum is what refuses that unit slip); a 6 x 6 m square is 6004.
            Assert.Equal("over_budget", CeilingFramingRules.Plan(Ceiling(1, Loop(0, 0, 6000, 0, 6000, 6000, 0, 6000)), Budget).Refusal);
        }

        [Fact]
        public void Inside_and_distance_outside_honour_holes_and_edges()
        {
            var loops = new List<List<double[]>>
            {
                new List<double[]> { new[] { 0.0, 0 }, new[] { 1000.0, 0 }, new[] { 1000.0, 500 }, new[] { 0.0, 500 } },
                new List<double[]> { new[] { 400.0, 200 }, new[] { 600.0, 200 }, new[] { 600.0, 300 }, new[] { 400.0, 300 } }
            };
            Assert.True(CeilingFramingRules.Inside(loops, 100, 100));
            Assert.False(CeilingFramingRules.Inside(loops, 500, 250));
            Assert.Equal(0, CeilingFramingRules.DistanceOutside(loops, 100, 100));
            Assert.Equal(50, CeilingFramingRules.DistanceOutside(loops, 500, 250), 6);
            Assert.Equal(10, CeilingFramingRules.DistanceOutside(loops, 1010, 250), 6);
            Assert.Equal(0, CeilingFramingRules.DistanceOutside(loops, 1000, 250), 6);
            Assert.Equal(500000, CeilingFramingRules.Area(loops[0]), 6);
            Assert.Equal(-500000, CeilingFramingRules.Area(new List<double[]>(loops[0].AsEnumerable().Reverse())), 6);
        }

        [Fact]
        public void A_curved_ceiling_with_many_tessellated_edges_is_not_refused_over_budget()
        {
            // 15 x 10 m with its long side replaced by a shallow arc of 240 chords: the old
            // bound (lines x edges) refused it; the plan itself has a few hundred members.
            var loop = new List<double[]> { new[] { 0.0, 0 }, new[] { 15000.0, 0 } };
            for (int k = 1; k < 240; k++)
            {
                double x = 15000 - k * 15000.0 / 240;
                loop.Add(new[] { x, 10000 + 500 * Math.Sin(Math.PI * x / 15000) });
            }
            loop.Add(new[] { 0.0, 10000 });
            var input = new CeilingFramingInput
            {
                Loops = new List<List<double[]>> { loop }, Direction = "long", MainSpacing = 1200, MainTypeKey = "main",
                CrossSpacing = 400, CrossTypeKey = "cross", PerimeterTypeKey = "perim",
                HangerSpacing = 1200, HangerEndOffset = 600, HangerTypeKey = "hanger",
            };
            CeilingFramingPlan plan = CeilingFramingRules.Plan(input, 5000);
            Assert.Null(plan.Refusal);
            Assert.InRange(plan.Members.Count, 300, 1000);
            Assert.Equal(loop.Count, plan.CountByRole()[FramingRoles.Perimeter]);

            // A tiny budget still refuses, and before the grid is fully allocated.
            Assert.Equal("over_budget", CeilingFramingRules.Plan(input, 100).Refusal);
            Assert.True(CeilingFramingRules.GridSegments(input.Loops, 0, 10, 50).Count <= 52);
        }
    }
}
