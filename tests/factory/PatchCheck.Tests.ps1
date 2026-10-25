# Pester 3 tests for the patch checks in Guards.psm1 (factory-runtime spec section 3, step 11; P6). Every patch here is
# real git output from tests/factory/fixtures, captured with the launcher's exact command.
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Guards.psm1') -Force
Import-Module (Join-Path $root 'scripts\factory\Triage.psm1') -Force
Import-Module (Join-Path $here 'Fixtures.psm1') -Force
$rules = Import-PatchRules (Join-Path $root 'scripts\factory\PatchRules.psd1')
$routing = Get-Content (Join-Path $root 'rules\routing.json') -Raw | ConvertFrom-Json
$forms = Get-Fixture 'git-patch-forms'
$refused = Get-Fixture 'git-patch-refused'
$missing = Test-FixturesMissing @('git-patch-forms', 'git-patch-refused')

Describe 'PatchRules.psd1' {
    It 'is data the factory reads without running it, with the new denied paths' {
        $rules.Allowed | Should Be '^(src|tests|styleguide/Company|docs/user-guide|changes)/'
        $rules.MaxLines | Should Be 800
        foreach ($p in "src/A/A.csproj.user", "src/A/bin/x.cs", "src/A/obj/x.cs", "tests/Directory.Build.props", "src/x.targets", "src/csc.rsp", "src/A/A.vbproj",
                       "src/A/A.fsproj", "src/A/Shared.projitems", "src/global.json", "src/Directory.Solution.props", "src/A.sln", "src/CLAUDE.md", "src/.claude/settings.json",
                       "src/.gitattributes", "src/.gitmodules", "src/.lfsconfig", "tests/x.runsettings", "tests/xunit.runner.json", "src/.editorconfig",
                       "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1", "tests/e2e/flaui/Deps.csproj", "src/Directory.Packages.props", "src/nuget.config",
                       "src/A/packages.lock.json", "SRC/A/A.CSPROJ".Replace("SRC", "src"), "src/A/Bin/x.cs") {
            Test-PatchPath $p $rules | Should Match 'no fix may change'
        }
        foreach ($p in "src/FlareAI.Core/Style/Ignore.cs", "tests/FlareAI.Core.Tests/IgnoreTests.cs", "tests/e2e/scenarios/new-one.ps1", "changes/fix.md", "docs/user-guide/Content/x.htm") {
            Test-PatchPath $p $rules | Should BeNullOrEmpty
        }
        Test-PatchPath "scripts/factory/x.ps1" $rules | Should Match 'outside the paths'
        Test-PatchPath "src/../scripts/x.ps1" $rules | Should Match 'leading / or \.\.'
        Test-PatchPath "/src/x.cs" $rules | Should Match 'leading / or \.\.'
    }
}

