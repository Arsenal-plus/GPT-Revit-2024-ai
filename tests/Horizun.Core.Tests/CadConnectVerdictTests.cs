// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// CadConnectVerdict is the arithmetic horizun_cad_connect's direct joins,
// elbow/tee/cross fittings and top-level composite verdict are built from.
// CadConnectCommand cannot be constructed without a UIApplication, so these
// scenarios were unreachable before this file existed.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadConnectVerdictTests
    {
        private static JObject AppliedBlock(string txStatus, int requested, int applied, int verified,
                                            int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject();
            ApplicationOutcome.StampApplied(payload, txStatus, requested, applied, verified, unresolved, failed, unknown);
            return payload;
        }

        private static JObject RehearsedBlock(int requested, int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject();
            ApplicationOutcome.StampRehearsal(payload, requested, unresolved, failed, unknown);
            return payload;
        }

        // ---- ChildVerdictBacksUp ----------------------------------------------------

        [Fact]
        public void A_verified_applied_child_backs_up_a_real_join_or_create()
        {
            CommandResult r = CommandResult.Ok(AppliedBlock("Committed", 1, 1, 1));
            Assert.True(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: false, out ApplicationState state));
            Assert.Equal(ApplicationState.VerifiedApplied, state);
        }

        [Fact]
        public void A_verified_applied_child_does_not_back_up_a_dry_run_slot()
        {
            // The inverse mistake: the child WROTE where a rehearsal was expected.
            CommandResult r = CommandResult.Ok(AppliedBlock("Committed", 1, 1, 1));
            Assert.False(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: true, out ApplicationState state));
            Assert.Equal(ApplicationState.VerifiedApplied, state);
        }

        [Fact]
        public void A_clean_rehearsal_backs_up_a_dry_run_but_not_an_apply()
        {
            CommandResult r = CommandResult.Ok(RehearsedBlock(1));
            Assert.True(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: true, out ApplicationState dryState));
            Assert.Equal(ApplicationState.Rehearsed, dryState);

            Assert.False(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: false, out ApplicationState applyState));
            Assert.Equal(ApplicationState.Rehearsed, applyState);
        }

        [Fact]
        public void A_legitimate_no_op_backs_up_either_a_dry_run_or_an_apply()
        {
            CommandResult r = CommandResult.Ok(AppliedBlock(ApplicationOutcome.NotStarted, 0, 0, 0));
            Assert.True(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: false, out ApplicationState applyState));
            Assert.Equal(ApplicationState.NoOp, applyState);
            Assert.True(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: true, out ApplicationState dryState));
            Assert.Equal(ApplicationState.NoOp, dryState);
        }

        [Fact]
        public void A_partial_child_never_backs_up_landed_even_though_it_succeeded()
        {
            CommandResult r = CommandResult.Ok(AppliedBlock("Committed", 4, 2, 2, failed: 2));
            Assert.False(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: false, out ApplicationState state));
            Assert.Equal(ApplicationState.Partial, state);
        }

        [Fact]
        public void A_child_that_declared_nothing_at_all_does_not_back_up_landed()
        {
            CommandResult r = CommandResult.Ok(new JObject { ["transaction_status"] = "Committed" });
            Assert.False(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: false, out ApplicationState state));
            Assert.Equal(ApplicationState.Uncertain, state);
        }

        [Fact]
        public void A_transport_failure_never_backs_up_landed()
        {
            CommandResult r = CommandResult.Fail("Revit refused it");
            Assert.False(CadConnectVerdict.ChildVerdictBacksUp(r, dryRun: false, out ApplicationState state));
            Assert.Equal(ApplicationState.Failed, state);
        }

        // ---- RowChild -> fed through CompositeVerdict --------------------------------

        private static JObject Row(string state) => new JObject { ["state"] = state };

        [Fact]
        public void Joined_and_created_rows_compose_to_a_verified_applied_composite()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RowChild(Row("joined")), CadConnectVerdict.RowChild(Row("created")));

            Assert.Equal("verified_applied", block.Value<string>("state"));
            Assert.True(block.Value<bool>("fully_applied"));
        }

        [Fact]
        public void Would_join_and_would_create_rows_compose_to_a_rehearsed_composite()
        {
            JObject block = CompositeVerdict.AggregateRehearsal(
                CadConnectVerdict.RowChild(Row("would_join")), CadConnectVerdict.RowChild(Row("would_create")));

            Assert.Equal("rehearsed", block.Value<string>("state"));
        }

        [Fact]
        public void An_already_connected_row_is_excluded_like_a_legitimate_no_op()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RowChild(Row("joined")), CadConnectVerdict.RowChild(Row("already_connected")));

            Assert.Equal("verified_applied", block.Value<string>("state"));
            Assert.Equal(1, block.Value<int>("requested"));
        }

        [Fact]
        public void A_refused_row_downgrades_the_composite_to_partial_and_all_refused_to_failed()
        {
            JObject mixed = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RowChild(Row("joined")), CadConnectVerdict.RowChild(Row("refused")));
            Assert.Equal("partial", mixed.Value<string>("state"));

            JObject allRefused = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RowChild(Row("refused")), CadConnectVerdict.RowChild(Row("refused")));
            Assert.Equal("failed", allRefused.Value<string>("state"));
        }

        [Fact]
        public void An_uncertain_row_makes_the_composite_uncertain_even_over_a_joined_sibling()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RowChild(Row("joined")), CadConnectVerdict.RowChild(Row("uncertain")));

            Assert.Equal("uncertain", block.Value<string>("state"));
        }

        [Fact]
        public void An_unrecognised_or_missing_state_is_treated_as_not_landed()
        {
            CompositeChild fromNull = CadConnectVerdict.RowChild(null);
            CompositeChild fromUnknown = CadConnectVerdict.RowChild(Row("skipped_unresolved"));

            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed, fromNull, fromUnknown);
            Assert.Equal("failed", block.Value<string>("state"));
        }

        // ---- RefitRowChild (CadRefit.cs, the operation=refit path) -------------------

        [Fact]
        public void Refitted_and_would_refit_rows_compose_as_applied_and_rehearsed_respectively()
        {
            JObject applied = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RefitRowChild(Row("refitted")));
            Assert.Equal("verified_applied", applied.Value<string>("state"));

            JObject rehearsed = CompositeVerdict.AggregateRehearsal(CadConnectVerdict.RefitRowChild(Row("would_refit")));
            Assert.Equal("rehearsed", rehearsed.Value<string>("state"));
        }

        [Fact]
        public void Rolled_back_not_viable_and_not_rehearsed_refit_rows_are_never_landed()
        {
            foreach (string state in new[] { "rolled_back", "not_viable", "not_rehearsed" })
            {
                JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                    CadConnectVerdict.RefitRowChild(Row("refitted")), CadConnectVerdict.RefitRowChild(Row(state)));
                Assert.Equal("partial", block.Value<string>("state"));
            }
        }

        [Fact]
        public void An_uncertain_refit_row_makes_the_composite_uncertain()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                CadConnectVerdict.RefitRowChild(Row("refitted")), CadConnectVerdict.RefitRowChild(Row("uncertain")));
            Assert.Equal("uncertain", block.Value<string>("state"));
        }
    }
}
