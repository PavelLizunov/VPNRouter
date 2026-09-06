#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
renderer="${RESVG:-resvg}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

command -v "$renderer" >/dev/null 2>&1 || {
  echo "resvg 0.47+ is required: https://github.com/linebender/resvg/releases" >&2
  exit 1
}
command -v python3 >/dev/null 2>&1 || {
  echo "python3 is required to package ICO and ICNS containers" >&2
  exit 1
}

render() { "$renderer" -w "$2" -h "$2" "$1" "$3"; }

render "$root/design/project/assets/mascot-master.svg" 640 "$root/VPNRouter.App/Assets/penguin_mascot.png"
render "$root/design/project/assets/mascot-master-dark.svg" 640 "$root/VPNRouter.App/Assets/penguin_mascot_white.png"
render "$root/design/project/assets/penguin.svg" 640 "$root/VPNRouter.App/Assets/penguin_mascot_tile.png"
cp "$root/VPNRouter.App/Assets/penguin_mascot_tile.png" "$root/VPNRouter.App/Assets/penguin_logo.png"

for spec in "mipmap-mdpi 48" "mipmap-hdpi 72" "mipmap-xhdpi 96" "mipmap-xxhdpi 144" "mipmap-xxxhdpi 192"; do
  read -r density size <<<"$spec"
  render "$root/design/project/assets/penguin.svg" "$size" "$root/VPNRouter.Android/Resources/$density/ic_launcher.png"
  render "$root/design/project/assets/penguin-round.svg" "$size" "$root/VPNRouter.Android/Resources/$density/ic_launcher_round.png"
done

for size in 16 24 32 48 64 128 256; do
  render "$root/design/project/assets/mascot-master.svg" "$size" "$tmp/dark-$size.png"
  render "$root/design/project/assets/mascot-master-dark.svg" "$size" "$tmp/light-$size.png"
done
for size in 16 32 64 128 256 512 1024; do
  render "$root/design/project/assets/penguin.svg" "$size" "$tmp/app-$size.png"
done

python3 - "$tmp" "$root/VPNRouter.App/Assets" <<'PY'
from pathlib import Path
import struct
import sys

tmp = Path(sys.argv[1])
out = Path(sys.argv[2])

def write_ico(target, prefix):
    images = [(size, (tmp / f"{prefix}-{size}.png").read_bytes()) for size in (16, 24, 32, 48, 64, 128, 256)]
    offset = 6 + 16 * len(images)
    entries = []
    payload = []
    for size, png in images:
        dim = 0 if size == 256 else size
        entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset))
        payload.append(png)
        offset += len(png)
    target.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(payload))

def write_icns(target):
    chunks = []
    for kind, size in ((b"icp4", 16), (b"ic11", 32), (b"icp5", 32), (b"ic12", 64),
                       (b"icp6", 64), (b"ic07", 128), (b"ic13", 256), (b"ic08", 256),
                       (b"ic14", 512), (b"ic09", 512), (b"ic10", 1024)):
        png = (tmp / f"app-{size}.png").read_bytes()
        chunks.append(kind + struct.pack(">I", len(png) + 8) + png)
    body = b"".join(chunks)
    target.write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)

write_ico(out / "penguin_mascot.ico", "dark")
write_ico(out / "penguin_mascot_white.ico", "light")
write_icns(out / "AppIcon.icns")
PY

echo "PNG, ICO, ICNS, and Android icon assets regenerated from Astra SVG masters."
