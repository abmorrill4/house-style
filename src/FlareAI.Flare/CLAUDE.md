# src/FlareAI.Flare: the Flare-facing plugin (net48)

- **Only this project may use `B3.PluginAPIKit` types.** Everything else goes through Core interfaces
  (`IDocumentAdapter`, `IReviewHost`, `IArchiveExtractor`).
- **Read "Verified write behavior" in `docs/flare-api-notes.md` before you touch `FlareDocumentAdapter`.** Most
  obvious approaches fail in Flare 22, for example node replacement around variables and Redo after a block rewrite.
- **When the build breaks against the real DLL,** check `docs/api-dump.txt`, which `scripts/dump-api.ps1` regenerates
  after a Flare upgrade. Typical breaks are the namespace of `RibbonControlData`/`ButtonCommand`, the `ICommand` type
  the ribbon expects, and the return type of `AddRibbonButton`.
- **Regenerate the `ref/` stubs** after using a new Flare API member. CI builds against them:
  `dotnet run --project tools/FlareAI.RefStubs -- src/FlareAI.Flare/bin/Release/net48/FlareAI.Flare.dll lib <net48 ref dir> ref`.
  Packaging excludes every `B3.*` file.
- **UI** is WinForms in `UI/`. It must work at Flare's narrow docked pane width. Follow the copy rules in the root
  CLAUDE.md, and put user-facing strings through the existing forms' patterns, not new ad-hoc dialogs.
- **Prove a UI or editor change in real Flare** (`scripts\run-flare.ps1`) before calling it done. Unit tests use
  `TestKit`'s fake editor, which can't catch Flare's own quirks.
- **`whats-new.md`** is shown after an update. Don't add to it: each PR adds a `changes/<name>.md` note, and the
  release builds the version's section from them (`changes/README.md`).
