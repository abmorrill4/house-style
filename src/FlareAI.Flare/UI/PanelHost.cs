using System;
using System.Drawing;
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
        private static readonly PaneSlots<FindingsPanel> Slots = new PaneSlots<FindingsPanel>();
        private static Form _floating;
        private static bool _changedSubscribed = false;

        // For tests only: force floating mode (no docked pane)
        internal static bool ForceFloating { get; set; }

        /// <summary>The findings panel currently shown (docked or floating), or null once closed.</summary>
        public static FindingsPanel Current { get { return Slots.IsOpen && Slots.ShownPanel != null && !Slots.ShownPanel.IsDisposed && Slots.ShownPanel.Parent != null ? Slots.ShownPanel : null; } }

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
            ShowSlot(slot, flareMainForm);
            return true;
        }

        public static void BeginClose()
        {
            Slots.Close();
        }

        public static void Show(FindingsPanel panel, Form flareMainForm)
        {
            Show(panel, flareMainForm, true);
        }

        public static void Show(FindingsPanel panel, Form flareMainForm, bool front)
        {
            var old = Slots.Put(panel.Slot, panel, front);
            if (old != null && old != panel)
            {
                try { old.WatchWrites(false); } catch (Exception ex) { Diagnostics.Log("unwatch: " + ex.GetType().Name); }
                if (!_paneBroken && !ForceFloating)
                    try { FlarePane.RemoveContent(old); } catch (Exception) { }
                try { old.Dispose(); } catch (Exception ex) { Diagnostics.Log("dispose old: " + ex.GetType().Name); }
            }

            panel.CloseRequested += (s, e) => { try { Slots.Close(); } finally { try { FlarePane.RemoveContent(panel); } catch (Exception) { } panel.WatchWrites(false); } };
            panel.TabRequested += (slot) => ShowSlot(slot, flareMainForm);

            if (!_changedSubscribed)
            {
                _changedSubscribed = true;
                FindingsPanel.Changed += (p) => PushCounts();
            }

            if (front)
            {
                if (!_paneBroken && !ForceFloating)
                {
                    try
                    {
                        if (FlarePane.Show(panel, flareMainForm)) { panel.Begin(); return; }
                    }
                    catch (Exception ex)
                    {
                        _paneBroken = true;
                        Diagnostics.Log("Docked pane unavailable, using a floating window", ex);
                    }
                }
                ShowFloating(panel, flareMainForm);
            }
            else
            {
                panel.WatchWrites(true);
                if (!_paneBroken && !ForceFloating)
                {
                    try
                    {
                        FlarePane.AddContent(panel);
                        PushCounts();
                        return;
                    }
                    catch (Exception)
                    {
                        _paneBroken = true;
                    }
                }
            }
            PushCounts();
        }

        private static void ShowSlot(PaneSlot slot, Form owner)
        {
            if (!Slots.Show(slot)) return;
            var shown = Slots.ShownPanel;
            if (shown == null || shown.IsDisposed) return;

            if (!_paneBroken && !ForceFloating)
            {
                try
                {
                    FlarePane.UpdateVisible(shown);
                    shown.FocusAfterSwitch();
                    return;
                }
                catch (Exception)
                {
                    _paneBroken = true;
                }
            }

            // Floating mode
            if (_floating != null && !_floating.IsDisposed)
            {
                _floating.Text = shown.Title;
                _floating.AccessibleName = shown.Title;
                shown.FocusAfterSwitch();
            }
        }

        public static void ClearTopic(string path)
        {
            var old = Slots.ClearTopicIf(p => string.Equals(p.TopicPath, path, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                try { old.WatchWrites(false); } catch (Exception) { }
                if (!_paneBroken && !ForceFloating)
                    try { FlarePane.RemoveContent(old); } catch (Exception) { }
                try { old.Dispose(); } catch (Exception) { }
                PushCounts();
            }
        }

        private static void PushCounts()
        {
            var topic = TopicPanel;
            var project = ProjectPanel;
            if (topic != null) topic.SetTabs(topic.OpenCount(), project != null ? project.OpenCount() : -1);
            if (project != null) project.SetTabs(topic != null ? topic.OpenCount() : -1, project.OpenCount());
        }

        private static void ShowFloating(FindingsPanel panel, Form owner)
        {
            panel.WatchWrites(true);

            if (_floating == null || _floating.IsDisposed)
            {
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                _floating = new Form
                {
                    Text = panel.Title, StartPosition = FormStartPosition.Manual, Size = new Size(720, 460),
                    MinimumSize = new Size(320, 260), ShowInTaskbar = false, KeyPreview = true, Font = SystemFonts.MessageBoxFont,
                    AutoScaleMode = AutoScaleMode.Font, AccessibleName = panel.Title
                };
                _floating.Location = new Point(area.Right - _floating.Width - 24, area.Bottom - _floating.Height - 24);
                _floating.FormClosing += (s, e) => { var t = TopicPanel; var p = ProjectPanel; if (t != null) try { t.Dispose(); } catch (Exception) { } if (p != null) try { p.Dispose(); } catch (Exception) { } };
                _floating.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) _floating.Close(); };
                _floating.Show(owner);
            }
            else
            {
                _floating.Text = panel.Title;
                _floating.AccessibleName = panel.Title;
            }

            panel.Dock = DockStyle.Fill;
            _floating.Controls.Add(panel);
            panel.Begin();
        }
    }

    /// <summary>The only code that depends on Flare internals. Keep it small.</summary>
    internal static class FlarePane
    {
        private static B3.AppKit.Pane _pane;
        private static SidebarView _view;

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
            panel.CloseRequested += (s, e) => Hide(dock);
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

        public static void UpdateVisible(FindingsPanel shown)
        {
            if (_view != null && !_view.IsDisposed)
                _view.UpdateVisible(shown);
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
                // As Flare's own ✕ does: UninstallPane alone leaves the pane showing when it is a tab in a pane group.
                if (_pane != null && !_pane.IsDisposed && !_pane.PerformClose() && dock.Panes.Contains(_pane)) dock.UninstallPane(_pane);
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
                if (Controls.Contains(panel)) return;   // Reopen while the pane is still open
                foreach (Control old in Controls)
                {
                    if (old != panel) old.Visible = false;
                }
                if (!Controls.Contains(panel))
                {
                    panel.Dock = DockStyle.Fill;
                    Controls.Add(panel);
                }
                panel.Visible = true;
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
