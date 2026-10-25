# Helpers for the factory's triage step: check the triage agent's brief, turn it into the comment on the private
# report, and check who approved a fix. Kept out of Invoke-Factory.ps1 so tests/factory can test them without a VM.

Import-Module (Join-Path $PSScriptRoot "Guards.psm1")   # ConvertFrom-TrxText

$Kinds = "bug", "friction", "feature", "question", "duplicate", "noise"
$AskKinds = "bug", "friction", "feature", "question"

# What's wrong with a brief, as a list of reasons; empty when it is usable.
function Test-TriageBrief($b) {
    $problems = @()
    if (-not $b) { return @("The brief is empty.") }
    if ($Kinds -notcontains $b.kind) { $problems += "kind '$(Format-Field $b.kind 30)' isn't one of $($Kinds -join ', ')." }
    foreach ($f in "summary", "goal", "impact") { if (-not ("" + $b.$f).Trim()) { $problems += "$f is missing." } }
    if (-not $b.reproduced -or @("yes", "no", "not-tried") -notcontains $b.reproduced.result) { $problems += "reproduced.result must be yes, no or not-tried." }
    if (@($b.options | Where-Object { $_ }).Count -lt 2) { $problems += "There are fewer than two options." }
    if (-not $b.recommendation -or -not ("" + $b.recommendation.option).Trim()) { $problems += "There is no recommendation." }
    if (@("high", "medium", "low") -notcontains $b.confidence) { $problems += "confidence must be high, medium or low." }
    if ($b.security -isnot [bool]) { $problems += "security must be true or false." }
    if ($null -ne $b.docs_gap -and $b.docs_gap -isnot [bool]) { $problems += "docs_gap must be true or false." }
    # The test name reaches dotnet test --filter, so it is an identifier and nothing else.
    if ($b.repro_test -and -not (Test-TestName $b.repro_test)) { $problems += "repro_test must be a test name (letters, digits, underscores and dots)." }
    $asks = @($b.asks | Where-Object { $_ })
    if (-not $asks.Count -and $b.kind -ne "noise") { $problems += "There are no asks." }
    if ($b.kind -eq "bug" -and $asks.Count -and -not @($asks | Where-Object { $_.kind -eq "bug" }).Count) { $problems += "kind is bug, but no ask is a bug." }
    if ($asks.Count -gt 4) { $problems += "There are more than four asks." }
    foreach ($a in $asks) {
        if (-not ("" + $a.ask).Trim()) { $problems += "An ask has no text." }
        if ($AskKinds -notcontains $a.kind) { $problems += "An ask's kind must be one of $($AskKinds -join ', ')." }
        if ($null -ne $a.fix_now -and $a.fix_now -isnot [bool]) { $problems += "An ask's fix_now must be true or false." }
    }
    $problems
}

function Test-TestName($name) { ("" + $name) -cmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){0,9}$' -and ("" + $name).Length -le 200 }

# Agent text for a comment: one line or a short paragraph, capped, with @mentions defused so nobody gets pinged, and
# no links, images or HTML, so a brief can't put a clickable or auto-loading URL in front of the owner.
function Format-Field($text, [int]$max = 600) {
    $t = ("" + $text) -replace '[\r\n]+', ' '
    $t = $t -replace '!\[[^\]]*\]\([^)]*\)', '' -replace '\[([^\]]*)\]\([^)]*\)', '$1' -replace '<[^>]*>', '' -replace '(?i)\b(https?|ftp|file)://\S+', '[link removed]'
    $t = $t.Trim()
    if ($t.Length -gt $max) { $t = $t.Substring(0, $max - 1) + "…" }
    $t -replace '@', ('@' + [char]0x200B)   # a zero-width space after @ stops the mention
}

function Format-TriageComment($b) {
    $lines = @(
        "**Triage:** $(Format-Field $b.kind 20) · confidence $(Format-Field $b.confidence 10)",
        "",
        "**What happened:** $(Format-Field $b.summary)",
        "**What they were trying to do:** $(Format-Field $b.goal 300)",
        "**Impact on writers:** $(Format-Field $b.impact 400)",
        "**Reproduced:** $(Format-Field $b.reproduced.result 10). $(Format-Field $b.reproduced.how 400)"
    )
    if ($b.repro_test -and (Test-TestName $b.repro_test)) { $lines += "**Test that should fail on main:** ``$($b.repro_test)``" }
    if ($b.security -eq $true) { $lines += "**Security:** the brief says this describes a vulnerability. Look at it privately." }
    $asks = @($b.asks | Where-Object { $_ } | Select-Object -First 4)
    if ($asks.Count) {
        $lines += "", "**What they asked for:**"
        foreach ($a in $asks) {
            $cov = if ($a.kind -eq "bug") { "a bug, so where it lines up isn't checked" }
                   elseif ($a.uncertain -eq $true) { "the judges split on which issue covers it" }
                   elseif ($a.covers -eq "none" -or -not $a.ref) { "no issue covers it" } else { "$(Format-Field $a.covers 10) in $(Format-Field $a.ref 30)" }
            $now = if ($a.fix_now -eq $true) { ", fix now" } else { "" }
            $lack = if (("" + $a.missing).Trim()) { ". Missing: " + (Format-Field $a.missing 200) } else { "" }
            $lines += "- $(Format-Field $a.ask 200) ($(Format-Field $a.kind 10)$now; $cov)$lack"
        }
    }
    $hits = @($b.matches | Select-Object -First 6)
    if ($hits.Count) {
        $lines += "", "**Lines up with:**"
        foreach ($m in $hits) { $lines += "- $(Format-Field $m.ref 30) $(Format-Field $m.title 120) ($(Format-Field $m.relation 12)): $(Format-Field $m.why 200)" }
    }
    $lines += "", "**Options:**"
    foreach ($o in @($b.options | Where-Object { $_ } | Select-Object -First 4)) {
        $lines += "- $(Format-Field $o.option 200) · effort $(Format-Field $o.effort 4) · risk: $(Format-Field $o.risk 200) · for writers: $(Format-Field $o.for_writers 200)"
    }
    $lines += "", "**Recommendation:** $(Format-Field $b.recommendation.option 200). $(Format-Field $b.recommendation.why 400)"
    $questions = @($b.questions | Where-Object { $_ } | Select-Object -First 5)
    if ($questions.Count) {
        $lines += "", "**Questions for you:**"
        foreach ($q in $questions) { $lines += "- $(Format-Field $q 300)" }
    }
    $lines += "", "Decide with a label: ``decision:fix`` (bugs only), ``decision:plan``, ``decision:park`` or ``decision:decline``, or ask Claude to triage the reports."
    $lines -join "`n"
}

# The account that last added $label to the report, from the issue's events (oldest first, as the API returns them).
# $null when the label was never added. A label that was removed and added again counts from the last add.
function Get-LabelActor($events, [string]$label) {
    $last = @($events | Where-Object { $_.event -eq "labeled" -and $_.label.name -eq $label }) | Select-Object -Last 1
    if ($last) { $last.actor.login } else { $null }
}

function Get-Labels($r) { @($r.labels | ForEach-Object { $_.name }) }

# Who added decision:fix last after $since (a saved fix's waiting time), from the report's events as the label-events
# fixture shows them: "owner", "other", or $null when nobody did.
function Get-DecisionFixSince($events, [datetime]$since, [string]$owner = "abmorrill4") {
    $last = @($events | Where-Object { $_.event -eq "labeled" -and $_.label.name -eq "decision:fix" -and $_.created_at -and ([datetime]$_.created_at).ToUniversalTime() -gt $since.ToUniversalTime() }) | Select-Object -Last 1
    if (-not $last) { return $null }
    if (("" + $last.actor.login) -eq $owner) { "owner" } else { "other" }
}

