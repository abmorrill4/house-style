using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FlareAI.Core.Update
{
    /// <summary>Unzips an archive into an empty folder. Core targets net40 (no ZipArchive), so the plugin supplies it.</summary>
    public interface IArchiveExtractor
    {
        /// <summary>Extracts <paramref name="zipPath"/> into <paramref name="folder"/>. Throws on a corrupt archive or an entry outside the folder.</summary>
        void Extract(string zipPath, string folder);
    }

    /// <summary>Why staging an update failed: shown to the writer as the reason.</summary>
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
        public UpdateException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Downloads a release into <c>staged\</c>, proves it is the file House Style published, and unpacks it:
    /// <c>checksums.txt</c> must carry a valid signature by the release key (<see cref="UpdateSigning"/>), and the
    /// SHA-256 of the zip must match it; when the new FlareAI.Flare.dll is signed its
    /// Authenticode signature must verify, and when the running plugin is signed the new one must be too. The payload
    /// must look like the plugin (FlareAI.Flare.dll of the announced version, FlareAI.Core.dll, the updater).
    /// Anything wrong: the download is deleted and an <see cref="UpdateException"/> says why.
    /// </summary>
    public sealed class UpdateStager
    {
        public const long MaxZipBytes = 300L * 1024 * 1024;
        private readonly UpdateLayout _layout;
        private readonly IArchiveExtractor _extractor;

        public UpdateStager(UpdateLayout layout, IArchiveExtractor extractor)
        {
            _layout = layout;
            _extractor = extractor;
            TimeoutMs = 10000;
        }

        public int TimeoutMs { get; set; }
        /// <summary>The running plugin folder; when its DLL is signed, unsigned updates are refused. Null skips that rule.</summary>
        public string RunningPluginDir { get; set; }
        /// <summary>The release key's public half; replaceable in tests.</summary>
        public string PublicKeyXml { get; set; } = UpdateSigning.PublicKeyXml;
        /// <summary>Signature check; replaceable in tests.</summary>
        public Func<string, SignatureStatus> CheckSignature = Authenticode.Check;

        /// <summary>The folder (inside <c>staged\&lt;version&gt;</c>) holding a verified plugin payload, if one is ready.</summary>
        public static string ReadyPayload(UpdateLayout layout, string version)
        {
            var dir = Path.Combine(layout.StagedDir(version), "FlareAI");
            return File.Exists(Path.Combine(dir, "FlareAI.Flare.dll")) && File.Exists(Path.Combine(layout.StagedDir(version), ".verified")) ? dir : null;
        }

        /// <summary>Downloads, verifies and unpacks <paramref name="release"/>. Returns the payload folder (staged\&lt;ver&gt;\FlareAI).</summary>
        public string Stage(ReleaseInfo release)
        {
            var ready = ReadyPayload(_layout, release.Version);
            if (ready != null) return ready;
            Directory.CreateDirectory(_layout.Staged);
            var zip = Path.Combine(_layout.Staged, Path.GetFileName(release.ZipName ?? ("HouseStyle-" + release.Version + ".zip")));
            var work = _layout.StagedDir(release.Version) + ".tmp";
            try
            {
                string checksums;
                try { checksums = UpdateCheck.Get(release.ChecksumsUrl, TimeoutMs); }
                catch (Exception ex) { throw new UpdateException("Couldn't download the checksums of the update (" + ex.Message + ").", ex); }
                // The checksums prove the zip; the signature proves the checksums came from House Style's release key.
                if (release.SignatureUrl == null) throw new UpdateException("The update isn't signed by House Style, so it was discarded.");
                string signature;
                try { signature = UpdateCheck.Get(release.SignatureUrl, TimeoutMs); }
                catch (Exception ex) { throw new UpdateException("Couldn't download the signature of the update (" + ex.Message + ").", ex); }
                if (!UpdateSigning.Verify(Encoding.UTF8.GetBytes(checksums), signature, PublicKeyXml))
                    throw new UpdateException("The update's signature doesn't match House Style's release key, so it was discarded.");
                var expected = ExpectedHash(checksums, Path.GetFileName(zip));
                if (expected == null) throw new UpdateException("The release's checksums.txt has no entry for " + Path.GetFileName(zip) + ".");

                Download(release.ZipUrl, zip);
                var actual = Sha256(zip);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("The downloaded update doesn't match its published checksum, so it was discarded.");

                DeleteDir(work);
                Directory.CreateDirectory(work);
                try { _extractor.Extract(zip, work); }
                catch (Exception ex) { throw new UpdateException("The update archive is damaged (" + ex.Message + ").", ex); }
                Validate(Path.Combine(work, "FlareAI"), release.Version);

                var final = _layout.StagedDir(release.Version);
                DeleteDir(final);
                Directory.Move(work, final);
                File.WriteAllText(Path.Combine(final, ".verified"), actual);
                WriteManifest(final);
                return Path.Combine(final, "FlareAI");
            }
            catch (UpdateException) { DeleteDir(work); throw; }
            catch (Exception ex) { DeleteDir(work); throw new UpdateException("Couldn't download the update (" + ex.Message + ").", ex); }
            finally { try { if (File.Exists(zip)) File.Delete(zip); } catch (Exception) { } }
        }

        /// <summary>Throws unless <paramref name="payload"/> is a whole plugin of <paramref name="version"/> with a valid signature (if signed).</summary>
        public void Validate(string payload, string version)
        {
            foreach (var f in new[] { "FlareAI.Flare.dll", "FlareAI.Core.dll", "Newtonsoft.Json.dll", "FlareAI.Updater.exe" })
                if (!File.Exists(Path.Combine(payload, f))) throw new UpdateException("The update is incomplete: " + f + " is missing.");
            if (!Directory.Exists(Path.Combine(payload, "StyleGuide"))) throw new UpdateException("The update is incomplete: the StyleGuide folder is missing.");
            var inZip = UpdateLayout.VersionIn(payload);
            if (SemVer.Compare(inZip, version) != 0) throw new UpdateException("The update says it is " + version + " but contains " + (inZip ?? "an unknown version") + ".");

            var dll = Path.Combine(payload, "FlareAI.Flare.dll");
            var sig = CheckSignature(dll);
            if (sig == SignatureStatus.Invalid) throw new UpdateException("The update's digital signature is not valid, so it was discarded.");
            if (sig == SignatureStatus.Unsigned && RunningPluginDir != null &&
                CheckSignature(Path.Combine(RunningPluginDir, "FlareAI.Flare.dll")) == SignatureStatus.Valid)
                throw new UpdateException("The update is not signed but the installed version is, so it was discarded.");
        }

        public const string ManifestName = ".manifest";

        /// <summary>
        /// Records the SHA-256 of every file in <c>staged\&lt;ver&gt;\FlareAI</c> right after it was verified, so the swap can
        /// tell that nothing changed in the folder between staging (while Flare ran) and the swap (after it closed).
        /// </summary>
        public static void WriteManifest(string stagedVersionDir)
        {
            var payload = Path.Combine(stagedVersionDir, "FlareAI");
            var sb = new StringBuilder();
            foreach (var f in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
                sb.Append(Sha256(f)).Append("  ").Append(f.Substring(payload.Length + 1)).Append('\n');
            File.WriteAllText(Path.Combine(stagedVersionDir, ManifestName), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Throws unless the staged payload still holds exactly the files, with the hashes, that were verified when it was staged.</summary>
        public static void VerifyManifest(string stagedVersionDir)
        {
            var path = Path.Combine(stagedVersionDir, ManifestName);
            var payload = Path.Combine(stagedVersionDir, "FlareAI");
            if (!File.Exists(path)) throw new UpdateException("The downloaded update can't be checked again before it is installed, so it was discarded.");
            var expected = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var i = line.IndexOf("  ", StringComparison.Ordinal);
                if (i > 0) expected[line.Substring(i + 2)] = line.Substring(0, i);
            }
            var actual = Directory.GetFiles(payload, "*", SearchOption.AllDirectories);
            bool same = actual.Length == expected.Count;
            for (int k = 0; same && k < actual.Length; k++)
            {
                string hash;
                same = expected.TryGetValue(actual[k].Substring(payload.Length + 1), out hash) &&
                       string.Equals(hash, Sha256(actual[k]), StringComparison.OrdinalIgnoreCase);
            }
            if (!same) throw new UpdateException("The downloaded update changed after it was checked, so it was discarded.");
        }

        /// <summary>The hash for <paramref name="fileName"/> in a sha256sum-style file ("hash  name" or "hash *name"); a single bare hash also counts.</summary>
        public static string ExpectedHash(string checksums, string fileName)
        {
            string bare = null; int lines = 0;
            foreach (var raw in (checksums ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                lines++;
                var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (!IsHex64(parts[0])) continue;
                if (parts.Length == 1) { bare = parts[0]; continue; }
                var name = parts[1].Trim().TrimStart('*');
                if (string.Equals(Path.GetFileName(name), fileName, StringComparison.OrdinalIgnoreCase)) return parts[0].ToLowerInvariant();
            }
            return lines == 1 && bare != null ? bare.ToLowerInvariant() : null;
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        private void Download(string url, string path)
        {
            var part = path + ".part";
            using (var resp = (HttpWebResponse)UpdateCheck.Request(url, TimeoutMs).GetResponse())
            {
                UpdateCheck.RequireAllowedFinalUrl(resp);
                if (resp.ContentLength > MaxZipBytes) throw new UpdateException("The update is unexpectedly large.");
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(part))
                {
                    var buf = new byte[81920]; long total = 0; int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxZipBytes) throw new UpdateException("The update is unexpectedly large.");
                        output.Write(buf, 0, n);
                    }
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }

        private static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        internal static void DeleteDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                if (Junction.IsLink(dir)) Junction.Remove(dir);
                else Directory.Delete(dir, true);
            }
            catch (Exception ex) { Diagnostics.Log("delete " + dir, ex); }
        }
    }

    public enum SignatureStatus { Unsigned, Valid, Invalid }

    /// <summary>Authenticode check of a file with WinVerifyTrust (no revocation check over the network).</summary>
    public static class Authenticode
    {
        public static SignatureStatus Check(string path)
        {
            try { X509Certificate.CreateFromSignedFile(path); }
            catch (Exception) { return SignatureStatus.Unsigned; }
            try { return Verify(path) == 0 ? SignatureStatus.Valid : SignatureStatus.Invalid; }
            catch (Exception) { return SignatureStatus.Invalid; }
        }

        private static int Verify(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x00000040   // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally { Marshal.FreeHGlobal(pFile); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    }
}
