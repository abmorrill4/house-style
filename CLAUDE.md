# CLAUDE.md: House Style (MadCap Flare plugin)

House Style lints and rewrites Flare topics against a layered style guide (Google, then the team's rules, then the open
project). It's a public repo (`abmorrill4/house-style`), it auto-updates every install, and its site is
gethousestyle.com. This file is for development sessions. Area notes load on their own when you work there:
`src/FlareAI.Flare/CLAUDE.md`, `src/FlareAI.Core/Update/CLAUDE.md`, `tools/feedback-worker/CLAUDE.md`.

## Start of every session
1. **Work in the main checkout `E:\HouseStyle\repos\house-style`, on a short branch from `origin/main`.** Sessions
   start in `E:\HouseStyle`. Add a worktree (`git worktree add ..\worktrees\<topic> -b <topic> origin/main`) only
   when another session is working in parallel, and remove it once its PR merges. Planning and design work go in
   `E:\HouseStyle\planning`, never in a worktree.
2. **Continuing earlier work?** The SessionStart hook lists the newest handoff notes. When the user says
   "continue", "pick up" or names a topic, read the matching note before anything else. Notes describe the past, so
   check them against `git log` and `gh pr view` before acting on them.
3. **Ending or switching chats**, or when context is getting long, run `/handoff` to write a note. The next chat
   then starts from that note and doesn't re-read the repo.