# Which report this run works on, and in which step. Triage first (new reports, and failed triage up for a retry), then
# one fix the owner approved. Only reports the feedback service filed ($owner, label problem or error) are considered.
# A report queued because the day's auto-fix cap was full is triaged and routed again once $capLeft allows. route:* and
# waiting:* labels are for display: they neither block triage nor approve anything.
# Returns @{ Report; Mode } or $null.
function Select-Report($all, [string]$owner, [int]$issue = 0, [int]$capLeft = 0, [bool]$wipFull = $false) {
    $ours = @($all | Where-Object {
        $l = Get-Labels $_
        $_.author.login -eq $owner -and ($l -contains "problem" -or $l -contains "error")
    } | Sort-Object number)
    if ($issue) { $ours = @($ours | Where-Object number -eq $issue) }
    foreach ($r in $ours) {
        $l = Get-Labels $r
        $new = -not ($l | Where-Object { $_ -like "decision:*" -or $_ -like "factory:*" -or ($_ -like "triage:*" -and $_ -ne "triage:failed") })
        if ($new) { return @{ Report = $r; Mode = "triage" } }
    }
    # A report waiting on the daily auto-fix cap (factory:queued) or on the open-PR limit (factory:wip-limit) is
    # triaged and routed again once that limit has room.
    foreach ($r in $ours) {
        $l = Get-Labels $r
        $waitsOn = @($l | Where-Object { $_ -eq "factory:queued" -or $_ -eq "factory:wip-limit" })
        if (-not $waitsOn.Count -or ($l | Where-Object { $_ -like "decision:*" -or ($_ -like "factory:*" -and $waitsOn -notcontains $_) })) { continue }
        if (($l -contains "factory:queued" -and $capLeft -le 0) -or ($l -contains "factory:wip-limit" -and $wipFull)) { continue }
        return @{ Report = $r; Mode = "triage" }
    }
    if ($wipFull) { return $null }   # no new fix starts while the open factory PRs are at their limit
    foreach ($r in $ours) {
        $l = Get-Labels $r
        $decisions = @($l | Where-Object { $_ -like "decision:*" })
        # decision:fix alone: a report with a second decision label waits for the owner and never blocks the queue.
        # factory:wip-limit and factory:queued (the day's agent runs ran out) only mean it waited.
        if ($decisions.Count -eq 1 -and $decisions[0] -eq "decision:fix" -and -not ($l | Where-Object { $_ -like "factory:*" -and @("factory:wip-limit", "factory:queued") -notcontains $_ })) { return @{ Report = $r; Mode = "fix" } }
    }
    $null
}

# Reports the owner approved with decision:fix that would start a fix now if the open factory PRs had room. They get
# factory:wip-limit while they wait, so the command center can show why.
function Get-WipWaiting($all, [string]$owner) {
    @($all | Where-Object {
        $l = Get-Labels $_
        $d = @($l | Where-Object { $_ -like "decision:*" })
        $_.author.login -eq $owner -and ($l -contains "problem" -or $l -contains "error") -and $d.Count -eq 1 -and $d[0] -eq "decision:fix" -and
            -not ($l | Where-Object { $_ -like "factory:*" })
    })
}

# Why a report may not be fixed yet, or $null when it may: exactly one decision label, decision:fix, and the last
# decision label added was decision:fix (so a later park or decline that someone forgot to clean up wins).
# The owner's account must be the one that added it: the repo's write access is not the only gate.
function Test-FixGate($labels, $events, [string]$owner = "abmorrill4") {
    $decisions = @($labels | Where-Object { $_ -like "decision:*" })
    if ($decisions.Count -ne 1 -or $decisions[0] -ne "decision:fix") { return "it has decision labels $($decisions -join ', '), not decision:fix alone" }
    $last = @($events | Where-Object { $_.event -eq "labeled" -and $_.label.name -like "decision:*" }) | Select-Object -Last 1
    if (-not $last -or $last.label.name -ne "decision:fix") { return "the last decision label added was $(if ($last) { $last.label.name } else { 'none' }), not decision:fix" }
    if (("" + $last.actor.login) -ne $owner) { return "decision:fix was added by an account other than the owner's" }
    $null
}

# Failed triage attempts since the owner last asked for a retry (a comment starting "**Retry triage**").
function Get-TriageFailures($comments) {
    $since = 0; $i = 0; $all = @($comments)
    foreach ($c in $all) { $i++; if (("" + $c.body).StartsWith("**Retry triage**")) { $since = $i } }
    @($all | Select-Object -Skip $since | Where-Object { ("" + $_.body).StartsWith("**Factory:** Triage failed") }).Count
}

# The factory writes only its own labels. decision:* is the owner's alone, so the factory can never approve a fix.
# route:* and waiting:* are for display; nothing reads them as permission.
# Why a run is blocked, one blocked:<cause> label per report (command center spec section 2). The factory sets these
# four. flare-api and upstream are the work loop's.
$BlockedCauses = "vm", "flaky-test", "owner", "other"

function Assert-FactoryLabel([string]$label) {
    $blocked = $label -match '^blocked:(.+)$' -and $BlockedCauses -contains $Matches[1]
    if ($label -and -not $blocked -and $label -notlike "triage:*" -and $label -notlike "factory:*" -and $label -notlike "route:*" -and $label -notlike "waiting:*") { throw "The factory may not set the label '$label'." }
}

# ==== Routing (work/report-pipeline/spec.md section 2) ==============================================================
# The brief only proposes. Get-Route decides from a record the script builds: the brief's enums, its matches resolved
# against issue lists the script fetched, and, once a fix is built, what the script itself found in the diff and the
# test runs. The record holds no report text, so it can be posted on the report and logged.

$Routes = "hold", "evidence", "auto-fix", "fix-for-review", "brief-only", "plan", "docs-gap", "pile"
# Why the owner is needed (spec section 3). One label per waiting item.
$WaitingReasons = "want", "plan-input", "plan-review", "merge", "security", "stuck", "rule-change", "decide"

# A public issue we curate: open, labeled from:*, and (once Set-CuratedAuthors has run) written by the owner or the
# factory's account. Only these get evidence comments, and only their bodies reach the agents.
function Test-CuratedIssue($issue) {
    if (-not $issue -or ("" + $issue.state).ToUpperInvariant() -ne "OPEN") { return $false }
    if (-not [bool](@(Get-Labels $issue) | Where-Object { $_ -like "from:*" })) { return $false }
    # Once the factory names the accounts it trusts, only their issues count: an issue's author can edit its body after
    # it is labeled, and curated bodies reach the triage agent and the coverage judge. Fails closed with no author.
    if ($script:CuratedAuthors.Count) { return [bool]($issue.author -and $script:CuratedAuthors -contains ("" + $issue.author.login)) }
    $true
}

# The accounts whose issues count as curated (the owner and the factory's own). Set once by the factory.
$script:CuratedAuthors = @()
function Set-CuratedAuthors([string[]]$logins) { $script:CuratedAuthors = @($logins | Where-Object { $_ } | Select-Object -Unique) }

# The area label of an issue: its first label in $areaLabels (the precedence list in rules/areas.json), or with no
# list its first area:* label. $null when it has none.
function Get-IssueArea($issue, $areaLabels = @()) {
    $labels = @(Get-Labels $issue)
    $known = @($areaLabels | Where-Object { $_ })
    if ($known.Count) { return @($known | Where-Object { $labels -contains $_ } | Select-Object -First 1)[0] }
    @($labels | Where-Object { $_ -like "area:*" } | Select-Object -First 1)[0]
}

# The brief's matches, checked against what the script fetched. A ref the lists don't hold is "missing" and counts
# for nothing. $issues: public issues (number, state, labels). $reports: the private reports. $self: this report.
# $areaLabels: the area labels in precedence order, from rules/areas.json.
function Resolve-Matches($briefMatches, $issues, $reports, [int]$self, $areaLabels = @()) {
    $byIssue = @{}; foreach ($i in @($issues)) { if ($i) { $byIssue[[int]$i.number] = $i } }
    $byReport = @{}; foreach ($r in @($reports)) { if ($r) { $byReport[[int]$r.number] = $r } }
    foreach ($m in @($briefMatches | Where-Object { $_ } | Select-Object -First 10)) {
        $rel = "" + $m.relation
        if (@("same", "part-of", "related", "conflicts") -notcontains $rel) { continue }
        $ref = ("" + $m.ref).Trim()
        if ($ref -match '^(?i)report\s*#(\d{1,6})$') {
            $num = [int]$Matches[1]; $r = $byReport[$num]
            $state = if ($num -eq $self -or -not $r) { "missing" } else { ("" + $r.state).ToLowerInvariant() }
            if (-not $state) { $state = "open" }   # an open-only list has no state field
            [pscustomobject]@{ relation = $rel; target = "report"; number = $num; state = $state; curated = $false; area = $null }
        }
        elseif ($ref -match '^#(\d{1,6})$') {
            $num = [int]$Matches[1]; $i = $byIssue[$num]
            $state = if ($i) { ("" + $i.state).ToLowerInvariant() } else { "missing" }
            [pscustomobject]@{ relation = $rel; target = "issue"; number = $num; state = $state; curated = (Test-CuratedIssue $i); area = $(if ($i) { Get-IssueArea $i $areaLabels } else { $null }) }
        }
    }
}

# The Get-Route input record, before any fix is built. area is the brief's area label when it is one of $areaLabels
# (rules/areas.json), otherwise empty: a label, never report text.
# $askRefs: the asks' refs resolved with Resolve-Matches, so an ask's issue is one the script fetched.
function New-RouteInput($brief, $resolved, [int]$capLeft, $areaLabels = @(), $askRefs = @()) {
    [pscustomobject]@{
        area = $(if ($brief.area -and @($areaLabels) -ccontains ("" + $brief.area)) { "" + $brief.area } else { "" })
        kind = "" + $brief.kind; security = ($brief.security -eq $true); reproduced = "" + $brief.reproduced.result
        confidence = "" + $brief.confidence
        repro_test = $(if ($brief.repro_test -and (Test-TestName $brief.repro_test)) { "" + $brief.repro_test } else { "" })
        docs_gap = ($brief.docs_gap -eq $true)
        matches = @($resolved)
        asks = @(ConvertTo-RouteAsks $brief $askRefs)
        our_words = $null   # not checked yet; $true or $false once the script has checked the text it would publish
        fix = $null         # set once a fix is built: repro, lines, files, behavior, owner_only, overlap
        cap_left = $capLeft
    }
}

