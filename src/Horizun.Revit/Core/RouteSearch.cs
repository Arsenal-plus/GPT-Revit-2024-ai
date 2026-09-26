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
// point may fall off-lattice, and the residual (see Snap) is closed with up to
// three short, still-axis-aligned stub segments rather than by moving the caller's
// point or by pretending the lattice reaches everywhere it does not.
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

            // Snap End onto the lattice anchored at Start; keep the residual to close with stubs.
            long ex = (long)Math.Round((req.End.X - req.Start.X) / grid, MidpointRounding.AwayFromZero);
            long ey = (long)Math.Round((req.End.Y - req.Start.Y) / grid, MidpointRounding.AwayFromZero);
            long ez = (long)Math.Round((req.End.Z - req.Start.Z) / grid, MidpointRounding.AwayFromZero);
            Point3 snappedEnd = req.Start.Add(ex * grid, ey * grid, ez * grid);

            Box3 bounds = req.SearchBounds ?? DefaultBounds(req.Start, snappedEnd, grid, req.MarginSteps);

            // The trivial case: start and the lattice-snapped end coincide (the residual stubs,
            // if any, are added below regardless).
            List<Point3> gridPolyline;
            int nodesExpanded;
            if (ex == 0 && ey == 0 && ez == 0)
            {
                gridPolyline = new List<Point3> { req.Start };
                nodesExpanded = 0;
            }
            else
            {
                AStarResult a = RunAStar(req.Start, snappedEnd, ex, ey, ez, grid, bendPenalty, obstacles, bounds, req.MaxNodes, req);
                if (!a.Found)
                {
                    Box3? blocking = FindBlockingRegion(req.Start, snappedEnd, obstacles);
                    return new Result
                    {
                        Found = false,
                        Reason = a.Reason ?? "no_route: no orthogonal path connects the two points within the search bounds and max_nodes budget",
                        BlockingRegion = blocking,
                        NodesExpanded = a.NodesExpanded
                    };
                }
                gridPolyline = a.Path;
                nodesExpanded = a.NodesExpanded;
            }

            // Close the residual with up to 3 axis stubs (X, then Y, then Z), each checked for collision.
            var full = new List<Point3>(gridPolyline);
            Point3 cursor = full[full.Count - 1];
            foreach (var axis in new[] { 'x', 'y', 'z' })
            {
                double dx = axis == 'x' ? req.End.X - cursor.X : 0;
                double dy = axis == 'y' ? req.End.Y - cursor.Y : 0;
                double dz = axis == 'z' ? req.End.Z - cursor.Z : 0;
                if (Math.Abs(dx) <= Tolerance && Math.Abs(dy) <= Tolerance && Math.Abs(dz) <= Tolerance) continue;
                Point3 next = cursor.Add(dx, dy, dz);
                if (Blocked(obstacles, cursor, next, out Box3 block))
                    return new Result { Found = false, Reason = "no_route: the final connector to the end point is blocked by " + Describe(block), BlockingRegion = block, NodesExpanded = nodesExpanded };
                full.Add(next);
                cursor = next;
            }
            if (cursor.DistanceTo(req.End) > Tolerance)
                return new Result { Found = false, Reason = "no_route: the residual connector to the end point could not be closed", NodesExpanded = nodesExpanded };

            List<Point3> simplified = Simplify(full);
            double length = 0;
            for (int i = 1; i < simplified.Count; i++) length += simplified[i - 1].DistanceTo(simplified[i]);
            return new Result
            {
                Found = true,
                Polyline = simplified,
                Length = length,
                Bends = Math.Max(0, simplified.Count - 2),
                NodesExpanded = nodesExpanded
            };
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
            public bool Equals(StateKey o) => X == o.X && Y == o.Y && Z == o.Z && Dir == o.Dir;
            public override bool Equals(object o) => o is StateKey k && Equals(k);
            public override int GetHashCode() => (X, Y, Z, Dir).GetHashCode();
        }

        private sealed class AStarResult { public bool Found; public List<Point3> Path; public int NodesExpanded; public string Reason; }

        private static AStarResult RunAStar(Point3 start, Point3 goal, long gx, long gy, long gz, double grid, double bendPenalty,
            IList<Box3> obstacles, Box3 bounds, int maxNodes, Request req)
        {
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
                    return new AStarResult { Found = false, NodesExpanded = expanded, Reason = "no_route: max_nodes (" + maxNodes + ") was exhausted before a route was found" };
                StateKey cur = heap.Pop();
                double gCur = best[cur];
                expanded++;
                if (cur.X == gx && cur.Y == gy && cur.Z == gz)
                    return new AStarResult { Found = true, NodesExpanded = expanded, Path = Reconstruct(parent, cur, start, grid) };

                foreach (Direction dir in AllDirections)
                {
                    if (cur.Dir != Direction.None && dir == Opposite(cur.Dir)) continue; // never backtrack on the spot
                    Step(dir, out long dx, out long dy, out long dz);
                    long nx = cur.X + dx, ny = cur.Y + dy, nz = cur.Z + dz;
                    Point3 fromP = start.Add(cur.X * grid, cur.Y * grid, cur.Z * grid);
                    Point3 toP = start.Add(nx * grid, ny * grid, nz * grid);
                    if (!bounds.Contains(toP)) continue;
                    if (Blocked(obstacles, fromP, toP, out _)) continue;

                    double stepCost = grid;
                    if (cur.Dir != Direction.None && dir != cur.Dir) stepCost += bendPenalty;
                    if (req.PreferredMinZ.HasValue && req.PreferredMaxZ.HasValue)
                    {
                        double z = toP.Z;
                        double outside = z < req.PreferredMinZ.Value ? req.PreferredMinZ.Value - z : z > req.PreferredMaxZ.Value ? z - req.PreferredMaxZ.Value : 0;
                        if (outside > 0) stepCost += outside * req.OutOfBandWeight;
                    }
                    double ng = gCur + stepCost;
                    var nk = new StateKey { X = nx, Y = ny, Z = nz, Dir = dir };
                    if (best.TryGetValue(nk, out double knownG) && knownG <= ng + 1e-9) continue;
                    best[nk] = ng;
                    parent[nk] = cur;
                    double f = ng + Heuristic(nx, ny, nz, gx, gy, gz, grid);
                    heap.Push(nk, f);
                }
            }
            return new AStarResult { Found = false, NodesExpanded = expanded, Reason = "no_route: the open set was exhausted with no path to the end point within the search bounds" };
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
