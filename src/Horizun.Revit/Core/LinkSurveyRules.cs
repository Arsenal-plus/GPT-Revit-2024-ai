// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free rules behind three
// horizun_manage_links operations: which importer a path goes to (add kind), when
// acquire_coordinates must refuse before Revit is asked, and how scan_deviation
// judges one face from its sampled point distances.
//
// WHY THE FACE RULE IS STRICT. A scan does not see every face: the top of a wall
// under a slab, the side of a column against a wall. A face that gathered too few
// points - in number, or for its area - was not measured, and reporting it "ok"
// would turn a hole in the scan into a pass. So the face is not_measured, the
// element that has one (sampled or not: non-planar faces and faces beyond the
// per-element limit count too) is at best partially_measured, and only elements
// whose every face was measured and within tolerance are "ok".
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
        /// <summary>
        /// A face whose points are fewer than this share of what a fully scanned face returns at the
        /// requested spacing is not_measured (low_coverage). A stated rule, not a measured one.
        /// </summary>
        public const double MinCoverageShare = 0.1;
        /// <summary>Share of the element's thickness behind a face that the sampled slab may reach inward.</summary>
        public const double InwardShareOfThickness = 0.4;
        public const double DefaultToleranceMm = 10;
        public const int MaxElements = 200;
        public const int MaxFacesPerElement = 60;

        /// <summary>
        /// Half-thickness of the slab sampled around each face, OUTWARD. Points farther than this from
        /// the face plane are never seen, so a deviation larger than the band cannot be measured; the
        /// reply publishes the band for that reason.
        /// </summary>
        public static double BandMm(double toleranceMm) => Math.Max(3 * toleranceMm, 30);

        /// <summary>
        /// How far the slab reaches INTO the element. Inward, the band would pick up the element's own
        /// opposite face once the tolerance nears its thickness (a 100 mm partition scanned from both
        /// rooms), so it is capped below half the thickness behind the face. Unknown thickness: the band.
        /// </summary>
        public static double InwardBandMm(double bandMm, double? thicknessBehindMm)
            => thicknessBehindMm.HasValue && thicknessBehindMm.Value > 0
                ? Math.Min(bandMm, InwardShareOfThickness * thicknessBehindMm.Value) : bandMm;

        /// <summary>Points a fully scanned face returns at the requested spacing, capped by the request's maximum.</summary>
        public static double ExpectedPoints(double areaM2, double averageDistanceMm, int maxPoints)
        {
            if (!(areaM2 > 0) || !(averageDistanceMm > 0)) return 0;
            double spacingM = averageDistanceMm / 1000.0;
            return Math.Min(maxPoints, areaM2 / (spacingM * spacingM));
        }

        /// <summary>
        /// Which frame the returned points are in: the one where most of them fall inside the sampled
        /// volume. "identity" when there is only one frame; null (undetermined) when neither frame holds
        /// half the points, or when BOTH do under a non-identity transform - a displacement smaller than
        /// the band cannot tell the frames apart, and picking one would be an assumption.
        /// </summary>
        public static string PointFrame(bool identity, int returned, int insideRaw, int insideMoved)
        {
            if (identity) return returned == 0 || insideRaw * 2 >= returned ? "identity" : null;
            if (returned == 0) return "identity";
            bool raw = insideRaw * 2 >= returned, moved = insideMoved * 2 >= returned;
            if (raw == moved) return null;
            return moved ? "instance_transform" : "raw";
        }

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
        /// acquire_coordinates: the refusals that are known before Revit is asked. RevitAPI lists
        /// "Cannot acquire coordinates from a model placed multiple times" and "The coordinate system
        /// of the selected model are the same as the host model" among Document.AcquireCoordinates'
        /// exceptions. A type placed several times is refused here only when no instance is named:
        /// with a named instance the rehearsal (a real transaction, rolled back) asks Revit itself,
        /// so its own answer is what the caller hears. Returns null when the operation may be rehearsed.
        /// </summary>
        public static string AcquireRefusal(long instanceId, IList<long> instancesOfSameType, double? siteDeltaMm, double toleranceMm,
                                            bool instanceNamed = false)
        {
            if (!instanceNamed && instancesOfSameType != null && instancesOfSameType.Count > 1)
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

        /// <summary>
        /// The distribution of face-to-point distances (signed, mm) and the face's state. expectedPoints
        /// (from ExpectedPoints) adds the coverage rule: a face judged on a patch of itself is not measured.
        /// </summary>
        public static JObject FaceVerdict(IList<double> signedMm, double toleranceMm, int minPoints, double expectedPoints = 0)
        {
            int n = signedMm?.Count ?? 0;
            var o = new JObject { ["points"] = n };
            if (expectedPoints > 0) o["coverage_share"] = Math.Round(Math.Min(1, n / expectedPoints), 4);
            if (n < minPoints)
            {
                o["state"] = "not_measured";
                o["reason"] = "too_few_points";
                o["note"] = n + " point(s) near the face, " + minPoints + " needed: the scan did not see it well enough to judge.";
                return o;
            }
            if (expectedPoints > 0 && n < MinCoverageShare * expectedPoints)
            {
                o["state"] = "not_measured";
                o["reason"] = "low_coverage";
                o["note"] = n + " point(s) where a fully scanned face returns about " + Math.Round(expectedPoints) +
                            ": under " + MinCoverageShare + " of it, the face was seen in patches and is not judged.";
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

        /// <summary>
        /// The same, with the faces nobody sampled (non-planar, beyond MaxFacesPerElement) counted as
        /// not_measured: an element with any of them is at best partially_measured.
        /// </summary>
        public static string ElementState(IEnumerable<string> measuredFaceStates, int unsampledFaces)
            => ElementState((measuredFaceStates ?? Enumerable.Empty<string>())
                            .Concat(Enumerable.Repeat("not_measured", Math.Max(0, unsampledFaces))));

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
