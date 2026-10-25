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


def trace(changed=None):
    rows = ['open\t0x1234\t854\t480\t2\t1\t1\t0\t1']
    for index in range(1080):
        digest = 'b' * 64 if index == changed else 'a' * 64
        rows.append(f'frame\t0x1234\t{index}\t{digest}\t{digest}\t{digest}')
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
            good + good.splitlines()[1] + '\n',
            good.replace('\t1079\t', '\t1080\t'),
            '\n'.join(good.splitlines()[1:]),
            good.replace('\t854\t480\t', '\t1920\t1080\t'),
            good.replace('a' * 64, 'invalid'),
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
            self.assertIn('inspect_encoder_or_hidden_frame_state', report['styles']['swiss']['conclusion'])
            self.assertIn('no_difference_reproduced', report['styles']['paper']['conclusion'])
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
        self.assertNotIn('cargo run', source)


if __name__ == '__main__':
    unittest.main(verbosity=2)
