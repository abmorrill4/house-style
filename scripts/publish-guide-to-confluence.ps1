<#
.SYNOPSIS
  Publish the style guide (Flare project topics) to Confluence Data Center 10 as one page per topic.
.DESCRIPTION
  Source of truth is the Flare guide project; Confluence holds a published copy. The script reads the guide's
  Content topics directly (they are well-formed XML, need no Flare build and no HTML5 skin scripts to strip),
  converts each to Confluence storage format and creates or updates one page per topic under a parent page.
  Uses the Confluence Data Center REST API v1 with a personal access token (Bearer).

  Environment (never printed or logged):
    CONFLUENCE_BASE_URL   e.g. https://wiki.example.com/confluence  (context path allowed)
    CONFLUENCE_PAT        personal access token
    CONFLUENCE_SPACE_KEY  optional; defaults to the space of the parent page
    CONFLUENCE_PARENT_ID  parent page id (or use -ParentPageId)

  -WhatIf prints the plan and writes the converted XHTML to -OutDir; it makes no API call.
  Pages carry the label flare-style-guide. A page whose topic was removed is labelled flare-orphaned and left alone.
  A page with the same title that was not published by this script is never overwritten (use -AdoptExisting).
.EXAMPLE
  .\scripts\publish-guide-to-confluence.ps1 -WhatIf
  .\scripts\publish-guide-to-confluence.ps1 -ParentPageId 123456
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$GuideProject = '',
    [string[]]$Folders = @('Company', 'Google'),
    [string]$BaseUrl = '',
    [string]$ParentPageId = '',
    [string]$SpaceKey = '',
    [string]$TitlePrefix = '',
    [string]$OutDir = '',
    [string]$Banner = 'Edited in Flare &#8212; changes here are overwritten; suggest changes to the style guide owners.',
    [switch]$AdoptExisting
)

$script:Label = 'flare-style-guide'
$script:OrphanLabel = 'flare-orphaned'
$script:PropKey = 'flare-guide-sync'
$script:AllowedMacros = @('info', 'note', 'warning', 'tip', 'code')

# ---------- conversion ----------

function ConvertTo-XmlText([string]$s) {
    if ($null -eq $s) { return '' }
    return $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}
function ConvertTo-XmlAttr([string]$s) { return (ConvertTo-XmlText $s).Replace('"', '&quot;') }

function Read-Topic([string]$path) {
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($path, $settings)
    try { $doc = New-Object System.Xml.XmlDocument; $doc.Load($reader) } finally { $reader.Close() }
    return $doc
}

function Get-TopicTitle($doc, [string]$path) {
    $t = $doc.SelectSingleNode('//*[local-name()="title"]')
    if ($t -and $t.InnerText.Trim()) { return $t.InnerText.Trim() }
    $h = $doc.SelectSingleNode('//*[local-name()="h1"]')
    if ($h -and $h.InnerText.Trim()) { return $h.InnerText.Trim() }
    return [IO.Path]::GetFileNameWithoutExtension($path)
}

