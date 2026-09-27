// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_manage_links acquire_coordinates / add kind=point_cloud|ifc /
// scan_deviation: the Revit-free rules and the contract wiring. Whether Revit
// acquires, links and samples as planned is a live question, answered by
// scripts/live-probes/links-survey.probes.ps1.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class LinkSurveyRulesTests
    {
        [Theory]
        [InlineData(null, @"C:\x\a.rcp", "point_cloud")]
        [InlineData(null, @"C:\x\a.RCS", "point_cloud")]
        [InlineData(null, @"C:\x\a.ifc", "ifc")]
        [InlineData(null, @"C:\x\a.rvt", "rvt")]
        [InlineData(null, @"C:\x\a.dwg", "rvt")] // falls to rvt, whose own validation names the problem
        [InlineData("ifc", @"C:\x\a.ifc", "ifc")]
        [InlineData("POINT_CLOUD", @"C:\x\a.rcp", "point_cloud")]
        public void Add_kind_follows_the_extension(string kind, string path, string expected)
        {
            Assert.Equal(expected, LinkSurveyRules.AddKind(kind, path, out string error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("ifc", @"C:\x\a.rcp")]
        [InlineData("point_cloud", @"C:\x\a.ifc")]
        [InlineData("rvt", @"C:\x\a.ifc")]
        [InlineData("e57", @"C:\x\a.e57")]
        [InlineData("ifc", @"C:\x\a.txt")]
        public void A_kind_that_disagrees_with_the_path_is_refused_by_name(string kind, string path)
        {
            Assert.Null(LinkSurveyRules.AddKind(kind, path, out string error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Acquire_refuses_a_type_placed_several_times_naming_every_instance()
        {
            string r = LinkSurveyRules.AcquireRefusal(11, new List<long> { 11, 12 }, 5000, 1);
            Assert.Contains("placed 2 times", r);
            Assert.Contains("11, 12", r);
            Assert.Contains("Nothing was written", r);
        }

        [Fact]
        public void Acquire_refuses_a_link_that_already_shares_the_site_and_allows_one_that_does_not()
        {
            Assert.Contains("already shares", LinkSurveyRules.AcquireRefusal(11, new List<long> { 11 }, 0.4, 1));
            Assert.Null(LinkSurveyRules.AcquireRefusal(11, new List<long> { 11 }, 10000, 1));
            Assert.Null(LinkSurveyRules.AcquireRefusal(11, new List<long> { 11 }, null, 1)); // CAD: no link document to compare
        }

        [Fact]
        public void A_face_with_too_few_points_is_not_measured_never_ok()
        {
            JObject v = LinkSurveyRules.FaceVerdict(Enumerable.Repeat(0.0, LinkSurveyRules.MinPointsPerFace - 1).ToList(), 10, LinkSurveyRules.MinPointsPerFace);
            Assert.Equal("not_measured", v.Value<string>("state"));
            Assert.Equal("too_few_points", v.Value<string>("reason"));
            Assert.Null(v["p95_abs_mm"]);
        }

        [Fact]
        public void A_face_is_judged_on_the_95th_percentile_of_absolute_distance()
        {
            var within = Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 3.0 : -3.0).ToList();
            within[0] = 80; // one stray point does not fail a face
            JObject ok = LinkSurveyRules.FaceVerdict(within, 10, 20);
            Assert.Equal("ok", ok.Value<string>("state"));
            Assert.Equal(80, ok.Value<double>("max_abs_mm"));
            Assert.Equal(0.99, ok.Value<double>("within_tolerance_share"));

            var off = Enumerable.Repeat(25.0, 50).Concat(Enumerable.Repeat(1.0, 50)).ToList();
            JObject bad = LinkSurveyRules.FaceVerdict(off, 10, 20);
            Assert.Equal("deviates", bad.Value<string>("state"));
            Assert.Equal(13, bad.Value<double>("mean_mm"));
        }

        [Fact]
        public void Element_and_verdict_never_turn_an_unmeasured_face_into_a_pass()
        {
            Assert.Equal("ok", LinkSurveyRules.ElementState(new[] { "ok", "ok" }));
            Assert.Equal("partially_measured", LinkSurveyRules.ElementState(new[] { "ok", "not_measured" }));
            Assert.Equal("not_measured", LinkSurveyRules.ElementState(new[] { "not_measured" }));
            Assert.Equal("not_measured", LinkSurveyRules.ElementState(new string[0]));
            Assert.Equal("deviates", LinkSurveyRules.ElementState(new[] { "not_measured", "deviates", "ok" }));
            Assert.Equal("passes", LinkSurveyRules.Verdict(new[] { "ok", "ok" }));
            Assert.Equal("not_decidable", LinkSurveyRules.Verdict(new[] { "ok", "partially_measured" }));
            Assert.Equal("fails", LinkSurveyRules.Verdict(new[] { "partially_measured", "deviates" }));
            Assert.Equal("not_decidable", LinkSurveyRules.Verdict(new string[0]));
        }

        [Fact]
        public void Faces_nobody_sampled_keep_an_element_from_ok()
        {
            var sixtyOk = Enumerable.Repeat("ok", LinkSurveyRules.MaxFacesPerElement).ToList();
            Assert.Equal("ok", LinkSurveyRules.ElementState(sixtyOk, 0));
            // 62 planar faces (a wall with 14 openings): the two beyond the limit were never sampled.
            Assert.Equal("partially_measured", LinkSurveyRules.ElementState(sixtyOk, 2));
            Assert.Equal("fails", LinkSurveyRules.Verdict(new[] { LinkSurveyRules.ElementState(new[] { "deviates" }, 5) }));
            Assert.Equal("not_decidable", LinkSurveyRules.Verdict(new[] { LinkSurveyRules.ElementState(sixtyOk, 1) }));
            Assert.Equal("not_measured", LinkSurveyRules.ElementState(new string[0], 3));
        }

        [Fact]
        public void A_face_seen_in_a_patch_is_not_measured_for_low_coverage()
        {
            // 10 m2 (4 m x 2.5 m): the face's own spacing keeps a fully scanned face under the cap - 50 mm, 4,000 points.
            double spacing = LinkSurveyRules.FaceAverageDistanceMm(10);
            Assert.Equal(50, spacing, 6);
            Assert.True(LinkSurveyRules.ExpectedPoints(10, spacing, LinkSurveyRules.MaxPointsPerFace) < LinkSurveyRules.MaxPointsPerFace);
            Assert.Equal(LinkSurveyRules.AverageDistanceMm, LinkSurveyRules.FaceAverageDistanceMm(0.5), 6);
            double s = spacing / 1000, u = 4, v = 2.5;                       // metres stand in for the UV units
            int nu = LinkSurveyRules.CoverageCells(u, s), nv = LinkSurveyRules.CoverageCells(v, s);
            Assert.Equal(20, nu);
            Assert.Equal(13, nv);
            // 600 points in a 0.6 m strip of the face (15 % of it): the old count rule (600 of 5,000 capped) called it ok.
            List<double[]> strip = Grid(0.6, 2.5, s);
            Assert.Equal(600, strip.Count);
            double? share = LinkSurveyRules.CoverageShare(strip, 0, 0, u, v, nu, nv, null);
            Assert.Equal(0.15, share.Value, 6);
            JObject patch = LinkSurveyRules.FaceVerdict(Enumerable.Repeat(1.0, strip.Count).ToList(), 10, LinkSurveyRules.MinPointsPerFace, share);
            Assert.Equal("not_measured", (string)patch["state"]);
            Assert.Equal("low_coverage", (string)patch["reason"]);
            Assert.Equal(0.15, (double)patch["coverage_share"], 6);
            // About as many points spread over the whole face: seen, and judged.
            List<double[]> spread = Grid(u, v, 0.13);
            double? whole = LinkSurveyRules.CoverageShare(spread, 0, 0, u, v, nu, nv, null);
            Assert.Equal(1.0, whole.Value, 6);
            Assert.Equal("ok", (string)LinkSurveyRules.FaceVerdict(Enumerable.Repeat(1.0, spread.Count).ToList(), 10,
                                                                    LinkSurveyRules.MinPointsPerFace, whole)["state"]);
        }

        [Fact]
        public void Coverage_counts_only_cells_on_the_face_and_is_undetermined_without_any()
        {
            // An L-shaped face in a 2 x 2 rectangle: the top-right cell is off the face and its points do not count.
            System.Func<int, int, bool> lShape = (i, j) => !(i == 1 && j == 1);
            var pts = new List<double[]> { new[] { 0.5, 0.5 }, new[] { 1.5, 0.5 }, new[] { 1.5, 1.5 } };
            Assert.Equal(2.0 / 3, LinkSurveyRules.CoverageShare(pts, 0, 0, 2, 2, 2, 2, lShape).Value, 6);
            // A point on the far edge belongs to the edge cell.
            Assert.Equal(1.0, LinkSurveyRules.CoverageShare(new List<double[]> { new[] { 2.0, 2.0 } }, 0, 0, 2, 2, 1, 1, null).Value, 6);
            Assert.Null(LinkSurveyRules.CoverageShare(pts, 0, 0, 2, 2, 2, 2, (i, j) => false));
            JObject v = LinkSurveyRules.FaceVerdict(Enumerable.Repeat(0.0, 50).ToList(), 10, 20, null);
            Assert.Equal("not_measured", (string)v["state"]);
            Assert.Equal("coverage_undetermined", (string)v["reason"]);
            // Too few points is said first, whatever the coverage.
            Assert.Equal("too_few_points", (string)LinkSurveyRules.FaceVerdict(new List<double> { 1 }, 10, 20, 1.0)["reason"]);
            // A strip keeps at least one cell across and at most MaxCellsPerAxis along.
            Assert.Equal(1, LinkSurveyRules.CoverageCells(0.01, 0.02));
            Assert.Equal(LinkSurveyRules.MaxCellsPerAxis, LinkSurveyRules.CoverageCells(1000, 0.02));
        }

        /// <summary>Points on a regular grid from the origin to (u1, v1), half a step in from each edge.</summary>
        private static List<double[]> Grid(double u1, double v1, double step)
        {
            var list = new List<double[]>();
            for (int i = 0; (i + 0.5) * step < u1; i++)
                for (int j = 0; (j + 0.5) * step < v1; j++) list.Add(new[] { (i + 0.5) * step, (j + 0.5) * step });
            return list;
        }

        [Fact]
        public void The_slab_never_reaches_the_elements_own_opposite_face()
        {
            double band = LinkSurveyRules.BandMm(50);                       // 150 mm outward
            Assert.Equal(40, LinkSurveyRules.InwardBandMm(band, 100), 6);   // a 100 mm partition: 40 mm inward, not 150
            Assert.True(LinkSurveyRules.InwardBandMm(band, 100) < 50);
            Assert.Equal(band, LinkSurveyRules.InwardBandMm(band, 1000), 6);
            Assert.Equal(band, LinkSurveyRules.InwardBandMm(band, null), 6);
        }

        [Fact]
        public void The_point_frame_is_taken_only_when_one_frame_holds()
        {
            Assert.Equal("identity", LinkSurveyRules.PointFrame(true, 100, 90, 90));
            Assert.Null(LinkSurveyRules.PointFrame(true, 100, 10, 10));
            Assert.Equal("instance_transform", LinkSurveyRules.PointFrame(false, 100, 5, 95));
            Assert.Equal("raw", LinkSurveyRules.PointFrame(false, 100, 95, 5));
            // A transform smaller than the band: both frames hold, so neither is assumed.
            Assert.Null(LinkSurveyRules.PointFrame(false, 100, 95, 97));
            Assert.Null(LinkSurveyRules.PointFrame(false, 100, 10, 20));
        }

        [Fact]
        public void A_named_instance_of_a_type_placed_twice_is_left_for_Revit_to_answer()
        {
            Assert.Null(LinkSurveyRules.AcquireRefusal(901, new List<long> { 901, 902 }, 10000, 1, instanceNamed: true));
            Assert.NotNull(LinkSurveyRules.AcquireRefusal(901, new List<long> { 901, 902 }, 10000, 1));
            Assert.NotNull(LinkSurveyRules.AcquireRefusal(901, new List<long> { 901, 902 }, 0.2, 1, instanceNamed: true));
        }

        [Fact]
        public void The_band_always_exceeds_the_tolerance()
        {
            Assert.Equal(30, LinkSurveyRules.BandMm(5));
            Assert.Equal(60, LinkSurveyRules.BandMm(20));
        }

        [Fact]
        public void The_contract_publishes_the_new_operations_and_their_arguments()
        {
            CommandContract c = Contract.Find("horizun_manage_links");
            var props = (JObject)c.InputSchema["properties"];
            var ops = props["operation"]["enum"].Values<string>().ToList();
            Assert.Contains("acquire_coordinates", ops);
            Assert.Contains("scan_deviation", ops);
            Assert.DoesNotContain("publish_coordinates", ops); // a later step, deliberately absent
            Assert.Equal(new[] { "rvt", "point_cloud", "ifc" }, props["kind"]["enum"].Values<string>());
            Assert.NotNull(props["element_ids"]);
            Assert.Equal(10, props["tolerance_mm"].Value<double>("default"));
            foreach (string arg in new[] { "kind", "link_instance_id", "element_ids", "tolerance_mm" })
                Assert.True(props[arg].Value<string>("description").Length <= 120, arg);
        }
    }
}
