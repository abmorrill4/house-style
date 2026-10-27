# Lint Project, Export Issues to a file: one row per issue, a link to each topic, and saving again replaces it (#238).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
$b = (Get-E2EState).pane.buttons | Where-Object { $_.text -eq "Export Issues" }
Assert-E2E ($b -and $b.enabled) "Export Issues is in the pane and enabled"
$csv = Join-Path $run.Hook "issues.csv"
Send-E2E saveAs $csv | Out-Null
Send-E2E click "Export Issues" | Out-Null
$status = (Get-E2EState).pane.panel.status
Assert-E2E ($status -match '^Exported ([\d,.\s\u00A0]+) issues? to ') "the status names the count (got: $status)"
$n = [int]($Matches[1] -replace '\D', '')
$rows = @(Import-Csv $csv -Encoding UTF8)
Assert-E2E ($rows.Count -eq $n -and $n -gt 0) "the file has one row per issue ($($rows.Count) rows, status says $n)"
Assert-E2E (@($rows | Where-Object { $_.Topic -eq 'billpay\faq.htm' -and $_.Link -like '*/Content/billpay/faq.htm*' }).Count -gt 0) "a row links to billpay\faq.htm"
Assert-E2E (@($rows | Where-Object { $_.Level -notin 'Error','Warning','Suggestion' -or $_.Status -notin 'Open','Changed','Text changed','Skipped' }).Count -eq 0) "Level and Status use the deck's values"
Set-Content $csv "old"; Send-E2E saveAs $csv | Out-Null; Send-E2E click "Export Issues" | Out-Null
Assert-E2E ((Get-Content $csv -TotalCount 1 -Encoding UTF8) -like 'Topic,Level,*') "saving again replaces the file"
Assert-E2E (@(Get-ChildItem $run.Hook -Filter "issues.csv.*.tmp").Count -eq 0) "no temp file is left"