using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class EnclosedRulesTests
    {
        [Fact] public void An_empty_circuit_above_min_area_is_created() => Assert.Equal("create", EnclosedRules.CircuitAction("room", false, 12.5, 10));
        [Fact] public void A_circuit_of_exactly_min_area_is_kept() => Assert.Equal("create", EnclosedRules.CircuitAction("space", false, 10, 10));
        [Fact] public void A_smaller_circuit_is_skipped() => Assert.Equal("skipped_min_area", EnclosedRules.CircuitAction("room", false, 2.4, 10));

        [Fact]
        public void An_occupied_circuit_is_skipped_as_occupied_whatever_its_area()
        {
            Assert.Equal("skipped_has_room", EnclosedRules.CircuitAction("room", true, 2.4, 10));
            Assert.Equal("skipped_has_space", EnclosedRules.CircuitAction("space", true, 50, 0));
        }

        [Fact] public void Another_kind_is_a_programming_error() => Assert.Throws<ArgumentOutOfRangeException>(() => EnclosedRules.CircuitAction("area", false, 1, 0));

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(-0.5)]
        [InlineData(double.PositiveInfinity)]
        public void A_min_area_that_is_not_finite_and_non_negative_is_refused(double v) => Assert.NotNull(EnclosedRules.MinAreaProblem(v));

        [Fact] public void Zero_min_area_keeps_every_empty_circuit() => Assert.Null(EnclosedRules.MinAreaProblem(0));

        [Fact]
        public void A_generated_row_is_xy_only_so_the_room_planner_accepts_it()
        {
            // NormalizePlan refuses a room point that is not exactly X,Y ("room.point requires XY only").
            JObject row = EnclosedRules.GeneratedRow("room", 7, 12, 1153.5, 2000.25, 0, 22.0444);
            Assert.Equal(new[] { 1153.5, 2000.25 }, row["point"].Select(t => (double)t));
            Assert.Equal(0, (int)row["enclosed_from"]);
            Assert.Equal(22.044, (double)row["circuit_area_m2"]);
            Assert.Equal(12L, (long)row["phase_id"]);
        }

        [Fact]
        public void All_enclosed_goes_in_a_batch_of_its_own()
        {
            Assert.Null(EnclosedRules.MixProblem(2, 0));
            Assert.Null(EnclosedRules.MixProblem(0, 5));
            Assert.Contains("batch of their own", EnclosedRules.MixProblem(1, 1));
        }

        [Fact]
        public void Nothing_to_fill_lists_in_a_rehearsal_refuses_an_apply_and_names_a_level_with_no_region()
        {
            JArray seen = JArray.Parse("[{ 'index': 0, 'level': 'L1', 'phase': 'New', 'circuits_seen': 2 }]".Replace('\'', '"'));
            JArray none = JArray.Parse(("[{ 'index': 0, 'level': 'L1', 'phase': 'New', 'circuits_seen': 2 }, " +
                                        "{ 'index': 1, 'level': 'L2', 'phase': 'New', 'circuits_seen': 0 }]").Replace('\'', '"'));
            Assert.Null(EnclosedRules.NothingToFillProblem(true, seen));
            Assert.StartsWith("stale_plan", EnclosedRules.NothingToFillProblem(false, seen));
            string refusal = EnclosedRules.NothingToFillProblem(true, none);
            Assert.StartsWith("no_enclosed_circuit", refusal);
            Assert.Contains("elements[1]", refusal);
            Assert.DoesNotContain("elements[0]", refusal);
        }
    }
}
