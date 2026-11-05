# Local mock of the Confluence Data Center 10 REST API v1 endpoints that publish-guide-to-confluence.ps1 uses.
# Built from the DC REST docs, not from a live server. Run with: MockConfluence.ps1 -Port 8931 -Context /confluence
# Control endpoints (no auth): GET /__state, POST /__inject, POST /__bump/{id}, POST /__stop
param([int]$Port = 8931, [string]$Context = '/confluence', [string]$Token = 'test-pat', [string]$SpaceKey = 'DOC', [int]$PageSize = 4)

$pages = @{}          # id -> hashtable
$nextId = 1000
$log = New-Object System.Collections.ArrayList
$inject = New-Object System.Collections.ArrayList
$allowedMacros = @('info', 'note', 'warning', 'tip', 'code')

$pages['100'] = @{ id = '100'; title = 'Style guide (parent)'; space = $SpaceKey; parent = $null; version = 1; body = ''; labels = (New-Object System.Collections.ArrayList); props = @{} }

function Page-Json($p) {
    $labels = @(); foreach ($l in $p.labels) { $labels += @{ prefix = 'global'; name = $l } }
    return @{
        id = $p.id; type = 'page'; status = 'current'; title = $p.title
        space = @{ key = $p.space }
        version = @{ number = $p.version }
        metadata = @{ labels = @{ results = $labels; size = $labels.Count } }
    }
}

function Send($ctx, [int]$status, $obj, [hashtable]$headers = @{}) {
    $ctx.Response.StatusCode = $status
    foreach ($k in $headers.Keys) { $ctx.Response.Headers[$k] = [string]$headers[$k] }
    if ($null -ne $obj) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 10 -Compress))
        $ctx.Response.ContentType = 'application/json; charset=utf-8'
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    }
    $ctx.Response.OutputStream.Close()
}

function Err($ctx, [int]$status, [string]$msg) { Send $ctx $status @{ statusCode = $status; message = $msg } }

