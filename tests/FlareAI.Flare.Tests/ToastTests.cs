using System;
using System.Reflection;
using FlareAI.Core.Settings;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The notice over the editor closes on Esc only when it is showing and Flare is the active window (#449).</summary>
    [Collection("Panes")]
    public class ToastTests : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-toast-" + Guid.NewGuid().ToString("N"));
        public ToastTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; }
        public void Dispose() { FlareAI.Core.Paths.Root = null; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

        private static EditorIntegration Editor() { return new EditorIntegration(null, a => null, () => null, null, null, null, null); }

        [Fact]
        public void WithCheckOnSaveOff_SavingStartsNoCheckAndShowsNoToast()
        {
            var settings = PluginSettings.Load(); settings.CheckOnSave = false; settings.Save();
            var before = Toast.Last;
            // A check would need the editor's document; with the switch off nothing touches it, so a null document is fine.
            typeof(EditorIntegration).GetMethod("OnSaved", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Editor(), new object[] { null, null });
            Assert.Same(before, Toast.Last);
        }

        [Fact]
        public void WithReportBeforeBuildOff_BuildingStartsNoCheckAndShowsNoToast()
        {
            var settings = PluginSettings.Load(); settings.ReportBeforeBuild = false; settings.Save();
            var before = Toast.Last;
            Editor().OnCompileStart(System.IO.Path.Combine(_root, "Any.flprj"));   // returns before it reads the editor or the project
            Assert.Same(before, Toast.Last);
        }

        [Fact] public void Esc_ClosesAShowingNotice_OnThePress() { Assert.True(Toast.EscapeDismisses(true, false, true, true)); }
        [Fact] public void Esc_DoesNothing_WhenNoNoticeIsShowing() { Assert.False(Toast.EscapeDismisses(true, false, false, true)); }
        [Fact] public void Esc_DoesNothing_WhenHeldDown() { Assert.False(Toast.EscapeDismisses(true, true, true, true)); }
        [Fact] public void Esc_DoesNothing_WhenAnotherAppIsActive() { Assert.False(Toast.EscapeDismisses(true, false, true, false)); }
        [Fact] public void Esc_DoesNothing_WhenNotPressed() { Assert.False(Toast.EscapeDismisses(false, false, true, true)); }
    }
}
