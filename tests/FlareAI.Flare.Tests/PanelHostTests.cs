using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Actions;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>How the panel host tracks which panels are shown and what closing the pane does (#273).</summary>
    [Collection("Panes")]   // panes share static state: never two at once
    public class PanelHostTests : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-host-" + Guid.NewGuid().ToString("N"));
        private readonly string _prevRoot = FlareAI.Core.Paths.Root;
        public PanelHostTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; FlareAI.Core.Settings.GuidedTour.Stop(); }
        public void Dispose() { FlareAI.Core.Paths.Root = _prevRoot; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

        private const string Topic = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1>" +
                                     "<p>Please note that the fee is waived for members who move money between their own accounts.</p></body></html>";

        private static void Run(Action<FindingsPanel, FindingsPanel> body, bool withProject)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                PanelHost.ForceFloating = true;
                PanelHost.ResetForTests();
                try
                {
                    var doc = FlareSimDocument.FromXml(Topic);
                    var runner = new ActionRunner(new ScriptedLlmClient(), new PromptBuilder(null), 1024);
                    var topicPanel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                    var projectPanel = withProject
                        ? FindingsPanel.ForProject(runner, doc, () => runner, p => null, new List<string> { "a.htm" }, "Project")
                        : null;
                    body(topicPanel, projectPanel);
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    try { PanelHost.BeginClose(); } catch (Exception) { }
                    PanelHost.ForceFloating = false;
                    PanelHost.ResetForTests();
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The panel host test didn't finish in 60 s.");
            if (error != null) throw new Exception(error.Message, error);
        }

        [DesktopFact]
        public void IsShown_IsTrueForBothTabsWhileOpen_AndFalseAfterClose()
        {
            Run((topicPanel, projectPanel) =>
            {
                PanelHost.Show(topicPanel, null);
                PanelHost.Show(projectPanel, null);
                Assert.True(PanelHost.IsOpen);
                Assert.True(PanelHost.IsShown(topicPanel));
                Assert.True(PanelHost.IsShown(projectPanel));

                PanelHost.BeginClose();
                Assert.False(PanelHost.IsOpen);
                Assert.False(PanelHost.IsShown(topicPanel));
                Assert.False(PanelHost.IsShown(projectPanel));
            }, true);
        }

        [DesktopFact]
        public void ClearTopic_WithOnlyTheTopicPanel_ClosesThePane()
        {
            Run((topicPanel, projectPanel) =>
            {
                PanelHost.Show(topicPanel, null);
                Assert.True(PanelHost.IsOpen);

                PanelHost.ClearTopic(topicPanel.TopicPath);

                Assert.False(PanelHost.IsOpen);
                Assert.Null(PanelHost.TopicPanel);
                Assert.False(PanelHost.Reopen(null));
            }, false);
        }

        [DesktopFact]
        public void ClearTopic_WithAProjectPanel_KeepsThePaneOpenOnProject()
        {
            Run((topicPanel, projectPanel) =>
            {
                PanelHost.Show(topicPanel, null);
                PanelHost.Show(projectPanel, null);

                PanelHost.ClearTopic(topicPanel.TopicPath);

                Assert.True(PanelHost.IsOpen);
                Assert.Null(PanelHost.TopicPanel);
                Assert.Same(projectPanel, PanelHost.Current);
            }, true);
        }

        [DesktopFact]
        public void ShowingTwice_ThenClosing_ClosesOnce()
        {
            Run((topicPanel, projectPanel) =>
            {
                PanelHost.Show(topicPanel, null);
                PanelHost.Show(topicPanel, null);
                Assert.True(PanelHost.IsOpen);

                var close = typeof(FindingsPanel).GetField("CloseRequested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(close);
                var handler = (EventHandler)close.GetValue(topicPanel);
                Assert.NotNull(handler);
                Assert.Single(handler.GetInvocationList());   // wired once, not once per Show
                handler(topicPanel, EventArgs.Empty);

                Assert.False(PanelHost.IsOpen);
            }, false);
        }
    }
}
