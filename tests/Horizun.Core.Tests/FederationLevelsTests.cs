// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// horizun_federation_check rule levels_match, Revit-free: names and heights of every
// loaded link's levels (already in host coordinates) against the host's.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FederationLevelsTests
    {
        private static JObject Rules(string json) => JObject.Parse(json.Replace('\'', '"'));
        private static FederationLevelFact L(long id, string name, double mm) => new FederationLevelFact { Id = id, Name = name, ElevationMm = mm };

        private static FederationLevelInput Input(params (long link, FederationLevelFact[] levels)[] links)
        {
            var input = new FederationLevelInput();
            input.Host.AddRange(new[] { L(1, "N+0.00", 0), L(2, "N+3.00", 3000), L(3, "N+6.00", 6000), L(4, "Roof", 9000) });
            foreach (var l in links) input.ByLink[l.link] = l.levels.ToList();
            return input;
        }

        private static List<FederationLinkFact> Links(params long[] ids) =>
            ids.Select(i => new FederationLinkFact { InstanceId = i, Title = "LINK-" + i, Loaded = true, SiteDeltaMm = 0 }).ToList();

        [Fact]
        public void Malformed_levels_match_is_refused_and_it_alone_is_something_to_check()
        {
            Assert.Null(FederationCheckRules.Validate(Rules("{ 'levels_match': true }")));
            Assert.Null(FederationCheckRules.Validate(Rules("{ 'levels_match': { 'tolerance_mm': 2 } }")));
            Assert.Contains("unknown key", FederationCheckRules.Validate(Rules("{ 'levels_match': { 'tol': 2 } }")));
            Assert.Contains(">= 0", FederationCheckRules.Validate(Rules("{ 'levels_match': { 'tolerance_mm': -1 } }")));
            Assert.Contains("must be true", FederationCheckRules.Validate(Rules("{ 'levels_match': 'yes' }")));
        }

        [Fact]
        public void Levels_match_false_asks_for_nothing_and_alone_is_refused()
        {
            Assert.Contains("declares nothing", FederationCheckRules.Validate(Rules("{ 'levels_match': false }")));
            Assert.Contains("declares nothing", FederationCheckRules.Validate(Rules("{ 'levels_match': false, 'same_site': false }")));
        }

        [Fact]
        public void Levels_match_with_no_link_instance_compared_nothing_and_does_not_pass()
        {
            JObject r = FederationCheckRules.Evaluate(Rules("{ 'levels_match': true, 'same_site': false }"),
                new List<FederationModelFact>(), Links(), 10, 50, Input());
            Assert.Equal("not_decidable", (string)r["verdict"]);
            Assert.Equal(0, (int)r["summary"]["links_compared"]);
            Assert.Empty(r["levels"]);
        }

        [Fact]
        public void A_link_whose_levels_agree_matches_and_the_host_levels_it_lacks_are_listed_not_judged()
        {
            JObject r = FederationCheckRules.Evaluate(Rules("{ 'levels_match': true, 'same_site': false }"),
                new List<FederationModelFact>(), Links(10),
                10, 50, Input((10, new[] { L(100, "N+0.00", 0.4), L(101, "N+3.00", 3000) })));
            JToken row = r["levels"].Single();
            Assert.Equal("matches", (string)row["state"]);
            Assert.Equal(2, (int)row["levels_matching"]);
            Assert.Equal(new[] { "N+6.00", "Roof" }, row["host_levels_not_in_link"].Select(t => (string)t));
            Assert.Equal("passes", (string)r["verdict"]);
            Assert.Equal(0, (int)r["summary"]["links_levels_differ"]);
        }

        [Fact]
        public void Height_name_and_absence_are_three_different_mismatches()
        {
            JObject r = FederationCheckRules.Evaluate(Rules("{ 'levels_match': { 'tolerance_mm': 5 }, 'same_site': false }"),
                new List<FederationModelFact>(), Links(10),
                10, 50, Input((10, new[] { L(100, "N+3.00", 3050), L(101, "Level 2", 6002), L(102, "Mezz", 4500), L(103, "N+0.00", 0) })));
            JToken row = r["levels"].Single();
            Assert.Equal("differs", (string)row["state"]);
            var byLevel = row["mismatches"].ToDictionary(m => (string)m["link_level"], m => m);
            Assert.Equal("elevation_differs", (string)byLevel["N+3.00"]["state"]);
            Assert.Equal(50.0, (double)byLevel["N+3.00"]["delta_mm"]);
            Assert.Equal("name_differs", (string)byLevel["Level 2"]["state"]);
            Assert.Equal("N+6.00", (string)byLevel["Level 2"]["host_levels"][0]["name"]);
            Assert.Equal("no_host_level", (string)byLevel["Mezz"]["state"]);
            Assert.False(byLevel.ContainsKey("N+0.00"));
            Assert.Equal("fails", (string)r["verdict"]);
            Assert.Equal(5.0, (double)row["tolerance_mm"]);
        }

        [Fact]
        public void Names_compare_ordinally_so_a_case_change_is_a_name_mismatch()
        {
            JObject r = FederationCheckRules.Evaluate(Rules("{ 'levels_match': true, 'same_site': false }"),
                new List<FederationModelFact>(), Links(10), 10, 50, Input((10, new[] { L(100, "ROOF", 9000) })));
            Assert.Equal("name_differs", (string)r["levels"][0]["mismatches"][0]["state"]);
        }

        [Fact]
        public void An_unloaded_link_is_not_read_and_never_counts_as_matching()
        {
            var links = Links(10, 11);
            links[1].Loaded = false;
            var input = Input((10, new[] { L(100, "N+0.00", 0) }));
            input.WhyNotRead[11] = "the link's levels could not be read: boom";
            JObject r = FederationCheckRules.Evaluate(Rules("{ 'levels_match': true, 'same_site': false }"),
                new List<FederationModelFact>(), links, 10, 50, input);
            JToken unread = r["levels"].First(x => (long)x["instance_id"] == 11);
            Assert.Equal("not_read", (string)unread["state"]);
            Assert.Contains("boom", (string)unread["reason"]);
            Assert.Equal("not_decidable", (string)r["verdict"]);
            Assert.Equal(1, (int)r["summary"]["links_levels_not_read"]);
        }

        [Fact]
        public void Without_levels_match_the_reply_keeps_its_old_shape()
        {
            JObject r = FederationCheckRules.Evaluate(Rules("{ 'same_site': true }"), new List<FederationModelFact>(), Links(10), 10, 50);
            Assert.Null(r["levels"]);
            Assert.Null(r["summary"]["links_levels_differ"]);
        }
    }
}
