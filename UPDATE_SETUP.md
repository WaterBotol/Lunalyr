# GitHub updates for WaterBotol/Lunalyr

GitHub Releases can host both the app ZIP and appcast.xml. Pages and a separate server are unnecessary. The app keeps its current name and bundle identifier so existing preferences and update identity remain consistent.

The permanent feed URL is:

    https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml

This is the intended endpoint; it does not exist until the first stable release with that asset is published. No dummy feed or private signing key is installed in the source build. Without a feed/key pair, the button explains that this build’s update service is not configured.

## Put the source in your repo

Extract the latest source into a fresh folder on your Mac. Install GitHub CLI and sign in through its browser flow:

```bash
brew install gh
gh auth login --hostname github.com --git-protocol https --web --scopes workflow
```

Choose the account which owns WaterBotol/Lunalyr. The workflow scope allows uploading the included Actions workflow. If gh is already installed, skip the brew command.

In Terminal type `cd ` (including the space), drag the extracted VersoCustom folder into Terminal and press Return. Then run:

```bash
bash Push-to-GitHub.command
```

The source files, project.yml and .github folder belong at the repository root. The command configures the GitHub credential helper only for this repository. If Git author identity is missing, it uses the signed-in account’s name and ID-based no-reply email, without changing global Git settings. Existing author settings remain.

It never force-pushes and excludes Build output and signing files. If remote work already exists and a push is rejected, stop and reconcile the changes in GitHub Desktop or a Codex session; do not force-push over it. GitHub Desktop is also an alternative to the command-line upload.

The GitHub connection used while preparing this version could read the empty repository, but its initial file-write request returned 403, Resource not accessible by integration. No source commit or workflow run was created by that failed request.

After upload, open [Actions](https://github.com/WaterBotol/Lunalyr/actions). **Build Lunalyr for Mac** runs the Python helper tests, native Swift tests and a Release build. Download the VersoCustom-Mac artifact, extract it, then extract VersoCustom-Mac.zip for the app. Friends do not need Xcode to use this compiled app. These builds are locally/ad-hoc signed; Developer ID signing and notarization are not configured.

## One-time signing setup

Use the matching Sparkle 2.10.0 tools in the UpdateTools.zip artifact produced by the Mac workflow, or in Build/UpdateTools after a local build. If unavailable, use the tools from [Sparkle’s official 2.10.0 distribution](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0).

Run on your Mac, from the directory containing UpdateTools:

    ./UpdateTools/bin/generate_keys
    ./UpdateTools/bin/generate_keys -p
    ./UpdateTools/bin/generate_keys -x "$HOME/Lunalyr-private-key.txt"

The first command generates a key in your macOS Keychain, or reuses an existing publisher key. The second prints the public key. The third exports the private signing seed. Keep a backup in a secure location; losing it can prevent updating existing installations.

In [this repository’s Actions secrets](https://github.com/WaterBotol/Lunalyr/settings/secrets/actions), add:

| Secret | Value |
| --- | --- |
| SPARKLE_PUBLIC_KEY | The public key printed by generate_keys -p |
| SPARKLE_PRIVATE_KEY | The complete contents of Lunalyr-private-key.txt |

Do not paste the private key into a chat, commit it or attach it to a release. The release workflow writes it only into a temporary runner file and removes that file afterward. The public key is embedded in the app.

For local builds, enable the same channel with:

    python3 Scripts/configure-updates.py --github WaterBotol/Lunalyr --public-key 'YOUR_PUBLIC_KEY'
    bash Build.command

This synchronizes project.yml and Info.plist. Use --disable to remove the local feed/key pair. Configuration checks reject mismatches, malformed keys, non-HTTPS feeds and credentials embedded in the feed URL.

## Publish an update

1. Update both CFBundleShortVersionString and CFBundleVersion in project.yml and Sources/Info.plist. Increase the numeric build version every release. Version 0.4.2 is build 9.
2. Commit and push the change.
3. In Actions → Build Lunalyr for Mac → Run workflow, enable **Prepare a signed draft GitHub release**.
4. The workflow runs tests, builds the app with the public key, packages it with ditto to preserve framework symlinks, signs the archive using Sparkle’s tooling and creates a draft release. The existing release/tag is never overwritten.
5. Test the new app and an upgrade from an older configured build. Review the draft assets, then publish the release as a stable/latest release. Attach both appcast.xml and the versioned app ZIP; the workflow already prepares these.

Once the first updater-enabled build is installed, friends can use **Check for Updates…** in the menu or Behaviour settings. Older copies that do not contain this updater need one manual installation first. Initial automatic checks and unattended installs are off; user-initiated checks are available after configuration. The app does not send optional Sparkle system-profile information.

For local release preparation with an exported signing seed:

    SPARKLE_PRIVATE_KEY_FILE="$HOME/Lunalyr-private-key.txt" bash Scripts/package-update.sh WaterBotol/Lunalyr

The helper’s appcast check validates URLs, lengths and signature format. Sparkle performs the actual cryptographic verification. Local setup/packaging and the CI workflow still require native Mac testing; a YAML or syntax check alone does not prove a signed upgrade works.
