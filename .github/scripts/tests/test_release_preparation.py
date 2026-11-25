import hashlib
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import call, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import prepare_release as release
import stamp_release_version as version

TAG = "v2.51.5-r3"
SHA = "a" * 40
RUN = {
    "id": 42, "run_attempt": 2, "head_sha": SHA, "head_branch": TAG,
    "path": ".github/workflows/test-windows-update.yml", "event": "workflow_dispatch",
    "status": "completed", "conclusion": "success",
}


class ReleasePreparationTests(unittest.TestCase):
    def test_stamp_verifies_tag_and_head_and_only_changes_the_version_line(self):
        with tempfile.TemporaryDirectory() as workspace:
            source = Path(workspace) / "AppVersion.cs"
            before = b'namespace VPNRouter.Core;\r\n    public static readonly string Version = "2.50.0";\r\n'
            source.write_bytes(before)
            with patch.object(version, "VERSION_SOURCE", source), \
                    patch.object(version.subprocess, "check_output", side_effect=[SHA, SHA]), \
                    patch.object(version.subprocess, "run") as fetch:
                version.stamp(TAG[1:], f"refs/tags/{TAG}", SHA)
            fetch.assert_called_once_with(["git", "fetch", "--no-tags", "origin", f"refs/tags/{TAG}"], check=True)
            self.assertEqual(source.read_bytes(), before.replace(b"2.50.0", TAG[1:].encode()))

    def test_stamp_refuses_tag_mismatch_before_fetch(self):
        with patch.object(version.subprocess, "run") as fetch:
            with self.assertRaisesRegex(ValueError, "exact workflow tag"):
                version.stamp(TAG[1:], "refs/heads/main", SHA)
        fetch.assert_not_called()

    def test_stamp_refuses_moved_remote_tag_before_touching_version(self):
        with patch.object(version.subprocess, "check_output", side_effect=[SHA, "b" * 40]), \
                patch.object(version.subprocess, "run"), patch.object(version, "VERSION_SOURCE") as source:
            with self.assertRaisesRegex(ValueError, "exact workflow commit"):
                version.stamp(TAG[1:], f"refs/tags/{TAG}", SHA)
        source.read_bytes.assert_not_called()

    def test_incomplete_signing_configuration_stops_before_dispatch(self):
        with patch.object(release, "api", side_effect=[
            [{"secrets": [{"name": "SIGNPATH_API_TOKEN"}]}], [{"variables": []}],
        ]):
            with self.assertRaisesRegex(release.PreparationError, "partially configured"):
                release.signing_configured()

    def test_new_candidate_creates_the_draft_before_dispatching_the_first_job(self):
        draft = {"id": 1, "tag_name": TAG, "draft": True, "prerelease": True, "assets": []}
        stopped = RuntimeError("recorded first dispatch")
        with patch.object(release, "command", side_effect=[
            "https://github.com/PavelLizunov/VPNRouter", "https://github.com/PavelLizunov/VPNRouter",
            "", SHA,
        ]), patch.object(release, "tag_commit", return_value=None), \
                patch.object(release, "check_source"), patch.object(release, "find_release", return_value=None), \
                patch.object(release, "signing_configured", return_value=False), \
                patch.object(release, "create_draft", return_value=draft) as create, \
                patch.object(release, "launch_or_resume", side_effect=stopped) as dispatch:
            calls = unittest.mock.Mock()
            calls.attach_mock(create, "create")
            calls.attach_mock(dispatch, "dispatch")
            with self.assertRaisesRegex(RuntimeError, "recorded first dispatch"):
                release.prepare(TAG, None, None)
        self.assertEqual([event[0] for event in calls.mock_calls], ["create", "dispatch"])

    def test_resume_refuses_to_dispatch_over_partial_platform_upload(self):
        with patch.object(release, "api", return_value={"workflow_runs": []}), \
                patch.object(release, "dispatch") as dispatch:
            with self.assertRaisesRegex(release.PreparationError, "existing assets"):
                release.launch_or_resume("build-mac.yml", TAG, SHA, {}, staged={"a"}, expected={"a", "b"})
        dispatch.assert_not_called()

    def test_windows_resume_dispatches_packages_after_an_ordinary_updater_test(self):
        ordinary = dict(RUN, display_title=f"Windows updater test {TAG}")
        with patch.object(release, "api", return_value={"workflow_runs": [ordinary]}), \
                patch.object(release, "dispatch", return_value=43) as dispatch:
            self.assertEqual(release.launch_or_resume(release.UPDATE, TAG, SHA, {"release_artifacts": "true"}), 43)
        dispatch.assert_called_once_with(release.UPDATE, TAG, {"release_artifacts": "true"})

    def test_wait_refuses_wrong_workflow_or_run_source(self):
        for field, other in {
            "head_sha": "b" * 40, "head_branch": "main", "event": "push",
            "path": ".github/workflows/test.yml",
        }.items():
            with self.subTest(field=field), patch.object(release, "api", return_value=dict(RUN, **{field: other})):
                with self.assertRaisesRegex(release.PreparationError, "exact tag"):
                    release.wait_for_runs({release.UPDATE: 42}, TAG, SHA, deadline=release.monotonic() + 1)

    def test_stable_source_uses_the_verified_candidate_commit(self):
        with patch.object(release, "api", side_effect=[{"commit": {"sha": "b" * 40}}, {"status": "ahead"}]), \
                patch.object(release, "find_release", return_value={"draft": False, "prerelease": True}), \
                patch.object(release, "tag_commit", return_value=SHA), \
                patch.object(release, "command") as command:
            release.check_source("v2.51.5", SHA, None, TAG)
        command.assert_called_once_with("git", "cat-file", "-e", f"{SHA}:.github/scripts/stamp_release_version.py")

    def test_legacy_candidate_tree_is_rejected_before_stable_preparation(self):
        missing_tool = subprocess.CalledProcessError(1, ["git", "cat-file"])
        with patch.object(release, "api", return_value={"commit": {"sha": SHA}}), \
                patch.object(release, "find_release", return_value={"draft": False, "prerelease": True}), \
                patch.object(release, "tag_commit", return_value=SHA), \
                patch.object(release, "command", side_effect=missing_tool):
            with self.assertRaisesRegex(release.PreparationError, "new candidate from main"):
                release.check_source("v2.51.5", SHA, None, TAG)

    def test_workflow_wait_can_resume_with_the_same_run_after_timeout(self):
        pending = dict(RUN, status="in_progress")
        with patch.object(release, "api", side_effect=[pending, RUN]), \
                patch.object(release, "monotonic", side_effect=[0, 1, 1, 2]), \
                patch.object(release, "sleep") as sleep:
            with self.assertRaisesRegex(release.PreparationError, "Rerun the same command"):
                release.wait_for_runs({release.UPDATE: 42}, TAG, SHA, deadline=1)
            result = release.wait_for_runs({release.UPDATE: 42}, TAG, SHA, deadline=10)
        self.assertEqual(result[release.UPDATE]["id"], 42)
        sleep.assert_called_once_with(0)

    def test_dispatch_reports_missing_run_identity_and_empty_response(self):
        with patch.object(release, "command", return_value=""):
            with self.assertRaisesRegex(release.PreparationError, "no usable JSON"):
                release.dispatch(release.UPDATE, TAG, {})
        with patch.object(release, "api", return_value={"html_url": "https://example.invalid/"}):
            with self.assertRaisesRegex(release.PreparationError, "did not return a workflow run ID"):
                release.dispatch(release.UPDATE, TAG, {})

    def test_windows_resume_uploads_only_missing_assets(self):
        existing = {"id": 1, "name": "present", "state": "uploaded", "size": 1, "digest": "sha256:same"}
        draft = {"tag_name": TAG, "draft": True, "prerelease": True, "assets": [existing]}
        with patch.object(release, "gh") as gh, \
                patch.object(release, "verify_windows_files", return_value={"present": "sha256:same",
                                                                          "missing": "sha256:other"}), \
                patch.object(release, "signing_configured", return_value=False), \
                patch.object(release, "tag_commit", return_value=SHA), patch.object(release, "api", return_value=draft):
            release.stage_windows(RUN, TAG, SHA, 1)
        self.assertEqual(gh.call_count, 2)
        working = gh.call_args_list[0].args[-1]
        self.assertEqual(gh.call_args_list, [
            call("run", "download", "42", "--name", f"windows-release-{TAG}-2", "--dir", working,
                 timeout=release.TRANSFER_TIMEOUT),
            call("release", "upload", TAG, str(Path(working) / "missing"), timeout=release.TRANSFER_TIMEOUT),
        ])

    def test_enabling_signing_during_build_blocks_unsigned_upload(self):
        with patch.object(release, "gh") as gh, \
                patch.object(release, "verify_windows_files", return_value={}), \
                patch.object(release, "signing_configured", return_value=True):
            with self.assertRaisesRegex(release.PreparationError, "Signing policy"):
                release.stage_windows(RUN, TAG, SHA, 1)
        self.assertEqual(gh.call_count, 1)
        self.assertEqual(gh.call_args.args[:2], ("run", "download"))

    def test_windows_artifact_must_match_the_exact_inventory_and_sidecar(self):
        with tempfile.TemporaryDirectory() as workspace:
            directory = Path(workspace)
            for name in release.windows_binaries(TAG):
                (directory / name).write_bytes(b"binary")
                good = hashlib.sha256(b"binary").hexdigest()
                (directory / f"{name}.sha256").write_text(f"{good}  {name}")
            self.assertEqual(len(release.verify_windows_files(directory, TAG)), 6)
            (directory / "extra").touch()
            with self.assertRaisesRegex(release.PreparationError, "six expected assets"):
                release.verify_windows_files(directory, TAG)
            (directory / "extra").unlink()
            (directory / f"{name}.sha256").write_text(f"{good}  other.exe")
            with self.assertRaisesRegex(release.PreparationError, "checksum or sidecar"):
                release.verify_windows_files(directory, TAG)

    def test_inventory_comparison_ignores_download_counts_but_detects_replacement(self):
        asset = {"id": 1, "state": "uploaded", "size": 100, "digest": "sha256:one", "download_count": 0}
        before = release.inventory_identity({"asset": asset})
        self.assertEqual(before, release.inventory_identity({"asset": dict(asset, download_count=1)}))
        self.assertNotEqual(before, release.inventory_identity({"asset": dict(asset, digest="sha256:two")}))

    def test_hosted_windows_artifact_name_and_inventory_match_staging_consumer(self):
        workflow = (release.ROOT / ".github/workflows/test-windows-update.yml").read_text()
        artifact = workflow.split("name: Retain tested Windows release packages", 1)[1]
        artifact = artifact.split("name: Capture logs as artifact", 1)[0]
        self.assertIn("name: windows-release-${{ github.ref_name }}-${{ github.run_attempt }}", artifact)
        names = {line.strip().replace("${{ steps.ver.outputs.version }}", TAG[1:])
                 for line in artifact.splitlines() if line.strip().startswith("VPNRouter-")}
        self.assertEqual(names, release.with_sidecars(release.windows_binaries(TAG)))

    def test_cli_and_integrity_workflow_use_the_same_eighteen_assets(self):
        source = (release.ROOT / ".github/workflows/verify-release-integrity.yml").read_text()
        checked = {name.replace("${EXPECTED_VERSION}", TAG[1:]) for name in re.findall(r'"(VPNRouter-[^"]+)"', source)
                   if "${EXPECTED_VERSION}" in name}
        built = release.with_sidecars(release.windows_binaries(TAG))
        for suffixes in release.PLATFORM_BUILDS.values():
            built |= release.with_sidecars(f"VPNRouter-{TAG}{suffix}" for suffix in suffixes)
        self.assertEqual(len(checked), 18)
        self.assertEqual(checked, built)


if __name__ == "__main__":
    unittest.main()