# The brief's asks as Get-Route reads them: kind, fix_now, covers and the resolved issue ($null when the ref isn't an
# issue the script fetched). index points back at the brief's ask, for the public text.
function ConvertTo-RouteAsks($brief, $askRefs = @()) {
    $i = 0
    foreach ($a in @($brief.asks | Where-Object { $_ } | Select-Object -First 4)) {
        $num = if (("" + $a.ref).Trim() -match '^#(\d{1,6})$') { [int]$Matches[1] } else { 0 }
        $hit = @($askRefs | Where-Object { $_ -and $_.target -eq "issue" -and $_.number -eq $num } | Select-Object -First 1)
        [pscustomobject]@{
            index = $i; kind = "" + $a.kind; fix_now = ($a.fix_now -eq $true); uncertain = ($a.uncertain -eq $true)
            covers = $(if (@("full", "partial") -contains $a.covers) { "" + $a.covers } else { "none" })
            issue = $(if ($hit.Count) { $hit[0] } else { $null })
        }
        $i++
    }
}

# ==== The coverage judge (.github/factory/cover-prompt.md) ===========================================================
# The triage brief proposes the asks. Whether an open issue covers each one is decided again by a judge with no tools,
# which sees the report, the ask and every open issue we curate, three times, by majority. A model comparison on the
# shadow reports and a held-out set (2026-10-24, planning/work/shadow-review) found this steadier than the brief's own
# covers, and immune to the brief missing the right issue.

# The judge's input: the report (capped at 20,000 characters), one ask, and the bodies of the open issues we curate
# (each capped at 6,000).
# All data. The tags that frame them are taken out of every part, so no part can close its block and pose as another.
function New-CoverInput([string]$report, [string]$ask, $issues) {
    function Unframe([string]$t) {
        # Lookalikes first: escaped and fullwidth angle brackets, and invisible characters inside a tag.
        $t = $t -replace '(?i)&lt;', '<' -replace '(?i)&gt;', '>' -replace '[\uFF1C\uFE64\u2039\u3008]', '<' -replace '[\uFF1E\uFE65\u203A\u3009]', '>'
        $t = $t -replace '[\u200B-\u200F\u2060\uFEFF\u00AD]', ''
        $t -replace '(?i)<\s*/?\s*(report|ask|issue)\b[^>]*>', '[tag]'
    }
    $body = ($report.Trim() -replace '(?is)^<report>\s*', '' -replace '(?is)\s*</report>$', '')
    if ($body.Length -gt 20000) { $body = $body.Substring(0, 19999) + "…" }   # the tester's text sets no one's costs
    $lines = @("<report>", (Unframe $body), "</report>", "", "<ask>$(Unframe (Format-Field $ask 300))</ask>", "")
    foreach ($i in @($issues | Where-Object { Test-CuratedIssue $_ } | Sort-Object { [int]$_.number })) {
        $text = Unframe ("" + $i.body).Trim(); if ($text.Length -gt 6000) { $text = $text.Substring(0, 5999) + "…" }
        $lines += "<issue>", "#$($i.number) $(Unframe (Format-Field $i.title 200))", "", $text, "</issue>", ""
    }
    $lines -join "`n"
}

# The judges' answer by majority, in two steps. First, is the ask covered at all: more than half the usable votes say
# full or partial. Then which issue: the one more than half the usable votes named (otherwise the ask is uncertain and
# goes to the owner), and full only when most votes for that issue say full. A ref that isn't one of $allowed (the issues the judge was shown) counts as
# none. $results: the judges' parsed JSON, $null for one that gave no usable answer. votes is how many were usable.
function Get-CoverVote($results, [int[]]$allowed) {
    $votes = @(foreach ($r in @($results | Where-Object { $_ -and $_.best })) {
        $c = "" + $r.best.covers; $num = 0
        if (("" + $r.best.ref).Trim() -match '^#(\d{1,6})$') { $num = [int]$Matches[1] }
        if (@("full", "partial") -notcontains $c -or $allowed -notcontains $num) { $c = "none"; $num = 0 }
        [pscustomobject]@{ covers = $c; number = $num; missing = "" + $r.best.missing }
    })
    $covered = @($votes | Where-Object { $_.covers -ne "none" })
    if ($votes.Count -and $covered.Count * 2 -eq $votes.Count) {
        # As many say covered as say not (one and one): the owner decides.
        return [pscustomobject]@{ ref = ""; covers = "none"; missing = ""; votes = $votes.Count; uncertain = $true }
    }
    if (-not $votes.Count -or $covered.Count * 2 -lt $votes.Count) {
        return [pscustomobject]@{ ref = ""; covers = "none"; missing = ""; votes = $votes.Count; uncertain = $false }
    }
    $top = @($covered | Group-Object number | Sort-Object @{ Expression = { $_.Count }; Descending = $true }, @{ Expression = { [int]$_.Name } })[0].Group
    # Covered, but no one issue has a majority: the judges split on which issue. The owner decides that one.
    if ($top.Count * 2 -le $votes.Count) { return [pscustomobject]@{ ref = ""; covers = "none"; missing = ""; votes = $votes.Count; uncertain = $true } }
    $full = @($top | Where-Object { $_.covers -eq "full" }).Count
    $covers = if ($full * 2 -gt $top.Count) { "full" } else { "partial" }
    $miss = @($top | Where-Object { $_.covers -eq "partial" -and $_.missing } | Select-Object -First 1)
    [pscustomobject]@{ ref = "#$($top[0].number)"; covers = $covers
                       missing = $(if ($covers -eq "partial" -and $miss.Count) { $miss[0].missing } else { "" }); votes = $votes.Count; uncertain = $false }
}

# The asks Get-Route works from. A record with none (an older brief) gets one ask from its kind and its first "same"
# match on an issue, so a part-of or related match never counts as covering the report.
function Get-RouteAsks($in) {
    $asks = @($in.asks | Where-Object { $_ })
    if ($asks.Count) { return $asks }
    $same = @($in.matches | Where-Object { $_ -and $_.target -eq "issue" -and $_.relation -eq "same" } | Select-Object -First 1)
    [pscustomobject]@{ index = -1; kind = "" + $in.kind; fix_now = $false
                       covers = $(if ($same.Count) { "full" } else { "none" }); issue = $(if ($same.Count) { $same[0] } else { $null }) }
}

# An ask that becomes evidence: not a bug, and an open issue we curate covers it. A fix-now ask on covered work is
# evidence too, and the report also gets the small fix as a draft PR (row 5, owner decision 2026-10-24).
function Test-EvidenceAsk($a) {
    $a.kind -ne "bug" -and $a.issue -and $a.issue.state -eq "open" -and $a.issue.curated -and @("full", "partial") -contains $a.covers
}

# The area label for a new plan or docs-gap issue: the brief's (already checked against rules/areas.json), otherwise
# that of the first open public issue it lines up with. $null when neither has one, so it shows under No area yet.
function Get-PlanArea($in, $resolved) {
    if ($in.area) { return "" + $in.area }
    $hit = @($resolved | Where-Object { $_ -and $_.target -eq "issue" -and $_.state -eq "open" -and $_.area } | Select-Object -First 1)
    if ($hit.Count) { return "" + $hit[0].area }
    $null
}

