// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// THE DECISION BEHIND CadSourceCoherence, with no Revit in it.
//
// Four facts go in - what was recorded when this bridge loaded the link, the source
// set as it is now, the link's geometry fingerprint as it is now, and whether the
// reading continued a snapshot - and one of four states comes out. Keeping it here
// is what makes the rule testable: the Revit side measures, this side decides, and
// the decision is the part that must not drift.
//
// The rule is one-directional on purpose. Only "the record describes what is loaded
// AND the sources still hash as they did" grants applicable; every other shape of
// the evidence withholds it and says which shape it was. A caller who wants the
// permission gets it by reloading the link through the bridge, which is one typed
// call and is verified by the geometry it returns.
//
// A PLAN THAT READS NOTHING FROM THE FILE HAS ONE HALF, NOT TWO. The comparison above
// exists because a plan can be made of the link's geometry and the FILE's sizes. A
// requirement set with no blocks, solid_hatch_layers or section rule reads nothing
// from the file - every action comes from the link - and the source-set identity it
// was waiting for is only ever written by the text extractor (headless AutoCAD), which
// such a plan never starts. MEASURED (dry run, class 4): 75 walls from a Revit-exported
// DWG were refused coherence_unknown, and neither re-planning with dwg_path nor
// horizun_cad_extract could clear it, because neither runs the extractor. For that
// plan the evidence that CAN be shown is what is decided on - the link was loaded by
// this bridge, its geometry is untouched since, and the host file still hashes as it
// did - and the reply names that basis (link_geometry_only) and what it did not check.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadSourceCoherenceRules
    {
        public const string Aligned = "sources_match_the_link";
        public const string NotAligned = "revisions_not_aligned";
        public const string Unknown = "coherence_unknown";
        public const string Snapshot = "continued_snapshot";

        /// <summary>
        /// Applicable, on the narrower basis: the plan reads nothing from the drawing file, the link was loaded
        /// by this bridge and is untouched since, and the host file still hashes as it did then. The
        /// references of the drawing were not checked - nothing in the plan came from them through the file.
        /// </summary>
        public const string LinkGeometryOnly = "link_geometry_only";

        /// <summary>The states under which a plan may be applied. Everything else withholds the permission.</summary>
        public static bool IsApplicableState(string state) =>
            string.Equals(state, Aligned, StringComparison.Ordinal) ||
            string.Equals(state, LinkGeometryOnly, StringComparison.Ordinal);

        /// <summary>
        /// The rules of a set that read the drawing FILE through the text extractor, one line each - empty when
        /// every action the set can produce comes from the CAD link's geometry alone. These are exactly the
        /// three branches that start CadDwgReader: blocks, solid_hatch_layers and a section read from labels.
        /// </summary>
        public static List<string> FileReadingRules(CadRequirementSet set)
        {
            var reads = new List<string>();
            if (set == null) return reads;
            foreach (CadRule rule in set.Rules)
            {
                if (rule == null) continue;
                if (rule.Geometry != null && rule.Geometry.Source == CadGeometrySource.Blocks)
                    reads.Add("rule '" + rule.Id + "': geometry.from = blocks (block names are read from the file)");
                if (rule.Geometry != null && rule.Geometry.SolidHatchLayers.Count > 0)
                    reads.Add("rule '" + rule.Id + "': solid_hatch_layers (the wall hatch is read from the file)");
                if (rule.Section != null)
                    reads.Add("rule '" + rule.Id + "': section (each run's size is read from the file's labels)");
            }
            return reads;
        }

        /// <summary>
        /// <paramref name="record"/> is what CadLinkLoads wrote when this bridge loaded the link, or null.
        /// The two "now" values are measured by the caller. Returns state, applicable, why and the sentences.
        /// </summary>
        public static JObject Decide(JObject record, string setNow, string fileShaNow, string printNow,
                                     bool fromSnapshot)
            => Decide(record, setNow, fileShaNow, printNow, fromSnapshot, false);

        /// <summary>
        /// The same decision, told whether the plan it is about reads ANYTHING from the drawing file.
        /// <paramref name="planReadsOnlyTheLink"/> true means no rule of its set reads the file (see
        /// FileReadingRules); a caller that does not know passes false and gets the strict answer.
        /// </summary>
        public static JObject Decide(JObject record, string setNow, string fileShaNow, string printNow,
                                     bool fromSnapshot, bool planReadsOnlyTheLink)
        {
            var o = new JObject();
            if (fromSnapshot)
            {
                o["state"] = Snapshot;
                o["applicable"] = false;
                o["means"] = "this reading continued a named snapshot and checked nothing: by its own contract " +
                             "no file was hashed and no reference was looked at. A plan cannot be declared " +
                             "current on a reading that did not look.";
                o["remedy"] = "read again without expect_analysis_fingerprint, or with require_current_sources.";
                return o;
            }

            if (record == null)
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = "no_record_of_loading_this_link";
                o["means"] = "this bridge did not load this link - it was linked in Revit, or loaded on another " +
                             "machine, or the model was saved as a copy - so it cannot say which issue of the " +
                             "drawing the geometry is. Revit records no moment for a CAD link's load.";
                o["remedy"] = "horizun_manage_cad_links operation=reload on this instance, then plan again: the " +
                              "reload is verified by the geometry it returns and records what it loaded.";
                return o;
            }

            string printThen = record.Value<string>("geometry_fingerprint");
            if (string.IsNullOrWhiteSpace(printThen) || string.IsNullOrWhiteSpace(printNow) ||
                !string.Equals(printThen, printNow, StringComparison.Ordinal))
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = string.IsNullOrWhiteSpace(printThen) || string.IsNullOrWhiteSpace(printNow)
                    ? "the_geometry_could_not_be_fingerprinted"
                    : "the_link_changed_after_this_bridge_recorded_it";
                o["means"] = "the geometry this link holds is not the geometry recorded when this bridge loaded " +
                             "it, so the record no longer describes what is loaded: somebody reloaded or edited " +
                             "the link elsewhere. What is loaded may well be current - this bridge cannot show it.";
                o["remedy"] = "horizun_manage_cad_links operation=reload, then plan again.";
                return o;
            }

            string setThen = record.Value<string>("source_set_sha256");
            if (string.IsNullOrWhiteSpace(setNow) || string.IsNullOrWhiteSpace(setThen))
            {
                if (planReadsOnlyTheLink) return DecideOnTheLinkAlone(o, record, fileShaNow);
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = "the_source_set_could_not_be_identified";
                o["means"] = "the identity of the file set could not be computed on one of the two sides - a " +
                             "reference that does not resolve from beside the host, or a drawing this machine " +
                             "has never read through the text extractor. Without both, the comparison would be " +
                             "between a number and nothing.";
                // WHAT ACTUALLY WRITES THE SET, named exactly. The earlier remedy sent the reader to
                // horizun_plan_from_cad or horizun_cad_networks, and neither starts the extractor unless a
                // rule reads the file - so following it changed nothing (MEASURED, dry run class 4).
                o["remedy"] = "the set identity is written only when the text extractor - AutoCAD's headless " +
                              "accoreconsole.exe, found under Program Files\\Autodesk\\AutoCAD* or named by " +
                              "HORIZUN_ACCORECONSOLE - reads the drawing. horizun_plan_from_cad does that, with " +
                              "dwg_path, when its requirement set has a rule that reads the file (geometry.from = " +
                              "blocks, solid_hatch_layers or section); check that every reference resolves, then " +
                              "plan again. A plan whose set reads NOTHING from the file does not need it: " +
                              "horizun_plan_from_cad and horizun_apply_cad_plan judge such a plan on the link and " +
                              "the host file's hash (state " + LinkGeometryOnly + ").";
                return o;
            }

            if (string.Equals(setThen, setNow, StringComparison.Ordinal))
            {
                o["state"] = Aligned;
                o["applicable"] = true;
                o["basis"] = "link_geometry_and_source_set";
                o["means"] = "the link was loaded by this bridge, nothing has touched it since, and the drawing " +
                             "and every reference still hash as they did then. The geometry in this plan and the " +
                             "sizes read from the file are the same issue of the drawing.";
                return o;
            }

            o["state"] = NotAligned;
            o["applicable"] = false;
            o["why"] = "the_sources_changed_since_the_link_was_loaded";
            o["differs"] = new JObject
            {
                ["file_sha256_when_loaded"] = record["file_sha256"],
                ["file_sha256_now"] = fileShaNow,
                ["host_file_changed"] = !string.Equals(record.Value<string>("file_sha256"), fileShaNow, StringComparison.Ordinal),
                ["source_set_when_loaded"] = setThen,
                ["source_set_now"] = setNow
            };
            o["means"] = "the drawing, or something it references, was revised after this link was loaded. The " +
                         "geometry here is the older issue; anything read from the file now is the newer one. A " +
                         "plan made of both would build one issue's runs with another issue's sizes.";
            o["remedy"] = "horizun_manage_cad_links operation=reload on this instance, then plan again.";
            return o;
        }

        /// <summary>
        /// The link and the host file, for a plan that reads nothing else. Reached only when the record
        /// describes what is loaded (the geometry fingerprint matched) and the set identity is missing.
        /// </summary>
        private static JObject DecideOnTheLinkAlone(JObject o, JObject record, string fileShaNow)
        {
            string fileThen = record.Value<string>("file_sha256");
            if (string.IsNullOrWhiteSpace(fileThen) || string.IsNullOrWhiteSpace(fileShaNow))
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = "the_drawing_file_could_not_be_hashed";
                o["means"] = "this plan reads nothing from the drawing file, so it is judged on the link and the " +
                             "host file - and the host file could not be hashed " +
                             (string.IsNullOrWhiteSpace(fileThen) ? "when this bridge loaded the link" : "now") +
                             ". Without it nothing shows the link is the drawing as it is on disk.";
                o["remedy"] = "make the DWG readable at the path the link names, then horizun_manage_cad_links " +
                              "operation=reload on this instance (it records the file's hash) and plan again.";
                return o;
            }
            if (!string.Equals(fileThen, fileShaNow, StringComparison.OrdinalIgnoreCase))
            {
                o["state"] = NotAligned;
                o["applicable"] = false;
                o["why"] = "the_drawing_changed_since_the_link_was_loaded";
                o["basis"] = "link_geometry_and_host_file";
                o["differs"] = new JObject
                {
                    ["file_sha256_when_loaded"] = fileThen,
                    ["file_sha256_now"] = fileShaNow,
                    ["host_file_changed"] = true
                };
                o["means"] = "the DWG on disk is not the file this link was loaded from: the drawing was revised " +
                             "after the load, and the link still shows the older issue. A plan made of it would " +
                             "build the drawing as it was, not as it is.";
                o["remedy"] = "horizun_manage_cad_links operation=reload on this instance, then plan again.";
                return o;
            }
            o["state"] = LinkGeometryOnly;
            o["applicable"] = true;
            o["basis"] = "link_geometry_and_host_file";
            o["references_checked"] = false;
            o["means"] = "every action in this plan comes from the CAD link's geometry - its requirement set reads " +
                         "nothing from the drawing file - and the link was loaded by this bridge, is untouched " +
                         "since, and the host DWG still hashes as it did then. There is no second half to disagree " +
                         "with. NOT checked: the drawing's external references, whose identity only the text " +
                         "extractor records; a reference revised after the load is still shown in its older " +
                         "issue until the link is reloaded.";
            return o;
        }
    }
}
