"""Validate CI-only x264 boundary traces; never emit benchmark acceptance receipts."""
import argparse
import hashlib
import json
from pathlib import Path
import re

STYLES = ('anime', 'swiss', 'blueprint', 'editorial', 'terminal', 'paper', 'kinetic', 'cinema')
EMPTY_SHA256 = hashlib.sha256(b'').hexdigest()
# One explicit ABI-164 field inventory is shared with the C emitter.
PARAM_FIELDS = {'param.' + name: kind for kind, name in re.findall(
    r'^PARAM_(INT|UINT|INT64|FLOAT|PRESENT|CQM)\(([^)]+)\)$',
    Path(__file__).with_name('x264-state-fields.inc').read_text(), re.M)}
PICTURE_FIELDS = ('i_type', 'i_qpplus1', 'i_pic_struct', 'img.i_csp', 'img.i_plane',
    'img.i_stride[0]', 'img.i_stride[1]', 'img.i_stride[2]', 'param',
    'prop.quant_offsets', 'prop.quant_offsets_free', 'prop.mb_info', 'prop.mb_info_free',
    'extra_sei.num_payloads', 'extra_sei.payloads', 'extra_sei.sei_free')
PICTURE_FLAGS = {'pic.' + name for name in ('param', 'prop.quant_offsets',
    'prop.quant_offsets_free', 'prop.mb_info', 'prop.mb_info_free',
    'extra_sei.payloads', 'extra_sei.sei_free')}
# Presence is not equality of pointed-to content. Never dereference unknown bounds.
OPTIONAL_PAYLOADS = ('param.psz_cqm_file', 'param.psz_dump_yuv', 'param.rc.psz_stat_in',
    'param.rc.psz_stat_out', 'param.rc.zones', 'param.rc.psz_zones',
    'param.opencl_device_id', 'param.psz_clbin_file', 'pic.param',
    'pic.prop.quant_offsets', 'pic.prop.mb_info', 'pic.extra_sei.payloads')


def read_state(fields, flush):
    values = {}
    for field in fields:
        name, value = field.split('=', 1)
        assert name not in values, f'Duplicate named state: {name}'
        values[name] = value
    expected = set(PARAM_FIELDS) | {'fp.mxcsr_control', 'fp.x87_control'}
    if not flush:
        expected |= {'pic.' + name for name in PICTURE_FIELDS}
    assert set(values) == expected, 'Missing or unknown named state fields'
    for name, value in values.items():
        kind = PARAM_FIELDS.get(name, 'PRESENT' if name in PICTURE_FLAGS else 'INT')
        pattern = {'FLOAT': '[0-9a-f]{8}', 'CQM': '[0-9a-f]{64}',
                   'PRESENT': '[01]', 'UINT': '[0-9]+'}.get(kind, '-?[0-9]+')
        assert re.fullmatch(pattern, value), f'Invalid named state: {name}={value}'
        if kind in ('INT', 'UINT', 'INT64'):
            bits = 64 if kind == 'INT64' else 32
            low, high = (0, 2**bits - 1) if kind == 'UINT' else (-2**(bits-1), 2**(bits-1) - 1)
            assert low <= int(value) <= high, f'Impossible native scalar: {name}={value}'
    assert tuple(int(values['param.' + name]) for name in ('i_width', 'i_height', 'i_csp')) == (854, 480, 2)
    assert int(values['fp.mxcsr_control']) & ~0xffc0 == 0
    assert 0 <= int(values['fp.x87_control']) <= 65535
    if not flush:
        assert (values['pic.img.i_csp'], values['pic.img.i_plane']) == ('2', '3')
        assert all(int(values[f'pic.img.i_stride[{i}]']) >= width
                   for i, width in enumerate((854, 427, 427)))
        assert int(values['pic.extra_sei.num_payloads']) >= 0
    return values


def payload(fields, result, count, sha, sizes):
    assert result >= 0 and 0 <= count <= 4096, f'Invalid native result: {fields}'
    assert len(sizes) == count and all(size > 0 for size in sizes), f'Invalid NAL sizes: {fields}'
    assert sum(sizes) == result, f'NAL byte count mismatch: {fields}'
    assert re.fullmatch('[0-9a-f]{64}', sha), f'Invalid NAL hash: {fields}'
    assert count or sha == EMPTY_SHA256, f'Invalid empty output hash: {fields}'


