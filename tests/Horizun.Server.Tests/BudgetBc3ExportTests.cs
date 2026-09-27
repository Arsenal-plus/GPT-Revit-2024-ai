// Horizun MCP server - original Horizun code.
// horizun_budget_compare operation=export_bc3, end to end against a disposable folder.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class BudgetBc3ExportTests : IDisposable
    {
        private readonly string _dir, _settingsRoot, _savedRoot;
        private readonly DurableCommandLedger _ledger;

        public BudgetBc3ExportTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-bc3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            // A profile of this suite's own (see BudgetCompareTests): writing a file needs full_write.
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-bc3-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
            File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""full_write""}");
            _ledger = new DurableCommandLedger(() => Path.Combine(_settingsRoot, "ledger"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private static JObject Row(string id, string code, double? volume, string state = "measured") => new JObject
        {
            ["element_id"] = id, ["document"] = "Host.rvt", ["classification_code"] = code,
            ["quantities"] = new JObject
            {
                ["volume"] = new JObject { ["value"] = volume.HasValue ? (JToken)volume.Value : JValue.CreateNull(), ["state"] = state, ["unit"] = "m3" }
            }
        };

        private static JArray Apu(params object[][] rows) =>
            new JArray(rows.Select(r => new JObject { ["code"] = (string)r[0], ["unit"] = (string)r[1], ["unit_price"] = JToken.FromObject(r[2]), ["description"] = (string)r[3] }));

        private JObject Export(JArray rows, JArray apu, string name = "out.bc3", JObject extra = null, string key = null)
        {
            var args = new JObject { ["operation"] = "export_bc3", ["model_rows"] = rows, ["apu"] = apu, ["bc3_path"] = Path.Combine(_dir, name),
                                     ["idempotency_key"] = key ?? Guid.NewGuid().ToString("N") };
            if (extra != null) foreach (var p in extra.Properties()) args[p.Name] = p.Value;
            return BudgetCompare.Handle(args, _ledger, null, CancellationToken.None);
        }

        [Fact]
        public void Export_writes_a_verified_bc3_whose_totals_match_the_takeoff()
        {
            var rows = new JArray(Row("1", "E05", 1.5), Row("2", "E05", 2.25), Row("3", "E08", 4), Row("4", "", 9));
            JObject r = Export(rows, Apu(new object[] { "E05", "m3", 100.5, "Hormigón €" }, new object[] { "E08", "m3", 20, null }, new object[] { "E99", "m3", 1, "sin uso" }));
            Assert.True((bool)r["verified"]);
            byte[] bytes = File.ReadAllBytes((string)r["file_path"]);
            Assert.Contains(bytes, b => b == 0x80);          // the euro sign as its windows-1252 byte
            Assert.Contains(bytes, b => b == 0xF3);          // 'ó' as one byte
            Assert.DoesNotContain(bytes, b => b == 0xC3);    // and no UTF-8 lead byte for it
            string text = Bc3Rules.Decode1252(bytes);
            Assert.Contains("~D|HZ_TAKEOFF##|E05\\1\\3.75\\E08\\1\\4\\|", text);
            Assert.Contains("~M|HZ_TAKEOFF##\\E05|", text);
            Assert.Equal(Math.Round(3.75 * 100.5 + 4 * 20, 2), (double)r["total_amount"]);
            Assert.Equal(new[] { "E99" }, r["apu_codes_unused"].Select(t => (string)t));
            Assert.Equal(1, (int)r["not_exported"]["unclassified_elements"]);
            Assert.Equal(2, (int)r["records"]["M"]);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Fact]
        public void A_code_without_a_price_refuses_the_whole_export_and_writes_nothing()
        {
            var rows = new JArray(Row("1", "E05", 1), Row("2", "E07", 2));
            var ex = Assert.Throws<ToolRefusal>(() => Export(rows, Apu(new object[] { "E05", "m3", 10, null })));
            Assert.Contains("E07", ex.Message);
            Assert.Contains("never invented", ex.Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void Partial_coverage_and_unit_mismatch_are_refused_by_code()
        {
            var partial = new JArray(Row("1", "E05", 1), Row("2", "E05", null, "absent"));
            Assert.Contains("E05", Assert.Throws<ToolRefusal>(() => Export(partial, Apu(new object[] { "E05", "m3", 10, null }))).Message);
            var rows = new JArray(Row("1", "E05", 1));
            Assert.Contains("E05", Assert.Throws<ToolRefusal>(() => Export(rows, Apu(new object[] { "E05", "m2", 10, null }))).Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void A_declared_conversion_is_applied_to_every_element_line()
        {
            var rows = new JArray(Row("1", "E05", 1), Row("2", "E05", 2));
            var mapping = new JObject { ["unit_conversions"] = new JArray(new JObject { ["from"] = "m3", ["to"] = "l", ["factor"] = 1000 }) };
            JObject r = Export(rows, Apu(new object[] { "E05", "l", 0.25, null }), extra: new JObject { ["mapping"] = mapping });
            Assert.True((bool)r["verified"]);
            Assert.Equal(3000.0, (double)r["lines"][0]["quantity"]);
            Assert.Contains("\\id 2\\2000\\", Bc3Rules.Decode1252(File.ReadAllBytes((string)r["file_path"])));
        }

        [Fact]
        public void Text_the_format_cannot_carry_is_refused_before_anything_is_written()
        {
            var rows = new JArray(Row("1", "E05", 1));
            Assert.Contains("separator", Assert.Throws<ToolRefusal>(() => Export(rows, Apu(new object[] { "E05", "m3", 1, "a|b" }))).Message);
            Assert.Contains("windows-1252", Assert.Throws<ToolRefusal>(() => Export(rows, Apu(new object[] { "E05", "m3", 1, "梁" }))).Message);
            Assert.Contains("'#'", Assert.Throws<ToolRefusal>(() => Export(rows, Apu(new object[] { "CAP#", "m3", 1, null }))).Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void Arguments_are_checked_before_anything_is_written()
        {
            var rows = new JArray(Row("1", "E05", 1));
            var apu = Apu(new object[] { "E05", "m3", 1, null });
            File.WriteAllText(Path.Combine(_dir, "exists.bc3"), "x");
            Assert.Contains("never overwritten", Assert.Throws<ToolRefusal>(() => Export(rows, apu, "exists.bc3")).Message);
            Assert.Contains(".bc3", Assert.Throws<ToolRefusal>(() => Export(rows, apu, "out.txt")).Message);
            Assert.Contains("not applicable", Assert.Throws<ToolRefusal>(() => Export(rows, apu, extra: new JObject { ["outputs"] = new JObject() })).Message);
            Assert.Contains("twice", Assert.Throws<ToolRefusal>(() => Export(rows, Apu(new object[] { "E05", "m3", 1, null }, new object[] { "E05", "m3", 2, null }))).Message);
            var noPrice = new JArray(new JObject { ["code"] = "E05", ["unit"] = "m3" });
            Assert.Contains("unit_price", Assert.Throws<ToolRefusal>(() => Export(rows, noPrice)).Message);
            Assert.Contains("operation", Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(new JObject { ["operation"] = "nope" }, CancellationToken.None)).Message);
            Assert.Equal(new[] { "exists.bc3" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        }

        [Fact]
        public void The_profile_gates_the_file_write()
        {
            File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""read_only""}");
            var ex = Assert.Throws<ToolRefusal>(() => Export(new JArray(Row("1", "E05", 1)), Apu(new object[] { "E05", "m3", 1, null })));
            Assert.Contains("profile", ex.Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void Export_is_once_per_key_a_retry_replays_after_rehashing_and_a_missing_key_is_refused()
        {
            var rows = new JArray(Row("1", "E05", 1.5));
            JArray apu = Apu(new object[] { "E05", "m3", 100, "x" });
            string key = "k-" + Guid.NewGuid().ToString("N");
            JObject first = Export(rows, apu, "once.bc3", key: key);
            JObject again = Export(rows, apu, "once.bc3", key: key);
            Assert.True((bool)again["replayed"]);
            Assert.Equal((string)first["sha256"], (string)again["sha256"]);
            Assert.Equal(new[] { "once.bc3" }, Directory.GetFiles(_dir).Select(Path.GetFileName));

            // The recorded answer no longer describes the disk: refused, nothing rewritten.
            File.Delete(Path.Combine(_dir, "once.bc3"));
            Assert.Contains("no longer exists", Assert.Throws<ToolRefusal>(() => Export(rows, apu, "once.bc3", key: key)).Message);
            Assert.Empty(Directory.GetFiles(_dir));

            var noKey = new JObject { ["operation"] = "export_bc3", ["model_rows"] = rows, ["apu"] = apu, ["bc3_path"] = Path.Combine(_dir, "nokey.bc3") };
            Assert.Contains("idempotency_key is required", Assert.Throws<ToolRefusal>(() => BudgetCompare.Handle(noKey, _ledger, null, CancellationToken.None)).Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }
    }
}
