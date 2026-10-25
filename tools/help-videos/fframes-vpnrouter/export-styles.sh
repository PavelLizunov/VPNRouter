#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"

mode="${1:-preview}"
if [ "$#" -gt 0 ]; then shift; fi
all_styles=(anime swiss blueprint editorial terminal paper kinetic cinema)
case "$mode" in
  preview)
    if [ "$#" -gt 0 ]; then echo 'Preview always exports all eight options.' >&2; exit 2; fi
    styles=("${all_styles[@]}")
    scale=0.4444444444444444
    ;;
  final)
    if [ "$#" -eq 0 ]; then echo 'Final export requires owner-selected style slugs.' >&2; exit 2; fi
    styles=("$@")
    scale=1
    ;;
  *) echo 'Usage: export-styles.sh preview | final <selected-style> ...' >&2; exit 2 ;;
esac
seen=' '
for style in "${styles[@]}"; do
  case "$style" in
    anime|swiss|blueprint|editorial|terminal|paper|kinetic|cinema) ;;
    *) echo "Unknown style: $style" >&2; exit 2 ;;
  esac
  case "$seen" in *" $style "*) echo "Duplicate style: $style" >&2; exit 2 ;; esac
  seen+="$style "
done

export RAYON_NUM_THREADS="${RAYON_NUM_THREADS:-2}"
# Compile against the runner's x264 ABI, not the incompatible prebuilt archive.
export FFMPEG_FORCE_BUILD=1
cargo build --release --locked
cargo test --release --locked
python3 -m unittest -v test_pipeline.py
bin=./target/release/vpnrouter-film
mkdir -p "out/styles/$mode"

for style in "${styles[@]}"; do
  echo "=== Exporting $mode: $style ==="
  out_dir="out/styles/$mode/$style"
  mkdir -p "$out_dir/frames"
  export FILM_STYLE="$style"
  "$bin" timeline --json > "$out_dir/timeline.json"
  "$bin" inspect --all-frames --fail-on warning --json > "$out_dir/inspection.json"
  "$bin" strip 3s,9s,16s,22s,28s,34s -n 6 --columns 3 --width 640 -o "$out_dir/contact-sheet.png"
  "$bin" --scale "$scale" frame 3s,9s,16s,22s,28s,34s --svg -o "$out_dir/frames"
  "$bin" inspect --pops --fail-on never --json > "$out_dir/continuity.json"
  "$bin" --scale "$scale" render -o "$out_dir/film-silent.mp4"
  ffmpeg -hide_banner -y -i "$out_dir/film-silent.mp4" -i media/interchange.mp3 \
    -map 0:v:0 -map 1:a:0 -t 36 -c:v copy -c:a aac -b:a 128k \
    -af 'atrim=0:36,asetpts=PTS-STARTPTS,loudnorm=I=-18:TP=-1.5:LRA=9,afade=t=in:d=0.7,afade=t=out:st=33:d=3' \
    -movflags +faststart -metadata title="Choose your routes | $style" \
    -metadata comment="${mode} render from Rust/SVG sources. UI uses sample data. Music: Interchange by TRG Banks (CC0)." \
    "$out_dir/film.mp4"
  ffprobe -v error -show_format -show_streams -of json "$out_dir/film.mp4" > "$out_dir/ffprobe.json"
  ffmpeg -hide_banner -i "$out_dir/film.mp4" -vn -af ebur128=peak=true -f null - 2> "$out_dir/audio-levels.txt"
  (
    cd "$out_dir"
    sha256sum film.mp4 contact-sheet.png timeline.json inspection.json continuity.json ffprobe.json audio-levels.txt frames/* > SHA256SUMS.txt
  )
done

# Rebuild rather than append to an old ZIP; include no caches or Git credentials.
python3 - <<'PY'
from pathlib import Path
import zipfile
items = ('Cargo.toml', 'Cargo.lock', 'src', 'media', 'licenses', 'README.md',
         'AGENTS.md', 'LICENSE', 'export.sh', 'export-styles.sh', 'verify_export.py',
         'verify_styles.py', 'test_pipeline.py', '.gitignore')
with zipfile.ZipFile('out/styles/film-styles-source.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for item in items:
        path = Path(item)
        for file in sorted(path.rglob('*')) if path.is_dir() else [path]:
            if file.is_file():
                archive.write(file, file.as_posix())
PY
(
  cd out/styles
  files=(film-styles-source.zip)
  for style in "${styles[@]}"; do files+=("$mode/$style/film.mp4" "$mode/$style/SHA256SUMS.txt"); done
  sha256sum "${files[@]}" > "$mode/SHA256SUMS.txt"
)
python3 verify_styles.py --mode "$mode" "${styles[@]}" | tee "out/styles/$mode/verification.txt"
