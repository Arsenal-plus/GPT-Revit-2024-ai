// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// COBIE 2.4 SHEETS FROM PLAIN FACTS: horizun_export format=cobie, the Revit-free
// half. The command reads the model into the facts below (levels, rooms or spaces,
// the component instances and their types, MEP systems, the parameters the mapping
// names, and where each component sits); everything that turns those facts into
// rows, and everything that JUDGES the rows, lives here and is unit-tested.
//
// SEVEN SHEETS, the COBie 2.4 column set of each, in COBie order: Facility, Floor,
// Space, Zone, Type, Component, System. Columns this tool has no source for are
// written EMPTY (never "n/a"), so the workbook keeps COBie's shape and a reviewer
// sees exactly what is missing.
//
// FINDINGS ARE REPORTED, NEVER SILENTLY FIXED:
//   blocking   required_field    a column this tool requires is empty on a row
//              duplicate_name    two rows share a sheet's key (case- and space-
//                                insensitive), or two Revit systems share a name
//              broken_reference  a Space's FloorName or a Component's Space names
//                                no row of the sheet it points at
//   advisory   name_fallback     component_name_parameter was asked for and the
//                                component's value was missing, empty or shared
//              spans_spaces      a component (not a door or window) sits in two or
//                                more spaces; all are written
//              not_in_zone       zone_parameter is empty on a space
//              parameter_missing a mapped parameter exists on no element of a sheet
//              empty_scope       no component instance matched
// deliverable_ready is false while any blocking finding exists. A workbook with
// gaps is still written: it is a deliverable under review, not a lie, and every
// gap is named.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A parameter as read from the model: missing, unreadable, empty, or a value.</summary>
    public sealed class CobieValue
    {
        public bool Exists;
        public string Text;
        public string Problem;

        public static CobieValue Missing() => new CobieValue { Exists = false };
        public static CobieValue Of(string text) => new CobieValue { Exists = true, Text = text };
        public static CobieValue Unreadable(string problem) => new CobieValue { Exists = true, Problem = problem };

        /// <summary>The value as a cell holds it: whitespace-only is empty.</summary>
        public string Value => string.IsNullOrWhiteSpace(Text) ? null : Text;
    }

    public sealed class CobieLevelFact
    {
        public long Id;
        public string UniqueId, Name;
        public double? ElevationFeet;
        public bool IsBuildingStory;
        public CobieValue Category;
    }

    public sealed class CobieSpaceFact
    {
        public long Id;
        public string UniqueId, RevitClass, Number, Name, LevelName;
        public double? UnboundedHeightFeet, AreaSquareFeet;
        public CobieValue Category, Zone;
    }

    public sealed class CobieTypeFact
    {
        public long Id;
        public string UniqueId, RevitClass, FamilyName, TypeName;
        /// <summary>category_parameter read on the type.</summary>
        public CobieValue Category;
        /// <summary>The type's built-in Description parameter.</summary>
        public CobieValue Description;
        /// <summary>type_fields: COBie column -> the value of its mapped parameter on the type.</summary>
        public readonly Dictionary<string, CobieValue> Fields = new Dictionary<string, CobieValue>(StringComparer.Ordinal);
    }

    public sealed class CobieSpaceRef
    {
        public long Id;
        public string Number;
    }

    public sealed class CobieComponentFact
    {
        public long Id, TypeId;
        public string UniqueId, RevitClass, Category;
        /// <summary>A door or window: its space comes from Revit's own opening rules.</summary>
        public bool IsOpening;
        public CobieValue NameValue;
        public readonly List<CobieSpaceRef> Spaces = new List<CobieSpaceRef>();
        public string SpaceBasis, SpaceProblem;
        /// <summary>component_fields: COBie column -> the value of its mapped parameter (instance, else type).</summary>
        public readonly Dictionary<string, CobieValue> Fields = new Dictionary<string, CobieValue>(StringComparer.Ordinal);
    }

    public sealed class CobieSystemFact
    {
        public long Id;
        public string UniqueId, RevitClass, Name;
        public CobieValue Category;
        public readonly List<long> MemberIds = new List<long>();
    }

    /// <summary>What the command read from the model, and nothing it decided.</summary>
    public sealed class CobieFacts
    {
        public string AuthoringSystem;
        public string LengthUnitTypeId, AreaUnitTypeId, VolumeUnitTypeId;
        /// <summary>SpatialElementBoundaryLocation of the rooms/spaces read (Finish, Center, ...); null when unread.</summary>
        public string AreaBoundary;
        public readonly List<CobieLevelFact> Levels = new List<CobieLevelFact>();
        public readonly List<CobieSpaceFact> Spaces = new List<CobieSpaceFact>();
        public readonly List<CobieTypeFact> Types = new List<CobieTypeFact>();
        public readonly List<CobieComponentFact> Components = new List<CobieComponentFact>();
        public readonly List<CobieSystemFact> Systems = new List<CobieSystemFact>();
    }

    /// <summary>The COBie unit names and the factors from Revit's internal feet.</summary>
    public sealed class CobieUnits
    {
        public string Linear, Area, Volume;
        public double LinearPerFoot = 1, AreaPerSquareFoot = 1, VolumePerCubicFoot = 1;
        public string LengthSource, AreaSource, VolumeSource;
        public readonly List<string> Notes = new List<string>();

        private static readonly Dictionary<string, KeyValuePair<string, double>> LengthUnits =
            new Dictionary<string, KeyValuePair<string, double>>(StringComparer.Ordinal)
            {
                ["millimeters"] = U("millimeters", 304.8),
                ["centimeters"] = U("centimeters", 30.48),
                ["meters"] = U("meters", 0.3048),
                ["metersCentimeters"] = U("meters", 0.3048),
                ["feet"] = U("feet", 1.0),
                ["feetFractionalInches"] = U("feet", 1.0),
                ["inches"] = U("inches", 12.0),
                ["fractionalInches"] = U("inches", 12.0)
            };

        private static readonly Dictionary<string, KeyValuePair<string, double>> AreaUnits =
            new Dictionary<string, KeyValuePair<string, double>>(StringComparer.Ordinal)
            {
                ["squareMillimeters"] = U("square millimeters", 92903.04),
                ["squareCentimeters"] = U("square centimeters", 929.0304),
                ["squareMeters"] = U("square meters", 0.09290304),
                ["squareFeet"] = U("square feet", 1.0),
                ["squareInches"] = U("square inches", 144.0)
            };

        private static readonly Dictionary<string, KeyValuePair<string, double>> VolumeUnits =
            new Dictionary<string, KeyValuePair<string, double>>(StringComparer.Ordinal)
            {
                ["cubicMillimeters"] = U("cubic millimeters", 28316846.592),
                ["cubicCentimeters"] = U("cubic centimeters", 28316.846592),
                ["cubicMeters"] = U("cubic meters", 0.028316846592),
                ["cubicFeet"] = U("cubic feet", 1.0),
                ["cubicInches"] = U("cubic inches", 1728.0),
                ["cubicYards"] = U("cubic yards", 1.0 / 27.0)
            };

        private static KeyValuePair<string, double> U(string name, double perFoot) => new KeyValuePair<string, double>(name, perFoot);

        /// <summary>"autodesk.unit.unit:squareMeters-1.0.1" -> "squareMeters".</summary>
        public static string UnitName(string typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId)) return null;
            string s = typeId.Trim();
            int colon = s.LastIndexOf(':');
            if (colon >= 0) s = s.Substring(colon + 1);
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// The document's display units as COBie pick-list names. A composite display unit
        /// (feet and fractional inches, meters and centimeters) is written in its decimal
        /// unit; a unit COBie has no name for is written in SI, and every such choice is a Note.
        /// </summary>
        public static CobieUnits Resolve(string lengthTypeId, string areaTypeId, string volumeTypeId)
        {
            var u = new CobieUnits { LengthSource = lengthTypeId, AreaSource = areaTypeId, VolumeSource = volumeTypeId };
            u.Linear = Pick(LengthUnits, lengthTypeId, "meters", 0.3048, "length", u.Notes, out u.LinearPerFoot);
            u.Area = Pick(AreaUnits, areaTypeId, "square meters", 0.09290304, "area", u.Notes, out u.AreaPerSquareFoot);
            u.Volume = Pick(VolumeUnits, volumeTypeId, "cubic meters", 0.028316846592, "volume", u.Notes, out u.VolumePerCubicFoot);
            return u;
        }

        private static string Pick(Dictionary<string, KeyValuePair<string, double>> table, string typeId, string fallback, double fallbackFactor,
                                   string what, List<string> notes, out double factor)
        {
            string name = UnitName(typeId);
            KeyValuePair<string, double> hit;
            if (name != null && table.TryGetValue(name, out hit))
            {
                factor = hit.Value;
                string direct = hit.Key.Replace(" ", "");
                if (!string.Equals(direct, name, StringComparison.OrdinalIgnoreCase))
                    notes.Add("the document shows " + what + " as '" + name + "'; COBie takes one decimal unit, so " + what + " values are written in " + hit.Key + ".");
                return hit.Key;
            }
            factor = fallbackFactor;
            notes.Add("the document's " + what + " unit '" + (typeId ?? "unreadable") + "' has no COBie pick-list name; " + what + " values are written in " + fallback + ".");
            return fallback;
        }

        public JObject ToJson() => new JObject
        {
            ["linear"] = Linear, ["area"] = Area, ["volume"] = Volume,
            ["document_length_unit"] = LengthSource, ["document_area_unit"] = AreaSource, ["document_volume_unit"] = VolumeSource,
            ["notes"] = new JArray(Notes)
        };
    }

    public sealed class CobieFinding
    {
        public string Kind, Sheet, Row, Column, Detail;
        public long? ElementId;
        internal int SheetIndex, RowIndex, ColumnIndex;

        public bool Blocking => CobieRules.IsBlocking(Kind);

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["kind"] = Kind, ["blocking"] = Blocking, ["sheet"] = Sheet, ["row"] = Row, ["column"] = Column, ["detail"] = Detail
            };
            if (ElementId.HasValue) o["element_id"] = ElementId.Value;
            return o;
        }
    }

    /// <summary>The workbook as planned: its sheets, every finding, and what the reply reports.</summary>
    public sealed class CobieWorkbookPlan
    {
        public readonly List<XlsxSheet> Sheets = new List<XlsxSheet>();
        public readonly List<CobieFinding> Findings = new List<CobieFinding>();
        public CobieUnits Units;
        public string CreatedOn;
        public int SystemsRead, SystemsWritten, SystemsWithoutScope, NameFallbacks;
        public readonly List<string> SystemsWithoutScopeSample = new List<string>();

        public int BlockingCount => Findings.Count(f => f.Blocking);
        public bool Ready => BlockingCount == 0;

        public XlsxSheet Sheet(string name) => Sheets.FirstOrDefault(s => s.Name == name);

        /// <summary>Every cell except the CreatedOn column: what the approval binds before the export time exists.</summary>
        public string ContentDigest() => XlsxWorkbookReader.CellsDigest(Sheets, CobieRules.CreatedOnColumn);

        /// <summary>Every cell, CreatedOn included: what the file on disk must hold.</summary>
        public string CellsDigest() => XlsxWorkbookReader.CellsDigest(Sheets);

        public JArray SheetsJson()
        {
            var a = new JArray();
            foreach (XlsxSheet s in Sheets)
                a.Add(new JObject
                {
                    ["name"] = s.Name, ["rows"] = Math.Max(0, s.Rows.Count - 1), ["columns"] = CobieRules.Columns[s.Name].Length,
                    ["required"] = new JArray(CobieRules.Required[s.Name])
                });
            return a;
        }

        public JObject FindingsJson(int max)
        {
            var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var bySheet = new JObject();
            foreach (CobieFinding f in Findings)
            {
                byKind[f.Kind] = (byKind.TryGetValue(f.Kind, out int k) ? k : 0) + 1;
                var sheet = (JObject)(bySheet[f.Sheet] ?? (bySheet[f.Sheet] = new JObject()));
                sheet[f.Kind] = (sheet.Value<int?>(f.Kind) ?? 0) + 1;
            }
            int listed = Math.Min(Math.Max(0, max), Findings.Count);
            return new JObject
            {
                ["total"] = Findings.Count, ["blocking"] = BlockingCount, ["advisory"] = Findings.Count - BlockingCount,
                ["by_kind"] = JObject.FromObject(byKind), ["by_sheet"] = bySheet,
                ["listed"] = listed, ["truncated"] = listed < Findings.Count,
                ["items"] = new JArray(Findings.Take(listed).Select(f => f.ToJson()))
            };
        }

        /// <summary>"required_field: 12" per blocking kind, for a one-line verdict.</summary>
        public JArray BlockingJson() => new JArray(Findings.Where(f => f.Blocking).GroupBy(f => f.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + ": " + g.Count().ToString(CultureInfo.InvariantCulture)));
    }

    public static class CobieRules
    {
        public const string FacilitySheet = "Facility", FloorSheet = "Floor", SpaceSheet = "Space", ZoneSheet = "Zone",
                            TypeSheet = "Type", ComponentSheet = "Component", SystemSheet = "System";
        public static readonly string[] SheetOrder = { FacilitySheet, FloorSheet, SpaceSheet, ZoneSheet, TypeSheet, ComponentSheet, SystemSheet };
        public const string CreatedOnColumn = "CreatedOn";

        public const string KindRequired = "required_field", KindDuplicate = "duplicate_name", KindReference = "broken_reference",
                            KindNameFallback = "name_fallback", KindSpansSpaces = "spans_spaces", KindNotInZone = "not_in_zone",
                            KindParameterMissing = "parameter_missing", KindEmptyScope = "empty_scope";

        public static bool IsBlocking(string kind) => kind == KindRequired || kind == KindDuplicate || kind == KindReference;

        /// <summary>The COBie 2.4 column set of each sheet, in COBie order.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Columns = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [FacilitySheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "ProjectName", "SiteName", "LinearUnits", "AreaUnits",
                "VolumeUnits", "CurrencyUnit", "AreaMeasurement", "ExternalSystem", "ExternalProjectObject", "ExternalProjectIdentifier",
                "ExternalSiteObject", "ExternalSiteIdentifier", "ExternalFacilityObject", "ExternalFacilityIdentifier", "Description",
                "ProjectDescription", "SiteDescription", "Phase" },
            [FloorSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "ExtSystem", "ExtObject", "ExtIdentifier", "Description",
                "Elevation", "Height" },
            [SpaceSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "FloorName", "Description", "ExtSystem", "ExtObject",
                "ExtIdentifier", "RoomTag", "UsableHeight", "GrossArea", "NetArea" },
            [ZoneSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "SpaceNames", "ExtSystem", "ExtObject", "ExtIdentifier",
                "Description" },
            [TypeSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "Description", "AssetType", "Manufacturer", "ModelNumber",
                "WarrantyGuarantorParts", "WarrantyDurationParts", "WarrantyGuarantorLabor", "WarrantyDurationLabor", "WarrantyDurationUnit",
                "ExtSystem", "ExtObject", "ExtIdentifier", "ReplacementCost", "ExpectedLife", "DurationUnit", "WarrantyDescription",
                "NominalLength", "NominalWidth", "NominalHeight", "ModelReference", "Shape", "Size", "Color", "Finish", "Grade", "Material",
                "Constituents", "Features", "AccessibilityPerformance", "CodePerformance", "SustainabilityPerformance" },
            [ComponentSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "TypeName", "Space", "Description", "ExtSystem", "ExtObject",
                "ExtIdentifier", "SerialNumber", "InstallationDate", "WarrantyStartDate", "TagNumber", "BarCode", "AssetIdentifier" },
            [SystemSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "ComponentNames", "ExtSystem", "ExtObject", "ExtIdentifier",
                "Description" }
        };

        /// <summary>
        /// The columns this tool REQUIRES per sheet: identity, classification and the
        /// references between sheets. Product data (manufacturer, model, warranty, cost,
        /// life, sizes) is not required here - map it with type_fields and judge it with the
        /// project's own COBie check.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [FacilitySheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "ProjectName", "SiteName", "LinearUnits", "AreaUnits",
                "VolumeUnits", "CurrencyUnit", "AreaMeasurement", "Phase" },
            [FloorSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category" },
            [SpaceSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "FloorName", "Description" },
            [ZoneSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "SpaceNames" },
            [TypeSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "Description" },
            [ComponentSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "TypeName", "Space", "Description" },
            [SystemSheet] = new[] { "Name", "CreatedBy", "CreatedOn", "Category", "ComponentNames" }
        };

        /// <summary>Type columns a type_fields map may fill (the rest are the row's identity, set by this tool).</summary>
        public static readonly string[] TypeFieldColumns = Columns[TypeSheet]
            .Where(c => c != "Name" && c != "CreatedBy" && c != "CreatedOn" && c != "Category" && !c.StartsWith("Ext", StringComparison.Ordinal))
            .ToArray();

        /// <summary>Component columns a component_fields map may fill.</summary>
        public static readonly string[] ComponentFieldColumns =
            { "Description", "SerialNumber", "InstallationDate", "WarrantyStartDate", "TagNumber", "BarCode", "AssetIdentifier" };

        private const int LengthDecimals = 4, AreaDecimals = 4;

        // ---- building ------------------------------------------------------------------

        private sealed class SheetBuilder
        {
            public readonly XlsxSheet Sheet;
            public readonly string[] ColumnNames;
            public readonly int Index;
            private readonly Dictionary<string, int> _columns = new Dictionary<string, int>(StringComparer.Ordinal);
            /// <summary>Row index (0 = header) -> the element the row came from.</summary>
            public readonly List<long?> ElementIds = new List<long?> { null };
            /// <summary>(row, column) -> why that cell is empty.</summary>
            public readonly Dictionary<long, string> Why = new Dictionary<long, string>();

            public SheetBuilder(string name, int index)
            {
                Sheet = new XlsxSheet(name);
                Index = index;
                ColumnNames = Columns[name];
                for (int i = 0; i < ColumnNames.Length; i++) _columns[ColumnNames[i]] = i;
                Sheet.Rows.Add(ColumnNames.Select(XlsxCell.Text).ToArray());
            }

            public int Column(string name) => _columns[name];
            public int DataRows => Sheet.Rows.Count - 1;

            public int NewRow(long? elementId)
            {
                var cells = new XlsxCell[ColumnNames.Length];
                for (int i = 0; i < cells.Length; i++) cells[i] = XlsxCell.Empty;
                Sheet.Rows.Add(cells);
                ElementIds.Add(elementId);
                return Sheet.Rows.Count - 1;
            }

            public void Text(int row, string column, string value, string whyEmpty = null)
            {
                int c = Column(column);
                if (string.IsNullOrWhiteSpace(value))
                {
                    Sheet.Rows[row][c] = XlsxCell.Empty;
                    if (whyEmpty != null) Why[Key(row, c)] = whyEmpty;
                }
                else Sheet.Rows[row][c] = XlsxCell.Text(value);
            }

            public void Number(int row, string column, double? value, int decimals, string whyEmpty)
            {
                int c = Column(column);
                if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                    Sheet.Rows[row][c] = XlsxCell.Number(Math.Round(value.Value, decimals, MidpointRounding.AwayFromZero));
                else
                {
                    Sheet.Rows[row][c] = XlsxCell.Empty;
                    if (whyEmpty != null) Why[Key(row, c)] = whyEmpty;
                }
            }

            public string Get(int row, string column)
            {
                XlsxCell cell = Sheet.Cell(row, Column(column));
                return cell.Kind == XlsxCellKind.Empty ? null : cell.Value;
            }

            public string WhyEmpty(int row, int column) => Why.TryGetValue(Key(row, column), out string why) ? why : null;

            private static long Key(int row, int column) => (long)row * 100000 + column;
        }

        /// <summary>
        /// The workbook the facts and the mapping make, and every finding over it. Pure:
        /// the same inputs give the same sheets, in the same order, cell for cell.
        /// </summary>
        public static CobieWorkbookPlan Build(CobieMapping m, CobieFacts f, DateTime createdOnUtc)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (f == null) throw new ArgumentNullException(nameof(f));
            var plan = new CobieWorkbookPlan { CreatedOn = CobieMapping.CreatedOnText(createdOnUtc) };
            CobieUnits u = CobieUnits.Resolve(f.LengthUnitTypeId, f.AreaUnitTypeId, f.VolumeUnitTypeId);
            plan.Units = u;
            var sheets = new Dictionary<string, SheetBuilder>(StringComparer.Ordinal);
            for (int i = 0; i < SheetOrder.Length; i++) sheets[SheetOrder[i]] = new SheetBuilder(SheetOrder[i], i);
            string by = m.CreatedBy, on = plan.CreatedOn;
            string spaceWord = m.UsesSpaces ? "space" : "room";

            // ---- Facility: one row --------------------------------------------------------
            SheetBuilder facility = sheets[FacilitySheet];
            int fr = facility.NewRow(null);
            facility.Text(fr, "Name", m.Facility.Name, "cobie.facility.name was not given");
            Stamp(facility, fr, by, on);
            facility.Text(fr, "Category", m.Facility.Category, "cobie.facility.category was not given");
            facility.Text(fr, "ProjectName", m.Facility.ProjectName, NotGiven("cobie.facility.project_name", m, "project.name"));
            facility.Text(fr, "SiteName", m.Facility.SiteName, NotGiven("cobie.facility.site_name", m, "project.location"));
            facility.Text(fr, "LinearUnits", u.Linear);
            facility.Text(fr, "AreaUnits", u.Area);
            facility.Text(fr, "VolumeUnits", u.Volume);
            facility.Text(fr, "CurrencyUnit", m.Facility.CurrencyUnit, "cobie.facility.currency_unit was not given");
            facility.Text(fr, "AreaMeasurement", m.Facility.AreaMeasurement ?? AreaMeasurementOf(f.AreaBoundary, spaceWord),
                          "cobie.facility.area_measurement was not given and the document's area boundary could not be read");
            facility.Text(fr, "ExternalSystem", f.AuthoringSystem);
            facility.Text(fr, "Description", m.Facility.Description);
            facility.Text(fr, "ProjectDescription", m.Facility.ProjectDescription);
            facility.Text(fr, "SiteDescription", m.Facility.SiteDescription);
            facility.Text(fr, "Phase", m.Facility.Phase, NotGiven("cobie.facility.phase", m, "appointment.stage"));

            // ---- Floor: the building stories, lowest first --------------------------------
            SheetBuilder floor = sheets[FloorSheet];
            foreach (CobieLevelFact level in f.Levels.Where(l => l.IsBuildingStory)
                         .OrderBy(l => l.ElevationFeet ?? 0).ThenBy(l => l.Name ?? "", StringComparer.Ordinal).ThenBy(l => l.Id))
            {
                int r = floor.NewRow(level.Id);
                floor.Text(r, "Name", level.Name, "the level's name could not be read");
                Stamp(floor, r, by, on);
                floor.Text(r, "Category", Mapped(level.Category, m.CategoryParameter, "category_parameter", "level", out string why), why);
                External(floor, r, f.AuthoringSystem, "Level", level.UniqueId);
                floor.Number(r, "Elevation", level.ElevationFeet * u.LinearPerFoot, LengthDecimals, "Level.Elevation could not be read");
            }

            // ---- Space: rooms or MEP spaces of the phase ---------------------------------
            SheetBuilder space = sheets[SpaceSheet];
            foreach (CobieSpaceFact s in f.Spaces.OrderBy(s => s.Number ?? "", StringComparer.Ordinal).ThenBy(s => s.Id))
            {
                int r = space.NewRow(s.Id);
                space.Text(r, "Name", s.Number, "the " + spaceWord + " has no Number");
                Stamp(space, r, by, on);
                space.Text(r, "Category", Mapped(s.Category, m.CategoryParameter, "category_parameter", spaceWord, out string why), why);
                space.Text(r, "FloorName", s.LevelName, "the " + spaceWord + "'s level could not be read");
                space.Text(r, "Description", s.Name, "the " + spaceWord + " has no Name");
                External(space, r, f.AuthoringSystem, s.RevitClass, s.UniqueId);
                space.Text(r, "RoomTag", s.Number);
                space.Number(r, "UsableHeight", s.UnboundedHeightFeet * u.LinearPerFoot, LengthDecimals, "the unbounded height could not be read");
                space.Number(r, "NetArea", s.AreaSquareFeet * u.AreaPerSquareFoot, AreaDecimals, "the area could not be read");
            }

            // ---- Zone: one row per zone per space ----------------------------------------
            SheetBuilder zone = sheets[ZoneSheet];
            var notInZone = new List<KeyValuePair<CobieSpaceFact, string>>();
            if (m.ZoneParameter != null)
            {
                var members = new List<KeyValuePair<string, CobieSpaceFact>>();
                foreach (CobieSpaceFact s in f.Spaces)
                {
                    string value = s.Zone?.Value;
                    if (value == null)
                        notInZone.Add(new KeyValuePair<CobieSpaceFact, string>(s, s.Zone == null || !s.Zone.Exists
                            ? "zone_parameter '" + m.ZoneParameter + "' is not on this " + spaceWord
                            : s.Zone.Problem != null ? "zone_parameter '" + m.ZoneParameter + "' could not be read: " + s.Zone.Problem
                            : "zone_parameter '" + m.ZoneParameter + "' is empty on this " + spaceWord));
                    else members.Add(new KeyValuePair<string, CobieSpaceFact>(value, s));
                }
                foreach (KeyValuePair<string, CobieSpaceFact> kv in members
                             .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Value.Number ?? "", StringComparer.Ordinal).ThenBy(x => x.Value.Id))
                {
                    int r = zone.NewRow(kv.Value.Id);
                    zone.Text(r, "Name", kv.Key);
                    Stamp(zone, r, by, on);
                    zone.Text(r, "Category", m.ZoneCategory, "cobie.zone_category was not given");
                    zone.Text(r, "SpaceNames", kv.Value.Number, "the " + spaceWord + " has no Number");
                }
            }

            // ---- Type: the types of the components in scope -------------------------------
            var typeById = f.Types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            var typeName = new Dictionary<long, string>();
            foreach (CobieTypeFact t in typeById.Values) typeName[t.Id] = TypeRowName(t);
            var usedTypes = new HashSet<long>(f.Components.Select(c => c.TypeId));
            SheetBuilder type = sheets[TypeSheet];
            foreach (CobieTypeFact t in typeById.Values.Where(t => usedTypes.Contains(t.Id))
                         .OrderBy(t => typeName[t.Id], StringComparer.Ordinal).ThenBy(t => t.Id))
            {
                int r = type.NewRow(t.Id);
                type.Text(r, "Name", typeName[t.Id], "the type's name could not be read");
                Stamp(type, r, by, on);
                type.Text(r, "Category", Mapped(t.Category, m.CategoryParameter, "category_parameter", "type", out string why), why);
                string descriptionParameter = m.TypeField("Description");
                type.Text(r, "Description", descriptionParameter != null
                        ? Mapped(Field(t.Fields, "Description"), descriptionParameter, "type_fields.Description", "type", out why)
                        : Builtin(t.Description, "the type's Description parameter", out why), why);
                foreach (KeyValuePair<string, string> map in m.TypeFields)
                {
                    if (map.Key == "Description") continue;
                    type.Text(r, map.Key, Mapped(Field(t.Fields, map.Key), map.Value, "type_fields." + map.Key, "type", out string w), w);
                }
                External(type, r, f.AuthoringSystem, t.RevitClass, t.UniqueId);
            }

            // ---- Component: the instances in scope ----------------------------------------
            var componentName = new Dictionary<long, string>();
            var fallbackWhy = new Dictionary<long, string>();
            var valueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (m.ComponentNameParameter != null)
                foreach (CobieComponentFact c in f.Components)
                {
                    string v = c.NameValue?.Value?.Trim();
                    if (v != null) valueCounts[v] = (valueCounts.TryGetValue(v, out int n) ? n : 0) + 1;
                }
            foreach (CobieComponentFact c in f.Components)
            {
                string fallback = (typeName.TryGetValue(c.TypeId, out string tn) ? tn : c.RevitClass ?? "Component") + "-" + c.Id.ToString(CultureInfo.InvariantCulture);
                if (m.ComponentNameParameter == null) { componentName[c.Id] = fallback; continue; }
                string v = c.NameValue?.Value?.Trim();
                if (v != null && valueCounts[v] == 1) { componentName[c.Id] = c.NameValue.Value; continue; }
                componentName[c.Id] = fallback;
                fallbackWhy[c.Id] = v != null ? "'" + v + "' is shared by " + valueCounts[v] + " components"
                    : c.NameValue == null || !c.NameValue.Exists ? "the parameter is on neither the component nor its type"
                    : c.NameValue.Problem != null ? "the parameter could not be read: " + c.NameValue.Problem
                    : "the parameter is empty";
            }
            plan.NameFallbacks = fallbackWhy.Count;
            SheetBuilder component = sheets[ComponentSheet];
            var componentRow = new Dictionary<long, int>();
            foreach (CobieComponentFact c in f.Components.OrderBy(c => componentName[c.Id], StringComparer.Ordinal).ThenBy(c => c.Id))
            {
                int r = component.NewRow(c.Id);
                componentRow[c.Id] = r;
                component.Text(r, "Name", componentName[c.Id]);
                Stamp(component, r, by, on);
                component.Text(r, "TypeName", typeName.TryGetValue(c.TypeId, out string tn) ? tn : null, "the component's type could not be read");
                List<string> numbers = c.Spaces.Select(s => s.Number).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
                component.Text(r, "Space", string.Join(",", numbers),
                    c.Spaces.Count > 0 && numbers.Count == 0 ? "the " + spaceWord + " it sits in has no Number" : c.SpaceProblem ?? "no " + spaceWord + " was found for it");
                string descriptionParameter = m.ComponentField("Description");
                string why;
                component.Text(r, "Description", descriptionParameter != null
                        ? Mapped(Field(c.Fields, "Description"), descriptionParameter, "component_fields.Description", "component or its type", out why)
                        : typeById.TryGetValue(c.TypeId, out CobieTypeFact ct) ? Builtin(ct.Description, "its type's Description parameter", out why)
                        : Unread("its type could not be read", out why), why);
                foreach (KeyValuePair<string, string> map in m.ComponentFields)
                {
                    if (map.Key == "Description") continue;
                    component.Text(r, map.Key, Mapped(Field(c.Fields, map.Key), map.Value, "component_fields." + map.Key, "component or its type", out string w), w);
                }
                External(component, r, f.AuthoringSystem, c.RevitClass, c.UniqueId);
            }

            // ---- System: one row per system per in-scope component -------------------------
            SheetBuilder system = sheets[SystemSheet];
            plan.SystemsRead = f.Systems.Count;
            foreach (CobieSystemFact s in f.Systems.OrderBy(s => s.Name ?? "", StringComparer.Ordinal).ThenBy(s => s.Id))
            {
                List<string> names = s.MemberIds.Where(componentName.ContainsKey).Select(id => componentName[id])
                    .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (names.Count == 0)
                {
                    plan.SystemsWithoutScope++;
                    if (plan.SystemsWithoutScopeSample.Count < 20) plan.SystemsWithoutScopeSample.Add(s.Name ?? "(unnamed " + s.RevitClass + " " + s.Id + ")");
                    continue;
                }
                plan.SystemsWritten++;
                foreach (string name in names)
                {
                    int r = system.NewRow(s.Id);
                    system.Text(r, "Name", s.Name, "the system's name could not be read");
                    Stamp(system, r, by, on);
                    system.Text(r, "Category", Mapped(s.Category, m.CategoryParameter, "category_parameter", "system or its type", out string why), why);
                    system.Text(r, "ComponentNames", name);
                    External(system, r, f.AuthoringSystem, s.RevitClass, s.UniqueId);
                }
            }

            foreach (string name in SheetOrder) plan.Sheets.Add(sheets[name].Sheet);

            // ---- judging -------------------------------------------------------------------
            var findings = plan.Findings;
            foreach (string name in SheetOrder) RequiredFindings(sheets[name], findings);
            UniqueNames(sheets[FloorSheet], findings);
            UniqueNames(sheets[SpaceSheet], findings);
            UniqueNames(sheets[TypeSheet], findings);
            UniqueNames(sheets[ComponentSheet], findings);
            UniqueKeys(sheets[ZoneSheet], "SpaceNames", findings);
            UniqueKeys(sheets[SystemSheet], "ComponentNames", findings);
            SystemIdentity(sheets[SystemSheet], findings);
            References(sheets, f, spaceWord, findings);

            if (m.ComponentNameParameter != null)
                foreach (CobieComponentFact c in f.Components)
                    if (fallbackWhy.TryGetValue(c.Id, out string why))
                        findings.Add(Finding(KindNameFallback, component, componentRow[c.Id], "Name",
                            "component_name_parameter '" + m.ComponentNameParameter + "': " + why + "; the row is named '" + componentName[c.Id] + "' (<type>-<element id>) instead"));
            foreach (CobieComponentFact c in f.Components)
                if (!c.IsOpening && c.Spaces.Count > 1)
                    findings.Add(Finding(KindSpansSpaces, component, componentRow[c.Id], "Space",
                        "it sits in " + c.Spaces.Count + " " + spaceWord + "s (" + string.Join(", ", c.Spaces.Select(s => s.Number)) + "); all are written"));
            foreach (KeyValuePair<CobieSpaceFact, string> kv in notInZone)
            {
                int row = RowOf(space, kv.Key.Id);
                findings.Add(Finding(KindNotInZone, space, row, null, kv.Value + "; it is in no Zone row"));
            }
            ParameterMissing(findings, sheets[FloorSheet], m.CategoryParameter, "category_parameter", f.Levels.Where(l => l.IsBuildingStory).Select(l => l.Category));
            ParameterMissing(findings, sheets[SpaceSheet], m.CategoryParameter, "category_parameter", f.Spaces.Select(s => s.Category));
            ParameterMissing(findings, sheets[SpaceSheet], m.ZoneParameter, "zone_parameter", f.Spaces.Select(s => s.Zone));
            ParameterMissing(findings, sheets[TypeSheet], m.CategoryParameter, "category_parameter",
                             typeById.Values.Where(t => usedTypes.Contains(t.Id)).Select(t => t.Category));
            foreach (KeyValuePair<string, string> map in m.TypeFields)
                ParameterMissing(findings, sheets[TypeSheet], map.Value, "type_fields." + map.Key,
                                 typeById.Values.Where(t => usedTypes.Contains(t.Id)).Select(t => Field(t.Fields, map.Key)));
            ParameterMissing(findings, sheets[ComponentSheet], m.ComponentNameParameter, "component_name_parameter", f.Components.Select(c => c.NameValue));
            foreach (KeyValuePair<string, string> map in m.ComponentFields)
                ParameterMissing(findings, sheets[ComponentSheet], map.Value, "component_fields." + map.Key, f.Components.Select(c => Field(c.Fields, map.Key)));
            if (plan.SystemsWritten > 0)
                ParameterMissing(findings, sheets[SystemSheet], m.CategoryParameter, "category_parameter",
                                 f.Systems.Where(s => s.MemberIds.Any(componentName.ContainsKey)).Select(s => s.Category));
            if (f.Components.Count == 0)
                findings.Add(new CobieFinding
                {
                    Kind = KindEmptyScope, Sheet = ComponentSheet, Row = null, Column = null, SheetIndex = sheets[ComponentSheet].Index,
                    Detail = "no instance of " + string.Join(", ", m.ComponentCategories) + " is in scope (phase '" + m.Phase +
                             "'): the Type and Component sheets hold only their headers"
                });

            findings.Sort((a, b) =>
            {
                int c = a.SheetIndex.CompareTo(b.SheetIndex);
                if (c == 0) c = a.RowIndex.CompareTo(b.RowIndex);
                if (c == 0) c = a.ColumnIndex.CompareTo(b.ColumnIndex);
                if (c == 0) c = string.CompareOrdinal(a.Kind, b.Kind);
                return c;
            });
            return plan;
        }

        // ---- cell helpers -----------------------------------------------------------------

        private static void Stamp(SheetBuilder s, int row, string by, string on)
        {
            s.Text(row, "CreatedBy", by, "cobie.created_by was not given");
            s.Text(row, CreatedOnColumn, on);
        }

        private static void External(SheetBuilder s, int row, string system, string revitClass, string uniqueId)
        {
            s.Text(row, "ExtSystem", system);
            s.Text(row, "ExtObject", revitClass);
            s.Text(row, "ExtIdentifier", uniqueId);
        }

        private static string NotGiven(string field, CobieMapping m, string contextField) =>
            field + " was not given" + (m.ProjectContextPath != null ? " and the project context has no " + contextField : "");

        private static CobieValue Field(Dictionary<string, CobieValue> fields, string column) =>
            fields != null && fields.TryGetValue(column, out CobieValue v) ? v : null;

        /// <summary>A mapped parameter's cell value, or null with why it is empty.</summary>
        private static string Mapped(CobieValue v, string parameter, string mapping, string where, out string why)
        {
            why = null;
            if (parameter == null) { why = mapping + " was not given"; return null; }
            if (v == null || !v.Exists) { why = mapping + " '" + parameter + "' is not a parameter of the " + where; return null; }
            if (v.Problem != null) { why = mapping + " '" + parameter + "' could not be read: " + v.Problem; return null; }
            if (v.Value == null) { why = mapping + " '" + parameter + "' is empty on the " + where; return null; }
            return v.Value;
        }

        private static string Builtin(CobieValue v, string what, out string why)
        {
            why = null;
            if (v == null || !v.Exists) { why = what + " does not exist"; return null; }
            if (v.Problem != null) { why = what + " could not be read: " + v.Problem; return null; }
            if (v.Value == null) { why = what + " is empty"; return null; }
            return v.Value;
        }

        private static string Unread(string reason, out string why) { why = reason; return null; }

        /// <summary>"Family: Type" for a family type; the type name alone when the family name is unknown.</summary>
        public static string TypeRowName(CobieTypeFact t)
        {
            string family = string.IsNullOrWhiteSpace(t.FamilyName) ? null : t.FamilyName;
            string name = string.IsNullOrWhiteSpace(t.TypeName) ? null : t.TypeName;
            if (family != null && name != null) return family + ": " + name;
            return name ?? family;
        }

        private static string AreaMeasurementOf(string boundary, string spaceWord)
        {
            if (string.IsNullOrWhiteSpace(boundary)) return null;
            string where = boundary == "Finish" ? "wall finish" : boundary == "Center" ? "wall center"
                         : boundary == "CoreBoundary" ? "wall core boundary" : boundary == "CoreCenter" ? "wall core center" : boundary;
            return "Revit " + spaceWord + " area, computed at the " + where;
        }

        // ---- findings ---------------------------------------------------------------------

        private static CobieFinding Finding(string kind, SheetBuilder s, int row, string column, string detail)
        {
            int c = column == null ? -1 : s.Column(column);
            return new CobieFinding
            {
                Kind = kind, Sheet = s.Sheet.Name, Row = RowLabel(s, row), Column = column, Detail = detail,
                ElementId = row > 0 && row < s.ElementIds.Count ? s.ElementIds[row] : null,
                SheetIndex = s.Index, RowIndex = row, ColumnIndex = c
            };
        }

        private static string RowLabel(SheetBuilder s, int row)
        {
            if (row <= 0) return null;
            string name = s.Get(row, "Name");
            if (s.Sheet.Name == ZoneSheet) return (name ?? "(no name)") + " / " + (s.Get(row, "SpaceNames") ?? "(no space)");
            if (s.Sheet.Name == SystemSheet) return (name ?? "(no name)") + " / " + (s.Get(row, "ComponentNames") ?? "(no component)");
            return name ?? "(row " + (row + 1).ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static int RowOf(SheetBuilder s, long elementId)
        {
            for (int r = 1; r < s.ElementIds.Count; r++) if (s.ElementIds[r] == elementId) return r;
            return 0;
        }

        private static void RequiredFindings(SheetBuilder s, List<CobieFinding> findings)
        {
            string[] required = Required[s.Sheet.Name];
            for (int r = 1; r < s.Sheet.Rows.Count; r++)
                foreach (string column in required)
                {
                    int c = s.Column(column);
                    if (s.Sheet.Rows[r][c].Kind != XlsxCellKind.Empty) continue;
                    findings.Add(Finding(KindRequired, s, r, column, s.WhyEmpty(r, c) ?? "empty"));
                }
        }

        private static string Normal(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

        private static void UniqueNames(SheetBuilder s, List<CobieFinding> findings)
        {
            foreach (IGrouping<string, int> group in Enumerable.Range(1, s.DataRows)
                         .Where(r => Normal(s.Get(r, "Name")) != null)
                         .GroupBy(r => Normal(s.Get(r, "Name")), StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                int first = group.First();
                findings.Add(Finding(KindDuplicate, s, first, "Name",
                    group.Count() + " " + s.Sheet.Name + " rows are named '" + s.Get(first, "Name") + "' (names compare without case and outer spaces)" +
                    ElementList(s, group)));
            }
        }

        private static void UniqueKeys(SheetBuilder s, string listColumn, List<CobieFinding> findings)
        {
            foreach (IGrouping<string, int> group in Enumerable.Range(1, s.DataRows)
                         .Where(r => Normal(s.Get(r, "Name")) != null)
                         .GroupBy(r => Normal(s.Get(r, "Name")) + "\u001f" + Normal(s.Get(r, "Category")) + "\u001f" + Normal(s.Get(r, listColumn)), StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                int first = group.First();
                findings.Add(Finding(KindDuplicate, s, first, listColumn,
                    group.Count() + " " + s.Sheet.Name + " rows share the key Name + Category + " + listColumn + " ('" + s.Get(first, "Name") + "', '" +
                    s.Get(first, "Category") + "', '" + s.Get(first, listColumn) + "')" + ElementList(s, group)));
            }
        }

        /// <summary>Two DIFFERENT Revit systems under one Name + Category read as one COBie system.</summary>
        private static void SystemIdentity(SheetBuilder s, List<CobieFinding> findings)
        {
            foreach (IGrouping<string, int> group in Enumerable.Range(1, s.DataRows)
                         .Where(r => Normal(s.Get(r, "Name")) != null)
                         .GroupBy(r => Normal(s.Get(r, "Name")) + "\u001f" + Normal(s.Get(r, "Category")), StringComparer.Ordinal))
            {
                List<long> ids = group.Select(r => s.ElementIds[r]).Where(id => id.HasValue).Select(id => id.Value).Distinct().OrderBy(id => id).ToList();
                if (ids.Count < 2) continue;
                int first = group.First();
                findings.Add(Finding(KindDuplicate, s, first, "Name",
                    ids.Count + " Revit systems are named '" + s.Get(first, "Name") + "' with the same Category (element ids " +
                    string.Join(", ", ids) + "): a COBie reader merges them into one system"));
            }
        }

        private static string ElementList(SheetBuilder s, IEnumerable<int> rows)
        {
            List<long> ids = rows.Select(r => s.ElementIds[r]).Where(id => id.HasValue).Select(id => id.Value).Distinct().ToList();
            return ids.Count == 0 ? "" : " (element ids " + string.Join(", ", ids) + ")";
        }

        private static void References(Dictionary<string, SheetBuilder> sheets, CobieFacts f, string spaceWord, List<CobieFinding> findings)
        {
            SheetBuilder floor = sheets[FloorSheet], space = sheets[SpaceSheet], component = sheets[ComponentSheet];
            var floors = new HashSet<string>(Enumerable.Range(1, floor.DataRows).Select(r => Normal(floor.Get(r, "Name"))).Where(n => n != null), StringComparer.Ordinal);
            var notStories = new HashSet<string>(f.Levels.Where(l => !l.IsBuildingStory).Select(l => Normal(l.Name)).Where(n => n != null), StringComparer.Ordinal);
            for (int r = 1; r <= space.DataRows; r++)
            {
                string floorName = space.Get(r, "FloorName");
                if (Normal(floorName) == null || floors.Contains(Normal(floorName))) continue;
                findings.Add(Finding(KindReference, space, r, "FloorName", notStories.Contains(Normal(floorName))
                    ? "level '" + floorName + "' is not marked Building Story, so it has no Floor row"
                    : "no Floor row is named '" + floorName + "'"));
            }
            var spaces = new HashSet<string>(Enumerable.Range(1, space.DataRows).Select(r => Normal(space.Get(r, "Name"))).Where(n => n != null), StringComparer.Ordinal);
            for (int r = 1; r <= component.DataRows; r++)
            {
                string value = component.Get(r, "Space");
                if (value == null) continue;
                List<string> missing = value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0 && !spaces.Contains(Normal(p))).ToList();
                if (missing.Count > 0)
                    findings.Add(Finding(KindReference, component, r, "Space",
                        "no Space row is named " + string.Join(", ", missing.Select(x => "'" + x + "'")) + " (the " + spaceWord +
                        " it sits in is not in scope: another phase, not placed or not enclosed)"));
            }
        }

        private static void ParameterMissing(List<CobieFinding> findings, SheetBuilder s, string parameter, string mapping, IEnumerable<CobieValue> values)
        {
            if (parameter == null) return;
            List<CobieValue> list = values.ToList();
            if (list.Count == 0 || list.Any(v => v != null && v.Exists)) return;
            findings.Add(new CobieFinding
            {
                Kind = KindParameterMissing, Sheet = s.Sheet.Name, Row = null, Column = null, SheetIndex = s.Index,
                Detail = mapping + " '" + parameter + "' is a parameter of none of the " + list.Count + " element(s) behind the " + s.Sheet.Name +
                         " sheet - check the name (Revit parameter names are exact)"
            });
        }
    }
}
