// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free rules behind three
// horizun_manage_links operations: which importer a path goes to (add kind), when
// acquire_coordinates must refuse before Revit is asked, and how scan_deviation
// judges one face from its sampled point distances.
//
// WHY THE FACE RULE IS STRICT. A scan does not see every face: the top of a wall
// under a slab, the side of a column against a wall. A face that gathered too few
// points was not measured - reporting it "ok" would turn a hole in the scan into a
// pass. So the face is not_measured, the element that has one is at best
// partially_measured, and only elements whose every face was measured and within
// tolerance are "ok".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class LinkSurveyRules
    {
        /// <summary>A face with fewer sampled points than this is not_measured.</summary>
        public const int MinPointsPerFace = 20;
        /// <summary>Upper bound of points requested from Revit per face (GetPoints numPoints).</summary>
        public const int MaxPointsPerFace = 5000;
        /// <summary>Requested average spacing between returned points (GetPoints averageDistance), mm.</summary>
        public const double AverageDistanceMm = 20;
        public const double DefaultToleranceMm = 10;
        public const int MaxElements = 200;
        public const int MaxFacesPerElement = 60;

        /// <summary>
        /// Half-thickness of the slab sampled around each face. Points farther than this from the
        /// face plane are never seen, so a deviation larger than the band cannot be measured; the
        /// reply publishes the band for that reason.
        /// </summary>
        public static double BandMm(double toleranceMm) => Math.Max(3 * toleranceMm, 30);

        /// <summary>
        /// add: which importer the path goes to. An explicit kind must agree with the extension -
        /// a .rcp sent as ifc is a mistake the caller should hear about, not a guess. Unknown
        /// extensions without a kind fall to "rvt", whose own validation names the problem.
        /// </summary>
        public static string AddKind(string kind, string path, out string error)
        {
            error = null;
            string ext = string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetExtension(path.Trim()).ToLowerInvariant();
            string byExt = ext == ".rcp" || ext == ".rcs" ? "point_cloud" : ext == ".ifc" ? "ifc" : ext == ".rvt" ? "rvt" : null;
            string k = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
            if (k == null) return byExt ?? "rvt";
            if (k != "rvt" && k != "point_cloud" && k != "ifc")
            {
                error = "kind '" + kind + "' is not one add understands. Known: rvt, point_cloud, ifc.";
                return null;
            }
            if (byExt != null && byExt != k)
            {
                error = "kind '" + k + "' does not match the path's extension '" + ext + "' (" + byExt +
                        "). rvt takes .rvt, point_cloud takes .rcp/.rcs, ifc takes .ifc. Nothing was linked.";
                return null;
            }
            if (byExt == null && !string.IsNullOrWhiteSpace(path))
            {
                error = "kind '" + k + "' needs a path ending in " +
                        (k == "rvt" ? ".rvt" : k == "ifc" ? ".ifc" : ".rcp or .rcs") + ". Nothing was linked.";
                return null;
            }
            return k;
        }

        /// <summary>
        /// acquire_coordinates: the refusals that are known before Revit is asked. Revit's own
        /// documentation lists "Cannot acquire coordinates from a model placed multiple times" and
        /// "The coordinate system of the selected model are the same as the host model" among
        /// Document.AcquireCoordinates' exceptions, so both are said by name here, with the ids.
        /// Returns null when the operation may be rehearsed.
        /// </summary>
        public static string AcquireRefusal(long instanceId, IList<long> instancesOfSameType, double? siteDeltaMm, double toleranceMm)
        {
            if (instancesOfSameType != null && instancesOfSameType.Count > 1)
                return "the link type of instance " + instanceId + " is placed " + instancesOfSameType.Count + " times (" +
                       string.Join(", ", instancesOfSameType) + "). Revit refuses to acquire coordinates from a model placed " +
                       "multiple times (RevitAPI Document.AcquireCoordinates), so no instance of it can be the source; remove " +
                       "the extra placements or acquire from another link. Nothing was written.";
            if (siteDeltaMm.HasValue && siteDeltaMm.Value <= toleranceMm)
                return "instance " + instanceId + " already shares the host's coordinates (same_site delta " +
                       Math.Round(siteDeltaMm.Value, 3) + " mm <= " + toleranceMm + " mm); acquiring would change nothing " +
                       "and Revit refuses it. Nothing was written.";
            return null;
        }

        /// <summary>The distribution of face-to-point distances (signed, mm) and the face's state.</summary>
        public static JObject FaceVerdict(IList<double> signedMm, double toleranceMm, int minPoints)
        {
            int n = signedMm?.Count ?? 0;
            var o = new JObject { ["points"] = n };
            if (n < minPoints)
            {
                o["state"] = "not_measured";
                o["reason"] = "too_few_points";
                o["note"] = n + " point(s) near the face, " + minPoints + " needed: the scan did not see it well enough to judge.";
                return o;
            }
            var abs = signedMm.Select(Math.Abs).OrderBy(x => x).ToList();
            double p95 = Percentile(abs, 0.95);
            o["mean_mm"] = Math.Round(signedMm.Average(), 2);
            o["mean_abs_mm"] = Math.Round(abs.Average(), 2);
            o["median_abs_mm"] = Math.Round(Percentile(abs, 0.5), 2);
            o["p95_abs_mm"] = Math.Round(p95, 2);
            o["max_abs_mm"] = Math.Round(abs[abs.Count - 1], 2);
            o["within_tolerance_share"] = Math.Round(abs.Count(x => x <= toleranceMm) / (double)n, 4);
            o["state"] = p95 <= toleranceMm ? "ok" : "deviates";
            return o;
        }

        /// <summary>ok only when every face was measured and within tolerance.</summary>
        public static string ElementState(IEnumerable<string> faceStates)
        {
            var s = (faceStates ?? Enumerable.Empty<string>()).ToList();
            if (s.Any(x => x == "deviates")) return "deviates";
            if (s.Count == 0 || s.All(x => x != "ok")) return "not_measured";
            return s.All(x => x == "ok") ? "ok" : "partially_measured";
        }

        /// <summary>passes only when every element is ok; any deviation fails; anything unmeasured leaves it open.</summary>
        public static string Verdict(IEnumerable<string> elementStates)
        {
            var s = (elementStates ?? Enumerable.Empty<string>()).ToList();
            if (s.Any(x => x == "deviates")) return "fails";
            if (s.Count > 0 && s.All(x => x == "ok")) return "passes";
            return "not_decidable";
        }

        /// <summary>Nearest-rank percentile of an ascending list.</summary>
        public static double Percentile(IList<double> ascending, double q)
        {
            if (ascending == null || ascending.Count == 0) return 0;
            int rank = (int)Math.Ceiling(q * ascending.Count);
            return ascending[Math.Min(ascending.Count, Math.Max(1, rank)) - 1];
        }
    }
}
