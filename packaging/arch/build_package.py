#!/usr/bin/env python3
"""Source export, pinned download verification, and staging driver for Arch Linux package.

Pure Python standard library implementation adhering to phase-omarchy-runtime-package invariants.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tarfile
from typing import Any
import urllib.error
import urllib.request

ARCH_DIR = Path(__file__).resolve().parent
if str(ARCH_DIR) not in sys.path:
    sys.path.insert(0, str(ARCH_DIR))

try:
    import staging_tools
except ImportError as err:
    raise ImportError(f"Failed to import staging_tools from {ARCH_DIR}: {err}") from err

try:
    import license_evidence
except ImportError as err:
    raise ImportError(f"Failed to import license_evidence from {ARCH_DIR}: {err}") from err

BACKEND_COMMIT: str = "05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a"
RUNTIME_URL: str = (
    "https://github.com/PavelLizunov/sing-box-vpnctl/releases/download/"
    "v1.14.0-vpnctl.5/sing-box-1.14.0-vpnctl.5-linux-amd64.tar.gz"
)
RUNTIME_SHA256: str = "5f98eacc95b9ed9c1d53592605d812d9d2cb8c496fa95723cfb7c5f9447fe46e"
RUNTIME_VERSION: str = "1.14.0-vpnctl.5"

CRONET_URL: str = (
    "https://github.com/SagerNet/sing-box/releases/download/"
    "v1.13.14/sing-box-1.13.14-linux-amd64.tar.gz"
)
CRONET_SHA256: str = "f48703461a15476951ac4967cdad339d986f4b8096b4eb3ff0829a500502d697"
CRONET_VERSION: str = "1.13.14"

ALLOWED_DOWNLOADS: dict[str, tuple[str, str]] = {
    RUNTIME_URL: (RUNTIME_SHA256, "runtime.tar.gz"),
    CRONET_URL: (CRONET_SHA256, "cronet.tar.gz"),
}

DOTNET_REQUIRED_VERSION: str = "10.0.301"
CHUNK_SIZE: int = 64 * 1024
MAX_CACHE_READ_SIZE: int = 512 * 1024 * 1024
MAX_DOWNLOAD_SIZE: int = 512 * 1024 * 1024


class StrictHttpsRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Enforce strict HTTPS scheme across all HTTP redirects."""

    def redirect_request(
        self, req: urllib.request.Request, fp: object, code: int, msg: str, headers: object, newurl: str
    ) -> urllib.request.Request | None:
        if not newurl.lower().startswith("https://"):
            raise ValueError(f"Insecure or non-HTTPS redirect scheme rejected: {newurl}")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def validate_sha256(val: str, name: str = "SHA256") -> str:
    s = val.strip().lower()
    if len(s) != 64 or not all(c in "0123456789abcdef" for c in s):
        raise ValueError(f"Invalid {name}: {val!r}")
    return s


def validate_work_dir(work_dir_arg: str | Path) -> Path:
    work_dir = Path(work_dir_arg)
    if not work_dir.is_absolute():
        raise ValueError(f"--work-dir must be an absolute path: {work_dir}")
    if work_dir.is_symlink():
        raise ValueError(f"--work-dir cannot be a symlink: {work_dir}")
    curr = work_dir.parent
    while True:
        if curr.is_symlink():
            raise ValueError(f"--work-dir ancestor is a symlink: {curr}")
        if curr == curr.parent:
            break
        curr = curr.parent
    if work_dir.exists():
        raise ValueError(f"--work-dir must be a new directory, already exists: {work_dir}")
    work_dir.mkdir(parents=True, mode=0o700, exist_ok=False)
    return work_dir


def verify_repo_commit(repo_dir: Path, commit_sha: str) -> None:
    if not repo_dir.is_dir():
        raise ValueError(f"--repo directory does not exist: {repo_dir}")
    res = subprocess.run(
        ["git", "cat-file", "-e", f"{commit_sha}^{{commit}}"],
        cwd=repo_dir,
        capture_output=True,
        text=True,
        check=False,
    )
    if res.returncode != 0:
        raise ValueError(f"Git object {commit_sha} not found in repository {repo_dir}: {res.stderr.strip()}")


