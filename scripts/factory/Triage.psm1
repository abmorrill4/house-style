# Helpers for the factory's triage step: check the triage agent's brief, turn it into the comment on the private
# report, and check who approved a fix. Kept out of Invoke-Factory.ps1 so tests/factory can test them without a VM.

$Kinds = "bug", "friction", "feature", "question", "duplicate", "noise"

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

# Which report this run works on, and in which step. Triage first (new reports, and failed triage up for a retry), then
# one fix the owner approved. Only reports the feedback service filed ($owner, label problem or error) are considered.
# A report queued because the day's auto-fix cap was full is triaged and routed again once $capLeft allows. route:* and
# waiting:* labels are for display: they neither block triage nor approve anything.
# Returns @{ Report; Mode } or $null.
function Select-Report($all, [string]$owner, [int]$issue = 0, [int]$capLeft = 0) {
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
    if ($capLeft -gt 0) {
        foreach ($r in $ours) {
            $l = Get-Labels $r
            if ($l -contains "factory:queued" -and -not ($l | Where-Object { $_ -like "decision:*" -or ($_ -like "factory:*" -and $_ -ne "factory:queued") })) { return @{ Report = $r; Mode = "triage" } }
        }
    }
    foreach ($r in $ours) {
        $l = Get-Labels $r
        $decisions = @($l | Where-Object { $_ -like "decision:*" })
        # decision:fix alone: a report with a second decision label waits for the owner and never blocks the queue.
        if ($decisions.Count -eq 1 -and $decisions[0] -eq "decision:fix" -and -not ($l | Where-Object { $_ -like "factory:*" })) { return @{ Report = $r; Mode = "fix" } }
    }
    $null
}

# Why a report may not be fixed yet, or $null when it may: exactly one decision label, decision:fix, and the last
# decision label added was decision:fix (so a later park or decline that someone forgot to clean up wins).
function Test-FixGate($labels, $events) {
    $decisions = @($labels | Where-Object { $_ -like "decision:*" })
    if ($decisions.Count -ne 1 -or $decisions[0] -ne "decision:fix") { return "it has decision labels $($decisions -join ', '), not decision:fix alone" }
    $last = @($events | Where-Object { $_.event -eq "labeled" -and $_.label.name -like "decision:*" }) | Select-Object -Last 1
    if (-not $last -or $last.label.name -ne "decision:fix") { return "the last decision label added was $(if ($last) { $last.label.name } else { 'none' }), not decision:fix" }
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
function Assert-FactoryLabel([string]$label) {
    if ($label -and $label -notlike "triage:*" -and $label -notlike "factory:*" -and $label -notlike "route:*" -and $label -notlike "waiting:*") { throw "The factory may not set the label '$label'." }
}

# ==== Routing (work/report-pipeline/spec.md section 2) ==============================================================
# The brief only proposes. Get-Route decides from a record the script builds: the brief's enums, its matches resolved
# against issue lists the script fetched, and, once a fix is built, what the script itself found in the diff and the
# test runs. The record holds no report text, so it can be posted on the report and logged.

$Routes = "hold", "evidence", "auto-fix", "fix-for-review", "brief-only", "plan", "docs-gap", "pile"
# Why the owner is needed (spec section 3). One label per waiting item.
$WaitingReasons = "want", "plan-input", "plan-review", "merge", "security", "stuck", "rule-change", "decide"

# A public issue we curate: open, and labeled theme or from:*. Only these get evidence comments.
function Test-CuratedIssue($issue) {
    if (-not $issue -or ("" + $issue.state).ToUpperInvariant() -ne "OPEN") { return $false }
    [bool](@(Get-Labels $issue) | Where-Object { $_ -eq "theme" -or $_ -like "from:*" })
}

# The brief's matches, checked against what the script fetched. A ref the lists don't hold is "missing" and counts
# for nothing. $issues: public issues (number, state, labels). $reports: the private reports. $self: this report.
function Resolve-Matches($briefMatches, $issues, $reports, [int]$self) {
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
            [pscustomobject]@{ relation = $rel; target = "report"; number = $num; state = $state; curated = $false; theme = $false }
        }
        elseif ($ref -match '^#(\d{1,6})$') {
            $num = [int]$Matches[1]; $i = $byIssue[$num]
            $state = if ($i) { ("" + $i.state).ToLowerInvariant() } else { "missing" }
            [pscustomobject]@{ relation = $rel; target = "issue"; number = $num; state = $state; curated = (Test-CuratedIssue $i); theme = [bool]($i -and (@(Get-Labels $i) -contains "theme")) }
        }
    }
}

