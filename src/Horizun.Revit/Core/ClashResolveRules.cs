// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CLASH RESOLUTION AS ARITHMETIC. Detecting a clash is common; resolving it with
// a verification is the part nobody does, and the part that has to be provable
// at a desk. Everything here is Revit-free:
//
//   * WHO MAY MOVE. Only flexible MEP runs (pipe, duct, conduit, cable tray, flex)
//     are movable. Structure and architecture are NEVER moved by default - a
//     proposal against them is "report only". A connected run is not moved on its
//     own: moving one segment tears it off its fittings.
//   * HOW FAR. The clearance distance is computed on the mover's exact cross
//     section around its centreline against the fixed element's box projected on
//     the escape direction. Conservative: a box is never smaller than its solid,
//     so the prediction over-clears rather than under-clears - and the apply
//     re-measures on solids anyway.
//   * WHICH WAY. Two kinds: `shift` (horizontal, perpendicular to the run) and
//     `elevation` (vertical offset, horizontal runs only). The run's own axis is
//     never an escape: sliding a run along itself changes where it ends, not
//     whether it collides.
//   * THE VERDICT. An apply keeps its work only when the targeted pair no longer
//     intersects AND no pair appears that was not there before the move.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>An axis-aligned box in millimetres (host coordinates).</summary>
    public sealed class ResolveBox
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public ResolveBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        { MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ; }

        /// <summary>Projection of the 8 corners on a unit direction: [min, max].</summary>
        public void Project(double dx, double dy, double dz, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            foreach (double x in new[] { MinX, MaxX })
                foreach (double y in new[] { MinY, MaxY })
                    foreach (double z in new[] { MinZ, MaxZ })
                    {
                        double p = x * dx + y * dy + z * dz;
                        if (p < min) min = p;
                        if (p > max) max = p;
                    }
        }
    }

    /// <summary>A movable MEP run: centreline segment (mm) and cross section (mm).</summary>
    public sealed class ResolveRun
    {
        public double[] Start, End;       // mm
        public double Width, Height;      // mm, cross section (round: both = diameter)
    }

    public sealed class ResolveCandidate
    {
        public string Kind;               // shift | elevation
        public double[] Direction;        // unit
        public double DistanceMm;
        public double[] VectorMm;
        public string Describe() => Kind + " " + DistanceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
    }

    public static class ClashResolveRules
    {
        public const string RoleMovable = "mep_flexible";
        public const string RoleStructure = "structure";
        public const string RoleArchitecture = "architecture";
        public const string RoleOther = "other";

        public const string CodeNotMovable = "report_only_no_movable_side";
        public const string CodeBothMovable = "ambiguous_both_movable";
        public const string CodeConnected = "connected_run_not_safe";
        public const string CodeLinked = "linked_element_not_writable";
        public const string CodeTooFar = "exceeds_max_move";
        public const string CodeNoGeometry = "no_run_geometry";
        public const string CodeSloped = "sloped_or_vertical_run";
        public const string CodePinned = "pinned";
        public const string CodeImmovableSide = "immovable_side_blocks_resolution";
        public const string CodeNetworkTooLarge = "network_exceeds_cap";
        public const string CodeInGroup = "member_in_group";
        public const string CodeBoundaryBlocked = "boundary_connector_blocked";

        /// <summary>The action a kept proposal took: one flexible run, or its whole connected network.</summary>
        public const string ModeSingle = "single";
        public const string ModeRunShift = "run_shift";

        /// <summary>How many members a connected network may reach before it is report-only (never moved).</summary>
        public const int MaxNetworkMembers = 60;

        private static readonly string[] Movable =
        {
            "OST_PipeCurves", "OST_DuctCurves", "OST_Conduit", "OST_CableTray", "OST_FlexPipeCurves", "OST_FlexDuctCurves"
        };
        private static readonly string[] Structural =
        {
            "OST_StructuralFraming", "OST_StructuralColumns", "OST_StructuralFoundation", "OST_Rebar", "OST_StructuralTruss"
        };
        private static readonly string[] Architectural =
        {
            "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Columns", "OST_Doors", "OST_Windows",
            "OST_Stairs", "OST_Ramps", "OST_CurtainWallPanels", "OST_CurtainWallMullions", "OST_GenericModel"
        };
        /// <summary>
        /// The fittings and accessories that keep a run connected end-to-end (elbows, tees,
        /// couplings, valves, dampers...). A network walk follows connectors through these
        /// AND through <see cref="Movable"/> itself; it never walks INTO equipment, fixtures
        /// or terminals - those are exactly the boundary a connected move must not disturb.
        /// </summary>
        private static readonly string[] NetworkFittings =
        {
            "OST_PipeFitting", "OST_PipeAccessory", "OST_DuctFitting", "OST_DuctAccessory",
            "OST_ConduitFitting", "OST_CableTrayFitting"
        };

        /// <summary>Pipe/duct/conduit/cable tray runs AND their fittings/accessories - the categories a run-shift network may include.</summary>
        public static bool IsNetworkMember(string builtInCategory)
            => builtInCategory != null && (Array.IndexOf(Movable, builtInCategory) >= 0 || Array.IndexOf(NetworkFittings, builtInCategory) >= 0);

        /// <summary>The role of a BuiltInCategory name. A structural wall/floor is passed as structure by the caller.</summary>
        public static string RoleOf(string builtInCategory, bool structuralFlag)
        {
            if (structuralFlag) return RoleStructure;
            if (Array.IndexOf(Movable, builtInCategory) >= 0) return RoleMovable;
            if (Array.IndexOf(Structural, builtInCategory) >= 0) return RoleStructure;
            if (Array.IndexOf(Architectural, builtInCategory) >= 0) return RoleArchitecture;
            return RoleOther;
        }

        /// <summary>
        /// Which side moves. Exactly one movable side, in the host, unconnected: that one.
        /// Both movable: the SMALLER cross section moves (the cheaper reroute), and a tie is
        /// ambiguous and reported. Otherwise: no auto-resolution, and the code says why.
        /// </summary>
        public static int ChooseMover(string roleA, bool hostA, double sectionA,
                                      string roleB, bool hostB, double sectionB,
                                      out string code, out string reason)
        {
            code = null; reason = null;
            bool a = roleA == RoleMovable && hostA, b = roleB == RoleMovable && hostB;
            if (!a && !b)
            {
                bool linked = (roleA == RoleMovable && !hostA) || (roleB == RoleMovable && !hostB);
                code = linked ? CodeLinked : CodeNotMovable;
                reason = linked
                    ? "the movable run lives in a linked model; a link is not writable from the host - resolve it in its own model"
                    : "neither side is a flexible MEP run; structure and architecture are never moved automatically - report only";
                return -1;
            }
            if (a && b)
            {
                if (Math.Abs(sectionA - sectionB) < 1e-6)
                {
                    code = CodeBothMovable;
                    reason = "both sides are MEP runs of the same section; which one yields is a design decision - report only";
                    return -1;
                }
                return sectionA < sectionB ? 0 : 1;
            }
            return a ? 0 : 1;
        }

        /// <summary>
        /// Apply an external tool's immovable-side rule (e.g. a navisworks issue's
        /// `immovable_side`) on top of the natural mover ChooseMover already picked.
        ///
        ///   * No preference, or ChooseMover found no mover at all: unchanged.
        ///   * The natural mover already respects the preference (it is not the marked
        ///     side): unchanged.
        ///   * The natural mover IS the marked-immovable side: invert to the other side,
        ///     but ONLY if that other side is itself a movable, host run - otherwise there
        ///     is nothing left that may move, and the finding is refused rather than moved
        ///     against the rule that named it.
        ///
        /// `forcedImmovableSide` and the eligibility flags are 0=A, 1=B throughout, the same
        /// indexing ChooseMover returns.
        /// </summary>
        public static bool EnforceImmovableSide(int naturalMover, bool aEligible, bool bEligible,
                                                int? forcedImmovableSide, out int finalMover,
                                                out string code, out string reason)
        {
            code = null; reason = null; finalMover = naturalMover;
            if (forcedImmovableSide == null || naturalMover < 0) return true;
            if (naturalMover != forcedImmovableSide.Value) return true;
            int alt = 1 - naturalMover;
            bool altEligible = alt == 0 ? aEligible : bEligible;
            string immovableLetter = forcedImmovableSide.Value == 0 ? "A" : "B";
            if (altEligible)
            {
                finalMover = alt;
                reason = "inverted: the coordination issue marks side " + immovableLetter +
                          " immovable, so side " + (alt == 0 ? "A" : "B") + " moves instead of the naturally " +
                          "smaller/only-eligible side.";
                return true;
            }
            finalMover = -1;
            code = CodeImmovableSide;
            reason = "side " + immovableLetter + " is immovable per the coordination issue, and the other side is " +
                      "not a movable, host, unconnected MEP run - report only.";
            return false;
        }

        /// <summary>
        /// Escape candidates for a run against a fixed box, smallest first. A horizontal run
        /// gets both a perpendicular horizontal shift and an elevation offset; a sloped run
        /// only the perpendicular shift; a vertical run the two horizontal axes.
        /// </summary>
        public static List<ResolveCandidate> Candidates(ResolveRun run, ResolveBox fixedBox, double clearanceMm,
                                                         out string code)
        {
            code = null;
            var list = new List<ResolveCandidate>();
            if (run == null || run.Start == null || run.End == null || fixedBox == null) { code = CodeNoGeometry; return list; }
            double dx = run.End[0] - run.Start[0], dy = run.End[1] - run.Start[1], dz = run.End[2] - run.Start[2];
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) { code = CodeNoGeometry; return list; }
            dx /= len; dy /= len; dz /= len;
            double horizontal = Math.Sqrt(dx * dx + dy * dy);
            bool isHorizontal = Math.Abs(dz) < 1e-3;
            double[] mid = { (run.Start[0] + run.End[0]) / 2, (run.Start[1] + run.End[1]) / 2, (run.Start[2] + run.End[2]) / 2 };

            if (horizontal < 1e-6)
            {
                // Vertical riser: escape along X or Y, half-extent = the larger of the section.
                double half = Math.Max(run.Width, run.Height) / 2;
                AddAxis(list, "shift", new[] { 1.0, 0, 0 }, mid, half, fixedBox, clearanceMm);
                AddAxis(list, "shift", new[] { 0, 1.0, 0 }, mid, half, fixedBox, clearanceMm);
            }
            else
            {
                // Perpendicular in plan: the run's width is what faces this direction.
                double[] n = { -dy / horizontal, dx / horizontal, 0 };
                AddAxis(list, "shift", n, mid, run.Width / 2, fixedBox, clearanceMm);
                if (isHorizontal)
                    AddAxis(list, "elevation", new[] { 0, 0, 1.0 }, mid, run.Height / 2, fixedBox, clearanceMm);
            }
            if (!isHorizontal && horizontal >= 1e-6) code = CodeSloped;
            return list.OrderBy(c => c.DistanceMm).ThenBy(c => c.Kind == "elevation" ? 0 : 1).ToList();
        }

        private static void AddAxis(List<ResolveCandidate> list, string kind, double[] n, double[] mid,
                                    double halfExtent, ResolveBox box, double clearance)
        {
            double c = mid[0] * n[0] + mid[1] * n[1] + mid[2] * n[2];
            box.Project(n[0], n[1], n[2], out double fMin, out double fMax);
            // + direction: the run's near face must pass the box's far face plus clearance.
            double plus = fMax + clearance + halfExtent - c;
            // - direction: the run's far face must pass the box's near face minus clearance.
            double minus = c + halfExtent - fMin + clearance;
            list.Add(Make(kind, n, Math.Max(0, plus), 1));
            list.Add(Make(kind, n, Math.Max(0, minus), -1));
        }

        private static ResolveCandidate Make(string kind, double[] n, double d, int sign)
        {
            double r = Math.Ceiling(d);   // whole millimetres, rounded AWAY from the clash
            var dir = new[] { n[0] * sign, n[1] * sign, n[2] * sign };
            return new ResolveCandidate
            {
                Kind = kind, Direction = dir, DistanceMm = r,
                VectorMm = new[] { Math.Round(dir[0] * r, 3), Math.Round(dir[1] * r, 3), Math.Round(dir[2] * r, 3) }
            };
        }

        /// <summary>Does a moved box still touch the fixed box (with the clearance)?</summary>
        public static bool BoxesOverlap(ResolveBox a, ResolveBox b, double clearanceMm)
        {
            return a.MinX < b.MaxX + clearanceMm && a.MaxX > b.MinX - clearanceMm
                && a.MinY < b.MaxY + clearanceMm && a.MaxY > b.MinY - clearanceMm
                && a.MinZ < b.MaxZ + clearanceMm && a.MaxZ > b.MinZ - clearanceMm;
        }

        /// <summary>Pairs present after that were not present before: the new clashes a move caused.</summary>
        public static List<string> NewPairs(IEnumerable<string> before, IEnumerable<string> after)
        {
            var had = new HashSet<string>(before ?? new string[0], StringComparer.Ordinal);
            return (after ?? new string[0]).Where(p => !had.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Keep or roll back. Every condition must be MEASURED: an unmeasured check is a
        /// rollback, never a keep - a verification nobody could run is not a pass.
        /// </summary>
        public static bool Keep(bool positionsVerified, bool? targetPairsCleared, int newClashCount, bool detectionComplete,
                                out string reason)
        {
            if (!positionsVerified) { reason = "a moved element did not re-read at its predicted position"; return false; }
            if (targetPairsCleared == null || !detectionComplete)
            { reason = "the re-detection could not be completed, so the result is unmeasured - nothing is kept"; return false; }
            if (targetPairsCleared == false) { reason = "the targeted pair still intersects after the move"; return false; }
            if (newClashCount > 0) { reason = newClashCount + " new clash(es) appeared with other elements"; return false; }
            reason = "the targeted pair(s) no longer intersect and no new clash appeared";
            return true;
        }

        /// <summary>
        /// Mark a finding resolved_by_model from a MEASURED re-detection, never from a
        /// request: `measured` must be true or nothing changes. The history says what
        /// measured it, so the ledger can tell this from a full scope run.
        /// </summary>
        public static bool ResolveMeasured(CoordinationFinding finding, bool measured, string evidence, string nowUtc)
        {
            if (finding == null || !measured) return false;
            if (finding.Status == CoordinationRules.StatusResolvedByModel) return false;
            finding.Status = CoordinationRules.StatusResolvedByModel;
            finding.ResolvedUtc = nowUtc;
            finding.UpdatedUtc = nowUtc;
            CoordinationRules.AppendEvent(finding, "resolved_by_model",
                "measured by horizun_resolve_clash: " + (evidence ?? "solid re-detection found the pair gone"), nowUtc);
            return true;
        }

        /// <summary>A ledger side "source|instance|uid" split; host only when source is "host" and no instance.</summary>
        public static bool ParseSide(string side, out bool host, out string uniqueId)
        {
            host = false; uniqueId = null;
            if (string.IsNullOrEmpty(side)) return false;
            string[] parts = side.Split('|');
            if (parts.Length < 3) return false;
            host = parts[0] == "host" && parts[1].Length == 0;
            uniqueId = string.Join("|", parts, 2, parts.Length - 2);
            return uniqueId.Length > 0;
        }

        /// <summary>A canonical pair key: order-normalized, so (x,y) and (y,x) are one clash.</summary>
        public static string PairKey(long a, long b) =>
            (a <= b ? a : b).ToString(CultureInfo.InvariantCulture) + "~" + (a <= b ? b : a).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// A pair key for a host element against an element inside a loaded link: distinct
        /// from <see cref="PairKey"/> (host ids and link ids are different namespaces, so the
        /// key is never order-normalized) and stable across a before/after re-detection as
        /// long as the link stays loaded under the same name.
        /// </summary>
        public static string LinkPairKey(long hostId, string linkName, long linkElementId) =>
            "link:" + (linkName ?? "") + ":" + hostId.ToString(CultureInfo.InvariantCulture) + "~" + linkElementId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The AABB of a box's 8 corners under an affine transform (mm throughout, no Revit
        /// type crosses this boundary): corner c maps to origin + c.X*basisX + c.Y*basisY +
        /// c.Z*basisZ. Basis vectors are unitless direction cosines (a rotation/reflection),
        /// so they need no unit conversion; only origin is a point and must already be in mm.
        /// Used to carry a host box into a linked model's coordinates (the link instance's
        /// total transform, inverted) for a conservative box-only contact prediction - the
        /// AABB of a rotated box is never smaller than the box itself, so this over-clears
        /// rather than under-clears, exactly like every other box check in this file.
        /// </summary>
        public static ResolveBox TransformBox(ResolveBox box, double[] basisX, double[] basisY, double[] basisZ, double[] originMm)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (double x in new[] { box.MinX, box.MaxX })
                foreach (double y in new[] { box.MinY, box.MaxY })
                    foreach (double z in new[] { box.MinZ, box.MaxZ })
                    {
                        double px = originMm[0] + x * basisX[0] + y * basisY[0] + z * basisZ[0];
                        double py = originMm[1] + x * basisX[1] + y * basisY[1] + z * basisZ[1];
                        double pz = originMm[2] + x * basisX[2] + y * basisY[2] + z * basisZ[2];
                        if (px < minX) minX = px; if (px > maxX) maxX = px;
                        if (py < minY) minY = py; if (py > maxY) maxY = py;
                        if (pz < minZ) minZ = pz; if (pz > maxZ) maxZ = pz;
                    }
            return new ResolveBox(minX, minY, minZ, maxX, maxY, maxZ);
        }

        // ---- connected MEP networks (run_shift) ------------------------------------------

        /// <summary>Per-member facts a run-shift network is judged on. Revit-free.</summary>
        public sealed class NetworkMemberFacts
        {
            public long Id;
            public bool Host;
            public bool Pinned;
            public bool InGroup;
        }

        /// <summary>A connector at the network's boundary that is connected to something outside it.</summary>
        public sealed class BoundaryBlock
        {
            public long OwnerId;
            public string BlockedByDescription;
        }

        /// <summary>
        /// Breadth-first walk of the mover's connected network, up to <paramref name="maxSize"/>
        /// members, over a caller-supplied adjacency function (Revit connectors, in production;
        /// a dictionary, in a test) - no Revit type crosses this boundary. <paramref name="truncated"/>
        /// is true when the cap stopped the walk before every reachable neighbour was visited:
        /// a truncated network is NEVER complete, so the caller must refuse it rather than move
        /// a network it did not fully see.
        /// </summary>
        public static List<long> CollectNetwork(long seed, Func<long, IEnumerable<long>> neighboursOf, int maxSize, out bool truncated)
        {
            truncated = false;
            var visited = new List<long>();
            var seen = new HashSet<long> { seed };
            var queue = new Queue<long>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                if (visited.Count >= maxSize) { truncated = true; break; }
                long id = queue.Dequeue();
                visited.Add(id);
                foreach (long n in neighboursOf(id) ?? Enumerable.Empty<long>())
                    if (seen.Add(n)) queue.Enqueue(n);
            }
            if (!truncated && queue.Count > 0) truncated = true;   // the cap was hit exactly as the queue drained
            return visited;
        }

        /// <summary>
        /// May the whole collected network move as one rigid body? Every member must be a
        /// host element, unpinned and outside any group, AND no boundary connector may reach
        /// outside the set (equipment, fixtures, terminals - anything the walk did not itself
        /// follow). A truncated walk is refused before either check: a network this call could
        /// not fully see is never declared safe.
        /// </summary>
        public static bool EligibleForRunShift(IReadOnlyList<NetworkMemberFacts> members, IReadOnlyList<BoundaryBlock> boundaryBlocks,
                                               bool truncated, out string code, out string reason)
        {
            code = null; reason = null;
            if (truncated)
            {
                code = CodeNetworkTooLarge;
                reason = "the connected network reaches more than " + MaxNetworkMembers + " elements; not moved automatically - report only";
                return false;
            }
            foreach (NetworkMemberFacts m in members ?? new List<NetworkMemberFacts>())
            {
                if (!m.Host)
                { code = CodeLinked; reason = "member " + m.Id + " of the connected network lives in a linked model - report only"; return false; }
                if (m.Pinned)
                { code = CodePinned; reason = "member " + m.Id + " of the connected network is pinned - report only"; return false; }
                if (m.InGroup)
                { code = CodeInGroup; reason = "member " + m.Id + " of the connected network belongs to a group - report only"; return false; }
            }
            if (boundaryBlocks != null && boundaryBlocks.Count > 0)
            {
                BoundaryBlock b = boundaryBlocks[0];
                code = CodeBoundaryBlocked;
                reason = "connector on " + b.OwnerId + " is connected to " + b.BlockedByDescription +
                          " outside the network - moving the network would tear that connection; report only";
                return false;
            }
            return true;
        }
    }
}
