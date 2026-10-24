# The findings and verdict formats from the findings and skeptic-protocol skills, as checks.
Set-StrictMode -Version 2

$script:Lenses = @('adversarial', 'correctness', 'consistency', 'security', 'goals')

function Test-ConcretePlace([string]$Where) {
    # A file (path/name.ext, optionally :line or :line-line), a spec section, or an issue or PR number.
    if ($Where -match '[\w\-/\\.]+\.[A-Za-z0-9]{1,8}(:\d+(-\d+)?)?(\b|$)') { return $true }
    $sectionSign = [string][char]0xA7
    if ($Where -match ('(?i)\bsection\s+\S+|' + $sectionSign + '\s*\d')) { return $true }
    if ($Where -match '#\d+') { return $true }
    return $false
}

function Get-Prop($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value } else { return $null }
}

function ConvertFrom-JsonText([string]$Text) {
    try { return [pscustomobject]@{ Ok = $true; Value = ($Text | ConvertFrom-Json) } }
    catch { return [pscustomobject]@{ Ok = $false; Value = $null } }
}

function Test-Findings([string]$Text) {
    # Returns one line per problem; nothing when the findings are well formed.
    $j = ConvertFrom-JsonText $Text
    if (-not $j.Ok -or $null -eq $j.Value) { return @("It isn't valid JSON.") }
    $list = Get-Prop $j.Value 'findings'
    if ($null -eq $list -and -not ($j.Value.PSObject.Properties.Name -contains 'findings')) { return @("There's no findings list.") }
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    $i = 0
    foreach ($f in @($list)) {
        $i++
        $n = "Finding $i"
        if ($f -isnot [pscustomobject]) { $problems.Add("$n isn't an object."); continue }
        $sev = Get-Prop $f 'severity'; $title = Get-Prop $f 'title'; $where = Get-Prop $f 'where'; $detail = Get-Prop $f 'detail'; $lens = Get-Prop $f 'lens'
        if ($sev -notin @('blocking', 'note')) { $problems.Add("$n has severity '$sev'; use blocking or note.") }
        if ([string]::IsNullOrWhiteSpace($title)) { $problems.Add("$n has no title.") }
        elseif ($title.Length -gt 120 -or $title -match "`n") { $problems.Add("$n has a title longer than one line of 120 characters.") }
        if ([string]::IsNullOrWhiteSpace($where)) { $problems.Add("$n doesn't say where.") }
        elseif (-not (Test-ConcretePlace $where)) { $problems.Add("$n says where as '$where'; name a file and line, a spec section or an issue.") }
        if ([string]::IsNullOrWhiteSpace($detail)) { $problems.Add("$n has no detail.") }
        if ($lens -and $lens -notin $script:Lenses) { $problems.Add("$n names an unknown lens '$lens'.") }
        if ($title -and $where) {
            $key = "$title|$where".ToLowerInvariant()
            if ($seen.ContainsKey($key)) { $problems.Add("$n repeats finding $($seen[$key]).") } else { $seen[$key] = $i }
        }
    }
    return $problems.ToArray()
}

function Test-Verdict([string]$Text) {
    $j = ConvertFrom-JsonText $Text
    if (-not $j.Ok -or $null -eq $j.Value) { return @("It isn't valid JSON.") }
    $v = $j.Value
    $problems = New-Object System.Collections.Generic.List[string]
    if ((Get-Prop $v 'refuted') -isnot [bool]) { $problems.Add("refuted must be true or false.") }
    if ([string]::IsNullOrWhiteSpace((Get-Prop $v 'reason'))) { $problems.Add("There's no reason.") }
    $e = Get-Prop $v 'evidence'
    if ([string]::IsNullOrWhiteSpace($e)) { $problems.Add("There's no evidence.") }
    elseif ($e -match '(?i)^\s*(i )?read the code\.?\s*$') { $problems.Add("The evidence has to name what was checked: a file and line, a test and its result, or a command and its output.") }
    return $problems.ToArray()
}

Export-ModuleMember -Function Test-ConcretePlace, Test-Findings, Test-Verdict