# The Get-Route input record, before any fix is built.
function New-RouteInput($brief, $resolved, [int]$capLeft) {
    [pscustomobject]@{
        kind = "" + $brief.kind; security = ($brief.security -eq $true); reproduced = "" + $brief.reproduced.result
        confidence = "" + $brief.confidence
        repro_test = $(if ($brief.repro_test -and (Test-TestName $brief.repro_test)) { "" + $brief.repro_test } else { "" })
        docs_gap = ($brief.docs_gap -eq $true)
        matches = @($resolved)
        our_words = $null   # not checked yet; $true or $false once the script has checked the text it would publish
        fix = $null         # set once a fix is built: repro, lines, files, behavior, owner_only, overlap
        cap_left = $capLeft
    }
}

# Takes the first matching row of the matrix. $rules is rules/routing.json. Returns route, row, reasons, waiting (the
# reason the owner is needed, or $null), regression (a closed issue number), target (where evidence goes), theme (for a
# plan), build (the fix step may run now) and queued (over the daily cap).
function Get-Route($in, $rules) {
    $out = [ordered]@{ route = ""; row = 0; reasons = @(); waiting = $null; regression = $null; target = $null; theme = $null; build = $false; queued = $false }
    $ms = @($in.matches | Where-Object { $_ })
    $fix = $in.fix

    # 1. hold: nothing public
    $hold = @()
    if ($in.security -eq $true) { $hold += "the brief says it describes a vulnerability" }
    if ($fix -and @($fix.owner_only | Where-Object { $_ }).Count) { $hold += "the fix touches owner-only code ($(@($fix.owner_only) -join ', '))" }
    if ($in.our_words -eq $false) { $hold += "text we would publish failed the our-words check" }
    if ($hold.Count) { $out.route = "hold"; $out.row = 1; $out.reasons = $hold; $out.waiting = "security"; return [pscustomobject]$out }

    # 2. evidence: an open issue we curate, or another open report
    $ev = @($ms | Where-Object { $_.target -eq "issue" -and $_.state -eq "open" -and $_.curated -and @("same", "part-of") -contains $_.relation } |
            Sort-Object @{ Expression = { if ($_.relation -eq "same") { 0 } else { 1 } } })
    if ($ev.Count) {
        $out.route = "evidence"; $out.row = 2; $out.target = [pscustomobject]@{ type = "issue"; number = $ev[0].number }
        $out.reasons = @("it is $($ev[0].relation) as open issue #$($ev[0].number)"); return [pscustomobject]$out
    }
    if ($in.kind -eq "duplicate") {
        $dup = @($ms | Where-Object { $_.target -eq "report" -and $_.state -eq "open" -and $_.relation -eq "same" })
        if ($dup.Count) {
            $out.route = "evidence"; $out.row = 2; $out.target = [pscustomobject]@{ type = "report"; number = $dup[0].number }
            $out.reasons = @("it duplicates open report #$($dup[0].number)"); return [pscustomobject]$out
        }
    }

    # 3. regression: the same as a closed issue, handled as a bug
    $reg = @($ms | Where-Object { $_.target -eq "issue" -and $_.state -eq "closed" -and $_.relation -eq "same" })
    if ($reg.Count) { $out.regression = $reg[0].number }

    if ($in.kind -eq "bug" -or $out.regression) {
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

    # 7. plan
    if (@("feature", "friction") -contains $in.kind) {
        $out.route = "plan"; $out.row = 7; $out.reasons = @("it asks for new or changed behavior")
        $th = @($ms | Where-Object { $_.target -eq "issue" -and $_.state -eq "open" -and $_.theme })
        if ($th.Count) { $out.theme = $th[0].number }
        return [pscustomobject]$out
    }
    # 8. docs-gap
    if ($in.kind -eq "question" -and $in.docs_gap -eq $true) { $out.route = "docs-gap"; $out.row = 8; $out.reasons = @("the docs or UI copy don't answer it"); return [pscustomobject]$out }
    # 9. pile
    if ($in.kind -eq "noise" -or $in.kind -eq "question") {
        $out.route = "pile"; $out.row = 9
        $out.reasons = @($(if ($in.kind -eq "noise") { "nothing to act on" } else { "a question the docs already answer" })); return [pscustomobject]$out
    }
    # Anything else (a duplicate of something that isn't open) goes to the owner with its brief.
    $out.route = "brief-only"; $out.row = 6; $out.waiting = "decide"; $out.reasons = @("it is a duplicate of nothing open we can link")
    [pscustomobject]$out
}

# The waiting reason to label. In shadow mode the owner still decides every report the old way, except the pile.
function Get-WaitingReason($route, [string]$mode) {
    if ($route.route -eq "hold") { return "security" }
    if ($mode -ne "live") { if ($route.route -eq "pile") { return $null } else { return "decide" } }
    $route.waiting
}

# The fix step runs only for a route this run computed (passed in, never read from a label) or the owner's
# decision:fix. A route:* label grants nothing. Returns $null when allowed, else why not.
function Test-FixAllowed($route, $labels, $events) {
    if ($route -and $route.build -and @("auto-fix", "fix-for-review") -contains $route.route) { return $null }
    Test-FixGate $labels $events
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
    }
    $problems
}

