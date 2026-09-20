"""Unit tests for packaging/arch/license_evidence.py."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

ARCH_DIR = Path(__file__).resolve().parent.parent
if str(ARCH_DIR) not in sys.path:
    sys.path.insert(0, str(ARCH_DIR))

import license_evidence

def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


class LicenseEvidenceRegressionMixin:
    """Regression test suite for parent fixes: uncataloged ID / wrong version reject, runtime+native join."""

    def test_regression_unknown_version_status_forged_verified_rejected(self) -> None:
        """Ensure forged verified_texts status on unknown version is rejected fail-closed.

        Must preserve native unresolved overallcompletefalse to hit the specific unreviewed guard.
        """
        self.catalog_content["components"][0]["version"] = "4.5.0"
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = self.mod.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        self.assertFalse(result["complete"])

        lic_text = "Apache License 2.0 Serilog text"
        lic_sha = sha256_text(lic_text)
        (self.payload_root / "licenses" / "Serilog-LICENSE").write_text(lic_text, encoding="utf-8")

        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        for c in data["components"]:
            if c["id"] == "Serilog":
                c["status"] = "verified_texts"
                c["unresolved"] = []
                c["notices"] = [{
                    "path": "licenses/Serilog-LICENSE",
                    "sha256": lic_sha,
                    "source_url": "https://example.com/serilog",
                }]

        # Preserve native unresolved: sing-box-vpnctl and Cronet remain unresolved,
        # ensuring data["complete"] is False matching computed expected_complete (False).
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            self.mod.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("Unreviewed component/version cannot be verified_texts", str(ctx.exception))
        self.assertIn("Serilog", str(ctx.exception))

    def test_regression_unknown_id_forged_verified_rejected(self) -> None:
        """Ensure uncataloged runtime package ID forged to verified_texts is rejected fail-closed."""
        assets_path = self.source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        assets["targets"]["net10.0/linux-x64"]["UnknownPkg/1.0.0"] = {
            "type": "package",
            "runtime": {"lib/net8.0/UnknownPkg.dll": {}},
        }
        assets["libraries"]["UnknownPkg/1.0.0"] = {"type": "package", "path": "unknownpkg/1.0.0"}
        assets_path.write_text(json.dumps(assets), encoding="utf-8")

        pkg_dir = self.nuget_dir / "unknownpkg" / "1.0.0"
        (pkg_dir / "lib" / "net8.0").mkdir(parents=True)
        dll_bytes = b"UNKNOWN_PKG_DLL_BYTES_123"
        (pkg_dir / "lib" / "net8.0" / "UnknownPkg.dll").write_bytes(dll_bytes)
        (self.payload_root / "UnknownPkg.dll").write_bytes(dll_bytes)

        nuspec_content = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>UnknownPkg</id>
    <version>1.0.0</version>
    <authors>Unknown Authors</authors>
  </metadata>
</package>"""
        (pkg_dir / "unknownpkg.nuspec").write_text(nuspec_content, encoding="utf-8")

        deps_path = self.payload_root / "VPNRouter.Headless.deps.json"
        deps = json.loads(deps_path.read_text(encoding="utf-8"))
        deps["targets"][".NETCoreApp,Version=v10.0/linux-x64"]["UnknownPkg/1.0.0"] = {
            "runtime": {"lib/net8.0/UnknownPkg.dll": {}}
        }
        deps["libraries"]["UnknownPkg/1.0.0"] = {"type": "package"}
        deps_path.write_text(json.dumps(deps), encoding="utf-8")

        result = self.mod.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        self.assertFalse(result["complete"])

        lic_text = "UnknownPkg license notice text"
        lic_sha = sha256_text(lic_text)
        (self.payload_root / "licenses" / "UnknownPkg-LICENSE").write_text(lic_text, encoding="utf-8")

        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        for c in data["components"]:
            if c["id"] == "UnknownPkg":
                c["status"] = "verified_texts"
                c["unresolved"] = []
                c["notices"] = [{
                    "path": "licenses/UnknownPkg-LICENSE",
                    "sha256": lic_sha,
                    "source_url": "https://example.com/unknown",
                }]

        # Preserve native unresolved and data["complete"] == False
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            self.mod.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("Unreviewed component/version cannot be verified_texts", str(ctx.exception))
        self.assertIn("UnknownPkg", str(ctx.exception))

    def test_regression_native_only_nuget_fixture_maps_asset(self) -> None:
        """Ensure native-only NuGet package (no runtime DLL) maps native asset into payload_files."""
        assets_path = self.source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        assets["targets"]["net10.0/linux-x64"]["NativePkg/1.0.0"] = {
            "type": "package",
            "native": {"runtimes/linux-x64/native/libnative.so": {}},
        }
        assets["libraries"]["NativePkg/1.0.0"] = {"type": "package", "path": "nativepkg/1.0.0"}
        assets_path.write_text(json.dumps(assets), encoding="utf-8")

        pkg_dir = self.nuget_dir / "nativepkg" / "1.0.0"
        (pkg_dir / "runtimes" / "linux-x64" / "native").mkdir(parents=True)
        so_bytes = b"NATIVE_SO_BYTES_123"
        (pkg_dir / "runtimes" / "linux-x64" / "native" / "libnative.so").write_bytes(so_bytes)
        (self.payload_root / "libnative.so").write_bytes(so_bytes)

        nuspec_content = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>NativePkg</id>
    <version>1.0.0</version>
    <repository type="git" url="https://github.com/example/nativepkg" commit="abcdef1234567890abcdef1234567890abcdef12" />
    <license type="expression">MIT</license>
    <authors>Native Authors</authors>
  </metadata>
