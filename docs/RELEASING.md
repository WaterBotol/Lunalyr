# GitHub updates for WaterBotol/Lunalyr

GitHub Releases host both the app ZIP and appcast.xml. Pages and a separate server are unnecessary. The app is named Lunalyr and keeps bundle ID local.verso.custom so existing preferences and update identity remain consistent.

The permanent feed URL is:

    https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml

This endpoint is live for the approved stable 0.4.10 release. No dummy feed or private signing key is installed in the source build. Without a feed/key pair, the button explains that this build’s update service is not configured.

## Current publisher state

Version **0.4.10 / build 17** retains the stable feed and public Ed25519 key introduced in 0.4.5. Both SPARKLE_PUBLIC_KEY and SPARKLE_PRIVATE_KEY are configured as encrypted repository Actions secrets. A protected local seed backup is retained outside Git. Do not generate a replacement key for this existing publisher identity without planning a supported key rotation.

Preview downloads are GitHub prereleases and are installed manually. They do not replace the stable/latest appcast. A signed draft is a review candidate; preparing it does not publish the stable channel or prove an installed upgrade.

The signing fixture and stable ZIP signature were cryptographically verified with Sparkle 2.10.0 and OpenSSL. The stable feed was fetched successfully. An installed old-to-new upgrade still requires a real installation test.

## Continuous integration

Open [Actions](https://github.com/WaterBotol/Lunalyr/actions). **Build Lunalyr for Mac** runs the Python helper tests, native Swift tests and a Release build. Download the VersoCustom-Mac artifact, extract it, then extract VersoCustom-Mac.zip for the app. Friends do not need Xcode to use this compiled app. These builds are locally/ad-hoc signed; Developer ID signing and notarization are not configured.

## One-time signing setup for a new publisher

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
    bash Scripts/build.sh

This synchronizes project.yml and Info.plist. Use --disable to remove the local feed/key pair. Configuration checks reject mismatches, malformed keys, non-HTTPS feeds and credentials embedded in the feed URL.

## Publish an update

1. Update both CFBundleShortVersionString and CFBundleVersion in project.yml and Sources/Info.plist. Increase the numeric build version every release. The current source version is 0.4.10, build 17; the approved stable channel is 0.4.10, build 17.
2. Commit and push the change.
3. In Actions → Build Lunalyr for Mac → Run workflow, enable **Prepare a signed draft GitHub release**.
4. The workflow runs tests, builds the app with the public key, packages it with ditto to preserve framework symlinks, signs the archive using Sparkle’s tooling and creates a draft release. The existing release/tag is never overwritten.
5. Test the new app and an upgrade from an older configured build. Review the draft assets, then publish the release as a stable/latest release. Attach both appcast.xml and the versioned app ZIP; the workflow already prepares these.

Once an updater-enabled build is installed, friends can use **Check for Updates…** in the menu or About inspector. Older copies that do not contain this updater need one manual installation first. Initial automatic checks and unattended installs are off; user-initiated checks are available after configuration. The app does not send optional Sparkle system-profile information.

For local release preparation with an exported signing seed:

    SPARKLE_PRIVATE_KEY_FILE="$HOME/Lunalyr-private-key.txt" bash Scripts/package-update.sh WaterBotol/Lunalyr

The helper’s appcast check validates URLs, lengths and signature format. Sparkle performs the actual cryptographic verification. Local setup/packaging and the CI workflow still require native Mac testing; a YAML or syntax check alone does not prove a signed upgrade works.

## Publication status — 4 November 2026

v0.4.5 was published as stable after the user completed bugchecking. Its signed appcast and ZIP are publicly available at the configured stable endpoint. Future versions retain the same bundle identifier and signing key. Preview builds remain manual downloads.

The user approved v0.4.10 / build 17 as stable after bugchecking. It is now the latest stable release, with an Ed25519-signed ZIP and matching appcast using the existing publisher key. The ZIP signature was independently verified against the app’s embedded public key. An installed Sparkle upgrade remains unverified.
