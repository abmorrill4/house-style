# src/FlareAI.Core/Update: self-update (every install trusts this code)

**The flow.** Each daily check goes through these steps:
1. `UpdateCheck` reads `releases/latest`. It skips drafts and pre-releases and accepts newer versions only.
2. `UpdateStager` downloads `checksums.txt` and `checksums.txt.sig`.
3. It verifies the signature against `UpdateSigning.PublicKeyXml`. The signature is RSA-SHA256, PKCS#1 v1.5, over the
   exact bytes of `checksums.txt`.
4. It downloads the zip and checks its SHA-256.
5. It unzips the payload into `staged\<ver>` (with the zip-slip guard in `FlareAI.Flare/ZipExtractor.cs`) and
   validates it.
6. After Flare exits, `tools/FlareAI.Updater` swaps `%LOCALAPPDATA%\HouseStyle\current`, using `UpdateSwap` and
   `UpdateLayout`.

**Don't break these:**
- **The signature is required.** Never add an "unsigned is OK" path, a dev bypass that ships in release builds, or
  a second trusted key without the owner asking.
- **Tests use their own test key,** through `MockReleaseServer` and `UpdateStager.PublicKeyXml`. The real private
  key exists only as the `release` environment secret and the owner's DPAPI backup.
- **Rotating the key** means shipping the new public key in a release signed with the old key. If both the key and
  its backup are lost, installs can't update automatically again.
- **Protect the release path in `release.yml`:**
  - Keep `permissions: {}` at workflow level and each job's own minimal permissions. Only `publish` writes, and it holds
    no secrets. `rsa` has no `id-token` and no third-party actions.
  - Keep `environment: release` on `sign`, `sign-exe` and `rsa` only (it deploys only from `main`), and `persist-credentials: false`.
  - Keep the signing order: sign the `FlareAI.*` PE files and `Install.ps1`, then zip and build the exe from them, then sign
    the exe, then write and RSA-sign `checksums.txt`. A signature failure stops the run; never fall back to unsigned.
  - Keep the version check (plain x.y.z, newer than the latest release, tag not existing) and the verify step in `publish`,
    which checks the signatures and the RSA signature with the key built into `FlareAI.Core.dll`.
  - `UpdateStager` checks every `FlareAI.*` DLL and EXE; the release signs the same set (`scripts/release/Get-SignFiles.ps1`).
- **Never delete a junction recursively.** The layout uses junctions (`Junction.Remove`). Remember the Flare 2026
  "junction to a junction" startup crash (#119).
- **Fake update server for manual tests:** `scripts\fake-update-server.ps1`. It signs with the owner's DPAPI key
  backup, so it only works on the owner's account. Use `-Unsigned` or `-Corrupt` to check that the update is
  refused.
- **The installer** (`HouseStyle-Setup.exe` runs the signed `scripts/release/Install.ps1`) elevates once, with a fixed encoded command, to link Flare's Plugins
  folder. It refuses to re-point a link another install owns (#137).
