using FlareAI.Core;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public sealed class SafePathTests
    {
        // What GuideRequests.IsValidId accepted and refused before SafePath existed. Pinned so the move changes nothing.
        [Theory]
        [InlineData("a", true)]
        [InlineData("req-20261105-ab12", true)]
        [InlineData("A_b.c-d", true)]
        [InlineData("9", true)]
        [InlineData("a.b", true)]
        [InlineData("", false)]
        [InlineData(".a", false)]
        [InlineData("-a", false)]
        [InlineData("_a", false)]
        [InlineData("a..b", false)]
        [InlineData("..", false)]
        [InlineData(@"..\x", false)]
        [InlineData("../x", false)]
        [InlineData(@"a\b", false)]
        [InlineData("a/b", false)]
        [InlineData(@"\\host\share\x", false)]
        [InlineData(@"C:\x", false)]
        [InlineData("C:x", false)]
        [InlineData("a b", false)]
        [InlineData("a\0b", false)]
        [InlineData("é", false)]
        public void IsValidId_KeepsItsOldAnswers(string id, bool expected)
        {
            Assert.Equal(expected, GuideRequests.IsValidId(id));
            Assert.Equal(expected, SafePath.IsValidId(id));
        }

        [Fact]
        public void IsValidId_Null_IsRefused()
        {
            Assert.False(GuideRequests.IsValidId(null));
            Assert.False(SafePath.IsValidId(null));
        }

        [Fact]
        public void IsValidId_LengthLimitIs100()
        {
            Assert.True(GuideRequests.IsValidId(new string('a', 100)));
            Assert.False(GuideRequests.IsValidId(new string('a', 101)));
            Assert.True(SafePath.IsValidId(new string('a', 100)));
            Assert.False(SafePath.IsValidId(new string('a', 101)));
        }

        [Theory]
        [InlineData(@"\\host\share", true)]
        [InlineData("//host/share", true)]
        [InlineData(@"\/host", true)]
        [InlineData(@"/\host", true)]
        [InlineData(@"C:\a", false)]
        [InlineData(@"a\b", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNetworkPath_KeepsItsOldAnswers(string path, bool expected)
        {
            Assert.Equal(expected, SafePath.IsNetworkPath(path));
        }

        [Theory]
        [InlineData("topic.htm", true)]
        [InlineData("My Topic (2).htm", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData(".", false)]
        [InlineData("..", false)]
        [InlineData("a..b", false)]
        [InlineData(@"a\b", false)]
        [InlineData("a/b", false)]
        [InlineData("C:", false)]
        [InlineData("C:a", false)]
        [InlineData("a:stream", false)]
        [InlineData(@"\\host", false)]
        [InlineData("a\tb", false)]
        [InlineData("a*b", false)]
        [InlineData("a.", false)]
        [InlineData("a ", false)]
        public void IsSafeSegment_RefusesAnythingThatLeavesTheFolder(string name, bool expected)
        {
            Assert.Equal(expected, SafePath.IsSafeSegment(name));
        }

        [Fact]
        public void IsSafeSegment_LengthLimitIs255()
        {
            Assert.True(SafePath.IsSafeSegment(new string('a', 255)));
            Assert.False(SafePath.IsSafeSegment(new string('a', 256)));
        }
    }
}
