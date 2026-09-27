// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_export format=cobie, the Revit-free half: the caller's mapping (every
// problem named, nothing defaulted from the machine), the project context's
// defaults (an explicit argument wins), and the COBie 2.4 rows built from plain
// facts - the columns of each sheet, the units, the room a component sits in, and
// every finding: a required cell left empty, a duplicate name, a reference to no
// row, a name that fell back. The facts are small and hand-built so every row is
// one an assertion is about.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CobieWorkbookTests
    {
        private const string Email = "handover@example.com";

        private static JObject MappingJson() => JObject.Parse(@"{
            ""created_by"": ""handover@example.com"",
            ""created_on"": ""2026-09-27T10:00:00Z"",
            ""facility"": { ""name"": ""Test Facility"", ""category"": ""Office"", ""project_name"": ""Test Project"", ""site_name"": ""Test Site"",
                            ""phase"": ""Handover"", ""currency_unit"": ""Dollars"" },
            ""phase"": ""New Construction"",
            ""component_categories"": [""OST_Doors"", ""OST_MechanicalEquipment""],
            ""category_parameter"": ""Classification""
        }");

        private static CobieMapping Mapping(Action<JObject> edit = null)
        {
            JObject json = MappingJson();
            edit?.Invoke(json);
            CobieMapping m = CobieMapping.Parse(json, out List<string> problems);
            Assert.Empty(problems);
            Assert.Empty(m.Missing());
            return m;
        }

        private static CobieSpaceFact Room(long id, string number, string name, string level, double areaSqFt, string category = "Room class")
            => new CobieSpaceFact
            {
                Id = id, UniqueId = "rm-" + id, RevitClass = "Room", Number = number, Name = name, LevelName = level,
                UnboundedHeightFeet = 10, AreaSquareFeet = areaSqFt, Category = category == null ? CobieValue.Missing() : CobieValue.Of(category)
            };

        private static CobieComponentFact Component(long id, long typeId, bool opening, params CobieSpaceFact[] spaces)
        {
            var c = new CobieComponentFact
            {
                Id = id, TypeId = typeId, UniqueId = "c-" + id, RevitClass = "FamilyInstance", Category = opening ? "OST_Doors" : "OST_MechanicalEquipment",
                IsOpening = opening, SpaceBasis = opening ? "to_from_room" : "location_point"
            };
            foreach (CobieSpaceFact s in spaces) c.Spaces.Add(new CobieSpaceRef { Id = s.Id, Number = s.Number });
            if (spaces.Length == 0) c.SpaceProblem = "it is in no room of phase 'New Construction' at its sample points (basis location_point)";
            return c;
        }

        /// <summary>Two stories and a reference level, two rooms, a door and an air handler, a supply system and one without scope.</summary>
        private static CobieFacts Facts()
        {
            var f = new CobieFacts
            {
                AuthoringSystem = "Autodesk Revit 2026", LengthUnitTypeId = "autodesk.unit.unit:millimeters-1.0.1",
                AreaUnitTypeId = "autodesk.unit.unit:squareMeters-1.0.1", VolumeUnitTypeId = "autodesk.unit.unit:cubicMeters-1.0.1", AreaBoundary = "Finish"
            };
            f.Levels.Add(new CobieLevelFact { Id = 10, UniqueId = "lv-10", Name = "Level 1", ElevationFeet = 0, IsBuildingStory = true, Category = CobieValue.Of("Storey") });
            f.Levels.Add(new CobieLevelFact { Id = 11, UniqueId = "lv-11", Name = "Level 2", ElevationFeet = 10, IsBuildingStory = true, Category = CobieValue.Of("Storey") });
            f.Levels.Add(new CobieLevelFact { Id = 12, UniqueId = "lv-12", Name = "Datum", ElevationFeet = 5, IsBuildingStory = false, Category = CobieValue.Missing() });
            CobieSpaceFact office = Room(20, "101", "Office", "Level 1", 100), plant = Room(21, "201", "Plant", "Level 2", 50);
            f.Spaces.Add(plant);
            f.Spaces.Add(office);
            f.Types.Add(new CobieTypeFact { Id = 30, UniqueId = "ty-30", RevitClass = "FamilySymbol", FamilyName = "Single-Flush", TypeName = "0915 x 2134mm",
                                            Category = CobieValue.Of("Doors"), Description = CobieValue.Of("Single flush door") });
            f.Types.Add(new CobieTypeFact { Id = 31, UniqueId = "ty-31", RevitClass = "FamilySymbol", FamilyName = "AHU", TypeName = "Large",
                                            Category = CobieValue.Of("Air handlers"), Description = CobieValue.Of("Air handling unit") });
            f.Types.Add(new CobieTypeFact { Id = 32, UniqueId = "ty-32", RevitClass = "FamilySymbol", FamilyName = "Unused", TypeName = "Type",
                                            Category = CobieValue.Of("x"), Description = CobieValue.Of("never placed in scope") });
            f.Components.Add(Component(40, 30, true, office));
            f.Components.Add(Component(41, 31, false, plant));
            var supply = new CobieSystemFact { Id = 50, UniqueId = "sy-50", RevitClass = "MechanicalSystem", Name = "Supply Air 1", Category = CobieValue.Of("Supply Air") };
            supply.MemberIds.AddRange(new long[] { 41, 999 });
            var other = new CobieSystemFact { Id = 51, UniqueId = "sy-51", RevitClass = "PipingSystem", Name = "Domestic Cold Water 1", Category = CobieValue.Of("DCW") };
            other.MemberIds.Add(998);
            f.Systems.Add(supply);
            f.Systems.Add(other);
            return f;
        }

        private static readonly DateTime On = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

        private static string Cell(CobieWorkbookPlan plan, string sheet, int row, string column)
        {
            XlsxSheet s = plan.Sheet(sheet);
            int c = Array.IndexOf(CobieRules.Columns[sheet], column);
            Assert.True(c >= 0, column + " is not a column of " + sheet);
            XlsxCell cell = s.Cell(row, c);
            return cell.Kind == XlsxCellKind.Empty ? null : cell.Value;
        }

        private static List<CobieFinding> Of(CobieWorkbookPlan plan, string kind) => plan.Findings.Where(f => f.Kind == kind).ToList();

        // ---- the sheets ------------------------------------------------------------------

        [Fact]
        public void The_seven_sheets_carry_the_cobie_2_4_columns_in_order()
        {
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), Facts(), On);
            Assert.Equal(new[] { "Facility", "Floor", "Space", "Zone", "Type", "Component", "System" }, plan.Sheets.Select(s => s.Name));
            foreach (XlsxSheet s in plan.Sheets)
                Assert.Equal(CobieRules.Columns[s.Name], s.Rows[0].Select(c => c.Value));
            Assert.Equal(22, CobieRules.Columns["Facility"].Length);
            Assert.Equal(10, CobieRules.Columns["Floor"].Length);
            Assert.Equal(13, CobieRules.Columns["Space"].Length);
            Assert.Equal(9, CobieRules.Columns["Zone"].Length);
            Assert.Equal(35, CobieRules.Columns["Type"].Length);
            Assert.Equal(15, CobieRules.Columns["Component"].Length);
            Assert.Equal(9, CobieRules.Columns["System"].Length);
            foreach (KeyValuePair<string, string[]> required in CobieRules.Required)
                Assert.All(required.Value, c => Assert.Contains(c, CobieRules.Columns[required.Key]));
        }

        [Fact]
        public void A_fully_mapped_model_is_ready_and_every_row_says_where_it_came_from()
        {
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), Facts(), On);
            Assert.Empty(plan.Findings);
            Assert.True(plan.Ready);

            // Facility: the caller's values, the document's units, the area basis read from Revit.
            Assert.Equal("Test Facility", Cell(plan, "Facility", 1, "Name"));
            Assert.Equal(Email, Cell(plan, "Facility", 1, "CreatedBy"));
            Assert.Equal("2026-09-27T10:00:00", Cell(plan, "Facility", 1, "CreatedOn"));
            Assert.Equal("millimeters", Cell(plan, "Facility", 1, "LinearUnits"));
            Assert.Equal("square meters", Cell(plan, "Facility", 1, "AreaUnits"));
            Assert.Equal("cubic meters", Cell(plan, "Facility", 1, "VolumeUnits"));
            Assert.Equal("Revit room area, computed at the wall finish", Cell(plan, "Facility", 1, "AreaMeasurement"));
            Assert.Equal("Autodesk Revit 2026", Cell(plan, "Facility", 1, "ExternalSystem"));
            Assert.Equal("Handover", Cell(plan, "Facility", 1, "Phase"));

            // Floor: building stories only, lowest first, elevation in the linear unit.
            XlsxSheet floor = plan.Sheet("Floor");
            Assert.Equal(3, floor.Rows.Count);
            Assert.Equal("Level 1", Cell(plan, "Floor", 1, "Name"));
            Assert.Equal("0", Cell(plan, "Floor", 1, "Elevation"));
            Assert.Equal("3048", Cell(plan, "Floor", 2, "Elevation"));
            Assert.Equal("Storey", Cell(plan, "Floor", 2, "Category"));
            Assert.Equal("lv-11", Cell(plan, "Floor", 2, "ExtIdentifier"));
            Assert.Equal("Level", Cell(plan, "Floor", 2, "ExtObject"));

            // Space: Name = Number, Description = Name, areas in square meters, GrossArea empty.
            Assert.Equal("101", Cell(plan, "Space", 1, "Name"));
            Assert.Equal("Office", Cell(plan, "Space", 1, "Description"));
            Assert.Equal("Level 1", Cell(plan, "Space", 1, "FloorName"));
            Assert.Equal("101", Cell(plan, "Space", 1, "RoomTag"));
            Assert.Equal("9.2903", Cell(plan, "Space", 1, "NetArea"));
            Assert.Null(Cell(plan, "Space", 1, "GrossArea"));
            Assert.Equal("3048", Cell(plan, "Space", 1, "UsableHeight"));
            Assert.Equal("4.6452", Cell(plan, "Space", 2, "NetArea"));
            Assert.Equal(XlsxCellKind.Number, plan.Sheet("Space").Rows[1][Array.IndexOf(CobieRules.Columns["Space"], "NetArea")].Kind);

            // Type: only the types in scope, "Family: Type", the type's Description.
            Assert.Equal(3, plan.Sheet("Type").Rows.Count);
            Assert.Equal("AHU: Large", Cell(plan, "Type", 1, "Name"));
            Assert.Equal("Single-Flush: 0915 x 2134mm", Cell(plan, "Type", 2, "Name"));
            Assert.Equal("Single flush door", Cell(plan, "Type", 2, "Description"));

            // Component: <Type name>-<id> without a name parameter; the room it sits in; the type's description.
            Assert.Equal("AHU: Large-41", Cell(plan, "Component", 1, "Name"));
            Assert.Equal("AHU: Large", Cell(plan, "Component", 1, "TypeName"));
            Assert.Equal("201", Cell(plan, "Component", 1, "Space"));
            Assert.Equal("Air handling unit", Cell(plan, "Component", 1, "Description"));
            Assert.Equal("Single-Flush: 0915 x 2134mm-40", Cell(plan, "Component", 2, "Name"));
            Assert.Equal("101", Cell(plan, "Component", 2, "Space"));

            // System: one row per in-scope component; a system with none is not written.
            Assert.Equal(2, plan.Sheet("System").Rows.Count);
            Assert.Equal("Supply Air 1", Cell(plan, "System", 1, "Name"));
            Assert.Equal("AHU: Large-41", Cell(plan, "System", 1, "ComponentNames"));
            Assert.Equal(2, plan.SystemsRead);
            Assert.Equal(1, plan.SystemsWritten);
            Assert.Equal(new[] { "Domestic Cold Water 1" }, plan.SystemsWithoutScopeSample);

            // Every row carries CreatedBy and CreatedOn.
            foreach (XlsxSheet s in plan.Sheets)
                for (int r = 1; r < s.Rows.Count; r++)
                {
                    Assert.Equal(Email, s.Cell(r, Array.IndexOf(CobieRules.Columns[s.Name], "CreatedBy")).Value);
                    Assert.Equal("2026-09-27T10:00:00", s.Cell(r, Array.IndexOf(CobieRules.Columns[s.Name], "CreatedOn")).Value);
                }
        }

        [Fact]
        public void The_same_facts_build_the_same_cells_and_only_created_on_moves_with_the_export_time()
        {
            CobieWorkbookPlan a = CobieRules.Build(Mapping(), Facts(), On);
            CobieWorkbookPlan b = CobieRules.Build(Mapping(), Facts(), On);
            CobieWorkbookPlan later = CobieRules.Build(Mapping(), Facts(), On.AddHours(3));
            Assert.Equal(a.CellsDigest(), b.CellsDigest());
            Assert.Equal(a.ContentDigest(), later.ContentDigest());
            Assert.NotEqual(a.CellsDigest(), later.CellsDigest());
            CobieFacts renamed = Facts();
            renamed.Spaces[1].Name = "Meeting";
            Assert.NotEqual(a.ContentDigest(), CobieRules.Build(Mapping(), renamed, On).ContentDigest());
        }

        [Fact]
        public void The_planned_workbook_is_written_and_read_back_cell_for_cell()
        {
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(m => m["zone_parameter"] = "Zone"), WithZones(Facts()), On);
            string dir = Path.Combine(Path.GetTempPath(), "hz-cobie-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "handover.xlsx");
                XlsxWorkbookWriter.WriteFile(path, plan.Sheets);
                List<XlsxSheet> back = XlsxWorkbookReader.Read(path);
                XlsxComparison comparison = XlsxWorkbookReader.Compare(plan.Sheets, back);
                Assert.True(comparison.Matches, string.Join("; ", comparison.Differences));
                Assert.Equal(plan.CellsDigest(), comparison.ReadDigest);
                Assert.Equal(CobieRules.SheetOrder, back.Select(s => s.Name));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- findings: required, unique, referenced ---------------------------------------

        [Fact]
        public void Without_category_parameter_every_category_cell_is_an_empty_required_field_never_na()
        {
            CobieMapping m = Mapping(j => j.Remove("category_parameter"));
            CobieWorkbookPlan plan = CobieRules.Build(m, Facts(), On);
            List<CobieFinding> required = Of(plan, CobieRules.KindRequired);
            Assert.Equal(new[] { "Floor", "Floor", "Space", "Space", "Type", "Type", "System" }, required.Select(f => f.Sheet));
            Assert.All(required, f =>
            {
                Assert.Equal("Category", f.Column);
                Assert.True(f.Blocking);
                Assert.Equal("category_parameter was not given", f.Detail);
            });
            Assert.Contains(required, f => f.Sheet == "Space" && f.Row == "101" && f.ElementId == 20);
            Assert.Null(Cell(plan, "Space", 1, "Category"));
            Assert.DoesNotContain(plan.Sheets.SelectMany(s => s.Rows.Skip(1)).SelectMany(r => r), c => c.Value == "n/a");
            Assert.False(plan.Ready);
            Assert.True(JToken.DeepEquals(new JArray("required_field: 7"), plan.BlockingJson()), plan.BlockingJson().ToString());
        }

        [Fact]
        public void A_missing_or_empty_classification_says_which_on_the_finding()
        {
            CobieFacts f = Facts();
            f.Spaces[0].Category = CobieValue.Of("   ");
            f.Spaces[1].Category = CobieValue.Missing();
            f.Types[0].Category = CobieValue.Unreadable("boom");
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            List<CobieFinding> required = Of(plan, CobieRules.KindRequired);
            Assert.Contains(required, x => x.Sheet == "Space" && x.Row == "201" && x.Detail == "category_parameter 'Classification' is empty on the room");
            Assert.Contains(required, x => x.Sheet == "Space" && x.Row == "101" && x.Detail == "category_parameter 'Classification' is not a parameter of the room");
            Assert.Contains(required, x => x.Sheet == "Type" && x.Row == "Single-Flush: 0915 x 2134mm" && x.Detail == "category_parameter 'Classification' could not be read: boom");
        }

        [Fact]
        public void Facility_fields_the_caller_left_out_are_findings_naming_the_argument()
        {
            CobieMapping m = Mapping(j => { ((JObject)j["facility"]).Remove("currency_unit"); ((JObject)j["facility"]).Remove("phase"); });
            CobieWorkbookPlan plan = CobieRules.Build(m, Facts(), On);
            List<CobieFinding> facility = plan.Findings.Where(f => f.Sheet == "Facility").ToList();
            Assert.Equal(new[] { "CurrencyUnit", "Phase" }, facility.Select(f => f.Column));
            Assert.Equal("cobie.facility.currency_unit was not given", facility[0].Detail);
            Assert.Equal("Test Facility", facility[0].Row);
        }

        [Fact]
        public void A_component_in_no_room_is_a_required_field_that_carries_the_membership_reason()
        {
            CobieFacts f = Facts();
            f.Components.Add(Component(42, 31, false));
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            CobieFinding finding = Of(plan, CobieRules.KindRequired).Single();
            Assert.Equal("Component", finding.Sheet);
            Assert.Equal("Space", finding.Column);
            Assert.Equal("AHU: Large-42", finding.Row);
            Assert.Equal(42, finding.ElementId);
            Assert.Contains("in no room of phase 'New Construction'", finding.Detail);
        }

        [Fact]
        public void Duplicate_names_are_findings_compared_without_case_and_outer_spaces()
        {
            CobieFacts f = Facts();
            f.Spaces.Add(Room(22, " 101 ", "Office annex", "Level 1", 20));
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            CobieFinding duplicate = Of(plan, CobieRules.KindDuplicate).Single();
            Assert.Equal("Space", duplicate.Sheet);
            Assert.Contains("2 Space rows are named", duplicate.Detail);
            Assert.Contains("element ids", duplicate.Detail);
            Assert.True(duplicate.Blocking);
            Assert.False(plan.Ready);
        }

        [Fact]
        public void Two_revit_systems_under_one_name_are_a_duplicate_even_with_different_components()
        {
            CobieFacts f = Facts();
            f.Components.Add(Component(43, 31, false, f.Spaces[0]));
            var twin = new CobieSystemFact { Id = 52, UniqueId = "sy-52", RevitClass = "MechanicalSystem", Name = "Supply Air 1", Category = CobieValue.Of("Supply Air") };
            twin.MemberIds.Add(43);
            f.Systems.Add(twin);
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            CobieFinding duplicate = Of(plan, CobieRules.KindDuplicate).Single();
            Assert.Equal("System", duplicate.Sheet);
            Assert.Contains("2 Revit systems are named 'Supply Air 1'", duplicate.Detail);
            Assert.Contains("50, 52", duplicate.Detail);
        }

        [Fact]
        public void A_space_on_a_level_that_is_not_a_story_breaks_its_floor_reference()
        {
            CobieFacts f = Facts();
            f.Spaces.Add(Room(23, "M01", "Mezzanine", "Datum", 30));
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            CobieFinding broken = Of(plan, CobieRules.KindReference).Single();
            Assert.Equal("Space", broken.Sheet);
            Assert.Equal("FloorName", broken.Column);
            Assert.Equal("M01", broken.Row);
            Assert.Equal("level 'Datum' is not marked Building Story, so it has no Floor row", broken.Detail);
            Assert.True(broken.Blocking);
        }

        [Fact]
        public void A_component_in_a_room_that_is_not_a_space_row_breaks_its_reference()
        {
            CobieFacts f = Facts();
            CobieComponentFact c = Component(44, 31, false);
            c.Spaces.Add(new CobieSpaceRef { Id = 77, Number = "X99" });
            c.SpaceProblem = null;
            f.Components.Add(c);
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            CobieFinding broken = Of(plan, CobieRules.KindReference).Single();
            Assert.Equal("Component", broken.Sheet);
            Assert.Contains("no Space row is named 'X99'", broken.Detail);
        }

        // ---- names, zones, spans, parameters ---------------------------------------------

        [Fact]
        public void A_missing_empty_or_shared_mark_falls_back_to_type_and_id_and_says_why()
        {
            CobieFacts f = Facts();
            f.Components[0].NameValue = CobieValue.Of("D-01");
            f.Components[1].NameValue = CobieValue.Of("  ");
            f.Components.Add(Component(45, 31, false, f.Spaces[0]));
            f.Components[2].NameValue = CobieValue.Of("AHU-1");
            f.Components.Add(Component(46, 31, false, f.Spaces[0]));
            f.Components[3].NameValue = CobieValue.Of("ahu-1 ");
            f.Components.Add(Component(47, 31, false, f.Spaces[0]));
            f.Components[4].NameValue = CobieValue.Missing();
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(j => j["component_name_parameter"] = "Mark"), f, On);

            var names = Enumerable.Range(1, plan.Sheet("Component").Rows.Count - 1).Select(r => Cell(plan, "Component", r, "Name")).ToList();
            Assert.Contains("D-01", names);
            Assert.Contains("AHU: Large-41", names);
            Assert.Contains("AHU: Large-45", names);
            Assert.Contains("AHU: Large-46", names);
            Assert.Contains("AHU: Large-47", names);
            List<CobieFinding> fallbacks = Of(plan, CobieRules.KindNameFallback);
            Assert.Equal(4, fallbacks.Count);
            Assert.All(fallbacks, x => Assert.False(x.Blocking));
            Assert.Contains(fallbacks, x => x.ElementId == 41 && x.Detail.Contains("the parameter is empty"));
            Assert.Contains(fallbacks, x => x.ElementId == 45 && x.Detail.Contains("'AHU-1' is shared by 2 components"));
            Assert.Contains(fallbacks, x => x.ElementId == 47 && x.Detail.Contains("on neither the component nor its type"));
            Assert.Equal(4, plan.NameFallbacks);
            Assert.True(plan.Ready);
        }

        private static CobieFacts WithZones(CobieFacts f)
        {
            f.Spaces[0].Zone = CobieValue.Of("Plant zone");
            f.Spaces[1].Zone = CobieValue.Of("Office zone");
            return f;
        }

        [Fact]
        public void Zones_are_one_row_per_zone_per_space_and_a_space_without_one_is_advisory()
        {
            CobieFacts f = WithZones(Facts());
            f.Spaces.Add(Room(24, "102", "Store", "Level 1", 10));
            f.Spaces[2].Zone = CobieValue.Of("Office zone");
            f.Spaces.Add(Room(25, "103", "Corridor", "Level 1", 10));
            f.Spaces[3].Zone = CobieValue.Of("");
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(j => { j["zone_parameter"] = "Zone"; j["zone_category"] = "Occupancy"; }), f, On);
            XlsxSheet zone = plan.Sheet("Zone");
            Assert.Equal(4, zone.Rows.Count);
            Assert.Equal(new[] { "Office zone/101", "Office zone/102", "Plant zone/201" },
                Enumerable.Range(1, 3).Select(r => Cell(plan, "Zone", r, "Name") + "/" + Cell(plan, "Zone", r, "SpaceNames")));
            Assert.Equal("Occupancy", Cell(plan, "Zone", 1, "Category"));
            CobieFinding notInZone = Of(plan, CobieRules.KindNotInZone).Single();
            Assert.Equal("103", notInZone.Row);
            Assert.False(notInZone.Blocking);
            Assert.True(plan.Ready);

            CobieWorkbookPlan noCategory = CobieRules.Build(Mapping(j => j["zone_parameter"] = "Zone"), WithZones(Facts()), On);
            Assert.Equal(2, Of(noCategory, CobieRules.KindRequired).Count(x => x.Sheet == "Zone" && x.Column == "Category"));
        }

        [Fact]
        public void A_component_across_spaces_lists_them_all_and_only_non_openings_are_flagged()
        {
            CobieFacts f = Facts();
            f.Components.Add(Component(48, 31, false, f.Spaces[0], f.Spaces[1]));
            f.Components.Add(Component(49, 30, true, f.Spaces[1], f.Spaces[0]));
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            int row = Enumerable.Range(1, plan.Sheet("Component").Rows.Count - 1).Single(r => Cell(plan, "Component", r, "Name") == "AHU: Large-48");
            Assert.Equal("201,101", Cell(plan, "Component", row, "Space"));
            CobieFinding spans = Of(plan, CobieRules.KindSpansSpaces).Single();
            Assert.Equal(48, spans.ElementId);
            Assert.False(spans.Blocking);
        }

        [Fact]
        public void A_mapped_parameter_that_exists_nowhere_is_one_advisory_finding_per_sheet()
        {
            CobieFacts f = Facts();
            foreach (CobieTypeFact t in f.Types) t.Fields["Manufacturer"] = CobieValue.Missing();
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(j => j["type_fields"] = new JObject { ["Manufacturer"] = "Manufactuer" }), f, On);
            CobieFinding missing = Of(plan, CobieRules.KindParameterMissing).Single();
            Assert.Equal("Type", missing.Sheet);
            Assert.Contains("type_fields.Manufacturer 'Manufactuer' is a parameter of none of the 2 element(s)", missing.Detail);
            Assert.True(plan.Ready);
        }

        [Fact]
        public void Mapped_fields_fill_their_columns_and_description_can_be_remapped()
        {
            CobieFacts f = Facts();
            f.Types[0].Fields["Manufacturer"] = CobieValue.Of("Acme Doors");
            f.Types[1].Fields["Manufacturer"] = CobieValue.Of("Acme Air");
            f.Components[0].Fields["TagNumber"] = CobieValue.Of("T-40");
            f.Components[1].Fields["TagNumber"] = CobieValue.Of("T-41");
            f.Components[0].Fields["Description"] = CobieValue.Of("Door to office");
            f.Components[1].Fields["Description"] = CobieValue.Of("Main air handler");
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(j =>
            {
                j["type_fields"] = new JObject { ["Manufacturer"] = "Manufacturer" };
                j["component_fields"] = new JObject { ["TagNumber"] = "Mark", ["Description"] = "Comments" };
            }), f, On);
            Assert.Equal("Acme Air", Cell(plan, "Type", 1, "Manufacturer"));
            Assert.Equal("T-41", Cell(plan, "Component", 1, "TagNumber"));
            Assert.Equal("Main air handler", Cell(plan, "Component", 1, "Description"));
            Assert.Empty(plan.Findings);
        }

        [Fact]
        public void No_component_in_scope_is_advisory_and_the_sheets_keep_their_headers()
        {
            CobieFacts f = Facts();
            f.Components.Clear();
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            Assert.Single(plan.Sheet("Component").Rows);
            Assert.Single(plan.Sheet("Type").Rows);
            Assert.Single(plan.Sheet("System").Rows);
            CobieFinding empty = Of(plan, CobieRules.KindEmptyScope).Single();
            Assert.Contains("OST_Doors, OST_MechanicalEquipment", empty.Detail);
            Assert.False(empty.Blocking);
        }

        [Fact]
        public void Findings_are_listed_up_to_the_cap_and_counted_whole()
        {
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(j => j.Remove("category_parameter")), Facts(), On);
            JObject json = plan.FindingsJson(3);
            Assert.Equal(7, (int)json["total"]);
            Assert.Equal(7, (int)json["blocking"]);
            Assert.Equal(3, (int)json["listed"]);
            Assert.True((bool)json["truncated"]);
            Assert.Equal(3, ((JArray)json["items"]).Count);
            Assert.Equal(2, (int)json["by_sheet"]["Space"]["required_field"]);
            Assert.Equal(7, (int)json["by_kind"]["required_field"]);
            Assert.Equal("Floor", (string)json["items"][0]["sheet"]);
        }

        // ---- units -------------------------------------------------------------------------

        [Fact]
        public void Units_come_from_the_document_and_a_composite_or_unknown_one_is_written_in_a_named_unit()
        {
            CobieUnits metric = CobieUnits.Resolve("autodesk.unit.unit:millimeters-1.0.1", "autodesk.unit.unit:squareMeters-1.0.1", "autodesk.unit.unit:cubicMeters-1.0.1");
            Assert.Equal(new[] { "millimeters", "square meters", "cubic meters" }, new[] { metric.Linear, metric.Area, metric.Volume });
            Assert.Equal(304.8, metric.LinearPerFoot);
            Assert.Equal(0.09290304, metric.AreaPerSquareFoot);
            Assert.Empty(metric.Notes);

            CobieUnits imperial = CobieUnits.Resolve("autodesk.unit.unit:feetFractionalInches-1.0.0", "autodesk.unit.unit:squareFeet-1.0.1", "autodesk.unit.unit:cubicYards-1.0.1");
            Assert.Equal(new[] { "feet", "square feet", "cubic yards" }, new[] { imperial.Linear, imperial.Area, imperial.Volume });
            Assert.Equal(1.0, imperial.LinearPerFoot);
            Assert.Single(imperial.Notes);
            Assert.Contains("'feetFractionalInches'", imperial.Notes[0]);

            CobieUnits odd = CobieUnits.Resolve("autodesk.unit.unit:decimeters-1.0.1", "autodesk.unit.unit:hectares-1.0.0", null);
            Assert.Equal(new[] { "meters", "square meters", "cubic meters" }, new[] { odd.Linear, odd.Area, odd.Volume });
            Assert.Equal(3, odd.Notes.Count);
            Assert.Contains(odd.Notes, n => n.Contains("'autodesk.unit.unit:decimeters-1.0.1' has no COBie pick-list name"));
            Assert.Contains(odd.Notes, n => n.Contains("'unreadable'"));
            Assert.Equal("squareMeters", CobieUnits.UnitName("autodesk.unit.unit:squareMeters-1.0.1"));
        }

        [Fact]
        public void Feet_documents_write_elevations_and_areas_in_feet()
        {
            CobieFacts f = Facts();
            f.LengthUnitTypeId = "autodesk.unit.unit:feet-1.0.1";
            f.AreaUnitTypeId = "autodesk.unit.unit:squareFeet-1.0.1";
            CobieWorkbookPlan plan = CobieRules.Build(Mapping(), f, On);
            Assert.Equal("10", Cell(plan, "Floor", 2, "Elevation"));
            Assert.Equal("100", Cell(plan, "Space", 1, "NetArea"));
            Assert.Equal("feet", Cell(plan, "Facility", 1, "LinearUnits"));
        }

        // ---- the mapping -------------------------------------------------------------------

        [Fact]
        public void The_mapping_names_every_problem_at_once()
        {
            var json = JObject.Parse(@"{
                ""created_by"": 5, ""creator"": ""x"", ""created_on"": ""yesterday"",
                ""facility"": { ""name"": ""F"", ""owner"": ""x"" },
                ""component_categories"": [""Doors"", ""OST_Doors,OST_Windows"", 3, ""OST_Doors""],
                ""space_source"": ""areas"", ""zone_category"": ""Fire"",
                ""type_fields"": { ""Name"": ""x"", ""Manufacturer"": """" },
                ""component_fields"": { ""Space"": ""x"" },
                ""project_context_path"": ""relative/context.json"", ""max_findings"": 0 }");
            CobieMapping.Parse(json, out List<string> problems);
            string all = string.Join(" | ", problems);
            Assert.Contains("cobie.created_by must be a string", all);
            Assert.Contains("cobie.creator is not a cobie field", all);
            Assert.Contains("cobie.created_on must be an ISO-8601", all);
            Assert.Contains("cobie.facility.owner is not a facility field", all);
            Assert.Contains("\"Doors\", which is not one BuiltInCategory token", all);
            Assert.Contains("\"OST_Doors,OST_Windows\", which is not one BuiltInCategory token", all);
            Assert.Contains("holds 3, which is not", all);
            Assert.Contains("cobie.space_source must be rooms or spaces", all);
            Assert.Contains("cobie.zone_category needs zone_parameter", all);
            Assert.Contains("cobie.type_fields.Name is not a column it can fill", all);
            Assert.Contains("cobie.type_fields.Manufacturer must name a Revit parameter", all);
            Assert.Contains("cobie.component_fields.Space is not a column it can fill", all);
            Assert.Contains("cobie.project_context_path must be an absolute path", all);
            Assert.Contains("cobie.max_findings must be 1..5000", all);
        }

        [Fact]
        public void Requirements_nothing_can_default_are_named_and_created_by_never_comes_from_the_machine()
        {
            CobieMapping m = CobieMapping.Parse(new JObject(), out List<string> problems);
            Assert.Empty(problems);
            string missing = string.Join(" | ", m.Missing());
            Assert.Contains("cobie.created_by is required", missing);
            Assert.Contains("never taken from this machine", missing);
            Assert.Contains("cobie.facility.name is required", missing);
            Assert.Contains("cobie.phase is required", missing);
            Assert.Contains("cobie.component_categories is required", missing);
            Assert.Null(m.CreatedBy);

            CobieMapping notEmail = CobieMapping.Parse(JObject.Parse(@"{ ""created_by"": ""Jane Doe"" }"), out problems);
            Assert.Contains(notEmail.Missing(), x => x.Contains("'Jane Doe' is not an e-mail address"));
        }

        [Fact]
        public void Created_on_accepts_a_date_a_date_time_and_what_the_request_parser_turned_into_a_date()
        {
            var problems = new List<string>();
            Assert.Equal(new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc), CobieMapping.CreatedOn(new JValue("2026-09-27"), problems));
            Assert.Equal(new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc), CobieMapping.CreatedOn(new JValue("2026-09-27T10:00:00+02:00"), problems));
            Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), CobieMapping.CreatedOn(new JValue("2026-09-27T10:00:00"), problems));
            // JObject.Parse turns an ISO string into a Date token before the command sees it.
            JToken parsed = JObject.Parse(@"{ ""d"": ""2026-09-27T10:00:00Z"" }")["d"];
            Assert.Equal(JTokenType.Date, parsed.Type);
            Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), CobieMapping.CreatedOn(parsed, problems));
            Assert.Empty(problems);
            Assert.Equal("2026-09-27T10:00:00", CobieMapping.CreatedOnText(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc)));
            Assert.Null(Mapping(j => j.Remove("created_on")).CreatedOnUtc);
        }

        // ---- the project context -----------------------------------------------------------

        private static string WriteContext(string dir, string json)
        {
            string path = Path.Combine(dir, "project-context.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void A_project_context_fills_what_the_arguments_left_out_and_an_argument_wins()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-cobie-ctx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = WriteContext(dir, @"{ ""schema_version"": 1,
                    ""project"": { ""code"": ""PRJ"", ""name"": ""Context Project"", ""location"": ""Context Site"", ""description"": ""From the context"" },
                    ""appointment"": { ""stage"": ""Stage 6 handover"" },
                    ""classification"": { ""system"": ""custom"", ""type_parameter"": ""Assembly Code"" } }");
                JObject json = MappingJson();
                ((JObject)json["facility"]).Remove("project_name");
                ((JObject)json["facility"]).Remove("phase");
                json.Remove("category_parameter");
                json["project_context_path"] = path;
                CobieMapping m = CobieMapping.Parse(json, out List<string> problems);
                Assert.Empty(problems);

                JObject context = CobieMapping.ReadProjectContext(path, out string sha, out string error);
                Assert.Null(error);
                Assert.Equal(64, sha.Length);
                List<string> applied = m.ApplyProjectContext(context);
                Assert.Equal(new[] { "facility.project_name <- /project/name", "facility.project_description <- /project/description",
                                     "facility.phase <- /appointment/stage", "category_parameter <- /classification/type_parameter" }, applied);
                Assert.Equal("Context Project", m.Facility.ProjectName);
                Assert.Equal("Test Site", m.Facility.SiteName);            // the argument wins
                Assert.Equal("Stage 6 handover", m.Facility.Phase);
                Assert.Equal("Assembly Code", m.CategoryParameter);
                JObject provenance = m.ProvenanceJson();
                Assert.Equal("argument", (string)provenance["created_by"]);
                Assert.Equal("argument", (string)provenance["facility.site_name"]);
                Assert.Equal("project_context /project/name", (string)provenance["facility.project_name"]);
                Assert.Equal("project_context /classification/type_parameter", (string)provenance["category_parameter"]);
                Assert.Empty(m.Missing());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void A_project_context_cannot_provide_created_by()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-cobie-ctx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = WriteContext(dir, @"{ ""schema_version"": 1, ""project"": { ""code"": ""PRJ"", ""name"": ""P"" },
                    ""intake"": { ""confirmed_by"": ""someone@example.com"" } }");
                JObject json = MappingJson();
                json.Remove("created_by");
                json["project_context_path"] = path;
                CobieMapping m = CobieMapping.Parse(json, out List<string> problems);
                m.ApplyProjectContext(CobieMapping.ReadProjectContext(path, out _, out _));
                Assert.Null(m.CreatedBy);
                Assert.Contains(m.Missing(), x => x.Contains("a project-context v1 file has no contact field"));
                Assert.Equal("absent", (string)m.ProvenanceJson()["created_by"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void A_file_that_is_not_a_v1_project_context_is_refused_by_reason()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-cobie-ctx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string error;
                Assert.Null(CobieMapping.ReadProjectContext(WriteContext(dir, @"{ ""schema_version"": 2 }"), out _, out error));
                Assert.Contains("schema_version is 2", error);
                Assert.Null(CobieMapping.ReadProjectContext(WriteContext(dir, "not json"), out _, out error));
                Assert.Contains("it is not JSON", error);
                Assert.Null(CobieMapping.ReadProjectContext(WriteContext(dir, "[1,2]"), out _, out error));
                Assert.Contains("not a JSON object", error);
                Assert.Null(CobieMapping.ReadProjectContext(Path.Combine(dir, "absent.json"), out _, out error));
                Assert.Contains("there is no file", error);
                Assert.Null(CobieMapping.ReadProjectContext("relative.json", out _, out error));
                Assert.Contains("absolute", error);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
