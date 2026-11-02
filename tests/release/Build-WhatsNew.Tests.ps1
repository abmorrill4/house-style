# Pester 3 tests (built into Windows PowerShell 5.1): powershell.exe -Command "Invoke-Pester tests\release"
# Builds a throwaway git repo with tags and changes/ files, then checks the shipped whats-new.md and the release notes.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $here '..\..\scripts\release\Build-WhatsNew.ps1'

function New-Repo {
    $r = Join-Path $env:TEMP ('wn-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force (Join-Path $r 'src\FlareAI.Flare'), (Join-Path $r 'changes'), (Join-Path $r 'docs\release-notes') | Out-Null
    git -C $r init -q; git -C $r config user.email t@example.com; git -C $r config user.name t; git -C $r config commit.gpgsign false
    $r
}
function Commit($r, $msg) { git -C $r add -A; git -C $r commit -q -m $msg }
function Put($r, $rel, $text) { Set-Content (Join-Path $r $rel) $text -Encoding UTF8 }

Describe 'Build-WhatsNew' {
    $r = New-Repo
    $hand = Join-Path $r 'docs\release-notes\0.3.6.md'
    Put $r 'src\FlareAI.Flare\whats-new.md' "# What's new in House Style`n`n<!-- intro -->`n`n## 0.3.4`n`n- Hand-written for 0.3.4.`n`n## 0.3.3`n`n- Old release."
    Commit $r 'base'; git -C $r tag v0.3.3
    Put $r 'changes\reopen.md' "- Reopen Issues brings back your last results."
    Commit $r 'reopen'
    Put $r 'changes\ignore.md' "- Ignore now sticks after you lint again."
    Commit $r 'ignore'

    It 'adds new notes to the hand-written section of the version being built' {
        $t = & $script -Version 0.3.4 -Repo $r
        $t | Should Match '(?s)## 0\.3\.4.*Hand-written for 0\.3\.4.*Reopen Issues.*Ignore now sticks.*## 0\.3\.3'
    }
    It 'gives a version with no hand-written section its own section, newest first' {
        git -C $r tag v0.3.4
        Put $r 'changes\rules.md' "- Edit Rule changes a rule in your guide."
        Commit $r 'rules'
        $t = & $script -Version 0.3.5 -Repo $r
        $t | Should Match '(?s)## 0\.3\.5\s+### Improved\s+- Edit Rule.*## 0\.3\.4.*Reopen Issues.*## 0\.3\.3'
        $t | Should Not Match '(?s)## 0\.3\.5.*Reopen Issues.*## 0\.3\.4'   # 0.3.4's notes stay in 0.3.4
    }
    It 'keeps earlier generated sections in later releases' {
        git -C $r tag v0.3.5
        Put $r 'changes\later.md' "- Something later."
        Commit $r 'later'
        $t = & $script -Version 0.3.6 -Repo $r
        $t | Should Match '(?s)## 0\.3\.6\s+### Improved\s+- Something later.*## 0\.3\.5\s+### Improved\s+- Edit Rule.*## 0\.3\.4'
    }
    It 'writes release notes for one version' {
        & $script -Version 0.3.6 -Repo $r -NotesOnly | Should Match 'Something later'
        & $script -Version 0.3.6 -Repo $r -NotesOnly | Should Not Match 'Edit Rule'
    }
    It 'puts the hand-written release notes first when there are some' {
        Set-Content $hand "Big release." -Encoding UTF8
        $n = (& $script -Version 0.3.6 -Repo $r -NotesOnly) -join "`n"
        $n | Should Match '(?s)Big release\..*Something later'
    }
    It 'puts new notes before a closing Install section' {
        Set-Content $hand "Big release.`n`n## Changes`n`n- Hand.`n`n## Install or update`n`nRun the installer." -Encoding UTF8
        $n = (& $script -Version 0.3.6 -Repo $r -NotesOnly) -join "`n"
        $n | Should Match '(?s)- Hand\..*Something later.*## Install or update'
        Remove-Item $hand
    }
    It 'shows a CI build the next release' {
        & $script -Version 0.0.0-ci -Repo $r | Should Match '## 0\.3\.6\s+### Improved\s+- Something later'
    }
    It 'skips a note that was added and later removed' {
        Put $r 'changes\gone.md' "- Gone."
        Commit $r 'gone'
        Remove-Item (Join-Path $r 'changes\gone.md'); Commit $r 'remove gone'
        & $script -Version 0.3.6 -Repo $r -NotesOnly | Should Not Match 'Gone'
    }
    It 'says something when there are no notes at all' {
        $empty = New-Repo
        Put $empty 'src\FlareAI.Flare\whats-new.md' "# What's new"; Commit $empty 'base'
        $n = (& $script -Version 0.9.0 -Repo $empty -NotesOnly) -join "`n"
        $n | Should Match '^Small fixes to make House Style more reliable\.'
        $n | Should Match '(?s)## Install or update(?!.*\n## )'
    }
}

Describe 'Build-WhatsNew grouping' {
    function New-Notes {
        $r = New-Repo
        Put $r 'src\FlareAI.Flare\whats-new.md' "# What's new"
        Commit $r 'base'; git -C $r tag v0.1.0
        $r
    }
    function Notes($r) { (& $script -Version 0.1.1 -Repo $r -NotesOnly) -join "`n" }
    function Add-Note($r, $name, $text) { Put $r "changes\$name" $text; Commit $r $name }

    It 'orders the groups New, Improved, Fixed whatever the order they were added' {
        $r = New-Notes
        Add-Note $r 'a.md' "Kind: Fixed`n- Fixes a thing."
        Add-Note $r 'b.md' "Kind: New`n- Adds a thing."
        Add-Note $r 'c.md' "- Improves a thing."
        Notes $r | Should Match '(?s)### New.*Adds a thing.*### Improved.*Improves a thing.*### Fixed.*Fixes a thing'
    }
    It 'leaves out a group with no bullets' {
        $r = New-Notes
        Add-Note $r 'a.md' "Kind: Fixed`n- Fixes a thing."
        Add-Note $r 'b.md' "Kind: New`n- Adds a thing."
        $n = Notes $r
        $n | Should Not Match '### Improved'
        $n | Should Match '### New'
    }
    It 'counts a note with no kind as Improved' {
        $r = New-Notes
        Add-Note $r 'c.md' "- Improves a thing."
        Notes $r | Should Match '### Improved\s+- Improves a thing'
    }
    It 'keeps the order the notes were added within a group' {
        $r = New-Notes
        Add-Note $r 'a.md' "- First thing."
        Add-Note $r 'b.md' "Kind: Improved`n- Second thing."
        Notes $r | Should Match '(?s)First thing.*Second thing'
    }
    It 'starts with the hand-written summary and ends with its Install section' {
        $r = New-Notes
        Add-Note $r 'a.md' "- Improves a thing."
        Set-Content (Join-Path $r 'docs\release-notes\0.1.1.md') "Summary sentence.`n`n## Changes`n`n- Hand.`n`n## Install or update`n`nRun the installer." -Encoding UTF8
        $n = Notes $r
        $n | Should Match '^Summary sentence\.'
        $n | Should Match '(?s)- Hand\..*### Improved.*Improves a thing.*## Install or update'
        $n | Should Match 'Run the installer\.$'
    }
    It 'adds no summary when there is no hand-written file, and ends with the Install section' {
        $r = New-Notes
        Add-Note $r 'a.md' "Kind: New`n- Adds a thing."
        $n = Notes $r
        $n | Should Match '^### New'
        $n | Should Match 'Unblock'
        $n | Should Match '(?s)## Install or update(?!.*\n## )'
    }
    It 'reports two notes that open with the same five words' {
        $r = New-Notes
        Add-Note $r 'a.md' "- Turn AI on or off from the House Style tab."
        Add-Note $r 'b.md' "- Turn AI on or off from the House Style pane."
        (& $script -Version 0.1.1 -Repo $r -Check) -join "`n" | Should Match 'same opening'
    }
    It 'reports a bullet that breaks the copy rules' {
        $r = New-Notes
        Add-Note $r 'a.md' "- fixes: a thing"
        $p = (& $script -Version 0.1.1 -Repo $r -Check) -join "`n"
        $p | Should Match 'capital'
        $p | Should Match 'colon'
        $p | Should Match 'full stop'
    }
    It 'reports nothing for clean notes' {
        $r = New-Notes
        Add-Note $r 'a.md' "Kind: New`n- Adds a thing."
        & $script -Version 0.1.1 -Repo $r -Check | Should BeNullOrEmpty
    }
}

Describe 'real changes folder' {
    $root = Resolve-Path (Join-Path $here '..\..')
    $hasTags = [bool](git -C $root tag -l 'v*')
    It 'has notes that follow the copy rules and do not open the same way' {
        if (-not $hasTags) { return }
        & $script -Version 0.0.0-ci -Repo $root -Check | Should BeNullOrEmpty
    }
    It 'builds release notes that end with the closing section' {
        if (-not $hasTags) { return }
        $n = (& $script -Version 0.0.0-ci -Repo $root -NotesOnly) -join "`n"
        $n | Should Match '(?s)## Install or update(?!.*\n## )'
    }
}
