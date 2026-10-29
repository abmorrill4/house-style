# Disable Rule on one of two twin rules (#127): the pane offers the glossary twin as "Also disable Glossary terms", and
# turning off the terminology rule for this project leaves the glossary row in the other paragraph showing. The project's
# overrides.ini then has the terminology rule and no glossary row.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$topic = Join-Path $run.Project "Content\twin-rules.htm"
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html><body><h1>Single Sign-On</h1>
<p>Enable Single Sign-On first.</p>
<p>Single Sign-On is set up by your administrator.</p>
</body></html>
"@
Send-E2E open $topic | Out-Null

Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone).pane.panel
$rows = @($p.rows)
$i = [Array]::IndexOf(@($rows.excerpt), "Single Sign-On")
Assert-E2E ($i -ge 0) "Lint Topic flagged Single Sign-On (rows: $(($rows.text) -join ' | '))"
Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null   # no menu item starts with this: selects the row only

Send-E2E click "Disable Rule" -NoWait   # a dialog: the click answers when it closes
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" } } 15 "the Disable Rule dialog"
Assert-E2E (@($d.buttons | Where-Object { $_.text -like "*Also disable Glossary terms*" }).Count -ge 1) "Disable Rule offers Also disable Glossary terms (buttons: $(($d.buttons | ForEach-Object { $_.text }) -join ' | '))"
Assert-E2E (($d | ConvertTo-Json -Depth 10 -Compress) -like "*This disables every glossary spelling in this guide, not just this one.*") "the dialog says what Also disable Glossary terms does"
Send-E2E dialogClick "For this project only" -Extra @{ title = "Disable Rule" } | Out-Null
Send-E2E dialogType "Single Sign-On is our product name." -Extra @{ title = "Disable Rule"; name = "Reason" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Disable Rule" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" }) } 15 "Disable Rule to close" | Out-Null

# Lint again: the terminology rule is off for this project, the glossary row in the other paragraph still shows.
Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone).pane.panel
$rows = @($p.rows)
Assert-E2E (@($rows | Where-Object { $_.tip -eq "company:terminology/6" -and $_.excerpt -eq "Single Sign-On" }).Count -eq 0) "no terminology row on Single Sign-On after Disable Rule (rows: $(($rows.text) -join ' | '))"
Assert-E2E (@($rows | Where-Object { $_.excerpt -eq "Single Sign-On" -and $_.tip -eq "company:terminology/7" }).Count -ge 1) "the glossary row in the other paragraph still shows"

# The project's own exception: a terminology/6 line and no glossary row in overrides.ini.
$ini = Get-Content (Join-Path $run.Project "Content\Resources\StyleGuide\overrides.ini") -Raw
Assert-E2E ($ini -like "*company:terminology/6*") "overrides.ini turns company:terminology/6 off"
Assert-E2E ($ini -notlike "*Company.GlossaryTerms*") "overrides.ini has no Company.GlossaryTerms row"
