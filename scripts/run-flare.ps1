# Launches the installed Flare. The plugin takes its AI key only from Settings (House Style > Settings),
# so enter it there once. Test runs (tests\e2e) store their own key in their own isolated settings.
. "$PSScriptRoot\Find-Flare.ps1"
$flare = Get-FlareInstall
Write-Host "Launching $($flare.Exe)..."
& $flare.Exe
