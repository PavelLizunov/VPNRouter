#!/usr/bin/env python3
"""Locked, worker-only full-export evaluator. No final-resolution workload exists."""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile

HERE = Path(__file__).resolve().parent
REL = Path('tools/help-videos/fframes-vpnrouter')
POLICY = json.loads((HERE/'policy.json').read_text())
STYLES = ('anime', 'swiss', 'blueprint', 'editorial', 'terminal', 'paper', 'kinetic', 'cinema')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    with path.open('x') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')


def ref(path, repo):
    return {'path': path.relative_to(repo).as_posix(), 'sha256': digest(path)}


def checker(root):
    namespace = {'__name__': 'locked_checker', '__file__': str(root/'verify_styles.py')}
    exec(compile((HERE/'checker.py').read_text(), 'locked_checker.py', 'exec'), namespace)
    return namespace


def negative_controls():
    """Exercise actual consumer-checker functions with deliberately invalid inputs."""
    result = {}
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        check = checker(root)

        def rejects(name, action):
            try:
                action()
            except (AssertionError, ValueError):
                result[name] = True
            else:
                raise AssertionError(f'Negative control accepted: {name}')

        film = root/'film.mp4'
        film.write_bytes(b'valid')
        manifest = root/'SHA256SUMS.txt'
        manifest.write_text(f'{digest(film)}  film.mp4\n')
        check['verify_checksums'](manifest, root)
        film.write_bytes(b'corrupt')
        rejects('changed_checksum', lambda: check['verify_checksums'](manifest, root))
        manifest.write_text(f'{digest(film)}  ../escape.mp4\n')
        rejects('path_escape', lambda: check['verify_checksums'](manifest, root))

        def sample():
            return {'streams': [dict(codec_type='video', width=854, height=480,
                    codec_name='h264', pix_fmt='yuv420p', avg_frame_rate='30/1',
                    nb_frames='1080', duration='36.0'), dict(codec_type='audio',
                    codec_name='aac', duration='36.0')],
                    'format': {'tags': {'title': 'Choose your routes | anime'}}}

        check['verify_streams'](sample(), 'preview', 'anime')
        bad = sample()
        bad['streams'][0]['width'] = 1920
        rejects('wrong_dimensions', lambda: check['verify_streams'](bad, 'preview', 'anime'))
        bad = sample()
        bad['format']['tags']['title'] = 'wrong title'
        rejects('wrong_title', lambda: check['verify_streams'](bad, 'preview', 'anime'))
        rejects('duplicate_style', lambda: check['selected_styles']('final', ['anime', 'anime']))
        rejects('implicit_final', lambda: check['selected_styles']('final', []))

        files = {}
        for item in check['SOURCE']:
            path = root/item
            if item in ('src', 'media', 'licenses'):
                path = path/'fixture.txt'
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f'fixture for {item}\n')
            files[path.relative_to(root).as_posix()] = path.read_bytes()
        archive = root/'source.zip'

        def archive_bytes(values):
            with zipfile.ZipFile(archive, 'w') as output:
                for name, data in values.items():
                    output.writestr(name, data)

        archive_bytes(files)
        with contextlib.redirect_stdout(io.StringIO()):
            check['verify_archive'](archive, root)
        changed = dict(files, **{'Cargo.toml': b'corrupt'})
        archive_bytes(changed)
        rejects('archive_changed_bytes', lambda: check['verify_archive'](archive, root))
        archive_bytes(dict(files, **{'unexpected': b'bad'}))
        rejects('archive_extra_entry', lambda: check['verify_archive'](archive, root))
    assert set(result) == set(POLICY['negative_controls'])
    return result


def check_sources(root):
    expected = json.loads((HERE/'protected-inputs.json').read_text())
    for name, sha in expected.items():
        assert digest(root/name) == sha, f'Protected input changed: {name}'
    # Added source/assets cannot hide outside the original hash inventory.
    actual = {p.relative_to(root).as_posix() for folder in ('src', 'media', 'licenses')
              for p in (root/folder).rglob('*') if p.is_file()}
    assert actual == {n for n in expected if n.split('/')[0] in ('src', 'media', 'licenses')}


