# What repro-must-fail.ps1 decides, as plain functions so tests/agent can check each rule.
Set-StrictMode -Version 2

$script:DotnetChannels = @('rule-fixtures', 'core', 'scenarios', 'flare-tests')

function Test-ReproChannel([string]$Channel) { return ($Channel -in ($script:DotnetChannels + 'pester')) }

function Get-TestShortName([string]$Test, [string]$Channel) {
    # The text that must appear on an added line of the diff: the method name for .NET, the It name for Pester.
    if ($Channel -eq 'pester') { return $Test }
    return ($Test -split '\.')[-1]
}

function Get-XunitFailureKind([string]$Message) {
    # xunit prints "Namespace.SomeException : message" for an exception the test didn't expect, and the assertion's
    # own message (Assert.Equal() Failure, or the message passed to Assert.True) for an assertion.
    if ($Message -match '^\s*(?!Xunit\.)[\w.`+]+(Exception|Error)\s*:') { return 'exception' }
    return 'assertion'
}

function Test-BuildError([string[]]$Output) {
    return [bool](($Output -join "`n") -match 'error CS\d{4}|error MSB\d{4}|Build FAILED|error NU\d{4}')
}

function Read-TrxOutcome {
    # Outcome of one test in a TRX file: passed, assertion, exception or notfound. Theory rows count together: any
    # exception wins over an assertion, any failure wins over a pass.
    param([Parameter(Mandatory)][string]$TrxPath, [Parameter(Mandatory)][string]$Test)
    [xml]$x = Get-Content -Raw -Encoding UTF8 $TrxPath
    $ns = New-Object System.Xml.XmlNamespaceManager $x.NameTable
    $ns.AddNamespace('t', $x.DocumentElement.NamespaceURI)
    $short = ($Test -split '\.')[-1]
    $rows = @($x.SelectNodes('//t:UnitTestResult', $ns) | Where-Object {
        $n = ($_.testName -replace '\(.*$', '')
        $n -eq $Test -or $n.EndsWith(".$Test") -or ($Test -notmatch '\.' -and $n.EndsWith(".$short"))
    })
    if (-not $rows.Count) { return [pscustomobject]@{ Outcome = 'notfound'; Message = "No test named $Test ran." } }
    $failed = @($rows | Where-Object outcome -eq 'Failed')
    if (-not $failed.Count) {
        if (@($rows | Where-Object outcome -eq 'Passed').Count) { return [pscustomobject]@{ Outcome = 'passed'; Message = '' } }
        return [pscustomobject]@{ Outcome = 'notfound'; Message = "$Test didn't run (skipped or not executed)." }
    }
    $kinds = foreach ($f in $failed) {
        $m = $f.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
        $msg = if ($m) { $m.InnerText } else { '' }
        [pscustomobject]@{ Kind = (Get-XunitFailureKind $msg); Message = $msg }
    }
    $ex = @($kinds | Where-Object Kind -eq 'exception')
    $pick = if ($ex.Count) { $ex[0] } else { $kinds[0] }
    return [pscustomobject]@{ Outcome = $pick.Kind; Message = (($pick.Message -split "`r?`n")[0]) }
}

function Read-PesterOutcome {
    # Outcome of one It block in the pester.json that test-channel writes. Only a Should failure is an assertion.
    param([Parameter(Mandatory)][string]$JsonPath, [Parameter(Mandatory)][string]$Test)
    $rows = @(Get-Content -Raw -Encoding UTF8 $JsonPath | ConvertFrom-Json) | ForEach-Object { $_ }
    $mine = @($rows | Where-Object { $_.name -eq $Test })
    if (-not $mine.Count) { return [pscustomobject]@{ Outcome = 'notfound'; Message = "No test named '$Test' ran." } }
    $failed = @($mine | Where-Object { -not $_.passed })
    if (-not $failed.Count) { return [pscustomobject]@{ Outcome = 'passed'; Message = '' } }
    $ex = @($failed | Where-Object { $_.errorId -ne 'PesterAssertionFailed' })
    $pick = if ($ex.Count) { $ex[0] } else { $failed[0] }
    $kind = if ($ex.Count) { 'exception' } else { 'assertion' }
    $msg = if ($pick.message) { (($pick.message -split "`r?`n")[0]) } else { '' }
    return [pscustomobject]@{ Outcome = $kind; Message = $msg }
}

