"""Build the pinned production runtime with the Linux gVisor close-order backport."""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import shutil
import subprocess

SOURCE_URL = "https://github.com/PavelLizunov/sing-box-vpnctl.git"
SOURCE_COMMIT = "f62085aa7cc0f3ee665ac3043d35698760a386e9"
VERSION = "1.14.2-vpnctl.2+omarchy-close1"
GO_VERSION = "go1.26.8"
BUILD_TAGS = "with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_v2ray_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg"
ARCH_DIR = Path(__file__).resolve().parent


def build_runtime(work_dir: Path, output: Path) -> dict:
    source = work_dir / "runtime-source"
    source.mkdir(mode=0o700)
    env = {**os.environ, "GOTOOLCHAIN": "local", "CGO_ENABLED": "0", "GOWORK": "off",
           "GOOS": "linux", "GOARCH": "amd64", "GOMODCACHE": str(work_dir / "go-mod"),
           "GOCACHE": str(work_dir / "go-build")}

    def run(args, **kwargs):
        return subprocess.run(args, cwd=source, env=env, text=True, timeout=900, **kwargs)

    version = run(["go", "version"], capture_output=True, check=True).stdout.strip()
    if GO_VERSION not in version.split():
        raise RuntimeError(f"Runtime source build requires {GO_VERSION}")
    for args in [["git", "init", "--quiet"], ["git", "remote", "add", "origin", SOURCE_URL],
                 ["git", "fetch", "--depth=1", "origin", SOURCE_COMMIT],
                 ["git", "checkout", "--detach", "--quiet", "FETCH_HEAD"],
                 ["git", "submodule", "update", "--init", "--recursive", "--depth=1"]]:
        run(args, check=True)
    actual = run(["git", "rev-parse", "HEAD"], capture_output=True, check=True).stdout.strip()
    if actual != SOURCE_COMMIT:
        raise RuntimeError("Runtime source commit mismatch")

    regression = ARCH_DIR / "runtime-close_test.go"
    shutil.copy2(regression, source / "protocol/tun/omarchy_close_test.go")
    test = ["go", "test", "-tags", BUILD_TAGS, "-ldflags=-checklinkname=0", "-count=1",
            "-timeout=120s", "./protocol/tun", "-run", "^TestLinuxGvisorClosesTunBeforeStack$"]
    red = run(test, capture_output=True, check=False)
    (work_dir / "runtime-close-red.log").write_text(red.stdout + red.stderr)
    if red.returncode == 0 or "Linux gvisor stack closed before TUN" not in red.stdout + red.stderr:
        raise RuntimeError("Unpatched close-order regression did not reproduce its expected failure")
    patch = ARCH_DIR / "runtime-close.patch"
    run(["git", "apply", "--check", str(patch)], check=True)
    run(["git", "apply", str(patch)], check=True)
    run(test, check=True)
    run(["go", "build", "-trimpath", "-tags", BUILD_TAGS,
         "-ldflags", "-s -w -checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=" + VERSION,
         "-o", str(output), "./cmd/sing-box"], check=True)
    info = run(["go", "version", "-m", str(output)], capture_output=True, check=True).stdout
    if not all(tag in info for tag in BUILD_TAGS.split(",")):
        raise RuntimeError("Patched runtime is missing production build tags")
    return {"repository": SOURCE_URL, "commit": SOURCE_COMMIT, "version": VERSION,
            "go_version": version, "build_tags": BUILD_TAGS,
            "patch_sha256": hashlib.sha256(patch.read_bytes()).hexdigest(),
            "regression_sha256": hashlib.sha256(regression.read_bytes()).hexdigest(),
            "binary_sha256": hashlib.sha256(output.read_bytes()).hexdigest()}
