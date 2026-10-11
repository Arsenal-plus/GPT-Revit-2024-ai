using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Numeric comparison for geometric snapshots; flags and ids remain exact.
    public static class StateComparison
    {
        public static JArray Differences(JToken expected, JToken actual, double lengthTolerance = 1e-6, double directionTolerance = 1e-9)
        {
            var rows = new JArray();
            Compare(expected, actual, "$", lengthTolerance, directionTolerance, rows);
            return rows;
        }
        private static void Compare(JToken a, JToken b, string path, double length, double direction, JArray rows)
        {
            if (rows.Count >= 64) return;
            if (a is JObject ao && b is JObject bo)
            {
                foreach (string key in ao.Properties().Select(p => p.Name).Union(bo.Properties().Select(p => p.Name)))
                    Compare(ao[key], bo[key], path + "." + key, length, direction, rows);
                return;
            }
            if (a is JArray aa && b is JArray ba && aa.Count == ba.Count)
            {
                for (int i = 0; i < aa.Count; i++) Compare(aa[i], ba[i], path + "[" + i + "]", length, direction, rows);
                return;
            }
            double? delta = null;
            double tolerance = path.Contains(".basis_") || path.Contains(".up[") || path.Contains(".forward[") ? direction : length;
            if (a != null && b != null && (a.Type == JTokenType.Float || b.Type == JTokenType.Float) &&
                (a.Type == JTokenType.Float || a.Type == JTokenType.Integer) && (b.Type == JTokenType.Float || b.Type == JTokenType.Integer))
            {
                double x = a.Value<double>(), y = b.Value<double>();
                if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsInfinity(x) && !double.IsInfinity(y))
                {
                    delta = Math.Abs(x - y);
                    if (delta <= tolerance) return;
                }
            }
            else if (JToken.DeepEquals(a, b)) return;
            rows.Add(new JObject { ["field"] = path, ["expected"] = a?.DeepClone(), ["actual"] = b?.DeepClone(),
                ["absolute_difference"] = delta, ["tolerance"] = delta.HasValue ? (JToken)tolerance : null });
        }
    }
}
