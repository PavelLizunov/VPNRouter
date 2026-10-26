"""Stdlib source/data tests only. Never compile, link, render, decode or use Git."""
import copy
import hashlib
import importlib.util
import io
import os
from pathlib import Path
import tarfile
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
RETAINED = ROOT / 'tools/help-videos/fframes-vpnrouter/out/repeatability-analysis/x264-ubuntu-3108-1'


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


internal = load('ffh_internal', HERE / 'internal.py')
instrument = load('ffh_instrument', HERE / 'instrument.py')
public = load('ffh_public', ROOT / '.github/diagnostics/analyze_fframes_input.py')


def digest(data):
    return hashlib.sha256(data).hexdigest()


class Fixture:
    """Real geometry and parser branches, sparse zero raw extents, no giant writes.

    The raw logical size is the required film size; sparse holes avoid allocating
    or writing its 1.3GB of zeros. The real parser still reads/hashes every extent.
    Only qpm's four initialized raw bytes need an actual per-picture write.
    """
    def __init__(self, root, count=2, qpm_bits=0):
        self.root, self.count = root, count
        self.rows = [f'schema\tffh-v1\tle\t{count}']
        self.offsets = {'pixels': 0, 'state': 0}
        self.hashes = {}
        self.public_rows = []
        self.qpm_bits = qpm_bits
        self.qpm_offsets = []
        for k in range(count):
            ident = k + 1
            config = dict(internal.OPEN_FIXED, cpu='1176575', stat_in_present='1', stat_out_present='1')
            self.rows.append(f'open\t{ident}\t' + '\t'.join(f'{n}={v}' for n, v in config.items()))
            self.public_rows.extend([f'open\t{ident}\t854\t480\t2\t1\t1\t0\t1',
                f'headers\t{ident}\t3\t1\t' + 'c'*64 + '\t3'])
            for local in range(24):
                pts = k*24 + local
                visible = []
                for stage in ('entry', 'coded', 'post_aq'):
                    metadata = ['pic_type=0', 'qpplus1=0', 'pic_struct=0'] if stage == 'entry' else []
                    descriptors = []
                    for name, (kind, width, height) in internal.RECTS[stage].items():
                        stride = {'Y': 896, 'U': 448, 'V': 448, 'UV': 896}.get(name)
                        size = width*height*{'u8': 1, 'f32': 4, 'u16': 2}[kind]
                        d = self.descriptor(stage, name, kind, width, height, stride or size)
                        descriptors.append(d)
                        if stage == 'entry': visible.append(d.rsplit(',', 1)[1])
                    self.rows.append(f'{stage}\t{ident}\t{local}\t{pts}\t' + '\t'.join(metadata+descriptors))
                self.public_rows.extend([self.state(ident, local, False),
                    f'frame\t{ident}\t{pts}\t' + '\t'.join(visible),
                    f'output\t{ident}\t{local}\t0\t0\t0\t-\t{public.EMPTY_SHA256}'])
            # Delayed B-like coding order, not presentation/input order.
            order = [0] + [v for start in range(1, 24, 3) for v in reversed(range(start, min(start+3, 24)))]
            for call, local in enumerate(order, 24):
                pts = k*24+local
                self.rows.append(f'qpm\t{ident}\t{local}\t{pts}\ttype=3\tref=1\tb_vbv=0\tbits={qpm_bits:08x}\t' +
                    self.descriptor('qpm', 'qpm', 'f32', 1, 1, 4))
                self.rows.append(f'pre_slice\t{ident}\t{local}\t{pts}\ttype=3\tref=1\tslice_type=0\tslice_init_qp=20\trc_global_qp=20\t' +
                    '\t'.join(self.descriptor('pre_slice', n, *shape, 6480) for n, shape in internal.RECTS['pre_slice'].items()))
                self.public_rows.extend([self.state(ident, call, True),
                    f'output\t{ident}\t{call}\t1\t4\t1\t{pts}\t' + 'd'*64 + '\t4'])
            self.public_rows.extend([self.state(ident, 48, True),
                f'output\t{ident}\t48\t1\t0\t0\t-\t{public.EMPTY_SHA256}', f'close\t{ident}\t24\t24\t49\t25\t1'])
            self.rows.extend([f'closing\t{ident}\t24\t24\t24\t24\t24', f'closed\t{ident}'])
        self.rows.append(f'terminal\t{count}\t{count*24}\t{self.offsets["pixels"]}\t{self.offsets["state"]}')
        for stream in ('pixels', 'state'):
            with (root / (stream+'.raw')).open('wb') as out:
                out.truncate(self.offsets[stream])
                if stream == 'state':
                    for offset in self.qpm_offsets:
                        out.seek(offset)
                        out.write(qpm_bits.to_bytes(4, 'little'))
        self.save()
        (root / 'public.tsv').write_text('\n'.join(self.public_rows)+'\n')
        # Exercise the unchanged public reader instead of fabricating its result.
        self.boundary = public.read_boundary(root/'public.tsv', expected_frames=count*24, require_state=True)

    def descriptor(self, stage, name, kind, width, height, stride):
        stream = 'pixels' if stage in ('entry', 'coded') else 'state'
        size = width*height*{'u8': 1, 'f32': 4, 'u16': 2}[kind]
        data = self.qpm_bits.to_bytes(4, 'little') if stage == 'qpm' else None
        key = (size, data)
        if key not in self.hashes:
            self.hashes[key] = digest(data if data is not None else bytes(size))
        offset = self.offsets[stream]
        self.offsets[stream] += size
        if stage == 'qpm': self.qpm_offsets.append(offset)
        return f'{name}={kind},{width},{height},{stride},{offset},{size},{self.hashes[key]}'

    def state(self, ident, call, flush):
        values = {'fp.mxcsr_control': '8064', 'fp.x87_control': '895'}
        for name, kind in public.PARAM_FIELDS.items():
            values[name] = 'a'*64 if kind == 'CQM' else '00000000' if kind == 'FLOAT' else '0'
        values.update({'param.'+n: v for n, v in internal.PUBLIC_GATE.items()})
        values.update({'param.cpu': '1176575', 'param.rc.psz_stat_in': '1', 'param.rc.psz_stat_out': '1'})
        if not flush:
            values.update({'pic.'+n: '0' for n in public.PICTURE_FIELDS})
            values.update({'pic.img.i_csp': '2', 'pic.img.i_plane': '3', 'pic.img.i_stride[0]': '896',
                           'pic.img.i_stride[1]': '448', 'pic.img.i_stride[2]': '448'})
        return f'state\t{ident}\t{call}\t{int(flush)}\t'+'\t'.join(f'{n}={v}' for n,v in values.items())

    def save(self, rows=None):
        (self.root/'internal.tsv').write_text('\n'.join(self.rows if rows is None else rows)+'\n')

    def read(self, boundary=None):
        return internal.read_internal(self.root, boundary or self.boundary, self.count)