def export_source(repo_dir: Path, commit_sha: str, work_dir: Path) -> tuple[Path, str]:
    res = subprocess.run(
        ["git", "ls-tree", "-r", "--name-only", commit_sha],
        cwd=repo_dir,
        capture_output=True,
        text=True,
        check=True,
    )
    tracked = set(res.stdout.strip().splitlines())
    required_paths: list[str] = []

    for path_str in sorted(tracked):
        if "/" not in path_str:
            if path_str.endswith((".props", ".targets", ".json", ".config")) or path_str in (
                "LICENSE",
                "NOTICE.md",
                "VPNRouter.sln",
                "Version.props",
            ):
                required_paths.append(path_str)

    for project_dir in ["VPNRouter.Core", "VPNRouter.Headless", "profiles"]:
        if any(p == project_dir or p.startswith(project_dir + "/") for p in tracked):
            required_paths.append(project_dir)

    source_tar = work_dir / "source.tar"
    subprocess.run(
        ["git", "archive", "--format=tar", commit_sha, *required_paths, "-o", str(source_tar)],
        cwd=repo_dir,
        check=True,
    )
    source_sha256 = compute_file_sha256(source_tar)
    return source_tar, source_sha256


def acquire_archive(
    url: str,
    expected_sha256: str,
    filename: str,
    work_dir: Path,
    archive_cache: Path | None,
) -> Path:
    if url not in ALLOWED_DOWNLOADS:
        raise ValueError(f"URL is not in allowed pinned downloads: {url}")
    allow_sha, allow_filename = ALLOWED_DOWNLOADS[url]
    expected_sha = validate_sha256(expected_sha256, "expected_sha256")
    if expected_sha != allow_sha.lower():
        raise ValueError(f"expected_sha256 {expected_sha256} does not match allowed hash {allow_sha}")
    if filename != allow_filename:
        raise ValueError(f"filename {filename} does not match allowed filename {allow_filename}")
    if not url.lower().startswith("https://"):
        raise ValueError(f"Only HTTPS URLs are allowed: {url}")

    target_path = work_dir / filename

    if archive_cache is not None:
        if os.path.islink(archive_cache) or not archive_cache.is_dir():
            raise ValueError(f"Archive cache is invalid or a symlink: {archive_cache}")
        curr = archive_cache
        while True:
            if os.path.islink(curr):
                raise ValueError(f"Archive cache ancestor is a symlink: {curr}")
            if curr == curr.parent:
                break
            curr = curr.parent

        cached_file = archive_cache / filename
        if os.path.islink(cached_file):
            raise ValueError(f"Cache file cannot be a symlink: {cached_file}")
        if cached_file.exists():
            if not cached_file.is_file():
                raise ValueError(f"Cache entry is not a regular file: {cached_file}")
            actual_sha = compute_file_sha256(cached_file)
            if actual_sha != expected_sha:
                raise ValueError(
                    f"Cache file SHA-256 mismatch for {filename}: expected {expected_sha}, got {actual_sha}"
                )
            shutil.copy2(cached_file, target_path)
            return target_path

    opener = urllib.request.build_opener(StrictHttpsRedirectHandler())
    req = urllib.request.Request(url, headers={"User-Agent": "VPNRouter-Arch-Build/0.1.0"})
    tmp_dl = work_dir / f".{filename}.download.{os.getpid()}"
    hasher = hashlib.sha256()
    bytes_downloaded = 0

    try:
        with opener.open(req, timeout=60) as resp, tmp_dl.open("wb") as out_fp:
            while chunk := resp.read(CHUNK_SIZE):
                bytes_downloaded += len(chunk)
                if bytes_downloaded > MAX_DOWNLOAD_SIZE:
                    raise ValueError(f"Download exceeded maximum size {MAX_DOWNLOAD_SIZE} bytes")
                hasher.update(chunk)
                out_fp.write(chunk)
        actual_sha = hasher.hexdigest().lower()
        if actual_sha != expected_sha:
            raise ValueError(
                f"Downloaded archive SHA-256 mismatch for {filename}: expected {expected_sha}, got {actual_sha}"
            )
        os.replace(tmp_dl, target_path)
    finally:
        if tmp_dl.exists():
            try:
                tmp_dl.unlink()
            except OSError:
                pass

    return target_path


