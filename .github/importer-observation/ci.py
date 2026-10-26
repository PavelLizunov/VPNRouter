"""One disposable-CI diagnostic, never a benchmark or historical-binary replay."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.error
import urllib.request
import zipfile

FROZEN = 'ac5ac03c4475c32dea13738e5941db00115c8da6'
X264_URL = ('https://archive.ubuntu.com/ubuntu/pool/universe/x/x264/'
            'x264_0.164.3108+git31e19f9.orig.tar.gz')
X264_SHA = '41606cb8e788a7f8c4514290646d4ba5c7bc68d9e1ccd1a73f446a90546913eb'
CRATE_SHA = '24719b199dd9da6c5d8aed3fa50bd4bc89af1b3dc9e096a6472cf774634ee0c0'
WITNESS_SHA = '344f65837ce259c8a687cea115c71969d2e5bf192e95c70831689b79dc8eaec1'
WITNESS_URL = ('https://api.github.com/repos/PavelLizunov/VPNRouter/'
               'actions/artifacts/12605741457/zip')
TRACES = ('batch-0/input-traces/anime-6138.tsv', 'batch-1/input-traces/anime-8133.tsv')
HERE = Path(__file__).resolve().parent


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value):
    with path.open('x') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')


def command(args, log, *, cwd=None, env=None):
    with log.open('xb') as stream:
        subprocess.run([str(arg) for arg in args], cwd=cwd, env=env,
                       stdout=stream, stderr=subprocess.STDOUT, check=True)


def fetch(url, path, expected, token=None):
    # No auth header is sent to the redirected artifact-storage origin.
    if token:
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                return None
        request = urllib.request.Request(url, headers={
            'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json'})
        try:
            urllib.request.build_opener(NoRedirect).open(request, timeout=60)
        except urllib.error.HTTPError as error:
            if error.code != 302:
                raise RuntimeError(f'Artifact access failed: HTTP {error.code}') from None
            url = error.headers['Location']
            if not url.startswith('https://'):
                raise RuntimeError('Non-HTTPS artifact redirect')
        else:
            raise RuntimeError('Expected artifact download redirect')
    with urllib.request.urlopen(url, timeout=120) as response, path.open('xb') as stream:
        shutil.copyfileobj(response, stream)
    if digest(path) != expected:
        raise RuntimeError('Downloaded operand hash mismatch')


def capacity(folder, receipt_name='preflight.json'):
    memory = {line.split(':')[0]: int(line.split()[1]) * 1024
              for line in Path('/proc/meminfo').read_text().splitlines()}
    processes = []
    conflicts = []
    names = {'cargo', 'rustc', 'cc1', 'cc1plus', 'clang', 'gcc', 'make', 'ninja',
             'dotnet', 'msbuild', 'java', 'VBCSCompiler', 'ffmpeg'}
    for path in Path('/proc').iterdir():
        if not path.name.isdigit():
            continue
        try:
            name = (path/'comm').read_text().strip()
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        if name in names:
            conflicts.append({'pid': int(path.name), 'name': name})
    for tool in ('rustc', 'cargo', 'clang', 'cc', 'make', 'pkg-config', 'readelf'):
        executable = shutil.which(tool)
        if not executable:
            raise RuntimeError(f'Missing preinstalled tool: {tool}')
        processes.append({'tool': tool, 'path': executable,
                          'version': subprocess.check_output([tool, '--version'], text=True).splitlines()[0]})
    result = {'logical_cpus': os.cpu_count(), 'load_average': list(os.getloadavg()),
              'ram_available_bytes': memory['MemAvailable'],
              'swap_used_bytes': memory['SwapTotal'] - memory['SwapFree'],
              'disk_available_bytes': shutil.disk_usage(folder).free,
              'conflicting_jobs': conflicts, 'tools': processes}
    save(folder/receipt_name, result)
    if (result['ram_available_bytes'] < 4 * 1024**3
            or result['disk_available_bytes'] < 14 * 1024**3
            or result['load_average'][0] >= result['logical_cpus'] * 2
            or result['swap_used_bytes'] > 1024**3 or conflicts):
        raise RuntimeError('Insufficient disposable-runner headroom; see preflight.json')


def prepare(root, output, work):
    output.mkdir()
    work.mkdir()
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    if head != FROZEN:
        raise RuntimeError('Fixture source is not the frozen SHA')
    save(output/'request.json', {'kind': 'bounded_importer_observation', 'frozen_source': head,
         'implementation_sha': os.environ['GITHUB_SHA'], 'github_run_id': os.environ['GITHUB_RUN_ID'],
         'metric': None, 'optimization_attempt': False, 'historical_binary': False,
         'target_pts': [768, 791], 'maximum_film_lifetimes': 3,
         'stop_on_transparency_failure_or_absent_reproduction': True})
    capacity(output)
    fetch(X264_URL, output/'x264-source.tar.gz', X264_SHA)
    archive = output/'historical-witness.zip'
    fetch(WITNESS_URL, archive, WITNESS_SHA, os.environ['GH_TOKEN'])
    history = output/'historical'
    history.mkdir()
    with zipfile.ZipFile(archive) as saved:
        for index, name in enumerate(TRACES):
            with (history/f'anime-{index}.tsv').open('xb') as stream:
                stream.write(saved.read(name))
    save(history/'identity.json', {'run': 40209722318, 'artifact': 12605741457,
         'archive_sha256': WITNESS_SHA, 'original_members': list(TRACES),
         'trace_sha256': {p.name: digest(p) for p in history.glob('*.tsv')},
         'raw_input_available': False})
    for variant in ('plain', 'instrumented'):
        source = work/variant
        source.mkdir()
        with tarfile.open(output/'x264-source.tar.gz') as saved:
            prefix = 'x264-0.164.3108+git31e19f9/'
            for member in saved.getmembers():
                if not member.name.startswith(prefix):
                    if member.isdir() and member.name == prefix.rstrip('/'):
                        continue
                    raise RuntimeError('Unexpected source archive root')
                relative = Path(member.name.removeprefix(prefix))
                if '..' in relative.parts or relative.is_absolute() or not (member.isfile() or member.isdir()):
                    raise RuntimeError('Unsupported source archive member')
                target = source/relative
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with target.open('xb') as stream:
                        shutil.copyfileobj(saved.extractfile(member), stream)
                    target.chmod(member.mode & 0o777)
    examples = root/'examples'
    examples.mkdir(exist_ok=True)
    with (examples/'importer_segment.rs').open('xb') as stream:
        stream.write((HERE/'segment.rs').read_bytes())
    frozen_files = [root/'Cargo.toml', root/'Cargo.lock']
    for name in ('src', 'media', 'licenses'):
        frozen_files.extend(p for p in (root/name).rglob('*') if p.is_file())
    save(output/'frozen-inputs.json', {str(p.relative_to(root)): digest(p) for p in sorted(frozen_files)})


def build(root, output, work):
    capacity(output, 'native-preflight.json')
    command(['python3', HERE/'instrument.py', '--source', work/'instrumented',
             '--header', HERE/'importer-observer.h'], output/'instrumentation.log')
    # Both fresh libraries use identical compiler/configure settings. This is not
    # a reproducibility rebuild of Ubuntu's historical distro binary.
    for variant in ('plain', 'instrumented'):
        source = work/variant
        prefix = work/f'{variant}-prefix'
        flags = ['./configure', f'--prefix={prefix}', '--enable-shared', '--disable-cli',
                 '--disable-opencl', '--extra-ldflags=-lcrypto']
        command(flags, output/f'{variant}-configure.log', cwd=source)
        command(['make', '-j2'], output/f'{variant}-build.log', cwd=source)
        command(['make', 'install-lib-shared'], output/f'{variant}-install.log', cwd=source)
        retained = output/variant
        retained.mkdir()
        for name in ('config.mak', 'config.h', 'x264_config.h', 'config.log', 'encoder/encoder.c', 'COPYING'):
            shutil.copyfile(source/name, retained/Path(name).name)
        shutil.copyfile(prefix/'lib/libx264.so.164', retained/'libx264.so.164')
        command(['readelf', '-n', prefix/'lib/libx264.so.164'], retained/'elf-notes.txt')
    plain = work/'plain-prefix'
    command(['cc', '-O2', '-Wall', '-Wextra', '-Werror', '-I'+str(plain/'include'),
             HERE.parent/'diagnostics/fframes-x264-input.c', '-shared', '-fPIC',
             '-o', work/'public-probe.so', '-ldl', '-lcrypto', '-pthread'], output/'public-probe-build.log')
    command(['cc', '-O2', '-Wall', '-Wextra', '-Werror', '-I'+str(plain/'include'),
             HERE/'smoke.c', '-L'+str(plain/'lib'), '-o', work/'smoke', '-lx264'], output/'smoke-build.log')
    env = os.environ.copy()
    env.update({'PKG_CONFIG_PATH': str(plain/'lib/pkgconfig'), 'LIBRARY_PATH': str(plain/'lib'),
                'LD_LIBRARY_PATH': str(plain/'lib'), 'FFMPEG_FORCE_BUILD': '1',
                'FFMPEG_BINARIES_CACHE': str(work/'fresh-ffmpeg-cache'),
                'CARGO_TARGET_DIR': str(work/'cargo-target')})
    command(['cargo', 'fetch', '--locked'], output/'cargo-fetch.log', cwd=root, env=env)
    crates = list((Path.home()/'.cargo/registry/cache').glob('*/fframes-1.3.0.crate'))
    if len(crates) != 1 or digest(crates[0]) != CRATE_SHA:
        raise RuntimeError('Locked fframes crate archive mismatch')
    command(['cargo', 'build', '--release', '--locked', '--example', 'importer_segment'],
            output/'cargo-build.log', cwd=root, env=env)
    binary = work/'cargo-target/release/examples/importer_segment'
    shutil.copyfile(binary, output/'importer_segment')
    shutil.copyfile(work/'public-probe.so', output/'public-probe.so')
    shutil.copyfile(work/'smoke', output/'smoke')
    shutil.copyfile(HERE/'importer-observer.h', output/'importer-observer.h')
    shutil.copyfile(HERE/'segment.rs', output/'segment.rs')
    ffmpeg_sources = list((work/'cargo-target/release/build').glob('ffmpeg-sys-*/out/ffmpeg*'))
    provenance = []
    native_folder = output/'ffmpeg-provenance'
    native_folder.mkdir()
    for index, source in enumerate(ffmpeg_sources):
        if not source.is_dir():
            continue
        row = {'path': str(source), 'git_head': None, 'files': {}}
        # Never let Git discover an enclosing repository and call it FFmpeg HEAD.
        if (source/'.git').is_dir() and not (source/'.git').is_symlink():
            commit = subprocess.run(['git', '--git-dir='+str(source/'.git'), 'rev-parse', 'HEAD'],
                                    capture_output=True, text=True)
            row.update({'git_head': commit.stdout.strip(), 'git_head_exit': commit.returncode})
        for name in ('config.h', 'ffbuild/config.mak', 'libavcodec/libx264.c', 'libavutil/version.h'):
            path = source/name
            if path.is_file():
                target = native_folder/f'{index}-{name.replace("/", "-")}'
                shutil.copyfile(path, target)
                row['files'][name] = digest(path)
        provenance.append(row)
    build_logs = output/'cargo-build-provenance'
    build_logs.mkdir()
    for index, path in enumerate(sorted((work/'cargo-target/release/build').glob('ffmpeg-sys-*/*'))):
        if path.is_file() and path.name in ('output', 'stderr'):
            shutil.copyfile(path, build_logs/f'{index}-{path.name}.txt')
    save(output/'build-identity.json', {'source_archive_sha256': X264_SHA, 'fframes_crate_sha256': CRATE_SHA,
         'fresh_libraries': {variant: digest(output/variant/'libx264.so.164')
                             for variant in ('plain', 'instrumented')},
         'binary_sha256': digest(binary), 'public_probe_sha256': digest(work/'public-probe.so'),
         'ffmpeg_source_observations': provenance, 'historical_distro_binary': False})


def load_checker():
    spec = importlib.util.spec_from_file_location('importer_analysis', HERE/'analyze.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_operand(label, variant, observing, *, film, root, output, work, checker):
    folder = output/label
    folder.mkdir()
    public = folder/'public'
    public.mkdir()
    env = os.environ.copy()
    for name in ('LD_PRELOAD', 'LD_LIBRARY_PATH', 'FFRAMES_IMPORTER_TRACE', 'FFRAMES_INPUT_TRACE', 'GH_TOKEN'):
        env.pop(name, None)
    library = work/f'{variant}-prefix/lib'
    env.update({'LD_LIBRARY_PATH': str(library), 'LD_PRELOAD': str(work/'public-probe.so'),
                'FFRAMES_INPUT_TRACE': str(public), 'FILM_STYLE': 'anime'})
    if observing:
        env['FFRAMES_IMPORTER_TRACE'] = str(folder/'importer.tsv')
    binary = work/'cargo-target/release/examples/importer_segment' if film else work/'smoke'
    target = folder/'segment' if film else folder/'encoded.h264'
    # LD_DEBUG records the loader's actual initialization path, not just ldd's prediction.
    env.update({'LD_DEBUG': 'libs', 'LD_DEBUG_OUTPUT': str(folder/'loader')})
    save(folder/'invocation.json', {'binary_sha256': digest(binary), 'variant': variant,
         'library_sha256': digest(library/'libx264.so.164'), 'internal_observation': observing,
         'public_observation': True, 'film_target': film, 'pts': [768, 791]})
    command([binary, target], folder/'run.log', cwd=root, env=env)
    loader = '\n'.join(p.read_text() for p in folder.glob('loader.*'))
    if not any('calling init:' in row and str(library/'libx264.so.164') in row
               for row in loader.splitlines()):
        raise RuntimeError('Actual loaded x264 path is not the selected isolated library')
    traces = list(public.glob('anime-*.tsv'))
    if len(traces) != 1:
        raise RuntimeError('Missing or multiple public lifetime traces')
    boundary = checker.read_segment(traces[0])
    imported = checker.read_importer(folder/'importer.tsv', boundary) if observing else None
    if not observing and (folder/'importer.tsv').exists():
        raise RuntimeError('Importer evidence leaked into plain control')
    media = target/'0000000768.mp4' if film else target
    if film:
        claims = json.loads((target/'claims.json').read_text())
        if (claims['claim_count'] != 24 or claims['concurrency'] != 1 or claims['frame_range'] is not None
                or [row['frame'] for row in claims['claims']] != list(range(768, 792))
                or sorted(p.name for p in target.iterdir()) != ['0000000768.mp4', 'claims.json']):
            raise RuntimeError('Fixture did not finish exactly the authorized claims')
    command(['ffprobe', '-v', 'error', '-show_streams', '-show_packets', '-show_data_hash',
             'sha256', '-of', 'json', media], folder/'packets.json')
    decode = ['ffmpeg', '-v', 'error', '-xerror', '-i', media, '-map', '0:v:0', '-an',
              '-pix_fmt', 'yuv420p', '-c:v', 'rawvideo', '-fps_mode', 'passthrough',
              '-f', 'framehash', '-hash', 'sha256', '-']
    command(decode, folder/'decoded.sha256')
    command(decode, folder/'redecoded.sha256')
    if (folder/'decoded.sha256').read_bytes() != (folder/'redecoded.sha256').read_bytes():
        raise RuntimeError('Same-file decoding unstable; stop without native localization')
    packets = json.loads((folder/'packets.json').read_text())
    streams = packets['streams']
    frame_rows = [row for row in (folder/'decoded.sha256').read_text().splitlines()
                  if row and not row.startswith('#')]
    if (len(streams) != 1 or streams[0]['codec_name'] != 'h264'
            or (streams[0]['width'], streams[0]['height']) != (854, 480)
            or streams[0]['pix_fmt'] != 'yuv420p' or len(frame_rows) != 24
            or len(packets['packets']) != 24):
        raise RuntimeError('Unexpected native output geometry/count/format')
    return {'boundary': boundary, 'importer': imported, 'decoded': frame_rows,
            'media_sha256': digest(media), 'folder': folder}


def transparency(left, right, checker):
    comparison = checker.compare_public(left['boundary'], right['boundary'])
    comparison.update({'decoded_equal': left['decoded'] == right['decoded'],
                       'container_bytes_equal': left['media_sha256'] == right['media_sha256']})
    comparison['passed'] = (comparison['observed_inputs_equal']
                            and comparison['native_payloads_equal'] and comparison['decoded_equal'])
    return comparison


def experiment(root, output, work):
    checker = load_checker()
    smoke = []
    for label, variant, observing in (('smoke-plain', 'plain', False),
            ('smoke-instrumented-off', 'instrumented', False),
            ('smoke-instrumented-on', 'instrumented', True)):
        smoke.append(run_operand(label, variant, observing, film=False, root=root,
                                 output=output, work=work, checker=checker))
        if len(smoke) > 1:
            verdict = transparency(smoke[0], smoke[-1], checker)
            save(output/(label+'-transparency.json'), verdict)
            if not verdict['passed']:
                save(output/'verdict.json', {'status': 'transparency_failure', 'phase': label,
                     'metric': None, 'causal_perturbation_proven': False, 'film_lifetimes': 0})
                raise RuntimeError('Synthetic transparency failed; film observation stopped')
    plain = run_operand('film-plain', 'plain', False, film=True, root=root,
                        output=output, work=work, checker=checker)
    observed = run_operand('film-observed-a', 'instrumented', True, film=True, root=root,
                           output=output, work=work, checker=checker)
    historical = [checker.read_segment(output/'historical'/f'anime-{index}.tsv', historical=True)
                  for index in range(2)]
    binding = {'plain_vs_historical': [checker.compare_public(plain['boundary'], old) for old in historical],
               'observed_vs_historical': [checker.compare_public(observed['boundary'], old) for old in historical],
               'historical_allocator_cache_history_preserved': False,
               'fresh_build_is_historical_binary': False}
    save(output/'historical-binding.json', binding)
    gate = transparency(plain, observed, checker)
    save(output/'film-transparency.json', gate)
    if not gate['passed']:
        save(output/'verdict.json', {'status': 'transparency_failure', 'phase': 'film',
             'metric': None, 'causal_perturbation_proven': False, 'film_lifetimes': 2})
        raise RuntimeError('Target transparency failed; no further native observation')
    if not all(row['observed_inputs_equal'] for group in
               ('plain_vs_historical', 'observed_vs_historical') for row in binding[group]):
        save(output/'verdict.json', {'status': 'historical_observed_input_mismatch',
             'metric': None, 'film_lifetimes': 2, 'complete_semantic_input_equality_established': False})
        raise RuntimeError('Reduced fixture did not match historical observed inputs/settings; stop')
    # One prospective pair, at this same affected lifetime, only after controls pass.
    repeated = run_operand('film-observed-b', 'instrumented', True, film=True, root=root,
                          output=output, work=work, checker=checker)
    public = checker.compare_public(observed['boundary'], repeated['boundary'])
    internal = checker.compare_importer(observed['importer'], repeated['importer'])
    decoded_equal = observed['decoded'] == repeated['decoded']
    reproduced = not public['native_payloads_equal'] or not decoded_equal
    status = 'observed_divergence' if reproduced else 'no_reproduction'
    if reproduced:
        location = ('before_import' if internal['entry_changed_pts'] else
                    'import_or_omitted_consumed_input' if internal['coded_changed_pts'] else
                    'downstream_or_unobserved_state')
    else:
        location = 'not_localized'
    save(output/'verdict.json', {'status': status, 'metric': None, 'film_lifetimes': 3,
         'public': public, 'internal': internal, 'decoded_equal': decoded_equal,
         'boundary_classification_not_root_cause': location,
         'complete_semantic_input_equality_established': False,
         'accepted_baseline': None, 'best_candidate': None, 'optimization_attempts': 0,
         'all_eight_export_ran': False, 'further_native_run_authorized_by_result': False})
    frozen = json.loads((output/'frozen-inputs.json').read_text())
    if any(digest(root/name) != expected for name, expected in frozen.items()):
        raise RuntimeError('Frozen source/media/lock bytes changed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('prepare', 'build', 'experiment'))
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('FFRAMES_NUM_THREADS') != '1':
        raise SystemExit('Native stages are disposable GitHub CI only, with original worker settings')
    root, output, work = (p.resolve() for p in (args.root, args.output, args.work))
    try:
        if args.phase == 'prepare':
            prepare(root, output, work)
        elif args.phase == 'build':
            build(root, output, work)
        else:
            experiment(root, output, work)
    except Exception as error:
        if output.is_dir():
            # Avoid exporting redirect URLs or credential-bearing exception details.
            reason = str(error) if isinstance(error, (RuntimeError, ValueError)) else type(error).__name__
            save(output/f'{args.phase}-failure.json', {'status': 'failed', 'exception': type(error).__name__,
                 'reason': reason, 'metric': None, 'operands_retained': True})
        raise


if __name__ == '__main__':
    main()