# Takes the first matching row of the matrix. $rules is rules/routing.json. Returns route, row, reasons, waiting (the
# reason the owner is needed, or $null), regression (a closed issue number), target (where evidence goes), area (the
# area:* label for a plan or docs-gap issue), build (the fix step may run now) and queued (over the daily cap).
function Get-Route($in, $rules) {
    $route = Get-RouteRow $in $rules
    if ($route.route -ne "evidence" -and @($route.evidence).Count) { $route.reasons = @($route.reasons) + "it also adds evidence on $(@($route.evidence | ForEach-Object { "#$($_.issue)" }) -join ', ') for the asks those issues cover" }
    if (@("plan", "docs-gap", "hold") -notcontains $route.route -and @($route.plan).Count) { $route.reasons = @($route.reasons) + "it also plans a new issue for the asks nothing covers" }
    $route
}

function Get-RouteRow($in, $rules) {
    $out = [ordered]@{ route = ""; row = 0; reasons = @(); waiting = $null; regression = $null; target = $null; area = $null; build = $false; queued = $false; evidence = @(); plan = @(); fix_ask = -1 }
    $ms = @($in.matches | Where-Object { $_ })
    $fix = $in.fix

    # 1. hold: nothing public
    $hold = @()
    if ($in.security -eq $true) { $hold += "the brief says it describes a vulnerability" }
    if ($fix -and @($fix.owner_only | Where-Object { $_ }).Count) { $hold += "the fix touches owner-only code ($(@($fix.owner_only) -join ', '))" }
    if ($in.our_words -eq $false) { $hold += "text we would publish failed the our-words check" }
    if ($hold.Count) { $out.route = "hold"; $out.row = 1; $out.reasons = $hold; $out.waiting = "security"; return [pscustomobject]$out }

    # A duplicate of an open report: the report it duplicates routes its own asks, so this one adds no evidence and plans
    # nothing. Noise has nothing to route, whatever asks the brief holds.
    if ($in.kind -eq "duplicate") {
        $dup = @($ms | Where-Object { $_.target -eq "report" -and $_.state -eq "open" -and $_.relation -eq "same" })
        if ($dup.Count) {
            $out.route = "evidence"; $out.row = 2; $out.target = [pscustomobject]@{ type = "report"; number = $dup[0].number }
            $out.reasons = @("it duplicates open report #$($dup[0].number)"); return [pscustomobject]$out
        }
    }
    if ($in.kind -eq "noise") { $out.route = "pile"; $out.row = 9; $out.reasons = @("nothing to act on"); return [pscustomobject]$out }

    # 2. evidence: each ask an open issue we curate covers, fully or partly. A report whose asks are all covered is
    # evidence. Otherwise those asks still go as evidence and the rest decide the route below. A bug never does.
    $asks = @(Get-RouteAsks $in)
    $byIssue = [ordered]@{}
    foreach ($a in @($asks | Where-Object { Test-EvidenceAsk $_ })) {
        $key = "" + [int]$a.issue.number
        if (-not $byIssue.Contains($key)) { $byIssue[$key] = [pscustomobject]@{ route = "evidence"; issue = [int]$key; asks = @(); amend = $false } }
        $byIssue[$key].asks += [int]$a.index
        if ($a.covers -eq "partial") { $byIssue[$key].amend = $true }
    }
    $parts = @($byIssue.Values)
    $rest = @($asks | Where-Object { -not (Test-EvidenceAsk $_) })
    # A question that rides along with covered asks is answered by the issue they go to.
    if ($parts.Count) {
        # Not when the docs don't answer it (docs_gap): that question gets its own issue, below.
        $riding = @(if ($in.docs_gap -ne $true) { $rest | Where-Object { $_.kind -eq "question" -and -not $_.fix_now -and -not $_.uncertain -and [int]$_.index -ge 0 } })
        $parts[0].asks = @($parts[0].asks) + @($riding | ForEach-Object { [int]$_.index })   # named in that issue's evidence
        $rest = @($rest | Where-Object { $riding -notcontains $_ })
    }
    # The bug rows follow the asks: a report is a bug when one of its asks is (an older record's one ask is its kind).
    $hasBug = [bool]@($asks | Where-Object { $_.kind -eq "bug" }).Count
    # A fix now on planned work gets both: the evidence, and a small interim fix as a draft PR (owner decision 2026-10-24).
    $nowAll = @($asks | Where-Object { $_.fix_now -and @("friction", "question") -contains $_.kind -and -not $_.uncertain -and [int]$_.index -ge 0 })
    if ($parts.Count -and -not $rest.Count -and -not $hasBug -and -not $nowAll.Count) {
        $out.route = "evidence"; $out.row = 2; $out.target = [pscustomobject]@{ type = "issue"; number = $parts[0].issue }
        $out.evidence = $parts
        $out.reasons = @($parts | ForEach-Object { "open issue #$($_.issue) covers $(if ($_.amend) { 'part of it, so the comment says what the issue lacks' } else { 'it' })" })
        return [pscustomobject]$out
    }
    $out.evidence = $parts
    # Asks nothing covers that a new issue should plan: features, and friction that isn't a fix now, but never one the
    # judges split on.
    # They get their planned issue whatever the route, like covered asks get their evidence.
    $planAsks = @($rest | Where-Object { ($_.kind -eq "feature" -or ($_.kind -eq "friction" -and -not $_.fix_now)) -and -not $_.uncertain -and [int]$_.index -ge 0 })
    # A question the docs don't answer (docs_gap) that comes with other asks still to route goes in the same new issue,
    # so it isn't lost when the report closes. Alone, it takes the docs-gap row below.
    if ($in.docs_gap -eq $true -and ($parts.Count -or @($rest | Where-Object { $_.kind -ne "question" }).Count)) {
        $planAsks = @($planAsks) + @($rest | Where-Object { $_.kind -eq "question" -and -not $_.fix_now -and -not $_.uncertain -and [int]$_.index -ge 0 })
    }
    $out.plan = @($planAsks | ForEach-Object { [int]$_.index })
    if ($planAsks.Count) { $out.area = Get-PlanArea $in $ms }

    # The judges split on which issue covers an ask: the owner decides, with the brief.
    if (@($rest | Where-Object { $_.uncertain -and $_.kind -ne "bug" }).Count) {
        $out.route = "brief-only"; $out.row = 6; $out.waiting = "decide"; $out.reasons = @("the judges split on which open issue covers one of its asks")
        return [pscustomobject]$out
    }

    # 3. regression: the same as a closed issue, handled as a bug
    $reg = @($ms | Where-Object { $_.target -eq "issue" -and $_.state -eq "closed" -and $_.relation -eq "same" })
    if ($reg.Count) { $out.regression = $reg[0].number }

    if ($hasBug -or $out.regression) {
        # The fix is for the bug. A small fix the brief wants now for another ask is planned instead of lost.
        $out.plan = @($out.plan) + @($rest | Where-Object { $_.fix_now -and @("friction", "question") -contains $_.kind -and [int]$_.index -ge 0 } | ForEach-Object { [int]$_.index })
        if (@($out.plan).Count -and -not $out.area) { $out.area = Get-PlanArea $in $ms }
        $pre = if ($out.regression) { @("it looks like a regression of closed issue #$($out.regression)") } else { @() }
        # 6. brief-only: not reproduced
        if ($in.reproduced -ne "yes") {
            $out.route = "brief-only"; $out.row = 6; $out.waiting = "decide"
            $out.reasons = $pre + "it wasn't reproduced"; return [pscustomobject]$out
        }
        # 4 and 5: every auto-fix check, done by the script
        $a = $rules.autoFix
        $fails = @()
        if ($in.confidence -ne $a.confidence) { $fails += "confidence is $($in.confidence), not $($a.confidence)" }
        if (-not $in.repro_test) { $fails += "the brief names no test that fails on main" }
        if ($fix) {
            if ($fix.repro -ne "pass") { $fails += "$($fix.repro)" }
            if ([int]$fix.lines -gt [int]$a.maxChangedLines) { $fails += "the fix changes $($fix.lines) lines, over $($a.maxChangedLines)" }
            if ([int]$fix.files -gt [int]$a.maxChangedFiles) { $fails += "the fix changes $($fix.files) files, over $($a.maxChangedFiles)" }
            if (@($fix.behavior | Where-Object { $_ }).Count) { $fails += "the fix may change behavior writers see ($(@($fix.behavior) -join ', '))" }
            if (@($fix.overlap | Where-Object { $_ }).Count) { $fails += "another open factory PR changes the same files (#$(@($fix.overlap) -join ', #'))" }
            if (@($fix.tamper | Where-Object { $_ }).Count) { $fails += "the fix changes existing tests or protected test files ($(@($fix.tamper) -join ', '))" }
            if ($fix.area -and @($rules.suspended | Where-Object { $_ -eq $fix.area }).Count) { $fails += "auto-fix is suspended for the $($fix.area) area after a reverted fix" }
            $outside = @(Get-RouteUncovered $fix $rules)
            if ($outside.Count) { $fails += "the fix changes files auto-fix doesn't cover ($($outside -join ', '))" }
        }
        if ($fails.Count) {
            $out.route = "fix-for-review"; $out.row = 5; $out.reasons = $pre + $fails
            $out.build = -not $fix; if ($fix) { $out.waiting = "merge" }
            return [pscustomobject]$out
        }
        $out.route = "auto-fix"; $out.row = 4
        if ([int]$in.cap_left -le 0) {
            $out.queued = $true; $out.reasons = $pre + "today's $($a.dailyCap) auto-fixes are used up, so it waits for tomorrow"
        }
        else {
            $out.build = -not $fix
            $out.reasons = $pre + $(if ($fix) { "every auto-fix check passed" } else { "it was reproduced with high confidence, so the fix is built and checked" })
        }
        return [pscustomobject]$out
    }

    # 5. fix-for-review without a bug: the brief wants a small change now (UI copy, a missing click). Built as a draft
    # PR for the owner, never auto-merged.
    if ($nowAll.Count) {
        $out.route = "fix-for-review"; $out.row = 5; $out.reasons = @("it is a small change to existing behavior, built as a draft for review")
        # Uncovered first: an untracked small fix has nowhere else to go, while a covered one already has its issue.
        $nows = @($nowAll | Sort-Object @{ Expression = { if (Test-EvidenceAsk $_) { 1 } else { 0 } } }, @{ Expression = { [int]$_.index } })
        $out.fix_ask = [int]$nows[0].index
        # One small fix per draft PR. Any other fix-now ask is planned instead of lost.
        $more = @($nows | Select-Object -Skip 1 | Where-Object { -not (Test-EvidenceAsk $_) } | ForEach-Object { [int]$_.index })
        if ($more.Count) { $out.plan = @($out.plan) + $more; if (-not $out.area) { $out.area = Get-PlanArea $in $ms } }
        if ($fix -and @($fix.behavior | Where-Object { $_ }).Count) { $out.reasons += "the fix changes behavior writers see ($(@($fix.behavior) -join ', '))" }
        if ($fix -and @($fix.tamper | Where-Object { $_ }).Count) { $out.reasons += "the fix changes existing tests or protected test files ($(@($fix.tamper) -join ', '))" }
        if ($fix -and [int]$fix.lines -gt [int]$rules.autoFix.maxChangedLines) { $out.reasons += "the fix changes $($fix.lines) lines, over $($rules.autoFix.maxChangedLines)" }
        if ($fix -and [int]$fix.files -gt [int]$rules.autoFix.maxChangedFiles) { $out.reasons += "the fix changes $($fix.files) files, over $($rules.autoFix.maxChangedFiles)" }
        if ($fix -and @($fix.overlap | Where-Object { $_ }).Count) { $out.reasons += "another open factory PR changes the same files (#$(@($fix.overlap) -join ', #'))" }
        if ($fix -and $fix.area -and @($rules.suspended | Where-Object { $_ -eq $fix.area }).Count) { $out.reasons += "fixes are suspended for the $($fix.area) area after a reverted fix" }
        $out.build = -not $fix; if ($fix) { $out.waiting = "merge" }
        return [pscustomobject]$out
    }

    # 7. plan: an ask no open issue covers
    if (@($planAsks | Where-Object { $_.kind -ne "question" }).Count -or (-not @($in.asks | Where-Object { $_ }).Count -and @("feature", "friction") -contains $in.kind)) {
        $out.route = "plan"; $out.row = 7; $out.reasons = @("it asks for new or changed behavior")
        $out.area = Get-PlanArea $in $ms
        return [pscustomobject]$out
    }
    # 8. docs-gap
    if (($in.kind -eq "question" -or $planAsks.Count) -and $in.docs_gap -eq $true) {
        $out.route = "docs-gap"; $out.row = 8; $out.reasons = @("the docs or UI copy don't answer it"); $out.area = Get-PlanArea $in $ms
        return [pscustomobject]$out
    }
    # 9. pile
    if ($in.kind -eq "noise" -or $in.kind -eq "question") {
        $out.route = "pile"; $out.row = 9
        $out.reasons = @($(if ($in.kind -eq "noise") { "nothing to act on" } else { "a question the docs already answer" })); return [pscustomobject]$out
    }
    # Anything else (a duplicate of something that isn't open) goes to the owner with its brief.
    $out.route = "brief-only"; $out.row = 6; $out.waiting = "decide"; $out.reasons = @("no row of the matrix fits it")
    [pscustomobject]$out
}

# The triage label follows the route, not the brief's kind: the pile is triage:noise, everything else triage:ready.
function Get-TriageLabel($route) { if ($route.route -eq "pile") { "triage:noise" } else { "triage:ready" } }

# The waiting reason to label. In shadow mode the owner still decides every report the old way, except the pile.
function Get-WaitingReason($route, [string]$mode) {
    if ($route.route -eq "hold") { return "security" }
    if ($mode -ne "live") { if ($route.route -eq "pile") { return $null } else { return "decide" } }
    $route.waiting
}

# The fix step runs only for a route this run computed (passed in, never read from a label) or the owner's
# decision:fix. A route:* label grants nothing. Returns $null when allowed, else why not.
function Test-FixAllowed($route, $labels, $events, [string]$owner = "abmorrill4") {
    if ($route -and $route.build -and @("auto-fix", "fix-for-review") -contains $route.route) { return $null }
    Test-FixGate $labels $events $owner
}

# Our words: the text we would publish shares no run of $runLength words with the report, and holds no email address,
# UNC path or user-profile path. $texts: name -> text. Returns the problems, never the matching words.
function Test-OurWords($texts, [string]$report, [int]$runLength = 8) {
    $zw = [string][char]0x200B
    function Words($s) { @([regex]::Matches(("" + $s).Replace($zw, "").ToLowerInvariant(), "[\p{L}\p{N}']+") | ForEach-Object { $_.Value }) }
    $grams = New-Object 'System.Collections.Generic.HashSet[string]'
    $rw = Words $report
    for ($i = 0; $i -le $rw.Count - $runLength; $i++) { [void]$grams.Add(($rw[$i..($i + $runLength - 1)] -join " ")) }
    $problems = @()
    foreach ($name in @($texts.Keys | Sort-Object)) {
        $t = ("" + $texts[$name]).Replace($zw, "")
        $w = Words $t
        for ($i = 0; $i -le $w.Count - $runLength; $i++) {
            if ($grams.Contains(($w[$i..($i + $runLength - 1)] -join " "))) { $problems += "$name shares a run of $runLength words with the report"; break }
        }
        if ($t -match '[\w.+-]+@[\w-]+(\.[\w-]+)+') { $problems += "$name contains an email address" }
        if ($t -match '\\\\[\w.$-]+\\[\w.$-]+') { $problems += "$name contains a network (UNC) path" }
        if ($t -match '(?i)([a-z]:\\(users|documents and settings)\\[^\\\s"]+)|(/(users|home)/[^/\s"]+/)|%userprofile%|\$env:userprofile') { $problems += "$name contains a user-profile path" }
        if (Test-SecretShape $t) { $problems += "$name contains something shaped like a key or token" }
    }
    $problems
}

# Key and token shapes, the same ones the feedback worker redacts (tools/feedback-worker/src/redact.ts), plus a secret
# name assigned a long mixed value. Published text holding one is held. Says only whether one was found.
$SecretShapes = @(
    '\bsk-[A-Za-z0-9_-]{8,}', '\bgh[pousr]_[A-Za-z0-9]{20,}', '\bgithub_pat_[A-Za-z0-9_]{20,}', '\bxox[abprs]-[A-Za-z0-9-]{10,}',
    '\bAKIA[0-9A-Z]{16}\b', '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}', '(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{20,}'
)
$SecretAssignment = '(?i)(api[_-]?key|access[_-]?token|auth[_-]?token|token|secret|password|passwd|pwd|client[_-]?secret)["'']?\s*[:=]\s*["'']?([A-Za-z0-9+/=_.-]{20,})'
function Test-SecretShape([string]$text) {
    foreach ($p in $SecretShapes) { if ($text -cmatch $p) { return $true } }
    foreach ($m in [regex]::Matches($text, $SecretAssignment)) {
        $v = $m.Groups[2].Value
        if ($v -match '\d' -and $v -match '[A-Za-z]') { return $true }   # a long value mixing letters and digits, not a word or a call
    }
    $false
}

# Code a fix may not change without the owner: a fix touching it is held, never pushed. Updates, settings and keys, AI
# providers, the validators and diffs every DOM write goes through, the shared-folder feedback and requests code, the
# team guide's git handling (Git.Untrusted lives in Team.cs), the e2e hook, and the factory's own gates: agents,
# skills, workflows, scripts, routing rules, the factory's tests and everything under .github.
$OwnerOnly = '^src/FlareAI\.Core/(Update|Settings|Llm|Editing|Feedback)/|^src/FlareAI\.Core/Actions/(GuideRequests|Team)\.cs$|^src/FlareAI\.Flare/E2EHook\.cs$|' +
             '^\.claude/(agents|skills|workflows)/|^scripts/(factory|agent)/|^rules/|^tests/factory/|^\.github/'

# A unified diff, per file: the added and removed lines. Only lines inside a hunk count, so an added line whose text
# starts with "++" or "--" is still an added line, and only the real file headers are skipped.
# status is A for a new file, D for a deleted one, M otherwise, from the file's header lines.
function Split-Diff([string]$diff) {
    $files = @{}; $cur = $null; $inHunk = $false
    foreach ($line in ($diff -split "`r?`n")) {
        if ($line -match '^diff --git a/.+ b/(.+)$') { $cur = $Matches[1]; $files[$cur] = @{ added = @(); removed = @(); status = "M"; hunks = @() }; $inHunk = $false; continue }
        if (-not $cur) { continue }
        if ($line.StartsWith("@@")) { $inHunk = $true; $files[$cur].hunks += , @{ added = @(); removed = @() }; continue }
        if (-not $inHunk) {   # index, mode, ---, +++ and other header lines
            if ($line.StartsWith("new file mode")) { $files[$cur].status = "A" } elseif ($line.StartsWith("deleted file mode")) { $files[$cur].status = "D" }
            continue
        }
        $h = $files[$cur].hunks[-1]
        if ($line.StartsWith("+")) { $files[$cur].added += $line.Substring(1); $h.added += $line.Substring(1) }
        elseif ($line.StartsWith("-")) { $files[$cur].removed += $line.Substring(1); $h.removed += $line.Substring(1) }
    }
    $files
}

# Every added line of a diff, for the our-words check.
function Get-AddedText([string]$diff) {
    $files = Split-Diff $diff
    (@($files.Keys | Sort-Object | ForEach-Object { $files[$_].added }) | ForEach-Object { $_ }) -join "`n"
}

# Test data a fix may not change on its own (command center spec section 4.2): baselines, fixtures and golden files,
# the scenarios' recorded output, the corpus and its ground truth, the test kit and isolation wiring, project wiring,
# and the e2e harness (which the factory also refuses outright). Any change here, new files included, is for review.
$TestProtected = '^tests/(.+/)?(baselines|fixtures|golden)/|^tests/FlareAI\.Scenarios/Output/|^corpus/|^tests/FlareAI\.TestKit/|^tests/Isolation/|' +
                 '\.csproj$|(^|/)Directory\.(Build|Packages)\.|^tests/e2e/(Run-FlareE2E\.ps1|FlareE2E\.psm1)$|^tests/e2e/flaui/'

# The test-tamper guard. A reproduction test is a new test, so a new test file is fine, and so is a hunk in an existing
# test file that only adds whole new tests (Test-WholeTests). Everything else is listed: a deleted file, a hunk that
# removes or changes a line, a hunk that adds anything else (a line inside an existing test, a Skip, a using), and any
# change in a protected folder. $files: Split-Diff output.
function Get-TestTamper($files, [string]$protected = $TestProtected) {
    $found = @()
    foreach ($p in @($files.Keys | Sort-Object)) {
        $f = $files[$p]
        if ($p -match $protected) { $found += "$p (protected test files)"; continue }
        if ($p -notlike "tests/*" -or $f.status -eq "A") { continue }
        if ($f.status -eq "D") { $found += "$p (deleted)"; continue }
        $lang = if ($p -like "*.cs") { "cs" } elseif ($p -like "*.Tests.ps1") { "pester" } else { "" }
        foreach ($h in @($f.hunks)) {
            if (@($h.removed).Count) { $found += "$p (changed or deleted lines)"; break }
            if (@($h.added).Count -and -not (Test-WholeTests @($h.added) $lang)) { $found += "$p (added lines that aren't whole new tests)"; break }
        }
    }
    $found
}

# A line with its strings, characters and comments taken out, so only real braces are counted.
function Get-CodeOnly([string]$line, [string]$lang) {
    if ($lang -eq "cs") { $line -replace '@"([^"]|"")*"', '""' -replace '"(\\.|[^"\\])*"', '""' -replace "'(\\.|[^'\\])+'", "''" -replace '/\*.*?\*/', '' -replace '//.*$', '' }
    else { $line -replace '"(`.|[^"`])*"', '""' -replace "'([^']|'')*'", "''" -replace '#.*$', '' }
}

# True for an added line the brace count can't see through: in C# a preprocessor directive (#if, #endif, ...), a block
# comment left open, or a verbatim or raw string left open; in Pester a block comment mark (<# or #>) or a here-string
# opener. Such a line could hide code over several lines, so a hunk holding one goes for review.
function Test-OpaqueLine([string]$line, [string]$lang) {
    if ($lang -eq "cs") {
        if ($line -match '^\s*#') { return $true }
        if ($line.Contains('"""')) { return $true }
        $s = $line -replace '@"([^"]|"")*"', '""' -replace '"(\\.|[^"\\])*"', '""' -replace "'(\\.|[^'\\])+'", "''" -replace '/\*.*?\*/', ''
        $s = $s -replace '//.*$', ''
        return ($s.Contains('/*') -or $s.Contains('*/') -or $s.Contains('@"'))
    }
    $line.Contains('<#') -or $line.Contains('#>') -or ($line -match '@["'']\s*$') -or ($line -match '^\s*["'']@')
}

# True when the added lines of one hunk are nothing but complete new tests: in C#, members that start with test
# attributes ([Fact], [Theory], [InlineData] and the like, never a Skip) and end with their balanced closing brace; in
# Pester, complete It, Describe or Context blocks with no -Skip or -Pending. Blank and comment lines between them are fine.
function Test-WholeTests($lines, [string]$lang) {
    if (@("cs", "pester") -notcontains $lang) { return $false }
    $lines = @($lines); $i = 0; $n = $lines.Count; $members = 0
    # Lines that could hide code across lines are never whole tests: they could switch existing tests off.
    if (@($lines | Where-Object { Test-OpaqueLine ("" + $_) $lang }).Count) { return $false }
    if ($lang -eq "pester" -and @($lines | Where-Object { $_ -match '(?i)\s-(Skip|Pending)\b|Set-ItResult' }).Count) { return $false }
    while ($i -lt $n) {
        $t = ("" + $lines[$i]).Trim()
        if (-not $t -or ($lang -eq "cs" -and $t.StartsWith("//")) -or ($lang -eq "pester" -and $t.StartsWith("#"))) { $i++; continue }
        if ($lang -eq "cs") {
            $attrs = @()
            while ($i -lt $n -and ("" + $lines[$i]).Trim() -match '^\[.*\]$') { $attrs += ("" + $lines[$i]).Trim(); $i++ }
            if (-not $attrs.Count -or @($attrs | Where-Object { $_ -match '(?i)skip' }).Count) { return $false }
            if (@($attrs | Where-Object { $_ -notmatch '^\[\s*(Fact|Theory|InlineData|MemberData|ClassData|Trait|Test|TestCase|TestMethod)\b' }).Count) { return $false }
            if (-not @($attrs | Where-Object { $_ -match '^\[\s*(Fact|Theory|Test|TestCase|TestMethod)\b' }).Count) { return $false }
            if ($i -ge $n -or ("" + $lines[$i]) -notmatch '\w\s*\(') { return $false }   # the method's signature
        }
        elseif ($t -notmatch '^(It|Describe|Context)\s+[''"]') { return $false }
        $depth = 0; $opened = $false; $closed = $false
        while ($i -lt $n) {
            foreach ($ch in (Get-CodeOnly ("" + $lines[$i]) $lang).ToCharArray()) {
                if ($ch -eq '{') { $depth++; $opened = $true } elseif ($ch -eq '}') { $depth--; if ($depth -lt 0) { return $false } }
            }
            $i++
            if ($opened -and $depth -eq 0) { $closed = $true; break }
        }
        if (-not $closed) { return $false }
        $members++
    }
    $members -gt 0
}

# The area a fix belongs to: the folder under src/FlareAI.Core/ or src/FlareAI.Flare/ it changed the most lines in
# (for example Style or UI). A file directly in a project folder counts as Core or Flare. $null when it changed neither.
function Get-FixArea($numstat) {
    $by = @{}
    foreach ($s in @($numstat | Where-Object { $_ })) {
        $p = $s -split "`t"; $path = $p[-1]
        if ($path -notmatch '^src/FlareAI\.(Core|Flare)/(.+)$') { continue }
        $rest = $Matches[2]; $area = if ($rest.Contains("/")) { $rest.Split("/")[0] } else { $Matches[1] }
        $by[$area] = [int]$by[$area] + [int]($p[0] -replace '-', '0') + [int]($p[1] -replace '-', '0')
    }
    if (-not $by.Count) { return $null }
    @($by.GetEnumerator() | Sort-Object @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key } })[0].Key
}

