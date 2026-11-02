using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Settings;
using FlareAI.Core.Update;
using FlareAI.Flare.UI;

namespace FlareAI.Flare
{
    /// <summary>
    /// Automatic updates and What's new, as the writer sees them (issue #101):
    /// on start, the result of the last install ("Updated to 0.4.0" with What's new, or "didn't install, still on 0.3.2");
    /// then, in the background and at most once a day, a check for a newer release, offered as a notice with
    /// "Update now" and "Later". Update now downloads and verifies it, then offers to finish when Flare closes;
    /// FlareAI.Updater.exe waits for Flare to exit and swaps the files. Only the per-user install (Install.cmd's default)
    /// can update itself; the Program Files copy (-NoJunction) never checks.
    /// </summary>
    internal sealed class UpdateFlow
    {
        private static readonly string[] UpdaterFiles = { "FlareAI.Updater.exe", "FlareAI.Updater.exe.config", "FlareAI.Core.dll", "Newtonsoft.Json.dll" };

        private readonly Func<Form> _owner;
        private readonly string _pluginDir;
        private readonly string _version;
        private readonly UpdateLayout _layout = UpdateLayout.Default;
        private bool _busy;

        public UpdateFlow(Func<Form> owner, string pluginDir, string version)
        {
            _owner = owner; _pluginDir = pluginDir; _version = version;
        }

        /// <summary>Called once Flare has loaded the plugin: a few seconds later, the last result, What's new, then the check.</summary>
        public void Start()
        {
            var timer = new Timer { Interval = 4000 };
            timer.Tick += (s, e) =>
            {
                timer.Stop(); timer.Dispose();
                try { ShowLastResultOrWhatsNew(); } catch (Exception ex) { Diagnostics.Log("update result", ex); }
                try { CheckInBackground(); } catch (Exception ex) { Diagnostics.Log("update check", ex); }
            };
            timer.Start();
        }

        // ---- after an update / first run of a version ---------------------------------------------------------

        private void ShowLastResultOrWhatsNew()
        {
            var state = UpdateState.Load(_layout.StatePath);
            var result = UpdateResult.Read(_layout.ResultPath);
            if (result != null)
            {
                try { File.Delete(_layout.ResultPath); } catch (Exception) { }
                state.LastInstall = result.Ok ? (result.From ?? "?") + " to " + result.To : "failed (" + result.To + "): " + result.Error;
                if (result.Ok) state.StagedVersion = null;
                state.Save(_layout.StatePath);
                if (!result.Ok || SemVer.Compare(result.To, _version) != 0)
                {
                    var why = result.Ok ? "Flare loaded " + _version + " instead." : result.Error;
                    Diagnostics.Log("Update to " + result.To + " failed: " + why);
                    Toast.Show(_owner(), "The update to House Style " + result.To + " didn't install, so you're still on " + _version + ".", "Details",
                               () => Notify.Error(_owner(), "The update to House Style " + result.To + " didn't install. You're still on " + _version + ", and it works as before.\n\nReason: " + why,
                                                  "House Style tries again with the next update. To install it by hand, download it from the releases page and run Install.cmd.", why, "Update"));
                    return;
                }
                ShowWhatsNew(state, "Updated to House Style " + _version + ". Here's what's new.");
                return;
            }
            // A writer with no settings yet has never used an earlier version: nothing is "new" to them, so skip the window.
            if (!File.Exists(PluginSettings.SettingsPath)) { state.WhatsNewSeen = _version; state.Save(_layout.StatePath); return; }
            if (WhatsNew.Load(_pluginDir).Unseen(state.WhatsNewSeen, _version).Count > 0) ShowWhatsNew(state, null);
        }

        private void ShowWhatsNew(UpdateState state, string banner)
        {
            var sections = WhatsNew.Load(_pluginDir).Unseen(state.WhatsNewSeen, _version);
            if (sections.Count == 0 && banner == null) return;
            using (var f = new WhatsNewForm(sections, banner, true))
            {
                f.ShowDialog(_owner());
                if (f.DontShowAgain) { state.WhatsNewSeen = _version; state.Save(_layout.StatePath); }
            }
        }

        /// <summary>The What's new command: every section up to this version.</summary>
        public static void ShowAll(IWin32Window owner, string pluginDir, string version)
        {
            var notes = WhatsNew.Load(pluginDir);
            var sections = notes.UpTo(version);
            if (sections.Count == 0) sections = notes.UpTo(null);
            using (var f = new WhatsNewForm(sections, null, false)) f.ShowDialog(owner);
        }

        // ---- the daily check --------------------------------------------------------------------------------

        /// <summary>Why updates are off, or null when this install checks for updates.</summary>
        public string WhyOff()
        {
            string reason;
            if (!_layout.Manages(_pluginDir, out reason))
            {
                Diagnostics.Log("Updates: install not managed: " + reason);
                if (_layout.NeedsRepair) return "paused: your install uses an older folder layout. Run Install.cmd from the latest download once to repair it";
                return "not available: this copy was installed by hand. Run Install.cmd from the download once to turn them on";
            }
            var policy = UpdatePolicy.Resolve(PluginSettings.Load().AutoUpdate, _pluginDir);
            if (!policy.Enabled) return policy.FromPolicyFile ? "turned off by your IT department (update-policy.json)" : "turned off in Settings";
            return null;
        }

