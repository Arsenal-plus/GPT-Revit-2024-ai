// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit-free half of horizun_transform_elements operation=edit_sketch: the
// arithmetic that decides what a floor/ceiling/opening boundary SHOULD look like
// after the edit and whether the model agrees afterwards. Everything here works
// in the sketch plane's own 2D coordinates, in millimetres, so it can be proved
// without a building.
//
// WHY LOOPS ARE COMPARED AS CYCLES, IN EITHER DIRECTION, AS A MULTISET: Revit is
// free to start a re-read loop at any vertex, to walk it the other way round and
// to list the loops of a profile in its own order. None of that is a different
// boundary, so none of it may fail a verification - and a vertex that really
// moved still does, because every vertex must land within tolerance.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A point in a sketch plane's own coordinates, in mm.</summary>
    public struct SketchPt
    {
        public double X, Y;
        public SketchPt(double x, double y) { X = x; Y = y; }
        public double DistanceTo(SketchPt o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    }

    /// <summary>One curve of a profile loop, by its two ends (and its midpoint, which tells two arcs apart).</summary>
    public struct SketchSegment
    {
        public SketchPt A, B, Mid;
        public SketchSegment(SketchPt a, SketchPt b, SketchPt mid) { A = a; B = b; Mid = mid; }
    }

    /// <summary>A step of a chained loop: which segment, and whether it is walked B to A.</summary>
    public struct ChainStep
    {
        public int Segment;
        public bool Reversed;
        public ChainStep(int segment, bool reversed) { Segment = segment; Reversed = reversed; }
    }

    public static class SketchEditRules
    {
        /// <summary>How close two sketch points must be to be the same vertex, in mm.</summary>
        public const double MatchToleranceMm = 0.5;
        /// <summary>
        /// The shortest edge this operation will draw, in mm. Revit's own short-curve
        /// tolerance is below a millimetre; an edge shorter than this is a typo far more
        /// often than a design, and the rehearsal would refuse it anyway, less legibly.
        /// </summary>
        public const double MinimumSegmentMm = 1.0;
        /// <summary>How far a requested point may sit off the sketch plane before it is refused, in mm.</summary>
        public const double OffPlaneToleranceMm = 1.0;

        /// <summary>Shoelace area in mm2, positive for a counter-clockwise loop.</summary>
        public static double SignedArea(IList<SketchPt> loop)
        {
            if (loop == null || loop.Count < 3) return 0;
            double s = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                SketchPt a = loop[i], b = loop[(i + 1) % loop.Count];
                s += a.X * b.Y - b.X * a.Y;
            }
            return s / 2.0;
        }

        public static bool PointInPolygon(SketchPt p, IList<SketchPt> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                SketchPt a = poly[i], b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// The area a profile encloses, in mm2, with nesting decided by containment rather
        /// than by size: a loop inside an even number of others is solid, inside an odd
        /// number it is a hole. A floor of two separate islands is two solids, not an
        /// island with a bigger hole.
        /// </summary>
        public static double NetArea(IList<IList<SketchPt>> loops)
        {
            if (loops == null) return 0;
            double total = 0;
            for (int i = 0; i < loops.Count; i++)
            {
                if (loops[i] == null || loops[i].Count < 3) continue;
                double a = Math.Abs(SignedArea(loops[i]));
                total += Depth(loops, i) % 2 == 0 ? a : -a;
            }
            return total;
        }

        /// <summary>How many OTHER loops contain loop i, tested at its first edge's midpoint: even is solid, odd a hole.</summary>
        public static int Depth(IList<IList<SketchPt>> loops, int i)
        {
            if (loops == null || i < 0 || i >= loops.Count || loops[i] == null || loops[i].Count < 3) return 0;
            SketchPt probe = Centroidish(loops[i]);
            int depth = 0;
            for (int j = 0; j < loops.Count; j++)
                if (j != i && loops[j] != null && loops[j].Count >= 3 && PointInPolygon(probe, loops[j])) depth++;
            return depth;
        }

        /// <summary>
        /// Null when every loop keeps its role across an edit - solid or hole, by the same
        /// containment parity NetArea uses; otherwise the first loop whose role changes, named.
        /// A hole the new boundary leaves outside would become a solid island (slab where the
        /// opening was) and an island a grown boundary swallows would become a hole: neither is
        /// a boundary edit, and the area cannot tell, because it agrees with the new sketch.
        /// </summary>
        public static string RoleChange(IList<IList<SketchPt>> before, IList<IList<SketchPt>> after)
        {
            if (before == null || after == null || before.Count != after.Count) return "the number of loops changed.";
            for (int i = 0; i < before.Count; i++)
            {
                bool holeBefore = Depth(before, i) % 2 == 1, holeAfter = Depth(after, i) % 2 == 1;
                if (holeBefore != holeAfter)
                    return "loop " + i + " would turn from " + (holeBefore ? "a hole into a solid island" : "a solid into a hole") +
                           ": the edit carries a boundary across it. Keep every loop inside or outside the others as it was.";
            }
            return null;
        }

        // The midpoint of the first edge nudged inwards would be exact; a vertex is not
        // (it may sit ON another loop's edge). The first edge's midpoint is on this
        // loop's own boundary and, for loops that do not touch, strictly inside or
        // outside every other loop - which is all the containment test needs.
        private static SketchPt Centroidish(IList<SketchPt> loop) =>
            new SketchPt((loop[0].X + loop[1].X) / 2.0, (loop[0].Y + loop[1].Y) / 2.0);

        /// <summary>Drops a closing point that repeats the first one.</summary>
        public static List<SketchPt> Normalise(IList<SketchPt> loop)
        {
            var list = (loop ?? new List<SketchPt>()).ToList();
            if (list.Count > 3 && list[0].DistanceTo(list[list.Count - 1]) < MatchToleranceMm) list.RemoveAt(list.Count - 1);
            return list;
        }

        /// <summary>
        /// Null when the polygon is drawable as a sketch loop; otherwise the reason, in words.
        /// Refused: fewer than three vertices, an edge shorter than MinimumSegmentMm, two
        /// non-adjacent edges that touch or cross, a loop that encloses no area.
        /// </summary>
        public static string ValidateLoop(IList<SketchPt> loop)
        {
            if (loop == null || loop.Count < 3) return "a loop needs at least three distinct vertices.";
            int n = loop.Count;
            for (int i = 0; i < n; i++)
                if (loop[i].DistanceTo(loop[(i + 1) % n]) < MinimumSegmentMm)
                    return "the edge from vertex " + i + " to vertex " + ((i + 1) % n) + " is shorter than " + MinimumSegmentMm + " mm.";
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;   // adjacent edges share a vertex by design
                    if (SegmentsTouch(loop[i], loop[(i + 1) % n], loop[j], loop[(j + 1) % n]))
                        return "edge " + i + " and edge " + j + " touch or cross: the loop intersects itself.";
                }
            if (Math.Abs(SignedArea(loop)) < 1.0) return "the loop encloses no area.";
            return null;
        }

        /// <summary>
        /// A vertex move on a loop that holds arcs, held on the loop's tessellated outline (the
        /// moved vertex at index k): both edges that meet there at least MinimumSegmentMm, neither
        /// touching a segment it shares no vertex with, and the outline still enclosing area.
        /// ValidateLoop cannot be used there - an arc's chords are sub-millimetre by design - and
        /// only these two edges changed, so only they can make the loop cross itself.
        /// </summary>
        public static string ValidateMovedVertex(IList<SketchPt> outline, int k)
        {
            int n = outline == null ? 0 : outline.Count;
            if (n < 3 || k < 0 || k >= n) return "the moved vertex is not on the loop.";
            int prev = (k - 1 + n) % n, next = (k + 1) % n;
            if (outline[prev].DistanceTo(outline[k]) < MinimumSegmentMm || outline[k].DistanceTo(outline[next]) < MinimumSegmentMm)
                return "an edge at the moved vertex would be shorter than " + MinimumSegmentMm + " mm.";
            foreach (int e in new[] { prev, k })
                for (int j = 0; j < n; j++)
                {
                    if (j == e || j == (e - 1 + n) % n || j == (e + 1) % n) continue;   // shares a vertex with e
                    if (SegmentsTouch(outline[e], outline[(e + 1) % n], outline[j], outline[(j + 1) % n]))
                        return "an edge at the moved vertex meets segment " + j + " of the loop's outline: the loop would cross itself.";
                }
            if (Math.Abs(SignedArea(outline)) < 1.0) return "the loop would enclose no area.";
            return null;
        }

        /// <summary>
        /// For a loop redrawn with the same number of vertices: map[i] is the index in the new
        /// loop that old vertex i goes to - the rotation and direction with the least total
        /// displacement - so each existing edge is reshaped onto the new edge nearest to where it
        /// was, and what that curve carries stays on its side. Null when the counts differ.
        /// </summary>
        public static int[] AlignCyclic(IList<SketchPt> oldLoop, IList<SketchPt> newLoop)
        {
            if (oldLoop == null || newLoop == null || oldLoop.Count != newLoop.Count || oldLoop.Count == 0) return null;
            int n = oldLoop.Count;
            int[] best = null; double bestCost = double.MaxValue;
            for (int offset = 0; offset < n; offset++)
                for (int dir = 1; dir >= -1; dir -= 2)
                {
                    double cost = 0;
                    for (int i = 0; i < n; i++) cost += oldLoop[i].DistanceTo(newLoop[((offset + dir * i) % n + n) % n]);
                    if (cost < bestCost - 1e-9)
                    {
                        bestCost = cost;
                        best = new int[n];
                        for (int i = 0; i < n; i++) best[i] = ((offset + dir * i) % n + n) % n;
                    }
                }
            return best;
        }

        /// <summary>
        /// Null when loop `index` neither touches nor crosses any OTHER loop of the profile;
        /// otherwise which loop and which edges meet. Revit refuses such a sketch only when it
        /// is FINISHED - the apply's scope commit, after a dry run has already Cancelled - so the
        /// plan holds the edited loop to it first, rather than let a rehearsal pass an edit the
        /// apply must refuse. Arcs are held by their tessellation; Revit's commit stays the last word.
        /// </summary>
        public static string CrossesOtherLoops(IList<IList<SketchPt>> loops, int index)
        {
            if (loops == null || index < 0 || index >= loops.Count || loops[index] == null) return null;
            IList<SketchPt> a = loops[index];
            for (int j = 0; j < loops.Count; j++)
            {
                IList<SketchPt> b = loops[j];
                if (j == index || b == null || b.Count < 2) continue;
                for (int i = 0; i < a.Count; i++)
                    for (int k = 0; k < b.Count; k++)
                        if (SegmentsTouch(a[i], a[(i + 1) % a.Count], b[k], b[(k + 1) % b.Count]))
                            return "it touches or crosses loop " + j + " (its edge " + i + " meets that loop's edge " + k + "); loops of one sketch must stay apart.";
            }
            return null;
        }

        private static double Cross(SketchPt o, SketchPt a, SketchPt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static bool OnSegment(SketchPt p, SketchPt a, SketchPt b) =>
            Math.Min(a.X, b.X) - 1e-9 <= p.X && p.X <= Math.Max(a.X, b.X) + 1e-9 &&
            Math.Min(a.Y, b.Y) - 1e-9 <= p.Y && p.Y <= Math.Max(a.Y, b.Y) + 1e-9;

        public static bool SegmentsTouch(SketchPt p1, SketchPt p2, SketchPt q1, SketchPt q2)
        {
            double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            const double eps = 1e-9;
            if (Math.Abs(d1) < eps && OnSegment(p1, q1, q2)) return true;
            if (Math.Abs(d2) < eps && OnSegment(p2, q1, q2)) return true;
            if (Math.Abs(d3) < eps && OnSegment(q1, p1, p2)) return true;
            if (Math.Abs(d4) < eps && OnSegment(q2, p1, p2)) return true;
            return false;
        }

        /// <summary>
        /// Orders unordered segments into closed loops, end to start. Null (with the reason)
        /// when some segment leaves a loop open: a profile that does not close is not one this
        /// operation can describe, let alone edit.
        /// </summary>
        public static List<List<ChainStep>> Chain(IList<SketchSegment> segments, double tolMm, out string problem)
        {
            problem = null;
            var loops = new List<List<ChainStep>>();
            if (segments == null || segments.Count == 0) { problem = "the profile has no curves."; return null; }
            var used = new bool[segments.Count];
            for (int start = 0; start < segments.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;
                var loop = new List<ChainStep> { new ChainStep(start, false) };
                SketchPt origin = segments[start].A, cursor = segments[start].B;
                int guard = 0;
                while (cursor.DistanceTo(origin) >= tolMm)
                {
                    if (++guard > segments.Count) { problem = "the profile's curves do not close into loops."; return null; }
                    int next = -1; bool rev = false;
                    for (int k = 0; k < segments.Count && next < 0; k++)
                    {
                        if (used[k]) continue;
                        if (segments[k].A.DistanceTo(cursor) < tolMm) { next = k; rev = false; }
                        else if (segments[k].B.DistanceTo(cursor) < tolMm) { next = k; rev = true; }
                    }
                    if (next < 0) { problem = "a profile loop is open near (" + Math.Round(cursor.X, 1) + ", " + Math.Round(cursor.Y, 1) + ") mm."; return null; }
                    used[next] = true;
                    loop.Add(new ChainStep(next, rev));
                    cursor = rev ? segments[next].A : segments[next].B;
                }
                loops.Add(loop);
            }
            return loops;
        }

        /// <summary>The vertices of a chained loop: the start of every step.</summary>
        public static List<SketchPt> Vertices(IList<SketchSegment> segments, IList<ChainStep> loop) =>
            loop.Select(s => s.Reversed ? segments[s.Segment].B : segments[s.Segment].A).ToList();

        /// <summary>Two segments are the same curve when their ends match (either way round) and their midpoints do.</summary>
        public static bool SameSegment(SketchSegment x, SketchSegment y, double tolMm)
        {
            bool ends = (x.A.DistanceTo(y.A) < tolMm && x.B.DistanceTo(y.B) < tolMm) ||
                        (x.A.DistanceTo(y.B) < tolMm && x.B.DistanceTo(y.A) < tolMm);
            return ends && x.Mid.DistanceTo(y.Mid) < tolMm;
        }

        /// <summary>The same closed polygon: same vertex count, some rotation, either direction.</summary>
        public static bool SameLoop(IList<SketchPt> a, IList<SketchPt> b, double tolMm)
        {
            if (a == null || b == null || a.Count != b.Count || a.Count == 0) return false;
            int n = a.Count;
            for (int offset = 0; offset < n; offset++)
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    bool all = true;
                    for (int i = 0; i < n && all; i++)
                    {
                        int j = ((offset + dir * i) % n + n) % n;
                        all = a[i].DistanceTo(b[j]) < tolMm;
                    }
                    if (all) return true;
                }
            return false;
        }

        /// <summary>
        /// Null when every expected loop matches a DISTINCT actual loop and nothing is left
        /// over; otherwise what disagrees. Order-free, because Revit's is its own.
        /// </summary>
        public static string MatchLoops(IList<IList<SketchPt>> expected, IList<IList<SketchPt>> actual, double tolMm)
        {
            if (expected == null || actual == null) return "a loop set is missing.";
            if (expected.Count != actual.Count)
                return "expected " + expected.Count + " loop(s) and the model has " + actual.Count + ".";
            var taken = new bool[actual.Count];
            for (int i = 0; i < expected.Count; i++)
            {
                int hit = -1;
                for (int j = 0; j < actual.Count && hit < 0; j++)
                    if (!taken[j] && SameLoop(expected[i], actual[j], tolMm)) hit = j;
                if (hit < 0) return "expected loop " + i + " (" + expected[i].Count + " vertices) has no match in the model.";
                taken[hit] = true;
            }
            return null;
        }

        /// <summary>
        /// Finds the ONE vertex within tolerance of p across all loops. Zero matches and more
        /// than one are both refusals: moving "the vertex near here" when two qualify would be
        /// this build choosing which.
        /// </summary>
        public static bool FindVertex(IList<IList<SketchPt>> loops, SketchPt p, double tolMm,
                                      out int loopIndex, out int vertexIndex, out string problem)
        {
            loopIndex = -1; vertexIndex = -1; problem = null;
            int hits = 0;
            for (int l = 0; l < loops.Count; l++)
                for (int v = 0; v < loops[l].Count; v++)
                    if (loops[l][v].DistanceTo(p) < tolMm) { hits++; loopIndex = l; vertexIndex = v; }
            if (hits == 1) return true;
            problem = hits == 0
                ? "no vertex of the boundary lies within " + tolMm + " mm of start (" + Math.Round(p.X, 1) + ", " + Math.Round(p.Y, 1) + " in the sketch plane); the current boundary is listed with this refusal."
                : hits + " vertices lie within " + tolMm + " mm of start; name one unambiguously.";
            loopIndex = -1; vertexIndex = -1;
            return false;
        }

        /// <summary>Returns a copy of the loop with one vertex replaced.</summary>
        public static List<SketchPt> MoveVertex(IList<SketchPt> loop, int vertexIndex, SketchPt to)
        {
            var copy = loop.ToList();
            copy[vertexIndex] = to;
            return copy;
        }

        /// <summary>
        /// Whether the element's own Area parameter can be held to the sketch: only when it
        /// agreed with the sketch BEFORE the edit. A sloped floor, a shape-edited slab or one a
        /// shaft cuts reports an area the sketch alone does not predict, and demanding it would
        /// fail a correct edit; the check is then named not_applicable, never passed.
        /// </summary>
        public static bool AreaAgrees(double measuredM2, double expectedM2) =>
            Math.Abs(measuredM2 - expectedM2) <= Math.Max(0.0005, Math.Abs(expectedM2) * 0.001);
    }
}
