# A scripted world for running Invoke-Factory.ps1's passes in tests (factory-runtime spec P8, P9b, P11): a fake private
# reports repo and ledger, scripted agent jobs, a fake host that claims and answers Flare checks, and a real git origin
# and checkout in TestDrive, so the patch worktree, bundles and package-file diffs are git's own. Dot-source it inside a
# Describe; it dot-sources Invoke-Factory.ps1, then replaces its seams to GitHub and the agents VM.
$hRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
. (Join-Path $hRoot 'scripts\factory\Invoke-Factory.ps1')

$global:W = $null
function New-World([string]$name, [string]$mode = "shadow") {
    $base = Join-Path $TestDrive $name
    $root = Join-Path $base "HouseStyle"
    foreach ($d in "agent-jobs", "agent-work", "agent-in", "agent-tools", "nuget", "fix-state", "flare-outbox", "flare-inbox", "factory-logs", "patch-work-parent") { New-Item -ItemType Directory -Force (Join-Path $root $d) | Out-Null }
    # A real origin with main's files: the rules, the prompts and the agent scripts the run reads from its base.
    $origin = Join-Path $base "origin.git"; $seed = Join-Path $base "seed"; $checkout = Join-Path $base "checkout"
    $ErrorActionPreference = "Continue"
    & git.exe init -q --bare $origin 2>&1 | Out-Null
    & git.exe init -q $seed 2>&1 | Out-Null
    foreach ($c in @(@("user.name", "seed"), @("user.email", "seed@example.invalid"), @("core.autocrlf", "false"))) { & git.exe -C $seed config $c[0] $c[1] }
    foreach ($p in "rules\routing.json", "rules\areas.json", "scripts\factory\PatchRules.psd1", ".github\factory\fix-prompt.md", ".github\factory\review-prompt.md",
                   ".github\factory\triage-prompt.md", ".github\factory\cover-prompt.md", "scripts\agent\test-channel.ps1", "scripts\agent\AgentCommon.psm1") {
        $dest = Join-Path $seed $p; New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item (Join-Path $hRoot $p) $dest
    }
    $files = @{ "src/App/Thing.cs" = "class Thing`n{`n    int A() => 1;`n}`n"; "src/App/App.csproj" = "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <ItemGroup>`n    <PackageReference Include=`"Newtonsoft.Json`" Version=`"13.0.1`" />`n  </ItemGroup>`n</Project>`n"
                "tests/e2e/scenarios/reopen-issues.ps1" = "# scenario`n"; "tests/App.Tests/ThingTests.cs" = "class ThingTests { }`n"; "README.md" = "seed`n" }
    foreach ($k in $files.Keys) { $dest = Join-Path $seed $k; New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null; [IO.File]::WriteAllText($dest, $files[$k]) }
    & git.exe -C $seed add -A 2>&1 | Out-Null; & git.exe -C $seed commit -q -m "main" 2>&1 | Out-Null
    & git.exe -C $seed branch -M main 2>&1 | Out-Null
    & git.exe -C $seed remote add origin $origin; & git.exe -C $seed push -q origin main 2>&1 | Out-Null
    & git.exe clone -q $origin $checkout 2>&1 | Out-Null
    foreach ($c in @(@("user.name", "House Style factory"), @("user.email", "factory@gethousestyle.com"))) { & git.exe -C $checkout config $c[0] $c[1] }
    $script:Run = New-RunContext -Mode $mode -DryRunMode $false -Report 0 -Root $root -Checkout $checkout -OAuth ("sk-ant-oat01-" + ("Hn4wPq8z" * 12)) -GhToken "not-used" -AgentSid "S-1-5-21-0-0-0-1002"
    $global:W = [pscustomobject]@{
        root = $root; seed = $seed; origin = $origin; now = [datetime]::new(2026, 11, 2, 12, 0, 0, [DateTimeKind]::Utc)
        reports = @{}; ledger = (New-Object System.Collections.ArrayList); jobs = (New-Object System.Collections.ArrayList)
        calls = (New-Object System.Collections.ArrayList); published = (New-Object System.Collections.ArrayList); openPrs = 0; fork = '{"approval_policy":"all_external_contributors"}'
        agents = @{}; fullSuiteExit = 0; reviewVerdict = "MERGE"; repro = "pass"
    }
    Set-Trial "active" "2026-12-31"
    $global:W
}
function Set-Trial([string]$state, [string]$end, [bool]$wasLicensed = $false) {
    $o = [ordered]@{ state = $state }; if ($end) { $o.endDate = $end }; $o.wasLicensed = $wasLicensed
    $p = Join-Path $global:W.root "flare-inbox\trial.json"
    if ($state -eq "missing") { Remove-Item $p -ErrorAction SilentlyContinue; return }
    [IO.File]::WriteAllText($p, ($o | ConvertTo-Json -Compress))
}
function Move-Clock([int]$minutes) { $global:W.now = $global:W.now.AddMinutes($minutes) }

# ---- the private reports repo --------------------------------------------------------------------------------------
function Add-Report([int]$n, [string[]]$labels, [string]$title = "Ignoring an issue doesn't stick") {
    $global:W.reports[$n] = [pscustomobject]@{ number = $n; title = $title; body = "It comes back after a relint."; author = [pscustomobject]@{ login = "abmorrill4" }
        labels = (New-Object System.Collections.ArrayList); comments = (New-Object System.Collections.ArrayList); events = (New-Object System.Collections.ArrayList); closed = $false }
    foreach ($l in @("problem") + @($labels | Where-Object { $_ })) { Add-OwnerLabel $n $l }
}
# A label the owner adds, with its event, as the label-events fixture shows one.
function Add-OwnerLabel([int]$n, [string]$label, [string]$actor = "abmorrill4") {
    $r = $global:W.reports[$n]
    if (-not $r.labels.Contains($label)) { [void]$r.labels.Add($label) }
    [void]$r.events.Add([pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = $label }; actor = [pscustomobject]@{ login = $actor }; created_at = $global:W.now.ToString("o") })
}
function Add-OwnerComment([int]$n, [string]$body) { [void]$global:W.reports[$n].comments.Add([pscustomobject]@{ body = $body; author = [pscustomobject]@{ login = "abmorrill4" }; createdAt = $global:W.now.ToString("o") }) }
function Get-ReportLabelList([int]$n) { @($global:W.reports[$n].labels) }
function Get-FactoryComments([int]$n) { @($global:W.reports[$n].comments | Where-Object { $_.author.login -eq "factory-bot" } | ForEach-Object { $_.body }) }

