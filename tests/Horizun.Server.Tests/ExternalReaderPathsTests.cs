// -----------------------------------------------------------------------------
// Horizun MCP server tests - original Horizun code.
//
// SCRIPTS OUTSIDE THE SERVER READ THE ADVERTISED SCHEMAS. The release gate
// (scripts/verify-live.ps1) and the live harness (scripts/live/horizun-live.lib.ps1)
// read enums and property maps straight out of tools/list, and generate-inventory
// used to. Abridging the advertised copy must never move a path they read, so each
// one is resolved here in the published entry and compared with the contract.
// verify-live itself needs a running Revit; this is its offline half.
// -----------------------------------------------------------------------------
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExternalReaderPathsTests
    {
        private static JObject Advertised(string tool) =>
            (JObject)Tools.Publish(Tools.Find(tool), true)["inputSchema"];

        [Theory]
        // scripts/verify-live.ps1, the release gate's shape checks (~1662-1699).
        [InlineData("horizun_create_elements", "properties.elements.items.properties.kind.enum")]
        [InlineData("horizun_manage_views", "properties.actions.items.properties.operation.enum")]
        [InlineData("horizun_export", "properties.format.enum")]
        [InlineData("horizun_create_family", "properties.forms")]
        [InlineData("horizun_create_family", "properties.connectors")]
        [InlineData("horizun_create_family", "properties.parameters")]
        [InlineData("horizun_create_family", "properties.types")]
        [InlineData("horizun_create_family", "properties.reference_planes")]
        [InlineData("horizun_create_family", "properties.dimensions")]
        [InlineData("horizun_create_family", "properties.family_lines")]
        [InlineData("horizun_create_family", "properties.nested_instances")]
        [InlineData("horizun_create_family", "properties.forms.items.properties.kind.enum")]
        [InlineData("horizun_manage_system_types", "properties.actions.items.properties.compound_structure.properties")]
        // scripts/verify-live.ps1 ~6354-6363: the planimetry fix tool's closed schema.
        [InlineData("horizun_fix_planimetry", "properties.actions.items.properties.operation.enum")]
        [InlineData("horizun_fix_planimetry", "additionalProperties")]
        [InlineData("horizun_fix_planimetry", "properties.dry_run.default")]
        public void Reader_path_resolves_and_equals_the_contract(string tool, string path)
        {
            JToken advertised = Advertised(tool).SelectToken(path);
            JToken full = Contract.Find(tool).InputSchema.SelectToken(path);
            Assert.True(full != null, tool + " " + path + " is not in the contract; the reader list is stale");
            Assert.True(advertised != null, tool + " " + path + " no longer resolves in tools/list");
            if (full is JObject fo && advertised is JObject ao)
            {
                // A property map: the reader enumerates names (compound_structure.properties.*,
                // power_bi_push) or reads nested enums, so names and every enum must survive.
                Assert.Equal(string.Join(",", Names(fo)), string.Join(",", Names(ao)));
                foreach (JToken e in fo.SelectTokens("..enum"))
                    Assert.True(JToken.DeepEquals(e, ao.SelectToken(e.Path.Substring(fo.Path.Length).TrimStart('.'))),
                        tool + " " + path + ": " + e.Path + " differs");
            }
            else
                Assert.True(JToken.DeepEquals(full, advertised), tool + " " + path + " differs from the contract");
        }

        [Fact]
        public void Gate_checks_on_the_planimetry_fix_still_hold()
        {
            JObject s = Advertised("horizun_fix_planimetry");
            Assert.Equal(false, (bool)s["additionalProperties"]);
            Assert.NotNull(s.SelectToken("properties.confirmation_token"));
            Assert.NotNull(s.SelectToken("properties.idempotency_key"));
            Assert.Equal(true, (bool)s.SelectToken("properties.dry_run.default"));
        }

        [Fact]
        public void Power_bi_push_property_names_are_the_contract_ones()
        {
            JObject full = (JObject)Contract.Find("horizun_power_bi_push").InputSchema["properties"];
            JObject adv = (JObject)Advertised("horizun_power_bi_push")["properties"];
            Assert.Equal(string.Join(",", Names(full)), string.Join(",", Names(adv)));
        }

        [Fact]
        public void Live_harness_top_level_enums_equal_the_contract()
        {
            // scripts/live/horizun-live.lib.ps1 ~503 reads inputSchema.properties.<p>.enum or
            // .items.enum for whatever tool and property a probe names: check them all.
            foreach (CommandContract c in Contract.All)
            {
                if (!(c.InputSchema?["properties"] is JObject props)) continue;
                JObject adv = (JObject)Advertised(c.Name)["properties"];
                foreach (JProperty p in props.Properties())
                    foreach (string sub in new[] { "enum", "items.enum" })
                    {
                        JToken f = p.Value.SelectToken(sub);
                        if (f == null) continue;
                        Assert.True(JToken.DeepEquals(f, adv[p.Name]?.SelectToken(sub)), c.Name + "." + p.Name + "." + sub + " differs");
                    }
            }
        }

        private static System.Collections.Generic.IEnumerable<string> Names(JObject o)
        {
            foreach (JProperty p in o.Properties()) yield return p.Name;
        }
    }
}
