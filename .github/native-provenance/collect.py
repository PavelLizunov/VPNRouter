"""Collect cached native source/build evidence without executing cached code."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

SOURCE = 'ac5ac03c4475c32dea13738e5941db00115c8da6'
FFMPEG_FILES = (
    'libavcodec/libx264.c', 'libavcodec/encode.c', 'libavcodec/internal.h',
    'libavutil/frame.c', 'libavutil/frame.h', 'libavutil/buffer.c',
    'libavutil/buffer.h', 'libavutil/imgutils.c', 'libavutil/imgutils.h',
    'libavutil/mem.c', 'config.h', 'config_components.h', 'ffbuild/config.mak',
    'libavcodec/libx264.d',
)
FFRAMES_FILES = (
    'Cargo.toml', '.cargo-checksum.json', 'src/renderer/frame_export.rs',
    'src/renderer/encoder_frame.rs', 'src/renderer/encoder.rs',
    'src/renderer/segment_writer.rs', 'src/renderer/stream.rs',
)
MAX_COPY_BYTES = 8 * 1024**2
REQUIRED_FFMPEG_FILES = ('libavcodec/libx264.c', 'libavcodec/encode.c',
    'libavutil/frame.c', 'libavutil/frame.h', 'libavutil/buffer.c',
    'libavutil/buffer.h', 'libavutil/imgutils.c', 'libavutil/mem.c',
    'config.h', 'ffbuild/config.mak')


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024**2), b''):
            value.update(block)
    return value.hexdigest()


def confined(path, root):
    """Reject escaping paths and symlinks, including intermediate components."""
    root = root.resolve(strict=True)
    path = path.absolute()
    relative = path.relative_to(root)
    assert '..' not in relative.parts
    current = root
    for part in relative.parts:
        current /= part
        assert not current.is_symlink(), f'Symlink in evidence input: {path}'
    assert path.resolve(strict=True).is_relative_to(root), f'Escaping input: {path}'
    return path


class Collector:
    def __init__(self, output):
        self.output = output.absolute()
        self.output.mkdir(parents=True, exist_ok=False)
        self.files = {}
        self.missing = []
        self.commands = []

    def file(self, path, root, label, copy=True):
        assert label not in self.files, f'Duplicate evidence label: {label}'
        target = self.output / label
        assert not Path(label).is_absolute() and '..' not in Path(label).parts
        assert target.absolute().is_relative_to(self.output)
        if not path.exists() and not path.is_symlink():
            self.missing.append(label)
            return None
        path = confined(path, root)
        assert path.is_file(), f'Not a regular file: {path}'
        size = path.stat().st_size
        assert not copy or size <= MAX_COPY_BYTES, f'Oversized copied input: {label}'
        sha = digest(path)
        if copy:
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open('xb') as stream, path.open('rb') as source:
                shutil.copyfileobj(source, stream)
            assert digest(target) == sha, f'Copy changed: {label}'
        assert digest(path) == sha and path.stat().st_size == size, f'Input changed during collection: {label}'
        self.files[label] = {'sha256': sha, 'bytes': size, 'copied': copy}
        return self.files[label]

    def command(self, arguments, label):
        """Only the caller's fixed git/dpkg/readelf commands, never cached binaries."""
        receipt = {'label': label, 'argv': list(map(str, arguments)), 'returncode': None}
        self.commands.append(receipt)
        # Bound resident output; timeouts/errors retain the command and partial output.
        with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
            try:
                result = subprocess.run(arguments, stdout=stdout, stderr=stderr, timeout=30)
                receipt['returncode'] = result.returncode
            except (OSError, subprocess.SubprocessError) as error:
                receipt['error'] = str(error)
                raise
            finally:
                for name, stream in (('stdout', stdout), ('stderr', stderr)):
                    size = stream.tell()
                    stream.seek(0)
                    receipt[name] = stream.read(MAX_COPY_BYTES).decode('utf-8', errors='replace')
                    receipt[name + '_truncated'] = size > MAX_COPY_BYTES
        return subprocess.CompletedProcess(arguments, result.returncode,
                                           receipt['stdout'], receipt['stderr'])