</package>"""
        (pkg_dir / "nativepkg.nuspec").write_text(nuspec_content, encoding="utf-8")

        deps_path = self.payload_root / "VPNRouter.Headless.deps.json"
        deps = json.loads(deps_path.read_text(encoding="utf-8"))
        deps["targets"][".NETCoreApp,Version=v10.0/linux-x64"]["NativePkg/1.0.0"] = {
            "native": {"runtimes/linux-x64/native/libnative.so": {}}
        }
        deps["libraries"]["NativePkg/1.0.0"] = {"type": "package"}
        deps_path.write_text(json.dumps(deps), encoding="utf-8")

        lic_text = "MIT License NativePkg text"
        lic_sha = sha256_text(lic_text)
        (self.catalog_dir / "NativePkg-LICENSE").write_text(lic_text, encoding="utf-8")

        self.catalog_content["components"].append({
            "id": "NativePkg",
            "version": "1.0.0",
            "repository_url": "https://github.com/example/nativepkg",
            "revision": "abcdef1234567890abcdef1234567890abcdef12",
            "license_expression": "MIT",
            "files": [{"path": "NativePkg-LICENSE", "sha256": lic_sha, "source_url": "https://example.com/native"}],
            "coverage": "verified_texts",
            "unresolved": [],
        })
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = self.mod.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp = next(c for c in result["components"] if c["id"] == "NativePkg")
        self.assertEqual([pf["path"] for pf in comp["payload_files"]], ["libnative.so"])
        self.assertEqual(comp["status"], "verified_texts")
        self.mod.verify_license_evidence(self.payload_root, self.catalog_dir)

    def test_regression_mixed_runtime_and_native_validates_both_not_loses_one(self) -> None:
        """Ensure package with both runtime and native assets binds and validates both without losing one."""
        assets_path = self.source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        assets["targets"]["net10.0/linux-x64"]["MixedPkg/1.0.0"] = {
            "type": "package",
            "runtime": {"lib/net8.0/MixedPkg.dll": {}},
            "native": {"runtimes/linux-x64/native/libmixed.so": {}},
        }
        assets["libraries"]["MixedPkg/1.0.0"] = {"type": "package", "path": "mixedpkg/1.0.0"}
        assets_path.write_text(json.dumps(assets), encoding="utf-8")

        pkg_dir = self.nuget_dir / "mixedpkg" / "1.0.0"
        (pkg_dir / "lib" / "net8.0").mkdir(parents=True)
        (pkg_dir / "runtimes" / "linux-x64" / "native").mkdir(parents=True)
        dll_bytes = b"MIXED_DLL_BYTES_123"
        so_bytes = b"MIXED_SO_BYTES_456"
        (pkg_dir / "lib" / "net8.0" / "MixedPkg.dll").write_bytes(dll_bytes)
        (pkg_dir / "runtimes" / "linux-x64" / "native" / "libmixed.so").write_bytes(so_bytes)
        (self.payload_root / "MixedPkg.dll").write_bytes(dll_bytes)
        (self.payload_root / "libmixed.so").write_bytes(so_bytes)

        nuspec_content = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>MixedPkg</id>
    <version>1.0.0</version>
    <repository type="git" url="https://github.com/example/mixedpkg" commit="1234567890abcdef1234567890abcdef12345678" />
    <license type="expression">MIT</license>
    <authors>Mixed Authors</authors>
  </metadata>
</package>"""
        (pkg_dir / "mixedpkg.nuspec").write_text(nuspec_content, encoding="utf-8")

        deps_path = self.payload_root / "VPNRouter.Headless.deps.json"
        deps = json.loads(deps_path.read_text(encoding="utf-8"))
        deps["targets"][".NETCoreApp,Version=v10.0/linux-x64"]["MixedPkg/1.0.0"] = {
            "runtime": {"lib/net8.0/MixedPkg.dll": {}},
            "native": {"runtimes/linux-x64/native/libmixed.so": {}},
        }
        deps["libraries"]["MixedPkg/1.0.0"] = {"type": "package"}
        deps_path.write_text(json.dumps(deps), encoding="utf-8")

        lic_text = "MIT License MixedPkg text"
        lic_sha = sha256_text(lic_text)
        (self.catalog_dir / "MixedPkg-LICENSE").write_text(lic_text, encoding="utf-8")

        self.catalog_content["components"].append({
            "id": "MixedPkg",
            "version": "1.0.0",
            "repository_url": "https://github.com/example/mixedpkg",
            "revision": "1234567890abcdef1234567890abcdef12345678",
            "license_expression": "MIT",
            "files": [{"path": "MixedPkg-LICENSE", "sha256": lic_sha, "source_url": "https://example.com/mixed"}],
            "coverage": "verified_texts",
            "unresolved": [],
        })
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = self.mod.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp = next(c for c in result["components"] if c["id"] == "MixedPkg")
        payload_paths = {pf["path"] for pf in comp["payload_files"]}
        self.assertEqual(payload_paths, {"MixedPkg.dll", "libmixed.so"})
        self.assertEqual(comp["status"], "verified_texts")

        self.mod.verify_license_evidence(self.payload_root, self.catalog_dir)

        # Verifier must fail if one asset (the native .so) is removed from evidence
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        for c in data["components"]:
            if c["id"] == "MixedPkg":
                c["payload_files"] = [pf for pf in c["payload_files"] if pf["path"] != "libmixed.so"]
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            self.mod.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("missing payload path bindings", str(ctx.exception))