class ParserTests(unittest.TestCase):
    def test_full_45_lifetimes_delayed_consumption_sparse_raw(self):
        with tempfile.TemporaryDirectory() as temp:
            fixture = Fixture(Path(temp), 45)
            result = fixture.read()
            self.assertEqual(len(result['frames']), 1080)
            self.assertEqual([v['id'] for v in result['lifetimes']], list(range(1, 46)))
            self.assertEqual(result['raw_sizes']['pixels'], 1335916800)
            self.assertTrue(all(v == 1080 for v in result['coverage'].values()))
            self.assertTrue(all(not v for v in internal.compare_internal(result, result).values()))
            # No pointer is serialized or used to infer lifetimes. Thus the same
            # native address reused after each closed/open pair cannot merge IDs.
            self.assertNotIn('pointer', (fixture.root/'internal.tsv').read_text())

    def test_two_lifetimes_signed_zero_nonfinite_exact_bits(self):
        for bits in (0x80000000, 0x7f800000, 0xff800000, 0x7fc00001):
            with self.subTest(bits=hex(bits)), tempfile.TemporaryDirectory() as temp:
                fixture = Fixture(Path(temp), qpm_bits=bits)
                result = fixture.read()
                self.assertEqual(result['frames'][0]['qpm']['metadata']['bits'], f'{bits:08x}')
                changed = copy.deepcopy(result)
                changed['frames'][0]['qpm']['metadata']['bits'] = f'{bits ^ 1:08x}'
                changed['frames'][0]['qpm']['raw']['qpm']['sha256'] = digest((bits ^ 1).to_bytes(4, 'little'))
                self.assertEqual(internal.compare_internal(result, changed)['qpm_changed_pts'], [0])
                changed = copy.deepcopy(result)
                changed['frames'][1]['pre_slice']['raw']['offset']['sha256'] = 'f'*64
                self.assertEqual(internal.compare_internal(result, changed)['pre_slice_changed_pts'], [1])

    def test_real_parsed_operands_detect_low_mantissa_and_signed_zero(self):
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            left = Fixture(Path(a), qpm_bits=0x3f800000).read()
            right = Fixture(Path(b), qpm_bits=0x3f800001).read()
            comparison = internal.compare_internal(left, right)
            self.assertEqual(comparison['qpm_changed_pts'], list(range(48)))
            self.assertTrue(all(not values for name, values in comparison.items() if name != 'qpm_changed_pts'))
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            left = Fixture(Path(a), qpm_bits=0).read()
            right = Fixture(Path(b), qpm_bits=0x80000000).read()
            self.assertEqual(internal.compare_internal(left, right)['qpm_changed_pts'], list(range(48)))

    def test_actual_array_nonfinite_and_low_bit_preservation(self):
        with tempfile.TemporaryDirectory() as temp:
            fixture = Fixture(Path(temp))
            before = fixture.read()
            index = next(i for i,r in enumerate(fixture.rows) if r.startswith('post_aq\t1\t0\t0'))
            fields = fixture.rows[index].split('\t')
            name, descriptor = fields[4].split('=')
            parts = descriptor.split(',')
            offset, size = int(parts[4]), int(parts[5])
            data = bytearray(size)
            # Preserve signaling/quiet payloads, infinity, signed zero, low bit.
            data[:20] = b''.join(v.to_bytes(4, 'little') for v in
                (0x7f800001, 0x7fc00001, 0x7f800000, 0x80000000, 0x3f800001))
            with (fixture.root/'state.raw').open('r+b') as stream:
                stream.seek(offset)
                stream.write(data)
            parts[6] = digest(data)
            fields[4] = name+'='+','.join(parts)
            fixture.rows[index] = '\t'.join(fields)
            fixture.save()
            after = fixture.read()
            self.assertEqual(after['frames'][0]['post_aq']['raw']['offset']['sha256'], digest(data))
            self.assertEqual(internal.compare_internal(before, after)['post_aq_changed_pts'], [0])

    def test_consumption_matches_public_output_order_and_call_chronology(self):
        with tempfile.TemporaryDirectory() as temp:
            f = Fixture(Path(temp))
            rows = []
            for row in f.public_rows:
                fields = row.split('\t')
                if fields[0] == 'output' and fields[5] != '0':
                    base = (int(fields[1])-1)*24
                    fields[6] = str(base+23-(int(fields[6])-base))
                rows.append('\t'.join(fields))
            (f.root/'public.tsv').write_text('\n'.join(rows)+'\n')
            changed = public.read_boundary(f.root/'public.tsv', expected_frames=48, require_state=True)
            with self.assertRaisesRegex(ValueError, 'call order mismatch'):
                f.read(changed)
            # Same output sequence, but consume before all submissions although
            # this fixture's public path emits every nonempty result in flush.
            first_qpm = next(i for i,r in enumerate(f.rows) if r.startswith('qpm\t1\t0\t'))
            next_entry = next(i for i,r in enumerate(f.rows) if r.startswith('entry\t1\t1\t'))
            reordered = f.rows[:first_qpm]+f.rows[first_qpm+2:]
            reordered[next_entry:next_entry] = f.rows[first_qpm:first_qpm+2]
            f.save(reordered)
            with self.assertRaisesRegex(ValueError, 'call order mismatch'):
                f.read()

    def test_rc_global_and_signaled_slice_qp_have_distinct_native_domains(self):
        with tempfile.TemporaryDirectory() as temp:
            f = Fixture(Path(temp))
            for qp in (52, 69):
                f.save([row.replace('slice_init_qp=20', 'slice_init_qp=51')
                       .replace('rc_global_qp=20', f'rc_global_qp={qp}') for row in f.rows])
                self.assertEqual(f.read()['frames'][0]['pre_slice']['metadata']['rc_global_qp'], str(qp))
            for name, value in (('slice_init_qp', 52), ('rc_global_qp', 70)):
                f.save([row.replace(f'{name}=20', f'{name}={value}') for row in f.rows])
                with self.assertRaisesRegex(ValueError, 'Invalid slice initialization'):
                    f.read()

    def test_lifecycle_phase_terminal_mutations(self):
        with tempfile.TemporaryDirectory() as temp:
            f = Fixture(Path(temp))
            stage = next(i for i,r in enumerate(f.rows) if r.startswith('post_aq\t1\t0\t0'))
            qpm = next(i for i,r in enumerate(f.rows) if r.startswith('qpm\t1\t0\t0'))
            close = next(i for i,r in enumerate(f.rows) if r == 'closed\t1')
            cases = [f.rows[:stage]+f.rows[stage+1:], f.rows[:stage]+[f.rows[stage]]+f.rows[stage:],
                f.rows[:-1], f.rows+[f.rows[-1]], f.rows[:close]+f.rows[close+1:],
                [r.replace('closing\t1\t24', 'closing\t1\t23') for r in f.rows],
                [r.replace('open\t2\t', 'open\t1\t') for r in f.rows],
                [r.replace('post_aq\t1\t0\t0', 'post_aq\t1\t1\t0') for r in f.rows],
                [r.replace('bits=00000000', 'bits=00000001') for r in f.rows],
                [r.replace('b_vbv=0', 'b_vbv=1') for r in f.rows],
                f.rows[:qpm]+[f.rows[qpm+1], f.rows[qpm]]+f.rows[qpm+2:],
                f.rows[:1]+[f.rows[1], f.rows[1]]+f.rows[2:],
                [r.replace('ref=1', 'ref=0') if r.startswith('pre_slice\t1\t0\t') else r for r in f.rows],
                [r.replace('qpm=f32,1,1,4,', 'qpm=f64,1,1,4,') for r in f.rows],
                [r.replace('offset=f32,1620,1,6480,', 'offset=f32,1619,1,6480,') for r in f.rows],
                [r.replace('terminal\t2\t48', 'terminal\t2\t47') for r in f.rows]]
            for index, rows in enumerate(cases):
                with self.subTest(index=index):
                    f.save(rows)
                    with self.assertRaises(ValueError): f.read()
            f.save()
            with (f.root/'state.raw').open('r+b') as stream: stream.truncate(f.offsets['state']-1)
            with self.assertRaises(ValueError): f.read()

    def test_public_join_and_unsupported_settings(self):
        with tempfile.TemporaryDirectory() as temp:
            f = Fixture(Path(temp))
            changes = {'param.rc.i_aq_mode': '0', 'param.rc.b_stat_read': '1',
                'param.rc.b_stat_write': '1', 'param.rc.i_vbv_buffer_size': '1',
                'param.i_threads': '2', 'param.i_lookahead_threads': '2',
                'param.i_sync_lookahead': '1', 'param.rc.zones': '1', 'pic.prop.quant_offsets': '1',
                'pic.param': '1', 'pic.extra_sei.num_payloads': '1',
                'pic.img.i_stride[0]': '897', 'param.rc.f_aq_strength': '3f19999b'}
            for name, value in changes.items():
                boundary = copy.deepcopy(f.boundary)
                boundary['segments'][0]['states'][0][name] = value
                with self.subTest(field=name), self.assertRaises(ValueError): f.read(boundary)
            for key, value in (('lowres', '0'), ('aq_mode', '0'), ('mb_stride', '55'), ('thread_frames', '2')):
                f.save([r.replace(f'{key}={internal.OPEN_FIXED[key]}', f'{key}={value}') if r.startswith('open\t') else r for r in f.rows])
                with self.subTest(native=key), self.assertRaises(ValueError): f.read()
            f.save()
            boundary = copy.deepcopy(f.boundary)
            boundary['frames'][0] = ('f'*64,)*3
            with self.assertRaises(ValueError): f.read(boundary)
            path = f.root/'pixels.raw'
            path.unlink()
            path.symlink_to(f.root/'state.raw')
            with self.assertRaises(ValueError): f.read()

    def test_raw_extent_bounds_size_hash_type_and_low_mantissa(self):
        raw = b'\x01\x00\x80\x3f' # one low mantissa bit, not decimal rendering
        descriptor = f'f32,1,1,4,0,4,{digest(raw)}'
        value, saved = internal.read_extent(io.BytesIO(raw), descriptor, ('f32',1,1), 0)
        self.assertEqual(saved, raw)
        for mutation in (descriptor.replace('f32', 'f64'), descriptor.replace(',0,4,', ',1,4,'),
            descriptor.replace(',0,4,', ',0,8,'), descriptor[:-1]+'x'):
            with self.assertRaises(ValueError): internal.read_extent(io.BytesIO(raw), mutation, ('f32',1,1), 0)
        for data in (raw[:-1], b'\x00'+raw[1:]):
            with self.assertRaises(ValueError): internal.read_extent(io.BytesIO(data), descriptor, ('f32',1,1), 0)


class InstrumentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.archive = Path(os.environ.get('FFH_TEST_X264_ARCHIVE', str(RETAINED / instrument.ARCHIVE_NAME)))
        # Missing retained evidence is a failure, not a skipped test.
        if not cls.archive.is_file(): raise AssertionError('Retained source archive is required')
        with tarfile.open(cls.archive) as tar:
            cls.members = {name: tar.extractfile(instrument.PREFIX+name).read() for name in instrument.SOURCE_HASHES}

    def tree(self, root):
        source = root/'source'
        for name, data in self.members.items():
            path = source/name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        return source

    def test_actual_patcher_against_retained_archive_and_17_members(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self.tree(Path(temp))
            result = instrument.instrument(source, HERE/'full-history-observer.h', self.archive)
            self.assertEqual(len(result['original_source_sha256']), 17)
            for name in self.members:
                actual = (source/name).read_bytes()
                if name not in ('encoder/encoder.c', 'encoder/ratecontrol.c'):
                    self.assertEqual(actual, self.members[name])
            encoder = (source/'encoder/encoder.c').read_text()
            rc = (source/'encoder/ratecontrol.c').read_text()
            self.assertEqual(rc.count('    ffh_qpm( h );'), 1)
            original_qp = self.members['encoder/ratecontrol.c'].decode().split(instrument.QPM)[1].split('\n}')[0]
            patched_qp = rc.split(instrument.QPM)[1].split('\n}')[0]
            self.assertEqual(patched_qp.replace('    ffh_qpm( h );\n',''), original_qp)
            self.assertIn('#endif\n    ffh_closed();\n}', encoder)
            self.assertEqual(encoder.count('ffh_open( h );'), 1)
            with self.assertRaises(ValueError): instrument.instrument(source, HERE/'full-history-observer.h', self.archive)

    def test_hash_failure_before_any_source_write(self):
        for name in ('encoder/encoder.c', 'common/cpu.h', 'configure'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                source = self.tree(Path(temp))
                (source/name).write_bytes(self.members[name]+b'\n')
                before = {n: (source/n).read_bytes() for n in self.members}
                with self.assertRaises(ValueError): instrument.instrument(source, HERE/'full-history-observer.h', self.archive)
                self.assertEqual({n: (source/n).read_bytes() for n in self.members}, before)
                self.assertFalse((source/'encoder/full-history-observer.h').exists())

    def test_wrong_archive_and_symlink_member_fail_before_writes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = self.tree(root)
            bad = root/'wrong.tar.gz'
            bad.write_bytes(self.archive.read_bytes()+b'wrong')
            with self.assertRaises(ValueError): instrument.instrument(source, HERE/'full-history-observer.h', bad)
            self.assertFalse((source/'encoder/full-history-observer.h').exists())
            target = source/'common/cpu.h'
            retained = root/'cpu.h'
            retained.write_bytes(target.read_bytes())
            target.unlink()
            target.symlink_to(retained)
            with self.assertRaises(ValueError): instrument.instrument(source, HERE/'full-history-observer.h', self.archive)
            self.assertEqual((source/'encoder/encoder.c').read_bytes(), self.members['encoder/encoder.c'])

    def test_independent_missing_duplicate_and_shifted_anchor_guards(self):
        data = self.members['encoder/encoder.c']
        for anchor in (instrument.OPEN, instrument.ENTRY, instrument.CODED, instrument.AQ,
                       instrument.SLICE, instrument.CLOSING, instrument.CLOSED):
            with self.subTest(anchor=anchor[:40]):
                with self.assertRaises(ValueError): instrument.patch_encoder(data.replace(anchor.encode(), b'', 1))
                with self.assertRaises(ValueError): instrument.patch_encoder(data+anchor.encode())
        # Preserve uniqueness but move entry behind coded: exercises the order guard.
        shifted = data.replace(instrument.ENTRY.encode(), b'', 1).replace(instrument.CODED.encode(),
                    (instrument.CODED+instrument.ENTRY).encode(), 1)
        with self.assertRaises(ValueError): instrument.patch_encoder(shifted)
        rc = self.members['encoder/ratecontrol.c']
        for anchor in (instrument.RC_INCLUDE, instrument.QPM):
            with self.assertRaises(ValueError): instrument.patch_ratecontrol(rc.replace(anchor.encode(), b'', 1))
            with self.assertRaises(ValueError): instrument.patch_ratecontrol(rc+anchor.encode())

    def test_header_source_bound_no_fp_evaluation_and_hidden_8bit_bridge(self):
        text = (HERE/'full-history-observer.h').read_text()
        self.assertIn('memcpy(raw, &h->rc->qpm, sizeof raw);', text)
        self.assertNotIn('%f', text)
        self.assertNotIn('x264_emms', text)
        self.assertIn('visibility("hidden")', text)
        self.assertIn('#if BIT_DEPTH == 8\nextern int ffh8_enabled', text)
        self.assertIn('ffh_seen[5]', text)
        self.assertIn('static void ffh_closed(void)', text)
        self.assertNotIn('X264_API', text)
        self.assertNotIn('->rc->qpm', text.split('#else\n#include <openssl/sha.h>')[1])
        # Actual source runtime gates, not just parser fixtures.
        for branch in ('!h->frames.b_have_lowres', 'h->param.rc.b_stat_read',
            'h->param.rc.i_vbv_buffer_size', 'h->param.i_threads != 1',
            'p->prop.quant_offsets', 'h->param.rc.i_aq_mode != 1', 'b_vbv ||'):
            self.assertIn(branch, text)


if __name__ == '__main__':
    unittest.main()
