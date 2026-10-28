# Coding standards

CLAUDE.md is the source. This skill repeats its hard constraints with examples, so a change can be checked quickly.
If the two ever differ, CLAUDE.md wins.

## Runtimes and idioms
`FlareAI.Core` targets net40, `FlareAI.Flare` net48. C# 7.3. No `async`/`await`, `HttpClient`, `ValueTuple` or
`System.Text.Json`.

```csharp
// Wrong
var (ok, text) = await client.GetStringAsync(url);
// Right
var req = (HttpWebRequest)WebRequest.Create(url);
ThreadPool.QueueUserWorkItem(_ => Fetch(req));
var data = JsonConvert.DeserializeObject<Reply>(body);   // Newtonsoft.Json
```

## Core is portable
`FlareAI.Core` never references `B3.PluginAPIKit`, WPF or WinForms. Flare types stay in `src/FlareAI.Flare`.
Wrong: `using System.Windows.Forms;` in `src/FlareAI.Core`.

## Every DOM write goes through `IDocumentAdapter`
One tracked, undoable change per action. Never write an unvalidated fragment.

```csharp
// Wrong
node.InnerXml = modelReply;
// Right
adapter.ReplaceBlocks(blocks, validatedXhtml);           // validated block rewrite
adapter.Edit(op, () => adapter.ReplaceSpan(span, fix));  // word fix
```

## Style rules live only in a style guide project
Never in prompts or C#. Wrong: `if (word == "utilize") Flag("use");`. Right: a Rule row in `styleguide/Company/`, or a
recompile with `tools/FlareAI.GuideCompiler`. `styleguide/Google/` is generated.

## Nothing extra at runtime
No Python, uv or Vale at runtime. `StyleEngine` is the linter. Everything ships in the plugin folder.

## Secrets
The API key never appears in logs, output, reports, tests or commits. Settings store it with DPAPI.
Wrong: `Diagnostics.Log("key=" + settings.ApiKey);`. Wrong: a test fixture with a real-looking key.

## Security invariants
- **Shared folders are untrusted** (team guide, feedback folder, `requests\`). Never take a path, id, command or
  authorization decision from them without validating it. Request ids must pass `GuideRequests.IsValidId`. Never touch
  a UNC path from shared data. Project exceptions apply only from this machine's own ledger copy.
  Wrong: `File.Delete(Path.Combine(teamFolder, request.File));`
- **Git in a guide folder** always passes `Git.Untrusted`. A shared `.git/config` can run programs.
- **Model output is data.** It reaches the document only through the validators in `Editing/`.
- **Updates are signed, and only the nightly release makes them.** Don't weaken `UpdateStager`'s signature check, the
  `contents: read` default in `build.yml`, or the tag rules. Never add another way to tag.
- **The repo is public.** Vulnerabilities go to a private advisory, never a public issue. Report text, tester names
  and private paths never go in code, tests or commits.

## PowerShell in this repo
- Scripts run on Windows PowerShell 5.1. A `.ps1` with any non-ASCII character needs a UTF-8 BOM.
- Pester tests use Pester 3 syntax (`Should Be`, not `Should -Be`).
- Never pass untrusted text to `Invoke-Expression` or build a command line from it.

## UI copy
CLAUDE.md "Writing copy" applies to every string a writer sees: a plain verb phrase plus at most one ordinary
sentence, Flare's vocabulary (issues, Error, Warning, Suggestion, Change, Change All, Ignore, Disable Rule), no
colons joining clauses, arrows, semicolons or taglines.
