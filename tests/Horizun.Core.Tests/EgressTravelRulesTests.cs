// -----------------------------------------------------------------------------
// Egress travel distance, Revit-free: the farthest sampled start, the exit a route
// ended at, the rule verdict per room, and the re-read agreement of a kept path.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class EgressTravelRulesTests
    {
        [Fact]
        public void Corners_are_pulled_inward_by_the_inset_toward_the_room_point()
        {
            var square = new List<PlanPoint> { new PlanPoint(0, 0), new PlanPoint(4000, 0), new PlanPoint(4000, 4000), new PlanPoint(0, 4000) };
            List<PlanPoint> inset = EgressTravelRules.InsetToward(square, new PlanPoint(2000, 2000), 300);
            Assert.Equal(4, inset.Count);
            Assert.Equal(300, inset[0].DistanceTo(square[0]), 6);
            Assert.True(inset[0].X > 0 && inset[0].Y > 0);
        }

        [Fact]
        public void A_corner_closer_than_the_inset_collapses_onto_the_reference_once()
        {
            var tiny = new List<PlanPoint> { new PlanPoint(0, 0), new PlanPoint(100, 0) };
            List<PlanPoint> inset = EgressTravelRules.InsetToward(tiny, new PlanPoint(50, 0), 300);
            Assert.Single(inset);
            Assert.Equal(50, inset[0].X, 9);
        }

        [Fact]
        public void The_farthest_candidate_ignores_candidates_that_did_not_route()
        {
            Assert.Equal(2, EgressTravelRules.FarthestIndex(new double?[] { 5.0, null, 9.5, 7.0 }));
            Assert.Equal(-1, EgressTravelRules.FarthestIndex(new double?[] { null, null }));
            Assert.Equal(-1, EgressTravelRules.FarthestIndex(new double?[0]));
        }

        [Fact]
        public void An_end_point_matches_the_nearest_exit_within_tolerance_or_none()
        {
            var exits = new List<PlanPoint> { new PlanPoint(0, 0), new PlanPoint(10000, 0) };
            Assert.Equal(1, EgressTravelRules.NearestIndex(new PlanPoint(9990, 20), exits, 100));
            // FindEndsOfShortestPaths returns the START when no path exists: no exit reached.
            Assert.Equal(-1, EgressTravelRules.NearestIndex(new PlanPoint(5000, 5000), exits, 100));
        }

        [Fact]
        public void Polyline_length_sums_its_segments()
        {
            var path = new List<PlanPoint> { new PlanPoint(0, 0), new PlanPoint(3000, 0), new PlanPoint(3000, 4000) };
            Assert.Equal(7000, EgressTravelRules.Length(path), 9);
            Assert.Equal(0, EgressTravelRules.Length(new List<PlanPoint> { new PlanPoint(1, 1) }));
        }

        [Theory]
        // lower, upper, max, not_assessable -> outcome
        [InlineData(24.0, 24.9, 25.0, null, "passes")]
        [InlineData(24.0, 25.0, 25.0, null, "passes")]
        [InlineData(25.1, 26.0, 25.0, null, "fails")]
        [InlineData(25.1, null, 25.0, null, "fails")]
        [InlineData(12.0, null, null, null, "measured")]
        [InlineData(null, null, 25.0, null, "not_decidable")]
        [InlineData(10.0, null, 25.0, "room is on another level", "not_assessable")]
        [InlineData(null, null, 25.0, "no exit on this level", "not_assessable")]
        // THE NEAR-LIMIT CASE the lower bound exists for: the routed sample is under the
        // limit but nothing bounds the true farthest point from above - not a pass.
        [InlineData(44.95, null, 45.0, null, "not_decidable")]
        [InlineData(44.95, 45.25, 45.0, null, "not_decidable")]
        public void A_room_is_judged_against_the_callers_limit(double? lower, double? upper, double? max, string notAssessable, string expected)
        {
            Assert.Equal(expected, EgressTravelRules.Evaluate(lower, upper, max, notAssessable));
        }

        [Fact]
        public void Convexity_is_read_from_the_turns_and_an_L_is_not_convex()
        {
            var rect = new[] { new PlanPoint(0, 0), new PlanPoint(10, 0), new PlanPoint(10, 5), new PlanPoint(5, 5), new PlanPoint(0, 5) };
            Assert.True(EgressTravelRules.IsConvex(rect));   // a collinear vertex does not break it
            var l = new[] { new PlanPoint(0, 0), new PlanPoint(10, 0), new PlanPoint(10, 2), new PlanPoint(2, 2), new PlanPoint(2, 10), new PlanPoint(0, 10) };
            Assert.False(EgressTravelRules.IsConvex(l));
            Assert.False(EgressTravelRules.IsConvex(new[] { new PlanPoint(0, 0), new PlanPoint(1, 1) }));
        }

        [Fact]
        public void The_upper_bound_is_the_best_routed_sample_plus_its_reach_to_the_farthest_vertex()
        {
            var rect = new[] { new PlanPoint(0, 0), new PlanPoint(4, 0), new PlanPoint(4, 3), new PlanPoint(0, 3) };
            // Centre (2,1.5) routed at 10, reach 2.5 -> 12.5; corner (0.3,0.3) routed at 11, reach |(4,3)-(0.3,0.3)| = 4.58.. -> 15.58.
            double? u = EgressTravelRules.UpperBound(new List<(PlanPoint, double)> { (new PlanPoint(2, 1.5), 10), (new PlanPoint(0.3, 0.3), 11) }, rect);
            Assert.Equal(12.5, u.Value, 9);
            Assert.Null(EgressTravelRules.UpperBound(new List<(PlanPoint, double)>(), rect));
        }

        [Fact]
        public void A_kept_path_agrees_with_the_measurement_within_fifty_mm_or_one_percent()
        {
            Assert.True(EgressTravelRules.LengthsAgree(10000, 10040, out double d1));
            Assert.Equal(40, d1, 9);
            Assert.True(EgressTravelRules.LengthsAgree(40000, 40390, out _));
            Assert.False(EgressTravelRules.LengthsAgree(10000, 10200, out _));
            Assert.False(EgressTravelRules.LengthsAgree(10000, 0, out _));
        }

        [Fact]
        public void The_summary_always_names_every_outcome()
        {
            var s = EgressTravelRules.Summary(new[] { "passes", "fails", "passes", "not_assessable" });
            Assert.Equal(2, (int)s["passes"]);
            Assert.Equal(1, (int)s["fails"]);
            Assert.Equal(0, (int)s["not_decidable"]);
            Assert.Equal(1, (int)s["not_assessable"]);
            Assert.Equal(0, (int)s["measured"]);
        }
    }
}
