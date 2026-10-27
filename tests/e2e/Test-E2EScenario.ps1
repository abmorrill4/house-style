<#
.SYNOPSIS Checks a scenario in seconds, without Flare, for mistakes that would otherwise cost a Flare launch each.
.DESCRIPTION Writes one line per problem and exits with the number of problems. Run-FlareE2E.ps1 runs it before each
scenario and records a scenario with problems as a "preflight" failure without starting Flare. It checks that:
  - the script parses
  - it has no control characters (a "\b" or "\f" written through another language's string escapes)
  - every Send-E2E verb is one E2EHook.cs answers
  - every Invoke-Ribbon or Invoke-RibbonMenu label is a string in the plugin's source
  - every literal path under the run's project (Join-Path $run.Project "...") exists in the fixture
  - the text of every Send-E2E replace or select is in a fixture topic, or is text the scenario itself typed
.EXAMPLE powershell.exe -File tests\e2e\Test-E2EScenario.ps1 -Path tests\e2e\scenarios\pane-tabs.ps1
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Fixture,
    [string]$RepoRoot
)
$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path }
if (-not $Fixture) { $Fixture = Join-Path $RepoRoot "corpus\DigitalBanking" }
$name = [IO.Path]::GetFileName($Path)
$problems = New-Object System.Collections.Generic.List[string]
function Problem([string]$Text, $Extent) { $problems.Add("$name$(if ($Extent) { ":$($Extent.StartLineNumber)" }): $Text") }

$raw = [IO.File]::ReadAllText($Path)
$lines = $raw -split "`n"
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') {
        Problem "line $($i + 1) has a control character (0x$('{0:X2}' -f [int][char]$Matches[0])). Write backslashes as they are, with the Edit tool." $null
    }
}

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($raw, [ref]$tokens, [ref]$errors)
foreach ($e in $errors) { Problem "doesn't parse: $($e.Message)" $e.Extent }

$hook = [IO.File]::ReadAllText((Join-Path $RepoRoot "src\FlareAI.Flare\E2EHook.cs"))
$verbs = @([regex]::Matches($hook, 'case "(\w+)":') | ForEach-Object { $_.Groups[1].Value })
$source = (Get-ChildItem (Join-Path $RepoRoot "src\FlareAI.Flare") -Recurse -Filter *.cs | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
$topics = @(Get-ChildItem (Join-Path $Fixture "Content") -Recurse -Include *.htm, *.html -ErrorAction SilentlyContinue)

# A string argument's literal text, or $null when it's built from variables.
function Literal($el) {
    if ($el -is [System.Management.Automation.Language.StringConstantExpressionAst]) { return $el.Value }
    if ($el -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and -not $el.NestedExpressions.Count) { return $el.Value }
    $null
}
# Text the scenario types itself (Send-E2E replace ... -Extra @{ text = "..." }) can be the target of a later replace.
$typed = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true) | ForEach-Object {
    foreach ($kv in $_.KeyValuePairs) { if ((Literal $kv.Item1) -eq "text") { $kv.Item2.Extent.Text.Trim('"', "'") } }
})

# Files the scenario writes itself (Set-Content $topic ...) needn't be in the fixture.
$written = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in "Set-Content", "Out-File", "Copy-Item", "New-Item" }, $true) |
    ForEach-Object { $_.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.VariableExpressionAst] } | ForEach-Object { $_.VariablePath.UserPath } })
# A call inside Fails { ... } is meant to fail, so it's not checked.
function InFails($n) { for ($p = $n.Parent; $p; $p = $p.Parent) { if ($p -is [System.Management.Automation.Language.CommandAst] -and $p.GetCommandName() -eq "Fails") { return $true } }; $false }

foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
    if (InFails $c) { continue }
    $cmd = $c.GetCommandName()
    $args1 = @($c.CommandElements | Select-Object -Skip 1 | Where-Object { $_ -isnot [System.Management.Automation.Language.CommandParameterAst] })
    $first = if ($args1.Count) { Literal $args1[0] } else { $null }
    switch ($cmd) {
        "Send-E2E" {
            if ($first -and $verbs -notcontains $first) { Problem "Send-E2E $first isn't a verb E2EHook.cs answers ($($verbs -join ', '))." $c.Extent }
            if ($first -in "replace", "select" -and $args1.Count -ge 2) {
                $text = Literal $args1[1]
                if ($text -and -not ($typed | Where-Object { $_.Contains($text) }) -and
                    -not ($topics | Select-String -SimpleMatch -Pattern $text -List -ErrorAction SilentlyContinue | Select-Object -First 1)) {
                    Problem "Send-E2E $first `"$text`": no fixture topic has that text, and the scenario doesn't type it." $c.Extent
                }
            }
        }
        { $_ -in "Invoke-Ribbon", "Invoke-RibbonMenu" } {
            foreach ($a in $args1) {
                $label = Literal $a
                if ($label -and -not $source.Contains("`"$label`"")) { Problem "$cmd `"$label`" isn't a label in src\FlareAI.Flare." $c.Extent }
            }
        }
        "Join-Path" {
            if ($args1.Count -ge 2 -and $args1[0].Extent.Text -match '\.Project$') {
                $rel = Literal $args1[1]
                $assign = $c.Parent; while ($assign -and $assign -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { $assign = $assign.Parent }
                $var = if ($assign -and $assign.Left -is [System.Management.Automation.Language.VariableExpressionAst]) { $assign.Left.VariablePath.UserPath }
                $exists = try { Test-Path -LiteralPath (Join-Path $Fixture $rel) } catch { $false }   # a control character makes the path illegal
                if ($rel -and -not ($var -and $written -contains $var) -and -not $exists) { Problem "Join-Path $($args1[0].Extent.Text) `"$rel`": the fixture has no such file." $c.Extent }
            }
        }
    }
}

$problems | ForEach-Object { Write-Output $_ }
exit $problems.Count
