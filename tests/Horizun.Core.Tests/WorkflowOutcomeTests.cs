using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WorkflowOutcomeTests
    {
        private static JObject Completed(string tool = "horizun_create_elements")
        {
            var data = new JObject();
            if (tool == "horizun_model_snapshot") data["complete"] = true;
            else if (tool == "horizun_capture_view") { data["captured"] = true; data["view_restored"] = true; }
            else if (tool == "horizun_document_session") { data["operation"] = "open"; data["active_document_verified"] = true; }
            else ApplicationOutcome.StampApplied(data, "Committed", 1, 1, 1, 0, 0, 0);
            var payload = new JObject {
                ["atomic"] = false,
                ["workflow"] = new JObject { ["state"] = "verified_completed", ["completed_steps"] = 1 },
                ["steps"] = new JArray(new JObject { ["phase"] = "execute", ["step"] = "one",
                    ["tool"] = tool, ["success"] = true, ["verified"] = true, ["data"] = data.DeepClone() }),
                ["results"] = new JObject { ["one"] = data }
            };
            ApplicationOutcome.Stamp(payload, ApplicationOutcome.Declare(ApplicationState.VerifiedApplied,
                "per_step", 1, 1, 1, 0, 0, 0));
            return payload;
        }

        [Theory]
        [InlineData("horizun_create_elements")]
        [InlineData("horizun_model_snapshot")]
        [InlineData("horizun_capture_view")]
        [InlineData("horizun_document_session")]
        public void Completed_children_corroborate_the_non_atomic_aggregate(string tool)
        {
            Assert.Equal(ApplicationState.VerifiedApplied, ApplicationOutcome.Read(Completed(tool)));
        }

        [Theory]
        [InlineData("steps")]
        [InlineData("results")]
        [InlineData("workflow")]
        [InlineData("atomic")]
        public void Missing_evidence_does_not_pass(string field)
        {
            var payload = Completed(); payload.Remove(field);
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Theory]
        [InlineData("success")]
        [InlineData("verified")]
        public void Unverified_child_is_not_hidden_by_aggregate_counts(string field)
        {
            var payload = Completed(); payload["steps"][0][field] = false;
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Theory]
        [InlineData("RolledBack")]
        [InlineData("Pending")]
        [InlineData("per_step")]
        public void Child_declarations_are_checked_even_when_step_claims_success(string status)
        {
            var payload = Completed();
            payload["steps"][0]["data"]["application"]["transaction_status"] = status;
            payload["results"]["one"] = payload["steps"][0]["data"].DeepClone();
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Theory]
        [InlineData("horizun_model_snapshot", "complete")]
        [InlineData("horizun_capture_view", "view_restored")]
        [InlineData("horizun_document_session", "active_document_verified")]
        public void Read_and_activation_evidence_must_be_explicit(string tool, string field)
        {
            var payload = Completed(tool);
            ((JObject)payload["steps"][0]["data"]).Remove(field);
            payload["results"]["one"] = payload["steps"][0]["data"].DeepClone();
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Fact]
        public void Duplicate_steps_and_result_mismatches_fail_closed()
        {
            var payload = Completed();
            ((JArray)payload["steps"]).Add(payload["steps"][0].DeepClone());
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
            payload = Completed(); payload["results"]["one"]["different"] = true;
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Theory]
        [InlineData("horizun_document_session", "operation")]
        [InlineData("horizun_create_elements", "application")]
        public void Malformed_child_evidence_returns_uncertain_without_throwing(string tool, string field)
        {
            var payload = Completed(tool);
            payload["steps"][0]["data"][field] = new JArray("unexpected");
            payload["results"]["one"] = payload["steps"][0]["data"].DeepClone();
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Fact]
        public void Aggregate_counters_still_require_complete_verification()
        {
            var payload = Completed(); payload["application"]["verified"] = 0;
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Applied("per_step", 1, 1, 1, 0, 0, 0));
        }
    }
}
