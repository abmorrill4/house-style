# A missing alt text issue names the image by its file name, and Go To selects the image itself (#451).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Send-E2E insertRaw '<p>Before the picture. <img src="../Images/none.png" /> After the picture.</p>' -Extra @{ after = "How long does a payee take?" } | Out-Null

Invoke-Ribbon "Lint Topic"
$pane = (Wait-PaneDone).pane.panel
$i = (0..($pane.rows.Count - 1) | Where-Object { $pane.rows[$_].rule -eq "Structure.AltText" })[0]
Assert-E2E ($null -ne $i) "Lint Topic lists a Structure.AltText row"
$text = ($pane.rows[$i].text -replace '[^\x20-\x7e]', '').Trim()
Assert-E2E ($text -like '*Alt text for "none.png"*') "the row names the image by its file name (text: $text)"

# Put the caret in other text first, so a change in what Flare reports can only come from Go To.
Send-E2E select "Most changes to a payee" | Out-Null
Assert-E2E (@((Get-E2EState).editor.selectionNodes) -notcontains "img") "before Go To the selection holds no image"
Send-E2E rowMenu "Go To" -Extra @{ index = $i } | Out-Null
# Flare reports the selected image among the selected nodes (with a character each side, as an image takes no room in its positions).
$e = Wait-Until { $s = (Get-E2EState).editor; if (@($s.selectionNodes) -contains "img") { $s } } 15 "the image to be selected"
Assert-E2E (@($e.selectionNodes) -contains "img") "Go To selects the image (selected nodes: $(@($e.selectionNodes) -join ', '))"
Save-E2EScreenshot "alt-text-goto-image"
