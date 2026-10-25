"""Validate CI-only x264 input traces; never emit benchmark acceptance receipts."""
import argparse
import json
from pathlib import Path
import re

STYLES = ('anime', 'swiss', 'blueprint', 'editorial', 'terminal', 'paper', 'kinetic', 'cinema')


def read_trace(path):
    frames = {}
    settings = set()
    opened = set()
    for row in path.read_text().splitlines():
        fields = row.split('\t')
        if fields[0] == 'open':
            assert len(fields) == 9, f'Malformed open row: {path}'
            _, encoder, width, height, csp, threads, lookahead, sync, deterministic = fields
            assert (int(width), int(height), int(csp)) == (854, 480, 2)
            opened.add(encoder)
            settings.add((int(threads), int(lookahead), int(sync), int(deterministic)))
        else:
            assert len(fields) == 6 and fields[0] == 'frame', f'Malformed frame row: {path}'
            _, encoder, pts, *hashes = fields
            assert encoder in opened, f'Frame before open: {path}'
            index = int(pts)
            assert 0 <= index < 1080 and index not in frames, f'Invalid/duplicate PTS: {path}:{pts}'
            assert all(re.fullmatch('[0-9a-f]{64}', value) for value in hashes)
            frames[index] = tuple(hashes)
    assert set(frames) == set(range(1080)), f'Incomplete trace: {path} ({len(frames)} frames)'
    assert settings, f'No encoder settings: {path}'
    return frames, sorted(settings)


def analyze(root):
    decoded = json.loads((root/'summary.json').read_text())
    result = {'kind': 'x264_input_diagnostic', 'metric': None,
              'not_a_measurement_or_quality_receipt': True, 'styles': {}}
    for style in STYLES:
        batches = []
        settings = []
        for index in range(2):
            paths = list((root/f'batch-{index}/input-traces').glob(f'{style}-*.tsv'))
            assert len(paths) == 1, f'Expected one complete render trace: {style}, batch {index}'
            frames, actual = read_trace(paths[0])
            batches.append(frames)
            settings.append(actual)
        changed = [i for i in range(1080) if batches[0][i] != batches[1][i]]
        item = decoded['styles'][style]
        video_equal = item['between_batches']['video']
        assert type(video_equal) is bool, f'Invalid decoded equality: {style}'
        repeated = item['same_file_redecode']
        assert len(repeated) == 2 and all(type(row['video']) is bool for row in repeated)
        decoding_stable = all(row['video'] for row in repeated)
        assert item['frame_rows'] == [1080, 1080], f'Incomplete decoded frames: {style}'
        if not decoding_stable:
            conclusion = 'same_file_video_decode_unstable; original_failure_not_localized'
        elif video_equal:
            conclusion = ('raw_input_differs_but_decoded_video_matches; original_failure_not_localized'
                          if changed else 'no_difference_reproduced_in_this_instrumented_pair')
        elif changed:
            conclusion = 'raw_encoder_input_and_decoded_video_differ; inspect_upstream_input_path'
        else:
            conclusion = 'visible_input_equal_but_decoded_video_differs; inspect_encoder_or_hidden_frame_state'
        result['styles'][style] = {
            'input_frame_count': [len(batch) for batch in batches],
            'actual_encoder_settings_threads_lookahead_sync_deterministic': settings,
            'different_visible_input_frames': changed,
            'decoded_video_equal': video_equal,
            'same_file_video_decoding_stable': decoding_stable,
            'conclusion': conclusion,
        }
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    args = parser.parse_args()
    report = analyze(args.root)
    with (args.root/'input-summary.json').open('x') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps(report, indent=2))
