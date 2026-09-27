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
