// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Comité de obra 2026-10-01: horizun_export format=nwc left 11 rebar modified in a
// structural model. An export is output: the exporters that commit run inside a
// rolled-back group, and the reply names what the exporter did and what is left.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ExportIsolationRulesTests
    {
        private static ExportIsolationRules.Facts Facts(bool started = true, string status = "RolledBack",
                                                        int modified = 0, int residual = 0, string error = null)
            => new ExportIsolationRules.Facts
            {
                GroupStarted = started, RollbackStatus = started ? status : null, RollbackError = error,
                Modified = modified, ResidualModified = residual,
                UnavailableReason = started ? null : "the document is read-only"
            };

        [Theory]
        [InlineData("nwc", true)]
        [InlineData("ifc", true)]
        [InlineData("pdf", false)]
        [InlineData("dwg", false)]
        [InlineData("fbx", false)]
        public void Only_the_exporters_that_commit_are_isolated(string format, bool isolated)
            => Assert.Equal(isolated, ExportIsolationRules.Isolates(format));

        [Fact]
        public void The_measured_case_eleven_rebar_rolled_back_is_reported_and_leaves_the_model_clean()
        {
            var f = Facts(modified: 11);
            f.Transactions.Add("Regenerate");
            Assert.Equal(ExportIsolationRules.RolledBack, ExportIsolationRules.Status(f));
            Assert.False(ExportIsolationRules.ModelLeftModified(f));
            var report = ExportIsolationRules.Report("nwc", f);
            Assert.Equal(11, (int)report["exporter_changes"]["modified"]);
            Assert.Equal(0, (int)report["after_rollback"]["modified"]);
            Assert.False((bool)report["model_left_modified"]);
            string headline = ExportIsolationRules.Headline("nwc", f);
            Assert.Contains("11 element(s)", headline);
            Assert.Contains("rolled back", headline);
        }

        [Fact]
        public void An_exporter_that_changed_nothing_says_nothing()
        {
            var f = Facts();
            Assert.Equal(ExportIsolationRules.NoModelChange, ExportIsolationRules.Status(f));
            Assert.Null(ExportIsolationRules.Headline("nwc", f));
        }

        [Fact]
        public void What_is_still_changed_after_the_rollback_wins_over_the_rollback_status()
        {
            var f = Facts(modified: 11, residual: 3);
            Assert.Equal(ExportIsolationRules.RollbackFailed, ExportIsolationRules.Status(f));
            Assert.True(ExportIsolationRules.ModelLeftModified(f));
            Assert.StartsWith("THE MODEL WAS LEFT MODIFIED", ExportIsolationRules.Headline("nwc", f));
        }

        [Theory]
        [InlineData("Started", null)]
        [InlineData("RolledBack", "group cannot roll back over an open transaction")]
        public void A_rollback_that_did_not_report_RolledBack_is_not_a_clean_model(string status, string error)
        {
            var f = Facts(status: status, modified: 4, error: error);
            Assert.Equal(ExportIsolationRules.RollbackFailed, ExportIsolationRules.Status(f));
            Assert.True(ExportIsolationRules.ModelLeftModified(f));
        }

        [Fact]
        public void A_group_that_could_not_open_names_why_and_says_the_model_changed()
        {
            var f = Facts(started: false, modified: 2, residual: 2);
            Assert.Equal(ExportIsolationRules.UnavailableModified, ExportIsolationRules.Status(f));
            var report = ExportIsolationRules.Report("ifc", f);
            Assert.Equal("the document is read-only", (string)report["unavailable_reason"]);
            Assert.Contains("could not be isolated", ExportIsolationRules.Headline("ifc", f));
            Assert.Equal(ExportIsolationRules.UnavailableUnchanged, ExportIsolationRules.Status(Facts(started: false)));
        }

        private static string Source(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void The_export_opens_the_group_before_the_exporter_and_closes_it_before_judging_the_files()
        {
            string s = Source("ExportCommand.cs");
            int begin = s.IndexOf("ExportIsolation.Begin(app, doc, format)", StringComparison.Ordinal);
            int nwc = s.IndexOf("doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), nwc)", StringComparison.Ordinal);
            int ifc = s.IndexOf("doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), ifc)", StringComparison.Ordinal);
            int end = s.IndexOf("isolation.End()", StringComparison.Ordinal);
            int after = s.IndexOf("var after = Snapshot(", StringComparison.Ordinal);
            Assert.True(begin > 0 && nwc > begin && ifc > begin && end > nwc && end > ifc && after > end,
                "the exporters run inside the isolation group, and it is rolled back before success is judged");
            // A failed export must still roll the group back: no early return between the two.
            Assert.DoesNotContain("catch (Exception ex) { return CommandResult.FailWithDetail(\"Revit export failed", s);
            Assert.Contains("[\"model_isolation\"] = isolationReport", s);
        }

        [Fact]
        public void The_isolation_rolls_back_and_recounts_against_the_model()
        {
            string s = Source("ExportIsolation.cs");
            int rollback = s.IndexOf("_group.RollBack()", StringComparison.Ordinal);
            int settle = s.IndexOf("_watch?.Settle()", StringComparison.Ordinal);
            Assert.True(rollback > 0 && settle > rollback, "residual changes are measured after the rollback");
            Assert.DoesNotContain(".Assimilate()", s);
            Assert.DoesNotContain(".Commit()", s);
        }
    }
}