# Facts about a fix, read from the diff. $numstat: git diff --numstat lines. $diff: git diff -U0 text. The test that
# reproduces the report (the files under tests/ whose added lines name it) doesn't count toward size.
function Get-DiffFacts($numstat, [string]$diff, [string]$reproTest, $rules, [string]$ownerOnly) {
    $files = @{}; $cur = $null
    foreach ($line in ($diff -split "`r?`n")) {
        if ($line -match '^diff --git a/.+ b/(.+)$') { $cur = $Matches[1]; $files[$cur] = @{ added = @(); removed = @() }; continue }
        if (-not $cur -or $line.StartsWith("+++") -or $line.StartsWith("---")) { continue }
        if ($line.StartsWith("+")) { $files[$cur].added += $line.Substring(1) } elseif ($line.StartsWith("-")) { $files[$cur].removed += $line.Substring(1) }
    }
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
    $behavior = @($paths | Where-Object { $path = $_; @($rules.behaviorPaths | Where-Object { $path -match $_ }).Count })
    foreach ($p in $files.Keys) {
        $ui = @($rules.uiStringPaths | Where-Object { $p -match $_ }).Count
        if (-not $ui -or $behavior -contains $p) { continue }
        $changed = @($files[$p].added + $files[$p].removed | Where-Object { $_ -notmatch '^\s*//' -and $_ -match '"[^"]*\p{L}[^"]*"' })
        if ($changed.Count) { $behavior += "$p (UI text)" }
    }
    [pscustomobject]@{
        lines = $lines; files = $count; test_files = $testFiles; repro_in_diff = [bool]$testFiles.Count
        behavior = @($behavior | Sort-Object -Unique)
        owner_only = @($paths | Where-Object { $ownerOnly -and $_ -match $ownerOnly })
    }
}

# Reads the TRX files of a filtered test run and says whether the named test behaved as $expect ("fail" on the base
# commit, with an assertion; "pass" on the fix). $null when it did, else why not.
function Test-ReproResult($trx, [string]$name, [string]$expect) {
    $where = if ($expect -eq "fail") { "the base commit" } else { "the fix" }
    $results = @(foreach ($t in @($trx | Where-Object { $_ })) {
        $x = [xml]$t
        @($x.TestRun.Results.UnitTestResult) | Where-Object { $_ -and ("" + $_.testName).Contains($name) }
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
    switch ($route.route) {
        "evidence" {
            if ($route.target.type -ne "issue") { return @{} }
            @{ "evidence comment" = "**Evidence from testers**`n`nAnother tester ran into this. $summary`n`n$impact`n`nFrom tester report $n, in the private reports repo." }
        }
        { $_ -eq "plan" -or $_ -eq "docs-gap" } {
            $title = Format-Field (($brief.goal -split '(?<=[.!?])\s')[0]) 90
            $title = $title.TrimEnd(".")
            $lead = if ($_ -eq "docs-gap") { "A tester couldn't find how to do this in the docs or the UI." } else { "A tester asked for this." }
            @{ "issue title" = $title
               "issue body" = "$lead $summary`n`n**What they were trying to do.** $goal`n`n**Impact on writers.** $impact`n`nFrom tester report $n, in the private reports repo." }
        }
        default { @{} }
    }
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

Export-ModuleMember -Function Test-TriageBrief, Format-TriageComment, Get-LabelActor, Format-Field, Select-Report, Test-FixGate, Assert-FactoryLabel, Get-TriageFailures, Get-Labels,
    Test-TestName, Test-CuratedIssue, Resolve-Matches, New-RouteInput, Get-Route, Get-WaitingReason, Test-FixAllowed, Test-OurWords, Get-DiffFacts,
    Test-ReproResult, Get-PublicText, Format-RouteComment -Variable Routes, WaitingReasons
