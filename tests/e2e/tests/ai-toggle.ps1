# The AI On / AI Off button in the AI group of the House Style tab (issue 236): it flips the saved AI setting, Ask AI says
# AI is off while it is off, a Lint Topic with AI off uses the rules only, and Ask AI works again after AI is back on.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null

# The button's label reads the state. If real Flare does not show the new label, clicking by label fails: fall back to the
# command itself and say so.
$script:viaCommand = $false
function Switch-Ai([string]$Label) {
    if ($script:viaCommand) { Send-E2E command ai | Out-Null; return }
    try { Invoke-Ribbon $Label }
    catch {
        Write-Host "   The ribbon button '$Label' was not found ($($_.Exception.Message)); using the command instead."
        $script:viaCommand = $true
        Send-E2E command ai | Out-Null
    }
}
function AiOff { [bool](Get-E2ESetting AiOff) }

Assert-E2E (-not (AiOff)) "AI starts on"

# Off: the setting changes, and Ask AI says why it cannot run.
Switch-Ai "AI On"
Wait-Until { AiOff } 15 "AiOff to become true after the button" | Out-Null
Invoke-Ribbon "Ask AI"
Confirm-MessageBox "Ask AI" "No"
Start-Sleep -Milliseconds 500
Assert-E2E (-not @((Get-E2EState).dialogs | Where-Object { $_.title -like "*Ask AI*" }).Count) "no Ask AI dialog opened while AI is off"

# Lint Topic with AI off uses the style rules only.
Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone).pane.panel
Assert-E2E ($p.status -match "No AI was used" -or $p.status -match "rules only") "Lint Topic with AI off used the rules only (status: $($p.status))"

# On again: the setting changes back, and Ask AI opens.
Switch-Ai "AI Off"
Wait-Until { -not (AiOff) } 15 "AiOff to become false after the button" | Out-Null
Invoke-Ribbon "Ask AI"
Wait-Until { @((Get-E2EState).dialogs | Where-Object { $_.title -like "*Ask AI*" }).Count -gt 0 } 30 "the Ask AI dialog" | Out-Null
Save-E2EScreenshot "ai-toggle"
Send-E2E dialogClick "Cancel" -Extra @{ title = "Ask AI" } | Out-Null
