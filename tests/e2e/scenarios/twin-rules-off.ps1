# Two rules flag the same words (#127): Disable Rule on the Single Sign-On row offers the rules that flag it, and the
# glossary checker as "Also disable Glossary terms", unticked with its warning. Turning off company:terminology/6 for
# this project leaves the glossary row in the other paragraph, and the project's overrides.ini has the rule, not the checker.
param($Fixture)
# The twin rules (company:terminology/6 and the glossary) are in the bundled starter guide, so the default guide serves.
$run = Start-FlareE2E -Fixture $Fixture -Force
$topic = Join-Path $run.Project "Content\billpay\twin-rules.htm"
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd">
    <head><title>Sign-in</title></head>
    <body>
        <h1>Sign-in</h1>
        <p>Enable Single Sign-On first.</p>
        <p>The Identity Provider must be set up before any member can sign in.</p>
    </body>
</html>
"@
Send-E2E open $topic | Out-Null
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone 300).pane.panel.rows)
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.excerpt -like "*Single Sign-On*" })
Assert-E2E ($i -ge 0) "Lint Topic flagged Single Sign-On (rows: $(($rows.text) -join ' | '))"
Assert-E2E (@($rows | Where-Object { $_.excerpt -like "*Identity Provider*" }).Count -ge 1) "Lint Topic flagged Identity Provider in the other paragraph (rows: $(($rows.text) -join ' | '))"

# Select the Single Sign-On row, then Disable Rule. The item opens a dialog, so the click answers when it closes.
Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null
Send-E2E click "Disable Rule" -NoWait   # a dialog: the click answers when it closes
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" } } 15 "the Disable Rule dialog"
$text = @($d.text) -join " "
Assert-E2E (@($d.buttons | Where-Object { $_.text -eq "Also disable Glossary terms" }).Count -eq 1) "Disable Rule offers Also disable Glossary terms (buttons: $(($d.buttons.text) -join ' | '))"
Assert-E2E ($text -like "*This disables every glossary spelling in this guide, not just this one.*") "the dialog warns that it disables every glossary spelling (text: $text)"

# For this project only, with a reason. The checker box stays unticked.
Send-E2E dialogClick "For this project only" -Extra @{ title = "Disable Rule" } | Out-Null
Send-E2E dialogType "The Single Sign-On step is covered in the admin guide." -Extra @{ title = "Disable Rule"; name = "Reason" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Disable Rule" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" }) } 15 "Disable Rule to close" | Out-Null

# Fresh lint: the glossary still flags Single Sign-On (its box stayed unticked), but rule 6 no longer does, and the Identity Provider paragraph keeps its row.
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone 300).pane.panel.rows)
$sso = @($rows | Where-Object { $_.excerpt -like "*Single Sign-On*" })
Assert-E2E ($sso.Count -eq 1 -and $sso[0].rule -eq "Company.GlossaryTerms") "only the glossary still flags Single Sign-On after Disable Rule (rows: $(($rows | ForEach-Object { $_.rule + ': ' + $_.text }) -join ' | '))"
Assert-E2E (@($rows | Where-Object { $_.excerpt -like "*Identity Provider*" }).Count -ge 1) "Lint Topic after Disable Rule still flags Identity Provider (rows: $(($rows | ForEach-Object { $_.rule + ': ' + $_.text }) -join ' | '))"

# The project's override file: the rule is off, and the glossary checker isn't in it, because its box was unticked.
Assert-E2E (Test-Path (Join-Path $run.Project "Content\Resources\StyleGuide\overrides.ini")) "the project's overrides.ini was written"
Assert-E2E ((Get-Content (Join-Path $run.Project "Content\Resources\StyleGuide\overrides.ini") -Raw) -match "company:terminology/6") "overrides.ini has a company:terminology/6 row"
Assert-E2E ((Get-Content (Join-Path $run.Project "Content\Resources\StyleGuide\overrides.ini") -Raw) -notmatch "Company\.GlossaryTerms") "overrides.ini has no Company.GlossaryTerms row (the checker box was unticked)"
