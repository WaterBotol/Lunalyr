#!/bin/sh
# Lists every UserDefaults key Lunalyr's sources name, for
# Tests/Swift/SettingsReachabilityTests.swift:
#   - every "custom.*" string literal under Sources (a key built by
#     interpolation shows as its prefix, e.g. "custom.lyricStyle."), and
#   - the older plain keys Settings.swift reads (`forKey: "fontChoice"`).
# Paste the output over the test's `sourceCustomKeys` / `sourcePlainKeys`
# when it changes, then add the new key to SettingsCatalogue (or to its
# excludedKeys, with the reason).
#
# Usage: Scripts/settings-keys.sh [custom|plain]   (default: both, as Swift)
set -eu
cd "$(dirname "$0")/.."

custom_keys() {
    grep -rhoE '"custom\.[A-Za-z0-9_.]+' Sources | tr -d '"' | LC_ALL=C sort -u
}

plain_keys() {
    grep -hoE 'forKey: "[a-z][A-Za-z0-9]*"' Sources/Settings/Settings.swift \
        | sed -E 's/forKey: "([^"]+)"/\1/' | LC_ALL=C sort -u
}

swift_list() {
    printf '    static let %s: [String] = [\n' "$1"
    while IFS= read -r key; do printf '        "%s",\n' "$key"; done
    printf '    ]\n'
}

case "${1:-}" in
    custom) custom_keys ;;
    plain) plain_keys ;;
    *)
        custom_keys | swift_list sourceCustomKeys
        plain_keys | swift_list sourcePlainKeys
        ;;
esac
