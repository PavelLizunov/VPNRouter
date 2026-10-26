"""Strict saved-byte binder for the original silent film and synthetic lifetimes.

No subprocesses, decoder, report writer or localization lives here. The driver owns
capture/retention and must preserve failing operands before calling these functions.
The MP4 subset is intentionally nonfragmented, single-video avc1 with one sample
entry, unit-rate edit lists, and 4-byte AVCC. Unsupported syntax fails closed.
"""
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import re
import struct

from h264_helpers import parameter_sets, idr_header


class BindingError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise BindingError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def integer(value):
    require(type(value) in (int, str) and re.fullmatch(r'-?(0|[1-9][0-9]*)', str(value)),
            'Noncanonical integer')
    return int(value)


def rational(value):
    require(type(value) in (str, int), 'Timing must not use floating point')
    try:
        return Fraction(value)
    except (ValueError, ZeroDivisionError) as error:
        raise BindingError('Invalid rational timing') from error


def boxes(data, start=0, end=None):
    end = len(data) if end is None else end
    rows = []
    while start < end:
        require(start + 8 <= end, 'Truncated MP4 box')
        size, kind = struct.unpack_from('>I4s', data, start)
        header = 8
        if size == 1:
            require(start + 16 <= end, 'Truncated extended box')
            size = int.from_bytes(data[start + 8:start + 16], 'big')
            header = 16
        require(size >= header and start + size <= end, 'Invalid MP4 box extent')
        rows.append((kind, start + header, start + size))
        start += size
    require(start == end, 'MP4 box coverage')
    return rows


def one(rows, kind):
    matches = [row for row in rows if row[0] == kind]
    require(len(matches) == 1, 'Missing/ambiguous ' + repr(kind))
    return matches[0]


def body(data, row):
    return data[row[1]:row[2]]


def children(data, row):
    return boxes(data, row[1], row[2])


def full(data, versions=(0,)):
    require(len(data) >= 4 and data[0] in versions and data[1:4] == b'\0\0\0',
            'Unsupported full-box version/flags')
    return data[0]


def table(data, row, width, versions=(0,)):
    raw = body(data, row)
    version = full(raw, versions)
    require(len(raw) >= 8, 'Truncated sample table')
    count = int.from_bytes(raw[4:8], 'big')
    require(count <= 1080 and len(raw) == 8 + count * width * 4, 'Sample-table extent/count')
    return version, [struct.unpack_from('>' + 'I' * width, raw, 8 + i * width * 4)
                     for i in range(count)]


def clock(data, row):
    raw = body(data, row)
    version = full(raw, (0, 1))
    offset = 12 if version == 0 else 20
    require(len(raw) >= offset + (8 if version == 0 else 12), 'Truncated media clock')
    scale = int.from_bytes(raw[offset:offset + 4], 'big')
    duration = int.from_bytes(raw[offset + 4:offset + (8 if version == 0 else 12)], 'big')
    require(scale > 0, 'Zero timescale')
    return scale, duration


def avcc_sets(raw):
    require(len(raw) >= 11 and raw[0] == 1 and raw[4] == 255 and raw[5] == 225,
            'Unsupported/ambiguous avcC framing or SPS count')
    cursor, sets = 6, []
    for kind in (7, 8):
        if kind == 8:
            require(cursor < len(raw) and raw[cursor] == 1, 'Ambiguous PPS count')
            cursor += 1
        require(cursor + 2 <= len(raw), 'Truncated parameter length')
        size = int.from_bytes(raw[cursor:cursor + 2], 'big')
        cursor += 2
        require(size > 0 and cursor + size <= len(raw), 'Parameter extent')
        nal = raw[cursor:cursor + size]
        require(nal_type(nal) == kind, 'Wrong parameter NAL type')
        sets.append(nal)
        cursor += size
    # High-profile avcC extension: chroma=1, bit depths=8, no extended SPS.
    require(raw[cursor:] in (b'', b'\xfd\xf8\xf8\x00'), 'Unknown avcC extension')
    return sets


