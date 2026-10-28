using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Actions;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>
    /// The Exemptions dialog driven headless: one row per exempted text with the rule's title, the stale notes for a text or
    /// topic that is gone, and Remove Exemption writing the list and reloading. The project is a temp folder.
    /// </summary>
    [Collection("Panes")]
    public class ExemptionsFormTests : IDisposable
    {
        private const string Rule = "Utilize";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-exf-" + Guid.NewGuid().ToString("N"));
        private readonly string _anyPath;

        public ExemptionsFormTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Content"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(_root, @"Content\A.htm"), "<html><body><p>We utilize it.</p></body></html>");
            _anyPath = Path.Combine(_root, @"Content\A.htm");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private void Exempt(string topic, string text)
        {
            Exemptions.Add(_anyPath, topic, Rule, text, "", "Quoting the spec", "Ana", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        [Fact]
        public void ListsRowsShowsTheRuleTitleAndRemoves()
        {
            Exempt("A.htm", "We utilize it.");
            Sta(() =>
            {
                using (var f = new ExemptionsForm(_anyPath, id => id == Rule ? "Use plain words: Utilize" : null))
                {
                    f.Show();
                    Pump(() => f.Rows.Items.Count > 0);
                    Assert.Single(f.Rows.Items);
                    var cells = f.Rows.Items[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray();
                    Assert.Equal(new[] { "A.htm", "Use plain words: Utilize", "We utilize it.", "Quoting the spec", "Ana", "2026-10-01" }, cells);
                    Assert.True(f.RemoveButton.Enabled);

                    f.RemoveSelected();

                    Assert.Empty(Exemptions.Load(_anyPath).Entries);
                    Assert.Empty(f.Rows.Items);
                    Assert.StartsWith("Removed. It shows again the next time you lint.", f.Header);
                    Assert.False(f.RemoveButton.Enabled);
                }
            });
        }

        [Fact]
        public void MarksTextNotFoundAndTopicNotFound()
        {
            Exempt("A.htm", "We utilize it.");
            Exempt("A.htm", "Old wording that is gone.");
            Exempt("Missing.htm", "Anything at all.");
            Sta(() =>
            {
                using (var f = new ExemptionsForm(_anyPath, id => null))
                {
                    f.Show();
                    Pump(() => f.Rows.Items.Count > 0);
                    Assert.Equal(new[] { StaleNote.None, StaleNote.TextNotFound, StaleNote.TopicNotFound }, f.RowNotes.ToArray());
                    Assert.Equal("Utilize", f.Rows.Items[0].SubItems[1].Text);   // no title: the rule id shows
                    Assert.Contains("Text not found", f.Rows.Items[1].SubItems[2].Text);
                    Assert.Contains("Topic not found", f.Rows.Items[2].SubItems[2].Text);
                    Assert.Equal(3, Exemptions.Load(_anyPath).Entries.Count);   // checking never writes
                }
            });
        }

        private static void Pump(Func<bool> done, int ms = 15000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done()) return;
                Thread.Sleep(10);
            }
            throw new TimeoutException("The dialog didn't load its rows.");
        }

        private static void Sta(Action body)
        {
            Exception error = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The UI test didn't finish in 60 s.");
            if (error != null) throw new Exception(error.Message, error);
        }
    }
}