def resolve_dotnet() -> str:
    dotnet_root = os.environ.get("DOTNET_ROOT")
    if dotnet_root:
        candidate = Path(dotnet_root) / "dotnet"
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    which_dotnet = shutil.which("dotnet")
    if which_dotnet:
        return which_dotnet
    raise FileNotFoundError("dotnet binary not found in PATH or DOTNET_ROOT")


def publish_headless(source_tree: Path, publish_dir: Path, work_dir: Path) -> None:
    if not (source_tree / "global.json").is_file():
        raise FileNotFoundError(f"global.json not found in source tree: {source_tree / 'global.json'}")

    proj_path = source_tree / "VPNRouter.Headless" / "VPNRouter.Headless.csproj"
    if not proj_path.is_file():
        raise FileNotFoundError(f"Headless project file not found: {proj_path}")

    dotnet_bin = resolve_dotnet()

    dotnet_home = work_dir / ".dotnet_home"
    dotnet_home.mkdir(parents=True, mode=0o700, exist_ok=True)
    nuget_packages = work_dir / ".nuget_packages"
    nuget_packages.mkdir(parents=True, mode=0o700, exist_ok=True)

    env = os.environ.copy()
    env.update({
        "HOME": str(dotnet_home),
        "XDG_CONFIG_HOME": str(dotnet_home / ".config"),
        "XDG_DATA_HOME": str(dotnet_home / ".local" / "share"),
        "XDG_CACHE_HOME": str(dotnet_home / ".cache"),
        "NUGET_PACKAGES": str(nuget_packages),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_NOLOGO": "1",
    })

    ver_res = subprocess.run(
        [dotnet_bin, "--version"],
        cwd=source_tree,
        env=env,
        capture_output=True,
        text=True,
        check=False,
    )
    if ver_res.returncode != 0:
        raise RuntimeError(f"Failed to check dotnet version (exit {ver_res.returncode}): {ver_res.stderr.strip()}")
    actual_ver = ver_res.stdout.strip()
    if actual_ver != DOTNET_REQUIRED_VERSION:
        raise ValueError(f"dotnet version {actual_ver!r} does not match required {DOTNET_REQUIRED_VERSION}")

    cmd = [
        dotnet_bin, "publish", str(proj_path), "-c", "Release", "-r", "linux-x64",
        "--self-contained", "false", "-o", str(publish_dir),
    ]
    res = subprocess.run(cmd, cwd=source_tree, env=env, capture_output=True, text=True, check=False)
    if res.returncode != 0:
        raise RuntimeError(f"dotnet publish failed (exit {res.returncode}):\n{res.stderr or res.stdout}")


def validate_publish(publish_dir: Path) -> list[dict[str, Any]]:
    pub_inv = staging_tools.inventory(publish_dir)
    for entry in pub_inv:
        entry_path = entry["path"]
        if "avalonia" in Path(entry_path).name.lower() or "avalonia" in entry_path.lower():
            raise ValueError(f"Forbidden Avalonia artifact found in publish output: {entry_path}")

    headless_deps = publish_dir / "VPNRouter.Headless.deps.json"
    if not headless_deps.is_file() or headless_deps.is_symlink():
        raise FileNotFoundError(f"Missing required Headless deps.json: {headless_deps}")

    for deps_file in sorted(publish_dir.glob("*.deps.json")):
        if deps_file.is_file() and not deps_file.is_symlink():
            content = deps_file.read_text(encoding="utf-8")
            if "avalonia" in content.lower():
                raise ValueError(f"Forbidden Avalonia reference found in deps.json content: {deps_file.name}")

    return pub_inv


def find_file(root: Path, filename: str) -> Path:
    matches = sorted(
        p for p in root.rglob(filename)
        if p.is_file() and not p.is_symlink()
    )
    if not matches:
        raise FileNotFoundError(f"File '{filename}' not found under {root}")
    if len(matches) > 1:
        raise ValueError(f"Ambiguous file matches for '{filename}' under {root}: {matches}")
    return matches[0]


