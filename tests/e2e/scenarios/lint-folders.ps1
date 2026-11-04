# Lint Folders on Lint Project's menu (#227, reports #14 and #29): the picker lists the project's folders, picking billpay
# lints only that folder, and the pane's title names it.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null

$item = "Lint Folders" + [char]0x2026   # the menu item ends with an ellipsis; this file is read as ANSI
Invoke-RibbonMenu -Button "Lint Project" -Item $item
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Lint Folders*" } } 30 "the Lint Folders dialog"
Assert-E2E (@($d.rows).Count -gt 5) "the dialog lists the project's folders (rows: $(@($d.rows).Count))"
Assert-E2E (@($d.rows | Where-Object { $_.text -eq "billpay" }).Count -eq 1) "billpay is one of the folders"
Assert-E2E (@($d.rows | Where-Object { $_.text -like "Resources\StyleGuide*" }).Count -eq 0) "the style guide folder is not offered"

Send-E2E dialogSelect -Extra @{ title = "Lint Folders"; text = "billpay" } | Out-Null
Send-E2E dialogClick "Lint" -Extra @{ title = "Lint Folders" } | Out-Null
$p = (Wait-PaneDone 300).pane.panel
Assert-E2E ($p.title -like "*billpay*") "the pane's title names the folder (title: $($p.title))"

Send-E2E click "By file" | Out-Null
$rows = @((Get-E2EState).pane.panel.rows | Where-Object { $_.file })
Assert-E2E ($rows.Count -gt 0) "billpay has issues"
$outside = @($rows | Where-Object { $_.file -notlike "*\Content\billpay\*" })
Assert-E2E ($outside.Count -eq 0) "every issue is in billpay (outside: $(($outside.file | Select-Object -Unique) -join ' | '))"
Save-E2EScreenshot "lint-folders"
