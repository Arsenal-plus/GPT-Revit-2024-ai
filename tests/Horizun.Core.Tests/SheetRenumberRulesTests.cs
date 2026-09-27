// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// renumber_sheets planning (SheetRenumberRules): collisions are refused against
// EVERY sheet before a write, and the step order is one Revit accepts - no step
// may assign a number another sheet still holds at that moment.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SheetRenumberRulesTests
    {
        private static List<KeyValuePair<string, string>> Map(params string[] pairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < pairs.Length; i += 2) list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
            return list;
        }

        /// <summary>Replays the steps the way Revit would: every assignment must land on a free number.</summary>
        private static Dictionary<string, string> Replay(SheetRenumberPlan plan, IEnumerable<string> existing)
        {
            var bySheet = existing.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
            foreach (var s in plan.Steps)
            {
                Assert.Equal(bySheet[s.Sheet], s.From, StringComparer.OrdinalIgnoreCase);
                string holder = bySheet.FirstOrDefault(kv => string.Equals(kv.Value, s.To, StringComparison.OrdinalIgnoreCase)).Key;
                Assert.True(holder == null || string.Equals(holder, s.Sheet, StringComparison.OrdinalIgnoreCase),
                            "step " + s.From + " -> " + s.To + " lands on a number sheet " + holder + " still holds");
                bySheet[s.Sheet] = s.To;
            }
            return bySheet;
        }

        [Fact]
        public void A_chain_moves_in_an_order_without_temporaries()
        {
            var sheets = new[] { "A101", "A102", "A103" };
            var plan = SheetRenumberRules.Plan(Map("A101", "A102", "A102", "A103", "A103", "A104"), sheets);
            Assert.Equal(0, plan.TemporarySteps);
            var end = Replay(plan, sheets);
            Assert.Equal("A102", end["A101"]); Assert.Equal("A103", end["A102"]); Assert.Equal("A104", end["A103"]);
        }

        [Fact]
        public void A_swap_parks_exactly_one_sheet()
        {
            var sheets = new[] { "A101", "A102", "S1" };
            var plan = SheetRenumberRules.Plan(Map("A101", "A102", "A102", "A101"), sheets);
            Assert.Equal(1, plan.TemporarySteps);
            Assert.Equal(3, plan.Steps.Count);
            var end = Replay(plan, sheets);
            Assert.Equal("A102", end["A101"]); Assert.Equal("A101", end["A102"]); Assert.Equal("S1", end["S1"]);
        }

        [Fact]
        public void Two_disjoint_cycles_each_cost_one_temporary()
        {
            var sheets = new[] { "1", "2", "3", "X", "Y" };
            var plan = SheetRenumberRules.Plan(Map("1", "2", "2", "3", "3", "1", "X", "Y", "Y", "X"), sheets);
            Assert.Equal(2, plan.TemporarySteps);
            var end = Replay(plan, sheets);
            Assert.Equal("2", end["1"]); Assert.Equal("3", end["2"]); Assert.Equal("1", end["3"]);
            Assert.Equal("Y", end["X"]); Assert.Equal("X", end["Y"]);
        }

        [Fact]
        public void A_temporary_number_never_equals_any_held_or_target_number()
        {
            var sheets = new[] { "HZTMP-1", "A", "B" };
            var plan = SheetRenumberRules.Plan(Map("A", "B", "B", "A"), sheets, new[] { "HZTMP-2" });
            var temp = plan.Steps.Single(s => s.Temporary).To;
            Assert.Equal("HZTMP-3", temp);
        }

        [Fact]
        public void A_target_held_by_a_sheet_outside_the_map_is_refused_before_writing()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SheetRenumberRules.Plan(Map("A101", "A200"), new[] { "A101", "A200" }));
            Assert.Contains("'A200' (target of 'A101') is held by a sheet the map does not move", ex.Message);
        }

        [Fact]
        public void A_collision_is_case_insensitive_like_the_create_operations()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SheetRenumberRules.Plan(Map("A101", "a200"), new[] { "A101", "A200" }));
            Assert.Contains("held by a sheet the map does not move", ex.Message);
        }

        [Fact]
        public void A_target_held_by_an_identity_entry_is_still_a_collision()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SheetRenumberRules.Plan(Map("A1", "A2", "A2", "A2"), new[] { "A1", "A2" }));
            Assert.Contains("'A2' (target of 'A1')", ex.Message);
        }

        [Fact]
        public void Every_problem_is_named_at_once()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SheetRenumberRules.Plan(Map("A1", "Z", "A2", "Z", "Q9", "Q10", "A3", " "), new[] { "A1", "A2", "A3" }));
            Assert.Contains("'A1' and 'A2' would both become 'Z'", ex.Message);
            Assert.Contains("no sheet is numbered 'Q9'", ex.Message);
            Assert.Contains("'A3' -> a blank number", ex.Message);
        }

        [Fact]
        public void A_number_reserved_by_the_batch_is_refused()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SheetRenumberRules.Plan(Map("A1", "N1"), new[] { "A1" }, new[] { "n1" }));
            Assert.Contains("taken by another action of this batch", ex.Message);
        }

        [Fact]
        public void An_old_number_named_twice_is_refused()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                SheetRenumberRules.Plan(Map("A1", "B1", "a1", "C1"), new[] { "A1" }));
            Assert.Contains("named twice", ex.Message);
        }

        [Fact]
        public void Identity_entries_are_unchanged_and_a_case_only_change_is_one_direct_step()
        {
            var plan = SheetRenumberRules.Plan(Map("A1", "A1", "b2", "B2"), new[] { "A1", "b2" });
            Assert.Equal(new[] { "A1" }, plan.Unchanged);
            var step = Assert.Single(plan.Steps);
            Assert.Equal("B2", step.To); Assert.False(step.Temporary);
        }

        [Fact]
        public void An_empty_map_is_refused()
        {
            Assert.Throws<ArgumentException>(() => SheetRenumberRules.Plan(Map(), new[] { "A1" }));
        }
    }
}