function Get-Now { $global:W.now }
function Gh { throw "The test world has no gh: $($args -join ' ')" }
function Get-AllReports { @($global:W.reports.Values | Where-Object { -not $_.closed } | ForEach-Object { [pscustomobject]@{ number = $_.number; title = $_.title; body = $_.body; author = $_.author; labels = @($_.labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) } }) }
function Get-ReportComments([int]$n) { @($global:W.reports[$n].comments) }
function Get-ReportEvents([int]$n) { @($global:W.reports[$n].events) }
function Say([int]$n, [string]$text) { [void]$global:W.reports[$n].comments.Add([pscustomobject]@{ body = "**Factory:** " + $text; author = [pscustomobject]@{ login = "factory-bot" }; createdAt = $global:W.now.ToString("o") }) }
function Set-ReportLabels([int]$n, $add, $remove) {
    $r = $global:W.reports[$n]
    foreach ($x in @($remove)) { if ($x) { [void]$r.labels.Remove($x) } }
    foreach ($x in @($add)) { if ($x -and -not $r.labels.Contains($x)) { [void]$r.labels.Add($x); [void]$r.events.Add([pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = $x }; actor = [pscustomobject]@{ login = "factory-bot" }; created_at = $global:W.now.ToString("o") }) } }
}
function Close-Report([int]$n) { $global:W.reports[$n].closed = $true }
function Get-FactoryLogin { "factory-bot" }
function Get-LedgerIssue { 1 }
function Get-LedgerEntries([int]$days = 1) { @($global:W.ledger) }
function Add-LedgerComment([string]$body) { $e = ConvertFrom-LedgerComment $body; if ($e) { [void]$global:W.ledger.Add($e) } }
function Get-OpenPrCount { $global:W.openPrs }
function Get-CapLeft($rules) { [int]$rules.autoFix.dailyCap }
function Get-ForkApprovalText { $global:W.fork }
function Get-PublicIssues([string]$state, [int]$limit, [string]$fields) { @() }
function Add-Evidence { [void]$global:W.calls.Add("evidence") }
function Add-PlanIssue { [void]$global:W.calls.Add("plan"); "https://github.com/x/y/issues/1" }
function Get-RevertedAutoFixes($reverted) { @() }
function Get-Overlap($paths, [string]$branch) { @() }
function Publish-Fix([int]$n, [string]$commit, [string]$title, [string]$body, [bool]$draft, [bool]$autoFix, [bool]$noNotes) {
    [void]$global:W.published.Add([pscustomobject]@{ report = $n; commit = $commit; draft = $draft; autoFix = $autoFix; body = $body })
    "https://github.com/abmorrill4/house-style/pull/$(500 + $global:W.published.Count)"
}
# ---- the agents VM ------------------------------------------------------------------------------------------------
function Test-AgentsReady { @() }
function Remove-StaleCaches { [void]$global:W.calls.Add("remove-stale-caches"); @() }
function Initialize-RunCache {
    [void]$global:W.calls.Add("restore:$($Run.base)")
    $Run.cache = Join-Path $Run.root "nuget\20261102000000-$($Run.base.Substring(0, 6))"
    $Run.ctx = [pscustomobject]@{ cache = $Run.cache }
    $null
}
function Initialize-Labels { }
# Scripted jobs: each kind answers from $W.agents (a scriptblock per kind) or a default.
function Invoke-Job([hashtable]$spec) {
    [void]$global:W.jobs.Add([pscustomobject]@{ kind = $spec.kind; base = $spec.base; channel = $spec.channel; exact = $spec.exact; patch = $spec.patch; cache = $Run.cache; at = $global:W.now; calls = $global:W.calls.Count })
    [void]$global:W.calls.Add("job:$($spec.kind):$($spec.base)")
    if ($global:W.agents.ContainsKey($spec.kind)) { return (& $global:W.agents[$spec.kind] $spec) }
    $ok = '{"type":"result","subtype":"success","is_error":false,"num_turns":3,"result":"done","usage":{"input_tokens":10,"output_tokens":20}}'
    $files = @{ "stdout.txt" = $ok; "stderr.txt" = ""; "launcher.json" = "{}" }
    switch ($spec.kind) {
        "triage" { $files["result.json"] = (New-BugBrief | ConvertTo-Json -Depth 6) }
        "fix" { $f = New-FixFiles $spec.base; foreach ($k in $f.Keys) { $files[$k] = $f[$k] } }
        "review" { $files["result.json"] = (@{ verdict = $global:W.reviewVerdict; reasons = @("looks right") } | ConvertTo-Json) }
        "test" { $files["stdout.txt"] = "tests ran" }
    }
    [pscustomobject]@{ id = [guid]::NewGuid().ToString(); kind = $spec.kind; exit = $(if ($spec.kind -eq "test") { $global:W.fullSuiteExit } else { 0 }); timedOut = $false; files = $files; refused = $null; stuck = $null }
}
function Invoke-ReproCheck([int]$n, [string]$name, $testFiles, [string]$base, [string]$patch) { [void]$global:W.calls.Add("repro:$base"); $global:W.repro }
function New-BugBrief {
    [pscustomobject]@{
        kind = "bug"; summary = "Ignoring an issue doesn't stick after a relint."; goal = "Hide a false positive."; impact = "Anyone who ignores an issue."
        reproduced = [pscustomobject]@{ result = "yes"; how = "a failing test" }; matches = @()
        options = @([pscustomobject]@{ option = "Fix now"; effort = "S"; risk = "Low"; for_writers = "Ignore sticks." }, [pscustomobject]@{ option = "Park"; effort = "S"; risk = "None"; for_writers = "Nothing changes." })
        recommendation = [pscustomobject]@{ option = "Fix now"; why = "Small and clear." }; questions = @(); confidence = "high"; security = $false; docs_gap = $false
        repro_test = "App.Tests.ThingTests.IgnoreSticks"; asks = @([pscustomobject]@{ ask = "Make ignoring stick."; kind = "bug"; fix_now = $false })
    }
}
# A fix's files as the launcher writes them: the patch from a real git diff on the base, its commit messages and status.
function New-FixFiles([string]$base, [hashtable]$extra = @{}) {
    $d = Join-Path $TestDrive ("fix-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
    $ErrorActionPreference = "Continue"
    & git.exe clone -q $global:W.origin $d 2>&1 | Out-Null
    & git.exe -C $d -c advice.detachedHead=false checkout -q --detach $base 2>&1 | Out-Null
    [IO.File]::WriteAllText((Join-Path $d "src\App\Ignore.cs"), "class Ignore`n{`n    bool Sticks() => true;`n}`n")
    [IO.File]::WriteAllText((Join-Path $d "tests\App.Tests\IgnoreTests.cs"), "class IgnoreTests`n{`n    [Fact]`n    public void IgnoreSticks() { }`n}`n")
    New-Item -ItemType Directory -Force (Join-Path $d "changes") | Out-Null
    [IO.File]::WriteAllText((Join-Path $d "changes\ignore-sticks.md"), "- Ignored issues stay ignored after you lint again.`n")
    foreach ($k in $extra.Keys) { $p = Join-Path $d $k; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $extra[$k]) }
    & git.exe -C $d add -A 2>&1 | Out-Null
    & git.exe -C $d -c user.name=a -c user.email=a@example.invalid commit -q -m "Make ignoring stick" 2>&1 | Out-Null
    $patch = (& git.exe -C $d diff --no-ext-diff --no-textconv --no-renames --src-prefix=a/ --dst-prefix=b/ $base HEAD | Out-String).Replace("`r`n", "`n")
    @{ "result.json" = (@{ kind = "fixed"; summary = "Ignoring now sticks." } | ConvertTo-Json); "fix.patch" = $patch; "commits.txt" = "Make ignoring stick`n"; "status.txt" = "" }
}
# Main moves on: a commit on origin's main, optionally changing a package file.
function Add-MainCommit([switch]$PackageChange, [string]$file = "README.md") {
    $ErrorActionPreference = "Continue"
    if ($PackageChange) { $script:pkg = 1 + [int]$script:pkg; [IO.File]::WriteAllText((Join-Path $global:W.seed "src\App\App.csproj"), "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <ItemGroup>`n    <PackageReference Include=`"Newtonsoft.Json`" Version=`"13.0.$(2 + $script:pkg)`" />`n  </ItemGroup>`n</Project>`n") }
    else { Add-Content (Join-Path $global:W.seed $file) "more" -Encoding ASCII }
    & git.exe -C $global:W.seed add -A 2>&1 | Out-Null; & git.exe -C $global:W.seed commit -q -m "main moves" 2>&1 | Out-Null; & git.exe -C $global:W.seed push -q origin main 2>&1 | Out-Null
    (& git.exe -C $global:W.seed rev-parse HEAD).Trim()
}

# ---- the host ------------------------------------------------------------------------------------------------------
function Get-OutboxRequests { @(Get-ChildItem (Join-Path $global:W.root "flare-outbox") -Directory | Where-Object { $_.Name -match '^[0-9a-f-]{36}$' } | ForEach-Object Name) }
# The host claims the waiting request (a rename) and writes its first progress.
function Invoke-HostClaim {
    $ids = @(Get-OutboxRequests); if (-not $ids.Count) { return $null }
    $id = $ids[0]
    Rename-Item (Join-Path $global:W.root "flare-outbox\$id") "$id.claimed"
    $in = Join-Path $global:W.root "flare-inbox\$id"; New-Item -ItemType Directory -Force $in | Out-Null
    [IO.File]::WriteAllText((Join-Path $in "progress.json"), (@{ check = $id; step = 1; at = $global:W.now.ToString("o") } | ConvertTo-Json -Compress))
    $id
}
function Set-HostProgress([string]$id) { [IO.File]::WriteAllText((Join-Path $global:W.root "flare-inbox\$id\progress.json"), (@{ check = $id; step = 6; at = $global:W.now.ToString("o") } | ConvertTo-Json -Compress)) }
# The host's result for a claimed check, written under .tmp- and renamed, as Write-AgentsVM writes.
function Set-HostResult([string]$id, [string]$outcome, [string]$vmKind, [string[]]$failed = @(), [switch]$LeaveTmp) {
    $o = [ordered]@{ check = $id; outcome = $outcome }; if ($vmKind) { $o.vmKind = $vmKind }; $o.failed = @($failed); $o.retried = @()
    $in = Join-Path $global:W.root "flare-inbox\$id"; New-Item -ItemType Directory -Force $in | Out-Null
    [IO.File]::WriteAllText((Join-Path $in ".tmp-result.json"), ($o | ConvertTo-Json -Compress))
    if (-not $LeaveTmp) { Move-Item (Join-Path $in ".tmp-result.json") (Join-Path $in "result.json") -Force }
}
function Complete-HostCheck([string]$outcome = "pass", [string]$vmKind) { $id = Invoke-HostClaim; if ($id) { Set-HostResult $id $outcome $vmKind }; $id }
function Get-Fix([int]$n) { $p = Join-Path $global:W.root "fix-state\$n\state.json"; if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json } else { $null } }
# One pass, as factory.yml runs one: a fresh label log, the same world.
function Invoke-Run {
    $Run.labelLog = @{}; $Run.base = $null; $Run.fixPath = $null
    # The pass sets git's safety overrides in the environment; they go again afterwards, so other tests don't see them.
    $saved = @{}; foreach ($v in "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1", "GIT_NO_REPLACE_OBJECTS") { $saved[$v] = [Environment]::GetEnvironmentVariable($v) }
    try { Invoke-FactoryPass }
    finally { foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v]) } }
}
