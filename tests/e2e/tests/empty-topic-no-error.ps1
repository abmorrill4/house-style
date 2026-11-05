# Saving an empty topic with Check on save on shows no error (#314).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ CheckOnSave = $true }
$file = Join-Path $run.Project "Content\empty-314.htm"
Set-Content $file -Encoding UTF8 '<?xml version="1.0" encoding="utf-8"?><html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd"><head /><body></body></html>'

Send-E2E open $file | Out-Null
Send-E2E save | Out-Null
Start-Sleep -Seconds 4
Assert-E2E (@((Get-E2EState).dialogs).Count -eq 0) "saving an empty topic showed no error dialog"

Invoke-Ribbon "Lint Topic"
Start-Sleep -Seconds 3
Save-E2EScreenshot "empty-topic-no-error"
