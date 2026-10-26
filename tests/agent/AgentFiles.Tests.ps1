# Pester 3: powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\agent"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$agentsDir = Join-Path $repo '.claude\agents'
$skillsDir = Join-Path $repo '.claude\skills'

# The spec's table (planning/work/agent-system/spec.md section 1): one file per role and tier.
$expected = [ordered]@{
    'hs-intake'             = 'haiku/low'
    'hs-intake-sonnet'      = 'sonnet/low'
    'hs-security-screen'    = 'opus/low'
    'hs-match'              = 'haiku/low'
    'hs-match-sonnet'       = 'sonnet/low'
    'hs-scout'              = 'haiku/low'
    'hs-scout-sonnet'       = 'sonnet/medium'
    'hs-repro'              = 'sonnet/medium'
    'hs-repro-opus'         = 'opus/medium'
    'hs-spec'               = 'sonnet/medium'
    'hs-spec-opus'          = 'opus/medium'
    'hs-review-adversarial' = 'opus/medium'
    'hs-review-correctness' = 'sonnet/medium'
    'hs-review-consistency' = 'sonnet/medium'
    'hs-review-security'    = 'opus/high'
    'hs-review-goals'       = 'sonnet/medium'
    'hs-skeptic'            = 'sonnet/medium'
    'hs-skeptic-opus'       = 'opus/medium'
    'hs-fix'                = 'sonnet/medium'
    'hs-fix-opus'           = 'opus/medium'
    'hs-notes'              = 'haiku/low'
    'hs-rules'              = 'sonnet/medium'
    'hs-plan'               = 'sonnet/medium'
    'hs-build'              = 'haiku/high'
}
# Same job on a higher tier: the body must be the same, so the two never drift.
$tierPairs = @{
    'hs-intake-sonnet' = 'hs-intake'; 'hs-match-sonnet' = 'hs-match'; 'hs-scout-sonnet' = 'hs-scout'
    'hs-repro-opus' = 'hs-repro'; 'hs-spec-opus' = 'hs-spec'; 'hs-skeptic-opus' = 'hs-skeptic'; 'hs-fix-opus' = 'hs-fix'
}
$allowedTools = @('Read', 'Grep', 'Glob', 'Edit', 'Write', 'Bash')
# Agents with Bash, and the guard's allow list each gets: worker commits and runs every agent script, tester runs
# tests, reviewer only reads. Every other agent has no Bash at all.
$bashRoles = @{
    'hs-repro' = 'worker'; 'hs-repro-opus' = 'worker'; 'hs-fix' = 'worker'; 'hs-fix-opus' = 'worker'; 'hs-rules' = 'worker'; 'hs-build' = 'worker'
    'hs-review-correctness' = 'tester'; 'hs-skeptic' = 'tester'; 'hs-skeptic-opus' = 'tester'
    'hs-review-adversarial' = 'reviewer'; 'hs-review-consistency' = 'reviewer'; 'hs-review-security' = 'reviewer'
}

