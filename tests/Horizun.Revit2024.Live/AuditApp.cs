using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit2024.Live
{
    // Loaded by a temporary manifest only. Runs only in an explicitly launched test
    // process, never attaches to a user's existing Revit session or document.
    public sealed partial class AuditApp : IExternalApplication
    {
        private UIApplication ui;
        private Document doc;
        private string root;
        private readonly JArray cases = new JArray();
        private readonly List<Document> owned = new List<Document>();
        private JObject report;
        private bool started;
        private bool awaitingMcp;
        private ExternalEvent finishEvent;
        private sealed class FinishHandler : IExternalEventHandler
        {
            internal AuditApp Owner;
            public string GetName() => "Finish isolated regression suite";
            public void Execute(UIApplication app) => Owner.Finish();
        }
        private ExternalEvent startEvent;
        private sealed class StartHandler : IExternalEventHandler
        {
            internal AuditApp Owner;
            public string GetName() => "Isolated Revit 2024 regression suite";
            public void Execute(UIApplication app) => Owner.Run(app);
        }
        private Level level;
        private Wall wall;
        private Floor floor;
        private ViewPlan plan;
        private long materialId, topoId, memberId, panelId;

        public Result OnStartup(UIControlledApplication app)
        {
            root = Environment.GetEnvironmentVariable("HORIZUN_AUDIT_RUN_ROOT");
            if (!string.IsNullOrWhiteSpace(root))
            {
                startEvent = ExternalEvent.Create(new StartHandler { Owner = this });
                finishEvent = ExternalEvent.Create(new FinishHandler { Owner = this });
                app.Idling += Start;
            }
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= Start; startEvent?.Dispose(); finishEvent?.Dispose(); return Result.Succeeded; }
        private void Start(object sender, IdlingEventArgs e)
        {
            if (awaitingMcp && File.Exists(Path.Combine(root, "mcp-complete"))) { awaitingMcp = false; finishEvent.Raise(); }
            if (started) return;
            started = true;
            startEvent.Raise();
        }
        private void Run(UIApplication app)
        {
            ui = app;
            Directory.CreateDirectory(root);
            report = new JObject { ["started_utc"] = DateTime.UtcNow.ToString("o"), ["cases"] = cases,
                ["health"] = Envelope(new HealthCommand().Execute(ui, "{}")), ["state"] = "running" };
            Flush();
            try
            {
                Require(ui.Application.VersionNumber == "2024" && ui.Application.VersionBuild == "24.0.4.427", "This suite is pinned to installed Revit 2024 RTM 24.0.4.427.");
                Require(ui.Application.Documents.Cast<Document>().Count() == 0, "Refusing a session with existing documents.");
                CreateFixture(ui.Application.Language.ToString().StartsWith("Russian") ? "Default_M_RUS.rte" : "Default_M_ENU.rte", "metric");
                Suite();
                ReliabilitySuite();
                Case("imperial_template_material_units", () =>
                {
                    CreateFixture("Default_I_ENU.rte", "imperial");
                    MaterialUnits();
                });
                Case("electrical_fixture", ElectricalFixture);
                Case("circuit_set_path", CircuitPath);
                Case("panel_move_slot", MoveSlot);
                report["capabilities"] = Envelope(new HealthCommand().Execute(ui, "{\"include_capabilities\":true}"));
                report["state"] = cases.Any(c => (string)c["state"] == "failed") ? "failed" : "passed";
            }
            catch (Exception ex) { report["state"] = "failed"; report["fatal"] = ex.ToString(); }
            finally
            {
                // Save only paths created by this run; no external or user-owned models.
                foreach (Document d in owned.Where(d => d.IsValidObject))
                    try { if (d.IsModified) d.Save(); } catch (Exception ex) { report["cleanup_error"] = ex.Message; }
                report["finished_utc"] = DateTime.UtcNow.ToString("o");
                Flush();
                bool hasUnowned = ui.Application.Documents.Cast<Document>().Any(d => !owned.Any(o => o.IsValidObject && o.Equals(d)));
                if (hasUnowned) { report["cleanup_blocked"] = "A document not owned by this test appeared; Revit is left open."; Flush(); }
                if (!hasUnowned && Environment.GetEnvironmentVariable("HORIZUN_AUDIT_KEEP_OPEN") != "1")
                    ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
                else if (!hasUnowned) awaitingMcp = true;
            }
        }
        private void Finish()
        {
            if (ui.Application.Documents.Cast<Document>().Any(d => !owned.Any(o => o.IsValidObject && o.Equals(d)))) return;
            foreach (Document d in owned.Where(d => d.IsValidObject)) if (d.IsModified) d.Save();
            ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
        private void CreateFixture(string template, string name)
        {
            string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT 2024", "Templates", template);
            Require(File.Exists(source), "Missing fixture template: " + source);
            string path = Path.Combine(root, name + ".rvt");
            Require(!File.Exists(path), "Run directory must be fresh.");
            Document background = ui.Application.NewProjectDocument(source);
            background.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
            background.Close(false);
            doc = ui.OpenAndActivateDocument(path).Document;
            owned.Add(doc);
            Setup(() =>
            {
                level = Level.Create(doc, 0); level.Name = "Audit Level";
                var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(x => x.ViewFamily == ViewFamily.FloorPlan);
                plan = ViewPlan.Create(doc, viewType.Id, level.Id);
                var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(x => x.Kind == WallKind.Basic);
                wall = Wall.Create(doc, Line.CreateBound(XYZ.Zero, new XYZ(20, 0, 0)), wallType.Id, level.Id, 10, 0, false, false);
                var ft = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First();
                floor = Floor.Create(doc, new List<CurveLoop> { Rectangle(0, 20, 20, 40) }, ft.Id, level.Id);
            });
        }
        private void Suite()
        {
            Case("structural_asset_api_probe", AssetProbe);
            Case("element_id_64_bit", () => Require(Rid.Value(Rid.Make(2147483648L)) == 2147483648L, "64-bit ids must not truncate."));
            Case("read_document_full_path_and_wrong_path", () =>
            {
                Require(DocumentGate.ReadGuard(doc, new JObject { ["target_document"] = doc.PathName.Replace('\\', '/') }, "audit") == null, "Correct normalized path was refused.");
                Require(DocumentGate.ReadGuard(doc, new JObject { ["target_document"] = Path.Combine(root, "other", Path.GetFileName(doc.PathName)) }, "audit") != null, "Wrong path with same filename was accepted.");
            });
            Case("materials_create_physical_thermal", MaterialUnits);
            Case("materials_physical_only_update", () => Apply(new ManageMaterialsCommand(), new JObject { ["actions"] = new JArray(new JObject
            { ["key"] = "density", ["operation"] = "update", ["material_id"] = materialId, ["structural"] = new JObject { ["density"] = Quantity(2500, "KilogramsPerCubicMeter") } }) }));
            Case("materials_wrong_dimension_rolls_back", () =>
            {
                var before = ((Material)doc.GetElement(Rid.Make(materialId))).Color;
                CommandResult bad = Call(new ManageMaterialsCommand(), new JObject { ["actions"] = new JArray(new JObject
                { ["key"] = "bad", ["operation"] = "update", ["material_id"] = materialId, ["color"] = "#FF0000", ["structural"] = new JObject { ["density"] = Quantity(5, "Meters") } }) });
                Require(!bad.Success, "Incompatible density unit accepted.");
                Require(((Material)doc.GetElement(Rid.Make(materialId))).Color.Red == before.Red, "Failed batch changed graphics.");
            });
            Case("materials_stale_smoothness", () =>
            {
                var cmd = new ManageMaterialsCommand();
                var request = new JObject { ["actions"] = new JArray(new JObject { ["key"] = "stale", ["operation"] = "update", ["material_id"] = materialId, ["smoothness"] = 33 }) };
                JObject preview = Success(Call(cmd, request));
                Setup(() => ((Material)doc.GetElement(Rid.Make(materialId))).Smoothness = 34);
                request["dry_run"] = false; request["confirmation_token"] = preview["confirmation_token"];
                Require(!Call(cmd, request).Success, "Changed smoothness did not invalidate approval.");
            });
            Case("materials_shared_asset_duplicate", SharedMaterial);
            Case("materials_appearance_edit", Appearance);
            Case("global_parameter_builtin_association", Globals);
            Case("duplicate_parameter_name_and_explicit_id", DuplicateParameterIdentity);
            Case("cad_provenance_postcommit_and_rollback", Provenance);
            Case("ifc_provenance_postcommit_and_rollback", () =>
            {
                var p = new IfcProvenance { GlobalId = "audit-ifc", IfcClass = "IfcWall", SourcePath = "", WrittenUtc = DateTime.UtcNow.ToString("o") };
                using (var t = new Transaction(doc, "audit IFC rollback")) { t.Start(); string why; Require(IfcProvenanceStore.Write(wall, p, out why), why); t.RollBack(); }
                Require(!IfcProvenanceStore.Matches(wall, p), "Rolled-back IFC metadata reported as persisted.");
                Setup(() => { string why; Require(IfcProvenanceStore.Write(wall, p, out why), why); });
                Require(IfcProvenanceStore.Matches(wall, p), "Committed IFC record differs, including blank/null fields.");
            });
            Case("room_separator_shared_datum", Boundaries);
            Case("site_boundary_with_hole", () =>
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstOrDefault();
                if (type == null) throw new MissingDependency("No Toposolid type in template.");
                var result = Advanced("site_create", new JObject { ["type_id"] = Rid.Value(type.Id), ["level_id"] = Rid.Value(level.Id),
                    ["loops"] = new JArray(Polygon(40, 0, 80, 40), Polygon(50, 10, 60, 20)) });
                topoId = result["result"].Value<long>("element_id");
            });
            Case("site_subdivision", () => { Require(topoId > 0, "Site creation prerequisite failed."); Advanced("site_subdivide", new JObject { ["host_id"] = topoId, ["loops"] = new JArray { Polygon(65, 25, 75, 35) } }); });
            Case("toposolid_slab_shape_shared_datum", () => Apply(new SlabShapeCommand(), new JObject { ["operation"] = "add_point", ["element_id"] = topoId, ["units"] = "feet", ["points"] = new JArray { new JArray(70, 10, 1) } }));
            Case("site_convert_legacy", () =>
            {
                TopographySurface source = null;
#pragma warning disable CS0618 // Deliberately create a legacy surface to test the migration API.
                Setup(() => source = TopographySurface.Create(doc, new List<XYZ> { new XYZ(90, 0, 0), new XYZ(100, 0, 0), new XYZ(100, 10, 0), new XYZ(90, 10, 0) }));
#pragma warning restore CS0618
                var type = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).First();
                Advanced("site_convert", new JObject { ["element_id"] = Rid.Value(source.Id), ["type_id"] = Rid.Value(type.Id), ["level_id"] = Rid.Value(level.Id) });
            });
            Case("analytical_member_create", () => memberId = Advanced("analytical_member_create", new JObject { ["points"] = new JArray(new JArray(0, 50, 0), new JArray(20, 50, 0)) })["result"].Value<long>("element_id"));
            Case("analytical_member_edit", () => Advanced("analytical_member_edit", new JObject { ["element_id"] = memberId, ["points"] = new JArray(new JArray(0, 50, 0), new JArray(25, 50, 0)) }));
            Case("atomic_plan_revit2024", () => Apply(new ExecutePlanCommand(name => name == "horizun_revit2024" ? new Revit2024Command() : null), new JObject
            { ["actions"] = new JArray(new JObject { ["key"] = "member", ["tool"] = "horizun_revit2024", ["arguments"] = new JObject
                { ["operation"] = "analytical_member_create", ["units"] = "feet", ["points"] = new JArray(new JArray(0, 51, 0), new JArray(20, 51, 0)) } }) }));
            Case("analytical_panel_create", () => panelId = Advanced("analytical_panel_create", new JObject { ["points"] = Polygon(0, 55, 20, 75) })["result"].Value<long>("element_id"));
            Case("analytical_panel_edit", () => Advanced("analytical_panel_edit", new JObject { ["element_id"] = panelId, ["points"] = Polygon(0, 55, 25, 75) }));
            Case("analytical_association", () => Advanced("analytical_associate", new JObject { ["element_id"] = panelId, ["physical_element_id"] = Rid.Value(floor.Id) }));
            foreach (string kind in new[] { "point", "line", "area" })
                Case("analytical_" + kind + "_load", () => Load(kind));
            Case("stale_plan_same_count_replacement", StaleRecipe);
            Case("wire_create", () =>
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Electrical.WireType)).FirstOrDefault();
                if (type == null) throw new MissingDependency("No WireType in fixture template.");
                Advanced("wire_create", new JObject { ["type_id"] = Rid.Value(type.Id), ["view_id"] = Rid.Value(plan.Id), ["points"] = new JArray(new JArray(0, 80, 0), new JArray(5, 82, 0), new JArray(10, 80, 0)) });
            });
            Case("family_reference_plane_localized", FamilyReference);
            Case("family_adaptive", Adaptive);
            Case("family_curve", CurveFamily);
            Case("fabrication_dependency_diagnostic", () =>
            {
                report["installed_fabrication_configurations"] = new JArray(FabricationConfigurationInfo.GetAllFabricationConfigurations()
                    .Select(c => new JObject { ["name"] = c.Name, ["version"] = c.Version, ["units"] = c.UnitSystem.ToString() }));
                JObject catalog = Success(Call(new Revit2024Command(), new JObject { ["operation"] = "catalog" }));
                report["fabrication_catalog"] = catalog;
                if (!(catalog["fabrication"]?["services"] is JArray services) || services.Count == 0) throw new MissingDependency("No Fabrication services configured: load/create/convert cannot be live-verified here.");
            });
        }
        private void MaterialUnits()
        {
            var r = Apply(new ManageMaterialsCommand(), new JObject { ["actions"] = new JArray(new JObject
            {
                ["key"] = "material", ["operation"] = "create", ["name"] = "Audit Material " + Guid.NewGuid().ToString("N"),
                ["structural"] = new JObject { ["class"] = "Generic", ["density"] = Quantity(2400, "KilogramsPerCubicMeter") },
                ["thermal"] = new JObject { ["material_type"] = "Solid", ["density"] = Quantity(2400, "KilogramsPerCubicMeter") }
            }) });
            materialId = r["rows"][0].Value<long>("created_material_id");
            var m = (Material)doc.GetElement(Rid.Make(materialId));
            var asset = ((PropertySetElement)doc.GetElement(m.StructuralAssetId)).GetStructuralAsset();
            Require(Math.Abs(UnitUtils.ConvertFromInternalUnits(asset.Density, UnitTypeId.KilogramsPerCubicMeter) - 2400) < 1e-6, "Density conversion is wrong.");
        }
        private void SharedMaterial()
        {
            Material source = (Material)doc.GetElement(Rid.Make(materialId)); Material copy = null;
            Setup(() => copy = source.Duplicate("Audit shared"));
            ElementId previous = source.StructuralAssetId;
            var r = new JObject { ["actions"] = new JArray(new JObject { ["key"] = "shared", ["operation"] = "update", ["material_id"] = Rid.Value(copy.Id), ["structural"] = new JObject { ["density"] = Quantity(2000, "KilogramsPerCubicMeter") } }) };
            Require(!Call(new ManageMaterialsCommand(), r).Success, "Shared mutation was accepted without isolation.");
            r["actions"][0]["duplicate_shared_assets"] = true;
            Apply(new ManageMaterialsCommand(), r);
            Require(copy.StructuralAssetId != previous && source.StructuralAssetId == previous, "Shared asset was not isolated.");
        }
        private void Appearance()
        {
            var asset = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>().FirstOrDefault(a => a.GetRenderingAsset().FindByName("generic_diffuse") is Autodesk.Revit.DB.Visual.AssetPropertyDoubleArray4d);
            if (asset == null) throw new MissingDependency("No editable generic appearance asset in template.");
            Apply(new ManageMaterialsCommand(), new JObject { ["actions"] = new JArray(new JObject { ["key"] = "appearance", ["operation"] = "update", ["material_id"] = materialId,
                ["appearance_asset_id"] = Rid.Value(asset.Id), ["appearance_properties"] = new JArray(new JObject { ["path"] = "generic_diffuse", ["value"] = new JArray(0.2, 0.3, 0.4, 1.0) }) }) });
        }
        private void Globals()
        {
            Apply(new ManageParametersCommand(), new JObject { ["operation"] = "global_create", ["name"] = "Audit Height", ["data_type"] = "length", ["value"] = 3000 });
            var gp = new FilteredElementCollector(doc).OfClass(typeof(GlobalParameter)).Cast<GlobalParameter>().Single(g => g.Name == "Audit Height");
            report["global_parameter_probe"] = new JArray(wall.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.Double).Select(p => new JObject
                { ["name"] = p.Definition.Name, ["id"] = Rid.Value(p.Id), ["spec"] = p.Definition.GetDataType().TypeId, ["can_associate"] = p.CanBeAssociatedWithGlobalParameter(gp.Id) }));
            Apply(new ManageParametersCommand(), new JObject { ["operation"] = "global_set", ["name"] = "Audit Height", ["associate"] = new JArray(new JObject
            { ["element_id"] = Rid.Value(wall.Id), ["parameter"] = "WALL_BASE_OFFSET" }) });
            Require(wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).GetAssociatedGlobalParameter() != ElementId.InvalidElementId, "Global association is absent.");
        }
        private void AssetProbe()
        {
            var rows = new JArray(); report["structural_asset_probe"] = rows;
            foreach (string variant in new[] { "same_name", "renamed", "name_released", "regenerate", "isotropic", "populated_before_create" })
            {
                var row = new JObject { ["variant"] = variant }; rows.Add(row);
                using (var tx = new Transaction(doc, "Asset API probe"))
                {
                    tx.Start();
                    try
                    {
                        var a = new StructuralAsset("Probe " + variant, StructuralAssetClass.Generic);
                        if (variant == "isotropic") a.Behavior = StructuralBehavior.Isotropic;
                        if (variant == "populated_before_create") a.Density = 67.96043;
                        var set = PropertySetElement.Create(doc, a);
                        if (variant == "regenerate") doc.Regenerate();
                        a = set.GetStructuralAsset();
                        a.Density = 70;
                        if (variant == "renamed") a.Name += " edited";
                        if (variant == "name_released") set.Name += " temporary";
                        set.SetStructuralAsset(a);
                        row["passed"] = Math.Abs(set.GetStructuralAsset().Density - 70) < 1e-8;
                    }
                    catch (Exception ex) { row["error"] = ex.Message; }
                    finally { tx.RollBack(); }
                }
            }
        }
        private void DuplicateParameterIdentity()
        {
            string previous = ui.Application.SharedParametersFilename;
            string path = Path.Combine(root, "parameters.txt");
            File.WriteAllText(path, "# Revit shared parameter test fixture\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n");
            try
            {
                ui.Application.SharedParametersFilename = path;
                var file = ui.Application.OpenSharedParameterFile();
                Require(file != null, "Could not open isolated shared parameter fixture.");
                Setup(() =>
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var definition = file.Groups.Create("Audit " + i).Definitions.Create(new ExternalDefinitionCreationOptions("Audit Duplicate", SpecTypeId.String.Text) { GUID = Guid.NewGuid() });
                        var categories = ui.Application.Create.NewCategorySet(); categories.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Walls));
                        Require(doc.ParameterBindings.Insert(definition, ui.Application.Create.NewInstanceBinding(categories), GroupTypeId.Data), "Could not bind duplicate-name fixture.");
                    }
                });
                var parameters = wall.GetParameters("Audit Duplicate");
                Require(parameters.Count == 2, "Fixture did not create two distinct parameters with the same visible name.");
                var resolve = typeof(ManageParametersCommand).Assembly.GetType("Horizun.Revit.Commands.ParameterResolver").GetMethod("ForWrite");
                bool refused = false;
                try { resolve.Invoke(null, new object[] { wall, "Audit Duplicate" }); } catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { refused = true; }
                Require(refused, "Ambiguous parameter name was accepted for writing.");
                Require(((Parameter)resolve.Invoke(null, new object[] { wall, "id:" + Rid.Value(parameters[1].Id) })).Id == parameters[1].Id, "Explicit parameter identity resolved incorrectly.");
            }
            finally { ui.Application.SharedParametersFilename = previous; }
        }
        private void Provenance()
        {
            var p = new CadProvenance { SchemaVersion = CadProvenanceStore.CurrentVersion, CandidateId = "audit", GeometryId = "g", SemanticId = "s", RuleId = "r", Confidence = 0.81, WrittenUtc = DateTime.UtcNow.ToString("o"), PlacementId = "fixture" };
            using (var t = new Transaction(doc, "audit rollback")) { t.Start(); Require(CadProvenanceStore.Write(wall, p), "Cannot stage provenance."); t.RollBack(); }
            string why;
            Require(!CadProvenanceStore.Matches(wall, p, out why), "Rolled-back metadata reported as persisted.");
            Setup(() => Require(CadProvenanceStore.Write(wall, p), "Cannot write provenance."));
            Require(CadProvenanceStore.Matches(wall, p, out why), "Committed provenance mismatch: " + why);
        }
        private void Boundaries()
        {
            Setup(() =>
            {
                doc.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, ui.Application.Create.NewProjectPosition(0, 0, 100, 0));
                Parameter datum = level.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE) ?? doc.GetElement(level.GetTypeId()).get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE);
                Require(datum != null, "Level elevation base parameter missing."); datum.Set(1);
            });
            Require(Math.Abs(level.Elevation - level.ProjectElevation) > 10, "Fixture did not establish a shared elevation offset.");
            Apply(new CreateElementsCommand(), new JObject { ["units"] = "feet", ["elements"] = new JArray(new JObject { ["kind"] = "room_separator", ["level_id"] = Rid.Value(level.Id),
                ["view_id"] = Rid.Value(plan.Id), ["profile"] = new JArray { new JArray(new JArray(0, 5, 0), new JArray(20, 5, 0)) } }) });
            ViewPlan area = null;
            Setup(() => area = ViewPlan.CreateAreaPlan(doc, new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).First().Id, level.Id));
            Apply(new CreateElementsCommand(), new JObject { ["units"] = "feet", ["elements"] = new JArray(new JObject { ["kind"] = "area_boundary", ["view_id"] = Rid.Value(area.Id),
                ["profile"] = new JArray { new JArray(new JArray(0, 5, 0), new JArray(20, 5, 0)) } }) });
        }
        private void Load(string kind)
        {
            var loadCase = new FilteredElementCollector(doc).OfClass(typeof(LoadCase)).FirstOrDefault();
            if (loadCase == null) throw new MissingDependency("No structural load case in fixture.");
            var r = new JObject { ["host_id"] = kind == "area" ? panelId : memberId, ["load_case_id"] = Rid.Value(loadCase.Id), ["force"] = new JArray(0, 0, -1),
                ["force_unit"] = kind == "point" ? "Kilonewtons" : kind == "line" ? "KilonewtonsPerMeter" : "KilonewtonsPerSquareMeter" };
            if (kind != "area") { r["moment"] = new JArray(0, 0, 0); r["moment_unit"] = "internal"; }
            if (kind == "point") r["point"] = new JArray(0, 50, 0);
            Advanced("analytical_" + kind + "_load", r);
        }
        private void StaleRecipe()
        {
            var cmd = new SplitFloorLoopsCommand();
            var r = new JObject { ["element_ids"] = new JArray(Rid.Value(floor.Id)) };
            JObject preview = Success(Call(cmd, r));
            Setup(() => floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("same element count, changed version"));
            r["dry_run"] = false; r["confirmation_token"] = preview["confirmation_token"];
            Require(!Call(cmd, r).Success, "Recipe accepted stale plan with the same counts.");
        }
        private void FamilyReference()
        {
            var r = new JObject { ["template_path"] = FamilyTemplate("Metric Generic Model.rft"), ["output_path"] = Path.Combine(root, "reference.rfa"), ["load_into_project"] = false,
                ["reference_planes"] = new JArray(new JObject { ["key"] = "ref", ["name"] = "Audit Reference", ["bubble_end"] = new JArray(0, 0, 0), ["free_end"] = new JArray(1000, 0, 0), ["cut_vector"] = new JArray(0, 0, 1) }) };
            JObject result = Apply(new CreateFamilyCommand(), r, false);
            Require(result.Value<int>("reference_planes_verified") == 1, "Reference plane not verified.");
        }
        private void Adaptive()
        {
            Document family = ui.Application.NewFamilyDocument(FamilyTemplate("Metric Generic Model Adaptive.rft"));
            string path = Path.Combine(root, "adaptive.rfa");
            using (var t = new Transaction(family, "fixture adaptive"))
            {
                t.Start();
                for (int i = 0; i < 2; i++) { ReferencePoint p = family.FamilyCreate.NewReferencePoint(new XYZ(i, 0, 0)); AdaptiveComponentFamilyUtils.MakeAdaptivePoint(family, p.Id, AdaptivePointType.PlacementPoint); AdaptiveComponentFamilyUtils.SetPlacementNumber(family, p.Id, i + 1); }
                family.FamilyManager.NewType("Audit"); Guard.Commit(t, "fixture");
            }
            family.SaveAs(path); family.Close(false);
            Family loaded = null; Setup(() => Require(doc.LoadFamily(path, out loaded), "Could not load adaptive fixture."));
            Advanced("family_adaptive", new JObject { ["type_id"] = Rid.Value(loaded.GetFamilySymbolIds().First()), ["points"] = new JArray(new JArray(0, 90, 0), new JArray(10, 90, 5)) });
        }
        private void CurveFamily()
        {
            Document family = ui.Application.NewFamilyDocument(FamilyTemplate("Metric Generic Model line based.rft"));
            string path = Path.Combine(root, "curve.rfa");
            using (var t = new Transaction(family, "fixture curve")) { t.Start(); family.FamilyManager.NewType("Audit"); Guard.Commit(t, "fixture"); }
            family.SaveAs(path); family.Close(false);
            Family loaded = null; Setup(() => Require(doc.LoadFamily(path, out loaded), "Could not load curve fixture."));
            using (var probe = new Transaction(doc, "Curve placement diagnostic"))
            {
                probe.Start();
                var symbol = (FamilySymbol)doc.GetElement(loaded.GetFamilySymbolIds().First());
                symbol.Activate(); doc.Regenerate();
                var fi = doc.Create.NewFamilyInstance(Line.CreateBound(new XYZ(0, 100, 0), new XYZ(10, 100, 0)), symbol, level, StructuralType.NonStructural);
                doc.Regenerate();
                report["curve_placement_diagnostic"] = new JObject { ["level_id"] = Rid.Value(fi.LevelId), ["host_id"] = fi.Host == null ? -1 : Rid.Value(fi.Host.Id),
                    ["host_type"] = fi.Host?.GetType().Name, ["parameters"] = new JArray(fi.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.ElementId)
                        .Select(p => new JObject { ["id"] = Rid.Value(p.Id), ["name"] = p.Definition.Name, ["value"] = Rid.Value(p.AsElementId()), ["read_only"] = p.IsReadOnly })) };
                Require(Guard.RollBack(probe).Confirmed, "Curve diagnostic rollback failed.");
            }
            Advanced("family_curve", new JObject { ["type_id"] = Rid.Value(loaded.GetFamilySymbolIds().First()), ["level_id"] = Rid.Value(level.Id), ["points"] = new JArray(new JArray(0, 100, 0), new JArray(10, 100, 0)) });
        }
        private ElectricalSystem testCircuit;
        private PanelScheduleView testPanelView;
        private void ElectricalFixture()
        {
            CreateFixture(Path.Combine("English", "Electrical-Default_Metric.rte"), "electrical");
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            report["electrical_symbols"] = new JArray(symbols.Where(s => s.Category?.Id == new ElementId(BuiltInCategory.OST_ElectricalEquipment) || s.Category?.Id == new ElementId(BuiltInCategory.OST_ElectricalFixtures))
                .Select(s => new JObject { ["id"] = Rid.Value(s.Id), ["family"] = s.FamilyName, ["type"] = s.Name }));
            var panelSymbol = symbols.FirstOrDefault(s => s.FamilyName.Contains("Panelboard - 208V MLO") && s.Name == "100 A");
            var deviceSymbol = symbols.FirstOrDefault(s => s.FamilyName.Contains("Duplex Receptacle") && s.Name == "Standard")
                ?? symbols.FirstOrDefault(s => s.FamilyName.Contains("Duplex Receptacle"));
            if (panelSymbol == null || deviceSymbol == null) throw new MissingDependency("Electrical template lacks compatible panelboard/receptacle symbols.");
            long Place(FamilySymbol s, double x) => Apply(new CreateElementsCommand(), new JObject
            { ["units"] = "feet", ["elements"] = new JArray(new JObject { ["kind"] = "family_instance", ["type_id"] = Rid.Value(s.Id), ["host_id"] = Rid.Value(wall.Id),
                ["point"] = new JArray(x, 1, 4), ["coordinate_mode"] = "absolute", ["level_id"] = Rid.Value(level.Id) }) })["rows"][0].Value<long>("element_id");
            long panel = Place(panelSymbol, 3), device = Place(deviceSymbol, 15);
            Setup(() =>
            {
                var equipment = ((FamilyInstance)doc.GetElement(Rid.Make(panel))).MEPModel as ElectricalEquipment;
                var compatible = ElectricalSetting.GetElectricalSettings(doc).DistributionSysTypes.Cast<DistributionSysType>()
                    .Where(t => equipment.IsValidDistributionSystem(t)).ToList();
                report["compatible_distribution_systems"] = new JArray(compatible.Select(t => new JObject { ["id"] = Rid.Value(t.Id), ["name"] = t.Name,
                    ["voltage_ln"] = t.VoltageLineToGround?.ActualValue, ["voltage_ll"] = t.VoltageLineToLine?.ActualValue }));
                // VoltageType values are explicitly in volts (RevitAPI.xml), not internal units.
                var system = compatible.FirstOrDefault(t => Math.Abs(t.VoltageLineToGround.ActualValue - 120) < 1);
                Require(system != null, "No compatible 120/208V distribution system in the fixture.");
                equipment.DistributionSystem = system;
            });
            var created = Apply(new ElectricalCommand(), new JObject { ["operation"] = "create_circuit", ["element_ids"] = new JArray(device), ["panel_id"] = panel });
            testCircuit = (ElectricalSystem)doc.GetElement(Rid.Make(created["result"].Value<long>("id")));
            var schedule = Apply(new ElectricalCommand(), new JObject { ["operation"] = "panel_schedule", ["panel_id"] = panel });
            testPanelView = (PanelScheduleView)doc.GetElement(Rid.Make(schedule["result"].Value<long>("panel_schedule_view_id")));
        }
        private void CircuitPath()
        {
            Require(testCircuit != null, "Electrical fixture prerequisite failed.");
            var points = testCircuit.GetCircuitPath().Select(p => new JArray(p.X, p.Y, p.Z)).ToArray();
            Advanced("circuit_set_path", new JObject { ["element_id"] = Rid.Value(testCircuit.Id), ["points"] = new JArray(points) });
            Require(testCircuit.CircuitPathMode == ElectricalCircuitPathMode.Custom, "Circuit path was not made custom.");
        }
        private void MoveSlot()
        {
            Require(testPanelView != null, "Electrical fixture prerequisite failed.");
            var body = testPanelView.GetTableData().GetSectionData(SectionType.Body);
            for (int r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
                for (int c = body.FirstColumnNumber; c <= body.LastColumnNumber; c++)
                {
                    ElectricalSystem source;
                    try { source = testPanelView.GetCircuitByCell(r, c); } catch { continue; }
                    if (source?.Id != testCircuit.Id) continue;
                    for (int target = body.FirstRowNumber; target <= body.LastRowNumber; target++)
                        if (target != r && testPanelView.IsRowInCircuitTable(target) && testPanelView.GetSlotNumberByCell(target, c) > 0 && testPanelView.CanMoveSlotTo(r, c, target, c))
                        { Advanced("panel_move_slot", new JObject { ["view_id"] = Rid.Value(testPanelView.Id), ["row"] = r, ["column"] = c, ["to_row"] = target, ["to_column"] = c }); return; }
                }
            throw new InvalidOperationException("No movable circuit cell in the test panel schedule.");
        }
        private string FamilyTemplate(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT 2024", "Family Templates", "English", name);
        private JObject Advanced(string operation, JObject request) { request["operation"] = operation; request["units"] = "feet"; return Apply(new Revit2024Command(), request); }
        private JObject Apply(ICommand command, JObject request, bool requireApplication = true)
        {
            JObject preview = Success(Call(command, request));
            Require(preview["confirmation_token"] != null, "Preview supplied no confirmation token: " + preview.ToString(Formatting.None));
            request["dry_run"] = false; request["confirmation_token"] = preview["confirmation_token"].DeepClone();
            JObject done = Success(Call(command, request));
            if (requireApplication) Require(ApplicationOutcome.Read(done) == ApplicationState.VerifiedApplied, "Not verified_applied: " + done.ToString(Formatting.None));
            return done;
        }
        private CommandResult Call(ICommand command, JObject request)
        {
            request["target_document"] = doc.PathName;
            CommandResult result = command.Execute(ui, request.ToString(Formatting.None));
            File.AppendAllText(Path.Combine(root, "calls.jsonl"), new JObject { ["tool"] = command.Name, ["request"] = request.DeepClone(), ["response"] = Envelope(result) }.ToString(Formatting.None) + Environment.NewLine);
            return result;
        }
        private static JObject Envelope(CommandResult r) => JObject.FromObject(r);
        private static JObject Success(CommandResult r) { Require(r.Success, r.Error + " " + JsonConvert.SerializeObject(r)); return r.Data as JObject ?? JObject.FromObject(r.Data); }
        private void Setup(Action body) { using (var t = new Transaction(doc, "Audit fixture")) { t.Start(); body(); Guard.Commit(t, "audit fixture"); } }
        private void Case(string name, Action run)
        {
            var result = new JObject { ["name"] = name, ["state"] = "running" }; cases.Add(result); Flush();
            try { run(); result["state"] = "passed"; }
            catch (MissingDependency ex) { result["state"] = "unavailable_dependency"; result["reason"] = ex.Message; }
            catch (Exception ex) { result["state"] = "failed"; result["error"] = ex.ToString(); }
            Flush();
        }
        private void Flush() => File.WriteAllText(Path.Combine(root, "report.json"), report.ToString(Formatting.Indented));
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
        private static JObject Quantity(double value, string unit) => new JObject { ["value"] = value, ["unit"] = unit };
        private static JArray Polygon(double x1, double y1, double x2, double y2) => new JArray(new JArray(x1, y1, 0), new JArray(x2, y1, 0), new JArray(x2, y2, 0), new JArray(x1, y2, 0));
        private static CurveLoop Rectangle(double x1, double y1, double x2, double y2)
        {
            XYZ[] p = { new XYZ(x1, y1, 0), new XYZ(x2, y1, 0), new XYZ(x2, y2, 0), new XYZ(x1, y2, 0) };
            return CurveLoop.Create(p.Select((v, i) => (Curve)Line.CreateBound(v, p[(i + 1) % 4])).ToList());
        }
        private sealed class MissingDependency : Exception { internal MissingDependency(string message) : base(message) { } }
    }
}
