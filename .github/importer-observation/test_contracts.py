"""Lightweight source/data tests; never compile, render or decode media."""
import ast
import io
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


analysis = load('bounded_analysis', HERE/'analyze.py')
instrument = load('bounded_instrument', HERE/'instrument.py')
ci = load('bounded_ci', HERE/'ci.py')
fixtures = load('public_test_fixtures', HERE.parent/'diagnostics/test_fframes_input.py')


def segment_trace():
    full = fixtures.with_state(fixtures.trace())
    rows = []
    for row in full.splitlines():
        fields = row.split('\t')
        if fields[1] == '33':
            fields[1] = '1'
            rows.append('\t'.join(fields))
    return '\n'.join(rows) + '\n'


def importer_trace(boundary):
    rows = ['identity\t1\t854\t480\t864\t480\t0\t8\t1']
    for index in range(24):
        pts = 768 + index
        rows.append(f'entry\t1\t{index}\t{pts}\t896\t448\t448\t'
                    + '\t'.join(boundary['frames'][pts]))
        rows.append(f'coded\t1\t{index}\t{pts}\t960\t960\t' + 'b'*64 + '\t' + 'c'*64)
    return '\n'.join(rows + ['complete\t1\t24']) + '\n'


class BoundaryTests(unittest.TestCase):
    def parse(self, value):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'public.tsv'
            path.write_text(value)
            return analysis.read_segment(path)

    def test_exact_pts_and_historical_lifetime_remap(self):
        current = self.parse(segment_trace())
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'full.tsv'
            path.write_text(fixtures.with_state(fixtures.trace()))
            historical = analysis.read_segment(path, historical=True)
        self.assertEqual(set(current['frames']), set(range(768, 792)))
        compared = analysis.compare_public(current, historical)
        self.assertTrue(compared['observed_inputs_equal'])
        self.assertTrue(compared['native_payloads_equal'])
        self.assertFalse(compared['complete_semantic_input_equality_established'])

    def test_rejects_wrong_pts_lifetime_missing_call_state_and_duplicate(self):
        good = segment_trace()
        variants = [good.replace('frame\t1\t768\t', 'frame\t1\t0\t'),
                    good.replace('open\t1\t', 'open\t2\t'),
                    '\n'.join(row for row in good.splitlines() if not row.startswith('state\t')),
                    good.replace('close\t1\t24\t24\t25\t1\t1', ''),
                    good + good.splitlines()[3] + '\n',
                    good.replace('pic.img.i_stride[1]=448', 'pic.img.i_stride[1]=426')]
        for value in variants:
            with self.subTest(value=value[:50]):
                with self.assertRaises((AssertionError, ValueError, IndexError)):
                    self.parse(value)

    def test_native_input_header_state_and_pts_mutations_are_detected(self):
        original = self.parse(segment_trace())
        for mutate, key in (
            (lambda b: b['frames'].__setitem__(768, ('f'*64,)*3), 'observed_inputs_equal'),
            (lambda b: b['item']['states'][0].__setitem__('param.cpu', '123'), 'observed_inputs_equal'),
            (lambda b: b['item']['headers'].append((1, 1, 'f'*64, (1,))), 'native_payloads_equal'),
            (lambda b: b['item']['calls'].__setitem__(0, (0, 4, 1, '1', 'd'*64, (4,))), 'native_payloads_equal'),
        ):
            changed = copy.deepcopy(original)
            mutate(changed)
            self.assertFalse(analysis.compare_public(original, changed)[key])

    def test_importer_exact_bounds_order_and_public_binding(self):
        boundary = self.parse(segment_trace())
        text = importer_trace(boundary)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'importer.tsv'
            path.write_text(text)
            good = analysis.read_importer(path, boundary)
            self.assertEqual(len(good['frames']), 24)
            for value in (text.replace('854\t480\t864', '854\t480\t896'),
                          text.replace('complete\t1\t24', ''),
                          text.replace('entry\t1\t0\t768', 'entry\t1\t0\t769'),
                          text.replace('\t896\t448\t448', '\t853\t448\t448', 1),
                          text.replace('\t960\t960', '\t863\t960', 1),
                          text.replace('\t960\t960', '\t2147483648\t960', 1),
                          text.replace('\t960\t960', '\t' + '9'*400 + '\t960', 1),
                          text.replace('\t960\t960', '\t 960\t960', 1),
                          text.replace('\t960\t960', '\t0960\t960', 1),
                          text.replace('\t480\t0\t8\t1', '\t480\t4294967296\t8\t1'),
                          text.replace('a'*64, 'f'*64, 1)):
                path.write_text(value)
                with self.assertRaises((AssertionError, ValueError)):
                    analysis.read_importer(path, boundary)
            changed = copy.deepcopy(good)
            changed['frames'][768]['coded'][0] = 'd'*64
            self.assertEqual(analysis.compare_importer(good, changed),
                             {'entry_changed_pts': [], 'coded_changed_pts': [768]})


