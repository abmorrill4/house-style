# Setup with the team's guide asks for the team feedback folder, the last step says where requests go, Settings shows
# the folder on the Style guide tab with the requests sentence, and setup run again doesn't ask a second time (#91).
param($Fixture)
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
# A team guide: the repo's guide without Google's layer (a folder holding Google's guide is refused as a team guide).
$guide = Join-Path (Get-E2ERoot) "team-guide-$stamp"
Copy-Item (Join-Path $repo "styleguide") $guide -Recurse
Remove-Item (Join-Path $guide "Content\Resources\StyleGuide\Google") -Recurse -Force
$feedback = Join-Path (Get-E2ERoot) "team-feedback-$stamp"
New-Item -ItemType Directory -Force $feedback | Out-Null

$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ StyleLayers = ""; FeedbackFolder = "" }
function Dialog([string]$Title) { (Get-E2EState).dialogs | Where-Object { $_.title -like "*$Title" } }
function Has([object]$d, [string]$Text) { @($d.text | Where-Object { $_ -like "*$Text*" }).Count -gt 0 }
function Box([object]$d, [string]$Name) { @($d.boxes | Where-Object { $_.name -eq $Name }) | Select-Object -First 1 }

function Start-Setup {
    Send-E2E command settings -NoWait   # a dialog: the command answers when it closes
    Wait-Until { Dialog "Settings" } 30 "the Settings dialog" | Out-Null
    Send-E2E dialogClick "Run setup again" -Extra @{ title = "Settings" } -NoWait
    Wait-Until { Dialog "Setup" } 30 "the Setup dialog" | Out-Null
    Send-E2E dialogClick "Next" -Extra @{ title = "Setup" } | Out-Null             # role
    Send-E2E dialogClick "Skip this step" -Extra @{ title = "Setup" } | Out-Null   # AI
}

# ---- first setup: team guide plus a feedback folder ----------------------------------------------------------------
Start-Setup
Send-E2E dialogClick "My team's shared style guide folder" -Extra @{ title = "Setup" } | Out-Null
$d = Dialog "Setup"
Assert-E2E ($null -ne (Box $d "Team feedback folder")) "the guide step asks for the team feedback folder (boxes: $(@($d.boxes.name) -join ' | '))"
Send-E2E dialogType $guide -Extra @{ title = "Setup"; name = "Team style guide folder" } | Out-Null
Send-E2E dialogType $feedback -Extra @{ title = "Setup"; name = "Team feedback folder" } | Out-Null
Send-E2E dialogClick "Next" -Extra @{ title = "Setup" } | Out-Null   # starts the guide folder check
Wait-Until { Has (Dialog "Setup") "StyleGuide (" } 30 "the guide folder check" | Out-Null
Send-E2E dialogClick "Next" -Extra @{ title = "Setup" } | Out-Null
$summary = Wait-Until { $x = Dialog "Setup"; if (Has $x "Requests: sent to your style guide owners through") { $x } } 15 "the last step's requests line"
Assert-E2E (Has $summary $feedback) "the last step names the feedback folder (text: $(@($summary.text) -join ' | '))"
Send-E2E dialogClick "Finish" -Extra @{ title = "Setup" } | Out-Null
Wait-Until { -not (Dialog "Setup") } 15 "Setup to close" | Out-Null
Assert-E2E ((Get-E2ESetting FeedbackFolder) -eq $feedback) "setup saved the feedback folder (got: $(Get-E2ESetting FeedbackFolder))"
Assert-E2E ((Get-E2ESetting StyleLayers) -eq $guide) "setup saved the team guide (got: $(Get-E2ESetting StyleLayers))"

# ---- Settings: the folder is on the Style guide tab and says where requests go --------------------------------------
Send-E2E command settings -NoWait
Wait-Until { Dialog "Settings" } 30 "the Settings dialog" | Out-Null
Send-E2E dialogTab "Style guide" -Extra @{ title = "Settings" } | Out-Null
$d = Wait-Until { $x = Dialog "Settings"; if (Has $x "Your requests to your style guide owners go here too.") { $x } } 30 "the feedback folder status with the requests sentence"
Assert-E2E (@($d.items) -contains $guide) "Style guide lists the team guide (items: $(@($d.items) -join ' | '))"
$box = Box $d "Team feedback folder"
Assert-E2E ($box -and $box.text -eq $feedback) "the Style guide tab shows the feedback folder (got: $($box.text))"
Send-E2E dialogClick "Cancel" -Extra @{ title = "Settings" } | Out-Null
Wait-Until { -not (Dialog "Settings") } 15 "Settings to close" | Out-Null

# ---- setup run again: a folder is set, so it doesn't ask ------------------------------------------------------------
Start-Setup
$d = Dialog "Setup"
Assert-E2E (@($d.buttons | Where-Object { $_.text -eq "My team's shared style guide folder" -and $_.checked }).Count -eq 1) "setup starts from the team guide"
Assert-E2E ($null -eq (Box $d "Team feedback folder")) "setup doesn't ask for a feedback folder that is already set (boxes: $(@($d.boxes.name) -join ' | '))"
Send-E2E dialogClick "Skip this step" -Extra @{ title = "Setup" } | Out-Null   # guide
Send-E2E dialogClick "Finish" -Extra @{ title = "Setup" } | Out-Null
Wait-Until { -not (Dialog "Setup") } 15 "Setup to close" | Out-Null
Assert-E2E ((Get-E2ESetting FeedbackFolder) -eq $feedback) "setup run again kept the feedback folder"

Remove-Item $guide, $feedback -Recurse -Force -ErrorAction SilentlyContinue
