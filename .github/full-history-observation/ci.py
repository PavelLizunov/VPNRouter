"""One finite full-history diagnosis on disposable CI; never benchmark acceptance."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import zipfile

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('bounded_ci_helpers',
                                             HERE.parent/'importer-observation/ci.py')
HELPERS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPERS)
FROZEN = HELPERS.FROZEN
X264_SHA = HELPERS.X264_SHA
CRATE_SHA = HELPERS.CRATE_SHA
save, digest, command = HELPERS.save, HELPERS.digest, HELPERS.command
PIXEL_BYTES = 2671833600
SAVED_MEDIA_ARTIFACT = 12669620506
SAVED_MEDIA_SHA = '6e0cc25044f2ec79dbde871a626043ae9cf27048115a7334349cf1daf76df5ae'
MANIFEST = (
    ('synthetic-plain', 'plain', False, False),
    ('synthetic-off', 'instrumented', False, False),
    ('synthetic-on', 'instrumented', True, False),
    ('film-P', 'plain', False, True),
    ('film-A', 'instrumented', True, True),
    ('film-B', 'instrumented', True, True),
)


def load(name):
    spec = importlib.util.spec_from_file_location('full_history_'+name, HERE/(name+'.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def capacity(output, name):
    HELPERS.capacity(output, name)
    value = json.loads((output/name).read_text())
    # Reserve packed pixels plus typed state, media, two fresh native/Cargo builds
    # and the retained diagnostic closure. Available capacity is not consumption.
    if value['disk_available_bytes'] < 32*1024**3 + PIXEL_BYTES:
        raise RuntimeError('Insufficient evidence/build headroom; no cache cleanup')


def frozen_inputs(root):
    files = [root/'Cargo.toml', root/'Cargo.lock']
    for name in ('src', 'media', 'licenses'):
        for path in (root/name).rglob('*'):
            if path.is_symlink():
                raise ValueError('Frozen operand contains a symlink')
            if path.is_file():
                files.append(path)
    return {str(p.relative_to(root)): digest(p) for p in sorted(files)}


def prepare_test_media(output):
    """Copy only inert, digest-bound saved data needed by fresh-checkout tests."""
    archive_path = output/'saved-media-tests.zip'
    HELPERS.fetch('https://api.github.com/repos/PavelLizunov/VPNRouter/actions/artifacts/'
                  f'{SAVED_MEDIA_ARTIFACT}/zip', archive_path, SAVED_MEDIA_SHA,
                  os.environ['GH_TOKEN'])
    destination = output/'saved-media-tests'
    destination.mkdir()
    files = {}
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate saved-data archive member')
        for label in ('film-plain', 'film-observed-a', 'film-observed-b',
                      'smoke-plain', 'smoke-instrumented-off', 'smoke-instrumented-on'):
            prefix = label+'/public/anime-'
            traces = [name for name in names if name.startswith(prefix)
                      and name[len(prefix):].removesuffix('.tsv').isdigit() and name.endswith('.tsv')]
            if len(traces) != 1:
                raise ValueError('Missing or ambiguous saved public trace')
            required = ['decoded.sha256', 'redecoded.sha256']
            required += ['packets.json', 'segment/0000000768.mp4'] if label.startswith('film-') else ['encoded.h264']
            for name in traces + [label+'/'+name for name in required]:
                member = archive.getinfo(name)
                if (member.is_dir() or member.file_size > 16*1024**2
                        or (member.external_attr >> 16) & 0o170000 == 0o120000):
                    raise ValueError('Unsupported saved-data archive member')
                target = destination/name
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(member) as source, target.open('xb') as stream:
                    shutil.copyfileobj(source, stream)
                files[name] = digest(target)
    save(output/'saved-media-tests-identity.json', {
        'artifact': SAVED_MEDIA_ARTIFACT, 'archive_sha256': SAVED_MEDIA_SHA,
        'files': files, 'native_payloads_extracted_or_executed': False,
    })


def prepare(root, output, work):
    output.mkdir()
    work.mkdir()
    save(output/'request.json', {
        'kind': 'finite_full_history_observation', 'frozen_source': FROZEN,
        'implementation_sha': os.environ['GITHUB_SHA'], 'run': os.environ['GITHUB_RUN_ID'],
        'maximum_processes': 6, 'maximum_frames': 3384, 'maximum_lifetimes': 141,
        'manifest': MANIFEST, 'metric': None, 'baseline': None,
        'optimization_attempt': False, 'historical_binary_replay': False,
        'no_retry_or_style_expansion': True, 'packed_pixels_maximum_bytes': PIXEL_BYTES,
    })
    capacity(output, 'prepare-preflight.json')
    repository = root.parents[2]
    if subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repository,
                               text=True).strip() != FROZEN:
        raise RuntimeError('Film checkout is not the exact frozen source')
    save(output/'frozen-inputs.json', frozen_inputs(root))
    HELPERS.fetch(HELPERS.X264_URL, output/'x264-source.tar.gz', X264_SHA)
    HELPERS.fetch(HELPERS.WITNESS_URL, output/'historical-witness.zip',
                  HELPERS.WITNESS_SHA, os.environ['GH_TOKEN'])
    history = output/'historical'
    history.mkdir()
    with zipfile.ZipFile(output/'historical-witness.zip') as archive:
        for index, member in enumerate(HELPERS.TRACES):
            with (history/f'anime-{index}.tsv').open('xb') as stream:
                stream.write(archive.read(member))
    save(history/'identity.json', {
        'run': 40209722318, 'artifact': 12605741457,
        'archive_sha256': HELPERS.WITNESS_SHA, 'members': HELPERS.TRACES,
        'trace_sha256': {p.name: digest(p) for p in history.glob('*.tsv')},
        'historical_raw_inputs_available': False,
    })
    for variant in ('plain', 'instrumented'):
        target = work/variant
        target.mkdir()
        with tarfile.open(output/'x264-source.tar.gz') as archive:
            prefix = 'x264-0.164.3108+git31e19f9/'
            for member in archive.getmembers():
                if member.name == prefix.rstrip('/') and member.isdir():
                    continue
                if not member.name.startswith(prefix):
                    raise ValueError('Unsupported source archive prefix')
                relative = Path(member.name[len(prefix):])
                if relative.is_absolute() or '..' in relative.parts or not (member.isfile() or member.isdir()):
                    raise ValueError('Unsupported native archive member')
                path = target/relative
                if member.isdir():
                    path.mkdir(parents=True, exist_ok=True)
                else:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with path.open('xb') as stream:
                        shutil.copyfileobj(archive.extractfile(member), stream)
                    path.chmod(member.mode & 0o777)
    save(output/'implementation-inputs.json', {
        str(p.relative_to(HERE.parent.parent)): digest(p)
        for p in sorted(HERE.glob('*')) if p.is_file()
    })
    prepare_test_media(output)


def retain_build(output, work):
    """Keep partial source/config/library operands even after a failed build."""
    retained = output/'native-build-operands'
    retained.mkdir(exist_ok=True)
    for variant in ('plain', 'instrumented'):
        folder = retained/variant
        folder.mkdir(exist_ok=True)
        source = work/variant
        names = ('config.mak', 'config.h', 'x264_config.h', 'config.log', 'COPYING',
                 'encoder/encoder.c', 'encoder/ratecontrol.c',
                 'encoder/full-history-observer.h')
        for name in names:
            path = source/name
            if path.is_file():
                shutil.copyfile(path, folder/name.replace('/', '-'))
        library = work/f'{variant}-prefix/lib/libx264.so.164'
        if library.is_file():
            shutil.copyfile(library, folder/'libx264.so.164')
    for name, target in (('public-probe.so', 'public-probe.so'),
                         ('smoke', 'smoke'),
                         ('cargo-target/release/vpnrouter-film', 'vpnrouter-film')):
        path = work/name
        if path.is_file():
            shutil.copyfile(path, retained/target)
    # Retain the FFmpeg C source/configuration actually used by the fresh build.
    ffmpeg = retained/'ffmpeg'
    ffmpeg.mkdir(exist_ok=True)
    rows = []
    for index, source in enumerate(sorted((work/'cargo-target/release/build').glob('ffmpeg-sys-*/out/ffmpeg*'))):
        if not source.is_dir():
            continue
        row = {'source_path': str(source), 'git_head': None, 'files': {}}
        if (source/'.git').is_dir() and not (source/'.git').is_symlink():
            result = subprocess.run(['git', '--git-dir='+str(source/'.git'), 'rev-parse', 'HEAD'],
                                    capture_output=True, text=True)
            row.update({'git_head': result.stdout.strip(), 'git_exit': result.returncode})
        for name in ('config.h', 'ffbuild/config.mak', 'libavcodec/libx264.c',
                     'libavcodec/encode.c', 'libavutil/version.h'):
            path = source/name
            if path.is_file():
                shutil.copyfile(path, ffmpeg/f'{index}-{name.replace("/", "-")}')
                row['files'][name] = digest(path)
        rows.append(row)
    for index, path in enumerate(sorted((work/'cargo-target/release/build').glob('ffmpeg-sys-*/*'))):
        if path.is_file() and path.name in ('output', 'stderr'):
            shutil.copyfile(path, ffmpeg/f'build-{index}-{path.name}')
    # This snapshot can legitimately change as a failed/partial build advances.
    (retained/'ffmpeg-source-observations.json').write_text(json.dumps(rows, indent=2)+'\n')


def retain_system_file(path, output):
    path = path.resolve(strict=True)
    if not path.is_file() or not str(path).startswith(('/usr/', '/lib/', '/lib64/')):
        raise ValueError('Unsupported system media executable/library path')
    target = output/'media-tool-files'/str(path).lstrip('/')
    target.parent.mkdir(parents=True, exist_ok=True)
    value = digest(path)
    if target.exists():
        if digest(target) != value:
            raise RuntimeError('System media executable/library changed')
    else:
        shutil.copyfile(path, target)
    return {'path': str(path), 'sha256': value, 'retained': str(target.relative_to(output))}


def loaded_media_files(folder, prefix):
    paths = set()
    for log in folder.glob(prefix+'.*'):
        for row in log.read_text().splitlines():
            if 'calling init:' in row:
                path = Path(row.split('calling init:', 1)[1].strip())
                if not path.is_absolute():
                    raise ValueError('Unbound media loader library path')
                paths.add(str(path.resolve(strict=True)))
    if not paths:
        raise RuntimeError('Missing actual media loader initialization evidence')
    return paths


def capture_media_tools(output):
    """Retain exact system report/decoder bytes, independently of film libraries."""
    tools = {}
    for name in ('ffmpeg', 'ffprobe'):
        executable = shutil.which(name)
        if executable is None:
            raise RuntimeError('Missing system media tool: '+name)
        tool = retain_system_file(Path(executable), output)
        command(['ldd', tool['path']], output/(name+'-linked-libraries.txt'))
        linked = set()
        for row in (output/(name+'-linked-libraries.txt')).read_text().splitlines():
            if 'not found' in row:
                raise RuntimeError('Unresolved system media library')
            match = re.search(r'(?:=>\s+)?(/[^\s]+)\s+\(0x[0-9a-f]+\)', row)
            if match:
                linked.add(str(Path(match[1]).resolve(strict=True)))
        env = os.environ.copy()
        for key in ('GH_TOKEN', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'LD_DEBUG', 'LD_DEBUG_OUTPUT'):
            env.pop(key, None)
        env.update({'LD_DEBUG': 'libs', 'LD_DEBUG_OUTPUT': str(output/(name+'-version-loader'))})
        command([tool['path'], '-version'], output/(name+'-version.txt'), env=env)
        linked |= loaded_media_files(output, name+'-version-loader')
        tool['libraries'] = [retain_system_file(Path(path), output) for path in sorted(linked)]
        if not tool['libraries']:
            raise RuntimeError('Missing media executable/library closure')
        tools[name] = tool
    save(output/'media-tools.json', tools)
    return tools


def verify_media_tool(tool):
    for item in [tool, *tool['libraries']]:
        if digest(Path(item['path'])) != item['sha256']:
            raise RuntimeError('Media executable/library identity drift')


def media_command(args, log, tool, label):
    verify_media_tool(tool)
    env = os.environ.copy()
    for key in ('GH_TOKEN', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'LD_DEBUG', 'LD_DEBUG_OUTPUT'):
        env.pop(key, None)
    env.update({'LD_DEBUG': 'libs', 'LD_DEBUG_OUTPUT': str(log.parent/(label+'-loader'))})
    save(log.parent/(label+'-invocation.json'), {
        'argv': [str(a) for a in args], 'executable_sha256': tool['sha256'],
        'libraries': tool['libraries'], 'loader_prefix': label+'-loader',
    })
    command(args, log, env=env)
    loaded = loaded_media_files(log.parent, label+'-loader')
    if not loaded <= {item['path'] for item in tool['libraries']}:
        raise RuntimeError('Media process loaded an unretained shared library')
    verify_media_tool(tool)


def build(root, output, work):
    capacity(output, 'build-preflight.json')
    try:
        command(['python3', HERE/'instrument.py', '--source', work/'instrumented',
                 '--header', HERE/'full-history-observer.h',
                 '--archive', output/'x264-source.tar.gz'], output/'instrumentation.log')
        for variant in ('plain', 'instrumented'):
            source = work/variant
            prefix = work/f'{variant}-prefix'
            command(['./configure', f'--prefix={prefix}', '--enable-shared',
                     '--disable-cli', '--disable-opencl', '--extra-ldflags=-lcrypto'],
                    output/f'{variant}-configure.log', cwd=source)
            command(['make', '-j2'], output/f'{variant}-build.log', cwd=source)
            command(['make', 'install-lib-shared'], output/f'{variant}-install.log', cwd=source)
            command(['readelf', '-Ws', prefix/'lib/libx264.so.164'],
                    output/f'{variant}-symbols.txt')
            command(['readelf', '--dyn-syms', '--wide', prefix/'lib/libx264.so.164'],
                    output/f'{variant}-dynamic-symbols.txt')
            exported = (output/f'{variant}-dynamic-symbols.txt').read_text()
            if any('ffh8_' in row or 'fframes_history' in row for row in exported.splitlines()):
                raise RuntimeError('Internal observation bridge escaped dynamic symbol visibility')
        plain = work/'plain-prefix'
        command(['cc', '-O2', '-Wall', '-Wextra', '-Werror', '-I'+str(plain/'include'),
                 HERE.parent/'diagnostics/fframes-x264-input.c', '-shared', '-fPIC',
                 '-o', work/'public-probe.so', '-ldl', '-lcrypto', '-pthread'],
                output/'public-probe-build.log')
        command(['cc', '-O2', '-Wall', '-Wextra', '-Werror', '-I'+str(plain/'include'),
                 HERE/'smoke.c', '-L'+str(plain/'lib'), '-o', work/'smoke', '-lx264'],
                output/'smoke-build.log')
        env = os.environ.copy()
        for name in ('GH_TOKEN', 'LD_PRELOAD', 'FFRAMES_HISTORY_TRACE', 'FFRAMES_INPUT_TRACE'):
            env.pop(name, None)
        env.update({'PKG_CONFIG_PATH': str(plain/'lib/pkgconfig'),
                    'LIBRARY_PATH': str(plain/'lib'), 'LD_LIBRARY_PATH': str(plain/'lib'),
                    'FFMPEG_FORCE_BUILD': '1', 'FFMPEG_BINARIES_CACHE': str(work/'fresh-ffmpeg-cache'),
                    'CARGO_TARGET_DIR': str(work/'cargo-target')})
        command(['cargo', 'fetch', '--locked'], output/'cargo-fetch.log', cwd=root, env=env)
        crates = list((Path.home()/'.cargo/registry/cache').glob('*/fframes-1.3.0.crate'))
        if len(crates) != 1 or digest(crates[0]) != CRATE_SHA:
            raise RuntimeError('Locked fframes crate archive mismatch')
        with tarfile.open(crates[0]) as archive:
            caller = {}
            for name in ('cli.rs', 'cpu.rs', 'scheduler.rs', 'segment_writer.rs',
                         'concatenator.rs', 'renderer.rs', 'encoder.rs', 'stream.rs'):
                member = 'fframes-1.3.0/src/renderer/'+name
                data = archive.extractfile(member).read()
                target = output/('caller-'+name)
                with target.open('xb') as stream:
                    stream.write(data)
                caller[member] = digest(target)
        save(output/'locked-caller-source.json', caller)
        shutil.copyfile(crates[0], output/'fframes-1.3.0.crate')
        command(['cargo', 'build', '--release', '--locked', '--bin', 'vpnrouter-film'],
                output/'cargo-build.log', cwd=root, env=env)
        binary = work/'cargo-target/release/vpnrouter-film'
        command(['readelf', '-d', binary], output/'film-dynamic-dependencies.txt')
        command(['readelf', '-n', binary], output/'film-elf-notes.txt')
        tools = capture_media_tools(output)
        save(output/'build-identity.json', {
            'source_archive_sha256': X264_SHA, 'locked_crate_sha256': CRATE_SHA,
            'binary_sha256': digest(binary), 'public_probe_sha256': digest(work/'public-probe.so'),
            'libraries': {v: digest(work/f'{v}-prefix/lib/libx264.so.164')
                          for v in ('plain', 'instrumented')},
            'original_film_backend': True, 'historical_distro_binary': False,
            'media_tools': tools,
        })
    finally:
        retain_build(output, work)


def media_reports(media, folder, tools):
    folder.mkdir()
    media_command([tools['ffprobe']['path'], '-v', 'error', '-count_packets', '-show_streams', '-show_packets',
                   '-show_data_hash', 'sha256', '-of', 'json', media], folder/'packets.json', tools['ffprobe'], 'probe')
    decode = [tools['ffmpeg']['path'], '-v', 'error', '-xerror', '-i', media, '-map', '0:v:0', '-an',
              '-pix_fmt', 'yuv420p', '-c:v', 'rawvideo', '-fps_mode', 'passthrough',
              '-f', 'framehash', '-hash', 'sha256', '-']
    media_command(decode, folder/'decoded.sha256', tools['ffmpeg'], 'decode')
    media_command(decode, folder/'redecoded.sha256', tools['ffmpeg'], 'redecode')
    return (folder/'packets.json', folder/'decoded.sha256', folder/'redecoded.sha256')


def launch(label, variant, observing, film, root, output, work):
    # Every attempt owns its directory before spawning; failure never permits replay.
    folder = output/label
    folder.mkdir()
    public_dir = folder/'public'
    public_dir.mkdir()
    env = os.environ.copy()
    for name in ('GH_TOKEN', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'FFRAMES_INPUT_TRACE',
                 'FFRAMES_IMPORTER_TRACE', 'FFRAMES_HISTORY_TRACE', 'FFRAMES_HISTORY_LIFETIMES'):
        env.pop(name, None)
    library = work/f'{variant}-prefix/lib'
    env.update({'LD_LIBRARY_PATH': str(library), 'LD_PRELOAD': str(work/'public-probe.so'),
                'FFRAMES_INPUT_TRACE': str(public_dir), 'FILM_STYLE': 'anime',
                'FFRAMES_NUM_THREADS': '1', 'RAYON_NUM_THREADS': '2',
                'LD_DEBUG': 'libs', 'LD_DEBUG_OUTPUT': str(folder/'loader')})
    lifetimes = 45 if film else 2
    if observing:
        (folder/'internal').mkdir()
        env.update({'FFRAMES_HISTORY_TRACE': str(folder/'internal'),
                    'FFRAMES_HISTORY_LIFETIMES': str(lifetimes)})
    if film:
        shutil.copytree(root/'media', folder/'media', symlinks=False)
        if {str(p.relative_to(folder)): digest(p) for p in (folder/'media').rglob('*') if p.is_file()} != {
                k: v for k, v in frozen_inputs(root).items() if k.startswith('media/')}:
            raise RuntimeError('Exclusive media copy does not match frozen inputs')
        binary = work/'cargo-target/release/vpnrouter-film'
        argv = [binary, '--scale', '0.4444444444444444', 'render',
                '-o', 'out/styles/preview/anime/film-silent.mp4']
    else:
        binary = work/'smoke'
        argv = [binary, folder]
    save(folder/'invocation.json', {
        'argv': [str(a) for a in argv], 'cwd': str(folder),
        'binary_sha256': digest(binary), 'library_sha256': digest(library/'libx264.so.164'),
        'variant': variant, 'internal_observation': observing, 'public_observation': True,
        'film': film, 'expected_lifetimes': lifetimes, 'expected_frames': lifetimes*24,
        'environment': {k: env[k] for k in ('LD_PRELOAD', 'LD_LIBRARY_PATH', 'FILM_STYLE',
                        'FFRAMES_NUM_THREADS', 'RAYON_NUM_THREADS', 'FFRAMES_INPUT_TRACE')},
    })
    command(argv, folder/'run.log', cwd=folder, env=env)
    save(folder/'process-completed.json', {'exit_code': 0})
    loader_files = list(folder.glob('loader.*'))
    loader = '\n'.join(p.read_text() for p in loader_files)
    loaded = [row.strip() for row in loader.splitlines() if 'calling init:' in row and 'libx264.so' in row]
    if len(loaded) != 1 or str(library/'libx264.so.164') not in loaded[0]:
        raise RuntimeError('Actual selected x264 loader identity is ambiguous or wrong')
    save(folder/'loader-binding.json', {'selected_library': str(library/'libx264.so.164'),
                                      'sha256': digest(library/'libx264.so.164'), 'rows': loaded})
    traces = list(public_dir.glob('anime-*.tsv'))
    if len(traces) != 1:
        raise RuntimeError('Expected exactly one complete public process trace')
    boundary = load('public').read_public(traces[0], lifetimes)
    internal = load('internal').read_internal(folder/'internal', boundary, lifetimes) if observing else None
    if not observing and (folder/'internal').exists():
        raise RuntimeError('Internal observation leaked into plain/off operand')
    media = load('media')
    tools = json.loads((output/'media-tools.json').read_text())
    segments = []
    if film:
        files = sorted((folder/'render-tmp').iterdir())
        if [p.name for p in files] != [f'{i*24:010}.mp4' for i in range(45)] or any(
                not p.is_file() or p.is_symlink() or p.stat().st_size == 0 for p in files):
            raise RuntimeError('Missing/extra/empty original segment operand')
        concat = folder/'out/styles/preview/anime/film-silent.mp4'
        if not concat.is_file() or concat.stat().st_size == 0:
            raise RuntimeError('Original final silent concat is missing')
        for index, path in enumerate(files):
            report = folder/'reports'/f'segment-{index:02}'
            report.parent.mkdir(exist_ok=True)
            probe, decoded, redecoded = media_reports(path, report, tools)
            bound = media.verify_segment(path, probe, decoded, redecoded,
                                         boundary['segments'][index], pts_base=index*24)
            save(report/'binding.json', bound)
            segments.append(bound)
        probe, decoded, redecoded = media_reports(concat, folder/'reports/concat', tools)
        binding = media.verify_concat(concat, probe, decoded, redecoded, segments)
        save(folder/'concat-binding.json', binding)
        decoded_rows = binding['decoded']
    else:
        decoded_rows = {}
        for index in range(2):
            path = folder/f'synthetic-{index*24:010}.h264'
            report = folder/f'report-{index}'
            probe, decoded, redecoded = media_reports(path, report, tools)
            bound = media.verify_synthetic(path, boundary['segments'][index], pts_base=index*24)
            save(report/'binding.json', bound)
            decoded_local = media.read_decoded(decoded, redecoded, frames=24)
            # The binder defines local decoded PTS; comparison retains both lifetimes.
            decoded_rows[index] = decoded_local
            segments.append(bound)
    return {'boundary': boundary, 'internal': internal, 'decoded': decoded_rows,
            'segments': segments, 'folder': folder,
            'media_sha256': {str(p.relative_to(folder)): digest(p) for p in folder.rglob('*')
                             if p.is_file() and p.suffix in ('.mp4', '.h264')}}


def transparency(left, right):
    comparison = load('public').compare_public(left['boundary'], right['boundary'])
    comparison.update({'decoded_equal': left['decoded'] == right['decoded'],
                       'container_bytes_equal': left['media_sha256'] == right['media_sha256'],
                       'all_packet_header_bindings_valid': True})
    comparison['passed'] = comparison['observed_inputs_equal'] and comparison['native_payloads_equal'] and comparison['decoded_equal']
    return comparison


def experiment(root, output, work):
    capacity(output, 'experiment-preflight.json')
    results = []
    try:
        for index, (label, variant, observing, film) in enumerate(MANIFEST):
            # Persist the finite schedule before native launch; no code path loops
            # back, increments a retry budget or substitutes another style.
            save(output/f'launch-{index}.json', {'ordinal': index, 'label': label,
                 'frames': 1080 if film else 48, 'lifetimes': 45 if film else 2})
            result = launch(label, variant, observing, film, root, output, work)
            results.append(result)
            if index in (1, 2):
                compared = transparency(results[0], result)
                save(output/(label+'-transparency.json'), compared)
                if not compared['passed']:
                    save(output/'verdict.json', {'status': 'SYNTHETIC_TRANSPARENCY_FAILURE',
                         'phase': label, 'metric': None, 'native_processes_launched': index+1})
                    raise RuntimeError('Synthetic transparency failed; stop')
            elif index == 4:
                compared = transparency(results[3], results[4])
                save(output/'film-P-A-transparency.json', compared)
                public = load('public')
                historical = [public.read_public(output/'historical'/f'anime-{n}.tsv', 45) for n in (0, 1)]
                binding = {label: [public.compare_public(current['boundary'], old) for old in historical]
                           for label, current in (('P', results[3]), ('A', results[4]))}
                save(output/'historical-observed-binding.json', binding)
                if not compared['passed']:
                    save(output/'verdict.json', {'status': 'TRANSPARENCY_FAILURE_OR_AMBIGUOUS_NATURAL_VARIATION',
                         'metric': None, 'observer_causation_proven': False, 'native_processes_launched': 5})
                    raise RuntimeError('Full P/A transparency failed; B is not authorized')
                if not all(row['observed_inputs_equal'] for group in binding.values() for row in group):
                    save(output/'verdict.json', {'status': 'HISTORICAL_OBSERVED_INPUT_MISMATCH',
                         'metric': None, 'complete_semantic_input_equality_established': False,
                         'native_processes_launched': 5})
                    raise RuntimeError('Historical observed binding failed; B is not authorized')
        a, b = results[4:6]
        public = load('public').compare_public(a['boundary'], b['boundary'])
        internal = load('internal').compare_internal(a['internal'], b['internal'])
        decoded_equal = a['decoded'] == b['decoded']
        reproduced = not public['native_payloads_equal'] or not decoded_equal
        stage = 'not_localized'
        if reproduced:
            for name in ('entry', 'coded', 'post_aq', 'qpm', 'pre_slice'):
                if internal[name+'_changed_pts']:
                    stage = name
                    break
        save(output/'verdict.json', {
            'status': 'OBSERVED_DIVERGENCE_FULL_HISTORY' if reproduced else 'NO_REPRODUCTION_FULL_HISTORY',
            'first_measured_different_stage': stage, 'public': public, 'internal': internal,
            'decoded_equal': decoded_equal, 'metric': None, 'baseline': None, 'best': None,
            'native_processes_launched': 6, 'frames': 3384, 'lifetimes': 141,
            'cause_established': False, 'complete_semantic_input_equality_established': False,
            'mandatory_stop_reached': True, 'retry_authorized': False,
            'historical_heap_library_and_cache_state_reproduced': False,
        })
    finally:
        same = frozen_inputs(root) == json.loads((output/'frozen-inputs.json').read_text())
        save(output/'frozen-inputs-after.json', {'unchanged': same, 'actual': frozen_inputs(root)})
        if not same:
            raise RuntimeError('Frozen source/media/license/lock operands changed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('prepare', 'build', 'experiment', 'snapshot'))
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--work', required=True, type=Path)
    args = parser.parse_args()
    if (os.environ.get('GITHUB_ACTIONS') != 'true'
            or os.environ.get('RAYON_NUM_THREADS') != '2'
            or os.environ.get('FFRAMES_NUM_THREADS') != '1'):
        raise SystemExit('Disposable exact-SHA GitHub CI with original worker settings only')
    root, output, work = (p.resolve() for p in (args.root, args.output, args.work))
    try:
        if args.phase == 'prepare':
            prepare(root, output, work)
        elif args.phase == 'build':
            build(root, output, work)
        elif args.phase == 'experiment':
            experiment(root, output, work)
        else:
            output.mkdir(exist_ok=True)
            retain_build(output, work)
            save(output/'snapshot-job-status.json', {
                'observed_job_status': os.environ.get('DIAGNOSTIC_JOB_STATUS'),
                'verdict_present': (output/'verdict.json').is_file(),
                'failure_files': [p.name for p in output.glob('*-failure.json')],
                'retention_limit': 'Hard timeout or runner loss can prevent always steps',
            })
    except Exception as error:
        if output.is_dir():
            save(output/(args.phase+'-failure.json'), {
                'status': 'failed', 'exception': type(error).__name__,
                'reason': str(error) if isinstance(error, (RuntimeError, ValueError)) else type(error).__name__,
                'metric': None, 'no_retry': True, 'partial_operands_retained_in_place': True})
        # Do not expose credential-bearing network redirect/exception details.
        raise SystemExit('Diagnostic failed: '+type(error).__name__) from None


if __name__ == '__main__':
    main()
