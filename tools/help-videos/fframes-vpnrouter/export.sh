#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
mkdir -p out/frames
export RAYON_NUM_THREADS="${RAYON_NUM_THREADS:-2}"

cargo build --release
cargo test --release
bin=./target/release/vpnrouter-film
"$bin" timeline --json > out/timeline.json
"$bin" inspect --all-frames --fail-on warning --json > out/inspection.json
"$bin" strip 0.8s,3s,6s,10s,13s,16s,20s,23s,26s,28s,31s,35s -n 12 --columns 3 --width 640 -o out/contact-sheet.png
"$bin" frame 3s,9s,16s,22s,28s,34s -o out/frames
"$bin" inspect --pops --json > out/continuity.json
"$bin" render -o out/vpnrouter-film-silent.mp4

# The visual master is rendered entirely by fframes. FFmpeg only adds the licensed
# soundtrack, delivery metadata and a smaller distribution copy.
ffmpeg -hide_banner -y -i out/vpnrouter-film-silent.mp4 -i media/interchange.mp3 \
  -map 0:v:0 -map 1:a:0 -t 36 -c:v copy -c:a aac -b:a 160k \
  -af 'atrim=0:36,asetpts=PTS-STARTPTS,loudnorm=I=-18:TP=-1.5:LRA=9,afade=t=in:d=0.7,afade=t=out:st=33:d=3' \
  -movflags +faststart -metadata title='VPNRouter | Choose your routes' \
  -metadata comment='Rust/SVG motion rendered with fframes. UI images are sample-data renders. Music: Interchange by TRG Banks (CC0).' \
  out/vpnrouter-film.mp4
ffmpeg -hide_banner -y -i out/vpnrouter-film.mp4 -vf 'scale=1280:720' \
  -c:v libx264 -preset fast -crf 24 -pix_fmt yuv420p -c:a aac -b:a 96k \
  -movflags +faststart out/vpnrouter-github.mp4
ffprobe -v error -show_format -show_streams -of json out/vpnrouter-film.mp4 > out/ffprobe.json
ffmpeg -v error -xerror -i out/vpnrouter-film.mp4 -f null -
ffmpeg -hide_banner -i out/vpnrouter-film.mp4 -vn -af ebur128=peak=true -f null - 2> out/audio-levels.txt
python3 verify_export.py

# Keep the deliverable source archive self-contained, with media and licenses,
# without the compiled binary, build cache or source-repository credentials.
zip -q -r out/vpnrouter-film-source.zip Cargo.toml Cargo.lock README.md LICENSE \
  src media licenses export.sh verify_export.py .gitignore
sha256sum out/vpnrouter-film.mp4 out/vpnrouter-github.mp4 out/vpnrouter-film-source.zip > out/SHA256SUMS.txt
