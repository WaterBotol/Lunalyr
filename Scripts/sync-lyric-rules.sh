#!/bin/bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "Usage: $0 <Windows repository checkout> [commit|--check]" >&2
  exit 2
fi

root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
destination="$root/Shared/LyricRules"
mode="${2:-HEAD}"
revision="$mode"
if [[ "$mode" == "--check" ]]; then
  revision="$(cat "$destination/UPSTREAM")"
fi
commit="$(git -C "$1" rev-parse --verify --end-of-options "$revision^{commit}")"
staged="$(mktemp -d)"
trap 'rm -rf "$staged"' EXIT
files=(LyricRules.h LyricRules.c Fixtures.json)

for name in "${files[@]}"; do
  git -C "$1" show "$commit:shared/LyricRules/$name" > "$staged/$name"
done

if [[ "$mode" == "--check" ]]; then
  for name in "${files[@]}"; do
    cmp "$staged/$name" "$destination/$name"
  done
else
  mkdir -p "$destination"
  cp "$staged/"* "$destination/"
  printf '%s\n' "$commit" > "$destination/UPSTREAM"
fi
