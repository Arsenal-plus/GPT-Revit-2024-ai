// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A SUM IS NOT AN ANSWER UNTIL IT SAYS WHAT IT COUNTS IN.
//
// MEASURED (dry run 2026-09): horizun_query_model sum_parameters returned wall areas
// and room areas as bare numbers in Revit's internal units - square feet - while
// horizun_quantities mode=takeoff, over the same walls, answered in m2. A room of
// 38.66 m2 read 416.16, and nothing in the reply said which. A reader who takes the
// number as metres is wrong by a factor of 10.76, and the reply gave them no way to
// notice.
//
// The fix is additive. `sum` stays the internal-unit total it always was (callers
// that already convert keep working), and beside it every sum now names:
//   - sum_unit: what `sum` is in ("ft2 (Revit internal)", "unitless", ...);
//   - quantity: length | area | volume | measurable | unitless | identifier | mixed | unknown;
//   - value + unit: the SI reading (m, m2, m3) for length, area and volume, with the
//     same factors horizun_quantities converts with (Reconcile.Ft*To*);
//   - value_display + unit_display: the document's display unit, when the Revit
//     half could read it and the unit is a plain scale (an offset unit - a
//     temperature - cannot convert a SUM as one number, and is refused by name).
// A spec that is not a measurable quantity (number, integer, yes/no) is summed as-is
// and labelled unitless. Contributors whose specs disagree are not converted at all:
// a sum of square feet and cubic feet has no unit, and the reply says so.
//
// Revit-free: the Revit half classifies the spec and reads the display unit; this
// decides what the number may be called.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The document's display unit for a spec, as the Revit half read it.</summary>
    public sealed class DisplayUnitFact
    {
        /// <summary>The unit's ForgeTypeId TypeId.</summary>
        public string UnitTypeId;
        /// <summary>The unit's label as Revit shows it ("Square meters").</summary>
        public string Label;
        /// <summary>ConvertFromInternalUnits(1) - ConvertFromInternalUnits(0).</summary>
        public double Factor;
        /// <summary>ConvertFromInternalUnits(0). Non-zero: an affine unit (temperature).</summary>
        public double Offset;
    }

    public static class SumUnitRules
    {
        public const string Length = "length";
        public const string Area = "area";
        public const string Volume = "volume";
        public const string Measurable = "measurable";
        public const string Unitless = "unitless";
        public const string Identifier = "identifier";
        public const string Mixed = "mixed";
        public const string Unknown = "unknown";

        /// <summary>
        /// The block added beside an existing sum. `quantities` is the classification of
        /// each contributing value's spec (one entry per DISTINCT spec seen, keyed by the
        /// spec TypeId, or "" when the spec could not be read); `display` is the
        /// document's display unit for that spec, or null.
        /// </summary>
        public static JObject Describe(double internalSum, int summed,
                                       IDictionary<string, string> quantityBySpec,
                                       DisplayUnitFact display)
        {
            var o = new JObject();
            if (summed == 0 || quantityBySpec == null || quantityBySpec.Count == 0)
            {
                // Nothing was summed: there is no number whose unit could be named.
                o["sum_unit"] = null;
                o["quantity"] = summed == 0 ? null : Unknown;
                o["value"] = null;
                o["unit"] = null;
                o["unit_note"] = summed == 0
                    ? "no element contributed a number, so the sum has no unit."
                    : "the spec of the summed parameter could not be read, so its unit is unknown; `sum` is the raw total.";
                return o;
            }
            if (quantityBySpec.Count > 1)
            {
                o["sum_unit"] = "mixed";
                o["quantity"] = Mixed;
                o["specs"] = new JArray(quantityBySpec.Keys.OrderBy(k => k, StringComparer.Ordinal));
                o["value"] = null;
                o["unit"] = null;
                o["unit_note"] = "the contributing parameters have different specs (" +
                                 string.Join(", ", quantityBySpec.Values.Distinct().OrderBy(v => v, StringComparer.Ordinal)) +
                                 "), so `sum` adds numbers in different units and is not converted. " +
                                 "Narrow the query (categories) so one spec is summed.";
                return o;
            }

            KeyValuePair<string, string> only = quantityBySpec.First();
            string spec = only.Key;
            string quantity = only.Value ?? Unknown;
            o["spec"] = string.IsNullOrEmpty(spec) ? JValue.CreateNull() : new JValue(spec);
            o["quantity"] = quantity;
            switch (quantity)
            {
                case Length:
                    o["sum_unit"] = "ft (Revit internal)";
                    o["value"] = Round(Reconcile.ToM(internalSum));
                    o["unit"] = "m";
                    break;
                case Area:
                    o["sum_unit"] = "ft2 (Revit internal)";
                    o["value"] = Round(Reconcile.ToM2(internalSum));
                    o["unit"] = "m2";
                    break;
                case Volume:
                    o["sum_unit"] = "ft3 (Revit internal)";
                    o["value"] = Round(Reconcile.ToM3(internalSum));
                    o["unit"] = "m3";
                    break;
                case Unitless:
                    o["sum_unit"] = "unitless";
                    o["value"] = internalSum;
                    o["unit"] = "unitless";
                    break;
                case Identifier:
                    o["sum_unit"] = "element ids";
                    o["value"] = null;
                    o["unit"] = null;
                    o["unit_note"] = "the parameter stores element ids; their sum is not a quantity and is not converted.";
                    return o;
                case Measurable:
                    o["sum_unit"] = "Revit internal units of " + (spec ?? "an unknown spec");
                    o["value"] = null;
                    o["unit"] = null;
                    break;
                default:
                    o["sum_unit"] = "unknown";
                    o["value"] = null;
                    o["unit"] = null;
                    o["unit_note"] = "the spec of the summed parameter could not be read, so its unit is unknown; `sum` is the raw total.";
                    return o;
            }

            if (quantity == Unitless) return o;
            if (display == null)
            {
                if (quantity == Measurable)
                    o["unit_note"] = "the document's display unit for this spec could not be read, so `sum` is " +
                                     "in Revit internal units and is not converted.";
                return o;
            }
            if (Math.Abs(display.Offset) > 1e-12)
            {
                o["value_display"] = null;
                o["unit_display"] = display.Label;
                o["unit_note"] = "the display unit '" + display.Label + "' has an offset (like a temperature): a sum " +
                                 "cannot be converted to it as one number. `sum` is in Revit internal units.";
                return o;
            }
            o["value_display"] = Round(internalSum * display.Factor);
            o["unit_display"] = display.Label;
            o["unit_display_type_id"] = display.UnitTypeId;
            return o;
        }

        private static double Round(double v) => Math.Round(v, 6);
    }
}
