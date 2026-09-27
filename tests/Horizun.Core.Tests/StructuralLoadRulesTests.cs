// Horizun Revit MCP - original Horizun code.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class StructuralLoadRulesTests
    {
        [Theory]
        [InlineData("DL1", true, "DL1")]
        [InlineData("", false, StructuralLoadRules.NoLoadCase)]
        [InlineData(null, false, StructuralLoadRules.NoLoadCase)]
        [InlineData("", true, StructuralLoadRules.UnreadableCase)]
        [InlineData("  ", null, StructuralLoadRules.UnreadableCase)]
        [InlineData("LL2", null, "LL2")]
        public void A_load_case_key_is_its_name_no_case_or_unreadable_and_never_empty(string name, bool? assigned, string expected)
        {
            string key = StructuralLoadRules.CaseKey(name, assigned);
            Assert.Equal(expected, key);
            Assert.False(string.IsNullOrWhiteSpace(key));
        }

        [Fact]
        public void No_case_is_a_fact_about_the_load_not_a_failure_to_read_it()
        {
            // MEASURED 2026-09-27: an API point load in a document with no load case reads
            // an invalid LoadCaseId and an empty LoadCaseName.
            Assert.False(StructuralLoadRules.CaseNameUnread("", false));
            Assert.True(StructuralLoadRules.CaseNameUnread("", true));
            Assert.True(StructuralLoadRules.CaseNameUnread(null, null));
            Assert.False(StructuralLoadRules.CaseNameUnread("DL1", true));
        }
    }
}
