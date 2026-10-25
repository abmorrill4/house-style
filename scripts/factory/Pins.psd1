# The tool versions the factory runs, read as data. Setup installs exactly these on the agents VM
# (Install-FactoryTools.ps1 -NoFlare), and every fixture in tests/factory/fixtures records the version it came from,
# which must match its pin here (Fixtures.Tests.ps1). Changing a pin means capturing that tool's fixtures again with
# Save-Fixtures.ps1 (factory-runtime spec section 12). An empty pin is set when its first fixture is captured, at the
# migration step named beside it.
@{
    'git'          = '2.53.0.windows.1'
    'dotnet'       = '8.0.426'
    'claude'       = '2.1.270'
    'github-api'   = '2022-11-28'
    'windows'      = ''   # the agents VM's Windows build, major.minor.build.revision (migration step 2)
    'host-windows' = ''   # THESEUS's Windows build, for Hyper-V and PowerShell Direct (migration step 3)
    'flare'        = ''   # Flare on the Flare VM, from its Flare.exe file version (migration step 3)
    'runner'       = ''   # the GitHub Actions runner (migration step 3)
}
