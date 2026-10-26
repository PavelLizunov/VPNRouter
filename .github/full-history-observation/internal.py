"""Strict saved-data validator for ffh-v1; no native execution or decimal floats.

read_internal(folder, boundary, lifetimes) consumes an already-validated unchanged
analyze_fframes_input.read_boundary(..., require_state=True) result. Expected
lifetimes is explicit (2 for synthetic, 45 for the original film), never inferred.
The TSV grammar is the emitter in full-history-observer.h. Descriptors are
name=type,width,height,source_stride,packed_offset,size,sha256. Stream offsets are
contiguous and exact; only initialized packed rectangles/typed objects are valid.
"""
import hashlib
from pathlib import Path
import re
import stat

STAGES = ('entry', 'coded', 'post_aq', 'pre_slice', 'qpm')
OPEN_FIXED = {'threads': '1', 'thread_frames': '1', 'lookahead_threads': '1',
    'sync_lookahead': '0', 'sliced_threads': '0', 'interlaced': '0',
    'fake_interlaced': '0', 'pic_struct': '0', 'mb_interlaced': '0',
    'mb_width': '54', 'mb_height': '30', 'mb_stride': '54', 'mb_count': '1620',
    'lowres': '1', 'aq_mode': '1', 'aq_strength': '3f19999a', 'mbtree': '1',
    'stat_read': '0', 'stat_write': '0', 'vbv_maxrate': '0', 'vbv_buffer': '0'}
PUBLIC_GATE = {'i_width': '854', 'i_height': '480', 'i_csp': '2', 'i_bitdepth': '8',
    'i_threads': '1', 'i_lookahead_threads': '1', 'i_sync_lookahead': '0',
    'b_sliced_threads': '0', 'b_interlaced': '0', 'b_fake_interlaced': '0',
    'b_pic_struct': '0', 'rc.i_aq_mode': '1', 'rc.f_aq_strength': '3f19999a',
    'rc.b_mb_tree': '1', 'rc.b_stat_read': '0', 'rc.b_stat_write': '0',
    'rc.i_vbv_max_bitrate': '0', 'rc.i_vbv_buffer_size': '0', 'rc.i_zones': '0',
    'rc.zones': '0', 'rc.psz_zones': '0', 'psz_cqm_file': '0', 'psz_dump_yuv': '0',
    'b_opencl': '0', 'opencl_device_id': '0', 'psz_clbin_file': '0', 'nalu_process': '0'}
OPEN_PUBLIC = {'cpu': 'cpu', 'threads': 'i_threads', 'lookahead_threads': 'i_lookahead_threads',
    'sync_lookahead': 'i_sync_lookahead', 'sliced_threads': 'b_sliced_threads',
    'interlaced': 'b_interlaced', 'fake_interlaced': 'b_fake_interlaced',
    'pic_struct': 'b_pic_struct', 'aq_mode': 'rc.i_aq_mode',
    'aq_strength': 'rc.f_aq_strength', 'mbtree': 'rc.b_mb_tree',
    'stat_read': 'rc.b_stat_read', 'stat_write': 'rc.b_stat_write',
    'vbv_maxrate': 'rc.i_vbv_max_bitrate', 'vbv_buffer': 'rc.i_vbv_buffer_size',
    'stat_in_present': 'rc.psz_stat_in', 'stat_out_present': 'rc.psz_stat_out'}
RECTS = {'entry': {'Y': ('u8', 854, 480), 'U': ('u8', 427, 240), 'V': ('u8', 427, 240)},
    'coded': {'Y': ('u8', 864, 480), 'UV': ('u8', 864, 240)},
    'post_aq': {'offset': ('f32', 1620, 1), 'offset_aq': ('f32', 1620, 1), 'inv_qscale': ('u16', 1620, 1)},
    'pre_slice': {'offset': ('f32', 1620, 1), 'offset_aq': ('f32', 1620, 1)},
    'qpm': {'qpm': ('f32', 1, 1)}}
META = {'entry': ('pic_type', 'qpplus1', 'pic_struct'), 'coded': (), 'post_aq': (),
    'qpm': ('type', 'ref', 'b_vbv', 'bits'),
    'pre_slice': ('type', 'ref', 'slice_type', 'slice_init_qp', 'rc_global_qp')}


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def integer(text, low=0, high=2**63-1):
    require(isinstance(text, str) and re.fullmatch(r'0|-?[1-9][0-9]*', text), 'Noncanonical integer')
    value = int(text)
    require(low <= value <= high, 'Integer outside bounded schema')
    return value


