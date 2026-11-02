using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// The zip Settings &gt; Send Diagnostics makes: the log files and a readme that lists them, nothing else. No Flare
    /// types, so tests can use it directly.
    /// </summary>
    internal static class DiagnosticsBundle
    {
        /// <summary>The log files (*.jsonl) in the folder, newest first. Empty when there is no folder.</summary>
        public static List<FileInfo> Files(string logsFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(logsFolder) || !Directory.Exists(logsFolder)) return new List<FileInfo>();
                return new DirectoryInfo(logsFolder).GetFiles("*.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception) { return new List<FileInfo>(); }
        }

        /// <summary>Writes the zip and returns its path. The log files are read while the log may still be writing to them.</summary>
        public static string Build(string logsFolder, string zipPath)
        {
            var files = Files(logsFolder);
            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    var entry = zip.CreateEntry(f.Name, CompressionLevel.Optimal);
                    using (var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var output = entry.Open())
                        input.CopyTo(output);
                }
                var readme = zip.CreateEntry("readme.txt");
                using (var w = new StreamWriter(readme.Open(), new UTF8Encoding(false)))
                {
                    w.WriteLine("House Style log files, made " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC.");
                    w.WriteLine("They hold what the plugin did: counts, times, rule names and error types.");
                    w.WriteLine("They have no topic text and no API key.");
                    w.WriteLine();
                    foreach (var f in files) w.WriteLine(f.Name + "  " + f.Length + " bytes");
                }
            }
            return zipPath;
        }
    }
}