def sample_table(data):
    """Derive sample positions and rational DTS/PTS from actual MP4 tables/edit list."""
    top = boxes(data)
    require(not any(row[0] in (b'moof', b'sidx') for row in top), 'Fragmented MP4 unsupported')
    mdats = [(lo, hi) for kind, lo, hi in top if kind == b'mdat']
    require(mdats, 'Missing mdat')
    moov = children(data, one(top, b'moov'))
    require(not any(row[0] == b'mvex' for row in moov), 'Fragmented movie unsupported')
    movie_scale, _ = clock(data, one(moov, b'mvhd'))
    # Original film is silent. Reject other tracks rather than guess stream indices.
    trak = children(data, one(moov, b'trak'))
    mdia = children(data, one(trak, b'mdia'))
    handler = body(data, one(mdia, b'hdlr'))
    require(len(handler) >= 12 and handler[8:12] == b'vide', 'Not a unique video track')
    scale, media_duration = clock(data, one(mdia, b'mdhd'))
    minf = children(data, one(mdia, b'minf'))
    stbl = children(data, one(minf, b'stbl'))
    require(all(row[0] in (b'stsd', b'stts', b'stss', b'ctts', b'stsc', b'stsz', b'stco', b'co64')
                for row in stbl), 'Unsupported sample storage/modifier')
    require(len({row[0] for row in stbl}) == len(stbl), 'Duplicate sample table')
    stsd = body(data, one(stbl, b'stsd'))
    full(stsd)
    require(len(stsd) >= 8 and stsd[4:8] == b'\0\0\0\1', 'Multiple sample descriptions')
    entries = boxes(stsd, 8)
    require(len(entries) == 1, 'Ambiguous sample-entry extent')
    entry = one(entries, b'avc1')
    visual = body(stsd, entry)
    require(len(visual) >= 78 and visual[6:8] == b'\0\1'
            and struct.unpack_from('>HH', visual, 24) == (854, 480), 'Unsupported visual entry')
    visual_boxes = boxes(visual, 78)
    require(all(row[0] in (b'avcC', b'pasp', b'btrt', b'colr', b'fiel') for row in visual_boxes),
            'Unsupported visual sample-entry extension')
    require(len({row[0] for row in visual_boxes}) == len(visual_boxes), 'Duplicate visual extension')
    avcc_row = one(visual_boxes, b'avcC')
    avcc = body(visual, avcc_row)
    sets = avcc_sets(avcc)
    # Reuse the bounded saved SPS/PPS parser on this structurally selected box only.
    parameters = parameter_sets(struct.pack('>I4s', len(avcc) + 8, b'avcC') + avcc)
    raw = body(data, one(stbl, b'stsz'))
    full(raw)
    require(len(raw) >= 12, 'Truncated stsz')
    fixed, count = struct.unpack_from('>II', raw, 4)
    require(0 < count <= 1080 and len(raw) == 12 + (0 if fixed else count * 4), 'stsz extent')
    sizes = [fixed] * count if fixed else list(struct.unpack_from('>' + 'I' * count, raw, 12))
    require(all(sizes), 'Empty sample')
    chunk_rows = [row for row in stbl if row[0] in (b'stco', b'co64')]
    require(len(chunk_rows) == 1, 'Ambiguous chunk offset tables')
    raw = body(data, chunk_rows[0])
    full(raw)
    require(len(raw) >= 8, 'Truncated chunk table')
    chunks = int.from_bytes(raw[4:8], 'big')
    width = 8 if chunk_rows[0][0] == b'co64' else 4
    require(0 < chunks <= count and len(raw) == 8 + width * chunks, 'Chunk table extent')
    offsets = [int.from_bytes(raw[8 + i * width:8 + (i + 1) * width], 'big') for i in range(chunks)]
    _, mapping = table(data, one(stbl, b'stsc'), 3)
    require(mapping and mapping[0][0] == 1 and all(a[0] < b[0] for a, b in zip(mapping, mapping[1:])),
            'Invalid stsc order')
    require(all(1 <= first <= chunks and 0 < num <= count and desc == 1
                for first, num, desc in mapping), 'Unsupported chunk mapping')
    positions, index, entry_index = [], 0, 0
    for chunk, offset in enumerate(offsets, 1):
        while entry_index + 1 < len(mapping) and mapping[entry_index + 1][0] <= chunk:
            entry_index += 1
        num = mapping[entry_index][1]
        require(index + num <= count, 'Too many chunk samples')
        for size in sizes[index:index + num]:
            require(any(lo <= offset < offset + size <= hi for lo, hi in mdats), 'Sample outside mdat')
            positions.append(offset)
            offset += size
        index += num
    require(index == count, 'Missing chunk samples')
    extents = sorted(zip(positions, sizes))
    require(all(a + size <= b for (a, size), (b, _) in zip(extents, extents[1:])), 'Overlapping samples')
    _, runs = table(data, one(stbl, b'stts'), 2)
    require(all(num > 0 and delta > 0 for num, delta in runs) and sum(num for num, _ in runs) == count,
            'stts coverage')
    durations = [delta for num, delta in runs for _ in range(num)]
    require(sum(durations) == media_duration, 'mdhd/stts duration disagreement')
    ctts = [row for row in stbl if row[0] == b'ctts']
    composition = [0] * count
    if ctts:
        version, runs = table(data, one(stbl, b'ctts'), 2, (0, 1))
        require(all(num > 0 for num, _ in runs) and sum(num for num, _ in runs) == count, 'ctts coverage')
        composition = [offset - (2**32 if version == 1 and offset >= 2**31 else 0)
                       for num, offset in runs for _ in range(num)]
    shift, edit_duration = Fraction(0), None
    edits = [row for row in trak if row[0] == b'edts']
    if edits:
        edit = children(data, one(trak, b'edts'))
        require(len(edit) == 1, 'Unsupported edit structure')
        raw = body(data, one(edit, b'elst'))
        version = full(raw, (0, 1))
        width = 20 if version else 12
        require(len(raw) >= 8, 'Truncated edit list')
        num = int.from_bytes(raw[4:8], 'big')
        require(num in (1, 2) and len(raw) == 8 + num * width, 'Unsupported edit count/extent')
        parsed = [struct.unpack_from('>Qqhh' if version else '>Iihh', raw, 8 + i * width) for i in range(num)]
        require(all(rate == 1 and fraction == 0 for _, _, rate, fraction in parsed), 'Non-unit edit rate')
        empty = 0
        if num == 2:
            empty, media_time, _, _ = parsed[0]
            require(media_time == -1 and empty > 0, 'Unsupported initial edit')
        length, media_time, _, _ = parsed[-1]
        require(media_time >= 0 and length > 0, 'Unsupported active edit')
        shift = Fraction(empty, movie_scale) - Fraction(media_time, scale)
        edit_duration = Fraction(length, movie_scale)
    rows, dts = [], 0
    for pos, size, delta, offset in zip(positions, sizes, durations, composition):
        rows.append({'pos': pos, 'size': size, 'dts': Fraction(dts, scale) + shift,
                     'pts': Fraction(dts + offset, scale) + shift, 'duration': Fraction(delta, scale)})
        dts += delta
    require(all(row['duration'] == Fraction(1, 30) for row in rows), 'Non-30fps sample duration')
    require(edit_duration is None or edit_duration == Fraction(count, 30), 'Edit duration disagreement')
    sync = [row for row in stbl if row[0] == b'stss']
    if sync:
        _, entries = table(data, one(stbl, b'stss'), 1)
        sync_samples = [entry[0] for entry in entries]
        require(sync_samples == sorted(set(sync_samples))
                and all(1 <= entry <= count for entry in sync_samples), 'Invalid sync sample coverage')
    else:
        sync_samples = list(range(1, count + 1))
    return {'samples': rows, 'time_base': Fraction(1, scale), 'avcc': avcc, 'sets': sets,
            'parameters': parameters, 'edit_shift': str(shift), 'sync_samples': sync_samples}


