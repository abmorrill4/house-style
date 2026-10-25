# Test helpers for the captured fixtures in tests/factory/fixtures (factory-runtime spec section 12). Parser tests read
# real output only through Get-Fixture. A fixture that isn't captured yet skips its tests with the migration step that
# captures it, so CI stays green until then.
$FixtureDir = Join-Path $PSScriptRoot "fixtures"

function Get-ExpectedFixtures { @((Get-Content (Join-Path $FixtureDir "expected.json") -Raw | ConvertFrom-Json).fixtures) }

# The fixture, or $null when it isn't captured yet.
function Get-Fixture([string]$name) {
    $p = Join-Path $FixtureDir "$name.json"
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json
}

# One case's output from a fixture.
function Get-FixtureCase($fixture, [string]$case) {
    $c = @($fixture.cases | Where-Object { $_.case -eq $case })
    if ($c.Count -ne 1) { throw "Fixture $($fixture.name) has no case '$case'." }
    $c[0]
}

# $null when the fixture is here, else the reason its tests skip.
function Get-FixtureSkip([string]$name) {
    if (Test-Path -LiteralPath (Join-Path $FixtureDir "$name.json")) { return $null }
    $e = @(Get-ExpectedFixtures | Where-Object { $_.name -eq $name })
    $step = if ($e.Count) { $e[0].step } else { "?" }
    if ("$step" -match '^\d+$') { "fixture not captured yet (migration step $step)" } else { "fixture not captured yet ($step)" }
}

# A test title with the skip reason after it, so the skipped test says why.
function Get-FixtureTitle([string]$title, [string[]]$names) {
    $why = @($names | ForEach-Object { Get-FixtureSkip $_ } | Where-Object { $_ } | Select-Object -Unique)
    if ($why.Count) { "$title [skipped: $($why -join '; ')]" } else { $title }
}
function Test-FixturesMissing([string[]]$names) { [bool]@($names | Where-Object { Get-FixtureSkip $_ }).Count }

Export-ModuleMember -Function Get-ExpectedFixtures, Get-Fixture, Get-FixtureCase, Get-FixtureSkip, Get-FixtureTitle, Test-FixturesMissing -Variable FixtureDir