        private void CheckInBackground()
        {
            var off = WhyOff();
            if (off != null)
            {
                Diagnostics.Log("Updates: " + off);
                try { FlareAI.Core.Logging.Log.Current.Info("update.check", FlareAI.Core.Logging.F.Of("result", "off")); } catch (Exception) { }
                return;
            }
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                var state = UpdateState.Load(_layout.StatePath);
                var release = new UpdateCheck(_version).CheckIfDue(state, DateTime.UtcNow);
                state.Save(_layout.StatePath);
                e.Result = new object[] { release, state };
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (e.Error != null) { Diagnostics.Log("update check", e.Error); ErrorReporter.Background(_owner(), "House Style couldn't check for updates.", e.Error); return; }
                var release = (ReleaseInfo)((object[])e.Result)[0];
                var state = (UpdateState)((object[])e.Result)[1];
                // Automatic: download, verify and queue the install for when Flare closes. Settings or update-policy.json turn it off.
                if (release != null) Download(release);
                else if (state.StagedVersion != null && SemVer.Compare(state.StagedVersion, _version) > 0 && UpdateStager.ReadyPayload(_layout, state.StagedVersion) != null)
                    LaunchUpdater(state.StagedVersion);
            };
            worker.RunWorkerAsync();
        }

        private void Download(ReleaseInfo release)
        {
            if (_busy) return;
            _busy = true;
            Diagnostics.Log("Update available: " + release.Version + " (running " + _version + "); downloading");
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                var stager = new UpdateStager(_layout, new ZipExtractor()) { RunningPluginDir = _pluginDir };
                e.Result = stager.Stage(release);
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                _busy = false;
                if (e.Error != null)
                {
                    Diagnostics.Log("Update download " + release.Version, e.Error);
                    ErrorReporter.Background(_owner(), "House Style couldn't download the update.", e.Error);
                    var why = e.Error is UpdateException ? e.Error.Message : "Something went wrong while downloading (" + e.Error.Message + ").";
                    Toast.Show(_owner(), "House Style couldn't download " + release.Version + ": " + why.TrimEnd('.') + ". You're still on " + _version + "; it tries again tomorrow.");
                    return;
                }
                var state = UpdateState.Load(_layout.StatePath);
                state.StagedVersion = release.Version;
                state.Save(_layout.StatePath);
                LaunchUpdater(release.Version);
            };
            worker.RunWorkerAsync();
        }

        /// <summary>Starts FlareAI.Updater.exe (a copy in HouseStyle\updater, so it locks nothing it replaces); it waits for Flare to exit.</summary>
        private void LaunchUpdater(string version)
        {
            try
            {
                var source = File.Exists(Path.Combine(_pluginDir, UpdaterFiles[0])) ? _pluginDir : UpdateStager.ReadyPayload(_layout, version);
                if (source == null) throw new FileNotFoundException("FlareAI.Updater.exe is missing.");
                Directory.CreateDirectory(_layout.UpdaterDir);
                foreach (var f in UpdaterFiles)
                {
                    var from = Path.Combine(source, f);
                    if (!File.Exists(from)) continue;
                    try { File.Copy(from, Path.Combine(_layout.UpdaterDir, f), true); }
                    catch (IOException) { /* an updater from earlier is still waiting: it uses the same files */ }
                }
                var psi = new ProcessStartInfo(Path.Combine(_layout.UpdaterDir, UpdaterFiles[0]),
                    "--root \"" + _layout.Root + "\" --version \"" + version + "\" --wait-pid " + Process.GetCurrentProcess().Id)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _layout.UpdaterDir };
                Process.Start(psi).Dispose();
                Diagnostics.Log("Updater started for " + version + "; it installs when Flare closes");
                Toast.Show(_owner(), "House Style " + version + " installs when you close Flare. Your next start shows what's new.");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Updater start", ex);
                Notify.Failed(_owner(), "start the update", ex, "Update");
            }
        }

        /// <summary>For Diagnostics (Help/About): version, whether updates are on, the last check and the last install.</summary>
        public string Describe()
        {
            var state = UpdateState.Load(_layout.StatePath);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("House Style " + _version);
            var off = WhyOff();
            sb.AppendLine("Automatic updates: " + (off == null ? "on" : off));
            sb.AppendLine("Last update check: " + (state.LastCheckUtc.HasValue ? state.LastCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " (" + (state.LastCheckResult ?? "?") + ")" : "never")
                          + (state.LatestVersion == null ? "" : ", latest release " + state.LatestVersion));
            if (state.StagedVersion != null) sb.AppendLine("Downloaded, waiting to install: " + state.StagedVersion);
            if (state.LastInstall != null) sb.AppendLine("Last install: " + state.LastInstall);
            return sb.ToString();
        }
    }
}
