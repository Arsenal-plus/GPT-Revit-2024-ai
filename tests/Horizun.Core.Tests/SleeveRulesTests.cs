// Horizun Revit MCP - original Horizun code. Pure geometry for
// horizun_resolve_clash's propose_opening/apply_opening (sleeves and structural
// openings) - crossing, size-with-clearance and host-kind/route decisions.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SleeveRulesTests
    {
        [Fact]
        public void LineBoxIntersect_finds_entry_and_exit_for_a_run_straight_through_a_wall()
        {
            // A wall box 200mm thick along X, a run crossing it perpendicular to X at its centre.
            var wall = new ResolveBox(0, -1000, 0, 200, 1000, 500);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 100, 250 }, new[] { 500.0, 100, 250 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.True(ok);
            Assert.Null(code);
            Assert.Equal(0, entry[0], 3);
            Assert.Equal(200, exit[0], 3);
            double[] mid = SleeveRules.Midpoint(entry, exit);
            Assert.Equal(100, mid[0], 3);
        }

        [Fact]
        public void LineBoxIntersect_reports_no_crossing_when_the_run_misses_the_host_sideways()
        {
            // Constant in Y (parallel to the box's X/Z faces) and entirely outside the box's Y
            // range: caught by the axis-aligned "outside and parallel" branch.
            var wall = new ResolveBox(0, -1000, 0, 200, 1000, 500);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 5000, 250 }, new[] { 500.0, 5000, 250 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeParallel, code);
            Assert.Null(entry); Assert.Null(exit);
        }

        [Fact]
        public void LineBoxIntersect_reports_no_crossing_when_the_run_passes_diagonally_beside_the_host()
        {
            // A run that moves in every axis but still slips past the box's Z range - only the
            // X/Y interval check can catch this one, so tmin/tmax must actually cross to false.
            var wall = new ResolveBox(-100, -100, 0, 100, 100, 50);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, -500, 1000.0 }, new[] { 500.0, 500, 1100.0 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeNoCrossing, code);
            Assert.Null(entry); Assert.Null(exit);
        }

        [Fact]
        public void LineBoxIntersect_reports_no_crossing_when_the_run_stops_short_of_the_host()
        {
            // The segment ends BEFORE reaching the box along X: clipped to [0,1] of the segment
            // itself, so it must not extrapolate past the run's own two endpoints.
            var wall = new ResolveBox(1000, -1000, 0, 1200, 1000, 500);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 0, 250 }, new[] { 500.0, 0, 250 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeNoCrossing, code);
        }

        [Fact]
        public void LineBoxIntersect_reports_parallel_when_the_run_lies_outside_a_flat_host()
        {
            // The run runs along X at Z=1000, entirely above a thin floor box at Z in [0,300].
            var floor = new ResolveBox(-1000, -1000, 0, 1000, 1000, 300);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 0, 1000 }, new[] { 500.0, 0, 1000 }, floor,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeParallel, code);
        }

        [Fact]
        public void OpeningSize_adds_clearance_and_keeps_round_square()
        {
            SleeveRules.OpeningSize(150, 150, 50, "round", out double w, out double h, out string shape);
            Assert.Equal(SleeveRules.ShapeRound, shape);
            Assert.Equal(200, w, 3);
            Assert.Equal(200, h, 3);
        }

        [Fact]
        public void OpeningSize_keeps_width_and_height_independent_for_a_rectangular_duct()
        {
            SleeveRules.OpeningSize(400, 200, 50, "rectangular", out double w, out double h, out string shape);
            Assert.Equal(SleeveRules.ShapeRect, shape);
            Assert.Equal(450, w, 3);
            Assert.Equal(250, h, 3);
        }

        [Theory]
        [InlineData("OST_Walls", SleeveRules.HostWall, SleeveRules.RouteWallOpening)]
        [InlineData("OST_Floors", SleeveRules.HostFloor, SleeveRules.RouteFloorOpening)]
        [InlineData("OST_Roofs", SleeveRules.HostRoof, SleeveRules.RouteFloorOpening)]
        [InlineData("OST_Ceilings", SleeveRules.HostCeiling, SleeveRules.RouteFloorOpening)]
        [InlineData("OST_StructuralFraming", SleeveRules.HostFramingOrColumn, SleeveRules.RouteSleeveOnly)]
        [InlineData("OST_StructuralColumns", SleeveRules.HostFramingOrColumn, SleeveRules.RouteSleeveOnly)]
        [InlineData("OST_Furniture", SleeveRules.HostUnsupported, SleeveRules.RouteRefused)]
        public void HostKindOf_and_RouteFor_map_every_named_host(string bic, string expectedKind, string expectedRoute)
        {
            string kind = SleeveRules.HostKindOf(bic);
            Assert.Equal(expectedKind, kind);
            Assert.Equal(expectedRoute, SleeveRules.RouteFor(kind));
        }

        [Fact]
        public void ContainsCrossingWithClearance_is_true_only_when_the_margin_is_actually_there()
        {
            var opening = new ResolveBox(-100, -100, -100, 100, 100, 100);
            Assert.True(SleeveRules.ContainsCrossingWithClearance(opening, new double[] { 0, 0, 0 }, 20, 20, 50));
            // The same opening cannot clear a run whose half-width alone eats the whole margin.
            Assert.False(SleeveRules.ContainsCrossingWithClearance(opening, new double[] { 0, 0, 0 }, 60, 20, 50));
        }
    }
}
