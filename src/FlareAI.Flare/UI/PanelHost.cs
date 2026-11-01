using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using FlareAI.Core;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// Shows a findings panel in Flare's own docking system (a pane on the right, like Flare's built-in panes);
    /// falls back to a floating window if that is unavailable. Everything that touches Flare's internal pane API
    /// (B3.AppKit, not part of the public plugin API) sits in <see cref="FlarePane"/> and runs under try/catch,
    /// so a Flare update can only downgrade the sidebar to a window, never break the plugin.
    /// </summary>
    public static class PanelHost
    {
        private static bool _paneBroken;
        private static PaneSlots<FindingsPanel> Slots = new PaneSlots<FindingsPanel>();
        private static Form _floating;
        private static bool _changedSubscribed = false;
        private static Form _owner;
        private static readonly ConditionalWeakTable<FindingsPanel, object> Wired = new ConditionalWeakTable<FindingsPanel, object>();

        // For tests only: forget every panel and window an earlier test left behind
        internal static void ResetForTests()
        {
            Slots = new PaneSlots<FindingsPanel>();
            _floating = null;
            _paneBroken = false;
        }

        // For tests only: force floating mode (no docked pane)
        internal static bool ForceFloating { get; set; }

        /// <summary>The findings panel currently shown (docked or floating), or null once closed.</summary>
        public static FindingsPanel Current { get { return Slots.IsOpen && Slots.ShownPanel != null && !Slots.ShownPanel.IsDisposed && Slots.ShownPanel.Parent != null ? Slots.ShownPanel : null; } }

        internal static bool IsOpen { get { return Slots.IsOpen; } }

        internal static bool IsShown(FindingsPanel p) { return p != null && Slots.IsOpen && (ReferenceEquals(Slots.Topic, p) || ReferenceEquals(Slots.Project, p)); }

        /// <summary>The topic panel if one exists, regardless of whether it's shown or the pane is closed.</summary>
        public static FindingsPanel TopicPanel { get { return Slots.Topic != null && !Slots.Topic.IsDisposed ? Slots.Topic : null; } }

        /// <summary>The project panel if one exists, regardless of whether it's shown or the pane is closed.</summary>
        public static FindingsPanel ProjectPanel { get { return Slots.Project != null && !Slots.Project.IsDisposed ? Slots.Project : null; } }

        /// <summary>Shows the last panel again, with its results, after the writer closed it; false if there is none.</summary>
        public static bool Reopen(Form flareMainForm)
        {
            PaneSlot slot;
            if (!Slots.TryReopen(out slot)) return false;
            var panel = Slots.Get(slot);
            if (panel == null || panel.IsDisposed) return false;
            Slots.Show(slot);
            Display(panel, flareMainForm);
            return true;
        }

        public static void BeginClose()
        {
            CloseAll();
        }

        private static void Unwatch()
        {
            foreach (var p in Slots.Both())
            {
                try { p.WatchWrites(false); } catch (Exception ex) { Diagnostics.Log("unwatch: " + ex.GetType().Name); }
            }
        }

        private static void CloseAll()
        {
            Slots.Close();
            Unwatch();
            DetachAll();
        }

        public static void Show(FindingsPanel panel, Form flareMainForm)
        {
            Show(panel, flareMainForm, true);
        }

        public static void Show(FindingsPanel panel, Form flareMainForm, bool front)
        {
            var old = Slots.Put(panel.Slot, panel, front);
            if (old != null && old != panel) Discard(old);

            _owner = flareMainForm;
            object o;
            if (!Wired.TryGetValue(panel, out o))
            {
                Wired.Add(panel, new object());
                panel.CloseRequested += (s, e) => CloseAll();
                panel.TabRequested += (slot) => ShowSlot(slot, _owner);
            }

            if (!_changedSubscribed)
            {
                _changedSubscribed = true;
                FindingsPanel.Changed += (p) => PushCounts();
            }

            panel.WatchWrites(true);
            if (front) Display(panel, flareMainForm);
            else { SyncHost(); panel.Begin(); }   // a hidden panel still lints
            PushCounts();
        }

        /// <summary>Puts the shown panel in the pane (or the floating window) and starts it.</summary>
        private static void Display(FindingsPanel panel, Form owner)
        {
            foreach (var p in Slots.Both())
            {
                try { p.WatchWrites(true); } catch (Exception ex) { Diagnostics.Log("watch: " + ex.GetType().Name); }
            }
            if (!_paneBroken && !ForceFloating)
            {
                try
                {
                    if (FlarePane.Show(panel, owner)) { SyncHost(); PushCounts(); panel.Begin(); return; }
                }
                catch (Exception ex) // TypeLoadException, MissingMethodException, FileNotFoundException, InvalidCastException, …
                {
                    _paneBroken = true;
                    Diagnostics.Log("Docked pane unavailable, using a floating window", ex);
                }
            }
            ShowFloating(panel, owner);
            PushCounts();
        }

        private static void Discard(FindingsPanel old)
        {
            try { old.WatchWrites(false); } catch (Exception ex) { Diagnostics.Log("unwatch: " + ex.GetType().Name); }
            if (_floating != null && !_floating.IsDisposed && old.Parent == _floating) try { _floating.Controls.Remove(old); } catch (Exception) { }
            if (!_paneBroken && !ForceFloating) try { FlarePane.RemoveContent(old); } catch (Exception) { }
            try { old.Dispose(); } catch (Exception ex) { Diagnostics.Log("dispose old: " + ex.GetType().Name); }
        }

        /// <summary>Takes both panels out of their host so closing the host doesn't dispose them (Reopen needs them).</summary>
        private static void DetachAll()
        {
            var f = _floating;
            foreach (var p in Slots.Both())
            {
                if (f != null && !f.IsDisposed && p.Parent == f) try { f.Controls.Remove(p); } catch (Exception) { }
            }
            if (!_paneBroken && !ForceFloating) try { FlarePane.RemoveAll(); } catch (Exception) { }
            if (f != null && !f.IsDisposed) try { f.Close(); } catch (Exception) { }
        }

        /// <summary>Makes the host show exactly the shown slot, adding a panel the host doesn't hold yet.</summary>
        private static void SyncHost()
        {
            var shown = Slots.ShownPanel;
            if (shown == null || shown.IsDisposed) return;
            if (_floating != null && !_floating.IsDisposed)
            {
                foreach (var p in Slots.Both())
                {
                    if (p.Parent != _floating) { p.Dock = DockStyle.Fill; _floating.Controls.Add(p); }
                    p.Visible = p == shown;
                }
                _floating.Text = shown.Title;
                _floating.AccessibleName = shown.Title;
            }
            else if (!_paneBroken && !ForceFloating)
            {
                try
                {
                    foreach (var p in Slots.Both()) FlarePane.AddContent(p);
                    FlarePane.UpdateVisible(shown);
                }
                catch (Exception) { _paneBroken = true; }
            }
        }

        private static void ShowSlot(PaneSlot slot, Form owner)
        {
            if (!Slots.Show(slot)) return;
            var shown = Slots.ShownPanel;
            if (shown == null || shown.IsDisposed) return;
            if (Slots.IsOpen)
            {
                SyncHost();
                // Focusing a tab while the other panel's tab is still handling its click locks the form up, so let the click finish first.
                if (shown.IsHandleCreated) shown.BeginInvoke(new Action(() => { if (!shown.IsDisposed) shown.FocusAfterSwitch(); }));
                else shown.FocusAfterSwitch();
            }
            else Display(shown, owner);
        }

        public static void ClearTopic(string path)
        {
            var old = Slots.ClearTopicIf(p => string.Equals(p.TopicPath, path, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                Discard(old);
                if (Slots.Both().Count == 0)
                {
                    Slots.Close();
                    Unwatch();
                    DetachAll();
                    if (!_paneBroken && !ForceFloating) try { FlarePane.ClosePane(); } catch (Exception) { }
                    PushCounts();
                    return;
                }
                SyncHost();
                PushCounts();
            }
        }

        private static void PushCounts()
        {
            var topic = TopicPanel;
            var project = ProjectPanel;
            var any = topic ?? project;
            // Changed can be raised from a worker thread.
            if (any != null && any.IsHandleCreated && any.InvokeRequired)
            {
                try { any.BeginInvoke(new Action(PushCounts)); } catch (Exception) { }
                return;
            }
            if (topic != null) topic.SetTabs(topic.OpenCount(), project != null ? project.OpenCount() : -1);
            if (project != null) project.SetTabs(topic != null ? topic.OpenCount() : -1, project.OpenCount());
        }

        private static void ShowFloating(FindingsPanel panel, Form owner)
        {
            if (_floating == null || _floating.IsDisposed)
            {
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                var form = new Form
                {
                    Text = panel.Title, StartPosition = FormStartPosition.Manual, Size = new Size(720, 460),
                    MinimumSize = new Size(320, 260), ShowInTaskbar = false, KeyPreview = true, Font = SystemFonts.MessageBoxFont,
                    AutoScaleMode = AutoScaleMode.Font, AccessibleName = panel.Title
                };
                _floating = form;
                form.Location = new Point(area.Right - form.Width - 24, area.Bottom - form.Height - 24);
                form.FormClosing += (s, e) =>
                {
                    Slots.Close();
                    Unwatch();
                    // Keep the panels alive for Reopen.
                    foreach (var p in Slots.Both()) if (p.Parent == form) try { form.Controls.Remove(p); } catch (Exception) { }
                };
                form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };
                form.Show(owner);
            }
            SyncHost();
            panel.Begin();
        }
    }

    /// <summary>The only code that depends on Flare internals. Keep it small.</summary>
    internal static class FlarePane
    {
        private static B3.AppKit.Pane _pane;
        private static SidebarView _view;
        private static readonly ConditionalWeakTable<FindingsPanel, object> Wired = new ConditionalWeakTable<FindingsPanel, object>();

        /// <summary>Takes the pane off screen when the panels didn't ask for it.</summary>
        public static void ClosePane()
        {
            if (_pane == null || _pane.IsDisposed) return;
            Hide(_pane.PaneDock);
        }

        /// <summary>Shows the panel in a pane docked right; false if Flare's main form has no pane dock.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // B3.AppKit is resolved when this method is JIT-compiled, i.e. inside the caller's try
        public static bool Show(FindingsPanel panel, Form flareMainForm)
        {
            var editor = flareMainForm as B3.AppKit.Editor;
            var dock = editor == null ? null : editor.PaneDock;
            if (dock == null)
            {
                Diagnostics.Log("Docked pane: main form is " + (flareMainForm == null ? "null" : flareMainForm.GetType().FullName) + ", no PaneDock");
                return false;
            }

            // Closing the pane with Flare's own ✕ uninstalls it without disposing it: reinstall then.
            if (_pane == null || _pane.IsDisposed || _view == null || _view.IsDisposed || !dock.Panes.Contains(_pane))
            {
                _view = new SidebarView();
                _pane = new B3.AppKit.Pane { Name = "FlareAI.Findings", Title = panel.Title, View = _view };
                // Flare picks the group (the left panes by default); the author can drag it anywhere and Flare's
                // layout remembers it by pane name. (Installing under another pane replaces that pane's view.)
                dock.InstallPane(_pane); // (PinPane = auto-hide: the pane collapses whenever the editor gets focus)
            }
            _view.SetContent(panel);
            _pane.Title = panel.Title;
            object o;
            if (!Wired.TryGetValue(panel, out o))
            {
                Wired.Add(panel, new object());
                panel.CloseRequested += (s, e) => Hide(dock);
            }
            _pane.BringPaneToFront(true);
            return true;
        }

        public static void AddContent(FindingsPanel panel)
        {
            if (_view != null && !_view.IsDisposed)
                _view.AddContent(panel);
        }

        public static void RemoveContent(FindingsPanel panel)
        {
            if (_view != null && !_view.IsDisposed)
                _view.RemoveContent(panel);
        }

        public static void RemoveAll()
        {
            if (_view != null && !_view.IsDisposed)
                _view.RemoveAll();
        }

        public static void UpdateVisible(FindingsPanel shown)
        {
            if (_view != null && !_view.IsDisposed)
                _view.UpdateVisible(shown);
            if (_pane != null && !_pane.IsDisposed) _pane.Title = shown.Title;
        }

        /// <summary>
        /// For the e2e hook: floats the docked pane as Flare's Float command does, at <paramref name="width"/> x
        /// <paramref name="height"/> when given. True when the pane is a window afterwards (or never was docked).
        /// </summary>
        public static bool Float(int width, int height)
        {
            if (_pane == null || _pane.IsDisposed) return true;   // shown in House Style's own window, not docked
            if (width > 0 && height > 0) _pane.UndockedSize = new Size(width, height);
            if (!_pane.IsFloating && _pane.PaneDock != null) _pane.PaneDock.FloatPane(_pane);
            if (!_pane.IsFloating) _pane.PerformFloating();   // the pane's own Float command
            return _pane.IsFloating;
        }

        private static void Hide(B3.AppKit.IPaneDock dock)
        {
            try
            {
                if (_view != null && !_view.IsDisposed) _view.RemoveAll();   // Flare disposes the view, not the panels
                // As Flare's own ✕ does: UninstallPane alone leaves the pane showing when it is a tab in a pane group.
                if (_pane != null && !_pane.IsDisposed && !_pane.PerformClose() && dock != null && dock.Panes.Contains(_pane)) dock.UninstallPane(_pane);
            }
            catch (Exception ex) { Diagnostics.Log("Docked pane close", ex); }
            _pane = null;
            _view = null;
        }

        /// <summary>A Flare pane view that hosts one or both panels (topic and project).</summary>
        private sealed class SidebarView : B3.AppKit.PaneView
        {
            public void SetContent(FindingsPanel panel)
            {
                AddContent(panel);
                UpdateVisible(panel);
            }

            public void AddContent(FindingsPanel panel)
            {
                if (!Controls.Contains(panel))
                {
                    panel.Dock = DockStyle.Fill;
                    Controls.Add(panel);
                }
            }

            public void RemoveContent(FindingsPanel panel)
            {
                if (Controls.Contains(panel))
                    Controls.Remove(panel);
            }

            public void RemoveAll()
            {
                foreach (var p in new List<FindingsPanel>(Controls.OfType<FindingsPanel>()))
                    Controls.Remove(p);
            }

            public void UpdateVisible(FindingsPanel shown)
            {
                foreach (Control ctl in Controls)
                {
                    ctl.Visible = (ctl == shown);
                }
            }
        }
    }
}
