// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_mep_routing hangers, the Revit-free half: where a support station
// falls along a run, given end_offset, spacing and interior fitting positions.
// Units here are an arbitrary consistent length (the command itself calls this
// in feet); the rule does not care which.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HangerRulesTests
    {
        private const int Budget = 1000;

        private static List<double> Stations(double runLength, double endOffset, double spacing, List<double> fittings = null)
            => HangerRules.Plan(runLength, endOffset, spacing, fittings, Budget).Stations;

        private static void AssertNoGapAbove(IReadOnlyList<double> stations, double spacing)
        {
            for (int i = 1; i < stations.Count; i++)
                Assert.True(stations[i] - stations[i - 1] <= spacing + 1e-9, "gap " + (stations[i] - stations[i - 1]) + " at " + stations[i - 1]);
        }

        private static void AssertClearOf(IReadOnlyList<double> stations, IEnumerable<double> fittings, double endOffset)
        {
            foreach (double s in stations)
                foreach (double f in fittings)
                    Assert.True(System.Math.Abs(s - f) >= endOffset - 1e-9, "station " + s + " is " + System.Math.Abs(s - f) + " from the fitting at " + f);
        }

        [Fact]
        public void Short_run_gets_one_station_when_the_two_ends_coincide()
        {
            // runLength == 2*offset: the two end stations are the same point.
            Assert.Equal(new[] { 1.0 }, Stations(2.0, 1.0, 5.0));
        }

        [Fact]
        public void Short_run_gets_two_stations_when_the_two_ends_are_distinct()
        {
            var stations = Stations(2.2, 1.0, 5.0);
            Assert.Equal(2, stations.Count);
            Assert.Equal(1.0, stations[0], 9);
            Assert.Equal(1.2, stations[1], 9);
        }

        [Fact]
        public void Run_shorter_than_twice_the_offset_gets_no_station()
        {
            HangerStationPlan plan = HangerRules.Plan(1.5, 1.0, 5.0, null, Budget);
            Assert.Empty(plan.Stations);
            Assert.True(plan.TooShort);
        }

        [Fact]
        public void Exact_multiple_of_the_spacing_lands_exactly_on_spacing()
        {
            // start=1, end=21 (runLength=22, offset=1): 20 / 5 = 4 intervals exactly.
            Assert.Equal(new[] { 1.0, 6.0, 11.0, 16.0, 21.0 }, Stations(22.0, 1.0, 5.0));
        }

        [Fact]
        public void Non_exact_length_splits_evenly_never_exceeding_spacing()
        {
            // start=0, end=19: 19/5 -> 4 intervals, step 4.75 (<= 5), not a 5+5+5+4 tail.
            var stations = Stations(19.0, 0.0, 5.0);
            Assert.Equal(5, stations.Count);
            AssertNoGapAbove(stations, 5.0);
            Assert.Equal(0.0, stations.First());
            Assert.Equal(19.0, stations.Last());
        }

        [Fact]
        public void An_interior_fitting_splits_the_run_and_the_stations_are_replaced_at_its_clearance_edges()
        {
            // start=1, end=21, fitting at 11.3 -> exclusion zone (10.3, 12.3). The free
            // segments [1, 10.3] and [12.3, 21] each get edge stations and an even split:
            // 1, 5.65, 10.3 | 12.3, 16.65, 21. The station the fitting displaced is
            // REPLACED at the zone's edges, so no gap exceeds the spacing (the old grid
            // simply dropped 11 and left a gap of 10 = 2 x spacing).
            var fittings = new List<double> { 11.3 };
            HangerStationPlan plan = HangerRules.Plan(22.0, 1.0, 5.0, fittings, Budget);
            Assert.Equal(new[] { 1.0, 5.65, 10.3, 12.3, 16.65, 21.0 }, plan.Stations.Select(s => System.Math.Round(s, 9)));
            AssertNoGapAbove(plan.Stations, 5.0);
            AssertClearOf(plan.Stations, fittings, 1.0);
            Assert.Empty(plan.WideGaps);
        }

        [Fact]
        public void Several_fittings_never_leave_a_gap_above_the_spacing_when_the_zones_are_narrower_than_it()
        {
            var fittings = new List<double> { 3.2, 7.9, 8.4, 14.0, 19.7 };
            foreach (double offset in new[] { 0.5, 1.0, 2.0 })
            {
                HangerStationPlan plan = HangerRules.Plan(22.0, offset, 5.0, fittings, Budget);
                Assert.NotEmpty(plan.Stations);
                AssertNoGapAbove(plan.Stations, 5.0);
                AssertClearOf(plan.Stations, fittings, offset);
                Assert.Empty(plan.WideGaps);
            }
        }

        [Fact]
        public void A_clearance_zone_wider_than_the_spacing_is_reported_not_hidden()
        {
            // offset 3 -> zone (8, 14) around a fitting at 11: 6 > spacing 5 cannot be closed
            // without breaking the clearance, so the gap is named.
            HangerStationPlan plan = HangerRules.Plan(22.0, 3.0, 5.0, new List<double> { 11.0 }, Budget);
            Assert.Single(plan.WideGaps);
            Assert.Equal(8.0, plan.WideGaps[0][0], 9);
            Assert.Equal(14.0, plan.WideGaps[0][1], 9);
            Assert.Equal(plan.WideGaps.Count, HangerRules.GapsAbove(plan.Stations, 5.0).Count);
        }

        [Fact]
        public void An_end_station_inside_a_taps_clearance_moves_to_the_zones_far_edge()
        {
            // Run 6000, offset 300, tap at 350: the end station at 300 would be 50 from the
            // tap. It moves to 650 (the zone's far edge) and StartShift says by 350.
            var fittings = new List<double> { 350.0 };
            HangerStationPlan plan = HangerRules.Plan(6000.0, 300.0, 1500.0, fittings, Budget);
            Assert.Equal(650.0, plan.Stations.First(), 9);
            Assert.Equal(350.0, plan.StartShift, 9);
            Assert.Equal(5700.0, plan.Stations.Last(), 9);
            Assert.Equal(0.0, plan.EndShift, 9);
            AssertClearOf(plan.Stations, fittings, 300.0);
            AssertNoGapAbove(plan.Stations, 1500.0);
        }

        [Fact]
        public void A_tap_near_the_far_end_moves_the_last_station_inward()
        {
            HangerStationPlan plan = HangerRules.Plan(6000.0, 300.0, 1500.0, new List<double> { 5600.0 }, Budget);
            Assert.Equal(5300.0, plan.Stations.Last(), 9);
            Assert.Equal(400.0, plan.EndShift, 9);
        }

        [Fact]
        public void A_span_entirely_inside_fitting_clearances_gets_no_station()
        {
            HangerStationPlan plan = HangerRules.Plan(3.0, 1.0, 5.0, new List<double> { 1.5 }, Budget);
            Assert.Empty(plan.Stations);
            Assert.True(plan.NoFreeSpan);
        }

        [Fact]
        public void Fittings_at_the_runs_own_ends_never_suppress_the_end_stations()
        {
            // A fitting exactly at the run's physical ends is exactly end_offset from the end
            // stations - not LESS than it - so the end stations are kept.
            Assert.Equal(new[] { 1.0, 6.0, 11.0, 16.0, 21.0 }, Stations(22.0, 1.0, 5.0, new List<double> { 0.0, 22.0 }));
        }

        [Fact]
        public void A_station_count_above_the_budget_refuses_before_allocating()
        {
            // spacing 1e-9 on a 3 m run: the ratio exceeds int.MaxValue. It must refuse as
            // over budget, never wrap to two stations or run out of memory.
            HangerStationPlan huge = HangerRules.Plan(3.0, 0.0, 1e-9, null, Budget);
            Assert.True(huge.OverBudget);
            Assert.Empty(huge.Stations);
            HangerStationPlan tight = HangerRules.Plan(22.0, 1.0, 5.0, null, 4);
            Assert.True(tight.OverBudget);
            Assert.False(HangerRules.Plan(22.0, 1.0, 5.0, null, 5).OverBudget);
        }

        [Fact]
        public void Invalid_inputs_return_no_stations_rather_than_throw()
        {
            Assert.Empty(Stations(0.0, 1.0, 5.0));
            Assert.Empty(Stations(10.0, 1.0, 0.0));
            Assert.Empty(Stations(10.0, -1.0, 5.0));
            Assert.Empty(Stations(10.0, double.NaN, 5.0));
        }
    }
}
