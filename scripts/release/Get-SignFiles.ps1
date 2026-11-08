# The PE files a release signs: every FlareAI.*.dll and FlareAI.*.exe directly in the plugin folder. This is the same
# pattern the updater checks (UpdateStager.PeFiles), so what is signed is what is verified. Prints the full paths.
param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = "Stop"
# Extension compared exactly: a -Filter on *.dll would also match names like FlareAI.Flare.dll.config.
Get-ChildItem -LiteralPath $Dir -File -Filter "FlareAI.*" |
    Where-Object { $_.Extension -in ".dll", ".exe" } |
    Sort-Object Name |
    ForEach-Object { $_.FullName }
