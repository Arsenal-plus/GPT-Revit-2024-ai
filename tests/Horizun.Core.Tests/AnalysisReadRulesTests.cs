// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Analysis reads, proved by running the rules. The load-bearing properties are
// negative: a system nobody calculated is never judged "ok" and never lets the
// aggregate read as the whole truth, a limit whose value was not claimed is
// named rather than passed, a network Revit does not call well connected never
// passes, and a beam framing into the middle of a girder, a column top inside a
// slab and a column base on its support are NOT gaps.
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
        public void Flow_only_claims_flow_alone_so_velocity_and_pressure_limits_are_unmeasured()
        {
            // SystemCalculationLevel.Flow is "System calculation is only for flow" (RevitAPI.xml);
            // a velocity Revit may not have computed is not judged until a live run proves it is.
            AnalysisReadRules.ParseLimits(
                JObject.Parse("{\"max_velocity_m_s\":5,\"max_pressure_loss_pa\":100}"), out var limits);
            var unmeasured = new List<string>();
            var s = new AnalysisReadRules.SectionReading { Number = 1, VelocityMs = 7.25, PressureLossPa = 20 };
            JArray breaches = AnalysisReadRules.Breaches(s, AnalysisReadRules.FlowOnly, limits, unmeasured);
            Assert.Empty(breaches);
            Assert.Equal(new[] { "1#max_velocity_m_s", "1#max_pressure_loss_pa" }, unmeasured);
        }

        [Fact]
        public void A_not_calculated_system_beside_a_calculated_one_is_never_the_whole_truth()
        {
            string judged = AnalysisReadRules.SystemCoverage(AnalysisReadRules.Calculated, true, 0, 0, 0, true);
            string unjudged = AnalysisReadRules.SystemCoverage(AnalysisReadRules.NotCalculated, false, 0, 0, 0, null);
            Assert.Equal(StructuralCoverage.Complete, judged);
            Assert.Equal(StructuralCoverage.Unreadable, unjudged);
            string mixed = StructuralCoverage.Weakest(new[] { judged, unjudged, unjudged });
            Assert.Equal(StructuralCoverage.Partial, mixed);
            Assert.False((bool)StructuralCoverage.Declare(mixed, 1, 2)["is_whole_truth"]);
            // Nothing calculated is "the model would not answer", not "the question does not arise".
            Assert.Equal(StructuralCoverage.Unreadable, StructuralCoverage.Weakest(new[] { unjudged, unjudged }));
        }

        [Theory]
        [InlineData(false, "not_well_connected")]
        [InlineData(null, "connectivity_unreadable")]
        public void A_network_not_provably_well_connected_is_neither_passed_nor_breached(bool? wellConnected, string word)
        {
            // RevitAPI.xml 2023 and 2026, IsWellConnected: "If the system is not well connected,
            // parameters which need to be calculated are invalid." Invalid, not understated: a
            // breach judged on them is as false as a pass.
            Assert.False(AnalysisReadRules.ValuesValid(wellConnected));
            Assert.Equal(word, AnalysisReadRules.SystemVerdict(false, 1, 0, wellConnected, 3, 3, out _));
            Assert.Equal(word, AnalysisReadRules.SystemVerdict(true, 1, 0, wellConnected, 3, 3, out _));
            Assert.Equal(StructuralCoverage.Unreadable,
                AnalysisReadRules.SystemCoverage(AnalysisReadRules.Calculated, true, 0, 0, 0, wellConnected, out string why));
            Assert.StartsWith(word, why);
            Assert.Equal("within_limits", AnalysisReadRules.SystemVerdict(false, 1, 0, true, 3, 3, out _));
            Assert.Equal("beyond_limits", AnalysisReadRules.SystemVerdict(true, 1, 0, true, 3, 3, out _));
        }

        [Fact]
        public void A_critical_path_whose_sections_all_failed_to_read_never_says_numbers_read()
        {
            Assert.Equal("critical_path_unreadable",
                AnalysisReadRules.SystemVerdict(false, 0, 0, true, 0, 4, out string means));
            Assert.DoesNotContain("numbers read", means);
            Assert.Equal("no_limits_given", AnalysisReadRules.SystemVerdict(false, 0, 0, true, 3, 4, out means));
            Assert.Contains("3 of 4", means);
            Assert.Equal(StructuralCoverage.Unreadable,
                AnalysisReadRules.SystemCoverage(AnalysisReadRules.Calculated, false, 4, 0, 0, true, out string why));
            Assert.StartsWith("critical_path_unread", why);
        }

        [Fact]
        public void A_partial_system_names_its_own_cause_and_a_complete_one_none()
        {
            Assert.Equal(StructuralCoverage.Partial,
                AnalysisReadRules.SystemCoverage(AnalysisReadRules.FlowOnly, true, 0, 0, 0, true, out string why));
            Assert.StartsWith("flow_only", why);
            Assert.Equal(StructuralCoverage.Partial,
                AnalysisReadRules.SystemCoverage(AnalysisReadRules.Calculated, true, 1, 2, 0, true, out why));
            Assert.Contains("1 critical-path section(s) would not read", why);
            Assert.Contains("2 section limit(s) unmeasured", why);
            Assert.Equal(StructuralCoverage.Complete,
                AnalysisReadRules.SystemCoverage(AnalysisReadRules.Calculated, true, 0, 0, 0, true, out why));
            Assert.Null(why);
            AnalysisReadRules.SystemCoverage(AnalysisReadRules.NotCalculated, false, 0, 0, 0, null, out why);
            Assert.StartsWith("not_calculated", why);
        }

        [Fact]
        public void A_claimed_quantity_that_came_back_null_is_named_even_without_limits()
        {
            var s = new AnalysisReadRules.SectionReading { Number = 2, FlowLs = 1, VelocityMs = null, PressureLossPa = 3 };
            List<string> unread = AnalysisReadRules.UnreadQuantities(s, AnalysisReadRules.Calculated);
            Assert.Equal(new[] { "2#velocity", "2#friction" }, unread);
            Assert.Empty(AnalysisReadRules.UnreadQuantities(new AnalysisReadRules.SectionReading { Number = 2, FlowLs = 1 },
                AnalysisReadRules.FlowOnly));
            Assert.Equal(StructuralCoverage.Partial,
                AnalysisReadRules.SystemCoverage(AnalysisReadRules.Calculated, true, 0, 0, unread.Count, true));
        }

        [Theory]
        [InlineData("monday", "Monday")]
        [InlineData(" Friday ", "Friday")]
        [InlineData("Monday, Tuesday", null)]
        [InlineData("Monday,Tuesday", null)]
        [InlineData("+3", null)]
        [InlineData("-5", null)]
        [InlineData("1", null)]
        public void Only_one_exact_defined_name_is_accepted(string input, string expected)
        {
            // Enum.TryParse would turn "Monday, Tuesday" into Wednesday (1|2 = 3).
            Assert.Equal(expected, AnalysisReadRules.ExactName(input, System.Enum.GetNames(typeof(System.DayOfWeek))));
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

        private static AnalysisReadRules.AnalyticalSurface Square(long id, double size, double z, params double[][] hole)
        {
            var s = new AnalysisReadRules.AnalyticalSurface { ElementId = id };
            s.Outer.AddRange(new[] { new double[] { 0, 0, z }, new[] { size, 0, z }, new[] { size, size, z }, new[] { 0, size, z } });
            if (hole.Length > 0) s.Holes.Add(new List<double[]>(hole));
            return s;
        }

        private static AnalysisReadRules.AnalyticalPolyline Edges(AnalysisReadRules.AnalyticalSurface s)
        {
            var l = Line(s.ElementId, s.Outer.ToArray());
            l.Points.Add(s.Outer[0]);
            return l;
        }

        [Fact]
        public void A_column_top_inside_a_slab_panel_is_connected_and_its_supported_base_is_no_gap()
        {
            var slab = Square(10, 8000, 3000);
            var column = Line(2, new double[] { 4000, 4000, 0 }, new double[] { 4000, 4000, 3000 });
            var targets = new[] { Edges(slab), column };
            var free = AnalysisReadRules.ClassifyEnds(new[] { column }, targets, new[] { slab }, null, 1.0);
            Assert.Equal(0, free.Unconnected.Single().End); // only the base: the top is on the slab surface
            var support = new AnalysisReadRules.SupportTarget { ElementId = 77 };
            support.Points.Add(new double[] { 4000, 4000, 0 });
            var held = AnalysisReadRules.ClassifyEnds(new[] { column }, targets, new[] { slab }, new[] { support }, 1.0);
            Assert.Empty(held.Unconnected);
            Assert.Equal(77L, held.Supported.Single().NearestElementId);
        }

        [Fact]
        public void An_end_inside_a_panel_opening_or_off_its_plane_is_not_on_the_panel()
        {
            var slab = Square(10, 8000, 3000, new double[] { 3000, 3000, 3000 }, new double[] { 5000, 3000, 3000 },
                                                new double[] { 5000, 5000, 3000 }, new double[] { 3000, 5000, 3000 });
            Assert.False(AnalysisReadRules.OnSurface(new double[] { 4000, 4000, 3000 }, slab, 1.0));
            Assert.True(AnalysisReadRules.OnSurface(new double[] { 3000, 4000, 3000 }, slab, 1.0)); // on the opening's edge
            Assert.True(AnalysisReadRules.OnSurface(new double[] { 1000, 1000, 3000.5 }, slab, 1.0));
            Assert.False(AnalysisReadRules.OnSurface(new double[] { 1000, 1000, 3010 }, slab, 1.0));
            Assert.False(AnalysisReadRules.OnSurface(new double[] { 9000, 1000, 3000 }, slab, 1.0));
        }

        [Fact]
        public void A_curved_target_is_decided_by_its_real_curve_not_its_display_chords()
        {
            // The chord sits 60 mm from the end; the real curve passes 0.5 mm from it.
            var arc = Line(1, new double[] { 0, 0, 0 }, new double[] { 10000, 0, 0 });
            arc.ExactMm = p => 0.5;
            arc.SlackMm = 100;
            var beam = Line(2, new double[] { 5000, 60, 0 }, new double[] { 5000, 6000, 0 });
            var support = new AnalysisReadRules.SupportTarget { ElementId = 3 };
            support.Points.Add(new double[] { 5000, 6000, 0 });
            Assert.Empty(AnalysisReadRules.ClassifyEnds(new[] { beam }, new[] { arc, beam }, null, new[] { support }, 1.0).Unconnected);
            // With a slack the chord already rules out, the real curve is not consulted.
            arc.SlackMm = 10;
            var gap = AnalysisReadRules.ClassifyEnds(new[] { beam }, new[] { arc, beam }, null, new[] { support }, 1.0).Unconnected.Single();
            Assert.Equal(60.0, gap.NearestMm);
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
