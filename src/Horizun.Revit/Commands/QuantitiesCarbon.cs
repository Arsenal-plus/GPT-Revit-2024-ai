// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_quantities mode='carbon' - volume, area and mass per material, times
// the caller's factor table. No factor is compiled in.
//
// Revit's material takeoff (Element.GetMaterialIds(false), GetMaterialVolume,
// GetMaterialArea) is the same third opinion mode 'volume' already reads; the
// density comes from the material's StructuralAsset when it has one. The factors
// are the caller's - an EPD list, an EC3 export - keyed by material name or class,
// per m3 or per kg, with a factor_source that travels in the reply, because an
// embodied-carbon figure nobody can trace to a declaration is not a figure.
//
// WHAT IS NOT COUNTED IS NAMED, BY REASON: a material with no matching factor, a
// per-kg factor on a material with no density, a volume that could not be read,
// an element that reports no materials. Each group says how many readings it
// counted out of how many it had; a group that is not complete says so. Nothing
// becomes a zero on the way to a dashboard.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class QuantitiesCommand
    {
        private CommandResult ExecuteCarbon(Document doc, JObject request, int top)
        {
            var factors = new List<CarbonFactor>();
            var table = request["carbon_factors"] as JArray;
            if (table != null)
                foreach (var t in table)
                {
                    var o = t as JObject;
                    if (o == null) { factors.Add(null); continue; }
                    double f;
                    var ft = o["factor"];
                    factors.Add(new CarbonFactor
                    {
                        Material = o.Value<string>("material"), MaterialClass = o.Value<string>("material_class"),
                        Per = o.Value<string>("per"),
                        Factor = ft != null && (ft.Type == JTokenType.Float || ft.Type == JTokenType.Integer) ? (f = ft.Value<double>()) : double.NaN
                    });
                }
            string problem = CarbonRules.Validate(factors);
            if (problem != null) return CommandResult.Fail("mode 'carbon': " + problem + " Nothing was measured.");
            string factorSource = request.Value<string>("factor_source");
            if (string.IsNullOrWhiteSpace(factorSource))
                return CommandResult.Fail("mode 'carbon' needs factor_source: where the factors came from (the EPD list, the EC3 export, " +
                                          "the project document). A carbon figure nobody can trace is not a figure. Nothing was measured.");
            string codeParameter = request.Value<string>("code_parameter");
            if (string.IsNullOrWhiteSpace(codeParameter)) codeParameter = null;

            var failed = new JArray();
            var elements = ResolveHostScope(doc, request, failed, out problem);
            if (elements == null) return CommandResult.Fail("mode 'carbon': " + problem + " Nothing was measured.");

            var readings = new List<CarbonReading>();
            var noMaterials = new JArray();
            var unreadable = new JArray();
            var densityCache = new Dictionary<long, double?>();
            var levelCache = new Dictionary<long, string>();
            foreach (var e in elements)
            {
                long eid = Rid.Value(e.Id);
                ICollection<ElementId> mats;
                try { mats = e.GetMaterialIds(false); }
                catch (Exception ex) { failed.Add(new JObject { ["element_id"] = eid, ["error"] = "Materials could not be read: " + ex.Message }); continue; }
                if (mats == null || mats.Count == 0) { noMaterials.Add(eid); continue; }
                string level = LevelLabel(doc, e, levelCache);
                string code = codeParameter == null ? null : ReadCode(doc, e, codeParameter);
                foreach (var mid in mats)
                {
                    var mat = doc.GetElement(mid) as Material;
                    var r = new CarbonReading
                    {
                        ElementId = eid.ToString(), Material = mat?.Name ?? "(material " + Rid.Value(mid) + " unreadable)",
                        MaterialClass = mat == null ? null : SafeString(() => mat.MaterialClass), Code = code, Level = level
                    };
                    try { r.VolumeM3 = e.GetMaterialVolume(mid) * CarbonRules.CubicFeetToM3; }
                    catch (Exception ex) { unreadable.Add(new JObject { ["element_id"] = eid, ["material"] = r.Material, ["error"] = ex.Message }); }
                    try { r.AreaM2 = e.GetMaterialArea(mid, false) * RoomFinishRules.SquareFeetToM2; } catch { }
                    r.DensityKgM3 = DensityOf(doc, mat, densityCache);
                    readings.Add(r);
                }
            }

            var groups = CarbonRules.Group(readings, factors);
            var rows = new JArray();
            foreach (var g in groups.Take(top))
                rows.Add(new JObject
                {
                    ["material"] = g.Material, ["material_class"] = g.MaterialClass, ["code"] = g.Code, ["level"] = g.Level,
                    ["factor"] = g.Factor, ["factor_per"] = g.FactorPer, ["matched_by"] = g.MatchedBy,
                    ["volume_m3"] = Math.Round(g.VolumeM3, 6), ["area_m2"] = Math.Round(g.AreaM2, 4),
                    ["mass_kg"] = g.MassReadings > 0 ? (JToken)Math.Round(g.MassKg, 3) : JValue.CreateNull(),
                    ["mass_readings"] = g.MassReadings,
                    ["kgco2e"] = g.Counted > 0 ? (JToken)Math.Round(g.KgCO2e, 3) : JValue.CreateNull(),
                    ["readings"] = g.Readings, ["counted"] = g.Counted, ["no_factor"] = g.NoFactor,
                    ["no_density"] = g.NoDensity, ["unreadable_volume"] = g.UnreadableVolume, ["complete"] = g.Complete
                });

            bool complete = failed.Count == 0 && noMaterials.Count == 0 && groups.All(g => g.Complete);
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "carbon",
                ["factor_source"] = factorSource,
                ["code_parameter"] = codeParameter,
                ["scope"] = "host document only; linked models are not read",
                ["rule"] = "kgco2e sums ONLY counted readings. A reading with no factor, a per-kg factor without a density, or an " +
                           "unreadable volume is counted by reason and excluded - never a zero. Rows are flat for horizun_power_bi_push.",
                ["rows"] = rows,
                ["rows_total"] = groups.Count,
                ["truncated"] = groups.Count > top,
                ["kgco2e_counted_total"] = Math.Round(groups.Sum(g => g.KgCO2e), 3),
                ["materials_without_factor"] = new JArray(groups.Where(g => g.NoFactor > 0).Select(g => g.Material).Distinct()),
                ["materials_without_density"] = new JArray(groups.Where(g => g.NoDensity > 0).Select(g => g.Material).Distinct()),
                ["unreadable_volumes"] = unreadable,
                ["elements_without_materials"] = noMaterials,
                ["failed"] = failed,
                ["coverage"] = new JObject
                {
                    ["elements"] = elements.Count, ["readings"] = readings.Count,
                    ["counted"] = groups.Sum(g => g.Counted), ["complete"] = complete
                }
            });
        }

        /// <summary>element_ids, or a category swept in the host document.</summary>
        private static List<Element> ResolveHostScope(Document doc, JObject request, JArray failed, out string problem)
        {
            problem = null;
            var list = new List<Element>();
            var ids = request["element_ids"] as JArray;
            if (ids != null && ids.Count > 0)
            {
                foreach (var tok in ids)
                {
                    long id;
                    if (tok.Type != JTokenType.Integer || !Rid.CanRepresentElementId(id = tok.Value<long>()))
                    { failed.Add(new JObject { ["element_id"] = tok.ToString(), ["error"] = "Not a usable element id." }); continue; }
                    var e = doc.GetElement(Rid.ToElementId(id));
                    if (e == null) failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." });
                    else list.Add(e);
                }
                return list;
            }
            string catName = request.Value<string>("category");
            BuiltInCategory bic;
            if (string.IsNullOrWhiteSpace(catName) || !Enum.TryParse(catName, true, out bic))
            {
                problem = "Pass element_ids, or a BuiltInCategory name in category (e.g. OST_Walls).";
                return null;
            }
            list.AddRange(new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements());
            return list;
        }

        /// <summary>The element's level: LevelId, then the schedule level, then the reference level; else named.</summary>
        private static string LevelLabel(Document doc, Element e, Dictionary<long, string> cache)
        {
            ElementId lid = null;
            try { lid = e.LevelId; } catch { }
            foreach (var bip in new[] { BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM })
            {
                if (lid != null && lid != ElementId.InvalidElementId) break;
                try { var p = e.get_Parameter(bip); lid = p == null ? null : p.AsElementId(); } catch { }
            }
            if (lid == null || lid == ElementId.InvalidElementId) return "(no level)";
            long key = Rid.Value(lid);
            string name;
            if (!cache.TryGetValue(key, out name))
                cache[key] = name = SafeString(() => doc.GetElement(lid)?.Name) ?? "(level " + key + " unreadable)";
            return name;
        }

        /// <summary>kg/m3 from the material's StructuralAsset; null when it has none or it is not positive.</summary>
        private static double? DensityOf(Document doc, Material mat, Dictionary<long, double?> cache)
        {
            if (mat == null) return null;
            long key = Rid.Value(mat.Id);
            double? d;
            if (cache.TryGetValue(key, out d)) return d;
            d = null;
            try
            {
                var aid = mat.StructuralAssetId;
                var pse = aid == null || aid == ElementId.InvalidElementId ? null : doc.GetElement(aid) as PropertySetElement;
                var asset = pse?.GetStructuralAsset();
                if (asset != null && asset.Density > 0) d = CarbonRules.KgPerCubicFootToKgPerM3(asset.Density);
            }
            catch { d = null; }
            cache[key] = d;
            return d;
        }

        private static string SafeString(Func<string> f) { try { return f(); } catch { return null; } }
    }
}
