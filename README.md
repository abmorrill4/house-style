<picture>
  <source media="(prefers-color-scheme: dark)" srcset="site/brand/lockup-white.svg">
  <img src="site/brand/lockup.svg" alt="House Style" height="48">
</picture>

# House Style for MadCap Flare

House Style is a free plugin for MadCap Flare Desktop. It lints your topics against the Google developer
documentation style guide and your team's own rules, and lists the issues it finds. You change or ignore each one, and
every change is a Flare tracked change.

Linting runs on your computer at no cost. An AI review, rewrites and other AI features are optional. They use your
own Anthropic API key or your company's AI gateway.

Website: <https://gethousestyle.com/>

## Install

1. Download `HouseStyle-<version>.zip` from [Releases](../../releases) and extract all of it.
2. Close Flare.
3. Double-click `Install.cmd` and approve the administrator prompt. You see the prompt only the first time.
4. Start Flare. The installer turns House Style on, so the House Style tab is on the ribbon.
5. A four-step setup asks how you use House Style, how you use AI and where your style rules are. Then a short
   practice shows you a first lint. To practice again, select **Help > Start the Tour Again**.

House Style needs Flare 2026 in the Ribbon interface. Flare 2024 and 2025 aren't tested. Updates install themselves
when you close Flare. If something goes wrong, see the [install page](https://gethousestyle.com/install.html)
or `docs/README-INSTALL.md`.

## Docs

| For | Read |
|---|---|
| Writers | The user guide, a Flare project in `docs/user-guide` (open `FlareAI-Guide.flprj`) |
| Pilot testers | `docs/TESTER-GUIDE.md` |
| Style guide owners | User guide topics *Set up a team style guide*, *Maintain the style guide* and *Publish the style guide to Confluence* |
| Gateway admins | `docs/bifrost-migration.md` |
| What changed | `docs/release-notes/` and **House Style > Help > What's New** |
| AI cost | `docs/testing/ai-cost.md` |

## Privacy

Style checks run on your computer. When you use an AI feature, the text being checked (for alt text, the image) and the
rules that apply go to Anthropic under your key, or to your company's gateway. Your key is encrypted with Windows DPAPI
and never logged. House Style sends a report only when you choose Send, or by itself when **Send error reports
automatically** is checked in Settings. Reports never include your topic text unless you type it in, and never your key
or settings that could hold a secret. You can see exactly what a report holds before it goes. Once a day House Style
asks GitHub for the latest release, with no text, key or identifier. Details:
[Privacy](https://gethousestyle.com/privacy.html).

## For IT

- `Install.ps1 -Silent` installs with no questions. Exit code 0 means installed.
- `Install.ps1 -Flare 21` picks a Flare version by folder number (22 is Flare 2026, 21 is Flare 2025).
- `Install.ps1 -NoAutoUpdate`, or an `update-policy.json` file with `{"autoUpdate": false}` in the plugin folder,
  turns updates off. Settings then shows "Set by your IT department".
- Updates are downloaded from this repository's GitHub releases and checked against the release's SHA-256 checksum
  before they install.

## The style guide

The style guide is a Flare project. The plugin ships one (`styleguide/`); a team keeps its own copy in a shared folder.

- `Content/Resources/StyleGuide/Google/`: the Google developer documentation style guide, compiled into rules. Generated
  by `tools/FlareAI.GuideCompiler` from `tools/google-style/source`. Don't edit it.
- `Content/Resources/StyleGuide/Company/`: the team's rules. They win over Google's. `Google-overrides.htm` holds House
  Style's default changes to Google's rules; a team's own row for the same rule wins.
- The open Flare project adds its variables, glossary terms and its own `Content/Resources/StyleGuide/`. They win over
  both.

Rule format, in short (the guide's *About this style guide* topic has the rest):

- **Bullets** are rules for the AI. Nested `Bad:` and `Good:` items make them testable.
- **Avoid / Use / Severity / Why / Example** tables become rules with exact fixes. `(delete)` in Use deletes the text;
  an empty Use only flags it. A **Pattern** column takes a regular expression.
- **Rule / Setting / Reason** tables disable rules or change their level. House Style writes these rows when someone
  uses **Disable Rule…**.
- **Content** condition tags limit a rule to sections with that content (Steps, Code, UI and others). **Product** tags
  limit it to products in the guide's Product tag set.
- Every rule has a permanent ID (`company:terminology/1`) that the AI cites and team feedback is keyed on. It's stored
  on the rule as `id="rule-…"`, hidden in Flare's editor.

Team files in the guide's `FlareAI/` folder: `owners.txt` (who may change rules; others send requests),
`layer.txt`, `attribution.txt`, `models.json` and `prompts/`. See the user guide topic *Set up a team style guide*.

To publish the guide to Confluence Data Center, use `scripts/publish-guide-to-confluence.ps1`. See the user guide topic
*Publish the style guide to Confluence*.

### Keeping the Google rules current

`tools/google-style/snapshot.py` copies developers.google.com/style (CC BY 4.0) into `tools/google-style/source` with a
hash per page. `tools/FlareAI.GuideCompiler` recompiles changed pages into `Google/` topics. Every source unit must be
covered by a rule or marked not applicable, and every table row must pass the validator. `tools/google-style/coverage.md`
is the report. These are maintainer tools; writers never need them.

## Develop

Prerequisites: Windows, MadCap Flare Desktop 2026, .NET SDK 8 or later, Git, and a `.env` at the repository root (git-ignored)
with `ANTHROPIC_API_KEY=...`. Architecture: `PLAN.md`. Flare API notes: `docs/flare-api-notes.md`.

```powershell
powershell.exe -File .\scripts\bootstrap.ps1   # find Flare, copy the API DLL, dump the API, build
dotnet build FlareAI.sln -c Debug
dotnet test
.\scripts\smoke-api.ps1        # one real API call with the key from .env
.\scripts\install-plugin.ps1   # as admin: copy the build into Flare's Plugins folder
.\scripts\run-flare.ps1        # start Flare with the key from .env
```

- `tools/FlareAI.Mcp`: the same style engine as an MCP stdio server (`lint`, `style_lookup`, `glossary_lookup`,
  `validate_guide`, `guide_examples`, `mine_style`). `.mcp.json` registers it for Claude Code.
- `tools/FlareAI.Bench`: lints the test corpus (`corpus/`) against its ground truth.
- Test plan, corpus and bug template: `docs/testing/`.
- Releases: push a `v*` tag. CI builds, tests and publishes the zip with `checksums.txt`.

### Export for LLMs in CI

**House Style > Export for LLMs** turns built HTML5 or Clean HTML output into one Markdown file per topic, plus
`llms.txt` and `llms-full.txt`. For `madbuild`, add a post-build event to the target:

```
"<Flare plugins folder>\FlareAI\FlareAI.Mcp.exe" --llm-export "$(OutputDirectory)" --url https://docs.example.com
```

## Where things live

| What | Where |
|---|---|
| Settings, including the encrypted key | `%APPDATA%\FlareAI\settings.json` |
| Diagnostics log | `%APPDATA%\FlareAI\diag.log` |
| Your own rule changes, when you have no team guide | `%APPDATA%\FlareAI\StyleGuide` |

## License

Code: MIT (`LICENSE`). The
Google style rules are derived from the [Google developer documentation style guide](https://developers.google.com/style)
under CC BY 4.0. See `NOTICE.md`. Not affiliated with MadCap Software or Google.
