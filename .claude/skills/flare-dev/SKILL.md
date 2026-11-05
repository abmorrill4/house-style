---
name: flare-dev
description: Use before writing or changing anything that touches MadCap Flare itself: the plugin API (B3.PluginAPIKit), the ribbon, menus, docked panes, the editor and document adapter, topic and snippet XML, tracked changes, undo, variables, conditions, targets, or how a Flare feature behaves. Look it up in the local Flare 2026 documentation and our verified notes. Never answer from memory or a fresh web search.
---

# flare-dev: look Flare up, don't recall it

Flare's plugin API is small, thinly documented and changes between versions. Memory and ad-hoc searches have been wrong
here before (the docs say .NET 4.0, the DLL is 4.8; an issue was written around a Content Explorer right-click that
the API doesn't have). Use these sources, in this order, and say which one an answer came from.

1. **`docs/api-dump.txt`**: reflection over the real `B3.PluginAPIKit.dll` (Flare 22). It is the authority on what exists.
   It has more than the docs: `ICompilerContext` (CompileStart, CompileComplete) and `RibbonControlData` and
   `RibbonMenuData` as concrete classes.
2. **`docs/flare-api-notes.md`**: what we verified by testing in real Flare (write behavior, tracked changes, Redo
   crashes, panes, split buttons, junction limits). Read the section for your area before touching `FlareDocumentAdapter`
   or `UI/PanelHost.cs`.
3. **The local Flare 2026 documentation**, `E:\HouseStyle\planning\research\flare-docs\` (private, untracked):
   - `INDEX.md`: every help topic by title and file. Scan it, or Grep `help\` (Markdown copy of the online help, 2,443
     topics, each file starts with its source URL).
   - `pdf\` and `pdf-all\`: the Plug-In API guide and every PDF guide, tutorial and cheat sheet, each with a `.txt` copy.
     Grep the `.txt` files. The Snippets, Variables, Condition Tags, Topics, Targets, Tables and Styles guides are there.
   - `README.md` there says how it was built and how to refresh it after a Flare upgrade.
4. **Real Flare.** If none of the above settles it, it is unverified. Test it with `tests/e2e` (see the root
   `CLAUDE.md`), then record the result in `docs/flare-api-notes.md` with the Flare version and date. Don't guess, and
   don't ship behavior that rests on an unverified assumption.

"Flare 2026" in the docs is the same build as the DLL's version 22. The help describes features and the UI; it hardly
documents topic markup (`MadCap:conditions`, `snippetBlock` and the like appear almost nowhere). For markup, read real
files under `corpus/`, the "Flare topic XML shape" section of `docs/flare-api-notes.md`, and the schema from
`IEditorContext.GetMadCapSchema()`. An attribute that only our notes mention is not verified against MadCap's docs.

If the local docs folder is missing (a fresh machine, a cloud run), say so and fall back to 1 and 2. Don't search the
web to fill the gap without telling the owner.

## What the official API gives you (Flare 2026, from the Plug-In API guide)
- **Lifecycle.** `IPlugin` has `IsActivated`, `GetName/GetVersion/GetAuthor/GetDescription`, `Initialize(IHost)`,
  `Execute()`, `Stop()`. `Initialize` only stores the host. Get the editor and navigation contexts in `Execute()`.
  `Stop()` detaches events, reverts UI changes, calls `IHost.Dispose()` and clears `IsActivated`.
- **Install and enable.** A folder per plugin under `Flare.app\Plugins`; the writer enables it in File > Options >
  Plugins, then restarts Flare. Relative paths passed to API calls resolve from the `Plugins` folder.
- **`IHost`.** `GetEditorContext()`, `GetNavContext()`, `GetName()`, `GetCompany()`, `Dispose()`.
- **`IEditorContext`.** `GetActiveDocument()`, `GetDocuments()`, `OpenDocument(path[, EditorView])`,
  `GetMadCapSchema()`, event `DocumentSwitched`.
- **`IDocument`.** `GetXmlDocument()`, `GetDocumentXml/Text()`, `GetSourceUrl()`, cursor get/set, `Select(string|XmlNode)`,
  `Insert/Replace/RemoveDocumentNode(..., bool trackChange)`, `StartOperation/EndOperation` (one undo step), `Save()`,
  `ApplyStylesheet/RemoveStylesheet`, `LookupVariableValue(set, name)`, `UpdateView()`, `Close()`, and events for
  saving, closing, content change, keys and mouse.
- **`ISelection`.** `GetText()`, `ReplaceText()`, `GetXmlNodeList()`, `SetSelectionLength()`, `TrackChange`, and
  formatting helpers.
- **`INavContext`.** `GetParentForm()`, `GetRibbon()`, `CreateCustomToolBar()`, `CreateToolStripMenu()`.
  Ribbon: `AddNewRibbonTab` > `AddNewRibbonGroup` > buttons, check boxes, combo boxes, menu and split-menu buttons,
  toggle buttons. Menu items get a label, command, icon and nothing else (no key tip or tooltip).
- **Context menus.** Only the editor document has one: add `ToolStripItem`s to the list from
  `IDocument.GetUserPendingContextMenuItems()` and clear it yourself for every document. **There is no hook for the
  Content Explorer, Project Explorer or any other pane's right-click menu.** A feature that needs one has to be a
  ribbon, menu or toolbar command with its own picker (Lint Folders is built this way).
- **Modes.** A custom ribbon tab shows only in Ribbon mode, and a custom toolbar only in Tool Strip mode. Cover both.
- **Targets.** The guide says .NET Framework 4.0. Flare 22's DLL is 4.8, so we build net48. `FlareAI.Core` stays net40.

## Not in the docs, only in our notes (check `docs/flare-api-notes.md`)
Docked panes (internal `B3.AppKit` types, kept in `PanelHost`), which writes undo and redo cleanly and which crash Flare,
tracked-change markup, `Closing` firing when we write a closed topic, `Select` matching the first occurrence only,
`SelectionMarker` elements left in the DOM, and split buttons by reflection. Anything near these needs a real-Flare test.

## When you learn something new
Add it to `docs/flare-api-notes.md` (what, which Flare build, how it was verified) in the same PR. If a document and
the DLL or a live test disagree, trust the DLL and the test, and note the difference there. A change that uses a new
Flare API member also regenerates the `ref/` stubs (see `src/FlareAI.Flare/CLAUDE.md`).