# Facts about a fix, read from the diff. $numstat: git diff --numstat lines. $diff: git diff -U0 text. The test that
# reproduces the report (the files under tests/ whose added lines name it) doesn't count toward size or behavior.
function Get-DiffFacts($numstat, [string]$diff, [string]$reproTest, $rules, [string]$ownerOnly = $OwnerOnly) {
    $files = Split-Diff $diff
    $method = if ($reproTest) { ($reproTest -split '\.')[-1] } else { "" }
    $testFiles = @(foreach ($p in $files.Keys) {
        if ($method -and $p -like "tests/*" -and @($files[$p].added | Where-Object { $_ -cmatch "\b$([regex]::Escape($method))\b" }).Count) { $p }
    })
    $lines = 0; $count = 0; $paths = @()
    foreach ($s in @($numstat | Where-Object { $_ })) {
        $p = $s -split "`t"; $path = $p[-1]; $paths += $path
        if ($testFiles -contains $path) { continue }
        $count++; $lines += [int]($p[0] -replace '-', '0') + [int]($p[1] -replace '-', '0')
    }
    $behavior = @($paths | Where-Object { $path = $_; $testFiles -notcontains $path -and @($rules.behaviorPaths | Where-Object { $path -match $_ }).Count })
    foreach ($p in $files.Keys) {
        $ui = @($rules.uiStringPaths | Where-Object { $p -match $_ }).Count
        if (-not $ui -or $behavior -contains $p -or $testFiles -contains $p) { continue }
        $changed = @($files[$p].added + $files[$p].removed | Where-Object { $_ -notmatch '^\s*//' -and $_ -match '"[^"]*\p{L}[^"]*"' })
        if ($changed.Count) { $behavior += "$p (UI text)" }
    }
    $owner = @($paths | Where-Object { $ownerOnly -and $_ -match $ownerOnly })
    # Defining or redefining the Git class or Git.Untrusted anywhere is owner-only too, wherever it moves.
    foreach ($p in $files.Keys) {
        if ($owner -contains $p) { continue }
        if (@($files[$p].added + $files[$p].removed | Where-Object { $_ -match '\bclass\s+Git\b|\bUntrusted\s*=' }).Count) { $owner += "$p (Git.Untrusted)" }
    }
    [pscustomobject]@{
        lines = $lines; files = $count; test_files = $testFiles; repro_in_diff = [bool]$testFiles.Count
        behavior = @($behavior | Sort-Object -Unique)
        owner_only = @($owner)
        tamper = @(Get-TestTamper $files)
        area = Get-FixArea $numstat
        paths = @($paths)
        uncovered = @(Get-UncoveredPaths $paths $files $rules)
    }
}

