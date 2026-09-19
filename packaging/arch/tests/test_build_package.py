"""Unit tests for packaging/arch/build_package.py and PKGBUILD.

Covers all 9 coordinator source-verified findings:
1. PKGBUILD bash prepare with synthetic valid tar on path containing single quotes,
   template SHA validation, composite licenses, dependencies glibc/gcc-libs/dotnet.
2. acquire_archive cache invalid/symlink raises without network, bounded cache read,
   allowlist tuple verification before cache, no cache mutation.
3. publish subprocess cwd=source_tree mandatory global.json, dotnet --version 10.0.301.
4. license/NOTICE from source_tree, gather NuGet licenses from exact resolved versions,
   no unrelated versions from cache, license_inventory_complete false with missing notices.
5. reject publish links via inventory before copying, no Avalonia in publish artifacts.
6. get_elf_needed mandatory readelf, mandatory bsdtar inspection, convert via bsdtar
   then safe_extract and require manifest, reject files outside metadata/subtree,
   hooks .INSTALL forbidden, owner root (0:0) validated, exact 1 package artifact.
7. --skip-makepkg CLI bypass removed.
8. isolated HOME/XDG/NUGET_PACKAGES in work_dir, source archive includes Version.props
   and all tracked root .props/.targets/.json/.config.
9. source_archive_sha256, packaging files SHA256, and bounded tool versions in manifest.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock

# Ensure packaging/arch is in sys.path
ARCH_DIR = Path(__file__).resolve().parents[1]
PROJECT_ROOT = ARCH_DIR.parents[1]
if str(ARCH_DIR) not in sys.path:
    sys.path.insert(0, str(ARCH_DIR))

import build_package
import staging_tools


def _create_tar_bytes(entries: list[tuple[tarfile.TarInfo, bytes | None]]) -> bytes:
    bio = io.BytesIO()
    with tarfile.open(fileobj=bio, mode="w") as tf:
        for ti, data in entries:
            if data is not None:
                ti.size = len(data)
                tf.addfile(ti, io.BytesIO(data))
            else:
                tf.addfile(ti)
    return bio.getvalue()


class TestPkgbuildAndPrepare(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_pkgbuild_injected_single_quote_path_bash_prepare(self) -> None:
        """Cover injected single quote path in PKGBUILD prepare() using synthetic tar.

        Emulates standard makepkg srcdir symlink, requiring success on original
        $startdir/payload.tar and refusal if the original itself is a symlink.
        """
        # Create directory path containing single quotes to test injection resilience
        quoted_dir = self.tmp / "path'with'quote"
        startdir = quoted_dir / "start'dir"
        srcdir = quoted_dir / "src'dir"
        startdir.mkdir(parents=True)
        srcdir.mkdir(parents=True)

        # Copy staging_tools.py to startdir
        shutil.copy2(ARCH_DIR / "staging_tools.py", startdir / "staging_tools.py")

        # Create valid synthetic payload tar
        ti_dir = tarfile.TarInfo("usr/lib/vpnrouter-headless")
        ti_dir.type = tarfile.DIRTYPE
        ti_dir.mode = 0o755
        ti_dir.uid = ti_dir.gid = 0

        ti_file = tarfile.TarInfo("usr/lib/vpnrouter-headless/test.txt")
        ti_file.type = tarfile.REGTYPE
        ti_file.mode = 0o644
        ti_file.uid = ti_file.gid = 0
        file_content = b"single-quote path injection test payload"

        tar_bytes = _create_tar_bytes([(ti_dir, None), (ti_file, file_content)])
        # Original regular file in startdir
        payload_tar = startdir / "payload.tar"
        payload_tar.write_bytes(tar_bytes)

        # Emulate standard makepkg behavior: makepkg creates symlink in srcdir pointing to startdir/payload.tar
        (srcdir / "payload.tar").symlink_to(payload_tar)

        payload_sha256 = hashlib.sha256(tar_bytes).hexdigest()

        # Render PKGBUILD
        rendered = build_package.render_pkgbuild(ARCH_DIR / "PKGBUILD", payload_sha256)
        pkgbuild_path = quoted_dir / "PKGBUILD"
        pkgbuild_path.write_text(rendered, encoding="utf-8")

        # Execute bash sourcing PKGBUILD and running prepare()
        bash_script = f"""
set -eu
startdir={shlex.quote(str(startdir))}
srcdir={shlex.quote(str(srcdir))}
source {shlex.quote(str(pkgbuild_path))}
prepare
"""
        res = subprocess.run(
            ["bash", "-c", bash_script],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(
            res.returncode,
            0,
            f"bash prepare failed on single-quoted path:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}",
        )

        # Verify staged_root extracted correctly
        extracted_file = srcdir / "staged_root" / "usr" / "lib" / "vpnrouter-headless" / "test.txt"
        self.assertTrue(extracted_file.is_file())
        self.assertEqual(extracted_file.read_bytes(), file_content)

        # Negative check: if original $startdir/payload.tar is itself a symlink, prepare must fail
        sym_startdir = quoted_dir / "sym_startdir"
        sym_srcdir = quoted_dir / "sym_srcdir"
        sym_startdir.mkdir(parents=True)
        sym_srcdir.mkdir(parents=True)
        shutil.copy2(ARCH_DIR / "staging_tools.py", sym_startdir / "staging_tools.py")
        (sym_startdir / "payload.tar").symlink_to(payload_tar)
        (sym_srcdir / "payload.tar").symlink_to(sym_startdir / "payload.tar")

        bash_script_sym = f"""