# $ctx: @{ Dir = topic folder; Map = @{ lower-case full path = page title }; Warnings = list }
function Resolve-TopicLink([string]$href, $ctx) {
    $h = $href
    $i = $h.IndexOf('#'); if ($i -ge 0) { $h = $h.Substring(0, $i) }
    if (-not $h) { return $null }
    try { $full = [IO.Path]::GetFullPath((Join-Path $ctx.Dir ($h.Replace('/', '\')))) } catch { return $null }
    $key = $full.ToLowerInvariant()
    if ($ctx.Map.ContainsKey($key)) { return $ctx.Map[$key] }
    return $null
}

function ConvertTo-StorageChildren($node, $ctx) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($c in $node.ChildNodes) { [void]$sb.Append((ConvertTo-StorageNode $c $ctx)) }
    return $sb.ToString()
}

function ConvertTo-StorageNode($n, $ctx) {
    switch ($n.NodeType) {
        'Text' { return (ConvertTo-XmlText $n.Value) }
        'CDATA' { return (ConvertTo-XmlText $n.Value) }
        'SignificantWhitespace' { return (ConvertTo-XmlText $n.Value) }
        'Whitespace' { return ' ' }
        'Element' { }
        default { return '' }
    }
    $name = $n.LocalName.ToLowerInvariant()
    switch -regex ($name) {
        '^(p|ul|ol|li|h[1-6]|strong|em|code|sup|sub|thead|tbody|blockquote)$' {
            return "<$name>" + (ConvertTo-StorageChildren $n $ctx) + "</$name>"
        }
        '^b$' { return '<strong>' + (ConvertTo-StorageChildren $n $ctx) + '</strong>' }
        '^i$' { return '<em>' + (ConvertTo-StorageChildren $n $ctx) + '</em>' }
        '^br$' { return '<br />' }
        '^hr$' { return '<hr />' }
        '^(td|th)$' {
            $attrs = ''
            foreach ($a in 'colspan', 'rowspan') {
                $v = $n.GetAttribute($a)
                if ($v -match '^\d+$') { $attrs += " $a=`"$v`"" }
            }
            return "<$name$attrs>" + (ConvertTo-StorageChildren $n $ctx) + "</$name>"
        }
        '^tr$' { return '<tr>' + (ConvertTo-StorageChildren $n $ctx) + '</tr>' }
        '^table$' {
            $inner = ''
            $loose = ''
            foreach ($c in $n.ChildNodes) {
                if ($c.NodeType -ne 'Element') { continue }
                $ln = $c.LocalName.ToLowerInvariant()
                if ($ln -eq 'tr') { $loose += (ConvertTo-StorageNode $c $ctx) }
                elseif ($ln -eq 'thead' -or $ln -eq 'tbody') { $inner += (ConvertTo-StorageNode $c $ctx) }
            }
            if ($loose) { $inner += "<tbody>$loose</tbody>" }
            return "<table>$inner</table>"
        }
        '^pre$' {
            $t = $n.InnerText.Replace(']]>', ']]]]><![CDATA[>')
            return '<ac:structured-macro ac:name="code"><ac:plain-text-body><![CDATA[' + $t + ']]></ac:plain-text-body></ac:structured-macro>'
        }
        '^(a|xref)$' {
            $href = $n.GetAttribute('href')
            $inner = (ConvertTo-StorageChildren $n $ctx)
            if (-not $href) { return $inner }
            if ($href -match '^([a-zA-Z][a-zA-Z0-9+.-]*:|//)') {
                if ($href -match '^(?i)(https?|mailto):') {
                    if (-not $inner) { $inner = ConvertTo-XmlText $href }
                    return '<a href="' + (ConvertTo-XmlAttr $href) + '">' + $inner + '</a>'
                }
                return $inner
            }
            $title = Resolve-TopicLink $href $ctx
            if ($title) {
                $text = $n.InnerText.Trim()
                if (-not $text) { $text = $title }
                return '<ac:link><ri:page ri:content-title="' + (ConvertTo-XmlAttr $title) + '" /><ac:link-body>' + (ConvertTo-XmlText $text) + '</ac:link-body></ac:link>'
            }
            if ($href -notmatch '^#') { [void]$ctx.Warnings.Add("link '$href' does not point at a published topic; kept as text") }
            return $inner
        }
        '^(img|colgroup|col|script|style|head)$' { return '' }
        default { return (ConvertTo-StorageChildren $n $ctx) }   # div, span, MadCap:* and anything unknown: keep the text
    }
}

function ConvertTo-ConfluenceStorage($doc, $ctx, [string]$banner) {
    $body = $doc.SelectSingleNode('//*[local-name()="body"]')
    $sb = New-Object System.Text.StringBuilder
    if ($banner) {
        [void]$sb.Append('<ac:structured-macro ac:name="info"><ac:rich-text-body><p>' + $banner + '</p></ac:rich-text-body></ac:structured-macro>')
    }
    foreach ($c in $body.ChildNodes) {
        # the page title carries the topic's h1
        if ($c.NodeType -eq 'Element' -and $c.LocalName -eq 'h1') { continue }
        [void]$sb.Append((ConvertTo-StorageNode $c $ctx))
    }
    return $sb.ToString()
}

# Parses a storage fragment (wrapped with the ac/ri namespaces) and checks macros. Returns $null when valid, else the problem.
function Test-StorageXhtml([string]$xhtml) {
    $wrapped = '<root xmlns:ac="http://atlassian.com/content" xmlns:ri="http://atlassian.com/resource/identifier">' + $xhtml + '</root>'
    $doc = New-Object System.Xml.XmlDocument
    $doc.XmlResolver = $null
    try { $doc.LoadXml($wrapped) } catch { return 'not well-formed: ' + $_.Exception.Message }
    foreach ($m in $doc.SelectNodes('//*[local-name()="structured-macro"]')) {
        $mn = $m.GetAttribute('name', 'http://atlassian.com/content')
        if ($script:AllowedMacros -notcontains $mn) { return "macro '$mn' is not allowed" }
    }
    return $null
}

function Get-Sha256([string]$s) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) } finally { $sha.Dispose() }
    return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
}

# Reads every topic and returns objects { Folder; Source; Title; Xhtml; Hash; Warnings }.
function Get-GuidePages([string]$guide, [string[]]$folders, [string]$prefix, [string]$banner) {
    $root = Join-Path $guide 'Content\Resources\StyleGuide'
    $topics = @()
    foreach ($f in $folders) {
        $dir = Join-Path $root $f
        if (-not (Test-Path $dir)) { throw "Guide folder not found: $dir" }
        foreach ($file in (Get-ChildItem -Path $dir -Filter '*.htm' | Sort-Object Name)) {
            $doc = Read-Topic $file.FullName
            $topics += New-Object psobject -Property @{ Folder = $f; File = $file.FullName; Source = "$f/$($file.Name)"; Doc = $doc; Title = (Get-TopicTitle $doc $file.FullName) }
        }
    }
    # Titles are unique per space: prefix the folder when two topics share a title.
    $dupes = @($topics | Group-Object { $_.Title.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    foreach ($t in $topics) {
        $t.Title = $prefix + $(if ($dupes -contains $t.Title.ToLowerInvariant()) { "$($t.Folder) - $($t.Title)" } else { $t.Title })
    }
    $map = @{}
    # Same normalisation as Resolve-TopicLink: GetFullPath expands 8.3 short names (C:\Users\RUNNER~1\...).
    foreach ($t in $topics) { $map[[IO.Path]::GetFullPath($t.File).ToLowerInvariant()] = $t.Title }
    $pages = @()
    foreach ($t in $topics) {
        $ctx = @{ Dir = (Split-Path $t.File -Parent); Map = $map; Warnings = (New-Object System.Collections.ArrayList) }
        $xhtml = ConvertTo-ConfluenceStorage $t.Doc $ctx $banner
        $problem = Test-StorageXhtml $xhtml
        if ($problem) { throw "$($t.Source): converted page is invalid ($problem)" }
        $pages += New-Object psobject -Property @{
            Folder = $t.Folder; Source = $t.Source; Title = $t.Title; Xhtml = $xhtml
            Hash = (Get-Sha256 ($t.Title + "`n" + $xhtml)); Warnings = @($ctx.Warnings)
        }
    }
    return $pages
}

# ---------- Confluence Data Center REST v1 ----------

function New-Conf([string]$baseUrl, [string]$pat) {
    $base = $baseUrl.TrimEnd('/')
    $u = $null
    if (-not [Uri]::TryCreate($base, [UriKind]::Absolute, [ref]$u)) { throw "CONFLUENCE_BASE_URL is not a web address. Use an address that starts with https://, for example https://wiki.example.com/confluence" }
    $loopback = ($u.IsLoopback -or $u.Host -eq 'localhost' -or $u.Host.EndsWith('.localhost'))
    if ($u.Scheme -ne 'https' -and -not ($u.Scheme -eq 'http' -and $loopback)) {
        throw "CONFLUENCE_BASE_URL must start with https:// so the token is not sent unencrypted. Change CONFLUENCE_BASE_URL (or -BaseUrl) and run again."
    }
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 } catch { }
    return @{ Base = $base; Origin = $u.GetLeftPart([UriPartial]::Authority); Ctx = $u.AbsolutePath.TrimEnd('/'); Pat = $pat }
}

# $path is relative to the base URL (starts with /rest/api/...). Returns @{ Status; Json; Message }.
# 401/403/429 (after retries) throw with advice; other non-OK statuses are returned for the caller.
function Invoke-Conf($conf, [string]$method, [string]$path, $body = $null, [int[]]$okStatus = @(200)) {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $req = [Net.HttpWebRequest]::Create($conf.Base + $path)
        $req.Method = $method
        $req.AllowAutoRedirect = $false
        $req.Accept = 'application/json'
        $req.Headers['Authorization'] = 'Bearer ' + $conf.Pat
        $req.Headers['X-Atlassian-Token'] = 'no-check'
        $req.UserAgent = 'FlareAI-guide-publisher'
        if ($null -ne $body) {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 12 -Compress))
            $req.ContentType = 'application/json; charset=utf-8'
            $req.ContentLength = $bytes.Length
            $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
        }
        $retry = 0
        try {
            $resp = $req.GetResponse()
        } catch [Net.WebException] {
            $resp = $_.Exception.Response
            if ($null -eq $resp) { throw "Cannot reach Confluence at $($conf.Origin): $($_.Exception.Message)" }
        }
        $status = [int]$resp.StatusCode
        $sr = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd(); $sr.Close()
        [void][int]::TryParse([string]$resp.Headers['Retry-After'], [ref]$retry)
        $resp.Close()
        if ($status -eq 429 -and $attempt -lt 5) { Start-Sleep -Seconds ([Math]::Min([Math]::Max($retry, 0), 60)); continue }
        $json = $null
        if ($text) { try { $json = $text | ConvertFrom-Json } catch { } }
        if ($okStatus -contains $status) { return @{ Status = $status; Json = $json } }
        $msg = ''
        if ($json -and $json.message) { $msg = ': ' + $json.message }
        switch ($status) {
            401 { throw "401 Unauthorized: check CONFLUENCE_PAT (is it valid, not expired or revoked?)$msg" }
            403 { throw "403 Forbidden: the token's user needs view and add/edit page permission in the space, and view on the parent page$msg" }
            { $_ -ge 300 -and $_ -lt 400 } { throw "HTTP ${status}: Confluence redirected this request, and the publisher does not follow redirects. Change CONFLUENCE_BASE_URL to the final address and run again." }
            429 { throw "429 Too Many Requests: Confluence is rate limiting this account; try again later$msg" }
            default { return @{ Status = $status; Json = $json; Message = "$method $path -> HTTP $status$msg" } }
        }
    }
}

function Get-ResultList($conf, [string]$path) {
    $all = @()
    while ($path) {
        $r = Invoke-Conf $conf 'GET' $path
        if ($r.Status -ne 200) { throw "GET $path -> HTTP $($r.Status)" }
        $all += @($r.Json.results)
        $path = $null
        if ($r.Json._links -and $r.Json._links.next) {
            $next = [string]$r.Json._links.next
            # next may or may not include the context path
            if ($conf.Ctx -and $next.StartsWith($conf.Ctx + '/')) { $path = $next.Substring($conf.Ctx.Length) } else { $path = $next }
        }
    }
    return $all
}

function Get-LabelNames($page) {
    if ($page.metadata -and $page.metadata.labels -and $page.metadata.labels.results) { return @($page.metadata.labels.results | ForEach-Object { $_.name }) }
    return @()
}

function Set-PageLabel($conf, [string]$id, [string]$name) {
    $r = Invoke-Conf $conf 'POST' "/rest/api/content/$id/label" @(@{ prefix = 'global'; name = $name })
    if ($r.Status -ne 200) { throw "adding label $name -> HTTP $($r.Status)" }
}

function Set-PageHash($conf, [string]$id, [string]$hash, [string]$source) {
    $value = @{ hash = $hash; source = $source }
    $cur = Invoke-Conf $conf 'GET' "/rest/api/content/$id/property/$($script:PropKey)"
    if ($cur.Status -eq 404) {
        $r = Invoke-Conf $conf 'POST' "/rest/api/content/$id/property" @{ key = $script:PropKey; value = $value }
    } else {
        $r = Invoke-Conf $conf 'PUT' "/rest/api/content/$id/property/$($script:PropKey)" @{ key = $script:PropKey; value = $value; version = @{ number = ([int]$cur.Json.version.number + 1) } }
    }
    if ($r.Status -ne 200) { throw "saving change hash -> HTTP $($r.Status)" }
}

function Publish-Page($conf, $page, [string]$spaceKey, [string]$parentId, [bool]$adopt) {
    $q = '/rest/api/content?type=page&spaceKey=' + [Uri]::EscapeDataString($spaceKey) + '&title=' + [Uri]::EscapeDataString($page.Title) + '&expand=version,metadata.labels'
    $found = Invoke-Conf $conf 'GET' $q
    if ($found.Status -ne 200) { throw "lookup -> HTTP $($found.Status)" }
    $existing = @($found.Json.results) | Select-Object -First 1
    $storage = @{ value = $page.Xhtml; representation = 'storage' }

    if (-not $existing) {
        $r = Invoke-Conf $conf 'POST' '/rest/api/content' @{
            type = 'page'; title = $page.Title; space = @{ key = $spaceKey }
            ancestors = @(@{ id = $parentId }); body = @{ storage = $storage }
        }
        if ($r.Status -ne 200) { throw $r.Message }
        $id = [string]$r.Json.id
        Set-PageLabel $conf $id $script:Label
        Set-PageHash $conf $id $page.Hash $page.Source
        return 'created'
    }

    $id = [string]$existing.id
    $labels = Get-LabelNames $existing
    if ($labels -notcontains $script:Label) {
        if (-not $adopt) { throw "a page titled '$($page.Title)' exists but was not published by this script; rename it or use -AdoptExisting" }
        Set-PageLabel $conf $id $script:Label
    }
    if ($labels -contains $script:OrphanLabel) {
        [void](Invoke-Conf $conf 'DELETE' "/rest/api/content/$id/label/$($script:OrphanLabel)" $null @(200, 204))
    }
    $prop = Invoke-Conf $conf 'GET' "/rest/api/content/$id/property/$($script:PropKey)"
    if ($prop.Status -eq 200 -and $prop.Json.value.hash -eq $page.Hash) { return 'unchanged' }

    for ($try = 1; $try -le 2; $try++) {
        $n = [int]$existing.version.number + 1
        $r = Invoke-Conf $conf 'PUT' "/rest/api/content/$id" @{
            id = $id; type = 'page'; title = $page.Title; space = @{ key = $spaceKey }
            version = @{ number = $n; message = 'Published from the Flare style guide' }; body = @{ storage = $storage }
        }
        if ($r.Status -eq 200) { break }
        if ($r.Status -eq 409 -and $try -eq 1) {   # someone saved in between: re-read the version and retry once
            $again = Invoke-Conf $conf 'GET' "/rest/api/content/$id`?expand=version"
            $existing = $again.Json
            continue
        }
        throw $r.Message
    }
    Set-PageHash $conf $id $page.Hash $page.Source
    return 'updated'
}

function Update-OrphanLabels($conf, [string]$parentId, $pages) {
    $titles = @{}; foreach ($p in $pages) { $titles[$p.Title] = $true }
    $children = Get-ResultList $conf "/rest/api/content/$parentId/child/page?limit=100&expand=metadata.labels"
    $out = @()
    foreach ($c in $children) {
        $labels = Get-LabelNames $c
        if ($labels -contains $script:Label -and -not $titles.ContainsKey($c.title)) {
            if ($labels -notcontains $script:OrphanLabel) { Set-PageLabel $conf ([string]$c.id) $script:OrphanLabel }
            $out += $c.title
        }
    }
    return $out
}

# ---------- main ----------

function Invoke-GuidePublish {
    $guide = $GuideProject
    if (-not $guide) { $guide = Join-Path $PSScriptRoot '..\styleguide' }
    $guide = [IO.Path]::GetFullPath($guide)
    $whatIf = [bool]$WhatIfPreference

    $pages = @(Get-GuidePages $guide $Folders $TitlePrefix $Banner)
    Write-Host ("Converted {0} topic(s) from {1}" -f $pages.Count, $guide)
    foreach ($p in $pages) { foreach ($w in $p.Warnings) { Write-Warning "$($p.Source): $w" } }

    $out = $OutDir
    if (-not $out -and $whatIf) { $out = Join-Path (Get-Location).Path 'confluence-preview' }
    if ($out) {
        [void][IO.Directory]::CreateDirectory($out)
        foreach ($p in $pages) {
            $file = Join-Path $out (($p.Source -replace '[\\/]', '-') -replace '\.htm$', '.xhtml')
            [IO.File]::WriteAllText($file, $p.Xhtml, (New-Object Text.UTF8Encoding($false)))
        }
        Write-Host "Wrote converted XHTML to $out"
    }

    if ($whatIf) {
        Write-Host 'WhatIf: no Confluence calls made. Plan (each page is created, or updated only if its content hash changed):'
        foreach ($p in $pages) { Write-Host ("  {0,-60} <- {1}  [{2}]" -f $p.Title, $p.Source, $p.Hash.Substring(0, 8)) }
        return 0
    }

    $base = $BaseUrl; if (-not $base) { $base = $env:CONFLUENCE_BASE_URL }
    $pat = $env:CONFLUENCE_PAT
    $parent = $ParentPageId; if (-not $parent) { $parent = $env:CONFLUENCE_PARENT_ID }
    $space = $SpaceKey; if (-not $space) { $space = $env:CONFLUENCE_SPACE_KEY }
    if (-not $base) { throw 'Set CONFLUENCE_BASE_URL (or -BaseUrl), for example https://wiki.example.com/confluence' }
    if (-not $pat) { throw 'Set CONFLUENCE_PAT to a personal access token' }
    if (-not $parent) { throw 'Set CONFLUENCE_PARENT_ID (or -ParentPageId): the page the guide pages go under' }
    $conf = New-Conf $base $pat

    $par = Invoke-Conf $conf 'GET' "/rest/api/content/$parent`?expand=space"
    if ($par.Status -ne 200) { throw "Parent page $parent not found (HTTP $($par.Status)); check the id and that the token's user can view it" }
    if (-not $space) { $space = [string]$par.Json.space.key }
    elseif ($par.Json.space.key -and $par.Json.space.key -ne $space) { throw "Parent page $parent is in space $($par.Json.space.key), not $space" }
    $sp = Invoke-Conf $conf 'GET' ('/rest/api/space/' + [Uri]::EscapeDataString($space))
    if ($sp.Status -ne 200) { throw "Space $space not found (HTTP $($sp.Status)); check the key and the token's permissions" }
    Write-Host "Publishing to space $space under page $parent"

    $counts = @{ created = 0; updated = 0; unchanged = 0; failed = 0 }
    foreach ($p in $pages) {
        try {
            $res = Publish-Page $conf $p $space $parent ([bool]$AdoptExisting)
            $counts[$res]++
            Write-Host ("  {0,-9} {1}" -f $res, $p.Title)
        } catch {
            $counts.failed++
            Write-Host ("  FAILED    {0}: {1}" -f $p.Title, $_.Exception.Message)
            if ($_.Exception.Message -match '^40[13] ') { break }   # auth problems affect every page
        }
    }
    $orph = @()
    if ($counts.failed -eq 0) { $orph = @(Update-OrphanLabels $conf $parent $pages) }
    foreach ($o in $orph) { Write-Host "  orphaned  $o (topic no longer in the guide; labelled $($script:OrphanLabel), not deleted)" }
    Write-Host ("Done: {0} created, {1} updated, {2} unchanged, {3} failed, {4} orphaned" -f $counts.created, $counts.updated, $counts.unchanged, $counts.failed, $orph.Count)
    if ($counts.failed -gt 0) { return 1 }
    return 0
}

if ($MyInvocation.InvocationName -ne '.') {
    $code = 1
    try { $code = Invoke-GuidePublish } catch { Write-Host ("ERROR: " + $_.Exception.Message) }
    exit $code
}
