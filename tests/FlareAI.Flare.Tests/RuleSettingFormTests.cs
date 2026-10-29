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
        private static RuleRef Twin(string id) { return new RuleRef { Id = id, Name = "Company · Page: " + id }; }
        private static RuleRef Checker(string name, string layer = null) { return new RuleRef { Id = name, Name = name, Layer = layer, IsChecker = true }; }

        private static List<CheckBox> Boxes(Form f)
        {
            return f.Controls.OfType<CheckBox>().ToList();
        }

        [Fact]
        public void TwinCheckbox_IsTicked_CheckerCheckbox_IsNot()
        {
            using (var f = new RuleSettingForm("rule", true, false, new List<RuleRef> { Twin("a"), Checker("Company.GlossaryTerms") }))
            {
                var boxes = Boxes(f);
                Assert.Equal(2, boxes.Count);
                var twin = boxes.Single(b => b.Text.Contains("the same words"));
                var checker = boxes.Single(b => b.Text == "Also disable Glossary terms");
                Assert.True(twin.Checked);
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
                Assert.False(f.AlsoOthers);
            }
        }

        [Fact]
        public void TwoCheckers_TickingOne_ReturnsOnlyThatOne()
        {
            using (var f = new RuleSettingForm("rule", true, false, new List<RuleRef> { Checker("Company.GlossaryTerms"), Checker("Project.GlossaryTerms") }))
            {
                var boxes = Boxes(f);
                Assert.Equal(2, boxes.Count);
                var project = boxes.Single(b => b.Text == "Also disable Glossary terms (Project)");
                Assert.Contains(boxes, b => b.Text == "Also disable Glossary terms (Company)");
                project.Checked = true;
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
