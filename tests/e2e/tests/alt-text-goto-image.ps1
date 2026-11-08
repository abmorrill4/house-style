# A missing alt text issue names the image by its file name, and Go To selects the image itself (#451). The images are in the middle of
# text, alone in a paragraph, beside spaces, beside text with a tracked deletion in it, and first and last in the topic. Flare reports no
# image for a selection that has nothing before it or nothing after it, so for those two Go To selects the text beside the image.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Send-E2E insertRaw '<p><img src="../Images/first.png" /> First words follow the picture.</p>' -Extra @{ first = 1 } | Out-Null
Send-E2E insertRaw '<p>Spaced words. <img src="../Images/spaced.png" />      Spaced words after.</p>' -Extra @{ after = "How long does a payee take?" } | Out-Null
Send-E2E insertRaw '<p><img src="../Images/alone.png" /></p>' -Extra @{ after = "How long does a payee take?" } | Out-Null
Send-E2E insertRaw '<p>Before the picture. <img src="../Images/mid.png" /> After the picture.</p>' -Extra @{ after = "How long does a payee take?" } | Out-Null
Send-E2E insertRaw '<p>Closing words <img src="../Images/last.png" /></p>' -Extra @{ after = "Contact your bank if a payee is missing." } | Out-Null
# A tracked deletion in the text after the alone image.
Send-E2E replace "Most changes" -Extra @{ text = "Many changes" } | Out-Null

Invoke-Ribbon "Lint Topic"
$pane = (Wait-PaneDone).pane.panel
$rows = @($pane.rows)
$alt = @(0..($rows.Count - 1) | Where-Object { $rows[$_].rule -eq "Structure.AltText" })
Assert-E2E ($alt.Count -eq 5) "Lint Topic lists five Structure.AltText rows (got $($alt.Count))"
foreach ($i in $alt) {
    $text = ($rows[$i].text -replace '[^\x20-\x7e]', '').Trim()
    Assert-E2E ($text -like 'Alt text for "*.png"*') "the row names the image by its file name (text: $text)"
    $name = [regex]::Match($text, '"([^"]+)"').Groups[1].Value
    # Put the caret in other text first, so a change in what Flare reports can only come from Go To.
    Send-E2E select "Only the account owner" | Out-Null
    Assert-E2E (@((Get-E2EState).editor.selectionImages).Count -eq 0) "before Go To to $name the selection holds no image"
    Send-E2E rowMenu "Go To" -Extra @{ index = $i } | Out-Null
    if ($name -eq "first.png" -or $name -eq "last.png") {
        $beside = if ($name -eq "first.png") { "First words follow" } else { "Closing words" }
        $e = Wait-Until { $s = (Get-E2EState).editor; if ($s.selection -like "*$beside*") { $s } } 15 "the text beside $name to be selected"
        Assert-E2E ($e.selection -like "*$beside*") "Go To to $name selects the text beside it (selection: '$($e.selection)')"
    } else {
        $e = Wait-Until { $s = (Get-E2EState).editor; if (@($s.selectionImages) -contains "../Images/$name") { $s } } 15 "$name to be selected"
        Assert-E2E (@($e.selectionImages) -contains "../Images/$name") "Go To selects $name (selected images: $(@($e.selectionImages) -join ', '))"
    }
    Save-E2EScreenshot "alt-text-goto-$name"
}

# A topic that is only an image has no text to anchor an issue on, and has none, as before.
$only = Join-Path $run.Project "Content\structure\image-only.htm"
New-Item -ItemType Directory -Force (Split-Path $only) | Out-Null
Set-Content $only -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd">
    <head><title>Image only</title></head>
    <body><p><img src="../Images/only.png" /></p></body>
</html>
"@
Send-E2E open $only | Out-Null
Invoke-Ribbon "Lint Topic"
$pane = (Wait-PaneDone).pane.panel
Assert-E2E (@(@($pane.rows) | Where-Object { $_.rule -eq "Structure.AltText" }).Count -eq 0) "a topic with only an image has no alt text row, as before"
Assert-E2E (@((Get-E2EState).dialogs).Count -eq 0) "no error dialog for a topic with only an image"
