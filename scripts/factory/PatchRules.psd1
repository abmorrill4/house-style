# The paths a fix's patch may touch, read as data (Import-PowerShellDataFile), never run. The factory loads this file
# once a run from the run's base (section 3, step 8 of the factory-runtime spec), and the Flare check loads it from its
# own clone of main (Invoke-FlareCheck.ps1). A fix can't change it: it's outside Allowed.
# Every pattern is a .NET regular expression matched against the patch's forward-slash path, ignoring case.
@{
    # A path must match this.
    Allowed = '^(src|tests|styleguide/Company|docs/user-guide|changes)/'

    # And none of these. Build and package wiring (it runs during a restore or build), tool settings, agent settings,
    # git attributes, build output and the e2e harness.
    Denied = @(
        '\.user$'
        '(^|/)(bin|obj)/'
        '\.(props|targets|rsp)$'
        '\.[A-Za-z0-9]*proj$'
        '\.projitems$'
        '(^|/)Directory\.Build\.'
        '(^|/)Directory\.Packages\.props$'
        '(^|/)Directory\.Solution\.'
        '\.sln$'
        '(^|/)global\.json$'
        '(^|/)nuget\.config$'
        '(^|/)packages\.lock\.json$'
        '(^|/)CLAUDE\.md$'
        '(^|/)\.claude/'
        '(^|/)\.gitattributes$'
        '(^|/)\.gitmodules$'
        '(^|/)\.lfsconfig$'
        '\.runsettings$'
        '(^|/)xunit\.runner\.json$'
        '(^|/)\.editorconfig$'
        '^tests/e2e/(Run-FlareE2E\.ps1|FlareE2E\.psm1)$'
        '^tests/e2e/flaui/'
    )

    # Added plus removed lines, over every file.
    MaxLines = 800
}
