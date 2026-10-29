# Ignore's "The suggestion is wrong" takes row 0 off the list, Ignored Issues (1) lists it, Restore in that dialog puts it back
# (the button goes), the dialog's empty text shows once nothing is ignored, and Lint Again keeps the restored issue (#251).
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$run = Start-FlareE2E -Fixture $Fixture -Force
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
function Rows { @((Get-E2EState).pane.panel.rows) }
function Dialog([string]$Title) { (Get-E2EState).dialogs | Where-Object { $_.title -like "*$Title*" } }
function Has([object]$d, [string]$Text) { @($d.text | Where-Object { $_ -like "*$Text*" }).Count -gt 0 }

Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone).pane.panel.rows)
Assert-E2E ($rows.Count -gt 0) "Lint Topic lists issues in faq.htm (rows: $(($rows.text) -join ' | '))"
$row = $rows[0]
Write-Host "   row 0: $($row.text)"

$hidden = $false
try {
    # ---- 1-2: ignore row 0 by its reasons menu --------------------------------------------------------------------------
    Send-E2E rowMenu "(select only)" -Extra @{ index = 0 } | Out-Null   # no item starts with that, so it only selects the row
    Assert-E2E ((Send-E2E paneFocus).value.focused) "paneFocus put the focus in the issues list"
    Keys "{DELETE}"   # opens Ignore's reasons
    Send-E2E paneMenu "The suggestion is wrong" | Out-Null
    $hidden = $true   # from here a failed run must not leave row 0 hidden (the finally block restores it)
    Wait-Until { @(Rows).Count -eq $rows.Count - 1 } 15 "the ignored issue to leave the list" | Out-Null
    Assert-E2E (@(Rows | Where-Object { $_.text -eq $row.text }).Count -eq 0) "the ignored issue is gone from the list"
    $s = (Get-E2EState).pane
    Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Ignored Issues (1)" }).Count -eq 1) "the pane has Ignored Issues (1) (buttons: $($s.buttons.text -join ' | '))"

    # ---- 3: the Ignored Issues dialog lists it ----------------------------------------------------------------------------
    Send-E2E click "Ignored Issues" -NoWait | Out-Null   # a dialog: the click answers when it closes
    $d = Wait-Until { Dialog "Ignored Issues" } 10 "the Ignored Issues dialog"
    Assert-E2E (@($d.rows).Count -eq 1 -and @($d.rows | Where-Object { $_.text -eq $row.text }).Count -eq 1) "the dialog lists the ignored issue (rows: $(@($d.rows.text) -join ' | '))"

    # ---- 4: Restore puts it back; the dialog then shows the empty text ----------------------------------------------------
    Send-E2E dialogSelect -Extra @{ title = "Ignored Issues"; text = $row.text } | Out-Null
    Send-E2E dialogClick "Restore" -Extra @{ title = "Ignored Issues" } | Out-Null
    $hidden = $false
    $d = Dialog "Ignored Issues"
    Assert-E2E (@($d.rows).Count -eq 0) "Restore took the issue off the dialog's list (rows: $(@($d.rows.text) -join ' | '))"
    Assert-E2E (Has $d "You haven't ignored any issues that are still in the text.") "the dialog shows the empty text (text: $(@($d.text) -join ' | '))"
    Send-E2E dialogClick "Close" -Extra @{ title = "Ignored Issues" } | Out-Null
    Wait-Until { -not (Dialog "Ignored Issues") } 10 "the Ignored Issues dialog to close" | Out-Null

    # ---- 5: the issue is back in the list and the button is gone ----------------------------------------------------------
    Wait-Until { @(Rows | Where-Object { $_.text -eq $row.text }).Count -eq 1 } 15 "the restored issue back in the list" | Out-Null
    Wait-Until { @((Get-E2EState).pane.buttons | Where-Object { $_.text -like "Ignored Issues*" }).Count -eq 0 } 10 "the Ignored Issues button to go" | Out-Null

    # ---- 6: Lint Again keeps it ---------------------------------------------------------------------------------------------
    Send-E2E click "Lint Again" | Out-Null
    $after = @((Wait-PaneDone).pane.panel.rows)
    Assert-E2E (@($after | Where-Object { $_.text -eq $row.text }).Count -eq 1) "Lint Again keeps the restored issue in the list"

    # ---- 7 ---------------------------------------------------------------------------------------------------------------
    Save-E2EScreenshot "ignored-restore"
} finally {
    # ---- 8: a run that stopped after the ignore leaves nothing hidden --------------------------------------------------------
    if ($hidden) {
        try {
            if (-not (Dialog "Ignored Issues")) {
                Send-E2E click "Ignored Issues" -NoWait | Out-Null
                Wait-Until { Dialog "Ignored Issues" } 10 "the Ignored Issues dialog" | Out-Null
            }
            Send-E2E dialogSelect -Extra @{ title = "Ignored Issues"; text = $row.text } | Out-Null
            Send-E2E dialogClick "Restore" -Extra @{ title = "Ignored Issues" } | Out-Null
            Send-E2E dialogClick "Close" -Extra @{ title = "Ignored Issues" } | Out-Null
        } catch { Write-Host "   (couldn't restore row 0 after a failed run: $($_.Exception.Message))" }
    }
}
