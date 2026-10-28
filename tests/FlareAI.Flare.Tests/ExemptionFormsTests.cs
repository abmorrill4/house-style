using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core.Actions;
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

    /// <summary>The Exemptions list: every exempted place, its rule title, its stale note, and Remove Exemption (issue #29).</summary>
    [Collection("Panes")]
    public class ExemptionFormsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-exf-" + Guid.NewGuid().ToString("N"));

        public ExemptionFormsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Content", "Sales"));
            Directory.CreateDirectory(Path.Combine(_root, "Project", "HouseStyle"));
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private void WriteTopic(string relative, string html)
        {
            File.WriteAllText(Path.Combine(_root, "Content", relative), html);
        }

        private void WriteExemptions(params string[] rows)
        {
            var lines = new List<string> { "# Text exempted from a rule. One per line, tab separated." };
            lines.AddRange(rows);
            File.WriteAllLines(Path.Combine(_root, "Project", "HouseStyle", "Exemptions.txt"), lines.ToArray());
        }

        private static string Row(params string[] fields) { return string.Join("\t", fields); }

        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in All(c)) yield return d;
            }
        }

        private static string[] Cells(ListViewItem item)
        {
            return item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray();
        }

        private static string Title(string rule) { return rule == "Google.Will" ? "Will: Use simple words" : null; }

        [Fact]
        public void ListsRowsShowsTheRuleTitleAndRemoves()
        {
            WriteTopic(@"Sales\Topic.htm", "<p>It will ship. It was made.</p>");
            WriteExemptions(
                Row(@"Sales\Topic.htm", "Google.Will", "will", "It ", "legal wording", "ann", "2026-10-01"),
                Row(@"Sales\Topic.htm", "Google.Passive", "was", "It ", "why", "bo", "2026-10-02"));

            using (var form = new ExemptionsForm(_root, Title))
            {
                form.Show();
                var list = All(form).OfType<ListView>().Single();
                var remove = All(form).OfType<Button>().Single(b => b.Text == "Remove Exemption");
                var head = All(form).OfType<Label>().Single(l => l.Dock == DockStyle.Top);

                Assert.Equal(2, list.Items.Count);
                Assert.Equal(new[] { "Topic", "Rule", "Text", "Reason", "Who", "When" }, list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray());
                Assert.Equal(new[] { @"Sales\Topic.htm", "Will: Use simple words", "will", "legal wording", "ann", "2026-10-01" }, Cells(list.Items[0]));
                Assert.Equal(new[] { @"Sales\Topic.htm", "Google.Passive", "was", "why", "bo", "2026-10-02" }, Cells(list.Items[1]));
                Assert.True(remove.Enabled);

                remove.PerformClick();

                Assert.Equal("Removed. It shows again the next time you lint.", head.Text);
                Assert.Single(list.Items);
                Assert.Equal("Google.Passive", Cells(list.Items[0])[1]);
                Assert.Equal(1, Exemptions.Count(_root));
                var kept = File.ReadAllText(Path.Combine(_root, "Project", "HouseStyle", "Exemptions.txt"));
                Assert.DoesNotContain("Google.Will", kept);
                Assert.Contains("Google.Passive", kept);
            }
        }

        [Fact]
        public void MarksTextNotFoundAndTopicNotFound()
        {
            WriteTopic(@"Sales\Topic.htm", "<p>It will ship. It was made.</p>");
            WriteExemptions(
                Row(@"Sales\Topic.htm", "Google.Will", "will be", "It ", "reworded", "ann", "2026-10-01"),
                Row(@"Gone\Missing.htm", "Google.Will", "x", "It ", "moved", "bo", "2026-10-02"),
                Row(@"Sales\Topic.htm", "Google.Passive", "was", "It ", "why", "cy", "2026-10-03"));

            using (var form = new ExemptionsForm(_root, Title))
            {
                form.Show();
                var list = All(form).OfType<ListView>().Single();

                Assert.Equal(3, list.Items.Count);
                Assert.Equal("will be (Text not found)", Cells(list.Items[0])[2]);
                Assert.Equal("x (Topic not found)", Cells(list.Items[1])[2]);
                Assert.Equal("was", Cells(list.Items[2])[2]);
            }
        }
    }
}
