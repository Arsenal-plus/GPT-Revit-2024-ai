// Horizun Revit MCP - original Horizun code.
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class LinkPathRulesTests
    {
        [Fact]
        public void A_local_path_with_forward_slashes_reaches_Revit_with_native_separators()
        {
            string asked = "C:/links/model.rvt";
            string given = LinkPathRules.ForRevit(asked);
            if (Path.DirectorySeparatorChar == '\\') Assert.Equal(@"C:\links\model.rvt", given);
            else Assert.Equal(Path.GetFullPath(asked), given);
        }

        [Theory]
        [InlineData("RSN://server/project/model.rvt")]
        [InlineData("Autodesk Docs://Sample Project/model.rvt")]
        [InlineData("relative/model.rvt")]
        [InlineData("")]
        public void Server_cloud_relative_and_empty_paths_are_left_as_given(string asked)
            => Assert.Equal(asked, LinkPathRules.ForRevit(asked));
    }
}
