# Pester 3 tests (built into Windows PowerShell 5.1): powershell.exe -Command "Invoke-Pester tests\publish-guide"
# No real Confluence: the end-to-end tests talk to MockConfluence.ps1 on localhost.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = [IO.Path]::GetFullPath((Join-Path $here '..\..'))
$script = Join-Path $repo 'scripts\publish-guide-to-confluence.ps1'
$guide = Join-Path $repo 'styleguide'

function Test-Xhtml([string]$x) {
    $d = New-Object System.Xml.XmlDocument
    $d.LoadXml('<root xmlns:ac="http://atlassian.com/content" xmlns:ri="http://atlassian.com/resource/identifier">' + $x + '</root>')
}

Describe 'Converter' {
    . $script
    $dir = Join-Path $env:TEMP ('cp-conv-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($dir)
    $other = Join-Path $dir 'Other.htm'
    [IO.File]::WriteAllText($other, '<html><head><title>Other topic</title></head><body><h1>Other topic</h1></body></html>')
    $src = @'
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd"><head><title>Sample &amp; test</title></head><body>
<h1>Sample</h1><h2>Part</h2>
<p>See <MadCap:xref href="Other.htm#x">the other topic</MadCap:xref>, <a href="Missing.htm">gone</a>, <a href="https://example.com/a?b=1&amp;c=2">ext</a>. <b>Bold</b> <i>it</i> a &lt; b <MadCap:variable name="x.y" /></p>
<ul><li id="rule-1" MadCap:conditions="Content.Links">One<ul><li>Bad: x</li></ul></li></ul>
<table><tr><th>A</th></tr><tr><td colspan="2">1</td></tr></table>
<pre>if (a &lt; b) { ]]&gt; }</pre>
</body></html>
'@
    $file = Join-Path $dir 'Sample.htm'
    [IO.File]::WriteAllText($file, $src, (New-Object Text.UTF8Encoding($false)))
    $doc = Read-Topic $file
    $map = @{}; $map[[IO.Path]::GetFullPath($other).ToLowerInvariant()] = 'Other topic'
    $ctx = @{ Dir = $dir; Map = $map; Warnings = (New-Object System.Collections.ArrayList) }
    $out = ConvertTo-ConfluenceStorage $doc $ctx 'BANNER'

    It 'produces well-formed storage XHTML' { { Test-Xhtml $out } | Should Not Throw; Test-StorageXhtml $out | Should BeNullOrEmpty }
    It 'drops the h1 and adds the banner in an info macro' {
        $out | Should Not Match '<h1>'
        $out | Should Match '^<ac:structured-macro ac:name="info"><ac:rich-text-body><p>BANNER</p>'
    }
    It 'rewrites links between guide pages to page links' {
        $out | Should Match '<ac:link><ri:page ri:content-title="Other topic" /><ac:link-body>the other topic</ac:link-body></ac:link>'
    }
    It 'keeps external links, escaped, and flattens unresolved ones to text with a warning' {
        $out | Should Match '<a href="https://example.com/a\?b=1&amp;c=2">ext</a>'
        $out | Should Not Match 'Missing.htm'
        ($ctx.Warnings -join ';') | Should Match 'Missing.htm'
    }
    It 'strips Flare attributes, wraps loose table rows in tbody, keeps colspan' {
        $out | Should Not Match 'MadCap|rule-1|conditions'
        $out | Should Match '<table><tbody><tr><th>A</th></tr><tr><td colspan="2">1</td></tr></tbody></table>'
    }
    It 'turns pre into a code macro with safe CDATA' { $out | Should Match 'ac:name="code"><ac:plain-text-body><!\[CDATA\[if \(a < b\) \{ \]\]\]\]><!\[CDATA\[> \}' }
    It 'rejects macros outside the allowed set' { Test-StorageXhtml '<ac:structured-macro ac:name="html"></ac:structured-macro>' | Should Match 'not allowed' }
    Remove-Item $dir -Recurse -Force
}

