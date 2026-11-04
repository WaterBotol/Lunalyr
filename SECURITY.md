# Security policy

## Supported versions

Security fixes go into the latest stable release, which is offered through the built-in updater. Please update before reporting, and check whether the problem still occurs.

| Version | Supported |
| --- | --- |
| Latest stable release ([releases page](https://github.com/WaterBotol/Lunalyr/releases/latest)) | Yes |
| Older releases and preview builds | No |

## Reporting a vulnerability

Please do not report security problems in public issues, pull requests or discussions.

Report them privately through GitHub instead: open the repository's **Security** tab and choose **[Report a vulnerability](https://github.com/WaterBotol/Lunalyr/security/advisories/new)**. Only the maintainers can see the report. Please include:

- what is affected, and what an attacker could do;
- steps or a small proof of concept to reproduce it;
- the Lunalyr and macOS versions you tested;
- whether the issue is already public anywhere.

Lunalyr is a small project, so please allow a few days for a first reply. We will confirm the problem, keep you updated while we work on a fix, and credit you in the release notes unless you would rather stay anonymous. Please give us a reasonable time to ship a fix before disclosing details publicly.

Never send signing keys, access tokens or other people's personal data, even to show a problem.

## Scope

Reports are especially welcome about:

- **Updates**: anything that could make the app install an update that was not signed with the project's key, or fetch its feed or archive insecurely.
- **Untrusted input**: lyric data from online sources or local files (LRC, TTML, YRC, Lyricsfile YAML), imported images and videos, and web pages or API responses the app parses.
- **Local data**: reading or writing files outside the app's own folders, or exposing preferences, cached lyrics or audio.
- **Build and release automation**: the GitHub Actions workflows and scripts in `Scripts/`, including how signing secrets are handled.

Out of scope:

- Problems in the third-party lyric services themselves. Please report those to the service.
- Vulnerabilities in Sparkle, Yams or other dependencies that do not affect Lunalyr. Report those upstream, and tell us if Lunalyr needs an update.
- The Gatekeeper warning on first launch. Releases are ad-hoc signed and not notarised by Apple, as explained in the [README](README.md#first-launch).
- Attacks that require an attacker who already controls your user account or Mac.

## How Lunalyr protects you

- Update archives are signed with an EdDSA (Ed25519) key. Sparkle checks the signature against the public key embedded in the app before installing. The private key is never stored in this repository.
- Updates and lyric requests use HTTPS. Automatic update checks are off.
- Audio for the visualisers is analysed in memory. Nothing is recorded or uploaded, and the microphone is never used.
- The TTML parser refuses external entities and DTD declarations, and limits input size and nesting depth.

More detail on privacy is in the [README](README.md#lyrics-sources-and-privacy).
