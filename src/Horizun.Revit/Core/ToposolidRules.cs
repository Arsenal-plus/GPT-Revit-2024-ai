// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Revit-free checks for horizun_create_elements kind='toposolid': whether a set of
// points can be a top surface at all, and which of them the post-commit re-read
// samples. Toposolid.Create triangulates whatever it is given; points on one line,
// or two heights at one plan point, describe no surface, and what Revit does with
// them is not something to discover inside a transaction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class ToposolidRules
    {
        // = the contract's points maxItems (shared with the flex kinds), so the schema and this agree.
        // A larger surface comes from a file: landxml_path, capped at LandXmlTinRules.MaxFilePoints.
        public const int MaxPoints = 100;
        /// <summary>How many input points the post-commit re-read samples at most (the reply names each one).</summary>
        public const int MaxSamples = 50;

        /// <summary>Null when the points can make a surface; otherwise why not. One unit throughout, tolerance included.</summary>
        public static string ValidatePoints(IList<double[]> points, double tolerance, int maxPoints = MaxPoints)
        {
            if (points == null || points.Count < 3) return "points: a toposolid needs at least 3 XYZ points.";
            if (points.Count > maxPoints) return "points: at most " + maxPoints + " points in one toposolid.";
            for (int i = 0; i < points.Count; i++)
            {
                double[] p = points[i];
                if (p == null || p.Length != 3) return "points[" + i + "] must be [x, y, z].";
                if (p.Any(v => double.IsNaN(v) || double.IsInfinity(v))) return "points[" + i + "] is not finite.";
            }
            // Sorted by X and swept: a file brings thousands of points, and a pair is compared
            // only when its X values already agree within the tolerance.
            int[] order = Enumerable.Range(0, points.Count).OrderBy(k => points[k][0]).ToArray();
            for (int u = 0; u < order.Length; u++)
                for (int w = u + 1; w < order.Length && points[order[w]][0] - points[order[u]][0] <= tolerance; w++)
                    if (Math.Abs(points[order[u]][1] - points[order[w]][1]) <= tolerance)
                        return "points[" + Math.Min(order[u], order[w]) + "] and points[" + Math.Max(order[u], order[w]) +
                               "] share one X,Y: a surface has one height per plan point.";
            // Duplicates are refused above, so points[1] is a distinct plan point: some point
            // must stand off the plan line through the first two, or they bound no area.
            double[] a = points[0], b = points[1];
            double dx = b[0] - a[0], dy = b[1] - a[1], len = Math.Sqrt(dx * dx + dy * dy);
            bool off = points.Any(q => Math.Abs(dx * (q[1] - a[1]) - dy * (q[0] - a[0])) / len > tolerance);
            return off ? null : "points all lie on one line in plan: they bound no area.";
        }

        /// <summary>
        /// Indices re-read after the commit, ascending and distinct: every point when there
        /// are at most <paramref name="max"/>; otherwise the lowest, the highest and an even
        /// spread - never more than <paramref name="max"/>.
        /// </summary>
        public static List<int> SampleIndices(IList<double[]> points, int max = MaxSamples)
        {
            int n = points?.Count ?? 0;
            if (n <= max) return Enumerable.Range(0, n).ToList();
            int lo = 0, hi = 0;
            for (int i = 1; i < n; i++) { if (points[i][2] < points[lo][2]) lo = i; if (points[i][2] > points[hi][2]) hi = i; }
            var set = new SortedSet<int> { lo, hi };
            int spread = Math.Max(2, max - 2);
            for (int k = 0; k < spread && set.Count < max; k++) set.Add((int)Math.Round((double)k * (n - 1) / (spread - 1)));
            return set.ToList();
        }
    }
}
