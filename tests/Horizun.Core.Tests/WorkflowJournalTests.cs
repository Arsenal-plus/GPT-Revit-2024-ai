using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
namespace Horizun.Core.Tests
{
    public class WorkflowJournalTests
    {
        [Fact] public void EvidenceIsReadableBeforeDisposalAndCannotBeOverwritten()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-workflow-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var journal = new WorkflowJournal(dir, "../not-a-path"))
                {
                    journal.Append(new JObject { ["phase"] = "execution_started", ["step"] = "Кириллица" });
                    string[] lines = ReadLines(journal.Path);
                    Assert.Single(lines); Assert.Equal("Кириллица", JObject.Parse(lines[0]).Value<string>("step"));
                    Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(journal.Path));
                    Assert.Throws<IOException>(() => new WorkflowJournal(dir, "../not-a-path"));
                    journal.Append(new JObject { ["phase"] = "execute", ["verified"] = true });
                    Assert.Equal(2, ReadLines(journal.Path).Length);
                }
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
        private static string[] ReadLines(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        [Fact]
        public void ReceiptKeepsStepOutcomesWithoutCopyingGeometryPayloads()
        {
            var detail = JObject.Parse("{trace_path:'trace.jsonl',steps:[{step:'one',phase:'execute',verified:true,data:{geometry:'large-private-payload'}}]}");
            var receipt = ReceiptLedger.Build("horizun_run_workflow", false, "second step failed", null,
                0, 1, "test", DateTime.UtcNow, null, detail);
            Assert.Equal("one", receipt["diagnostic"]["steps"][0].Value<string>("step"));
            Assert.True(receipt["diagnostic"]["steps"][0].Value<bool>("verified"));
            Assert.DoesNotContain("large-private-payload", receipt.ToString());
            Assert.Equal("trace.jsonl", receipt["diagnostic"].Value<string>("trace_path"));
        }
        [Theory]
        [InlineData("partial", OperationDescription.Partial)]
        [InlineData("rolled_back", OperationDescription.RolledBack)]
        [InlineData("uncertain", OperationDescription.Unverified)]
        public void TransportSuccessDoesNotHideApplicationOutcome(string state, string expected)
        {
            var receipt = ReceiptLedger.Build("horizun_write_params_verified", true, null,
                new JObject { ["application"] = new JObject { ["state"] = state } },
                0, 1, "test", DateTime.UtcNow);
            Assert.Equal(expected, OperationDescription.Kind(receipt));
        }
    }
}
