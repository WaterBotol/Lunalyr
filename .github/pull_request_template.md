<!-- Thanks for contributing! Please read CONTRIBUTING.md first. -->

## Summary

<!-- What does this change, and why? Link the issue it addresses, for example "Fixes" followed by the issue number. -->

## How it was tested

<!-- List the checks you actually ran, with your Mac model and macOS version. Say what you could not test. -->

- [ ] `bash Scripts/test.sh` (XCTest)
- [ ] `bash Scripts/build.sh` (C suites and Release build)
- [ ] `python3 -m unittest discover -s Tests/Python`
- [ ] Checked by hand in the running app (describe what you checked)

## Screenshots or recording

<!-- Required for visual changes: a screenshot or short recording of the real app.
For performance changes, include before-and-after measurements with the same song, composition, wallpaper,
frame rate and displays, in a Release build with the workspace window closed. -->

## Checklist

- [ ] The change is focused, with no unrelated reformatting.
- [ ] User-visible changes are noted under **Unreleased** in `CHANGELOG.md`.
- [ ] Documentation is updated where needed (README, `docs/`).
- [ ] Existing preference keys, the bundle identifier and storage folders are unchanged, or a migration is included.
- [ ] Any third-party code or assets are compatibly licensed and recorded in `THIRD_PARTY_NOTICES.md`.
- [ ] No secrets, signing material or personal data are included.
