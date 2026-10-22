#!/usr/bin/env python3
"""CI-only installed package acceptance in an isolated Arch network namespace."""

import argparse
import json
import os
from pathlib import Path
import select
import subprocess
import time

ROOT = Path('/usr/lib/vpnrouter-headless')
RUNTIME = ROOT / 'runtime/sing-box'


class Backend:
    def __init__(self):
        self.process = subprocess.Popen(
            ['runuser', '-u', 'builder', '--', str(ROOT / 'VPNRouter.Headless'), '--stdio'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None, bufsize=0,
            env={key: value for key, value in os.environ.items()
                 if not key.startswith('DOTNET_ROOT')},
        )
        self.pending = b''
        self.sequence = 0

    def send(self, method, params=None):
        self.sequence += 1
        identity = f'ci-{self.sequence}'
        self.process.stdin.write((json.dumps({'v': 1, 'id': identity, 'method': method,
                                              'params': params or {}}) + '\n').encode())
        return identity

    def response(self, identity, timeout=90):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if b'\n' not in self.pending:
                ready, _, _ = select.select([self.process.stdout], [], [], min(1, deadline - time.monotonic()))
                if not ready:
                    continue
                chunk = os.read(self.process.stdout.fileno(), 65536)
                assert chunk, f'Backend exited: {self.process.poll()}'
                self.pending += chunk
                continue
            line, self.pending = self.pending.split(b'\n', 1)
            frame = json.loads(line)
            if frame.get('id') == identity:
                assert 'error' not in frame, frame
                return frame['result']
        raise TimeoutError(identity)

    def call(self, method, params=None):
        return self.response(self.send(method, params))

    def close(self):
        self.process.stdin.close()
        try:
            assert self.process.wait(timeout=20) == 0
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
            raise


def snapshot(expected, reason=None):
    backend = Backend()
    try:
        result = backend.call('snapshot')
        print(json.dumps(result), flush=True)
        assert result['capabilities'] == {'connect': expected, 'killSwitch': False, 'dnsLockdown': False}, result
        if reason:
            assert reason in result['unavailableReason'], result
        return result
    finally:
        backend.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source-sha', required=True)
    args = parser.parse_args()
    manifest = json.loads((ROOT / 'manifest.json').read_text())
    assert manifest['source_commit'] == args.source_sha
    snapshot(True)

    # Exercise the real policy through the normal-user installed backend.
    subprocess.run(['setcap', '-r', str(RUNTIME)], check=True)
    try:
        snapshot(False, 'missing capabilities')
    finally:
        subprocess.run(['setcap', 'cap_net_admin,cap_net_raw+ep', str(RUNTIME)], check=True)
    for target in (RUNTIME, RUNTIME.parent):
        original = target.stat().st_mode & 0o7777
        target.chmod(original | 0o020)
        try:
            snapshot(False, 'untrusted')
        finally:
            target.chmod(original)
    os.chown(RUNTIME, 1000, 1000)
    try:
        snapshot(False, 'untrusted')
    finally:
        os.chown(RUNTIME, 0, 0)
        subprocess.run(['setcap', 'cap_net_admin,cap_net_raw+ep', str(RUNTIME)], check=True)
    saved = RUNTIME.with_name('sing-box.saved')
    RUNTIME.rename(saved)
    RUNTIME.symlink_to(saved)
    try:
        snapshot(False, 'untrusted')
    finally:
        RUNTIME.unlink()
        saved.rename(RUNTIME)

    # NET_ADMIN alone must suffice; BIND_SERVICE and NET_RAW are not required.
    subprocess.run(['setcap', 'cap_net_admin+ep', str(RUNTIME)], check=True)
    snapshot(True)
    backend = Backend()
    try:
        current = backend.call('snapshot')
        config = {
            'log': {'level': 'info'},
            'inbounds': [{'type': 'tun', 'tag': 'tun-in', 'interface_name': 'vpnrouter-ci',
                          'address': ['172.30.20.1/30'], 'auto_route': False, 'stack': 'system'}],
            'outbounds': [{'type': 'direct', 'tag': 'direct'}],
        }
        imported = backend.call('custom.import', {'revision': current['revision'], 'name': 'ci-tun',
                                                   'text': json.dumps(config)})
        identity = backend.send('connect', {'revision': imported['revision']})
        deadline = time.monotonic() + 60
        while not Path('/sys/class/net/vpnrouter-ci').exists() and time.monotonic() < deadline:
            assert backend.process.poll() is None
            time.sleep(0.2)
        assert Path('/sys/class/net/vpnrouter-ci').exists(), 'Backend did not create the real TUN'
        print('Normal-user backend created vpnrouter-ci TUN with CAP_NET_ADMIN only', flush=True)
        backend.call('disconnect')
        deadline = time.monotonic() + 10
        while Path('/sys/class/net/vpnrouter-ci').exists() and time.monotonic() < deadline:
            time.sleep(0.1)
        assert not Path('/sys/class/net/vpnrouter-ci').exists(), 'Disconnect left the TUN alive'
        print('Normal-user backend disconnected and removed TUN', flush=True)
    finally:
        backend.close()
        subprocess.run(['setcap', 'cap_net_admin,cap_net_raw+ep', str(RUNTIME)], check=True)


if __name__ == '__main__':
    main()
