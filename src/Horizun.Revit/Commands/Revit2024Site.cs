using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class Revit2024Command
    {
        private static ModelEdit Site(Document doc, JObject r, string op, double scale)
        {
#if REVIT2023
            throw new ArgumentException("Toposolid requires Revit 2024 or later.");
#else
            ElementId created = null;
            var edit = Edit(op);
            switch (op)
            {
                case "site_create":
                {
                    var type = Need<ToposolidType>(doc, r, "type_id");
                    var level = Need<Level>(doc, r, "level_id");
                    var raw = r["loops"] as JArray;
                    if (raw == null || raw.Count == 0) throw new ArgumentException("loops is required (outer boundary and optional holes).");
                    List<CurveLoop> loops = raw.Select(x => Loop(x, scale)).ToList();
                    edit.Apply = d => created = Toposolid.Create(d, loops, type.Id, level.Id).Id;
                    edit.Verify = d =>
                    {
                        var t = d.GetElement(created) as Toposolid;
                        var sketch = t == null ? null : d.GetElement(t.SketchId) as Sketch;
                        var curves = sketch?.Profile.Cast<CurveArray>().SelectMany(a => a.Cast<Curve>()).ToList();
                        return new PostconditionCheck("type", "level", "boundary")
                            .Compare("type", Rid.Value(type.Id), t == null ? -1 : Rid.Value(t.GetTypeId()))
                            .Compare("level", Rid.Value(level.Id), t == null ? -1 : Rid.Value(t.LevelId))
                            .Compare("boundary", true, curves != null && SameLoop(loops.SelectMany(l => l), curves));
                    };
                    break;
                }
                case "site_subdivide":
                {
                    var host = Need<Toposolid>(doc, r, "host_id");
                    if (!(r["loops"] is JArray raw) || raw.Count == 0) throw new ArgumentException("loops is required.");
                    List<CurveLoop> loops = raw.Select(x => Loop(x, scale)).ToList();
                    edit = Edit(op, host);
                    edit.Apply = d => created = ((Toposolid)d.GetElement(host.Id)).CreateSubDivision(d, loops).Id;
                    edit.Verify = d =>
                    {
                        var t = d.GetElement(created) as Toposolid;
                        var sketch = t == null ? null : d.GetElement(t.SketchId) as Sketch;
                        var curves = sketch?.Profile.Cast<CurveArray>().SelectMany(a => a.Cast<Curve>()).ToList();
                        return new PostconditionCheck("host", "boundary")
                            .Compare("host", Rid.Value(host.Id), t == null ? -1 : Rid.Value(t.HostTopoId))
                            .Compare("boundary", true, curves != null && SameLoop(loops.SelectMany(l => l), curves));
                    };
                    break;
                }
                case "site_convert":
                {
                    var source = Need<TopographySurface>(doc, r, "element_id");
                    var type = Need<ToposolidType>(doc, r, "type_id");
                    var level = Need<Level>(doc, r, "level_id");
                    edit = Edit(op, source);
                    var sourcePoints = source.GetPoints().ToList();
                    edit.Apply = d => created = Toposolid.CreateFromTopographySurface(d, source.Id, type.Id, level.Id).Id;
                    edit.Verify = d =>
                    {
                        var t = d.GetElement(created) as Toposolid;
                        var vertices = t?.GetSlabShapeEditor()?.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => v.Position).ToList();
                        return new PostconditionCheck("type", "level", "geometry", "source_points")
                            .Compare("type", Rid.Value(type.Id), t == null ? -1 : Rid.Value(t.GetTypeId()))
                            .Compare("level", Rid.Value(level.Id), t == null ? -1 : Rid.Value(t.LevelId))
                            .Compare("geometry", true, t != null && t.get_Geometry(new Options()).OfType<Solid>().Any(s => s.Volume > 0))
                            .Compare("source_points", true, vertices != null && sourcePoints.Count > 0 && sourcePoints.All(p => vertices.Any(v => v.DistanceTo(p) < 1e-6)));
                    };
                    edit.Warning = "Revit creates a Toposolid using its conversion API; the source surface is not explicitly deleted by this tool. Inspect both before removing the legacy surface.";
                    break;
                }
                default: throw new ArgumentException("Unknown site operation: " + op);
            }
            edit.Result = d => Created(d, created);
            return edit;
#endif
        }
    }
}
