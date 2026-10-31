# Ignore and exempt, one issue at a time in Lint Topic. Ignore from the keyboard (Delete, then The suggestion is wrong)
# takes row 0 off the list, Ignored Issues (1) lists it, Restore brings it back and Lint Again keeps it (#251). Exempt
# This Text on one issue keeps that text out of later lints with Exemptions (1) in the pane, and Remove Exemption
# brings it back (#29). The finally block restores an ignored issue if the run stops while it is hidden.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Add-Type -AssemblyName System.Windows.Forms
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
function Rows { @((Get-E2EState).pane.panel.rows) }
function Restore-Ignored {
    if (-not (@((Get-E2EState).dialogs | Where-Object { $_.title -like "*Ignored Issues*" })).Count) {
        Send-E2E click "Ignored Issues" -NoWait   # a dialog: the click answers when it closes
        Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Ignored Issues*" } } 15 "the Ignored Issues dialog" | Out-Null
    }
    Send-E2E dialogSelect -Extra @{ title = "Ignored Issues"; index = 0 } | Out-Null
    Send-E2E dialogClick "Restore" -Extra @{ title = "Ignored Issues" } | Out-Null
    Send-E2E dialogClick "Close" -Extra @{ title = "Ignored Issues" } | Out-Null
    Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Ignored Issues*" }) } 10 "Ignored Issues to close" | Out-Null
}

Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
try {
    # 1. Lint the topic, then select row 0 only (no item starts with that, so it only selects the row).
    Invoke-Ribbon "Lint Topic"
    $rows = @((Wait-PaneDone 300).pane.panel.rows)
    Assert-E2E ($rows.Count -gt 0) "Lint Topic lists issues to ignore"
    $row = $rows[0]
    Write-Host "   row 0: $($row.text)"
    # The list words each issue its own way (Rewrite shortens the excerpt), so match on the start of the excerpt.
    $key = [string]$row.excerpt
    $key = $key.Substring(0, [Math]::Min(24, $key.Length))
    Send-E2E rowMenu "(select only)" -Extra @{ index = 0 } | Out-Null

    # 2. Focus the list, open Ignore's reasons with Delete, and pick one by name.
    Assert-E2E ((Send-E2E paneFocus).value.focused) "paneFocus put the focus in the issues list"
    Keys "{DELETE}"   # opens Ignore's reasons
    Send-E2E paneMenu "The suggestion is wrong" | Out-Null
    $want = $rows.Count - 1
    Wait-Until { @(Rows).Count -eq $want } 15 "the ignored issue to leave the list (was $($rows.Count) rows; status: $((Get-E2EState).pane.panel.status))" | Out-Null
    $s = (Get-E2EState).pane
    Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Ignored Issues (1)" }).Count -eq 1) "Ignored Issues (1) appears in the pane (buttons: $($s.buttons.text -join ' | '))"

    # 3. Open Ignored Issues: one row, and it is the ignored issue.
    Send-E2E click "Ignored Issues" -NoWait   # a dialog: the click answers when it closes
    $d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Ignored Issues*" } } 15 "the Ignored Issues dialog"
    Assert-E2E (@($d.rows).Count -eq 1) "the Ignored Issues dialog lists one issue (got $(@($d.rows).Count))"
    Assert-E2E (@($d.rows)[0].text.Contains($key)) "the listed issue is the ignored one (listed: $(@($d.rows)[0].text))"

    # 4. Select it, Restore, and the dialog shows the empty text with no rows. Close it.
    Send-E2E dialogSelect -Extra @{ title = "Ignored Issues"; text = $key } | Out-Null
    Send-E2E dialogClick "Restore" -Extra @{ title = "Ignored Issues" } | Out-Null
    $d = Wait-Until { $x = (Get-E2EState).dialogs | Where-Object { $_.title -like "*Ignored Issues*" }; if ($x -and @($x.rows).Count -eq 0) { $x } } 15 "the Ignored Issues dialog to empty"
    Assert-E2E (@($d.text | Where-Object { $_ -like "*You haven't ignored any issues*" }).Count -eq 1) "the dialog says no issues are ignored after Restore"
    Send-E2E dialogClick "Close" -Extra @{ title = "Ignored Issues" } | Out-Null
    Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Ignored Issues*" }) } 10 "Ignored Issues to close" | Out-Null

    # 5. The issue is back in the list, and the Ignored Issues button is gone.
    $back = Wait-Until { $s = (Get-E2EState).pane; if (@($s.panel.rows | Where-Object { $_.excerpt -eq $row.excerpt }).Count -eq 1) { $s } } 15 "the restored issue back in the list"
    Assert-E2E (@($back.buttons | Where-Object { $_.text -like "Ignored Issues*" }).Count -eq 0) "the Ignored Issues button is gone after Restore (buttons: $($back.buttons.text -join ' | '))"

    # 6. Lint Again: the restored issue is still in the list.
    Send-E2E click "Lint Again" | Out-Null
    Wait-PaneDone 300 | Out-Null
    Assert-E2E (@(Rows | Where-Object { $_.excerpt -eq $row.excerpt }).Count -eq 1) "the restored issue is still in the list after Lint Again"
}
finally {
    # 8. A run that stopped while the issue was still ignored restores it, so row 0 doesn't stay hidden.
    try {
        if (@((Get-E2EState).pane.buttons | Where-Object { $_.text -like "Ignored Issues (*" }).Count) { Restore-Ignored }
    } catch { Write-Host "   (couldn't restore the ignored issue: $($_.Exception.Message))" }
}

# Exempt: the pane is on the Lint Again results, so those rows are the "before".
$before = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($before.Count -gt 0) "the pane lists issues to exempt"
# Exempt This Text on the first issue, with a reason. The item opens a dialog, so the verb answers only when it closes.
Send-E2E rowMenu "Exempt This Text" -Extra @{ index = 0 } -NoWait
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" } } 15 "the Exempt This Text dialog" | Out-Null
Send-E2E dialogType "The product name is always written this way." -Extra @{ title = "Exempt This Text"; name = "Why is this text exempt?" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Exempt This Text" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" }) } 15 "Exempt This Text to close" | Out-Null
$after = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($after.Count -eq $before.Count - 1) "the exempt text left the rows ($($before.Count) before, $($after.Count) after)"

# Re-lint: the exempt text is still gone, and the pane counts one exemption.
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$s = (Get-E2EState).pane
Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Exemptions (1)" }).Count -eq 1) "Exemptions (1) appears after re-lint (buttons: $($s.buttons.text -join ' | '))"
Assert-E2E (@($s.panel.rows).Count -eq $after.Count) "the exempt text is still gone after re-lint ($($after.Count) rows, got $(@($s.panel.rows).Count))"

# Remove Exemption: the text is flagged again on the next lint.
Send-E2E click "Exemptions" -NoWait   # a dialog: the click answers when it closes
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" } } 10 "the Exemptions dialog"
Assert-E2E (@($d.items).Count -ge 1 -or @($d.rows).Count -ge 1) "the Exemptions dialog lists the exempt text"
Send-E2E dialogSelect -Extra @{ title = "Exemptions"; index = 0 } | Out-Null
Send-E2E dialogClick "Remove Exemption" -Extra @{ title = "Exemptions" } | Out-Null
Send-E2E dialogClick "Close" -Extra @{ title = "Exemptions" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" }) } 10 "Exemptions to close" | Out-Null

Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$back = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($back.Count -eq $before.Count) "removing the exemption brings the issue back ($($before.Count) rows before, $($back.Count) now)"