Describe 'WhatIf over the bundled guide' {
    $out = Join-Path $env:TEMP ('cp-whatif-' + [guid]::NewGuid().ToString('N'))
    $env:CONFLUENCE_PAT = 'must-not-be-needed'; $env:CONFLUENCE_BASE_URL = 'http://localhost:1'
    $text = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -WhatIf -Folders Company -OutDir $out 2>&1) -join "`n"
    $code = $LASTEXITCODE
    $env:CONFLUENCE_PAT = $null; $env:CONFLUENCE_BASE_URL = $null
    It 'exits 0 without calling the API' { $code | Should Be 0; $text | Should Match 'no Confluence calls made' }
    It 'writes one parsable XHTML file per Company topic' {
        $files = @(Get-ChildItem $out -Filter '*.xhtml')
        $files.Count | Should Be @(Get-ChildItem (Join-Path $guide 'Content\Resources\StyleGuide\Company') -Filter '*.htm').Count
        foreach ($f in $files) { { Test-Xhtml ([IO.File]::ReadAllText($f.FullName)) } | Should Not Throw }
    }
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Guide project' {
    It 'has a TOC entry for every topic and a target that points at it' {
        $toc = [IO.File]::ReadAllText((Join-Path $guide 'Project\TOCs\Main.fltoc'))
        $topics = Get-ChildItem (Join-Path $guide 'Content\Resources\StyleGuide') -Recurse -Filter '*.htm'
        foreach ($t in $topics) { $toc | Should Match ([regex]::Escape($t.Directory.Name + '/' + $t.Name)) }
        [IO.File]::ReadAllText((Join-Path $guide 'Project\Targets\HTML5.fltar')) | Should Match 'MasterToc="/Project/TOCs/Main.fltoc"'
    }
}

