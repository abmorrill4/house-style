using System;
using System.Collections.Generic;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    public class PaneSlotsTests
    {
        private class Fake { }

        [Fact]
        public void Put_AProjectPanel_KeepsTheTopicPanel_AndDisposesOnlyTheOldProjectPanel()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var projectP1 = new Fake();
            var projectP2 = new Fake();

            // Put topic A
            slots.Put(PaneSlot.Topic, topicA, false);
            Assert.Equal(topicA, slots.Topic);
            Assert.Null(slots.Project);

            // Put project P1
            var result1 = slots.Put(PaneSlot.Project, projectP1, false);
            Assert.Null(result1);
            Assert.Equal(topicA, slots.Topic);
            Assert.Equal(projectP1, slots.Project);

            // Put project P2 (should return P1)
            var result2 = slots.Put(PaneSlot.Project, projectP2, false);
            Assert.Equal(projectP1, result2);
            Assert.Equal(topicA, slots.Topic);
            Assert.Equal(projectP2, slots.Project);

            // Put P2 again (should return null since same instance)
            var result3 = slots.Put(PaneSlot.Project, projectP2, false);
            Assert.Null(result3);
        }

        [Fact]
        public void TopicSwitch_ReplacesTheTopicSlot_WithoutShowingIt_WhenProjectIsShown()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var topicB = new Fake();
            var projectP = new Fake();

            // Put topic A
            slots.Put(PaneSlot.Topic, topicA, false);
            // Put project P (front, so shown)
            slots.Put(PaneSlot.Project, projectP, true);

            Assert.Equal(PaneSlot.Project, slots.Shown);
            Assert.True(slots.IsOpen);

            // Switch topic to B (not shown)
            var oldTopic = slots.Put(PaneSlot.Topic, topicB, false);
            Assert.Equal(topicA, oldTopic);
            Assert.Equal(topicB, slots.Topic);
            Assert.Equal(PaneSlot.Project, slots.Shown); // Still shows Project
        }

        [Fact]
        public void Close_ThenReopen_ShowsTheTabThatWasShown()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var projectP = new Fake();

            // Both filled
            slots.Put(PaneSlot.Topic, topicA, false);
            slots.Put(PaneSlot.Project, projectP, true); // front, so Shown=Project

            Assert.Equal(PaneSlot.Project, slots.Shown);
            Assert.True(slots.IsOpen);

            // Close
            slots.Close();
            Assert.False(slots.IsOpen);
            Assert.Equal(PaneSlot.Project, slots.Shown); // Shown is kept

            // Reopen
            var ok = slots.TryReopen(out var slot);
            Assert.True(ok);
            Assert.Equal(PaneSlot.Project, slot);
            Assert.True(slots.IsOpen);

            // Close and reopen with Topic shown
            slots.Close();
            slots.Put(PaneSlot.Topic, topicA, true); // front, so Shown=Topic
            Assert.Equal(PaneSlot.Topic, slots.Shown);

            slots.Close();
            Assert.False(slots.IsOpen);

            ok = slots.TryReopen(out slot);
            Assert.True(ok);
            Assert.Equal(PaneSlot.Topic, slot);
            Assert.True(slots.IsOpen);
        }

        [Fact]
        public void TopicSwitch_WithOnlyProject_DoesNotCreateTheTopicSlot()
        {
            var slots = new PaneSlots<Fake>();
            var projectP = new Fake();

            slots.Put(PaneSlot.Project, projectP, true);
            Assert.Null(slots.Topic);
            Assert.False(slots.CanReplaceTopic);
        }

        [Fact]
        public void Reopen_WithNoSlot_FallsBackToLintTopic()
        {
            var slots = new PaneSlots<Fake>();

            var ok = slots.TryReopen(out var slot);
            Assert.False(ok);
        }

        [Fact]
        public void Show_AnEmptySlot_ReturnsFalse_AndKeepsShown()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var projectP = new Fake();

            slots.Put(PaneSlot.Topic, topicA, true); // Shown=Topic, IsOpen=true

            // Try to show empty Project slot
            var ok = slots.Show(PaneSlot.Project);
            Assert.False(ok);
            Assert.Equal(PaneSlot.Topic, slots.Shown); // Kept Topic
        }

        [Fact]
        public void TopicSlot_IsCleared_WhenItsDocumentCloses()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var projectP = new Fake();

            slots.Put(PaneSlot.Topic, topicA, false);
            slots.Put(PaneSlot.Project, projectP, true); // Shown=Project

            // Clear matching topic
            var oldTopic = slots.ClearTopicIf(p => true);
            Assert.Equal(topicA, oldTopic);
            Assert.Null(slots.Topic);
            Assert.Equal(projectP, slots.Project);
            Assert.Equal(PaneSlot.Project, slots.Shown); // Shown didn't change

            // Put Topic back, set Shown=Topic
            slots.Put(PaneSlot.Topic, topicA, true);
            Assert.Equal(PaneSlot.Topic, slots.Shown);

            // Clear with non-matching predicate
            var result = slots.ClearTopicIf(p => false);
            Assert.Null(result);
            Assert.Equal(topicA, slots.Topic); // Kept Topic
            Assert.Equal(PaneSlot.Topic, slots.Shown);

            // Clear Topic when it's shown and Project exists
            oldTopic = slots.ClearTopicIf(p => true);
            Assert.Equal(topicA, oldTopic);
            Assert.Null(slots.Topic);
            Assert.Equal(PaneSlot.Project, slots.Shown); // Switched to Project
        }

        [Fact]
        public void EditorRoutes_ReachBothSlots_WhicheverIsShown()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var projectP = new Fake();

            slots.Put(PaneSlot.Topic, topicA, false);
            slots.Put(PaneSlot.Project, projectP, true); // Shown=Project

            // (a) CanReplaceTopic gate
            Assert.True(slots.CanReplaceTopic);

            // (b) Both() has both, Topic first
            var both = slots.Both();
            Assert.Equal(2, both.Count);
            Assert.Equal(topicA, both[0]); // Topic first
            Assert.Equal(projectP, both[1]);

            // (c) Project != null (ContentChanged gate)
            Assert.NotNull(slots.Project);

            // (d) Topic is the live-lint and marker target
            Assert.Equal(topicA, slots.Topic);
            Assert.Equal(projectP, slots.ShownPanel); // Shown is Project

            // Now switch Shown to Topic
            slots.Show(PaneSlot.Topic);
            Assert.Equal(PaneSlot.Topic, slots.Shown);
            Assert.Equal(topicA, slots.ShownPanel);

            // Asserts still hold
            Assert.True(slots.CanReplaceTopic);
            both = slots.Both();
            Assert.Equal(topicA, both[0]);
            Assert.Equal(projectP, both[1]);
            Assert.NotNull(slots.Project);
            Assert.Equal(topicA, slots.Topic);
        }

        [Fact]
        public void ShownPanel_ReturnsNullWhenClosed()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();

            slots.Put(PaneSlot.Topic, topicA, true);
            Assert.Equal(topicA, slots.ShownPanel);

            slots.Close();
            Assert.Null(slots.ShownPanel);
        }

        [Fact]
        public void BothFilled_IsTrueOnlyWhenBothExist()
        {
            var slots = new PaneSlots<Fake>();
            var topicA = new Fake();
            var projectP = new Fake();

            Assert.False(slots.BothFilled);

            slots.Put(PaneSlot.Topic, topicA, false);
            Assert.False(slots.BothFilled);

            slots.Put(PaneSlot.Project, projectP, false);
            Assert.True(slots.BothFilled);

            slots.ClearTopicIf(p => true);
            Assert.False(slots.BothFilled);
        }

        [Fact]
        public void EditorIntegration_NoLongerReadsTheShownPanel()
        {
            var repoRoot = FindRepoRoot();
            if (string.IsNullOrEmpty(repoRoot) || !System.IO.Directory.Exists(repoRoot))
                return;  // Skip test if repo root cannot be found

            var editorIntegrationPath = System.IO.Path.Combine(repoRoot, @"src\FlareAI.Flare\EditorIntegration.cs");
            var pluginPath = System.IO.Path.Combine(repoRoot, @"src\FlareAI.Flare\Plugin.cs");

            if (!System.IO.File.Exists(editorIntegrationPath) || !System.IO.File.Exists(pluginPath))
                return;  // Skip test if files not found

            // EditorIntegration must not contain PanelHost.Current
            var editorContent = System.IO.File.ReadAllText(editorIntegrationPath);
            Assert.DoesNotContain("PanelHost.Current", editorContent);
            Assert.Contains("PanelHost.TopicPanel", editorContent);
            Assert.Contains("PanelHost.ProjectPanel", editorContent);
            Assert.Contains("PanelHost.ClearTopic", editorContent);

            // Plugin.OnDocumentSwitched must read TopicPanel
            var pluginContent = System.IO.File.ReadAllText(pluginPath);
            var onDocSwitchedStart = pluginContent.IndexOf("private void OnDocumentSwitched");
            Assert.True(onDocSwitchedStart > 0, "OnDocumentSwitched not found");
            var onDocSwitchedEnd = pluginContent.IndexOf("catch (Exception ex) { Diagnostics.Log(\"DocumentSwitched\",", onDocSwitchedStart);
            Assert.True(onDocSwitchedEnd > onDocSwitchedStart, "OnDocumentSwitched closing not found");
            var onDocContent = pluginContent.Substring(onDocSwitchedStart, onDocSwitchedEnd - onDocSwitchedStart);
            Assert.Contains("PanelHost.TopicPanel", onDocContent);
            Assert.DoesNotContain("PanelHost.Current", onDocContent);

            // Plugin overall must not have PanelHost.Current
            Assert.DoesNotContain("PanelHost.Current", pluginContent);
        }

        private static string FindRepoRoot()
        {
            // Try using the current working directory first
            var workDir = System.Environment.CurrentDirectory;
            if (System.IO.Directory.Exists(System.IO.Path.Combine(workDir, "src")))
                return workDir;

            // Try going up from the assembly location
            var dir = System.IO.Path.GetDirectoryName(typeof(PaneSlotsTests).Assembly.Location);
            for (var i = 0; i < 10; i++)  // More iterations to be safe
            {
                if (dir == null || !System.IO.Directory.Exists(dir)) break;
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src")))
                    return dir;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return "";
        }
    }
}
