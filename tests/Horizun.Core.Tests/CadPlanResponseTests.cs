using System;
using System.IO;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// A PLAN THE CLIENT CAN READ.
    ///
    /// MEASURED (dry run, classes 4 and 6): horizun_plan_from_cad answered 232 kB for 75 walls from one layer
    /// and the client truncated it to a file. response_mode=summary keeps what a person reads whole and cuts
    /// the row lists to a sample, naming each; the whole plan is kept under plan_id for the apply.
    /// </summary>
    public class CadPlanResponseTests
    {
        /// <summary>The shape of the dry-run reply: 75 wall rows in one batch, 11 deferred, 27 layers.</summary>
        private static JObject DryRunShapedPlan()
        {
            var elements = new JArray();
            var candidates = new JArray();
            for (int i = 0; i < 75; i++)
            {
                elements.Add(new JObject
                {
                    ["kind"] = "wall", ["source_row"] = i + 1,
                    ["start"] = new JArray(1000.0 * i, 0.0, 30000.0), ["end"] = new JArray(1000.0 * i + 900, 0.0, 30000.0),
                    ["height"] = 3000.0, ["level_id"] = 275900L, ["type_id"] = 4100L,
                    ["wall_type_choices"] = new JArray("Generic - 200mm"), ["interpreted_thickness_mm"] = 200.0
                });
                candidates.Add(new JObject
                {
                    ["element_index"] = i, ["source_row"] = i + 1, ["candidate_id"] = "cadrev:" + i.ToString("x8"),
                    ["geometry_id"] = "cadgeo:" + i, ["semantic_id"] = "cadsem:" + i, ["rule_id"] = "r-wall",
                    ["layer"] = "A-WALL-____-MCUT", ["confidence"] = 0.97,
                    ["source_entities"] = new JArray(Enumerable.Range(0, 4).Select(k => "cadent:" + i + ":" + k))
                });
            }
            var deferred = new JArray(Enumerable.Range(0, 11).Select(i => new JObject
            {
                ["candidate_id"] = "cadrev:d" + i, ["proposed_kind"] = "wall", ["rule_id"] = "r-wall",
                ["reasons"] = new JArray("the span is 290 mm in one part and 303 mm in another: the geometry does not say which")
            }));
            var layers = new JObject();
            for (int i = 0; i < 27; i++) layers["LAYER-" + i] = new JArray("r-wall");
            var binding = new JObject
            {
                ["plan_fingerprint"] = "cadplan:fe29a19215c159d327a9ffdbc4e32e1a",
                ["actions_fingerprint"] = "cadacts:1",
                ["source_fingerprint"] = "cadsrc:1",
                ["requirement_set_sha256"] = "rules:1",
                ["coherence_state"] = CadSourceCoherenceRules.LinkGeometryOnly,
                ["resolved_names"] = new JArray(Enumerable.Range(0, 6).Select(i => new JObject { ["id"] = i, ["name"] = "n" + i }))
            };
            return new JObject
            {
                ["plan_fingerprint"] = "cadplan:fe29a19215c159d327a9ffdbc4e32e1a",
                ["actions"] = 75, ["deferred"] = 11,
                ["coverage"] = new JObject { ["segments_considered"] = 8719, ["segments_consumed"] = 255, ["fraction"] = 0.0292 },
                ["warnings"] = new JArray(Enumerable.Range(0, 5).Select(i => (JToken)("warning " + i))),
                ["deferred_detail"] = deferred,
                ["layer_map"] = layers,
                ["unclaimed"] = new JArray(Enumerable.Range(0, 26).Select(i => new JObject { ["layer"] = "L" + i, ["entity_count"] = 300 })),
                ["coherence"] = new JObject { ["state"] = CadSourceCoherenceRules.LinkGeometryOnly, ["applicable"] = true },
                ["candidate_index"] = new JArray(new JObject { ["key"] = "cad-stage-1-batch-1", ["candidates"] = candidates }),
                ["execute_plan_request"] = new JObject
                {
                    ["target_document"] = "T",
                    ["actions"] = new JArray(new JObject
                    {
                        ["key"] = "cad-stage-1-batch-1", ["tool"] = "horizun_create_elements",
                        ["arguments"] = new JObject { ["stage"] = 1, ["units"] = "mm", ["elements"] = elements }
                    })
                },
                ["apply_binding"] = binding
            };
        }

        private static int Bytes(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));

        [Fact]
        public void Full_is_the_default_and_summary_is_the_only_other_mode()
        {
            Assert.Null(CadPlanResponse.ValidateMode(null));
            Assert.Null(CadPlanResponse.ValidateMode("full"));
            Assert.Null(CadPlanResponse.ValidateMode("summary"));
            Assert.NotNull(CadPlanResponse.ValidateMode("compact"));
        }

        [Fact]
        public void A_summary_is_a_fraction_of_the_full_reply_and_says_what_it_cut()
        {
            JObject full = DryRunShapedPlan();
            string before = full.ToString(Formatting.None);
            JObject summary = CadPlanResponse.Summarize(full);

            Assert.Equal(before, full.ToString(Formatting.None));      // the full plan is not touched
            Assert.True(Bytes(summary) * 4 < Bytes(full),
                "summary " + Bytes(summary) + " B against full " + Bytes(full) + " B");
            Assert.Equal("summary", (string)summary["response_mode"]);
            Assert.False((bool)summary["response_detail_complete"]);

            var cut = summary["response_omissions"].OfType<JObject>().ToDictionary(o => (string)o["json_pointer"]);
            Assert.Equal(75, (int)cut["/execute_plan_request/actions/0/arguments/elements"]["total"]);
            Assert.Equal(3, (int)cut["/execute_plan_request/actions/0/arguments/elements"]["shown"]);
            Assert.Equal(75, (int)cut["/candidate_index/0/candidates"]["total"]);
            Assert.Equal(26, (int)cut["/unclaimed"]["total"]);
            Assert.Equal(3, ((JArray)summary["execute_plan_request"]["actions"][0]["arguments"]["elements"]).Count);
        }

        [Fact]
        public void What_a_person_reads_and_what_the_apply_needs_are_kept_whole()
        {
            JObject full = DryRunShapedPlan();
            JObject summary = CadPlanResponse.Summarize(full);
            Assert.Equal(full["apply_binding"].ToString(), summary["apply_binding"].ToString());
            Assert.Equal(full["warnings"].ToString(), summary["warnings"].ToString());
            Assert.Equal(full["coverage"].ToString(), summary["coverage"].ToString());
            Assert.Equal(full["coherence"].ToString(), summary["coherence"].ToString());
            // the deferred - the half a reviewer reads - keep up to 25 rows, so all 11 of the dry run survive
            Assert.Equal(11, ((JArray)summary["deferred_detail"]).Count);
            Assert.Equal(75, (int)summary["actions"]);
            Assert.Equal(27, ((JObject)summary["layer_map"]).Count);
            // a coordinate is a value, not a row list, and is never cut
            Assert.Equal(3, ((JArray)summary["execute_plan_request"]["actions"][0]["arguments"]["elements"][0]["start"]).Count);
        }

        [Fact]
        public void A_small_plan_summarises_to_itself_and_says_it_is_complete()
        {
            var tiny = new JObject { ["actions"] = 1, ["deferred_detail"] = new JArray(new JObject { ["a"] = 1 }) };
            JObject summary = CadPlanResponse.Summarize(tiny);
            Assert.True((bool)summary["response_detail_complete"]);
            Assert.Empty((JArray)summary["response_omissions"]);
        }

        [Fact]
        public void The_plan_id_names_the_plan_and_its_exact_actions()
        {
            string a = CadPlanResponse.PlanId("cadplan:1", "cadacts:1");
            Assert.StartsWith("cadplanid:", a);
            Assert.Equal(a, CadPlanResponse.PlanId("cadplan:1", "cadacts:1"));
            Assert.NotEqual(a, CadPlanResponse.PlanId("cadplan:1", "cadacts:2"));
            Assert.NotEqual(a, CadPlanResponse.PlanId("cadplan:2", "cadacts:1"));
        }

        [Fact]
        public void A_kept_plan_comes_back_whole_under_its_id_and_never_under_another()
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-cadplans-" + Guid.NewGuid().ToString("N"));
            try
            {
                JObject full = DryRunShapedPlan();
                string id = CadPlanResponse.PlanId("cadplan:fe29", "cadacts:1");
                full["plan_id"] = id;
                Assert.NotNull(CadPlanStore.Save(root, id, full));

                JObject back = CadPlanStore.Load(root, id);
                Assert.NotNull(back);
                Assert.Equal(full.ToString(Formatting.None), back.ToString(Formatting.None));
                Assert.Equal(75, ((JArray)back["execute_plan_request"]["actions"][0]["arguments"]["elements"]).Count);

                Assert.Null(CadPlanStore.Load(root, CadPlanResponse.PlanId("cadplan:other", "cadacts:1")));

                // a file is data: one whose own plan_id is not the id asked for is not handed back
                string other = CadPlanResponse.PlanId("cadplan:x", "cadacts:x");
                File.Copy(CadPlanStore.PathFor(root, id), CadPlanStore.PathFor(root, other));
                Assert.Null(CadPlanStore.Load(root, other));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void A_plan_older_than_the_keeping_period_is_not_offered_back()
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-cadplans-" + Guid.NewGuid().ToString("N"));
            try
            {
                string id = CadPlanResponse.PlanId("cadplan:old", "cadacts:old");
                Assert.NotNull(CadPlanStore.Save(root, id, new JObject { ["plan_id"] = id }));
                File.SetLastWriteTimeUtc(CadPlanStore.PathFor(root, id), DateTime.UtcNow - CadPlanStore.KeepFor - TimeSpan.FromHours(1));
                Assert.Null(CadPlanStore.Load(root, id));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
