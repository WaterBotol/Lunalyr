#!/bin/bash
set -euo pipefail
project="$(cd -- "$(dirname -- "$0")/.." && pwd)"
revision=5ef5a252deef71290096b1c5dd1f0edc0dc93acd
source_repo="${1:?Pass a local clone of https://github.com/dineshkadavakuduru-cmd/wavecore}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

git -C "$source_repo" archive "$revision" | tar -xf - -C "$stage"
cp "$project"/ThirdParty/Wavecore/lunalyr-* "$stage/"
(
  cd "$stage"
  git apply --recount "$project/ThirdParty/Wavecore/canvas.patch" "$project/ThirdParty/Wavecore/brightness.patch"
  npm ci --legacy-peer-deps --ignore-scripts --no-audit --no-fund
  npx --yes --package esbuild@0.25.12 esbuild lunalyr-engine.tsx \
    --bundle --minify --jsx=automatic --target=safari17 --outfile=wavecore.js --metafile=inputs.json \
    --alias:@/lib/audio/provider=./lunalyr-provider.ts \
    --alias:@react-three/drei=@react-three/drei/core/Sparkles.js
)
python3 - "$stage" "$project/Sources/Resources" "$project/ThirdParty/Wavecore" <<'PY'
import json
from pathlib import Path
import re
import sys

stage, resources, third_party = map(Path, sys.argv[1:])
script = re.sub(r"</script", r"<\\/script", (stage / "wavecore.js").read_text(), flags=re.I)
# Wavecore's camera and core cap each frame's step at 50 ms and its audio
# driver at 1/20 s, so a 15 fps wallpaper ran them at 75% speed. Lunalyr's
# clock caps a step at 0.1 s; raise theirs to match (ThirdParty/Wavecore/SOURCE.md).
script, caps = re.subn(r"Math\.min\((\w+),\.05\)", r"Math.min(\1,.1)", script)
script, drivers = re.subn(r"\b(\w+)=1/20,", r"\1=.1,", script)
if (caps, drivers) != (2, 1):
    raise SystemExit(f"Expected Wavecore's two 50 ms step caps and one 1/20 s driver step, found {caps} and "
                     f"{drivers}: update the frame-rate patch described in ThirdParty/Wavecore/SOURCE.md")
(resources / "WavecoreEngine.html").write_text(
    '<!doctype html>\n<meta charset="utf-8">\n'
    '<meta http-equiv="Content-Security-Policy" '
    'content="default-src \'none\'; script-src \'unsafe-inline\'; style-src \'unsafe-inline\'">\n'
    '<style>html,body,#wavecore{margin:0;width:100%;height:100%;overflow:hidden;background:#04050a}</style>\n'
    '<div id="wavecore"></div>\n<script>\n' + script + '\n</script>\n'
)

# Include each package that contributes code to the actual bundled output.
outputs = json.loads((stage / "inputs.json").read_text())["outputs"]
packages = set()
for output in outputs.values():
    for path, contribution in output["inputs"].items():
        if "node_modules/" not in path or contribution["bytesInOutput"] == 0:
            continue
        parent, name = path.rsplit("node_modules/", 1)
        parts = name.split("/")
        packages.add(parent + "node_modules/" + "/".join(parts[:2 if name.startswith("@") else 1]))
notices = []
for name in sorted(packages):
    directory = stage / name
    metadata = json.loads((directory / "package.json").read_text())
    licence_files = sorted(path for path in directory.iterdir() if path.is_file()
                           and re.match(r"(licen[cs]e|copying|notice)(\.|$)", path.name, re.I))
    if not licence_files and metadata["name"] == "@react-three/fiber":
        licence_files = [third_party / f'react-three-fiber-{metadata["version"]}-LICENSE.txt']
    if not licence_files:
        raise SystemExit(f"Missing licence files for {name}")
    notices.append(f'{metadata["name"]} {metadata["version"]}\n\n' +
                   "\n\n".join(path.read_text().strip() for path in licence_files))
(resources / "Wavecore-Web-Dependencies-LICENSE.txt").write_text("\n\n".join(notices) + "\n")
PY
