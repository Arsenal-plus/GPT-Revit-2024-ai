// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_mep_routing's `route` operation: a 3-D A* search
// on an orthogonal grid that returns an axis-aligned polyline from a start point
// to an end point, avoiding a set of obstacle boxes (already inflated by the
// caller with clearance + half the run's own size - this file knows nothing
// about pipes, ducts or Revit units, only boxes and points).
//
// WHY THE GRID IS ANCHORED AT THE START POINT, NOT AT THE WORLD ORIGIN. A lattice
// anchored at (0,0,0) would put the start point off-grid almost always (nobody's
// routing points land on round multiples of grid_mm from the Revit origin), and
// snapping the START would silently move the very point the caller asked to leave
// a run at. Anchoring at start makes start exact by construction; only the END
// point may fall off-lattice.
//
// HOW THE OFF-LATTICE END IS REACHED. The end is snapped TOWARD the start on each
// axis (truncation, not rounding), so the residual always points the way the route
// already travels along that axis, and it is ABSORBED into the route's last leg
// along that axis: that leg and every vertex after it shift by the residual (under
// one grid step) and are re-checked against the obstacles. Reviewed defect of the
// first version: residuals closed with separate stubs gave legs a few mm long that
// no elbow fits on, and a NEGATIVE residual (end 2.6 on a 1-unit grid rounded to 3)
// doubled the route back on itself (0 -> 3 -> 2.6, a 180-degree "bend"). Only an
// axis the route never travels (offset under one step, no detour along it) still
// needs a stub, and the minimum-leg rule below refuses it by name when it is short.
//
// MINIMUM LEG LENGTHS. Every bend becomes an elbow, and an elbow trims both runs
// back by its own take-off, so a leg between two bends must hold two take-offs and
// the first/last legs one. The search enforces it in grid steps (the state carries
// the current leg's length) and the finished polyline is re-checked in world units,
// because absorbing the residual can shorten a leg by less than one step.
//
// WHY THE SEARCH BOX GROWS. A detour wider than MarginSteps around start/end used
// to be refused as no_route whatever max_nodes said; now an exhausted box is retried
// at x3 and x9 the margin (same node budget, shared) before refusing, and the
// refusal says when the box, not max_nodes, was the binding limit.
//
// WHY STATE INCLUDES THE ARRIVAL DIRECTION. Cost is length + a per-bend penalty,
// so two paths of equal length are broken by whichever bends less - which the
// search can only see if "have I just turned" is part of what makes two visits to
// the same grid cell different. A state is therefore (cell, direction of the move
// that reached it); Direction.None only ever occurs at the start.
//
// WHY NOT System.Collections.Generic.PriorityQueue<T,P>: this file is compiled,
// unmodified, into Horizun.Revit for Revit 2023/2024 (net48) as well as 2025+ -
// PriorityQueue is .NET 6+ only. The open set below is a small hand-rolled
// binary min-heap instead, portable to net48.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class RouteSearch
    {
        public const double DefaultGrid = 100.0 / 304.8;          // 100 mm, in feet-equivalent world units
        public const double DefaultBendPenaltyFraction = 0.10;    // a bend costs 10% of one grid step
        public const int DefaultMaxNodes = 20000;
        private const double Tolerance = 1e-6;

        public readonly struct Point3
        {
            public readonly double X, Y, Z;
            public Point3(double x, double y, double z) { X = x; Y = y; Z = z; }
            public Point3 Add(double dx, double dy, double dz) => new Point3(X + dx, Y + dy, Z + dz);
            public double DistanceTo(Point3 o) { double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
            public override string ToString() => "(" + X.ToString("R") + "," + Y.ToString("R") + "," + Z.ToString("R") + ")";
        }

        /// <summary>An axis-aligned box, already inflated by the caller (clearance + half the run size).</summary>
        public readonly struct Box3
        {
            public readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public readonly string Name;
            public Box3(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, string name = null)
            {
                MinX = Math.Min(minX, maxX); MaxX = Math.Max(minX, maxX);
                MinY = Math.Min(minY, maxY); MaxY = Math.Max(minY, maxY);
                MinZ = Math.Min(minZ, maxZ); MaxZ = Math.Max(minZ, maxZ);
                Name = name;
            }

            public bool Contains(Point3 p, double eps = Tolerance) =>
                p.X >= MinX - eps && p.X <= MaxX + eps && p.Y >= MinY - eps && p.Y <= MaxY + eps && p.Z >= MinZ - eps && p.Z <= MaxZ + eps;

            /// <summary>True when the axis-aligned segment a-b (which must vary along exactly one axis, or be a
            /// single point) overlaps this box - the only kind of segment an orthogonal route ever draws.</summary>
            public bool IntersectsAxisSegment(Point3 a, Point3 b, double eps = Tolerance)
            {
                double loX = Math.Min(a.X, b.X), hiX = Math.Max(a.X, b.X);
                double loY = Math.Min(a.Y, b.Y), hiY = Math.Max(a.Y, b.Y);
                double loZ = Math.Min(a.Z, b.Z), hiZ = Math.Max(a.Z, b.Z);
                return loX <= MaxX + eps && hiX >= MinX - eps &&
                       loY <= MaxY + eps && hiY >= MinY - eps &&
                       loZ <= MaxZ + eps && hiZ >= MinZ - eps;
            }
        }

        public enum Direction { None = 0, PosX, NegX, PosY, NegY, PosZ, NegZ }

        private static readonly Direction[] AllDirections =
            { Direction.PosX, Direction.NegX, Direction.PosY, Direction.NegY, Direction.PosZ, Direction.NegZ };

        private static Direction Opposite(Direction d)
        {
            switch (d)
            {
                case Direction.PosX: return Direction.NegX;
                case Direction.NegX: return Direction.PosX;
                case Direction.PosY: return Direction.NegY;
                case Direction.NegY: return Direction.PosY;
                case Direction.PosZ: return Direction.NegZ;
                case Direction.NegZ: return Direction.PosZ;
                default: return Direction.None;
            }
        }

        private static void Step(Direction d, out long dx, out long dy, out long dz)
        {
            dx = dy = dz = 0;
            switch (d)
            {
                case Direction.PosX: dx = 1; break;
                case Direction.NegX: dx = -1; break;
                case Direction.PosY: dy = 1; break;
                case Direction.NegY: dy = -1; break;
                case Direction.PosZ: dz = 1; break;
                case Direction.NegZ: dz = -1; break;
            }
        }

        public sealed class Request
        {
            public Point3 Start, End;
            public IList<Box3> Obstacles = new List<Box3>();
            public double GridSize = DefaultGrid;
            public int MaxNodes = DefaultMaxNodes;
            public double? BendPenalty;   // world units; default DefaultBendPenaltyFraction * GridSize
            /// <summary>Optional search bounds (world units). Default: the box of Start/snapped-End, expanded by MarginSteps grid cells.</summary>
            public Box3? SearchBounds;
            public int MarginSteps = 6;
            /// <summary>Without SearchBounds, an exhausted box is retried at MarginSteps x3 and x9 before refusing.</summary>
            public bool GrowBounds = true;
            /// <summary>World units; 0 = none. First/last leg of a bent route (one elbow) and a leg between two bends (two elbows).</summary>
            public double MinEndLeg, MinInteriorLeg;
            /// <summary>Optional soft preference: a node outside [MinZ,MaxZ] costs extra per unit of vertical distance outside the band. Never a hard constraint.</summary>
            public double? PreferredMinZ, PreferredMaxZ;
            public double OutOfBandWeight = 0.5;
        }

        public sealed class Result
        {
            public bool Found;
            public List<Point3> Polyline;
            public double Length;
            public int Bends;
            public int NodesExpanded;
            public string Reason;
            public Box3? BlockingRegion;
            public int MarginStepsUsed;
            /// <summary>True when the search box, not max_nodes, stopped the search.</summary>
            public bool BoundsBinding;
            /// <summary>Set when a leg of the found polyline is shorter than its minimum (0-based leg index).</summary>
            public int? ShortLeg;
            public double ShortLegLength, ShortLegMinimum;
        }

        public static Result Find(Request req)
        {
            if (req == null) return Fail("no request");
            double grid = req.GridSize;
            if (!(grid > 0) || double.IsInfinity(grid)) return Fail("grid_size must be a positive, finite number");
            if (req.MaxNodes <= 0) return Fail("max_nodes must be positive");
            double bendPenalty = req.BendPenalty ?? grid * DefaultBendPenaltyFraction;
            if (bendPenalty < 0) return Fail("bend penalty must not be negative");

            IList<Box3> obstacles = req.Obstacles ?? new List<Box3>();
            if (Blocked(obstacles, req.Start, out Box3 startBlock))
                return Fail("the start point is inside " + Describe(startBlock), startBlock);
            // Symmetric up-front check for the end: without it an end inside an obstacle is
            // only discovered by exhausting the whole search (up to max_nodes) - same refusal,
            // but after the full budget, and naming a region guessed from the straight line.
            if (Blocked(obstacles, req.End, out Box3 endBlock))
                return Fail("no_route: the end point is inside " + Describe(endBlock), endBlock);

            if (req.MinEndLeg < 0 || req.MinInteriorLeg < 0 || double.IsNaN(req.MinEndLeg) || double.IsNaN(req.MinInteriorLeg))
                return Fail("minimum leg lengths must not be negative");

            // Snap End TOWARD Start on the lattice anchored at Start; the residual is absorbed below.
            long ex = SnapTowardStart(req.End.X - req.Start.X, grid);
            long ey = SnapTowardStart(req.End.Y - req.Start.Y, grid);
            long ez = SnapTowardStart(req.End.Z - req.Start.Z, grid);
            Point3 snappedEnd = req.Start.Add(ex * grid, ey * grid, ez * grid);
            int minEndSteps = StepsFor(req.MinEndLeg, grid), minInteriorSteps = StepsFor(req.MinInteriorLeg, grid);

            List<Point3> gridPolyline;
            int nodesExpanded = 0, marginUsed = req.MarginSteps;
            if (ex == 0 && ey == 0 && ez == 0)
            {
                gridPolyline = new List<Point3> { req.Start };
            }
            else
            {
                int[] margins = req.SearchBounds.HasValue || !req.GrowBounds ? new[] { req.MarginSteps }
                    : new[] { req.MarginSteps, req.MarginSteps * 3, req.MarginSteps * 9 };
                AStarResult a = null;
                foreach (int m in margins)
                {
                    Box3 bounds = req.SearchBounds ?? DefaultBounds(req.Start, snappedEnd, grid, m);
                    // Only the obstacles this box can touch: the caller collects for the largest box the
                    // search may grow to, and every step tests every obstacle it is given.
                    a = RunAStar(req.Start, ex, ey, ez, grid, bendPenalty, Within(obstacles, bounds), bounds, req.MaxNodes - nodesExpanded, req, minEndSteps, minInteriorSteps);
                    nodesExpanded += a.NodesExpanded;
                    marginUsed = m;
                    if (a.Found || !a.BoundsExhausted || nodesExpanded >= req.MaxNodes) break;
                }
                if (!a.Found)
                {
                    Box3? blocking = FindBlockingRegion(req.Start, snappedEnd, obstacles);
                    return new Result
                    {
                        Found = false,
                        Reason = (a.Reason ?? "no_route: no orthogonal path connects the two points within the search bounds and max_nodes budget") +
                            (a.BoundsExhausted && !req.SearchBounds.HasValue ? " (the search box, " + marginUsed + " grid steps around start/end, was the binding limit, not max_nodes)" : ""),
                        BlockingRegion = blocking, NodesExpanded = nodesExpanded, MarginStepsUsed = marginUsed, BoundsBinding = a.BoundsExhausted
                    };
                }
                gridPolyline = a.Path;
            }

            // Absorb the residual, axis by axis, into the last leg along that axis.
            List<Point3> path = Simplify(gridPolyline);
            double[] residual = { req.End.X - snappedEnd.X, req.End.Y - snappedEnd.Y, req.End.Z - snappedEnd.Z };
            for (int axis = 0; axis < 3; axis++)
            {
                double r = residual[axis];
                if (Math.Abs(r) <= Tolerance) continue;
                List<Point3> absorbed = null; Box3? block = null;
                // A leg already travelling the residual's way first: lengthening never shortens a leg below its fitting.
                foreach (bool sameWay in new[] { true, false })
                    for (int i = path.Count - 2; i >= 0 && absorbed == null; i--)
                    {
                        if (!IsAlong(path[i], path[i + 1], axis)) continue;
                        if ((Coord(path[i + 1], axis) - Coord(path[i], axis)) * r > 0 != sameWay) continue;
                        var candidate = new List<Point3>(path);
                        for (int k = i + 1; k < candidate.Count; k++) candidate[k] = Shift(candidate[k], axis, r);
                        if (FirstBlockedLeg(obstacles, candidate, i, out Box3 hit)) { block = hit; continue; }
                        absorbed = candidate;
                    }
                if (absorbed != null) { path = absorbed; continue; }
                if (block.HasValue)
                    return new Result { Found = false, Reason = "no_route: the end point's off-grid remainder cannot be absorbed into the route without crossing " + Describe(block.Value), BlockingRegion = block, NodesExpanded = nodesExpanded, MarginStepsUsed = marginUsed };
                // No leg travels this axis: the remainder (under one grid step) needs its own stub at the end.
                Point3 last = path[path.Count - 1], next = Shift(last, axis, r);
                if (Blocked(obstacles, last, next, out Box3 stubBlock))
                    return new Result { Found = false, Reason = "no_route: the final connector to the end point is blocked by " + Describe(stubBlock), BlockingRegion = stubBlock, NodesExpanded = nodesExpanded, MarginStepsUsed = marginUsed };
                path.Add(next);
            }
            path = Simplify(path);
            if (path[path.Count - 1].DistanceTo(req.End) > Tolerance)
                return new Result { Found = false, Reason = "no_route: the residual connector to the end point could not be closed", NodesExpanded = nodesExpanded, MarginStepsUsed = marginUsed };

            double length = 0;
            for (int i = 1; i < path.Count; i++) length += path[i - 1].DistanceTo(path[i]);
            int legs = path.Count - 1;
            for (int j = 0; legs >= 2 && j < legs; j++)
            {
                double need = j == 0 || j == legs - 1 ? req.MinEndLeg : req.MinInteriorLeg;
                double len = path[j].DistanceTo(path[j + 1]);
                if (len + Tolerance < need)
                    return new Result
                    {
                        Found = false, Polyline = path, Length = length, Bends = legs - 1, NodesExpanded = nodesExpanded, MarginStepsUsed = marginUsed,
                        ShortLeg = j, ShortLegLength = len, ShortLegMinimum = need,
                        Reason = "no_route: leg " + (j + 1) + " of " + legs + " is " + len.ToString("R") + " long, shorter than the " + need.ToString("R") + " its elbow(s) need"
                    };
            }
            return new Result
            {
                Found = true,
                Polyline = path,
                Length = length,
                Bends = Math.Max(0, legs - 1),
                NodesExpanded = nodesExpanded,
                MarginStepsUsed = marginUsed
            };
        }

        /// <summary>Grid steps from start toward the end, truncated so the residual keeps the travel's sign; a quotient within 1e-9 of an integer is that integer.</summary>
        private static long SnapTowardStart(double delta, double grid)
        {
            double q = delta / grid, k = Math.Round(q);
            return (long)(Math.Abs(q - k) <= 1e-9 ? k : Math.Truncate(q));
        }

        private static List<Box3> Within(IList<Box3> obstacles, Box3 bounds)
        {
            var list = new List<Box3>();
            foreach (Box3 o in obstacles)
                if (o.MinX <= bounds.MaxX && o.MaxX >= bounds.MinX && o.MinY <= bounds.MaxY && o.MaxY >= bounds.MinY && o.MinZ <= bounds.MaxZ && o.MaxZ >= bounds.MinZ)
                    list.Add(o);
            return list;
        }

        private static int StepsFor(double length, double grid) => length <= Tolerance ? 0 : (int)Math.Ceiling(length / grid - 1e-9);

        private static double Coord(Point3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

        private static Point3 Shift(Point3 p, int axis, double d) => p.Add(axis == 0 ? d : 0, axis == 1 ? d : 0, axis == 2 ? d : 0);

        private static bool IsAlong(Point3 a, Point3 b, int axis)
        {
            for (int k = 0; k < 3; k++)
                if (k != axis && Math.Abs(Coord(a, k) - Coord(b, k)) > Tolerance) return false;
            return Math.Abs(Coord(a, axis) - Coord(b, axis)) > Tolerance;
        }

        private static bool FirstBlockedLeg(IList<Box3> obstacles, List<Point3> path, int from, out Box3 hit)
        {
            for (int i = Math.Max(0, from); i < path.Count - 1; i++)
                if (Blocked(obstacles, path[i], path[i + 1], out hit)) return true;
            hit = default;
            return false;
        }

        private static Result Fail(string reason, Box3? blocking = null) => new Result { Found = false, Reason = reason, BlockingRegion = blocking };

        private static string Describe(Box3 b) => (b.Name ?? "an obstacle") +
            " [" + b.MinX.ToString("F3") + ".." + b.MaxX.ToString("F3") + ", " + b.MinY.ToString("F3") + ".." + b.MaxY.ToString("F3") + ", " + b.MinZ.ToString("F3") + ".." + b.MaxZ.ToString("F3") + "]";

        private static bool Blocked(IList<Box3> obstacles, Point3 p, out Box3 hit)
        {
            for (int i = 0; i < obstacles.Count; i++) if (obstacles[i].Contains(p)) { hit = obstacles[i]; return true; }
            hit = default;
            return false;
        }

        private static bool Blocked(IList<Box3> obstacles, Point3 a, Point3 b, out Box3 hit)
        {
            for (int i = 0; i < obstacles.Count; i++) if (obstacles[i].IntersectsAxisSegment(a, b)) { hit = obstacles[i]; return true; }
            hit = default;
            return false;
        }

        /// <summary>The first obstacle the direct (non-orthogonal) line from a to b passes through - named
        /// as a hint for "why can't I route directly", even though the search itself never draws it.</summary>
        private static Box3? FindBlockingRegion(Point3 a, Point3 b, IList<Box3> obstacles)
        {
            const int samples = 40;
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
                var p = new Point3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
                for (int j = 0; j < obstacles.Count; j++)
                    if (obstacles[j].Contains(p, 0)) return obstacles[j];
            }
            return null;
        }

        private static Box3 DefaultBounds(Point3 start, Point3 end, double grid, int marginSteps)
        {
            double margin = grid * Math.Max(1, marginSteps);
            double minX = Math.Min(start.X, end.X) - margin, maxX = Math.Max(start.X, end.X) + margin;
            double minY = Math.Min(start.Y, end.Y) - margin, maxY = Math.Max(start.Y, end.Y) + margin;
            double minZ = Math.Min(start.Z, end.Z) - margin, maxZ = Math.Max(start.Z, end.Z) + margin;
            return new Box3(minX, minY, minZ, maxX, maxY, maxZ, "search bounds");
        }

        /// <summary>Consecutive collinear points collapse to their two ends, so the polyline's interior
        /// vertices are exactly the bends.</summary>
        private static List<Point3> Simplify(List<Point3> path)
        {
            var outp = new List<Point3>();
            foreach (Point3 p in path)
            {
                if (outp.Count >= 2)
                {
                    Point3 a = outp[outp.Count - 2], b = outp[outp.Count - 1];
                    double ax = b.X - a.X, ay = b.Y - a.Y, az = b.Z - a.Z;
                    double bx = p.X - b.X, by = p.Y - b.Y, bz = p.Z - b.Z;
                    // Same direction (parallel, non-negative dot, cross ~ 0) => collapse the middle point.
                    double cross = Math.Abs(ay * bz - az * by) + Math.Abs(az * bx - ax * bz) + Math.Abs(ax * by - ay * bx);
                    double dot = ax * bx + ay * by + az * bz;
                    if (cross < 1e-9 && dot >= -1e-9) { outp.RemoveAt(outp.Count - 1); outp.Add(p); continue; }
                }
                outp.Add(p);
            }
            return outp;
        }

        // ---- the A* search itself -----------------------------------------------------------

        private struct StateKey : IEquatable<StateKey>
        {
            public long X, Y, Z; public Direction Dir;
            public int Run;    // grid steps of the current leg, capped (0 when no minimum leg is tracked)
            public bool Bent;  // the path has turned at least once (its current leg is not the first)
            public bool Equals(StateKey o) => X == o.X && Y == o.Y && Z == o.Z && Dir == o.Dir && Run == o.Run && Bent == o.Bent;
            public override bool Equals(object o) => o is StateKey k && Equals(k);
            public override int GetHashCode() => (X, Y, Z, Dir, Run, Bent).GetHashCode();
        }

        private sealed class AStarResult { public bool Found, BoundsExhausted; public List<Point3> Path; public int NodesExpanded; public string Reason; }

        private static AStarResult RunAStar(Point3 start, long gx, long gy, long gz, double grid, double bendPenalty,
            IList<Box3> obstacles, Box3 bounds, int maxNodes, Request req, int minEndSteps, int minInteriorSteps)
        {
            int cap = Math.Max(minEndSteps, minInteriorSteps);
            bool track = cap > 1; // a one-step minimum is met by every grid leg
            bool prunedByBounds = false;
            double bandLo = req.PreferredMinZ ?? double.NegativeInfinity, bandHi = req.PreferredMaxZ ?? double.PositiveInfinity;
            var heap = new BinaryHeap<StateKey>();
            var best = new Dictionary<StateKey, double>();
            var parent = new Dictionary<StateKey, StateKey?>();

            var startKey = new StateKey { X = 0, Y = 0, Z = 0, Dir = Direction.None };
            best[startKey] = 0;
            parent[startKey] = null;
            heap.Push(startKey, Heuristic(0, 0, 0, gx, gy, gz, grid));

            int expanded = 0;
            while (heap.Count > 0)
            {
                if (expanded >= maxNodes)
                    return new AStarResult { Found = false, NodesExpanded = expanded, Reason = "no_route: max_nodes (" + req.MaxNodes + ") was exhausted before a route was found" };
                StateKey cur = heap.Pop();
                double gCur = best[cur];
                expanded++;
                if (cur.X == gx && cur.Y == gy && cur.Z == gz && (!track || !cur.Bent || cur.Run >= minEndSteps))
                    return new AStarResult { Found = true, NodesExpanded = expanded, Path = Reconstruct(parent, cur, start, grid) };

                foreach (Direction dir in AllDirections)
                {
                    if (cur.Dir != Direction.None && dir == Opposite(cur.Dir)) continue; // never backtrack on the spot
                    bool turning = cur.Dir != Direction.None && dir != cur.Dir;
                    if (track && turning && cur.Run < (cur.Bent ? minInteriorSteps : minEndSteps)) continue; // leg too short for its elbow(s)
                    Step(dir, out long dx, out long dy, out long dz);
                    long nx = cur.X + dx, ny = cur.Y + dy, nz = cur.Z + dz;
                    Point3 fromP = start.Add(cur.X * grid, cur.Y * grid, cur.Z * grid);
                    Point3 toP = start.Add(nx * grid, ny * grid, nz * grid);
                    if (!bounds.Contains(toP)) { prunedByBounds = true; continue; }
                    if (Blocked(obstacles, fromP, toP, out _)) continue;

                    double stepCost = grid;
                    if (cur.Dir != Direction.None && dir != cur.Dir) stepCost += bendPenalty;
                    // A one-sided band is unbounded on its missing side (it used to be ignored entirely).
                    if (req.PreferredMinZ.HasValue || req.PreferredMaxZ.HasValue)
                    {
                        double z = toP.Z;
                        double outside = z < bandLo ? bandLo - z : z > bandHi ? z - bandHi : 0;
                        if (outside > 0) stepCost += outside * req.OutOfBandWeight;
                    }
                    double ng = gCur + stepCost;
                    var nk = new StateKey { X = nx, Y = ny, Z = nz, Dir = dir,
                        Run = !track ? 0 : turning || cur.Dir == Direction.None ? 1 : Math.Min(cap, cur.Run + 1),
                        Bent = track && (cur.Bent || turning) };
                    if (best.TryGetValue(nk, out double knownG) && knownG <= ng + 1e-9) continue;
                    best[nk] = ng;
                    parent[nk] = cur;
                    double f = ng + Heuristic(nx, ny, nz, gx, gy, gz, grid);
                    heap.Push(nk, f);
                }
            }
            return new AStarResult { Found = false, BoundsExhausted = prunedByBounds, NodesExpanded = expanded, Reason = "no_route: the open set was exhausted with no path to the end point within the search bounds" };
        }

        private static double Heuristic(long x, long y, long z, long gx, long gy, long gz, double grid)
            => (Math.Abs(gx - x) + Math.Abs(gy - y) + Math.Abs(gz - z)) * grid;

        private static List<Point3> Reconstruct(Dictionary<StateKey, StateKey?> parent, StateKey goal, Point3 start, double grid)
        {
            var cells = new List<StateKey>();
            StateKey? cur = goal;
            while (cur.HasValue) { cells.Add(cur.Value); cur = parent[cur.Value]; }
            cells.Reverse();
            var pts = new List<Point3>(cells.Count);
            foreach (StateKey k in cells) pts.Add(start.Add(k.X * grid, k.Y * grid, k.Z * grid));
            return pts;
        }

        /// <summary>Small binary min-heap keyed by a double priority - portable to net48, unlike
        /// System.Collections.Generic.PriorityQueue (.NET 6+ only).</summary>
        private sealed class BinaryHeap<T>
        {
            private readonly List<(double Priority, T Item)> _items = new List<(double, T)>();
            public int Count => _items.Count;

            public void Push(T item, double priority)
            {
                _items.Add((priority, item));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].Priority <= _items[i].Priority) break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public T Pop()
            {
                T top = _items[0].Item;
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = i * 2 + 2, smallest = i;
                    if (l < _items.Count && _items[l].Priority < _items[smallest].Priority) smallest = l;
                    if (r < _items.Count && _items[r].Priority < _items[smallest].Priority) smallest = r;
                    if (smallest == i) break;
                    (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                    i = smallest;
                }
                return top;
            }
        }
    }
}
