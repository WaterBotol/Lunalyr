#!/bin/bash
set -euo pipefail
project="$(cd -- "$(dirname -- "$0")/.." && pwd)"
revision=217f285645914866df7ca685a7194308a42fd430
source_repo="${1:?Pass a local clone of https://github.com/coreyepstein/cymatic}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

git -C "$source_repo" archive "$revision" | tar -xf - -C "$stage"
cp "$project/ThirdParty/Cymatic/lunalyr-light-3d.ts" "$stage/"
(
  cd "$stage"
  npx --yes --package esbuild@0.25.12 esbuild lunalyr-light-3d.ts \
    --bundle --minify --target=safari17 --outfile=light-3d.js \
    --alias:@cymatic/core=./packages/core/src/index.ts
)
python3 - "$stage" "$project/Sources/Resources" <<'PY'
from pathlib import Path
import re
import sys

stage, resources = map(Path, sys.argv[1:])
script = re.sub(r"</script", r"<\\/script", (stage / "light-3d.js").read_text(), flags=re.I)
(resources / "Light3DEngine.html").write_text(
    '<!doctype html>\n<meta charset="utf-8">\n'
    '<meta http-equiv="Content-Security-Policy" '
    'content="default-src \'none\'; script-src \'unsafe-inline\'; style-src \'unsafe-inline\'">\n'
    '<style>html,body,canvas{margin:0;width:100%;height:100%;overflow:hidden;display:block;background:#000}</style>\n'
    '<canvas></canvas>\n<script>\n' + script + '\n</script>\n'
)
PY
cp "$stage/LICENSE" "$project/Sources/Resources/Cymatic-LICENSE.txt"
