using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
namespace Horizun.Core.Tests
{
    public class WorkflowRulesTests
    {
        private static JObject Request(string tool = "horizun_create_elements") => new JObject
        { ["target_document"] = "fixture", ["steps"] = new JArray(new JObject
        { ["key"] = "one", ["tool"] = tool, ["arguments"] = new JObject() }) };
        [Fact] public void ClosedToolSetRejectsArbitraryCodeAndRecursion()
        {
            Assert.NotNull(WorkflowRules.Validate(Request("horizun_execute_python")));
            Assert.NotNull(WorkflowRules.Validate(Request("horizun_run_workflow")));
            Assert.Null(WorkflowRules.Validate(Request()));
        }
        [Fact] public void CallerCannotSmuggleChildApprovals()
        {
            var r = Request(); r["steps"][0]["arguments"]["confirmation_token"] = "other-approval";
            Assert.NotNull(WorkflowRules.Validate(r));
        }
        [Fact] public void ClosingDocumentsIsNotImplicitRecovery()
        {
            var r = Request("horizun_document_session"); r["steps"][0]["arguments"]["operation"] = "close";
            Assert.NotNull(WorkflowRules.Validate(r));
            r["steps"][0]["arguments"]["operation"] = "open";
            Assert.Null(WorkflowRules.Validate(r));
        }
        [Fact] public void MisspelledChildArgumentsAreRefusedBeforeAnyPreparation()
        {
            var r = Request(); r["steps"][0]["arguments"]["elementz"] = new JArray();
            Assert.Contains("Unknown argument", WorkflowRules.Validate(r));
        }
        [Fact] public void ARecoveredTaskKeepsEvidenceAndDistinctStatus()
        {
            var receipt = JObject.Parse("{outcome:'ok',workflow:{recovered:true,completed_steps:2}}");
            Assert.Equal(OperationDescription.Recovered,OperationDescription.Kind(receipt));
        }
        [Fact] public void AFailedRestoreCannotBeCalledRolledBack()
        {
            var receipt = JObject.Parse("{outcome:'failed',diagnostic:{rollback_status:'RolledBack',view_restored:false}}");
            Assert.Equal(OperationDescription.Unverified,OperationDescription.Kind(receipt));
        }
    }
}
