using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace FlareAI.Core.Style
{
    /// <summary>A saved copy of a team guide: where it is and when it was saved.</summary>
    public sealed class GuideCopyInfo
    {
        public string Path { get; set; }
        public DateTime CopiedUtc { get; set; }
    }

    /// <summary>
    /// The last copy of each team style guide, kept in the writer's own data folder, so a guide that can't be reached
    /// (offline, VPN down, sync paused) still lints with the team's rules (#246). The guide folder is untrusted input: only
    /// the files the guide loader reads are copied, links inside it are never followed, nothing in the copy runs, and the
    /// copy is never a write target (<see cref="IsCopy"/>). A copy is found only through the configured path's hash, never
    /// through anything written inside the guide.
    /// </summary>
    public sealed class GuideCopy
    {
        /// <summary>The folder under the writer's data folder that holds the copies.</summary>
        public const string FolderName = "team-guide-copies";

        /// <summary>Limits: a larger guide gets no new copy (the previous one stays).</summary>
        public const int MaxFiles = 5000;
        public const long MaxBytes = 50L * 1024 * 1024;

        private static readonly string[] Extensions = { ".htm", ".html", ".flvar", ".flglo", ".flcts", ".flprj", ".json", ".ini", ".txt", ".md" };
        private static readonly string[] ProjectFolders = { @"Content\Resources\StyleGuide", @"Project\VariableSets", @"Project\Glossaries", @"Project\ConditionTagSets" };
        private static readonly Regex CopyName = new Regex(@"^[0-9a-f]{16}(\.(tmp|old)-[0-9a-f]{32})?$", RegexOptions.IgnoreCase);

        public GuideCopy(string folder)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentNullException("folder");
            Folder = folder;
        }

        /// <summary>The copies of this writer (<c>%APPDATA%\FlareAI\team-guide-copies</c>, or the redirected data folder).</summary>
        public static GuideCopy ForWriter { get { return new GuideCopy(Paths.In(FolderName)); } }

        public string Folder { get; private set; }

        // ---- where ----------------------------------------------------------------------------------------------

        private static string Key(string guideRoot)
        {
            return System.IO.Path.GetFullPath(guideRoot).TrimEnd('\\', '/').ToLowerInvariant();
        }

        private static string Hash(string guideRoot)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Key(guideRoot)));
                var sb = new StringBuilder();
                for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The copy's folder for a configured guide. Depends only on the configured path, never on the guide's contents.</summary>
        public string PathFor(string guideRoot) { return System.IO.Path.Combine(Folder, Hash(guideRoot)); }

        private string RecordFor(string guideRoot) { return PathFor(guideRoot) + ".json"; }

        /// <summary>
        /// True when <paramref name="path"/> is inside a saved copy (any writer data folder): a copy is never written to.
        /// Decided by the path alone, so it holds for a copy in a redirected data folder too.
        /// </summary>
        public static bool IsCopy(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                for (var d = new DirectoryInfo(System.IO.Path.GetFullPath(path)); d != null && d.Parent != null; d = d.Parent)
                    if (string.Equals(d.Parent.Name, FolderName, StringComparison.OrdinalIgnoreCase) && CopyName.IsMatch(d.Name)) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>Throws the copy refusal when <paramref name="path"/> is inside a saved copy. Every guide write calls it.</summary>
        internal static void RefuseWrite(string path)
        {
            if (IsCopy(path)) throw new InvalidOperationException(Actions.RuleChanges.CopyRefusal);
        }

        // ---- record -----------------------------------------------------------------------------------------------

        private sealed class Record
        {
            [JsonProperty("source")] public string Source { get; set; }   // for the log only; never read back as a path
            [JsonProperty("stamp")] public string Stamp { get; set; }
            [JsonProperty("copiedUtc")] public DateTime CopiedUtc { get; set; }
        }

        private Record ReadRecord(string guideRoot)
        {
            try
            {
                var p = RecordFor(guideRoot);
                return File.Exists(p) ? JsonConvert.DeserializeObject<Record>(File.ReadAllText(p)) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The complete copy of <paramref name="guideRoot"/> and its date, or null when there is none.</summary>
        public GuideCopyInfo Find(string guideRoot)
        {
            if (string.IsNullOrEmpty(guideRoot)) return null;
            try
            {
                var path = PathFor(guideRoot);
                var rec = ReadRecord(guideRoot);
                if (rec == null || !Directory.Exists(path)) return null;
                return new GuideCopyInfo { Path = path, CopiedUtc = DateTime.SpecifyKind(rec.CopiedUtc, DateTimeKind.Utc) };
            }
            catch (Exception) { return null; }
        }

        // ---- refresh ----------------------------------------------------------------------------------------------

        private static readonly object LogGate = new object();
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void LogOnce(string guideRoot, string what, Exception ex = null)
        {
            var name = System.IO.Path.GetFileName(guideRoot.TrimEnd('\\', '/'));
            lock (LogGate) if (!Logged.Add(guideRoot.TrimEnd('\\', '/') + "|" + what)) return;
            if (ex == null) Diagnostics.Log("Style guide copy of " + name + ": " + what);
            else Diagnostics.Log("Style guide copy of " + name + ": " + what, ex);
        }

        /// <summary>
        /// A stamp over exactly the files a copy would hold (names, sizes, write times; nothing is read), or null when the
        /// guide is over the limits. Throws when the guide can't be listed.
        /// </summary>
        public static string Stamp(string guideRoot)
        {
            var files = Walk(guideRoot);
            return files == null ? null : StampOf(files);
        }

        private static string StampOf(List<Entry> files)
        {
            var sb = new StringBuilder();
            foreach (var f in files.OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase))
                sb.Append(f.Rel.ToLowerInvariant()).Append('|').Append(f.Size).Append('|').Append(f.WriteTicks).Append('\n');
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        /// <summary>
        /// Copies the guide when its files changed since the last copy. Returns true when a new copy was saved. Never throws:
        /// on any failure the temporary folder is deleted, the previous copy stays, and it is logged once per guide.
        /// </summary>
        public bool Refresh(string guideRoot)
        {
            if (string.IsNullOrEmpty(guideRoot)) return false;
            string temp = null;
            try
            {
                var target = PathFor(guideRoot);
                Directory.CreateDirectory(Folder);
                CleanStrays(target);
                var files = Walk(guideRoot);
                if (files == null) { LogOnce(guideRoot, "the guide is over " + MaxFiles + " files or " + (MaxBytes / 1024 / 1024) + " MB, so the previous copy stays"); return false; }
                var stamp = StampOf(files);
                var rec = ReadRecord(guideRoot);
                if (rec != null && rec.Stamp == stamp && Directory.Exists(target)) return false;

                temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(temp);
                var root = System.IO.Path.GetFullPath(guideRoot).TrimEnd('\\', '/');
                foreach (var f in files)
                {
                    var to = System.IO.Path.Combine(temp, f.Rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                    File.Copy(System.IO.Path.Combine(root, f.Rel), to);
                }

                string old = null;
                if (Directory.Exists(target))
                {
                    old = target + ".old-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, old);
                }
                Directory.Move(temp, target);
                temp = null;
                var json = JsonConvert.SerializeObject(new Record { Source = root, Stamp = stamp, CopiedUtc = DateTime.UtcNow }, Formatting.Indented);
                var recTemp = target + ".json.tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(recTemp, json, new UTF8Encoding(false));
                if (File.Exists(target + ".json")) File.Delete(target + ".json");
                File.Move(recTemp, target + ".json");
                if (old != null) TryDelete(old);
                return true;
            }
            catch (Exception ex)
            {
                if (temp != null) TryDelete(temp);
                LogOnce(guideRoot, "couldn't be saved, so the previous copy stays", ex);
                return false;
            }
        }

        /// <summary>
        /// Queues <see cref="Refresh"/> on the thread pool, at most one at a time per guide, so copying never delays a lint.
        /// </summary>
        public void RefreshInBackground(string guideRoot)
        {
            if (string.IsNullOrEmpty(guideRoot)) return;
            string key;
            try { key = PathFor(guideRoot); } catch (Exception) { return; }
            lock (Running) if (!Running.Add(key)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Refresh(guideRoot); }
                catch (Exception ex) { Diagnostics.Log("style guide copy", ex); }
                finally { lock (Running) Running.Remove(key); }
            });
        }

        /// <summary>
        /// Leftovers of a refresh that stopped midway: a temporary copy is deleted; a copy set aside is put back when the new one
        /// never arrived, else deleted.
        /// </summary>
        private static void CleanStrays(string target)
        {
            var dir = System.IO.Path.GetDirectoryName(target);
            var name = System.IO.Path.GetFileName(target);
            foreach (var d in Directory.GetDirectories(dir, name + ".tmp-*")) TryDelete(d);
            foreach (var f in Directory.GetFiles(dir, name + ".json.tmp-*")) try { File.Delete(f); } catch (Exception) { }
            foreach (var d in Directory.GetDirectories(dir, name + ".old-*"))
            {
                if (!Directory.Exists(target)) { try { Directory.Move(d, target); continue; } catch (Exception) { } }
                TryDelete(d);
            }
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Diagnostics.Log("style guide copy: clean up", ex); }
        }

        /// <summary>Deletes the copies (and records) of guides that are no longer configured.</summary>
        public void Prune(IEnumerable<string> configuredRoots)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in configuredRoots ?? new string[0])
                    if (!string.IsNullOrEmpty(g)) try { keep.Add(Hash(g)); } catch (Exception) { }
                foreach (var d in Directory.GetDirectories(Folder))
                {
                    var n = System.IO.Path.GetFileName(d);
                    if (CopyName.IsMatch(n) && !keep.Contains(n.Substring(0, 16))) TryDelete(d);
                }
                foreach (var f in Directory.GetFiles(Folder))
                {
                    var n = System.IO.Path.GetFileName(f);
                    if (n.Length >= 16 && Regex.IsMatch(n, @"^[0-9a-f]{16}\.json", RegexOptions.IgnoreCase) && !keep.Contains(n.Substring(0, 16)))
                        try { File.Delete(f); } catch (Exception ex) { Diagnostics.Log("style guide copy: prune", ex); }
                }
            }
            catch (Exception ex) { Diagnostics.Log("style guide copy: prune", ex); }
        }

        // ---- the walk ---------------------------------------------------------------------------------------------

        private sealed class Entry
        {
            public string Rel;
            public long Size;
            public long WriteTicks;
        }

        private sealed class Limits
        {
            public int Files;
            public long Bytes;
            public bool Over;
        }

        /// <summary>
        /// The files a copy holds, relative to the guide root, as <see cref="GuideLayer.Load"/> reads them: for a Flare
        /// project its .flprj, StyleGuide folder (not Google), variable sets, glossaries, condition tag sets and
        /// FlareAI\layer.txt; for a plain folder the folder itself (not Google, and of FlareAI only layer.txt). Links are
        /// skipped at every level, before they are opened. Null when the guide is over the limits.
        /// </summary>
        private static List<Entry> Walk(string guideRoot)
        {
            var root = System.IO.Path.GetFullPath(guideRoot).TrimEnd('\\', '/');
            // The configured folder itself is the writer's own setting (it may be a mapped or linked folder); only what is
            // inside it is untrusted.
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The style guide folder isn't there.");
            var top = List(root);
            var isProject = top.Any(e => !e.IsDirectory && !e.IsLink && e.Name.EndsWith(".flprj", StringComparison.OrdinalIgnoreCase));
            var files = new List<Entry>();
            var limits = new Limits();
            if (isProject)
            {
                foreach (var e in top.Where(e => !e.IsDirectory && !e.IsLink && e.Name.EndsWith(".flprj", StringComparison.OrdinalIgnoreCase)))
                    if (!Add(files, limits, e.Name, e)) return null;
                foreach (var sub in ProjectFolders)
                {
                    if (!IsPlainFolder(root, sub)) continue;
                    var google = sub == ProjectFolders[0] ? "Google" : null;
                    if (!Descend(root, sub, google, null, files, limits)) return null;
                }
            }
            else if (!Descend(root, "", "Google", "FlareAI", files, limits)) return null;

            var layer = System.IO.Path.Combine(root, @"FlareAI\layer.txt");
            if (IsPlainFolder(root, "FlareAI"))
            {
                var e = Stat(layer);
                if (e != null && !e.IsDirectory && !e.IsLink && !Add(files, limits, @"FlareAI\layer.txt", e)) return null;
            }
            return files;
        }

        /// <summary>True when every folder on the way to <paramref name="rel"/> (inside root) is a real folder, not a link.</summary>
        private static bool IsPlainFolder(string root, string rel)
        {
            var path = root;
            foreach (var part in rel.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                path = System.IO.Path.Combine(path, part);
                var e = Stat(path);
                if (e == null || !e.IsDirectory || e.IsLink) return false;
            }
            return true;
        }

        /// <summary>
        /// Adds the allowed files under <paramref name="rel"/>, one folder at a time. <paramref name="skipTop"/> and
        /// <paramref name="flareAiTop"/> name subfolders of the first folder that are left out (Google) or only partly copied
        /// (FlareAI: layer.txt, added by the caller). False when a limit is reached.
        /// </summary>
        private static bool Descend(string root, string rel, string skipTop, string flareAiTop, List<Entry> files, Limits limits)
        {
            var pending = new Stack<KeyValuePair<string, bool>>();
            pending.Push(new KeyValuePair<string, bool>(rel, true));
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                var dir = item.Key.Length == 0 ? root : System.IO.Path.Combine(root, item.Key);
                foreach (var e in List(dir))
                {
                    if (e.IsLink) continue;   // a symlink, junction or mount point: never opened, never followed
                    var childRel = item.Key.Length == 0 ? e.Name : item.Key + "\\" + e.Name;
                    if (e.IsDirectory)
                    {
                        if (e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                        if (item.Value && skipTop != null && e.Name.Equals(skipTop, StringComparison.OrdinalIgnoreCase)) continue;
                        if (item.Value && flareAiTop != null && e.Name.Equals(flareAiTop, StringComparison.OrdinalIgnoreCase)) continue;
                        pending.Push(new KeyValuePair<string, bool>(childRel, false));
                        continue;
                    }
                    if (!Allowed(e.Name)) continue;
                    if (!Add(files, limits, childRel, e)) return false;
                }
            }
            return true;
        }

        private static bool Allowed(string name)
        {
            if (name.Equals("owners.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("models.json", StringComparison.OrdinalIgnoreCase)) return false;
            var ext = System.IO.Path.GetExtension(name);
            return Extensions.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Add(List<Entry> files, Limits limits, string rel, FindEntry e)
        {
            limits.Files++;
            limits.Bytes += e.Size;
            if (limits.Files > MaxFiles || limits.Bytes > MaxBytes) { limits.Over = true; return false; }
            files.Add(new Entry { Rel = rel, Size = e.Size, WriteTicks = e.WriteTicks });
            return true;
        }

        // ---- FindFirstFileW: attributes, size and reparse tag of each entry, without opening it ----------------------

        private const uint ReparsePoint = 0x400;
        private const uint DirectoryAttribute = 0x10;
        private const uint NameSurrogate = 0x20000000;

        /// <summary>
        /// True for a reparse tag that points somewhere else (symlink, junction, mount point): Windows marks those as name
        /// surrogates. OneDrive and SharePoint placeholders (the cloud tags) are not, and copy as ordinary files.
        /// </summary>
        public static bool IsLinkTag(uint tag) { return (tag & NameSurrogate) != 0; }

        private sealed class FindEntry
        {
            public string Name;
            public bool IsDirectory;
            public bool IsLink;
            public long Size;
            public long WriteTicks;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out Win32FindData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle, out Win32FindData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        private static readonly IntPtr Invalid = new IntPtr(-1);
        private const int FileNotFound = 2, PathNotFound = 3, NoMoreFiles = 18, AccessDenied = 5;

        private static FindEntry ToEntry(Win32FindData d)
        {
            var reparse = (d.FileAttributes & ReparsePoint) != 0;
            var write = ((long)(uint)d.LastWriteTime.dwHighDateTime << 32) | (uint)d.LastWriteTime.dwLowDateTime;
            return new FindEntry
            {
                Name = d.FileName,
                IsDirectory = (d.FileAttributes & DirectoryAttribute) != 0,
                IsLink = reparse && IsLinkTag(d.Reserved0),
                Size = ((long)d.FileSizeHigh << 32) | d.FileSizeLow,
                WriteTicks = write
            };
        }

        /// <summary>One entry's own data (a folder is not listed), or null when it isn't there.</summary>
        private static FindEntry Stat(string path)
        {
            Win32FindData d;
            var h = FindFirstFileW(path, out d);
            if (h == Invalid)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == FileNotFound || err == PathNotFound) return null;
                throw Fail(err, path);
            }
            FindClose(h);
            return ToEntry(d);
        }

        /// <summary>The entries of one folder, without "." and "..".</summary>
        private static List<FindEntry> List(string dir)
        {
            var list = new List<FindEntry>();
            Win32FindData d;
            var h = FindFirstFileW(System.IO.Path.Combine(dir, "*"), out d);
            if (h == Invalid)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == FileNotFound) return list;
                throw Fail(err, dir);
            }
            try
            {
                do
                {
                    if (d.FileName == "." || d.FileName == "..") continue;
                    list.Add(ToEntry(d));
                }
                while (FindNextFileW(h, out d));
                var last = Marshal.GetLastWin32Error();
                if (last != NoMoreFiles && last != 0) throw Fail(last, dir);
            }
            finally { FindClose(h); }
            return list;
        }

        private static Exception Fail(int err, string path)
        {
            var inner = new Win32Exception(err);
            if (err == AccessDenied) return new UnauthorizedAccessException(inner.Message, inner);
            if (err == PathNotFound) return new DirectoryNotFoundException(inner.Message);
            return new IOException(inner.Message, inner);
        }
    }
}
