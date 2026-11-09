# Install House Style

House Style adds a **House Style** tab to MadCap Flare Desktop. Every edit it makes is a tracked change.

## Quick install (recommended)

1. **Download HouseStyle-Setup.exe** from the [releases page](https://github.com/abmorrill4/house-style/releases/latest).
   It is signed by Arthur Morrill. To check it, run `Get-AuthenticodeSignature .\HouseStyle-Setup.exe` in PowerShell,
   or compare `Get-FileHash` with its line in `checksums.txt` on the release page.
2. Close Flare.
3. Run it. Say yes to the administrator prompt.
4. It unblocks the files, copies the plugin to `%LOCALAPPDATA%\HouseStyle`, links your Flare to it, turns the plugin
   on in Flare for you and prints what to do next. The administrator prompt is needed only this first time: updates
   install by themselves when you close Flare (House Style checks GitHub once a day; see the privacy page).
5. Start Flare. The **House Style** tab appears and a short setup opens. It asks how you'll use House Style, how to
   use AI (none, your Anthropic API key, or your company's gateway key and address) and which style guide to follow.
6. Practice your first lint on the sample project or on a topic of your own, or click **Skip**. Upgrading from an
   earlier version? Open **House Style > Settings > Run setup again** to see the new setup.

House Style installs for your own Windows account. Shared computers aren't supported.

If the **House Style** tab doesn't appear, choose **File > Options > Plugins**, select **House Style**, click
**Enable**, and restart Flare.

If you have several Flare versions, the installer picks the newest. To pick another, open PowerShell in the unzipped
folder and run `.\Install.ps1 -Flare 21` (use the folder number from the table below).

**Upgrading from an earlier test build?** Run **HouseStyle-Setup.exe** once. Earlier release candidates
used a folder layout that makes Flare 2026 crash at startup; the installer repairs it, and your settings stay.

### Where the files go

```
%LOCALAPPDATA%\HouseStyle\
  current\            the plugin files Flare loads (a real folder)
  versions\<ver>\     the version before the last update, kept for rollback
  staged\<ver>\       a downloaded, verified update waiting for Flare to close
...\Flare.app\Plugins\FlareAI  ->  %LOCALAPPDATA%\HouseStyle\current   (a junction, made once as administrator)
```

When Flare closes, the updater copies the new version to `current.new`, renames `current` to `current.old` and
`current.new` to `current`, and checks the result. If the check fails it renames them back. Then `current.old` becomes
`versions\<previous version>`. Renaming needs no administrator rights, because Flare's junction points at the path
`...\HouseStyle\current`. If Flare still holds the files, nothing changes and the update is offered again.

> **Keep it to one junction.** Flare 2026 crashes while opening its main window (before any plugin code runs) when
> `Plugins\FlareAI` is a junction to another junction. `current` must stay a real folder. Earlier release candidates
> made `current` a junction to `versions\<ver>`; the installer and the updater replace that with a real folder, and
> House Style pauses automatic updates (and says to run the installer) while it finds that layout.

## Which Flare is which

| Install folder | Flare version |
|---|---|
| `MadCap Flare 22` | Flare 2026 |
| `MadCap Flare 21` | Flare 2025 (including 2025 R2) |
| `MadCap Flare 20` | Flare 2024 |

House Style is tested on Flare 2026. The plugin loads per Flare version, so install it into the one you open.

## Manual install (if the installer does not work)

A manual copy doesn't update itself: install each new version the same way.

1. **Unblock first.** Right-click the zip > **Properties** > tick **Unblock** > OK, then unzip. Already unzipped?
   Run this in PowerShell: `Get-ChildItem <unzipped folder> -Recurse | Unblock-File`. Windows silently stops Flare
   from loading blocked DLLs, so the plugin just never appears.
2. Close Flare.
3. Copy the **`FlareAI`** folder to
   `C:\Program Files\MadCap Software\MadCap Flare 22\Flare.app\Plugins\` (change `22` for your version).
4. **Check there is no wrapper folder.** The DLL must be at
   `...\Flare.app\Plugins\FlareAI\FlareAI.Flare.dll`. If you see `Plugins\HouseStyle-0.1.3\FlareAI\...`, your unzip
   tool added an extra folder: move the inner `FlareAI` folder up into `Plugins` and delete the wrapper.
5. Start Flare > **File > Options > Plugins > House Style > Enable**. (The installer does this step for you; a manual
   copy doesn't.)
6. **Restart Flare.** Two restarts in total: one so Flare finds the plugin, one after enabling it.

## If House Style is not in the Plugins list

- Re-check step 4 (wrapper folder) and step 1 (unblock).
- Confirm you installed into the Flare version you are running (table above).
- Flare must be fully closed when you copy, and restarted afterwards.

Nothing else to install: the style guide, linting and the LLM export run inside the plugin. More in
`TESTER-GUIDE.md` in the repository.
