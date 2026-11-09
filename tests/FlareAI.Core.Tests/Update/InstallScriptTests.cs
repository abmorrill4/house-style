using System;
using System.Diagnostics;
using System.IO;
using FlareAI.Core.Update;
using Xunit;

namespace FlareAI.Core.Tests.Update
{
    /// <summary>
    /// scripts\release\Install.ps1 under Windows PowerShell 5.1 (what testers have): it parses, and the per-user install
    /// runs end to end against a fake Flare (FLARE_ROOT) and a fake %LOCALAPPDATA%, so no admin rights are needed. The
    /// plugin is turned on under a throwaway registry key (HOUSESTYLE_PLUGINS_KEY), never Flare's real one.
    /// </summary>
    public class InstallScriptTests : IDisposable
    {
        /// <summary>This test class's stand-in for HKCU\Software\MadCap Software\Flare\Plugins\House Style.</summary>
        private readonly string _testKey = @"Software\HouseStyleInstallTests\" + Guid.NewGuid().ToString("N") + @"\House Style";

        public void Dispose()
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_testKey.Substring(0, _testKey.LastIndexOf('\\')), false); } catch (Exception) { }
            try
            {
                using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HouseStyleInstallTests", true))
                    if (parent != null && parent.SubKeyCount == 0) Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\HouseStyleInstallTests", false);
            }
            catch (Exception) { }
        }

        /// <summary>Activated under the test key, or null when the key or value is missing.</summary>
        private string Activated()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_testKey))
                return k == null ? null : k.GetValue("Activated") as string;
        }

        private Microsoft.Win32.RegistryValueKind ActivatedKind()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_testKey))
                return k.GetValueKind("Activated");
        }

        private void ForgetActivation() { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_testKey, false); }
        private static string Script { get { return Path.Combine(WhatsNewTests.RepoRoot(), "scripts", "release", "Install.ps1"); } }

        private static string PowerShell51
        {
            get
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                return File.Exists(p) ? p : null;
            }
        }

        [Fact]
        public void ParsesInWindowsPowerShell51()
        {
            if (PowerShell51 == null) return;
            var r = Run("-NoProfile -Command \"$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + Script + "', [ref]$null, [ref]$e); " +
                        "if ($e.Count) { $e | ForEach-Object { $_.ToString() }; exit 1 }; $PSVersionTable.PSVersion.Major\"", null);
            Assert.True(r.Code == 0, r.Output);
            Assert.Equal("5", r.Output.Trim());
        }

        [Fact]
        public void ElevatedLinkCommand_IsSelfContained_ParsesAndRefusesOtherFolders()
        {
            if (PowerShell51 == null) return;
            using (var t = new TempDir())
            {
                var harness = Path.Combine(t["harness"], "check.ps1");
                Directory.CreateDirectory(Path.GetDirectoryName(harness));
                File.WriteAllText(harness, @"
param([string]$Script)
$tok = $null; $err = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Script, [ref]$tok, [ref]$err)
foreach ($f in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) { . ([scriptblock]::Create($f.Extent.Text)) }
$pf = [Environment]::GetFolderPath('ProgramFiles')
$good = Join-Path $pf 'MadCap Software\MadCap Flare 2026\Flare.app\Plugins\FlareAI'
'good=' + (Test-AllowedLinkDest $good)
'other=' + (Test-AllowedLinkDest 'C:\Windows\System32')
'traversal=' + (Test-AllowedLinkDest ($pf + '\MadCap Software\MadCap Flare 2026\Flare.app\Plugins\..\..\..\..\x\Plugins\FlareAI'))
$cmd = New-LinkCommand $good ""C:\Users\o'neil\AppData\Local\HouseStyle\current""
$e = $null
[void][Management.Automation.Language.Parser]::ParseInput($cmd, [ref]$tok, [ref]$e)
'parseErrors=' + $e.Count
$bad = New-LinkCommand 'C:\Windows\System32' 'C:\x\HouseStyle\current'
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bad))
& (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -NonInteractive -EncodedCommand $enc
'childExit=' + $LASTEXITCODE
");
                var r = Run("-NoProfile -ExecutionPolicy Bypass -File \"" + harness + "\" -Script \"" + Script + "\"", null);
                Assert.Contains("good=True", r.Output);
                Assert.Contains("other=False", r.Output);
                Assert.Contains("traversal=False", r.Output);
                Assert.Contains("parseErrors=0", r.Output);
                Assert.Contains("childExit=1", r.Output);
            }
        }

        [Fact]
        public void LauncherCallsWindowsPowerShellByItsFullPath()
        {
            var cmd = File.ReadAllText(Path.Combine(WhatsNewTests.RepoRoot(), "scripts", "release", "Install.cmd"));
            Assert.Contains(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", cmd);
        }

        [Fact]
        public void PerUserInstall_LinksFlare_ReinstallKeepsData_AndMigratesACopyInstall()
        {
            if (PowerShell51 == null) return;
            using (var t = new TempDir())
            {
                // A fake Flare 99 outside Program Files (so no elevation).
                var flare = t["MadCap Flare 99"];
                Directory.CreateDirectory(Path.Combine(flare, "Flare.app"));
                File.WriteAllText(Path.Combine(flare, "Flare.app", "Flare.exe"), "");
                var plugins = Path.Combine(flare, @"Flare.app\Plugins\FlareAI");
                FakePlugin.Write(plugins, "0.7.0");

                var kit = t["unzipped"];
                FakePlugin.Write(Path.Combine(kit, "FlareAI"), "0.8.0");
                File.Copy(Script, Path.Combine(kit, "Install.ps1"));
                var local = t["Local"];
                Directory.CreateDirectory(local);

                Assert.Null(Activated());
                var first = Install(kit, flare, local);
                Assert.True(first.Code == 0, first.Output);
                // Flare shows the House Style tab on first start: Activated = "True" (REG_SZ) for the installing user.
                Assert.Equal("True", Activated());
                Assert.Equal(Microsoft.Win32.RegistryValueKind.String, ActivatedKind());
                Assert.Contains("The House Style tab appears and a short setup opens.", first.Output);
                Assert.Contains("If the House Style tab doesn't appear", first.Output);
                Assert.DoesNotContain("(first install only)", first.Output);
                var layout = new UpdateLayout(Path.Combine(local, "HouseStyle"));
                Assert.True(Junction.IsLink(plugins), "Plugins\\FlareAI should be a link now");
                AssertOneHop(plugins, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(plugins));
                string why;
                Assert.True(layout.Manages(plugins, out why), why);

                // Something only the writer has (a previous version kept for rollback) must survive a re-install.
                FakePlugin.Write(layout.VersionDir("0.7.5"), "0.7.5");
                var second = Install(kit, flare, local);
                Assert.True(second.Code == 0, second.Output);
                AssertOneHop(plugins, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(plugins));
                Assert.True(File.Exists(Path.Combine(layout.VersionDir("0.7.5"), "FlareAI.Flare.dll")), "re-install deleted a kept version");
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.VersionDir("0.8.0")));   // the replaced copy, kept for rollback
                Assert.True(File.Exists(Path.Combine(layout.Current, "StyleGuide", "rules.txt")));

                // The updater can take it from here.
                FakePlugin.Write(Path.Combine(layout.StagedDir("0.9.0"), "FlareAI"), "0.9.0");
                File.WriteAllText(Path.Combine(layout.StagedDir("0.9.0"), ".verified"), "x");
                UpdateStager.WriteManifest(layout.StagedDir("0.9.0"));
                var swapped = new UpdateSwap(layout) { LockRetries = 0 }.Apply("0.9.0");
                Assert.True(swapped.Ok, swapped.Error);
                AssertOneHop(plugins, layout);
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(plugins));
            }
        }

        [Fact]
        public void PerUserInstall_OverTheJunctionLayoutOfEarlierBuilds_MakesCurrentARealFolder()
        {
            if (PowerShell51 == null) return;
            using (var t = new TempDir())
            {
                // An earlier release candidate: current -> versions\0.7.0 (a junction) and Plugins\FlareAI -> current.
                // Flare 2026 crashes at startup with that chain; the installer must leave a single hop.
                var flare = FakeFlare(t);
                var plugins = Path.Combine(flare, @"Flare.app\Plugins\FlareAI");
                var local = t["Local"];
                var layout = new UpdateLayout(Path.Combine(local, "HouseStyle"));
                FakePlugin.Write(layout.VersionDir("0.7.0"), "0.7.0");
                Junction.Create(layout.Current, layout.VersionDir("0.7.0"));
                Directory.CreateDirectory(Path.GetDirectoryName(plugins));
                Junction.Create(plugins, layout.Current);
                Assert.True(layout.NeedsRepair);

                var kit = t["unzipped"];
                FakePlugin.Write(Path.Combine(kit, "FlareAI"), "0.8.0");
                File.Copy(Script, Path.Combine(kit, "Install.ps1"));

                var r = Install(kit, flare, local);
                Assert.True(r.Code == 0, r.Output);
                AssertOneHop(plugins, layout);
                Assert.False(layout.NeedsRepair);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(plugins));
                Assert.Equal("0.7.0", UpdateLayout.VersionIn(layout.VersionDir("0.7.0")));   // the old link's target is untouched
                string why;
                Assert.True(layout.Manages(plugins, out why), why);
            }
        }

        [Fact]
        public void PerUserInstall_RefusesToRepointALinkThatAnotherInstallOwns_ButReplacesADanglingOne()
        {
            if (PowerShell51 == null) return;
            using (var t = new TempDir())
            {
                // Issue #137: Plugins\FlareAI is machine-wide. A live link into another HouseStyle root is someone else's install.
                var flare = FakeFlare(t);
                var plugins = Path.Combine(flare, @"Flare.app\Plugins\FlareAI");
                var other = Path.Combine(t["OtherUser"], @"HouseStyle\current");
                FakePlugin.Write(other, "0.7.0");
                Directory.CreateDirectory(Path.GetDirectoryName(plugins));
                Junction.Create(plugins, other);
                var local = t["Local"];
                var kit = t["unzipped"];
                FakePlugin.Write(Path.Combine(kit, "FlareAI"), "0.8.0");
                File.Copy(Script, Path.Combine(kit, "Install.ps1"));

                var r = Install(kit, flare, local);
                Assert.NotEqual(0, r.Code);
                Assert.Contains("belongs to another install", r.Output);
                Assert.True(UpdateLayout.SamePath(Junction.FinalPath(plugins), Junction.FinalPath(other)), "the link was moved");

                // The other install is gone (the link dangles): this one takes over.
                Directory.Delete(other, true);
                r = Install(kit, flare, local);
                Assert.True(r.Code == 0, r.Output);
                AssertOneHop(plugins, new UpdateLayout(Path.Combine(local, "HouseStyle")));
            }
        }

        [Fact]
        public void PerUserInstall_RepointsAPluginsLinkThatSkipsCurrent()
        {
            if (PowerShell51 == null) return;
            using (var t = new TempDir())
            {
                // A hand-made workaround: Plugins\FlareAI -> versions\0.7.0 directly, and the old junction current.
                var flare = FakeFlare(t);
                var plugins = Path.Combine(flare, @"Flare.app\Plugins\FlareAI");
                var local = t["Local"];
                var layout = new UpdateLayout(Path.Combine(local, "HouseStyle"));
                FakePlugin.Write(layout.VersionDir("0.7.0"), "0.7.0");
                Junction.Create(layout.Current, layout.VersionDir("0.7.0"));
                Directory.CreateDirectory(Path.GetDirectoryName(plugins));
                Junction.Create(plugins, layout.VersionDir("0.7.0"));

                var kit = t["unzipped"];
                FakePlugin.Write(Path.Combine(kit, "FlareAI"), "0.8.0");
                File.Copy(Script, Path.Combine(kit, "Install.ps1"));

                var r = Install(kit, flare, local);
                Assert.True(r.Code == 0, r.Output);
                AssertOneHop(plugins, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(plugins));
                Assert.Equal("0.7.0", UpdateLayout.VersionIn(layout.VersionDir("0.7.0")));
            }
        }

        /// <summary>Plugins\FlareAI is one junction straight to a real HouseStyle\current.</summary>
        private static void AssertOneHop(string plugins, UpdateLayout layout)
        {
            Assert.True(Junction.IsLink(plugins));
            Assert.True(Directory.Exists(layout.Current));
            Assert.False(Junction.IsLink(layout.Current), "current must be a real folder (Flare 2026 crashes on a junction to a junction)");
            Assert.True(UpdateLayout.SamePath(Junction.FinalPath(plugins), Junction.FinalPath(layout.Current)));
            Assert.False(Directory.Exists(layout.Current + ".installing"));
            Assert.False(Directory.Exists(layout.Current + ".old"));
        }

        /// <summary>A fake Flare 99 outside Program Files (so no elevation).</summary>
        private static string FakeFlare(TempDir t)
        {
            var flare = t["MadCap Flare 99"];
            Directory.CreateDirectory(Path.Combine(flare, "Flare.app"));
            File.WriteAllText(Path.Combine(flare, "Flare.app", "Flare.exe"), "");
            return flare;
        }

        private Result Install(string kit, string flare, string local, string extra = "")
        {
            return Run("-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(kit, "Install.ps1") + "\" -Silent -Flare 99 " + extra,
                       psi =>
                       {
                           psi.EnvironmentVariables["FLARE_ROOT"] = flare; psi.EnvironmentVariables["LOCALAPPDATA"] = local; psi.EnvironmentVariables["HOUSESTYLE_INSTALL_TEST"] = "1";
                           psi.EnvironmentVariables["HOUSESTYLE_PLUGINS_KEY"] = @"HKCU:\" + _testKey;
                       });
        }

        private struct Result { public int Code; public string Output; }

        private static Result Run(string args, Action<ProcessStartInfo> env)
        {
            var psi = new ProcessStartInfo(PowerShell51, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (env != null) env(psi);
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
                return new Result { Code = p.ExitCode, Output = output + err.Result };
            }
        }
    }
}
