using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core.Style;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    [Collection("Panes")]
    public class RuleSettingFormTests
    {
        private static RuleRef Rule(string id) { return new RuleRef { Id = id, Name = "Company · Terms: " + id }; }
        private static RuleRef Checker(string id, string layer = "Project") { return new RuleRef { Id = id, Name = "Glossary terms", Layer = layer, IsChecker = true }; }

        private static List<CheckBox> Boxes(RuleSettingForm f) { return f.Controls.OfType<CheckBox>().ToList(); }

        [Fact]
        public void TwinCheckbox_IsTicked_CheckerCheckbox_IsNot()
        {
            using (var f = new RuleSettingForm("rule", true, false, new List<RuleRef> { Rule("a"), Checker("Company.GlossaryTerms") }))
            {
                var boxes = Boxes(f);
                Assert.Equal(2, boxes.Count);
                Assert.True(boxes.Single(b => b.Text.Contains("other rule")).Checked);
                var checker = boxes.Single(b => b.Text == "Also disable Glossary terms");
                Assert.False(checker.Checked);
                Assert.True(checker.TabStop);
                Assert.True(f.AlsoOthers);
            }
        }

        [Fact]
        public void AlsoCheckers_IsEmpty_UntilTicked()
        {
            using (var f = new RuleSettingForm("rule", true, false, new List<RuleRef> { Checker("Company.GlossaryTerms") }))
            {
                Assert.Empty(f.AlsoCheckers);
                Boxes(f).Single().Checked = true;
                Assert.Equal(new[] { "Company.GlossaryTerms" }, f.AlsoCheckers.ToArray());
            }
        }

        [Fact]
        public void TwoCheckers_TickingOne_ReturnsOnlyThatOne()
        {
            using (var f = new RuleSettingForm("rule", true, false, new List<RuleRef> { Checker("Company.GlossaryTerms", "Company"), Checker("Project.GlossaryTerms", "Project") }))
            {
                var boxes = Boxes(f);
                Assert.Equal(2, boxes.Count);
                boxes.Single(b => b.Text.EndsWith("(Project)")).Checked = true;
                Assert.Equal(new[] { "Project.GlossaryTerms" }, f.AlsoCheckers.ToArray());
            }
        }

        [Fact]
        public void NoTwins_NoExtraCheckbox()
        {
            using (var f = new RuleSettingForm("rule", true))
            {
                Assert.Empty(Boxes(f));
                Assert.Empty(f.AlsoCheckers);
                Assert.False(f.AlsoOthers);
            }
        }
    }
}
