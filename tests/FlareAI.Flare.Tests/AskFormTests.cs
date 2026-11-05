using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The Ask AI box: the writer picks "Ask a question" or "Make a change", and nothing is picked for them.</summary>
    [Collection("Panes")]
    public class AskFormTests
    {
        private static List<RadioButton> Choices(Control root)
        {
            return root.Controls.OfType<RadioButton>().ToList();
        }

        private static T Find<T>(Control root, string text) where T : Control
        {
            return root.Controls.OfType<T>().First(c => c.Text == text);
        }

        /// <summary>A click on a form that isn't shown: PerformClick does nothing there, so raise the button's own click.</summary>
        private static void Click(Button b)
        {
            typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(b, new object[] { System.EventArgs.Empty });
        }

        [Fact]
        public void NeitherChoiceIsCheckedAtFirst_AndTheyReadAsInTheCopy()
        {
            using (var f = new AskForm())
            {
                var choices = Choices(f);
                Assert.Equal(2, choices.Count);
                Assert.All(choices, c => Assert.False(c.Checked));
                Assert.Contains(choices, c => c.Text == "Ask a question");
                Assert.Contains(choices, c => c.Text == "Make a change");
                Assert.False(f.IsQuestion);
            }
        }

        [Fact]
        public void IsQuestionFollowsTheChoice()
        {
            using (var f = new AskForm())
            {
                Find<RadioButton>(f, "Ask a question").Checked = true;
                Assert.True(f.IsQuestion);
                Find<RadioButton>(f, "Make a change").Checked = true;
                Assert.False(f.IsQuestion);
                Assert.False(Find<RadioButton>(f, "Ask a question").Checked);
            }
        }

        [Fact]
        public void RunWithTextButNoChoice_StaysOpen()
        {
            using (var f = new AskForm())
            {
                f.Controls.OfType<TextBox>().First().Text = "What's unclear here?";
                Click(Find<Button>(f, "Run"));
                Assert.Equal(DialogResult.None, f.DialogResult);
            }
        }

        [Fact]
        public void RunWithAChoiceButNoText_StaysOpen()
        {
            using (var f = new AskForm())
            {
                Find<RadioButton>(f, "Ask a question").Checked = true;
                Click(Find<Button>(f, "Run"));
                Assert.Equal(DialogResult.None, f.DialogResult);
            }
        }

        [Fact]
        public void RunWithAChoiceAndText_Closes_AndCancelClosesWithoutAnAnswer()
        {
            using (var f = new AskForm())
            {
                Find<RadioButton>(f, "Ask a question").Checked = true;
                f.Controls.OfType<TextBox>().First().Text = "What's unclear here?";
                Click(Find<Button>(f, "Run"));
                Assert.Equal(DialogResult.OK, f.DialogResult);
                Assert.Equal("What's unclear here?", f.Instruction);
            }
            using (var f = new AskForm())
            {
                Click(Find<Button>(f, "Cancel"));
                Assert.Equal(DialogResult.Cancel, f.DialogResult);
            }
        }
    }
}
