"""Lightweight evaluator contracts; no compiler or media process is invoked."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

PATH = Path(__file__).with_name('benchmark.py')
spec = importlib.util.spec_from_file_location('export_benchmark', PATH)
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class EvaluatorTests(unittest.TestCase):
    def test_all_declared_mutations_are_rejected_by_real_checker(self):
        self.assertEqual(benchmark.negative_controls(),
                         {name: True for name in benchmark.POLICY['negative_controls']})

    def test_worktree_root_is_not_renderer_parent(self):
        root = Path('/tmp/example-worktree')
        self.assertEqual((root/benchmark.REL).parents[2], root)

    def test_run_export_measures_child_and_rejects_nonzero_exit(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            script = root/'export-styles.sh'
            script.write_text('printf "fixture only\\n"\n')
            sample = benchmark.run_export(root, root/'target', root/'pass.log')
            self.assertGreater(sample['seconds'], 0)
            self.assertGreater(sample['peak_rss_bytes'], 0)
            script.write_text('exit 17\n')
            with self.assertRaisesRegex(AssertionError, '17'):
                benchmark.run_export(root, root/'target', root/'fail.log')

    def test_profile_uses_timestamped_trace_but_plain_run_does_not(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root/'export-styles.sh').write_text('printf "first\\n"\nprintf "second\\n"\n')
            benchmark.run_export(root, root/'target', root/'plain.log')
            benchmark.run_export(root, root/'target', root/'profile.log', profile=True)
            self.assertEqual((root/'plain.log').read_text(), 'first\nsecond\n')
            import re
            stamps = re.findall(r'^\+(\d+\.\d+) export-styles.sh:',
                                (root/'profile.log').read_text(), re.MULTILINE)
            self.assertEqual(len(stamps), 2)
            self.assertGreater(float(stamps[1]), float(stamps[0]))

    def check_receipt_publication(self, failure=None):
        """Synthetic control flow only; Git, exports and media checks are stubbed."""
        sha = '1' * 40
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)/'repo'
            (repo/benchmark.REL).mkdir(parents=True)
            evidence = repo/'evidence'
            expected = json.loads((benchmark.HERE/'workflow-inputs.json').read_text())

            def fake_git(command, **kwargs):
                if command[:3] == ['git', 'worktree', 'add']:
                    (Path(command[-2])/benchmark.REL).mkdir(parents=True)
                elif command[:3] == ['git', 'worktree', 'remove'] and failure == 'cleanup':
                    raise RuntimeError('injected cleanup failure')

            def fake_copy(*args, **kwargs):
                if failure == 'copy':
                    raise OSError('injected copy failure')

            argv = ['benchmark.py', '--repo', str(repo), '--control', sha,
                    '--evidence', str(evidence)]
            with patch.object(benchmark.sys, 'argv', argv), \
                    patch.dict(benchmark.os.environ, {'GITHUB_ACTIONS': 'true',
                        'GITHUB_SHA': sha, 'RUNNER_TEMP': temp}), \
                    patch.object(benchmark.subprocess, 'check_output', return_value=sha), \
                    patch.object(benchmark.subprocess, 'run', side_effect=fake_git), \
                    patch.object(benchmark, 'check_sources'), \
                    patch.object(benchmark, 'negative_controls', return_value={
                        name: True for name in benchmark.POLICY['negative_controls']}), \
                    patch.object(benchmark, 'digest', side_effect=lambda path:
                        expected['.github/workflows/render-fframes-film.yml']
                        if path.name == 'render-fframes-film.yml' else '0' * 64), \
                    patch.object(benchmark, 'run_export', return_value={
                        'seconds': 1.0, 'peak_rss_bytes': 1024}), \
                    patch.object(benchmark, 'inspect', return_value={'synthetic': 'not media'}), \
                    patch.object(benchmark.shutil, 'copytree', side_effect=fake_copy), \
                    contextlib.redirect_stdout(io.StringIO()):
                if failure:
                    with self.assertRaisesRegex((OSError, RuntimeError), f'injected {failure}'):
                        benchmark.main()
                    self.assertFalse(list(evidence.glob('*-quality.json')))
                    self.assertFalse(list(evidence.glob('*-measurement.json')))
                else:
                    benchmark.main()
                    self.assertTrue((evidence/'control-quality.json').is_file())
                    self.assertTrue((evidence/'control-measurement.json').is_file())
                if failure == 'copy':
                    self.assertEqual(json.loads((evidence/'failure.json').read_text())['metric'], None)

    def test_copy_failure_does_not_publish_passed_receipts(self):
        self.check_receipt_publication('copy')

    def test_cleanup_failure_does_not_publish_passed_receipts(self):
        self.check_receipt_publication('cleanup')

    def test_success_publishes_receipts_after_collection_and_cleanup(self):
        self.check_receipt_publication()


if __name__ == '__main__':
    unittest.main()
