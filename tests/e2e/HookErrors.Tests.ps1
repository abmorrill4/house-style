# Pester 3. The hook's errors list the valid names, so a scenario can be fixed from the log alone. These read the source;
# tests/FlareAI.Flare.Tests/E2EHookErrorTests.cs runs the helpers.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$hook = [IO.File]::ReadAllText((Join-Path $here "..\..\src\FlareAI.Flare\E2EHook.cs"))
$panel = [IO.File]::ReadAllText((Join-Path $here "..\..\src\FlareAI.Flare\UI\FindingsPanel.cs"))

Describe "E2EHook errors" {
    It "keeps the Verbs list equal to the switch's case labels" {
        $cases = @([regex]::Matches($hook, 'case "(\w+)":') | ForEach-Object { $_.Groups[1].Value })
        $list = [regex]::Match($hook, 'Verbs =\s*\{(.*?)\};', 'Singleline').Groups[1].Value
        $verbs = @([regex]::Matches($list, '"(\w+)"') | ForEach-Object { $_.Groups[1].Value })
        ($verbs -join ",") | Should Be ($cases -join ",")
    }
    It "lists the valid names in each error" {
        foreach ($m in 'Unknown verb', 'No open dialog titled', 'No enabled button', 'No tab', 'No row with', 'No box named') {
            $line = [regex]::Match($hook, [regex]::Escape('"' + $m) + '.*?;', 'Singleline').Value
            $line | Should Match "Valid\("
        }
        $panel | Should Match 'No menu item " \+ prefix \+ E2EHook\.Valid\('
    }
}
