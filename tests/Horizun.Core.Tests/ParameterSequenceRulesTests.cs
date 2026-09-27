// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The 'sequence' generator of horizun_write_params_verified (ParameterSequenceRules):
// a declared total order, explicit formatting, and a refusal that names every target
// missing the datum its order needs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ParameterSequenceRulesTests
    {
        private static SequenceTarget T(long id, string level, double elev, double x, double y, string room = null) =>
            new SequenceTarget { Id = id, Level = level, LevelElevation = elev, X = x, Y = y, Room = room };

        private static SequenceOptions O(params string[] order) => new SequenceOptions { OrderBy = order.ToList() };

        [Fact]
        public void Orders_by_level_then_x_with_prefix_start_step_and_padding()
        {
            var targets = new[] { T(5, "L2", 3, 0, 0), T(1, "L1", 0, 10, 0), T(2, "L1", 0, 2, 0) };
            var o = O("level", "x"); o.Prefix = "D-"; o.Start = 10; o.Step = 5; o.Pad = 3;
            var r = ParameterSequenceRules.Generate(targets, o);
            Assert.Equal(new long[] { 2, 1, 5 }, r.Assignments.Select(a => a.Id));
            Assert.Equal(new[] { "D-010", "D-015", "D-020" }, r.Assignments.Select(a => a.Value));
        }

        [Fact]
        public void Restart_per_level_counts_each_level_from_start_and_flags_the_repeat()
        {
            var targets = new[] { T(1, "L1", 0, 0, 0), T(2, "L1", 0, 1, 0), T(3, "L2", 3, 0, 0) };
            var o = O("level", "x"); o.RestartPerLevel = true;
            var r = ParameterSequenceRules.Generate(targets, o);
            Assert.Equal(new[] { "1", "2", "1" }, r.Assignments.Select(a => a.Value));
            Assert.True(r.RepeatsAcrossLevels);
        }

        [Fact]
        public void Restart_per_level_without_level_first_is_refused()
        {
            var o = O("x", "level"); o.RestartPerLevel = true;
            var ex = Assert.Throws<ArgumentException>(() => ParameterSequenceRules.Generate(new[] { T(1, "L1", 0, 0, 0) }, o));
            Assert.Contains("FIRST order_by key", ex.Message);
        }

        [Fact]
        public void Coordinates_a_rounding_error_apart_tie_and_fall_back_to_the_id()
        {
            double hair = 1e-9;
            var targets = new[] { T(9, "L1", 0, 1 + hair, 0), T(3, "L1", 0, 1, 0) };
            var r = ParameterSequenceRules.Generate(targets, O("x"));
            Assert.Equal(new long[] { 3, 9 }, r.Assignments.Select(a => a.Id));
        }

        [Fact]
        public void Room_order_is_natural()
        {
            var targets = new[] { T(1, "L1", 0, 0, 0, "10"), T(2, "L1", 0, 0, 0, "2"), T(3, "L1", 0, 0, 0, "A2"), T(4, "L1", 0, 0, 0, "A10") };
            var r = ParameterSequenceRules.Generate(targets, O("room"));
            Assert.Equal(new long[] { 2, 1, 3, 4 }, r.Assignments.Select(a => a.Id));
        }

        [Fact]
        public void A_target_outside_every_room_is_named_not_sorted_to_an_end()
        {
            var targets = new[] { T(1, "L1", 0, 0, 0, "1"), T(77, "L1", 0, 0, 0, null) };
            var ex = Assert.Throws<ArgumentException>(() => ParameterSequenceRules.Generate(targets, O("room")));
            Assert.Contains("1 target(s) are not inside a room at the phase: 77", ex.Message);
        }

        [Fact]
        public void A_target_without_a_location_is_named_when_x_or_y_orders()
        {
            var targets = new[] { new SequenceTarget { Id = 4, Level = "L1", LevelElevation = 0 } };
            var ex = Assert.Throws<ArgumentException>(() => ParameterSequenceRules.Generate(targets, O("y")));
            Assert.Contains("have no location point: 4", ex.Message);
        }

        [Fact]
        public void Bad_options_are_all_named_at_once()
        {
            var o = O("z", "x", "x"); o.Step = 0; o.Start = -1; o.Pad = 20;
            var ex = Assert.Throws<ArgumentException>(() => ParameterSequenceRules.Generate(new[] { T(1, "L1", 0, 0, 0) }, o));
            Assert.Contains("'z' is not one of", ex.Message);
            Assert.Contains("names a key twice", ex.Message);
            Assert.Contains("step must be 1 or more", ex.Message);
            Assert.Contains("start must be 0 or more", ex.Message);
            Assert.Contains("pad must be 0..12", ex.Message);
        }

        [Fact]
        public void The_same_input_in_any_order_yields_the_same_assignment()
        {
            var a = new[] { T(1, "L1", 0, 5, 1), T(2, "L1", 0, 5, 0), T(3, "L2", 3, 0, 0), T(4, "L1", 0, 1, 9) };
            var r1 = ParameterSequenceRules.Generate(a, O("level", "x", "y"));
            var r2 = ParameterSequenceRules.Generate(Enumerable.Reverse(a).ToArray(), O("level", "x", "y"));
            Assert.Equal(r1.Assignments.Select(x => x.Id + "=" + x.Value), r2.Assignments.Select(x => x.Id + "=" + x.Value));
            Assert.Equal(new long[] { 4, 2, 1, 3 }, r1.Assignments.Select(x => x.Id));
        }

        [Fact]
        public void A_target_named_twice_is_refused()
        {
            var ex = Assert.Throws<ArgumentException>(() => ParameterSequenceRules.Generate(new[] { T(1, "L1", 0, 0, 0), T(1, "L1", 0, 1, 0) }, O("x")));
            Assert.Contains("targets named twice: 1", ex.Message);
        }
    }
}