def get_elf_needed(elf_path: Path) -> list[str]:
    readelf_bin = shutil.which("readelf")
    if not readelf_bin:
        raise FileNotFoundError("readelf executable is mandatory and not found in PATH")
    res = subprocess.run([readelf_bin, "-d", str(elf_path)], capture_output=True, text=True, check=True)
    needed: list[str] = []
    for line in res.stdout.splitlines():
        if "(NEEDED)" in line and "[" in line and "]" in line:
            needed.append(line.split("[")[1].split("]")[0])
    return sorted(needed)


def gather_nuget_licenses(
    source_tree: Path, nuget_packages_dirs: list[Path], dest_licenses_dir: Path
) -> tuple[list[str], list[str]]:
    gathered: list[str] = []
    missing: list[str] = []
    target_packages = {"serilog": "Serilog", "yamldotnet": "YamlDotNet"}

    assets_file = source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
    if not assets_file.is_file():
        found_assets = list(source_tree.rglob("project.assets.json"))
        assets_file = found_assets[0] if found_assets else None

    if assets_file is None:
        return ([], sorted(target_packages.values()))

    try:
        with assets_file.open("r", encoding="utf-8") as fp:
            assets_data = json.load(fp)
    except (json.JSONDecodeError, OSError):
        return ([], sorted(target_packages.values()))

    raw_folders = assets_data.get("packageFolders", {})
    candidate_folders: list[Path] = []
    if isinstance(raw_folders, dict):
        candidate_folders.extend(Path(p) for p in raw_folders.keys())
    elif isinstance(raw_folders, list):
        candidate_folders.extend(Path(p) for p in raw_folders)
    for extra in nuget_packages_dirs:
        if extra not in candidate_folders:
            candidate_folders.append(extra)

    libraries = assets_data.get("libraries", {})
    resolved_libs: dict[str, str] = {}
    for lib_key, lib_val in libraries.items():
        if isinstance(lib_val, dict) and lib_val.get("type") == "package":
            pkg_lower = lib_key.split("/")[0].lower()
            if pkg_lower in target_packages and lib_val.get("path"):
                resolved_libs[pkg_lower] = lib_val["path"]

    for pkg_lower, out_prefix in target_packages.items():
        if pkg_lower not in resolved_libs:
            missing.append(f"{out_prefix}-LICENSE")
            continue
        exact_rel = resolved_libs[pkg_lower]
        found = False
        for folder in candidate_folders:
            pkg_dir = folder / exact_rel
            if pkg_dir.is_dir() and not pkg_dir.is_symlink():
                for item in sorted(pkg_dir.iterdir()):
                    if item.is_file() and not item.is_symlink() and item.name.lower().startswith("license"):
                        out_name = f"{out_prefix}-{item.name}"
                        dest = dest_licenses_dir / out_name
                        shutil.copy2(item, dest)
                        os.chmod(dest, 0o644)
                        gathered.append(out_name)
                        found = True
                        break
            if found:
                break
        if not found:
            missing.append(f"{out_prefix}-LICENSE")

    return (sorted(gathered), sorted(missing))


def copy_source_licenses(source_tree: Path, licenses_dest: Path) -> list[str]:
    missing: list[str] = []
    for src_file, out_name in [
        (source_tree / "LICENSE", "VPNRouter-LICENSE"),
        (source_tree / "NOTICE.md", "VPNRouter-NOTICE.md"),
    ]:
        if src_file.is_file() and not src_file.is_symlink():
            shutil.copy2(src_file, licenses_dest / out_name)
            os.chmod(licenses_dest / out_name, 0o644)
        else:
            missing.append(out_name)
    return missing


def create_payload_tar(stage_root: Path, output_tar: Path) -> str:
    with tarfile.open(output_tar, mode="w") as tf:
        for root, dirs, files in os.walk(stage_root, followlinks=False):
            dirs.sort()
            files.sort()
            rel_dir = Path(root).relative_to(stage_root)
            if rel_dir != Path("."):
                ti_dir = tarfile.TarInfo(rel_dir.as_posix())
                ti_dir.type = tarfile.DIRTYPE
                ti_dir.mode = 0o755
                ti_dir.mtime = ti_dir.uid = ti_dir.gid = 0
                ti_dir.uname = ti_dir.gname = ""
                tf.addfile(ti_dir)

            for f in files:
                f_path = Path(root) / f
                rel_f = f_path.relative_to(stage_root)
                st = f_path.lstat()
                ti_f = tarfile.TarInfo(rel_f.as_posix())
                ti_f.type = tarfile.REGTYPE
                ti_f.size = st.st_size
                ti_f.mtime = ti_f.uid = ti_f.gid = 0
                ti_f.uname = ti_f.gname = ""
                ti_f.mode = 0o755 if (st.st_mode & 0o111) else 0o644
                with f_path.open("rb") as fp:
                    tf.addfile(ti_f, fp)

    return compute_file_sha256(output_tar)


