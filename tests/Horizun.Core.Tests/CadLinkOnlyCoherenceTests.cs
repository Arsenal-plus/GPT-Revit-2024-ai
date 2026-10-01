using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// A PLAN THAT READS NOTHING FROM THE FILE IS JUDGED ON WHAT IT IS MADE OF.
    ///
    /// MEASURED (dry run, class 4): a Revit-exported DWG was linked through horizun_manage_cad_links, 75 walls
    /// were planned from one double-line layer, and horizun_apply_cad_plan refused coherence_unknown -
    /// "a drawing this machine has never read through the text extractor". The source-set identity it waited
    /// for is written only by headless AutoCAD, which a walls-only set never starts; re-planning with dwg_path
    /// and horizun_cad_extract were both tried and neither runs it. The typed DWG-to-model path could not be
    /// finished on a normal machine.
    ///
    /// The fix does not delete the guard. A set that reads the file keeps the strict rule; a set that reads
    /// nothing from it is decided on the link (loaded by this bridge, untouched since) and the host file's
    /// hash, and the reply names that basis and what it did not check.
    /// </summary>
    public class CadLinkOnlyCoherenceTests
    {
        // The dry-run shape: the link was added by this bridge, so there IS a record and its geometry still
        // fingerprints as recorded - but nothing on the machine ever ran the extractor, so neither side has
        // a source-set identity.
        private static JObject Record(string file = "sha:host1", string print = "fp:111") => new JObject
        {
            ["source_set_sha256"] = null,
            ["geometry_fingerprint"] = print,
            ["file_sha256"] = file,
            ["by"] = "horizun_manage_cad_links add"
        };

        private static CadRequirementSet Set(string geometry, string extra = "")
        {
            string doc = (@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5,
                              'face_projection_mm': 300 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL-____-MCUT'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic - 200mm', 'level': 'MDP - Prueba CAD',
                           'height_mm': 3000, 'geometry': GEOMETRY } EXTRA ]
            }").Replace("GEOMETRY", geometry).Replace("EXTRA", extra).Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static readonly string Walls =
            "{ 'from': 'double_lines', 'min_thickness_mm': 100, 'max_thickness_mm': 350 }";

        [Fact]
        public void The_dry_run_evidence_is_still_unknown_for_a_caller_that_does_not_say_what_the_plan_reads()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", false);
            Assert.Equal(CadSourceCoherenceRules.Unknown, d.Value<string>("state"));
            Assert.False(d.Value<bool>("applicable"));
            Assert.Equal("the_source_set_could_not_be_identified", d.Value<string>("why"));
            // THE REMEDY NAMES WHAT ACTUALLY WRITES THE SET, and the way out for a plan that needs none.
            string remedy = d.Value<string>("remedy");
            Assert.Contains("accoreconsole", remedy);
            Assert.Contains("HORIZUN_ACCORECONSOLE", remedy);
            Assert.Contains(CadSourceCoherenceRules.LinkGeometryOnly, remedy);
            Assert.DoesNotContain("horizun_cad_networks", remedy);
        }

        [Fact]
        public void The_dry_run_evidence_for_a_plan_that_reads_only_the_link_is_applicable_and_names_its_basis()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", false, true);
            Assert.Equal(CadSourceCoherenceRules.LinkGeometryOnly, d.Value<string>("state"));
            Assert.True(d.Value<bool>("applicable"));
            Assert.Equal("link_geometry_and_host_file", d.Value<string>("basis"));
            Assert.False(d.Value<bool>("references_checked"));
            Assert.Contains("NOT checked", d.Value<string>("means"));
            Assert.True(CadSourceCoherenceRules.IsApplicableState(d.Value<string>("state")));
        }

        [Fact]
        public void A_host_file_revised_after_the_load_is_not_aligned_even_for_a_link_only_plan()
        {
            JObject d = CadSourceCoherenceRules.Decide(Record(), null, "sha:host2", "fp:111", false, true);
            Assert.Equal(CadSourceCoherenceRules.NotAligned, d.Value<string>("state"));
            Assert.False(d.Value<bool>("applicable"));
            Assert.Equal("the_drawing_changed_since_the_link_was_loaded", d.Value<string>("why"));
            Assert.True(d["differs"].Value<bool>("host_file_changed"));
            Assert.Contains("reload", d.Value<string>("remedy"));
        }

        [Fact]
        public void A_host_file_that_cannot_be_hashed_withholds_the_permission()
        {
            JObject now = CadSourceCoherenceRules.Decide(Record(), null, null, "fp:111", false, true);
            Assert.Equal(CadSourceCoherenceRules.Unknown, now.Value<string>("state"));
            Assert.Equal("the_drawing_file_could_not_be_hashed", now.Value<string>("why"));
            JObject then = CadSourceCoherenceRules.Decide(Record(file: null), null, "sha:host1", "fp:111", false, true);
            Assert.Equal("the_drawing_file_could_not_be_hashed", then.Value<string>("why"));
            Assert.False(then.Value<bool>("applicable"));
        }

        [Fact]
        public void The_flag_never_rescues_a_link_this_bridge_did_not_load_or_that_changed_since()
        {
            JObject noRecord = CadSourceCoherenceRules.Decide(null, null, "sha:host1", "fp:111", false, true);
            Assert.Equal("no_record_of_loading_this_link", noRecord.Value<string>("why"));
            Assert.False(noRecord.Value<bool>("applicable"));

            JObject reloaded = CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:999", false, true);
            Assert.Equal("the_link_changed_after_this_bridge_recorded_it", reloaded.Value<string>("why"));
            Assert.False(reloaded.Value<bool>("applicable"));

            JObject snapshot = CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", true, true);
            Assert.Equal(CadSourceCoherenceRules.Snapshot, snapshot.Value<string>("state"));
            Assert.False(snapshot.Value<bool>("applicable"));
        }

        [Fact]
        public void A_known_source_set_is_still_compared_whatever_the_plan_reads()
        {
            var record = new JObject
            {
                ["source_set_sha256"] = "set:AAA", ["geometry_fingerprint"] = "fp:111", ["file_sha256"] = "sha:host1"
            };
            JObject aligned = CadSourceCoherenceRules.Decide(record, "set:AAA", "sha:host1", "fp:111", false, true);
            Assert.Equal(CadSourceCoherenceRules.Aligned, aligned.Value<string>("state"));
            Assert.Equal("link_geometry_and_source_set", aligned.Value<string>("basis"));
            JObject moved = CadSourceCoherenceRules.Decide(record, "set:BBB", "sha:host1", "fp:111", false, true);
            Assert.Equal(CadSourceCoherenceRules.NotAligned, moved.Value<string>("state"));
        }

        [Fact]
        public void A_walls_only_set_reads_nothing_from_the_file()
        {
            Assert.Empty(CadSourceCoherenceRules.FileReadingRules(Set(Walls)));
        }

        [Fact]
        public void Each_rule_that_starts_the_text_extractor_is_named()
        {
            var hatch = CadSourceCoherenceRules.FileReadingRules(Set(
                "{ 'from': 'double_lines', 'min_thickness_mm': 100, 'max_thickness_mm': 350, " +
                "'solid_hatch_layers': ['A-WALL-PATT'] }"));
            Assert.Single(hatch);
            Assert.Contains("solid_hatch_layers", hatch[0]);

            var blocks = CadSourceCoherenceRules.FileReadingRules(Set(Walls,
                ", { 'id': 'r-dev', 'layers': ['E-DEV'], 'produces': 'electrical_fixture', " +
                "'family_type': 'Receptacle: Duplex', 'level': 'Level 1', 'hosted_on': 'wall', " +
                "'geometry': { 'from': 'blocks', 'blocks': ['REC'] } }"));
            Assert.Single(blocks);
            Assert.Contains("r-dev", blocks[0]);
            Assert.Contains("blocks", blocks[0]);
        }

        // ---- the apply's guard accepts the new state, in both places it is asked ----------------------

        private static JObject Binding(string planned) => new JObject { ["coherence_state"] = planned };

        [Fact]
        public void The_apply_guard_lets_a_link_only_plan_through_when_it_is_still_link_only()
        {
            JObject now = CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", false, true);
            string message;
            Assert.Null(CadApplyGuard.CoherenceRefusal(Binding(CadSourceCoherenceRules.LinkGeometryOnly), now, out message));
            Assert.Null(message);
        }

        [Fact]
        public void The_apply_guard_still_refuses_a_plan_made_while_unknown_even_when_link_only_now()
        {
            JObject now = CadSourceCoherenceRules.Decide(Record(), null, "sha:host1", "fp:111", false, true);
            string message;
            Assert.NotNull(CadApplyGuard.CoherenceRefusal(Binding(CadSourceCoherenceRules.Unknown), now, out message));
            Assert.Contains("plan again", message);
        }

        [Fact]
        public void The_apply_guard_refuses_a_link_only_plan_once_the_host_file_moved()
        {
            JObject now = CadSourceCoherenceRules.Decide(Record(), null, "sha:host2", "fp:111", false, true);
            string message;
            JObject refusal = CadApplyGuard.CoherenceRefusal(Binding(CadSourceCoherenceRules.LinkGeometryOnly), now, out message);
            Assert.NotNull(refusal);
            Assert.StartsWith("plan_not_applicable: " + CadSourceCoherenceRules.NotAligned, message);
            Assert.Contains("NOTHING WAS WRITTEN", message);
        }

        [Fact]
        public void A_reload_between_the_plan_and_the_apply_is_drift_on_the_link_geometry()
        {
            // plan_from_cad's binding now records the link's geometry, so a reload that leaves the file's bytes
            // alone - and would make the coherence look fine again - is still caught.
            var binding = new JObject { ["link_geometry_fingerprint"] = "fp:111" };
            JArray drift = CadApplyGuard.Drift(binding, new CadApplyNow { LinkGeometryFingerprint = "fp:222" });
            Assert.Equal(new[] { "the link's geometry" }, drift.OfType<JObject>().Select(x => (string)x["what"]).ToArray());
        }
    }
}
