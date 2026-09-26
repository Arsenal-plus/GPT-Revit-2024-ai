// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_framing operation=wall: which studs, tracks,
// kings, jacks, headers, sills, cripples and blocking a framed partition needs,
// given the wall's length and height, its openings, and the caller's spec.
//
// Everything is in the WALL'S OWN 2-D FRAME: x runs along the wall from its
// start (0) to its end (Length); z runs up from the wall's base (0) to its top
// (Height). y (the lateral position inside the chosen layer) is the command's
// business and stays 0 here. Units are any consistent length (the command calls
// this in millimetres); the rule does not care which.
//
// The rules, stated once so a unit test can hold them exactly:
//  * A vertical member is a stud, king, jack or cripple, StudWidth wide along x
//    (its flange), centred on its station.
//  * END STUDS sit flush with both wall ends (x = w/2 and L - w/2), doubled
//    inward when asked (x = 1.5w and L - 1.5w).
//  * LAYOUT STUDS follow the spacing from the start rule: wall_start measures
//    k*spacing from x = 0, wall_end from x = L, centred puts one stud on the
//    wall's midpoint and steps both ways. max_first_bay caps the bay next to
//    each end stud: a longer end bay gets one extra stud at that distance.
//  * OPENINGS own a void [start, end] x [sill, head]. Jacks stand at each edge
//    (base to head), kings outside them (base to top), 1 or 2 per side. A header
//    spans between the kings at the head, a sill between the jacks at the sill
//    (only when the sill is above the bottom track). Cripples fill above the
//    header and below the sill: at the layout stations the opening removed, or
//    evenly at cripple_spacing when one is given.
//  * A HEADER SITS ON THE HEAD, A SILL UNDER THE SILL LINE: with a header depth
//    the header's axis is head + depth/2 and the cripples above start at
//    head + depth; with a sill depth the sill's axis is sill - depth/2 and the
//    cripples below end there. Without a depth the axis sits ON the line (half
//    the member hangs into the void) and the plan says so in its warnings. A
//    header or sill that does not fit between the tracks is not placed, named.
//  * NO VERTICAL MEMBER CROSSES AN OPENING VOID, and no two vertical members
//    overlap (closer than one StudWidth while their heights overlap). Conflicts
//    resolve by priority: jack > king > end stud > layout stud; the loser is
//    dropped (a dropped king between two close openings is REPORTED, not hidden).
//  * TRACKS run the full length at the base and at the top; the bottom track is
//    cut across every opening whose sill is at the base (a door).
//  * BLOCKING rows are split at every vertical member that spans their height,
//    and interrupted across an opening void at that height.
//
// THE COUNT IS BOUNDED BEFORE ANYTHING IS ALLOCATED: a spacing typed in the
// wrong unit refuses arithmetically instead of placing millions of members.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>The roles a framing member can have; the counts per role are what the verification compares.</summary>
    public static class FramingRoles
    {
        public const string Stud = "stud", Track = "track", King = "king", Jack = "jack", Header = "header",
            Sill = "sill", Cripple = "cripple", Blocking = "blocking",
            Main = "main", Cross = "cross", Perimeter = "perimeter", Hanger = "hanger";

        public static readonly string[] Wall = { Stud, Track, King, Jack, Header, Sill, Cripple, Blocking };
        public static readonly string[] Ceiling = { Main, Cross, Perimeter, Hanger };

        public static bool IsVertical(string role) => role == Stud || role == King || role == Jack || role == Cripple;
    }

    /// <summary>One planned member: a straight axis from (X0,Y0,Z0) to (X1,Y1,Z1) in the source's own frame.</summary>
    public sealed class FramingMember
    {
        public string Role { get; set; }
        /// <summary>The caller's type key (an element id as text); the command resolves it.</summary>
        public string TypeKey { get; set; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double Z0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double Z1 { get; set; }
        /// <summary>Opening index (wall), main index (hanger) or blocking row index; -1 when none.</summary>
        public int Source { get; set; } = -1;

        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0) + (Z1 - Z0) * (Z1 - Z0));
    }

    public static class FramingPlanSignature
    {
        /// <summary>
        /// A stable hash of a plan: every member's role, type and both endpoints
        /// rounded to 0.1 unit, in plan order. The confirmation token binds this, so
        /// an apply whose re-resolved plan moved by more than rounding is refused.
        /// </summary>
        public static string Of(IEnumerable<FramingMember> members)
        {
            var sb = new StringBuilder();
            foreach (FramingMember m in members ?? Enumerable.Empty<FramingMember>())
            {
                sb.Append(m.Role).Append('|').Append(m.TypeKey ?? "").Append('|');
                foreach (double v in new[] { m.X0, m.Y0, m.Z0, m.X1, m.Y1, m.Z1 })
                    sb.Append(Math.Round(v, 1).ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append('\n');
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        public static Dictionary<string, int> CountByRole(IEnumerable<FramingMember> members)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (FramingMember m in members ?? Enumerable.Empty<FramingMember>())
                counts[m.Role] = counts.TryGetValue(m.Role, out int n) ? n + 1 : 1;
            return counts;
        }
    }

    /// <summary>An opening in the wall's frame: [Start, End] along x, [Sill, Head] in z.</summary>
    public sealed class WallOpeningSpan
    {
        public string Id { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public double Sill { get; set; }
        public double Head { get; set; }
    }

    public sealed class BlockingRow
    {
        public double Height { get; set; }
        public string TypeKey { get; set; }
    }

    public sealed class WallFramingInput
    {
        public double Length { get; set; }
        public double Height { get; set; }
        public double StudSpacing { get; set; }
        /// <summary>The member's width along the wall (a stud's flange); overlap and clearance are measured with it.</summary>
        public double StudWidth { get; set; }
        /// <summary>wall_start | wall_end | centred.</summary>
        public string StartRule { get; set; } = "wall_start";
        /// <summary>0 = no cap.</summary>
        public double MaxFirstBay { get; set; }
        public bool DoubleAtEnds { get; set; }
        public double BottomTrackThickness { get; set; }
        public double TopTrackThickness { get; set; }
        public int KingStuds { get; set; } = 1;
        public bool JackStuds { get; set; } = true;
        /// <summary>0 = cripples on the layout stations the opening removed.</summary>
        public double CrippleSpacing { get; set; }
        /// <summary>The header's section depth (z); 0 = its axis sits on the head line, warned.</summary>
        public double HeaderDepth { get; set; }
        /// <summary>The sill's section depth (z); 0 = its axis sits on the sill line, warned.</summary>
        public double SillDepth { get; set; }
        public string StudTypeKey { get; set; }
        public string BottomTrackTypeKey { get; set; }
        public string TopTrackTypeKey { get; set; }
        public string HeaderTypeKey { get; set; }
        public string SillTypeKey { get; set; }
        public List<WallOpeningSpan> Openings { get; set; } = new List<WallOpeningSpan>();
        public List<BlockingRow> Blocking { get; set; } = new List<BlockingRow>();
    }

    public sealed class WallFramingPlan
    {
        public List<FramingMember> Members { get; } = new List<FramingMember>();
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Null when planned; otherwise a stable code saying why nothing was planned.</summary>
        public string Refusal { get; set; }
        public Dictionary<string, int> CountByRole() => FramingPlanSignature.CountByRole(Members);
    }

    public static class WallFramingRules
    {
        private const double Tol = 1e-6;

        private sealed class Vertical
        {
            public double X, Z0, Z1;
            public string Role;
            public int Priority, Opening = -1;
        }

        public static WallFramingPlan Plan(WallFramingInput input, int maxMembers)
        {
            var plan = new WallFramingPlan();
            if (input == null) { plan.Refusal = "no_input"; return plan; }
            double L = input.Length, H = input.Height, s = input.StudSpacing, w = input.StudWidth;
            double tb = input.BottomTrackThickness, tt = input.TopTrackThickness;
            if (!Finite(L) || !Finite(H) || !(L > 0) || !(H > 0)) { plan.Refusal = "wall_has_no_length_or_height"; return plan; }
            if (!Finite(s) || !Finite(w) || !(w > 0) || !(s > w)) { plan.Refusal = "spacing_must_exceed_stud_width"; return plan; }
            if (L < w - Tol) { plan.Refusal = "wall_shorter_than_one_stud"; return plan; }
            if (!(tb >= 0) || !(tt >= 0) || tb + tt >= H) { plan.Refusal = "tracks_thicker_than_wall"; return plan; }
            if (input.KingStuds < 1 || input.KingStuds > 2) { plan.Refusal = "king_studs_must_be_1_or_2"; return plan; }
            string rule = (input.StartRule ?? "wall_start").Trim().ToLowerInvariant();
            if (rule != "wall_start" && rule != "wall_end" && rule != "centred") { plan.Refusal = "unknown_start_rule"; return plan; }
            double cs = input.CrippleSpacing;
            if (!(cs >= 0) || !Finite(cs)) { plan.Refusal = "bad_cripple_spacing"; return plan; }
            double hd = input.HeaderDepth, sd = input.SillDepth;
            if (!(hd >= 0) || !Finite(hd) || !(sd >= 0) || !Finite(sd)) { plan.Refusal = "bad_header_or_sill_depth"; return plan; }

            double zb = tb, zt = H - tt;
            List<WallOpeningSpan> openings = new List<WallOpeningSpan>();
            foreach (WallOpeningSpan o in input.Openings ?? new List<WallOpeningSpan>())
            {
                if (o == null || !Finite(o.Start) || !Finite(o.End) || !Finite(o.Sill) || !Finite(o.Head)) { plan.Refusal = "opening_not_finite"; return plan; }
                double st = Math.Max(0, o.Start), en = Math.Min(L, o.End);
                if (en - st <= Tol || o.Head - o.Sill <= Tol) { plan.Warnings.Add("opening_outside_wall:" + o.Id); continue; }
                openings.Add(new WallOpeningSpan { Id = o.Id, Start = st, End = en, Sill = Math.Max(zb, o.Sill), Head = Math.Min(zt, o.Head) });
            }
            openings = openings.OrderBy(o => o.Start).ToList();
            for (int i = 1; i < openings.Count; i++)
                if (openings[i].Start < openings[i - 1].End - Tol
                    && openings[i].Sill < openings[i - 1].Head - Tol && openings[i - 1].Sill < openings[i].Head - Tol)
                { plan.Refusal = "openings_overlap:" + openings[i - 1].Id + "," + openings[i].Id; return plan; }

            // Bound first, as a double: layout stations + per-opening members + blocking pieces.
            int rows = input.Blocking?.Count ?? 0;
            double layoutCount = L / s + 4;
            double estimate = layoutCount * (1 + rows) + openings.Count * (8 + 2 * (L / Math.Max(w, cs > 0 ? cs : s))) + 4;
            if (estimate > maxMembers) { plan.Refusal = "over_budget"; return plan; }

            // ---- candidate verticals --------------------------------------------------
            var candidates = new List<Vertical>();
            for (int i = 0; i < openings.Count; i++)
            {
                WallOpeningSpan o = openings[i];
                bool reachesTop = o.Head >= zt - Tol;
                if (input.JackStuds)
                {
                    double jz1 = reachesTop ? zt : o.Head;
                    candidates.Add(new Vertical { X = o.Start - w / 2, Z0 = zb, Z1 = jz1, Role = FramingRoles.Jack, Priority = 3, Opening = i });
                    candidates.Add(new Vertical { X = o.End + w / 2, Z0 = zb, Z1 = jz1, Role = FramingRoles.Jack, Priority = 3, Opening = i });
                }
                double baseOffset = input.JackStuds ? w : 0;
                for (int k = 0; k < input.KingStuds; k++)
                {
                    candidates.Add(new Vertical { X = o.Start - baseOffset - w / 2 - k * w, Z0 = zb, Z1 = zt, Role = FramingRoles.King, Priority = 2, Opening = i });
                    candidates.Add(new Vertical { X = o.End + baseOffset + w / 2 + k * w, Z0 = zb, Z1 = zt, Role = FramingRoles.King, Priority = 2, Opening = i });
                }
            }
            candidates.Add(new Vertical { X = w / 2, Z0 = zb, Z1 = zt, Role = FramingRoles.Stud, Priority = 1 });
            candidates.Add(new Vertical { X = L - w / 2, Z0 = zb, Z1 = zt, Role = FramingRoles.Stud, Priority = 1 });
            if (input.DoubleAtEnds)
            {
                candidates.Add(new Vertical { X = 1.5 * w, Z0 = zb, Z1 = zt, Role = FramingRoles.Stud, Priority = 1 });
                candidates.Add(new Vertical { X = L - 1.5 * w, Z0 = zb, Z1 = zt, Role = FramingRoles.Stud, Priority = 1 });
            }
            List<double> layout = LayoutStations(L, s, rule);
            double innerStart = (input.DoubleAtEnds ? 1.5 : 0.5) * w, innerEnd = L - innerStart;
            if (input.MaxFirstBay > Tol && layout.Count > 0)
            {
                // The bay next to each end stud, measured to the nearest layout station inside it.
                var inside = layout.Where(x => x - w / 2 > innerStart + w / 2 - Tol && x + w / 2 < innerEnd - w / 2 + Tol).ToList();
                if (inside.Count > 0)
                {
                    if (inside.Min() - innerStart > input.MaxFirstBay + Tol) layout.Add(innerStart + input.MaxFirstBay);
                    if (innerEnd - inside.Max() > input.MaxFirstBay + Tol) layout.Add(innerEnd - input.MaxFirstBay);
                }
                else if (innerEnd - innerStart > input.MaxFirstBay + Tol)
                    layout.Add((innerStart + innerEnd) / 2);
            }
            foreach (double x in layout)
                candidates.Add(new Vertical { X = x, Z0 = zb, Z1 = zt, Role = FramingRoles.Stud, Priority = 0 });

            // ---- acceptance: bounds, openings, overlap -----------------------------------
            var accepted = new List<Vertical>();
            var removedByOpening = new List<double>();
            foreach (Vertical v in candidates.OrderByDescending(c => c.Priority).ThenBy(c => c.X))
            {
                if (v.X - w / 2 < -Tol || v.X + w / 2 > L + Tol)
                {
                    if (v.Opening >= 0) plan.Warnings.Add("opening_at_wall_end:" + v.Role + ":" + openings[v.Opening].Id);
                    continue;
                }
                if (CrossesOpening(v.X, v.Z0, v.Z1, w, openings))
                {
                    if (v.Role == FramingRoles.Stud) removedByOpening.Add(v.X);
                    else if (v.Opening >= 0) plan.Warnings.Add("member_inside_other_opening:" + v.Role + ":" + openings[v.Opening].Id);
                    continue;
                }
                Vertical clash = accepted.FirstOrDefault(a => Math.Abs(a.X - v.X) < w - Tol && a.Z0 < v.Z1 - Tol && v.Z0 < a.Z1 - Tol);
                if (clash != null)
                {
                    if (v.Role == FramingRoles.King || v.Role == FramingRoles.Jack)
                        plan.Warnings.Add((v.Role == FramingRoles.King ? "king_merged:" : "openings_share_jack:") + openings[v.Opening].Id);
                    continue;
                }
                accepted.Add(v);
            }

            // ---- cripples ------------------------------------------------------------
            var cripples = new List<Vertical>();
            for (int i = 0; i < openings.Count; i++)
            {
                WallOpeningSpan o = openings[i];
                bool header = HeaderFits(o, zt, hd) && o.Head + hd < zt - Tol, sill = SillFits(o, zb, sd) && o.Sill - sd > zb + Tol;
                if (!header && !sill) continue;
                List<double> xs;
                if (cs > Tol)
                {
                    xs = new List<double>();
                    double span = o.End - o.Start;
                    int n = (int)Math.Max(1, Math.Ceiling(span / cs - Tol));
                    for (int k = 1; k < n; k++) xs.Add(o.Start + k * span / n);
                }
                else xs = removedByOpening.Where(x => x - w / 2 >= o.Start - Tol && x + w / 2 <= o.End + Tol).Distinct().ToList();
                foreach (double x in xs.OrderBy(x => x))
                {
                    if (header) cripples.Add(new Vertical { X = x, Z0 = o.Head + hd, Z1 = zt, Role = FramingRoles.Cripple, Opening = i });
                    if (sill) cripples.Add(new Vertical { X = x, Z0 = zb, Z1 = o.Sill - sd, Role = FramingRoles.Cripple, Opening = i });
                }
            }

            // ---- members, in a stable order: verticals by x, then horizontals -----------
            foreach (Vertical v in accepted.Concat(cripples).OrderBy(v => v.X).ThenBy(v => v.Z0))
                plan.Members.Add(new FramingMember { Role = v.Role, TypeKey = input.StudTypeKey, X0 = v.X, Z0 = v.Z0, X1 = v.X, Z1 = v.Z1, Source = v.Opening });

            // Tracks: bottom cut across door-like openings, top full length.
            double cursor = 0;
            foreach (WallOpeningSpan o in openings.Where(o => o.Sill <= zb + Tol))
            {
                if (o.Start - cursor > Tol) plan.Members.Add(Horizontal(FramingRoles.Track, input.BottomTrackTypeKey, cursor, o.Start, tb / 2, -1));
                cursor = Math.Max(cursor, o.End);
            }
            if (L - cursor > Tol) plan.Members.Add(Horizontal(FramingRoles.Track, input.BottomTrackTypeKey, cursor, L, tb / 2, -1));
            plan.Members.Add(Horizontal(FramingRoles.Track, input.TopTrackTypeKey ?? input.BottomTrackTypeKey, 0, L, H - tt / 2, -1));

            // Headers between the kings, sills between the jacks.
            double jackW = input.JackStuds ? w : 0;
            bool headerOnLine = false, sillOnLine = false;
            for (int i = 0; i < openings.Count; i++)
            {
                WallOpeningSpan o = openings[i];
                if (!(o.Head < zt - Tol)) plan.Warnings.Add("opening_reaches_top_no_header:" + o.Id);
                else if (!HeaderFits(o, zt, hd)) plan.Warnings.Add("header_does_not_fit_below_top_track:" + o.Id);
                else
                {
                    plan.Members.Add(Horizontal(FramingRoles.Header, input.HeaderTypeKey ?? input.TopTrackTypeKey ?? input.BottomTrackTypeKey,
                        Math.Max(0, o.Start - jackW), Math.Min(L, o.End + jackW), o.Head + hd / 2, i));
                    headerOnLine |= hd <= Tol;
                }
                if (!(o.Sill > zb + Tol)) continue;
                if (!SillFits(o, zb, sd)) { plan.Warnings.Add("sill_does_not_fit_above_bottom_track:" + o.Id); continue; }
                plan.Members.Add(Horizontal(FramingRoles.Sill, input.SillTypeKey ?? input.BottomTrackTypeKey, o.Start, o.End, o.Sill - sd / 2, i));
                sillOnLine |= sd <= Tol;
            }
            if (headerOnLine) plan.Warnings.Add("no_header_depth:header_axis_on_head_line");
            if (sillOnLine) plan.Warnings.Add("no_sill_depth:sill_axis_on_sill_line");

            // Blocking: split at every vertical spanning the row's height, interrupted across voids.
            List<Vertical> verticals = accepted.Concat(cripples).ToList();
            for (int r = 0; r < rows; r++)
            {
                BlockingRow row = input.Blocking[r];
                if (row == null || !Finite(row.Height) || row.Height <= zb + Tol || row.Height >= zt - Tol)
                { plan.Warnings.Add("blocking_outside_wall:" + r); continue; }
                double h = row.Height;
                List<double> xs = verticals.Where(v => v.Z0 <= h + Tol && v.Z1 >= h - Tol).Select(v => v.X).OrderBy(x => x).ToList();
                for (int k = 1; k < xs.Count; k++)
                {
                    double a = xs[k - 1] + w / 2, b = xs[k] - w / 2;
                    if (b - a <= Tol) continue;
                    double mid = (a + b) / 2;
                    // The framed void: the opening plus its header and sill depths.
                    if (openings.Any(o => mid > o.Start && mid < o.End && h > o.Sill - sd - Tol && h < o.Head + hd + Tol)) continue;
                    plan.Members.Add(Horizontal(FramingRoles.Blocking, row.TypeKey ?? input.StudTypeKey, a, b, h, r));
                }
            }

            if (plan.Members.Count > maxMembers) { plan.Members.Clear(); plan.Refusal = "over_budget"; }
            return plan;
        }

        /// <summary>A header below a head under the top track fits when head + depth reaches no higher than the top track.</summary>
        private static bool HeaderFits(WallOpeningSpan o, double zt, double depth) => o.Head < zt - Tol && o.Head + depth <= zt + Tol;

        /// <summary>A sill above the bottom track fits when sill - depth reaches no lower than the bottom track.</summary>
        private static bool SillFits(WallOpeningSpan o, double zb, double depth) => o.Sill > zb + Tol && o.Sill - depth >= zb - Tol;

        /// <summary>The layout stations of the start rule, inside [0, L], unfiltered.</summary>
        public static List<double> LayoutStations(double length, double spacing, string startRule)
        {
            var xs = new List<double>();
            if (!(length > 0) || !(spacing > 0)) return xs;
            int n = (int)Math.Floor(length / spacing + Tol);
            switch ((startRule ?? "wall_start").Trim().ToLowerInvariant())
            {
                case "wall_end":
                    for (int k = 0; k <= n; k++) xs.Add(length - k * spacing);
                    break;
                case "centred":
                    double mid = length / 2;
                    xs.Add(mid);
                    for (int k = 1; k * spacing <= mid + Tol; k++) { xs.Add(mid - k * spacing); xs.Add(mid + k * spacing); }
                    break;
                default:
                    for (int k = 0; k <= n; k++) xs.Add(k * spacing);
                    break;
            }
            return xs.OrderBy(x => x).ToList();
        }

        /// <summary>True when a vertical member at x, w wide, spanning [z0, z1], enters any opening's void.</summary>
        public static bool CrossesOpening(double x, double z0, double z1, double w, IEnumerable<WallOpeningSpan> openings)
        {
            foreach (WallOpeningSpan o in openings)
                if (x + w / 2 > o.Start + Tol && x - w / 2 < o.End - Tol && z1 > o.Sill + Tol && z0 < o.Head - Tol)
                    return true;
            return false;
        }

        private static FramingMember Horizontal(string role, string type, double x0, double x1, double z, int source)
            => new FramingMember { Role = role, TypeKey = type, X0 = x0, Z0 = z, X1 = x1, Z1 = z, Source = source };

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