def render_pkgbuild(template_path: Path, payload_sha256: str) -> str:
    if not template_path.is_file() or template_path.is_symlink():
        raise ValueError(f"Template PKGBUILD is not a valid regular file: {template_path}")
    validated_sha = validate_sha256(payload_sha256, "payload_sha256")
    template_text = template_path.read_text(encoding="utf-8")
    placeholder = "@PAYLOAD_SHA256@"
    if placeholder not in template_text:
        raise ValueError(f"Template PKGBUILD missing required placeholder: {placeholder}")
    rendered = template_text.replace(placeholder, validated_sha)
    for line in rendered.splitlines():
        if line.startswith("sha256sums=") and "@" in line:
            raise ValueError(f"Unexpanded placeholder remaining in PKGBUILD: {line}")
    return rendered


def get_tool_versions(
    dotnet_bin: str,
    source_tree: Path | None = None,
    env: dict[str, str] | None = None,
    dotnet_version: str | None = DOTNET_REQUIRED_VERSION,
) -> dict[str, str]:
    def run_ver(cmd: list[str], cwd: Path | None = None, run_env: dict[str, str] | None = None, max_len: int = 128) -> str:
        try:
            res = subprocess.run(cmd, cwd=cwd, env=run_env, capture_output=True, text=True, check=True, timeout=10)
            return (res.stdout.strip().splitlines()[0] if res.stdout.strip() else "")[:max_len]
        except Exception as err:
            return f"unavailable: {err}"[:max_len]

    readelf_bin = shutil.which("readelf") or "readelf"
    bsdtar_bin = shutil.which("bsdtar") or "bsdtar"
    makepkg_bin = shutil.which("makepkg") or "makepkg"

    if dotnet_version is not None:
        dotnet_str = dotnet_version[:128]
    else:
        dotnet_str = run_ver([dotnet_bin, "--version"], cwd=source_tree, run_env=env)

    return {
        "python": sys.version.split()[0][:128],
        "dotnet": dotnet_str,
        "readelf": run_ver([readelf_bin, "--version"]),
        "bsdtar": run_ver([bsdtar_bin, "--version"]),
        "makepkg": run_ver([makepkg_bin, "--version"]),
    }


def compute_file_sha256(file_path: Path) -> str:
    if not file_path.is_file() or file_path.is_symlink():
        raise ValueError(f"Cannot compute SHA-256 of non-regular/symlink file: {file_path}")
    hasher = hashlib.sha256()
    bytes_read = 0
    with file_path.open("rb") as fp:
        while chunk := fp.read(CHUNK_SIZE):
            bytes_read += len(chunk)
            if bytes_read > MAX_CACHE_READ_SIZE:
                raise ValueError(f"File {file_path} exceeds maximum hash size")
            hasher.update(chunk)
    return hasher.hexdigest().lower()