function Get-RunOutcome {
    # Turns one run (exit code, output, result file) into an outcome: passed, assertion, exception, build-error,
    # notfound or no-result.
    param([string]$Channel, [string]$Test, [int]$ExitCode, [string[]]$Output = @(), [string]$ResultFile)
    if ($ResultFile -and (Test-Path $ResultFile)) {
        if ($Channel -eq 'pester') { return Read-PesterOutcome -JsonPath $ResultFile -Test $Test }
        return Read-TrxOutcome -TrxPath $ResultFile -Test $Test
    }
    if (Test-BuildError $Output) {
        $first = @($Output | Where-Object { $_ -match 'error (CS|MSB|NU)\d{4}' } | Select-Object -First 1)
        $msg = 'The build failed.'
        if ($first.Count -and $first[0] -match '(error (CS|MSB|NU)\d{4}:[^\[]*)') { $msg = $Matches[1].Trim() }
        return [pscustomobject]@{ Outcome = 'build-error'; Message = $msg }
    }
    return [pscustomobject]@{ Outcome = 'no-result'; Message = "The run ended with exit code $ExitCode and wrote no results." }
}

function Get-ReproVerdict {
    # Pass only when the test fails on base with an assertion and passes on head.
    param([Parameter(Mandatory)]$Base, [Parameter(Mandatory)]$Head, [string]$Test)
    $why = switch ($Base.Outcome) {
        'assertion'   { $null }
        'passed'      { "$Test passes on base, so it doesn't show the bug." }
        'exception'   { "$Test fails on base with an exception, not an assertion ($($Base.Message)). Make it fail on the behavior." }
        'build-error' { "The tests don't build on base ($($Base.Message)), so they don't show the bug. Write the test against code that already exists on base." }
        'notfound'    { "$Test didn't run on base. $($Base.Message)" }
        default       { "No result on base. $($Base.Message)" }
    }
    if (-not $why) {
        $why = switch ($Head.Outcome) {
            'passed'      { $null }
            'build-error' { "The tests don't build on head ($($Head.Message))." }
            'notfound'    { "$Test didn't run on head. $($Head.Message)" }
            'no-result'   { "No result on head. $($Head.Message)" }
            default       { "$Test still fails on head ($($Head.Message))." }
        }
    }
    if ($why) { return [pscustomobject]@{ Pass = $false; Reason = $why } }
    return [pscustomobject]@{ Pass = $true; Reason = "$Test fails on base with an assertion and passes on head." }
}

function Find-TestInDiff {
    # Files under tests/ whose added lines in the unified diff name the test. For Pester the name must be an It
    # block's name (It "name" or It 'name'), not a Describe or text that happens to contain it.
    param([string[]]$DiffLines, [Parameter(Mandatory)][string]$Name, [switch]$Pester)
    $files = New-Object System.Collections.Generic.List[string]
    $current = $null
    $itRx = '(?i)(^|[\s;{])It\s+(-Name\s+)?["'']' + [regex]::Escape($Name) + '["'']'
    foreach ($l in $DiffLines) {
        if ($l -match '^\+\+\+ b/(.+)$') { $current = $Matches[1]; continue }
        $named = if ($Pester) { $l -match $itRx } else { $l.Contains($Name) }
        if ($current -and $current -like 'tests/*' -and $l.StartsWith('+') -and -not $l.StartsWith('+++') -and $named) {
            if (-not $files.Contains($current)) { $files.Add($current) }
        }
    }
    return $files.ToArray()
}

Export-ModuleMember -Function Test-ReproChannel, Get-TestShortName, Get-XunitFailureKind, Test-BuildError, Read-TrxOutcome,
    Read-PesterOutcome, Get-RunOutcome, Get-ReproVerdict, Find-TestInDiff