function Read-AgentFile([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    if ($text -notmatch '(?s)^---\r?\n(.*?)\r?\n---\r?\n(.*)$') { throw "No frontmatter in $Path" }
    $front = $Matches[1]; $body = $Matches[2]
    $fields = @{}
    $list = $null
    foreach ($line in ($front -split '\r?\n')) {
        if ($line -match '^([A-Za-z]+):\s*(.*)$') {
            $list = $null
            $fields[$Matches[1]] = $Matches[2].Trim()
            if ($Matches[2].Trim() -eq '') { $list = $Matches[1]; $fields[$list] = @() }
        } elseif ($list -and $line -match '^  - (\S+)\s*$') {
            $fields[$list] += $Matches[1]
        }
    }
    return [pscustomobject]@{ Fields = $fields; Front = $front; Body = $body }
}

Describe 'hs-* agent files' {
    $files = @(Get-ChildItem $agentsDir -Filter 'hs-*.md')

    It 'has exactly the agents in the spec table' {
        ($files | ForEach-Object BaseName | Sort-Object) -join ',' | Should Be (($expected.Keys | Sort-Object) -join ',')
    }

    It 'every hs-* file names model, effort, tools and skills (spec verification plan)' {
        foreach ($f in $files) {
            $fields = (Read-AgentFile $f.FullName).Fields
            foreach ($k in 'model', 'effort', 'tools') { "$($f.BaseName) $k=$($fields[$k])" | Should Match "$k=\S" }
            "$($f.BaseName) skills=$(@($fields['skills']).Count)" | Should Match 'skills=[1-9]'
        }
    }

    foreach ($f in $files) {
        $a = Read-AgentFile $f.FullName
        $name = $f.BaseName

        It "$name names itself, a delegation trigger, model, effort, tools and skills" {
            $a.Fields['name'] | Should Be $name
            $a.Fields['description'] | Should Match '^Use '
            "$($a.Fields['model'])/$($a.Fields['effort'])" | Should Be $expected[$name]
            $a.Fields['tools'] | Should Not BeNullOrEmpty
            @($a.Fields['skills']).Count | Should BeGreaterThan 0
        }

        It "$name uses only local tools: no web, no subagents, no MCP" {
            foreach ($t in ($a.Fields['tools'] -split ',\s*')) { $allowedTools -contains $t | Should Be $true }
            $a.Front | Should Not Match 'WebFetch|WebSearch|mcp__|Agent\('
        }

        It "$name preloads only skills that exist" {
            foreach ($s in @($a.Fields['skills'])) { Test-Path (Join-Path $skillsDir "$s\SKILL.md") | Should Be $true }
        }

        It "$name has an objective, an output and boundaries" {
            $a.Body | Should Match '(?m)^## Objective'
            $a.Body | Should Match '(?m)^## Output'
            $a.Body | Should Match '(?m)^## Boundaries'
        }

        $hasBash = ($a.Fields['tools'] -split ',\s*') -contains 'Bash'
        It "$name has Bash only if its role needs it" {
            $hasBash | Should Be ($bashRoles.ContainsKey($name))
        }
        if ($hasBash) {
            It "$name runs every Bash and PowerShell command through the guard, by absolute path, failing closed, as a $($bashRoles[$name])" {
                $rx = '(?s)PreToolUse:\s+- matcher: "Bash\|PowerShell"\s+hooks:\s+- type: command\s+shell: bash\s+onFailure: block\s+' +
                    [regex]::Escape("command: 'powershell -NoProfile -ExecutionPolicy Bypass -File `"`${CLAUDE_PROJECT_DIR}/scripts/agent/bash-guard.ps1`" -Role $($bashRoles[$name]) || exit 2'")
                $a.Front | Should Match $rx
            }
        }

        if ($tierPairs.ContainsKey($name)) {
            It "$name has the same brief as $($tierPairs[$name])" {
                $lower = Read-AgentFile (Join-Path $agentsDir "$($tierPairs[$name]).md")
                $a.Body | Should Be $lower.Body
                $a.Fields['tools'] | Should Be $lower.Fields['tools']
                (@($a.Fields['skills']) -join ',') | Should Be (@($lower.Fields['skills']) -join ',')
            }
        }
    }

    It 'gives the fix agents the Stop hook that runs the fast checks' {
        foreach ($n in 'hs-fix', 'hs-fix-opus') {
            (Read-AgentFile (Join-Path $agentsDir "$n.md")).Front |
                Should Match ('(?s)Stop:\s+- hooks:\s+- type: command\s+shell: bash\s+onFailure: block\s+' + [regex]::Escape("command: 'powershell -NoProfile -ExecutionPolicy Bypass -File `"`${CLAUDE_PROJECT_DIR}/scripts/agent/stop-check.ps1`" || exit 2'"))
        }
    }
}

Describe 'agent skills' {
    foreach ($s in 'repo-map', 'testing', 'findings', 'skeptic-protocol', 'coding-standards', 'routing-rules') {
        It "$s has a name and a description" {
            $text = [IO.File]::ReadAllText((Join-Path $skillsDir "$s\SKILL.md"))
            $text | Should Match "(?m)^name: $s\s*$"
            $text | Should Match '(?m)^description: \S'
        }
    }
}

Describe 'callers never pass a model to an hs-* agent' {
    It 'no workflow passes model alongside agentType' {
        foreach ($wf in Get-ChildItem (Join-Path $repo '.claude\workflows') -Filter *.js) {
            $text = [IO.File]::ReadAllText($wf.FullName)
            $opts = [regex]::Matches($text, '\{[^{}]*agentType[^{}]*\}')
            foreach ($o in $opts) { $o.Value | Should Not Match '\bmodel\s*:' }
            $text | Should Not Match "model\s*:\s*['""]?hs-"
        }
    }
    It 'no factory script passes a model with an hs-* agent' {
        $dir = Join-Path $repo 'scripts\factory'
        if (Test-Path $dir) {
            foreach ($f in Get-ChildItem $dir -Recurse -Include *.ps1, *.psm1) {
                foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
                    if ($line -match 'hs-[a-z]') { $line | Should Not Match '--model' }
                }
            }
        }
    }
}
