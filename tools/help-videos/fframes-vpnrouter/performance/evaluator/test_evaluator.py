"""Lightweight evaluator contracts; no compiler or media process is invoked."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

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


if __name__ == '__main__':
    unittest.main()
