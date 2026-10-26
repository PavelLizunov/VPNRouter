"""Lightweight native-provenance contracts; no native execution or compilation."""
import ast
import importlib.util
import io
import json
import tarfile
from pathlib import Path
import subprocess
import tempfile
import re
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('native_collector', HERE / 'collect.py')
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class CollectorTests(unittest.TestCase):
    def fixture(self, root):
        repo = root / 'repo'
        repo.mkdir()
        for name in ('Cargo.lock', 'Cargo.toml'):
            (repo / name).write_text('fixture')
        source = repo / 'target/release/build/ffmpeg-sys-fframes-abc/out/ffmpeg-9.0'
        source.mkdir(parents=True)
        for name in collector.FFMPEG_FILES:
            path = source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('retained-source')
        registry = root / 'registry'
        crate = registry / 'registry-id/fframes-1.3.0'
        for name in collector.FFRAMES_FILES:
            path = crate / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('crate-source')
        film = repo / 'target/release/vpnrouter-film'
        film.write_bytes(b'not-an-executable')
        self.locked_crate(repo, registry)
        return repo, source, registry

    def locked_crate(self, repo, registry, *, changed=None, missing=None,
                     duplicate=None, special=None, size=None, wrong_checksum=False):
        crate = registry / 'registry-id/fframes-1.3.0'
        archive = registry.parent / 'cache/registry-id/fframes-1.3.0.crate'
        archive.parent.mkdir(parents=True, exist_ok=True)
        names = ('Cargo.toml', 'src/renderer/frame_export.rs',
                 'src/renderer/encoder_frame.rs', 'src/renderer/encoder.rs',
                 'src/renderer/segment_writer.rs', 'src/renderer/stream.rs')
        with tarfile.open(archive, 'w:gz') as stream:
            for name in names:
                if name == missing:
                    continue
                data = (crate / name).read_bytes()
                if name == changed:
                    data += b'changed'
                member = tarfile.TarInfo('fframes-1.3.0/' + name)
                member.size = size if name == special and size is not None else len(data)
                if name == special and size is None:
                    member.type = tarfile.SYMTYPE
                    member.linkname = 'outside'
                payload = b'X' * member.size if name == special and size is not None else data
                stream.addfile(member, io.BytesIO(payload) if member.isfile() else None)
                if name == duplicate:
                    stream.addfile(member, io.BytesIO(data))
        checksum = '0' * 64 if wrong_checksum else collector.digest(archive)
        (repo / 'Cargo.lock').write_text(
            'version = 4\n[[package]]\nname = "fframes"\nversion = "1.3.0"\n'
            'source = "registry+https://github.com/rust-lang/crates.io-index"\n'
            f'checksum = "{checksum}"\n')
        return archive

    def build_evidence(self, repo, source):
        (source.parent.parent / 'output').write_text('cargo-link-data')
        for base, names in ((source, ('libavcodec/libx264.o', 'libavutil/frame.o', 'libavutil/buffer.o')),
                            (source.parent / 'dist/lib', ('libavcodec.a', 'libavutil.a', 'libavformat.a'))):
            for name in names:
                path = base / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'object-bytes')
        fingerprint = repo / 'target/release/.fingerprint/ffmpeg-sys-fframes-abc/lib.json'
        fingerprint.parent.mkdir(parents=True)
        fingerprint.write_text('{}')

    def test_locked_crate_proves_caller_without_generated_checksum_metadata(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            self.build_evidence(repo, source)
            self.locked_crate(repo, registry)
            (registry / 'registry-id/fframes-1.3.0/.cargo-checksum.json').unlink()
            c = collector.Collector(root / 'evidence')
            candidates = collector.collect_cache(c, repo, registry)
            self.assertTrue(collector.evidence_complete(c, candidates))
            self.assertIn('fframes-0/.cargo-checksum.json', c.missing)
            self.assertFalse(c.commands)
            self.assertFalse(candidates[0]['active_binary_binding_established'])
            self.assertTrue(c.callers[0]['locked_archive_matches_sources'])

    def test_locked_crate_rejects_missing_modified_or_ambiguous_members(self):
        name = 'src/renderer/frame_export.rs'
        variants = ({'wrong_checksum': True}, {'changed': name}, {'missing': name},
                    {'duplicate': name}, {'special': name},
                    {'special': name, 'size': collector.MAX_COPY_BYTES + 1})
        for options in variants:
            with self.subTest(options=options), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                repo, source, registry = self.fixture(root)
                self.build_evidence(repo, source)
                self.locked_crate(repo, registry, **options)
                c = collector.Collector(root / 'evidence')
                candidates = collector.collect_cache(c, repo, registry)
                self.assertFalse(collector.evidence_complete(c, candidates))
                self.assertFalse(c.callers[0]['locked_archive_matches_sources'])
                self.assertTrue(c.callers[0]['error'])
                self.assertFalse(c.commands)

    def test_archive_missing_symlink_and_decompression_bound_fail_closed(self):
        for case in ('missing', 'symlink', 'expanded', 'missing-source', 'bad-lock'):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                repo, source, registry = self.fixture(root)
                self.build_evidence(repo, source)
                archive = self.locked_crate(repo, registry)
                if case in ('missing', 'symlink'):
                    data = archive.read_bytes()
                    archive.unlink()
                    if case == 'symlink':
                        outside = root / 'outside.crate'
                        outside.write_bytes(data)
                        archive.symlink_to(outside)
                if case == 'missing-source':
                    (registry / 'registry-id/fframes-1.3.0/src/renderer/frame_export.rs').unlink()
                if case == 'bad-lock':
                    (repo / 'Cargo.lock').write_text('version = [')
                c = collector.Collector(root / 'evidence')
                with patch.object(collector, 'MAX_CRATE_TAR_BYTES', 1 if case == 'expanded' else collector.MAX_CRATE_TAR_BYTES):
                    candidates = collector.collect_cache(c, repo, registry)
                self.assertFalse(collector.evidence_complete(c, candidates))
                self.assertFalse(c.callers[0]['locked_archive_matches_sources'])
                self.assertTrue(c.callers[0]['error'])

    def test_main_accepts_verified_caller_without_fabricating_missing_metadata(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            self.build_evidence(repo, source)
            (registry / 'registry-id/fframes-1.3.0/.cargo-checksum.json').unlink()
            output = root / 'evidence'
            def command(c, arguments, label):
                c.commands.append({'label': label, 'argv': list(map(str, arguments)), 'returncode': 0,
                                   'stdout': collector.SOURCE if label == 'checkout-head' else '',
                                   'stdout_truncated': False, 'stderr': '', 'stderr_truncated': False})
                return SimpleNamespace(returncode=0, stdout=c.commands[-1]['stdout'])
            original = collector.Collector.file
            def file(c, path, base, label, copy=True):
                # System packages are not part of this source-cache fixture.
                if label.startswith('runner/'):
                    return None
                return original(c, path, base, label, copy=copy)
            original_resolve = Path.resolve
            def resolve(path, *args, **kwargs):
                if str(path) == '/usr/lib/x86_64-linux-gnu/libx264.so.164':
                    return root / 'system-library-fixture'
                return original_resolve(path, *args, **kwargs)
            def invoke(output):
                argv = ['collect.py', '--repo', str(repo), '--registry', str(registry), '--output', str(output)]
                with patch('sys.argv', argv), patch.object(collector.Collector, 'command', command), \
                     patch.object(collector.Collector, 'file', file), patch.object(Path, 'resolve', resolve), \
                     patch('builtins.print'):
                    return collector.main()
            code = invoke(output)
            report = json.loads((output / 'manifest.json').read_text())
            self.assertEqual(code, 0, report.get('error', report['status']))
            self.assertTrue(report['required_evidence_complete'])
            self.assertTrue(report['caller_candidates'][0]['locked_archive_matches_sources'])
            self.assertIn('fframes-0/.cargo-checksum.json', report['missing'])
            self.assertFalse((output / 'fframes-0/.cargo-checksum.json').exists())
            self.assertFalse(report['active_binary_binding_established'])
            self.assertFalse(report['cached_code_executed'])
            # A verified caller cannot hide another discovered caller's failure.
            second = registry / 'registry-second/fframes-1.3.0'
            second.mkdir(parents=True)
            for name in collector.REQUIRED_FFRAMES_FILES:
                path = second / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((registry / 'registry-id/fframes-1.3.0' / name).read_bytes())
            archive = registry.parent / 'cache/registry-second/fframes-1.3.0.crate'
            archive.parent.mkdir(parents=True)
            archive.write_bytes((registry.parent / 'cache/registry-id/fframes-1.3.0.crate').read_bytes())
            (second / 'src/renderer/frame_export.rs').write_text('changed caller bytes')
            for case in ('changed-source', 'missing-archive'):
                if case == 'missing-archive':
                    archive.unlink()
                destination = root / case
                self.assertEqual(invoke(destination), 1)
                failed = json.loads((destination / 'manifest.json').read_text())
                self.assertEqual(failed['status'], 'required_evidence_missing')
                self.assertFalse(failed['required_evidence_complete'])
                self.assertEqual([row['locked_archive_matches_sources'] for row in failed['caller_candidates']], [True, False])
                self.assertTrue(failed['caller_candidates'][1]['error'])
                self.assertFalse(failed['cached_code_executed'])

    def test_copies_only_allowlisted_sources_and_hashes_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            (source / 'do-not-copy.txt').write_text('excluded')
            c = collector.Collector(root / 'evidence')
            with patch.object(c, 'command', return_value=SimpleNamespace(returncode=0, stdout='b'*40+'\n')):
                candidates = collector.collect_cache(c, repo, registry)
            self.assertEqual(len(candidates), 1)
            self.assertTrue(candidates[0]['required_sources_present'])
            self.assertFalse(candidates[0]['active_binary_binding_established'])
            self.assertEqual(c.files['ffmpeg-0/source/libavcodec/libx264.c']['sha256'],
                             collector.digest(source / 'libavcodec/libx264.c'))
            self.assertFalse(c.files['renderer/vpnrouter-film']['copied'])
            self.assertFalse((c.output / 'renderer/vpnrouter-film').exists())
            self.assertFalse(list(c.output.rglob('do-not-copy.txt')))
            self.assertIn('fframes-0/src/renderer/frame_export.rs', c.files)

    def test_missing_and_multiple_candidates_never_claim_active_build(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            c = collector.Collector(root / 'evidence')
            # A second cache entry can be recorded, not silently selected as active.
            second = repo / 'target/release/build/ffmpeg-sys-fframes-def/out/ffmpeg-9.0'
            second.mkdir(parents=True)
            with patch.object(c, 'command', return_value=SimpleNamespace(returncode=0, stdout='c'*40)):
                candidates = collector.collect_cache(c, repo, registry)
            self.assertEqual(len(candidates), 2)
            self.assertEqual([row['required_sources_present'] for row in candidates], [True, False])
            self.assertTrue(all(not row['active_binary_binding_established'] for row in candidates))
            self.assertIn('ffmpeg-1/source/libavcodec/libx264.c', c.missing)

    def test_path_escape_symlinks_and_oversized_copy_fail(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            c = collector.Collector(root / 'evidence')
            outside = root / 'outside'
            outside.write_bytes(b'outside')
            link = source / 'linked.c'
            link.symlink_to(outside)
            directory = repo / 'linked-directory'
            directory.symlink_to(source, target_is_directory=True)
            for path, label in ((outside, 'outside'), (link, 'link'),
                                (directory / 'config.h', 'directory-link')):
                with self.subTest(label=label), self.assertRaises((AssertionError, ValueError)):
                    c.file(path, repo, label)
            with self.assertRaises(AssertionError):
                c.file(source / 'config.h', repo, '../escape')
            with patch.object(collector, 'MAX_COPY_BYTES', 1), self.assertRaises(AssertionError):
                c.file(source / 'config.h', repo, 'oversized')

    def test_command_failure_and_collection_failure_preserve_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            output = root / 'evidence'
            argv = ['collect.py', '--repo', str(repo), '--registry', str(registry), '--output', str(output)]
            failure = SimpleNamespace(returncode=7)
            with patch('sys.argv', argv), patch.object(subprocess, 'run', return_value=failure), patch('builtins.print'):
                self.assertEqual(collector.main(), 1)
            report = json.loads((output / 'manifest.json').read_text())
            self.assertEqual(report['status'], 'collection_failed')
            self.assertEqual(report['commands'][0]['returncode'], 7)
            self.assertFalse(report['cached_code_executed'])
            self.assertIsNone(report['metric'])
            self.assertFalse(report['accepted_baseline'])

    def test_input_change_and_existing_output_fail_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            c = collector.Collector(root / 'evidence')
            with self.assertRaises(FileExistsError):
                collector.Collector(root / 'evidence')
            with patch.object(collector, 'digest', side_effect=['a'*64, 'b'*64]), self.assertRaises(AssertionError):
                c.file(source / 'config.h', repo, 'changed')

    def test_missing_caller_build_and_renderer_prevent_complete_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            build = source.parent.parent
            (build / 'output').write_text('cargo-link-data')
            for base, names in ((source, ('libavcodec/libx264.o', 'libavutil/frame.o', 'libavutil/buffer.o')),
                                (source.parent / 'dist/lib', ('libavcodec.a', 'libavutil.a', 'libavformat.a'))):
                for name in names:
                    path = base / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(b'object-bytes')
            fingerprint = repo / 'target/release/.fingerprint/ffmpeg-sys-fframes-abc/lib.json'
            fingerprint.parent.mkdir(parents=True)
            fingerprint.write_text('{}')
            for missing in (None, 'caller', 'object', 'renderer', 'fingerprint'):
                with self.subTest(missing=missing):
                    c = collector.Collector(root / ('evidence-' + str(missing)))
                    with patch.object(c, 'command', return_value=SimpleNamespace(returncode=0, stdout='b'*40)):
                        candidates = collector.collect_cache(c, repo, registry if missing != 'caller' else root / 'absent')
                    if missing == 'object':
                        candidates[0]['required_build_evidence_present'] = False
                    if missing == 'renderer':
                        c.files.pop('renderer/vpnrouter-film')
                    if missing == 'fingerprint':
                        c.files = {key: value for key, value in c.files.items() if not key.startswith('cargo-fingerprints/')}
                    self.assertEqual(collector.evidence_complete(c, candidates), missing is None)
                    if missing == 'caller':
                        self.assertIn('fframes/required-caller-sources', c.missing)

    def test_native_git_does_not_discover_parent_or_execute_cached_filters(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            repo, source, registry = self.fixture(root)
            def git(folder, *arguments):
                return subprocess.run(['git', '-C', str(folder), *arguments], check=True,
                                      text=True, capture_output=True).stdout
            git(repo, 'init', '-q')
            git(repo, '-c', 'user.name=fixture', '-c', 'user.email=fixture@example.invalid',
                'commit', '--allow-empty', '-qm', 'parent fixture')
            c = collector.Collector(root / 'parent-evidence')
            self.assertIsNone(collector.collect_cache(c, repo, registry)[0]['git_head'])
            self.assertFalse(c.commands)  # No source Git metadata means no Git commands.
            git(source, 'init', '-q')
            git(source, 'add', 'config.h')
            git(source, '-c', 'user.name=fixture', '-c', 'user.email=fixture@example.invalid',
                'commit', '-qm', 'source fixture')
            expected = git(source, 'rev-parse', 'HEAD').strip()
            marker = root / 'cached-helper-ran'
            git(source, 'config', 'filter.fixture.clean', f'touch {marker}; cat')
            git(source, 'config', 'core.fsmonitor', f'touch {marker}')
            (source / '.gitattributes').write_text('config.h filter=fixture\n')
            original = (source / 'config.h').read_text()
            (source / 'config.h').write_text('X' + original[1:])
            c = collector.Collector(root / 'native-evidence')
            candidates = collector.collect_cache(c, repo, registry)
            self.assertEqual(candidates[0]['git_head'], expected)
            self.assertFalse(marker.exists())
            self.assertEqual(len(c.commands), 1)
            self.assertNotIn('status', c.commands[0]['argv'])

    def test_command_timeout_keeps_receipt_and_bounds_captured_output(self):
        with tempfile.TemporaryDirectory() as temp:
            c = collector.Collector(Path(temp) / 'evidence')
            def timeout(arguments, stdout, stderr, **kwargs):
                stdout.write(b'partial')
                raise subprocess.TimeoutExpired(arguments, kwargs['timeout'])
            with patch.object(subprocess, 'run', side_effect=timeout):
                with self.assertRaises(subprocess.TimeoutExpired):
                    c.command(['readelf', '-n', 'fixture'], 'timeout')
            self.assertIsNone(c.commands[0]['returncode'])
            self.assertEqual(c.commands[0]['stdout'], 'partial')
            self.assertIn('error', c.commands[0])
            def large(arguments, stdout, stderr, **kwargs):
                stdout.write(b'0123456789')
                return SimpleNamespace(returncode=0)
            with patch.object(collector, 'MAX_COPY_BYTES', 4), patch.object(subprocess, 'run', side_effect=large):
                c.command(['readelf', '-n', 'fixture'], 'large')
            self.assertEqual(c.commands[-1]['stdout'], '0123')
            self.assertTrue(c.commands[-1]['stdout_truncated'])

    def test_workflow_exact_source_read_only_cache_and_shell(self):
        # Reuse the frozen workflow's actual YAML block extractor, not a second parser.
        repo = HERE.parents[1]
        path = repo / 'tools/help-videos/fframes-vpnrouter/performance/evaluators/warm-export-v2/test_workflow.py'
        spec = importlib.util.spec_from_file_location('workflow_contract', path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        source = (repo / '.github/workflows/inspect-fframes-native.yml').read_text()
        for block in module.run_blocks(source):
            result = subprocess.run(['bash', '-n'], input=block, text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            bodies = re.findall(r"(?ms)^ *python3 - <<'PY'\n(.*?)^PY$", block)
            self.assertEqual(len(bodies), block.count("<<'PY'"))
            for body in bodies:
                ast.parse(body)
        self.assertIn('ref: ' + collector.SOURCE, source)
        self.assertIn('ref: ${{ github.sha }}', source)
        self.assertIn('actions/cache/restore@0057852bfaa89a56745cba8c7296529d2fc39830', source)
        self.assertIn('if: always()', source)
        self.assertIn('include-hidden-files: true', source)
        for command in ('cargo build', 'cargo test', 'cargo run', 'export-styles.sh', 'LD_PRELOAD', 'ldd '):
            self.assertNotIn(command, source)
        ast.parse((HERE / 'collect.py').read_text())


if __name__ == '__main__':
    unittest.main(verbosity=2)
