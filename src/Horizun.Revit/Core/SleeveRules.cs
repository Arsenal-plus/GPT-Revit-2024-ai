// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// SLEEVES AND STRUCTURAL OPENINGS - the pure, Revit-free half of
// horizun_resolve_clash's propose_opening / apply_opening. Used when a clash
// between an MEP run and a wall/floor/roof/framing/column cannot be resolved by
// moving the run (ClashResolveRules already covers the move path): instead of
// relocating anything, the run keeps its line and the HOST gets an opening (or,
// for framing/columns the API cannot cut, a sleeve family only).
//
//   * THE CROSSING. The run's centreline (a segment, mm) against the host's own
//     bounding box (mm, already how the rest of this bridge measures a host
//     conservatively - see ResolveClashCommand.Box): a standard 3D slab
//     (Liang-Barsky) clip gives the entry and exit point where the segment
//     crosses the box, or a code saying why it does not cross at all. The
//     midpoint of entry/exit is the crossing point a sleeve centres on.
//   * THE SIZE. Opening size = the run's own outer cross-section (from
//     MepFacts.TryProfile, mm) plus clearance_mm - round stays round (both
//     dimensions equal, diameter-based), everything else is the rectangle the
//     run's width/height describe.
//   * THE ROUTE. Which Revit API call the command layer uses is a property of
//     the HOST's kind alone: a wall gets a rectangular NewOpening(wall, pt1,
//     pt2); a floor/roof/ceiling gets a boundary-loop NewOpening(host,
//     CurveArray, bool); framing and columns get NEITHER - the API has no way to
//     cut a beam or column except with a void-cutting family, so the only route
//     is a sleeve family instance, and even that is a caller-supplied family
//     (this bridge is organisation-neutral: no family is compiled in).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SleeveRules
    {
        public const string ShapeRound = "round";
        public const string ShapeRect = "rect";

        public const string HostWall = "wall";
        public const string HostFloor = "floor";
        public const string HostRoof = "roof";
        public const string HostCeiling = "ceiling";
        public const string HostFramingOrColumn = "framing_or_column";
        public const string HostUnsupported = "unsupported";

        /// <summary>doc.Create.NewOpening(Wall, XYZ, XYZ) - a rectangular cut through a wall's full thickness.</summary>
        public const string RouteWallOpening = "wall_opening";
        /// <summary>doc.Create.NewOpening(HostObject, CurveArray, bool) - a boundary-loop cut through a floor/roof/ceiling.</summary>
        public const string RouteFloorOpening = "floor_opening";
        /// <summary>No cut is possible; only a caller-supplied sleeve family instance may be placed, uncut, at the crossing.</summary>
        public const string RouteSleeveOnly = "sleeve_only";
        /// <summary>Neither a cut nor a documented sleeve route exists for this host kind.</summary>
        public const string RouteRefused = "refused";

        public const string CodeNoCrossing = "run_does_not_cross_host";
        public const string CodeParallel = "run_parallel_to_host_face";
        public const string CodeNoProfile = "run_has_no_profile";
        public const string CodeNoHostBox = "host_has_no_bounding_box";
        public const string CodeHostUnsupported = "host_kind_not_supported";
        /// <summary>Framing/columns: the API has no cut for them (only a void-cutting family instance can), so a cut is refused by name.</summary>
        public const string CodeCutRefused = "host_cannot_be_cut_by_api";

        /// <summary>
        /// The host kind that decides the route. `structural` is the caller's own
        /// structural-significance read (a non-structural wall/floor still gets its normal
        /// wall/floor route: structural-significance only matters for framing/columns, which
        /// have no other route to begin with).
        /// </summary>
        public static string HostKindOf(string builtInCategory)
        {
            switch (builtInCategory)
            {
                case "OST_Walls": return HostWall;
                case "OST_Floors": return HostFloor;
                case "OST_Roofs": return HostRoof;
                case "OST_Ceilings": return HostCeiling;
                case "OST_StructuralFraming":
                case "OST_StructuralColumns":
                case "OST_Columns":
                    return HostFramingOrColumn;
                default: return HostUnsupported;
            }
        }

        /// <summary>The Revit route for a host kind - never guesses; an unmapped kind is refused.</summary>
        public static string RouteFor(string hostKind)
        {
            switch (hostKind)
            {
                case HostWall: return RouteWallOpening;
                case HostFloor:
                case HostRoof:
                case HostCeiling:
                    return RouteFloorOpening;
                case HostFramingOrColumn: return RouteSleeveOnly;
                default: return RouteRefused;
            }
        }

        /// <summary>
        /// A 3D segment (mm) clipped against an axis-aligned box (Liang-Barsky slab method,
        /// clipped to the segment's own [0,1] as well, so a run that only grazes the host near
        /// one end is reported honestly rather than extrapolated). `code` explains a false
        /// return: parallel-and-outside, or the segment simply never reaches the box within its
        /// own length.
        /// </summary>
        public static bool LineBoxIntersect(double[] startMm, double[] endMm, ResolveBox box,
                                            out double[] entryMm, out double[] exitMm, out string code)
        {
            entryMm = null; exitMm = null; code = null;
            if (startMm == null || endMm == null || box == null) { code = CodeNoHostBox; return false; }
            double[] d = { endMm[0] - startMm[0], endMm[1] - startMm[1], endMm[2] - startMm[2] };
            double[] bmin = { box.MinX, box.MinY, box.MinZ };
            double[] bmax = { box.MaxX, box.MaxY, box.MaxZ };
            double tmin = 0, tmax = 1;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-9)
                {
                    if (startMm[i] < bmin[i] - 1e-6 || startMm[i] > bmax[i] + 1e-6) { code = CodeParallel; return false; }
                    continue;
                }
                double t1 = (bmin[i] - startMm[i]) / d[i];
                double t2 = (bmax[i] - startMm[i]) / d[i];
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) { code = CodeNoCrossing; return false; }
            }
            entryMm = new[] { startMm[0] + tmin * d[0], startMm[1] + tmin * d[1], startMm[2] + tmin * d[2] };
            exitMm = new[] { startMm[0] + tmax * d[0], startMm[1] + tmax * d[1], startMm[2] + tmax * d[2] };
            return true;
        }

        /// <summary>Midpoint of two mm points - the crossing point a sleeve/opening centres on.</summary>
        public static double[] Midpoint(double[] a, double[] b) =>
            new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 };

        /// <summary>
        /// Opening/sleeve size: the run's own outer cross-section plus clearance_mm. Round stays
        /// round (both dimensions equal, so a caller can build a circular boundary); a
        /// rectangular or oval run always becomes a rectangular opening.
        /// </summary>
        public static void OpeningSize(double runWidthMm, double runHeightMm, double clearanceMm, string runShape,
                                       out double openingWidthMm, out double openingHeightMm, out string shape)
        {
            bool round = runShape == "round";
            openingWidthMm = Math.Max(0, runWidthMm) + Math.Max(0, clearanceMm);
            openingHeightMm = round ? openingWidthMm : Math.Max(0, runHeightMm) + Math.Max(0, clearanceMm);
            shape = round ? ShapeRound : ShapeRect;
        }

        /// <summary>
        /// Does `outer` contain `point` with at least `marginMm` of clearance on every side that
        /// the run's own half cross-section would occupy around it? The check runs only on the
        /// axes ACROSS the run: the axis the run travels along (|component| >= 0.5 of the unit
        /// direction) is skipped, because an opening is as thin as its host there and a sleeve
        /// is as long as it is. Vertical (Z) needs `halfHeightMm`; horizontal axes need
        /// `halfWidthMm` (for a vertical run, plan Y uses `halfHeightMm` - see FloorFootprint,
        /// which squares a rectangular section for exactly that reason). Used as the apply
        /// postcondition: the built opening/sleeve must actually clear the run by the clearance
        /// that was proposed, not merely overlap it. Conservative like every box check in this
        /// bridge: exact on an axis-aligned opening, a safe under-estimate otherwise.
        /// </summary>
        public static bool ContainsCrossingWithClearance(ResolveBox outer, double[] pointMm, double[] runDirection,
                                                          double halfWidthMm, double halfHeightMm, double marginMm)
        {
            if (outer == null || pointMm == null) return false;
            double[] u = Unit(runDirection);
            double[] min = { outer.MinX, outer.MinY, outer.MinZ };
            double[] max = { outer.MaxX, outer.MaxY, outer.MaxZ };
            bool vertical = u != null && Math.Abs(u[2]) >= 0.5;
            for (int i = 0; i < 3; i++)
            {
                if (u != null && Math.Abs(u[i]) >= 0.5) continue;
                double half = i == 2 ? halfHeightMm : (vertical && i == 1 ? halfHeightMm : halfWidthMm);
                double need = half + marginMm;
                if (min[i] > pointMm[i] - need + 1e-6 || max[i] < pointMm[i] + need - 1e-6) return false;
            }
            return true;
        }

        /// <summary>
        /// Plan footprint (X by Y, mm) of a floor/roof/ceiling opening. A round run keeps its
        /// diameter; a rectangular section is SQUARED to its larger side because this route does
        /// not read the section's rotation in plan - an axis-aligned rectangle of the raw width
        /// by height would miss a duct turned 90 degrees, and the postcondition would then (rightly)
        /// roll the cut back. Over-size rather than a guess that fails.
        /// </summary>
        public static void FloorFootprint(double openingWidthMm, double openingHeightMm, string shape, out double xMm, out double yMm)
        {
            if (shape == ShapeRound) { xMm = openingWidthMm; yMm = openingWidthMm; return; }
            double side = Math.Max(openingWidthMm, openingHeightMm);
            xMm = side; yMm = side;
        }

        /// <summary>
        /// New clash pairs after an opening/sleeve, minus the one that is the design itself: a
        /// sleeve sitting inside its host (created~host) is the point of a sleeve, not a new
        /// clash. Anything else new - the sleeve against the run, against a third element, or
        /// the run against anything - stays in the list and fails the apply.
        /// </summary>
        public static List<string> UnintendedNewPairs(IEnumerable<string> before, IEnumerable<string> after, long createdId, long hostId)
        {
            string intended = ClashResolveRules.PairKey(createdId, hostId);
            return ClashResolveRules.NewPairs(before, after).Where(p => p != intended).ToList();
        }

        private static double[] Unit(double[] d)
        {
            if (d == null || d.Length < 3) return null;
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            return len < 1e-9 ? null : new[] { d[0] / len, d[1] / len, d[2] / len };
        }

        public static string Describe(string hostKind, double widthMm, double heightMm, string shape) =>
            shape + " opening " + widthMm.ToString("0.#", CultureInfo.InvariantCulture) + "x" +
            heightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm on " + hostKind;
    }
}
