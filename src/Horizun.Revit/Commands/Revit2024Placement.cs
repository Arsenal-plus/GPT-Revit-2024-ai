using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class Revit2024Command
    {
        private static ModelEdit Placement(Document doc, JObject r, string op, double scale)
        {
            ElementId id = null;
            var edit = Edit(op);
            switch (op)
            {
                case "wire_create":
                {
                    WireType type = Need<WireType>(doc, r, "type_id");
                    View view = Need<View>(doc, r, "view_id");
                    var points = Points(r["points"], scale);
                    WiringType wiring;
                    if (!Enum.TryParse(r.Value<string>("wiring_type") ?? "Chamfer", true, out wiring) || !Enum.IsDefined(typeof(WiringType), wiring)) throw new ArgumentException("Invalid wiring_type.");
                    edit.Apply = d => id = Wire.Create(d, type.Id, view.Id, wiring, points, null, null).Id;
                    edit.Verify = d =>
                    {
                        Wire w = d.GetElement(id) as Wire;
                        return new PostconditionCheck("type", "view", "vertices")
                            .Compare("type", Rid.Value(type.Id), w == null ? -1 : Rid.Value(w.GetTypeId()))
                            .Compare("view", Rid.Value(view.Id), w == null ? -1 : Rid.Value(w.OwnerViewId))
                            .Compare("vertices", true, w != null && w.NumberOfVertices == points.Count && points.Select((p, i) => p.DistanceTo(w.GetVertex(i)) < 1e-6).All(x => x));
                    };
                    edit.Warning = "Creates a view-specific schematic wire without connecting device connectors or creating a circuit.";
                    break;
                }
                case "circuit_set_path":
                {
                    var circuit = Need<ElectricalSystem>(doc, r, "element_id");
                    var points = Points(r["points"], scale);
                    id = circuit.Id; edit = Edit(op, circuit);
                    edit.Apply = d => ((ElectricalSystem)d.GetElement(id)).SetCircuitPath(points);
                    edit.Verify = d =>
                    {
                        var actual = ((ElectricalSystem)d.GetElement(id)).GetCircuitPath();
                        return new PostconditionCheck("path").Compare("path", true, actual.Count == points.Count && points.Select((p, i) => p.DistanceTo(actual[i]) < 1e-6).All(x => x));
                    };
                    break;
                }
                case "panel_move_slot":
                {
                    var view = Need<PanelScheduleView>(doc, r, "view_id");
                    int row = r.Value<int?>("row") ?? -1, column = r.Value<int?>("column") ?? -1;
                    int toRow = r.Value<int?>("to_row") ?? -1, toColumn = r.Value<int?>("to_column") ?? -1;
                    if (!view.IsRowInCircuitTable(row) || !view.IsRowInCircuitTable(toRow) ||
                        view.GetSlotNumberByCell(row, column) < 1 || view.GetSlotNumberByCell(toRow, toColumn) < 1)
                        throw new ArgumentException("Source and destination must be circuit slot cells, not panel schedule headings or totals.");
                    if (!view.CanMoveSlotTo(row, column, toRow, toColumn)) throw new ArgumentException("Revit does not permit this slot move.");
                    ElementId circuit = view.GetCircuitByCell(row, column)?.Id;
                    if (circuit == null || circuit == ElementId.InvalidElementId) throw new ArgumentException("The source cell contains no circuit.");
                    id = view.Id; edit = Edit(op, view);
                    edit.Apply = d => ((PanelScheduleView)d.GetElement(id)).MoveSlotTo(row, column, toRow, toColumn);
                    edit.Verify = d => new PostconditionCheck("destination_circuit").Compare("destination_circuit", Rid.Value(circuit), Rid.Value(((PanelScheduleView)d.GetElement(id)).GetCircuitByCell(toRow, toColumn)?.Id ?? ElementId.InvalidElementId));
                    edit.Warning = "Revit moves all circuits in a slot group together; the destination circuit is verified after commit.";
                    break;
                }
                case "family_curve":
                {
                    FamilySymbol symbol = Need<FamilySymbol>(doc, r, "type_id");
                    Level level = Need<Level>(doc, r, "level_id");
                    Curve curve = CurveFor(r, scale);
                    StructuralType structural;
                    if (!Enum.TryParse(r.Value<string>("structural_type") ?? "NonStructural", true, out structural) || !Enum.IsDefined(typeof(StructuralType), structural)) throw new ArgumentException("Invalid structural_type.");
                    if (symbol.Family.FamilyPlacementType != FamilyPlacementType.CurveBased && symbol.Family.FamilyPlacementType != FamilyPlacementType.CurveDrivenStructural)
                        throw new ArgumentException("This family does not support curve placement.");
                    edit.Apply = d =>
                    {
                        if (!symbol.IsActive) { symbol.Activate(); d.Regenerate(); }
                        FamilyInstance fi = d.Create.NewFamilyInstance(curve, symbol, level, structural); id = fi.Id;
                        d.Regenerate();
                        if (PlacementLevel(fi) != Rid.Value(level.Id))
                        {
                            var p = fi.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId && p.AsElementId() != level.Id)
                            {
                                p.Set(level.Id); // false also means unchanged; judge the readback
                                if (p.AsElementId() != level.Id) throw new InvalidOperationException("The family refused its schedule level.");
                            }
                        }
                    };
                    edit.Verify = d =>
                    {
                        var fi = d.GetElement(id) as FamilyInstance;
                        return new PostconditionCheck("symbol", "curve", "level")
                            .Compare("symbol", Rid.Value(symbol.Id), fi == null ? -1 : Rid.Value(fi.GetTypeId()))
                            .Compare("curve", true, SameCurve((fi?.Location as LocationCurve)?.Curve, curve))
                            .Compare("level", Rid.Value(level.Id), PlacementLevel(fi));
                    };
                    break;
                }
                case "family_adaptive":
                {
                    FamilySymbol symbol = Need<FamilySymbol>(doc, r, "type_id");
                    if (!AdaptiveComponentInstanceUtils.IsAdaptiveFamilySymbol(symbol)) throw new ArgumentException("type_id is not an adaptive family symbol.");
                    var points = Points(r["points"], scale, 1);
                    edit.Apply = d =>
                    {
                        if (!symbol.IsActive) { symbol.Activate(); d.Regenerate(); }
                        FamilyInstance fi = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(d, symbol);
                        id = fi.Id;
                        var refs = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(fi);
                        if (refs.Count != points.Count) throw new ArgumentException("points count must equal the family's adaptive placement point count (" + refs.Count + ").");
                        for (int i = 0; i < refs.Count; i++) ((ReferencePoint)d.GetElement(refs[i])).Position = points[i];
                    };
                    edit.Verify = d =>
                    {
                        var fi = d.GetElement(id) as FamilyInstance;
                        var refs = fi == null ? null : AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(fi);
                        return new PostconditionCheck("symbol", "points")
                            .Compare("symbol", Rid.Value(symbol.Id), fi == null ? -1 : Rid.Value(fi.GetTypeId()))
                            .Compare("points", true, refs != null && refs.Count == points.Count && refs.Select((p, i) =>
                                d.GetElement(p) is ReferencePoint rp && rp.Position.DistanceTo(points[i]) < 1e-6).All(x => x));
                    };
                    break;
                }
                default: throw new ArgumentException("Unknown operation: " + op);
            }
            edit.Result = d => Created(d, id);
            return edit;
        }
        private static long PlacementLevel(FamilyInstance fi)
        {
            if (fi == null) return -1;
            if (fi.LevelId != ElementId.InvalidElementId) return Rid.Value(fi.LevelId);
            // Nonstructural curve-based instances can expose the reference level
            // as their host while LevelId and Schedule Level stay unset in RTM.
            if (fi.Host is Level hostedLevel) return Rid.Value(hostedLevel.Id);
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM })
            {
                Parameter p = fi.get_Parameter(bip);
                if (p?.StorageType == StorageType.ElementId && p.AsElementId() != ElementId.InvalidElementId) return Rid.Value(p.AsElementId());
            }
            return -1;
        }
    }
}
