// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// A JSON Schema validator for exactly the keyword subset the contract's input
// schemas use. It began as the examples test's private helper; it lives in the
// server now because two things must judge arguments with the SAME rules: the
// tests that prove the advertised tools/list copy gives the same verdicts as the
// full contract, and the advice a failed call carries (schema_help). Sharing one
// validator is what keeps that advice from contradicting the proof.
//
// It is ADVISORY. Nothing calls it before dispatch: what a call is validated
// against is ToolInputRules (Horizun.Contracts), the per-command parsers and the
// host tools' own refusals, all reading the full contract. Changing this file
// never changes what is accepted or rejected.
//
// Unknown keywords are ERRORS, never skipped. `format` is an annotation here, as it
// is by default in JSON Schema 2020-12; `default` and `description` are annotations.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ContractSchemaCheck
    {
        // The patterns are the contract's, the strings are a caller's: bound each match
        // so a pathological input can never hold the advice path (it runs after a
        // failure, and a timeout there surfaces as "no advice", never as a hang).
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

        /// <summary>Validates <paramref name="value"/> against <paramref name="schema"/>
        /// and returns "pointer: message" strings; empty when it passes.</summary>
        internal static List<string> Validate(JToken value, JToken schema)
        {
            var errors = new List<string>();
            Validate(value, schema, "", errors);
            return errors;
        }

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "enum", "const", "properties", "required", "additionalProperties", "items",
            "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern",
            "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minProperties", "maxProperties",
            "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
            "format", "default", "description", "title", "examples", "$comment"
        };

        internal static IEnumerable<string> UnknownKeywords(JToken schema)
        {
            if (!(schema is JObject s)) yield break;
            foreach (JProperty p in s.Properties())
            {
                if (!Known.Contains(p.Name)) yield return p.Name;
                if (p.Name == "properties" && p.Value is JObject props)
                {
                    foreach (JProperty child in props.Properties())
                        foreach (string k in UnknownKeywords(child.Value)) yield return k;
                }
                else if (p.Name == "items" || p.Name == "additionalProperties" || p.Name == "not" ||
                         p.Name == "if" || p.Name == "then" || p.Name == "else")
                {
                    foreach (string k in UnknownKeywords(p.Value)) yield return k;
                }
                else if ((p.Name == "allOf" || p.Name == "anyOf" || p.Name == "oneOf") && p.Value is JArray arr)
                {
                    foreach (JToken branch in arr)
                        foreach (string k in UnknownKeywords(branch)) yield return k;
                }
            }
        }

        internal static void Validate(JToken value, JToken schemaToken, string pointer, List<string> errors)
        {
            if (schemaToken is JValue b && b.Type == JTokenType.Boolean)
            {
                if (!(bool)b) errors.Add(At(pointer) + "is not allowed here");
                return;
            }
            if (!(schemaToken is JObject schema)) { errors.Add(At(pointer) + "schema is not an object"); return; }
            foreach (JProperty p in schema.Properties())
                if (!Known.Contains(p.Name)) errors.Add(At(pointer) + "the validator does not understand keyword '" + p.Name + "'");

            if (schema["type"] != null)
            {
                IEnumerable<string> types = schema["type"] is JArray ta ? ta.Select(t => (string)t) : new[] { (string)schema["type"] };
                if (!types.Any(t => HasType(value, t)))
                {
                    errors.Add(At(pointer) + "must be " + schema["type"].ToString(Formatting.None) + ", got " + value.Type);
                    return;
                }
            }
            if (schema["const"] != null && !JToken.DeepEquals(schema["const"], value))
                errors.Add(At(pointer) + "must be " + schema["const"].ToString(Formatting.None));
            if (schema["enum"] is JArray allowed && !allowed.Any(a => JToken.DeepEquals(a, value)))
                errors.Add(At(pointer) + "must be one of " + allowed.ToString(Formatting.None) + ", got " + value.ToString(Formatting.None));

            switch (value.Type)
            {
                case JTokenType.String:
                    string s = (string)value;
                    if (schema["minLength"] != null && s.Length < (int)schema["minLength"]) errors.Add(At(pointer) + "is shorter than minLength");
                    if (schema["maxLength"] != null && s.Length > (int)schema["maxLength"]) errors.Add(At(pointer) + "is longer than maxLength");
                    if (schema["pattern"] != null && !Regex.IsMatch(s, (string)schema["pattern"], RegexOptions.None, PatternTimeout))
                        errors.Add(At(pointer) + "'" + s + "' does not match " + schema["pattern"]);
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    double d = value.Value<double>();
                    if (schema["minimum"] != null && d < schema["minimum"].Value<double>()) errors.Add(At(pointer) + "is below minimum");
                    if (schema["maximum"] != null && d > schema["maximum"].Value<double>()) errors.Add(At(pointer) + "is above maximum");
                    if (schema["exclusiveMinimum"] != null && d <= schema["exclusiveMinimum"].Value<double>()) errors.Add(At(pointer) + "is not above exclusiveMinimum");
                    if (schema["exclusiveMaximum"] != null && d >= schema["exclusiveMaximum"].Value<double>()) errors.Add(At(pointer) + "is not below exclusiveMaximum");
                    break;
                case JTokenType.Array:
                    var arr = (JArray)value;
                    if (schema["minItems"] != null && arr.Count < (int)schema["minItems"]) errors.Add(At(pointer) + "has fewer than minItems");
                    if (schema["maxItems"] != null && arr.Count > (int)schema["maxItems"]) errors.Add(At(pointer) + "has more than maxItems");
                    if (schema["uniqueItems"] != null && (bool)schema["uniqueItems"])
                        for (int i = 0; i < arr.Count; i++)
                            for (int j = i + 1; j < arr.Count; j++)
                                if (JToken.DeepEquals(arr[i], arr[j])) errors.Add(At(pointer) + "items " + i + " and " + j + " are equal");
                    if (schema["items"] != null)
                        for (int i = 0; i < arr.Count; i++) Validate(arr[i], schema["items"], pointer + "/" + i, errors);
                    break;
                case JTokenType.Object:
                    var obj = (JObject)value;
                    if (schema["minProperties"] != null && obj.Count < (int)schema["minProperties"]) errors.Add(At(pointer) + "has fewer than minProperties");
                    if (schema["maxProperties"] != null && obj.Count > (int)schema["maxProperties"]) errors.Add(At(pointer) + "has more than maxProperties");
                    if (schema["required"] is JArray required)
                        foreach (JToken r in required)
                            if (obj[(string)r] == null) errors.Add(At(pointer) + "'" + (string)r + "' is required");
                    var props = schema["properties"] as JObject;
                    foreach (JProperty p in obj.Properties())
                    {
                        string child = pointer + "/" + p.Name;
                        if (props?[p.Name] != null) Validate(p.Value, props[p.Name], child, errors);
                        else if (schema["additionalProperties"] != null) Validate(p.Value, schema["additionalProperties"], child, errors);
                    }
                    break;
            }

            if (schema["allOf"] is JArray all)
                foreach (JToken branch in all) Validate(value, branch, pointer, errors);
            if (schema["anyOf"] is JArray any && !any.Any(branch => Passes(value, branch, pointer)))
                errors.Add(At(pointer) + "matches none of anyOf: " + Diagnose(value, any, pointer));
            if (schema["oneOf"] is JArray one)
            {
                int matched = one.Count(branch => Passes(value, branch, pointer));
                if (matched != 1)
                    errors.Add(At(pointer) + "matches " + matched + " branches of oneOf (exactly one required)" +
                               (matched == 0 ? ": " + Diagnose(value, one, pointer) : ""));
            }
            if (schema["not"] != null && Passes(value, schema["not"], pointer))
                errors.Add(At(pointer) + "matches a schema it must not match");
            if (schema["if"] != null)
            {
                if (Passes(value, schema["if"], pointer)) { if (schema["then"] != null) Validate(value, schema["then"], pointer, errors); }
                else if (schema["else"] != null) Validate(value, schema["else"], pointer, errors);
            }
        }

        private static bool Passes(JToken value, JToken schema, string pointer)
        {
            var errors = new List<string>();
            Validate(value, schema, pointer, errors);
            return errors.Count == 0;
        }

        /// <summary>The errors of the closest branch, so a failing oneOf says WHY.</summary>
        private static string Diagnose(JToken value, JArray branches, string pointer)
        {
            List<string> best = null;
            foreach (JToken branch in branches)
            {
                var errors = new List<string>();
                Validate(value, branch, pointer, errors);
                if (best == null || errors.Count < best.Count) best = errors;
            }
            return best == null ? "" : string.Join("; ", best.Take(5));
        }

        private static bool HasType(JToken v, string type)
        {
            switch (type)
            {
                case "object": return v.Type == JTokenType.Object;
                case "array": return v.Type == JTokenType.Array;
                case "string": return v.Type == JTokenType.String;
                case "boolean": return v.Type == JTokenType.Boolean;
                case "null": return v.Type == JTokenType.Null;
                case "number": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case "integer":
                    return v.Type == JTokenType.Integer ||
                           (v.Type == JTokenType.Float && Math.Floor(v.Value<double>()) == v.Value<double>());
                default: return false;
            }
        }

        private static string At(string pointer) => (pointer.Length == 0 ? "/" : pointer) + ": ";
    }
}
