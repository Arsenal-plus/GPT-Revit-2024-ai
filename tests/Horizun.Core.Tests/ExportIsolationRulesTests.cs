// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Comité de obra 2026-10-01: horizun_export format=nwc replied model_changes.modified
// = 11 (rebar). Measured live that evening: the exporter writes (transaction
// "Navisworks23", 12 added + 12 modified), Document.IsModified is false afterwards,
// the eleven rebar re-read byte-identical - with v2.1.5 and with the rolled-back
// group alike - and DocumentChanged still lists them. Revit's flag is the witness.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ExportIsolationRulesTests
    {
        private static ExportIsolationRules.Facts Facts(bool started = true, string status = "RolledBack",
                                                        int added = 0, int modified = 0, int residual = 0,
                                                        bool? before = false, bool? after = false)
            => new ExportIsolationRules.Facts
            {
                GroupStarted = started, RollbackStatus = started ? status : null,
                Added = added, Modified = modified, ResidualModified = residual,
                ModifiedBefore = before, ModifiedAfter = after,
                UnavailableReason = started ? null : "the document is read-only"
            };

        /// <summary>The case measured live on CO-Mirador-estructura and LNK-Mirador-estructura.</summary>
        private static ExportIsolationRules.Facts Measured()
        {
            var f = Facts(added: 12, modified: 12, residual: 11);
            f.Transactions.Add("Navisworks23");
            return f;
        }

        [Theory]
        [InlineData("nwc", true)]
        [InlineData("ifc", true)]
        [InlineData("pdf", false)]
        [InlineData("dwg", false)]
        [InlineData("fbx", false)]
        public void Only_the_exporters_that_commit_are_isolated(string format, bool isolated)
            => Assert.Equal(isolated, ExportIsolationRules.Isolates(format));

        [Fact]
        public void The_measured_case_is_proven_unchanged_and_the_eleven_rebar_are_named_as_residue_not_change()
        {
            var f = Measured();
            Assert.Equal(ExportIsolationRules.RolledBack, ExportIsolationRules.Status(f));
            Assert.False(ExportIsolationRules.ModelLeftModified(f));
            Assert.Null(ExportIsolationRules.Headline("nwc", f));   // no false alarm on every NWC export
            JObject report = ExportIsolationRules.Report("nwc", f);
            Assert.Equal(12, (int)report["exporter_changes"]["added"]);
            Assert.Equal(11, (int)report["event_residue"]["modified"]);
            Assert.Equal("Document.IsModified before and after", (string)report["proof"]);

            JObject changes = ExportIsolationRules.ProvenModelChanges(f);
            Assert.Equal(0, (int)changes["modified"]);
            Assert.True((bool)changes["proven_unchanged"]);
            Assert.Equal(11, (int)changes["events_listed"]["modified"]);
        }

        [Fact]
        public void A_clean_document_that_comes_back_dirty_is_left_modified_whatever_the_events_say()
        {
            var f = Facts(added: 12, modified: 12, residual: 0, after: true);
            Assert.Equal(ExportIsolationRules.RollbackFailed, ExportIsolationRules.Status(f));
            Assert.True(ExportIsolationRules.ModelLeftModified(f));
            Assert.StartsWith("THE MODEL WAS LEFT MODIFIED", ExportIsolationRules.Headline("nwc", f));
            Assert.Null(ExportIsolationRules.ProvenModelChanges(f));
        }

        [Fact]
        public void Unsaved_work_before_the_export_means_the_flag_cannot_testify()
        {
            var f = Facts(added: 12, modified: 12, residual: 11, before: true, after: true);
            Assert.Equal(ExportIsolationRules.Unverified, ExportIsolationRules.Status(f));
            Assert.Null(ExportIsolationRules.ModelLeftModified(f));
            Assert.Contains("already had unsaved changes", ExportIsolationRules.Headline("nwc", f));
            Assert.Null(ExportIsolationRules.ProvenModelChanges(f));
            Assert.Equal(JTokenType.Null, ExportIsolationRules.Report("nwc", f)["model_left_modified"].Type);
        }

        [Fact]
        public void An_unreadable_flag_is_not_a_clean_document()
        {
            var f = Facts(added: 3, modified: 1, residual: 1, before: null, after: null);
            Assert.Equal(ExportIsolationRules.Unverified, ExportIsolationRules.Status(f));
            Assert.Contains("did not report", ExportIsolationRules.Headline("nwc", f));
        }

        [Fact]
        public void An_exporter_that_wrote_nothing_says_nothing()
        {
            var f = Facts();
            Assert.Equal(ExportIsolationRules.NoModelChange, ExportIsolationRules.Status(f));
            Assert.Null(ExportIsolationRules.Headline("ifc", f));
            Assert.Equal(ExportIsolationRules.NoModelChange, ExportIsolationRules.Status(Facts(before: true, after: true)));
        }

        [Fact]
        public void A_group_that_could_not_open_still_has_the_flag_as_witness()
        {
            var changed = Facts(started: false, modified: 2, residual: 2, after: true);
            Assert.Equal(ExportIsolationRules.UnavailableModified, ExportIsolationRules.Status(changed));
            Assert.Contains("could not be isolated", ExportIsolationRules.Headline("ifc", changed));
            Assert.Equal("the document is read-only", (string)ExportIsolationRules.Report("ifc", changed)["unavailable_reason"]);
            Assert.Equal(ExportIsolationRules.UnavailableUnchanged,
                         ExportIsolationRules.Status(Facts(started: false, modified: 2, residual: 2)));
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "src", "Horizun.Revit" }.Concat(parts).ToArray()));
        }

        [Fact]
        public void The_export_opens_the_group_before_the_exporter_and_closes_it_before_judging_the_files()
        {
            string s = Source("Commands", "ExportCommand.cs");
            int begin = s.IndexOf("ExportIsolation.Begin(app, doc, format)", StringComparison.Ordinal);
            int nwc = s.IndexOf("doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), nwc)", StringComparison.Ordinal);
            int ifc = s.IndexOf("doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), ifc)", StringComparison.Ordinal);
            int end = s.IndexOf("isolation.End()", StringComparison.Ordinal);
            int after = s.IndexOf("var after = Snapshot(", StringComparison.Ordinal);
            Assert.True(begin > 0 && nwc > begin && ifc > begin && end > nwc && end > ifc && after > end,
                "the exporters run inside the isolation group, and it is rolled back before success is judged");
            Assert.DoesNotContain("catch (Exception ex) { return CommandResult.FailWithDetail(\"Revit export failed", s);
            Assert.Contains("[\"model_isolation\"] = isolationReport", s);
            Assert.Contains("exportResult[\"model_changes\"] = provenChanges", s);
        }

        [Fact]
        public void The_isolation_reads_the_flag_around_the_rollback()
        {
            string s = Source("Commands", "ExportIsolation.cs");
            int before = s.IndexOf("ModifiedBefore = doc.IsModified", StringComparison.Ordinal);
            int group = s.IndexOf("new TransactionGroup(", StringComparison.Ordinal);
            int rollback = s.IndexOf("_group.RollBack()", StringComparison.Ordinal);
            int afterFlag = s.IndexOf("ModifiedAfter = _doc.IsModified", StringComparison.Ordinal);
            Assert.True(before > 0 && group > before && rollback > group && afterFlag > rollback);
            Assert.DoesNotContain(".Assimilate()", s);
        }

        [Fact]
        public void A_proven_unchanged_reply_is_not_recorded_as_a_write()
        {
            string s = Source("Core", "SpatialAfterWrite.cs");
            int guard = s.IndexOf("if (ProvenUnchanged(result)) return;", StringComparison.Ordinal);
            int record = s.IndexOf("ChangeLedger.Record(tool, d)", StringComparison.Ordinal);
            Assert.True(guard > 0 && record > guard, "the proof is honoured before the ledger records the residue");
        }
    }
}
