using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class AppearanceProperties
    {
        internal static void Apply(Document doc, Material material, JArray fields, bool duplicateShared)
        {
            if (fields == null) return;
            if (fields.Count == 0 || fields.Count > 100) throw new ArgumentException("appearance_properties must contain 1..100 fields.");
            var element = doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;
            if (element == null) throw new ArgumentException("Assign an existing appearance asset before editing its properties.");
            int users = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Count(m => m.AppearanceAssetId == element.Id);
            if (users > 1)
            {
                if (!duplicateShared) throw new ArgumentException("The appearance asset is shared. Set duplicate_shared_assets=true to edit this material's own copy.");
                element = element.Duplicate(material.Name + " appearance " + Guid.NewGuid().ToString("N"));
                material.AppearanceAssetId = element.Id;
            }
            using (var scope = new AppearanceAssetEditScope(doc))
            {
                Asset asset = scope.Start(element.Id);
                foreach (JToken token in fields)
                {
                    if (!(token is JObject row)) throw new ArgumentException("Each appearance edit must be an object.");
                    AssetProperty property = Resolve(asset, row.Value<string>("path"));
                    JToken value = row["value"] ?? throw new ArgumentException("Appearance property requires value.");
                    if (property is AssetPropertyString s && value.Type == JTokenType.String) s.Value = (string)value;
                    else if (property is AssetPropertyBoolean b && value.Type == JTokenType.Boolean) b.Value = (bool)value;
                    else if (property is AssetPropertyInteger i && value.Type == JTokenType.Integer) i.Value = (int)value;
                    else if (property is AssetPropertyDouble d) d.Value = Number(value);
                    else if (property is AssetPropertyFloat f) f.Value = (float)Number(value);
                    else if (property is AssetPropertyDoubleArray4d a && value is JArray array && array.Count == 4)
                        a.SetValueAsDoubles(array.Select(Number).ToList());
                    else throw new ArgumentException("Unsupported appearance property type or value at " + row.Value<string>("path") + ".");
                }
                scope.Commit(true);
            }
        }

        internal static bool Verify(Document doc, Material material, JArray fields, out string error)
        {
            error = null;
            if (fields == null) return true;
            var element = doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;
            if (element == null) { error = "Appearance asset is missing."; return false; }
            using (Asset asset = element.GetRenderingAsset())
                foreach (JObject row in fields)
                {
                    JToken actual = Value(Resolve(asset, row.Value<string>("path")));
                    if (!Equivalent(row["value"], actual)) { error = "Appearance property did not persist: " + row.Value<string>("path"); return false; }
                }
            return true;
        }

        internal static JObject Read(Document doc, Material material)
        {
            var element = doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;
            var rows = new JArray();
            if (element != null) using (Asset asset = element.GetRenderingAsset()) ReadAsset(asset, "", rows, 0);
            return new JObject { ["material_id"] = Rid.Value(material.Id), ["appearance_asset_id"] = element == null ? null : (JToken)Rid.Value(element.Id),
                ["properties"] = rows, ["limit"] = 500, ["path_format"] = "property/connected-property; existing connected assets only",
                ["numeric_units"] = "Rendering schema native values; no geometric-unit conversion." };
        }

        private static void ReadAsset(Asset asset, string prefix, JArray rows, int depth)
        {
            if (asset == null || depth > 4) return;
            for (int i = 0; i < asset.Size && rows.Count < 500; i++)
            {
                AssetProperty p = asset[i];
                string path = prefix + p.Name;
                JToken value = Value(p);
                rows.Add(new JObject { ["path"] = path, ["api_type"] = p.GetType().Name, ["value"] = value,
                    ["supported_value_type"] = value != null && value.Type != JTokenType.Null });
                if (p.NumberOfConnectedProperties == 1) ReadAsset(p.GetSingleConnectedAsset(), path + "/", rows, depth + 1);
            }
        }
        private static AssetProperty Resolve(Asset asset, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Appearance property path is required.");
            string[] parts = path.Split('/');
            if (parts.Length > 5) throw new ArgumentException("Appearance property path exceeds five components.");
            AssetProperty p = null;
            for (int i = 0; i < parts.Length; i++)
            {
                p = asset?.FindByName(parts[i]);
                if (p == null) throw new ArgumentException("Appearance property does not exist: " + path);
                if (i < parts.Length - 1) asset = p.GetSingleConnectedAsset();
            }
            return p;
        }
        private static JToken Value(AssetProperty p)
        {
            if (p is AssetPropertyString s) return s.Value;
            if (p is AssetPropertyBoolean b) return b.Value;
            if (p is AssetPropertyInteger i) return i.Value;
            if (p is AssetPropertyDouble d) return d.Value;
            if (p is AssetPropertyFloat f) return f.Value;
            if (p is AssetPropertyDoubleArray4d a) return new JArray(a.GetValueAsDoubles());
            return JValue.CreateNull();
        }
        private static double Number(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) throw new ArgumentException("Expected a number.");
            double n = (double)value;
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException("Expected a finite number.");
            return n;
        }
        private static bool Equivalent(JToken wanted, JToken actual)
        {
            if (wanted is JArray wa && actual is JArray aa) return wa.Count == aa.Count && wa.Select((v, i) => Equivalent(v, aa[i])).All(x => x);
            if (wanted != null && actual != null && (wanted.Type == JTokenType.Float || wanted.Type == JTokenType.Integer) &&
                (actual.Type == JTokenType.Float || actual.Type == JTokenType.Integer)) return Math.Abs((double)wanted - (double)actual) < 1e-6;
            return JToken.DeepEquals(wanted, actual);
        }
    }
}
