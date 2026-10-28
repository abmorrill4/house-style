using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Actions;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>
    /// The Exemptions dialog (issue #29) against a real Flare project folder: the rows and their columns, the rule's title,
    /// the stale notes for text and topics that changed, and Remove Exemption writing the project's file.
    /// </summary>
    [Collection("Panes")]
    public class ExemptionFormsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hs-exform-" + Guid.NewGuid().ToString("N"));
        private readonly string _topic;

        public ExemptionFormsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Content"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(_root, @"Content\A.htm"), "<html><body><p>We utilize it.</p></body></html>");
            File.WriteAllText(Path.Combine(_root, @"Content\B.htm"), "<html><body><p>Ask the user.</p></body></html>");
            _topic = Path.Combine(_root, @"Content\A.htm");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        [Fact]
        public void ListsRowsShowsTheRuleTitleAndRemoves()
        {
            Sta(() =>
            {
                Exemptions.Add(_topic, "A.htm", "plain-words.utilize", "utilize", "We ", "Quoted in a glossary", "tester");
                Exemptions.Add(_topic, "B.htm", "person.the-user", "the user", "Ask ", "Product name", "tester");
                using (var f = new ExemptionsForm(_topic, rule => rule == "plain-words.utilize" ? "Plain words: utilize" : rule))
                {
                    f.Show();
                    Application.DoEvents();
                    var list = All(f).OfType<ListView>().Single();
                    Assert.Equal(new[] { "Topic", "Rule", "Text", "Reason", "Who", "When" }, list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray());
                    Assert.Equal(2, list.Items.Count);
                    Assert.Equal("A.htm", list.Items[0].SubItems[0].Text);
                    Assert.Equal("Plain words: utilize", list.Items[0].SubItems[1].Text);   // the title, not the id
                    Assert.Equal("person.the-user", list.Items[1].SubItems[1].Text);        // no title: the id itself
                    Assert.Equal("Quoted in a glossary", list.Items[0].SubItems[3].Text);
                    Assert.Equal("tester", list.Items[0].SubItems[4].Text);

                    var remove = All(f).OfType<Button>().Single(b => b.Text == "Remove Exemption");
                    Assert.True(remove.Enabled);   // the first row is selected when the list opens
                    remove.PerformClick();

                    Assert.Single(list.Items);
                    Assert.Equal("person.the-user", list.Items[0].SubItems[1].Text);
                    Assert.Single(Exemptions.Load(_topic));
                    Assert.StartsWith("Removed. It shows again the next time you lint.", All(f).OfType<LiveLabel>().Single().Text);
                }
            });
        }

        [Fact]
        public void MarksTextNotFoundAndTopicNotFound()
        {
            Sta(() =>
            {
                Exemptions.Add(_topic, "A.htm", "plain-words.utilize", "utilize", "We ", "Quoted", "tester");
                Exemptions.Add(_topic, "A.htm", "plain-words.utilize", "absent words", "", "Edited since", "tester");
                Exemptions.Add(_topic, "Missing.htm", "plain-words.utilize", "anything", "", "Topic removed", "tester");
                using (var f = new ExemptionsForm(_topic, null))
                {
                    f.Show();
                    Application.DoEvents();
                    var list = All(f).OfType<ListView>().Single();
                    Assert.Equal(3, list.Items.Count);
                    Assert.Equal("utilize", Row(list, "A.htm", "Quoted").SubItems[2].Text);
                    Assert.Equal("absent words (Text not found)", Row(list, "A.htm", "Edited since").SubItems[2].Text);
                    Assert.Equal("anything (Topic not found)", Row(list, "Missing.htm", "Topic removed").SubItems[2].Text);
                    Assert.Equal("plain-words.utilize", Row(list, "Missing.htm", "Topic removed").SubItems[1].Text);   // no lookup: the id
                }
            });
        }

        private static ListViewItem Row(ListView list, string topic, string reason)
        {
            return list.Items.Cast<ListViewItem>().Single(i => i.SubItems[0].Text == topic && i.SubItems[3].Text == reason);
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        /// <summary>Runs the body on an STA thread, the way the dialogs run in Flare, and rethrows what it raised.</summary>
        private static void Sta(Action body)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                try { body(); } catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The Exemptions dialog test didn't finish in 60 s.");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>The required text prompt behind Exempt This Text: OK waits for a reason.</summary>
    [Collection("Panes")]
    public class TextPromptTests
    {
        [Fact]
        public void OkIsOffUntilThereIsAReason()
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                try
                {
                    using (var form = TextPrompt.Build("Exempt This Text", "Why is this text exempt?", "", false, true))
                    {
                        var ok = form.Controls.OfType<Button>().Single(b => b.Text == "OK");
                        var box = form.Controls.OfType<TextBox>().Single();
                        Assert.False(ok.Enabled);
                        box.Text = "   ";
                        Assert.False(ok.Enabled);
                        box.Text = " Quoted in a glossary ";
                        Assert.True(ok.Enabled);
                        box.Text = "";
                        Assert.False(ok.Enabled);
                    }
                    using (var form = TextPrompt.Build("Edit", "Text", "", false, false))
                        Assert.True(form.Controls.OfType<Button>().Single(b => b.Text == "OK").Enabled);
                }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The text prompt test didn't finish in 60 s.");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
