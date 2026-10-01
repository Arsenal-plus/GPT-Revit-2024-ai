// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH FIELDS A NON-ITEMIZED SCHEDULE GROUPS BY, without a Revit in the room.
//
// A non-itemized schedule collapses every element that agrees on the sort/group
// fields into ONE row. Grouping by an IDENTITY field (type, family, level, mark,
// a text parameter) is what "one row per type" means. Grouping by a QUANTITY
// (length, area, volume, a count, any number) is the opposite: two walls of the
// same type almost never share a length to the last decimal, so every wall stays
// on its own row and the "summary" is the itemized list again with blank cells.
//
// Measured on a real model (dry run of the "Revit con agentes" course,
// 2026-09-30): OST_Walls with Type/Count/Length/Area/Volume, itemized=false,
// produced 119 body rows instead of 8, because horizun_create_schedule grouped
// by every non-Count field - Length, Area and Volume included.
//
// The decisions, in one place:
//
//   * A field is a QUANTITY when Revit says it is a number: the Count field, a
//     material quantity, a percentage or a formula; any field whose spec is
//     measurable (has units); any integer or plain-number spec; or any field
//     Revit lets you TOTAL. Otherwise it is an IDENTITY. Revit answers each of
//     those itself (ScheduleFieldType, GetSpecTypeId, UnitUtils.IsMeasurableSpec,
//     CanTotal); this file only combines the answers.
//
//   * Derived grouping (no explicit list): sort/group by the IDENTITY fields in
//     the order they were requested, and turn on totals for every QUANTITY field
//     Revit can total, except Count, which a grouped row already counts by
//     itself. Without totals a grouped row shows a quantity only when every
//     element in it agrees - i.e. the column comes out blank.
//
//   * An EXPLICIT list (group_by) is honoured exactly: those fields, that order,
//     nothing added. Each name must be one of the requested fields - grouping by
//     a field that is not in the schedule is refused, not silently added.
//
//   * Itemized schedules group by nothing unless the caller asked: every element
//     is its own row already, and the order of the request is not a sort.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One field the schedule was given, as Revit described it. Plain facts.</summary>
    public sealed class ScheduleGroupingField
    {
        /// <summary>The name the request used for the field (what group_by refers to).</summary>
        public string RequestedName { get; set; }
        /// <summary>ScheduleFieldType name: Instance, ElementType, Count, MaterialQuantity, Formula...</summary>
        public string FieldType { get; set; }
        /// <summary>ScheduleField.GetSpecTypeId().TypeId; empty or null when the field is not a number with units.</summary>
        public string SpecTypeId { get; set; }
        /// <summary>UnitUtils.IsMeasurableSpec of that spec; null when it could not be asked.</summary>
        public bool? IsMeasurable { get; set; }
        /// <summary>ScheduleField.CanTotal(); null when it could not be asked.</summary>
        public bool? CanTotal { get; set; }
    }

    public enum ScheduleFieldRole { Identity, Quantity }

    /// <summary>What the schedule will sort/group by and which columns total, with the reason for each field.</summary>
    public sealed class ScheduleGroupingPlan
    {
        /// <summary>"derived" (from the field roles), "explicit" (group_by), or "none" (itemized).</summary>
        public string Source { get; set; }
        /// <summary>Indices into the field list, in sort/group order.</summary>
        public List<int> SortGroup { get; } = new List<int>();
        /// <summary>Indices of fields whose display type becomes Totals.</summary>
        public List<int> Totals { get; } = new List<int>();
        public List<ScheduleFieldRole> Roles { get; } = new List<ScheduleFieldRole>();
        public List<string> Reasons { get; } = new List<string>();
        /// <summary>Non-null when the request cannot be honoured; nothing may be written then.</summary>
        public string Error { get; set; }
    }

    public static class ScheduleGroupingRules
    {
        // ScheduleFieldType names whose values are numbers by construction.
        private static readonly HashSet<string> NumericFieldTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Count", "MaterialQuantity", "Percentage", "Formula"
        };

        // Unitless numeric specs. IsMeasurableSpec is false for these (they have no
        // units) and yet grouping by them is grouping by a number.
        private static readonly string[] NumericSpecPrefixes =
        {
            "autodesk.spec:spec.int",      // Integer (int64)
            "autodesk.spec:spec.double",   // Number
        };

        /// <summary>Identity or quantity, with the fact that decided it.</summary>
        public static ScheduleFieldRole Classify(ScheduleGroupingField field, out string reason)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (!string.IsNullOrEmpty(field.FieldType) && NumericFieldTypes.Contains(field.FieldType))
            {
                reason = "field type " + field.FieldType + " is a number";
                return ScheduleFieldRole.Quantity;
            }
            if (field.IsMeasurable == true)
            {
                reason = "spec " + field.SpecTypeId + " is measurable (has units)";
                return ScheduleFieldRole.Quantity;
            }
            string spec = field.SpecTypeId ?? "";
            foreach (string prefix in NumericSpecPrefixes)
                if (spec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "spec " + spec + " is a plain number";
                    return ScheduleFieldRole.Quantity;
                }
            if (field.CanTotal == true)
            {
                reason = "Revit can total this field";
                return ScheduleFieldRole.Quantity;
            }
            reason = spec.Length == 0
                ? "no numeric spec and Revit cannot total it"
                : "spec " + spec + " is not a number and Revit cannot total it";
            return ScheduleFieldRole.Identity;
        }

        /// <summary>
        /// The grouping for a schedule with these fields, in the order the request
        /// listed them. explicitGroupBy null = derive; an empty list = group by nothing.
        /// </summary>
        public static ScheduleGroupingPlan Plan(IReadOnlyList<ScheduleGroupingField> fields, bool itemized,
                                                IReadOnlyList<string> explicitGroupBy)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            var plan = new ScheduleGroupingPlan();
            foreach (ScheduleGroupingField f in fields)
            {
                string why;
                plan.Roles.Add(Classify(f, out why));
                plan.Reasons.Add(why);
            }

            if (explicitGroupBy != null)
            {
                plan.Source = "explicit";
                plan.Error = ValidateGroupBy(fields.Select(f => f.RequestedName).ToList(), explicitGroupBy);
                if (plan.Error != null) return plan;
                foreach (string name in explicitGroupBy) plan.SortGroup.Add(IndexOf(fields, name));
            }
            else if (itemized)
            {
                plan.Source = "none";
            }
            else
            {
                plan.Source = "derived";
                for (int i = 0; i < fields.Count; i++)
                    if (plan.Roles[i] == ScheduleFieldRole.Identity) plan.SortGroup.Add(i);
            }

            // Totals only matter where rows collapse. A field the caller groups by is
            // constant within its row and needs no total; Count counts by itself.
            if (!itemized)
                for (int i = 0; i < fields.Count; i++)
                    if (plan.Roles[i] == ScheduleFieldRole.Quantity && fields[i].CanTotal == true &&
                        !string.Equals(fields[i].FieldType, "Count", StringComparison.OrdinalIgnoreCase) &&
                        !plan.SortGroup.Contains(i))
                        plan.Totals.Add(i);
            return plan;
        }

        /// <summary>
        /// Null when every group_by name is one of the requested field names (case-insensitive,
        /// each at most once); otherwise the refusal. Decided on the REQUEST alone, so the
        /// rehearsal refuses exactly what the apply would.
        /// </summary>
        public static string ValidateGroupBy(IReadOnlyList<string> requestedFields, IReadOnlyList<string> groupBy)
        {
            if (groupBy == null) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in groupBy)
            {
                string name = (raw ?? "").Trim();
                if (!requestedFields.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                    return "group_by names '" + name + "', which is not one of the requested fields (" +
                        string.Join(", ", requestedFields) + "). A schedule cannot group by a field it does not " +
                        "have; add it to fields or remove it from group_by. Nothing was changed.";
                if (!seen.Add(name)) return "group_by names '" + name + "' twice. Nothing was changed.";
            }
            return null;
        }

        private static int IndexOf(IReadOnlyList<ScheduleGroupingField> fields, string name)
        {
            string wanted = (name ?? "").Trim();
            for (int i = 0; i < fields.Count; i++)
                if (string.Equals(fields[i].RequestedName, wanted, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