def nal_type(nal):
    require(bool(nal) and nal[0] & 128 == 0 and nal[0] & 31 in (1, 5, 6, 7, 8),
            'Unknown/forbidden NAL type')
    kind, ref = nal[0] & 31, (nal[0] >> 5) & 3
    require((kind != 6 or ref == 0) and (kind not in (5, 7, 8) or ref != 0),
            'Invalid NAL reference metadata')
    require(b'\0\0\1' not in nal, 'Ambiguous unescaped start code inside NAL')
    return kind


def split_avcc(raw):
    nals, cursor = [], 0
    while cursor < len(raw):
        require(cursor + 4 <= len(raw), 'Truncated AVCC length')
        size = int.from_bytes(raw[cursor:cursor + 4], 'big')
        cursor += 4
        require(size > 0 and cursor + size <= len(raw), 'Invalid AVCC NAL extent')
        nal = raw[cursor:cursor + size]
        nal_type(nal)
        nals.append(nal)
        cursor += size
    require(bool(nals), 'Empty AVCC packet')
    return nals


def native_tuple(value):
    require(len(value) == 4, 'Invalid native tuple')
    result, count, digest, sizes = value
    require(type(result) is int and type(count) is int and 0 < count <= 4096
            and len(sizes) == count and all(type(size) is int and size > 0 for size in sizes)
            and sum(sizes) == result and re.fullmatch('[0-9a-f]{64}', digest), 'Invalid native inventory')
    return result, count, digest, tuple(sizes)


