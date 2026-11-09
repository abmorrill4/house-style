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
- **Protect the release path in `build.yml`:**
  - Keep `contents: read` at workflow level. Only the `release` job writes.
  - Keep `environment: release` (it holds the signing key and deploys only `v*` tags), the check that the run was
    started by `nightly.yml`, and the "tag is on main" check. Releases are nightly with no approval click (2026-10-24).
  - Keep `persist-credentials: false`, and keep the tag name validated before it's used.
- **Never delete a junction recursively.** The layout uses junctions (`Junction.Remove`). Remember the Flare 2026
  "junction to a junction" startup crash (#119).
- **Fake update server for manual tests:** `scripts\fake-update-server.ps1`. It signs with the owner's DPAPI key
  backup, so it only works on the owner's account. Use `-Unsigned` or `-Corrupt` to check that the update is
  refused.
- **The installer** (`scripts/release/Install.ps1`) elevates once, with a fixed encoded command, to link Flare's Plugins
  folder. It refuses to re-point a link another install owns (#137).