class TestLicenseEvidenceBase(unittest.TestCase):
    mod = license_evidence

    def setUp(self) -> None:
        self.tmp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.tmp_dir_obj.name)
        self.source_tree = self.tmp / "source_tree"
        self.payload_root = self.tmp / "payload_root"
        self.dotnet_root = self.tmp / "dotnet_root"
        self.catalog_dir = self.tmp / "catalog_dir"
        # Unit tests use valid .nuget_packages under workdir (self.tmp)
        self.nuget_dir = self.tmp / ".nuget_packages"

        for d in [self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir, self.nuget_dir]:
            d.mkdir(parents=True)

        self._setup_synthetic_environment()
        self.mod = getattr(self, "mod", license_evidence) or license_evidence

    def tearDown(self) -> None:
        self.tmp_dir_obj.cleanup()

    def _setup_synthetic_environment(self) -> None:
        obj_dir = self.source_tree / "VPNRouter.Headless" / "obj"
        obj_dir.mkdir(parents=True)
        assets_content = {
            "version": 3,
            "packageFolders": {str(self.nuget_dir): {}},
            "targets": {
                "net10.0/linux-x64": {
                    "Serilog/4.4.0": {"type": "package", "runtime": {"lib/net8.0/Serilog.dll": {}}},
                    "YamlDotNet/18.1.0": {"type": "package", "runtime": {"lib/net8.0/YamlDotNet.dll": {}}},
                }
            },
            "libraries": {
                "Serilog/4.4.0": {"type": "package", "path": "serilog/4.4.0"},
                "YamlDotNet/18.1.0": {"type": "package", "path": "yamldotnet/18.1.0"},
            },
        }
        (obj_dir / "project.assets.json").write_text(json.dumps(assets_content), encoding="utf-8")

        # Serilog 4.4.0
        serilog_dir = self.nuget_dir / "serilog" / "4.4.0"
        (serilog_dir / "lib" / "net8.0").mkdir(parents=True)
        serilog_dll_bytes = b"MOCK_SERILOG_DLL_BYTES_4_4_0"
        (serilog_dir / "lib" / "net8.0" / "Serilog.dll").write_bytes(serilog_dll_bytes)
        serilog_nuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>Serilog</id>
    <version>4.4.0</version>
    <repository type="git" url="https://github.com/serilog/serilog" commit="497f80fda4f9e8f98b9c13ba34b1f0530f8c4449" />
    <license type="expression">Apache-2.0</license>
    <authors>Serilog Contributors</authors>
    <copyright>Copyright © Serilog Contributors</copyright>
  </metadata>
