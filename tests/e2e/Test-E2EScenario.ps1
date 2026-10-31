<#
.SYNOPSIS Checks a scenario in seconds, without Flare, for mistakes that would otherwise cost a Flare launch each.
.DESCRIPTION Writes one line per problem and exits with the number of problems. Run-FlareE2E.ps1 runs it before each
scenario and records a scenario with problems as a "preflight" failure without starting Flare. It checks that:
  - the script parses
  - it has no control characters (a "\b" or "\f" written through another language's string escapes)
  - every Send-E2E verb is one E2EHook.cs answers
  - every Invoke-Ribbon or Invoke-RibbonMenu label is a string in the plugin's source
  - every button, menu item, tab, box name and dialog title a Send-E2E call names is a string in the plugin's source
  - every Assert-E2E call has a condition and a message, and the condition is not a list or pipeline
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
# Each topic's text as the editor shows it: tags dropped, entities decoded, white space collapsed.
function Flat([string]$s) { ($s -replace '\s+', ' ').Trim() }
$topics = @(Get-ChildItem (Join-Path $Fixture "Content") -Recurse -Include *.htm, *.html -ErrorAction SilentlyContinue | ForEach-Object {
    Flat ([System.Net.WebUtility]::HtmlDecode(([IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8) -replace '<[^>]+>', '')))
})

# A string argument's literal text, or $null when it's built from variables.
function Literal($el) {
    if ($el -is [System.Management.Automation.Language.StringConstantExpressionAst]) { return $el.Value }
    if ($el -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and -not $el.NestedExpressions.Count) { return $el.Value }
    $null
}
# UI text is checked against the plugin source as plain text. Some dialog labels come from Core (ReviewSession), so Core counts too.
# $uiText has & and a trailing ellipsis removed, the way the hook compares button and menu text.
$allText = $source + "`n" + ((Get-ChildItem (Join-Path $RepoRoot "src\FlareAI.Core") -Recurse -Filter *.cs | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n")
$uiText = ($allText -replace '&', '') -replace '(\u2026|…)"', '"'
function Norm([string]$s) { ($s -replace '&', '').TrimEnd([char]0x2026) }
function IsList($n) {
    while ($n -is [System.Management.Automation.Language.ParenExpressionAst]) {
        $pl = $n.Pipeline
        if ($pl -isnot [System.Management.Automation.Language.PipelineAst]) { return $false }
        if ($pl.PipelineElements.Count -gt 1) { return $true }
        $e = $pl.PipelineElements[0]
        if ($e -isnot [System.Management.Automation.Language.CommandExpressionAst]) { return $false }
        $n = $e.Expression
    }
    $n -is [System.Management.Automation.Language.ArrayExpressionAst] -or $n -is [System.Management.Automation.Language.ArrayLiteralAst]
}
$filterOps = 'Ieq', 'Ine', 'Igt', 'Ige', 'Ilt', 'Ile', 'Ilike', 'Inotlike', 'Imatch', 'Inotmatch', 'Ceq', 'Cne', 'Cgt', 'Cge', 'Clt', 'Cle', 'Clike', 'Cnotlike', 'Cmatch', 'Cnotmatch'

# Text the scenario types itself (Send-E2E replace ... -Extra @{ text = "..." }) can be the target of a later replace.
$typed = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true) | ForEach-Object {
    foreach ($kv in $_.KeyValuePairs) { if ((Literal $kv.Item1) -eq "text") { $kv.Item2.Extent.Text.Trim('"', "'") } }
})

# Files the scenario writes itself (Set-Content $topic ...) needn't be in the fixture.
# A variable counts as written when any command that makes files (Set-, Add-, Out-, Copy-, Move-, New-) or any .NET
# Write call takes it.
$written = @($ast.FindAll({ param($n)
        ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -match '^(Set|Add|Out|Copy|Move|New)-') -or
        ($n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Extent.Text -match 'Write|Copy|Move|Create') }, $true) |
    ForEach-Object { $_.FindAll({ param($v) $v -is [System.Management.Automation.Language.VariableExpressionAst] }, $true) | ForEach-Object { $_.VariablePath.UserPath } })
# A call inside Fails { ... } or the body of a try that has a catch is meant to fail, so it's not checked.
function InFails($n) {
    for ($p = $n.Parent; $p; $p = $p.Parent) {
        if ($p -is [System.Management.Automation.Language.CommandAst] -and $p.GetCommandName() -eq "Fails") { return $true }
        if ($p -is [System.Management.Automation.Language.TryStatementAst] -and $p.CatchClauses.Count -and $p.Body.Extent.StartOffset -le $n.Extent.StartOffset -and $n.Extent.EndOffset -le $p.Body.Extent.EndOffset) { return $true }
    }
    $false
}

foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
    if (InFails $c) { continue }
    $cmd = $c.GetCommandName()
    $args1 = @($c.CommandElements | Select-Object -Skip 1 | Where-Object { $_ -isnot [System.Management.Automation.Language.CommandParameterAst] })
    $first = if ($args1.Count) { Literal $args1[0] } else { $null }
    switch ($cmd) {
        "Send-E2E" {
            if ($first -and $verbs -notcontains $first) { Problem "Send-E2E $first isn't a verb E2EHook.cs answers ($($verbs -join ', '))." $c.Extent }
            if ($first -and $verbs -contains $first -and $first -ne "(select only)") {
                $arg = if ($args1.Count -ge 2) { Literal $args1[1] } else { $null }
                if ($arg -and $arg -ne "(select only)") {
                    $n = Norm $arg
                    $found = $true
                    if ($first -in "click", "dialogClick", "paneMenu", "rowMenu") { $found = $uiText.Contains('"' + $n) }
                    elseif ($first -eq "dialogTab") { $found = $uiText.Contains('"' + $n + '"') }
                    if (-not $found) { Problem "Send-E2E $first `"$arg`": no button, tab or menu item with that text in the plugin source." $c.Extent }
                }
                foreach ($h in $c.FindAll({ param($x) $x -is [System.Management.Automation.Language.HashtableAst] }, $true)) {
                    foreach ($kv in $h.KeyValuePairs) {
                        $key = Literal $kv.Item1; $val = Literal $(if ($kv.Item2 -is [System.Management.Automation.Language.PipelineAst] -and $kv.Item2.PipelineElements.Count -eq 1 -and $kv.Item2.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) { $kv.Item2.PipelineElements[0].Expression })
                        if (-not $val) { continue }
                        if ($key -eq "name" -and ($first -in "dialogType", "paneType") -and -not $allText.Contains('"' + $val + '"')) {
                            Problem "Send-E2E $first -Extra name `"$val`": no box with that name in the plugin source." $c.Extent
                        }
                        if ($key -eq "title" -and -not $allText.Contains($val)) {
                            Problem "Send-E2E $first -Extra title `"$val`": no dialog title with that text in the plugin source." $c.Extent
                        }
                    }
                }
            }
            if ($first -in "replace", "select" -and $args1.Count -ge 2) {
                $text = Literal $args1[1]
                if ($text -and -not ($typed | Where-Object { $_.Contains($text) }) -and
                    -not ($topics | Where-Object { $_.Contains((Flat $text)) } | Select-Object -First 1)) {
                    Problem "Send-E2E $first `"$text`": no fixture topic has that text, and the scenario doesn't type it." $c.Extent
                }
            }
        }
        "Assert-E2E" {
            $hint = "this is a list or pipeline, not a true or false value. Compare it, for example (@(`$x).Count -gt 0)."
            $named = @($c.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] })
            if ($named.Count -or $args1.Count -ne 2) { Problem "Assert-E2E takes a condition in parentheses and a message." $c.Extent; continue }
            $cond = $args1[0]
            $bad = IsList $cond
            if (-not $bad -and $cond -is [System.Management.Automation.Language.ParenExpressionAst]) {
                $e = $cond.Pipeline.PipelineElements | Select-Object -First 1
                $x = if ($e -is [System.Management.Automation.Language.CommandExpressionAst]) { $e.Expression }
                if ($x -is [System.Management.Automation.Language.BinaryExpressionAst] -and ([string]$x.Operator) -in $filterOps -and (IsList $x.Left)) { $bad = $true }
            }
            if ($bad) { Problem "Assert-E2E $($cond.Extent.Text): $hint" $c.Extent }
        }
        { $_ -in "Invoke-Ribbon", "Invoke-RibbonMenu" } {
            foreach ($a in $args1) {
                $label = Literal $a
                if ($label -and -not $source.Contains("`"$label`"")) { Problem "$cmd `"$label`" isn't a label in the plugin source." $c.Extent }
            }
        }
        "Join-Path" {
            if ($args1.Count -ge 2 -and $args1[0].Extent.Text -match '\.Project$') {
                $rel = Literal $args1[1]
                $assign = $c.Parent; while ($assign -and $assign -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { $assign = $assign.Parent }
                $var = if ($assign -and $assign.Left -is [System.Management.Automation.Language.VariableExpressionAst]) { $assign.Left.VariablePath.UserPath }
                $exists = try { Test-Path -LiteralPath (Join-Path $Fixture $rel) } catch { $false }   # a control character makes the path illegal
                # Only a path held in a variable the scenario never writes must already be in the fixture.
                if ($rel -and $var -and $written -notcontains $var -and -not $exists) { Problem "Join-Path $($args1[0].Extent.Text) `"$rel`": the fixture has no such file." $c.Extent }
            }
        }
    }
}

$problems | ForEach-Object { Write-Output $_ }
exit ([Math]::Min($problems.Count, 255))   # a count of 256 would otherwise exit 0
