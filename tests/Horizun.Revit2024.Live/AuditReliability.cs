using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit2024.Live
{
    public sealed partial class AuditApp
    {
        private void ReliabilitySuite()
        {
            Case("capture_3d_repeated_restore", CaptureRepeated);
            Case("capture_plan_restore", () => CaptureAndVerify(plan, null));
            Case("wall_opening_rotated_host_cut", OpeningOnRotatedWall);
            Case("snapshot_null_room_and_missing_id", SnapshotUnavailable);
            Case("connector_primary_omitted", () => ConnectorFamily("omitted", false));
            Case("connector_explicit_primary_after_creation", () => ConnectorFamily("explicit", true));
            Case("workflow_prepares_inactive_project_and_references", WorkflowPrepare);
            Case("workflow_stops_after_partial_completion", WorkflowPartial);
            Case("workflow_rejects_stale_confirmation", WorkflowStale);
            Case("workflow_family_document_preparation", WorkflowFamily);
            Case("python_utf8_json_and_optional_boundaries", PythonHelpers);
        }
        private void CaptureRepeated()
        {
            View3D view = null;
            Setup(() =>
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(doc, type.Id);
                view.SetOrientation(new ViewOrientation3D(new XYZ(30, -20, 15), new XYZ(0, 0, 1), new XYZ(-1, 1, 0).Normalize()));
                view.IsSectionBoxActive = false;
            });
            foreach (string orientation in new[] { "top", "front", "right", "isometric", "top" }) CaptureAndVerify(view, orientation);
        }
        private void CaptureAndVerify(View view, string orientation)
        {
            bool crop = view.CropBoxActive, visible = view.CropBoxVisible;
            var before = (view as View3D)?.GetOrientation();
            var request = new JObject { ["view_id"] = Rid.Value(view.Id), ["pixel_size"] = 512,
                ["element_ids"] = new JArray(Rid.Value(wall.Id)), ["hide_annotations"] = true,
                ["display_style"] = "HLR" };
            if (orientation != null) request["orientation"] = orientation;
            JObject result = Success(Call(new CaptureViewCommand(), request));
            Require(result.Value<bool>("captured") && result.Value<bool>("view_restored"), "Capture/restore not verified.");
            Require(File.Exists(result.Value<string>("image_path")), "PNG does not exist.");
            Require(view.CropBoxActive == crop && view.CropBoxVisible == visible, "Crop flags changed.");
            if (before != null)
            {
                var after = ((View3D)view).GetOrientation();
                Require(before.EyePosition.DistanceTo(after.EyePosition) < 1e-6 &&
                    before.UpDirection.DistanceTo(after.UpDirection) < 1e-9 &&
                    before.ForwardDirection.DistanceTo(after.ForwardDirection) < 1e-9, "Camera changed after capture.");
            }
        }
        private void OpeningOnRotatedWall()
        {
            Wall host = null;
            XYZ start = new XYZ(80, 100, 0), axis = new XYZ(1, 1, 0).Normalize(), normal = new XYZ(-axis.Y, axis.X, 0);
            Setup(() => host = Wall.Create(doc, Line.CreateBound(start, start + 20 * axis), wall.WallType.Id, level.Id, 10, 0, false, false));
            double before = host.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
            XYZ a = start + 4 * axis + 0.1 * normal + 2 * XYZ.BasisZ;
            XYZ b = start + 9 * axis + 0.1 * normal + 6 * XYZ.BasisZ;
            JObject result = Apply(new CreateElementsCommand(), new JObject { ["units"] = "feet", ["elements"] = new JArray(new JObject
            { ["kind"] = "wall_opening", ["host_id"] = Rid.Value(host.Id), ["start"] = Vec(a), ["end"] = Vec(b) }) });
            var opening = (Opening)doc.GetElement(Rid.Make(result["rows"][0].Value<long>("element_id")));
            Require(opening.Host.Id == host.Id && opening.IsRectBoundary, "Opening host or boundary differs.");
            double cut = before - host.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
            Require(Math.Abs(cut - 5 * 4 * host.Width) < 1e-5, "Physical wall cut volume is incorrect: " + cut);
            Require(opening.BoundaryRect.Count == 2, "Rectangular opening has no two corners.");
        }
        private void SnapshotUnavailable()
        {
            Room room = null;
            Setup(() => room = doc.Create.NewRoom(doc.Phases.Cast<Phase>().Last()));
            var snapshot = Success(Call(new ModelSnapshotCommand(), new JObject { ["element_ids"] = new JArray(Rid.Value(room.Id), Rid.Value(wall.Id)) }));
            Require(snapshot.Value<bool>("complete"), "Snapshot failed on unplaced room.");
            JObject roomRow = snapshot["elements"].OfType<JObject>().Single(x => x.Value<long>("id") == Rid.Value(room.Id));
            Require(roomRow.Value<string>("boundaries_status") == "not_enclosed_or_unplaced", "Unavailable boundary was not explicitly reported.");
            var missing = Success(Call(new ModelSnapshotCommand(), new JObject { ["element_ids"] = new JArray(long.MaxValue) }));
            Require(!missing.Value<bool>("complete") && ((JArray)missing["missing_element_ids"]).Count == 1, "Missing id reported complete.");
        }
        private void ConnectorFamily(string name, bool explicitPrimary)
        {
            JObject first = new JObject { ["key"] = "first", ["kind"] = "pipe", ["host_form_key"] = "body",
                ["system_type"] = "SupplyHydronic", ["face_normal"] = new JArray(0, 0, 1) };
            var connectors = new JArray(first);
            if (explicitPrimary)
            {
                first["primary"] = false;
                connectors.Add(new JObject { ["key"] = "second", ["kind"] = "pipe", ["host_form_key"] = "body",
                    ["system_type"] = "SupplyHydronic", ["face_normal"] = new JArray(0, 0, -1), ["primary"] = true });
            }
            var request = new JObject { ["template_path"] = FamilyTemplate("Metric Mechanical Equipment.rft"),
                ["output_path"] = Path.Combine(root, "connector-" + name + ".rfa"), ["load_into_project"] = false,
                ["forms"] = new JArray(new JObject { ["key"] = "body", ["kind"] = "extrusion", ["depth"] = 1000,
                    ["profile"] = new JArray { Polygon(0, 0, 1000, 1000) } }), ["connectors"] = connectors };
            JObject result = Apply(new CreateFamilyCommand(), request, false);
            Require(result.Value<int>("connectors_verified") == connectors.Count, "Connectors not verified.");
            using (Document family = ui.Application.OpenDocumentFile(request.Value<string>("output_path")))
            {
                var actual = new FilteredElementCollector(family).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().ToList();
                Require(actual.Count == connectors.Count && actual.Count(c => c.IsPrimary) == 1, "Saved connector primary state differs.");
                family.Close(false);
            }
        }
        private RunWorkflowCommand Workflow() => new RunWorkflowCommand(name =>
        {
            switch (name)
            {
                case "horizun_document_session": return new DocumentSessionCommand();
                case "horizun_create_elements": return new CreateElementsCommand();
                case "horizun_create_family": return new CreateFamilyCommand();
                case "horizun_family_apply": return new FamilyApplyCommand();
                case "horizun_manage_materials": return new ManageMaterialsCommand();
                case "horizun_model_snapshot": return new ModelSnapshotCommand();
                default: return null;
            }
        });
        private static JObject Step(string key, string tool, JObject args) => new JObject { ["key"] = key, ["tool"] = tool, ["arguments"] = args };
        private JObject WorkflowRequest(params JObject[] steps) => new JObject { ["steps"] = new JArray(steps),
            ["idempotency_key"] = "live-workflow-" + Guid.NewGuid().ToString("N") };
        private void WorkflowPrepare()
        {
            string path = Path.Combine(root, "other-project.rvt");
            Document background = ui.Application.NewProjectDocument(UnitSystem.Metric);
            background.SaveAs(path); background.Close(false);
            Document other = ui.OpenAndActivateDocument(path).Document; owned.Add(other);
            try
            {
                var request = WorkflowRequest(
                    Step("make", "horizun_create_elements", new JObject { ["units"] = "feet", ["elements"] = new JArray(new JObject
                    { ["kind"] = "level", ["name"] = "Workflow Level", ["elevation"] = 123 }) }),
                    Step("read", "horizun_model_snapshot", new JObject { ["element_ids"] = new JArray("${make.rows.0.element_id}") }));
                JObject result = Apply(Workflow(), request);
                Require(ui.ActiveUIDocument.Document.Equals(doc), "Root project was not activated.");
                Require(result["workflow"].Value<bool>("recovered"), "Activation recovery was not reported.");
                Require(result["results"]["read"].Value<bool>("complete"), "Dependent read incomplete.");
                Require(File.ReadAllLines(result.Value<string>("trace_path")).Length >= 6, "Workflow trace is incomplete.");
            }
            finally { ui.OpenAndActivateDocument(doc.PathName); }
        }
        private void WorkflowPartial()
        {
            string name = "Workflow Partial " + Guid.NewGuid().ToString("N");
            var request = WorkflowRequest(
                Step("first", "horizun_create_elements", new JObject { ["units"] = "feet", ["elements"] = new JArray(new JObject { ["kind"] = "level", ["name"] = name, ["elevation"] = 124 }) }),
                Step("bad", "horizun_create_elements", new JObject { ["elements"] = new JArray(new JObject { ["kind"] = "wall_opening", ["host_id"] = 9223372036854775807L }) }));
            JObject preview = Success(Call(Workflow(), request));
            request["dry_run"] = false; request["confirmation_token"] = preview["confirmation_token"];
            CommandResult result = Call(Workflow(), request);
            Require(!result.Success, "Invalid second step did not stop.");
            JObject detail = JObject.FromObject(result.Detail);
            Require(detail.Value<int>("completed_steps") == 1 && detail["application"].Value<string>("state") == "partial", "Partial result missing.");
            Require(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Count(l => l.Name == name) == 1, "Completed first step lost or duplicated.");
            Require(File.ReadAllLines(detail.Value<string>("trace_path")).Length >= 5, "Partial trace missing.");
        }
        private void WorkflowStale()
        {
            var request = WorkflowRequest(Step("read", "horizun_model_snapshot", new JObject { ["element_ids"] = new JArray(Rid.Value(wall.Id)) }));
            var command = Workflow(); JObject preview = Success(Call(command, request));
            Setup(() => wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("invalidate workflow"));
            request["dry_run"] = false; request["confirmation_token"] = preview["confirmation_token"];
            Require(!Call(command, request).Success, "Stale model-state confirmation was accepted.");
        }
        private void WorkflowFamily()
        {
            string path = Path.Combine(root, "workflow-family.rfa");
            Document family = ui.Application.NewFamilyDocument(FamilyTemplate("Metric Generic Model.rft"));
            using (var tx = new Transaction(family, "fixture")) { tx.Start(); family.FamilyManager.NewType("Before"); Guard.Commit(tx, "fixture"); }
            family.SaveAs(path); family.Close(false);
            try
            {
                var request = WorkflowRequest(Step("rename", "horizun_family_apply", new JObject
                { ["target_document"] = path, ["family_name"] = "After", ["collapse_types"] = true, ["save"] = false }));
                JObject result = Apply(Workflow(), request);
                family = ui.ActiveUIDocument.Document;
                Require(family.IsFamilyDocument && family.FamilyManager.CurrentType.Name == "After", "Wrong family context or type.");
                owned.Add(family);
            }
            finally
            {
                foreach (var open in ui.Application.Documents.Cast<Document>().Where(d => d.IsFamilyDocument && d.PathName == path))
                    if (!owned.Contains(open)) owned.Add(open);
                ui.OpenAndActivateDocument(doc.PathName);
            }
        }
        private void PythonHelpers()
        {
            string path = Path.Combine(root, "кириллица.json");
            File.WriteAllText(path, "{\"name\":\"Художка\"}", new UTF8Encoding(true));
            var request = new JObject { ["code"] = "data = read_json(" + JsonConvert.SerializeObject(path) + ")\nassert data['name'] == u'Художка'\nassert optional_items(None) == []\nassert optional_items([1]) == [1]\n__output__ = {'helpers_verified': True}",
                ["read_only"] = true };
            JObject result = Success(Call(new ExecutePythonCommand(), request));
            Require(result.ToString().Contains("helpers_verified"), "Helper script output missing.");
        }
        private static JArray Vec(XYZ p) => new JArray(p.X, p.Y, p.Z);
    }
}
