using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class Revit2024Command
    {
        private static JObject FabricationCatalog(Document doc)
        {
            FabricationConfiguration config = FabricationConfiguration.GetFabricationConfiguration(doc);
            if (config == null) return new JObject { ["status"] = "unavailable_dependency", ["reason"] = "No Fabrication configuration in this project." };
            var services = config.GetAllServices().ToList();
            if (services.Count == 0) return new JObject { ["status"] = "unavailable_dependency", ["reason"] = "No Fabrication services configured in this project.", ["services"] = new JArray() };
            var loaded = new HashSet<int>(config.GetAllLoadedServices().Select(s => s.ServiceId));
            return new JObject { ["status"] = "available", ["services"] = new JArray(services.Select(s => new JObject
            {
                ["id"] = s.ServiceId, ["name"] = s.Name, ["loaded"] = loaded.Contains(s.ServiceId),
                ["palettes"] = new JArray(Enumerable.Range(0, s.PaletteCount).Select(p => new JObject
                {
                    ["index"] = p, ["name"] = s.GetPaletteName(p),
                    ["buttons"] = new JArray(Enumerable.Range(0, s.GetButtonCount(p)).Select(b => new JObject
                    { ["index"] = b, ["name"] = s.GetButton(p, b).Name, ["conditions"] = s.GetButton(p, b).ConditionCount }))
                }))
            })) };
        }

        private static ModelEdit Fabrication(Document doc, JObject r, string op, double scale)
        {
            FabricationConfiguration config = FabricationConfiguration.GetFabricationConfiguration(doc);
            if (config == null) throw new ArgumentException("unavailable_dependency: configure a Fabrication database and services in this project first.");
            int serviceId = r.Value<int?>("service_id") ?? throw new ArgumentException("service_id is required.");
            FabricationService service = config.GetAllServices().FirstOrDefault(s => s.ServiceId == serviceId);
            if (service == null) throw new ArgumentException("service_id is absent from the configured Fabrication database.");
            var edit = Edit(op, config);
            var created = new List<ElementId>();
            switch (op)
            {
                case "fabrication_load_service":
                    edit.Apply = d =>
                    {
                        var failed = FabricationConfiguration.GetFabricationConfiguration(d).LoadServices(new List<int> { serviceId });
                        if (failed.Count > 0) throw new InvalidOperationException("Revit could not load the requested service.");
                    };
                    edit.Verify = d => new PostconditionCheck("service_loaded").Compare("service_loaded", true,
                        FabricationConfiguration.GetFabricationConfiguration(d).GetAllLoadedServices().Any(s => s.ServiceId == serviceId));
                    break;
                case "fabrication_create":
                {
                    if (!config.GetAllLoadedServices().Any(s => s.ServiceId == serviceId)) throw new ArgumentException("Load the service first.");
                    int palette = r.Value<int?>("palette_index") ?? -1, buttonIndex = r.Value<int?>("button_index") ?? -1;
                    if (palette < 0 || palette >= service.PaletteCount || buttonIndex < 0 || buttonIndex >= service.GetButtonCount(palette)) throw new ArgumentException("Invalid palette_index or button_index; use catalog.");
                    FabricationServiceButton button = service.GetButton(palette, buttonIndex);
                    int condition = r.Value<int?>("condition_index") ?? -1;
                    if (condition < 0 || condition >= button.ConditionCount) throw new ArgumentException("condition_index is out of range.");
                    Level level = Need<Level>(doc, r, "level_id");
                    XYZ point = ModelEditRunner.Point(r["point"], scale, "point");
                    int connectorId = -1;
                    edit.Apply = d =>
                    {
                        FabricationPart part = FabricationPart.Create(d, button, condition, level.Id);
                        if (part == null) throw new InvalidOperationException("Revit returned no fabrication part.");
                        created = new List<ElementId> { part.Id };
                        d.Regenerate();
                        Connector anchor = part.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Id).FirstOrDefault();
                        if (anchor == null) throw new InvalidOperationException("The part has no connector to use as a placement anchor.");
                        connectorId = anchor.Id;
                        ElementTransformUtils.MoveElement(d, part.Id, point - anchor.Origin);
                    };
                    edit.Verify = d =>
                    {
                        var p = created.Count == 1 ? d.GetElement(created[0]) as FabricationPart : null;
                        var anchor = p?.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.Id == connectorId);
                        return new PostconditionCheck("service", "level", "anchor")
                            .Compare("service", serviceId, p?.ServiceId ?? -1)
                            .Compare("level", Rid.Value(level.Id), p == null ? -1 : Rid.Value(p.LevelId))
                            .Compare("anchor", true, anchor != null && anchor.Origin.DistanceTo(point) < 1e-6);
                    };
                    edit.Warning = "point locates the part's connector with the smallest connector id. The part is created disconnected.";
                    break;
                }
                case "fabrication_convert":
                {
                    if (!(r["element_ids"] is JArray raw) || raw.Count == 0 || raw.Count > 500) throw new ArgumentException("element_ids must contain 1..500 MEP elements.");
                    var ids = new HashSet<ElementId>(raw.Select(x => Rid.Make(x.Value<long>())));
                    if (ids.Any(id => doc.GetElement(id) == null)) throw new ArgumentException("A selected MEP element is missing.");
                    edit.Apply = d =>
                    {
                        using (var converter = new DesignToFabricationConverter(d))
                        {
                            if (converter.Convert(ids, serviceId) != DesignToFabricationConverterResult.Success)
                                throw new InvalidOperationException("Fabrication conversion was incomplete; the transaction will roll back.");
                            created = converter.GetConvertedFabricationParts().ToList();
                            if (converter.GetConvertedFabricationPartsWithInvalidConnections().Count > 0)
                                throw new InvalidOperationException("Conversion produced invalid connections.");
                        }
                    };
                    edit.Verify = d => new PostconditionCheck("created_parts", "design_elements_replaced")
                        .Compare("created_parts", true, created.Count > 0 && created.All(id => d.GetElement(id) is FabricationPart p && p.ServiceId == serviceId))
                        .Compare("design_elements_replaced", true, ids.All(id => d.GetElement(id) == null));
                    edit.Warning = "Replaces the selected design MEP elements with fabrication parts. Partial conversion is rolled back.";
                    break;
                }
                default: throw new ArgumentException("Unknown fabrication operation: " + op);
            }
            edit.Result = d => new JObject { ["service_id"] = serviceId, ["element_ids"] = new JArray(created.Select(Rid.Value)) };
            return edit;
        }
    }
}