def reconstruct(nals, recorded, framing):
    value = native_tuple(recorded)
    require(len(nals) == value[1], 'Native NAL count/order disagreement')
    pieces, prefixes = [], []
    for nal, size in zip(nals, value[3]):
        nal_type(nal)
        prefix = size - len(nal)
        if framing == 'annexb':
            require(prefix in (3, 4), 'Unknown Annex-B prefix length')
            pieces.append(b'\0' * (prefix - 1) + b'\1' + nal)
        else:
            require(framing == 'avcc' and prefix == 4, 'Unknown AVCC native framing')
            pieces.append(len(nal).to_bytes(4, 'big') + nal)
        prefixes.append(prefix)
    raw = b''.join(pieces)
    require(len(raw) == value[0] and sha(raw) == value[2], 'Native payload/header hash disagreement')
    return value, prefixes


def native_inventory(item, pts_base):
    expected = list(range(pts_base, pts_base + 24))
    require(item['inputs'] == expected and sorted(item['outputs']) == expected, '24-frame native coverage')
    require(len(item['headers']) == 1, 'Exactly one complete native header required')
    modes = {state['param.b_annexb'] for state in item['states']}
    require(len(modes) == 1 and modes <= {'0', '1'}, 'Unknown native framing metadata')
    framing = 'annexb' if modes == {'1'} else 'avcc'
    # Public parser owns lifecycle/state validation; additionally prove physical
    # header placement before the first returned nonempty bytes for raw binding.
    headers = [i for i, event in enumerate(item['events']) if event[0] == 'headers']
    nonempty = [i for i, event in enumerate(item['events'])
                if event[0] == 'output' and integer(event[4]) > 0]
    require(len(headers) == 1 and nonempty and headers[0] < nonempty[0], 'Native header/output byte order')
    outputs, order = {}, []
    for flush, result, count, pts, digest, sizes in item['calls']:
        require(flush in (0, 1), 'Unknown flush flag')
        if count:
            index = integer(pts)
            require(index in expected and index not in outputs, 'Missing/duplicate native output PTS')
            outputs[index] = native_tuple((result, count, digest, sizes))
            order.append(index)
        else:
            require((result, pts, digest, tuple(sizes)) == (0, '-', sha(b''), ()), 'Invalid empty output')
    require(set(outputs) == set(expected) and order == item['outputs'], 'Incomplete native call order')
    return outputs, order, framing


def read_decoded(decoded: Path, redecoded: Path, frames=24):
    """Validate saved same-file SHA256 framehash observations (not raw pixel proof)."""
    raw = Path(decoded).read_bytes()
    require(raw == Path(redecoded).read_bytes(), 'Same-file decode/redecode disagreement')
    text = raw.decode('utf-8')
    lines = text.splitlines()
    for required in ('#format: frame checksums', '#version: 2', '#hash: SHA256',
                     '#tb 0: 1/30', '#media_type 0: video', '#codec_id 0: rawvideo',
                     '#dimensions 0: 854x480'):
        require(lines.count(required) == 1, 'Missing/duplicate decode metadata: ' + required)
    allowed = ('#format:', '#version:', '#hash:', '#software:', '#tb 0:', '#media_type 0:',
               '#codec_id 0:', '#dimensions 0:', '#sar 0:', '#stream#,')
    require(all(not line.startswith('#') or line.startswith(allowed) for line in lines), 'Unknown decode stream/metadata')
    metadata = [line.split(':', 1)[0] for line in lines if line.startswith('#') and ':' in line]
    require(len(metadata) == len(set(metadata)), 'Duplicate decode metadata')
    rows = [line for line in lines if line and not line.startswith('#')]
    require(len(rows) == frames, 'Incomplete decoded frame coverage')
    result = {}
    for i, line in enumerate(rows):
        fields = [field.strip() for field in line.split(',')]
        require(len(fields) == 6 and [integer(value) for value in fields[:5]] == [0, i, i, 1, 614880]
                and re.fullmatch('[0-9a-f]{64}', fields[5]), 'Invalid decoded frame extent/order/PTS/hash')
        result[i] = fields[5]
    return result


