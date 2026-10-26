"""Lightweight contracts; no compilation, rendering or media decoding here."""
import ast
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location('input_analysis', HERE/'analyze_fframes_input.py')
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)
spec = importlib.util.spec_from_file_location('workflow_contract',
    ROOT/'tools/help-videos/fframes-vpnrouter/performance/evaluators/warm-export-v2/test_workflow.py')
workflow_contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow_contract)


def state_row(encoder, call, flush, updates=None):
    values = {'fp.mxcsr_control': '8064', 'fp.x87_control': '895'}
    for name, kind in analysis.PARAM_FIELDS.items():
        values[name] = 'a'*64 if kind == 'CQM' else '00000000' if kind == 'FLOAT' else '0'
    values.update({'param.i_width': '854', 'param.i_height': '480', 'param.i_csp': '2',
                   'param.i_threads': '1'})
    if not flush:
        values.update({'pic.' + name: '0' for name in analysis.PICTURE_FIELDS})
        values.update({'pic.img.i_csp': '2', 'pic.img.i_plane': '3',
                       'pic.img.i_stride[0]': '896', 'pic.img.i_stride[1]': '448',
                       'pic.img.i_stride[2]': '448'})
    values.update(updates or {})
    return f'state\t{encoder}\t{call}\t{flush}\t' + '\t'.join(f'{key}={value}' for key, value in values.items())


def with_state(value, changes=None):
    rows = []
    calls = {}
    for row in value.splitlines():
        fields = row.split('\t')
        kind, encoder = fields[:2]
        if kind == 'open':
            calls[encoder] = 0
        if kind == 'frame' or kind == 'output' and fields[3] == '1':
            call = calls[encoder]
            rows.append(state_row(encoder, call, int(kind == 'output'),
                                  (changes or {}).get((int(encoder), call))))
        rows.append(row)
        if kind == 'output':
            calls[encoder] += 1
    return '\n'.join(rows) + '\n'


def trace(changed=None, output_changed=None, header_changed=None):
    rows = []
    for segment in range(45):
        encoder = segment + 1
        rows.append(f'open\t{encoder}\t854\t480\t2\t1\t1\t0\t1')
        header = 'e' * 64 if segment == header_changed else 'c' * 64
        rows.append(f'headers\t{encoder}\t3\t1\t{header}\t3')
        for call in range(24):
            index = segment*24 + call
            digest = 'b' * 64 if index == changed else 'a' * 64
            rows.append(f'frame\t{encoder}\t{index}\t{digest}\t{digest}\t{digest}')
            native = 'f' * 64 if index == output_changed else 'd' * 64
            rows.append(f'output\t{encoder}\t{call}\t0\t4\t1\t{index}\t{native}\t4')
        rows.append(f'output\t{encoder}\t24\t1\t0\t0\t-\t{analysis.EMPTY_SHA256}')
        rows.append(f'close\t{encoder}\t24\t24\t25\t1\t1')
    return '\n'.join(rows) + '\n'


