using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class Revit2024Command
    {
        private static ModelEdit Analytical(Document doc, JObject r, string op, double scale)
        {
            ElementId id = null;
            var edit = Edit(op);
            switch (op)
            {
                case "analytical_member_create":
                case "analytical_member_edit":
                {
                    Curve curve = CurveFor(r, scale);
                    if (op.EndsWith("edit", StringComparison.Ordinal)) { var m = Need<AnalyticalMember>(doc, r, "element_id"); id = m.Id; edit = Edit(op, m); }
                    edit.Apply = d => { if (op.EndsWith("create", StringComparison.Ordinal)) id = AnalyticalMember.Create(d, curve).Id; else ((AnalyticalMember)d.GetElement(id)).SetCurve(curve); };
                    edit.Verify = d => new PostconditionCheck("curve").Compare("curve", true, SameCurve((d.GetElement(id) as AnalyticalMember)?.GetCurve(), curve));
                    break;
                }
                case "analytical_panel_create":
                case "analytical_panel_edit":
                {
                    CurveLoop loop = Loop(r["points"], scale);
                    if (op.EndsWith("edit", StringComparison.Ordinal)) { var p = Need<AnalyticalPanel>(doc, r, "element_id"); id = p.Id; edit = Edit(op, p); }
                    edit.Apply = d => { if (op.EndsWith("create", StringComparison.Ordinal)) id = AnalyticalPanel.Create(d, loop).Id; else ((AnalyticalPanel)d.GetElement(id)).SetOuterContour(loop); };
                    edit.Verify = d => new PostconditionCheck("contour").Compare("contour", true,
                        d.GetElement(id) is AnalyticalPanel p && SameLoop(loop, p.GetOuterContour()));
                    break;
                }
                case "analytical_associate":
                {
                    Element a = Need<AnalyticalElement>(doc, r, "element_id");
                    Element p = Need<Element>(doc, r, "physical_element_id");
                    if (!AnalyticalToPhysicalAssociationManager.IsPhysicalElement(doc, p.Id)) throw new ArgumentException("physical_element_id is not a supported physical element.");
                    id = a.Id; edit = Edit(op, a);
                    edit.Apply = d => AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(d).AddAssociation(a.Id, p.Id);
                    edit.Verify = d => new PostconditionCheck("association").Compare("association", Rid.Value(p.Id),
                        Rid.Value(AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(d).GetAssociatedElementId(a.Id)));
                    break;
                }
                case "analytical_point_load":
                case "analytical_line_load":
                case "analytical_area_load":
                {
                    Element host = Need<AnalyticalElement>(doc, r, "host_id");
                    LoadCase loadCase = Need<LoadCase>(doc, r, "load_case_id");
                    bool point = op == "analytical_point_load", line = op == "analytical_line_load";
                    XYZ force = QuantityVector(r, "force", point ? SpecTypeId.Force : line ? SpecTypeId.LinearForce : SpecTypeId.AreaForce);
                    XYZ moment = point || line ? QuantityVector(r, "moment", point ? SpecTypeId.Moment : SpecTypeId.LinearMoment) : XYZ.Zero;
                    XYZ position = point ? ModelEditRunner.Point(r["point"], scale, "point") : null;
                    edit.Apply = d =>
                    {
                        LoadBase load;
                        if (point) load = PointLoad.Create(d, host.Id, position, force, moment, null);
                        else if (line) load = LineLoad.Create(d, host.Id, force, moment, null);
                        else load = AreaLoad.Create(d, host.Id, force, null);
                        if (load == null) throw new InvalidOperationException("Revit returned no load.");
                        id = load.Id;
                        if (!load.get_Parameter(BuiltInParameter.LOAD_CASE_ID).Set(loadCase.Id)) throw new InvalidOperationException("Load case was refused.");
                    };
                    edit.Verify = d =>
                    {
                        var load = d.GetElement(id) as LoadBase;
                        XYZ actualForce = load is PointLoad pl ? pl.ForceVector : load is LineLoad ll ? ll.ForceVector1 : (load as AreaLoad)?.ForceVector1;
                        XYZ actualMoment = load is PointLoad pm ? pm.MomentVector : load is LineLoad lm ? lm.MomentVector1 : XYZ.Zero;
                        var check = new PostconditionCheck(point ? new[] { "host", "case", "force", "moment", "point" } : new[] { "host", "case", "force", "moment" })
                            .Compare("host", Rid.Value(host.Id), load == null ? -1 : Rid.Value(load.HostElementId))
                            .Compare("case", Rid.Value(loadCase.Id), load == null ? -1 : Rid.Value(load.LoadCaseId))
                            .Compare("force", true, actualForce != null && actualForce.DistanceTo(force) < 1e-6)
                            .Compare("moment", true, actualMoment != null && actualMoment.DistanceTo(moment) < 1e-6);
                        if (point) check.Compare("point", true, load is PointLoad pp && pp.Point.DistanceTo(position) < 1e-6);
                        return check;
                    };
                    break;
                }
                default: throw new ArgumentException("Unknown analytical operation: " + op);
            }
            edit.Result = d => Created(d, id);
            return edit;
        }
    }
}
