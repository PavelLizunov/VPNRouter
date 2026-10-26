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
            assert result['unavailableReasonCode'] == ('runtime-capabilities' if reason == 'lacks CAP_NET_ADMIN' else 'runtime-untrusted'), result
        return result
    finally:
        backend.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--offline', action='store_true', help='Check installed protocol and policy without starting TUN')
    args = parser.parse_args()
    manifest = json.loads((ROOT / 'manifest.json').read_text())
    assert manifest['source_commit'] == args.source_sha
    current = snapshot(True)
    assert current['unavailableReasonCode'] is None
    backend = Backend()
    try:
        apps = backend.call('apps.list')
        assert isinstance(apps['runningApps'], list) and len(apps['runningApps']) <= 200
        assert all(set(app) == {'name', 'displayName', 'icon', 'desktopId', 'hasWindow'}
                   for app in apps['runningApps'])
        assert any(item['status'] == 'info' for item in backend.call('diagnostics.check')['items'])
    finally:
        backend.close()

    # Exercise the real policy through the normal-user installed backend.
    subprocess.run(['setcap', '-r', str(RUNTIME)], check=True)
    try:
        result = snapshot(False, 'lacks CAP_NET_ADMIN')
        assert 'sudo setcap cap_net_admin,cap_net_raw+ep /usr/lib/vpnrouter-headless/runtime/sing-box' in result['unavailableReason'], result
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
    if args.offline:
        subprocess.run(['setcap', 'cap_net_admin,cap_net_raw+ep', str(RUNTIME)], check=True)
        print('Installed offline protocol and runtime-policy checks passed; no TUN started', flush=True)
        return
    backend = Backend()
    try:
        current = backend.call('snapshot')
        config = {
            'log': {'level': 'info'},
            'inbounds': [{'type': 'tun', 'tag': 'tun-in', 'interface_name': 'vpnrouter-ci',
                          'address': ['172.30.20.1/30'], 'auto_route': False, 'stack': 'system'}],
            'outbounds': [{'type': 'direct', 'tag': 'direct'},
                          {'type': 'socks', 'tag': 'proxy', 'server': '127.0.0.1', 'server_port': 10808}],
        }
        imported = backend.call('custom.import', {'revision': current['revision'], 'name': 'ci-tun',
                                                   'text': json.dumps(config)})
        identity = backend.send('connect', {'revision': imported['revision']})
        connected = backend.response(identity)
        assert connected['state'] == 'connected', connected
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
