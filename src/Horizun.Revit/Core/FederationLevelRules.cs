// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FEDERATION CHECK, rule levels_match - the Revit-free half.
//
// Every level of every LOADED link is brought into host coordinates through the
// instance's total transform (the Revit half does that) and compared with the
// host's levels: same name at a different height is elevation_differs, a host
// level at the same height under another name is name_differs, and neither is
// no_host_level. A link that could not be read is not_read and never counts as
// matching - an unloaded link has no levels to compare, which is not the same as
// levels that agree. Host levels a link does not carry are LISTED, not judged: a
// discipline model need not repeat every architectural level.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class FederationLevelFact
    {
        public long Id;
        public string Name;
        /// <summary>Elevation in mm in HOST internal coordinates (a link's level already through its total transform).</summary>
        public double ElevationMm;
    }

    public sealed class FederationLevelInput
    {
        public List<FederationLevelFact> Host = new List<FederationLevelFact>();
        /// <summary>Link instance id -> its levels in host coordinates. Absent = not read.</summary>
        public Dictionary<long, List<FederationLevelFact>> ByLink = new Dictionary<long, List<FederationLevelFact>>();
        /// <summary>Link instance id -> why its levels could not be read.</summary>
        public Dictionary<long, string> WhyNotRead = new Dictionary<long, string>();
    }

    public static partial class FederationCheckRules
    {
        /// <summary>Null when levels_match is absent or usable, else the refusal.</summary>
        public static string ValidateLevelsMatch(JToken t)
        {
            if (t == null || t.Type == JTokenType.Boolean) return null;
            if (!(t is JObject o)) return "levels_match must be true, false or {tolerance_mm}.";
            foreach (JProperty p in o.Properties())
                if (p.Name != "tolerance_mm") return "levels_match: unknown key '" + p.Name + "'. Known: tolerance_mm.";
            JToken tol = o["tolerance_mm"];
            if (tol != null && ((tol.Type != JTokenType.Integer && tol.Type != JTokenType.Float) || (double)tol < 0))
                return "levels_match.tolerance_mm must be a number >= 0.";
            return null;
        }

        /// <summary>A rule key that is absent, null or false asks for nothing.</summary>
        public static bool Off(JToken t) => t == null || t.Type == JTokenType.Null || (t.Type == JTokenType.Boolean && !(bool)t);

        public static bool LevelsRequested(JObject rules)
        {
            JToken t = rules?["levels_match"];
            return t is JObject || (t != null && t.Type == JTokenType.Boolean && (bool)t);
        }

        /// <summary>One row per link instance; differ/notRead feed the verdict.</summary>
        public static JArray EvaluateLevels(JObject rules, IList<FederationLinkFact> links, FederationLevelInput input,
                                            double defaultToleranceMm, int maxItems, out int differ, out int notRead)
        {
            differ = 0; notRead = 0;
            double tol = (rules["levels_match"] as JObject)?.Value<double?>("tolerance_mm") ?? defaultToleranceMm;
            List<FederationLevelFact> host = input?.Host ?? new List<FederationLevelFact>();
            var rows = new JArray();
            foreach (FederationLinkFact l in links)
            {
                var row = new JObject { ["instance_id"] = l.InstanceId, ["title"] = l.Title, ["tolerance_mm"] = tol };
                List<FederationLevelFact> mine = null;
                if (input == null || !input.ByLink.TryGetValue(l.InstanceId, out mine) || mine == null)
                {
                    string why = null;
                    input?.WhyNotRead.TryGetValue(l.InstanceId, out why);
                    row["state"] = "not_read";
                    row["reason"] = why ?? "the link is not loaded: there is no link document to read its levels from.";
                    notRead++; rows.Add(row); continue;
                }
                if (mine.Count == 0)
                {
                    row["state"] = "not_read";
                    row["reason"] = "the link document was read but carries no level: there is nothing to compare.";
                    notRead++; rows.Add(row); continue;
                }
                var mismatches = new JArray();
                int matched = 0;
                var accountedHost = new HashSet<long>();
                foreach (FederationLevelFact lv in mine.OrderBy(x => x.ElevationMm))
                {
                    // Names compare ordinally: "Level 1" and "LEVEL 1" are two names in a
                    // federation, and a naming inconsistency is exactly what this rule reports.
                    FederationLevelFact byName = host.FirstOrDefault(h => string.Equals(h.Name, lv.Name, StringComparison.Ordinal));
                    if (byName != null)
                    {
                        accountedHost.Add(byName.Id);
                        double delta = Math.Abs(lv.ElevationMm - byName.ElevationMm);
                        if (delta <= tol) { matched++; continue; }
                        mismatches.Add(Mismatch("elevation_differs", lv, new[] { byName }, delta));
                        continue;
                    }
                    var atHeight = host.Where(h => Math.Abs(h.ElevationMm - lv.ElevationMm) <= tol).ToList();
                    foreach (var h in atHeight) accountedHost.Add(h.Id);
                    mismatches.Add(atHeight.Count > 0
                        ? Mismatch("name_differs", lv, atHeight, atHeight.Min(h => Math.Abs(h.ElevationMm - lv.ElevationMm)))
                        : Mismatch("no_host_level", lv, new FederationLevelFact[0], null));
                }
                row["state"] = mismatches.Count == 0 ? "matches" : "differs";
                row["levels_compared"] = mine.Count;
                row["levels_matching"] = matched;
                row["mismatches"] = new JArray(mismatches.Take(maxItems));
                row["host_levels_not_in_link"] = new JArray(host.Where(h => !accountedHost.Contains(h.Id))
                    .OrderBy(h => h.ElevationMm).Take(maxItems).Select(h => h.Name));
                if (mismatches.Count > 0) differ++;
                rows.Add(row);
            }
            return rows;
        }

        private static JObject Mismatch(string state, FederationLevelFact lv, IList<FederationLevelFact> hostLevels, double? deltaMm) =>
            new JObject
            {
                ["state"] = state, ["link_level"] = lv.Name, ["link_level_id"] = lv.Id,
                ["link_elevation_mm"] = Math.Round(lv.ElevationMm, 1),
                ["host_levels"] = new JArray(hostLevels.Select(h => new JObject
                {
                    ["name"] = h.Name, ["id"] = h.Id, ["elevation_mm"] = Math.Round(h.ElevationMm, 1)
                })),
                ["delta_mm"] = deltaMm == null ? null : (JToken)Math.Round(deltaMm.Value, 1)
            };
    }
}
