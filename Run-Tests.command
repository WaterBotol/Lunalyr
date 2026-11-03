#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$PROJECT_DIR"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Run the Swift unit tests on macOS with Xcode and XcodeGen."
  exit 1
fi
xcodegen generate --spec project.yml
xcodebuild -project VersoCustom.xcodeproj -scheme VersoCustom -configuration Debug \
  -destination "platform=macOS,arch=$(uname -m)" test
