#!/bin/bash
# GitHub Actions steps shared by .github/workflows/macos.yml and release.yml.
#
#   select-xcode             Use the pinned Xcode; fail clearly when the runner lacks it.
#   install-xcodegen         Install the pinned, checksum-verified XcodeGen release.
#   package-cache-key        Print cache keys for the resolved Swift packages.
#   import-signing-identity  Import the optional code-signing certificate into a
#                            temporary keychain (ad hoc signing when it is absent).
#   remove-signing-identity  Delete that keychain again.
#
# The pins live here so both workflows build with the same tools. Bump them
# deliberately, after a build with the new version has passed.
set -euo pipefail
XCODE_VERSION=16.4
XCODEGEN_VERSION=2.46.0
XCODEGEN_SHA256=4d9e34b62172d645eed6457cac13fc222569974098ef4ee9c3368bedf0196806
: "${RUNNER_TEMP:?Run this inside GitHub Actions.}"
KEYCHAIN="$RUNNER_TEMP/lunalyr-signing.keychain-db"
CERTIFICATE="$RUNNER_TEMP/lunalyr-signing.p12"

select_xcode() {
  local developer="/Applications/Xcode_$XCODE_VERSION.app/Contents/Developer"
  if [[ ! -d "$developer" ]]; then
    echo "::error title=Pinned Xcode missing::This runner image has no Xcode $XCODE_VERSION. Update XCODE_VERSION in Scripts/ci.sh to an installed Xcode 16 or newer once the build passes with it."
    echo "Installed:"
    ls -d /Applications/Xcode*.app 2>/dev/null || true
    exit 1
  fi
  echo "DEVELOPER_DIR=$developer" >> "$GITHUB_ENV"
  DEVELOPER_DIR="$developer" xcodebuild -version
  echo "macOS SDK $(DEVELOPER_DIR="$developer" xcrun --sdk macosx --show-sdk-version)"
}

install_xcodegen() {
  local archive="$RUNNER_TEMP/xcodegen-$XCODEGEN_VERSION.zip"
  local destination="$RUNNER_TEMP/xcodegen-$XCODEGEN_VERSION"
  curl --fail --silent --show-error --location --retry 3 --proto '=https' --output "$archive" \
    "https://github.com/yonaskolb/XcodeGen/releases/download/$XCODEGEN_VERSION/xcodegen.zip"
  local actual
  actual="$(shasum -a 256 "$archive")"
  if [[ "${actual%% *}" != "$XCODEGEN_SHA256" ]]; then
    echo "::error title=XcodeGen checksum::The XcodeGen $XCODEGEN_VERSION download does not match its pinned SHA-256."
    exit 1
  fi
  unzip -o -q "$archive" -d "$destination"
  local reported
  reported="$("$destination/xcodegen/bin/xcodegen" --version)"
  if [[ "$reported" != *"$XCODEGEN_VERSION"* ]]; then
    echo "::error::Expected XcodeGen $XCODEGEN_VERSION, found: $reported"
    exit 1
  fi
  echo "$destination/xcodegen/bin" >> "$GITHUB_PATH"
  echo "XcodeGen $reported"
}

# Keyed on the package pins, the Xcode version and the runner, so a version
# bump elsewhere in project.yml still restores the previous packages.
package_cache_key() {
  local pins prefix
  pins="$(awk '/^packages:/ { keep = 1; print; next } keep && /^[^[:space:]]/ { keep = 0 } keep' project.yml \
    | shasum -a 256)"
  prefix="swift-packages-$(uname -s)-$(uname -m)-xcode$XCODE_VERSION-"
  echo "prefix=$prefix" >> "$GITHUB_OUTPUT"
  echo "key=$prefix${pins:0:16}" >> "$GITHUB_OUTPUT"
}

# A stable identity keeps macOS privacy grants (System Audio, Screen
# Recording) valid across updates; ad hoc signatures change with every build.
# Only the draft-release job calls this. An environment secret reaches only
# jobs that name its environment, so an empty value can also mean the secret
# lives in an environment this job does not use.
import_signing_identity() {
  if [[ -z "${MACOS_CODESIGN_P12:-}" ]]; then
    echo "::notice title=Ad-hoc signature::MACOS_CODESIGN_P12 is not available to this job, so the app stays signed ad hoc and macOS asks again for System Audio / Screen Recording after the update. Once a live release has a stable identity, release preparation stops here instead."
    return 0
  fi
  if [[ -z "${MACOS_CODESIGN_P12_PASSWORD:-}" ]]; then
    echo "::error::MACOS_CODESIGN_P12 is set, but MACOS_CODESIGN_P12_PASSWORD is empty."
    exit 1
  fi
  umask 077
  trap 'rm -f "$CERTIFICATE"' EXIT
  python3 -c 'import base64, os, sys; sys.stdout.buffer.write(base64.b64decode(os.environ["MACOS_CODESIGN_P12"]))' \
    > "$CERTIFICATE"
  local password
  password="$(uuidgen)"
  security create-keychain -p "$password" "$KEYCHAIN"
  security set-keychain-settings -lut 3600 "$KEYCHAIN"
  security unlock-keychain -p "$password" "$KEYCHAIN"
  if ! security import "$CERTIFICATE" -k "$KEYCHAIN" -f pkcs12 -P "$MACOS_CODESIGN_P12_PASSWORD" \
      -T /usr/bin/codesign >/dev/null; then
    echo "::error::The MACOS_CODESIGN_P12 certificate could not be imported; check it and its password."
    exit 1
  fi
  security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$KEYCHAIN" >/dev/null
  # Also searched while codesign builds the certificate chain.
  local existing
  existing="$(security list-keychains -d user | tr -d '"')"
  # shellcheck disable=SC2086 # One keychain path per word.
  security list-keychains -d user -s "$KEYCHAIN" $existing
  local identity="${MACOS_CODESIGN_IDENTITY:-}"
  if [[ -z "$identity" ]]; then
    identity="$(security find-identity -p codesigning "$KEYCHAIN" | awk '/^ *[0-9]+\) [0-9A-F]+ "/ { print $2; exit }')"
  fi
  if [[ -z "$identity" ]]; then
    echo "::error::MACOS_CODESIGN_P12 contains no code-signing identity."
    exit 1
  fi
  echo "LUNALYR_CODE_SIGN_IDENTITY=$identity" >> "$GITHUB_ENV"
  echo "LUNALYR_CODE_SIGN_KEYCHAIN=$KEYCHAIN" >> "$GITHUB_ENV"
  echo "Imported a code-signing identity into a temporary keychain."
}

remove_signing_identity() {
  if [[ -e "$KEYCHAIN" ]]; then
    security delete-keychain "$KEYCHAIN"
  fi
  rm -f "$CERTIFICATE"
}

case "${1:-}" in
  select-xcode) select_xcode ;;
  install-xcodegen) install_xcodegen ;;
  package-cache-key) package_cache_key ;;
  import-signing-identity) import_signing_identity ;;
  remove-signing-identity) remove_signing_identity ;;
  *) echo "usage: Scripts/ci.sh select-xcode | install-xcodegen | package-cache-key | import-signing-identity | remove-signing-identity" >&2
     exit 2 ;;
esac
