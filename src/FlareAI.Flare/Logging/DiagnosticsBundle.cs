using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// The zip of the log files the writer can send to support. It holds the *.log files and the crash marker, and
    /// nothing else. Building it makes no network call.
    /// </summary>
    internal static class DiagnosticsBundle
    {
        /// <summary>The names (not paths) of the files that go in the zip, in name order.</summary>
        public static List<string> Files(string folder)
        {
            var names = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return names;
                foreach (var f in Directory.GetFiles(folder, "*.log")) names.Add(Path.GetFileName(f));
                if (File.Exists(Path.Combine(folder, CrashTrail.MarkerName))) names.Add(CrashTrail.MarkerName);
            }
            catch (Exception) { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Writes the zip to <paramref name="outputZipPath"/> and returns that path.</summary>
        public static string Build(string folder, string outputZipPath)
        {
            var dir = Path.GetDirectoryName(outputZipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(outputZipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var name in Files(folder))
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using (var output = entry.Open())
                    using (var input = new FileStream(Path.Combine(folder, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        input.CopyTo(output);
                }
            }
            return outputZipPath;
        }
    }
}
