#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
renderer="${RESVG:-resvg}"

command -v "$renderer" >/dev/null 2>&1 || {
  echo "resvg 0.47+ is required: https://github.com/linebender/resvg/releases" >&2
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
  render "$root/design/project/assets/penguin.svg" "$size" "$root/VPNRouter.Android/Resources/$density/ic_launcher_round.png"
done

echo "PNG and Android icon assets regenerated from Astra SVG masters."
echo "ICO and ICNS container refresh requires a platform tool and remains a separate packaging step."
