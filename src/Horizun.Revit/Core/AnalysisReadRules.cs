// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ANALYSIS READS, AS REVIT COMPUTED THEM AND NOTHING MORE.
//
// Two read-only questions share this file because they share one trap: a
// number that was never computed looks exactly like a number that was.
//
// MEP. A duct system whose type is set to calculate nothing still publishes
// sections, and their flow, velocity and pressure loss read as 0. A report that
// compared those zeros against a velocity limit would call the system "ok" -
// it would be judging a calculation nobody ran. So the calculation level is
// read FIRST and decides which numbers may be judged at all: None, Performance
// and Volume are "not calculated", never "ok"; Flow claims flow and nothing
// else; only All claims velocity and pressure. A limit the caller gave whose
// value could not be read is NAMED as unmeasured, never counted as a pass, and
// a system nobody calculated weakens the aggregate instead of vanishing from it.
// A system Revit does not call well connected is not read either: the API calls
// its calculated values invalid - not understated - so they judge nothing.
//
// STRUCTURE. A member end is CONNECTED when another analytical element reaches
// it within the tolerance: a point ON another member's real curve (a secondary
// beam framing into mid-girder is connected; a node-to-node test would flag
// every one of them), an analytical link, a panel EDGE, or a panel SURFACE (a
// flat-slab column top sits inside the slab, not on its edge). An end that no
// element reaches but a boundary condition does is SUPPORTED - a column base on
// its support is not a gap. What is left is UNCONNECTED: a near-miss, or an
// intended free end such as a cantilever tip, which this read cannot tell apart
// and does not pretend to - nearest_mm says how far the nearest element is.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class AnalysisReadRules
    {
        public const string Calculated = "calculated";
        public const string FlowOnly = "flow_only";
        public const string NotCalculated = "not_calculated";
        public const string Unreadable = "unreadable";

        /// <summary>
        /// The status a system's SystemCalculationLevel allows us to claim. Volume
        /// claims neither flow nor pressure here: it is not a level whose flow
        /// numbers this bridge has seen proved, and under-claiming is the honest
        /// failure. An unknown or unread level is unreadable, not calculated.
        /// </summary>
        public static string CalculationStatus(string level)
        {
            switch (level)
            {
                case "All": return Calculated;
                case "Flow": return FlowOnly;
                case "None":
                case "Performance":
                case "Volume": return NotCalculated;
                default: return Unreadable;
            }
        }

        public static bool FlowClaimed(string status) => status == Calculated || status == FlowOnly;

        /// <summary>
        /// Velocity is claimed at All only. The API describes Flow as "System
        /// calculation is only for flow"; whether Revit fills MEPSection.Velocity
        /// at that level has not been measured, and a velocity limit judged on a
        /// number nobody computed is the zero-passes-the-limit trap. Under-claim
        /// until a live run proves otherwise.
        /// </summary>
        public static bool VelocityClaimed(string status) => status == Calculated;

        public static bool PressureClaimed(string status) => status == Calculated;

        /// <summary>
        /// Whether a system's calculated values may be read at all. RevitAPI.xml
        /// (2023 and 2026), MechanicalSystem/PipingSystem.IsWellConnected: "If the
        /// system is not well connected, parameters which need to be calculated
        /// are invalid." Invalid, not understated: judged against a limit they
        /// could fake a breach as easily as a pass. The critical path is the path
        /// of greatest pressure loss, chosen from those same values, so it is not
        /// read either. Connectivity that would not read proves nothing.
        /// </summary>
        public static bool ValuesValid(bool? wellConnected) => wellConnected == true;

        public static string SystemCoverage(string status, bool criticalPathRead, int unreadableSections,
                                            int unmeasuredLimits, int unreadQuantities, bool? wellConnected)
            => SystemCoverage(status, criticalPathRead, unreadableSections, unmeasuredLimits, unreadQuantities,
                              wellConnected, out _);

        /// <summary>
        /// The coverage word of one system row, and <paramref name="why"/> it is
        /// not complete (null when it is): the row publishes both, and the
        /// aggregate's reason is the row's own cause, not its verdict. A system
        /// Revit did not calculate is UNREADABLE, never not_applicable: the
        /// question "does it exceed the limits" arises, and the model has no
        /// computed answer to give. not_applicable is dropped by
        /// StructuralCoverage.Weakest once anything else was measured, so one
        /// judged system beside nine unjudged ones would publish complete - the
        /// aggregate "ok" over systems nobody judged. A system whose values the
        /// API calls invalid (not well connected, or connectivity unread) is
        /// unreadable for the same reason. <paramref name="criticalPathRead"/>
        /// means at least one critical-path section was read.
        /// </summary>
        public static string SystemCoverage(string status, bool criticalPathRead, int unreadableSections,
                                            int unmeasuredLimits, int unreadQuantities, bool? wellConnected,
                                            out string why)
        {
            if (status == NotCalculated)
            {
                why = "not_calculated: the system type calculates nothing this read may judge, so no number was read.";
                return StructuralCoverage.Unreadable;
            }
            if (status != Calculated && status != FlowOnly)
            {
                why = "calculation_level_unreadable: the system type's calculation level could not be read, so no " +
                      "number was read.";
                return StructuralCoverage.Unreadable;
            }
            if (!ValuesValid(wellConnected))
            {
                why = (wellConnected == false
                          ? "not_well_connected: Revit reports the system not well connected"
                          : "connectivity_unreadable: whether the system is well connected could not be read") +
                      "; the API calls the calculated values of a system that is not well connected invalid, so " +
                      "none was read or judged.";
                return StructuralCoverage.Unreadable;
            }
            if (!criticalPathRead)
            {
                why = "critical_path_unread: no critical-path section could be read, so nothing was judged.";
                return StructuralCoverage.Unreadable;
            }
            var causes = new List<string>();
            if (status == FlowOnly)
                causes.Add("flow_only: the type calculates flow only; velocity, pressure loss and friction were not read");
            if (unreadableSections > 0) causes.Add(unreadableSections + " critical-path section(s) would not read");
            if (unmeasuredLimits > 0) causes.Add(unmeasuredLimits + " section limit(s) unmeasured (unmeasured_limits)");
            if (unreadQuantities > 0)
                causes.Add(unreadQuantities + " claimed quantity(ies) came back null (unread_quantities)");
            if (causes.Count == 0) { why = null; return StructuralCoverage.Complete; }
            why = string.Join("; ", causes) + ".";
            return StructuralCoverage.Partial;
        }

        /// <summary>
        /// The verdict of a calculated system. Nothing is judged - neither a pass
        /// nor a breach - on a system whose calculated values the API calls
        /// invalid (<see cref="ValuesValid"/>), nor when no critical-path section
        /// was read: "numbers read" is claimed only over sections that were.
        /// </summary>
        public static string SystemVerdict(bool breached, int limitCount, int unmeasuredLimits, bool? wellConnected,
                                           int sectionsRead, int sectionCount, out string means)
        {
            if (!ValuesValid(wellConnected))
            {
                means = (wellConnected == false
                    ? "Revit reports the system NOT well connected"
                    : "whether the system is well connected could not be read") +
                    ": the API calls the calculated values of a system that is not well connected invalid (and the " +
                    "critical path is chosen from them), so nothing was read or judged - neither a pass nor a breach.";
                return wellConnected == false ? "not_well_connected" : "connectivity_unreadable";
            }
            if (sectionsRead <= 0)
            {
                means = "no critical-path section could be read, so no number was read or judged.";
                return "critical_path_unreadable";
            }
            string over = sectionsRead >= sectionCount ? "every critical-path section"
                : sectionsRead + " of " + sectionCount + " critical-path sections";
            if (breached)
            {
                means = "at least one critical-path section exceeds a limit you gave (read on " + over + ").";
                return "beyond_limits";
            }
            if (limitCount == 0)
            {
                means = "numbers read on " + over + "; nothing judged because no limits were given.";
                return "no_limits_given";
            }
            if (unmeasuredLimits > 0)
            {
                means = "no measured value exceeds a limit, but the listed limits were not measured, so this is not a pass.";
                return "limits_partly_unmeasured";
            }
            means = "every limit you gave was measured on every critical-path section.";
            return "within_limits";
        }

        /// <summary>
        /// The one defined name that <paramref name="input"/> spells (case
        /// ignored), or null. Enum.TryParse OR-combines comma lists even on enums
        /// that are not [Flags] and accepts signed numbers: 'SupplyAir,ReturnAir'
        /// parses to ExhaustAir and '-5' to a value nothing matches, so the call
        /// would silently read a different classification, or none.
        /// </summary>
        public static string ExactName(string input, IEnumerable<string> names)
        {
            if (string.IsNullOrWhiteSpace(input) || names == null) return null;
            string s = input.Trim();
            foreach (string n in names)
                if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        public static readonly string[] LimitKeys = { "max_velocity_m_s", "max_pressure_loss_pa", "max_friction_pa_per_m" };

        /// <summary>
        /// Parse the caller's limits. Unknown keys are refused rather than ignored:
        /// a misspelt limit silently dropped is a check the caller believes ran.
        /// Returns an error message, or null when every key was understood.
        /// </summary>
        public static string ParseLimits(JToken token, out Dictionary<string, double> limits)
        {
            limits = new Dictionary<string, double>(StringComparer.Ordinal);
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JObject o)) return "limits must be an object keyed by " + string.Join(", ", LimitKeys) + ".";
            foreach (JProperty p in o.Properties())
            {
                if (Array.IndexOf(LimitKeys, p.Name) < 0)
                    return "limits carries '" + p.Name + "'; the known limits are " + string.Join(", ", LimitKeys) + ".";
                if (p.Value.Type != JTokenType.Integer && p.Value.Type != JTokenType.Float)
                    return "limits." + p.Name + " must be a number.";
                double v = p.Value.Value<double>();
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
                    return "limits." + p.Name + " must be a positive number.";
                limits[p.Name] = v;
            }
            return null;
        }

        public sealed class SectionReading
        {
            public int Number;
            public double? FlowLs;
            public double? VelocityMs;
            public double? PressureLossPa;
            public double? FrictionPaPerM;
        }

        /// <summary>
        /// The limits this section exceeds. A limit whose value is unreadable - or
        /// whose quantity the calculation level does not claim - goes into
        /// <paramref name="unmeasured"/> as "section#limit": it was not checked,
        /// and a list of breaches that omitted it would read as a pass.
        /// </summary>
        public static JArray Breaches(SectionReading s, string status, IDictionary<string, double> limits,
                                      List<string> unmeasured)
        {
            var found = new JArray();
            if (limits == null) return found;
            foreach (KeyValuePair<string, double> limit in limits)
            {
                double? value;
                bool claimed;
                switch (limit.Key)
                {
                    case "max_velocity_m_s": value = s.VelocityMs; claimed = VelocityClaimed(status); break;
                    case "max_pressure_loss_pa": value = s.PressureLossPa; claimed = PressureClaimed(status); break;
                    default: value = s.FrictionPaPerM; claimed = PressureClaimed(status); break;
                }
                if (!claimed || !value.HasValue)
                {
                    unmeasured?.Add(s.Number + "#" + limit.Key);
                    continue;
                }
                if (value.Value > limit.Value)
                    found.Add(new JObject
                    {
                        ["limit"] = limit.Key,
                        ["limit_value"] = limit.Value,
                        ["measured"] = Math.Round(value.Value, 3)
                    });
            }
            return found;
        }

        /// <summary>
        /// The quantities the level claims that came back null, as "section#quantity".
        /// Without limits nothing else would notice them, and a null under a
        /// complete word reads as a quantity that was read.
        /// </summary>
        public static List<string> UnreadQuantities(SectionReading s, string status)
        {
            var unread = new List<string>();
            if (FlowClaimed(status) && !s.FlowLs.HasValue) unread.Add(s.Number + "#flow");
            if (VelocityClaimed(status) && !s.VelocityMs.HasValue) unread.Add(s.Number + "#velocity");
            if (PressureClaimed(status) && !s.PressureLossPa.HasValue) unread.Add(s.Number + "#pressure_loss");
            if (PressureClaimed(status) && !s.FrictionPaPerM.HasValue) unread.Add(s.Number + "#friction");
            return unread;
        }

        // ------------------------------------------------------ analytical nodes

        /// <summary>
        /// An analytical curve (a member, a link, a panel's edges) as a polyline in
        /// millimetres. A curved element's polyline is Revit's DISPLAY tessellation,
        /// whose chords sag millimetres off the real curve: such a target carries
        /// <see cref="ExactMm"/> (the distance to the real curve) and a
        /// <see cref="SlackMm"/> bounding the chord error, and any end the
        /// polyline puts within tolerance + slack is decided by the real curve.
        /// </summary>
        public sealed class AnalyticalPolyline
        {
            public long ElementId;
            public List<double[]> Points = new List<double[]>();
            public double SlackMm;
            public Func<double[], double?> ExactMm;
        }

        /// <summary>A planar surface in millimetres: a panel (outer contour less its openings) or an area support.</summary>
        public sealed class AnalyticalSurface
        {
            public long ElementId;
            public List<double[]> Outer = new List<double[]>();
            public List<List<double[]>> Holes = new List<List<double[]>>();
        }

        /// <summary>A boundary condition: a point, a polyline (line support) or a surface (area support).</summary>
        public sealed class SupportTarget
        {
            public long ElementId;
            public List<double[]> Points = new List<double[]>();
            public AnalyticalSurface Surface;
        }

        public sealed class NodeGap
        {
            public long ElementId;
            public int End;
            public double[] Point;
            public double? NearestMm;
            public long? NearestElementId;
        }

        /// <summary>The ends no analytical element reaches: supported by a boundary condition, or unconnected.</summary>
        public sealed class EndClassification
        {
            public List<NodeGap> Unconnected = new List<NodeGap>();
            public List<NodeGap> Supported = new List<NodeGap>();
        }

        /// <summary>
        /// Brute force costs ends x segments distance checks. Above this bound the
        /// check is NOT run and the caller is told to narrow it - a gap check that
        /// silently sampled would report the gaps it happened to look at.
        /// </summary>
        public const long MaxPairChecks = 50000000;

        public static long PairChecks(IList<AnalyticalPolyline> members, IList<AnalyticalPolyline> targets,
                                      IList<AnalyticalSurface> surfaces = null, IList<SupportTarget> supports = null)
        {
            long ends = 0, segments = 0;
            foreach (AnalyticalPolyline m in members) if (m.Points.Count >= 2) ends += 2;
            foreach (AnalyticalPolyline t in targets) segments += Math.Max(0, t.Points.Count - 1);
            if (surfaces != null)
                foreach (AnalyticalSurface s in surfaces) segments += Vertices(s);
            if (supports != null)
                foreach (SupportTarget s in supports) segments += Math.Max(1, s.Points.Count) + (s.Surface == null ? 0 : Vertices(s.Surface));
            return ends * segments;
        }

        private static long Vertices(AnalyticalSurface s)
        {
            long n = s.Outer.Count;
            foreach (List<double[]> h in s.Holes) n += h.Count;
            return n;
        }

        /// <summary>
        /// Every member end farther than <paramref name="toleranceMm"/> from every
        /// OTHER element in <paramref name="targets"/>, with the nearest distance
        /// it did find (null when there is no other element). No surfaces and no
        /// supports: see <see cref="ClassifyEnds"/>.
        /// </summary>
        public static List<NodeGap> NodeGaps(IList<AnalyticalPolyline> members, IList<AnalyticalPolyline> targets,
                                             double toleranceMm)
        {
            return ClassifyEnds(members, targets, null, null, toleranceMm).Unconnected;
        }

        /// <summary>
        /// The member ends no other analytical element reaches within
        /// <paramref name="toleranceMm"/> - not a curve (real curve for curved
        /// targets), not a panel surface - split into those a boundary condition
        /// reaches (supported) and the rest (unconnected).
        /// </summary>
        public static EndClassification ClassifyEnds(IList<AnalyticalPolyline> members, IList<AnalyticalPolyline> targets,
            IList<AnalyticalSurface> surfaces, IList<SupportTarget> supports, double toleranceMm)
        {
            var result = new EndClassification();
            foreach (AnalyticalPolyline m in members)
            {
                if (m.Points.Count < 2) continue;
                for (int end = 0; end < 2; end++)
                {
                    double[] p = end == 0 ? m.Points[0] : m.Points[m.Points.Count - 1];
                    double best = double.MaxValue;
                    long? bestId = null;
                    bool connected = false;
                    foreach (AnalyticalPolyline t in targets)
                    {
                        if (t.ElementId == m.ElementId) continue;
                        double d = PolylineDistance(p, t.Points);
                        if (t.ExactMm != null && d <= toleranceMm + t.SlackMm)
                        {
                            double? exact = t.ExactMm(p);
                            if (exact.HasValue) d = exact.Value;
                        }
                        if (d < best) { best = d; bestId = t.ElementId; }
                        if (d <= toleranceMm) { connected = true; break; }
                    }
                    if (!connected && surfaces != null)
                        foreach (AnalyticalSurface s in surfaces)
                            if (s.ElementId != m.ElementId && OnSurface(p, s, toleranceMm)) { connected = true; break; }
                    if (connected) continue;

                    var gap = new NodeGap
                    {
                        ElementId = m.ElementId,
                        End = end,
                        Point = p,
                        NearestMm = bestId.HasValue ? Math.Round(best, 2) : (double?)null,
                        NearestElementId = bestId
                    };
                    SupportTarget support = SupportAt(p, supports, toleranceMm, out double supportMm);
                    if (support != null)
                    {
                        result.Supported.Add(new NodeGap
                        {
                            ElementId = m.ElementId, End = end, Point = p,
                            NearestMm = Math.Round(supportMm, 2), NearestElementId = support.ElementId
                        });
                    }
                    else result.Unconnected.Add(gap);
                }
            }
            return result;
        }

        private static SupportTarget SupportAt(double[] p, IList<SupportTarget> supports, double tol, out double distance)
        {
            distance = double.MaxValue;
            if (supports == null) return null;
            foreach (SupportTarget s in supports)
            {
                double d = s.Points.Count == 1 ? Distance(p, s.Points[0])
                         : s.Points.Count >= 2 ? PolylineDistance(p, s.Points) : double.MaxValue;
                if (s.Surface != null && OnSurface(p, s.Surface, tol)) d = Math.Min(d, 0);
                if (d <= tol) { distance = d; return s; }
            }
            return null;
        }

        public static double PolylineDistance(double[] p, IList<double[]> pts)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double d = PointSegment(p, pts[i], pts[i + 1]);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>
        /// True when <paramref name="p"/> lies within <paramref name="tol"/> of the
        /// surface's plane and inside its outer contour, and not inside an opening
        /// (an end on an opening's edge is on the panel). A contour that is not
        /// planar within max(tol, 1 mm) is not treated as a surface at all: its
        /// edges still count, its interior is not guessed.
        /// </summary>
        public static bool OnSurface(double[] p, AnalyticalSurface s, double tol)
        {
            if (s == null || s.Outer.Count < 3) return false;
            if (!PlaneOf(s.Outer, out double[] origin, out double[] n)) return false;
            double flat = Math.Max(tol, 1.0);
            foreach (double[] v in s.Outer) if (Math.Abs(Dot(Sub(v, origin), n)) > flat) return false;
            if (Math.Abs(Dot(Sub(p, origin), n)) > tol) return false;
            Basis(n, out double[] u, out double[] w);
            double px = Dot(Sub(p, origin), u), py = Dot(Sub(p, origin), w);
            if (!Inside(px, py, s.Outer, origin, u, w)) return false;
            foreach (List<double[]> hole in s.Holes)
                if (hole.Count >= 3 && Inside(px, py, hole, origin, u, w) && PolylineDistance(p, Closed(hole)) > tol)
                    return false;
            return true;
        }

        private static List<double[]> Closed(List<double[]> loop)
        {
            var c = new List<double[]>(loop);
            if (loop.Count > 0 && Distance(loop[0], loop[loop.Count - 1]) > 1e-9) c.Add(loop[0]);
            return c;
        }

        // Newell's method: robust for any simple polygon, convex or not.
        private static bool PlaneOf(List<double[]> pts, out double[] origin, out double[] n)
        {
            origin = pts[0];
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double[] a = pts[i], b = pts[(i + 1) % pts.Count];
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            n = len < 1e-9 ? null : new[] { nx / len, ny / len, nz / len };
            return n != null;
        }

        private static void Basis(double[] n, out double[] u, out double[] w)
        {
            double[] a = Math.Abs(n[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            u = Normalize(Cross(n, a));
            w = Cross(n, u);
        }

        // Even-odd ray casting in the plane's own 2D frame.
        private static bool Inside(double x, double y, List<double[]> loop, double[] origin, double[] u, double[] w)
        {
            bool inside = false;
            int count = loop.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double xi = Dot(Sub(loop[i], origin), u), yi = Dot(Sub(loop[i], origin), w);
                double xj = Dot(Sub(loop[j], origin), u), yj = Dot(Sub(loop[j], origin), w);
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Normalize(double[] a)
        {
            double l = Math.Sqrt(Dot(a, a));
            return new[] { a[0] / l, a[1] / l, a[2] / l };
        }
        private static double Distance(double[] a, double[] b) => Math.Sqrt(Dot(Sub(a, b), Sub(a, b)));

        public static double PointSegment(double[] p, double[] a, double[] b)
        {
            double abx = b[0] - a[0], aby = b[1] - a[1], abz = b[2] - a[2];
            double apx = p[0] - a[0], apy = p[1] - a[1], apz = p[2] - a[2];
            double len2 = abx * abx + aby * aby + abz * abz;
            double t = len2 <= 0 ? 0 : (apx * abx + apy * aby + apz * abz) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = apx - t * abx, dy = apy - t * aby, dz = apz - t * abz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