def named(fields):
    result = {}
    for field in fields:
        require(field.count('=') == 1, 'Malformed named field')
        name, value = field.split('=')
        require(name not in result, 'Duplicate named field')
        result[name] = value
    return result


def file_hash(stream):
    stream.seek(0)
    digest = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        digest.update(chunk)
    return digest.hexdigest()


def read_extent(stream, descriptor, expected, offset):
    """Bounded helper shared by real parser and focused extent mutation tests."""
    fields = descriptor.split(',')
    require(len(fields) == 7, 'Malformed raw descriptor')
    kind, width, height, stride, start, size, digest = fields
    width, height, stride, start, size = [integer(v) for v in (width, height, stride, start, size)]
    require((kind, width, height) == expected, 'Wrong raw type/geometry')
    itemsize = {'u8': 1, 'u16': 2, 'f32': 4}[kind]
    require(size == width * height * itemsize and start == offset, 'Wrong raw offset/size')
    require(width * itemsize <= stride <= 2**31-1, 'Invalid source stride')
    if kind != 'u8':
        require(stride == size and height == 1, 'Typed array has slack or wrong object bounds')
    require(re.fullmatch('[0-9a-f]{64}', digest), 'Malformed raw SHA256')
    stream.seek(start)
    payload = stream.read(size)
    require(len(payload) == size, 'Truncated raw stream')
    require(hashlib.sha256(payload).hexdigest() == digest, 'Raw SHA256 mismatch')
    result = {'type': kind, 'width': width, 'height': height, 'stride': stride,
              'offset': start, 'size': size, 'sha256': digest}
    return result, payload if size == 4 else None


def _public(boundary, count):
    require(boundary.get('named_state_observed') is True, 'Named public state required')
    require(set(boundary['frames']) == set(boundary['outputs']) == set(range(count * 24)), 'Public PTS coverage')
    require(set(boundary['segments']) == set(range(count)), 'Public lifetime coverage')
    by_pts = {}
    for k in range(count):
        segment = boundary['segments'][k]
        require(segment['inputs'] == list(range(k*24, (k+1)*24)), 'Public lifetime order')
        require(len(segment['states']) == len(segment['calls']), 'Public state/call coverage')
        inputs = iter(segment['inputs'])
        for state, call in zip(segment['states'], segment['calls']):
            for name, expected in PUBLIC_GATE.items():
                require(state.get('param.' + name) == expected, 'Unsupported public setting: ' + name)
            # Unused default stat filename presence is allowed; pointed bytes are never read.
            for name in ('rc.psz_stat_in', 'rc.psz_stat_out'):
                require(state.get('param.' + name) in ('0', '1'), 'Missing stat filename presence')
            if not call[0]:
                pts = next(inputs)
                require(all(state.get('pic.'+name) == '0' for name in ('param',
                    'prop.quant_offsets', 'prop.quant_offsets_free', 'prop.mb_info', 'prop.mb_info_free',
                    'extra_sei.num_payloads', 'extra_sei.payloads', 'extra_sei.sei_free')), 'Unsupported picture optional payload')
                by_pts[pts] = state
        require(next(inputs, None) is None, 'Public input/call count')
    require(len(by_pts) == count*24, 'Public nonflush state coverage')
    return by_pts


def public_phase_order(segment):
    """One-thread source path returns the consumed frame in the same native call."""
    inputs = iter(segment['inputs'])
    result = []
    for flush, _, count, pts, _, _ in segment['calls']:
        if not flush:
            submitted = next(inputs)
            result.extend((stage, submitted) for stage in ('entry', 'coded', 'post_aq'))
        if count:
            consumed = integer(pts)
            result.extend((stage, consumed) for stage in ('qpm', 'pre_slice'))
    require(next(inputs, None) is None, 'Public phase input coverage')
    require([pts for stage, pts in result if stage == 'qpm'] == segment['outputs'],
            'Public consumed output order disagreement')
    return result