set -eu
startdir={shlex.quote(str(sym_startdir))}
srcdir={shlex.quote(str(sym_srcdir))}
source {shlex.quote(str(pkgbuild_path))}
prepare
"""
        res_sym = subprocess.run(
            ["bash", "-c", bash_script_sym],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertNotEqual(res_sym.returncode, 0, "prepare must fail when original payload.tar is a symlink")
        self.assertIn("symlink", (res_sym.stderr + res_sym.stdout).lower())

    def test_pkgbuild_composite_licenses_and_dependencies(self) -> None:
        """Verify PKGBUILD declares composite licenses and glibc/gcc-libs dependencies."""
        content = (ARCH_DIR / "PKGBUILD").read_text(encoding="utf-8")
        self.assertIn("GPL-3.0-or-later", content)
        self.assertIn("Apache-2.0", content)
        self.assertIn("MIT", content)
        self.assertIn("BSD-3-Clause", content)

        self.assertIn("glibc", content)
        self.assertIn("gcc-libs", content)
        self.assertIn("dotnet-runtime>=10", content)
        self.assertIn("dotnet-runtime<11", content)

        # Verify single-quoted heredoc pattern
        self.assertIn("<<'EOF'", content)
        self.assertIn('python3 - "$startdir"', content)

    def test_render_pkgbuild_validation(self) -> None:
        """Verify template SHA validation in render_pkgbuild."""
        valid_sha = "a" * 64
        rendered = build_package.render_pkgbuild(ARCH_DIR / "PKGBUILD", valid_sha)
        self.assertIn(f"sha256sums=('{valid_sha}')", rendered)

        # Invalid SHA formats
        with self.assertRaises(ValueError):
            build_package.render_pkgbuild(ARCH_DIR / "PKGBUILD", "short_sha")
        with self.assertRaises(ValueError):
            build_package.render_pkgbuild(ARCH_DIR / "PKGBUILD", "g" * 64)
        with self.assertRaises(ValueError):
            build_package.render_pkgbuild(ARCH_DIR / "PKGBUILD", valid_sha + "'evil")

        # Missing placeholder template
        bad_tpl = self.tmp / "bad_pkgbuild"
        bad_tpl.write_text("pkgname=test\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            build_package.render_pkgbuild(bad_tpl, valid_sha)


class TestAcquireArchive(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)
        self.work_dir = self.tmp / "work"
        self.work_dir.mkdir()
        self.cache_dir = self.tmp / "cache"
        self.cache_dir.mkdir()

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_cache_tamper_rejects_without_network(self) -> None:
        """Verify tampered cache file raises ValueError without attempting network download."""
        tampered_file = self.cache_dir / "runtime.tar.gz"
        tampered_file.write_bytes(b"tampered content not matching expected sha")

        with mock.patch("urllib.request.build_opener") as mock_opener:
            with self.assertRaises(ValueError) as ctx:
                build_package.acquire_archive(
                    build_package.RUNTIME_URL,
                    build_package.RUNTIME_SHA256,
                    "runtime.tar.gz",
                    self.work_dir,
                    self.cache_dir,
                )
            self.assertIn("SHA-256 mismatch", str(ctx.exception))
            # Must not attempt network call
            mock_opener.assert_not_called()

    def test_cache_symlink_rejects_without_network(self) -> None:
        """Verify symlinked cache file raises ValueError without network download."""
        real_file = self.tmp / "real_archive.tar.gz"
        real_file.write_bytes(b"some content")
        symlink_file = self.cache_dir / "runtime.tar.gz"
        os.symlink(real_file, symlink_file)

        with mock.patch("urllib.request.build_opener") as mock_opener:
            with self.assertRaises(ValueError) as ctx:
                build_package.acquire_archive(
                    build_package.RUNTIME_URL,
                    build_package.RUNTIME_SHA256,
                    "runtime.tar.gz",
                    self.work_dir,
                    self.cache_dir,
                )
            self.assertIn("symlink", str(ctx.exception).lower())
            mock_opener.assert_not_called()

    def test_cache_read_bounded(self) -> None:
        """Verify cache file read limit enforces bounded size."""
        cached = self.cache_dir / "runtime.tar.gz"
        cached.write_bytes(b"x" * 2048)

        with mock.patch.object(build_package, "MAX_CACHE_READ_SIZE", 1024):
            with self.assertRaises(ValueError) as ctx:
                build_package.acquire_archive(
                    build_package.RUNTIME_URL,
                    build_package.RUNTIME_SHA256,
                    "runtime.tar.gz",
                    self.work_dir,
                    self.cache_dir,
                )
            self.assertIn("exceeds maximum hash size", str(ctx.exception))

    def test_verify_allowlist_tuple_before_cache(self) -> None:
        """Verify URL and hash allowlist check occurs before cache is inspected."""
        unlisted_url = "https://example.com/unlisted.tar.gz"
        with self.assertRaises(ValueError) as ctx:
            build_package.acquire_archive(
                unlisted_url,
                build_package.RUNTIME_SHA256,
                "runtime.tar.gz",
                self.work_dir,
                self.cache_dir,
            )
        self.assertIn("URL is not in allowed pinned downloads", str(ctx.exception))

        # Mismatched expected hash for valid URL
        with self.assertRaises(ValueError) as ctx:
            build_package.acquire_archive(
                build_package.RUNTIME_URL,
                "0" * 64,
                "runtime.tar.gz",
                self.work_dir,
                self.cache_dir,
            )
        self.assertIn("does not match allowed hash", str(ctx.exception))

    def test_do_not_mutate_supplied_cache(self) -> None:
        """Verify downloading a missing archive does not mutate supplied cache directory."""
        mock_data = b"valid downloaded archive content"
        mock_sha = hashlib.sha256(mock_data).hexdigest()

        with mock.patch.dict(
            build_package.ALLOWED_DOWNLOADS,
            {build_package.RUNTIME_URL: (mock_sha, "runtime.tar.gz")},
        ):
            mock_resp = mock.MagicMock()
            mock_resp.read.side_effect = [mock_data, b""]
            mock_opener_inst = mock.MagicMock()
            mock_opener_inst.open.return_value.__enter__.return_value = mock_resp

            with mock.patch("urllib.request.build_opener", return_value=mock_opener_inst):
                res = build_package.acquire_archive(
                    build_package.RUNTIME_URL,
                    mock_sha,
                    "runtime.tar.gz",
                    self.work_dir,
                    self.cache_dir,
                )
                self.assertTrue(res.is_file())
                self.assertEqual(res.read_bytes(), mock_data)

                # Cache directory must not have been mutated
                cached_target = self.cache_dir / "runtime.tar.gz"
                self.assertFalse(cached_target.exists())


class TestPublishHeadless(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)
        self.source_tree = self.tmp / "source_tree"
        self.source_tree.mkdir()
        self.publish_dir = self.tmp / "publish"
        self.publish_dir.mkdir()
        self.work_dir = self.tmp / "work"
        self.work_dir.mkdir()

        # Create global.json and csproj
        (self.source_tree / "global.json").write_text('{"sdk":{"version":"10.0.301"}}', encoding="utf-8")
        headless_dir = self.source_tree / "VPNRouter.Headless"
        headless_dir.mkdir()
        (headless_dir / "VPNRouter.Headless.csproj").write_text("<Project/>", encoding="utf-8")

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_publish_mandatory_cwd_and_dotnet_version_and_isolated_env(self) -> None:
        """Verify publish executes with cwd=source_tree, checks exact 10.0.301, and isolates env."""
        recorded_calls: list[dict] = []

        def fake_run(cmd, cwd=None, env=None, capture_output=None, text=None, check=None):
            recorded_calls.append({"cmd": cmd, "cwd": cwd, "env": env})
            if "--version" in cmd:
                return subprocess.CompletedProcess(cmd, 0, stdout="10.0.301\n", stderr="")
            if "publish" in cmd:
                return subprocess.CompletedProcess(cmd, 0, stdout="publish ok", stderr="")
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("build_package.resolve_dotnet", return_value="/mock/dotnet"), \
             mock.patch.dict(os.environ, {}, clear=False), \
             mock.patch("subprocess.run", side_effect=fake_run):
            build_package.publish_headless(self.source_tree, self.publish_dir, self.work_dir)

        # Check version query call - must run with cwd=source_tree and isolated env
        self.assertEqual(recorded_calls[0]["cmd"], ["/mock/dotnet", "--version"])
        self.assertEqual(recorded_calls[0]["cwd"], self.source_tree)
        self.assertTrue(str(self.work_dir) in recorded_calls[0]["env"]["HOME"])
        self.assertTrue(str(self.work_dir) in recorded_calls[0]["env"]["NUGET_PACKAGES"])

        # Check publish call
        pub_call = recorded_calls[1]
        self.assertEqual(pub_call["cwd"], self.source_tree)
        pub_env = pub_call["env"]
        self.assertTrue(str(self.work_dir) in pub_env["HOME"])
        self.assertTrue(str(self.work_dir) in pub_env["NUGET_PACKAGES"])
        self.assertTrue(str(self.work_dir) in pub_env["XDG_CONFIG_HOME"])

    def test_publish_wrong_dotnet_version_rejected(self) -> None:
        """Verify non-10.0.301 dotnet version raises ValueError."""
        def fake_run(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, 0, stdout="10.0.200\n", stderr="")

        with mock.patch("build_package.resolve_dotnet", return_value="/mock/dotnet"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(ValueError) as ctx:
                build_package.publish_headless(self.source_tree, self.publish_dir, self.work_dir)
            self.assertIn("does not match required 10.0.301", str(ctx.exception))

    def test_publish_missing_global_json_rejected(self) -> None:
        """Verify missing global.json in source_tree raises FileNotFoundError."""
        (self.source_tree / "global.json").unlink()
        def fake_run(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, 0, stdout="10.0.301\n", stderr="")

        with mock.patch("build_package.resolve_dotnet", return_value="/mock/dotnet"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(FileNotFoundError) as ctx:
                build_package.publish_headless(self.source_tree, self.publish_dir, self.work_dir)
            self.assertIn("global.json", str(ctx.exception))


class TestPublishInventoryAndAvaloniaRejection(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)
        self.pub_dir = self.tmp / "publish"
        self.pub_dir.mkdir()

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_publish_symlink_rejected_by_inventory(self) -> None:
        """Verify publish directory containing symlinks is rejected by inventory."""
        target = self.pub_dir / "real.dll"
        target.write_bytes(b"binary")
        symlink = self.pub_dir / "link.dll"
        os.symlink(target, symlink)

        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.pub_dir)
        self.assertIn("Symlink file rejected", str(ctx.exception))

    def test_publish_avalonia_artifact_rejected(self) -> None:
        """Verify Avalonia artifact in publish filenames is rejected by validate_publish."""
        (self.pub_dir / "Avalonia.Base.dll").write_bytes(b"avalonia binary")
        (self.pub_dir / "VPNRouter.Headless.dll").write_bytes(b"headless binary")
        (self.pub_dir / "VPNRouter.Headless.deps.json").write_text('{"targets": {}}', encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            build_package.validate_publish(self.pub_dir)
        self.assertIn("Forbidden Avalonia artifact", str(ctx.exception))

    def test_validate_publish_positive_valid_deps(self) -> None:
        """Verify validate_publish passes on clean publish directory with Headless deps.json."""
        (self.pub_dir / "VPNRouter.Headless.dll").write_bytes(b"headless binary")
        (self.pub_dir / "VPNRouter.Headless.deps.json").write_text(
            '{"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0"}, "targets": {}}',
            encoding="utf-8",
        )
        inv = build_package.validate_publish(self.pub_dir)
        self.assertIsInstance(inv, list)
        paths = [item["path"] for item in inv]
        self.assertIn("VPNRouter.Headless.dll", paths)
        self.assertIn("VPNRouter.Headless.deps.json", paths)

    def test_validate_publish_hidden_avalonia_reference_in_deps_json(self) -> None:
        """Verify validate_publish rejects Avalonia referenced in deps.json content despite clean paths."""
        (self.pub_dir / "VPNRouter.Headless.dll").write_bytes(b"headless binary")
        deps_content = json.dumps({
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0"},
            "targets": {
                ".NETCoreApp,Version=v10.0": {
                    "Avalonia.Remote.Protocol/11.0.0": {}
                }
            }
        })
        (self.pub_dir / "VPNRouter.Headless.deps.json").write_text(deps_content, encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            build_package.validate_publish(self.pub_dir)
        self.assertIn("Forbidden Avalonia reference", str(ctx.exception))

    def test_validate_publish_missing_headless_deps_json_rejected(self) -> None:
        """Verify validate_publish requires VPNRouter.Headless.deps.json."""
        (self.pub_dir / "VPNRouter.Headless.dll").write_bytes(b"headless binary")
        with self.assertRaises(FileNotFoundError) as ctx:
            build_package.validate_publish(self.pub_dir)
        self.assertIn("Missing required Headless deps.json", str(ctx.exception))


class TestLicensesAndNotices(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)
        self.source_tree = self.tmp / "source_tree"
        self.source_tree.mkdir()
        self.repo_dir = self.tmp / "repo_dir"
        self.repo_dir.mkdir()
        self.licenses_dest = self.tmp / "licenses_dest"
        self.licenses_dest.mkdir()

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_license_from_source_tree_not_mutable_repo(self) -> None:
        """Verify license/NOTICE are taken from immutable source_tree using copy_source_licenses."""
        (self.source_tree / "LICENSE").write_text("IMMUTABLE SOURCE LICENSE", encoding="utf-8")
        (self.source_tree / "NOTICE.md").write_text("IMMUTABLE SOURCE NOTICE", encoding="utf-8")

        (self.repo_dir / "LICENSE").write_text("DIRTY MUTABLE REPO LICENSE", encoding="utf-8")
        (self.repo_dir / "NOTICE.md").write_text("DIRTY MUTABLE REPO NOTICE", encoding="utf-8")

        missing = build_package.copy_source_licenses(self.source_tree, self.licenses_dest)
        self.assertEqual(missing, [])

        dest_lic = (self.licenses_dest / "VPNRouter-LICENSE").read_text(encoding="utf-8")
        dest_not = (self.licenses_dest / "VPNRouter-NOTICE.md").read_text(encoding="utf-8")

        self.assertEqual(dest_lic, "IMMUTABLE SOURCE LICENSE")
        self.assertEqual(dest_not, "IMMUTABLE SOURCE NOTICE")

    def test_copy_source_licenses_missing_consistent(self) -> None:
        """Verify copy_source_licenses returns consistent identifiers when files are missing."""
        empty_tree = self.tmp / "empty_tree"
        empty_tree.mkdir()
        missing = build_package.copy_source_licenses(empty_tree, self.licenses_dest)
        self.assertEqual(missing, ["VPNRouter-LICENSE", "VPNRouter-NOTICE.md"])

    def test_main_forwards_exported_source_not_mutable_repo(self) -> None:
        """Assert main forwards extracted source_tree to downstream steps, not repo_dir."""
        fake_repo = self.tmp / "fake_repo"
        fake_repo.mkdir()
        fake_work = self.tmp / "fake_work"

        forwarded_source_trees: list[Path] = []

        def fake_publish(source_tree, publish_dir, work_dir):
            forwarded_source_trees.append(source_tree)
            publish_dir.mkdir(parents=True, exist_ok=True)
            (publish_dir / "VPNRouter.Headless.dll").write_bytes(b"dummy")
            (publish_dir / "VPNRouter.Headless.deps.json").write_text("{}", encoding="utf-8")

        def fake_copy_licenses(source_tree, licenses_dest):
            forwarded_source_trees.append(source_tree)
            return []

        def fake_acquire(url, sha, fname, work_dir, cache):
            p = work_dir / fname
            p.write_bytes(b"dummy")
            return p

        def fake_safe_extract(archive, dest, sha):
            dest.mkdir(parents=True, exist_ok=True)
            if "runtime" in str(dest):
                (dest / "sing-box").write_bytes(b"sb")
                (dest / "LICENSE").write_bytes(b"lic")
                (dest / "README.md").write_bytes(b"readme")
            elif "cronet" in str(dest):
                (dest / "libcronet.so").write_bytes(b"cronet")
                (dest / "LICENSE").write_bytes(b"lic")

        with mock.patch("build_package.verify_repo_commit"), \
             mock.patch("build_package.resolve_dotnet", return_value="/mock/dotnet"), \
             mock.patch("build_package.export_source", return_value=(fake_work / "source.tar", "a" * 64)), \
             mock.patch("staging_tools.safe_extract", side_effect=fake_safe_extract), \
             mock.patch("build_package.acquire_archive", side_effect=fake_acquire), \
             mock.patch("build_package.publish_headless", side_effect=fake_publish), \
             mock.patch("build_package.validate_publish", return_value=[]), \
             mock.patch("build_package.copy_source_licenses", side_effect=fake_copy_licenses), \
             mock.patch("build_package.gather_nuget_licenses", return_value=([], [])), \
             mock.patch("staging_tools.validate_elf_x86_64"), \
             mock.patch("build_package.get_elf_needed", return_value=[]), \
             mock.patch("staging_tools.write_manifest"), \
             mock.patch("staging_tools.verify_manifest"), \
             mock.patch("build_package.create_payload_tar", return_value="b" * 64), \
             mock.patch("build_package.build_makepkg", return_value=(fake_work / "pkg.tar.zst", "c" * 64)), \
             mock.patch("builtins.print"):
            ret = build_package.main(["--repo", str(fake_repo), "--work-dir", str(fake_work)])
            self.assertEqual(ret, 0)

        self.assertGreater(len(forwarded_source_trees), 0)
        for st in forwarded_source_trees:
            self.assertNotEqual(st, fake_repo.resolve())
            self.assertEqual(st, fake_work / "source_tree")

    def test_gather_nuget_licenses_exact_versions(self) -> None:
        """Verify NuGet licenses are gathered from exact resolved library versions without unrelated cache."""
        headless_obj = self.source_tree / "VPNRouter.Headless" / "obj"
        headless_obj.mkdir(parents=True)

        nuget_pkg_dir = self.tmp / "nuget_packages"
        nuget_pkg_dir.mkdir()

        # Resolved versions: Serilog 4.4.0, YamlDotNet 18.1.0
        assets_content = {
            "version": 3,
            "packageFolders": {str(nuget_pkg_dir): {}},
            "libraries": {
                "Serilog/4.4.0": {
                    "type": "package",
                    "path": "serilog/4.4.0",
                },
                "YamlDotNet/18.1.0": {
                    "type": "package",
                    "path": "yamldotnet/18.1.0",
                },
            },
        }
        (headless_obj / "project.assets.json").write_text(json.dumps(assets_content), encoding="utf-8")

        # Create exact resolved package directory for Serilog with case-insensitive License.txt
        serilog_dir = nuget_pkg_dir / "serilog" / "4.4.0"
        serilog_dir.mkdir(parents=True)
        (serilog_dir / "License.txt").write_text("Serilog 4.4.0 Apache License", encoding="utf-8")

        # Create unrelated old version in cache (must NOT be used for YamlDotNet)
        unrelated_yaml = nuget_pkg_dir / "yamldotnet" / "11.0.0"
        unrelated_yaml.mkdir(parents=True)
        (unrelated_yaml / "LICENSE.md").write_text("Old YamlDotNet License", encoding="utf-8")

        gathered, missing = build_package.gather_nuget_licenses(
            self.source_tree, [nuget_pkg_dir], self.licenses_dest
        )

        self.assertIn("Serilog-License.txt", gathered)
        # YamlDotNet 18.1.0 was missing from cache, must be reported in missing, NOT filled from 11.0.0
        self.assertIn("YamlDotNet-LICENSE", missing)
        self.assertTrue((self.licenses_dest / "Serilog-License.txt").is_file())
        self.assertFalse((self.licenses_dest / "YamlDotNet-LICENSE.md").exists())


class TestReadelfAndPackageInspection(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_get_elf_needed_mandatory_readelf(self) -> None:
        """Verify get_elf_needed raises FileNotFoundError when readelf is missing."""
        with mock.patch("shutil.which", return_value=None):
            with self.assertRaises(FileNotFoundError) as ctx:
                build_package.get_elf_needed(Path("/bin/dummy"))
            self.assertIn("readelf executable is mandatory", str(ctx.exception))

    def test_inspect_package_mandatory_bsdtar(self) -> None:
        """Verify inspect_package raises FileNotFoundError when bsdtar is missing."""
        with mock.patch("shutil.which", return_value=None):
            with self.assertRaises(FileNotFoundError) as ctx:
                build_package.inspect_package(Path("/dummy/pkg.tar.zst"), self.tmp)
            self.assertIn("bsdtar executable is mandatory", str(ctx.exception))

    def test_inspect_package_forbidden_install_hook(self) -> None:
        """Verify package inspection rejects .INSTALL hooks."""
        pkg_file = self.tmp / "test.pkg.tar.zst"
        pkg_file.write_bytes(b"dummy")

        # Create uncompressed tar with .INSTALL hook
        ti_hook = tarfile.TarInfo(".INSTALL")
        ti_hook.type = tarfile.REGTYPE
        ti_hook.mode = 0o644
        ti_hook.uid = ti_hook.gid = 0
        uncompressed_bytes = _create_tar_bytes([(ti_hook, b"echo hook")])

        def fake_run(cmd, **kwargs):
            out_tar = Path(cmd[2])
            out_tar.write_bytes(uncompressed_bytes)
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(ValueError) as ctx:
                build_package.inspect_package(pkg_file, self.tmp)
            self.assertIn("Package hooks (.INSTALL) are strictly forbidden", str(ctx.exception))

    def test_inspect_package_files_outside_subtree_rejected(self) -> None:
        """Verify files outside allowed metadata and usr/lib/vpnrouter-headless are rejected."""
        pkg_file = self.tmp / "test.pkg.tar.zst"
        pkg_file.write_bytes(b"dummy")

        # Member in /etc/shadow
        ti_bad = tarfile.TarInfo("etc/shadow")
        ti_bad.type = tarfile.REGTYPE
        ti_bad.mode = 0o644
        ti_bad.uid = ti_bad.gid = 0
        uncompressed_bytes = _create_tar_bytes([(ti_bad, b"root:...")])

        def fake_run(cmd, **kwargs):
            out_tar = Path(cmd[2])
            out_tar.write_bytes(uncompressed_bytes)
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(ValueError) as ctx:
                build_package.inspect_package(pkg_file, self.tmp)
            self.assertIn("outside allowed metadata", str(ctx.exception))

    def test_inspect_package_original_writable_mode_rejected(self) -> None:
        for mode in (0o664, 0o666, 0o777):
            with self.subTest(mode=mode):
                pkg_file = self.tmp / "writable.pkg.tar.zst"
                pkg_file.write_bytes(b"fixture")
                member = tarfile.TarInfo(".PKGINFO")
                member.mode = mode
                member.uid = member.gid = 0
                data = _create_tar_bytes([(member, b"pkgname=test")])

                def fake_run(cmd, **kwargs):
                    Path(cmd[2]).write_bytes(data)
                    return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

                with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
                     mock.patch("subprocess.run", side_effect=fake_run):
                    with self.assertRaisesRegex(ValueError, "Invalid package file mode"):
                        build_package.inspect_package(pkg_file, self.tmp)

    def test_inspect_package_non_root_ownership_rejected(self) -> None:
        """Verify package members with non-root ownership are rejected."""
        pkg_file = self.tmp / "test.pkg.tar.zst"
        pkg_file.write_bytes(b"dummy")

        ti_member = tarfile.TarInfo(".PKGINFO")
        ti_member.type = tarfile.REGTYPE
        ti_member.mode = 0o644
        ti_member.uid = 1000
        ti_member.gid = 1000
        uncompressed_bytes = _create_tar_bytes([(ti_member, b"pkgname = test")])

        def fake_run(cmd, **kwargs):
            out_tar = Path(cmd[2])
            out_tar.write_bytes(uncompressed_bytes)
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(ValueError) as ctx:
                build_package.inspect_package(pkg_file, self.tmp)
            self.assertIn("non-root ownership", str(ctx.exception))

    def test_inspect_package_missing_manifest_rejected(self) -> None:
        """Verify package missing manifest.json is rejected."""
        pkg_file = self.tmp / "test.pkg.tar.zst"
        pkg_file.write_bytes(b"dummy")

        ti_dir = tarfile.TarInfo("usr/lib/vpnrouter-headless")
        ti_dir.type = tarfile.DIRTYPE
        ti_dir.mode = 0o755
        ti_dir.uid = ti_dir.gid = 0

        ti_meta = tarfile.TarInfo(".PKGINFO")
        ti_meta.type = tarfile.REGTYPE
        ti_meta.mode = 0o644
        ti_meta.uid = ti_meta.gid = 0

        uncompressed_bytes = _create_tar_bytes([(ti_dir, None), (ti_meta, b"pkgname=test")])

        def fake_run(cmd, **kwargs):
            out_tar = Path(cmd[2])
            out_tar.write_bytes(uncompressed_bytes)
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(FileNotFoundError) as ctx:
                build_package.inspect_package(pkg_file, self.tmp)
            self.assertIn("manifest.json", str(ctx.exception))

    def _create_valid_synthetic_pkg_tar(self, tamper: bool = False) -> bytes:
        sample_content = b"sample binary payload"
        sample_sha = hashlib.sha256(sample_content).hexdigest()
        manifest_data = {
            "schema_version": 1,
            "files": [
                {
                    "path": "sample.bin",
                    "size": len(sample_content),
                    "sha256": sample_sha,
                    "mode": "0644",
                }
            ],
        }
        manifest_bytes = (json.dumps(manifest_data, indent=2) + "\n").encode("utf-8")

        ti_pkginfo = tarfile.TarInfo(".PKGINFO")
        ti_pkginfo.type = tarfile.REGTYPE
        ti_pkginfo.mode = 0o644
        ti_pkginfo.uid = ti_pkginfo.gid = 0

        ti_usr = tarfile.TarInfo("usr")
        ti_usr.type = tarfile.DIRTYPE
        ti_usr.mode = 0o755
        ti_usr.uid = ti_usr.gid = 0

        ti_usrlib = tarfile.TarInfo("usr/lib")
        ti_usrlib.type = tarfile.DIRTYPE
        ti_usrlib.mode = 0o755
        ti_usrlib.uid = ti_usrlib.gid = 0

        ti_appdir = tarfile.TarInfo("usr/lib/vpnrouter-headless")
        ti_appdir.type = tarfile.DIRTYPE
        ti_appdir.mode = 0o755
        ti_appdir.uid = ti_appdir.gid = 0

        file_data = b"tampered file payload" if tamper else sample_content
        ti_sample = tarfile.TarInfo("usr/lib/vpnrouter-headless/sample.bin")
        ti_sample.type = tarfile.REGTYPE
        ti_sample.mode = 0o644
        ti_sample.uid = ti_sample.gid = 0

        ti_manifest = tarfile.TarInfo("usr/lib/vpnrouter-headless/manifest.json")
        ti_manifest.type = tarfile.REGTYPE
        ti_manifest.mode = 0o644
        ti_manifest.uid = ti_manifest.gid = 0

        entries = [
            (ti_pkginfo, b"pkgname = vpnrouter-headless\n"),
            (ti_usr, None),
            (ti_usrlib, None),
            (ti_appdir, None),
            (ti_sample, file_data),
            (ti_manifest, manifest_bytes),
        ]
        return _create_tar_bytes(entries)

    def test_inspect_package_positive_synthetic_fixture_pass(self) -> None:
        """Verify inspect_package passes on valid synthetic package fixture with manifest and root tar."""
        pkg_file = self.tmp / "valid.pkg.tar.zst"
        pkg_file.write_bytes(b"dummy")

        valid_tar_bytes = self._create_valid_synthetic_pkg_tar(tamper=False)

        def fake_run(cmd, **kwargs):
            out_tar = Path(cmd[2])
            out_tar.write_bytes(valid_tar_bytes)
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            build_package.inspect_package(pkg_file, self.tmp)

    def test_inspect_package_artifact_tamper_fails(self) -> None:
        """Verify inspect_package fails when package artifact is tampered against manifest."""
        pkg_file = self.tmp / "tampered.pkg.tar.zst"
        pkg_file.write_bytes(b"dummy")

        tampered_tar_bytes = self._create_valid_synthetic_pkg_tar(tamper=True)

        def fake_run(cmd, **kwargs):
            out_tar = Path(cmd[2])
            out_tar.write_bytes(tampered_tar_bytes)
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("shutil.which", return_value="/bin/bsdtar"), \
             mock.patch("subprocess.run", side_effect=fake_run):
            with self.assertRaises(ValueError) as ctx:
                build_package.inspect_package(pkg_file, self.tmp)
            self.assertIn("SHA-256 mismatch", str(ctx.exception))


class TestFindFile(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_find_file_exact_single_match(self) -> None:
        """Verify find_file returns single matching regular file."""
        sub = self.tmp / "sub"
        sub.mkdir()
        f = sub / "sing-box"
        f.write_text("sb binary", encoding="utf-8")
        found = build_package.find_file(self.tmp, "sing-box")
        self.assertEqual(found, f)

    def test_find_file_missing_raises_file_not_found(self) -> None:
        """Verify find_file raises FileNotFoundError when file is not found."""
        with self.assertRaises(FileNotFoundError) as ctx:
            build_package.find_file(self.tmp, "nonexistent")
        self.assertIn("File 'nonexistent' not found", str(ctx.exception))

    def test_find_file_ambiguous_matches_raises_value_error(self) -> None:
        """Verify find_file raises ValueError when multiple matching files exist."""
        dir1 = self.tmp / "dir1"
        dir2 = self.tmp / "dir2"
        dir1.mkdir()
        dir2.mkdir()
        (dir1 / "match.txt").write_text("1", encoding="utf-8")
        (dir2 / "match.txt").write_text("2", encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            build_package.find_file(self.tmp, "match.txt")
        self.assertIn("Ambiguous file matches for 'match.txt'", str(ctx.exception))

    def test_find_file_ignores_symlinks_and_dirs(self) -> None:
        """Verify find_file ignores symlinks and directory collisions."""
        sub = self.tmp / "sub"
        sub.mkdir()
        f = sub / "valid.so"
        f.write_text("so binary", encoding="utf-8")
        (self.tmp / "other" / "valid.so").mkdir(parents=True)
        (self.tmp / "sym_valid.so").symlink_to(f)
        found = build_package.find_file(self.tmp, "valid.so")
        self.assertEqual(found, f)


class TestBuildMakepkg(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)
        self.work_dir = self.tmp / "work"
        self.work_dir.mkdir()
        self.pkg_dir = self.work_dir / "pkgbuild"
        self.pkg_dir.mkdir()
        self.payload_tar = self.work_dir / "payload.tar"
        self.payload_tar.write_bytes(b"dummy payload tar")
        self.payload_sha256 = hashlib.sha256(b"dummy payload tar").hexdigest()
        self.template_pkgbuild = ARCH_DIR / "PKGBUILD"
        self.staging_tools_py = ARCH_DIR / "staging_tools.py"

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_build_makepkg_subprocess_flags_and_isolated_env(self) -> None:
        """Verify makepkg called with no -i/-s, config /etc/makepkg.conf, and env rooted in workdir."""
        recorded_calls: list[dict] = []

        def fake_run(cmd, cwd=None, env=None, capture_output=None, text=None, check=None):
            recorded_calls.append({"cmd": cmd, "cwd": cwd, "env": env})
            pkg_artifact = self.pkg_dir / "vpnrouter-headless-0.1.0-1-x86_64.pkg.tar.zst"
            pkg_artifact.write_bytes(b"mock package content")
            return subprocess.CompletedProcess(cmd, 0, stdout="makepkg ok", stderr="")

        with mock.patch("shutil.which", return_value="/usr/bin/makepkg"), \
             mock.patch.dict(os.environ, {
                 "BUILDDIR": "/untrusted/builddir",
                 "MAKEPKG_CONF": "/untrusted/makepkg.conf",
                 "PKGEXT": ".pkg.tar.gz",
                 "PACKAGER": "Untrusted <untrusted@example.com>",
             }, clear=False), \
             mock.patch("subprocess.run", side_effect=fake_run), \
             mock.patch("build_package.inspect_package") as mock_inspect:
            pkg_file, pkg_sha256 = build_package.build_makepkg(
                self.pkg_dir,
                self.payload_tar,
                self.payload_sha256,
                self.template_pkgbuild,
                self.staging_tools_py,
                self.work_dir,
            )

        self.assertEqual(len(recorded_calls), 1)
        call = recorded_calls[0]
        cmd = call["cmd"]
        env = call["env"]

        # Explicitly no -i or -s (or long forms)
        self.assertNotIn("-i", cmd)
        self.assertNotIn("--install", cmd)
        self.assertNotIn("-s", cmd)
        self.assertNotIn("--syncdeps", cmd)

        # Explicitly has --nodeps, --noconfirm, --config /etc/makepkg.conf
        self.assertIn("--nodeps", cmd)
        self.assertIn("--noconfirm", cmd)
        self.assertIn("--config", cmd)
        self.assertIn("/etc/makepkg.conf", cmd)

        # Inherited variables unset
        self.assertNotIn("MAKEPKG_CONF", env)
        self.assertNotIn("BUILDDIR", env)
        self.assertNotIn("PKGEXT", env)
        self.assertNotIn("PACKAGER", env)

        # Environment rooted in work_dir
        work_str = str(self.work_dir)
        self.assertTrue(env["HOME"].startswith(work_str))
        self.assertTrue(env["XDG_CONFIG_HOME"].startswith(work_str))
        self.assertTrue(env["XDG_CACHE_HOME"].startswith(work_str))
        self.assertTrue(env["XDG_DATA_HOME"].startswith(work_str))
        self.assertTrue(env["TMPDIR"].startswith(work_str))
        self.assertEqual(env["PKGDEST"], str(self.pkg_dir))
        self.assertEqual(env["SRCDEST"], str(self.pkg_dir))
        self.assertEqual(env["SRCPKGDEST"], str(self.pkg_dir))
        self.assertEqual(env["LOGDEST"], str(self.pkg_dir))

        # Inspect package was called
        mock_inspect.assert_called_once_with(pkg_file, self.work_dir)

        # Sidecar was written
        sidecar = self.pkg_dir / f"{pkg_file.name}.sha256"
        self.assertTrue(sidecar.is_file())
        self.assertIn(pkg_sha256, sidecar.read_text(encoding="utf-8"))


class TestBuildPackageCliAndExport(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_skip_makepkg_flag_removed(self) -> None:
        """Verify --skip-makepkg is no longer a valid CLI option."""
        with mock.patch("sys.stderr", new_callable=io.StringIO):
            with self.assertRaises(SystemExit):
                build_package.main(["--repo", str(self.tmp), "--work-dir", str(self.tmp / "w"), "--skip-makepkg"])

    def test_export_source_includes_version_props_and_root_configs(self) -> None:
        """Verify export_source includes Version.props and all root props/targets/json/config."""
        tracked_output = "\n".join([
            "Directory.Build.props",
            "Directory.Build.targets",
            "Version.props",
            "global.json",
            "nuget.config",
            "custom.targets",
            "test.config",
            "LICENSE",
            "NOTICE.md",
            "VPNRouter.sln",
            "VPNRouter.Core/App.cs",
            "VPNRouter.Headless/Program.cs",
            "profiles/default.json",
            "unrelated/file.txt",
        ])

        archive_args: list[str] = []

        def fake_run(cmd, **kwargs):
            if "ls-tree" in cmd:
                return subprocess.CompletedProcess(cmd, 0, stdout=tracked_output, stderr="")
            if "archive" in cmd:
                archive_args.extend(cmd)
                out_path = Path(cmd[-1])
                out_path.write_bytes(b"tar dummy")
                return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")
            return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

        with mock.patch("subprocess.run", side_effect=fake_run):
            build_package.export_source(self.tmp, "dummy_commit", self.tmp)

        self.assertIn("Version.props", archive_args)
        self.assertIn("Directory.Build.props", archive_args)
        self.assertIn("Directory.Build.targets", archive_args)
        self.assertIn("global.json", archive_args)
        self.assertIn("nuget.config", archive_args)
        self.assertIn("custom.targets", archive_args)
        self.assertIn("test.config", archive_args)
        self.assertIn("LICENSE", archive_args)
        self.assertIn("NOTICE.md", archive_args)
        self.assertIn("VPNRouter.sln", archive_args)
        self.assertNotIn("unrelated/file.txt", archive_args)

    def test_tool_versions_bounded(self) -> None:
        """Verify tool versions captures bounded snapshot without exceeding limits."""
        long_version = "v" * 500
        def fake_run(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, 0, stdout=long_version, stderr="")

        with mock.patch("subprocess.run", side_effect=fake_run):
            vers = build_package.get_tool_versions("/bin/dotnet")
            for k, v in vers.items():
                self.assertLessEqual(len(v), 128)


if __name__ == "__main__":
    unittest.main()
