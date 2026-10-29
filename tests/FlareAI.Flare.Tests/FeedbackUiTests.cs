using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Feedback;
using FlareAI.Core.Settings;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Feedback;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>
    /// The feedback dialogs (issue #124) laid out for real at 100% and 125%: nothing cut off or overlapping, the wording
    /// the owner approved, Details and the false-positive reasons showing exactly what is sent, and the send step against
    /// a local mock of the service (sent, then a 502: saved, with Open GitHub Issues and Open Folder). Nothing contacts the real service.
    /// </summary>
    [Collection("Panes")]
    public class FeedbackUiTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hs-fbui-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _launched = new List<string>();
        private readonly string _oldUrl = Environment.GetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar);

        public FeedbackUiTests()
        {
            Directory.CreateDirectory(_dir);
            FeedbackForm.SaveFolder = Path.Combine(_dir, "reports");
            FeedbackForm.Launch = t => _launched.Add(t);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, _oldUrl);
            FeedbackForm.SaveFolder = null;
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private ReportContext Context()
        {
            var log = Path.Combine(_dir, "diag.log");
            File.WriteAllText(log, "10:00  House Style starting\n10:01  Lint project :: InvalidOperationException\n");
            return new ReportContext { ClientId = "0f8fad5b-d9cb-469f-a165-70867728950e", PluginVersion = "0.9.1", FlareVersion = "MadCap Flare 22.1", Os = "Windows 11", DiagLogPath = log, Settings = new PluginSettings() };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception Thrown()
        {
            try { throw new InvalidOperationException("The list was changed."); }
            catch (Exception ex) { return ex; }
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void TheErrorDialog_FitsItsText_AndDetailsShowExactlyWhatIsSent(float scale)
        {
            Sta(() =>
            {
                using (var f = Offscreen(new ErrorReportForm("House Style couldn't lint the project.", Thrown(), Context(), scale)))
                {
                    f.Show();
                    Pump(null, 150);
                    Assert.Equal("House Style ran into a problem", f.Text);
                    var labels = All(f).OfType<Label>().Where(l => l.Visible).Select(l => l.Text).ToList();
                    Assert.Contains("House Style couldn't lint the project.", labels);
                    Assert.Contains("What were you doing?", labels);
                    Assert.Equal(new[] { "Send", "Don't Send" }, VisibleButtonsLeftToRight(f).Where(t => t != "Show Details").ToArray());
                    var auto = All(f).OfType<CheckBox>().Single();
                    Assert.Equal("Send error reports automatically", auto.Text);
                    Assert.False(auto.Checked, "automatic reports are off by default");
                    CheckFits(f, "collapsed at " + scale);

                    var height = f.ClientSize.Height;
                    Button(f, "Show Details").PerformClick();
                    Pump(null, 50);
                    var details = All(f).OfType<TextBox>().Single(t => t.ReadOnly);
                    Assert.True(details.Visible);
                    Assert.True(f.ClientSize.Height > height);
                    var o = JObject.Parse(details.Text);
                    Assert.Equal("error", (string)o["kind"]);
                    Assert.Equal("System.InvalidOperationException", (string)o["exception"]["type"]);
                    Assert.Null(o["user_note"]);
                    CheckFits(f, "expanded at " + scale);

                    All(f).OfType<TextBox>().Single(t => !t.ReadOnly).Text = "Linting the Transfers project.";
                    Pump(null, 20);
                    Assert.Equal("Linting the Transfers project.", (string)JObject.Parse(details.Text)["user_note"]);
                    Assert.Equal("Hide Details", Button(f, "Hide Details").Text);
                }
            });
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void TheFalsePositiveDialog_OffersTheThreeReasons_AndSendsTheSentenceOnlyWhenTicked(float scale)
        {
            Sta(() =>
            {
                using (var f = Offscreen(new FalsePositiveForm("google:quotation-marks/2", "Put periods inside quotation marks", "feeding.", "Care and feeding.", Context(), scale)))
                {
                    f.Show();
                    Pump(null, 150);
                    Assert.Equal("House Style — Report False Positive", f.Text);
                    Assert.Equal(new[] { "The text is correct as written", "The suggested change is wrong", "This rule doesn't fit our docs" },
                                 All(f).OfType<RadioButton>().Select(r => r.Text).ToArray());
                    Assert.True(All(f).OfType<RadioButton>().All(r => !r.Checked));
                    var send = Button(f, "Send");
                    Assert.False(send.Enabled, "Send waits for a reason");
                    Assert.False(f.IncludeSentence.Checked);
                    Assert.Equal("Include the surrounding sentence", f.IncludeSentence.Text);
                    CheckFits(f, "false positive at " + scale);

                    f.Reasons[1].Checked = true;
                    Assert.True(send.Enabled);
                    var o = JObject.Parse(f.PayloadJson());
                    Assert.Equal("change_wrong", (string)o["reason"]);
                    Assert.Equal("google:quotation-marks/2", (string)o["rule_id"]);
                    Assert.Equal("Put periods inside quotation marks", (string)o["rule_name"]);
                    Assert.Equal("feeding.", (string)o["flagged_text"]);
                    Assert.Null(o["context"]);
                    f.IncludeSentence.Checked = true;
                    Assert.Equal("Care and feeding.", (string)JObject.Parse(f.PayloadJson())["context"]);
                }
            });
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void ReportAProblem_NeedsWhatHappened_AndFits(float scale)
        {
            Sta(() =>
            {
                using (var f = Offscreen(new ReportProblemForm(Context(), scale)))
                {
                    f.Show();
                    Pump(null, 150);
                    var labels = All(f).OfType<Label>().Select(l => l.Text).ToList();
                    Assert.Contains(ReportProblemForm.Prompt, labels);
                    Assert.DoesNotContain("What did you expect?", labels);
                    Assert.True(All(f).OfType<CheckBox>().Single(c => c.Text == "Include diagnostic details").Checked);
                    Assert.Equal(new[] { "Send", "Cancel" }, VisibleButtonsLeftToRight(f).ToArray());
                    Assert.False(Button(f, "Send").Enabled);
                    f.WhatHappened.Text = "The pane went blank.";
                    Assert.True(Button(f, "Send").Enabled);
                    CheckFits(f, "report a problem at " + scale);
                }
            });
        }

        [Fact]
        public void Send_ReachesTheService_AndAFailure_IsSaved_WithWhereToSendIt()
        {
            Sta(() =>
            {
                using (var server = new MockFeedbackServer())
                {
                    Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, server.BaseUrl);
                    using (var f = Offscreen(new ErrorReportForm("House Style couldn't lint the project.", Thrown(), Context())))
                    {
                        f.Show();
                        Pump(null, 100);
                        All(f).OfType<TextBox>().Single(t => !t.ReadOnly).Text = "Linting.";
                        Button(f, "Show Details").PerformClick();
                        var shown = All(f).OfType<TextBox>().Single(t => t.ReadOnly).Text;
                        Button(f, "Send").PerformClick();
                        Pump(() => f.Result != null, 15000);
                        Assert.True(f.Result.Ok, f.Result.ToString());
                        var req = server.Requests.Single();
                        Assert.Equal("/v1/report", req.Path);
                        Assert.Equal(shown, req.Body);   // exactly what Details showed
                    }

                    server.Respond = q => MockFeedbackServer.Answer(502, "{\"ok\":false,\"error\":\"GitHub couldn't be reached.\"}");
                    using (var f = Offscreen(new ErrorReportForm("House Style couldn't lint the project.", Thrown(), Context())))
                    {
                        f.Show();
                        Pump(null, 100);
                        Assert.False(Button(f, "Open GitHub Issues").Visible);
                        Button(f, "Send").PerformClick();
                        Pump(() => f.Result != null, 15000);
                        Pump(null, 50);
                        Assert.False(f.Result.Ok);
                        // Saved at once, and the dialog says where to send it.
                        Assert.True(File.Exists(f.SavedPath));
                        Assert.StartsWith(Path.Combine(_dir, "reports"), f.SavedPath);
                        Assert.Contains("House Style couldn't send the report. It's saved in Documents\\House Style Reports. Attach it to a new issue on GitHub.",
                                        All(f).OfType<Label>().Where(l => l.Visible).Select(l => l.Text));
                        Assert.Equal(new[] { "Open GitHub Issues", "Open Folder", "Try Again", "Don't Send" }, VisibleButtonsLeftToRight(f).Where(b => b != "Show Details").ToArray());
                        CheckFits(f, "after a failed send");
                        Button(f, "Open GitHub Issues").PerformClick();
                        Button(f, "Open Folder").PerformClick();
                        Assert.Equal(new[] { "https://github.com/abmorrill4/house-style/issues/new", "/select,\"" + f.SavedPath + "\"" }, _launched.ToArray());
                    }
                }
            });
        }

        [Fact]
        public void AnIssuesRightClickMenu_AndIgnoreMenu_OfferReportFalsePositive()
        {
            Sta(() =>
            {
                var doc = FlareAI.TestKit.Editor.FlareSimDocument.FromXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1><p>In order to move money, open the Transfers page.</p></body></html>");
                var runner = new FlareAI.Core.Actions.ActionRunner(new FlareAI.TestKit.Llm.ScriptedLlmClient().When(r => true, r => FlareAI.TestKit.Llm.ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new FlareAI.Core.Actions.PromptBuilder(null), 1024);
                using (var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, false))
                {
                    var list = All(panel).OfType<ListView>().Single();
                    var rowMenu = list.ContextMenuStrip.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                    Assert.Equal(new[] { "Go To", "Mark as Fixed", "Open File", "Copy File Path", "", "Exclude File from Lint Project", "Exclude Folder from Lint Project", "", FindingsPanel.ReportFalsePositiveLabel, "Exempt This Text…" }, rowMenu.ToArray());
                    var ignore = (ContextMenuStrip)typeof(FindingsPanel).GetField("_rejectMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(panel);
                    Assert.Equal(FindingsPanel.ReportFalsePositiveLabel, ignore.Items.Cast<ToolStripItem>().Last().Text);
                    Assert.Equal("Report False Positive…", FindingsPanel.ReportFalsePositiveLabel);
                }
            });
        }

        /// <summary>Tester report: the issues window closed by mistake comes back with its results (Reopen Issues).</summary>
        [DesktopFact]
        public void AClosedIssuesWindow_Reopens_WithTheSamePanel()
        {
            Sta(() =>
            {
                var doc = FlareAI.TestKit.Editor.FlareSimDocument.FromXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><p>Open the Transfers page.</p></body></html>");
                var runner = new FlareAI.Core.Actions.ActionRunner(new FlareAI.TestKit.Llm.ScriptedLlmClient().When(r => true, r => FlareAI.TestKit.Llm.ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new FlareAI.Core.Actions.PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, false);
                PanelHost.Show(panel, null);   // no Flare main form: the floating window
                var first = panel.FindForm();
                Assert.NotNull(first);
                first.Close();
                Assert.True(first.IsDisposed);
                Assert.False(panel.IsDisposed);
                Assert.Null(PanelHost.Current);
                Assert.True(PanelHost.Reopen(null));
                Assert.Same(panel, PanelHost.Current);
                var second = panel.FindForm();
                Assert.NotSame(first, second);
                Assert.True(second.Visible);
                second.Close();
                panel.Dispose();
                Assert.False(PanelHost.Reopen(null));   // nothing left to show: the command lints instead
            });
        }

        /// <summary>Tester report: scrolled to the bottom, the last line of What's New (the release notes link) was cut off.</summary>
        [Fact]
        public void WhatsNew_ScrolledToTheBottom_ShowsTheLastLineWhole()
        {
            Sta(() =>
            {
                var md = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whats-new.md"));
                var sections = FlareAI.Core.Update.WhatsNew.Parse(md).UpTo(null);
                using (var f = Offscreen(new WhatsNewForm(sections, null, false)))
                {
                    f.Show();
                    Pump(null, 150);
                    var view = All(f).OfType<ScrollableControl>().First(p => p.AutoScroll);
                    var notes = All(f).OfType<FlowLayoutPanel>().First(p => p.AccessibleName == "Release notes");
                    var last = notes.Controls.Cast<Control>().Last();
                    view.AutoScrollPosition = new Point(0, 100000);   // scrolled all the way down
                    Pump(null, 50);
                    var bottom = view.PointToClient(last.PointToScreen(new Point(0, last.Height))).Y;
                    Assert.True(bottom <= view.ClientSize.Height, "last line ends at " + bottom + ", view is " + view.ClientSize.Height + " high");
                    Assert.True(last.PreferredSize.Height <= last.Height + 1, "last line needs " + last.PreferredSize.Height + " px, has " + last.Height);
                }
            });
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private static T Offscreen<T>(T f) where T : Form
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-20000, -20000);
            f.ShowInTaskbar = false;
            return f;
        }

        private static Button Button(Form f, string text) { return All(f).OfType<Button>().Single(b => b.Text == text); }

        private static IEnumerable<string> VisibleButtonsLeftToRight(Form f)
        {
            return All(f).OfType<Button>().Where(b => b.Visible).OrderBy(b => b.RectangleToScreen(b.ClientRectangle).Top / 10)
                         .ThenBy(b => b.RectangleToScreen(b.ClientRectangle).Left).Select(b => b.Text);
        }

        /// <summary>Every visible control is inside the dialog, rows don't overlap, and wrapped text is as tall as it needs.</summary>
        private static void CheckFits(Form f, string where)
        {
            var client = f.ClientRectangle;
            var shown = All(f).Where(c => c.Visible).ToList();
            foreach (var c in shown)
            {
                var r = f.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                Assert.True(client.Contains(r), where + ": " + Describe(c) + " " + r + " is outside " + client);
                if (c is Label || c is CheckBox || c is RadioButton)
                {
                    var glyph = c is Label ? 0 : (int)Math.Round(24 * f.Font.Height / (double)SystemFonts.MessageBoxFont.Height);
                    var need = TextRenderer.MeasureText(string.IsNullOrEmpty(c.Text) ? " " : c.Text, c.Font, new Size(Math.Max(1, c.Width - glyph), 0),
                                                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height;
                    Assert.True(c.Height >= need, where + ": " + Describe(c) + " is cut off (" + c.Height + " < " + need + ")");
                }
                if (c is Button)
                    Assert.True(c.Width >= TextRenderer.MeasureText(c.Text, c.Font).Width, where + ": " + Describe(c) + " is too narrow for its text");
            }
            var rows = shown.Where(c => c.Parent == f).ToList();
            for (var i = 0; i < rows.Count; i++)
                for (var j = i + 1; j < rows.Count; j++)
                    Assert.False(rows[i].Bounds.IntersectsWith(rows[j].Bounds), where + ": " + Describe(rows[i]) + " overlaps " + Describe(rows[j]));
        }

        private static string Describe(Control c) { return c.GetType().Name + " '" + c.Text + "'"; }

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
            if (!t.Join(180000)) throw new TimeoutException("The UI test didn't finish in 180 s.");   // a loaded CI runner took the suite from 50 s to 2 min (2026-10-29)
            if (error != null) throw new Exception(error.Message, error);
        }
    }
}