</package>"""
        (serilog_dir / "serilog.nuspec").write_text(serilog_nuspec, encoding="utf-8")

        # YamlDotNet 18.1.0
        yaml_dir = self.nuget_dir / "yamldotnet" / "18.1.0"
        (yaml_dir / "lib" / "net8.0").mkdir(parents=True)
        yaml_dll_bytes = b"MOCK_YAMLDOTNET_DLL_BYTES_18_1_0"
        (yaml_dir / "lib" / "net8.0" / "YamlDotNet.dll").write_bytes(yaml_dll_bytes)
        yaml_nuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>YamlDotNet</id>
    <version>18.1.0</version>
    <repository type="git" url="https://github.com/aaubry/YamlDotNet" commit="748334a8fa7c227740018b284b71ad95cc6b7fc7" />
    <license type="expression">MIT</license>
    <authors>Antoine Aubry</authors>
    <copyright>Copyright (c) Antoine Aubry and contributors</copyright>
  </metadata>
</package>"""
        (yaml_dir / "yamldotnet.nuspec").write_text(yaml_nuspec, encoding="utf-8")

        # Payload files
        (self.payload_root / "Serilog.dll").write_bytes(serilog_dll_bytes)
        (self.payload_root / "YamlDotNet.dll").write_bytes(yaml_dll_bytes)

        deps_content = {
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64", "signature": ""},
            "targets": {
                ".NETCoreApp,Version=v10.0/linux-x64": {
                    "Serilog/4.4.0": {"runtime": {"lib/net8.0/Serilog.dll": {}}},
                    "YamlDotNet/18.1.0": {"runtime": {"lib/net8.0/YamlDotNet.dll": {}}},
                }
            },
            "libraries": {
                "Serilog/4.4.0": {"type": "package"},
                "YamlDotNet/18.1.0": {"type": "package"},
            },
        }
        (self.payload_root / "VPNRouter.Headless.deps.json").write_text(json.dumps(deps_content), encoding="utf-8")

        # Apphost template & published binary
        prefix = b"\x7fELF_FAKE_HEADER"
        placeholder = license_evidence.APPHOST_PLACEHOLDER
        reserved = b"\x00" * (license_evidence.APPHOST_REGION_LEN - len(placeholder))
        suffix = b"_REST_OF_APPHOST_BINARY"
        template_bytes = prefix + placeholder + reserved + suffix

        tpl_dir = self.dotnet_root / "packs" / "Microsoft.NETCore.App.Host.linux-x64" / "10.0.9" / "runtimes" / "linux-x64" / "native"
        tpl_dir.mkdir(parents=True)
        (tpl_dir / "apphost").write_bytes(template_bytes)
        self.template_sha256 = sha256_bytes(template_bytes)

        app_dll_name = b"VPNRouter.Headless.dll"
        pad_len = license_evidence.APPHOST_REGION_LEN - len(app_dll_name)
        published_apphost_bytes = prefix + app_dll_name + (b"\x00" * pad_len) + suffix
        (self.payload_root / "VPNRouter.Headless").write_bytes(published_apphost_bytes)

        # Native binaries
        (self.payload_root / "runtime").mkdir()
        mock_singbox = b"MOCK_SING_BOX_BINARY"
        mock_cronet = b"MOCK_LIBCRONET_SO"
        (self.payload_root / "runtime" / "sing-box").write_bytes(mock_singbox)
        (self.payload_root / "runtime" / "libcronet.so").write_bytes(mock_cronet)
        self.singbox_sha = sha256_bytes(mock_singbox)
        self.cronet_sha = sha256_bytes(mock_cronet)

        # Catalog & notices
        serilog_lic_text = "Apache License 2.0 Serilog text"
        yaml_lic_text = "MIT License YamlDotNet text"
        yaml_libyaml_text = "libyaml license text"
        apphost_lic_text = "MIT License .NET Apphost text"
        sing_box_lic_text = "GPL 3.0 sing-box text"
        cronet_lic_text = "BSD-3-Clause Chromium text"

        (self.catalog_dir / "Serilog-LICENSE").write_text(serilog_lic_text, encoding="utf-8")
        (self.catalog_dir / "YamlDotNet-LICENSE.txt").write_text(yaml_lic_text, encoding="utf-8")
        (self.catalog_dir / "YamlDotNet-LICENSE-libyaml.txt").write_text(yaml_libyaml_text, encoding="utf-8")
        (self.catalog_dir / "dotnet-LICENSE.TXT").write_text(apphost_lic_text, encoding="utf-8")
        (self.catalog_dir / "sing-box-LICENSE").write_text(sing_box_lic_text, encoding="utf-8")
        (self.catalog_dir / "cronet-LICENSE").write_text(cronet_lic_text, encoding="utf-8")

        self.catalog_content = {
            "schema_version": 1,
            "components": [
                {
                    "id": "Serilog",
                    "version": "4.4.0",
                    "repository_url": "https://github.com/serilog/serilog",
                    "revision": "497f80fda4f9e8f98b9c13ba34b1f0530f8c4449",
                    "license_expression": "Apache-2.0",
                    "files": [{"path": "Serilog-LICENSE", "sha256": sha256_text(serilog_lic_text), "source_url": "https://example.com/serilog"}],
                    "coverage": "verified_texts",
                    "unresolved": [],
                },
                {
                    "id": "YamlDotNet",
                    "version": "18.1.0",
                    "repository_url": "https://github.com/aaubry/YamlDotNet",
                    "revision": "748334a8fa7c227740018b284b71ad95cc6b7fc7",
                    "license_expression": "MIT",
                    "files": [
                        {"path": "YamlDotNet-LICENSE.txt", "sha256": sha256_text(yaml_lic_text), "source_url": "https://example.com/yaml"},
                        {"path": "YamlDotNet-LICENSE-libyaml.txt", "sha256": sha256_text(yaml_libyaml_text), "source_url": "https://example.com/yaml-libyaml"},
                    ],
                    "coverage": "verified_texts",
                    "unresolved": [],
                },
                {
                    "id": "Microsoft.NETCore.App.Host.linux-x64",
                    "version": "10.0.9",
                    "repository_url": "https://github.com/dotnet/dotnet",
                    "revision": "901ca941248413c79832d2fdbd709da0c4386353",
                    "license_expression": "MIT",
                    "template_sha256": self.template_sha256,
                    "files": [{"path": "dotnet-LICENSE.TXT", "sha256": sha256_text(apphost_lic_text), "source_url": "https://example.com/dotnet"}],
                    "coverage": "verified_texts",
                    "unresolved": [],
                },
                {
                    "id": "sing-box-vpnctl",
                    "version": "1.14.0-vpnctl.5",
                    "repository_url": "https://github.com/PavelLizunov/sing-box-vpnctl",
                    "revision": "8688eab3c51d07ff89a124ce247044409b9f93fd",
                    "license_expression": "GPL-3.0-or-later",
                    "payload_sha256": self.singbox_sha,
                    "files": [{"path": "sing-box-LICENSE", "sha256": sha256_text(sing_box_lic_text), "source_url": "https://example.com/sing-box"}],
                    "coverage": "partial",
                    "unresolved": ["embedded-go-notices"],
                },
                {
                    "id": "Cronet",
                    "version": "1.13.14",
                    "repository_url": "https://github.com/SagerNet/cronet-go",
                    "revision": "def9ff0fb992d0360f56ddbd4b43d65d29849771",
                    "license_expression": "BSD-3-Clause",
                    "payload_sha256": self.cronet_sha,
                    "files": [{"path": "cronet-LICENSE", "sha256": sha256_text(cronet_lic_text), "source_url": "https://example.com/cronet"}],
                    "coverage": "partial",
                    "unresolved": ["linked-dependency-selection"],
                },
            ],
        }
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")


