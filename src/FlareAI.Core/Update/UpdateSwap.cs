using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace FlareAI.Core.Update
{
    /// <summary>What the updater did, written to <c>update-result.json</c> and shown once on the next start.</summary>
    public sealed class UpdateResult
    {
        public bool Ok { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        /// <summary>Why it failed, in words for the writer. Null on success.</summary>
        public string Error { get; set; }
        public DateTime TimeUtc { get; set; }

        public static UpdateResult Read(string path)
        {
            try { return File.Exists(path) ? JsonConvert.DeserializeObject<UpdateResult>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch (Exception) { return null; }
        }

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// Installs a staged update while Flare is closed (run by FlareAI.Updater.exe). <c>current</c> is a real folder
    /// (never a junction: see <see cref="UpdateLayout"/> on why Flare 2026 needs a single hop), so the swap is folder renames:
    /// <list type="number">
    /// <item>copy the verified payload (<c>staged\&lt;ver&gt;\FlareAI</c>, or a kept <c>versions\&lt;ver&gt;</c>) to
    /// <c>current.new</c>;</item>
    /// <item>rename <c>current</c> to <c>current.old</c> (retried, then refused, while Flare still holds the files), then
    /// <c>current.new</c> to <c>current</c>;</item>
    /// <item>check that <c>current</c> holds the new version; if not, rename back (rollback);</item>
    /// <item>move <c>current.old</c> to <c>versions\&lt;previous&gt;</c> for rollback and remove older kept versions.</item>
    /// </list>
    /// <c>current</c> is missing only between two renames. A swap interrupted there is finished backwards on the next run.
    /// A <c>current</c> that is a junction (earlier release candidates) is replaced by a real folder the same way.
    /// </summary>
    public sealed class UpdateSwap
    {
        private readonly UpdateLayout _layout;

        public UpdateSwap(UpdateLayout layout)
        {
            _layout = layout;
            LockRetries = 5;
            LockRetryDelayMs = 2000;
        }

        /// <summary>Test seam: runs after the new files are in <c>current</c>, before they are accepted (throw to force a rollback).</summary>
        public Action<string> AfterSwap;

        /// <summary>How often renaming <c>current</c> away is retried while its files are in use, and the pause between tries.</summary>
        public int LockRetries { get; set; }
        public int LockRetryDelayMs { get; set; }

        private string Fresh { get { return _layout.Current + ".new"; } }
        private string Old { get { return _layout.Current + ".old"; } }

        public UpdateResult Apply(string version)
        {
            var result = new UpdateResult { To = version, TimeUtc = DateTime.UtcNow };
            var current = _layout.Current;
            try
            {
                RecoverInterruptedSwap();
                if (!Directory.Exists(current)) throw new UpdateException("House Style isn't installed in the per-user layout (" + current + " is missing).");
                var wasLink = Junction.IsLink(current);
                result.From = UpdateLayout.VersionIn(current);
                if (result.From != null && SemVer.Compare(result.From, version) == 0) throw new UpdateException("Version " + version + " is already installed.");

                // 1. The new files, as a real folder beside current.
                var source = UpdateStager.ReadyPayload(_layout, version);
                if (source != null)
                {
                    // The payload was verified when it was staged; check it is still the same files before it is installed.
                    try { UpdateStager.VerifyManifest(_layout.StagedDir(version)); }
                    catch (UpdateException) { UpdateStager.DeleteDir(_layout.StagedDir(version)); throw; }
                }
                if (source == null && File.Exists(Path.Combine(_layout.VersionDir(version), "FlareAI.Flare.dll"))) source = _layout.VersionDir(version);
                if (source == null) throw new UpdateException("No downloaded update for " + version + " was found.");
                UpdateStager.DeleteDir(Fresh);
                CopyDir(source, Fresh);

                // 2. Swap by renaming. The first rename fails while Flare still has the files open: nothing changes then.
                Retry(() => Directory.Move(current, Old));
                try { Directory.Move(Fresh, current); }
                catch (Exception) { Directory.Move(Old, current); throw; }

                // 3. Accept or roll back.
                try
                {
                    var now = UpdateLayout.VersionIn(current);
                    if (SemVer.Compare(now, version) != 0) throw new UpdateException("After the update the plugin folder holds " + (now ?? "nothing") + ", not " + version + ".");
                    var hook = AfterSwap;
                    if (hook != null) hook(current);
                }
                catch (Exception)
                {
                    RollBack();
                    UpdateStager.DeleteDir(_layout.StagedDir(version));   // a payload that failed its check isn't offered again
                    throw;
                }

                result.Ok = true;
                UpdateStager.DeleteDir(_layout.StagedDir(version));
                KeepPrevious(result.From, wasLink);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex is UpdateException ? ex.Message : "The files couldn't be swapped (" + ex.Message + ").";
                Diagnostics.Log("Update swap to " + version, ex);
                if (Directory.Exists(current)) UpdateStager.DeleteDir(Fresh);
            }
            try { result.Write(_layout.ResultPath); } catch (Exception ex) { Diagnostics.Log("update result", ex); }
            return result;
        }

        /// <summary>A swap that stopped between its two renames leaves current.old and no current: put it back. Leftovers go.</summary>
        private void RecoverInterruptedSwap()
        {
            if (!Directory.Exists(_layout.Current) && Directory.Exists(Old))
            {
                Diagnostics.Log("Update swap: restoring " + _layout.Current + " from an interrupted update");
                Directory.Move(Old, _layout.Current);
            }
            UpdateStager.DeleteDir(Fresh);
            UpdateStager.DeleteDir(Old);
        }

        /// <summary>current (the rejected version) back to current.new, current.old back to current; then current.new goes.</summary>
        private void RollBack()
        {
            try
            {
                Directory.Move(_layout.Current, Fresh);
                Directory.Move(Old, _layout.Current);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Update rollback", ex);
                throw new UpdateException("The update failed and the previous version couldn't be restored (" + ex.Message + "). Download the latest installer and run it to repair House Style.", ex);
            }
            UpdateStager.DeleteDir(Fresh);
        }

        /// <summary>
        /// current.old becomes versions\&lt;previous&gt; (the rollback copy); older kept versions are removed. When the old
        /// current was a junction, only the link goes: the folder it pointed to is already in versions\. Best effort.
        /// </summary>
        private void KeepPrevious(string previous, bool wasLink)
        {
            try
            {
                string keep;
                if (wasLink)
                {
                    keep = Junction.FinalPath(Old);
                    Junction.Remove(Old);
                }
                else
                {
                    keep = _layout.VersionDir(string.IsNullOrEmpty(previous) ? "previous" : previous);
                    UpdateStager.DeleteDir(keep);
                    Directory.CreateDirectory(_layout.Versions);
                    Directory.Move(Old, keep);
                }
                if (keep != null) Prune(keep);
            }
            catch (Exception ex) { Diagnostics.Log("keep previous version", ex); }
        }

        /// <summary>Removes kept versions other than <paramref name="keep"/> (best effort; a locked folder stays).</summary>
        private void Prune(string keep)
        {
            if (!Directory.Exists(_layout.Versions)) return;
            var k = Junction.FinalPath(keep) ?? keep;
            foreach (var dir in Directory.GetDirectories(_layout.Versions))
            {
                if (UpdateLayout.SamePath(Junction.FinalPath(dir) ?? dir, k)) continue;
                UpdateStager.DeleteDir(dir);
            }
        }

        private void Retry(Action action)
        {
            for (int i = 0; ; i++)
            {
                try { action(); return; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (i >= LockRetries)
                        throw new UpdateException("House Style's files are still in use (is Flare still running?), so nothing was changed. House Style offers the update again when Flare starts.", ex);
                    Thread.Sleep(LockRetryDelayMs);
                }
            }
        }

        /// <summary>Copies a folder tree (junctions inside it are skipped, never followed).</summary>
        internal static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from))
            {
                if (Junction.IsLink(d)) continue;
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
            }
        }
    }
}
