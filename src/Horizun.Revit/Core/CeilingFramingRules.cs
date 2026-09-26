// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_framing operation=ceiling: the main (carrying)
// channels, cross (furring) members, perimeter track and hanger stations of a
// suspended drywall ceiling, given the ceiling's boundary and the caller's spec.
//
// Everything is in the ceiling's plan (x, y); z is the command's business (mains
// at the top face + drop, furring on the top face, hangers up to the structure
// found above). Units are any consistent length (the command uses millimetres).
//
// The rules, stated once so a unit test can hold them exactly:
//  * DIRECTION. An explicit angle wins. Otherwise the boundary's minimum-area
//    rectangle over its own edge directions gives two axes: 'long' runs the
//    mains parallel to its longer side, 'short' parallel to its shorter side.
//  * GRID ("centred strips"). Across the boundary's extent R perpendicular to
//    the members, n = ceil(R / spacing) lines, the first and last one
//    (R - (n-1) x spacing) / 2 from the edge - so every line serves one strip of
//    the spacing's width and none sits on the boundary itself.
//  * CLIPPING. Each line is clipped to the boundary with its holes by the
//    even-odd rule with half-open edges (an edge counts where v0 <= v < v1), so
//    a line passing exactly through a vertex is counted once, not twice.
//  * PERIMETER follows every loop's edges, outer and holes.
//  * HANGERS. Along each main piece, HangerRules' stations (end offset, spacing
//    split evenly, never above the spacing). A piece too short to keep the end
//    offset from both ends still hangs: it gets one hanger at its midpoint.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CeilingFramingInput
    {
        /// <summary>The boundary: first loop outer, the rest holes; each point is {x, y}.</summary>
        public List<List<double[]>> Loops { get; set; } = new List<List<double[]>>();
        /// <summary>short | long; ignored when DirectionAngleRad is set.</summary>
        public string Direction { get; set; } = "long";
        public double? DirectionAngleRad { get; set; }
        public double MainSpacing { get; set; }
        public string MainTypeKey { get; set; }
        /// <summary>0 = no cross members.</summary>
        public double CrossSpacing { get; set; }
        public string CrossTypeKey { get; set; }
        /// <summary>Null = no perimeter track.</summary>
        public string PerimeterTypeKey { get; set; }
        /// <summary>0 = no hangers.</summary>
        public double HangerSpacing { get; set; }
        public double HangerEndOffset { get; set; }
        public string HangerTypeKey { get; set; }
    }

    public sealed class CeilingFramingPlan
    {
        public List<FramingMember> Members { get; } = new List<FramingMember>();
        public List<string> Warnings { get; } = new List<string>();
        public string Refusal { get; set; }
        /// <summary>The direction the mains run, radians from +x.</summary>
        public double MainAngleRad { get; set; }
        public Dictionary<string, int> CountByRole() => FramingPlanSignature.CountByRole(Members);
    }

    public static class CeilingFramingRules
    {
        private const double Tol = 1e-6;

        public static CeilingFramingPlan Plan(CeilingFramingInput input, int maxMembers)
        {
            var plan = new CeilingFramingPlan();
            if (input?.Loops == null || input.Loops.Count == 0 || input.Loops[0] == null || input.Loops[0].Count < 3)
            { plan.Refusal = "boundary_needs_three_points"; return plan; }
            var loops = new List<List<double[]>>();
            foreach (List<double[]> loop in input.Loops)
            {
                if (loop == null || loop.Count < 3) { plan.Warnings.Add("degenerate_loop_skipped"); continue; }
                if (loop.Any(p => p == null || p.Length < 2 || !Finite(p[0]) || !Finite(p[1]))) { plan.Refusal = "boundary_point_not_finite"; return plan; }
                loops.Add(loop);
            }
            if (!(input.MainSpacing > 0) || !Finite(input.MainSpacing)) { plan.Refusal = "main_spacing_must_be_positive"; return plan; }
            if (input.CrossSpacing < 0 || !Finite(input.CrossSpacing) || input.HangerSpacing < 0 || !Finite(input.HangerSpacing) || input.HangerEndOffset < 0)
            { plan.Refusal = "negative_spacing"; return plan; }

            double theta;
            if (input.DirectionAngleRad.HasValue)
            {
                if (!Finite(input.DirectionAngleRad.Value)) { plan.Refusal = "direction_not_finite"; return plan; }
                theta = input.DirectionAngleRad.Value;
            }
            else
            {
                string d = (input.Direction ?? "long").Trim().ToLowerInvariant();
                if (d != "long" && d != "short") { plan.Refusal = "unknown_direction"; return plan; }
                double longAxis = LongAxisAngle(loops[0]);
                theta = d == "long" ? longAxis : longAxis + Math.PI / 2;
            }
            plan.MainAngleRad = theta;

            // Bound first: the number of lines in each direction, as doubles.
            double mainLines = Extent(loops[0], theta + Math.PI / 2) / input.MainSpacing;
            double crossLines = input.CrossSpacing > 0 ? Extent(loops[0], theta) / input.CrossSpacing : 0;
            int edges = loops.Sum(l => l.Count);
            if ((mainLines + crossLines + 2) * Math.Max(1, edges / 2.0) + edges > maxMembers) { plan.Refusal = "over_budget"; return plan; }

            var mains = GridSegments(loops, theta, input.MainSpacing);
            if (mains.Count == 0) { plan.Refusal = "no_main_inside_boundary"; return plan; }
            foreach (double[] m in mains)
                plan.Members.Add(new FramingMember { Role = FramingRoles.Main, TypeKey = input.MainTypeKey, X0 = m[0], Y0 = m[1], X1 = m[2], Y1 = m[3] });

            if (input.CrossSpacing > 0)
                foreach (double[] c in GridSegments(loops, theta + Math.PI / 2, input.CrossSpacing))
                    plan.Members.Add(new FramingMember { Role = FramingRoles.Cross, TypeKey = input.CrossTypeKey, X0 = c[0], Y0 = c[1], X1 = c[2], Y1 = c[3] });

            if (input.PerimeterTypeKey != null)
                foreach (List<double[]> loop in loops)
                    for (int i = 0; i < loop.Count; i++)
                    {
                        double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                        if (Math.Abs(q[0] - p[0]) + Math.Abs(q[1] - p[1]) <= Tol) continue;
                        plan.Members.Add(new FramingMember { Role = FramingRoles.Perimeter, TypeKey = input.PerimeterTypeKey, X0 = p[0], Y0 = p[1], X1 = q[0], Y1 = q[1] });
                    }

            if (input.HangerSpacing > 0)
            {
                int mainCount = mains.Count;
                for (int i = 0; i < mainCount; i++)
                {
                    double[] m = mains[i];
                    double len = Math.Sqrt((m[2] - m[0]) * (m[2] - m[0]) + (m[3] - m[1]) * (m[3] - m[1]));
                    int budgetLeft = maxMembers - plan.Members.Count;
                    HangerStationPlan hp = HangerRules.Plan(len, input.HangerEndOffset, input.HangerSpacing, null, Math.Max(0, budgetLeft));
                    if (hp.OverBudget) { plan.Members.Clear(); plan.Refusal = "over_budget"; return plan; }
                    List<double> stations = hp.Stations.Count > 0 ? hp.Stations : new List<double> { len / 2 };
                    foreach (double t in stations)
                    {
                        double f = len > Tol ? t / len : 0.5;
                        double x = m[0] + f * (m[2] - m[0]), y = m[1] + f * (m[3] - m[1]);
                        plan.Members.Add(new FramingMember { Role = FramingRoles.Hanger, TypeKey = input.HangerTypeKey, X0 = x, Y0 = y, X1 = x, Y1 = y, Source = i });
                    }
                }
            }
            if (plan.Members.Count > maxMembers) { plan.Members.Clear(); plan.Refusal = "over_budget"; }
            return plan;
        }

        /// <summary>The angle (radians) of the longer side of the loop's minimum-area rectangle over its own edge directions.</summary>
        public static double LongAxisAngle(IReadOnlyList<double[]> loop)
        {
            double bestArea = double.MaxValue, best = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                double dx = q[0] - p[0], dy = q[1] - p[1];
                if (Math.Sqrt(dx * dx + dy * dy) <= Tol) continue;
                double a = Math.Atan2(dy, dx);
                double eu = Extent(loop, a), ev = Extent(loop, a + Math.PI / 2);
                double area = eu * ev;
                if (area < bestArea * (1 - 1e-9)) // a tie keeps the first edge's rectangle
                {
                    bestArea = area;
                    best = eu >= ev - Tol ? a : a + Math.PI / 2;
                }
            }
            return Normalise(best);
        }

        /// <summary>
        /// The segments of the "centred strips" grid of lines running at angle theta,
        /// spacing apart, clipped to the loops (even-odd). Each segment is {x0,y0,x1,y1}.
        /// </summary>
        public static List<double[]> GridSegments(IReadOnlyList<List<double[]>> loops, double theta, double spacing)
        {
            var result = new List<double[]>();
            double c = Math.Cos(theta), s = Math.Sin(theta);
            double vmin = double.MaxValue, vmax = double.MinValue;
            foreach (double[] p in loops[0]) { double v = -p[0] * s + p[1] * c; vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v); }
            double range = vmax - vmin;
            if (range <= Tol) return result;
            int n = (int)Math.Max(1, Math.Ceiling(range / spacing - Tol));
            double edge = (range - (n - 1) * spacing) / 2;
            for (int k = 0; k < n; k++)
            {
                double v = vmin + edge + k * spacing;
                foreach (double[] seg in Clip(loops, c, s, v))
                    result.Add(new[] { seg[0] * c - v * s, seg[0] * s + v * c, seg[1] * c - v * s, seg[1] * s + v * c });
            }
            return result;
        }

        /// <summary>The [u0, u1] intervals of the line v = const (in the frame rotated by (c, s)) inside the loops.</summary>
        private static IEnumerable<double[]> Clip(IReadOnlyList<List<double[]>> loops, double c, double s, double v)
        {
            var us = new List<double>();
            foreach (List<double[]> loop in loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                    double vp = -p[0] * s + p[1] * c, vq = -q[0] * s + q[1] * c;
                    if (!((vp <= v && v < vq) || (vq <= v && v < vp))) continue;
                    double up = p[0] * c + p[1] * s, uq = q[0] * c + q[1] * s;
                    us.Add(up + (v - vp) / (vq - vp) * (uq - up));
                }
            us.Sort();
            for (int i = 0; i + 1 < us.Count; i += 2)
                if (us[i + 1] - us[i] > Tol) yield return new[] { us[i], us[i + 1] };
        }

        private static double Extent(IReadOnlyList<double[]> loop, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle), lo = double.MaxValue, hi = double.MinValue;
            foreach (double[] p in loop) { double u = p[0] * c + p[1] * s; lo = Math.Min(lo, u); hi = Math.Max(hi, u); }
            return hi - lo;
        }

        /// <summary>An axis angle folded into [0, pi): a line has no arrow.</summary>
        private static double Normalise(double a)
        {
            while (a < 0) a += Math.PI;
            while (a >= Math.PI - 1e-12) a -= Math.PI;
            return a;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
