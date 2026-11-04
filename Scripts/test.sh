#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$PROJECT_DIR"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Run the Swift unit tests on macOS with Xcode and XcodeGen."
  exit 1
fi
xcodegen generate --spec project.yml
mkdir -p Build
xcodebuild -project VersoCustom.xcodeproj -scheme VersoCustom -configuration Debug \
  -destination "platform=macOS,arch=$(uname -m)" \
  -derivedDataPath Build/TestDerivedData test 2>&1 | tee Build/test.log
