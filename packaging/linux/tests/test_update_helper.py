import os
from pathlib import Path
import subprocess
import tempfile
import unittest


HELPER = Path(__file__).resolve().parents[1] / "vpnrouter-update-helper"


class UpdateHelperTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(self.enterContext(tempfile.TemporaryDirectory(dir="/tmp")))
        self.install = self.root / "vpnrouter"
        self.source = self.root / "source"
        self.capabilities = self.root / "capabilities"
        self.env = dict(os.environ, VPNROUTER_TEST_CAPABILITIES=str(self.capabilities))
        tools = self.root / "tools"
        tools.mkdir()
        # Use the fixture owner in place of root. Real file types and modes
        # are checked, and the capability tool writes only to the fixture.
        self.stub(tools, "stat", """#!/bin/sh
case "$4" in
    /|/tmp) printf '0:755\n' ;;
    *) mode=$(/usr/bin/stat -c %a -- "$4") || exit 1
       printf '0:%s\n' "$mode" ;;
esac
""")
        self.stub(tools, "setcap", """#!/bin/sh
printf '%s\n' "$*" >> "$VPNROUTER_TEST_CAPABILITIES"
""")
        self.helper = self.root / "helper"
        self.helper.write_text(HELPER.read_text()
            .replace("/opt/vpnrouter", str(self.install))
            .replace("PATH=/usr/sbin:", f"PATH={tools}:/usr/sbin:")
            .replace("-uid 0", f"-uid {os.getuid()}"))
        self.make_tree(self.install, "old", 0o755, 0o644)
        self.make_tree(self.source, "new", 0o700, 0o600)

    @staticmethod
    def stub(directory, name, script):
        path = directory / name
        path.write_text(script)
        path.chmod(0o755)

    @staticmethod
    def make_tree(root, contents, directory_mode, file_mode):
        nested = root / "deps"
        nested.mkdir(parents=True)
        for name in ("VPNRouter.App", "sing-box", "deps/tool"):
            (root / name).write_text(contents)
            (root / name).chmod(directory_mode)
        (nested / "library.dll").write_text(contents)
        (nested / "library.dll").chmod(file_mode)
        root.chmod(directory_mode)
        nested.chmod(directory_mode)

    def apply(self):
        result = subprocess.run(
            ["/bin/bash", self.helper, self.source, self.install],
            capture_output=True, text=True, env=self.env, umask=0o077, timeout=10)
        self.assertFalse(list(self.root.glob(".vpnrouter-update.*")))
        return result

    def test_private_payload_modes_are_normalized(self):
        for name in ("VPNRouter.App", "sing-box"):
            (self.source / name).chmod(0o600)
        result = self.apply()
        self.assertEqual(0, result.returncode, result.stderr)
        for name in ("VPNRouter.App", "sing-box"):
            self.assertEqual(0o755, (self.install / name).stat().st_mode & 0o777)
        nested = self.install / "deps"
        self.assertEqual("new", (nested / "library.dll").read_text())
        self.assertEqual(0o755, nested.stat().st_mode & 0o777)
        self.assertEqual(0o644, (nested / "library.dll").stat().st_mode & 0o777)
        self.assertEqual(0o755, (nested / "tool").stat().st_mode & 0o777)
        applied, verified = self.capabilities.read_text().splitlines()
        self.assertEqual("-q -v " + applied, verified)

    def test_rejects_unsupported_entries_before_copy(self):
        for tree, code in ((self.source, 5), (self.install, 3)):
            with self.subTest(tree=tree):
                entry = tree / "deps" / "link"
                entry.symlink_to("library.dll")
                self.addCleanup(entry.unlink, missing_ok=True)
                result = self.apply()
                self.assertEqual(code, result.returncode, result.stderr)
                self.assertEqual("old", (self.install / "deps" / "library.dll").read_text())
                self.assertFalse(self.capabilities.exists())
                entry.unlink()

    def test_rejects_fifo_before_copy(self):
        os.mkfifo(self.source / "deps" / "unexpected")
        result = self.apply()
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertEqual("old", (self.install / "deps" / "library.dll").read_text())
        self.assertFalse(self.capabilities.exists())

    def test_rejects_unreadable_existing_file_before_copy(self):
        stale = self.install / "deps" / "old-library.dll"
        stale.write_text("old")
        stale.chmod(0o600)
        result = self.apply()
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("unreadable installation tree", result.stderr)
        self.assertEqual("old", (self.install / "deps" / "library.dll").read_text())
        self.assertFalse(self.capabilities.exists())