# ==== The auto-fix path rule (factory-runtime spec section 3, step 14) ===============================================
# Auto-fix merges only C# under src/ and tests/ (autoFix.paths in rules/routing.json), plus the fix's own release note
# under changes/ (autoFix.dataPaths), which nightly.yml reads only as data. Every other path sends the fix for review.
# A note still does when it holds a URL, runs over 20 lines or 1,000 characters, or isn't a new file the fix adds,
# because it ships to every writer's What's new. Rules without the lists cover nothing, so the rule fails closed.
function Test-NoteForAutoFix($file) {
    if (-not $file -or $file.status -ne "A") { return $false }
    $text = @($file.added) -join "`n"
    if (@($file.added).Count -gt 20 -or $text.Length -gt 1000) { return $false }
    -not ($text -match '(?i)\b[a-z][a-z0-9+.-]*://|\bwww\.')
}

# The changed paths auto-fix doesn't cover. $files: Split-Diff output, for the notes' text.
function Get-UncoveredPaths($paths, $files, $rules) {
    $code = @($rules.autoFix.paths | Where-Object { $_ }); $data = @($rules.autoFix.dataPaths | Where-Object { $_ })
    @(foreach ($p in @($paths | Where-Object { $_ })) {
        if (@($code | Where-Object { $p -cmatch $_ }).Count) { continue }
        if (@($data | Where-Object { $p -cmatch $_ }).Count -and $files -and (Test-NoteForAutoFix $files[$p])) { continue }
        $p
    })
}

