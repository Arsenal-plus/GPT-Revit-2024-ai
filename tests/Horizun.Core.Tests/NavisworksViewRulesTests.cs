// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Which 3D view Navisworks reads out of a .rvt (Core/NavisworksViewRules.cs):
// any view whose name contains "Navisworks" wins over {3D} - measured 2026-09-26.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NavisworksViewRulesTests
    {
        [Theory]
        [InlineData("Navisworks", true)]
        [InlineData("HZ Navisworks b3b1d996", true)]
        [InlineData("NAVISWORKS export", true)]
        [InlineData("{3D}", false)]
        [InlineData("Horizun - Coordination 2026-09-26", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void A_name_containing_Navisworks_in_any_case_is_a_candidate(string name, bool expected)
        {
            Assert.Equal(expected, NavisworksViewRules.IsCandidateName(name));
        }

        [Fact]
        public void The_exact_name_ranks_before_any_other_candidate()
        {
            Assert.True(NavisworksViewRules.Rank("Navisworks") < NavisworksViewRules.Rank("HZ Navisworks view"));
        }

        [Fact]
        public void A_created_view_named_with_Navisworks_is_refused_with_the_reason()
        {
            Assert.Contains("{3D}", NavisworksViewRules.RefuseCreatedViewName("Horizun - Navisworks 2026-09-26"));
            Assert.Null(NavisworksViewRules.RefuseCreatedViewName("Horizun - Coordination 2026-09-26"));
        }
    }
}