function Check-Storage($body) {
    if (-not $body -or -not $body.storage) { return 'body.storage is required' }
    if ($body.storage.representation -ne 'storage') { return 'body.storage.representation must be storage' }
    $wrapped = '<root xmlns:ac="http://atlassian.com/content" xmlns:ri="http://atlassian.com/resource/identifier">' + $body.storage.value + '</root>'
    $d = New-Object System.Xml.XmlDocument
    try { $d.LoadXml($wrapped) } catch { return 'storage value is not well-formed: ' + $_.Exception.Message }
    foreach ($m in $d.SelectNodes('//*[local-name()="structured-macro"]')) {
        $n = $m.GetAttribute('name', 'http://atlassian.com/content')
        if ($allowedMacros -notcontains $n) { return "unknown macro $n" }
    }
    foreach ($bad in 'script', 'iframe', 'object', 'style') {
        if ($d.SelectNodes("//*[local-name()='$bad']").Count -gt 0) { return "element $bad not allowed" }
    }
    return $null
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        try {
            $req = $ctx.Request
            $method = $req.HttpMethod
            $raw = $req.Url.AbsolutePath
            $query = $req.QueryString
            $bodyText = ''
            if ($req.HasEntityBody) { $sr = New-Object IO.StreamReader($req.InputStream, [Text.Encoding]::UTF8); $bodyText = $sr.ReadToEnd() }
            $body = $null; if ($bodyText) { $body = $bodyText | ConvertFrom-Json }

            if ($raw -eq '/__stop') { Send $ctx 200 @{ ok = $true }; break }
            if ($raw -eq '/__state') {
                $out = @(); foreach ($k in ($pages.Keys | Sort-Object)) { $p = $pages[$k]; $out += @{ id = $p.id; title = $p.title; parent = $p.parent; version = $p.version; body = $p.body; labels = @($p.labels); props = $p.props } }
                Send $ctx 200 @{ pages = $out; log = @($log) }; continue
            }
            if ($raw -eq '/__inject') { [void]$inject.Add(@{ method = $body.method; contains = $body.contains; status = [int]$body.status; count = [int]$body.count; retryAfter = [int]$body.retryAfter; location = [string]$body.location }); Send $ctx 200 @{ ok = $true }; continue }
            if ($raw -match '^/__bump/(\d+)$') { $pages[$Matches[1]].version++; Send $ctx 200 @{ ok = $true }; continue }

            if (-not $raw.StartsWith($Context + '/')) { Err $ctx 404 'no such context'; continue }
            $path = $raw.Substring($Context.Length)
            [void]$log.Add("$method $path$($req.Url.Query)")

            if ($req.Headers['Authorization'] -ne "Bearer $Token") { Err $ctx 401 'Unauthorized'; continue }
            $hit = $null
            foreach ($i in $inject) { if ($i.count -gt 0 -and $i.method -eq $method -and $path.Contains($i.contains)) { $hit = $i; break } }
            if ($hit) {
                $hit.count--
                $h = @{}; if ($hit.status -eq 429) { $h['Retry-After'] = $hit.retryAfter }; if ($hit.location) { $h['Location'] = $hit.location }
                Send $ctx $hit.status @{ message = "injected $($hit.status)" } $h; continue
            }

            if ($method -eq 'GET' -and $path -match '^/rest/api/space/([^/]+)$') {
                if ($Matches[1] -eq $SpaceKey) { Send $ctx 200 @{ key = $SpaceKey; name = 'Documentation' } } else { Err $ctx 404 'No space found' }
                continue
            }
            if ($method -eq 'GET' -and $path -match '^/rest/api/content/(\d+)$') {
                $p = $pages[$Matches[1]]
                if (-not $p) { Err $ctx 404 'No content found'; continue }
                Send $ctx 200 (Page-Json $p); continue
            }
            if ($method -eq 'GET' -and $path -eq '/rest/api/content') {
                $res = @(); foreach ($p in $pages.Values) { if ($p.space -eq $query['spaceKey'] -and $p.title -eq $query['title']) { $res += (Page-Json $p) } }
                Send $ctx 200 @{ results = $res; size = $res.Count; start = 0; limit = 25 }; continue
            }
            if ($method -eq 'POST' -and $path -eq '/rest/api/content') {
                if ($body.type -ne 'page' -or -not $body.title) { Err $ctx 400 'type page and title are required'; continue }
                if ($body.space.key -ne $SpaceKey) { Err $ctx 400 'unknown space'; continue }
                $par = $pages[[string]$body.ancestors[0].id]
                if (-not $par) { Err $ctx 400 'ancestor not found'; continue }
                $problem = Check-Storage $body.body
                if ($problem) { Err $ctx 400 $problem; continue }
                $dup = $false; foreach ($p in $pages.Values) { if ($p.title -eq $body.title) { $dup = $true } }
                if ($dup) { Err $ctx 400 "A page with this title already exists: $($body.title)"; continue }
                $script:nextId++
                $id = [string]$nextId
                $pages[$id] = @{ id = $id; title = $body.title; space = $SpaceKey; parent = $par.id; version = 1; body = $body.body.storage.value; labels = (New-Object System.Collections.ArrayList); props = @{} }
                Send $ctx 200 (Page-Json $pages[$id]); continue
            }
            if ($method -eq 'PUT' -and $path -match '^/rest/api/content/(\d+)$') {
                $p = $pages[$Matches[1]]
                if (-not $p) { Err $ctx 404 'No content found'; continue }
                if ([int]$body.version.number -ne $p.version + 1) { Err $ctx 409 "Version must be $($p.version + 1) but was $($body.version.number)"; continue }
                $problem = Check-Storage $body.body
                if ($problem) { Err $ctx 400 $problem; continue }
                $p.title = $body.title; $p.body = $body.body.storage.value; $p.version++
                Send $ctx 200 (Page-Json $p); continue
            }
            if ($method -eq 'GET' -and $path -match '^/rest/api/content/(\d+)/child/page$') {
                $kids = @(); foreach ($k in ($pages.Keys | Sort-Object)) { if ($pages[$k].parent -eq $Matches[1]) { $kids += (Page-Json $pages[$k]) } }
                $start = 0; if ($query['start']) { $start = [int]$query['start'] }
                $limit = $PageSize; if ($query['limit']) { $limit = [Math]::Min([int]$query['limit'], $PageSize) }
                $slice = @($kids | Select-Object -Skip $start -First $limit)
                $r = @{ results = $slice; size = $slice.Count; start = $start; limit = $limit; _links = @{ context = $Context } }
                if ($start + $slice.Count -lt $kids.Count) { $r._links.next = "/rest/api/content/$($Matches[1])/child/page?limit=$limit&start=$($start + $slice.Count)&expand=metadata.labels" }
                Send $ctx 200 $r; continue
            }
            if ($method -eq 'POST' -and $path -match '^/rest/api/content/(\d+)/label$') {
                $p = $pages[$Matches[1]]
                if (-not $p) { Err $ctx 404 'No content found'; continue }
                foreach ($l in @($body)) { if ($p.labels -notcontains $l.name) { [void]$p.labels.Add($l.name) } }
                $ls = @(); foreach ($l in $p.labels) { $ls += @{ prefix = 'global'; name = $l } }
                Send $ctx 200 @{ results = $ls; size = $ls.Count }; continue
            }
            if ($method -eq 'DELETE' -and $path -match '^/rest/api/content/(\d+)/label/([^/]+)$') {
                $p = $pages[$Matches[1]]
                if (-not $p) { Err $ctx 404 'No content found'; continue }
                $p.labels.Remove($Matches[2]); Send $ctx 204 $null; continue
            }
            if ($path -match '^/rest/api/content/(\d+)/property(/([^/]+))?$') {
                $p = $pages[$Matches[1]]; $key = $Matches[3]
                if (-not $p) { Err $ctx 404 'No content found'; continue }
                if ($method -eq 'GET' -and $key) {
                    if ($p.props.ContainsKey($key)) { Send $ctx 200 @{ key = $key; value = $p.props[$key].value; version = @{ number = $p.props[$key].version } } } else { Err $ctx 404 'No property' }
                    continue
                }
                if ($method -eq 'POST' -and -not $key) {
                    if ($p.props.ContainsKey($body.key)) { Err $ctx 409 'property exists'; continue }
                    $p.props[$body.key] = @{ value = $body.value; version = 1 }
                    Send $ctx 200 @{ key = $body.key; value = $body.value; version = @{ number = 1 } }; continue
                }
                if ($method -eq 'PUT' -and $key) {
                    if (-not $p.props.ContainsKey($key)) { Err $ctx 404 'No property'; continue }
                    if ([int]$body.version.number -ne $p.props[$key].version + 1) { Err $ctx 409 'property version conflict'; continue }
                    $p.props[$key] = @{ value = $body.value; version = [int]$body.version.number }
                    Send $ctx 200 @{ key = $key; value = $body.value; version = @{ number = [int]$body.version.number } }; continue
                }
            }
            Err $ctx 404 "mock has no route for $method $path"
        } catch {
            try { Err $ctx 500 $_.Exception.Message } catch { }
        }
    }
} finally { $listener.Stop() }
