// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Comité de obra 2026-10-01: horizun_clash answered partial with 487 pairs without a
// solid and an empty unresolved_pairs - no way to find one of them - and its full
// rows carried no clash_index. Both are pinned here.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ClashNoSolidLedgerTests
    {
        private static ClashNoSolidLedger.ElementFacts E(string id, string category, bool solid, string source = "host",
                                                         string geometry = null)
            => new ClashNoSolidLedger.ElementFacts
            {
                Key = PairLedger.ElementKey(source, null, id), ElementId = id, SourceModel = source,
                Category = category, Name = category + " " + id, HasSolid = solid,
                Geometry = solid ? null : geometry ?? "3 mesh(es)"
            };

        [Fact]
        public void Pairs_are_named_by_the_element_that_had_no_solid_most_pairs_first()
        {
            var ledger = new ClashNoSolidLedger();
            var terminal = E("900", "Air Terminals", false, "MEP.rvt");
            for (int i = 0; i < 5; i++) ledger.Add(E("1" + i, "Structural Framing", true), terminal);
            ledger.Add(E("20", "Structural Columns", true), E("901", "Mechanical Equipment", false, "MEP.rvt", "no geometry"));

            JObject j = ledger.ToJson();
            Assert.Equal(6, (int)j["pairs"]);
            Assert.Equal(2, (int)j["elements_without_solid"]);
            var first = (JObject)j["elements"][0];
            Assert.Equal("900", (string)first["element_id"]);
            Assert.Equal(5, (int)first["pairs"]);
            Assert.Equal("3 mesh(es)", (string)first["geometry"]);
            Assert.Equal(5, (int)j["by_category"]["Air Terminals"]["pairs"]);
            Assert.Equal("b", (string)j["pair_examples"][0]["without_solid"]);
            Assert.False((bool)j["elements_truncated"]);
        }

        [Fact]
        public void Both_sides_without_solid_count_for_both_elements()
        {
            var ledger = new ClashNoSolidLedger();
            ledger.Add(E("1", "Generic Models", false), E("2", "Generic Models", false));
            JObject j = ledger.ToJson();
            Assert.Equal(1, (int)j["pairs"]);
            Assert.Equal(2, (int)j["elements_without_solid"]);
            Assert.Equal("both", (string)j["pair_examples"][0]["without_solid"]);
        }

        [Fact]
        public void The_measured_count_is_never_truncated_only_its_lists_are()
        {
            var ledger = new ClashNoSolidLedger();
            for (int i = 0; i < 487; i++) ledger.Add(E("s" + i, "Structural Framing", true), E("m" + (i % 120), "Duct Fittings", false));
            JObject j = ledger.ToJson();
            Assert.Equal(487, (int)j["pairs"]);
            Assert.Equal(120, (int)j["elements_without_solid"]);
            Assert.Equal(ClashNoSolidLedger.MaxElements, ((JArray)j["elements"]).Count);
            Assert.True((bool)j["elements_truncated"]);
            Assert.Equal(ClashNoSolidLedger.MaxPairExamples, ((JArray)j["pair_examples"]).Count);
            Assert.True((bool)j["pair_examples_truncated"]);
            Assert.Equal(487, (int)j["by_category"]["Duct Fittings"]["pairs"]);
        }

        [Fact]
        public void An_empty_ledger_says_every_pair_had_solids()
        {
            JObject j = new ClashNoSolidLedger().ToJson();
            Assert.Equal(0, (int)j["pairs"]);
            Assert.Empty((JArray)j["elements"]);
        }

        private static string Source()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "ClashCommand.cs"));
        }

        [Fact]
        public void Every_full_row_carries_its_clash_index_and_coverage_names_the_skipped_pairs()
        {
            string s = Source();
            int add = s.IndexOf("clashes.Add(new JObject", StringComparison.Ordinal);
            int index = s.IndexOf("[\"clash_index\"] = clashes.Count", add, StringComparison.Ordinal);
            int a = s.IndexOf("[\"a\"] = Describe(a)", add, StringComparison.Ordinal);
            Assert.True(add > 0 && index > add && index < a, "clash_index is the row's position, set as it is added");
            int mark = s.IndexOf("pairs.MarkNoSolids();", StringComparison.Ordinal);
            int named = s.IndexOf("noSolid.Add(", mark, StringComparison.Ordinal);
            Assert.True(mark > 0 && named > mark && named - mark < 200, "every skipped pair is recorded where it is counted");
            Assert.Contains("[\"pairs_without_solid\"] = noSolid.ToJson()", s);
        }

        [Fact]
        public void The_summary_index_is_the_same_position_the_full_rows_carry()
        {
            var data = new JObject
            {
                ["clashes"] = new JArray(Enumerable.Range(0, 12).Select(i => (JToken)new JObject
                {
                    ["clash_index"] = i, ["intersection_volume_m3"] = i == 7 ? 9.0 : 0.001 * i,
                    ["a"] = new JObject { ["category"] = "Beams" }, ["b"] = new JObject { ["category"] = "Ducts" }
                }))
            };
            JObject shaped = ResponseSummaryRules.Clash(data);
            Assert.Equal(7, (int)shaped["clashes"][0]["clash_index"]);
        }
    }
}