# What Get-Route reads: the facts' uncovered list, plus every path in the facts' paths list that matches neither list
# in $rules, so a record missing uncovered still fails closed. A built fix with no paths list goes for review too.
function Get-RouteUncovered($fix, $rules) {
    $paths = @($fix.paths | Where-Object { $_ })
    if (-not $paths.Count) { return @("its changed paths weren't listed") }
    $code = @($rules.autoFix.paths | Where-Object { $_ }); $data = @($rules.autoFix.dataPaths | Where-Object { $_ })
    $out = @($fix.uncovered | Where-Object { $_ })
    foreach ($p in $paths) {
        if (@(@($code) + @($data) | Where-Object { $p -cmatch $_ }).Count) { continue }
        if ($out -notcontains $p) { $out += $p }
    }
    $out
}

# Reads the TRX files of a filtered test run and says whether the named test behaved as $expect ("fail" on the base
# commit, with an assertion; "pass" on the fix). $null when it did, else why not. The name matches exactly, or with a
# theory's arguments after it, never as a substring of another test's name.
function Test-ReproResult($trx, [string]$name, [string]$expect) {
    $where = if ($expect -eq "fail") { "the base commit" } else { "the fix" }
    $results = @(foreach ($t in @($trx | Where-Object { $_ })) {
        $x = try { ConvertFrom-TrxText $t } catch { $null }   # agent-written: DTDs off, no resolver; one that won't parse is skipped
        if (-not $x) { continue }
        @($x.TestRun.Results.UnitTestResult) | Where-Object { $_ -and (("" + $_.testName) -ceq $name -or ("" + $_.testName).StartsWith($name + "(", [StringComparison]::Ordinal)) }
    })
    if (-not $results.Count) { return "the test $name didn't run on $where (it may not build there)" }
    if ($expect -eq "pass") {
        $bad = @($results | Where-Object { $_.outcome -ne "Passed" })
        if ($bad.Count) { return "the test $name doesn't pass on the fix" }
        return $null
    }
    $failed = @($results | Where-Object { $_.outcome -eq "Failed" })
    if (-not $failed.Count) { return "the test $name passes on the base commit, so it doesn't show the bug" }
    $assert = @($failed | Where-Object {
        $e = "" + $_.Output.ErrorInfo.Message + "`n" + $_.Output.ErrorInfo.StackTrace
        $e -match 'Xunit\.Sdk\.\w+Exception|Assert\.\w+\(\) Failure|at Xunit\.Assert\.|AssertionException|AssertFailedException'
    })
    if (-not $assert.Count) { return "the test $name fails on the base commit without an assertion failing" }
    $null
}

