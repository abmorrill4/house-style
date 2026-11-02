using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Update
{
    /// <summary>
    /// Whether automatic updates are on. An IT policy file, <c>update-policy.json</c> in the plugin folder
    /// (<c>{"autoUpdate": false}</c>), wins over the writer's setting in either direction.
    /// </summary>
    public sealed class UpdatePolicy
    {
        public bool Enabled { get; private set; }
        /// <summary>True when the policy file decided (the Settings toggle should be read-only and say so).</summary>
        public bool FromPolicyFile { get; private set; }

        public static UpdatePolicy Resolve(bool settingAutoUpdate, string pluginDir)
        {
            var fromFile = ReadPolicyFile(pluginDir);
            return fromFile.HasValue
                ? new UpdatePolicy { Enabled = fromFile.Value, FromPolicyFile = true }
                : new UpdatePolicy { Enabled = settingAutoUpdate };
        }

        /// <summary>The policy file's <c>autoUpdate</c>, or null when there is no (readable) file. A file that exists but can't be read turns updates off.</summary>
        public static bool? ReadPolicyFile(string pluginDir)
        {
            if (string.IsNullOrEmpty(pluginDir)) return null;
            var path = Path.Combine(pluginDir, UpdateLayout.PolicyFileName);
            if (!File.Exists(path)) return null;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var v = o["autoUpdate"];
                if (v == null || v.Type != JTokenType.Boolean) return false;
                return (bool)v;
            }
            catch (Exception) { return false; }   // IT meant something by putting a file there: fail closed
        }
    }

    /// <summary>What the last check found, kept in <c>update-state.json</c>.</summary>
    public sealed class UpdateState
    {
        public DateTime? LastCheckUtc { get; set; }
        /// <summary>"ok", or why the last check failed (offline, timeout). Never shown as an error.</summary>
        public string LastCheckResult { get; set; }
        public string LatestVersion { get; set; }
        /// <summary>A verified update waiting in staged\ (the writer chose "Not now" when it was ready).</summary>
        public string StagedVersion { get; set; }
        /// <summary>The last install the updater finished: "0.3.0 to 0.4.0" or why it failed.</summary>
        public string LastInstall { get; set; }
        /// <summary>The newest What's new version the writer has seen (or chose not to see again).</summary>
        public string WhatsNewSeen { get; set; }

        public static UpdateState Load(string path)
        {
            try { if (File.Exists(path)) return JsonConvert.DeserializeObject<UpdateState>(File.ReadAllText(path, Encoding.UTF8)) ?? new UpdateState(); }
            catch (Exception) { }
            return new UpdateState();
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex) { Diagnostics.Log("update state", ex); }
        }
    }

    /// <summary>A release newer than the running version.</summary>
    public sealed class ReleaseInfo
    {
        public string Version { get; set; }
        public string Tag { get; set; }
        public string PageUrl { get; set; }
        public string Notes { get; set; }
        public string ZipName { get; set; }
        public string ZipUrl { get; set; }
        public long ZipSize { get; set; }
        public string ChecksumsUrl { get; set; }
        /// <summary>checksums.txt.sig, or null when the release has none (the stager then refuses it).</summary>
        public string SignatureUrl { get; set; }
    }

    /// <summary>
    /// The daily update check: asks GitHub for the latest release (<c>releases/latest</c>, which skips drafts and
    /// pre-releases) and compares it with the running version. At most once a day; silent when offline (logged only).
    /// Sends no data beyond the request itself (no identifiers, no key).
    /// </summary>
    public sealed class UpdateCheck
    {
        public const string DefaultUrl = "https://api.github.com/repos/abmorrill4/house-style/releases/latest";
        /// <summary>Points the check at another releases/latest URL (a local test server). Developers and testers only.</summary>
        public const string UrlEnvVar = "HOUSESTYLE_UPDATE_URL";
        public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

        public UpdateCheck(string currentVersion, string url = null)
        {
            CurrentVersion = currentVersion;
            Url = url ?? Environment.GetEnvironmentVariable(UrlEnvVar) ?? DefaultUrl;
            TimeoutMs = 10000;
        }

        public string CurrentVersion { get; private set; }
        public string Url { get; private set; }
        public int TimeoutMs { get; set; }

        /// <summary>True when a day has passed since the last check (or the clock went backwards).</summary>
        public static bool IsDue(UpdateState state, DateTime nowUtc)
        {
            if (state == null || !state.LastCheckUtc.HasValue) return true;
            var last = state.LastCheckUtc.Value;
            return nowUtc < last || nowUtc - last >= Interval;
        }

        /// <summary>
        /// Checks when due and records the attempt in <paramref name="state"/>. Returns the newer release, or null (not
        /// due, up to date, offline, or an answer that doesn't look like a release). Never throws.
        /// </summary>
        public ReleaseInfo CheckIfDue(UpdateState state, DateTime nowUtc)
        {
            if (!IsDue(state, nowUtc)) return null;
            state.LastCheckUtc = nowUtc;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var release = Parse(Get(Url, TimeoutMs));
                state.LastCheckResult = "ok";
                if (release == null) { LogCheck("up-to-date", null, timer); return null; }
                state.LatestVersion = release.Version;
                var newer = SemVer.Compare(release.Version, CurrentVersion) > 0;
                LogCheck(newer ? "available" : "up-to-date", release.Version, timer);
                return newer ? release : null;
            }
            catch (Exception ex)
            {
                state.LastCheckResult = ex.GetType().Name + ": " + ex.Message;
                Diagnostics.Log("Update check (" + Url + "): " + state.LastCheckResult);
                LogCheck("error", null, timer);
                return null;
            }
        }

        private static void LogCheck(string result, string latest, System.Diagnostics.Stopwatch timer)
        {
            try
            {
                if (latest == null) Logging.Log.Current.Info("update.check", Logging.F.Of("result", result), Logging.F.Of("ms", timer.ElapsedMilliseconds));
                else Logging.Log.Current.Info("update.check", Logging.F.Of("result", result), Logging.F.Of("latest", latest), Logging.F.Of("ms", timer.ElapsedMilliseconds));
            }
            catch (Exception) { }
        }

        /// <summary>The release in a GitHub <c>releases/latest</c> answer, or null when it has no usable zip and checksums.</summary>
        public static ReleaseInfo Parse(string json)
        {
            var o = JObject.Parse(json);
            if ((bool?)o["draft"] == true || (bool?)o["prerelease"] == true) return null;
            var tag = (string)o["tag_name"];
            var v = SemVer.Parse(tag);
            if (v == null) return null;
            var r = new ReleaseInfo { Tag = tag, Version = v.ToString(), PageUrl = (string)o["html_url"], Notes = (string)o["body"] };
            var assets = o["assets"] as JArray;
            if (assets == null) return null;
            foreach (var a in assets)
            {
                var name = (string)a["name"] ?? "";
                var url = (string)a["browser_download_url"];
                if (name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase)) r.ChecksumsUrl = url;
                else if (name.Equals(UpdateSigning.SignatureAssetName, StringComparison.OrdinalIgnoreCase)) r.SignatureUrl = IsAllowedUrl(url) ? url : null;
                else if (name.StartsWith("HouseStyle-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    r.ZipName = name; r.ZipUrl = url; r.ZipSize = (long?)a["size"] ?? 0;
                }
            }
            if (r.ZipUrl == null || r.ChecksumsUrl == null) return null;
            if (!IsAllowedUrl(r.ZipUrl) || !IsAllowedUrl(r.ChecksumsUrl)) return null;
            return r;
        }

        /// <summary>HTTPS anywhere; plain HTTP only to this computer (a local test server).</summary>
        public static bool IsAllowedUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;
        }

        // ---- HTTP (shared with the stager) ---------------------------------------------------------------------

        internal static HttpWebRequest Request(string url, int timeoutMs)
        {
            if (!IsAllowedUrl(url)) throw new WebException("Refusing a non-HTTPS update address: " + url);
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2 on net40
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "HouseStyle-Updater";   // GitHub rejects requests without one
            req.Accept = "application/vnd.github+json, application/octet-stream, */*";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = Math.Max(timeoutMs, 30000);
            req.AllowAutoRedirect = true;           // release assets redirect to GitHub's download host
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                proxy.Credentials = CredentialCache.DefaultCredentials;
                req.Proxy = proxy;
            }
            catch (Exception) { }
            return req;
        }

        internal static string Get(string url, int timeoutMs)
        {
            using (var resp = (HttpWebResponse)Request(url, timeoutMs).GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }
    }
}
