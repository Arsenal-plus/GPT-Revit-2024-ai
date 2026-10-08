using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class Revit2024InputRules
    {
        private static readonly Dictionary<string, string> Required = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = "", ["appearance_read"] = "material_id",
            ["site_create"] = "type_id level_id loops", ["site_subdivide"] = "host_id loops", ["site_convert"] = "element_id type_id level_id",
            ["analytical_member_create"] = "points", ["analytical_member_edit"] = "element_id points",
            ["analytical_panel_create"] = "points", ["analytical_panel_edit"] = "element_id points",
            ["analytical_associate"] = "element_id physical_element_id",
            ["analytical_point_load"] = "host_id load_case_id point force force_unit moment moment_unit",
            ["analytical_line_load"] = "host_id load_case_id force force_unit moment moment_unit",
            ["analytical_area_load"] = "host_id load_case_id force force_unit",
            ["fabrication_load_service"] = "service_id", ["fabrication_create"] = "service_id palette_index button_index condition_index level_id point",
            ["fabrication_convert"] = "service_id element_ids", ["wire_create"] = "type_id view_id points",
            ["circuit_set_path"] = "element_id points", ["panel_move_slot"] = "view_id row column to_row to_column",
            ["family_curve"] = "type_id level_id points", ["family_adaptive"] = "type_id points"
        };
        public static string Validate(JObject request)
        {
            if (request == null) return "A JSON object is required.";
            string op = request.Value<string>("operation");
            if (op == null || !Required.TryGetValue(op, out string fields)) return "Unknown operation.";
            var required = fields.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string field in required)
                if (request[field] == null || request[field].Type == JTokenType.Null) return op + " requires " + field + ".";
            var allowed = new HashSet<string>(required, StringComparer.Ordinal);
            allowed.UnionWith(new[] { "operation", "target_document", "dry_run", "confirmation_token", "idempotency_key", "transaction_name", "units" });
            if (op == "wire_create") allowed.Add("wiring_type");
            if (op == "family_curve") allowed.Add("structural_type");
            foreach (JProperty field in request.Properties())
                if (!allowed.Contains(field.Name)) return op + " does not use '" + field.Name + "'; refusing instead of ignoring it.";
            foreach (string field in new[] { "point", "force", "moment" })
                if (request[field] != null && !Point(request[field])) return field + " must be three finite numbers.";
            if (request["points"] != null && (!(request["points"] is JArray points) || points.Count < 1 || points.Count > 10000 || !points.All(Point)))
                return "points must contain 1..10000 XYZ triples.";
            if (request["loops"] is JArray loops && (loops.Count == 0 || loops.Count > 100 || loops.Any(l => !(l is JArray a) || a.Count < 3 || a.Count > 10000 || !a.All(Point))))
                return "loops must contain 1..100 polygon boundaries with at least three XYZ vertices each.";
            if (request["loops"] != null && !(request["loops"] is JArray)) return "loops must be an array.";
            return null;
        }
        // MCP clients can serialize the same object's keys in a different order.
        // Scope order must be stable; array order and all edit values remain bound.
        public static string[] BoundFields(JObject request) => request.Properties().Select(p => p.Name)
            .Where(n => n != "dry_run" && n != "confirmation_token" && n != "idempotency_key" && n != "transaction_name")
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        private static bool Point(JToken token) => token is JArray a && a.Count == 3 && a.All(x =>
            (x.Type == JTokenType.Integer || x.Type == JTokenType.Float) && !double.IsNaN((double)x) && !double.IsInfinity((double)x));
    }
}