Describe 'Test-PatchText on real git output (P6)' {
    It (Get-FixtureTitle 'accepts each of the five forms, and several files in one patch' @('git-patch-forms')) -Skip:$missing {
        $want = @{ "change" = "change"; "new" = "new"; "new-empty" = "new-empty"; "deleted" = "deleted"; "deleted-empty" = "deleted-empty" }
        foreach ($c in @($forms.cases)) {
            $c.exit | Should Be 0
            $r = Test-PatchText $c.stdout $rules
            ($c.case + ': ' + ($r.problems -join '; ')) | Should Be ($c.case + ': ')
            $r.ok | Should Be $true
            if ($want.ContainsKey($c.case)) { @($r.files).Count | Should Be 1; $r.files[0].form | Should Be $want[$c.case] }
        }
        @((Test-PatchText (Get-FixtureCase $forms 'several').stdout $rules).files | ForEach-Object form) -join ',' | Should Match 'change'
    }
    It (Get-FixtureTitle 'reads hunks by their counts, so content that looks like a header is still content' @('git-patch-forms')) -Skip:$missing {
        $r = Test-PatchText (Get-FixtureCase $forms 'content-looks-like-headers').stdout $rules
        $r.ok | Should Be $true
        $r.files[0].added | Should Be 4
    }
    It (Get-FixtureTitle 'keeps CR characters and a missing final newline as content' @('git-patch-forms')) -Skip:$missing {
        (Test-PatchText (Get-FixtureCase $forms 'crlf-lines').stdout $rules).ok | Should Be $true
        (Test-PatchText (Get-FixtureCase $forms 'no-newline-at-end').stdout $rules).ok | Should Be $true
    }
    It (Get-FixtureTitle 'refuses every refused case, the tool config names included' @('git-patch-refused')) -Skip:$missing {
        foreach ($c in @($refused.cases)) {
            $r = Test-PatchText $c.stdout $rules
            "$($c.case) ok=$($r.ok)" | Should Be "$($c.case) ok=False"
        }
        foreach ($n in "path:tests/App.runsettings", "path:tests/xunit.runner.json", "path:src/.editorconfig") { @($refused.cases | Where-Object case -eq $n).Count | Should Be 1 }
    }
    It (Get-FixtureTitle 'says why: modes, renames, symlinks, binary content and the no-prefix form' @('git-patch-refused')) -Skip:$missing {
        (Test-PatchText (Get-FixtureCase $refused 'mode-change').stdout $rules).problems -join ' ' | Should Match 'old mode'
        (Test-PatchText (Get-FixtureCase $refused 'exec-bit').stdout $rules).problems -join ' ' | Should Match 'new file mode'
        (Test-PatchText (Get-FixtureCase $refused 'symlink').stdout $rules).problems -join ' ' | Should Match 'new file mode'
        (Test-PatchText (Get-FixtureCase $refused 'binary').stdout $rules).problems -join ' ' | Should Match 'Binary files'
        (Test-PatchText (Get-FixtureCase $refused 'rename').stdout $rules).problems -join ' ' | Should Match 'two different paths'
        (Test-PatchText (Get-FixtureCase $refused 'no-prefix').stdout $rules).problems -join ' ' | Should Match "isn't a file header"
        (Test-PatchText (Get-FixtureCase $refused 'package-change').stdout $rules).problems -join ' ' | Should Match 'no fix may change'
        (Test-PatchText (Get-FixtureCase $refused 'path:src/App/Tool.CSPROJ').stdout $rules).problems -join ' ' | Should Match 'no fix may change'
        (Test-PatchText (Get-FixtureCase $refused 'path:README.md').stdout $rules).problems -join ' ' | Should Match 'outside the paths'
    }
    It (Get-FixtureTitle 'refuses a patch over the line cap' @('git-patch-forms')) -Skip:$missing {
        $tight = [pscustomobject]@{ Allowed = $rules.Allowed; Denied = $rules.Denied; MaxLines = 1 }
        (Test-PatchText (Get-FixtureCase $forms 'change').stdout $tight).problems -join ' ' | Should Match 'changes 2 lines, over 1'
    }
    It (Get-FixtureTitle 'refuses a patch cut short, or with a stray line after a hunk' @('git-patch-forms')) -Skip:$missing {
        $t = (Get-FixtureCase $forms 'change').stdout
        (Test-PatchText ($t.Substring(0, $t.LastIndexOf("`n", $t.Length - 2) + 1)) $rules).ok | Should Be $false
        (Test-PatchText ($t + "trailing text`n") $rules).ok | Should Be $false
        (Test-PatchText "" $rules).problems | Should Be "the patch changes nothing"
    }
    It (Get-FixtureTitle 'gives Get-DiffFacts the paths, and the uncovered ones, from the patch itself' @('git-patch-forms')) -Skip:$missing {
        $t = (Get-FixtureCase $forms 'several').stdout
        $check = Test-PatchText $t $rules
        $facts = Get-DiffFacts (ConvertTo-Numstat $check) $t "" $routing
        @($facts.paths | Sort-Object) -join ',' | Should Be 'changes/fix-thing.md,src/App/Gone.cs,src/App/Thing.cs,tests/App.Tests/ThingTests.cs'
        @($facts.uncovered).Count | Should Be 0
        $t2 = (Get-FixtureCase $forms 'new-empty').stdout
        @((Get-DiffFacts (ConvertTo-Numstat (Test-PatchText $t2 $rules)) $t2 "" $routing).uncovered).Count | Should Be 0
    }
}

Describe 'The token in text' {
    $token = "sk-ant-oat01-" + ("Ab3dEf9h" * 12)
    It 'is found as is, in hex of either case, and in base64 at each alignment' {
        Test-TokenText "x $token y" $token | Should Be $true
        $hex = -join ([Text.Encoding]::UTF8.GetBytes($token) | ForEach-Object { $_.ToString("x2") })
        Test-TokenText "x$hex" $token | Should Be $true
        Test-TokenText $hex.ToUpperInvariant() $token | Should Be $true
        foreach ($k in 0, 1, 2) {
            $pre = [Text.Encoding]::UTF8.GetBytes("abc".Substring(0, $k) + "padding before it ")
            $all = $pre + [Text.Encoding]::UTF8.GetBytes($token) + [Text.Encoding]::UTF8.GetBytes(" and after")
            Test-TokenText ("+ data: " + [Convert]::ToBase64String($all)) $token | Should Be $true
        }
    }
    It 'finds nothing in ordinary text, and needs a token to look for' {
        Test-TokenText "class Thing { int A() => 1; }" $token | Should Be $false
        Test-TokenText "anything" "" | Should Be $false
    }
}
