// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ANALYSIS READS, AS REVIT COMPUTED THEM AND NOTHING MORE.
//
// Two read-only questions share this file because they share one trap: a
// number that was never computed looks exactly like a number that was.
//
// MEP. A duct system whose type is set to calculate nothing still publishes
// sections, and their flow, velocity and pressure loss read as 0. A report that
// compared those zeros against a velocity limit would call the system "ok" -
// it would be judging a calculation nobody ran. So the calculation level is
// read FIRST and decides which numbers may be judged at all: None and
// Performance are "not calculated", never "ok"; Flow calculates flow and not
// pressure; only All publishes both. A limit the caller gave whose value could
// not be read is NAMED as unmeasured, never counted as a pass.
//
// STRUCTURE. An analytical member end that no other analytical curve reaches
// is a gap in the analytical model. It is measured to the nearest point ON
// every other curve, not to the other curves' end nodes: a secondary beam that
// frames into the middle of a girder is connected, and a node-to-node test
// would flag every one of them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class AnalysisReadRules
    {
        public const string Calculated = "calculated";
        public const string FlowOnly = "flow_only";
        public const string NotCalculated = "not_calculated";
        public const string Unreadable = "unreadable";

        /// <summary>
        /// The status a system's SystemCalculationLevel allows us to claim. Volume
        /// claims neither flow nor pressure here: it is not a level whose flow
        /// numbers this bridge has seen proved, and under-claiming is the honest
        /// failure. An unknown or unread level is unreadable, not calculated.
        /// </summary>
        public static string CalculationStatus(string level)
        {
            switch (level)
            {
                case "All": return Calculated;
                case "Flow": return FlowOnly;
                case "None":
                case "Performance":
                case "Volume": return NotCalculated;
                default: return Unreadable;
            }
        }

        public static bool FlowClaimed(string status) => status == Calculated || status == FlowOnly;
        public static bool PressureClaimed(string status) => status == Calculated;

        public static readonly string[] LimitKeys = { "max_velocity_m_s", "max_pressure_loss_pa", "max_friction_pa_per_m" };

        /// <summary>
        /// Parse the caller's limits. Unknown keys are refused rather than ignored:
        /// a misspelt limit silently dropped is a check the caller believes ran.
        /// Returns an error message, or null when every key was understood.
        /// </summary>
        public static string ParseLimits(JToken token, out Dictionary<string, double> limits)
        {
            limits = new Dictionary<string, double>(StringComparer.Ordinal);
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JObject o)) return "limits must be an object keyed by " + string.Join(", ", LimitKeys) + ".";
            foreach (JProperty p in o.Properties())
            {
                if (Array.IndexOf(LimitKeys, p.Name) < 0)
                    return "limits carries '" + p.Name + "'; the known limits are " + string.Join(", ", LimitKeys) + ".";
                if (p.Value.Type != JTokenType.Integer && p.Value.Type != JTokenType.Float)
                    return "limits." + p.Name + " must be a number.";
                double v = p.Value.Value<double>();
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
                    return "limits." + p.Name + " must be a positive number.";
                limits[p.Name] = v;
            }
            return null;
        }

        public sealed class SectionReading
        {
            public int Number;
            public double? VelocityMs;
            public double? PressureLossPa;
            public double? FrictionPaPerM;
        }

        /// <summary>
        /// The limits this section exceeds. A limit whose value is unreadable - or
        /// whose quantity the calculation level does not claim - goes into
        /// <paramref name="unmeasured"/> as "section#limit": it was not checked,
        /// and a list of breaches that omitted it would read as a pass.
        /// </summary>
        public static JArray Breaches(SectionReading s, string status, IDictionary<string, double> limits,
                                      List<string> unmeasured)
        {
            var found = new JArray();
            if (limits == null) return found;
            foreach (KeyValuePair<string, double> limit in limits)
            {
                double? value;
                bool claimed;
                switch (limit.Key)
                {
                    case "max_velocity_m_s": value = s.VelocityMs; claimed = FlowClaimed(status); break;
                    case "max_pressure_loss_pa": value = s.PressureLossPa; claimed = PressureClaimed(status); break;
                    default: value = s.FrictionPaPerM; claimed = PressureClaimed(status); break;
                }
                if (!claimed || !value.HasValue)
                {
                    unmeasured?.Add(s.Number + "#" + limit.Key);
                    continue;
                }
                if (value.Value > limit.Value)
                    found.Add(new JObject
                    {
                        ["limit"] = limit.Key,
                        ["limit_value"] = limit.Value,
                        ["measured"] = Math.Round(value.Value, 3)
                    });
            }
            return found;
        }

        // ------------------------------------------------------ analytical nodes

        /// <summary>An analytical curve as a polyline in millimetres.</summary>
        public sealed class AnalyticalPolyline
        {
            public long ElementId;
            public List<double[]> Points = new List<double[]>();
        }

        public sealed class NodeGap
        {
            public long ElementId;
            public int End;
            public double[] Point;
            public double? NearestMm;
            public long? NearestElementId;
        }

        /// <summary>
        /// Brute force costs ends x segments distance checks. Above this bound the
        /// check is NOT run and the caller is told to narrow it - a gap check that
        /// silently sampled would report the gaps it happened to look at.
        /// </summary>
        public const long MaxPairChecks = 50000000;

        public static long PairChecks(IList<AnalyticalPolyline> members, IList<AnalyticalPolyline> targets)
        {
            long ends = 0, segments = 0;
            foreach (AnalyticalPolyline m in members) if (m.Points.Count >= 2) ends += 2;
            foreach (AnalyticalPolyline t in targets) segments += Math.Max(0, t.Points.Count - 1);
            return ends * segments;
        }

        /// <summary>
        /// Every member end farther than <paramref name="toleranceMm"/> from every
        /// segment of every OTHER element in <paramref name="targets"/>, with the
        /// nearest distance it did find (null when there is no other element).
        /// </summary>
        public static List<NodeGap> NodeGaps(IList<AnalyticalPolyline> members, IList<AnalyticalPolyline> targets,
                                             double toleranceMm)
        {
            var gaps = new List<NodeGap>();
            foreach (AnalyticalPolyline m in members)
            {
                if (m.Points.Count < 2) continue;
                for (int end = 0; end < 2; end++)
                {
                    double[] p = end == 0 ? m.Points[0] : m.Points[m.Points.Count - 1];
                    double best = double.MaxValue;
                    long? bestId = null;
                    foreach (AnalyticalPolyline t in targets)
                    {
                        if (t.ElementId == m.ElementId) continue;
                        for (int i = 0; i + 1 < t.Points.Count; i++)
                        {
                            double d = PointSegment(p, t.Points[i], t.Points[i + 1]);
                            if (d < best) { best = d; bestId = t.ElementId; }
                        }
                    }
                    if (bestId.HasValue && best <= toleranceMm) continue;
                    gaps.Add(new NodeGap
                    {
                        ElementId = m.ElementId,
                        End = end,
                        Point = p,
                        NearestMm = bestId.HasValue ? Math.Round(best, 2) : (double?)null,
                        NearestElementId = bestId
                    });
                }
            }
            return gaps;
        }

        public static double PointSegment(double[] p, double[] a, double[] b)
        {
            double abx = b[0] - a[0], aby = b[1] - a[1], abz = b[2] - a[2];
            double apx = p[0] - a[0], apy = p[1] - a[1], apz = p[2] - a[2];
            double len2 = abx * abx + aby * aby + abz * abz;
            double t = len2 <= 0 ? 0 : (apx * abx + apy * aby + apz * abz) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = apx - t * abx, dy = apy - t * aby, dz = apz - t * abz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
