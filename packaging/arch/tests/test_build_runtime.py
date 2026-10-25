"""No compiler/network: exercise pinned build admission and red/green oracle handling."""
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import build_runtime


class RuntimeSourceBuildTests(unittest.TestCase):
    def exercise(self, *, red_message="Linux gvisor stack closed before TUN", source_commit=None):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            output = work / 'sing-box'
            calls = []
            tests = 0

            def run(args, **kwargs):
                nonlocal tests
                calls.append(args)
                self.assertEqual(kwargs['env']['GOTOOLCHAIN'], 'local')
                self.assertEqual(kwargs['env']['CGO_ENABLED'], '0')
                text = ''
                code = 0
                if args == ['go', 'version']:
                    text = 'go version go1.26.8 linux/amd64'
                elif args == ['git', 'rev-parse', 'HEAD']:
                    text = source_commit or build_runtime.SOURCE_COMMIT
                    (work / 'runtime-source/protocol/tun').mkdir(parents=True)
                elif args[:2] == ['go', 'test']:
                    tests += 1
                    code, text = (1, red_message) if tests == 1 else (0, 'PASS')
                elif args[:2] == ['go', 'build']:
                    output.write_bytes(b'fixture-built-runtime')
                elif args[:3] == ['go', 'version', '-m']:
                    text = 'build -tags='+build_runtime.BUILD_TAGS
                return subprocess.CompletedProcess(args, code, stdout=text, stderr='')

            with mock.patch('build_runtime.subprocess.run', side_effect=run):
                result = build_runtime.build_runtime(work, output)
            self.assertEqual(tests, 2)
            self.assertEqual(result['commit'], build_runtime.SOURCE_COMMIT)
            self.assertEqual(len(result['binary_sha256']), 64)
            self.assertLess(next(i for i,x in enumerate(calls) if x[:2] == ['go','test']),
                            next(i for i,x in enumerate(calls) if x[:3] == ['git','apply','--check']))
            self.assertIn(['git','submodule','update','--init','--recursive','--depth=1'], calls)
            return result

    def test_source_build_keeps_protocol_tags_and_pins(self):
        result = self.exercise()
        for tag in ['with_gvisor','with_awg','with_xhttp','with_v2ray_api','with_naive_outbound']:
            self.assertIn(tag, result['build_tags'].split(','))

    def test_infrastructure_failure_is_not_accepted_as_red_regression(self):
        with self.assertRaisesRegex(RuntimeError, 'did not reproduce'):
            self.exercise(red_message='compiler failed before any test ran')

    def test_source_drift_is_refused_before_patch_or_compiler(self):
        with self.assertRaisesRegex(RuntimeError, 'source commit mismatch'):
            self.exercise(source_commit='a'*40)


if __name__ == '__main__':
    unittest.main()
