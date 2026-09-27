// Horizun Revit MCP - original Horizun code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    // PowerShell variable names are case-insensitive: in a live probe that keeps its tool
    // name in `$C = 'horizun_...'`, a later `$c = & $Ctx.Apply ...` REPLACES the tool name
    // with the reply. MEASURED 2026-09-26 in the five-year matrix: the source_path case of
    // copy-between-documents reported its tool as System.Collections.Hashtable, the harness
    // counted it as a probe naming an unpublished tool, and every year closed with one fail
    // that no row carried (2027's report refused to write). This scan keeps any variable that
    // holds a tool name from being reassigned under another spelling.
    public sealed class ProbeVariableCaseTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }

        [Fact]
        public void A_variable_holding_a_tool_name_is_never_reassigned_in_a_live_probe()
        {
            var assign = new Regex(@"^\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
            string probes = Path.Combine(RepoRoot(), "scripts", "live-probes");
            var hits = new List<string>();
            foreach (string file in Directory.GetFiles(probes, "*.ps1"))
            {
                var rows = File.ReadAllLines(file)
                    .Select((line, i) => new { i, m = assign.Match(line) })
                    .Where(x => x.m.Success)
                    .Select(x => new { x.i, name = x.m.Groups[1].Value, rhs = x.m.Groups[2].Value.TrimStart() })
                    .ToList();
                var toolVars = new HashSet<string>(
                    rows.Where(r => r.rhs.StartsWith("'horizun_", StringComparison.Ordinal)).Select(r => r.name),
                    StringComparer.OrdinalIgnoreCase);
                hits.AddRange(rows
                    .Where(r => toolVars.Contains(r.name) && !r.rhs.StartsWith("'horizun_", StringComparison.Ordinal))
                    .Select(r => Path.GetFileName(file) + ":" + (r.i + 1) + " $" + r.name));
            }
            Assert.True(hits.Count == 0, "a tool-name variable is overwritten (PowerShell names ignore case): " + string.Join(", ", hits));
        }

        // Every module in one verify-live run shares the run id, so an idempotency key built as
        // ($run + '-xx-' + name) is unique only while no other module uses the same '-xx-'.
        // MEASURED 2026-09-27 in the matrix: quantities-rooms and rm-analysis both used '-rm-',
        // rm-analysis's own level reused quantities-rooms' key, the bridge refused it as a
        // different operation, and three cases went unmeasured.
        [Fact]
        public void No_two_live_probe_modules_share_an_idempotency_key_prefix()
        {
            var prefix = new Regex(@"\$run \+ '-([A-Za-z0-9]+)-");
            string probes = Path.Combine(RepoRoot(), "scripts", "live-probes");
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(probes, "*.probes.ps1"))
            {
                string module = Path.GetFileName(file);
                foreach (Match m in prefix.Matches(File.ReadAllText(file)))
                {
                    if (!owners.TryGetValue(m.Groups[1].Value, out HashSet<string> set)) owners[m.Groups[1].Value] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(module);
                }
            }
            Assert.NotEmpty(owners);
            var shared = owners.Where(kv => kv.Value.Count > 1).Select(kv => "'-" + kv.Key + "-': " + string.Join(" and ", kv.Value.OrderBy(v => v, StringComparer.Ordinal))).ToList();
            Assert.True(shared.Count == 0, "idempotency key prefixes shared across probe modules: " + string.Join("; ", shared));
        }
    }
}
