// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// CompositeVerdict is the arithmetic every composite tool (apply_cad_plan,
// apply_cad_update, cad_connect, apply_ifc_plan, execute_plan) is meant to route
// its children through instead of asking only "did the call answer Success?".
// These tests pin the five scenarios that motivated it.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CompositeVerdictTests
    {
        private static JObject Applied(string txStatus, int requested, int applied, int verified,
                                       int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject();
            ApplicationOutcome.StampApplied(payload, txStatus, requested, applied, verified, unresolved, failed, unknown);
            return payload;
        }

        private static JObject Rehearsed(int requested, int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject();
            ApplicationOutcome.StampRehearsal(payload, requested, unresolved, failed, unknown);
            return payload;
        }

        private static CompositeChild Verified(int n = 2) => CompositeChild.Of(true, Applied("Committed", n, n, n));
        private static CompositeChild NoOpChild() => CompositeChild.Of(true, Applied(ApplicationOutcome.NotStarted, 0, 0, 0));
        private static CompositeChild Undeclared() => CompositeChild.Of(true, new JObject { ["ok"] = true });
        private static CompositeChild TransportFailed() => CompositeChild.Of(false, null);
        private static CompositeChild PartialChild() => CompositeChild.Of(true, Applied("Committed", 10, 6, 6, failed: 4));

        // ---- Aggregate: the apply shape --------------------------------------------

        [Fact]
        public void All_verified_children_produce_a_verified_applied_composite()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                Verified(3), Verified(5), Verified(1));

            Assert.Equal("verified_applied", block.Value<string>("state"));
            Assert.True(block.Value<bool>("fully_applied"));
            Assert.Equal(3, block.Value<int>("requested"));
            Assert.Equal(3, block.Value<int>("applied"));
            Assert.Equal(3, block.Value<int>("verified"));
        }

        [Fact]
        public void One_unverified_child_downgrades_the_composite_to_partial_never_verified_applied()
        {
            // Two children land cleanly; the third declares itself Partial - it
            // succeeded (transport) but its own verification says it did not fully
            // apply. That must never be read as landed just because it answered.
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                Verified(2), Verified(2), PartialChild());

            Assert.Equal("partial", block.Value<string>("state"));
            Assert.False(block.Value<bool>("fully_applied"));
            Assert.Equal(3, block.Value<int>("requested"));
            Assert.Equal(2, block.Value<int>("applied"));
            Assert.Equal(1, block.Value<int>("failed"));
        }

        [Fact]
        public void A_legitimate_no_op_child_does_not_drag_a_verified_composite_down()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                Verified(4), NoOpChild(), Verified(1));

            Assert.Equal("verified_applied", block.Value<string>("state"));
            Assert.True(block.Value<bool>("fully_applied"));
            // The no-op child is excluded entirely, not counted as a landed unit.
            Assert.Equal(2, block.Value<int>("requested"));
        }

        [Fact]
        public void A_failed_child_among_verified_ones_is_partial_and_all_failed_children_is_failed()
        {
            JObject mixed = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                Verified(2), TransportFailed());
            Assert.Equal("partial", mixed.Value<string>("state"));

            JObject allFailed = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                TransportFailed(), TransportFailed());
            Assert.Equal("failed", allFailed.Value<string>("state"));
            Assert.Equal(0, allFailed.Value<int>("applied"));
        }

        [Fact]
        public void A_child_that_declared_no_verdict_at_all_makes_the_composite_uncertain_even_over_verified_siblings()
        {
            // Fail-closed: "this child told me nothing" must be at least as serious as
            // a declared rollback, and it must win over nine children that did land -
            // exactly ApplicationOutcome.Applied's own ordering (unknown checked before
            // partial/failed).
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed,
                Verified(9), Undeclared());

            Assert.Equal("uncertain", block.Value<string>("state"));
            Assert.False(block.Value<bool>("fully_applied"));
        }

        [Fact]
        public void Every_child_a_legitimate_no_op_is_a_no_op_composite_not_verified_applied()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed, NoOpChild(), NoOpChild());

            Assert.Equal("no_op", block.Value<string>("state"));
            Assert.True(block.Value<bool>("fully_applied"));
            Assert.Equal(0, block.Value<int>("requested"));
        }

        [Fact]
        public void No_children_at_all_is_a_no_op_composite()
        {
            JObject block = CompositeVerdict.Aggregate(ApplicationOutcome.Committed);
            Assert.Equal("no_op", block.Value<string>("state"));
        }

        // ---- AggregateRehearsal: the dry-run shape ---------------------------------

        [Fact]
        public void Every_child_rehearsed_cleanly_is_a_rehearsed_composite()
        {
            var a = CompositeChild.Of(true, Rehearsed(5));
            var b = CompositeChild.Of(true, Rehearsed(2));

            JObject block = CompositeVerdict.AggregateRehearsal(a, b);

            Assert.Equal("rehearsed", block.Value<string>("state"));
        }

        [Fact]
        public void A_child_that_wrote_during_a_rehearsal_slot_is_not_a_clean_rehearsal_composite()
        {
            // The inverse mistake PlanLedgerTests pins one level down: a child that
            // answered with a full VerifiedApplied where a Rehearsed was expected.
            var wroteInstead = CompositeChild.Of(true, Applied("Committed", 3, 3, 3));

            JObject block = CompositeVerdict.AggregateRehearsal(CompositeChild.Of(true, Rehearsed(1)), wroteInstead);

            Assert.NotEqual("rehearsed", block.Value<string>("state"));
        }
    }
}
