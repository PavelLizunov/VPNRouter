"""Stdlib saved-byte tests. Run with PYTHONDONTWRITEBYTECODE=1 python3 -B ...

Actual retained media are never executed or decoded. Handcrafted MP4s exercise
all45 table joins; they are test fixtures, not evidence of a new film execution.
"""
import copy
import importlib.util
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import media
import h264_helpers

ROOT = Path(__file__).resolve().parents[2]
ARTIFACT = Path(os.environ.get('FFH_TEST_MEDIA', str(ROOT / 'tools/help-videos/fframes-vpnrouter/out/native-importer-20261026/run-40359957912/artifact')))


def load_old():
    spec = importlib.util.spec_from_file_location('saved_importer_analyze', ROOT / '.github/importer-observation/analyze.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def shifted(item, base):
    result = copy.deepcopy(item)
    delta = base - result['inputs'][0]
    for key in ('inputs', 'outputs'):
        result[key] = [pts + delta for pts in result[key]]
    result['calls'] = [(f, r, c, str(int(p) + delta) if p != '-' else p, h, s)
                       for f, r, c, p, h, s in result['calls']]
    return result


def box(kind, data):
    return struct.pack('>I4s', len(data) + 8, kind) + data


def full(data):
    return b'\0' * 4 + data


def table(kind, rows):
    return box(kind, full(len(rows).to_bytes(4, 'big') + b''.join(struct.pack('>' + 'I' * len(row), *row) for row in rows)))


def make_mp4(raw_samples, offsets, avcc, base=0, *, co64=False, chunk_split=None, ctts_v1=False, edits_v1=False):
    """Small original-subset table fixture built from retained real NAL bodies."""
    count = len(raw_samples)
    ftyp = box(b'ftyp', b'isom\0\0\0\0isomavc1')
    mdat = box(b'mdat', b''.join(raw_samples))
    first = len(ftyp) + 8
    chunks = [raw_samples] if chunk_split is None else [raw_samples[:chunk_split], raw_samples[chunk_split:]]
    positions, cursor = [], first
    for chunk in chunks:
        positions.append(cursor)
        cursor += sum(map(len, chunk))
    visual = bytearray(78)
    visual[6:8] = b'\0\1'
    visual[24:28] = struct.pack('>HH', 854, 480)
    stsd = box(b'stsd', full(b'\0\0\0\1' + box(b'avc1', bytes(visual) + box(b'avcC', avcc))))
    stsz = box(b'stsz', full(struct.pack('>II', 0, count) + b''.join(len(raw).to_bytes(4, 'big') for raw in raw_samples)))
    stsc = table(b'stsc', [(i + 1, len(chunk), 1) for i, chunk in enumerate(chunks)])
    chunk_table = box(b'co64' if co64 else b'stco', full(len(positions).to_bytes(4, 'big') +
                      b''.join(pos.to_bytes(8 if co64 else 4, 'big') for pos in positions)))
    ctts = table(b'ctts', [(1, offset) for offset in offsets])
    if ctts_v1:
        ctts = ctts[:8] + b'\1' + ctts[9:]
    sync = [(i + 1,) for i, raw in enumerate(raw_samples)
            if any(media.nal_type(nal) == 5 for nal in media.split_avcc(raw))]
    stbl = box(b'stbl', stsd + table(b'stts', [(count, 512)]) + table(b'stss', sync) + ctts + stsc + stsz + chunk_table)
    mdhd = box(b'mdhd', full(struct.pack('>IIII', 0, 0, 15360, count * 512) + b'\0' * 4))
    mdia = box(b'mdia', mdhd + box(b'hdlr', full(b'\0' * 4 + b'vide' + b'\0' * 12)) + box(b'minf', stbl))
    edits = []
    if base:
        edits.append((base * 1000 // 30, -1, 1, 0))
    edits.append((count * 1000 // 30, 1024, 1, 0))
    edit_raw = bytes([1 if edits_v1 else 0, 0, 0, 0]) + len(edits).to_bytes(4, 'big')
    edit_raw += b''.join(struct.pack('>Qqhh' if edits_v1 else '>Iihh', *row) for row in edits)
    trak = box(b'trak', box(b'edts', box(b'elst', edit_raw)) + mdia)
    mvhd = box(b'mvhd', full(struct.pack('>IIII', 0, 0, 1000, (base + count) * 1000 // 30) + b'\0' * 4))
    return ftyp + mdat + box(b'moov', mvhd + trak)


def probe_for(data):
    parsed = media.sample_table(data)
    samples = parsed['samples']
    packets = []
    tb = parsed['time_base']
    for row in samples:
        raw = data[row['pos']:row['pos'] + row['size']]
        packet = {'codec_type': 'video', 'stream_index': 0, 'pos': str(row['pos']),
                  'size': str(row['size']), 'data_hash': 'SHA256:' + media.sha(raw),
                  'flags': 'K__' if any(media.nal_type(nal) == 5 for nal in media.split_avcc(raw)) else '___'}
        packet.update({key: int(row[key] / tb) for key in ('pts', 'dts', 'duration')})
        packets.append(packet)
    stream = {'codec_type': 'video', 'codec_name': 'h264', 'pix_fmt': 'yuv420p', 'width': 854,
              'height': 480, 'time_base': str(tb), 'r_frame_rate': '30/1', 'avg_frame_rate': '30/1',
              'nb_frames': str(len(samples)), 'nb_read_packets': str(len(samples)),
              'duration_ts': len(samples) * 512, 'duration': str(len(samples) / 30),
              'start_pts': int(min(row['pts'] for row in samples) / tb), 'index': 0,
              'is_avc': 'true', 'nal_length_size': '4', 'extradata_size': len(parsed['avcc']),
              'extradata_hash': 'SHA256:' + media.sha(parsed['avcc'])}
    return {'streams': [stream], 'packets': packets}


def decode_text(hashes):
    header = ('#format: frame checksums\n#version: 2\n#hash: SHA256\n#software: test fixture\n'
              '#tb 0: 1/30\n#media_type 0: video\n#codec_id 0: rawvideo\n#dimensions 0: 854x480\n'
              '#sar 0: 0/1\n#stream#, dts, pts, duration, size, hash\n')
    return header + ''.join(f'0, {i}, {i}, 1, 614880, {digest}\n' for i, digest in enumerate(hashes))


class MediaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not ARTIFACT.is_dir():
            raise RuntimeError('Required saved artifact is absent; no media execution fallback')
        cls.old = load_old()
        cls.saved = ARTIFACT / 'film-plain'
        cls.old_item = cls.old.read_segment(next((cls.saved / 'public').glob('*.tsv')))['item']
        cls.original = (cls.saved / 'segment/0000000768.mp4').read_bytes()
        cls.parsed = media.sample_table(cls.original)
        cls.raw_samples = [cls.original[row['pos']:row['pos'] + row['size']] for row in cls.parsed['samples']]
        cls.offsets = [int((row['pts'] - row['dts']) * 15360) for row in cls.parsed['samples']]
        cls.avcc = cls.parsed['avcc']
        cls.hashes = list(media.read_decoded(cls.saved / 'decoded.sha256', cls.saved / 'redecoded.sha256').values())

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)

    def files(self, data, hashes=None, probe=None, stem='fixture'):
        media_path = self.folder / (stem + '.mp4')
        media_path.write_bytes(data)
        probe_path = self.folder / (stem + '.json')
        probe_path.write_text(json.dumps(probe if probe is not None else probe_for(data)))
        decoded = self.folder / (stem + '.decoded')
        redecoded = self.folder / (stem + '.redecoded')
        decoded.write_text(decode_text(self.hashes if hashes is None else hashes))
        redecoded.write_bytes(decoded.read_bytes())
        return media_path, probe_path, decoded, redecoded

    def fixture(self, base=0, **kwargs):
        return make_mp4(self.raw_samples, self.offsets, self.avcc, base, **kwargs)

    def segment(self, base=0, **kwargs):
        return media.verify_segment(*self.files(self.fixture(base, **kwargs), stem=f'segment{base}'),
                                    shifted(self.old_item, base), pts_base=base)

    def concat_fixture(self):
        segments = [self.segment(i * 24) for i in range(45)]
        data = make_mp4(self.raw_samples * 45, self.offsets * 45, self.avcc)
        return segments, self.files(data, self.hashes * 45, stem='concat')

    def test_reused_definitions_and_bounds(self):
        # Source digest was checked against retained SHA4b3aad13... definitions;
        # source segments avoid Python-version-dependent AST field inventories.
        import ast
        text = Path(h264_helpers.__file__).read_text()
        current = ast.parse(text)
        definitions = {node.name: ast.get_source_segment(text, node) for node in current.body
                       if isinstance(node, (ast.FunctionDef, ast.ClassDef))}
        self.assertEqual(media.sha(json.dumps(definitions, sort_keys=True, separators=(',', ':')).encode()),
                         '3938ffe75ce6ffddba6041b0dff88172b7a9b8f1168ea5275a6f8eaca49aba83')
        h264_helpers.self_tests()

    def test_actual_saved_film_all_three(self):
        for label in ('film-plain', 'film-observed-a', 'film-observed-b'):
            with self.subTest(label=label):
                folder = ARTIFACT / label
                item = shifted(self.old.read_segment(next((folder / 'public').glob('*.tsv')))['item'], 768)
                result = media.verify_segment(folder / 'segment/0000000768.mp4', folder / 'packets.json',
                                              folder / 'decoded.sha256', folder / 'redecoded.sha256', item, pts_base=768)
                self.assertEqual(set(result['packets']), set(range(768, 792)))
                self.assertEqual(result['header_prefix_lengths'], [4, 4, 3])
                self.assertEqual(result['header'], tuple(item['headers'][0]))
                self.assertEqual(result['first_idr']['slice_qp'], 15)
                self.assertEqual(result['decoded'][768], self.hashes[0])

    def test_actual_saved_synthetic_and_second_lifetime(self):
        for label in ('smoke-plain', 'smoke-instrumented-off', 'smoke-instrumented-on'):
            folder = ARTIFACT / label
            item = self.old.read_segment(next((folder / 'public').glob('*.tsv')))['item']
            for base in (0, 24):
                with self.subTest(label=label, base=base):
                    result = media.verify_synthetic(folder / 'encoded.h264', shifted(item, base), pts_base=base)
                    self.assertEqual(set(result['packets']), set(range(base, base + 24)))
                    self.assertTrue(result['coverage']['complete'])
                    self.assertEqual(len(media.read_decoded(folder / 'decoded.sha256', folder / 'redecoded.sha256')), 24)

    def test_table_variants_exact_timing_all45(self):
        for base in range(0, 1080, 24):
            with self.subTest(base=base):
                result = self.segment(base, co64=bool(base % 48), chunk_split=12,
                                      ctts_v1=True, edits_v1=True)
                self.assertEqual(set(result['packets']), set(range(base, base + 24)))
                self.assertTrue(result['coverage']['complete'])

    def test_concat_all1080_and_each_own_header(self):
        segments, paths = self.concat_fixture()
        result = media.verify_concat(*paths, segments)
        self.assertEqual(len(result['bindings']), 1080)
        self.assertEqual(set(result['decoded']), set(range(1080)))
        self.assertEqual([row['segment'] for row in result['bindings'][::24]], list(range(45)))
        self.assertEqual(result['segment_headers'], [segment['header'] for segment in segments])

    def test_concat_uses_every_segments_own_sei_not_first_avcc(self):
        segments, all_samples = [], []
        for index in range(45):
            samples = list(self.raw_samples)
            nals = media.split_avcc(samples[0])
            sei = bytearray(nals[0])
            sei[-2] = 33 + index  # Retained header payload mutation, not a new encoding.
            nals[0] = bytes(sei)
            samples[0] = b''.join(len(nal).to_bytes(4, 'big') + nal for nal in nals)
            item = shifted(self.old_item, index * 24)
            header_nals = self.parsed['sets'] + [nals[0]]
            sizes = item['headers'][0][3]
            raw = b''.join(b'\0' * (size - len(nal) - 1) + b'\1' + nal for nal, size in zip(header_nals, sizes))
            item['headers'] = [(len(raw), 3, media.sha(raw), sizes)]
            data = make_mp4(samples, self.offsets, self.avcc, index * 24)
            segments.append(media.verify_segment(*self.files(data, stem=f'own{index}'), item, pts_base=index * 24))
            all_samples.extend(samples)
        self.assertEqual(len({segment['header'][2] for segment in segments}), 45)
        paths = self.files(make_mp4(all_samples, self.offsets * 45, self.avcc), self.hashes * 45)
        result = media.verify_concat(*paths, segments)
        self.assertEqual(result['segment_headers'], [segment['header'] for segment in segments])

    def test_concat_rejects_later_parameter_sets_not_used_by_sole_avcc(self):
        segments, paths = self.concat_fixture()
        for kind in ('sps', 'pps'):
            avcc = bytearray(self.avcc)
            sps_size = int.from_bytes(avcc[6:8], 'big')
            if kind == 'sps':
                avcc[3] ^= 1  # avcC level mirror and actual SPS level_idc.
                avcc[11] ^= 1
            else:
                pps_start = 8 + sps_size + 3
                avcc[pps_start+4] ^= 2  # constrained_intra_pred_flag; supported syntax.
            changed_sets = media.avcc_sets(avcc)
            item = shifted(self.old_item, 17*24)
            header_nals = changed_sets + [media.split_avcc(self.raw_samples[0])[0]]
            sizes = item['headers'][0][3]
            raw = b''.join(b'\0' * (size-len(nal)-1) + b'\1' + nal
                           for nal, size in zip(header_nals, sizes))
            item['headers'] = [(len(raw), 3, media.sha(raw), sizes)]
            data = make_mp4(self.raw_samples, self.offsets, bytes(avcc), 17*24)
            changed = media.verify_segment(*self.files(data, stem=kind), item, pts_base=17*24)
            operands = list(segments)
            operands[17] = changed
            with self.subTest(kind=kind), self.assertRaises(media.BindingError):
                media.verify_concat(*paths, operands)

    def test_native_header_output_hash_size_and_coverage_tamper(self):
        paths = self.files(self.fixture())
        for variant in ('output_hash', 'output_size', 'header_hash', 'header_size', 'missing_input',
                        'duplicate_input', 'duplicate_output', 'missing_output', 'call_pts', 'empty', 'mode', 'header_order'):
            item = shifted(self.old_item, 0)
            if variant.startswith('output_'):
                calls = list(item['calls'])
                idx = next(i for i, row in enumerate(calls) if row[2])
                row = list(calls[idx])
                if variant == 'output_hash':
                    row[4] = '0' * 64
                else:
                    row[1] += 1
                    row[5] = (row[5][0] + 1,)
                calls[idx] = tuple(row)
                item['calls'] = calls
            elif variant.startswith('header_'):
                result, count, digest, sizes = item['headers'][0]
                if variant == 'header_hash':
                    digest = '0' * 64
                else:
                    sizes = tuple(size + (1 if i == 2 else 0) for i, size in enumerate(sizes))
                    result += 1
                item['headers'] = [(result, count, digest, sizes)]
            elif variant == 'missing_input':
                item['inputs'].pop()
            elif variant == 'duplicate_input':
                item['inputs'][-1] = item['inputs'][0]
            elif variant == 'duplicate_output':
                item['outputs'][-1] = item['outputs'][0]
            elif variant == 'missing_output':
                item['calls'] = [row for row in item['calls'] if row[3] != '23']
            elif variant == 'call_pts':
                item['calls'] = [(f, r, c, '0' if p == '23' else p, h, s) for f, r, c, p, h, s in item['calls']]
            elif variant == 'empty':
                row = list(item['calls'][0]); row[4] = '0' * 64; item['calls'][0] = tuple(row)
            elif variant == 'header_order':
                headers = [event for event in item['events'] if event[0] == 'headers']
                item['events'] = [event for event in item['events'] if event[0] != 'headers'] + headers
            else:
                item['states'][0]['param.b_annexb'] = 'unexpected'
            with self.subTest(variant=variant), self.assertRaises((media.BindingError, AssertionError)):
                media.verify_segment(*paths, item, pts_base=0)

    def test_probe_position_size_hash_rational_timestamp_forgery(self):
        data = self.fixture()
        original = probe_for(data)
        mutations = []
        for field, value in (('pos', '0'), ('size', '1'), ('data_hash', 'SHA256:' + '0' * 64),
                             ('pts', 1), ('dts', 1), ('duration', 511)):
            report = copy.deepcopy(original); report['packets'][0][field] = value; mutations.append((field, report))
        for field, value in (('time_base', '1/15361'), ('r_frame_rate', '30000/1001'), ('duration', '0.800001'),
                             ('start_pts', 1), ('extradata_hash', 'SHA256:' + '0' * 64), ('nb_frames', '23')):
            report = copy.deepcopy(original); report['streams'][0][field] = value; mutations.append((field, report))
        report = copy.deepcopy(original); report['packets'].pop(); mutations.append(('missing', report))
        report = copy.deepcopy(original); report['packets'][-1] = report['packets'][0]; mutations.append(('duplicate', report))
        report = copy.deepcopy(original); report['streams'].append(report['streams'][0]); mutations.append(('two_video', report))
        # Coherent JSON-only timestamp shift must still fail source-table binding.
        report = copy.deepcopy(original)
        for row in report['packets']:
            row['pts'] += 512; row['dts'] += 512
        report['streams'][0]['start_pts'] += 512
        mutations.append(('fabricated_shift', report))
        for name, report in mutations:
            with self.subTest(name=name), self.assertRaises(media.BindingError):
                media.verify_segment(*self.files(data, probe=report), shifted(self.old_item, 0), pts_base=0)

    def test_media_header_sei_output_unknown_nal_and_avcc_tamper(self):
        original = self.fixture()
        parsed = media.sample_table(original)
        first = parsed['samples'][0]['pos']
        # First packet: 4-byte length + SEI body, then4-byte length + IDR.
        sei_len = int.from_bytes(original[first:first + 4], 'big')
        for name, location, value in (
                ('sei', first + 8, original[first + 8] ^ 1),
                ('output', first + 4 + sei_len + 8, original[first + 4 + sei_len + 8] ^ 1),
                ('unknown_nal', first + 4, 9),
                ('length_zero', first + 3, 0),
                ('avcc_length_metadata', original.index(b'avcC') + 8, 254)):
            data = bytearray(original); data[location] = value
            # Recreate probe hashes to ensure disagreement is independently caught at native/header stage.
            try:
                report = probe_for(data)
            except (media.BindingError, AssertionError):
                report = probe_for(original)
            with self.subTest(name=name), self.assertRaises((media.BindingError, AssertionError)):
                media.verify_segment(*self.files(data, probe=report), shifted(self.old_item, 0), pts_base=0)
        # Duplicate avcC structural box, not a string search heuristic.
        visual = bytearray(78); visual[6:8] = b'\0\1'; visual[24:28] = struct.pack('>HH', 854, 480)
        with self.assertRaises(media.BindingError):
            media.one(media.boxes(bytes(visual) + box(b'avcC', self.avcc) * 2, 78), b'avcC')

    def test_malformed_table_extents_timestamps_and_fragmentation(self):
        original = self.fixture()
        for name, data in (
                ('truncated', original[:-1]), ('fragment', original + box(b'moof', b'')),
                ('stsz', self.mutate_word(original, b'stsz', 8, 23)),
                ('stsc', self.mutate_word(original, b'stsc', 12, 23)),
                ('stco', self.mutate_word(original, b'stco', 8, 0)),
                ('stts', self.mutate_word(original, b'stts', 12, 511)),
                ('stss', self.mutate_word(original, b'stss', 8, 2)),
                ('ctts', self.mutate_word(original, b'ctts', 12, 1025)),
                ('elst', self.mutate_word(original, b'elst', 12, 1025)),
                ('unknown_table', original.replace(b'stts', b'cslg', 1))):
            with self.subTest(name=name), self.assertRaises((media.BindingError, AssertionError)):
                media.verify_segment(*self.files(data, probe=probe_for(original)), shifted(self.old_item, 0), pts_base=0)
        # Even mutually consistent fabricated probe + ctts timestamps cannot
        # impersonate native global PTS. Shift every composition offset by one tick.
        data = bytearray(original)
        ctts = data.index(b'ctts') + 4
        count = int.from_bytes(data[ctts + 4:ctts + 8], 'big')
        for i in range(count):
            at = ctts + 12 + i * 8
            value = int.from_bytes(data[at:at + 4], 'big') + 1
            data[at:at + 4] = value.to_bytes(4, 'big')
        report = probe_for(data)
        with self.assertRaises(media.BindingError):
            media.verify_segment(*self.files(data, probe=report), shifted(self.old_item, 0), pts_base=0)
        for raw in (b'\0\0\0', b'\0\0\0\0', b'\0\0\0\x05\x65', b'\0\0\0\1\x09'):
            with self.assertRaises(media.BindingError):
                media.split_avcc(raw)

    @staticmethod
    def mutate_word(data, kind, offset, value):
        data = bytearray(data)
        where = data.index(kind) + 4 + offset
        data[where:where + 4] = value.to_bytes(4, 'big')
        return bytes(data)

    def test_decode_mismatch_coverage_geometry_size_timebase_and_order(self):
        good = decode_text(self.hashes)
        for name, text in (
                ('missing', good.rsplit('\n', 2)[0] + '\n'),
                ('duplicate', good + good.splitlines()[-1] + '\n'),
                ('size', good.replace('614880', '614881', 1)),
                ('timebase', good.replace('1/30', '1/29')),
                ('geometry', good.replace('854x480', '864x480')),
                ('pts', good.replace('0, 0, 0, 1,', '0, 0, 1, 1,')),
                ('hash', good.replace(self.hashes[0], 'invalid')),
                ('extra_stream', good + '#tb 1: 1/30\n')):
            paths = self.files(self.fixture())
            paths[2].write_text(text); paths[3].write_text(text)
            with self.subTest(name=name), self.assertRaises(media.BindingError):
                media.read_decoded(paths[2], paths[3])
        paths = self.files(self.fixture())
        paths[3].write_text(good.replace(self.hashes[0], '0' * 64))
        with self.assertRaises(media.BindingError):
            media.read_decoded(paths[2], paths[3])

    def test_concat_missing_duplicate_segment_frame_and_transformed_bytes(self):
        segments, paths = self.concat_fixture()
        variants = [('missing_segment', segments[:-1]), ('duplicate_segment', segments[:-1] + [segments[0]])]
        altered = copy.deepcopy(segments); altered[17]['packets'].pop(408); variants.append(('missing_frame', altered))
        altered = copy.deepcopy(segments); altered[17]['decoded'].pop(408); variants.append(('missing_decode', altered))
        altered = copy.deepcopy(segments); altered[17]['packets'][408]['packet_sha256'] = '0' * 64
        variants.append(('transformed_packet', altered))
        altered = copy.deepcopy(segments); altered[17]['packets'][408]['dts'] = '999/30'
        variants.append(('shifted_dts', altered))
        altered = copy.deepcopy(segments); altered[17]['decoded'][408] = '0' * 64
        variants.append(('transformed_decode', altered))
        altered = copy.deepcopy(segments); altered[17]['header'] = (731, 3, '0' * 64, (33, 10, 688))
        variants.append(('own_header', altered))
        for name, operands in variants:
            with self.subTest(name=name), self.assertRaises(media.BindingError):
                media.verify_concat(*paths, operands)
        report = json.loads(paths[1].read_text()); report['packets'].pop()
        paths[1].write_text(json.dumps(report))
        with self.assertRaises(media.BindingError):
            media.verify_concat(*paths, segments)

    def test_native_prefix_metadata_avcc_and_annexb3_4(self):
        nals = [b'\x67abc', b'\x68de', b'\x06fgh']
        for mode, prefixes in (('annexb', (4, 4, 3)), ('annexb', (3, 4, 4)), ('avcc', (4, 4, 4))):
            raw = b''.join((b'\0' * (p - 1) + b'\1' if mode == 'annexb' else len(nal).to_bytes(4, 'big')) + nal
                           for nal, p in zip(nals, prefixes))
            value = (len(raw), 3, media.sha(raw), tuple(len(nal) + p for nal, p in zip(nals, prefixes)))
            self.assertEqual(media.reconstruct(nals, value, mode)[1], list(prefixes))
            with self.assertRaises(media.BindingError):
                media.reconstruct(nals, value, 'avcc' if mode == 'annexb' else 'annexb')
        # Complete AVCC-native bridge, using the actual saved NAL bodies and adapted inventories.
        item = shifted(self.old_item, 0)
        item['states'] = [state | {'param.b_annexb': '0'} for state in item['states']]
        def value(nals):
            raw = b''.join(len(nal).to_bytes(4, 'big') + nal for nal in nals)
            return len(raw), len(nals), media.sha(raw), tuple(len(nal) + 4 for nal in nals)
        item['headers'] = [value(self.parsed['sets'] + [media.split_avcc(self.raw_samples[0])[0]])]
        calls, index = [], 0
        for flush, result, count, pts, digest, sizes in item['calls']:
            if count:
                nals = media.split_avcc(self.raw_samples[index]); index += 1
                nals = [nal for nal in nals if media.nal_type(nal) != 6]
                result, count, digest, sizes = value(nals)
            calls.append((flush, result, count, pts, digest, sizes))
        item['calls'] = calls
        self.assertTrue(media.verify_segment(*self.files(self.fixture()), item, pts_base=0)['coverage']['complete'])

    def test_synthetic_byte_hash_header_order_extent_and_unknown_nal_tamper(self):
        folder = ARTIFACT / 'smoke-plain'
        item = self.old.read_segment(next((folder / 'public').glob('*.tsv')))['item']
        original = (folder / 'encoded.h264').read_bytes()
        path = self.folder / 'synthetic.h264'
        for name, data in (('truncated', original[:-1]), ('trailing', original + b'\0'),
                           ('header', original[:8] + bytes([original[8] ^ 1]) + original[9:]),
                           ('unknown', original[:4] + b'\x09' + original[5:]),
                           ('output', original[:-4] + bytes([original[-4] ^ 1]) + original[-3:])):
            path.write_bytes(data)
            with self.subTest(name=name), self.assertRaises((media.BindingError, AssertionError)):
                media.verify_synthetic(path, item, pts_base=0)
        item = copy.deepcopy(item); item['outputs'] = list(reversed(item['outputs']))
        path.write_bytes(original)
        with self.assertRaises(media.BindingError):
            media.verify_synthetic(path, item, pts_base=0)


if __name__ == '__main__':
    unittest.main()
