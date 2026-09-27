using System.Linq;
using Horizun.Revit.Core;
using Xunit;
using static Horizun.Revit.Core.HeadroomRules;

namespace Horizun.Core.Tests
{
    public class HeadroomRulesTests
    {
        private static Hit Own(double p) => new Hit(p, HitKind.Own, "own");
        private static Hit Target(double p, string key) => new Hit(p, HitKind.Target, key);

        [Fact]
        public void Clear_height_runs_from_the_last_own_hit_to_the_first_target_beyond_it()
        {
            Sample s = Read(new[] { Target(3.8, "floor"), Own(0.5), Own(0.1), Target(3.5, "floor") }, 0.003);
            Assert.Equal(SampleState.Measured, s.State);
            Assert.Equal(3.0, s.Clear, 9);
            Assert.Equal(0.5, s.FarFace, 9);
            Assert.Equal("floor", s.TargetKey);
        }

        [Fact]
        public void A_ray_that_never_crosses_the_element_is_off_it_and_never_judged()
        {
            Assert.Equal(SampleState.OffElement, Read(new[] { Target(2.0, "floor") }, 0.003).State);
            Assert.Equal(SampleState.OffElement, Read(Enumerable.Empty<Hit>(), 0.003).State);
        }

        [Fact]
        public void Crossing_the_element_and_finding_nothing_beyond_is_not_measured()
        {
            Sample s = Read(new[] { Own(0.1), Own(0.4), new Hit(2.0, HitKind.Other, "host:9") }, 0.003);
            Assert.Equal(SampleState.NothingBeyond, s.State);
        }

        [Fact]
        public void A_surface_touching_the_element_is_a_clear_height_of_zero_not_a_skip_to_its_far_side()
        {
            Sample s = Read(new[] { Own(0.1), Own(0.4), Target(0.4005, "floor"), Target(0.9, "floor") }, 0.003);
            Assert.Equal(SampleState.Measured, s.State);
            Assert.Equal(0.0, s.Clear, 2);
        }

        [Fact]
        public void An_element_inside_a_target_is_not_measured_against_that_targets_far_side()
        {
            Sample s = Read(new[] { Target(0.05, "slab"), Own(0.3), Own(0.6), Target(0.9, "slab"), Target(5.0, "floor") }, 0.003);
            Assert.Equal(SampleState.InsideTarget, s.State);
            Assert.Equal("slab", s.TargetKey);
        }

        [Fact]
        public void A_slab_the_ray_starts_in_above_the_element_does_not_hide_the_floor_below()
        {
            // A duct hung tight under a slab: the ray starts inside the slab and leaves it first.
            Sample s = Read(new[] { Target(0.1, "slab"), Own(0.1), Own(1.1), new Hit(2.0, HitKind.Other, "host:5"), Target(9.1, "floor") }, 0.003);
            Assert.Equal(SampleState.Measured, s.State);
            Assert.Equal(8.0, s.Clear, 9);
            Assert.Equal("floor", s.TargetKey);
        }

        [Theory]
        [InlineData(5, 5, 2500.0, 2100.0, "passes")]
        [InlineData(5, 4, 2500.0, 2100.0, "not_decidable")]
        [InlineData(5, 3, 1900.0, 2100.0, "fails")]
        [InlineData(5, 5, 2100.0, 2100.0, "passes")]
        [InlineData(5, 0, null, 2100.0, "not_measured")]
        [InlineData(0, 0, null, 2100.0, "not_measured")]
        public void Outcome_never_passes_what_was_not_measured(int considered, int measured, double? minClear, double threshold, string expected)
        {
            Assert.Equal(expected, Outcome(considered, measured, minClear, threshold));
        }

        [Fact]
        public void Samples_along_a_curve_sit_at_the_middle_of_equal_pieces()
        {
            var t = AlongCurve(10, 3, 400, out double used);
            Assert.Equal(new[] { 0.125, 0.375, 0.625, 0.875 }, t.ToArray());
            Assert.Equal(2.5, used, 9);
            Assert.Equal(400, AlongCurve(1000, 1, 400, out double capped).Count);
            Assert.Equal(2.5, capped, 9);
            Assert.Single(AlongCurve(0, 1, 400, out _));
        }

        [Fact]
        public void A_grid_grows_its_spacing_until_it_fits_the_cap_and_says_so()
        {
            var full = Grid(0, 0, 4, 2, 1, 400, out double used);
            Assert.Equal(8, full.Count);
            Assert.Equal(1.0, used, 9);
            Assert.Equal(0.5, full[0].Key, 9);
            Assert.Equal(0.5, full[0].Value, 9);
            var capped = Grid(0, 0, 4, 2, 1, 4, out double grown);
            Assert.True(capped.Count <= 4);
            Assert.True(grown > 1.0);
        }

        [Fact]
        public void Worst_outcomes_rank_first()
        {
            var order = new[] { "passes", "not_decidable", "fails", "not_measured" }.OrderBy(Rank).ToArray();
            Assert.Equal(new[] { "fails", "not_measured", "not_decidable", "passes" }, order);
        }
    }
}
