# Packaging, updates, launch at login and signing (milestone 8)

The decision and why are in [ARCHITECTURE.md](ARCHITECTURE.md#packaging-milestone-8). This file is how it works and
how to use it.

## The installer: `Lunalyr-Windows.exe`

The one file people download, named plainly so it's obvious (GitHub turns spaces in release files into dots, so
there is a dash). Once installed it keeps a copy of itself as `Lunalyr Setup.exe`, the uninstaller.

One file, per user, no administrator rights:

```
[Lunalyr Setup.exe, Native AOT, ~2.9 MB][zip of the app][zip length][SHA-256 of the zip]["LUNAPAY1"][padding]
[Authenticode signature, once signed]
```

Signing appends its certificate table after everything, and the PE header says where it starts, so the payload is
found the same way before and after signing and the signature covers it (`src/Lunalyr.Install/Payload.cs`).

### What it installs

```
%LOCALAPPDATA%\Programs\Lunalyr\
    Lunalyr Setup.exe     the installer without its payload: the uninstaller
    app\Lunalyr.exe       the app (self-contained .NET and Windows App SDK), swapped whole on each update
```

Lunalyr's data (settings, lyric cache, logs, downloaded updates) stays in `%LOCALAPPDATA%\Lunalyr`, a different
folder the installer never writes or removes. An install folder that is, holds or sits inside the data folder, a drive
root or a Windows folder is refused.

Registry, all under `HKCU`, all Lunalyr's own (`Lunalyr.Install.OwnedEntries` refuses any other write):