def decoded_signature(film, kind):
    args = ['ffmpeg', '-v', 'error', '-i', str(film)]
    if kind == 'video':
        args += ['-map', '0:v:0', '-an', '-pix_fmt', 'yuv420p', '-c:v', 'rawvideo']
    else:
        args += ['-map', '0:a:0', '-vn', '-c:a', 'pcm_s32le']
    return subprocess.check_output(args + ['-f', 'hash', '-hash', 'sha256', '-'], text=True).strip()


def run_export(root, target, log, profile=False):
    env = dict(os.environ, CARGO_BUILD_JOBS='2', RAYON_NUM_THREADS='2', FFMPEG_FORCE_BUILD='1',
               CARGO_TARGET_DIR=str(target), PYTHONDONTWRITEBYTECODE='1')
    command = ['bash', 'export-styles.sh', 'preview']
    if profile:
        env['PS4'] = '+${EPOCHREALTIME} ${BASH_SOURCE}:${LINENO}: '
        command.insert(1, '-x')
    started = time.perf_counter()
    with log.open('x') as stream:
        proc = subprocess.Popen(command, cwd=root, env=env, stdout=stream, stderr=subprocess.STDOUT)
        _, status, usage = os.wait4(proc.pid, 0)
        code = os.waitstatus_to_exitcode(status)
        proc.returncode = code
    seconds = time.perf_counter() - started
    assert code == 0, f'Export exited {code}; inspect {log}'
    return {'seconds': seconds, 'peak_rss_bytes': usage.ru_maxrss * 1024,
            'user_seconds': usage.ru_utime, 'system_seconds': usage.ru_stime}


