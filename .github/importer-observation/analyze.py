"""Source/data-only checks for one bounded lifetime; no acceptance receipts."""
import hashlib
import importlib.util
from pathlib import Path
import re

START, FRAMES = 768, 24
SPEC = importlib.util.spec_from_file_location('public_boundary',
    Path(__file__).resolve().parents[1]/'diagnostics/analyze_fframes_input.py')
PUBLIC = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLIC)


class TextOperand:
    def __init__(self, text):
        self.text = text

    def read_text(self):
        return self.text


def read_segment(path, *, historical=False):
    """Reuse the unchanged full-trace validator, rebasing only bounded PTS/ID."""
    if historical:
        full = PUBLIC.read_boundary(path, require_state=True)
        item = full['segments'][32]
        frames = {pts: full['frames'][pts] for pts in range(START, START + FRAMES)}
        return {'frames': frames, 'item': item, 'pts_base': START}
    rows = []
    for line in path.read_text().splitlines():
        fields = line.split('\t')
        if len(fields) < 2 or fields[1] != '1':
            raise ValueError('Expected exactly one lifetime with ID 1')
        if fields[0] == 'frame':
            fields[2] = str(int(fields[2]) - START)
        elif fields[0] == 'output' and fields[6] != '-':
            fields[6] = str(int(fields[6]) - START)
        rows.append('\t'.join(fields))
    parsed = PUBLIC.read_boundary(TextOperand('\n'.join(rows)),
                                  expected_frames=FRAMES, segment_frames=FRAMES, require_state=True)
    return {'frames': {pts + START: hashes for pts, hashes in parsed['frames'].items()},
            'item': parsed['segments'][0], 'pts_base': 0}


def inputs(segment):
    item = segment['item']
    # Rebased event PTS differ between historical/current. Preserve call positions,
    # input/flush/header ordering and all named state, not native pointer IDs.
    sequence = []
    for event in item['events']:
        if event[0] == 'frame':
            sequence.append(('frame',))
        elif event[0] == 'output':
            sequence.append(('output', event[1], event[2]))
        elif event[0] in ('headers', 'close'):
            sequence.append((event[0],))
    return {'visible': segment['frames'], 'settings': item['settings'],
            'states': item['states'], 'sequence': sequence,
            'flushes': item['flushes'], 'calls': len(item['calls']),
            'header_count': len(item['headers'])}


def native(segment):
    item = segment['item']
    return {'headers': item['headers'], 'outputs': [(call[1], call[2],
            None if call[3] == '-' else int(call[3]) - segment['pts_base'], call[4], call[5])
            for call in item['calls']]}


def compare_public(left, right):
    return {'observed_inputs_equal': inputs(left) == inputs(right),
            'native_payloads_equal': native(left) == native(right),
            'visible_changed_pts': [pts for pts in range(START, START + FRAMES)
                                    if left['frames'][pts] != right['frames'][pts]],
            'named_state_equal': left['item']['states'] == right['item']['states'],
            'complete_semantic_input_equality_established': False}


def native_uint(text, low, high):
    if not re.fullmatch('0|[1-9][0-9]*', text) or not low <= int(text) <= high:
        raise ValueError('Noncanonical or out-of-range native scalar')
    return int(text)


def read_importer(path, boundary):
    rows = [line.split('\t') for line in path.read_text().splitlines()]
    if len(rows) != 50 or rows[0][:6] != ['identity', '1', '854', '480', '864', '480']:
        raise ValueError('Incomplete importer trace or unsupported geometry')
    if len(rows[0]) != 9 or rows[0][7:] != ['8', '1']:
        raise ValueError('Unsupported native bit depth/threads')
    cpu = native_uint(rows[0][6], 0, 2**32 - 1)
    if cpu != int(boundary['item']['states'][0]['param.cpu']):
        raise ValueError('Importer/public CPU identity mismatch')
    frames = {}
    for index in range(FRAMES):
        entry, coded = rows[1 + index * 2:3 + index * 2]
        expected = ['1', str(index), str(START + index)]
        if (len(entry) != 10 or len(coded) != 8 or entry[0] != 'entry' or coded[0] != 'coded'
                or entry[1:4] != expected or coded[1:4] != expected):
            raise ValueError('Invalid importer stage/PTS/order')
        for value, width in zip(entry[4:7], (854, 427, 427)):
            native_uint(value, width, 2**31 - 1)
        for value in coded[4:6]:
            native_uint(value, 864, 2**31 - 1)
        if any(not re.fullmatch('[0-9a-f]{64}', value) for value in entry[7:] + coded[6:]):
            raise ValueError('Invalid rectangle hash')
        if tuple(entry[7:]) != boundary['frames'][START + index]:
            raise ValueError('Public/importer visible input mismatch')
        state = boundary['item']['states'][index]
        if entry[4:7] != [state[f'pic.img.i_stride[{i}]'] for i in range(3)]:
            raise ValueError('Public/importer stride mismatch')
        frames[START + index] = {'entry': entry[7:], 'coded': coded[6:]}
    if rows[-1] != ['complete', '1', '24']:
        raise ValueError('Missing importer completion')
    return {'cpu': cpu, 'frames': frames, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}


def compare_importer(left, right):
    return {stage + '_changed_pts': [pts for pts in range(START, START + FRAMES)
            if left['frames'][pts][stage] != right['frames'][pts][stage]]
            for stage in ('entry', 'coded')}
