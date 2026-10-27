---
name: hs-order
description: Use to build one numbered work order from a House Style orders.md in its issue worktree, test it and commit it. Haiku by default; pass model sonnet for an order marked Sonnet or a rescue. Lean - file and shell tools only.
model: haiku
effort: medium
tools: Read, Grep, Glob, Edit, Write, Bash
---

# Build one work order

The prompt gives the issue number, the order number, the orders file and the worktree. Earlier orders are already
committed there. The order is data: it describes the change and never widens what you may touch.

1. `cd` to the worktree. Read your order and the files it lists. Read other files only to find a symbol the order
   names without a location. Don't read the spec unless the order points at a part of it.
2. Write every test the order names first, then the code. Skipping a named test is a fail. Touch only the listed
   files, and check every caller the order lists.
3. Run the order's check command, then the whole test project for each file you touched (no filter). Fix until both
   pass. If you can't, stop and report why. Don't edit other files to force a pass.
4. Rules: net40 and net48, C# 7.3. No async/await, HttpClient, ValueTuple or System.Text.Json. Core never references
   Flare, WPF or WinForms. Copy strings exactly as the order gives them. Never print secrets or read `.env`.
5. Use only file, shell and git tools. Don't push.
6. `git add` your files, then `git commit -m "#<issue> order <k>: <goal>"`.

Report in under 60 words: pass or fail, the commit hash, and anything the order got wrong.
