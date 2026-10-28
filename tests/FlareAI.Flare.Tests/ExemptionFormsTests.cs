using System.Windows.Forms;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The text prompt used to ask why a place is exempt (issue #29).</summary>
    [Collection("Panes")]
    public class TextPromptTests
    {
        [Fact]
        public void OkIsOffUntilThereIsAReason()
        {
            TextBox box; Button ok;
            using (var form = TextPrompt.Build("Exempt", "Why is this place exempt?", "", false, true, out box, out ok))
            {
                Assert.False(ok.Enabled);
                box.Text = "   ";
                Assert.False(ok.Enabled);
                box.Text = " legal wording ";
                Assert.True(ok.Enabled);
                box.Text = "";
                Assert.False(ok.Enabled);
            }
        }

        [Fact]
        public void OkStaysOnWhenNothingIsRequired()
        {
            TextBox box; Button ok;
            using (var form = TextPrompt.Build("Edit", "Change to:", "", false, false, out box, out ok))
                Assert.True(ok.Enabled);
        }
    }
}