# What we would publish for a route, built from the brief by the script. Checked with Test-OurWords before use.
function Get-PublicText($route, $brief, [int]$n) {
    $summary = Format-Field $brief.summary 600; $impact = Format-Field $brief.impact 400; $goal = Format-Field $brief.goal 300
    $texts = @{}
    # One evidence comment per issue that covers an ask. It names the tester's ask, and for an issue that covers it only
    # partly, what the issue doesn't cover yet, so closing the report loses nothing.
    $all = @($brief.asks | Where-Object { $_ })
    foreach ($p in @($route.evidence)) {
        $mine = @(@($p.asks) | Where-Object { [int]$_ -ge 0 -and [int]$_ -lt $all.Count } | ForEach-Object { $all[[int]$_] })
        if ($mine.Count) {
            $lead = if (@($mine | Where-Object { $_.kind -ne "friction" }).Count) { "Another tester asked for this." } else { "Another tester ran into this." }
            $lines = @(foreach ($a in $mine) {
                $lack = if ($a.covers -ne "partial") { "" }
                        elseif (("" + $a.missing).Trim()) { " **Not covered here yet.** $(Format-Field $a.missing 300)" }
                        else { " **Not covered here yet.** This issue doesn't say this yet." }
                "- $(Format-Field $a.ask 300)$lack"
            })
            # The report's impact belongs to the whole report, so it goes with the evidence only when this is all of it.
            $imp = if ($all.Count -le 1) { "`n`n$impact" } else { "" }
            $texts["evidence on #$($p.issue)"] = "**Evidence from testers**`n`n$lead`n$($lines -join "`n")$imp`n`nFrom tester report $n, in the private reports repo."
        }
        else { $texts["evidence on #$($p.issue)"] = "**Evidence from testers**`n`nAnother tester ran into this. $summary`n`n$impact`n`nFrom tester report $n, in the private reports repo." }
    }
    if (@("plan", "docs-gap") -contains $route.route -or @($route.plan).Count) {
        # The new issue lists only the asks no open issue covers: the route's plan asks, or for a report without asks,
        # its summary alone.
        $planned = @(@($route.plan) | Where-Object { [int]$_ -lt $all.Count } | ForEach-Object { $all[[int]$_] })
        if ($route.route -eq "docs-gap" -and -not $planned.Count) {
            $taken = @($route.evidence | ForEach-Object { @($_.asks) } | ForEach-Object { [int]$_ })
            $planned = @(for ($i = 0; $i -lt $all.Count -and $i -lt 4; $i++) { if ($taken -notcontains $i) { $all[$i] } })
        }
        $title = if ($planned.Count) { Format-Field $planned[0].ask 90 } else { Format-Field (($brief.goal -split '(?<=[.!?])\s')[0]) 90 }
        $title = $title.TrimEnd(".")
        $lead = if ($route.route -eq "docs-gap") { "A tester couldn't find how to do this in the docs or the UI." } else { "A tester asked for this." }
        $askText = if ($planned.Count) { "`n`n**What this issue is for.**`n$(@($planned | ForEach-Object { '- ' + (Format-Field $_.ask 300) }) -join "`n")" } else { "" }
        $texts["issue title"] = $title
        # A split report's issue is built from its own asks alone: the summary, goal and impact describe the whole
        # report, asks that went elsewhere included.
        $split = @($route.evidence).Count -or ($planned.Count -and $planned.Count -lt $all.Count)
        $texts["issue body"] = if ($split) { "$lead$askText`n`nFrom tester report $n, in the private reports repo." }
                               else { "$lead $summary$askText`n`n**What they were trying to do.** $goal`n`n**Impact on writers.** $impact`n`nFrom tester report $n, in the private reports repo." }
    }
    $texts
}

$RouteNames = @{ "hold" = "Hold"; "evidence" = "Evidence"; "auto-fix" = "Auto-fix"; "fix-for-review" = "Fix for review"
                 "brief-only" = "Brief only"; "plan" = "Plan"; "docs-gap" = "Docs gap"; "pile" = "Pile" }

# The route comment on the private report. The JSON record is what the triage skill logs when the owner decides.
function Format-RouteComment($route, $in, [string]$mode, [string]$next) {
    $name = $RouteNames[$route.route]
    $head = "**Route:** $name$(if ($route.regression) { ", as a regression of #$($route.regression)" })."
    if ($mode -ne "live") { $head += " Shadow mode, so nothing public happened and you still decide this one." }
    $record = [ordered]@{ route = $route.route; row = $route.row; mode = $mode; reasons = @($route.reasons); input = $in }
    $json = $record | ConvertTo-Json -Depth 6 -Compress
    @($head, "", "**Why.** $((@($route.reasons) | ForEach-Object { $_.Substring(0,1).ToUpperInvariant() + $_.Substring(1) }) -join '. ').",
      "", "**Next.** $next", "", "<details><summary>Routing record</summary>", "", '```json', $json, '```', "", "</details>") -join "`n"
}

# ==== Lane suspension (command center spec section 4.6) =============================================================
# A reverted auto-fix suspends auto-fix for its area. Reverts are read from main's history: GitHub's Revert button, and
# git revert -m 1, both write "This reverts commit <merge commit>". Reinstating is the owner's call: an area listed in
# rules/routing.json "reinstated" with a date counts only reverts made after that date.

# Commits that main's history reverted. $log: git log --format="%x1e%cI%x1f%B" text. Returns sha -> revert date.
function Get-RevertedShas([string]$log) {
    $out = @{}
    foreach ($entry in ($log -split [char]0x1e)) {
        $parts = $entry -split [char]0x1f, 2
        if ($parts.Count -lt 2) { continue }
        foreach ($m in [regex]::Matches($parts[1], 'This reverts commit ([0-9a-f]{40})')) { $out[$m.Groups[1].Value] = ("" + $parts[0]).Trim() }
    }
    $out
}

# Merged auto-fix PRs whose merge commit was reverted, with their area. $prs: gh pr list --json
# number,mergeCommit,mergedAt,files (files: path, additions, deletions).
function Get-RevertedFixes($prs, $reverted) {
    foreach ($pr in @($prs | Where-Object { $_ -and $_.mergeCommit })) {
        $sha = "" + $pr.mergeCommit.oid
        if (-not $reverted.ContainsKey($sha)) { continue }
        $numstat = @($pr.files | ForEach-Object { "$($_.additions)`t$($_.deletions)`t$($_.path)" })
        [pscustomobject]@{ number = [int]$pr.number; area = (Get-FixArea $numstat); mergedAt = "" + $pr.mergedAt; revertedAt = $reverted[$sha] }
    }
}

# The areas where auto-fix is suspended: those rules/routing.json lists, and those of reverted auto-fixes, unless the
# owner reinstated the area after the revert.
function Get-SuspendedAreas($rules, $revertedFixes) {
    $areas = @($rules.suspended | Where-Object { $_ })
    foreach ($f in @($revertedFixes | Where-Object { $_ -and $_.area })) {
        $since = $null; if ($rules.reinstated) { $since = $rules.reinstated.($f.area) }
        if ($since -and ([datetime]$f.revertedAt) -le ([datetime]$since)) { continue }
        if ($areas -notcontains $f.area) { $areas += $f.area }
    }
    @($areas)
}

# ==== Rejection reason codes (command center spec section 4.5) =======================================================
# A fixed list, so the scorecard can count why work didn't go through without quoting anything. Each code matches the
# reasons Get-Route gives and the messages the factory posts when a run stops.
$ReasonCodes = [ordered]@{
    "security"         = 'describes a vulnerability'
    "owner-only"       = 'owner-only code|code the owner reviews'
    "secret-shape"     = 'shaped like a key or token'
    "our-words"        = 'our-words check|shares a run of|email address|UNC\) path|user-profile path'
    "not-owner"        = 'account other than the owner'
    "not-reproduced"   = "wasn't reproduced"
    "low-confidence"   = '^confidence is'
    "no-repro-test"    = 'names no test|fix doesn''t add the test'
    "repro-failed"     = 'passes on the base commit|didn''t run on|without an assertion|doesn''t pass on the fix'
    "too-big"          = 'changes \d+ lines, over|changes \d+ files, over|too big for the factory'
    "behavior-change"  = 'may change behavior writers see'
    "overlap"          = 'another open factory PR changes the same files'
    "test-tamper"      = 'changes existing tests or protected test files'
    "suspended-area"   = 'auto-fix is suspended for the'
    "daily-cap"        = 'auto-fixes are used up'
    "run-cap"          = 'agent runs are used up|ran past its|used all its turns'
    "unit-tests"       = 'Unit tests fail on the fix'
    "usage-limit"      = 'usage limit was reached'
    "agent-output"     = 'output on the agents VM'
    "flare-result"     = 'Flare check''s result couldn''t be read'
    "holdout"          = 'holdout scenario'
    "e2e"              = 'end-to-end checks (in Flare )?fail|end-to-end run stopped'
    "vm"               = '(VM|host) rather than the fix'
    "auto-fix-paths"   = 'files auto-fix doesn''t cover'
    "saved-fix-stale"  = 'saved fix no longer applies'
    "ci"               = 'Required checks'
    "review-blocked"   = 'Review blocked the fix'
    "disallowed-files" = 'files the factory may not touch|file modes or adds symlinks|binary files'
    "cannot-fix"       = "couldn't fix this"
    # Not rejections: they describe the route. The scorecard leaves them out.
    "regression"       = 'looks like a regression of closed issue'
    "passed"           = 'every auto-fix check passed|reproduced with high confidence'
}

# The codes for a list of reasons, in the reasons' order, each once. A reason no code matches counts as "other".
function Get-ReasonCodes($reasons) {
    $codes = @()
    foreach ($r in @($reasons | Where-Object { $_ })) {
        $hit = $null
        foreach ($k in $ReasonCodes.Keys) { if (("" + $r) -match $ReasonCodes[$k]) { $hit = $k; break } }
        if (-not $hit) { $hit = "other" }
        if ($codes -notcontains $hit) { $codes += $hit }
    }
    $codes
}

# Windows PowerShell 5.1 mangles double quotes in arguments to native programs, so any text sent to gh as --body can
# break the command (a JSON routing record did). Every body goes through a temp file and --body-file instead.
function New-BodyFile([string]$Text) {
    $path = [IO.Path]::GetTempFileName()
    [IO.File]::WriteAllText($path, $Text, (New-Object Text.UTF8Encoding($false)))
    $path
}

Export-ModuleMember -Function New-BodyFile, Test-TriageBrief, Format-TriageComment, Get-LabelActor, Format-Field, Select-Report, Test-FixGate, Assert-FactoryLabel, Get-TriageFailures, Get-Labels,
    Test-TestName, Test-CuratedIssue, Set-CuratedAuthors, Get-IssueArea, Get-PlanArea, Resolve-Matches, New-RouteInput, ConvertTo-RouteAsks, New-CoverInput, Get-CoverVote, Get-RouteAsks, Get-Route, Get-WaitingReason, Test-FixAllowed, Test-OurWords, Get-DiffFacts,
    Test-ReproResult, Get-PublicText, Format-RouteComment, Split-Diff, Get-AddedText, Get-TriageLabel, Get-WipWaiting, Get-TestTamper, Get-FixArea,
    Get-RevertedShas, Get-RevertedFixes, Get-SuspendedAreas, Get-ReasonCodes, Test-SecretShape, Test-WholeTests,
    Get-UncoveredPaths, Get-RouteUncovered, Test-NoteForAutoFix, Get-DecisionFixSince -Variable Routes, WaitingReasons, OwnerOnly, BlockedCauses, TestProtected, ReasonCodes
