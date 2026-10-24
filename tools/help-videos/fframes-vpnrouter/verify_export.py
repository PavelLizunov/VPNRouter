#!/usr/bin/env python3
"""Check the encoded consumer artifact; this is not a rendering substitute."""
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent
expected = (("vpnrouter-film.mp4", 1920, 1080), ("vpnrouter-github.mp4", 1280, 720))
for name, width, height in expected:
    data = json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json",
        str(root / "out" / name),
    ], text=True))
    videos = [s for s in data["streams"] if s["codec_type"] == "video"]
    audios = [s for s in data["streams"] if s["codec_type"] == "audio"]
    assert len(videos) == len(audios) == 1, (name, "expected one video and one audio stream")
    stream = videos[0]
    assert (stream["width"], stream["height"]) == (width, height), name
    assert stream["codec_name"] == "h264", name
    assert stream["pix_fmt"] == "yuv420p", name
    assert stream["avg_frame_rate"] == "30/1", name
    assert int(stream["nb_frames"]) == 1080, (name, stream["nb_frames"])
    assert abs(float(stream["duration"]) - 36.0) < 0.05, name
    assert audios[0]["codec_name"] == "aac", name
    assert abs(float(audios[0]["duration"]) - 36.0) < 0.1, name
    subprocess.run(["ffmpeg", "-v", "error", "-xerror", "-i", str(root / "out" / name), "-f", "null", "-"], check=True)
    print(f"PASS {name}: {width}x{height}, 1080 frames, 36 seconds, H.264 + AAC, complete decode")