def read_boundary(path, expected_frames=1080, segment_frames=24, require_state=False):
    frames, outputs, opened, active, segments = {}, {}, {}, set(), {}
    saw_state = False
    for row in path.read_text().splitlines():
        fields = row.split('\t')
        assert len(fields) >= 2, f'Malformed row: {path}'
        kind, encoder = fields[:2]
        if kind == 'open':
            assert len(fields) == 9 and encoder == str(len(opened) + 1), f'Invalid open generation: {path}'
            values = tuple(map(int, fields[2:]))
            assert values[:3] == (854, 480, 2), f'Invalid geometry: {path}'
            opened[encoder] = {'settings': values[3:], 'inputs': [], 'outputs': [], 'headers': [],
                               'calls': [], 'states': [], 'pending_state': None, 'events': [],
                               'flushes': 0, 'pending_input': False, 'draining': False}
            active.add(encoder)
            continue
        assert encoder in active, f'Call outside open lifetime: {path}:{encoder}'
        item = opened[encoder]
        # Retain relative header/input/output order, not just separate projections.
        item['events'].append(tuple(fields[0:1] + fields[2:]))
        if kind == 'state':
            assert len(fields) > 4 and item['pending_state'] is None and not item['pending_input']
            call, flush = map(int, fields[2:4])
            assert call == len(item['calls']) and flush in (0, 1), 'Invalid state call order'
            assert not item['draining'] or flush, 'Nonflush state after draining'
            state = read_state(fields[4:], flush)
            item['pending_state'] = (call, flush)
            item['states'].append(state)
            saw_state = True
        elif kind == 'frame':
            assert len(fields) == 6 and not item['pending_input'] and not item['draining']
            if require_state or saw_state:
                assert item['pending_state'] == (len(item['calls']), 0), 'Missing input state'
            index = int(fields[2])
            assert 0 <= index < expected_frames and index not in frames, f'Invalid/duplicate PTS: {path}'
            assert all(re.fullmatch('[0-9a-f]{64}', value) for value in fields[3:])
            frames[index] = tuple(fields[3:])
            item['inputs'].append(index)
            item['pending_input'] = True
        elif kind in ('headers', 'output'):
            if kind == 'headers':
                assert len(fields) >= 5 and not item['pending_input'] and not item['draining']
                assert item['pending_state'] is None, 'Header interrupts pending state'
                result, count = map(int, fields[2:4])
                sha, sizes = fields[4], tuple(map(int, fields[5:]))
            else:
                assert len(fields) >= 8
                call, flush, result, count = map(int, fields[2:6])
                pts, sha, sizes = fields[6], fields[7], tuple(map(int, fields[8:]))
                assert call == len(item['calls']) and flush in (0, 1), f'Invalid call order: {path}'
                if require_state or saw_state:
                    assert item['pending_state'] == (call, flush), 'Missing/mismatched call state'
                item['pending_state'] = None
                if flush:
                    assert not item['pending_input'] and item['inputs']
                    item['draining'] = True
                    item['flushes'] += 1
                else:
                    assert item['pending_input'] and not item['draining']
                    item['pending_input'] = False
                if count:
                    index = int(pts)
                    assert index in item['inputs'] and index not in outputs, f'Invalid output PTS: {path}'
                    outputs[index] = (result, count, sha, sizes)
                    item['outputs'].append(index)
                else:
                    assert pts == '-', f'PTS on empty output: {path}'
                item['calls'].append((flush, result, count, pts, sha, sizes))
            payload(fields, result, count, sha, sizes)
            if kind == 'headers':
                assert count > 0, f'Missing encoder headers: {path}'
                item['headers'].append((result, count, sha, sizes))
        elif kind == 'close':
            assert len(fields) == 7 and not item['pending_input'] and item['pending_state'] is None
            assert list(map(int, fields[2:])) == [len(item['inputs']), len(item['outputs']),
                len(item['calls']), item['flushes'], len(item['headers'])], f'Incorrect close totals: {path}'
            assert item['flushes'] and item['headers'], f'Missing flush/header coverage: {path}'
            assert len(item['inputs']) == segment_frames
            start = item['inputs'][0]
            assert start % segment_frames == 0
            assert item['inputs'] == list(range(start, start + segment_frames)), f'Input order: {path}'
            assert sorted(item['outputs']) == item['inputs'], f'Incomplete native output: {path}'
            segment = start // segment_frames
            assert segment not in segments
            segments[segment] = item
            active.remove(encoder)
        else:
            raise AssertionError(f'Unknown trace row: {path}:{kind}')
    assert not active and set(frames) == set(outputs) == set(range(expected_frames)), f'Incomplete trace: {path}'
    assert set(segments) == set(range(expected_frames // segment_frames)), f'Incomplete lifetimes: {path}'
    if require_state or saw_state:
        assert all(len(item['states']) == len(item['calls']) for item in opened.values()), 'Partial state coverage'
    settings = sorted({item['settings'] for item in opened.values()})
    return {'frames': frames, 'outputs': outputs, 'segments': segments, 'settings': settings,
            'named_state_observed': saw_state}


def read_trace(path):
    boundary = read_boundary(path)
    return boundary['frames'], boundary['settings']


def analyze(root, require_state=False):
    decoded = json.loads((root/'summary.json').read_text())
    result = {'kind': 'x264_boundary_diagnostic', 'metric': None,
              'not_a_measurement_or_quality_receipt': True, 'styles': {}}
    for style in STYLES:
        batches = []
        for index in range(2):
            paths = list((root/f'batch-{index}/input-traces').glob(f'{style}-*.tsv'))
            assert len(paths) == 1, f'Expected one complete render trace: {style}, batch {index}'
            batches.append(read_boundary(paths[0], require_state=require_state))
        changed = [i for i in range(1080) if batches[0]['frames'][i] != batches[1]['frames'][i]]
        native = [i for i in range(1080) if batches[0]['outputs'][i] != batches[1]['outputs'][i]]
        headers, calls, settings, state_changes = [], [], [], []
        state_observed = [batch['named_state_observed'] for batch in batches]
        assert state_observed[0] == state_observed[1], 'Mixed state observation modes'
        optional = set()
        for segment in range(45):
            a, b = (batch['segments'][segment] for batch in batches)
            if a['headers'] != b['headers']:
                headers.append(segment)
            if a['events'] != b['events']:
                calls.append(segment)
            if a['settings'] != b['settings']:
                settings.append(segment)
            if state_observed[0]:
                for state in a['states'] + b['states']:
                    optional.update(name for name in OPTIONAL_PAYLOADS if state.get(name) == '1')
                    if int(state['param.rc.i_zones']) or int(state.get('pic.extra_sei.num_payloads', '0')):
                        optional.add('unobserved_counted_payload')
                if len(a['states']) != len(b['states']):
                    state_changes.append({'segment': segment, 'call': None, 'fields': ['call_count']})
                else:
                    for call, (one, two) in enumerate(zip(a['states'], b['states'])):
                        fields = sorted(name for name in one.keys() | two.keys() if one.get(name) != two.get(name))
                        if fields:
                            state_changes.append({'segment': segment, 'call': call, 'fields': fields})
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
            conclusion = ('observed_boundary_differs_but_decoded_video_matches; original_failure_not_localized'
                          if changed or native or headers or calls or settings or state_changes
                          else 'no_difference_reproduced_in_this_instrumented_pair')
        elif changed:
            conclusion = 'raw_encoder_input_and_decoded_video_differ; inspect_upstream_input_path'
        elif state_changes:
            conclusion = 'visible_input_equal_but_named_state_differs; inspect_omitted_interface_state_without_causal_attribution'
        elif native or headers:
            conclusion = ('visible_and_named_fields_equal_but_native_payload_differs; hidden_storage_optional_payloads_and_encoder_state_unresolved'
                          if state_observed[0] else
                          'visible_input_equal_but_native_output_or_state_differs; inspect_hidden_inputs_and_encoder_path')
        elif calls or settings:
            conclusion = 'observed_event_or_open_setting_differs_without_native_payload_divergence; inspect_boundary_state_and_downstream_path'
        else:
            conclusion = 'observed_native_boundary_equal_but_decoded_video_differs; inspect_downstream_path'
        result['styles'][style] = {
            'input_frame_count': [len(batch['frames']) for batch in batches],
            'native_output_frame_count': [len(batch['outputs']) for batch in batches],
            'closed_encoder_lifetimes': [len(batch['segments']) for batch in batches],
            'actual_encoder_settings_threads_lookahead_sync_deterministic': [batch['settings'] for batch in batches],
            'different_visible_input_frames': changed,
            'different_native_output_frames': native,
            'different_header_segments': headers,
            'different_native_call_segments': calls,
            'different_recorded_setting_segments': settings,
            'named_state_observed': state_observed[0],
            'named_state_calls': [sum(len(item['states']) for item in batch['segments'].values()) for batch in batches],
            'different_named_state_calls': state_changes,
            'optional_payloads_not_dereferenced': sorted(optional),
            'complete_semantic_input_equality_established': False,
            'decoded_video_equal': video_equal,
            'same_file_video_decoding_stable': decoding_stable,
            'conclusion': conclusion,
        }
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--require-state', action='store_true')
    args = parser.parse_args()
    report = analyze(args.root, require_state=args.require_state)
    with (args.root/'input-summary.json').open('x') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps(report, indent=2))