def probe_packets(media, probe, frames):
    data = Path(media).read_bytes()
    parsed = sample_table(data)
    report = json.loads(Path(probe).read_text())
    streams = report['streams']
    require(len(streams) == 1, 'Expected one silent video stream')
    stream = streams[0]
    require(integer(stream['index']) == 0, 'Single-track probe stream index disagreement')
    require((stream['codec_type'], stream['codec_name'], stream['pix_fmt'], stream['width'], stream['height'])
            == ('video', 'h264', 'yuv420p', 854, 480), 'Unsupported probe stream')
    tb = rational(stream['time_base'])
    require(tb == parsed['time_base'] and rational(stream['r_frame_rate']) == 30
            and rational(stream['avg_frame_rate']) == 30, 'Probe/table framerate/timebase disagreement')
    require(integer(stream['nb_frames']) == frames and integer(stream['nb_read_packets']) == frames
            and rational(stream['duration_ts']) * tb == Fraction(frames, 30)
            and rational(stream['duration']) == Fraction(frames, 30), 'Probe duration/count disagreement')
    require(stream['is_avc'] == 'true' and integer(stream['nal_length_size']) == 4
            and integer(stream['extradata_size']) == len(parsed['avcc'])
            and stream['extradata_hash'] == 'SHA256:' + sha(parsed['avcc']), 'Probe avcC disagreement')
    samples, packets = parsed['samples'], report['packets']
    require(len(samples) == len(packets) == frames, 'Incomplete packet/sample coverage')
    require(rational(stream['start_pts']) * tb == min(row['pts'] for row in samples), 'Probe start disagreement')
    rows, coverage = [], set()
    for index, (packet, sample) in enumerate(zip(packets, samples)):
        require(packet['codec_type'] == 'video' and packet['stream_index'] == stream['index'], 'Packet stream disagreement')
        require(integer(packet['pos']) == sample['pos'] and integer(packet['size']) == sample['size'], 'Packet/sample extent disagreement')
        for key in ('pts', 'dts', 'duration'):
            require(rational(packet[key]) * tb == sample[key], 'Packet/sample rational timing disagreement')
        pts = sample['pts'] * 30
        require(pts.denominator == 1 and pts not in coverage, 'Fractional/duplicate packet PTS')
        coverage.add(pts)
        raw = data[sample['pos']:sample['pos'] + sample['size']]
        require(packet['data_hash'] == 'SHA256:' + sha(raw), 'Packet/media byte hash disagreement')
        nals = split_avcc(raw)
        slices = [nal for nal in nals if nal_type(nal) in (1, 5)]
        require(slices and all(nal_type(nal) in (1, 5, 6) for nal in nals), 'Unsupported packet NAL order/type')
        require(all(nal_type(nal) == nal_type(slices[0]) for nal in slices), 'Mixed slice types')
        require(('K' in packet['flags']) == (nal_type(slices[0]) == 5)
                == (index + 1 in parsed['sync_samples']), 'Keyframe/sample-table/slice disagreement')
        rows.append({'pts': int(pts), 'dts': str(sample['dts']), 'duration': str(sample['duration']),
                     'pos': sample['pos'], 'size': sample['size'], 'packet_sha256': sha(raw),
                     'order': index, 'nal_types': [nal_type(nal) for nal in nals],
                     'nal_sha256': [sha(nal) for nal in nals], '_nals': nals})
    return data, parsed, rows


def bind_packet(row, header, recorded, framing, first):
    nals = row['_nals']
    if first:
        require([nal_type(nal) for nal in nals][:2] == [6, 5]
                and sum(nal_type(nal) == 6 for nal in nals) == 1, 'Missing/ambiguous first-packet header SEI')
        payload = nals[1:]
    else:
        require(all(nal_type(nal) in (1, 5) for nal in nals), 'Unexpected non-header SEI')
        payload = nals
    value, prefixes = reconstruct(payload, recorded, framing)
    return {key: value for key, value in row.items() if key != '_nals'} | {
        'native': value, 'native_prefix_lengths': prefixes, 'payload_sha256': value[2],
        'header_sha256': header[2], 'payload_nal_sha256': [sha(nal) for nal in payload]}


