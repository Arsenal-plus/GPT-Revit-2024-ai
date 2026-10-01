// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Which fields a non-itemized schedule groups by. The case that opened this:
// OST_Walls with Type/Count/Length/Area/Volume, itemized=false, came out with
// 119 rows instead of 8 because Length, Area and Volume were in the sort. The
// fixture below is that exact field list, described the way Revit describes it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleGroupingRulesTests
    {
        private static ScheduleGroupingField Type() => new ScheduleGroupingField
            { RequestedName = "Type", FieldType = "Instance", SpecTypeId = "", IsMeasurable = false, CanTotal = false };
        private static ScheduleGroupingField Count() => new ScheduleGroupingField
            { RequestedName = "Count", FieldType = "Count", SpecTypeId = "", IsMeasurable = false, CanTotal = true };
        private static ScheduleGroupingField Measure(string name, string spec) => new ScheduleGroupingField
            { RequestedName = name, FieldType = "Instance", SpecTypeId = spec, IsMeasurable = true, CanTotal = true };

        /// <summary>The dry run's field list, in its order.</summary>
        private static List<ScheduleGroupingField> Walls() => new List<ScheduleGroupingField>
        {
            Type(), Count(),
            Measure("Length", "autodesk.spec.aec:length-2.0.0"),
            Measure("Area", "autodesk.spec.aec:area-2.0.0"),
            Measure("Volume", "autodesk.spec.aec:volume-2.0.0"),
        };

        private static string[] Names(List<ScheduleGroupingField> fields, IEnumerable<int> indices)
            => indices.Select(i => fields[i].RequestedName).ToArray();

        [Fact]
        public void The_dry_run_wall_schedule_groups_by_type_only_and_totals_the_quantities()
        {
            var fields = Walls();
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(fields, itemized: false, explicitGroupBy: null);
            Assert.Null(plan.Error);
            Assert.Equal("derived", plan.Source);
            // Before the fix this was Type, Length, Area, Volume: one row per wall.
            Assert.Equal(new[] { "Type" }, Names(fields, plan.SortGroup));
            Assert.Equal(new[] { "Length", "Area", "Volume" }, Names(fields, plan.Totals));
            Assert.Equal(new[] { ScheduleFieldRole.Identity, ScheduleFieldRole.Quantity, ScheduleFieldRole.Quantity,
                                 ScheduleFieldRole.Quantity, ScheduleFieldRole.Quantity }, plan.Roles);
        }

        [Fact]
        public void Identity_fields_keep_the_requested_order()
        {
            var fields = new List<ScheduleGroupingField>
            {
                new ScheduleGroupingField { RequestedName = "Level", FieldType = "Instance", SpecTypeId = "", IsMeasurable = false, CanTotal = false },
                Count(),
                new ScheduleGroupingField { RequestedName = "Family", FieldType = "Instance", SpecTypeId = "", IsMeasurable = false, CanTotal = false },
                Type(),
                new ScheduleGroupingField { RequestedName = "Mark", FieldType = "Instance", SpecTypeId = "autodesk.spec:spec.string-2.0.0", IsMeasurable = false, CanTotal = false },
                new ScheduleGroupingField { RequestedName = "Fire Rated", FieldType = "ElementType", SpecTypeId = "autodesk.spec:spec.bool-1.0.0", IsMeasurable = false, CanTotal = false },
            };
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(fields, false, null);
            Assert.Equal(new[] { "Level", "Family", "Type", "Mark", "Fire Rated" }, Names(fields, plan.SortGroup));
            Assert.Empty(plan.Totals);
        }

        [Fact]
        public void A_unitless_number_is_a_quantity_even_when_it_is_not_measurable()
        {
            var integer = new ScheduleGroupingField { RequestedName = "Treads", FieldType = "Instance", SpecTypeId = "autodesk.spec:spec.int64-2.0.0", IsMeasurable = false, CanTotal = true };
            var number = new ScheduleGroupingField { RequestedName = "Factor", FieldType = "Instance", SpecTypeId = "autodesk.spec:spec.double-1.0.0", IsMeasurable = false, CanTotal = false };
            string why;
            Assert.Equal(ScheduleFieldRole.Quantity, ScheduleGroupingRules.Classify(integer, out why));
            Assert.Equal(ScheduleFieldRole.Quantity, ScheduleGroupingRules.Classify(number, out why));
            Assert.Contains("plain number", why);
        }

        [Fact]
        public void Numeric_field_types_are_quantities_whatever_their_spec()
        {
            string why;
            foreach (string type in new[] { "Count", "MaterialQuantity", "Percentage", "Formula" })
                Assert.Equal(ScheduleFieldRole.Quantity, ScheduleGroupingRules.Classify(
                    new ScheduleGroupingField { RequestedName = type, FieldType = type }, out why));
        }

        [Fact]
        public void An_unreadable_spec_still_counts_as_a_quantity_when_Revit_can_total_it()
        {
            var length = new ScheduleGroupingField { RequestedName = "Length", FieldType = "Instance", SpecTypeId = null, IsMeasurable = null, CanTotal = true };
            var fields = new List<ScheduleGroupingField> { Type(), length };
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(fields, false, null);
            Assert.Equal(new[] { "Type" }, Names(fields, plan.SortGroup));
            Assert.Equal(new[] { "Length" }, Names(fields, plan.Totals));
        }

        [Fact]
        public void An_explicit_group_by_is_honoured_exactly_even_by_a_quantity()
        {
            var fields = Walls();
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(fields, false, new[] { "length", "Type" });
            Assert.Null(plan.Error);
            Assert.Equal("explicit", plan.Source);
            Assert.Equal(new[] { "Length", "Type" }, Names(fields, plan.SortGroup));
            // A grouped field is constant within its row: no total for it.
            Assert.Equal(new[] { "Area", "Volume" }, Names(fields, plan.Totals));
        }

        [Fact]
        public void An_empty_group_by_groups_by_nothing()
        {
            var fields = Walls();
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(fields, false, new string[0]);
            Assert.Null(plan.Error);
            Assert.Empty(plan.SortGroup);
            Assert.Equal(new[] { "Length", "Area", "Volume" }, Names(fields, plan.Totals));
        }

        [Fact]
        public void An_itemized_schedule_derives_no_grouping_and_no_totals()
        {
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(Walls(), true, null);
            Assert.Equal("none", plan.Source);
            Assert.Empty(plan.SortGroup);
            Assert.Empty(plan.Totals);
        }

        [Fact]
        public void An_itemized_schedule_still_sorts_by_an_explicit_group_by()
        {
            var fields = Walls();
            ScheduleGroupingPlan plan = ScheduleGroupingRules.Plan(fields, true, new[] { "Type" });
            Assert.Equal(new[] { "Type" }, Names(fields, plan.SortGroup));
            Assert.Empty(plan.Totals);
        }

        [Fact]
        public void Group_by_a_field_the_schedule_does_not_have_is_refused_before_anything_is_written()
        {
            string error = ScheduleGroupingRules.ValidateGroupBy(new[] { "Type", "Count", "Length" }, new[] { "Level" });
            Assert.NotNull(error);
            Assert.Contains("'Level'", error);
            Assert.Contains("Nothing was changed", error);
            Assert.NotNull(ScheduleGroupingRules.Plan(Walls(), false, new[] { "Level" }).Error);
        }

        [Fact]
        public void Group_by_the_same_field_twice_is_refused()
        {
            Assert.Contains("twice", ScheduleGroupingRules.ValidateGroupBy(new[] { "Type" }, new[] { "Type", "type" }));
            Assert.Null(ScheduleGroupingRules.ValidateGroupBy(new[] { "Type" }, null));
        }
    }
}