## Map
| Path | What it is |
|---|---|
| `src/FlareAI.Core` (net40) | The Flare-agnostic engine. `Style/` holds the guide, the linter (`StyleEngine`, `GuideLayer`) and the rule catalog. `Actions/` holds the review, rewrite, team, requests and feedback code (`ActionRunner`, `ReviewSession`, `Team`, `GuideRequests`). `Editing/` holds XHTML validation and diffs. `Llm/` holds the Anthropic and gateway clients. `Settings/` holds DPAPI settings and first run. `Feedback/` holds the reports. `Update/` holds the self-update code. |
| `src/FlareAI.Flare` (net48) | The plugin: ribbon (`Plugin.cs`, `Commands.cs`), the editor bridge (`FlareDocumentAdapter`), WinForms UI (`UI/`), `whats-new.md`. |
| `styleguide/` | The bundled guide project. `Google/` is generated, so never edit it by hand. `Company/` holds the starter team rules. |
| `tools/` | `FlareAI.Mcp` (the engine over MCP), `FlareAI.Updater` (swaps installs), `feedback-worker` (Cloudflare Worker), and dev tools that don't ship: `Bench`, `GuideCompiler`, `Labeler`, `CorpusKit`, `Scoring`, `RefStubs`, `FixtureGen`, `google-style`. |
| `tests/` | `FlareAI.Core.Tests` (unit), `FlareAI.Scenarios` (end to end over `TestKit`'s fake editor, recorded LLM and workspace), `RuleFixtures` (one fixture per lint rule, a ratchet), plus GuideCompiler, Labeler, Scoring and Flare tests. |
| `corpus/` | About 1,740 generated test topics for Bench and Scenarios. This is data. Don't read it wholesale. |
| `scripts/` | Bootstrap, install, run Flare, package, release `Install.ps1`/`.cmd`, Confluence publisher. |
| `site/` | gethousestyle.com: static GitHub Pages, deployed by `pages.yml` on pushes to `main`. |
| `docs/` | `PLAN.md` (architecture), `flare-api-notes.md` (verified Flare behavior), `api-dump.txt` (the real API, authoritative), `testing/`, `release-notes/`, `user-guide/`. |
| `../../planning/` | Outside the repo and not git tracked: the roadmap, plans, research and handoff notes. The main checkout lives at `<project>/repos/house-style`. |

## Hard constraints
- **Runtimes and idioms.**
  - `FlareAI.Core` targets **net40**. `FlareAI.Flare` targets **net48**, because Flare's `B3.PluginAPIKit.dll` is
    .NET 4.8.
  - Use old-runtime idioms in both. Not allowed: `async`/`await`, `HttpClient`, `ValueTuple`, `System.Text.Json`.
    Use instead: `HttpWebRequest`, Newtonsoft.Json, and `ThreadPool` or `BackgroundWorker`.
  - C# 7.3 syntax is fine.
- **Core is portable.** `FlareAI.Core` never references `B3.PluginAPIKit`, WPF or WinForms. Flare types stay in
  `src/FlareAI.Flare`.
- **Every DOM write goes through `IDocumentAdapter`** as one tracked, undoable change. Use `ReplaceBlocks(...)` for
  validated block rewrites and `Edit(op, () => ReplaceSpan(...))` for word fixes. Never write an unvalidated
  fragment.
- **Style rules live only in a style guide project:** the bundled one, the team's, or the docs project's variables,
  glossary and StyleGuide. Never put them in prompts or C#. Google rules change through `Company/` Rule/Setting rows,
  or through a recompile with `tools/FlareAI.GuideCompiler`.
- **Nothing extra at runtime.** Everything ships in the plugin folder, with no Python, uv or Vale at runtime.
  `StyleEngine` is the linter.
- **The API key never appears in logs, output, reports or commits.** Settings store it with DPAPI. The dev fallback
  is `ANTHROPIC_API_KEY` from the git-ignored `.env` (`scripts\Load-DotEnv.ps1`).

## Security invariants (from the 2026-10-22 audit)
- **Shared folders are untrusted input.** That covers the team guide, the feedback folder and its `requests\`
  subfolder. Any writer can drop files there.
  - Never take a path, id, command or authorization decision from a shared file without validating it.
  - Request ids must match `GuideRequests.IsValidId`.
  - Project exceptions apply only from this machine's own ledger copy.
  - Never touch a UNC path that came from shared data.
- **Never run git in a guide folder with its own config.** Always pass `Git.Untrusted`. A shared `.git/config` can
  run programs.
- **Updates must be signed, and only the nightly release makes them.** Don't weaken `UpdateStager`'s signature check
  or the `contents: read` default in `build.yml` (see `src/FlareAI.Core/Update/CLAUDE.md`). Releases ship nightly with
  no approval click (owner decision 2026-10-24). What protects them instead: the `release` environment deploys only
  `v*` tags and holds the signing key, the release job refuses any run not started by a running `nightly.yml` on
  `main` (checked through the API) and any tag not on `main`, and the tag ruleset blocks moving or deleting `v*` tags.
  No agent ever holds a token. Don't add another way to tag.
- **The repo is public, so vulnerabilities go to private advisories, never public issues.** Use
  `gh api repos/abmorrill4/house-style/security-advisories`. Publish the advisory with the release that fixes it.
  Hardening items with no exploit path can be public issues labeled `security`.
- **Model output is data.** It reaches the document only through the validators in `Editing/`.

## Build and test
```powershell
dotnet build FlareAI.sln -c Debug
dotnet test tests/FlareAI.Core.Tests --filter "FullyQualifiedName~<Area>"   # fast, about 20 s for the whole project
dotnet test tests/FlareAI.Scenarios --filter "FullyQualifiedName~Governance" # end to end, about 2 min for the whole project
dotnet test FlareAI.sln -c Release                                           # what CI runs: about 1,480 tests, about 4 min
.\tools\FlareAI.Bench\bin\Debug\net48\FlareAI.Bench.exe --no-ai              # lint the corpus against ground truth (CI bench gate)
powershell.exe -File .\scripts\bootstrap.ps1   # first run on a machine (Windows PowerShell 5.1)
.\scripts\install-plugin.ps1                   # no admin needed if the Plugins folder is writable. Then .\scripts\run-flare.ps1 starts Flare with the dev key
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Install   # build, install and check in real Flare, no clicking (tests/e2e/README.md)
```
- **Prove a UI change in real Flare with `tests/e2e`,** not by hand. Add a scenario for the behavior you changed.
- **Which tests to run.** Run the narrowest project and filter that covers your change while you iterate. Run the
  full Release suite before you push.
- **Rule and guide changes** also need `RuleFixtures` and the bench gate. If a change is meant to move bench
  numbers, update the baseline with `--update-baseline`.
- **CI** (`build.yml`) runs on windows-latest against the `ref/` stubs, because `lib\B3.PluginAPIKit.dll` is absent
  there. If you use a new Flare API member, regenerate the stubs (see `src/FlareAI.Flare/CLAUDE.md`).

## Git, GitHub and releases
- **Branches and PRs.** Use one branch and PR per task into `main`.
  - Merge with merge commits (`gh pr merge --merge`). The repo doesn't allow auto-merge.
  - Required checks are `build` and `feedback-worker`.
  - Commit messages are a short summary line plus a body listing what changed and why.
- **Issues** use labels `bug`, `enhancement`, `trust`, `pilot`, `security`, `ready-for-release`, at least one `area:*`
  label, plus one origin label: `from:tester`, `from:owner` or `from:research`.
  The owner adds the `ready` label when an issue is specced and can be built by the factory.
- **The roadmap** is the project board at https://github.com/users/abmorrill4/projects/2. There are no phases,
  milestones or themes. Every open issue has its own `When` (Now, Next, Later) on the board, and its `area:*` label
  puts it in a product area.
- **The build factory** is one local script (`scripts/factory.ps1`) in the private repo abmorrill4/house-style-factory,
  not here. This repo holds only the plugin, its tests, its CI and its releases.
- **Releases are nightly** (`nightly.yml`, 08:07 UTC). If anything under `src/` or `styleguide/` changed since the
  last tag, it tags the next patch version on `main` and starts `build.yml` on it, which signs and publishes.
  - **To stop releases,** disable the workflow (`gh workflow disable nightly`).
  - **Notes:** every PR writers would notice adds `changes/<name>.md` (see `changes/README.md`); CI fails one that
    doesn't, unless it has the `no-notes` label. A hand-written `docs/release-notes/x.y.z.md` goes first when present.
  - **To release now:** run the `nightly` workflow by hand (`gh workflow run nightly`). Never push a tag yourself.
  - **A failed night** is reported on the "Nightly release" issue; a tag with no published release is rebuilt the next
    night.
  - Then publish any advisories the release fixes.
- **The signing key backup is on the owner's machine,** DPAPI-encrypted at
  `%APPDATA%\HouseStyle-release\update-signing-key.dpapi`. Never print it, copy it or commit it.

## Writing copy (UI, What's new, site, docs)
Writers are the audience, and they notice generated-sounding text.
- **Write like Office or Flare's own UI.** Use a plain verb phrase plus at most one ordinary sentence, in the second
  person, saying what happens for the user.
- **Avoid:**
  - colons joining clauses, arrows and semicolons
  - taglines
  - explaining how something works inside
- **Use Flare's vocabulary.**
  - Lint Topic and Lint Project find **issues**, shown in the House Style Issues pane.
  - The levels are Error, Warning and Suggestion.
  - The actions are Change, Change All and Ignore, never Accept or Reject.
  - Rules are disabled with **Disable Rule**.
- **For anything longer than a few strings,** show a copy deck before wiring it in.

## When the build fails against the real Flare DLL
The Flare docs are thin, so treat `docs/api-dump.txt` as authoritative. Fix it in `src/FlareAI.Flare/Plugin.cs`. The
details are in `src/FlareAI.Flare/CLAUDE.md`.
