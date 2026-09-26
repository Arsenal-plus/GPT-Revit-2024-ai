using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SessionScopeRulesTests
    {
        private static SessionScopeRules.WriteEntry E(int minutesAgo, string tool, long[] added = null, long[] modified = null) =>
            new SessionScopeRules.WriteEntry { AtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo), Tool = tool, Added = added, Modified = modified };

        [Fact]
        public void Two_writes_in_a_row_are_unioned_with_duplicates_collapsed()
        {
            var history = new List<SessionScopeRules.WriteEntry>
            {
                E(5, "horizun_create_elements", added: new long[] { 1, 2 }),
                E(1, "horizun_transform_elements", modified: new long[] { 2, 3 })
            };
            SessionScopeRules.Outcome o = SessionScopeRules.Union(history, null);
            Assert.Equal(2, o.WritesConsidered);
            Assert.Equal(new List<long> { 1, 2, 3 }, o.Ids);
            Assert.Equal(3, o.TotalIdsFound);
            Assert.False(o.Truncated);
            Assert.Contains("horizun_create_elements", o.Tools);
            Assert.Contains("horizun_transform_elements", o.Tools);
        }

        [Fact]
        public void Since_utc_excludes_writes_before_it()
        {
            var history = new List<SessionScopeRules.WriteEntry>
            {
                E(30, "old", added: new long[] { 1 }),
                E(1, "recent", added: new long[] { 2 })
            };
            SessionScopeRules.Outcome o = SessionScopeRules.Union(history, DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(1, o.WritesConsidered);
            Assert.Equal(new List<long> { 2 }, o.Ids);
        }

        [Fact]
        public void Beyond_the_cap_the_union_is_truncated_and_says_so()
        {
            var added = new long[SessionScopeRules.MaxIds + 50];
            for (int i = 0; i < added.Length; i++) added[i] = i;
            var history = new List<SessionScopeRules.WriteEntry> { E(1, "big", added: added) };
            SessionScopeRules.Outcome o = SessionScopeRules.Union(history, null);
            Assert.Equal(SessionScopeRules.MaxIds, o.Ids.Count);
            Assert.Equal(added.Length, o.TotalIdsFound);
            Assert.True(o.Truncated);
        }

        [Fact]
        public void Empty_history_is_not_an_error()
        {
            SessionScopeRules.Outcome o = SessionScopeRules.Union(new List<SessionScopeRules.WriteEntry>(), null);
            Assert.Equal(0, o.WritesConsidered);
            Assert.Empty(o.Ids);
            Assert.False(o.Truncated);
        }

        [Fact]
        public void A_null_history_is_treated_as_empty()
        {
            SessionScopeRules.Outcome o = SessionScopeRules.Union(null, null);
            Assert.Equal(0, o.WritesConsidered);
            Assert.Empty(o.Ids);
        }
    }
}
