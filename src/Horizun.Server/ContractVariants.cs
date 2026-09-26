// -----------------------------------------------------------------------------
// Horizun MCP server - discriminated variants of the FULL contract.
//
// A "site" is a schema node whose oneOf/anyOf branches are each selected by one
// constant of the same field (create_elements elements[].kind, document_session
// operation). It is what lets a model ask for ONE kind's exact schema instead of a
// whole tool's (horizun://contract/tools/{tool}/{variant}) and what lets a failed
// call carry just the branch it violated (SchemaHelp).
//
// Sites are FOUND, not listed: the rule below runs over Contract.All once. A new
// site appears the day the contract grows one, and ContractTemplateTests pins the
// set so that day is a deliberate change. Nothing here is a contract row, so
// Contract.Hash does not move.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;

namespace Horizun.Server
{
    internal sealed class VariantSite
    {
        public string Tool;
        /// <summary>JSON pointer of the node inside input_schema ("" is the root).</summary>
        public string Pointer;
        public string[] Segments;
        public string Combinator;
        public string Discriminator;
        public JObject Node;
        /// <summary>Discriminator values in branch order.</summary>
        public List<string> Values = new List<string>();
        public Dictionary<string, int> BranchIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        public JObject Branch(string value) => (JObject)((JArray)Node[Combinator])[BranchIndex[value]];

        public string BranchPointer(string value) => Pointer + "/" + Combinator + "/" + BranchIndex[value];

        public string Uri(string value) => McpResources.ContractToolPrefix + Tool + "/" + value;

        /// <summary>The fields a call with this value must send: the node's own required
        /// list plus the branch's, in that order.</summary>
        public List<string> Required(string value)
        {
            var names = new List<string>();
            foreach (JToken source in new[] { Node["required"], Branch(value)["required"] })
                if (source is JArray a)
                    foreach (JToken t in a)
                        if (t.Type == JTokenType.String && !names.Contains((string)t)) names.Add((string)t);
            return names;
        }

        /// <summary>The body of horizun://contract/tools/{tool}/{variant}. The branch is the
        /// contract's own object, verbatim: full descriptions, nothing folded.</summary>
        public JObject Describe(string value) => new JObject
        {
            ["tool"] = Tool,
            ["pointer"] = BranchPointer(value),
            ["discriminator"] = Discriminator,
            ["value"] = value,
            ["required"] = new JArray(Required(value)),
            ["schema"] = Branch(value).DeepClone(),
            ["shared_arguments_uri"] = McpResources.ContractToolPrefix + Tool
        };
    }

    internal static class ContractVariants
    {
        private static readonly Lazy<List<VariantSite>> _all = new Lazy<List<VariantSite>>(() =>
        {
            var sites = new List<VariantSite>();
            foreach (CommandContract c in Contract.All)
                if (c.InputSchema is JObject root)
                    Walk(c.Name, root, new List<string>(), sites);
            return sites;
        });

        public static IReadOnlyList<VariantSite> All => _all.Value;

        public static IEnumerable<VariantSite> For(string tool) => All.Where(s => s.Tool == tool);

        private static void Walk(string tool, JObject node, List<string> path, List<VariantSite> sites)
        {
            VariantSite site = Detect(tool, node, path);
            if (site != null) sites.Add(site);
            foreach (JProperty p in node.Properties())
            {
                path.Add(p.Name);
                if (p.Value is JObject o) Walk(tool, o, path, sites);
                else if (p.Value is JArray a)
                    for (int i = 0; i < a.Count; i++)
                        if (a[i] is JObject item)
                        {
                            path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            Walk(tool, item, path, sites);
                            path.RemoveAt(path.Count - 1);
                        }
                path.RemoveAt(path.Count - 1);
            }
        }

        /// <summary>A site: 2+ object branches, one field D whose const selects each branch,
        /// distinct consts, and the node's own properties.D.enum equal to exactly that set.
        /// The enum condition is what makes the value list trustworthy as "every variant".</summary>
        private static VariantSite Detect(string tool, JObject node, List<string> path)
        {
            foreach (string combinator in new[] { "oneOf", "anyOf" })
            {
                if (!(node[combinator] is JArray branches) || branches.Count < 2) continue;
                if (branches.Any(b => !(b is JObject))) continue;
                if (!(branches[0]["properties"] is JObject firstProps)) continue;
                foreach (JProperty candidate in firstProps.Properties())
                {
                    string d = candidate.Name;
                    var values = new List<string>();
                    bool ok = true;
                    foreach (JObject b in branches.Cast<JObject>())
                    {
                        JToken k = b["properties"]?[d]?["const"];
                        if (k == null || k.Type != JTokenType.String || values.Contains((string)k)) { ok = false; break; }
                        values.Add((string)k);
                    }
                    if (!ok) continue;
                    if (!(node["properties"]?[d]?["enum"] is JArray e) || e.Count != values.Count ||
                        e.Any(v => v.Type != JTokenType.String || !values.Contains((string)v)))
                        continue;
                    var site = new VariantSite
                    {
                        Tool = tool,
                        Segments = path.ToArray(),
                        Pointer = string.Concat(path.Select(s => "/" + Escape(s))),
                        Combinator = combinator,
                        Discriminator = d,
                        Node = node
                    };
                    for (int i = 0; i < values.Count; i++) { site.Values.Add(values[i]); site.BranchIndex[values[i]] = i; }
                    return site;
                }
            }
            return null;
        }

        internal static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

        /// <summary>Every instance location a schema pointer governs, walking only
        /// properties/NAME and items (the two steps an argument tree mirrors). A site
        /// under any other keyword yields nothing, which means "no variant advice".</summary>
        internal static IEnumerable<KeyValuePair<string, JToken>> InstancesAt(JToken instance, string[] segments)
            => InstancesAt(instance, segments, 0, "");

        private static IEnumerable<KeyValuePair<string, JToken>> InstancesAt(
            JToken instance, string[] segs, int i, string pointer)
        {
            if (instance == null) yield break;
            if (i >= segs.Length) { yield return new KeyValuePair<string, JToken>(pointer, instance); yield break; }
            if (segs[i] == "properties" && i + 1 < segs.Length)
            {
                if (instance is JObject o && o.TryGetValue(segs[i + 1], out JToken child))
                    foreach (var x in InstancesAt(child, segs, i + 2, pointer + "/" + Escape(segs[i + 1])))
                        yield return x;
            }
            else if (segs[i] == "items" && instance is JArray a)
            {
                for (int j = 0; j < a.Count; j++)
                    foreach (var x in InstancesAt(a[j], segs, i + 1,
                                 pointer + "/" + j.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                        yield return x;
            }
        }
    }
}
