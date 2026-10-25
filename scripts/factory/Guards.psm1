# Checks the factory runs on text that crosses from code it doesn't trust: a fix's patch and commit messages (written
# by the agent user's job, or handed to the Flare VM), and the token search over anything the factory logs or posts.
# Shared by Invoke-Factory.ps1 (the script user), Start-AgentJob.ps1 (the agent user's launcher) and
# Invoke-FlareCheck.ps1 (the Flare VM), so each copy checks the same way. Windows PowerShell 5.1.

# ==== Patch rules =====================================================================================================
# PatchRules.psd1 as data: Allowed (one pattern), Denied (a list) and MaxLines. Read with Import-PowerShellDataFile,
# which runs nothing. $path is the copy to read: the run's base for the factory, the clone's main for the Flare check.
function Import-PatchRules([string]$path) {
    $d = Import-PowerShellDataFile -LiteralPath $path
    if (-not $d.Allowed -or -not @($d.Denied).Count -or -not [int]$d.MaxLines) { throw "PatchRules.psd1 is missing Allowed, Denied or MaxLines." }
    [pscustomobject]@{ Allowed = "" + $d.Allowed; Denied = @($d.Denied | ForEach-Object { "" + $_ }); MaxLines = [int]$d.MaxLines }
}

# Why a path may not be in a fix, or $null. Matching ignores case.
function Test-PatchPath([string]$path, $rules) {
    $ic = [Text.RegularExpressions.RegexOptions]::IgnoreCase
    if (-not $path) { return "an empty path" }
    if ($path.StartsWith("/") -or $path.Contains("..")) { return "$path (a leading / or ..)" }
    if ($path -match '[\x00-\x1f"\\]') { return "$path (a control character, quote or backslash)" }
    if (-not [regex]::IsMatch($path, $rules.Allowed, $ic)) { return "$path (outside the paths a fix may change)" }
    foreach ($d in $rules.Denied) { if ([regex]::IsMatch($path, $d, $ic)) { return "$path (a path no fix may change)" } }
    $null
}

# ==== The patch text ==================================================================================================
# A fix's patch, as the launcher writes it with exactly
#   git diff --no-ext-diff --no-textconv --no-renames --src-prefix=a/ --dst-prefix=b/ <base> HEAD
# Each file section is "diff --git a/<p> b/<p>" followed by exactly one of five header forms, then its hunks. Anything
# else is refused: other modes, mode changes, renames, copies, similarity, binary content, any other header line, a path
# outside the rules, and more than MaxLines added and removed lines. Hunks are read by their line counts, so a content
# line can never pass for a header. Returns ok, problems (why not) and files (path, form, added, removed).
$IndexLine = '^index [0-9a-f]{7,64}\.\.[0-9a-f]{7,64}( 100644)?$'
$HeaderForms = [ordered]@{
    "change"        = @('index', '--- a/P', '+++ b/P')
    "new"           = @('new file mode 100644', 'index', '--- /dev/null', '+++ b/P')
    "deleted"       = @('deleted file mode 100644', 'index', '--- a/P', '+++ /dev/null')
    "new-empty"     = @('new file mode 100644', 'index')
    "deleted-empty" = @('deleted file mode 100644', 'index')
}

