<#
.SYNOPSIS Starts a private Bifrost gateway on this machine for the ai-* scenarios that use the company AI service
(Start-FlareE2E -Bifrost), and sets HOUSESTYLE_TEST_BIFROST_URL and HOUSESTYLE_TEST_BIFROST_KEY for this process.
.DESCRIPTION The gateway listens on localhost only, forwards to Anthropic with ANTHROPIC_API_KEY (the run's own test
key), and takes a gateway key made up for this run, so no Bifrost secret exists anywhere. The pinned build is
downloaded once into C:\HouseStyle\tools\bifrost and checked against its SHA-256 before every start. Returns the
gateway process; stop it when the run ends (Stop-Process).
#>
param([int]$Port = 8089, [string]$ToolsDir = "C:\HouseStyle\tools\bifrost")
$ErrorActionPreference = "Stop"
# The build verified in docs/bifrost-migration.md. Change both together; the hash is checked before every start.
$Version = "v2.3.2"
$Sha256 = "PIN-ME"   # ponytail: set from the first verified download (scripts/factory/README.md, owner step)
if (-not $env:ANTHROPIC_API_KEY) { throw "The test gateway needs ANTHROPIC_API_KEY to forward to Anthropic." }

$dir = Join-Path $ToolsDir $Version
$exe = Join-Path $dir "bifrost-http.exe"
New-Item -ItemType Directory -Force $dir | Out-Null
if (-not (Test-Path $exe)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    (New-Object Net.WebClient).DownloadFile("https://downloads.getmaxim.ai/bifrost/$Version/windows/amd64/bifrost-http.exe", $exe)
}
$actual = (Get-FileHash $exe -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { Remove-Item $exe -Force; throw "The Bifrost download's SHA-256 ($actual) isn't the pinned one, so it won't run." }

# A fresh app folder per run: the config names the models and a gateway key that exists only for this run.
$app = Join-Path ([IO.Path]::GetTempPath()) ("hs-bifrost-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $app | Out-Null
$vk = "sk-bf-" + [guid]::NewGuid().ToString("N")
$models = @("claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5")
@{
    client = @{ enforce_auth_on_inference = $true }
    providers = @{ anthropic = @{ keys = @(@{ name = "anthropic-key-1"; value = "env.ANTHROPIC_API_KEY"; weight = 1.0; models = $models }) } }
    governance = @{ virtual_keys = @(@{ id = "vk-e2e"; name = "e2e"; value = $vk; is_active = $true
        provider_configs = @(@{ provider = "anthropic"; weight = 1.0; key_ids = @("*"); allowed_models = $models }) }) }
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $app "config.json") -Encoding UTF8

$proc = Start-Process $exe -ArgumentList "-app-dir", "`"$app`"", "-host", "127.0.0.1", "-port", "$Port" -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $app "out.log") -RedirectStandardError (Join-Path $app "err.log")
$url = "http://localhost:$Port"
$deadline = (Get-Date).AddSeconds(60)
while ($true) {
    try { if ((Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { break } } catch { }
    if ($proc.HasExited -or (Get-Date) -gt $deadline) {
        $proc | Stop-Process -Force -ErrorAction SilentlyContinue
        throw "The test gateway didn't start (its log is in $app)."
    }
    Start-Sleep -Seconds 2
}
$env:HOUSESTYLE_TEST_BIFROST_URL = $url
$env:HOUSESTYLE_TEST_BIFROST_KEY = $vk
$proc
