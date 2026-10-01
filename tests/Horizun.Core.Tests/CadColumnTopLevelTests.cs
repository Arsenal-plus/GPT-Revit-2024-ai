using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// WHERE A STRUCTURAL COLUMN STOPS.
    ///
    /// MEASURED (dry run, class 4): a closed_loops rule producing structural_column with
    /// M_Concrete-Round-Column: 300mm - a TwoLevelsBased family, as Revit's structural columns are - was refused
    /// by catalog_check_only with "a TwoLevelsBased family cannot be placed without a host (this mode takes
    /// OneLevelBased)", and there was no documented way to state the top level. No column was built.
    ///
    /// A rule producing structural_column may now declare top_level; the plan carries it to
    /// horizun_create_elements, which already places a two-level column base level to top level and reads both
    /// back. Without it the column is still refused, and the refusal names the field.
    /// </summary>
    public class CadColumnTopLevelTests
    {
        private const string RoundColumn = "M_Concrete-Round-Column: 300mm";

        private static CadRequirementSet Set(string columnExtra, string level = "MDP - Prueba CAD")
        {
            string doc = (@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'cols', 'version': '1.0.0', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ { 'id': 'columns', 'layers': ['S-COLS-____-MCUT'], 'produces': 'structural_column',
                           'category': 'OST_StructuralColumns', 'family_type': 'COLUMN', 'level': 'LEVEL'
                           EXTRA, 'geometry': { 'from': 'closed_loops' } } ]
            }").Replace("COLUMN", RoundColumn).Replace("LEVEL", level).Replace("EXTRA", columnExtra).Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static readonly HashSet<string> Levels = new HashSet<string> { "MDP - Prueba CAD", "MDP - Cubierta" };

        private static CadTypeFacts Facts(string placement) => new CadTypeFacts
        {
            Found = true, PlacementType = placement, Category = "OST_StructuralColumns"
        };

        private static JObject Row(CadRequirementSet set, string placement) =>
            CadCatalogCheck.Check(set, n => Facts(placement), Levels)["rules"].OfType<JObject>().Single();

        [Fact]
        public void Without_a_top_the_round_column_is_still_refused_and_the_refusal_names_top_level()
        {
            JObject row = Row(Set(""), "TwoLevelsBased");
            Assert.Equal("refused", (string)row["verdict"]);
            string problems = row["problems"].ToString();
            Assert.Contains("column_top_unstated", problems);
            Assert.Contains("top_level", problems);
            // the dry run's sentence, which offered no way out, is gone
            Assert.DoesNotContain("cannot be placed without a host", problems);
        }

        [Fact]
        public void With_a_top_level_the_round_column_is_usable()
        {
            JObject row = Row(Set(", 'top_level': 'MDP - Cubierta'"), "TwoLevelsBased");
            Assert.Equal("MDP - Cubierta", (string)row["top_level"]);
            Assert.Empty(row["problems"]);
            Assert.NotEqual("refused", (string)row["verdict"]);
        }

        [Fact]
        public void A_top_level_the_model_does_not_have_is_refused_by_name()
        {
            JObject row = Row(Set(", 'top_level': 'Roof'"), "TwoLevelsBased");
            Assert.Equal("refused", (string)row["verdict"]);
            Assert.Contains("level_not_found", row["problems"].ToString());
            Assert.Contains("Roof", row["problems"].ToString());
        }

        [Fact]
        public void A_one_level_column_is_unchanged_and_a_top_stated_for_it_is_refused_not_ignored()
        {
            Assert.Equal("usable_with_warnings", (string)Row(Set(""), "OneLevelBased")["verdict"]);
            JObject withTop = Row(Set(", 'top_level': 'MDP - Cubierta'"), "OneLevelBased");
            Assert.Equal("refused", (string)withTop["verdict"]);
            Assert.Contains("top_level_not_applicable", withTop["problems"].ToString());
        }

        [Fact]
        public void The_plan_and_the_catalogue_share_one_decision()
        {
            Assert.StartsWith("column_top_unstated", CadCatalogCheck.ColumnTopProblem("TwoLevelsBased", false));
            Assert.Null(CadCatalogCheck.ColumnTopProblem("TwoLevelsBased", true));
            Assert.Null(CadCatalogCheck.ColumnTopProblem("OneLevelBased", false));
            Assert.StartsWith("top_level_not_applicable", CadCatalogCheck.ColumnTopProblem("OneLevelBased", true));
        }

        [Fact]
        public void The_requirement_set_accepts_top_level_on_a_column_and_nowhere_it_would_be_ignored()
        {
            Assert.Equal("MDP - Cubierta", Set(", 'top_level': 'MDP - Cubierta'").Rules.Single().TopLevel);
            // the same storey is a column of no height
            Assert.Throws<CadRequirementSetException>(() => Set(", 'top_level': 'MDP - Prueba CAD'"));
            // still refused on a rule whose builder would ignore it
            string wall = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1.0.0', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ { 'id': 'walls', 'layers': ['A-WALL'], 'produces': 'wall', 'family_type': 'Basic Wall: G',
                           'level': 'L1', 'top_level': 'L2', 'height_mm': 3000,
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500 } } ]
            }".Replace('\'', '"');
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(JObject.Parse(wall)));
        }

        private static CadCandidate RingColumn()
        {
            var c = new CadCandidate
            {
                Id = "cadrev:col-1", SemanticId = "cadsem:col-1", GeometryId = "cadgeo:col-1",
                ProposedKind = "structural_column", RuleId = "columns", Layer = "S-COLS-____-MCUT",
                FamilyType = RoundColumn
            };
            c.Geometry.AddRange(new[]
            {
                new CadPoint(1000, 1000), new CadPoint(1300, 1000), new CadPoint(1300, 1300), new CadPoint(1000, 1300)
            });
            return c;
        }

        private static CadAuditSubject BuiltColumn(CadCandidate c, CadRequirementSet set, double x, double y)
        {
            var s = new CadAuditSubject
            {
                ElementId = 901, Category = "Structural Columns", TypeName = "300mm",
                Provenance = new CadProvenance
                {
                    SchemaVersion = 2, CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                    RuleId = c.RuleId, Layer = c.Layer, RequirementSetId = "cols", RequirementSetVersion = "1.0.0",
                    RequirementSetSha256 = set.Sha256, SourceFileSha256 = "sha"
                }
            };
            s.Geometry.Add(new CadPoint(x, y));
            return s;
        }

        [Fact]
        public void The_audit_measures_a_ring_column_from_the_centre_it_is_now_built_at()
        {
            CadRequirementSet set = Set(", 'top_level': 'MDP - Cubierta'");
            CadCandidate c = RingColumn();
            CadAudit atCentre = CadAuditRules.Compare(new[] { c }, new[] { BuiltColumn(c, set, 1150, 1150) }, set, "fp", "sha");
            Assert.Equal("agrees", Assert.Single(atCentre.Matches).State);
            // one built where the plan used to put it - the ring's first corner - is now seen to be off
            CadAudit atCorner = CadAuditRules.Compare(new[] { c }, new[] { BuiltColumn(c, set, 1000, 1000) }, set, "fp", "sha");
            Assert.NotEqual("agrees", Assert.Single(atCorner.Matches).State);
        }

        [Fact]
        public void The_emitted_column_row_carries_the_top_level_for_the_command_to_resolve()
        {
            CadRequirementSet set = Set(", 'top_level': 'MDP - Cubierta'");
            // a 300 mm column drawn as a closed square on the column layer
            var segs = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(300, 0), "S-COLS-____-MCUT"),
                new CadSegment(new CadPoint(300, 0), new CadPoint(300, 300), "S-COLS-____-MCUT"),
                new CadSegment(new CadPoint(300, 300), new CadPoint(0, 300), "S-COLS-____-MCUT"),
                new CadSegment(new CadPoint(0, 300), new CadPoint(0, 0), "S-COLS-____-MCUT")
            };
            CadConversionPlan plan = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(segs, set, "sha"), set, "cadsrc:x", true);
            List<JObject> rows = CadConversionPlanRules.AsCreateRequests(plan, "T")
                .SelectMany(q => ((JArray)q["elements"]).OfType<JObject>()).ToList();
            JObject column = rows.Single(r => (string)r["kind"] == "structural_column");
            Assert.Equal("MDP - Cubierta", (string)column["top_level_name"]);
            Assert.Equal("MDP - Prueba CAD", (string)column["level_name"]);
            // and it stands at the centre of the ring, not at its first corner (where it used to be emitted:
            // 150 mm off on both axes for a 300 mm column)
            Assert.Equal(150.0, (double)column["point"][0], 6);
            Assert.Equal(150.0, (double)column["point"][1], 6);
        }
    }
}