class SourceContracts(unittest.TestCase):
    def test_patch_rejects_missing_or_repeated_anchors_and_preserves_source(self):
        value = (instrument.INCLUDE + '\n' + instrument.ENTRY + '            return -1;\n'
                 + '        x264_frame_expand_border_mod16( h, fenc );\n' + instrument.CODED).encode()
        changed = instrument.patch_encoder(value)
        self.assertLess(changed.index(b'fframes_import_entry( h'), changed.index(instrument.ENTRY.encode()))
        self.assertLess(changed.index(b'x264_frame_expand_border_mod16'), changed.index(b'fframes_import_coded( h'))
        self.assertLess(changed.index(b'fframes_import_coded( h'), changed.index(instrument.CODED.encode()))
        for bad in (value.replace(instrument.ENTRY.encode(), b''), value + instrument.ENTRY.encode()):
            with self.assertRaises(ValueError):
                instrument.patch_encoder(bad)

    def test_hash_mismatch_does_not_mutate_source(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp)
            for name in instrument.SOURCE_HASHES:
                path = source/name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'not pinned source')
            with self.assertRaises(ValueError):
                instrument.instrument(source, HERE/'importer-observer.h')
            self.assertFalse((source/'encoder/importer-observer.h').exists())
            self.assertEqual((source/'encoder/encoder.c').read_bytes(), b'not pinned source')

    def test_adapter_preserves_scheduler_and_no_general_render_path(self):
        text = (HERE/'segment.rs').read_text()
        self.assertIn('const TIMELINE_FRAMES: usize = 1080;', text)
        self.assertIn('const SEGMENT: usize = 768;', text)
        self.assertIn('frame_range: None', text)
        self.assertIn('CpuRenderingBackend::default()', text)
        self.assertIn('CpuEncoderFrameRenderer::new', text)
        self.assertLess(text.index('if claim.segment != SEGMENT'), text.index('let svg = render_frame_guarded'))
        self.assertIn('.submit_frame(claim, frame)', text)
        self.assertNotIn('concat_video_files_with_audio(', text)
        self.assertNotIn('set_var(', text)
        self.assertNotIn('remove_', text)

    def test_native_hooks_read_fixed_rectangles_only(self):
        text = (HERE/'importer-observer.h').read_text()
        self.assertIn('864, 480, hashes[0]', text)
        self.assertIn('864, 240, hashes[1]', text)
        self.assertIn('p ? 427 : 854, p ? 240 : 480', text)
        self.assertIn('O_EXCL | O_NOFOLLOW', text)
        self.assertNotIn('memset(', text)
        self.assertNotIn('memcpy(', text)
        self.assertNotIn('->prop.', text)

    def test_experiment_stops_after_smoke_or_film_transparency_and_no_reproduction(self):
        # Execute the actual driver branching with all native calls mocked out.
        boundary = BoundaryTests().parse(segment_trace())
        internal = {'frames': {pts: {'entry': ['a']*3, 'coded': ['b']*2}
                               for pts in range(768, 792)}}
        operand = {'boundary': boundary, 'decoded': ['same'], 'media_sha256': 'same',
                   'importer': internal}
        corrupt = copy.deepcopy(operand)
        corrupt['decoded'] = ['changed']
        for stop_at, expected_count, status in ((1, 2, 'transparency_failure'),
                (4, 5, 'transparency_failure'), (None, 6, 'no_reproduction')):
            with self.subTest(stop_at=stop_at), tempfile.TemporaryDirectory() as temp:
                folder = Path(temp)
                (folder/'frozen-inputs.json').write_text('{}')
                values = [corrupt if index == stop_at else operand for index in range(6)]
                with patch.object(ci, 'load_checker', return_value=analysis), \
                     patch.object(ci, 'run_operand', side_effect=values) as run, \
                     patch.object(analysis, 'read_segment', return_value=boundary):
                    if stop_at is None:
                        ci.experiment(folder, folder, folder)
                    else:
                        with self.assertRaises(RuntimeError):
                            ci.experiment(folder, folder, folder)
                self.assertEqual(run.call_count, expected_count)
                self.assertEqual(json.loads((folder/'verdict.json').read_text())['status'], status)

    def test_historical_observed_input_mismatch_stops_before_third_lifetime(self):
        boundary = BoundaryTests().parse(segment_trace())
        historical = copy.deepcopy(boundary)
        historical['item']['states'][0]['param.cpu'] = '123'
        operand = {'boundary': boundary, 'decoded': ['same'], 'media_sha256': 'same',
                   'importer': {'frames': {pts: {'entry': ['a']*3, 'coded': ['b']*2}
                                          for pts in range(768, 792)}}}
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            with patch.object(ci, 'load_checker', return_value=analysis), \
                 patch.object(ci, 'run_operand', return_value=operand) as run, \
                 patch.object(analysis, 'read_segment', return_value=historical):
                with self.assertRaises(RuntimeError):
                    ci.experiment(folder, folder, folder)
            self.assertEqual(run.call_count, 5)
            self.assertEqual(json.loads((folder/'verdict.json').read_text())['status'],
                             'historical_observed_input_mismatch')

    def test_fetch_keeps_bearer_off_redirect_and_rejects_wrong_hash(self):
        # Exercise the actual fetch function with no sockets or real credential.
        redirect = ci.urllib.error.HTTPError('https://api.github.com/fixture', 302, 'redirect',
                                             {'Location': 'https://storage.example/operand'}, None)
        opener = unittest.mock.Mock()
        opener.open.side_effect = redirect
        data = b'bounded saved operand'
        token = 'synthetic-test-token'
        expected = hashlib.sha256(data).hexdigest()
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'operand.zip'
            with patch.object(ci.urllib.request, 'build_opener', return_value=opener), \
                 patch.object(ci.urllib.request, 'urlopen', return_value=io.BytesIO(data)) as get:
                ci.fetch('https://api.github.com/fixture', path, expected, token)
            request = opener.open.call_args.args[0]
            self.assertEqual(request.get_header('Authorization'), 'Bearer '+token)
            self.assertEqual(get.call_args.args, ('https://storage.example/operand',))
            self.assertEqual(path.read_bytes(), data)
            wrong = Path(temp)/'mismatch.zip'
            with patch.object(ci.urllib.request, 'urlopen', return_value=io.BytesIO(data)):
                with self.assertRaises(RuntimeError):
                    ci.fetch('https://storage.example/operand', wrong, '0'*64)
            self.assertEqual(wrong.read_bytes(), data)

    def test_prepare_hash_failure_retains_offending_archive_in_uploaded_tree(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output, work = root/'evidence', root/'work'
            def fake_fetch(url, path, expected, token=None):
                path.write_bytes(b'offending historical bytes' if token else b'x264 source')
                if token:
                    raise RuntimeError('Downloaded operand hash mismatch')
            with patch.object(ci.subprocess, 'check_output', return_value=ci.FROZEN+'\n'), \
                 patch.object(ci, 'capacity'), patch.object(ci, 'fetch', side_effect=fake_fetch), \
                 patch.dict(ci.os.environ, {'GITHUB_SHA': 'a'*40, 'GITHUB_RUN_ID': '1', 'GH_TOKEN': 'synthetic'}):
                with self.assertRaises(RuntimeError):
                    ci.prepare(root, output, work)
            self.assertEqual((output/'historical-witness.zip').read_bytes(), b'offending historical bytes')
            self.assertFalse((work/'historical-witness.zip').exists())

    def test_workflow_is_separate_exact_source_single_push_no_export(self):
        text = (ROOT/'.github/workflows/observe-fframes-importer.yml').read_text()
        self.assertIn('ref: ' + ci.FROZEN, text)
        self.assertIn('ref: ${{ github.sha }}', text)
        self.assertIn("if: always()", text)
        self.assertIn("FFRAMES_NUM_THREADS: '1'", text)
        self.assertNotIn('.github/diagnostics/**', text)
        self.assertNotIn('workflow_dispatch', text)
        self.assertNotIn('export-styles.sh', text)
        self.assertNotIn('actions/cache', text)
        self.assertNotIn('setup-rust', text)
        self.assertLess(text.index('ci.py prepare'), text.index('sudo apt-get'))
        self.assertLess(text.index('ci.py build'), text.index('ci.py experiment'))

    def test_python_syntax_and_fail_closed_transparency(self):
        for path in HERE.glob('*.py'):
            ast.parse(path.read_text(), filename=str(path))
        boundary = BoundaryTests().parse(segment_trace())
        operand = {'boundary': boundary, 'decoded': ['row'], 'media_sha256': 'a'}
        self.assertTrue(ci.transparency(operand, operand, analysis)['passed'])
        altered = copy.deepcopy(operand)
        altered['decoded'] = ['corrupt']
        self.assertFalse(ci.transparency(operand, altered, analysis)['passed'])
        source = (HERE/'ci.py').read_text()
        self.assertIn("'maximum_film_lifetimes': 3", source)
        self.assertIn("'status': 'transparency_failure'", source)
        self.assertIn("'no_reproduction'", source)
        self.assertNotIn('rmtree(', source)
        self.assertNotIn('export-styles', source)


if __name__ == '__main__':
    unittest.main()
