# Releasing

Releases are built locally and published as GitHub releases with four files: the installer, the portable zip, their SHA-256 checksums and the signature of the checksums. The app's one-click update installs a release only when that signature verifies, so a release without the `.sig` file can be downloaded by hand but not installed from the app.

## Once: the release signing key

The setup is not code-signed, and the checksum file comes from the same release page as the setup, so the checksums are signed with a key that never leaves your PC. The app has the public half built in. Create the pair once, with the private key outside the repository:

```powershell
dotnet run tools/release-signing.cs -- new-key "$env:USERPROFILE\.developer-island\release-signing-key.pem"
```

It writes the private key to that path and the public key to `src/DeveloperIsland.Core/Updates/release-signing-public-key.pem`. Commit the public key (it is the one `.pem` file the `.gitignore` lets through), and make the private key known to the build script:

```powershell
[Environment]::SetEnvironmentVariable('DI_RELEASE_SIGNING_KEY', "$env:USERPROFILE\.developer-island\release-signing-key.pem", 'User')
```

Open a new terminal afterwards so the build script sees it.

Keep a backup of the private key somewhere safe (a password manager or an offline drive). Installed apps trust exactly this public key: if the private key is lost, users can still install new releases by hand, but the one-click update stops working until they do. `new-key` refuses to overwrite an existing key for that reason.

The first release that contains the public key (0.4.0) has to be installed by hand; from then on, users update in one click.

## Checklist

1. **Version.** Set `<Version>` in [`Directory.Build.props`](../Directory.Build.props) (semantic versioning: `0.1.0`, `0.1.1`, `0.2.0`).
2. **Changelog.** Move the entries under "Unreleased" in [`CHANGELOG.md`](../CHANGELOG.md) to a new section with the version and date.
3. **Build.** Quit any Developer Island that runs from `artifacts/publish`, then:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
   ```

   It refuses to start without `DI_RELEASE_SIGNING_KEY` (pass `-SkipSigning` only for a test build), runs the tests and writes to `artifacts/`:

   | File | What it is |
   |---|---|
   | `DeveloperIsland-Setup-<version>.exe` | Per-user installer (no administrator rights, self-contained) |
   | `DeveloperIsland-<version>-win-x64-portable.zip` | Portable build; extract everything and run `DeveloperIsland.exe` |
   | `SHA256SUMS-<version>.txt` | Checksums of both |
   | `SHA256SUMS-<version>.txt.sig` | ECDSA P-256 signature of the checksums; the app verifies it before installing an update |

4. **Smoke test** on a machine (or a fresh user account) without the .NET SDK: install, start, open each module in normal and demo mode, check Settings, About shows the version, uninstall. Check the signature, for example for 0.1.0: `dotnet run tools/release-signing.cs -- verify artifacts/SHA256SUMS-0.1.0.txt` (the build already did; this confirms the files you are about to upload). After publishing, an installed previous version should offer the update with "Check now" in Settings, General.
5. **Commit and tag:**

   ```powershell
   git commit -am "release: 0.1.0"
   git tag -a v0.1.0 -m "Developer Island 0.1.0"
   git push origin main v0.1.0
   ```

6. **Publish** the release on GitHub (Releases, Draft a new release, tag `v0.1.0`), with the changelog section as notes and the four files attached. **Do not forget the `.sig` file**: without it the app offers the update but refuses to install it. With the [GitHub CLI](https://cli.github.com):

   ```powershell
   gh release create v0.1.0 --title "Developer Island 0.1.0" --notes-file notes.md `
     artifacts/DeveloperIsland-Setup-0.1.0.exe `
     artifacts/DeveloperIsland-0.1.0-win-x64-portable.zip `
     artifacts/SHA256SUMS-0.1.0.txt `
     artifacts/SHA256SUMS-0.1.0.txt.sig
   ```

## Notes

- The installer is not code-signed yet, so Windows SmartScreen asks for confirmation on first run. Mention this in the release notes. The one-click update runs the setup without SmartScreen (it is downloaded by the app, not the browser), after verifying the signed checksum.
- Only x64 is built and tested. `-Runtime win-arm64 -SkipInstaller` produces an experimental ARM64 zip that has not been validated.
- Uninstalling keeps `%LOCALAPPDATA%\DeveloperIsland` (history and settings), so a reinstall continues where it left off.
