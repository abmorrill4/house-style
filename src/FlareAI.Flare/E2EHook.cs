using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Flare.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// The pane tests call the hook's internal helpers (E2EFocusList, E2EMenuPick, TakeSavePath) as the hook does.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FlareAI.Flare.Tests")]

namespace FlareAI.Flare
{
    /// <summary>
    /// The end-to-end runner's hand on the plugin (tests/e2e). Off unless Flare was started with HOUSESTYLE_E2E_DIR set;
    /// then it polls that folder for cmd-N.json files, runs each on Flare's UI thread and writes res-N.json.
    /// Commands go through the same handlers and controls a writer clicks; nothing here changes what they do.
    /// Flare's own UI (trial dialog, ribbon) is driven with UI Automation by the runner; this covers the issues pane and
    /// House Style's dialogs, whose WinForms controls lose their roles inside Flare's WPF dock.
    /// </summary>
    internal sealed class E2EHook
    {
        public const string EnvVar = "HOUSESTYLE_E2E_DIR";

        /// <summary>Every verb Run accepts, in the switch's order. A test checks it against the switch.</summary>
        internal static readonly string[] Verbs =
        {
            "state", "command", "open", "click", "rowMenu", "dialogClick", "dialogSelect", "dialogType", "dialogTab",
            "paneFocus", "paneRow", "paneType", "paneMenu", "paneSize", "paneFloat", "replace", "select", "save", "saveAs"
        };

        /// <summary>" (valid: a, b, c)" for an error message, cut after 30 names, or " (none)" when there are none.</summary>
        internal static string Valid(IEnumerable<string> names)
        {
            var all = names.ToList();
            if (all.Count == 0) return " (none)";
            return " (valid: " + string.Join(", ", all.Take(30)) + (all.Count > 30 ? ", ..." : "") + ")";
        }

        private readonly string _dir;
        private readonly Func<string, Action> _command;
        private readonly Func<string, bool> _open;
        private readonly Func<Form> _main;
        private readonly Func<FlareDocumentAdapter> _active;
        private readonly Timer _timer = new Timer { Interval = 250 };
        private int _next = 1;
        private DateTime _beat;

        /// <summary>Document text in the state is cut to this many characters (the selection, the caret's block).</summary>
        private const int TextLimit = 200;

        /// <summary>The path the next Save dialog writes to instead of showing (the saveAs verb); one-shot.</summary>
        private static string _savePath;

        private E2EHook(string dir, Func<string, Action> command, Func<string, bool> open, Func<Form> main, Func<FlareDocumentAdapter> active)
        {
            _dir = dir; _command = command; _open = open; _main = main; _active = active;
            _timer.Tick += (s, e) => { Safe.Run("E2E", Poll); Beat(); };
        }

        /// <summary>
        /// Written from the UI thread every second, so a stale heartbeat.json tells the runner Flare's UI thread is
        /// blocked, and names the windows that were open (a modal one is the usual culprit).
        /// </summary>
        private void Beat()
        {
            if ((DateTime.UtcNow - _beat).TotalSeconds < 1) return;
            _beat = DateTime.UtcNow;
            // Not Safe.Run: its error dialog would sit over Flare and block the run. A missed beat only goes to the log.
            try
            {
                var forms = Application.OpenForms.Cast<Form>().Where(f => f.Visible).Select(f => f.Text + (f.Modal ? " (modal)" : ""));
                var wpf = System.Windows.Application.Current == null ? Enumerable.Empty<string>()
                    : System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().Where(w => w.IsVisible).Select(w => w.Title);
                var path = Path.Combine(_dir, "heartbeat.json");
                File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(new { at = _beat, windows = forms.Concat(wpf).Where(t => !string.IsNullOrEmpty(t)).ToList() }));
                File.Copy(path + ".tmp", path, true);
            }
            catch (Exception ex) { Diagnostics.Log("E2E heartbeat", ex); }
        }

