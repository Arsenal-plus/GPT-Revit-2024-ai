// -----------------------------------------------------------------------------
// Horizun MCP server - schema_help on a failed call.
//
// tools/list advertises an abridged copy of each schema (Tools.CompactSchema). When
// a call fails AND its arguments violate the FULL contract, the reply carries the
// failing JSON pointers and the exact branch of the variant the caller was trying to
// build, so a model corrects itself in one step instead of guessing from a
// shortened description.
//
// ADVICE ONLY. This runs after the verdict: it never turns a success into a failure
// or a failure into a success, never touches the arguments the add-in received, and
// attaches nothing when the contract finds nothing (a semantic refusal needs no
// schema). The validator is ContractSchemaCheck, the same one the example payloads
// are tested with; the real validation still lives where it always did (ToolInputRules
// in the add-in, the per-command parsers, the host-tool refusals).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;

namespace Horizun.Server
{
    internal static class SchemaHelp
    {
        internal const int MaxViolations = 20;
        internal const int MaxViolationChars = 512;
        internal const int MaxInlineSchemas = 3;
        internal const int MaxInlineSchemaBytes = 12288;
        internal const int MaxBytes = 16384;
        private const int MaxVariantEntries = 32;

        /// <summary>The result, with structuredContent.schema_help when the call failed
        /// (isError, or a rehearsal that counted invalid rows) and its arguments violate
        /// the contract. Pure and never throws: any failure returns the result unchanged.</summary>
        internal static JToken Attach(JToken result, string tool, JToken arguments)
            => Attach(result, tool, arguments, ContractSchemaCheck.Validate);

        internal static JToken Attach(JToken result, string tool, JToken arguments,
                                      Func<JToken, JToken, List<string>> validate)
        {
            try { return AttachCore(result, tool, arguments, validate); }
            catch { return result; }
        }

        private static JToken AttachCore(JToken result, string tool, JToken arguments,
                                         Func<JToken, JToken, List<string>> validate)
        {
            // A task handle is not a tool result; the task's own result passes through
            // here when it completes (McpTasks runs CallTool).
            if (!(result is JObject r) || !(r["content"] is JArray) || r["task"] != null) return result;
            bool isError = r["isError"]?.Type == JTokenType.Boolean && (bool)r["isError"];
            JToken sc = r["structuredContent"];
            if (sc != null && sc.Type != JTokenType.Object) return result;
            bool invalidRows = !isError && sc is JObject s && IsPositive(s["invalid"]);
            if (!isError && !invalidRows) return result;
            if (string.IsNullOrEmpty(tool)) return result;
            JToken schema = Contract.Find(tool)?.InputSchema;
            if (schema == null) return result;

            JToken args = arguments == null || arguments.Type == JTokenType.Null ? new JObject() : arguments;
            List<string> violations = validate(args, schema);
            if (violations == null || violations.Count == 0) return result;

            JObject help = Build(tool, args, violations);

            // Copy before writing: the caller's object may be shared (a cached or replayed
            // reply), and a failure below must leave it exactly as it was.
            var copy = (JObject)r.DeepClone();
            var structured = copy["structuredContent"] as JObject;
            if (structured == null) { structured = new JObject(); copy["structuredContent"] = structured; }
            structured["schema_help"] = help;

            // A SUCCESS keeps its text exactly the payload - clients parse it as one JSON
            // document (the same rule McpResult.Structured follows for the fallback block).
            // An error is prose already, so it gains one line pointing at the structure.
            if (isError)
            {
                JObject block = ((JArray)copy["content"]).OfType<JObject>()
                    .FirstOrDefault(b => (string)b["type"] == "text" && b["text"]?.Type == JTokenType.String);
                if (block != null)
                    block["text"] = (string)block["text"] + Environment.NewLine + Environment.NewLine + ErrorLine(help, violations[0]);
            }
            return copy;
        }

        internal static string ErrorLine(JObject help, string firstViolation)
        {
            int colon = firstViolation.IndexOf(": ", StringComparison.Ordinal);
            string pointer = colon > 0 ? firstViolation.Substring(0, colon) : "/";
            string uri = (string)((help["variants"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(v => v["uri"] != null)?["uri"]) ?? (string)help["contract_uri"];
            return "Arguments violate the contract at " + pointer +
                   "; exact schema in structuredContent.schema_help (also " + uri + ").";
        }

        private static bool IsPositive(JToken t)
            => t != null && ((t.Type == JTokenType.Integer && (long)t > 0) ||
                             (t.Type == JTokenType.Float && (double)t > 0));

        private static JObject Build(string tool, JToken args, List<string> violations)
        {
            var help = new JObject
            {
                ["contract_uri"] = McpResources.ContractToolPrefix + tool,
                ["violations"] = Violations(violations, MaxViolations)
            };

            var variants = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int inline = 0, inlineBytes = 0;
            foreach (VariantSite site in ContractVariants.For(tool))
                foreach (KeyValuePair<string, JToken> at in ContractVariants.InstancesAt(args, site.Segments))
                {
                    if (variants.Count >= MaxVariantEntries) break;
                    if (!(at.Value is JObject o) || !o.TryGetValue(site.Discriminator, out JToken d)) continue;
                    if (!seen.Add(site.Pointer + "\u0000" + d.ToString(Formatting.None))) continue;
                    if (d.Type == JTokenType.String && site.BranchIndex.ContainsKey((string)d))
                    {
                        string value = (string)d;
                        var entry = new JObject
                        {
                            ["discriminator"] = site.Discriminator,
                            ["value"] = value,
                            ["uri"] = site.Uri(value),
                            ["pointer"] = at.Key
                        };
                        JObject branch = site.Branch(value);
                        int bytes = Encoding.UTF8.GetByteCount(branch.ToString(Formatting.None));
                        // Beyond three kinds, or 12 KB of branches, the uri is the answer:
                        // one reply must not become a second copy of the contract.
                        if (inline < MaxInlineSchemas && inlineBytes + bytes <= MaxInlineSchemaBytes)
                        {
                            entry["schema"] = branch.DeepClone();
                            inline++;
                            inlineBytes += bytes;
                        }
                        variants.Add(entry);
                    }
                    else
                        variants.Add(new JObject
                        {
                            ["discriminator"] = site.Discriminator,
                            ["value"] = d.DeepClone(),
                            ["pointer"] = at.Key,
                            ["valid_values"] = new JArray(site.Values)
                        });
                }
            if (variants.Count > 0) help["variants"] = variants;

            // The whole block is bounded too. Shed in order of least use: inline schemas
            // (their uri stays), then violations beyond the first, then the variant list.
            int keep = Math.Min(MaxViolations, violations.Count);
            while (Size(help) > MaxBytes)
            {
                JObject withSchema = (help["variants"] as JArray)?.OfType<JObject>().LastOrDefault(v => v["schema"] != null);
                if (withSchema != null) { withSchema.Remove("schema"); continue; }
                if (keep > 1) { keep--; help["violations"] = Violations(violations, keep); continue; }
                if (help["variants"] != null) { help.Remove("variants"); continue; }
                break;
            }
            return help;
        }

        private static JArray Violations(List<string> all, int keep)
        {
            var arr = new JArray();
            foreach (string v in all.Take(keep))
                arr.Add(v.Length <= MaxViolationChars ? v : v.Substring(0, MaxViolationChars) + "...");
            if (all.Count > keep) arr.Add("... " + (all.Count - keep) + " more");
            return arr;
        }

        private static int Size(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));
    }
}
