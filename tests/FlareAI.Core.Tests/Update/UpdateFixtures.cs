using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using FlareAI.Core.Update;
using Microsoft.CSharp;

namespace FlareAI.Core.Tests.Update
{
    /// <summary>A temp folder that removes junctions inside it before deleting (never follows one).</summary>
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; private set; }
        public string this[string rel] { get { return System.IO.Path.Combine(Path, rel); } }

        public void Dispose() { SafeDelete(Path); }

        public static void SafeDelete(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                if (Junction.IsLink(dir)) { Junction.Remove(dir); return; }
                foreach (var d in Directory.GetDirectories(dir)) SafeDelete(d);
                Directory.Delete(dir, true);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Fake plugin payloads and release zips with a real FlareAI.Flare.dll of a chosen version.</summary>
    internal static class FakePlugin
    {
        private static readonly Dictionary<string, byte[]> Dlls = new Dictionary<string, byte[]>();

        /// <summary>A tiny assembly whose file and product versions are <paramref name="version"/>.</summary>
        public static byte[] Dll(string version)
        {
            lock (Dlls)
            {
                byte[] bytes;
                if (Dlls.TryGetValue(version, out bytes)) return bytes;
                var out_ = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-fake-" + Guid.NewGuid().ToString("N") + ".dll");
                var numeric = version.Split('-')[0];
                var src = "[assembly: System.Reflection.AssemblyVersion(\"" + numeric + ".0\")]\n" +
                          "[assembly: System.Reflection.AssemblyFileVersion(\"" + numeric + ".0\")]\n" +
                          "[assembly: System.Reflection.AssemblyInformationalVersion(\"" + version + "+abc123\")]\n" +
                          "namespace FakeFlareAI { public class Plugin { } }";
                using (var csc = new CSharpCodeProvider())
                {
                    var p = new CompilerParameters { GenerateExecutable = false, OutputAssembly = out_ };
                    var r = csc.CompileAssemblyFromSource(p, src);
                    if (r.Errors.HasErrors) throw new InvalidOperationException(r.Errors[0].ToString());
                }
                bytes = File.ReadAllBytes(out_);
                try { File.Delete(out_); } catch (Exception) { }
                Dlls[version] = bytes;
                return bytes;
            }
        }

        /// <summary>Writes a whole plugin folder of <paramref name="version"/> (what the stager's sanity check expects).</summary>
        public static void Write(string dir, string version)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(dir, "StyleGuide"));
            File.WriteAllBytes(System.IO.Path.Combine(dir, "FlareAI.Flare.dll"), Dll(version));
            File.WriteAllText(System.IO.Path.Combine(dir, "FlareAI.Core.dll"), "core " + version);
            File.WriteAllText(System.IO.Path.Combine(dir, "Newtonsoft.Json.dll"), "json");
            File.WriteAllText(System.IO.Path.Combine(dir, "FlareAI.Updater.exe"), "updater");
            File.WriteAllText(System.IO.Path.Combine(dir, "StyleGuide", "rules.txt"), "rules " + version);
        }

        /// <summary>A release zip shaped like the real one: Install.ps1, FlareAI\... at the root.</summary>
        public static byte[] Zip(string version, Action<string> tamper = null)
        {
            using (var t = new TempDir())
            {
                var stage = t["stage"];
                Write(System.IO.Path.Combine(stage, "FlareAI"), version);
                if (tamper != null) tamper(stage);
                var zip = t["r.zip"];
                ZipFile.CreateFromDirectory(stage, zip);
                return File.ReadAllBytes(zip);
            }
        }
    }
}
