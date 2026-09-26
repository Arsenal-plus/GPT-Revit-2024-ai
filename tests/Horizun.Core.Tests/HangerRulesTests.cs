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
        [Fact]
        public void Short_run_gets_one_station_when_the_two_ends_coincide()
        {
            // runLength == 2*offset: the two end stations are the same point.
            var stations = HangerRules.ComputeStations(runLength: 2.0, endOffset: 1.0, spacing: 5.0, interiorFittingPositions: null);
            Assert.Equal(new[] { 1.0 }, stations);
        }

        [Fact]
        public void Short_run_gets_two_stations_when_the_two_ends_are_distinct()
        {
            // runLength slightly more than 2*offset, well under spacing: both end
            // stations exist and are the only two (no room for an intermediate one).
            var stations = HangerRules.ComputeStations(runLength: 2.2, endOffset: 1.0, spacing: 5.0, interiorFittingPositions: null);
            Assert.Equal(2, stations.Count);
            Assert.Equal(1.0, stations[0], 9);
            Assert.Equal(1.2, stations[1], 9);
        }

        [Fact]
        public void Run_shorter_than_twice_the_offset_gets_no_station()
        {
            var stations = HangerRules.ComputeStations(runLength: 1.5, endOffset: 1.0, spacing: 5.0, interiorFittingPositions: null);
            Assert.Empty(stations);
        }

        [Fact]
        public void Exact_multiple_of_the_spacing_lands_exactly_on_spacing()
        {
            // start=1, end=21 (runLength=22, offset=1): 20 / 5 = 4 intervals exactly.
            var stations = HangerRules.ComputeStations(runLength: 22.0, endOffset: 1.0, spacing: 5.0, interiorFittingPositions: null);
            Assert.Equal(new[] { 1.0, 6.0, 11.0, 16.0, 21.0 }, stations);
        }

        [Fact]
        public void Non_exact_length_splits_evenly_never_exceeding_spacing()
        {
            // start=0, end=19: 19/5 -> 4 intervals, step 4.75 (<= 5), not one long
            // 5+5+5+4 tail. Every gap must be <= spacing.
            var stations = HangerRules.ComputeStations(runLength: 19.0, endOffset: 0.0, spacing: 5.0, interiorFittingPositions: null);
            Assert.Equal(5, stations.Count);
            for (int i = 1; i < stations.Count; i++)
                Assert.True(stations[i] - stations[i - 1] <= 5.0 + 1e-9);
            Assert.Equal(0.0, stations.First());
            Assert.Equal(19.0, stations.Last());
        }

        [Fact]
        public void An_interior_fitting_suppresses_the_station_within_end_offset_of_it()
        {
            // start=1, end=21, stations at 1,6,11,16,21. A fitting at 11.3 is within
            // offset(1) of the station at 11, which must be dropped; the others stay.
            var stations = HangerRules.ComputeStations(runLength: 22.0, endOffset: 1.0, spacing: 5.0,
                interiorFittingPositions: new List<double> { 11.3 });
            Assert.Equal(new[] { 1.0, 6.0, 16.0, 21.0 }, stations);
        }

        [Fact]
        public void Fittings_at_the_runs_own_ends_never_suppress_the_end_stations()
        {
            // A fitting sitting exactly at the run's physical ends (0 and runLength)
            // is exactly end_offset away from the end stations - not LESS than it -
            // so end_offset alone already gives the required clearance and the end
            // stations are kept.
            var stations = HangerRules.ComputeStations(runLength: 22.0, endOffset: 1.0, spacing: 5.0,
                interiorFittingPositions: new List<double> { 0.0, 22.0 });
            Assert.Equal(new[] { 1.0, 6.0, 11.0, 16.0, 21.0 }, stations);
        }

        [Fact]
        public void Invalid_inputs_return_no_stations_rather_than_throw()
        {
            Assert.Empty(HangerRules.ComputeStations(0.0, 1.0, 5.0, null));
            Assert.Empty(HangerRules.ComputeStations(10.0, 1.0, 0.0, null));
            Assert.Empty(HangerRules.ComputeStations(10.0, -1.0, 5.0, null));
        }
    }
}
