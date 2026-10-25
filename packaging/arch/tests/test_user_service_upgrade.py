"""Exercise package transaction ordering without controlling a real user manager."""
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest

INSTALL = Path(__file__).resolve().parents[1] / 'vpnrouter-headless.install'


class UserServiceUpgradeTests(unittest.TestCase):
    def exercise(self, remove=False):
        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp) / 'state'
            log = Path(tmp) / 'events'
            script = f'''
set -eu
source {shlex.quote(str(INSTALL))}
_vpnrouter_upgrade_state={shlex.quote(str(state))}
_vpnrouter_managers() {{ printf '%s\\n' 1000 1001; }}
_vpnrouter_userctl() {{
    printf '%s\\n' "$*" >> {shlex.quote(str(log))}
    [[ "$2" != is-active || "$1" == 1000 ]]
}}
setcap() {{ printf '%s\\n' capabilities >> {shlex.quote(str(log))}; }}
{'pre_remove; post_remove' if remove else 'pre_upgrade; post_upgrade'}
[[ ! -e "$_vpnrouter_upgrade_state" ]]
'''
            result = subprocess.run(['bash', '-c', script], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            return log.read_text().splitlines()

    def test_upgrade_masks_before_stop_and_restores_after_capabilities(self):
        self.assertEqual(self.exercise(), [
            '1000 is-active --quiet', '1000 mask --runtime --now',
            '1001 is-active --quiet', 'capabilities', '1000 unmask --runtime',
            '1000 daemon-reload', '1000 start'])

    def test_removal_never_restarts(self):
        events = self.exercise(remove=True)
        self.assertIn('1000 mask --runtime --now', events)
        self.assertNotIn('1000 start', events)
        self.assertNotIn('capabilities', events)


if __name__ == '__main__':
    unittest.main()
