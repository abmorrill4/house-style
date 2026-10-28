using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    /// The Exemptions dialog (issue #29) driven for real: a row per exemption with its rule title, the stale notes for a
    /// text or topic that is gone, and Remove Exemption writing the file and showing the Removed header.
    /// </summary>
    [Collection("Panes")]
    public class ExemptionsFormTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hs-exform-" + Guid.NewGuid().ToString("N"));

        public ExemptionsFormTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Content"));
            File.WriteAllText(Path.Combine(_root, "Project.flprj"), "");
            File.WriteAllText(Path.Combine(_root, "Content", "a.htm"), "<html><body><p>Care and feeding.</p></body></html>");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        [Fact]
        public void ListsRowsShowsTheRuleTitleAndRemoves()
        {
            WriteExemptions(
                Line("a.htm", "google:quotation-marks/2", "feeding.", "Care and ", "Brand term", "Ana", "2026-10-01"),
                Line("a.htm", "custom:spacing", "feeding.", "Care and ", "Sounds right", "Ben", "2026-10-02"));
            Sta(() =>
            {
                using (var f = Offscreen(new ExemptionsForm(_root, key => key == "google:quotation-marks/2" ? "Put periods inside quotation marks" : null)))
                {
                    f.Show();
                    Pump(null, 150);
                    Assert.Equal("Exempted text", List(f).AccessibleName);
                    Assert.Equal(new[] { "Topic", "Rule", "Text", "Reason", "Who", "When" }, List(f).Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray());
                    Assert.Equal(2, f.Count);
                    Assert.Equal(new[] { "a.htm", "Put periods inside quotation marks", "feeding.", "Brand term", "Ana", "2026-10-01" }, Cells(List(f).Items[0]));
                    Assert.Equal("custom:spacing", Cells(List(f).Items[1])[1]);

                    var remove = Button(f, "Remove Exemption");
                    Assert.True(remove.Enabled, "the first row is selected when the dialog opens");
                    remove.PerformClick();

                    Assert.Equal(1, f.Count);
                    Assert.Equal("custom:spacing", Cells(List(f).Items[0])[1]);
                    Assert.Contains("Removed. It shows again the next time you lint.", HeaderText(f));
                    var left = Exemptions.Load(_root).Entries;
                    Assert.Single(left);
                    Assert.Equal("custom:spacing", left[0].Rule);
                }
            });
        }

        [Fact]
        public void MarksTextNotFoundAndTopicNotFound()
        {
            WriteExemptions(
                Line("a.htm", "google:quotation-marks/2", "feeding.", "Care and ", "Brand term", "Ana", "2026-10-01"),
                Line("a.htm", "custom:spacing", "missing words.", "", "Gone now", "Ben", "2026-10-02"),
                Line("gone.htm", "custom:spacing", "feeding.", "Care and ", "Topic deleted", "Cy", "2026-10-03"));
            Sta(() =>
            {
                using (var f = Offscreen(new ExemptionsForm(_root, key => null)))
                {
                    f.Show();
                    Pump(null, 150);
                    Assert.Equal(3, f.Count);
                    Assert.Equal("feeding.", Cells(List(f).Items[0])[2]);
                    Assert.Equal("missing words. (Text not found)", Cells(List(f).Items[1])[2]);
                    Assert.Equal("feeding. (Topic not found)", Cells(List(f).Items[2])[2]);
                    Assert.Equal("custom:spacing", Cells(List(f).Items[2])[1]);
                }
            });
        }

        private void WriteExemptions(params string[] lines)
        {
            var file = Exemptions.FileFor(_root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllLines(file, lines);
        }

        private static string Line(params string[] fields) { return string.Join("\t", fields); }

        private static string[] Cells(ListViewItem item) { return item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray(); }

        private static ExemptionsForm Offscreen(ExemptionsForm f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-20000, -20000);
            f.ShowInTaskbar = false;
            return f;
        }

        private static ListView List(Form f) { return All(f).OfType<ListView>().Single(); }

        private static Button Button(Form f, string text) { return All(f).OfType<Button>().Single(b => b.Text == text); }

        /// <summary>The header label: the one docked to the top.</summary>
        private static string HeaderText(Form f) { return All(f).OfType<Label>().Single(l => l.Dock == DockStyle.Top).Text; }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        private static void Pump(Func<bool> done, int ms = 15000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done != null && done()) { Application.DoEvents(); return; }
                Thread.Sleep(10);
            }
            if (done != null) throw new TimeoutException("The dialog didn't finish.");
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