class TestLicenseEvidence(TestLicenseEvidenceBase, LicenseEvidenceRegressionMixin):
    mod = license_evidence

    def test_collect_and_verify_partial_coverage_cronet(self) -> None:
        """Verify standard collection where native components have partial coverage and unresolved notices."""
        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        self.assertEqual(result["schema_version"], 1)
        self.assertFalse(result["complete"])

        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Serilog"]["status"], "verified_texts")
        self.assertEqual(comps["Serilog"]["authors"], "Serilog Contributors")
        self.assertEqual(comps["YamlDotNet"]["status"], "verified_texts")
        self.assertEqual(comps["Microsoft.NETCore.App.Host.linux-x64"]["status"], "verified_texts")

        self.assertEqual(comps["sing-box-vpnctl"]["status"], "unresolved")
        self.assertEqual(comps["Cronet"]["status"], "unresolved")
        self.assertEqual(comps["Cronet"]["payload_files"][0]["path"], "runtime/libcronet.so")

        manifest_file = self.payload_root / "licenses" / "components.json"
        self.assertTrue(manifest_file.is_file())

        license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)

    def test_collect_and_verify_all_verified_complete_true(self) -> None:
        """Verify that when all components have verified_texts coverage, complete is True."""
        for c in self.catalog_content["components"]:
            c["coverage"] = "verified_texts"
            c["unresolved"] = []
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        self.assertTrue(result["complete"])
        license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)

    def test_catalog_partial_unresolved_even_if_unresolved_list_empty(self) -> None:
        """Ensure catalog coverage partial forces unresolved even if unresolved list accidentally empty."""
        for c in self.catalog_content["components"]:
            if c["id"] == "Cronet":
                c["coverage"] = "partial"
                c["unresolved"] = []  # accidentally empty
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Cronet"]["status"], "unresolved")
        self.assertIn("catalog coverage is partial", comps["Cronet"]["unresolved"])
        self.assertFalse(result["complete"])

    def test_verify_rejects_catalog_partial_falsely_marked_verified(self) -> None:
        """Ensure verifier catches self-modified evidence marking partial component as verified."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        for c in data["components"]:
            if c["id"] == "Cronet":
                c["status"] = "verified_texts"
                c["unresolved"] = []
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("partial coverage", str(ctx.exception))

    def test_xml_safety_dtd_rejected(self) -> None:
        """Ensure DOCTYPE declarations in XML are rejected fail-closed."""
        p = self.tmp / "test_dtd.xml"
        p.write_text("<?xml version=\"1.0\"?><!DOCTYPE note [<!ELEMENT note (to)>]><package/>", encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_read_xml(p)
        self.assertIn("DOCTYPE", str(ctx.exception))

    def test_xml_safety_entity_rejected(self) -> None:
        """Ensure ENTITY declarations in XML are rejected fail-closed."""
        p = self.tmp / "test_entity.xml"
        p.write_text("<?xml version=\"1.0\"?><!DOCTYPE test [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><package/>", encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_read_xml(p)
        self.assertIn("forbidden", str(ctx.exception))

    def test_xml_safety_utf16_and_nul_rejected(self) -> None:
        """Ensure UTF-16 encoding and NUL bytes in XML are rejected before DTD checks."""
        # UTF-16 LE BOM
        p_utf16 = self.tmp / "test_utf16.xml"
        p_utf16.write_bytes(b"\xff\xfe<\x00p\x00a\x00c\x00k\x00a\x00g\x00e\x00/\x00>\x00")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_read_xml(p_utf16)
        self.assertIn("UTF-16", str(ctx.exception))

        # NUL byte in XML
        p_nul = self.tmp / "test_nul.xml"
        p_nul.write_bytes(b"<package>\x00</package>")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_read_xml(p_nul)
        self.assertIn("NUL", str(ctx.exception))

        # Declared non-UTF-8 encoding
        p_decl = self.tmp / "test_decl.xml"
        p_decl.write_text("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><package/>", encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_read_xml(p_decl)
        self.assertIn("Non-UTF-8", str(ctx.exception))

    def test_json_duplicate_keys_rejected(self) -> None:
        """Ensure JSON with duplicate keys is rejected."""
        p = self.tmp / "dup_keys.json"
        p.write_text('{"key": 1, "key": 2}', encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_read_json(p)
        self.assertIn("Duplicate key", str(ctx.exception))

    def test_catalog_duplicate_ids_and_files_rejected(self) -> None:
        """Ensure duplicate component IDs or duplicate notice files in catalog are rejected."""
        # Duplicate component ID
        dup_id_content = {
            "schema_version": 1,
            "components": [
                {"id": "Serilog", "version": "1.0", "files": []},
                {"id": "serilog", "version": "2.0", "files": []},
            ],
        }
        p_dup_id = self.tmp / "dup_id_cat.json"
        p_dup_id.write_text(json.dumps(dup_id_content), encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.collect_license_evidence(self.source_tree, self.payload_root, self.dotnet_root, p_dup_id)
        self.assertIn("Duplicate component ID", str(ctx.exception))

        # Duplicate notice file path
        same_text = "content"
        same_sha = sha256_text(same_text)
        (self.tmp / "same.txt").write_text(same_text, encoding="utf-8")
        dup_file_content = {
            "schema_version": 1,
            "components": [
                {"id": "A", "files": [{"path": "same.txt", "sha256": same_sha, "source_url": "u"}]},
                {"id": "B", "files": [{"path": "same.txt", "sha256": same_sha, "source_url": "u"}]},
            ],
        }
        p_dup_file = self.tmp / "dup_file_cat.json"
        p_dup_file.write_text(json.dumps(dup_file_content), encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.collect_license_evidence(self.source_tree, self.payload_root, self.dotnet_root, p_dup_file)
        self.assertIn("Duplicate notice file path", str(ctx.exception))

    def test_empty_notice_file_rejected(self) -> None:
        """Ensure empty notice file (0 bytes) is rejected fail-closed."""
        empty_notice = self.catalog_dir / "empty-notice.txt"
        empty_notice.write_bytes(b"")
        copier = license_evidence.NoticeCopier(self.payload_root, self.payload_root / "licenses")
        with self.assertRaises(ValueError) as ctx:
            copier.copy_notice(empty_notice, "empty.txt", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "https://example.com")
        self.assertIn("empty", str(ctx.exception))

    def test_fifo_rejected_before_open(self) -> None:
        """Ensure FIFO named pipes are rejected before open."""
        fifo_path = self.tmp / "test_fifo"
        try:
            os.mkfifo(fifo_path)
        except (AttributeError, OSError):
            self.skipTest("FIFO creation not supported in this environment")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.check_safe_path(fifo_path)
        self.assertIn("Non-regular", str(ctx.exception))

    def test_symlink_and_ancestor_rejected(self) -> None:
        """Ensure symlinks and symlink ancestors are rejected."""
        real_file = self.tmp / "real.txt"
        real_file.write_text("hello", encoding="utf-8")
        link_file = self.tmp / "link.txt"
        link_file.symlink_to(real_file)
        with self.assertRaises(ValueError) as ctx:
            license_evidence.check_safe_path(link_file)
        self.assertIn("Symlink rejected", str(ctx.exception))

    def test_hardlink_rejected(self) -> None:
        """Ensure hardlinks with st_nlink > 1 are rejected."""
        f1 = self.tmp / "f1.txt"
        f1.write_text("content", encoding="utf-8")
        f2 = self.tmp / "f2.txt"
        try:
            os.link(f1, f2)
        except OSError:
            self.skipTest("Filesystem does not support hard links")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.check_safe_path(f1)
        self.assertIn("Hardlink rejected", str(ctx.exception))

    def test_path_traversal_backslash_and_dots_rejected(self) -> None:
        """Ensure escaping, backslashes, empty, and dot paths are rejected."""
        with self.assertRaises(ValueError):
            license_evidence.safe_resolve_relative(self.catalog_dir, "../outside.txt")
        with self.assertRaises(ValueError):
            license_evidence.safe_resolve_relative(self.catalog_dir, "subdir\\file.txt")
        with self.assertRaises(ValueError):
            license_evidence.safe_resolve_relative(self.catalog_dir, "")
        with self.assertRaises(ValueError):
            license_evidence.safe_resolve_relative(self.catalog_dir, ".")
        with self.assertRaises(ValueError):
            license_evidence.safe_resolve_relative(self.catalog_dir, "a/./b.txt")

    def test_apphost_verification_success_and_tamper(self) -> None:
        """Test apphost customization verification against template and tampered failure."""
        prefix = b"\x7fELF_FAKE_HEADER"
        placeholder = license_evidence.APPHOST_PLACEHOLDER
        reserved = b"\x00" * (license_evidence.APPHOST_REGION_LEN - len(placeholder))
        suffix = b"_REST_OF_BINARY"
        tpl = prefix + placeholder + reserved + suffix

        # Valid apphost
        app_dll = b"MyApp.dll"
        pad = license_evidence.APPHOST_REGION_LEN - len(app_dll)
        valid_pub = prefix + app_dll + (b"\x00" * pad) + suffix
        self.assertTrue(license_evidence.verify_apphost_customization(tpl, valid_pub, "MyApp.dll"))

        # Tampered prefix
        tampered_prefix = b"\x7fELF_HACK_HEADER" + app_dll + (b"\x00" * pad) + suffix
        self.assertFalse(license_evidence.verify_apphost_customization(tpl, tampered_prefix, "MyApp.dll"))

        # Tampered suffix
        tampered_suffix = prefix + app_dll + (b"\x00" * pad) + b"_TAMPERED_SUFFIX"
        self.assertFalse(license_evidence.verify_apphost_customization(tpl, tampered_suffix, "MyApp.dll"))

        # Tampered embedded region
        tampered_region = prefix + app_dll + b"\x01" + (b"\x00" * (pad - 1)) + suffix
        self.assertFalse(license_evidence.verify_apphost_customization(tpl, tampered_region, "MyApp.dll"))

    def test_apphost_template_mismatch_records_unresolved(self) -> None:
        """Ensure apphost template hash mismatch records unresolved."""
        tpl_file = self.dotnet_root / "packs" / "Microsoft.NETCore.App.Host.linux-x64" / "10.0.9" / "runtimes" / "linux-x64" / "native" / "apphost"
        tpl_file.write_bytes(b"CORRUPTED_TEMPLATE_BYTES")

        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        apphost_comp = comps["Microsoft.NETCore.App.Host.linux-x64"]
        self.assertEqual(apphost_comp["status"], "unresolved")
        self.assertTrue(any("template hash mismatch" in u for u in apphost_comp["unresolved"]))

    def test_native_mapping_exact_cronet(self) -> None:
        """Ensure native Cronet is mapped strictly to runtime/libcronet.so (no lib substring collision)."""
        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Cronet"]["payload_files"][0]["path"], "runtime/libcronet.so")

    def test_root_manifest_complete_mismatch_rejected(self) -> None:
        """Ensure root manifest.json license_inventory_complete must equal evidence complete."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        # Write manifest.json with license_inventory_complete=True (while evidence complete=False)
        manifest_data = {"license_inventory_complete": True}
        (self.payload_root / "manifest.json").write_text(json.dumps(manifest_data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("license_inventory_complete", str(ctx.exception))

    def test_mandatory_component_removal_rejected(self) -> None:
        """Ensure removing a mandatory native component raises ValueError."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        data["components"] = [c for c in data["components"] if c["id"] != "Cronet"]
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("Mandatory component", str(ctx.exception))

    def test_empty_payload_files_rejected(self) -> None:
        """Ensure component with empty payload_files is rejected."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        data["components"][0]["payload_files"] = []
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("empty payload_files", str(ctx.exception))

    def test_empty_notices_rejected(self) -> None:
        """Ensure component with empty notices is rejected."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        data["components"][0]["notices"] = []
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("empty notices", str(ctx.exception))

    def test_unknown_package_version_records_unresolved(self) -> None:
        """Ensure unknown package version records unresolved and does not use old text as exact."""
        self.catalog_content["components"][0]["version"] = "4.5.0"
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Serilog"]["status"], "unresolved")
        self.assertTrue(any("unknown version" in u for u in comps["Serilog"]["unresolved"]))
        # Old notice text should not be copied as verified
        self.assertEqual(len(comps["Serilog"]["notices"]), 0)

    def test_payload_hash_drift_vs_nuget_records_unresolved(self) -> None:
        """Ensure hash drift between payload DLL and NuGet package asset records unresolved."""
        (self.payload_root / "Serilog.dll").write_bytes(b"CORRUPTED_SERILOG_PAYLOAD")
        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Serilog"]["status"], "unresolved")
        self.assertTrue(any("payload hash mismatch" in u for u in comps["Serilog"]["unresolved"]))

    def test_nuspec_path_traversal_rejected(self) -> None:
        """Ensure path traversal in nuspec license file is rejected."""
        nuspec_traversal = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>BadPkg</id>
    <version>1.0.0</version>
    <license type="file">../../outside.txt</license>
  </metadata>
</package>"""
        pkg_dir = self.nuget_dir / "badpkg" / "1.0.0"
        pkg_dir.mkdir(parents=True)
        (pkg_dir / "badpkg.nuspec").write_text(nuspec_traversal, encoding="utf-8")
        with self.assertRaises(ValueError):
            license_evidence.safe_resolve_relative(pkg_dir, "../../outside.txt")

    def test_real_catalog_verification(self) -> None:
        """Verify the actual production catalog.json with 5 components and 51 notices."""
        real_catalog_dir = ARCH_DIR / "notices"
        cat_file = real_catalog_dir / "catalog.json"
        self.assertTrue(cat_file.is_file())
        cat_data = license_evidence.safe_read_json(cat_file)
        comps = cat_data.get("components", [])
        self.assertEqual(len(comps), 5)
        total_files = sum(len(c.get("files", [])) for c in comps)
        self.assertEqual(total_files, 51)
        for c in comps:
            for f in c.get("files", []):
                p = license_evidence.safe_resolve_relative(real_catalog_dir, f["path"])
                self.assertTrue(p.is_file())
                self.assertGreater(p.lstat().st_size, 0)
                self.assertEqual(license_evidence.compute_sha256(p), f["sha256"])

    def test_traversal_asset_and_package_cache(self) -> None:
        """Ensure path traversal in assets libraries path or runtime asset path is rejected."""
        assets_path = self.source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
        assets_data = json.loads(assets_path.read_text(encoding="utf-8"))

        orig_path = assets_data["libraries"]["Serilog/4.4.0"]["path"]
        assets_data["libraries"]["Serilog/4.4.0"]["path"] = "../../outside"
        assets_path.write_text(json.dumps(assets_data), encoding="utf-8")
        with self.assertRaises(ValueError):
            license_evidence.collect_license_evidence(
                self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
            )

        assets_data["libraries"]["Serilog/4.4.0"]["path"] = orig_path
        assets_data["targets"]["net10.0/linux-x64"]["Serilog/4.4.0"]["runtime"] = {"../../outside.dll": {}}
        assets_path.write_text(json.dumps(assets_data), encoding="utf-8")
        with self.assertRaises(ValueError):
            license_evidence.collect_license_evidence(
                self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
            )

    def test_two_targets_different_bytes_selects_exact_rid(self) -> None:
        """Ensure exact 'net10.0/linux-x64' target is selected even if another generic net10.0 target exists."""
        serilog_dir = self.nuget_dir / "serilog" / "4.4.0"
        (serilog_dir / "lib" / "net10.0").mkdir(parents=True)
        generic_bytes = b"GENERIC_TARGET_BYTES_DIFFERENT"
        (serilog_dir / "lib" / "net10.0" / "Serilog.dll").write_bytes(generic_bytes)

        assets_path = self.source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
        assets_data = json.loads(assets_path.read_text(encoding="utf-8"))
        assets_data["targets"] = {
            "net10.0": {
                "Serilog/4.4.0": {"type": "package", "runtime": {"lib/net10.0/Serilog.dll": {}}},
            },
            "net10.0/linux-x64": assets_data["targets"]["net10.0/linux-x64"],
        }
        assets_path.write_text(json.dumps(assets_data), encoding="utf-8")

        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Serilog"]["status"], "verified_texts")
        self.assertEqual(comps["Serilog"]["payload_files"][0]["sha256"], sha256_bytes(b"MOCK_SERILOG_DLL_BYTES_4_4_0"))

    def test_verify_rejects_removed_or_renamed_notice(self) -> None:
        """Ensure verifier rejects evidence if a catalog notice was removed or renamed."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))

        saved_notices = None
        for c in data["components"]:
            if c["id"] == "YamlDotNet":
                saved_notices = list(c["notices"])
                c["notices"] = [c["notices"][0]]
        comp_file.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertTrue("Notice count mismatch" in str(ctx.exception) or "Notice paths mismatch" in str(ctx.exception))

        for c in data["components"]:
            if c["id"] == "YamlDotNet":
                c["notices"] = list(saved_notices)
                c["notices"][0]["path"] = "licenses/YamlDotNet-RENAMED.txt"
        (self.payload_root / "licenses" / "YamlDotNet-RENAMED.txt").write_text("MIT License YamlDotNet text", encoding="utf-8")
        comp_file.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("Notice paths mismatch", str(ctx.exception))

    def test_missing_repo_revision_records_unresolved(self) -> None:
        """Ensure nuspec missing repository revision is marked unresolved and not false verified."""
        serilog_dir = self.nuget_dir / "serilog" / "4.4.0"
        serilog_nuspec_no_rev = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>Serilog</id>
    <version>4.4.0</version>
    <repository type="git" url="https://github.com/serilog/serilog" />
    <license type="expression">Apache-2.0</license>
    <authors>Serilog Contributors</authors>
    <copyright>Copyright © Serilog Contributors</copyright>
  </metadata>
</package>"""
        (serilog_dir / "serilog.nuspec").write_text(serilog_nuspec_no_rev, encoding="utf-8")
        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Serilog"]["status"], "unresolved")
        self.assertTrue(any("missing repository revision" in u for u in comps["Serilog"]["unresolved"]))
        self.assertFalse(result["complete"])

    def test_missing_deps_rejected(self) -> None:
        """Ensure missing VPNRouter.Headless.deps.json fails both collection and verification."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        deps_file = self.payload_root / "VPNRouter.Headless.deps.json"
        deps_file.unlink()

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("VPNRouter.Headless.deps.json", str(ctx.exception))

        with self.assertRaises(ValueError) as ctx:
            license_evidence.collect_license_evidence(
                self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
            )
        self.assertIn("VPNRouter.Headless.deps.json", str(ctx.exception))

    def test_native_swapped_binding_rejected(self) -> None:
        """Ensure swapped native payload file bindings are rejected by verifier."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))

        sb_comp = next(c for c in data["components"] if c["id"] == "sing-box-vpnctl")
        cr_comp = next(c for c in data["components"] if c["id"] == "Cronet")
        sb_comp["payload_files"], cr_comp["payload_files"] = cr_comp["payload_files"], sb_comp["payload_files"]
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("missing exact payload path", str(ctx.exception))

    def test_schema_false_complete_rejected(self) -> None:
        """Ensure components.json complete flag must exactly match computed completeness."""
        license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comp_file = self.payload_root / "licenses" / "components.json"
        data = json.loads(comp_file.read_text(encoding="utf-8"))
        self.assertFalse(data["complete"])
        data["complete"] = True
        comp_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)
        self.assertIn("does not match computed completeness", str(ctx.exception))

    def test_unknown_version_unresolved_remains_verifiable(self) -> None:
        """Ensure unknown version component remains unresolved with 0 notices and passes verification."""
        self.catalog_content["components"][0]["version"] = "4.5.0"
        (self.catalog_dir / "catalog.json").write_text(json.dumps(self.catalog_content), encoding="utf-8")

        result = license_evidence.collect_license_evidence(
            self.source_tree, self.payload_root, self.dotnet_root, self.catalog_dir
        )
        comps = {c["id"]: c for c in result["components"]}
        self.assertEqual(comps["Serilog"]["status"], "unresolved")
        self.assertEqual(len(comps["Serilog"]["notices"]), 0)
        self.assertEqual(comps["Serilog"]["source"]["revision"], "497f80fda4f9e8f98b9c13ba34b1f0530f8c4449")
        self.assertFalse(result["complete"])

        license_evidence.verify_license_evidence(self.payload_root, self.catalog_dir)

    def test_xml_duplicate_elements_rejected(self) -> None:
        """Ensure duplicate metadata, id, repository, and license tags in XML nuspec are rejected."""
        serilog_dir = self.nuget_dir / "serilog" / "4.4.0"
        dup_xml = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>Serilog</id>
    <id>SerilogDuplicate</id>
    <version>4.4.0</version>
    <license type="expression">Apache-2.0</license>
  </metadata>
</package>"""
        (serilog_dir / "serilog.nuspec").write_text(dup_xml, encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            license_evidence.parse_nuspec(serilog_dir / "serilog.nuspec")
        self.assertIn("Duplicate <id>", str(ctx.exception))

    def test_safe_resolve_relative_double_slash_and_parent_symlink(self) -> None:
        """Ensure double slash segments and symlinked parent of missing targets are rejected."""
        with self.assertRaises(ValueError) as ctx:
            license_evidence.safe_resolve_relative(self.tmp, "subdir//file.txt")
        self.assertIn("double slash", str(ctx.exception))

        real_dir = self.tmp / "real_target_dir"
        real_dir.mkdir()
        link_dir = self.tmp / "link_target_dir"
        link_dir.symlink_to(real_dir)
        with self.assertRaises(ValueError) as ctx:
            license_evidence.check_safe_path(link_dir / "non_existent_file.txt", root=self.tmp)
        self.assertIn("Symlink", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
