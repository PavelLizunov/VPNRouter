"""Full-history projections over the unchanged public x264 trace validator."""
import importlib.util
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    'full_history_public_boundary',
    Path(__file__).resolve().parents[1]/'diagnostics/analyze_fframes_input.py')
BOUNDARY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BOUNDARY)


def read_public(path, lifetimes):
    if lifetimes not in (2, 45):
        raise ValueError('Unsupported bounded lifetime manifest')
    value = BOUNDARY.read_boundary(path, expected_frames=24*lifetimes,
                                   segment_frames=24, require_state=True)
    # The original one-worker path must not overlap encoder lifetimes.
    active = None
    generation = 0
    submitted = 0
    for row in path.read_text().splitlines():
        fields = row.split('\t')
        if fields[0] == 'open':
            if active is not None:
                raise ValueError('Overlapping encoder lifetimes')
            active = fields[1]
            generation += 1
            submitted = 0
        elif fields[0] == 'close':
            if fields[1] != active or submitted != 24:
                raise ValueError('Close does not match active complete lifetime')
            active = None
        elif fields[1] != active:
            raise ValueError('Public event outside sequential lifetime')
        elif fields[0] == 'frame':
            if int(fields[2]) != (generation-1)*24 + submitted:
                raise ValueError('Public generation partition order mismatch')
            submitted += 1
    if active is not None:
        raise ValueError('Unclosed public lifetime')
    for index, item in value['segments'].items():
        if item['inputs'] != list(range(index*24, (index+1)*24)):
            raise ValueError('Unexpected original segment partition')
        if len(item['headers']) != 1:
            raise ValueError('Expected one complete header per lifetime')
        for state in item['states']:
            validate_state(state)
    return value


def validate_state(state):
    expected = {
        'i_width': '854', 'i_height': '480', 'i_csp': '2', 'i_bitdepth': '8',
        'i_threads': '1', 'i_lookahead_threads': '1', 'i_sync_lookahead': '0',
        'b_sliced_threads': '0', 'b_deterministic': '1', 'b_interlaced': '0',
        'rc.i_aq_mode': '1', 'rc.f_aq_strength': '3f19999a', 'rc.b_mb_tree': '1',
        'rc.i_vbv_buffer_size': '0', 'rc.i_vbv_max_bitrate': '0',
        'rc.b_stat_read': '0', 'rc.b_stat_write': '0', 'rc.i_zones': '0',
        'b_opencl': '0', 'b_repeat_headers': '0', 'b_annexb': '1',
    }
    for name, expected_value in expected.items():
        if state['param.'+name] != expected_value:
            raise ValueError('Unsupported observed native setting: '+name)
    # Default stat filename pointers may be present but are inactive when both
    # stat flags are zero. No optional payload content is inferred from presence.
    inactive_names = {'param.rc.psz_stat_in', 'param.rc.psz_stat_out'}
    for name in BOUNDARY.OPTIONAL_PAYLOADS:
        if name in state and name not in inactive_names and state[name] != '0':
            raise ValueError('Unobserved optional native payload: '+name)
    for name in ('prop.quant_offsets_free', 'prop.mb_info_free', 'extra_sei.sei_free'):
        if state.get('pic.'+name, '0') != '0':
            raise ValueError('Unsupported optional picture callback')
    if state.get('pic.extra_sei.num_payloads', '0') != '0':
        raise ValueError('Unsupported extra picture SEI count')


def observed(value):
    result = []
    for index, item in sorted(value['segments'].items()):
        shape = []
        for event in item['events']:
            kind = event[0]
            if kind == 'state':
                shape.append((kind, event[1], event[2]))
            elif kind == 'frame':
                shape.append((kind, event[1]))
            elif kind == 'output':
                shape.append((kind, event[1], event[2], event[5]))
            elif kind in ('headers', 'close'):
                shape.append((kind,))
            else:
                raise ValueError('Unknown public event projection')
        result.append((index, item['settings'], item['inputs'], item['states'],
                       shape, item['flushes']))
    return {'visible': value['frames'], 'segments': result}


def native(value):
    return [(index, item['headers'], item['calls'])
            for index, item in sorted(value['segments'].items())]


def compare_public(left, right):
    if set(left['frames']) != set(right['frames']):
        raise ValueError('Public operand coverage differs')
    return {
        'observed_inputs_equal': observed(left) == observed(right),
        'native_payloads_equal': native(left) == native(right),
        'visible_changed_pts': [pts for pts in sorted(left['frames'])
                                if left['frames'][pts] != right['frames'][pts]],
        'complete_semantic_input_equality_established': False,
    }