def verify_segment(media: Path, probe: Path, decoded: Path, redecoded: Path,
                   native_segment: dict, *, pts_base: int) -> dict:
    outputs, order, framing = native_inventory(native_segment, pts_base)
    data, parsed, rows = probe_packets(media, probe, 24)
    expected = set(range(pts_base, pts_base + 24))
    require({row['pts'] for row in rows} == expected and [row['pts'] for row in rows] == order,
            'Segment/global native PTS or output order disagreement')
    first = rows[0]['_nals']
    require(rows[0]['pts'] == pts_base and [nal_type(nal) for nal in first][:2] == [6, 5], 'Segment must begin with SEI/IDR')
    header_nals = parsed['sets'] + [first[0]]
    header, prefixes = reconstruct(header_nals, native_segment['headers'][0], framing)
    packets = {row['pts']: bind_packet(row, header, outputs[row['pts']], framing, i == 0)
               for i, row in enumerate(rows)}
    decoded_rows = read_decoded(decoded, redecoded)
    return {'packets': packets, 'decoded': {pts_base + i: value for i, value in decoded_rows.items()},
            'header': header, 'header_prefix_lengths': prefixes,
            'header_nal_sha256': [sha(nal) for nal in header_nals],
            'header_nals_hex': [nal.hex() for nal in header_nals],
            'avcc_sha256': sha(parsed['avcc']), 'media_sha256': sha(data), 'pts_base': pts_base,
            'packet_order': order, 'framing': framing, 'time_base': str(parsed['time_base']),
            'edit_shift': parsed['edit_shift'], 'first_idr': idr_header(first[1], parsed['parameters'])[0],
            'coverage': {'inputs': 24, 'native_outputs': 24, 'packets': 24, 'decoded': 24,
                         'header_nals': 3, 'complete': True},
            'decode_evidence': 'saved framehash observations; raw pixels not independently rehashed'}