        /// <summary>A test run keeps its settings, guide and logs in its own folder (the runner seeds it), never the writer's.</summary>
        public static void IsolateIfRequested()
        {
            var dir = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(dir)) Paths.Root = Path.Combine(dir, "appdata");
        }

        /// <summary>Starts the hook when the runner asked for it; call on Flare's UI thread.</summary>
        /// <param name="active">The adapter for Flare's active topic, null when none is open.</param>
        public static void StartIfRequested(Func<string, Action> command, Func<string, bool> open, Func<Form> main, Func<FlareDocumentAdapter> active)
        {
            var dir = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            new E2EHook(dir, command, open, main, active)._timer.Start();
            var build = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(E2EHook).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            File.WriteAllText(Path.Combine(dir, "ready.json"), JsonConvert.SerializeObject(new { version = build == null ? Plugin.Version : build.InformationalVersion }));
            Diagnostics.Log("E2E hook on: " + dir);
        }

        private void Poll()
        {
            var cmd = Path.Combine(_dir, "cmd-" + _next + ".json");
            if (!File.Exists(cmd)) return;
            var id = _next++;
            JObject c;
            try { c = JObject.Parse(File.ReadAllText(cmd)); }
            catch (IOException) { _next--; return; }   // the runner is still writing it
            catch (Exception ex) { Answer(id, () => { throw new ArgumentException("Bad command file: " + ex.Message); }); return; }
            var verb = (string)c["verb"] ?? "";
            // Reads and dialog commands answer now (they run inside a dialog's message loop too); the rest are posted so a
            // dialog they open doesn't block the hook, and answer when they return.
            if (verb == "state" || verb.StartsWith("dialog", StringComparison.Ordinal)) Answer(id, () => Run(verb, c));
            else _main().BeginInvoke((Action)(() => Answer(id, () => Run(verb, c))));
        }

        private void Answer(int id, Func<object> run)
        {
            object result;
            try { result = new { ok = true, value = run(), state = State() }; }
            catch (Exception ex) { result = new { ok = false, error = ex.GetType().Name + ": " + ex.Message, state = State() }; }
            var path = Path.Combine(_dir, "res-" + id + ".json");
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(result, Formatting.Indented));
            File.Move(path + ".tmp", path);
        }

        private object Run(string verb, JObject c)
        {
            var arg = (string)c["arg"];
            switch (verb)
            {
                case "state": return null;
                case "command": _command(arg)(); return null;                       // a ribbon command by Commands id
                case "open": if (!_open(arg)) throw new InvalidOperationException("Flare didn't open " + arg); return null;
                case "click": Click(Pane(), arg); return null;                        // a button in the issues pane
                case "rowMenu": return Pane().E2ERowMenu((int)c["index"], arg);       // right-click a row, click an item
                case "dialogClick": Click(Dialog((string)c["title"]), arg); return null;
                case "dialogSelect":                                                  // a list item by index, or a list view row by its text
                    if (c.Value<string>("text") != null) SelectRow(Dialog((string)c["title"]), (string)c["text"]); else Select(Dialog((string)c["title"]), (int)c["index"]);
                    return null;
                case "dialogType": Type(Dialog((string)c["title"]), (string)c["name"], arg); return null;   // a text box or list by its accessible name
                case "dialogTab": Tab(Dialog((string)c["title"]), arg); return null;
                // The scenario sends keys itself (SendKeys) after this, so they pass through Flare's WPF dock as a writer's do.
                case "paneFocus": return new { focused = Pane().E2EFocusList() };
                case "paneRow": return new { text = Pane().E2EClickHeading(arg) };   // a file heading by part of its text: collapses or expands the file
                case "paneType": Type(Pane(), (string)c["name"], arg); return null;
                case "paneMenu": Pane().E2EMenuPick(arg); return null;               // an item in the pane's open drop-down menu
                case "paneSize": return SizePane(Pane(), (int)c["width"], (int)c["height"]);
                case "paneFloat": return FloatPane(Pane(), (int?)c["width"] ?? 0, (int?)c["height"] ?? 0);   // as Flare's own Float
                case "replace": Replace(Active(), arg, (string)c["text"]); return null;
                case "select": Select(Active(), arg); return null;
                case "save": return new { saved = Active().SaveAsWriter() };                   // as Ctrl+S: not marked as the plugin's own save
                case "saveAs": SetSavePath(arg); return null;
                default: throw new ArgumentException("Unknown verb " + verb + Valid(Verbs));
            }
        }

        private FlareDocumentAdapter Active()
        {
            var a = _active == null ? null : _active();
            if (a == null || a.Document == null) throw new InvalidOperationException("No topic is open.");
            return a;
        }

        /// <summary>The first editable text run in the topic's body that contains <paramref name="text"/>, and where.</summary>
        private static System.Xml.XmlText Find(FlareDocumentAdapter a, string text, out int at)
        {
            at = -1;
            var body = FlareAI.Core.Editing.BlockSelector.FindBody(a.Document);
            if (string.IsNullOrEmpty(text) || body == null) throw new InvalidOperationException("No text " + text + " in one text run");
            foreach (var run in FlareAI.Core.Style.TextRuns.Of(new List<System.Xml.XmlNode> { body }))
            {
                at = run.Value.IndexOf(text, StringComparison.Ordinal);
                if (at >= 0) return run;
            }
            throw new InvalidOperationException("No text " + text + " in one text run");
        }

        /// <summary>One tracked, undoable change, as typing makes: Flare raises ContentChanged and the pane reacts on the next pause.</summary>
        private static void Replace(FlareDocumentAdapter a, string text, string with)
        {
            int at;
            var run = Find(a, text, out at);
            var done = false;
            a.Edit("E2E edit", () => done = a.ReplaceSpan(run, at, text.Length, with ?? ""));
            if (!done) throw new InvalidOperationException("Flare didn't replace " + text);
        }

        private static void Select(FlareDocumentAdapter a, string text)
        {
            int at;
            var run = Find(a, text, out at);
            if (!a.SelectText(run, at, text.Length)) throw new InvalidOperationException("Flare didn't select " + text);
        }

        /// <summary>Sets the path the next Save dialog writes to (see <see cref="TakeSavePath"/>). Only a full path inside the run folder.</summary>
        internal static void SetSavePath(string path)
        {
            var dir = Environment.GetEnvironmentVariable(EnvVar);
            string full = null;
            try { if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path)) full = Path.GetFullPath(path); }
            catch (Exception) { full = null; }
            if (string.IsNullOrEmpty(dir) || full == null || !FlareAI.Core.Settings.PluginSettings.IsUnder(full, dir) ||
                string.Equals(full.TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Save path must be inside the run folder");
            _savePath = full;
        }

        /// <summary>
        /// For pane code that shows a Save dialog: the path a scenario set with saveAs, which the caller writes to instead of
        /// showing the dialog. Null (show the dialog) when the hook is off or no path was set. Clears it, so it's one-shot.
        /// </summary>
        internal static string TakeSavePath()
        {
            var p = _savePath;
            _savePath = null;
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)) ? null : p;
        }

        /// <summary>Selects the dialog's tab whose text is <paramref name="text"/>.</summary>
        internal static void Tab(Control root, string text)
        {
            foreach (var tabs in All(root).OfType<TabControl>().Where(t => t.Visible))
            {
                var page = tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text.Replace("&", "") == text);
                if (page != null) { tabs.SelectedTab = page; return; }
            }
            throw new InvalidOperationException("No tab " + text + Valid(All(root).OfType<TabControl>().Where(t => t.Visible)
                .SelectMany(t => t.TabPages.Cast<TabPage>()).Select(p => p.Text.Replace("&", ""))));
        }

        /// <summary>
        /// Floating: sizes the pane's window. Docked: sizes the topmost control below Flare's main form that holds the pane,
        /// and fails when Flare's dock keeps its own size. Returns the panel's size afterwards.
        /// </summary>
        private static object SizePane(FindingsPanel pane, int width, int height)
        {
            var form = pane.FindForm();
            if (form != null && form.Controls.Contains(pane)) form.ClientSize = new System.Drawing.Size(width, height);   // the floating window
            else
            {
                Control top = pane;
                while (top.Parent != null && !(top.Parent is Form)) top = top.Parent;
                var before = pane.ClientSize;
                top.Size = new System.Drawing.Size(width + top.Width - pane.Width, height + top.Height - pane.Height);
                top.PerformLayout();
                if (pane.ClientSize == before) throw new InvalidOperationException("Flare didn't resize the docked pane.");
            }
            return new { width = pane.ClientSize.Width, height = pane.ClientSize.Height };
        }

        /// <summary>
        /// Floats the docked issues pane as Flare's Float command does (a no-op when it is already a window), then sizes it
        /// when <paramref name="width"/> and <paramref name="height"/> are given. Returns the pane's size and whether it floats.
        /// </summary>
        private static object FloatPane(FindingsPanel pane, int width, int height)
        {
            var floating = FlarePane.Float(width, height);
            Application.DoEvents();   // let Flare build the floating window before it is sized
            if (width > 0 && height > 0 && pane.ClientSize != new System.Drawing.Size(width, height))
            {
                try { SizePane(pane, width, height); }
                catch (InvalidOperationException) { }   // the floating window took UndockedSize instead
            }
            var hosts = new List<string>();   // what holds the pane, for a scenario's failure message
            for (Control p = pane.Parent; p != null && hosts.Count < 8; p = p.Parent) hosts.Add(p.GetType().Name);
            return new { floating, width = pane.ClientSize.Width, height = pane.ClientSize.Height, hosts };
        }

        private static FindingsPanel Pane()
        {
            var p = PanelHost.Current;
            if (p == null) throw new InvalidOperationException("The issues pane isn't open.");
            return p;
        }

        private Form Dialog(string title)
        {
            var f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => IsOurs(x) && x.Text.Contains(title));
            if (f == null) throw new InvalidOperationException("No open dialog titled " + title +
                Valid(Application.OpenForms.Cast<Form>().Where(IsOurs).Select(x => x.Text)));
            return f;
        }

        /// <summary>Clicks the visible, enabled button whose text (without "&amp;" or "…") starts with <paramref name="text"/>.</summary>
        internal static void Click(Control root, string text)
        {
            var b = All(root).OfType<ButtonBase>().FirstOrDefault(x => x.Visible && x.Enabled && Label(x).StartsWith(text, StringComparison.Ordinal));
            if (b == null) throw new InvalidOperationException("No enabled button " + text +
                Valid(All(root).OfType<ButtonBase>().Where(x => x.Visible && x.Enabled).Select(Label)));
            var button = b as Button;
            if (button != null) button.PerformClick();
            else b.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(b, new object[] { EventArgs.Empty });
        }

        /// <summary>A visible House Style window (Flare's own forms are left out; the floating pane counts).</summary>
        private static bool IsOurs(Form f)
        {
            return f.Visible && (f.GetType().Namespace ?? "").StartsWith("FlareAI", StringComparison.Ordinal) || f.Visible && f.Controls.OfType<FindingsPanel>().Any();
        }

        internal static string Label(ButtonBase b) { return b.Text.Replace("&", "").TrimEnd('…'); }

        private static void Select(Control root, int index)
        {
            var box = All(root).OfType<ListBox>().FirstOrDefault(x => x.Visible);
            if (box != null) { box.SelectedIndex = index; return; }
            var view = All(root).OfType<ListView>().FirstOrDefault(x => x.Visible);   // the Exemptions dialog lists rows
            if (view == null) throw new InvalidOperationException("No list in the dialog");
            if (index < 0 || index >= view.Items.Count) throw new InvalidOperationException("No row " + index + " in the dialog's list (" + view.Items.Count + " rows)");
            view.SelectedItems.Clear();
            view.Items[index].Selected = true; view.Items[index].Focused = true;
        }

        /// <summary>Selects the first row of the dialog's list view whose text contains <paramref name="text"/>, as a click would.</summary>
        internal static void SelectRow(Control root, string text)
        {
            var list = All(root).OfType<ListView>().FirstOrDefault(x => x.Visible);
            if (list == null) throw new InvalidOperationException("No list view in the dialog");
            var item = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            if (item == null) throw new InvalidOperationException("No row with " + text +
                Valid(list.Items.Cast<ListViewItem>().Take(10).Select(i => i.Text)));
            list.SelectedItems.Clear();
            item.Selected = true; item.Focused = true; item.EnsureVisible();
        }

        /// <summary>Types <paramref name="text"/> into the dialog's text box (or picks it in the list) named <paramref name="name"/>.</summary>
        internal static void Type(Control root, string name, string text)
        {
            var box = All(root).FirstOrDefault(x => x.Visible && (x is TextBox || x is ComboBox) && x.AccessibleName == name);
            if (box == null) throw new InvalidOperationException("No box named " + name +
                Valid(All(root).Where(x => x.Visible && (x is TextBox || x is ComboBox) && !string.IsNullOrEmpty(x.AccessibleName) && !(x is TextBox && ((TextBox)x).UseSystemPasswordChar))
                    .Select(x => x.AccessibleName)));
            var combo = box as ComboBox;
            if (combo != null && combo.DropDownStyle == ComboBoxStyle.DropDownList) combo.SelectedItem = combo.Items.Cast<object>().First(i => i.ToString() == text);
            else box.Text = text;
        }

        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in All(c)) yield return d;
            }
        }

        /// <summary>The pane, every House Style dialog and what it shows: enough to assert on without a screenshot.</summary>
        private object State()
        {
            var pane = PanelHost.Current;
            return new
            {
                pane = pane == null ? null : new { panel = pane.E2EState(), buttons = Buttons(pane), size = new { width = pane.ClientSize.Width, height = pane.ClientSize.Height } },
                editor = Editor(),
                dialogs = Application.OpenForms.Cast<Form>().Where(IsOurs).Select(f => new
                {
                    title = f.Text,
                    focus = Focus(f),
                    buttons = Buttons(f),
                    text = All(f).OfType<Label>().Where(l => l.Visible && l.Text.Length > 0).Select(l => l.Text).ToList(),
                    items = All(f).OfType<ListBox>().SelectMany(l => l.Items.Cast<object>().Select(i => i.ToString())).ToList(),
                    rows = All(f).OfType<ListView>().SelectMany(l => l.Items.Cast<ListViewItem>().Select(i => new { text = i.Text, tip = i.ToolTipText, selected = i.Selected })).ToList(),
                    // Never a password box: an API key must not reach the results.
                    boxes = All(f).Where(x => x.Visible && (x is TextBox || x is ComboBox) && !string.IsNullOrEmpty(x.AccessibleName) &&
                                              !(x is TextBox && (((TextBox)x).UseSystemPasswordChar || ((TextBox)x).PasswordChar != '\0')))
                                  .Select(x => new { name = x.AccessibleName, text = x.Text }).ToList()
                }).ToList()
            };
        }

        /// <summary>The control with the keyboard focus in <paramref name="f"/>: its accessible name, else its text.</summary>
        private static string Focus(Form f)
        {
            Control c = f.ActiveControl;
            for (var inner = c as ContainerControl; inner != null && inner.ActiveControl != null; inner = inner.ActiveControl as ContainerControl) c = inner.ActiveControl;
            return c == null ? null : !string.IsNullOrEmpty(c.AccessibleName) ? c.AccessibleName : c.Text.Replace("&", "").TrimEnd('…');
        }

        /// <summary>The active topic's path and the start of its selection; null when no topic is open. Never the topic text.</summary>
        private object Editor()
        {
            try
            {
                var a = _active == null ? null : _active();
                if (a == null) return null;
                var selection = a.SelectionText ?? "";
                return new { path = a.SourcePath, selection = selection.Length > TextLimit ? selection.Substring(0, TextLimit) : selection };
            }
            catch (Exception ex) { Diagnostics.Log("E2E editor state", ex); return null; }
        }

        private static List<object> Buttons(Control root)
        {
            return All(root).OfType<ButtonBase>().Where(b => b.Visible).Select(b =>
            {
                var box = b as CheckBox;
                var radio = b as RadioButton;
                if (box != null || radio != null) return (object)new { text = Label(b), enabled = b.Enabled, @checked = box != null ? box.Checked : radio.Checked };
                return new { text = Label(b), enabled = b.Enabled };
            }).ToList();
        }
    }
}