class TraceTests(unittest.TestCase):
    def test_complete_trace(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'trace.tsv'
            path.write_text(trace())
            frames, settings = analysis.read_trace(path)
            self.assertEqual(len(frames), 1080)
            self.assertEqual(settings, [(1, 1, 0, 1)])

    def test_rejects_partial_duplicate_invalid_or_unopened_frames(self):
        good = trace()
        invalid = (
            good.rsplit('frame', 1)[0],
            good + good.splitlines()[2] + '\n',
            good.replace('\t1079\t', '\t1080\t'),
            '\n'.join(good.splitlines()[1:]),
            good.replace('\t854\t480\t', '\t1920\t1080\t'),
            good.replace('a' * 64, 'invalid'),
            good.replace('close\t45\t24\t24\t25\t1\t1\n', ''),
            good.replace('close\t1\t24\t24\t25\t1\t1', 'close\t1\t24\t23\t25\t1\t1'),
            good.replace('output\t1\t0\t0\t4\t1\t0', 'output\t1\t1\t0\t4\t1\t0'),
            good.replace('output\t1\t0\t0\t4\t1\t0', 'output\t1\t0\t0\t-1\t1\t0'),
            good.replace('output\t1\t1\t0\t4\t1\t1', 'output\t1\t1\t0\t4\t1\t0'),
            good.replace('\t4\t1\t0\t' + 'd'*64 + '\t4', '\t4\t1\t0\t' + 'd'*64 + '\t3'),
            good.replace('\t0\t0\t-\t' + analysis.EMPTY_SHA256, '\t0\t0\t-\t' + 'a'*64),
            good.replace('headers\t1\t3\t1\t' + 'c'*64 + '\t3\n', ''),
            good.replace('output\t1\t24\t1\t0\t0', 'output\t1\t24\t0\t0\t0'),
            good.replace('open\t2\t', 'open\t1\t'),
        )
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'trace.tsv'
            for value in invalid:
                with self.subTest(value=value[:70]):
                    path.write_text(value)
                    with self.assertRaises((AssertionError, ValueError)):
                        analysis.read_trace(path)

    def test_conclusions_require_complete_input_and_decode_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            decoded = {'styles': {style: {'between_batches': {'video': True},
                                  'same_file_redecode': [{'video': True}, {'video': True}],
                                  'frame_rows': [1080, 1080]} for style in analysis.STYLES}}
            decoded['styles']['anime']['between_batches']['video'] = False
            decoded['styles']['swiss']['between_batches']['video'] = False
            (root/'summary.json').write_text(json.dumps(decoded))
            for batch in range(2):
                folder = root/f'batch-{batch}/input-traces'
                folder.mkdir(parents=True)
                for style in analysis.STYLES:
                    changed = 17 if batch == 1 and style in ('anime', 'blueprint') else None
                    (folder/f'{style}-123.tsv').write_text(trace(changed))
            report = analysis.analyze(root)
            self.assertIsNone(report['metric'])
            self.assertEqual(report['styles']['anime']['different_visible_input_frames'], [17])
            self.assertIn('inspect_upstream_input_path', report['styles']['anime']['conclusion'])
            self.assertIn('original_failure_not_localized', report['styles']['blueprint']['conclusion'])
            self.assertIn('inspect_downstream_path', report['styles']['swiss']['conclusion'])
            self.assertIn('no_difference_reproduced', report['styles']['paper']['conclusion'])
            swiss = root/'batch-1/input-traces/swiss-123.tsv'
            swiss.write_text(trace(output_changed=17))
            native_report = analysis.analyze(root)['styles']['swiss']
            self.assertEqual(native_report['different_native_output_frames'], [17])
            self.assertIn('inspect_hidden_inputs_and_encoder_path', native_report['conclusion'])
            swiss.write_text(trace(header_changed=2))
            header_report = analysis.analyze(root)['styles']['swiss']
            self.assertEqual(header_report['different_header_segments'], [2])
            self.assertIn('inspect_hidden_inputs_and_encoder_path', header_report['conclusion'])
            rows = trace().splitlines()
            header = rows.pop(1)
            rows.insert(3, header)  # Move headers across the first completed encode.
            swiss.write_text('\n'.join(rows) + '\n')
            order_report = analysis.analyze(root)['styles']['swiss']
            self.assertEqual(order_report['different_header_segments'], [])
            self.assertEqual(order_report['different_native_output_frames'], [])
            self.assertEqual(order_report['different_native_call_segments'], [0])
            self.assertIn('without_native_payload_divergence', order_report['conclusion'])
            decoded['styles']['swiss']['same_file_redecode'][0]['video'] = False
            (root/'summary.json').write_text(json.dumps(decoded))
            self.assertIn('same_file_video_decode_unstable',
                          analysis.analyze(root)['styles']['swiss']['conclusion'])
            decoded['styles']['swiss']['same_file_redecode'][0]['video'] = 'false'
            (root/'summary.json').write_text(json.dumps(decoded))
            with self.assertRaises(AssertionError):
                analysis.analyze(root)
            decoded['styles']['swiss']['same_file_redecode'][0]['video'] = True
            decoded['styles']['swiss']['frame_rows'] = [1080, 1079]
            (root/'summary.json').write_text(json.dumps(decoded))
            with self.assertRaises(AssertionError):
                analysis.analyze(root)
            decoded['styles']['swiss']['frame_rows'] = [1080, 1080]
            (root/'summary.json').write_text(json.dumps(decoded))
            (root/'batch-1/input-traces/paper-456.tsv').write_text(trace())
            with self.assertRaises(AssertionError):
                analysis.analyze(root)

    def test_delayed_output_flush_reordering_and_exact_lifetimes(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'trace.tsv'
            rows = ['open\t1\t854\t480\t2\t1\t1\t0\t1',
                    'headers\t1\t3\t1\t' + 'c'*64 + '\t3']
            for index in range(3):
                rows += [f'frame\t1\t{index}\t' + '\t'.join(['a'*64]*3),
                         f'output\t1\t{index}\t0\t0\t0\t-\t{analysis.EMPTY_SHA256}']
            for call, pts in enumerate((0, 2, 1), 3):
                rows.append(f'output\t1\t{call}\t1\t4\t1\t{pts}\t' + 'd'*64 + '\t4')
            rows.append('close\t1\t3\t3\t6\t3\t1')
            path.write_text('\n'.join(rows) + '\n')
            value = analysis.read_boundary(path, expected_frames=3, segment_frames=3)
            self.assertEqual(value['segments'][0]['outputs'], [0, 2, 1])
            self.assertEqual(value['segments'][0]['flushes'], 3)
            self.assertEqual(set(value['outputs']), {0, 1, 2})
            # A missing empty-output call must not silently pass complete PTS coverage.
            path.write_text(path.read_text().replace(rows[3] + '\n', ''))
            with self.assertRaises(AssertionError):
                analysis.read_boundary(path, expected_frames=3, segment_frames=3)

    def test_named_state_complete_coverage_and_malformed_fields(self):
        good = with_state(trace())
        first = next(row for row in good.splitlines() if row.startswith('state\t'))
        invalid = (
            trace(), good.replace(first + '\n', '', 1), good + first + '\n',
            good.replace(first, first + '\tparam.i_threads=1', 1),
            good.replace('\tparam.i_threads=1', '', 1),
            good.replace('fp.mxcsr_control=8064', 'fp.mxcsr_control=8065', 1),
            good.replace('param.psz_cqm_file=0', 'param.psz_cqm_file=2', 1),
            good.replace('pic.img.i_stride[1]=448', 'pic.img.i_stride[1]=426', 1),
            good.replace('state\t1\t0\t0', 'state\t1\t1\t0', 1),
            good.replace('state\t1\t24\t1', 'state\t1\t24\t0', 1),
            good.replace('param.analyse.f_psy_rd=00000000', 'param.analyse.f_psy_rd=nan', 1),
            good.replace('param.cqm_4iy=' + 'a'*64, 'param.cqm_4iy=bad', 1),
        )
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'trace.tsv'
            path.write_text(good)
            boundary = analysis.read_boundary(path, require_state=True)
            self.assertEqual(sum(len(item['states']) for item in boundary['segments'].values()), 1125)
            self.assertTrue(boundary['named_state_observed'])
            for value in invalid:
                with self.subTest(value=value[:70]):
                    path.write_text(value)
                    with self.assertRaises((AssertionError, ValueError)):
                        analysis.read_boundary(path, require_state=True)
            path.write_text(trace())
            self.assertFalse(analysis.read_boundary(path)['named_state_observed'])

    def test_named_state_native_scalar_widths(self):
        for field, low, high in (
            ('param.cpu', 0, 2**32-1),
            ('param.i_threads', -2**31, 2**31-1),
            ('param.mastering_display.i_display_max', -2**63, 2**63-1),
            ('pic.i_type', -2**31, 2**31-1),
        ):
            for value in (low-1, low, high, high+1):
                with self.subTest(field=field, value=value):
                    fields = state_row(1, 0, 0, {field: str(value)}).split('\t')[4:]
                    if low <= value <= high:
                        self.assertEqual(analysis.read_state(fields, 0)[field], str(value))
                    else:
                        with self.assertRaises((AssertionError, ValueError)):
                            analysis.read_state(fields, 0)

    def test_named_state_differences_and_optional_payloads_never_prove_input_equality(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            decoded = {'styles': {style: {'between_batches': {'video': False},
                'same_file_redecode': [{'video': True}, {'video': True}],
                'frame_rows': [1080, 1080]} for style in analysis.STYLES}}
            (root/'summary.json').write_text(json.dumps(decoded))
            for batch in range(2):
                folder = root/f'batch-{batch}/input-traces'
                folder.mkdir(parents=True)
                for style in analysis.STYLES:
                    changes = {(1, 0): {'pic.i_qpplus1': '24'}} if batch == 1 and style == 'anime' else {}
                    # Same non-NULL flags cannot stand in for equal pointed-to content.
                    if style == 'blueprint':
                        changes = {(1, 0): {'pic.prop.quant_offsets': '1'}}
                    value = trace(output_changed=17 if batch == 1 else None)
                    (folder/f'{style}-123.tsv').write_text(with_state(value, changes))
            report = analysis.analyze(root, require_state=True)
            anime = report['styles']['anime']
            self.assertIn('named_state_differs', anime['conclusion'])
            self.assertEqual(anime['different_named_state_calls'],
                [{'segment': 0, 'call': 0, 'fields': ['pic.i_qpplus1']}])
            self.assertIn('named_fields_equal', report['styles']['swiss']['conclusion'])
            blueprint = report['styles']['blueprint']
            self.assertEqual(blueprint['optional_payloads_not_dereferenced'], ['pic.prop.quant_offsets'])
            self.assertFalse(blueprint['complete_semantic_input_equality_established'])
            swiss = root/'batch-1/input-traces/swiss-123.tsv'
            rows = with_state(trace()).splitlines()
            header = rows.pop(1)
            rows.insert(4, header)  # Native payloads equal; only call/header order moves.
            swiss.write_text('\n'.join(rows) + '\n')
            order = analysis.analyze(root, require_state=True)['styles']['swiss']
            self.assertEqual(order['different_native_output_frames'], [])
            self.assertEqual(order['different_header_segments'], [])
            self.assertEqual(order['different_named_state_calls'], [])
            self.assertEqual(order['different_native_call_segments'], [0])
            self.assertIn('without_native_payload_divergence', order['conclusion'])
            decoded['styles']['anime']['between_batches']['video'] = True
            (root/'summary.json').write_text(json.dumps(decoded))
            self.assertIn('original_failure_not_localized',
                          analysis.analyze(root, require_state=True)['styles']['anime']['conclusion'])

    def test_real_workflow_pair_order_preload_isolation_and_failure_collection(self):
        source = (ROOT/'.github/workflows/diagnose-fframes-repeatability.yml').read_text()
        blocks = workflow_contract.run_blocks(source)
        body, = [body for block in blocks for body in
                 re.findall(r"(?ms)^ *python3 - <<'PY'\n(.*?)^PY$", block)
                 if 'execution_order =' in body]
        for failure in (None, 'export', 'inspect', 'decode'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                (root/'out/repeatability').mkdir(parents=True)
                calls = []
                def run_export(worktree, target, log):
                    index = int(log.parent.name.split('-')[1])
                    traced = index in (0, 1)
                    self.assertEqual('LD_PRELOAD' in os.environ, traced)
                    self.assertEqual('FFRAMES_INPUT_TRACE' in os.environ, traced)
                    calls.append(index)
                    output = root/'out/styles/preview/anime'
                    output.mkdir(parents=True, exist_ok=True)
                    for name in ('film.mp4', 'film-silent.mp4'):
                        (output/name).write_bytes(b'synthetic-media')
                    log.write_text('synthetic export')
                    if traced:
                        (Path(os.environ['FFRAMES_INPUT_TRACE'])/'anime-123.tsv').write_text(trace())
                    if failure == 'export' and index == 0:
                        raise RuntimeError('injected export failure')
                def inspect(worktree):
                    self.assertNotIn('LD_PRELOAD', os.environ)
                    self.assertNotIn('FFRAMES_INPUT_TRACE', os.environ)
                    if failure == 'inspect' and calls[-1] == 0:
                        raise RuntimeError('injected inspect failure')
                    return {'anime': {'video': 'same-video', 'audio': 'same-audio'}}
                def decode(path, kind):
                    self.assertNotIn('LD_PRELOAD', os.environ)
                    if failure == 'decode' and calls[-1] == 0:
                        raise RuntimeError('injected decode failure')
                    return f'same-{kind}'
                def command(args, **kwargs):
                    self.assertNotIn('LD_PRELOAD', os.environ)
                    return ('\n'.join(str(i) for i in range(1080)) if 'framehash' in args else '{}')
                bench = SimpleNamespace(STYLES=('anime',), run_export=run_export,
                                        inspect=inspect, decoded_signature=decode)
                fake_spec = SimpleNamespace(loader=SimpleNamespace(exec_module=lambda module: None))
                with patch.dict(os.environ, {'TARGET_SHA': 'a'*40, 'RUNNER_TEMP': temp,
                                             'GITHUB_RUN_ID': 'synthetic'}, clear=True), \
                     patch.object(Path, 'cwd', return_value=root), \
                     patch.object(importlib.util, 'spec_from_file_location', return_value=fake_spec), \
                     patch.object(importlib.util, 'module_from_spec', return_value=bench), \
                     patch.object(subprocess, 'check_output', side_effect=command), \
                     patch('builtins.print'):
                    if failure:
                        with self.assertRaisesRegex(RuntimeError, f'injected {failure}'):
                            exec(compile(body, '<workflow>', 'exec'), {})
                    else:
                        exec(compile(body, '<workflow>', 'exec'), {})
                    self.assertNotIn('LD_PRELOAD', os.environ)
                    self.assertNotIn('FFRAMES_INPUT_TRACE', os.environ)
                out = root/'out/repeatability'
                self.assertEqual(calls, [2, 0] if failure else [2, 0, 1, 3])
                for index in calls:
                    self.assertTrue((out/f'batch-{index}/outputs/preview/anime/film.mp4').is_file())
                self.assertEqual((out/'summary.json').exists(), failure is None)
                if not failure:
                    for name, indices, traced in (('summary.json', [0, 1], True),
                                                   ('plain-summary.json', [2, 3], False)):
                        summary = json.loads((out/name).read_text())
                        self.assertEqual(summary['batch_indices'], indices)
                        self.assertEqual(summary['input_probe'], traced)
                        self.assertTrue(summary['styles']['anime']['between_batches']['video'])
                    for index in (2, 3):
                        self.assertFalse(list((out/f'batch-{index}/input-traces').iterdir()))

    def test_actual_extracted_smoke_requires_and_checks_named_state(self):
        source = (ROOT/'.github/workflows/diagnose-fframes-repeatability.yml').read_text()
        blocks = workflow_contract.run_blocks(source)
        body, = [body for block in blocks for body in
                 re.findall(r"(?ms)^ *python3 - <<'PY'\n(.*?)^PY$", block)
                 if "folder = Path('out/repeatability/probe-smoke')" in body]
        for failure in (None, 'missing-state', 'bad-picture', 'bad-nal'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temp:
                folder = Path(temp)/'out/repeatability/probe-smoke'
                folder.mkdir(parents=True)
                rows = ['open\t1\t854\t480\t2\t1\t1\t0\t1',
                        'headers\t1\t1\t1\t' + analysis.hashlib.sha256(b'H').hexdigest() + '\t1']
                for index in range(3):
                    hashes = [analysis.hashlib.sha256(bytes([32+index*20])*854*480).hexdigest()]
                    hashes += [analysis.hashlib.sha256(bytes([128])*427*240).hexdigest()]*2
                    rows += [f'frame\t1\t{index}\t' + '\t'.join(hashes),
                             f'output\t1\t{index}\t0\t1\t1\t{index}\t'
                             + analysis.hashlib.sha256(b'V').hexdigest() + '\t1']
                rows += ['output\t1\t3\t1\t0\t0\t-\t' + analysis.EMPTY_SHA256,
                         'close\t1\t3\t3\t4\t1\t1']
                value = '\n'.join(rows) + '\n'
                if failure != 'missing-state':
                    value = with_state(value, {(1, 0): {'pic.i_type': '1'}} if failure == 'bad-picture' else None)
                (folder/'smoke-123.tsv').write_text(value)
                (folder/'traced.h264').write_bytes(b'HVVX' if failure == 'bad-nal' else b'HVVV')
                cwd = os.getcwd()
                try:
                    os.chdir(temp)
                    with patch.dict(os.environ, {'GITHUB_WORKSPACE': str(ROOT.parent)}), patch('builtins.print'):
                        # Only resolve the extracted smoke's diagnostic-code import.
                        real_spec = importlib.util.spec_from_file_location
                        with patch.object(importlib.util, 'spec_from_file_location',
                            side_effect=lambda name, path: real_spec(name, HERE/'analyze_fframes_input.py')):
                            if failure:
                                with self.assertRaises(AssertionError):
                                    exec(compile(body, '<smoke>', 'exec'), {})
                            else:
                                exec(compile(body, '<smoke>', 'exec'), {})
                finally:
                    os.chdir(cwd)

    def test_probe_uses_stack_sha256_without_evp_heap_allocations(self):
        source = (HERE/'fframes-x264-input.c').read_text()
        self.assertIn('SHA256_CTX ctx;', source)
        for name in ('SHA256_Init', 'SHA256_Update', 'SHA256_Final'):
            self.assertIn(name, source)
        self.assertNotIn('EVP_MD_CTX_new', source)
        self.assertNotIn('EVP_MD_CTX_free', source)

    def test_workflow_shell_python_and_exact_source_contract(self):
        source = (ROOT/'.github/workflows/diagnose-fframes-repeatability.yml').read_text()
        blocks = workflow_contract.run_blocks(source)
        self.assertGreaterEqual(len(blocks), 4)
        for block in blocks:
            result = subprocess.run(['bash', '-n'], input=block, text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
        bodies = [body for block in blocks for body in
                  re.findall(r"(?ms)^ *python3 - <<'PY'\n(.*?)^PY$", block)]
        self.assertEqual(len(bodies), sum(block.count("<<'PY'") for block in blocks))
        for body in bodies:
            ast.parse(body)
        self.assertIn('ref: ac5ac03c4475c32dea13738e5941db00115c8da6', source)
        self.assertIn('ref: ${{ github.sha }}', source)
        self.assertIn("FFRAMES_NUM_THREADS: '1'", source)
        self.assertIn("os.environ.pop('LD_PRELOAD', None)", source)
        self.assertIn("shutil.copytree(root/'out/styles', folder/'outputs')", source)
        self.assertIn('if: always()', source)
        self.assertIn('out/repeatability --require-state', source)
        self.assertIn('segment_frames=3, require_state=True', source)
        self.assertNotIn('cargo run', source)


if __name__ == '__main__':
    unittest.main(verbosity=2)