def verify_concat(media, probe, decoded, redecoded, segments: list[dict]) -> dict:
    require(len(segments) == 45, 'Concat requires exactly 45 segments')
    expected = set(range(1080))
    for index, segment in enumerate(segments):
        coverage = set(range(24 * index, 24 * index + 24))
        require(segment['pts_base'] == index * 24 and set(segment['packets']) == coverage
                and set(segment['decoded']) == coverage and segment['coverage']['complete'],
                'Missing/duplicate/out-of-order segment coverage')
    data, parsed, rows = probe_packets(media, probe, 1080)
    require({row['pts'] for row in rows} == expected, 'Concat PTS coverage')
    require(sha(parsed['avcc']) == segments[0]['avcc_sha256'], 'Concat first avcC disagreement')
    packets, bindings = {}, []
    offset, next_start = 0, Fraction(0)
    for index, segment in enumerate(segments):
        group = rows[offset:offset + 24]
        header_nals = [bytes.fromhex(value) for value in segment['header_nals_hex']]
        reconstruct(header_nals, segment['header'], segment['framing'])
        require([sha(nal) for nal in header_nals] == segment['header_nal_sha256']
                and [nal_type(nal) for nal in header_nals] == [7, 8, 6]
                and header_nals[:2] == parsed['sets']
                and group[0]['_nals'][0] == header_nals[2], 'Concat own-segment header disagreement')
        require([row['pts'] for row in group] == segment['packet_order'], 'Concat packet rebase/order disagreement')
        source_start = min(Fraction(pts, 30) for pts in segment['packets'])
        shift = next_start - source_start
        require(next_start == Fraction(index * 24, 30), 'Concat source-model continuation gap')
        for local, row in enumerate(group):
            pts = row['pts']
            original = segment['packets'][pts]
            require(row['nal_sha256'] == original['nal_sha256'] and row['nal_types'] == original['nal_types']
                    and row['packet_sha256'] == original['packet_sha256'], 'Concat transformed packet/header bytes')
            require(rational(row['dts']) == rational(original['dts']) + shift
                    and row['duration'] == original['duration'], 'Concat source-model DTS rebasing disagreement')
            bound = bind_packet(row, segment['header'], original['native'], segment['framing'], local == 0)
            require(bound['payload_nal_sha256'] == original['payload_nal_sha256'], 'Concat native join disagreement')
            packets[pts] = bound
            bindings.append({'segment': index, 'global_pts': pts, 'source_pos': original['pos'],
                             'concat_pos': row['pos'], 'source_media_sha256': segment['media_sha256'],
                             'segment_header_sha256': segment['header'][2], 'rebase_seconds': str(shift)})
        next_start = max(Fraction(pts, 30) + rational(packet['duration']) + shift
                         for pts, packet in segment['packets'].items())
        offset += 24
    require(next_start == 36, 'Concat source-model total duration')
    decoded_rows = read_decoded(decoded, redecoded, frames=1080)
    require(all(decoded_rows[pts] == segments[pts // 24]['decoded'][pts] for pts in expected),
            'Segment/concat decoded frame disagreement')
    return {'packets': packets, 'decoded': decoded_rows, 'media_sha256': sha(data),
            'bindings': bindings, 'first_avcc_sha256': sha(parsed['avcc']),
            'segment_headers': [segment['header'] for segment in segments],
            'coverage': {'segments': 45, 'packets': 1080, 'decoded': 1080, 'complete': True},
            'timing_model': 'source start_time=min(sample PTS); next_video_start=max(PTS+duration); exact rational shift',
            'decode_evidence': 'saved framehash observations; raw pixels not independently rehashed'}


def verify_synthetic(media: Path, native_segment: dict, *, pts_base: int) -> dict:
    outputs, order, framing = native_inventory(native_segment, pts_base)
    require(framing == 'annexb', 'Synthetic raw .h264 requires observed Annex-B metadata')
    data, cursor = Path(media).read_bytes(), 0

    def take(recorded, types):
        nonlocal cursor
        value = native_tuple(recorded)
        start, nals, prefixes = cursor, [], []
        for size in value[3]:
            raw = data[cursor:cursor + size]
            require(len(raw) == size, 'Truncated synthetic native extent')
            if raw.startswith(b'\0\0\0\1'):
                prefix = 4
            elif raw.startswith(b'\0\0\1'):
                prefix = 3
            else:
                raise BindingError('Unknown synthetic Annex-B prefix')
            nal = raw[prefix:]
            require(nal_type(nal) in types and b'\0\0\1' not in nal, 'Unknown/ambiguous synthetic NAL framing')
            nals.append(nal)
            prefixes.append(prefix)
            cursor += size
        reconstruct(nals, value, framing)
        return value, nals, prefixes, start

    header, header_nals, prefixes, _ = take(native_segment['headers'][0], (6, 7, 8))
    require([nal_type(nal) for nal in header_nals] == [7, 8, 6], 'Synthetic complete header order')
    sps, pps, _ = header_nals
    avcc = b'\1' + sps[1:4] + b'\xff\xe1' + len(sps).to_bytes(2, 'big') + sps + b'\1' + len(pps).to_bytes(2, 'big') + pps
    parameters = parameter_sets(struct.pack('>I4s', len(avcc) + 8, b'avcC') + avcc)
    packets = {}
    first_idr = None
    for index, pts in enumerate(order):
        value, nals, native_prefixes, start = take(outputs[pts], (1, 5))
        if index == 0:
            require(all(nal_type(nal) == 5 for nal in nals), 'Synthetic first output not IDR')
            first_idr = idr_header(nals[0], parameters)[0]
        packets[pts] = {'native': value, 'pos': start, 'size': value[0], 'order': index,
                        'payload_sha256': value[2], 'header_sha256': header[2],
                        'native_prefix_lengths': native_prefixes, 'nal_types': [nal_type(nal) for nal in nals],
                        'nal_sha256': [sha(nal) for nal in nals]}
    require(cursor == len(data), 'Trailing/unbound synthetic bytes')
    return {'packets': packets, 'header': header, 'header_prefix_lengths': prefixes,
            'header_nal_sha256': [sha(nal) for nal in header_nals], 'pts_base': pts_base,
            'packet_order': order, 'media_sha256': sha(data), 'first_idr': first_idr,
            'coverage': {'inputs': 24, 'native_outputs': 24, 'header_nals': 3, 'complete': True},
            'timing_model': 'raw Annex-B has no timestamps; public nonempty call order only'}
