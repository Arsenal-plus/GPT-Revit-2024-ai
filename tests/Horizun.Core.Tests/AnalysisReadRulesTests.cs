// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Analysis reads, proved by running the rules. The load-bearing properties are
// negative: a system nobody calculated is never judged "ok", a limit whose value
// was not claimed is named rather than passed, and a beam framing into the
// middle of a girder is NOT a gap.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AnalysisReadRulesTests
    {
        [Theory]
        [InlineData("All", AnalysisReadRules.Calculated)]
        [InlineData("Flow", AnalysisReadRules.FlowOnly)]
        [InlineData("None", AnalysisReadRules.NotCalculated)]
        [InlineData("Performance", AnalysisReadRules.NotCalculated)]
        [InlineData("Volume", AnalysisReadRules.NotCalculated)]
        [InlineData(null, AnalysisReadRules.Unreadable)]
        [InlineData("Whatever2030", AnalysisReadRules.Unreadable)]
        public void Calculation_level_decides_what_may_be_claimed(string level, string expected)
        {
            Assert.Equal(expected, AnalysisReadRules.CalculationStatus(level));
        }

        [Fact]
        public void A_system_not_calculated_is_never_judged_ok_its_limits_are_unmeasured()
        {
            AnalysisReadRules.ParseLimits(JObject.Parse("{\"max_velocity_m_s\":5}"), out var limits);
            var unmeasured = new List<string>();
            var s = new AnalysisReadRules.SectionReading { Number = 3, VelocityMs = 0 };
            JArray breaches = AnalysisReadRules.Breaches(s, AnalysisReadRules.NotCalculated, limits, unmeasured);
            Assert.Empty(breaches);
            Assert.Equal(new[] { "3#max_velocity_m_s" }, unmeasured);
        }

        [Fact]
        public void Flow_only_judges_velocity_and_names_pressure_as_unmeasured()
        {
            AnalysisReadRules.ParseLimits(
                JObject.Parse("{\"max_velocity_m_s\":5,\"max_pressure_loss_pa\":100}"), out var limits);
            var unmeasured = new List<string>();
            var s = new AnalysisReadRules.SectionReading { Number = 1, VelocityMs = 7.25, PressureLossPa = 20 };
            JArray breaches = AnalysisReadRules.Breaches(s, AnalysisReadRules.FlowOnly, limits, unmeasured);
            Assert.Single(breaches);
            Assert.Equal("max_velocity_m_s", (string)breaches[0]["limit"]);
            Assert.Equal(new[] { "1#max_pressure_loss_pa" }, unmeasured);
        }

        [Fact]
        public void Calculated_section_within_limits_has_no_breach_and_nothing_unmeasured()
        {
            AnalysisReadRules.ParseLimits(
                JObject.Parse("{\"max_velocity_m_s\":5,\"max_friction_pa_per_m\":2}"), out var limits);
            var unmeasured = new List<string>();
            var s = new AnalysisReadRules.SectionReading { Number = 2, VelocityMs = 4, FrictionPaPerM = 1.5 };
            Assert.Empty(AnalysisReadRules.Breaches(s, AnalysisReadRules.Calculated, limits, unmeasured));
            Assert.Empty(unmeasured);
        }

        [Fact]
        public void Unreadable_value_under_a_calculated_system_is_named_not_passed()
        {
            AnalysisReadRules.ParseLimits(JObject.Parse("{\"max_pressure_loss_pa\":10}"), out var limits);
            var unmeasured = new List<string>();
            var s = new AnalysisReadRules.SectionReading { Number = 4, PressureLossPa = null };
            Assert.Empty(AnalysisReadRules.Breaches(s, AnalysisReadRules.Calculated, limits, unmeasured));
            Assert.Equal(new[] { "4#max_pressure_loss_pa" }, unmeasured);
        }

        [Theory]
        [InlineData("{\"max_speed\":5}")]
        [InlineData("{\"max_velocity_m_s\":0}")]
        [InlineData("{\"max_velocity_m_s\":\"5\"}")]
        [InlineData("[1]")]
        public void Limits_that_would_be_silently_ignored_are_refused(string json)
        {
            Assert.NotNull(AnalysisReadRules.ParseLimits(JToken.Parse(json), out _));
        }

        private static AnalysisReadRules.AnalyticalPolyline Line(long id, params double[][] pts)
        {
            var l = new AnalysisReadRules.AnalyticalPolyline { ElementId = id };
            l.Points.AddRange(pts);
            return l;
        }

        [Fact]
        public void A_beam_framing_into_mid_girder_is_connected_not_a_gap()
        {
            var girder = Line(1, new double[] { 0, 0, 0 }, new double[] { 10000, 0, 0 });
            var beam = Line(2, new double[] { 5000, 0, 0 }, new double[] { 5000, 6000, 0 });
            var support = Line(3, new double[] { 5000, 6000, 0 }, new double[] { 5000, 6000, -3000 });
            var all = new[] { girder, beam, support };
            var gaps = AnalysisReadRules.NodeGaps(new[] { beam }, all, 1.0);
            Assert.Empty(gaps);
        }

        [Fact]
        public void An_end_short_of_every_other_curve_is_a_gap_with_its_nearest_distance()
        {
            var girder = Line(1, new double[] { 0, 0, 0 }, new double[] { 10000, 0, 0 });
            var beam = Line(2, new double[] { 5000, 25, 0 }, new double[] { 5000, 6000, 0 });
            var gaps = AnalysisReadRules.NodeGaps(new[] { beam }, new[] { girder, beam }, 10.0);
            var start = gaps.Single(g => g.End == 0);
            Assert.Equal(25.0, start.NearestMm);
            Assert.Equal(1L, start.NearestElementId);
            // The far end reaches nothing but is still measured to the girder.
            Assert.Contains(gaps, g => g.End == 1 && g.NearestMm > 5000);
        }

        [Fact]
        public void A_lone_member_reports_its_ends_with_no_nearest_rather_than_zero()
        {
            var beam = Line(2, new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 });
            var gaps = AnalysisReadRules.NodeGaps(new[] { beam }, new[] { beam }, 10.0);
            Assert.Equal(2, gaps.Count);
            Assert.All(gaps, g => Assert.Null(g.NearestMm));
        }

        [Fact]
        public void Pair_check_bound_counts_ends_times_segments()
        {
            var a = Line(1, new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 2, 0, 0 });
            var b = Line(2, new double[] { 0, 1, 0 }, new double[] { 1, 1, 0 });
            Assert.Equal(2L * 3L, AnalysisReadRules.PairChecks(new[] { a }, new[] { a, b }));
        }
    }
}