| Key | What |
|---|---|
| `Software\Classes\lunalyr` | `URL:Lunalyr link`, `URL Protocol`, `DefaultIcon`, and `shell\open\command` = `"…\app\Lunalyr.exe" --link "%1"` |
| `Software\Microsoft\Windows\CurrentVersion\Uninstall\Lunalyr` | Settings › Apps: name, version, publisher, icon, location, size, `UninstallString` (`"…\Lunalyr Setup.exe" --uninstall`) |
| `Software\Microsoft\Windows\CurrentVersion\Run`, value `Lunalyr` only | launch at login (the app's switch, below) |

Plus `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Lunalyr.lnk`.

### What it does, in order

1. Checks the zip's SHA-256 (a damaged download stops here, nothing changed).
2. Unpacks to `app.new` beside the current version. Entries that would land outside it are refused.
3. Asks a Lunalyr running **from the install folder** to quit, the way its tray menu's Quit does (`WM_APP+2` to its
   `Lunalyr.Host.…` window: settings flushed, the desktop's wallpaper restored). It never kills. If it doesn't quit in
   20 s, Setup stops and nothing has changed.
4. Swaps folders: `app` → `app.old`, `app.new` → `app` (put back if the second move fails), then removes `app.old`.
   An `app` folder that doesn't hold `Lunalyr.exe` is never replaced.
5. Writes the registry entries and the shortcut, and points an existing launch-at-login entry for this install at the
   new app.
6. Opens Lunalyr (interactive install: on "Open Lunalyr"; update: with `--updated <version>`, which shows "Updated to
   Lunalyr …" once).

A Lunalyr running from **another folder** (a test build) uses the same data folder, so the installed one can't start
while it runs. Setup says so and offers a check box to quit it first; it is never quit without that.

### Uninstall

Settings › Apps runs `Lunalyr Setup.exe --uninstall`. It copies itself to `%TEMP%` and continues from there (a running
program can't delete itself), asks a running copy from the install to quit, removes the entries **that still point at
this install** (another copy's `lunalyr://` or launch-at-login entry stays), the shortcut, the `app` folder (only if it
holds `Lunalyr.exe`) and itself. The Temp copy deletes itself two seconds after it exits. The data folder is kept and
the last dialog says where it is.

### Command line

| | |
|---|---|
| (none) | Install for this user, asking first (Windows task dialogs). |
| `--update` | Apply an update without questions (Lunalyr starts the downloaded installer this way, then quits). Starts Lunalyr again afterwards, also after a failure (the old version). |
| `--uninstall` | Remove. |
| `--silent` | No windows; the exit code (0 done, 1 failed, 2 bad command line, 3 cancelled) and the log say how it went. |
| `--no-launch` | Don't start Lunalyr afterwards. |
| `--log <file>` | Log there (default `%TEMP%\Lunalyr Setup.log`). |
| `--root <folder>`, `--registry-base Software\Lunalyr\<name>`, `--start-menu <folder>` | **Checking runs**: install somewhere else, write the registry under `HKCU\Software\Lunalyr\<name>` instead of `HKCU`, put the shortcut elsewhere. Nothing of the real install is touched. |

Checking the installer without touching the real install (what milestone 8's checks did):

```powershell
& "Lunalyr-Windows.exe" --silent --no-launch --root $env:TEMP\lunalyr-check\Programs\Lunalyr `
    --registry-base Software\Lunalyr\SetupCheck --start-menu $env:TEMP\lunalyr-check\StartMenu --log $env:TEMP\lunalyr-check\setup.log
reg query HKCU\Software\Lunalyr\SetupCheck /s
& "$env:TEMP\lunalyr-check\Programs\Lunalyr\Lunalyr Setup.exe" --uninstall --silent --root … (same three options)
reg delete HKCU\Software\Lunalyr\SetupCheck /f
```

### Building it

```powershell
tools\Packaging\build-installer.ps1 -Version 0.8.0
```

publishes the app and the setup (Native AOT), drops the `.pdb` files, and writes
`artifacts\installer\0.8.0\Lunalyr-Windows.exe` (about 42 MB). With `-Thumbprint` it signs (below); with
`-FeedUrl`/`-UpdateKey` the app is built with its update service; with `-DownloadUrl`/`-FeedKeyFile` it writes and signs
`update.json`. It uploads nothing.

`tools/InstallerBuilder` does the steps: `make`, `feed`, `sign` (the feed), `verify`, `keygen`.

## Updates

Re-implemented from the Mac's behaviour (Sparkle with an EdDSA-signed appcast; the Mac's `AppUpdates.swift`,
`UpdateConfiguration.swift` and their tests are Verso lines, so nothing was translated).

- **The feed** is `update.json` at an HTTPS address, with its signature at `update.json.sig`:
  ```json
  { "product": "Lunalyr", "version": "0.9.0", "url": "https://…/Lunalyr-Windows.exe",
    "sha256": "…", "size": 42512232, "notes": "…", "published": "2026-11-20T00:00:00+00:00" }
  ```
  The signature is ECDSA P-256 over SHA-256 of the file's exact bytes (Base64, IEEE P1363). .NET has no Ed25519, so
  P-256 replaces the Mac's EdDSA key. The public key is built into the app; the private key is the owner's and never in
  the repo.
- **Configured at build time**: `-p:LunalyrUpdateFeed=https://… -p:LunalyrUpdateKey=<Base64 SubjectPublicKeyInfo>`
  (assembly metadata). A build without both has no update service, and Settings › General says so in the Mac's words:
  "An update service hasn’t been set up for this build yet." **Test Build 8 has none**: no feed has been published.
- **Check** (`Lunalyr.Shell.Updates.UpdateService`): the feed and signature (each at most 64 KB) are fetched, the
  signature checked before anything is parsed, then product, version, `https` download address, checksum and size. A
  redirect may only land on another `https` address.
- **Download** to `%LOCALAPPDATA%\Lunalyr\Updates\Lunalyr Setup <version>.exe.partial`, cut off past the feed's size,
  then verified: size and SHA-256 against the feed, the installer's own payload checksum, the version inside it, and,
  once Lunalyr is signed (`-p:LunalyrRequireSignature=true`), its Authenticode signature (`WinVerifyTrust`, no
  revocation fetch, no network). Only then is it renamed and offered.
- **Automatically** once a day while "Check for updates automatically" is on (`automaticUpdateChecks`, default on):
  the first look two minutes after start, then hourly to see if a day has passed (`Updates\last-check.txt`). A newer
  version is downloaded and verified in the background; the action HUD says "Lunalyr 0.9.0 is ready to install" once,
  and the tray menu gets **Restart to update to Lunalyr 0.9.0**. Nothing installs without that click (or Restart and
  Update in Settings).
- **Installing**: an installed Lunalyr starts the verified installer with `--update` and quits; Setup swaps the app and
  starts it with `--updated 0.9.0`. A copy run from any other folder (a test build) opens the installer normally, which
  installs Lunalyr for this user.
- **No feed yet** (404, as GitHub's `releases/latest/download/update.json` answers before the first release): up to
  date, quietly ("no update has been published yet"), logged as info, asked again in a day.
- **Never** in test runs: `--demo`, `--data` and `--no-network` turn checks off and say so.

### Publishing an update (the owner's step, not done)

1. `tools\InstallerBuilder` `keygen --private <somewhere safe, outside the repo>` once; keep the printed public key.
2. `build-installer.ps1 -Version 0.9.0 -FeedUrl https://…/update.json -UpdateKey <public key> -DownloadUrl https://github.com/WaterBotol/Lunalyr/releases/download/<tag>/Lunalyr-Windows.exe -FeedKeyFile <private key> [-Thumbprint …]`
3. Upload the installer, then `update.json` and `update.json.sig` (in that order: the feed must never point at a file
   that isn't there yet). A GitHub release with `releases/latest/download/update.json` works.

Nothing is published or uploaded without the owner's OK.

## Launch at login

Settings › General › Startup, and the welcome's tray step. The Windows counterpart of the Mac's `SMAppService` login
item: one value named `Lunalyr` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, holding
`"…\Lunalyr.exe" --login`. `--login` starts quietly in the tray (no settings window, no welcome); a second launch with
it opens nothing.

The switch reads Windows' state back every time (`Lunalyr.Install.LaunchAtLogin`, `Lunalyr.Shell.Startup.LaunchAtLoginSwitch`):

| Windows says | The row shows |
|---|---|
| no value | Off |
| the value starts this copy | On |
| Task Manager › Startup apps (or Settings › Apps › Startup) has it off: `…\Explorer\StartupApproved\Run` › `Lunalyr`, first byte odd | Off, with a warning "Turned off in Startup apps" and **Open Startup apps** (`ms-settings:startupapps`). Lunalyr doesn't override the user's choice there. |
| the value starts another copy | Off, with a warning naming that folder; turning it on points it at this copy |
| a write failed | the switch's previous state, with "Launch at login didn’t change" and Windows' reason |

A copy run from a test-build folder can use it too (the note says moving the folder breaks it). `--demo` and `--data`
runs never change it (the switch is disabled with a note). The installer moves an entry for its own install to the new
app on update and removes it on uninstall, only while it starts this install.

## `lunalyr://` links

Registered by the installer as `"…\app\Lunalyr.exe" --link "%1"`. `ShellOptions` reads everything after `--link` as
links only, so a link shaped like `--data` or `--stats` can't become a flag, and a second launch forwards the link to
the running Lunalyr over `WM_COPYDATA` (milestone 7). A portable copy (a test build folder) doesn't register the scheme.

## Code signing and SmartScreen

**Not signed yet: signing needs the owner's certificate.** Until then Windows SmartScreen shows "Windows protected your
PC" for the downloaded installer (More info › Run anyway), and the installed app has no publisher name.

Prepared:
- `tools\Packaging\sign.ps1 -Thumbprint <40 hex digits> -Files …`: `signtool sign /fd SHA256 /tr <timestamp> /td SHA256 /sha1 <thumbprint>`
  then `signtool verify /pa`. The thumbprint defaults to a placeholder of zeros and the script refuses to run with it.
  Azure Trusted Signing works through `-Dlib`/`-DlibMetadata` instead. The script never reads or lists certificate
  stores itself; signtool finds the certificate by the thumbprint the owner gives.
- `build-installer.ps1 -Thumbprint …` signs, in this order: `app\Lunalyr.exe` and the setup that goes inside the
  payload, then (after the payload is appended to the **unsigned** setup) the whole installer. Lunalyr's other files
  are Microsoft's, already signed.
- Once signed, build with `-p:LunalyrRequireSignature=true` (the script does) so updates must carry a trusted signature.

What the owner needs to choose (asked in the milestone 8 report):
- **An OV or EV code-signing certificate** (CAs now issue them only on a hardware token or a cloud HSM), or **Azure
  Trusted Signing** (a monthly subscription with identity validation; check that it is offered to individuals where you
  live). SmartScreen's reputation builds up with downloads in every case; as far as I know an EV certificate no longer
  skips that.
- The timestamp server of whichever CA issues it.
