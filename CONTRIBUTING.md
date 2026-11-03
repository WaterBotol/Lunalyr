# Contributing to Lunalyr

Lunalyr is a native macOS SwiftUI and Metal app derived from Verso. Read LICENSE and THIRD_PARTY_NOTICES.md before contributing.

## Development

Install full Xcode, its Metal compiler and XcodeGen. Run `bash Run-Tests.command`, then `bash Build.command`. The generated project and Build directory are excluded from Git.

Open a focused pull request describing the problem, resulting behaviour and checks performed. For visual changes, include a short recording from the actual app. For performance changes, compare the same track, composition, wallpaper and displays in a Release build with Settings closed.

## Reporting problems

Include your macOS version, Mac model, app version and steps to reproduce. Audio issues can include Settings → Background → Copy audio status. Remove personal information from logs. Never include signing keys or account tokens.

Native audio capture, permissions, multiple displays and visual quality need real Mac verification; portable tests alone do not establish these results.
