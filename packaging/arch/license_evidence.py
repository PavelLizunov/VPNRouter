"""Offline license and notice evidence collector and verifier for Arch packaging.

Pure Python standard library implementation with bounded resource limits, safe path
validation, XML DOCTYPE/ENTITY defense, exact metadata matching, and apphost verification.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
from typing import Any
import xml.etree.ElementTree as ET

MAX_JSON_SIZE: int = 2 * 1024 * 1024            # 2 MiB
MAX_XML_SIZE: int = 256 * 1024                  # 256 KiB
MAX_NOTICE_SIZE: int = 2 * 1024 * 1024          # 2 MiB
MAX_TOTAL_NOTICES_SIZE: int = 10 * 1024 * 1024  # 10 MiB
MAX_APPHOST_SIZE: int = 2 * 1024 * 1024         # 2 MiB
MAX_PAYLOAD_SIZE: int = 64 * 1024 * 1024        # 64 MiB
MAX_ENTRIES: int = 256

APPHOST_PLACEHOLDER: bytes = b"c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"
APPHOST_REGION_LEN: int = 1024
MANDATORY_IDS: frozenset[str] = frozenset({"microsoft.netcore.app.host.linux-x64", "sing-box-vpnctl", "cronet"})
CANONICAL_REQUIRED_IDS: frozenset[str] = frozenset({"serilog", "yamldotnet", "microsoft.netcore.app.host.linux-x64", "sing-box-vpnctl", "cronet"})
NATIVE_PAYLOAD_MAP: dict[str, str] = {"cronet": "runtime/libcronet.so", "sing-box-vpnctl": "runtime/sing-box"}


def compute_sha256(path: Path, max_size: int = MAX_PAYLOAD_SIZE) -> str:
    check_safe_path(path)
    st = path.lstat()
    if st.st_size > max_size:
        raise ValueError(f"File size exceeds limit ({max_size} bytes): {path}")
    h = hashlib.sha256()
    total_read = 0
    with path.open("rb") as fp:
        while chunk := fp.read(min(65536, max_size - total_read + 1)):
            total_read += len(chunk)
            if total_read > max_size:
                raise ValueError(f"File size exceeded limit ({max_size} bytes) during read: {path}")
            h.update(chunk)
    return h.hexdigest()


def check_safe_path(target: Path, root: Path | None = None) -> None:
    root_res = root.resolve() if root else None
    if target.exists() or os.path.islink(target):
        if os.path.islink(target):
            raise ValueError(f"Symlink rejected: {target}")
        st = target.lstat()
        if not (stat.S_ISREG(st.st_mode) or stat.S_ISDIR(st.st_mode)):
            raise ValueError(f"Non-regular file/dir rejected (FIFO/device): {target}")
        if stat.S_ISREG(st.st_mode) and st.st_nlink > 1:
            raise ValueError(f"Hardlink rejected: {target}")
        curr = target
    else:
        curr = target.parent

    while True:
        if os.path.islink(curr):
            raise ValueError(f"Symlink ancestor rejected: {curr}")
        if (root_res and curr.resolve() == root_res) or curr == curr.parent:
            break
        curr = curr.parent

    if root_res:
        try:
            target.resolve().relative_to(root_res)
        except ValueError as e:
            raise ValueError(f"Path escapes permitted root {root}: {target}") from e


def safe_resolve_relative(root: Path, rel_path: str | Path) -> Path:
    s = str(rel_path)
    if "\\" in s or not s.strip() or s.strip() == ".":
        raise ValueError(f"Invalid or empty/backslash path rejected: {rel_path}")
    raw_parts = s.replace("\\", "/").split("/")
    if "" in raw_parts or "." in raw_parts or ".." in raw_parts:
        raise ValueError(f"Unsafe empty/dot segment or double slash in relative path: {rel_path}")
    p = Path(rel_path)
    if p.is_absolute():
        raise ValueError(f"Unsafe absolute path: {rel_path}")
    check_safe_path(root)
    target = root / p
    try:
        target.resolve().relative_to(root.resolve())
    except ValueError as e:
        raise ValueError(f"Path escapes permitted root {root}: {rel_path}") from e
    check_safe_path(target, root)
    return target


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    res: dict[str, Any] = {}
    for k, v in pairs:
        if k in res:
            raise ValueError(f"Duplicate key in JSON: {k}")
        res[k] = v
    return res


def safe_read_json(file_path: Path) -> Any:
    check_safe_path(file_path)
    if file_path.lstat().st_size > MAX_JSON_SIZE:
        raise ValueError(f"JSON size exceeds {MAX_JSON_SIZE}: {file_path}")
    raw = file_path.read_bytes()
    if len(raw) > MAX_JSON_SIZE:
        raise ValueError(f"JSON size exceeds {MAX_JSON_SIZE}: {file_path}")
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=_reject_duplicate_keys)
    except (json.JSONDecodeError, UnicodeDecodeError) as e:
        raise ValueError(f"Malformed JSON in {file_path}: {e}") from e


def safe_read_xml(file_path: Path) -> ET.Element:
    check_safe_path(file_path)
    if file_path.lstat().st_size > MAX_XML_SIZE:
        raise ValueError(f"XML size exceeds {MAX_XML_SIZE}: {file_path}")
    raw = file_path.read_bytes()
    if len(raw) > MAX_XML_SIZE:
        raise ValueError(f"XML size exceeds {MAX_XML_SIZE}: {file_path}")
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        raise ValueError(f"UTF-16 encoding forbidden in XML: {file_path}")
    if b"\x00" in raw:
        raise ValueError(f"NUL byte forbidden in XML: {file_path}")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        raise ValueError(f"XML must be valid UTF-8: {file_path}: {e}") from e
    if re.search(r'encoding\s*=\s*["\'](?!utf-8["\'])', text, re.IGNORECASE):
        raise ValueError(f"Non-UTF-8 encoding in XML declaration forbidden: {file_path}")
    upper = text.upper()
    if "<!DOCTYPE" in upper or "<!ENTITY" in upper:
        raise ValueError(f"XML DOCTYPE or ENTITY declaration forbidden: {file_path}")
    try:
        return ET.fromstring(text)
    except ET.ParseError as e:
        raise ValueError(f"Malformed XML in {file_path}: {e}") from e


def _find_unique_elem(parent: ET.Element, tag: str, required: bool = False) -> ET.Element | None:
    matches = [c for c in parent if c.tag.split("}")[-1].lower() == tag.lower()]
    if len(matches) > 1:
        raise ValueError(f"Duplicate <{tag}> element in XML: {parent.tag}")
    if required and len(matches) == 0:
        raise ValueError(f"Missing required <{tag}> element in XML: {parent.tag}")
    return matches[0] if matches else None


def _get_text(elem: ET.Element | None) -> str:
    return elem.text.strip() if elem is not None and elem.text else ""


def parse_nuspec(nuspec_path: Path) -> dict[str, Any]:
    root = safe_read_xml(nuspec_path)
    meta = _find_unique_elem(root, "metadata", required=True)
    if meta is None:
        raise ValueError(f"Malformed nuspec missing <metadata>: {nuspec_path}")
    id_elem = _find_unique_elem(meta, "id", required=True)
    ver_elem = _find_unique_elem(meta, "version", required=True)
    pkg_id, pkg_ver = _get_text(id_elem), _get_text(ver_elem)
    if not pkg_id or not pkg_ver:
        raise ValueError(f"Malformed nuspec missing id or version: {nuspec_path}")
    repo = _find_unique_elem(meta, "repository", required=False)
    repo_url = repo.attrib.get("url", "").strip() if repo is not None else ""
    repo_commit = (repo.attrib.get("commit") or repo.attrib.get("revision") or "").strip() if repo is not None else ""
    lic_elem = _find_unique_elem(meta, "license", required=False)
    lic_type = lic_elem.attrib.get("type", "").strip() if lic_elem is not None else ""
    lic_val = _get_text(lic_elem)
    authors_elem = _find_unique_elem(meta, "authors", required=False)
    copyright_elem = _find_unique_elem(meta, "copyright", required=False)
    return {
        "id": pkg_id, "version": pkg_ver, "repository_url": repo_url, "revision": repo_commit,
        "license_type": lic_type, "license_value": lic_val,
        "authors": _get_text(authors_elem), "copyright": _get_text(copyright_elem),
    }


def verify_apphost_customization(template_bytes: bytes, published_bytes: bytes, app_dll_name: str) -> bool:
    if len(template_bytes) != len(published_bytes) or len(template_bytes) > MAX_APPHOST_SIZE:
        return False
    pos = template_bytes.find(APPHOST_PLACEHOLDER)
    app_bytes = app_dll_name.encode("utf-8")
    if pos < 0 or len(app_bytes) > APPHOST_REGION_LEN:
        return False
    end = pos + APPHOST_REGION_LEN
    if end > len(template_bytes) or published_bytes[:pos] != template_bytes[:pos] or published_bytes[end:] != template_bytes[end:]:
        return False
    return published_bytes[pos:end] == (app_bytes + b"\x00" * (APPHOST_REGION_LEN - len(app_bytes)))


class NoticeCopier:
    def __init__(self, payload_root: Path, licenses_dir: Path):
        self.payload_root, self.licenses_dir = payload_root, licenses_dir
        self.total_size = 0
        self.count = 0

    def copy_notice(self, src: Path, rel_out: str, expected_sha: str, source_url: str, role: str | None = None) -> dict[str, str]:
        if self.count >= MAX_ENTRIES:
            raise ValueError(f"Exceeded max notices entries ({MAX_ENTRIES})")
        check_safe_path(src)
        st = src.lstat()
        if st.st_size == 0:
            raise ValueError(f"Notice file is empty: {src}")
        if st.st_size > MAX_NOTICE_SIZE or self.total_size + st.st_size > MAX_TOTAL_NOTICES_SIZE:
            raise ValueError(f"Notice file exceeds size limits: {src}")
        actual_sha = compute_sha256(src, max_size=MAX_NOTICE_SIZE)
        if actual_sha != expected_sha:
            raise ValueError(f"Known catalog text hash mismatch for {src.name}: {actual_sha} != {expected_sha}")
        dst = safe_resolve_relative(self.licenses_dir, rel_out)
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        os.chmod(dst, 0o644)
        self.total_size += st.st_size
        self.count += 1
        entry: dict[str, str] = {
            "path": dst.relative_to(self.payload_root).as_posix(),
            "sha256": expected_sha,
            "source_url": source_url,
        }
        if role is not None:
            entry["role"] = role
        return entry


def load_catalog(catalog_dir: Path | None = None) -> tuple[Path, dict[str, dict[str, Any]]]:
    cat_dir = catalog_dir or (Path(__file__).resolve().parent / "notices")
    cat_base = cat_dir if cat_dir.is_dir() else cat_dir.parent
    cat_file = cat_dir if cat_dir.is_file() else (cat_dir / "catalog.json")

    if not (cat_file.is_file() and not cat_file.is_symlink()):
        raise ValueError(f"Trusted catalog file missing: {cat_file}")

    data = safe_read_json(cat_file)
    if data.get("schema_version") != 1 or not isinstance(data.get("components"), list) or len(data["components"]) == 0:
        raise ValueError(f"Invalid catalog schema or empty components list: {cat_file}")

    catalog_comps: dict[str, dict[str, Any]] = {}
    seen_files: set[str] = set()

    for c in data["components"]:
        cid = c.get("id")
        if not cid or not isinstance(cid, str):
            raise ValueError(f"Catalog component missing id: {c}")
        k = cid.lower()
        if k in catalog_comps:
            raise ValueError(f"Duplicate component ID in catalog: {cid}")
        catalog_comps[k] = c
        files = c.get("files", [])
        if not isinstance(files, list):
            raise ValueError(f"Catalog component {cid} files must be a list")
        for f in files:
            fp = f.get("path")
            if not fp or not isinstance(fp, str):
                raise ValueError(f"Catalog component {cid} has file with missing path: {f}")
            if fp in seen_files:
                raise ValueError(f"Duplicate notice file path in catalog: {fp}")
            seen_files.add(fp)
            f_abs = safe_resolve_relative(cat_base, fp)
            if not f_abs.is_file() or f_abs.is_symlink():
                raise ValueError(f"Catalog notice file missing or symlink: {fp}")
            st = f_abs.lstat()
            if st.st_size == 0:
                raise ValueError(f"Catalog notice file is empty: {fp}")
            if st.st_size > MAX_NOTICE_SIZE:
                raise ValueError(f"Catalog notice file exceeds limit ({MAX_NOTICE_SIZE} bytes): {fp}")

    missing_required = CANONICAL_REQUIRED_IDS - set(catalog_comps.keys())
    if missing_required:
        raise ValueError(f"Catalog missing canonical required components: {sorted(missing_required)}")

    return cat_base, catalog_comps


def _copy_catalog_notices(cat_c: dict[str, Any], cat_base: Path, copier: NoticeCopier) -> list[dict[str, str]]:
    return [
        copier.copy_notice(
            safe_resolve_relative(cat_base, f["path"]),
            f["path"],
            f["sha256"],
            f["source_url"],
            role=f.get("role"),
        )
        for f in cat_c.get("files", [])
    ]


def _find_deps_packages(payload_root: Path) -> tuple[dict[str, dict[str, Any]], str]:
    deps_file = payload_root / "VPNRouter.Headless.deps.json"
    if not (deps_file.is_file() and not deps_file.is_symlink()):
        raise ValueError(f"VPNRouter.Headless.deps.json missing or symlink: {deps_file}")
    deps_data = safe_read_json(deps_file)
    rt = deps_data.get("runtimeTarget")
    tname = rt.get("name") if isinstance(rt, dict) else (rt if isinstance(rt, str) else None)
    if not tname or not isinstance(tname, str) or not tname.endswith("/linux-x64"):
        raise ValueError(f"runtimeTarget.name must end with /linux-x64: {tname}")
    targets = deps_data.get("targets", {})
    if not isinstance(targets, dict) or tname not in targets:
        raise ValueError(f"runtimeTarget {tname} not found in targets of {deps_file}")
    tdict = targets[tname]
    if not isinstance(tdict, dict):
        raise ValueError(f"Target dictionary invalid in {deps_file}")
    libs = deps_data.get("libraries", {})
    pkgs = {
        k: {"target": v, "meta": libs.get(k, {})}
        for k, v in tdict.items()
        if isinstance(libs.get(k), dict)
        and libs[k].get("type") == "package"
        and (v.get("runtime") or v.get("native"))
    }
    return pkgs, tname


def _find_assets_data(source_tree: Path) -> tuple[Path, dict[str, Any], dict[str, Any]]:
    assets_file = source_tree / "VPNRouter.Headless" / "obj" / "project.assets.json"
    if not (assets_file.is_file() and not assets_file.is_symlink()):
        raise ValueError(f"project.assets.json missing or symlink: {assets_file}")
    check_safe_path(source_tree.parent)
    cache_dir = source_tree.parent / ".nuget_packages"
    data = safe_read_json(assets_file)
    libs = data.get("libraries", {})
    targets = data.get("targets", {})
    return cache_dir, libs, targets


def collect_license_evidence(source_tree: Path, payload_root: Path, dotnet_root: Path, catalog_dir: Path | None = None) -> dict[str, Any]:
    cat_base, catalog_comps = load_catalog(catalog_dir)

    licenses_dir = payload_root / "licenses"
    licenses_dir.mkdir(parents=True, exist_ok=True)
    copier = NoticeCopier(payload_root, licenses_dir)
    runtime_packages, _ = _find_deps_packages(payload_root)
    nuget_cache, assets_libs, assets_targets = _find_assets_data(source_tree)
    components: list[dict[str, Any]] = []

    target_rid = "net10.0/linux-x64"
    if target_rid not in assets_targets:
        raise ValueError(f"Target '{target_rid}' not found in project.assets.json targets")
    matched_target = assets_targets[target_rid]

    for lib_key, lib_info in runtime_packages.items():
        pkg_id, pkg_ver = lib_key.split("/", 1)
        payload_files, notices, unresolved = [], [], []
        alib = assets_libs.get(lib_key) or {}
        rel_pkg = alib.get("path") or lib_info["meta"].get("path") or f"{pkg_id.lower()}/{pkg_ver}"
        try:
            pkg_dir = safe_resolve_relative(nuget_cache, rel_pkg)
        except ValueError as e:
            raise ValueError(f"Package path for {lib_key} escapes cache or is unsafe: {e}") from e

        nuspec_meta: dict[str, Any] | None = None
        if pkg_dir.is_dir() and not pkg_dir.is_symlink():
            check_safe_path(pkg_dir)
            nuspecs = sorted(pkg_dir.glob("*.nuspec"))
            if len(nuspecs) == 1:
                nuspec_meta = parse_nuspec(nuspecs[0])
                if nuspec_meta["id"].lower() != pkg_id.lower() or nuspec_meta["version"] != pkg_ver:
                    unresolved.append(f"nuspec identity/version mismatch: {nuspec_meta['id']}@{nuspec_meta['version']} != {lib_key}")
            elif len(nuspecs) == 0:
                unresolved.append(f"nuspec not found in {pkg_dir}")
            else:
                unresolved.append(f"multiple nuspec files found in {pkg_dir}: {[p.name for p in nuspecs]}")
        else:
            unresolved.append(f"package directory not found for {lib_key}")

        target_entry = matched_target.get(lib_key, {})
        target_assets = {**target_entry.get("runtime", {}), **target_entry.get("native", {})}
        if not target_assets:
            raise ValueError(f"Runtime package {lib_key} missing assets in exact RID target")
        for asset_rel in target_assets:
            if pkg_dir and pkg_dir.is_dir():
                try:
                    nu_file = safe_resolve_relative(pkg_dir, asset_rel)
                except ValueError as e:
                    raise ValueError(f"Asset path {asset_rel} escapes package dir or is unsafe: {e}") from e
            else:
                nu_file = None

            fname = Path(asset_rel).name
            pay_file = safe_resolve_relative(payload_root, fname)
            if pay_file.is_file() and not pay_file.is_symlink():
                check_safe_path(pay_file)
                if pay_file.lstat().st_size == 0:
                    unresolved.append(f"payload file is empty: {fname}")
                p_sha = compute_sha256(pay_file)
                payload_files.append({"path": fname, "sha256": p_sha})
                if nu_file:
                    if nu_file.is_file() and not nu_file.is_symlink():
                        check_safe_path(nu_file)
                        nu_sha = compute_sha256(nu_file)
                        if p_sha != nu_sha:
                            unresolved.append(f"payload hash mismatch for {fname}: {p_sha} != {nu_sha}")
                    else:
                        unresolved.append(f"nuget asset file missing: {asset_rel}")
            else:
                unresolved.append(f"payload file missing: {fname}")

        cat_c = catalog_comps.get(pkg_id.lower())
        if not cat_c:
            unresolved.append(f"unknown package not in catalog: {pkg_id}")
            if nuspec_meta and nuspec_meta["license_type"] == "file" and pkg_dir and pkg_dir.is_dir():
                lic_file = safe_resolve_relative(pkg_dir, nuspec_meta["license_value"])
                if lic_file.is_file():
                    notices.append(copier.copy_notice(lic_file, f"{pkg_id}-{lic_file.name}", compute_sha256(lic_file, max_size=MAX_NOTICE_SIZE), f"nuspec:file/{pkg_id}/{lic_file.name}"))
                    unresolved.append("unreviewed file license from metadata copied as local evidence")
            components.append({
                "id": pkg_id, "version": pkg_ver, "payload_files": payload_files,
                "license_expression": (nuspec_meta["license_value"] if nuspec_meta else "UNKNOWN"),
                "source": {"repository_url": (nuspec_meta["repository_url"] if nuspec_meta else ""), "revision": (nuspec_meta["revision"] if nuspec_meta else "")},
                "authors": nuspec_meta.get("authors", "") if nuspec_meta else "",
                "copyright": nuspec_meta.get("copyright", "") if nuspec_meta else "",
                "notices": notices, "status": "unresolved", "unresolved": unresolved,
            })
            continue

        if cat_c.get("version") != pkg_ver:
            unresolved.append(f"unknown version: catalog {cat_c.get('version')} != deps {pkg_ver}")
            lic_expr = nuspec_meta.get("license_value", "") if nuspec_meta else ""
            repo_url = nuspec_meta.get("repository_url", "") if nuspec_meta else ""
            repo_rev = nuspec_meta.get("revision", "") if nuspec_meta else ""
            authors_val = nuspec_meta.get("authors", "") if nuspec_meta else ""
            copyright_val = nuspec_meta.get("copyright", "") if nuspec_meta else ""
        else:
            lic_expr = cat_c.get("license_expression", "")
            repo_url = cat_c.get("repository_url", "")
            repo_rev = cat_c.get("revision", "")
            authors_val = (nuspec_meta.get("authors") if nuspec_meta else cat_c.get("authors", "")) or ""
            copyright_val = (nuspec_meta.get("copyright") if nuspec_meta else cat_c.get("copyright", "")) or ""

            if not nuspec_meta:
                unresolved.append("missing nuspec metadata")
            else:
                if cat_c.get("revision"):
                    if not nuspec_meta.get("revision"):
                        unresolved.append(f"nuspec missing repository revision: expected {cat_c['revision']}")
                    elif cat_c["revision"] != nuspec_meta["revision"]:
                        unresolved.append(f"repository revision mismatch: {cat_c['revision']} != {nuspec_meta['revision']}")
                if cat_c.get("repository_url"):
                    if not nuspec_meta.get("repository_url"):
                        unresolved.append(f"nuspec missing repository url: expected {cat_c['repository_url']}")
                    elif cat_c["repository_url"] != nuspec_meta["repository_url"]:
                        unresolved.append(f"repository url mismatch: {cat_c['repository_url']} != {nuspec_meta['repository_url']}")

                lic_type = nuspec_meta.get("license_type")
                lic_value = nuspec_meta.get("license_value")
                if not lic_type or not lic_value:
                    unresolved.append("nuspec missing license element or value")
                elif lic_type not in ("expression", "file"):
                    unresolved.append(f"unsupported nuspec license type: {lic_type}")
                elif lic_type == "expression":
                    if lic_value != cat_c.get("license_expression"):
                        unresolved.append(f"license expression mismatch: {lic_value} != {cat_c.get('license_expression')}")
                elif lic_type == "file":
                    if pkg_dir and pkg_dir.is_dir():
                        lic_file = safe_resolve_relative(pkg_dir, lic_value)
                        if lic_file.is_file():
                            notices.append(copier.copy_notice(lic_file, f"{pkg_id}-{lic_file.name}", compute_sha256(lic_file, max_size=MAX_NOTICE_SIZE), f"nuspec:file/{pkg_id}/{lic_file.name}"))
                            unresolved.append("unreviewed file license from metadata copied as local evidence")

            notices.extend(_copy_catalog_notices(cat_c, cat_base, copier))

        if cat_c.get("coverage") != "verified_texts":
            unresolved.append(f"catalog coverage is {cat_c.get('coverage')}")
        if cat_c.get("unresolved"):
            unresolved.extend(cat_c["unresolved"])

        comp_dict = {
            "id": pkg_id, "version": pkg_ver, "payload_files": payload_files,
            "license_expression": lic_expr,
            "source": {"repository_url": repo_url, "revision": repo_rev},
            "authors": authors_val, "copyright": copyright_val,
            "notices": notices, "status": ("verified_texts" if not unresolved else "unresolved"), "unresolved": unresolved,
        }
        if "provenance" in cat_c and cat_c.get("version") == pkg_ver:
            comp_dict["provenance"] = cat_c["provenance"]
        components.append(comp_dict)

    # Apphost
    apphost_id = "Microsoft.NETCore.App.Host.linux-x64"
    cat_apphost = catalog_comps.get(apphost_id.lower())
    apphost_files, apphost_unres = [], []
    pub_apphost = payload_root / "VPNRouter.Headless"

    if pub_apphost.is_file() and not pub_apphost.is_symlink():
        check_safe_path(pub_apphost)
        if pub_apphost.lstat().st_size == 0:
            apphost_unres.append("published apphost executable is empty")
        elif pub_apphost.lstat().st_size > MAX_APPHOST_SIZE:
            apphost_unres.append(f"published apphost exceeds {MAX_APPHOST_SIZE} bytes")
        apphost_files.append({"path": "VPNRouter.Headless", "sha256": compute_sha256(pub_apphost, max_size=MAX_APPHOST_SIZE)})
        app_dll = "VPNRouter.Headless.dll"
        tpl_ver = cat_apphost.get("version", "") if cat_apphost else ""
        tpl_path = dotnet_root / "packs" / "Microsoft.NETCore.App.Host.linux-x64" / tpl_ver / "runtimes" / "linux-x64" / "native" / "apphost" if tpl_ver else None

        if tpl_path and tpl_path.is_file() and not tpl_path.is_symlink() and cat_apphost and cat_apphost.get("template_sha256"):
            check_safe_path(tpl_path)
            if tpl_path.lstat().st_size > MAX_APPHOST_SIZE:
                apphost_unres.append(f"apphost template exceeds {MAX_APPHOST_SIZE} bytes")
            else:
                tpl_sha = compute_sha256(tpl_path, max_size=MAX_APPHOST_SIZE)
                if tpl_sha != cat_apphost["template_sha256"]:
                    apphost_unres.append(f"apphost template hash mismatch: {tpl_sha} != {cat_apphost['template_sha256']}")
                elif not verify_apphost_customization(tpl_path.read_bytes(), pub_apphost.read_bytes(), app_dll):
                    apphost_unres.append("customized apphost byte comparison against template failed")
        else:
            apphost_unres.append(f"apphost template for version {tpl_ver} missing or catalog record missing")
    else:
        apphost_unres.append("published apphost executable VPNRouter.Headless missing from payload")

    apphost_notices = _copy_catalog_notices(cat_apphost, cat_base, copier) if cat_apphost else []
    if cat_apphost:
        if cat_apphost.get("coverage") != "verified_texts":
            apphost_unres.append(f"catalog coverage is {cat_apphost.get('coverage')}")
        if cat_apphost.get("unresolved"):
            apphost_unres.extend(cat_apphost["unresolved"])
    else:
        apphost_unres.append("apphost not in catalog")

    apphost_dict = {
        "id": apphost_id, "version": (cat_apphost.get("version", "") if cat_apphost else ""),
        "payload_files": apphost_files, "license_expression": (cat_apphost.get("license_expression", "MIT") if cat_apphost else "MIT"),
        "source": {"repository_url": (cat_apphost.get("repository_url", "") if cat_apphost else ""), "revision": (cat_apphost.get("revision", "") if cat_apphost else "")},
        "authors": cat_apphost.get("authors", "") if cat_apphost else "", "copyright": cat_apphost.get("copyright", "") if cat_apphost else "",
        "notices": apphost_notices, "status": ("verified_texts" if not apphost_unres else "unresolved"), "unresolved": apphost_unres,
    }
    if cat_apphost and "provenance" in cat_apphost:
        apphost_dict["provenance"] = cat_apphost["provenance"]
    components.append(apphost_dict)

    # Native components: iterate ONLY native mapped IDs
    for native_id, expected_rel in NATIVE_PAYLOAD_MAP.items():
        if native_id not in catalog_comps:
            raise ValueError(f"Native component {native_id} missing from catalog")
        cat_c = catalog_comps[native_id]
        c_id = cat_c.get("id", native_id)
        c_payloads, c_unres = [], list(cat_c.get("unresolved", []))
        if cat_c.get("coverage") == "partial" and not c_unres:
            c_unres.append("catalog coverage is partial")
        elif cat_c.get("coverage") != "verified_texts" and not c_unres:
            c_unres.append(f"catalog coverage is {cat_c.get('coverage')}")

        cand = payload_root / expected_rel
        if cand.is_file() and not cand.is_symlink():
            check_safe_path(cand)
            if cand.lstat().st_size == 0:
                c_unres.append(f"native payload file is empty: {expected_rel}")
            p_sha = compute_sha256(cand)
            c_payloads.append({"path": expected_rel, "sha256": p_sha})
            if cat_c.get("payload_sha256") and p_sha != cat_c["payload_sha256"]:
                c_unres.append(f"payload hash mismatch for {expected_rel}: {p_sha} != {cat_c['payload_sha256']}")
        else:
            c_unres.append(f"native payload file missing: {expected_rel}")

        c_dict = {
            "id": c_id, "version": cat_c.get("version", ""), "payload_files": c_payloads,
            "license_expression": cat_c.get("license_expression", ""),
            "source": {"repository_url": cat_c.get("repository_url", ""), "revision": cat_c.get("revision", "")},
            "authors": cat_c.get("authors", ""), "copyright": cat_c.get("copyright", ""),
            "notices": _copy_catalog_notices(cat_c, cat_base, copier),
            "status": ("verified_texts" if not c_unres else "unresolved"), "unresolved": c_unres,
        }
        if "provenance" in cat_c:
            c_dict["provenance"] = cat_c["provenance"]
        components.append(c_dict)

    complete = len(components) > 0 and all(c["status"] == "verified_texts" and len(c["unresolved"]) == 0 for c in components)
    result = {"schema_version": 1, "components": components, "complete": complete}
    out_manifest = licenses_dir / "components.json"
    out_manifest.write_text(json.dumps(result, indent=2), encoding="utf-8")
    os.chmod(out_manifest, 0o644)
    return result


def verify_license_evidence(payload_root: Path, catalog_dir: Path | None = None) -> None:
    comp_file = safe_resolve_relative(payload_root, "licenses/components.json")
    if not comp_file.is_file():
        raise ValueError(f"components.json not found: {comp_file}")
    data = safe_read_json(comp_file)
    if data.get("schema_version") != 1 or not isinstance(data.get("components"), list) or len(data["components"]) == 0 or not isinstance(data.get("complete"), bool):
        raise ValueError("Invalid components.json schema or empty components list")

    comps = data["components"]
    expected_complete = len(comps) > 0 and all(c.get("status") == "verified_texts" and len(c.get("unresolved", [])) == 0 for c in comps)
    if data["complete"] != expected_complete:
        raise ValueError(f"components.json complete flag ({data['complete']}) does not match computed completeness ({expected_complete})")

    cat_base, catalog_comps = load_catalog(catalog_dir)

    deps_pkgs, _ = _find_deps_packages(payload_root)
    runtime_deps_ids = {k.split("/", 1)[0].lower() for k in deps_pkgs.keys()}
    allowed_ids = set(MANDATORY_IDS) | runtime_deps_ids

    comp_ids: set[str] = set()
    comps_by_id: dict[str, dict[str, Any]] = {}
    tot_size, tot_count, seen_payload_files = 0, 0, set()

    for c in comps:
        cid = c.get("id")
        if not cid or not isinstance(cid, str):
            raise ValueError(f"Component missing valid id: {c}")
        k = cid.lower()
        if k in comp_ids:
            raise ValueError(f"Duplicate component id in components.json: {cid}")
        comp_ids.add(k)
        comps_by_id[k] = c

        status = c.get("status")
        if status not in ("verified_texts", "unresolved"):
            raise ValueError(f"Invalid status '{status}' for component {cid}")

        unresolved = c.get("unresolved")
        if not isinstance(unresolved, list):
            raise ValueError(f"unresolved field must be a list in component {cid}")
        if status == "unresolved" and len(unresolved) == 0:
            raise ValueError(f"Component {cid} has status 'unresolved' but empty unresolved list")
        if status == "verified_texts" and len(unresolved) > 0:
            raise ValueError(f"Component {cid} has status 'verified_texts' but non-empty unresolved list")

        pfiles = c.get("payload_files")
        notices = c.get("notices")
        if not isinstance(pfiles, list) or len(pfiles) == 0:
            raise ValueError(f"Component {cid} has empty payload_files")
        if not isinstance(notices, list):
            raise ValueError(f"Component {cid} notices must be a list")

        is_native = k in NATIVE_PAYLOAD_MAP
        is_apphost = k == "microsoft.netcore.app.host.linux-x64"
        cat_c = catalog_comps.get(k)
        is_known_same_version = (cat_c is not None and cat_c.get("version") == c.get("version"))

        if status == "verified_texts" or is_native or is_apphost or is_known_same_version:
            if len(notices) == 0:
                raise ValueError(f"Component {cid} has empty notices")

        for pf in pfiles:
            p_rel = pf.get("path")
            if not p_rel or p_rel in seen_payload_files:
                raise ValueError(f"Invalid or duplicate payload file in {cid}: {p_rel}")
            seen_payload_files.add(p_rel)
            pfile = safe_resolve_relative(payload_root, p_rel)
            if not pfile.is_file() or pfile.lstat().st_size == 0 or compute_sha256(pfile) != pf.get("sha256"):
                raise ValueError(f"Payload file missing, empty, or hash mismatch: {p_rel}")

        for nf in notices:
            n_rel = nf.get("path")
            if not n_rel:
                raise ValueError(f"Notice entry missing path in {cid}")
            nfile = safe_resolve_relative(payload_root, n_rel)
            if not nfile.is_file() or nfile.lstat().st_size == 0 or compute_sha256(nfile, max_size=MAX_NOTICE_SIZE) != nf.get("sha256"):
                raise ValueError(f"Notice file missing, empty, or hash mismatch: {n_rel}")
            fst = nfile.lstat()
            if fst.st_size > MAX_NOTICE_SIZE:
                raise ValueError(f"Notice file exceeds limit: {nfile}")
            tot_size += fst.st_size
            tot_count += 1
            if tot_size > MAX_TOTAL_NOTICES_SIZE or tot_count > MAX_ENTRIES:
                raise ValueError("Notices exceeded total size or count limit")

    extra_comps = comp_ids - allowed_ids
    if extra_comps:
        raise ValueError(f"Extra unknown component not in runtime deps or mandatory: {sorted(extra_comps)}")

    missing_mandatory = MANDATORY_IDS - comp_ids
    if missing_mandatory:
        raise ValueError(f"Mandatory component(s) missing from components.json: {sorted(missing_mandatory)}")

    apphost_c = comps_by_id.get("microsoft.netcore.app.host.linux-x64")
    if apphost_c:
        apphost_paths = [pf.get("path") for pf in apphost_c.get("payload_files", [])]
        if "VPNRouter.Headless" not in apphost_paths:
            raise ValueError(f"Apphost component missing exact payload path 'VPNRouter.Headless': {apphost_paths}")

    for nid, nrel in NATIVE_PAYLOAD_MAP.items():
        if nid in comps_by_id:
            native_paths = [pf.get("path") for pf in comps_by_id[nid].get("payload_files", [])]
            if nrel not in native_paths:
                raise ValueError(f"Native component {nid} missing exact payload path '{nrel}': {native_paths}")

    for k, lib_info in deps_pkgs.items():
        pname, pver = k.split("/", 1)
        if pname.lower() not in comp_ids:
            raise ValueError(f"External runtime library {pname} missing in components.json")
        comp_rec = comps_by_id[pname.lower()]
        if comp_rec.get("version") != pver:
            raise ValueError(f"Component version mismatch for {pname}: {comp_rec.get('version')} != {pver}")
        v = lib_info["target"]
        expected_paths = {Path(p).name for p in (*v.get("runtime", {}), *v.get("native", {}))}
        actual_paths = {pf["path"] for pf in comp_rec.get("payload_files", [])}
        if not expected_paths.issubset(actual_paths):
            raise ValueError(f"Component {pname} missing payload path bindings: {expected_paths - actual_paths}")

    manifest_file = payload_root / "manifest.json"
    if manifest_file.is_file() and not manifest_file.is_symlink():
        manifest_data = safe_read_json(manifest_file)
        if "license_inventory_complete" in manifest_data and manifest_data["license_inventory_complete"] != data["complete"]:
            raise ValueError(f"manifest.json license_inventory_complete != components.json complete")

    for cid_k, ev_c in comps_by_id.items():
        cat_c = catalog_comps.get(cid_k)
        if (cat_c is None or ev_c.get("version") != cat_c.get("version")) and ev_c.get("status") != "unresolved":
            raise ValueError(f"Unreviewed component/version cannot be verified_texts: {ev_c['id']}")

    for cid_k, cat_c in catalog_comps.items():
        if cid_k in comps_by_id:
            ev_c = comps_by_id[cid_k]
            if cat_c.get("coverage") == "partial" and (ev_c.get("status") == "verified_texts" or len(ev_c.get("unresolved", [])) == 0):
                raise ValueError(f"Component {cat_c['id']} has partial coverage in catalog but marked verified/resolved in evidence")
            if cat_c.get("coverage") != "verified_texts" and ev_c.get("status") == "verified_texts":
                raise ValueError(f"Component {cat_c['id']} has coverage '{cat_c.get('coverage')}' in catalog but marked verified_texts")

            if ev_c.get("version") == cat_c.get("version"):
                if ev_c.get("id").lower() != cat_c.get("id").lower():
                    raise ValueError(f"Component ID mismatch: {ev_c.get('id')} != {cat_c.get('id')}")
                if cat_c.get("revision") and ev_c.get("source", {}).get("revision") != cat_c.get("revision"):
                    raise ValueError(f"Source revision mismatch for {cat_c['id']}: {ev_c.get('source', {}).get('revision')} != {cat_c['revision']}")
                if cat_c.get("repository_url") and ev_c.get("source", {}).get("repository_url") != cat_c.get("repository_url"):
                    raise ValueError(f"Source repository_url mismatch for {cat_c['id']}: {ev_c.get('source', {}).get('repository_url')} != {cat_c['repository_url']}")
                if cat_c.get("license_expression") and ev_c.get("license_expression") != cat_c.get("license_expression"):
                    raise ValueError(f"License expression mismatch for {cat_c['id']}: {ev_c.get('license_expression')} != {cat_c['license_expression']}")

                expected_cat_notices = [
                    {
                        "path": f"licenses/{f['path']}",
                        "sha256": f["sha256"],
                        "source_url": f["source_url"],
                        **({"role": f["role"]} if "role" in f else {}),
                    }
                    for f in cat_c.get("files", [])
                ]
                ev_notices = ev_c.get("notices", [])
                if len(ev_notices) > len(expected_cat_notices):
                    prefix_len = len(ev_notices) - len(expected_cat_notices)
                    prefix_notices = ev_notices[:prefix_len]
                    tail_notices = ev_notices[prefix_len:]
                    if ev_c.get("status") != "unresolved":
                        raise ValueError(f"Component {cat_c['id']} has additional file license notice but status is not unresolved")
                    for pfn in prefix_notices:
                        if not pfn.get("source_url", "").startswith("nuspec:file/"):
                            raise ValueError(f"Unexpected additional notice in {cat_c['id']}: {pfn}")
                else:
                    tail_notices = ev_notices

                if len(tail_notices) != len(expected_cat_notices):
                    raise ValueError(f"Notice count mismatch for {cat_c['id']}: {len(tail_notices)} != {len(expected_cat_notices)} expected")

                tail_by_path = {n["path"]: n for n in tail_notices}
                expected_by_path = {n["path"]: n for n in expected_cat_notices}
                if tail_by_path.keys() != expected_by_path.keys():
                    raise ValueError(f"Notice paths mismatch for {cat_c['id']}: {set(tail_by_path.keys())} != {set(expected_by_path.keys())}")
                for np, exp_entry in expected_by_path.items():
                    actual_entry = tail_by_path[np]
                    if actual_entry.get("sha256") != exp_entry["sha256"]:
                        raise ValueError(f"Notice hash mismatch for {np}: {actual_entry.get('sha256')} != {exp_entry['sha256']}")
                    if actual_entry.get("source_url") != exp_entry["source_url"]:
                        raise ValueError(f"Notice source_url mismatch for {np}: {actual_entry.get('source_url')} != {exp_entry['source_url']}")
                    if "role" in exp_entry and actual_entry.get("role") != exp_entry["role"]:
                        raise ValueError(f"Notice role mismatch for {np}: {actual_entry.get('role')} != {exp_entry['role']}")

                if cat_c.get("payload_sha256"):
                    for pf in ev_c.get("payload_files", []):
                        if pf.get("sha256") != cat_c["payload_sha256"]:
                            raise ValueError(f"Payload hash in evidence does not match trusted catalog for {cat_c['id']}")
