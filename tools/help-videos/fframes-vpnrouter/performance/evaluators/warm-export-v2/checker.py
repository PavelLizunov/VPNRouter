#!/usr/bin/env python3
"""Validate actual encoded previews/finals, checksums and standalone source bytes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

STYLES = ('anime', 'swiss', 'blueprint', 'editorial', 'terminal', 'paper', 'kinetic', 'cinema')
FORBIDDEN = ('vpnrouter', 'pavel', 'lizunov', 'github', 'http')
TIMESTAMPS = ('3s', '9s', '16s', '22s', '28s', '34s')
SOURCE = ('Cargo.toml', 'Cargo.lock', 'src', 'media', 'licenses', 'README.md', 'AGENTS.md',
          'LICENSE', 'export.sh', 'export-styles.sh', 'verify_export.py', 'verify_styles.py',
          'test_pipeline.py', '.gitignore')


def selected_styles(mode, values):
    if mode not in ('preview', 'final'):
        raise ValueError('Expected preview or final mode')
    styles = tuple(values) if values else STYLES if mode == 'preview' else ()
    if not styles or len(set(styles)) != len(styles) or not set(styles) <= set(STYLES):
        raise ValueError('Expected unique, supported, explicitly selected final styles')
    if mode == 'preview' and styles != STYLES:
        raise ValueError('Preview must contain all eight styles in canonical order')
    return styles


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_checksums(path, base):
    entries = {}
    for line in path.read_text().splitlines():
        digest, name = line.split(None, 1)
        name = name.lstrip('*')
        target = (base / name).resolve()
        assert target.is_relative_to(base.resolve()), 'Checksum path escaped output'
        assert name not in entries and len(digest) == 64
        assert sha(target) == digest, (name, 'checksum mismatch')
        entries[name] = digest
    assert entries, 'Empty checksum manifest'
    return entries


def verify_archive(path, root):
    expected = {f.relative_to(root).as_posix(): f
                for item in SOURCE
                for f in ((root/item).rglob('*') if (root/item).is_dir() else [root/item])
                if f.is_file()}
    with zipfile.ZipFile(path) as archive:
        assert archive.testzip() is None
        names = [n for n in archive.namelist() if not n.endswith('/')]
        assert len(names) == len(set(names)) and set(names) == set(expected), 'Archive entry mismatch'
        for name, source in expected.items():
            assert archive.read(name) == source.read_bytes(), (name, 'source byte mismatch')
    print(f'PASS source ZIP: {len(expected)} exact files, CRC, media and licenses')


def verify_streams(data, mode, style):
    videos = [s for s in data['streams'] if s['codec_type'] == 'video']
    audios = [s for s in data['streams'] if s['codec_type'] == 'audio']
    assert len(videos) == len(audios) == 1
    v, a = videos[0], audios[0]
    dimensions = (854, 480) if mode == 'preview' else (1920, 1080)
    assert (v['width'], v['height']) == dimensions, (style, 'dimensions')
    assert (v['codec_name'], v['pix_fmt'], v['avg_frame_rate']) == ('h264', 'yuv420p', '30/1')
    assert int(v['nb_frames']) == 1080 and abs(float(v['duration'])-36) < 0.05
    assert a['codec_name'] == 'aac' and abs(float(a['duration'])-36) < 0.1
    title = data['format']['tags']['title']
    assert title == f'Choose your routes | {style}'
    for tags in [data['format'].get('tags', {}), *(s.get('tags', {}) for s in data['streams'])]:
        assert all(term not in tags.get('title', '').lower() for term in FORBIDDEN)
    return dimensions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mode', choices=('preview', 'final'), default='preview')
    parser.add_argument('styles', nargs='*')
    args = parser.parse_args()
    styles = selected_styles(args.mode, args.styles)
    root = Path(__file__).resolve().parent
    outputs = root/'out/styles'
    mode_dir = outputs/args.mode
    hashes, pictures = set(), set()
    for style in styles:
        folder = mode_dir/style
        entries = verify_checksums(folder/'SHA256SUMS.txt', folder)
        assert {'film.mp4', 'contact-sheet.png', 'timeline.json', 'inspection.json',
                'continuity.json', 'ffprobe.json', 'audio-levels.txt'} <= entries.keys()
        for stamp in TIMESTAMPS:
            for suffix in ('png', 'svg'):
                assert f'frames/{stamp}.{suffix}' in entries
        timeline = json.loads((folder/'timeline.json').read_text())
        assert (timeline['fps'], timeline['duration_frames'], timeline['duration_seconds']) == (30, 1080, 36.0)
        assert len(timeline['scenes']) == 6 and timeline['scenes'][-1]['end_frame'] == 1080
        inspection = json.loads((folder/'inspection.json').read_text())
        assert inspection['checked_frames'] == 1080
        assert not inspection['findings'], (style, inspection['findings'])
        continuity = json.loads((folder/'continuity.json').read_text())
        assert continuity['pop_scanned_frames'] == 1080
        film = folder/'film.mp4'
        assert film.stat().st_size > 100_000, (style, 'not a full film')
        data = json.loads(subprocess.check_output(['ffprobe', '-v', 'error', '-show_format',
                '-show_streams', '-of', 'json', str(film)], text=True))
        dimensions = verify_streams(data, args.mode, style)
        subprocess.run(['ffmpeg', '-v', 'error', '-xerror', '-i', str(film), '-f', 'null', '-'], check=True)
        hashes.add(sha(film))
        # File hashes alone can differ only because metadata differs. Check a
        # decoded representative picture too; this is not subjective review.
        pixels = subprocess.check_output(['ffmpeg', '-v', 'error', '-ss', '3', '-i', str(film),
                    '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'])
        pictures.add(hashlib.sha256(pixels).hexdigest())
        print(f'PASS {args.mode}/{style}: {dimensions}, 1080 frames, 36s H.264/AAC, full decode')
    assert len(hashes) == len(pictures) == len(styles), 'Duplicate films or decoded pictures'
    if args.mode == 'preview':
        assert {p.parent.name for p in mode_dir.glob('*/film.mp4')} == set(STYLES)
    entries = verify_checksums(mode_dir/'SHA256SUMS.txt', outputs)
    assert {'film-styles-source.zip', *(f'{args.mode}/{s}/film.mp4' for s in styles),
            *(f'{args.mode}/{s}/SHA256SUMS.txt' for s in styles)} == entries.keys()
    verify_archive(outputs/'film-styles-source.zip', root)


if __name__ == '__main__':
    main()
