// schema_help is advice attached AFTER the verdict. These facts use the real reply
// builders (McpResult.Structured / Error / Text) and the create_elements rehearsal
// fields (valid, invalid, errors, note), and prove two things: a failed call that
// violates the contract gets the exact branch it violated, and nothing a client
// already parses - isError, the success text, invalid, errors, fallback,
// capability_gaps - changes by a single byte.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Horizun.Contracts;
using Horizun.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SchemaHelpTests
    {
        private const string CreateElements = "horizun_create_elements";

        private static VariantSite Kinds => ContractVariants.For(CreateElements).Single();

        private static JObject Rehearsal(int valid, int invalid)
        {
            var data = new JObject
            {
                ["valid"] = valid,
                ["invalid"] = invalid,
                ["errors"] = new JArray(Enumerable.Range(0, invalid).Select(i =>
                    new JObject { ["index"] = i, ["error"] = "row " + i + " is not buildable" })),
                ["note"] = "dry_run: nothing was written."
            };
            return McpResult.Structured(data, data.ToString(Formatting.None));
        }

        private static JObject Rows(params JObject[] rows) => new JObject { ["elements"] = new JArray(rows) };

        private static JObject Help(JToken attached) => (JObject)attached["structuredContent"]?["schema_help"];

        [Fact]
        public void A_wall_row_missing_its_fields_gets_the_wall_branch_on_an_invalid_rehearsal()
        {
            JObject reply = Rehearsal(0, 1);
            JObject args = Rows(new JObject { ["kind"] = "wall" });
            JToken attached = SchemaHelp.Attach(reply, CreateElements, args);
            JObject help = Help(attached);

            Assert.NotNull(help);
            Assert.Equal("horizun://contract/tools/" + CreateElements, (string)help["contract_uri"]);
            Assert.Contains(help["violations"].Select(v => (string)v), v => v.StartsWith("/elements/0", StringComparison.Ordinal));
            JObject wall = (JObject)help["variants"][0];
            Assert.Equal("wall", (string)wall["value"]);
            Assert.Equal("kind", (string)wall["discriminator"]);
            Assert.Equal("/elements/0", (string)wall["pointer"]);
            Assert.Equal("horizun://contract/tools/" + CreateElements + "/wall", (string)wall["uri"]);
            Assert.True(JToken.DeepEquals(Kinds.Branch("wall"), wall["schema"]));

            // A success keeps its text exactly the payload, and its verdict fields.
            Assert.Equal((string)reply["content"][0]["text"], (string)attached["content"][0]["text"]);
            Assert.False((bool)attached["isError"]);
            foreach (string k in new[] { "valid", "invalid", "errors", "note" })
                Assert.True(JToken.DeepEquals(reply["structuredContent"][k], attached["structuredContent"][k]), k);
            // ...and the caller's object is not written to.
            Assert.Null(reply["structuredContent"]["schema_help"]);
        }

        [Fact]
        public void Nothing_is_attached_when_the_contract_finds_nothing_or_nothing_failed()
        {
            JObject failed = Rehearsal(0, 1);
            Assert.Same(failed, SchemaHelp.Attach(failed, CreateElements, Rows(new JObject { ["kind"] = "wall" }),
                (a, s) => new List<string>()));

            JObject clean = Rehearsal(2, 0);
            Assert.Same(clean, SchemaHelp.Attach(clean, CreateElements, Rows(new JObject { ["kind"] = "wall" })));

            JObject text = McpResult.Text("{\"ok\":true}", false);
            Assert.Same(text, SchemaHelp.Attach(text, CreateElements, Rows(new JObject { ["kind"] = "wall" })));

            JObject task = new JObject { ["task"] = new JObject { ["taskId"] = "t1" }, ["content"] = new JArray() };
            Assert.Same(task, SchemaHelp.Attach(task, CreateElements, Rows(new JObject { ["kind"] = "wall" })));

            JObject unknownTool = McpResult.Text("refused", true);
            Assert.Same(unknownTool, SchemaHelp.Attach(unknownTool, "horizun_no_such_tool", new JObject { ["x"] = 1 }));
        }

        [Fact]
        public void An_error_keeps_every_verdict_byte_and_gains_one_line()
        {
            var fallback = new JObject
            {
                ["recommended_tool"] = "horizun_execute_python", ["allowed"] = false,
                ["reason"] = "invalid_arguments", ["write_started"] = false
            };
            var gaps = new JArray(new JObject { ["index"] = 1, ["kind"] = "no_such_kind" });
            JObject reply = McpResult.Error("create_elements refused: row 0 is missing fields.", fallback, gaps);
            string before = reply.ToString(Formatting.None);

            JToken attached = SchemaHelp.Attach(reply, CreateElements,
                Rows(new JObject { ["kind"] = "wall" }, new JObject { ["kind"] = "no_such_kind" }));

            Assert.Equal(before, reply.ToString(Formatting.None));
            Assert.True((bool)attached["isError"]);
            Assert.True(JToken.DeepEquals(reply["structuredContent"]["fallback"], attached["structuredContent"]["fallback"]));
            Assert.True(JToken.DeepEquals(reply["structuredContent"]["capability_gaps"], attached["structuredContent"]["capability_gaps"]));

            string original = (string)reply["content"][0]["text"];
            string now = (string)attached["content"][0]["text"];
            Assert.StartsWith(original + Environment.NewLine + Environment.NewLine, now);
            string line = now.Substring(original.Length + 2 * Environment.NewLine.Length);
            Assert.DoesNotContain("\n", line);
            Assert.StartsWith("Arguments violate the contract at /", line);
            Assert.Contains("structuredContent.schema_help", line);
            Assert.Contains("horizun://contract/tools/" + CreateElements + "/wall", line);

            JObject unknown = Help(attached)["variants"].OfType<JObject>().Single(v => (string)v["value"] == "no_such_kind");
            Assert.Equal(Kinds.Values, unknown["valid_values"].Select(v => (string)v).ToList());
            Assert.Null(unknown["schema"]);
        }

        [Fact]
        public void An_error_without_structured_content_gets_one()
        {
            JObject reply = McpResult.Text("export refused: unknown format.", true);
            JToken attached = SchemaHelp.Attach(reply, "horizun_export", new JObject { ["format"] = "no_such_format" });
            JObject help = Help(attached);
            Assert.NotNull(help);
            Assert.Equal("horizun://contract/tools/horizun_export", (string)help["contract_uri"]);
            Assert.Null(help["variants"]);
            Assert.Contains(help["violations"].Select(v => (string)v), v => v.StartsWith("/format", StringComparison.Ordinal));
            Assert.Contains("(also horizun://contract/tools/horizun_export)", (string)attached["content"][0]["text"]);
        }

        [Fact]
        public void A_session_save_without_its_target_gets_the_save_variant()
        {
            JObject args = new JObject { ["operation"] = "save" };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Contract.Find("horizun_document_session").InputSchema));
            JToken attached = SchemaHelp.Attach(McpResult.Text("save refused.", true), "horizun_document_session", args);
            JObject save = (JObject)Help(attached)["variants"].Single();
            Assert.Equal("save", (string)save["value"]);
            Assert.Equal("", (string)save["pointer"]);
            VariantSite site = ContractVariants.For("horizun_document_session").Single();
            Assert.True(JToken.DeepEquals(site.Branch("save"), save["schema"]));
        }

        [Fact]
        public void Beyond_three_kinds_only_the_uri_travels_and_the_caps_hold()
        {
            JObject[] rows = Kinds.Values.Take(6).Select(k => new JObject { ["kind"] = k }).ToArray();
            JToken attached = SchemaHelp.Attach(Rehearsal(0, rows.Length), CreateElements, Rows(rows));
            JObject help = Help(attached);
            var variants = help["variants"].OfType<JObject>().ToList();

            Assert.Equal(6, variants.Count);
            Assert.All(variants, v => Assert.NotNull(v["uri"]));
            var inline = variants.Where(v => v["schema"] != null).ToList();
            Assert.InRange(inline.Count, 1, SchemaHelp.MaxInlineSchemas);
            Assert.True(inline.Sum(v => Encoding.UTF8.GetByteCount(v["schema"].ToString(Formatting.None)))
                        <= SchemaHelp.MaxInlineSchemaBytes);
            Assert.True(Encoding.UTF8.GetByteCount(help.ToString(Formatting.None)) <= SchemaHelp.MaxBytes);
        }

        [Fact]
        public void Violations_are_bounded_in_count_length_and_total()
        {
            var many = Enumerable.Range(0, 100).Select(i => "/elements/" + i + ": " + new string('x', 900)).ToList();
            JToken attached = SchemaHelp.Attach(Rehearsal(0, 1), CreateElements, Rows(new JObject { ["kind"] = "wall" }),
                (a, s) => many);
            JObject help = Help(attached);
            var v = help["violations"].Select(x => (string)x).ToList();
            Assert.True(v.Count <= SchemaHelp.MaxViolations + 1);
            Assert.StartsWith("... ", v.Last());
            Assert.All(v, x => Assert.True(x.Length <= SchemaHelp.MaxViolationChars + 3));
            Assert.True(Encoding.UTF8.GetByteCount(help.ToString(Formatting.None)) <= SchemaHelp.MaxBytes);
        }

        [Fact]
        public void A_throwing_validator_leaves_the_result_unchanged()
        {
            JObject reply = Rehearsal(0, 1);
            string before = reply.ToString(Formatting.None);
            JToken attached = SchemaHelp.Attach(reply, CreateElements, Rows(new JObject { ["kind"] = "wall" }),
                (a, s) => throw new InvalidOperationException("validator bug"));
            Assert.Same(reply, attached);
            Assert.Equal(before, reply.ToString(Formatting.None));
        }
    }
}