def inspect(root):
    check_sources(root)
    check = checker(root)
    for item in check['SOURCE']:
        assert (root/item).exists(), f'Required archive input missing: {item}'
    old_args = sys.argv
    try:
        sys.argv = ['locked_checker.py', '--mode', 'preview']
        check['main']()
    finally:
        sys.argv = old_args
    signatures = {style: {kind: decoded_signature(root/f'out/styles/preview/{style}/film.mp4', kind)
                         for kind in ('video', 'audio')} for style in STYLES}
    assert len({v['video'] for v in signatures.values()}) == 8
    return signatures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--negative-controls', action='store_true')
    parser.add_argument('--repo', type=Path)
    parser.add_argument('--control')
    parser.add_argument('--candidate')
    parser.add_argument('--phase', choices=('baseline', 'search', 'confirmation'), default='baseline')
    parser.add_argument('--evidence', type=Path)
    parser.add_argument('--pairs', type=int, choices=(3, 5), default=3)
    args = parser.parse_args()
    negatives = negative_controls()
    if args.negative_controls:
        print(json.dumps(negatives, indent=2))
        return
    assert os.environ.get('GITHUB_ACTIONS') == 'true', 'Heavy evaluator runs only on Actions'
    assert args.repo and args.control and args.evidence
    repo = args.repo.resolve()
    evidence = args.evidence.resolve()
    assert evidence.is_relative_to(repo)
    evidence.mkdir(parents=True, exist_ok=False)
    roles = {'control': args.control}
    if args.candidate:
        roles['candidate'] = args.candidate
    assert args.phase == 'baseline' or args.candidate
    save(evidence/'run.json', {'phase': args.phase, 'commits': roles,
         'order': 'AB/BA with reversed confirmation start',
         'runner': os.environ.get('RUNNER_NAME'), 'github_run_id': os.environ.get('GITHUB_RUN_ID')})
    for role, sha in roles.items():
        assert len(sha) == 40 and all(c in '0123456789abcdef' for c in sha)
        assert subprocess.check_output(['git', 'rev-parse', f'{sha}^{{commit}}'], cwd=repo, text=True).strip() == sha
    # Confirmation may name the accepted candidate commit rather than its later
    # evidence/request-only child. Never label a different implementation green.
    tested = roles.get('candidate', roles['control'])
    subprocess.run(['git', 'diff', '--exit-code', tested, os.environ['GITHUB_SHA'], '--',
                    str(REL), f':(exclude){REL}/performance/request.json'], cwd=repo, check=True)
    roots, records, signatures, targets = {}, {}, {}, {}
    try:
        for role, sha in roles.items():
            worktree = Path(tempfile.mkdtemp(prefix=f'fframes-worktree-{role}-', dir=os.environ['RUNNER_TEMP']))
            subprocess.run(['git', 'worktree', 'add', '--detach', str(worktree), sha], cwd=repo, check=True)
            root = worktree/REL
            roots[role] = root
            check_sources(root)
            for name, expected in json.loads((HERE/'workflow-inputs.json').read_text()).items():
                assert digest(worktree/name) == expected, f'Workflow changed during campaign: {name}'
            # Give each role its own warm build state; switching roles must not charge
            # just one side for Cargo path/fingerprint invalidation. Preserve the
            # original restored target because native cache metadata may refer to it.
            cached = repo/REL/'target'
            if role == 'control':
                target = cached
                target.mkdir(exist_ok=True)
            else:
                target = Path(tempfile.mkdtemp(prefix=f'fframes-target-{role}-', dir=os.environ['RUNNER_TEMP']))
                if cached.is_dir():
                    shutil.copytree(cached, target, dirs_exist_ok=True)
            (root/'target').symlink_to(target, target_is_directory=True)
            targets[role] = target
            records[role] = []
            warm = run_export(root, target, evidence/f'{role}-warmup.log')
            save(evidence/f'{role}-warmup.json', warm)
            signatures[role] = inspect(root)
        assert len(roles) == 1 or signatures['control'] == signatures['candidate'], 'Decoded media changed'
        if args.phase == 'baseline':
            profiled = run_export(roots['control'], targets['control'], evidence/'control-profile.log', profile=True)
            save(evidence/'control-profile.json', profiled)
        for pair in range(args.pairs):
            order = list(roles)
            reverse = (pair % 2 == 1) ^ (args.phase == 'confirmation')
            if reverse:
                order.reverse()
            for role in order:
                sample = run_export(roots[role], targets[role], evidence/f'{role}-{pair}.log')
                records[role].append(sample)
                current = inspect(roots[role])
                assert current == signatures['control'], 'Decoded output changed between runs'
                print(json.dumps({'pair': pair, 'role': role, **sample}), flush=True)
        # Collect output artifacts before publishing any passed receipts.
        for role in roles:
            destination = evidence/f'{role}-outputs'
            shutil.copytree(roots[role]/'out/styles', destination)
    except Exception as error:
        save(evidence/'failure.json', {'phase': args.phase, 'commits': roles,
             'status': 'failed', 'metric': None, 'error': str(error),
             'logs': sorted(p.name for p in evidence.glob('*.log'))})
        raise
    finally:
        for role, root in roots.items():
            worktree = root.parents[2]
            assert worktree.resolve().parent == Path(os.environ['RUNNER_TEMP']).resolve()
            assert worktree.name.startswith(f'fframes-worktree-{role}-')
            link = root/'target'
            if link.is_symlink():
                link.unlink()
            subprocess.run(['git', 'worktree', 'remove', '--force', str(worktree)], cwd=repo, check=True)
    # Copy and required cleanup must both succeed before any passed receipts.
    workload = POLICY['workloads']['confirmation' if args.phase == 'confirmation' else 'search']
    for role, sha in roles.items():
        quality = dict(kind='quality', commit=sha, workload=workload,
                       target_id=POLICY['target']['id'], status='passed', negative_controls=negatives,
                       checks={name: True for name in POLICY['quality_checks']})
        qpath = evidence/f'{role}-quality.json'
        save(qpath, quality)
        save(evidence/f'{role}-measurement.json', dict(kind='measurement', commit=sha,
             workload=workload, target_id=POLICY['target']['id'],
             metric={'name': 'end_to_end_wall_seconds', 'unit': 's'},
             samples=[r['seconds'] for r in records[role]], quality=ref(qpath, repo),
             resources={'peak_rss_bytes': max(r['peak_rss_bytes'] for r in records[role])},
             raw=records[role], signatures=signatures[role]))


if __name__ == '__main__':
    main()
