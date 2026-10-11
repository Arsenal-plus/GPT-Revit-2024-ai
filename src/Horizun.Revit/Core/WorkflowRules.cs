using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    public static class WorkflowRules
    {
        public static readonly HashSet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_execute_plan", "horizun_create_elements", "horizun_create_family", "horizun_family_apply",
            "horizun_write_params_verified", "horizun_manage_materials", "horizun_transform_elements",
            "horizun_capture_view", "horizun_model_snapshot", "horizun_document_session"
        };
        public static string Validate(JObject r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Value<string>("target_document"))) return "target_document is required.";
            if (!(r["steps"] is JArray steps) || steps.Count == 0 || steps.Count > 20) return "steps must contain 1..20 entries.";
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in steps)
            {
                if (!(token is JObject step)) return "Each step must be an object.";
                string key = step.Value<string>("key"), tool = step.Value<string>("tool");
                if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || !keys.Add(key)) return "Step keys must be nonempty and unique.";
                if (!Tools.Contains(tool ?? "")) return "Unsupported workflow tool: " + tool;
                if (!(step["arguments"] is JObject args)) return "Each step needs an arguments object.";
                var properties = Contract.Find(tool)?.InputSchema?["properties"] as JObject;
                if (properties == null) return "No child contract is available: " + tool;
                foreach (JProperty argument in args.Properties())
                    if (properties[argument.Name] == null) return "Unknown argument for " + tool + ": " + argument.Name;
                if (args["confirmation_token"] != null || args["idempotency_key"] != null || args["dry_run"] != null)
                    return "Child confirmation_token, idempotency_key and dry_run are managed by the workflow.";
                if (tool == "horizun_document_session" && args.Value<string>("operation") != "open")
                    return "Workflow document_session supports open only; closing or saving documents is never implicit.";
                foreach (string reference in PlanReferences.ReferenceKeys(args))
                    if (reference == key || !keys.Contains(reference)) return "A reference must name a previous step.";
            }
            return null;
        }
    }
}
