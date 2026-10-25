"""Lightweight contracts; no compilation, rendering or media decoding here."""
import ast
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

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
