# Releasing Lunalyr

This guide is for maintainers. It covers versioning, the update feed, signing keys, and publishing preview and stable releases.

## How releases reach users

Lunalyr has two release channels, both hosted on GitHub Releases. No GitHub Pages site or separate server is needed.

- **Stable** releases are normal GitHub releases marked *Latest*. Each one carries a versioned app archive (`Lunalyr-<version>.zip`) and an `appcast.xml`. The app's built-in updater (Sparkle 2.10.0) reads the appcast from the permanent feed URL:

  ```text
  https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml
  ```

- **Preview** releases are GitHub pre-releases tagged `v<version>-preview`, with a single `Lunalyr-<version>-Mac.zip`. They are installed by hand and never change the stable feed, because GitHub's `latest` URL ignores pre-releases.

The bundle identifier must stay `local.verso.custom`; the [README note](../README.md#build-from-source) explains why. Builds are ad-hoc signed; Developer ID signing and notarisation are not configured, which is why the README documents the first-launch steps.

A source build without a feed and public key has no update service: its update button explains that updates are not configured. No placeholder feed or private key is ever committed.

## Publisher state

Since 0.4.5, the stable feed and the public EdDSA (Ed25519) key in `project.yml` have stayed the same. The repository's encrypted Actions secrets hold `SPARKLE_PUBLIC_KEY` and `SPARKLE_PRIVATE_KEY`, and a protected backup of the private seed is kept outside Git.

> [!WARNING]
> Do not generate a replacement key for the existing publisher identity without planning a supported key rotation. Installed copies only accept updates signed with the key they shipped with.

As of 0.4.10 (build 17), the stable feed serves 0.4.10. Its ZIP signature was checked against the app's embedded public key with Sparkle's tools and OpenSSL, and the feed was fetched successfully. An installed old-to-new upgrade through Sparkle has not yet been verified end to end. [VALIDATION.md](VALIDATION.md) has the details.

## Continuous integration

[**Build Lunalyr for Mac**](https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml) runs the Python helper tests, the native Swift tests and a Release build on every pull request and on pushes to `main` that change more than documentation. Its `VersoCustom-Mac` artifact contains:

- `Lunalyr-Mac.zip`: the app, which runs without Xcode.
- `UpdateTools.zip`: the Sparkle 2.10.0 publisher tools from the same build.

## Prepare a release

1. Bump `CFBundleShortVersionString` (the marketing version) and `CFBundleVersion` (the build number) in both `project.yml` and `Sources/Info.plist`. The build number must increase with every release, previews included.
2. In [CHANGELOG.md](../CHANGELOG.md), move the **Unreleased** entries under a new heading such as `## [0.4.11] - YYYY-MM-DD`, and update the comparison links at the bottom of the file.
3. Commit, push, and wait for **Build Lunalyr for Mac** to pass on `main`.

## Publish a preview

Run [**Publish preview release**](https://github.com/WaterBotol/Lunalyr/actions/workflows/release.yml) from the Actions tab. It runs the Python tests, the native tests and the Release build, then publishes `v<version>-preview` as a pre-release with the app ZIP. It stops if a release with that tag already exists, so bump the version for each new preview. If you delete a preview release, delete its tag too (`gh release delete v<version>-preview --cleanup-tag`), or the next preview of that version is attached to the old tag.

## Publish a stable release

1. In Actions, open **Build Lunalyr for Mac**, choose **Run workflow** and enable **Prepare a signed draft GitHub release**.
2. The workflow runs the tests, builds the app with the public key, packages it with `ditto` to keep the framework symlinks intact, signs the archive with Sparkle's tools, and creates a draft release `v<version>` with the ZIP and `appcast.xml`. An existing release or tag is never overwritten.
3. The draft's notes are the whole of `CHANGELOG.md`. Trim them to the new version's section.
4. Test the new app, and an upgrade from an older updater-enabled build if you can.
5. Publish the draft as the latest release. Updater-enabled copies can then install it with **Check for updates…** in the menu-bar panel or under **Settings → General**. Copies built without a configured updater need one manual installation.

Automatic update checks, unattended installation and Sparkle's system-profile reporting are all off. Users always start an update themselves.

### Packaging locally

To prepare the same signed files on your Mac from an exported signing seed, build first and then run:

```bash
SPARKLE_PRIVATE_KEY_FILE="$HOME/Lunalyr-private-key.txt" bash Scripts/package-update.sh WaterBotol/Lunalyr
```

This writes `Build/Release-<version>/` with the ZIP and appcast. `Scripts/verify-appcast.py` checks the appcast's URLs, lengths and signature format; Sparkle performs the cryptographic verification on the user's Mac. Passing these checks does not prove that an installed upgrade works, so test one on a real Mac.

`Scripts/release.sh` builds a drag-to-Applications DMG instead, for manual distribution.

## One-time signing setup for a new publisher

Only do this for a new publisher identity, such as a fork with its own feed. Use the Sparkle 2.10.0 tools from the `UpdateTools.zip` CI artifact, from `Build/UpdateTools` after a local build, or from [Sparkle's official 2.10.0 release](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0). From the folder containing `UpdateTools`, run the commands below. With Sparkle's official archive, run them from the archive's top folder as `./bin/generate_keys` instead:

```bash
./UpdateTools/bin/generate_keys
./UpdateTools/bin/generate_keys -p
./UpdateTools/bin/generate_keys -x "$HOME/Lunalyr-private-key.txt"
```

The first command creates a key in your macOS Keychain, or reuses an existing one. The second prints the public key. The third exports the private signing seed. Back it up somewhere secure: losing it can stop existing installations from receiving updates.

Then add these secrets in the repository's **Settings → Secrets and variables → Actions**:

| Secret | Value |
| --- | --- |
| `SPARKLE_PUBLIC_KEY` | The public key printed by `generate_keys -p`. |
| `SPARKLE_PRIVATE_KEY` | The complete contents of `Lunalyr-private-key.txt`. |

Never paste the private key into a chat, commit it, or attach it to a release. The workflow writes it only to a temporary file on the runner and deletes it afterwards. Only the public key is embedded in the app.

To configure a local build for the same feed:

```bash
python3 Scripts/configure-updates.py --github WaterBotol/Lunalyr --public-key 'YOUR_PUBLIC_KEY'
bash Scripts/build.sh
```

This keeps `project.yml` and `Sources/Info.plist` in sync. Pass `--disable` to remove the feed and key. The configuration check rejects mismatched files, malformed keys, non-HTTPS feeds and feed URLs containing credentials.