def inspect_package(pkg_file: Path, work_dir: Path) -> None:
    bsdtar_bin = shutil.which("bsdtar")
    if not bsdtar_bin:
        raise FileNotFoundError("bsdtar executable is mandatory for package inspection")

    temp_uncompressed = work_dir / f".pkg_uncompressed.{os.getpid()}.tar"
    verify_pkg_dir = work_dir / f".pkg_verified.{os.getpid()}"

    try:
        conv_res = subprocess.run(
            [bsdtar_bin, "-cf", str(temp_uncompressed), f"@{pkg_file}"],
            capture_output=True,
            text=True,
            timeout=120,
            check=False,
        )
        if conv_res.returncode != 0:
            raise RuntimeError(f"bsdtar failed to uncompress package: {conv_res.stderr.strip()}")

        allowed_metadata = {".PKGINFO", ".BUILDINFO", ".MTREE"}
        with tarfile.open(temp_uncompressed, mode="r:*") as tf:
            for member in tf:
                # Validate original archive modes before safe_extract normalizes them.
                if member.isdir() and member.mode != 0o755:
                    raise ValueError(f"Invalid package directory mode: {member.name}")
                if member.isreg() and member.mode not in (0o644, 0o755):
                    raise ValueError(f"Invalid package file mode: {member.name}")
                if member.uid != 0 or member.gid != 0:
                    raise ValueError(f"Package member has non-root ownership: {member.name} ({member.uid}:{member.gid})")
                norm = member.name
                while norm.startswith("./"):
                    norm = norm[2:]
                norm = norm.rstrip("/")
                if not norm or norm == ".":
                    continue
                base_name = Path(norm).name
                if base_name.upper() == ".INSTALL" or norm.endswith(".install"):
                    raise ValueError(f"Package hooks (.INSTALL) are strictly forbidden: {norm}")
                if norm in allowed_metadata or norm in ("usr", "usr/lib", "usr/lib/vpnrouter-headless"):
                    continue
                if norm.startswith("usr/lib/vpnrouter-headless/"):
                    continue
                raise ValueError(f"Forbidden file in package outside allowed metadata and usr/lib/vpnrouter-headless: {norm}")

        temp_sha = compute_file_sha256(temp_uncompressed)
        staging_tools.safe_extract(temp_uncompressed, verify_pkg_dir, temp_sha)

        manifest_root = verify_pkg_dir / "usr" / "lib" / "vpnrouter-headless"
        if not (manifest_root / "manifest.json").is_file():
            raise FileNotFoundError("Package missing required manifest.json")
        staging_tools.verify_manifest(manifest_root)
        license_evidence.verify_license_evidence(manifest_root)
    finally:
        if temp_uncompressed.exists():
            try:
                temp_uncompressed.unlink()
            except OSError:
                pass
        if verify_pkg_dir.exists():
            shutil.rmtree(verify_pkg_dir, ignore_errors=True)


