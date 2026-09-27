// -----------------------------------------------------------------------------
// Horizun Revit MCP - egress travel distance, the Revit-free half. Original Horizun code.
//
// horizun_code_check's travel_distance operation (Commands/CodeCheckTravel.cs) asks
// Revit's PathOfTravel service for the routes; everything that DECIDES something
// about those routes lives here, on plain doubles, so it is unit-tested without Revit:
//
//   - which sampled start point in a room is the farthest from its nearest exit,
//   - which declared exit a route ended at,
//   - whether a measured distance passes a caller's "max travel distance <= N m",
//   - and whether a kept PathOfTravel element re-read after the commit is the route
//     that was measured.
//
// WHY SAMPLED START POINTS. PathOfTravel.FindStartsOfLongestPathsFromRooms takes
// destinations only - no room argument - and returns the worst point(s) across the
// WHOLE plan (documented: the plan is tiled and only the longest path's start is
// returned). It is not "the farthest point of room X". For each room this bridge
// therefore routes a small set of candidates - the room's boundary corners pulled
// inward, its location point, and any whole-plan longest-path start that falls in
// the room - and keeps the longest. That number is a LOWER BOUND on the room's true
// farthest travel distance, never the value itself: the sampled points are points a
// person can stand on, so the true worst is at least as long, and it may be longer
// (distance to the nearest of several doors is not convex; a non-convex room hides
// points). A lower bound above the limit is a certain fail. A pass needs an UPPER
// bound under the limit too, and there is one only in a room that is a convex polygon
// of straight walls with no island and every sample routed: any point p in it walks
// straight to a routed sample c inside the room, so travel(p) <= travel(c) + |p - c|,
// and |p - c| over a convex polygon is largest at a vertex (UpperBound). The straight
// walk assumes the room's interior is clear between p and c; that assumption is
// stated in every reply that uses the bound.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A point in plan, in whatever length unit the caller uses consistently.</summary>
    public struct PlanPoint
    {
        public double X, Y;
        public PlanPoint(double x, double y) { X = x; Y = y; }
        public double DistanceTo(PlanPoint o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    }

    public static class EgressTravelRules
    {
        /// <summary>How far each boundary corner is pulled toward the room's own point before routing, in mm.</summary>
        public const double DefaultInsetMm = 300;

        /// <summary>Outcomes, in the requirement-set vocabulary plus the one egress needs.</summary>
        public const string Passes = "passes", Fails = "fails", NotDecidable = "not_decidable",
                            NotAssessable = "not_assessable", Measured = "measured";

        /// <summary>
        /// Each vertex moved toward <paramref name="reference"/> by <paramref name="inset"/>,
        /// or onto it when it is closer than that. A corner exactly on the boundary is where
        /// PathOfTravel may refuse to start (the point sits on the wall's face); a point just
        /// inside is a point a person can stand on. Duplicates (within 1e-9) are dropped.
        /// </summary>
        public static List<PlanPoint> InsetToward(IList<PlanPoint> vertices, PlanPoint reference, double inset)
        {
            var result = new List<PlanPoint>();
            if (vertices == null) return result;
            foreach (PlanPoint v in vertices)
            {
                double d = v.DistanceTo(reference);
                PlanPoint p = d <= inset || d < 1e-12
                    ? reference
                    : new PlanPoint(v.X + (reference.X - v.X) * inset / d, v.Y + (reference.Y - v.Y) * inset / d);
                if (!result.Any(q => q.DistanceTo(p) < 1e-9)) result.Add(p);
            }
            return result;
        }

        /// <summary>Index of the longest routed distance; -1 when none of the candidates routed.</summary>
        public static int FarthestIndex(IList<double?> distances)
        {
            int best = -1;
            if (distances == null) return best;
            for (int i = 0; i < distances.Count; i++)
                if (distances[i].HasValue && (best < 0 || distances[i].Value > distances[best].Value)) best = i;
            return best;
        }

        /// <summary>
        /// Which destination a route ended at: the nearest one within <paramref name="tolerance"/>,
        /// or -1. PathOfTravel.FindEndsOfShortestPaths returns the START point when no path could
        /// be computed, so an end that matches no destination is "no exit reached", not a guess.
        /// </summary>
        public static int NearestIndex(PlanPoint point, IList<PlanPoint> destinations, double tolerance)
        {
            int best = -1;
            double bestD = double.MaxValue;
            if (destinations == null) return best;
            for (int i = 0; i < destinations.Count; i++)
            {
                double d = point.DistanceTo(destinations[i]);
                if (d <= tolerance && d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>Sum of segment lengths; 0 for fewer than two points.</summary>
        public static double Length(IList<PlanPoint> polyline)
        {
            double total = 0;
            if (polyline == null) return total;
            for (int i = 1; i < polyline.Count; i++) total += polyline[i].DistanceTo(polyline[i - 1]);
            return total;
        }

        /// <summary>
        /// One room against the caller's limit. <paramref name="lowerM"/> is the longest routed
        /// sample (the true farthest is at least that); <paramref name="upperM"/> is UpperBound's
        /// number, or null when the room admits none. not_assessable wins over everything (the
        /// measurement does not apply: another level, a stair); no value is not_decidable (never
        /// a pass); no limit is "measured" (a number, no verdict); a lower bound over the limit is
        /// a certain fail; a pass needs the upper bound under the limit; anything else is
        /// not_decidable, because the true farthest point may still exceed it.
        /// </summary>
        public static string Evaluate(double? lowerM, double? upperM, double? maxM, string notAssessableReason)
        {
            if (!string.IsNullOrEmpty(notAssessableReason)) return NotAssessable;
            if (!lowerM.HasValue || double.IsNaN(lowerM.Value)) return NotDecidable;
            if (!maxM.HasValue) return Measured;
            if (lowerM.Value > maxM.Value + 1e-9) return Fails;
            return upperM.HasValue && upperM.Value <= maxM.Value + 1e-9 ? Passes : NotDecidable;
        }

        /// <summary>
        /// True for a simple polygon whose turns all go the same way (collinear vertices allowed).
        /// Fewer than three distinct vertices is not a polygon, so not convex.
        /// </summary>
        public static bool IsConvex(IList<PlanPoint> polygon)
        {
            if (polygon == null || polygon.Count < 3) return false;
            int sign = 0, n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                PlanPoint a = polygon[i], b = polygon[(i + 1) % n], c = polygon[(i + 2) % n];
                double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (Math.Abs(cross) < 1e-9) continue;
                int s = cross > 0 ? 1 : -1;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return sign != 0;
        }

        /// <summary>
        /// An upper bound on the travel distance from ANY point of a convex room: the smallest
        /// routed(c) + max over vertices |v - c| among the routed samples c (all in one unit).
        /// Null when nothing routed or the room has no vertex. Valid only for a convex room whose
        /// interior is clear - the caller checks convexity and says the rest.
        /// </summary>
        public static double? UpperBound(IList<(PlanPoint point, double distance)> routed, IList<PlanPoint> vertices)
        {
            if (routed == null || routed.Count == 0 || vertices == null || vertices.Count == 0) return null;
            double best = double.MaxValue;
            foreach (var (c, d) in routed)
            {
                double reach = vertices.Max(v => v.DistanceTo(c));
                if (d + reach < best) best = d + reach;
            }
            return best;
        }

        /// <summary>
        /// Whether a kept PathOfTravel element, re-read after the commit, is the route that was
        /// measured: same length within max(50 mm, 1 %). Revit may re-solve the path when the
        /// element is created, so an exact match is not expected; a different route is.
        /// </summary>
        public static bool LengthsAgree(double measuredMm, double rereadMm, out double deltaMm)
        {
            deltaMm = Math.Abs(measuredMm - rereadMm);
            return rereadMm > 0 && deltaMm <= Math.Max(50.0, 0.01 * measuredMm);
        }

        /// <summary>Counts per outcome, every outcome present (0 when absent) so a reader never infers from a missing key.</summary>
        public static JObject Summary(IEnumerable<string> outcomes)
        {
            var counts = new JObject
            {
                [Passes] = 0, [Fails] = 0, [NotDecidable] = 0, [NotAssessable] = 0, [Measured] = 0
            };
            foreach (string o in outcomes ?? Enumerable.Empty<string>())
                counts[o] = (counts.Value<int?>(o) ?? 0) + 1;
            return counts;
        }
    }
}