def read_internal(folder: Path, boundary: dict, lifetimes: int) -> dict:
    require(type(lifetimes) is int and lifetimes in (2, 45), 'Expected lifetimes must be 2 or 45')
    folder = Path(folder)
    require(folder.is_dir() and not folder.is_symlink(), 'Invalid internal evidence directory')
    for name in ('internal.tsv', 'pixels.raw', 'state.raw'):
        path = folder / name
        require(not path.is_symlink() and path.exists() and stat.S_ISREG(path.stat().st_mode), 'Missing/nonregular internal evidence')
    public = _public(boundary, lifetimes)
    require((folder / 'internal.tsv').stat().st_size <= 16 * 1024 * 1024, 'Oversized internal TSV')
    with (folder / 'internal.tsv').open('rb') as stream:
        trace = stream.read(16 * 1024 * 1024 + 1)
    require(len(trace) <= 16 * 1024 * 1024 and trace.endswith(b'\n'), 'Truncated/oversized internal TSV')
    rows = trace.decode('ascii').splitlines()
    require(rows and rows[0] in (f'schema\tffh-v1\tle\t{lifetimes}', f'schema\tffh-v1\tbe\t{lifetimes}'), 'Unsupported internal schema')
    endian = 'little' if rows[0].split('\t')[2] == 'le' else 'big'
    frames, lives = {}, []
    active = None
    closed, offsets = 0, {'pixels': 0, 'state': 0}
    terminal = False
    with (folder / 'pixels.raw').open('rb') as pixels, (folder / 'state.raw').open('rb') as state_raw:
        streams = {'pixels': pixels, 'state': state_raw}
        for row in rows[1:]:
            require(not terminal, 'Data after terminal')
            f = row.split('\t')
            kind = f[0]
            if kind == 'open':
                require(active is None and closed < lifetimes and len(f) > 2, 'Overlapping/excess open')
                ident = integer(f[1], 1, lifetimes)
                require(ident == closed + 1, 'Nonmonotonic successful lifetime ID')
                config = named(f[2:])
                require(set(config) == set(OPEN_FIXED) | {'cpu', 'stat_in_present', 'stat_out_present'}, 'Open configuration fields')
                require(all(config[n] == v for n, v in OPEN_FIXED.items()), 'Unsupported native configuration')
                integer(config['cpu'], 0, 2**32-1)
                require(config['stat_in_present'] in ('0', '1') and config['stat_out_present'] in ('0', '1'), 'Stat presence')
                segment = boundary['segments'][closed]
                for s in segment['states']:
                    require(all(config[n] == s.get('param.'+p) for n, p in OPEN_PUBLIC.items()), 'Internal/public named configuration mismatch')
                active = {'id': ident, 'configuration': config, 'seen': {s: set() for s in STAGES},
                          'phase_order': public_phase_order(segment), 'phase_index': 0,
                          'next': 0, 'pending': None, 'qpm_pending': None, 'closing': False}
                continue
            if kind == 'terminal':
                require(active is None and closed == lifetimes and len(f) == 5, 'Premature/malformed terminal')
                require([integer(v) for v in f[1:]] == [lifetimes, lifetimes*24, offsets['pixels'], offsets['state']], 'Incorrect terminal totals')
                terminal = True
                continue
            require(active is not None and len(f) >= 2 and integer(f[1], 1, lifetimes) == active['id'], 'Event outside matching lifetime')
            if kind == 'closing':
                require(not active['closing'] and len(f) == 7 and f[2:] == ['24']*5, 'Malformed closing totals')
                require(active['pending'] is None and active['qpm_pending'] is None and active['next'] == 24
                    and active['phase_index'] == len(active['phase_order'])
                    and all(v == set(range(24)) for v in active['seen'].values()), 'Incomplete lifetime at closing')
                active['closing'] = True
                continue
            if kind == 'closed':
                require(active['closing'] and len(f) == 2, 'Closed without native close begin')
                lives.append({'id': active['id'], 'configuration': active['configuration'],
                              'coverage': {s: sorted(v) for s, v in active['seen'].items()}, 'closed': True})
                closed += 1
                active = None
                continue
            require(kind in STAGES and not active['closing'] and len(f) >= 4, 'Unknown or closing-phase event')
            local, pts = integer(f[2], 0, 23), integer(f[3], 0, lifetimes*24-1)
            require(pts == (active['id']-1)*24+local and local not in active['seen'][kind], 'Invalid/duplicate phase PTS')
            phase_index = active['phase_index']
            require(phase_index < len(active['phase_order'])
                    and active['phase_order'][phase_index] == (kind, pts),
                    'Internal phase/public input-flush-output call order mismatch')
            active['phase_index'] += 1
            if kind == 'entry':
                require(active['pending'] is None and active['qpm_pending'] is None and local == active['next'], 'Entry submission order')
                active['pending'] = ('entry', local)
            elif kind == 'coded':
                require(active['pending'] == ('entry', local), 'Coded without pending entry')
                active['pending'] = ('coded', local)
            elif kind == 'post_aq':
                require(active['pending'] == ('coded', local), 'Post-AQ without coded input')
                active['pending'] = None
                active['next'] += 1
            elif kind == 'qpm':
                require(active['pending'] is None and active['qpm_pending'] is None and local in active['seen']['post_aq'], 'Qpm before AQ or outstanding pair')
                active['qpm_pending'] = local
            else:
                require(active['pending'] is None and active['qpm_pending'] == local, 'Pre-slice without matching actual qpm')
                active['qpm_pending'] = None
            values = named(f[4:])
            require(set(values) == set(META[kind]) | set(RECTS[kind]), 'Missing/unknown phase fields')
            metadata = {n: values[n] for n in META[kind]}
            for n, value in metadata.items():
                if n == 'bits':
                    require(re.fullmatch('[0-9a-f]{8}', value), 'Invalid raw qpm bits')
                else:
                    integer(value, -2**31, 2**31-1)
            if kind in ('qpm', 'pre_slice'):
                require(metadata['ref'] in ('0', '1') and 1 <= int(metadata['type']) <= 6, 'Invalid actual frame type/ref')
            if kind == 'qpm':
                require(metadata['b_vbv'] == '0', 'Actual private RC VBV enabled')
            if kind == 'pre_slice':
                # r3108 QP_MAX=69 at8bit; SPEC_QP clamps only the signaled slice.
                require(metadata['slice_type'] in ('0', '1', '2')
                        and 0 <= int(metadata['slice_init_qp']) <= 51
                        and 0 <= int(metadata['rc_global_qp']) <= 69, 'Invalid slice initialization')
                require(all(metadata[n] == frames[pts]['qpm']['metadata'][n] for n in ('type', 'ref')), 'Qpm/consumed type/ref mismatch')
            stream_name = 'pixels' if kind in ('entry', 'coded') else 'state'
            descriptors = {}
            for name, expected in RECTS[kind].items():
                descriptor, small = read_extent(streams[stream_name], values[name], expected, offsets[stream_name])
                offsets[stream_name] += descriptor['size']
                descriptors[name] = descriptor
                if kind == 'qpm':
                    require(f'{int.from_bytes(small, endian):08x}' == metadata['bits'], 'Qpm raw bits/descriptor mismatch')
                    bits = int(metadata['bits'], 16)
                    metadata['classification'] = ('nan' if bits & 0x7fffff else 'infinity') if bits & 0x7f800000 == 0x7f800000 else 'finite'
            if kind == 'entry':
                s = public[pts]
                require(tuple(descriptors[n]['sha256'] for n in ('Y', 'U', 'V')) == boundary['frames'][pts], 'Public visible hash mismatch')
                require(all(str(descriptors[n]['stride']) == s.get(f'pic.img.i_stride[{i}]') for i, n in enumerate(('Y', 'U', 'V'))), 'Public input stride mismatch')
                require(all(metadata[n] == s.get('pic.'+p) for n, p in (('pic_type', 'i_type'), ('qpplus1', 'i_qpplus1'), ('pic_struct', 'i_pic_struct'))), 'Public picture scalar mismatch')
            frames.setdefault(pts, {})[kind] = {'lifetime': active['id'], 'local_frame': local,
                'pts': pts, 'metadata': metadata, 'raw': descriptors}
            active['seen'][kind].add(local)
        require(terminal and active is None and closed == lifetimes, 'Missing terminal/completed close')
        for name, stream in streams.items():
            stream.seek(0, 2)
            require(stream.tell() == offsets[name], 'Unaccounted/truncated raw bytes')
        hashes = {name + '_sha256': file_hash(stream) for name, stream in streams.items()}
    require(set(frames) == set(range(lifetimes*24)) and all(set(v) == set(STAGES) for v in frames.values()), 'Incomplete phase coverage')
    return {'schema': 'ffh-v1', 'endianness': endian, 'frames': frames, 'lifetimes': lives,
            'coverage': {s: lifetimes*24 for s in STAGES}, 'raw_sizes': offsets,
            'raw_hashes': hashes, 'trace_sha256': hashlib.sha256(trace).hexdigest()}


def compare_internal(left: dict, right: dict) -> dict:
    """Exact typed-bit signatures, never decimal/tolerance or stream-offset equality."""
    require(left['schema'] == right['schema'] == 'ffh-v1' and left['endianness'] == right['endianness'], 'Incompatible internal schemas')
    require(set(left['frames']) == set(right['frames']), 'Incompatible PTS coverage')
    def signature(frame, stage):
        value = frame[stage]
        return (value['lifetime'], value['local_frame'], value['pts'], value['metadata'],
                {n: {k: v for k, v in d.items() if k != 'offset'} for n, d in value['raw'].items()})
    return {s + '_changed_pts': [p for p in sorted(left['frames'])
            if signature(left['frames'][p], s) != signature(right['frames'][p], s)] for s in STAGES}