def collect_cache(collector, repo, registry):
    repo = repo.resolve(strict=True)
    target = repo / 'target'
    collector.file(repo / 'Cargo.lock', repo, 'checkout/Cargo.lock')
    collector.file(repo / 'Cargo.toml', repo, 'checkout/Cargo.toml')
    # No scanning of unrelated target trees or generic environment/config files.
    sources = sorted(target.glob('release/build/ffmpeg-sys-fframes-*/out/ffmpeg-9.0'))
    assert len(sources) <= 8, 'Too many cached FFmpeg source candidates'
    candidates = []
    for index, source in enumerate(sources):
        confined(source, repo)
        assert source.is_dir()
        prefix = f'ffmpeg-{index}'
        build = source.parent.parent
        copied = {}
        for name in FFMPEG_FILES:
            copied[name] = collector.file(source / name, repo, f'{prefix}/source/{name}')
        cargo = {}
        for name in ('output', 'root-output', 'stderr'):
            cargo[name] = collector.file(build / name, repo, f'{prefix}/cargo/{name}')
        objects = []
        for name in ('libavcodec/libx264.o', 'libavutil/frame.o', 'libavutil/buffer.o'):
            objects.append(collector.file(source / name, repo, f'{prefix}/objects/{name}', copy=False))
        libraries = []
        for name in ('libavcodec.a', 'libavutil.a', 'libavformat.a'):
            libraries.append(collector.file(source.parent / 'dist/lib' / name, repo,
                                            f'{prefix}/libraries/{name}', copy=False))
        # Never let Git discover the enclosing project as the native source root.
        metadata = source / '.git'
        revision = None
        if metadata.is_dir():
            confined(metadata, repo)
            git = ['git', '--no-optional-locks', '-c', 'core.fsmonitor=false',
                   '-c', f'safe.directory={source}', '--git-dir', str(metadata),
                   '--work-tree', str(source)]
            revision = collector.command(git + ['rev-parse', 'HEAD'], f'{prefix}/git-head')
            # No status/diff: cached attributes can execute clean filters.
        candidates.append({'cache_relative_path': str(source.relative_to(repo)),
            'git_head': revision.stdout.strip() if revision and revision.returncode == 0 else None,
            'worktree_cleanliness_verified': False,
            'required_sources_present': all(copied[name] for name in REQUIRED_FFMPEG_FILES),
            'required_build_evidence_present': bool(cargo['output']) and all(objects) and all(libraries),
            'active_binary_binding_established': False})
    collector.file(target / 'release/vpnrouter-film', repo, 'renderer/vpnrouter-film', copy=False)
    fingerprints = sorted(target.glob('release/.fingerprint/ffmpeg-sys-fframes-*/*.json'))
    assert len(fingerprints) <= 32, 'Too many Cargo fingerprints'
    if not fingerprints:
        collector.missing.append('cargo-fingerprints/any')
    for index, path in enumerate(fingerprints):
        collector.file(path, repo, f'cargo-fingerprints/{index}-{path.name}')
    registry_sources = sorted(registry.glob('*/fframes-1.3.0'))
    assert len(registry_sources) <= 4, 'Too many cached fframes crates'
    if not registry_sources:
        collector.missing.append('fframes/required-caller-sources')
    for index, source in enumerate(registry_sources):
        for name in FFRAMES_FILES:
            collector.file(source / name, registry, f'fframes-{index}/{name}')
    return candidates


def evidence_complete(collector, candidates):
    caller = any(all(f'fframes-{index}/{name}' in collector.files for name in FFRAMES_FILES)
                 for index in range(4))
    native = any(row['required_sources_present'] and row['required_build_evidence_present']
                 for row in candidates)
    return bool(caller and native and 'renderer/vpnrouter-film' in collector.files
                and any(label.startswith('cargo-fingerprints/') for label in collector.files))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True, type=Path)
    parser.add_argument('--registry', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    collector = Collector(args.output)
    report = {'kind': 'cached_native_provenance_diagnostic', 'metric': None,
        'not_a_measurement_or_quality_receipt': True, 'accepted_baseline': False,
        'optimization_candidate_count': 0, 'cache_candidate_count': 0, 'expected_source': SOURCE,
        'cached_code_executed': False, 'active_binary_binding_established': False}
    code = 1
    try:
        head = collector.command(['git', '-C', str(args.repo), 'rev-parse', 'HEAD'], 'checkout-head')
        assert head.returncode == 0 and head.stdout.strip() == SOURCE, 'Unexpected checkout SHA'
        report['cache_candidates'] = collect_cache(collector, args.repo, args.registry)
        report['cache_candidate_count'] = len(report['cache_candidates'])
        # System package identity is not the cached executable's link identity.
        collector.command(['dpkg-query', '-W', '-f=${Package}\t${Version}\t${source:Package}\t${source:Version}\n',
                           'libx264-164', 'libx264-dev'], 'runner-x264-packages')
        for name in ('x264.h', 'x264_config.h'):
            collector.file(Path('/usr/include') / name, Path('/usr'), f'runner/{name}')
        library = Path('/usr/lib/x86_64-linux-gnu/libx264.so.164').resolve(strict=True)
        collector.file(library, Path('/usr'), 'runner/libx264.so.164')
        collector.command(['readelf', '-n', str(library)], 'runner-x264-build-id')
        film = args.repo / 'target/release/vpnrouter-film'
        if film.exists():
            confined(film, args.repo)
            collector.command(['readelf', '-d', str(film)], 'cached-renderer-dynamic-tags')
            collector.command(['readelf', '-n', str(film)], 'cached-renderer-build-id')
        complete = evidence_complete(collector, report['cache_candidates'])
        report['required_evidence_complete'] = complete
        report['status'] = 'collected_candidates_not_active_binary_proven' if complete else 'required_evidence_missing'
        # An unbound candidate is usable for inspection, never proof of the active build.
        commands_ok = all(row['returncode'] == 0 and not row.get('stdout_truncated')
                          and not row.get('stderr_truncated') for row in collector.commands)
        code = 0 if complete and commands_ok else 1
        if complete and not commands_ok:
            report['status'] = 'command_evidence_failed'
    except (AssertionError, OSError, ValueError, subprocess.SubprocessError) as error:
        report['status'] = 'collection_failed'
        report['error'] = str(error)
    finally:
        report.update(files=collector.files, missing=collector.missing, commands=collector.commands)
        report['exit_code'] = code
        with (collector.output / 'manifest.json').open('x') as stream:
            json.dump(report, stream, indent=2)
            stream.write('\n')
        print(json.dumps({key: report[key] for key in ('status', 'exit_code', 'cached_code_executed',
                                                       'active_binary_binding_established')}))
    return code


if __name__ == '__main__':
    raise SystemExit(main())
