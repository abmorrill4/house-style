using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class AllowedRootsTests
    {
        private static readonly string[] Roots = { @"C:\Docs" };

        [Theory]
        [InlineData(@"C:\Docs\Project\a.htm", true)]
        [InlineData(@"c:\docs", true)]
        [InlineData(@"C:\Docs\..\Secret", false)]
        [InlineData(@"C:\DocsEvil\a.htm", false)]
        [InlineData(@"\\server\share\x", false)]
        [InlineData(@"D:\Docs", false)]
        public void Path_must_be_inside_a_root(string path, bool allowed)
        {
            Assert.Equal(allowed, AllowedRoots.IsAllowed(path, Roots));
        }

        [Fact]
        public void No_roots_allows_everything_and_an_empty_path_is_fine()
        {
            Assert.True(AllowedRoots.IsAllowed(@"D:\anything", new string[0]));
            Assert.True(AllowedRoots.IsAllowed(null, Roots));
        }
    }
}