Describe 'Publish against the mock Data Center server' {
    $port = 8931
    $root = "http://localhost:$port/confluence"
    $work = Join-Path $env:TEMP ('cp-e2e-' + [guid]::NewGuid().ToString('N'))
    $proj = Join-Path $work 'guide'
    $co = Join-Path $proj 'Content\Resources\StyleGuide\Company'
    [void][IO.Directory]::CreateDirectory($co)
    Copy-Item (Join-Path $guide 'Content\Resources\StyleGuide\Company\*.htm') $co
    $mock = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'MockConfluence.ps1'), '-Port', $port)
    for ($i = 0; $i -lt 40; $i++) { try { [void](Invoke-RestMethod "http://localhost:$port/__state"); break } catch { Start-Sleep -Milliseconds 250 } }

    function Get-State { Invoke-RestMethod "http://localhost:$port/__state" }
    function Add-Fault($method, $contains, $status, $retry = 0, $location = '') {
        Invoke-RestMethod "http://localhost:$port/__inject" -Method Post -ContentType 'application/json' -Body (@{ method = $method; contains = $contains; status = $status; count = 1; retryAfter = $retry; location = $location } | ConvertTo-Json) | Out-Null
    }
    function Run-Publish($pat = 'test-pat', $baseUrl = $root) {
        $env:CONFLUENCE_BASE_URL = $baseUrl; $env:CONFLUENCE_PAT = $pat; $env:CONFLUENCE_PARENT_ID = '100'
        $r = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -GuideProject $proj -Folders Company 2>&1) -join "`n"
        $script:lastCode = $LASTEXITCODE
        $env:CONFLUENCE_BASE_URL = $null; $env:CONFLUENCE_PAT = $null; $env:CONFLUENCE_PARENT_ID = $null
        return $r
    }
    function Kids { @((Get-State).pages | Where-Object { $_.parent -eq '100' }) }
    function Writes($from) { @((Get-State).log | Select-Object -Skip $from | Where-Object { $_ -match '^(POST|PUT|DELETE)' }) }

    It 'first run creates one labelled page per topic under the parent' {
        $o = Run-Publish
        $lastCode | Should Be 0
        $o | Should Match '6 created, 0 updated, 0 unchanged, 0 failed'
        $k = Kids
        $k.Count | Should Be 6
        foreach ($p in $k) { $p.labels -contains 'flare-style-guide' | Should Be $true; $p.version | Should Be 1; $p.body | Should Match 'Edited in Flare' }
    }
    It 'a second run with no changes writes nothing' {
        $n = (Get-State).log.Count
        $o = Run-Publish
        $o | Should Match '0 created, 0 updated, 6 unchanged'
        (Writes $n).Count | Should Be 0
    }
    It 'an edited topic updates only that page, version + 1' {
        $f = Join-Path $co 'Claims.htm'
        [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f).Replace('</body>', '<p>New paragraph.</p></body>')))
        $o = Run-Publish
        $o | Should Match '0 created, 1 updated, 5 unchanged'
        $k = Kids
        ($k | Where-Object { $_.title -eq 'Claims' }).version | Should Be 2
        ($k | Where-Object { $_.title -eq 'Claims' }).body | Should Match 'New paragraph'
        @($k | Where-Object { $_.version -ne 1 }).Count | Should Be 1
    }
    It 'recovers from a version conflict (409) by re-reading the version' {
        $f = Join-Path $co 'Claims.htm'
        [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f).Replace('New paragraph.', 'Again.')))
        Add-Fault 'PUT' '/rest/api/content/' 409
        $o = Run-Publish
        $lastCode | Should Be 0
        $o | Should Match '1 updated'
    }
    It 'a removed topic is labelled orphaned, not deleted; restoring it clears the label' {
        $f = Join-Path $co 'Terminology.htm'
        $bak = Join-Path $work 'Terminology.bak'
        Move-Item $f $bak
        $o = Run-Publish
        $o | Should Match '1 orphaned'
        $p = Kids | Where-Object { $_.labels -contains 'flare-orphaned' }
        @($p).Count | Should Be 1
        Kids | Measure-Object | ForEach-Object { $_.Count } | Should Be 6
        Move-Item $bak $f
        $o = Run-Publish
        $o | Should Match '0 orphaned'
        @(Kids | Where-Object { $_.labels -contains 'flare-orphaned' }).Count | Should Be 0
    }
    It 'retries after a 429 with Retry-After' {
        Add-Fault 'GET' '/rest/api/space/' 429 0
        $o = Run-Publish
        $lastCode | Should Be 0
    }
    It 'explains a bad token without printing it' {
        $o = Run-Publish 'wrong-secret-token'
        $lastCode | Should Be 1
        $o | Should Match 'check CONFLUENCE_PAT'
        $o | Should Not Match 'wrong-secret-token'
    }
    It 'explains missing permissions (403)' {
        Add-Fault 'GET' '/rest/api/space/' 403
        $o = Run-Publish
        $lastCode | Should Be 1
        $o | Should Match 'permission'
    }
    It 'refuses plain http to a host other than this computer, before any request' {
        $before = @((Get-State).log).Count
        $o = Run-Publish 'test-pat' 'http://wiki.example.com/confluence'
        $lastCode | Should Be 1
        $o | Should Match 'https://'
        @((Get-State).log).Count | Should Be $before
    }
    It 'does not follow a redirect and sends the token nowhere else' {
        Add-Fault 'GET' '/rest/api/content/100' 302 0 "http://127.0.0.1:$port/confluence/rest/api/space/DOC"
        $before = @((Get-State).log).Count
        $o = Run-Publish
        $lastCode | Should Be 1
        $o | Should Match 'redirect'
        $o | Should Not Match 'test-pat'
        @((Get-State).log | Select-Object -Skip $before).Count | Should Be 1
    }
    It 'never overwrites a page it did not publish' {
        [IO.File]::WriteAllText((Join-Path $co 'Collide.htm'), '<html><head><title>Style guide (parent)</title></head><body><h1>x</h1></body></html>')
        $o = Run-Publish
        $lastCode | Should Be 1
        $o | Should Match 'not published by this script'
        (Get-State).pages | Where-Object { $_.id -eq '100' } | ForEach-Object { $_.version } | Should Be 1
    }

    try { Invoke-RestMethod "http://localhost:$port/__stop" -Method Post | Out-Null } catch { }
    if (-not $mock.WaitForExit(3000)) { $mock.Kill() }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
