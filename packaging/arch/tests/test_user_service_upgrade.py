"""Exercise package transaction ordering without controlling a real user manager."""
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest

INSTALL = Path(__file__).resolve().parents[1] / 'vpnrouter-headless.install'


class UserServiceUpgradeTests(unittest.TestCase):
    def exercise(self, remove=False, reenter=False, legacy_marker=False, owner_mask=False):
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
    if [[ "$2" == is-enabled && "$1" == 1001 && {"yes" if owner_mask else "no"} == yes ]]; then
        printf '%s\\n' masked-runtime
        return 1
    fi
    [[ "$2" != is-active || "$1" == 1000 ]]
}}
setcap() {{ printf '%s\\n' capabilities >> {shlex.quote(str(log))}; }}
{'mkdir -p "$_vpnrouter_upgrade_state"; touch "$_vpnrouter_upgrade_state/1000"' if legacy_marker else ''}
{'pre_upgrade; pre_upgrade; post_upgrade' if reenter else 'pre_remove; post_remove' if remove else 'pre_upgrade; post_upgrade'}
[[ ! -e "$_vpnrouter_upgrade_state" ]]
'''
            result = subprocess.run(['bash', '-c', script], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            return log.read_text().splitlines()

    def test_upgrade_masks_before_stop_and_restores_after_capabilities(self):
        self.assertEqual(self.exercise(), [
            '1000 is-enabled', '1000 is-active --quiet', '1000 mask --runtime --now',
            '1001 is-enabled', '1001 is-active --quiet', '1001 mask --runtime --now', 'capabilities',
            '1000 unmask --runtime', '1000 daemon-reload', '1000 start',
            '1001 unmask --runtime', '1001 daemon-reload'])

    def test_removal_never_restarts(self):
        events = self.exercise(remove=True)
        self.assertIn('1000 mask --runtime --now', events)
        self.assertIn('1001 mask --runtime --now', events)
        self.assertIn('1001 unmask --runtime', events)
        self.assertNotIn('1000 start', events)
        self.assertNotIn('1001 start', events)
        self.assertNotIn('capabilities', events)

    def test_retry_preserves_original_restart_intent_while_service_is_masked(self):
        events = self.exercise(reenter=True)
        self.assertEqual(events.count('1000 is-active --quiet'), 1)
        self.assertEqual(events.count('1000 mask --runtime --now'), 2)
        self.assertEqual(events.count('1001 mask --runtime --now'), 2)
        self.assertEqual(events.count('1000 start'), 1)
        self.assertNotIn('1001 start', events)

    def test_interrupted_older_upgrade_empty_marker_still_restarts(self):
        events = self.exercise(legacy_marker=True)
        self.assertNotIn('1000 is-active --quiet', events)
        self.assertIn('1000 start', events)
        self.assertNotIn('1001 start', events)


    def test_owner_runtime_mask_is_preserved(self):
        events = self.exercise(owner_mask=True)
        self.assertNotIn('1001 mask --runtime --now', events)
        self.assertNotIn('1001 unmask --runtime', events)
        self.assertNotIn('1001 start', events)
        self.assertIn('1000 start', events)


if __name__ == '__main__':
    unittest.main()
