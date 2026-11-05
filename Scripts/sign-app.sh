#!/bin/bash
# Re-sign a built Lunalyr.app with a stable code-signing identity.
#
# Usage: LUNALYR_CODE_SIGN_IDENTITY="<certificate name or SHA-1>" Scripts/sign-app.sh Build/Lunalyr.app
#        (LUNALYR_CODE_SIGN_KEYCHAIN optionally names the keychain holding it)
#
# macOS ties privacy grants (System Audio, Screen Recording) to the code
# signature. Xcode signs this project ad hoc, which gives every build a new
# identity, so the grants stop applying after each update. Signing every
# release with the same certificate keeps them. Without an identity the app
# keeps its ad hoc signature and nothing changes.
set -euo pipefail
APP="${1:?Pass the .app to sign.}"
IDENTITY="${LUNALYR_CODE_SIGN_IDENTITY:-}"
if [[ -z "$IDENTITY" ]]; then
  exit 0
fi
FLAGS=(--force --sign "$IDENTITY" --preserve-metadata=identifier,entitlements,flags)
if [[ -n "${LUNALYR_CODE_SIGN_KEYCHAIN:-}" ]]; then
  FLAGS+=(--keychain "$LUNALYR_CODE_SIGN_KEYCHAIN")
fi
# Inside out: helpers nested in each framework (Sparkle's installer, updater
# and XPC services), then the framework, then the app that seals them all.
for framework in "$APP"/Contents/Frameworks/*.framework; do
  [[ -d "$framework" ]] || continue
  main="$(basename -- "$framework" .framework)"
  # Paths are searched relative to the framework, so the patterns below
  # cannot match the enclosing Lunalyr.app.
  while IFS= read -r -d '' nested; do
    codesign "${FLAGS[@]}" "$framework/$nested"
  done < <(cd -- "$framework" && find Versions -depth -mindepth 2 \
    \( -type d \( -name '*.app' -o -name '*.xpc' \) -o -type f -perm -u+x ! -name "$main" \) \
    ! -path '*/Resources/*' ! -path '*.app/*' ! -path '*.xpc/*' -print0)
  codesign "${FLAGS[@]}" "$framework"
done
codesign "${FLAGS[@]}" "$APP"
codesign --verify --deep --strict "$APP"
