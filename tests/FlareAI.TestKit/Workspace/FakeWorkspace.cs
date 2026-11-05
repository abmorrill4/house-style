using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.TestKit.Editor;

namespace FlareAI.TestKit.Workspace
{
    /// <summary>
    /// A disposable sandbox for one test: a temp copy of a corpus project, the documents "open in Flare", folders
    /// made read-only with a Windows ACL (as Program Files is for a standard user), the writer's AppData redirected to a
    /// temp folder (<see cref="Paths.Root"/>), a <see cref="FakeClock"/>, and Core's statics (Team.Current, feedback
    /// paths, diagnostics) reset on entry and restored on dispose. Workspaces are exclusive: a second one waits until
    /// the first is disposed, so test tests that use them never race on Core's statics.
    /// </summary>
    public sealed class FakeWorkspace : IDisposable
    {
        private static readonly SemaphoreSlim Exclusive = new SemaphoreSlim(1, 1);
        /// <summary>Build output and per-user state, relative to the project folder: never copied.</summary>
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Output", "Analyzer", @"Project\Reports", @"Project\Users" };

        private readonly List<FlareSimDocument> _open = new List<FlareSimDocument>();
        private readonly List<string> _openedByPlugin = new List<string>();
        private readonly List<KeyValuePair<string, FileSystemAccessRule>> _denied = new List<KeyValuePair<string, FileSystemAccessRule>>();
        private readonly Saved _saved;
        private bool _disposed;

        /// <summary>A copy of corpus/&lt;project&gt; (e.g. "DigitalBanking").</summary>
        public static FakeWorkspace FromCorpus(string project)
        {
            var source = Path.Combine(Repo.Corpus, project);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("No corpus project " + source);
            return new FakeWorkspace(source, project);
        }

        /// <summary>A copy of any Flare project folder.</summary>
        public static FakeWorkspace FromFolder(string projectFolder) { return new FakeWorkspace(Path.GetFullPath(projectFolder), Path.GetFileName(projectFolder.TrimEnd('\\', '/'))); }

        /// <summary>An empty project (flprj, Content, Project\VariableSets) to fill with <see cref="WriteFile"/>.</summary>
        public static FakeWorkspace Empty(string name = "Scratch") { return new FakeWorkspace(null, name); }

