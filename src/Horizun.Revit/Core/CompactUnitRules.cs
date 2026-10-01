// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A COMPACT NUMBER SAYS WHAT IT COUNTS IN.
//
// MEASURED (Comité de obra, 2026-10-01): horizun_query_model response_mode=compact
// returned "Volume": 15.53 for a column whose volume is 0.44 m3 - Revit's internal
// cubic feet, under a key that reads like the project's unit. Full mode carries
// `display` beside `raw`; compact kept only `raw`, so the one shape built for reading
// many rows was the one that silently needed a conversion factor.
//
// Compact now converts every measurable double to the HOST document's display unit
// for its spec (what Revit shows the user, the same unit `display` prints in full
// mode) and the reply names that unit once per parameter in `parameter_units`. A
// value that cannot be converted as one number (no readable display unit, an offset
// unit like a temperature) stays raw and its unit says "Revit internal". Text,
// integers and ids are untouched. parameter_format=full still returns `raw`.
//
// Revit-free: the Revit half classifies the spec and reads the display unit
// (SumUnitRules' DisplayUnitFact); this converts and keeps the ledger.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CompactUnitTally
    {
        private sealed class Unit
        {
            public string Label, UnitTypeId, Quantity, Spec; public bool Converted; public int Values;
            public string Key => (Spec ?? "") + "\u001f" + (Label ?? "") + "\u001f" + Converted;
        }

        private readonly Dictionary<string, Dictionary<string, Unit>> _byParameter =
            new Dictionary<string, Dictionary<string, Unit>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The value a compact row carries for one parameter. `quantity` is SumUnitRules'
        /// classification of the spec; `display` the host's display unit for it, or null.
        /// </summary>
        public JToken Value(string parameter, JToken raw, string spec, string quantity, DisplayUnitFact display)
        {
            if (raw == null || (raw.Type != JTokenType.Float && raw.Type != JTokenType.Integer)) return raw;
            if (quantity == SumUnitRules.Identifier) return raw;
            if (quantity == SumUnitRules.Unitless) { Record(parameter, new Unit { Label = "unitless", Quantity = quantity, Spec = spec }); return raw; }
            bool measurable = quantity == SumUnitRules.Length || quantity == SumUnitRules.Area ||
                              quantity == SumUnitRules.Volume || quantity == SumUnitRules.Measurable;
            if (!measurable || raw.Type != JTokenType.Float)
            {
                Record(parameter, new Unit { Label = "unknown (raw value)", Quantity = quantity ?? SumUnitRules.Unknown, Spec = spec });
                return raw;
            }
            if (display == null || Math.Abs(display.Offset) > 1e-12)
            {
                Record(parameter, new Unit
                {
                    Label = "Revit internal units of " + (string.IsNullOrEmpty(spec) ? "an unknown spec" : spec) +
                            (display == null ? " (no display unit could be read)" : " (display unit '" + display.Label + "' has an offset)"),
                    Quantity = quantity, Spec = spec
                });
                return raw;
            }
            Record(parameter, new Unit { Label = display.Label, UnitTypeId = display.UnitTypeId, Quantity = quantity, Spec = spec, Converted = true });
            return Math.Round(raw.Value<double>() * display.Factor, 9);
        }

        private void Record(string parameter, Unit u)
        {
            if (!_byParameter.TryGetValue(parameter, out var units)) _byParameter[parameter] = units = new Dictionary<string, Unit>(StringComparer.Ordinal);
            if (units.TryGetValue(u.Key, out Unit seen)) seen.Values++;
            else { u.Values = 1; units[u.Key] = u; }
        }

        public bool Any => _byParameter.Count > 0;

        public JObject ToJson()
        {
            var o = new JObject();
            foreach (var p in _byParameter.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<Unit> units = p.Value.Values.ToList();
                if (units.Count == 1) o[p.Key] = Json(units[0]);
                else o[p.Key] = new JObject
                {
                    ["mixed"] = true,
                    ["units"] = new JArray(units.Select(u => (JToken)Json(u))),
                    ["note"] = "rows hold this parameter under different specs; each value was converted by its own spec, " +
                               "so values in different units sit under one key. Narrow the query before adding them."
                };
            }
            return o;
        }

        private static JObject Json(Unit u)
        {
            var j = new JObject
            {
                ["unit"] = u.Label, ["quantity"] = u.Quantity, ["converted"] = u.Converted, ["values"] = u.Values
            };
            if (u.UnitTypeId != null) j["unit_type_id"] = u.UnitTypeId;
            if (!string.IsNullOrEmpty(u.Spec)) j["spec"] = u.Spec;
            return j;
        }

        public const string Means =
            "Compact numbers are in the unit named here per parameter: measurable values are converted to the host " +
            "document's display unit (what Revit shows); converted=false means the value is Revit's raw internal number. " +
            "parameter_format=full returns raw and display side by side.";
    }
}