function Test-PatchText([string]$patch, $rules) {
    $problems = New-Object System.Collections.Generic.List[string]
    $files = @()
    $lines = @($patch -split "`n")
    if ($lines.Count -and $lines[-1] -eq "") { $lines = @($lines | Select-Object -First ($lines.Count - 1)) }   # the final newline
    if (-not $lines.Count) { return [pscustomobject]@{ ok = $false; problems = @("the patch changes nothing"); files = @() } }
    $i = 0; $total = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        if (-not $line.StartsWith("diff --git a/")) { $problems.Add("line $($i + 1) isn't a file header the factory accepts"); break }
        $rest = $line.Substring(13)
        # "diff --git a/" + p + " b/" + p: the same path on both sides, never a rename.
        $p = $null
        if ($rest.Length -ge 4 -and ($rest.Length - 3) % 2 -eq 0) {
            $half = ($rest.Length - 3) / 2
            if ($rest.Substring($half, 3) -eq " b/" -and $rest.Substring(0, $half) -ceq $rest.Substring($half + 3)) { $p = $rest.Substring(0, $half) }
        }
        if (-not $p) { $problems.Add("line $($i + 1) names two different paths, or a quoted one"); break }
        $why = Test-PatchPath $p $rules
        if ($why) { $problems.Add($why) }
        $i++
        # The header: the longest form whose lines all match, then a hunk, the next file or the end.
        $form = $null
        foreach ($name in $HeaderForms.Keys) {
            $want = $HeaderForms[$name]
            if ($i + $want.Count -gt $lines.Count) { continue }
            $ok = $true
            for ($k = 0; $k -lt $want.Count; $k++) {
                $have = $lines[$i + $k]
                $match = if ($want[$k] -eq 'index') { $have -cmatch $IndexLine } else { $have -ceq $want[$k].Replace("P", $p) }
                if (-not $match) { $ok = $false; break }
            }
            if (-not $ok) { continue }
            $next = if ($i + $want.Count -lt $lines.Count) { $lines[$i + $want.Count] } else { $null }
            $empty = $name -like "*-empty"
            if ($empty -and $null -ne $next -and -not $next.StartsWith("diff --git ")) { continue }
            if (-not $empty -and ($null -eq $next -or -not $next.StartsWith("@@ "))) { continue }
            $form = $name; $i += $want.Count; break
        }
        if (-not $form) {
            # Name the header line that doesn't fit: one no form allows, else a mode other than 100644, else the first.
            $hdr = @(); for ($k = $i; $k -lt $lines.Count -and -not $lines[$k].StartsWith("@@ ") -and -not $lines[$k].StartsWith("diff --git "); $k++) { $hdr += $lines[$k] }
            $never = @($hdr | Where-Object { $_ -match '^(old mode|new mode|similarity index|dissimilarity index|rename from|rename to|copy from|copy to|Binary files|GIT binary patch)' })
            $mode = @($hdr | Where-Object { $_ -match '^(new file mode|deleted file mode) ' -and $_ -notmatch ' 100644$' })
            $shown = if ($never.Count) { ([regex]::Match($never[0], '^(old mode|new mode|similarity index|dissimilarity index|rename from|rename to|copy from|copy to|Binary files|GIT binary patch)')).Value }
                     elseif ($mode.Count) { ([regex]::Match($mode[0], '^(new file mode|deleted file mode)')).Value }
                     elseif ($hdr.Count) { "another header line" } else { "no header" }
            $problems.Add("$p has a header the factory doesn't accept ($shown)")
            break
        }
        $added = 0; $removed = 0
        while ($i -lt $lines.Count -and $lines[$i].StartsWith("@@ ")) {
            $m = [regex]::Match($lines[$i], '^@@ -\d+(,(\d+))? \+\d+(,(\d+))? @@')
            if (-not $m.Success) { $problems.Add("$p has a hunk header the factory can't read"); $i = $lines.Count; break }
            $old = if ($m.Groups[2].Success) { [int]$m.Groups[2].Value } else { 1 }
            $new = if ($m.Groups[4].Success) { [int]$m.Groups[4].Value } else { 1 }
            $i++
            while ($old -gt 0 -or $new -gt 0 -or ($i -lt $lines.Count -and $lines[$i].StartsWith("\ "))) {
                if ($i -ge $lines.Count) { $problems.Add("$p ends in the middle of a hunk"); break }
                $c = $lines[$i]
                if ($c.StartsWith("\ ")) { $i++; continue }   # "\ No newline at end of file"
                if ($c.StartsWith(" ") -and $old -gt 0 -and $new -gt 0) { $old--; $new-- }
                elseif ($c.StartsWith("-") -and $old -gt 0) { $old--; $removed++ }
                elseif ($c.StartsWith("+") -and $new -gt 0) { $new--; $added++ }
                else { $problems.Add("$p has a hunk whose lines don't match its counts"); $i = $lines.Count; break }
                $i++
            }
        }
        $total += $added + $removed
        $files += [pscustomobject]@{ path = $p; form = $form; added = $added; removed = $removed }
        if ($i -lt $lines.Count -and -not $lines[$i].StartsWith("diff --git ")) {
            $problems.Add("$p is followed by a line that isn't a hunk or a file header"); break
        }
    }
    if ($total -gt $rules.MaxLines) { $problems.Add("the patch changes $total lines, over $($rules.MaxLines)") }
    $dupes = @($files | Group-Object path | Where-Object Count -gt 1)
    if ($dupes.Count) { $problems.Add("the patch names $($dupes[0].Name) twice") }
    [pscustomobject]@{ ok = ($problems.Count -eq 0); problems = @($problems); files = $files }
}

# ==== The token in text ===============================================================================================
# The ways the OAuth token could appear in text the factory reads back: as is, in hex (either case), and in base64 at
# each of its three alignments, so a token inside a longer base64 run is still found. Only the characters that come
# from the token alone are searched for.
function Get-TokenForms([string]$token) {
    if (-not $token -or $token.Length -lt 16) { return @() }
    $bytes = [Text.Encoding]::UTF8.GetBytes($token)
    $hex = -join ($bytes | ForEach-Object { $_.ToString("x2") })
    $forms = @($token, $hex, $hex.ToUpperInvariant())
    foreach ($k in 0, 1, 2) {
        $b = New-Object byte[] ($k + $bytes.Length)
        [Array]::Copy($bytes, 0, $b, $k, $bytes.Length)
        $s = [Convert]::ToBase64String($b)
        $start = @(0, 2, 3)[$k]
        $end = [Math]::Floor(8 * ($k + $bytes.Length) / 6)
        if ($end - $start -ge 16) { $forms += $s.Substring($start, $end - $start) }
    }
    @($forms | Select-Object -Unique)
}

# True when $text holds the token in any of its forms.
function Test-TokenText([string]$text, [string]$token) {
    if (-not $text) { return $false }
    foreach ($f in (Get-TokenForms $token)) { if ($text.Contains($f)) { return $true } }
    $false
}

# git diff --numstat lines for a checked patch (added, removed, path), so Get-DiffFacts reads the patch itself.
function ConvertTo-Numstat($check) { @($check.files | ForEach-Object { "$($_.added)`t$($_.removed)`t$($_.path)" }) }

Export-ModuleMember -Function Import-PatchRules, Test-PatchPath, Test-PatchText, Get-TokenForms, Test-TokenText, ConvertTo-Numstat
