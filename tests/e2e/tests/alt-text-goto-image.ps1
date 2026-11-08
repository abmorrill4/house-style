# A missing alt text issue names the image by its file name, and Go To selects the image itself (#451).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$topic = Join-Path $run.Project "Content\structure\alt-text.htm"
New-Item -ItemType Directory -Force (Split-Path $topic) | Out-Null
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd">
    <head><title>Alt text</title></head>
    <body>
        <h1>Alt text</h1>
        <p>Before the picture. <img src="../Images/none.png" /> After the picture.</p>
    </body>
</html>
"@

Send-E2E open $topic | Out-Null
Invoke-Ribbon "Lint Topic"
$pane = (Wait-PaneDone).pane.panel
$i = (0..($pane.rows.Count - 1) | Where-Object { $pane.rows[$_].rule -eq "Structure.AltText" })[0]
Assert-E2E ($null -ne $i) "Lint Topic lists a Structure.AltText row"
$text = ($pane.rows[$i].text -replace '[^\x20-\x7e]', '').Trim()
Assert-E2E ($text -like '*Alt text for "none.png"*') "the row names the image by its file name (text: $text)"

# Put the caret in other text first, so a change in what Flare reports can only come from Go To.
Send-E2E select "After the picture." | Out-Null
Assert-E2E (@((Get-E2EState).editor.selectionNodes) -notcontains "img") "before Go To the selection holds no image"
Send-E2E rowMenu "Go To" -Extra @{ index = $i } | Out-Null
# Flare reports the selected image among the selected nodes (with a character each side, as an image takes no room in its positions).
$e = Wait-Until { $s = (Get-E2EState).editor; if (@($s.selectionNodes) -contains "img") { $s } } 15 "the image to be selected"
Assert-E2E (@($e.selectionNodes) -contains "img") "Go To selects the image (selected nodes: $(@($e.selectionNodes) -join ', '))"
Save-E2EScreenshot "alt-text-goto-image"
