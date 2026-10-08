using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class CapabilityRegistry
    {
        private static readonly Dictionary<string, JObject> Observed = new Dictionary<string, JObject>(StringComparer.Ordinal);

        internal static void Record(UIApplication app, string tool, string operation, bool verified)
        {
            if (!verified) return;
            Observed[tool + ":" + operation] = new JObject
            {
                ["status"] = "live_verified_scenario", ["revit_version"] = app.Application.VersionNumber,
                ["revit_build"] = app.Application.VersionBuild, ["plugin_commit"] = Build.Commit,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["scope"] = "One successful committed call in this session. Does not prove all inputs, templates or languages."
            };
        }

        internal static JObject Read(UIApplication app)
        {
            var rows = new JArray();
            foreach (CommandContract tool in Contract.All)
            {
                var selectors = new List<KeyValuePair<string, string>>();
                Selectors(tool.InputSchema, "$", selectors);
                if (selectors.Count == 0) selectors.Add(new KeyValuePair<string, string>("$", "default"));
                foreach (var selector in selectors)
                {
                    string op = selector.Value;
                    Observed.TryGetValue(tool.Name + ":" + op, out JObject evidence);
                    var row = new JObject
                    {
                        ["tool"] = tool.Name, ["selector"] = selector.Key, ["operation_or_variant"] = op,
                        ["implementation"] = "declared_in_contract", ["verification"] = evidence?.DeepClone() ?? new JObject { ["status"] = "not_live_verified_in_this_session" },
                        ["contract_resource"] = "horizun://contract/tools/" + tool.Name,
                        ["minimum_build"] = tool.Name == "horizun_revit2024" ? "24.0.4.427 (2024 RTM target)" : null
                    };
                    if (tool.Name == "horizun_revit2024")
                    {
                        string apiType = ApiType(op);
                        bool available = apiType == null || typeof(Element).Assembly.GetType(apiType) != null;
                        row["api_type"] = apiType; row["api_type_present"] = available;
                        row["status"] = available ? "implemented_unverified_for_general_inputs" : "unavailable_api";
                        row["document_kind"] = "project";
                        row["verification_mechanism"] = "rehearsal + transaction/group + postcommit property checklist";
                        row["dependencies"] = op.StartsWith("fabrication_", StringComparison.Ordinal)
                            ? "Configured and loaded Fabrication database/service; compatible buttons/conditions and design elements."
                            : op.StartsWith("family_", StringComparison.Ordinal) ? "Loaded family symbol with matching placement type."
                            : op.StartsWith("analytical_", StringComparison.Ordinal) ? "Valid analytical geometry; loads require a load case and compatible host."
                            : "Compatible document, views, types and editable elements; see operation schema.";
                    }
                    rows.Add(row);
                }
            }
            return new JObject
            {
                ["revit_version"] = app.Application.VersionNumber, ["revit_build"] = app.Application.VersionBuild,
                ["api_file_version"] = FileVersionInfo.GetVersionInfo(typeof(Element).Assembly.Location).FileVersion,
                ["contract_hash"] = Contract.Hash, ["plugin_commit"] = Build.Commit,
                ["tools"] = Contract.All.Count, ["entries"] = rows,
                ["meaning"] = "An inventory of declared operations/variants, not a claim to automate every Revit ribbon action. API type presence, dependencies and observed scenarios are separate facts. Historical release reports are not evidence for this loaded build.",
                ["known_public_api_limits"] = new JArray("Not every Revit UI command exposes a synchronous public API.",
                    "Modal interactive commands cannot be treated as committed verified model edits.",
                    "External exporters, Fabrication databases, content and worksharing permissions remain prerequisites.")
            };
        }

        private static void Selectors(JToken token, string path, List<KeyValuePair<string, string>> rows)
        {
            if (token is JObject obj)
                foreach (JProperty p in obj.Properties())
                {
                    if ((p.Name == "operation" || p.Name == "kind" || p.Name == "mode" || p.Name == "placement") && p.Value is JObject schema && schema["enum"] is JArray values)
                        foreach (JToken value in values) rows.Add(new KeyValuePair<string, string>(path + "." + p.Name, (string)value));
                    Selectors(p.Value, path + "." + p.Name, rows);
                }
            else if (token is JArray array)
                for (int i = 0; i < array.Count; i++) Selectors(array[i], path + "[" + i + "]", rows);
        }
        private static string ApiType(string operation)
        {
            if (operation.StartsWith("site_", StringComparison.Ordinal)) return "Autodesk.Revit.DB.Toposolid";
            if (operation.StartsWith("analytical_panel", StringComparison.Ordinal)) return "Autodesk.Revit.DB.Structure.AnalyticalPanel";
            if (operation.StartsWith("analytical_", StringComparison.Ordinal)) return "Autodesk.Revit.DB.Structure.AnalyticalMember";
            if (operation.StartsWith("fabrication_", StringComparison.Ordinal)) return "Autodesk.Revit.DB.FabricationPart";
            if (operation == "wire_create") return "Autodesk.Revit.DB.Electrical.Wire";
            if (operation == "circuit_set_path") return "Autodesk.Revit.DB.Electrical.ElectricalSystem";
            if (operation == "panel_move_slot") return "Autodesk.Revit.DB.Electrical.PanelScheduleView";
            if (operation == "family_adaptive") return "Autodesk.Revit.DB.AdaptiveComponentInstanceUtils";
            if (operation == "family_curve") return "Autodesk.Revit.DB.FamilyInstance";
            if (operation == "appearance_read") return "Autodesk.Revit.DB.Visual.AppearanceAssetEditScope";
            return null;
        }
    }
}