def build_makepkg(
    pkg_dir: Path,
    payload_tar: Path,
    payload_sha256: str,
    template_pkgbuild: Path,
    staging_tools_py: Path,
    work_dir: Path,
) -> tuple[Path, str]:
    shutil.copy2(payload_tar, pkg_dir / "payload.tar")
    shutil.copy2(staging_tools_py, pkg_dir / "staging_tools.py")
    rendered_pkgbuild = render_pkgbuild(template_pkgbuild, payload_sha256)
    (pkg_dir / "PKGBUILD").write_text(rendered_pkgbuild, encoding="utf-8")

    makepkg_bin = shutil.which("makepkg")
    if not makepkg_bin:
        raise FileNotFoundError("makepkg executable not found in PATH")

    makepkg_home = work_dir / ".makepkg_home"
    makepkg_home.mkdir(parents=True, mode=0o700, exist_ok=True)
    makepkg_tmp = work_dir / ".makepkg_tmp"
    makepkg_tmp.mkdir(parents=True, mode=0o700, exist_ok=True)

    env = os.environ.copy()
    for var in [
        "MAKEPKG_CONF",
        "BUILDDIR",
        "SRCPKGDEST",
        "PKGEXT",
        "PKGDEST",
        "SRCDEST",
        "LOGDEST",
        "PACKAGER",
        "PKGCACHE",
        "SRCEXT",
    ]:
        env.pop(var, None)

    env.update({
        "HOME": str(makepkg_home),
        "XDG_CONFIG_HOME": str(makepkg_home / ".config"),
        "XDG_CACHE_HOME": str(makepkg_home / ".cache"),
        "XDG_DATA_HOME": str(makepkg_home / ".local" / "share"),
        "TMPDIR": str(makepkg_tmp),
        "PKGDEST": str(pkg_dir),
        "SRCDEST": str(pkg_dir),
        "SRCPKGDEST": str(pkg_dir),
        "LOGDEST": str(pkg_dir),
    })

    cmd = [makepkg_bin, "--nodeps", "--noconfirm", "--config", "/etc/makepkg.conf"]
    res = subprocess.run(cmd, cwd=pkg_dir, env=env, capture_output=True, text=True, check=False)
    if res.returncode != 0:
        raise RuntimeError(f"makepkg failed (exit {res.returncode}):\n{res.stderr or res.stdout}")

    pkg_files = sorted(p for p in pkg_dir.glob("*.pkg.tar.*") if p.is_file() and not p.is_symlink())
    if len(pkg_files) != 1:
        raise ValueError(f"Expected exactly 1 package artifact in {pkg_dir}, found {len(pkg_files)}: {pkg_files}")
    pkg_file = pkg_files[0]

    pkg_sha256 = compute_file_sha256(pkg_file)
    sidecar = pkg_dir / f"{pkg_file.name}.sha256"
    sidecar.write_text(f"{pkg_sha256}  {pkg_file.name}\n", encoding="utf-8")

    inspect_package(pkg_file, work_dir)
    return pkg_file, pkg_sha256


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Build staging Arch Linux package for VPNRouter Headless daemon and runtime"
    )
    parser.add_argument("--repo", type=Path, required=True, help="Path to VPNRouter git repository")
    parser.add_argument("--work-dir", type=Path, required=True, help="New absolute private working directory")
    parser.add_argument(
        "--archive-cache",
        type=Path,
        default=None,
        help="Optional existing directory for caching downloaded runtime archives (read-only)",
    )
    args = parser.parse_args(argv)

    work_dir = validate_work_dir(args.work_dir)
    repo_dir = args.repo.resolve()
    verify_repo_commit(repo_dir, BACKEND_COMMIT)

    source_tar, source_sha256 = export_source(repo_dir, BACKEND_COMMIT, work_dir)
    source_tree = work_dir / "source_tree"
    staging_tools.safe_extract(source_tar, source_tree, source_sha256)

    runtime_tar = acquire_archive(RUNTIME_URL, RUNTIME_SHA256, "runtime.tar.gz", work_dir, args.archive_cache)
    extracted_runtime = work_dir / "extracted_runtime"
    staging_tools.safe_extract(runtime_tar, extracted_runtime, RUNTIME_SHA256)

    cronet_tar = acquire_archive(CRONET_URL, CRONET_SHA256, "cronet.tar.gz", work_dir, args.archive_cache)
    extracted_cronet = work_dir / "extracted_cronet"
    staging_tools.safe_extract(cronet_tar, extracted_cronet, CRONET_SHA256)

    publish_dir = work_dir / "publish"
    publish_headless(source_tree, publish_dir, work_dir)

    pub_inv = validate_publish(publish_dir)

    stage_root = work_dir / "stage"
    payload_dir = stage_root / "usr" / "lib" / "vpnrouter-headless"
    payload_dir.mkdir(parents=True, mode=0o755)

    for item in sorted(publish_dir.iterdir()):
        dest_item = payload_dir / item.name
        if item.is_dir():
            shutil.copytree(item, dest_item)
            for dp, _, fns in os.walk(dest_item):
                os.chmod(dp, 0o755)
                for fn in fns:
                    os.chmod(Path(dp) / fn, 0o644)
        else:
            shutil.copy2(item, dest_item)
            mode = 0o755 if (item.stat().st_mode & 0o111) or item.name == "VPNRouter.Headless" else 0o644
            os.chmod(dest_item, mode)

    runtime_dest_dir = payload_dir / "runtime"
    runtime_dest_dir.mkdir(parents=True, mode=0o755)
    for src_root, fname, dst_name in [
        (extracted_runtime, "sing-box", "sing-box"),
        (extracted_cronet, "libcronet.so", "libcronet.so"),
    ]:
        sf = find_file(src_root, fname)
        df = runtime_dest_dir / dst_name
        shutil.copy2(sf, df)
        os.chmod(df, 0o755)

    licenses_dest_dir = payload_dir / "licenses"
    licenses_dest_dir.mkdir(parents=True, mode=0o755)
    missing_licenses: list[str] = []
    missing_licenses.extend(copy_source_licenses(source_tree, licenses_dest_dir))

    for src_root, fname, out_name in [
        (extracted_runtime, "LICENSE", "sing-box-vpnctl-LICENSE"),
        (extracted_runtime, "README.md", "sing-box-vpnctl-README.md"),
        (extracted_cronet, "LICENSE", "cronet-sing-box-LICENSE"),
    ]:
        try:
            f = find_file(src_root, fname)
            shutil.copy2(f, licenses_dest_dir / out_name)
            os.chmod(licenses_dest_dir / out_name, 0o644)
        except FileNotFoundError:
            missing_licenses.append(out_name)

    dotnet_root = Path(resolve_dotnet()).resolve().parent
    catalog_dir = ARCH_DIR / "notices"
    evidence = license_evidence.collect_license_evidence(
        source_tree=source_tree,
        payload_root=payload_dir,
        dotnet_root=dotnet_root,
        catalog_dir=catalog_dir,
    )
    for comp in evidence.get("components", []):
        comp_id = comp.get("id", "unknown")
        for reason in comp.get("unresolved", []):
            missing_licenses.append(f"{comp_id}:{reason}")

    headless_elf = payload_dir / "VPNRouter.Headless"
    staging_tools.validate_elf_x86_64(headless_elf)
    staging_tools.validate_elf_x86_64(runtime_dest_dir / "sing-box")
    staging_tools.validate_elf_x86_64(runtime_dest_dir / "libcronet.so")

    needed_deps = {
        "VPNRouter.Headless": get_elf_needed(headless_elf),
        "sing-box": get_elf_needed(runtime_dest_dir / "sing-box"),
        "libcronet.so": get_elf_needed(runtime_dest_dir / "libcronet.so"),
    }

    template_pkgbuild = ARCH_DIR / "PKGBUILD"
    staging_tools_py = ARCH_DIR / "staging_tools.py"
    build_package_py = ARCH_DIR / "build_package.py"
    license_evidence_py = ARCH_DIR / "license_evidence.py"
    catalog_json = ARCH_DIR / "notices" / "catalog.json"

    metadata = {
        "source_commit": BACKEND_COMMIT,
        "source_archive_sha256": source_sha256,
        "packaging_files_sha256": {
            "PKGBUILD": compute_file_sha256(template_pkgbuild),
            "staging_tools.py": compute_file_sha256(staging_tools_py),
            "build_package.py": compute_file_sha256(build_package_py),
            "license_evidence.py": compute_file_sha256(license_evidence_py),
            "notices/catalog.json": compute_file_sha256(catalog_json),
        },
        "tool_versions": get_tool_versions(
            resolve_dotnet(),
            source_tree=source_tree,
            dotnet_version=DOTNET_REQUIRED_VERSION,
        ),
        "arch": "x86_64",
        "runtime_version": RUNTIME_VERSION,
        "cronet_version": CRONET_VERSION,
        "archive_urls": {"runtime": RUNTIME_URL, "cronet": CRONET_URL},
        "archive_hashes": {"runtime": RUNTIME_SHA256, "cronet": CRONET_SHA256},
        "dependencies": {
            "arch_packages": ["glibc", "gcc-libs", "dotnet-runtime>=10", "dotnet-runtime<11"],
            "elf_needed": needed_deps,
        },
        "license_inventory_complete": len(missing_licenses) == 0 and bool(evidence.get("complete", False)),
        "missing_licenses": sorted(set(missing_licenses)),
        "execution_authorized": False,
    }
    staging_tools.write_manifest(payload_dir, metadata)
    staging_tools.verify_manifest(payload_dir)
    license_evidence.verify_license_evidence(payload_dir)

    payload_tar = work_dir / "payload.tar"
    payload_sha256 = create_payload_tar(stage_root, payload_tar)

    pkg_dir = work_dir / "pkgbuild"
    pkg_dir.mkdir(parents=True, mode=0o700)
    pkg_file, pkg_sha256 = build_makepkg(
        pkg_dir, payload_tar, payload_sha256, template_pkgbuild, staging_tools_py, work_dir
    )

    print(f"Package: {pkg_file}")
    print(f"SHA256: {pkg_sha256}")
    print(f"Sidecar: {pkg_file}.sha256")
    return 0


if __name__ == "__main__":
    sys.exit(main())