        private FakeWorkspace(string source, string name)
        {
            Exclusive.Wait();
            try
            {
                _saved = Saved.Capture();
                Root = Path.Combine(Path.GetTempPath(), "flareai-ws-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                ProjectDir = Path.Combine(Root, name);
                AppData = Path.Combine(Root, "AppData");
                Directory.CreateDirectory(AppData);
                if (source != null) Copy(source, ProjectDir, "");
                else
                {
                    Directory.CreateDirectory(Path.Combine(ProjectDir, "Content"));
                    Directory.CreateDirectory(Path.Combine(ProjectDir, @"Project\VariableSets"));
                    File.WriteAllText(Path.Combine(ProjectDir, name + ".flprj"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<CatapultProject />");
                }
                Clock = new FakeClock();
                Redirect();
                Variables = LoadVariables(ProjectDir);
            }
            catch
            {
                Exclusive.Release();
                throw;
            }
        }

        public string Root { get; private set; }
        public string ProjectDir { get; private set; }
        public string ContentDir { get { return Path.Combine(ProjectDir, "Content"); } }
        /// <summary>The redirected %APPDATA%\FlareAI (also <see cref="Paths.Root"/> while the workspace lives).</summary>
        public string AppData { get; private set; }
        public FakeClock Clock { get; private set; }

        /// <summary>The project file (*.flprj), or null.</summary>
        public string ProjectFile
        {
            get
            {
                var f = Directory.GetFiles(ProjectDir, "*.flprj");
                return f.Length > 0 ? f[0] : null;
            }
        }

        /// <summary>Project variables, "Set.Name" → value, from Project\VariableSets\*.flvar.</summary>
        public Dictionary<string, string> Variables { get; private set; }

        /// <summary>A path inside the project ("Content/transfers/about.htm").</summary>
        public string PathOf(string relative)
        {
            return Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(ProjectDir, relative.Replace('/', '\\')));
        }

        public void WriteFile(string relative, string text)
        {
            var p = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        public string ReadFile(string relative) { return File.ReadAllText(PathOf(relative)); }

        // ---- open documents ------------------------------------------------------------------

        /// <summary>The author opens a topic or snippet (or brings it to the front if already open).</summary>
        public FlareSimDocument Open(string path) { return OpenCore(PathOf(path), false); }

        /// <summary>
        /// The host delegate the plugin uses to open a file it needs (a snippet, a project topic): returns the open
        /// document, or opens it marked <see cref="FlareSimDocument.OpenedByPlugin"/>.
        /// </summary>
        public IDocumentAdapter OpenForPlugin(string path) { return OpenCore(PathOf(path), true); }

        public Func<string, IDocumentAdapter> HostOpen { get { return OpenForPlugin; } }

        public bool IsOpen(string path)
        {
            var full = PathOf(path);
            lock (_open) return _open.Exists(d => Same(d.SourcePath, full));
        }

        public IList<FlareSimDocument> OpenDocuments { get { lock (_open) return _open.ToArray(); } }

        /// <summary>Paths the plugin opened through <see cref="OpenForPlugin"/>, in order.</summary>
        public IList<string> OpenedByPlugin { get { lock (_open) return _openedByPlugin.ToArray(); } }

        private FlareSimDocument OpenCore(string full, bool byPlugin)
        {
            lock (_open)
            {
                var existing = _open.Find(d => Same(d.SourcePath, full));
                if (existing != null) return existing;
                var doc = FlareSimDocument.FromFile(full, Clock);
                foreach (var kv in Variables) doc.Variables[kv.Key] = kv.Value;
                doc.OpenedByPlugin = byPlugin;
                doc.Closed += (s, e) => { lock (_open) _open.Remove((FlareSimDocument)s); };
                _open.Add(doc);
                if (byPlugin) _openedByPlugin.Add(full);
                return doc;
            }
        }

        private static bool Same(string a, string b) { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }

        // ---- read-only folders ----------------------------------------------------------------

        /// <summary>
        /// Denies the current Windows user write, create and delete on a file or folder (inherited by everything in a
        /// folder), the way Program Files behaves for a standard user. Undone on dispose.
        /// </summary>
        public void MakeReadOnly(string path)
        {
            Deny(path, FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
                       FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles);
        }

        /// <summary>
        /// Denies the current Windows user reading a file, or listing and reading a folder (inherited by everything in it),
        /// as a share the writer has lost access to behaves. Undone on dispose (with <see cref="ClearReadOnly"/>).
        /// </summary>
        public void MakeUnreadable(string path)
        {
            Deny(path, FileSystemRights.ReadData | FileSystemRights.ReadExtendedAttributes);
        }

        private void Deny(string path, FileSystemRights rights)
        {
            var full = PathOf(path);
            var sid = WindowsIdentity.GetCurrent().User;
            if (Directory.Exists(full))
            {
                var rule = new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
                var sec = Directory.GetAccessControl(full);
                sec.AddAccessRule(rule);
                Directory.SetAccessControl(full, sec);
                _denied.Add(new KeyValuePair<string, FileSystemAccessRule>(full, rule));
            }
            else if (File.Exists(full))
            {
                var rule = new FileSystemAccessRule(sid, rights, AccessControlType.Deny);
                var sec = File.GetAccessControl(full);
                sec.AddAccessRule(rule);
                File.SetAccessControl(full, sec);
                _denied.Add(new KeyValuePair<string, FileSystemAccessRule>(full, rule));
            }
            else throw new FileNotFoundException("Nothing to deny access to at " + full);
        }

        /// <summary>Lifts every read-only rule this workspace set.</summary>
        public void ClearReadOnly()
        {
            for (var i = _denied.Count - 1; i >= 0; i--)
            {
                var path = _denied[i].Key;
                var rule = _denied[i].Value;
                try
                {
                    if (Directory.Exists(path))
                    {
                        var sec = Directory.GetAccessControl(path);
                        sec.RemoveAccessRuleSpecific(rule);
                        Directory.SetAccessControl(path, sec);
                    }
                    else if (File.Exists(path))
                    {
                        var sec = File.GetAccessControl(path);
                        sec.RemoveAccessRuleSpecific(rule);
                        File.SetAccessControl(path, sec);
                    }
                }
                catch (Exception) { /* best effort; the temp folder may then outlive the test */ }
            }
            _denied.Clear();
        }

        // ---- statics ----------------------------------------------------------------------------

        /// <summary>Clears the team context (Core keeps it in a static).</summary>
        public static void ResetTeam() { Team.Current = null; }

        private void Redirect()
        {
            Paths.Root = AppData;
            Team.Current = null;
            Team.AnonymousIdPath = null;          // → Paths.WriterId under the redirected root
            FeedbackLog.PersonalPath = Paths.Feedback;
            Diagnostics.Sink = null;
            Diagnostics.Enabled = true;           // diag.log goes to the temp AppData, where tests can read it
        }

        public string DiagLog { get { return Paths.DiagLog; } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                foreach (var d in OpenDocuments) d.Close();
                ClearReadOnly();
                _saved.Restore();
                try { Directory.Delete(Root, true); } catch (Exception) { }
            }
            finally { Exclusive.Release(); }
        }

        private sealed class Saved
        {
            private string _root;
            private Team _team;
            private string _idPath, _feedback;
            private bool _diagEnabled;
            private Action<string> _sink;

            public static Saved Capture()
            {
                return new Saved
                {
                    _root = Paths.IsRedirected ? Paths.Root : null,
                    _team = Team.Current,
                    _idPath = Team.AnonymousIdPath,
                    _feedback = FeedbackLog.PersonalPath,
                    _diagEnabled = Diagnostics.Enabled,
                    _sink = Diagnostics.Sink
                };
            }

            public void Restore()
            {
                Paths.Root = _root;
                Team.Current = _team;
                Team.AnonymousIdPath = _idPath;
                FeedbackLog.PersonalPath = _feedback;
                Diagnostics.Enabled = _diagEnabled;
                Diagnostics.Sink = _sink;
            }
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static void Copy(string from, string to, string relative)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
            {
                if (f.EndsWith(".flprjuser", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            }
            foreach (var d in Directory.GetDirectories(from))
            {
                var rel = relative.Length == 0 ? Path.GetFileName(d) : relative + @"\" + Path.GetFileName(d);
                if (SkipDirs.Contains(rel)) continue;
                Copy(d, Path.Combine(to, Path.GetFileName(d)), rel);
            }
        }

        private static Dictionary<string, string> LoadVariables(string project)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            var dir = Path.Combine(project, @"Project\VariableSets");
            if (!Directory.Exists(dir)) return vars;
            foreach (var f in Directory.GetFiles(dir, "*.flvar"))
            {
                var x = new XmlDocument();
                x.Load(f);
                foreach (XmlElement v in x.GetElementsByTagName("Variable"))
                    vars[Path.GetFileNameWithoutExtension(f) + "." + v.GetAttribute("Name")] = v.InnerText;
            }
            return vars;
        }
    }
}
