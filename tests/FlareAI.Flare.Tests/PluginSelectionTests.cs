using Xunit;

namespace FlareAI.Flare.Tests
{
    public class PluginSelectionTests
    {
        [Fact]
        public void Is_SameTextAndPathIgnoringCase_IsTrue()
        {
            PluginSelection.Remember(null, null);
            PluginSelection.Remember("C:\\p\\t.htm", "Single Sign-On");
            Assert.True(PluginSelection.Is("c:\\p\\t.htm", "Single Sign-On"));
        }

        [Fact]
        public void Is_DifferentText_IsFalse()
        {
            PluginSelection.Remember(null, null);
            PluginSelection.Remember("C:\\p\\t.htm", "Single Sign-On");
            Assert.False(PluginSelection.Is("C:\\p\\t.htm", "Identity Provider"));
        }

        [Fact]
        public void Is_DifferentPath_IsFalse()
        {
            PluginSelection.Remember(null, null);
            PluginSelection.Remember("C:\\p\\t.htm", "Single Sign-On");
            Assert.False(PluginSelection.Is("C:\\p\\other.htm", "Single Sign-On"));
        }

        [Fact]
        public void Is_NullPathAndText_IsFalse()
        {
            PluginSelection.Remember(null, null);
            PluginSelection.Remember("a", "b");
            Assert.False(PluginSelection.Is(null, null));
        }
    }
}
