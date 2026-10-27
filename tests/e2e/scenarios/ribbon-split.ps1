# Ribbon split buttons: Invoke-RibbonMenu opens Help's menu and picks What's New, and plain buttons still work through
# Invoke-Ribbon. Get-E2ESetting reads one setting and refuses the API key (#257, for #162 and #253).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
function WhatsNew { (Get-E2EState).dialogs | Where-Object { $_.title -like "*What's new*" } }

Invoke-RibbonMenu -Button "Help" -Item "What's New"
Wait-Until { WhatsNew } 30 "What's New to open from Help's menu" | Out-Null
Save-E2EScreenshot "ribbon-split-whatsnew"
Send-E2E dialogClick "Close" -Extra @{ title = "What's new" } | Out-Null
Wait-Until { -not (WhatsNew) } 15 "What's New to close" | Out-Null

Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
$s = Wait-PaneDone
Assert-E2E ($s.pane.panel.rows.Count -gt 0) "Lint Topic still works through Invoke-Ribbon"

$live = Get-E2ESetting LiveCheck
Write-Host "   LiveCheck: $live"
Assert-E2E ($live -eq $false) "Get-E2ESetting reads the run's LiveCheck (false for e2e runs)"
try { Get-E2ESetting ProtectedApiKey | Out-Null; $err = "" } catch { $err = $_.Exception.Message }
Assert-E2E ($err -like "*won't read ProtectedApiKey*") "Get-E2ESetting refuses ProtectedApiKey (got: $err)"
