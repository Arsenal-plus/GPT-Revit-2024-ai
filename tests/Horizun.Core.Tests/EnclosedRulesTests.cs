using System;
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
    }
}
