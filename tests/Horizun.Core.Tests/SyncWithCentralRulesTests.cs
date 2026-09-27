using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SyncWithCentralRulesTests
    {
        [Theory]
        [InlineData(null, SyncRelinquish.All)]
        [InlineData("all", SyncRelinquish.All)]
        [InlineData("KEEP_BORROWED", SyncRelinquish.KeepBorrowed)]
        [InlineData(" none ", SyncRelinquish.None)]
        public void Relinquish_parses_the_three_choices(string text, SyncRelinquish expected)
        {
            Assert.True(SyncWithCentralRules.TryParseRelinquish(text, out var choice));
            Assert.Equal(expected, choice);
        }

        [Fact]
        public void Relinquish_refuses_anything_else()
        {
            Assert.False(SyncWithCentralRules.TryParseRelinquish("some", out _));
        }

        [Fact]
        public void A_detached_copy_is_refused_by_name()
        {
            var r = SyncWithCentralRules.DocumentRefusal(true, true);
            Assert.Equal("detached_copy", r.Code);
            Assert.Contains("Nothing ran", r.Message);
        }

        [Theory]
        [InlineData(false, false, "not_workshared")]
        [InlineData(null, false, "workshared_state_unreadable")]
        [InlineData(true, null, "detached_state_unreadable")]
        public void Non_central_or_unknown_documents_are_refused(bool? workshared, bool? detached, string code)
        {
            Assert.Equal(code, SyncWithCentralRules.DocumentRefusal(workshared, detached).Code);
        }

        [Fact]
        public void A_workshared_local_passes_the_shape_check()
        {
            Assert.Null(SyncWithCentralRules.DocumentRefusal(true, false));
        }

        [Fact]
        public void Off_by_default_and_the_refusal_names_how_the_owner_enables_it()
        {
            var r = SyncWithCentralRules.AuthorisationRefusal(false, false, @"C:\x\settings.json");
            Assert.Equal("sync_not_authorised", r.Code);
            Assert.Contains("Advanced options > Synchronize with central", r.Message);
            Assert.Contains(@"C:\x\settings.json", r.Message);
        }

        [Fact]
        public void The_workshared_read_only_policy_wins_over_the_grant()
        {
            Assert.Equal("force_read_only_on_workshared",
                SyncWithCentralRules.AuthorisationRefusal(true, true, "s").Code);
            Assert.Null(SyncWithCentralRules.AuthorisationRefusal(true, false, "s"));
        }

        [Fact]
        public void The_sample_is_deterministic_spread_plus_owned()
        {
            var all = Enumerable.Range(1, 1000).Select(i => (long)i).ToList();
            var owned = new long[] { 999, 5, 7 };
            var a = SyncWithCentralRules.Sample(all, owned, 10, 2);
            var b = SyncWithCentralRules.Sample(all.AsEnumerable().Reverse(), owned, 10, 2);
            Assert.Equal(a, b);
            Assert.Contains(1L, a);
            Assert.Contains(5L, a);
            Assert.Contains(7L, a);
            Assert.DoesNotContain(999L, a); // only the first `owned` owned ids, in id order
            Assert.True(a.Count <= 12);
        }

        [Fact]
        public void The_estimate_binds_ownership_modified_and_sampled_statuses()
        {
            var f = SyncWithCentralRules.EstimateFields;
            foreach (string field in new[] { "document", "relinquish", "owned_worksets", "owned_elements",
                                             "borrowed_elements", "is_modified", "sample_status_counts" })
                Assert.Contains(field, f);
        }

        [Fact]
        public void Relinquish_all_holds_only_when_nothing_is_left_owned()
        {
            var ok = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 2, new long[] { 1, 2 }, new long[] { 2 },
                0, new long[0], 0, _ => true);
            Assert.True(ok.Verified);

            var bad = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 2, new long[] { 1, 2 }, new long[] { 2 },
                0, new long[] { 2 }, 0, _ => true);
            Assert.False(bad.Verified);
            Assert.Equal(new long[] { 2 }, bad.UnexpectedlyOwned);
        }

        [Fact]
        public void Keep_borrowed_keeps_exactly_the_borrowed_that_still_exist()
        {
            var ok = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 1, new long[] { 1, 2, 3 },
                new long[] { 2, 3 }, 0, new long[] { 2 }, 0, id => id != 3);
            Assert.True(ok.Verified);

            var released = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 1, new long[] { 1, 2 },
                new long[] { 2 }, 0, new long[0], 0, _ => true);
            Assert.False(released.Verified);
            Assert.Equal(new long[] { 2 }, released.UnexpectedlyReleased);

            var worksetKept = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 1, new long[] { 2 },
                new long[] { 2 }, 1, new long[] { 2 }, 0, _ => true);
            Assert.False(worksetKept.Verified);
        }

        [Fact]
        public void None_keeps_worksets_and_owned_elements()
        {
            var ok = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.None, 3, new long[] { 1, 2 }, new long[] { 2 },
                3, new long[] { 1, 2 }, 0, _ => true);
            Assert.True(ok.Verified);
            var lost = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.None, 3, new long[] { 1, 2 }, new long[] { 2 },
                0, new long[] { 1, 2 }, 0, _ => true);
            Assert.False(lost.Verified);
        }

        [Fact]
        public void Unreadable_ownership_is_unmeasured_not_a_pass()
        {
            var v = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 1, new long[0], new long[0],
                null, new long[0], 0, _ => true);
            Assert.Null(v.Verified);
            var w = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 1, new long[0], new long[0],
                0, new long[0], 4, _ => true);
            Assert.Null(w.Verified);
        }

        [Fact]
        public void After_a_sync_every_sampled_present_element_is_current()
        {
            var ok = SyncWithCentralRules.VerifyUpdates(new Dictionary<long, string>
            { [1] = "CurrentWithCentral", [2] = null });
            Assert.True(ok.Verified);

            var stale = SyncWithCentralRules.VerifyUpdates(new Dictionary<long, string>
            { [1] = "CurrentWithCentral", [2] = "NotYetInCentral" });
            Assert.False(stale.Verified);
            Assert.Equal(new long[] { 2 }, stale.UnexpectedlyOwned);

            var unread = SyncWithCentralRules.VerifyUpdates(new Dictionary<long, string>
            { [1] = "CurrentWithCentral", [2] = SyncWithCentralRules.Unreadable });
            Assert.Null(unread.Verified);
        }

        [Fact]
        public void Status_counts_are_ordinal_and_name_gone_elements()
        {
            var c = SyncWithCentralRules.Counts(new[] { "b", "a", null, "a" });
            Assert.Equal(new[] { "a", "b", "gone" }, c.Keys.ToArray());
            Assert.Equal(2, c["a"]);
        }
    }
}
