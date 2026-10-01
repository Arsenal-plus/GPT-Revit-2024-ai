// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A PROFILE A CLIENT CAN ACTUALLY READ.
//
// MEASURED in the 2026-09-30 dry run: horizun_query_cad mode=profile over a
// drawing of 27 layers and 8,719 segments answered 78 kB, and the desktop client
// cut it to a file. response_mode=compact is the opt-in answer. These tests build
// a drawing of the same size, and hold compact to two things: it is small, and
// it keeps every number a requirement set is written from - the same numbers the
// full profile reports.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class CadLayerProfileCompactTests
    {
        private readonly ITestOutputHelper _out;
        public CadLayerProfileCompactTests(ITestOutputHelper output) { _out = output; }

        /// <summary>
        /// 27 layers, ~8,700 segments: on each layer double-line runs of varying
        /// thickness (walls), rings (columns) and loose lines (annotation).
        /// </summary>
        private static List<CadSegment> DryRunSizedDrawing()
        {
            var all = new List<CadSegment>();
            for (int l = 0; l < 27; l++)
            {
                string layer = "X-LAYR-" + l.ToString("00") + "-____-MCUT";
                double y0 = l * 100000;
                for (int i = 0; i < 60; i++)
                {
                    double half = 50 + (i % 9) * 25;
                    double y = y0 + i * 1500;
                    all.Add(new CadSegment(new CadPoint(0, y - half), new CadPoint(6000 + i * 10, y - half), layer));
                    all.Add(new CadSegment(new CadPoint(0, y + half), new CadPoint(6000 + i * 10, y + half), layer));
                }
                for (int i = 0; i < 50; i++)
                {
                    double x = 20000 + i * 1000, y = y0, s = 300 + (i % 4) * 50;
                    all.Add(new CadSegment(new CadPoint(x, y), new CadPoint(x + s, y), layer));
                    all.Add(new CadSegment(new CadPoint(x + s, y), new CadPoint(x + s, y + s), layer));
                    all.Add(new CadSegment(new CadPoint(x + s, y + s), new CadPoint(x, y + s), layer));
                    all.Add(new CadSegment(new CadPoint(x, y + s), new CadPoint(x, y), layer));
                }
                for (int i = 0; i < 3; i++)
                    all.Add(new CadSegment(new CadPoint(-5000, y0 + i * 777), new CadPoint(-4000, y0 + i * 777 + 333), layer));
            }
            return all;
        }

        private static int Bytes(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));

        [Fact]
        public void A_dry_run_sized_profile_in_compact_stays_well_under_twenty_kilobytes()
        {
            List<CadSegment> drawing = DryRunSizedDrawing();
            Assert.InRange(drawing.Count, 8000, 9500);

            JObject full = CadLayerProfiler.Profile(drawing, "millimeter", 40);
            JObject compact = CadLayerProfiler.Compact(full);

            Assert.Equal(27, (int)compact["layers_profiled"]);
            _out.WriteLine("segments=" + drawing.Count + " full=" + Bytes(full) + " B compact=" + Bytes(compact) + " B");
            // The command adds coverage, visibility and the provenance kinds on top
            // (~2-3 kB); the shaped body leaves that room under 20 kB.
            Assert.True(Bytes(compact) < 16000, "compact profile is " + Bytes(compact) + " bytes");
            Assert.True(Bytes(full) > 3 * Bytes(compact),
                "full " + Bytes(full) + " B vs compact " + Bytes(compact) + " B: compact should be a real cut");
        }

        [Fact]
        public void Compact_keeps_every_number_the_full_profile_reports_per_layer()
        {
            JObject full = CadLayerProfiler.Profile(DryRunSizedDrawing(), "millimeter", 40);
            JObject compact = CadLayerProfiler.Compact(full);

            var fullRows = ((JArray)full["layers"]).OfType<JObject>().ToList();
            var compactRows = ((JArray)compact["layers"]).OfType<JObject>().ToList();
            Assert.Equal(fullRows.Count, compactRows.Count);
            for (int i = 0; i < fullRows.Count; i++)
            {
                JObject f = fullRows[i], c = compactRows[i];
                Assert.Equal((string)f["layer"], (string)c["layer"]);
                Assert.Equal((int)f["segments"], (int)c["segments"]);
                Assert.Equal((bool)f["structure_found"], (bool)c["structure_found"]);
                Assert.Equal((string)f["best_reading"]["from"], (string)c["best"]["from"]);
                Assert.Equal((int)f["best_reading"]["candidates"], (int)c["best"]["candidates"]);
                foreach (JObject reading in ((JArray)f["would_read"]).OfType<JObject>())
                    Assert.Equal((int)reading["candidates"], (int)c["candidates_by_source"][(string)reading["from"]]);
                var thickness = f["best_reading"]["thickness_mm"] as JObject;
                if (thickness != null)
                {
                    Assert.Equal((double)thickness["min"], (double)c["best"]["thickness_mm"][0]);
                    Assert.Equal((double)thickness["max"], (double)c["best"]["thickness_mm"][1]);
                }
            }
            Assert.Equal(((JArray)full["requirement_set_skeleton"]["rules"]).Count,
                         ((JArray)compact["requirement_set_skeleton"]["rules"]).Count);
        }

        [Fact]
        public void Compact_still_refuses_to_say_what_a_layer_means_and_names_what_it_left_out()
        {
            JObject compact = CadLayerProfiler.Compact(CadLayerProfiler.Profile(DryRunSizedDrawing(), "millimeter", 40));

            foreach (JObject rule in ((JArray)compact["requirement_set_skeleton"]["rules"]).OfType<JObject>())
            {
                Assert.Equal(JTokenType.Null, rule["produces"].Type);
                Assert.Null(rule["_measured"]);
            }
            Assert.Equal("compact", (string)compact["response_mode"]);
            Assert.Contains("response_mode='full'", (string)compact["omitted"]["how_to_get_it"]);
            Assert.Contains("layer=", (string)compact["omitted"]["how_to_get_it"]);
        }

        [Fact]
        public void What_was_not_profiled_is_still_counted_in_compact()
        {
            JObject compact = CadLayerProfiler.Compact(CadLayerProfiler.Profile(DryRunSizedDrawing(), "millimeter", 5));

            Assert.Equal(5, (int)compact["layers_profiled"]);
            Assert.Equal(27, (int)compact["layers_in_drawing"]);
            Assert.Equal(22, (int)compact["layers_not_profiled"]);
            Assert.NotNull((string)compact["layers_not_profiled_means"]);
        }

        [Fact]
        public void A_reader_that_threw_is_still_named_in_compact()
        {
            var full = new JObject
            {
                ["layers"] = new JArray(new JObject
                {
                    ["layer"] = "L", ["segments"] = 1, ["from_curves"] = 0, ["structure_found"] = false,
                    ["would_read"] = new JArray(new JObject { ["from"] = "double_lines", ["candidates"] = 0, ["unreadable"] = true, ["why"] = "boom" })
                })
            };
            JObject row = (JObject)((JArray)CadLayerProfiler.Compact(full)["layers"])[0];
            Assert.Equal("double_lines", (string)((JArray)row["unreadable_sources"])[0]);
            Assert.Equal(JTokenType.Null, row["best"].Type);
        }
    }
}
