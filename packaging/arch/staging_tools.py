"""Staging and package validation utilities for Arch Linux packages.

Pure Python standard library implementation for safe archive extraction,
ELF x86-64 validation, inventory generation, and manifest verification.
"""

from __future__ import annotations

import errno
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import sys
import tarfile
import tempfile
from typing import Any, BinaryIO
import zlib
import gzip

# Resource limit constants (monkeypatchable in unit tests)
MAX_COMPRESSED_SIZE: int = 512 * 1024 * 1024  # 512 MiB
MAX_TOTAL_UNCOMPRESSED_SIZE: int = 1 * 1024 * 1024 * 1024  # 1 GiB
MAX_FILE_SIZE: int = 256 * 1024 * 1024  # 256 MiB
MAX_ENTRIES: int = 20000
MAX_PATH_LEN: int = 1024
MAX_MANIFEST_SIZE: int = 8 * 1024 * 1024  # 8 MiB
CHUNK_SIZE: int = 64 * 1024  # 64 KiB
MAX_METADATA_SIZE: int = 64 * 1024  # 64 KiB

SAFE_PAX_KEYS: frozenset[str] = frozenset(
    {"path", "mtime", "atime", "ctime", "comment"}
)


def _get_ancestors(posix_path: str) -> list[str]:
    parts = posix_path.split("/")
    ancestors = []
    for i in range(1, len(parts)):
        ancestors.append("/".join(parts[:i]))
    return ancestors


def _check_no_symlink_ancestors(path: Path, label: str = "Path") -> None:
    curr = path
    while True:
        if os.path.islink(curr):
            raise ValueError(f"{label} or ancestor is a symlink: {curr}")
        if curr == curr.parent:
            break
        curr = curr.parent


def _check_no_xattrs(path: Path) -> None:
    if hasattr(os, "listxattr"):
        try:
            xattrs = os.listxattr(path, follow_symlinks=False)
            if xattrs:
                raise ValueError(f"Extended attributes rejected on {path}: {xattrs}")
        except OSError as e:
            if e.errno not in (
                getattr(errno, "ENOTSUP", 95),
                getattr(errno, "EOPNOTSUPP", 95),
            ):
                raise


def _check_dir_mode_and_xattrs(dir_path: Path, label: str = "Directory") -> None:
    dst = dir_path.lstat()
    if not stat.S_ISDIR(dst.st_mode):
        raise ValueError(f"{label} is not a directory: {dir_path}")
    if dst.st_mode & (stat.S_ISUID | stat.S_ISGID | stat.S_ISVTX):
        raise ValueError(f"{label} has unsafe mode (suid/sgid/sticky): {dir_path}")
    if dst.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
        raise ValueError(f"{label} has unsafe mode (group/world writable): {dir_path}")
    _check_no_xattrs(dir_path)


def _parse_raw_tar_size(size_bytes: bytes) -> int:
    if not size_bytes:
        return 0
    # Binary encoding (base-256): top bit of first byte is set
    if size_bytes[0] & 0x80:
        if size_bytes[0] == 0x80:
            return int.from_bytes(
                bytes([size_bytes[0] & 0x7F]) + size_bytes[1:], byteorder="big"
            )
        elif size_bytes[0] == 0xFF:
            return int.from_bytes(size_bytes, byteorder="big", signed=True)
        else:
            raise ValueError("Invalid binary tar size encoding")
    # Octal encoding
    s = size_bytes.rstrip(b"\x00 ").strip(b"\x00 ")
    if not s:
        return 0
    try:
        val = int(s, 8)
    except ValueError as e:
        raise ValueError(f"Invalid octal tar size {size_bytes!r}: {e}") from e
    return val


def _inspect_raw_tar_headers(f: BinaryIO, total_size: int) -> None:
    f.seek(0)
    raw_entries = 0
    while True:
        hdr_pos = f.tell()
        block = f.read(512)
        if not block:
            break
        if len(block) < 512:
            raise ValueError(f"Truncated tar header at offset {hdr_pos}")
        if block == b"\x00" * 512:
            # End of archive block or padding
            continue

        raw_entries += 1
        if raw_entries > MAX_ENTRIES:
            raise ValueError(f"Archive exceeds maximum entries limit: {MAX_ENTRIES}")

        typeflag = block[156:157]
        size_bytes = block[124:136]
        size = _parse_raw_tar_size(size_bytes)
        if size < 0:
            raise ValueError(f"Negative size {size} in tar header at offset {hdr_pos}")

        name_bytes = block[:100].rstrip(b"\x00")
        if (name_bytes == b"." or name_bytes.startswith(b"./")) and typeflag in (
            b"0",
            b"\x00",
            b"7",
        ):
            if name_bytes in (b".", b"./"):
                raise ValueError("Root entry '.' cannot be a regular file")

        # Metadata size limits: PAX g/x, GNU L/K <= 64KiB (MAX_METADATA_SIZE)
        if typeflag in (b"x", b"g", b"L", b"K", b"X", b"G"):
            if size > MAX_METADATA_SIZE:
                raise ValueError(
                    f"Tar metadata header {typeflag!r} size {size} exceeds maximum {MAX_METADATA_SIZE} at offset {hdr_pos}"
                )
        elif typeflag == b"5":  # Directory
            if size > 0:
                raise ValueError(
                    f"Directory entry has size > 0 ({size}) at offset {hdr_pos}"
                )
        elif typeflag in (b"0", b"\x00", b"7"):  # Regular file
            if size > MAX_FILE_SIZE:
                raise ValueError(
                    f"File size {size} exceeds maximum {MAX_FILE_SIZE} at offset {hdr_pos}"
                )

        data_blocks_size = ((size + 511) // 512) * 512
        if hdr_pos + 512 + data_blocks_size > total_size:
            raise ValueError(
                f"Tar entry data extends past end of archive at offset {hdr_pos}"
            )
        f.seek(data_blocks_size, os.SEEK_CUR)


def safe_extract(archive: Path, dest: Path, expected_sha256: str) -> None:
    """Safely extract a tar/tar.gz archive after complete pre-validation.

    Performs SHA-256 integrity verification first, then stream-decompresses to a
    bounded private temporary file, inspects raw 512-byte tar headers for metadata limits,
    validates all entries against path traversal, links, special devices, unsafe modes,
    sparse flags, and size bounds before any writes occur to the destination directory.
    """
    archive = Path(archive)
    dest = Path(dest)

    # 1. Verify archive exists and is not a symlink
    if os.path.islink(archive):
        raise ValueError(f"Archive cannot be a symlink: {archive}")
    if not archive.is_file():
        raise FileNotFoundError(f"Archive file not found: {archive}")

    compressed_size = archive.stat().st_size
    if compressed_size > MAX_COMPRESSED_SIZE:
        raise ValueError(
            f"Compressed archive size {compressed_size} exceeds maximum {MAX_COMPRESSED_SIZE}"
        )

    # SHA-256 verification first
    hasher = hashlib.sha256()
    with archive.open("rb") as f:
        bytes_read = 0
        while True:
            chunk = f.read(CHUNK_SIZE)
            if not chunk:
                break
            bytes_read += len(chunk)
            if bytes_read > MAX_COMPRESSED_SIZE:
                raise ValueError(
                    f"Compressed archive stream exceeds maximum {MAX_COMPRESSED_SIZE}"
                )
            hasher.update(chunk)
    actual_sha256 = hasher.hexdigest().lower()
    if actual_sha256 != expected_sha256.strip().lower():
        raise ValueError(
            f"SHA-256 mismatch: expected {expected_sha256}, got {actual_sha256}"
        )

    # 2. Destination checks: must not exist, no existing symlink ancestors
    if dest.exists() or dest.is_symlink():
        raise FileExistsError(f"Destination already exists: {dest}")

    curr = dest.parent
    while True:
        if os.path.islink(curr):
            raise ValueError(f"Destination ancestor is a symlink: {curr}")
        if curr == curr.parent:
            break
        curr = curr.parent

    # 3. Stream decompress gzip (or raw tar; reject unsupported bz2/xz) to private tempfile
    tmp_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            prefix="staging_tar_", suffix=".tar", delete=False
        ) as tmp_tar:
            tmp_path = Path(tmp_tar.name)
            os.chmod(tmp_path, 0o600)

            with archive.open("rb") as f_magic:
                magic = f_magic.read(6)

            if magic.startswith(b"\x1f\x8b"):
                # Gzip stream
                total_uncompressed = 0
                try:
                    with gzip.GzipFile(archive, mode="rb") as gz_in:
                        while True:
                            chunk = gz_in.read(CHUNK_SIZE)
                            if not chunk:
                                break
                            total_uncompressed += len(chunk)
                            if total_uncompressed > MAX_TOTAL_UNCOMPRESSED_SIZE:
                                raise ValueError(
                                    f"Decompressed archive size exceeds maximum {MAX_TOTAL_UNCOMPRESSED_SIZE}"
                                )
                            tmp_tar.write(chunk)
                except (gzip.BadGzipFile, zlib.error, EOFError) as e:
                    raise ValueError(f"Gzip decompression failed: {e}") from e
            elif magic.startswith(b"BZh"):
                raise ValueError("Unsupported compression: bz2 is not supported")
            elif magic.startswith(b"\xfd7zXZ\x00"):
                raise ValueError("Unsupported compression: xz is not supported")
            else:
                # Raw tar
                total_uncompressed = 0
                with archive.open("rb") as raw_in:
                    while True:
                        chunk = raw_in.read(CHUNK_SIZE)
                        if not chunk:
                            break
                        total_uncompressed += len(chunk)
                        if total_uncompressed > MAX_TOTAL_UNCOMPRESSED_SIZE:
                            raise ValueError(
                                f"Archive size exceeds maximum {MAX_TOTAL_UNCOMPRESSED_SIZE}"
                            )
                        tmp_tar.write(chunk)

            if total_uncompressed == 0:
                raise ValueError("Archive is empty")
            if total_uncompressed % 512 != 0:
                raise ValueError(
                    f"Archive size {total_uncompressed} is not a multiple of 512 bytes"
                )

            tmp_tar.flush()

        # 4. Inspect RAW 512-byte tar headers before tarfile parser allocation
        with tmp_path.open("rb") as raw_f:
            _inspect_raw_tar_headers(raw_f, total_uncompressed)

        # 5. Whole archive validation pass on bounded private temp
        entry_count = 0
        total_uncompressed_files = 0
        seen_paths: set[str] = set()
        seen_files: set[str] = set()
        seen_dirs: set[str] = set()
        seen_dir_ancestors: set[str] = set()
        validated_entries: list[tuple[tarfile.TarInfo, str]] = []

        try:
            with tarfile.open(tmp_path, mode="r:") as tar:
                # Check global pax headers if present
                if getattr(tar, "pax_headers", None):
                    for k in tar.pax_headers:
                        if k not in SAFE_PAX_KEYS:
                            if "xattr" in k.lower() or k.startswith(
                                (
                                    "SCHILY.xattr",
                                    "LIBARCHIVE.xattr",
                                    "security.",
                                    "system.",
                                    "trusted.",
                                    "user.",
                                )
                            ):
                                raise ValueError(f"PAX extended attribute rejected: {k}")
                            if "sparse" in k.lower() or k.startswith(
                                ("GNU.sparse", "SCHILY.realsize")
                            ):
                                raise ValueError(
                                    f"PAX sparse extended attribute rejected: {k}"
                                )
                            raise ValueError(f"PAX header key rejected: {k}")

                for member in tar:
                    entry_count += 1
                    if entry_count > MAX_ENTRIES:
                        raise ValueError(
                            f"Archive exceeds maximum entries limit: {MAX_ENTRIES}"
                        )

                    raw_name = member.name
                    if len(raw_name) > MAX_PATH_LEN:
                        raise ValueError(
                            f"Path length {len(raw_name)} exceeds maximum {MAX_PATH_LEN}"
                        )
                    if "\\" in raw_name:
                        raise ValueError(f"Path contains backslash: {raw_name!r}")
                    if raw_name.startswith("/") or os.path.isabs(raw_name):
                        raise ValueError(f"Absolute path rejected: {raw_name!r}")

                    norm = raw_name
                    while norm.startswith("./"):
                        norm = norm[2:]
                    norm = norm.rstrip("/")

                    if not norm or norm == ".":
                        norm_path = "."
                    else:
                        parts = norm.split("/")
                        if any(part in ("..", ".", "") for part in parts):
                            if any(part == ".." for part in parts):
                                raise ValueError(
                                    f"Path traversal '..' rejected: {raw_name!r}"
                                )
                            raise ValueError(
                                f"Invalid path segment rejected: {raw_name!r}"
                            )
                        norm_path = "/".join(parts)

                    if len(norm_path) > MAX_PATH_LEN:
                        raise ValueError(
                            f"Normalized path length exceeds maximum {MAX_PATH_LEN}"
                        )

                    if norm_path in seen_paths:
                        raise ValueError(f"Duplicate normalized path: {norm_path}")
                    seen_paths.add(norm_path)

                    if member.issym() or member.islnk() or member.type in (
                        tarfile.SYMTYPE,
                        tarfile.LNKTYPE,
                    ):
                        raise ValueError(f"Link rejected: {raw_name}")
                    if member.isfifo() or member.isdev() or member.type in (
                        tarfile.FIFOTYPE,
                        tarfile.CHRTYPE,
                        tarfile.BLKTYPE,
                    ):
                        raise ValueError(f"Special file/device/FIFO rejected: {raw_name}")
                    if (
                        getattr(member, "issparse", lambda: False)()
                        or getattr(member, "sparse", None) is not None
                        or member.type in (getattr(tarfile, "GNUTYPE_SPARSE", b"S"), b"S")
                    ):
                        raise ValueError(f"Sparse file rejected: {raw_name}")
                    if member.pax_headers:
                        for k in member.pax_headers:
                            if k not in SAFE_PAX_KEYS:
                                if "xattr" in k.lower() or k.startswith(
                                    (
                                        "SCHILY.xattr",
                                        "LIBARCHIVE.xattr",
                                        "security.",
                                        "system.",
                                        "trusted.",
                                        "user.",
                                    )
                                ):
                                    raise ValueError(
                                        f"PAX extended attribute rejected: {k}"
                                    )
                                if "sparse" in k.lower() or k.startswith(
                                    ("GNU.sparse", "SCHILY.realsize")
                                ):
                                    raise ValueError(
                                        f"PAX sparse extended attribute rejected: {k}"
                                    )
                                raise ValueError(f"PAX header key rejected: {k}")
                    if member.mode & (0o4000 | 0o2000 | 0o1000):
                        raise ValueError(
                            f"Unsafe mode (suid/sgid/sticky) rejected on {raw_name}: {oct(member.mode)}"
                        )

                    if norm_path == ".":
                        if not member.isdir():
                            raise ValueError("Root entry '.' cannot be a regular file")

                    if member.isdir():
                        if member.size > 0:
                            raise ValueError(
                                f"Directory {norm_path} has size > 0: {member.size}"
                            )
                        if norm_path != ".":
                            for anc in _get_ancestors(norm_path):
                                if anc in seen_files:
                                    raise ValueError(
                                        f"Prefix file conflict: {anc} is a file, cannot be parent of {norm_path}"
                                    )
                                seen_dir_ancestors.add(anc)
                            seen_dirs.add(norm_path)
                        else:
                            seen_dirs.add(".")
                    elif member.isreg():
                        if norm_path == ".":
                            raise ValueError("Root entry '.' cannot be a regular file")
                        for anc in _get_ancestors(norm_path):
                            if anc in seen_files:
                                raise ValueError(
                                    f"Prefix file conflict: {anc} is a file, cannot be parent of {norm_path}"
                                )
                            seen_dir_ancestors.add(anc)
                        if norm_path in seen_dirs or norm_path in seen_dir_ancestors:
                            raise ValueError(
                                f"Prefix file conflict: {norm_path} is already a directory or parent of existing entry"
                            )
                        if member.size < 0:
                            raise ValueError(f"Negative file size for {norm_path}")
                        if member.size > MAX_FILE_SIZE:
                            raise ValueError(
                                f"File size {member.size} exceeds maximum {MAX_FILE_SIZE} for {norm_path}"
                            )
                        total_uncompressed_files += member.size
                        if total_uncompressed_files > MAX_TOTAL_UNCOMPRESSED_SIZE:
                            raise ValueError(
                                f"Total uncompressed size exceeds maximum {MAX_TOTAL_UNCOMPRESSED_SIZE}"
                            )
                        seen_files.add(norm_path)
                    else:
                        raise ValueError(
                            f"Unsupported member type {member.type!r} for {raw_name}"
                        )

                    validated_entries.append((member, norm_path))
        except (tarfile.TarError, EOFError, OSError) as e:
            raise ValueError(f"Archive validation failed: {e}") from e

        # 6. Materialization pass: create new destination only after full validation passed
        dest.mkdir(parents=True, exist_ok=False)
        os.chmod(dest, 0o755)

        try:
            with tarfile.open(tmp_path, mode="r:") as tar:
                for (val_info, norm_path), member in zip(validated_entries, tar):
                    if norm_path == ".":
                        continue
                    target = dest / norm_path
                    if member.isdir():
                        target.mkdir(mode=0o755, parents=True, exist_ok=True)
                        os.chmod(target, 0o755)
                    elif member.isreg():
                        target.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
                        parent = target.parent
                        while parent != dest and parent != parent.parent:
                            os.chmod(parent, 0o755)
                            parent = parent.parent

                        target_mode = 0o755 if (member.mode & 0o111) else 0o644
                        f_in = tar.extractfile(member)
                        if f_in is None:
                            raise ValueError(f"Failed to extract member: {member.name}")
                        fd = os.open(
                            target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, target_mode
                        )
                        f_out = None
                        try:
                            f_out = os.fdopen(fd, "wb")
                            written = 0
                            while True:
                                chunk = f_in.read(CHUNK_SIZE)
                                if not chunk:
                                    break
                                written += len(chunk)
                                if written > MAX_FILE_SIZE:
                                    raise ValueError(
                                        f"File exceeded max size during write: {norm_path}"
                                    )
                                f_out.write(chunk)
                            if written != member.size:
                                raise ValueError(
                                    f"Size mismatch during write for {norm_path}: expected {member.size}, got {written}"
                                )
                        finally:
                            f_in.close()
                            if f_out is not None:
                                f_out.close()
                            else:
                                os.close(fd)
                        os.chmod(target, target_mode)
        except Exception:
            shutil.rmtree(dest, ignore_errors=True)
            raise
    finally:
        if tmp_path is not None and tmp_path.exists():
            try:
                tmp_path.unlink()
            except OSError:
                pass


def validate_elf_x86_64(path: Path) -> None:
    """Validate 64-bit little-endian x86-64 ELF binary header without executing."""
    path = Path(path)
    if os.path.islink(path):
        raise ValueError(f"ELF binary cannot be a symlink: {path}")
    if not path.is_file():
        raise ValueError(f"ELF binary does not exist or is not a regular file: {path}")

    st = path.lstat()
    if st.st_size < 64:
        raise ValueError(
            f"File size {st.st_size} too small for 64-byte ELF header: {path}"
        )

    with path.open("rb") as fp:
        header = fp.read(64)
    if len(header) < 64:
        raise ValueError(f"Could not read complete 64-byte ELF header from {path}")

    if header[:4] != b"\x7fELF":
        raise ValueError(f"Not an ELF binary (invalid magic): {path}")
    if header[4] != 2:
        raise ValueError(
            f"Not a 64-bit ELF (ELFCLASS64=2 expected, got {header[4]}): {path}"
        )
    if header[5] != 1:
        raise ValueError(
            f"Not a little-endian ELF (ELFDATA2LSB=1 expected, got {header[5]}): {path}"
        )
    if header[6] != 1:
        raise ValueError(
            f"Invalid ELF ident version (expected 1, got {header[6]}): {path}"
        )

    e_type = int.from_bytes(header[16:18], byteorder="little")
    if e_type not in (2, 3):
        raise ValueError(
            f"Invalid ELF type {e_type} (expected ET_EXEC=2 or ET_DYN=3): {path}"
        )

    e_machine = int.from_bytes(header[18:20], byteorder="little")
    if e_machine != 62:
        raise ValueError(
            f"Invalid ELF machine {e_machine} (expected EM_X86_64=62): {path}"
        )

    e_version = int.from_bytes(header[20:24], byteorder="little")
    if e_version != 1:
        raise ValueError(f"Invalid ELF e_version {e_version} (expected 1): {path}")


def inventory(root: Path) -> list[dict]:
    """Generate deterministic file inventory with path, size, sha256, and mode."""
    root = Path(root)
    _check_no_symlink_ancestors(root, label="Root")
    if not root.is_dir():
        raise ValueError(f"Root does not exist or is not a directory: {root}")

    _check_dir_mode_and_xattrs(root, label="Root directory")

    items: list[dict] = []
    total_entries = 0
    total_size = 0

    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dp = Path(dirpath)
        if os.path.islink(dp):
            raise ValueError(f"Symlink directory rejected: {dp}")
        dst = dp.lstat()
        if not stat.S_ISDIR(dst.st_mode):
            raise ValueError(f"Expected directory: {dp}")
        if dst.st_mode & (stat.S_ISUID | stat.S_ISGID | stat.S_ISVTX):
            raise ValueError(f"Directory has unsafe mode (suid/sgid/sticky): {dp}")
        if dst.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise ValueError(f"Directory has unsafe mode (group/world writable): {dp}")
        _check_no_xattrs(dp)

        for d in dirnames:
            d_full = dp / d
            if os.path.islink(d_full):
                raise ValueError(f"Symlink directory rejected: {d_full}")

        for f in filenames:
            f_full = dp / f
            if os.path.islink(f_full):
                raise ValueError(f"Symlink file rejected: {f_full}")
            fst = f_full.lstat()
            if not stat.S_ISREG(fst.st_mode):
                raise ValueError(f"Non-regular file rejected: {f_full}")
            if getattr(fst, "st_nlink", 1) > 1:
                raise ValueError(
                    f"Hardlinked file rejected (st_nlink={fst.st_nlink}): {f_full}"
                )
            if fst.st_mode & (stat.S_ISUID | stat.S_ISGID | stat.S_ISVTX):
                raise ValueError(f"File has unsafe mode (suid/sgid/sticky): {f_full}")
            if fst.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
                raise ValueError(f"File has unsafe mode (group/world writable): {f_full}")
            _check_no_xattrs(f_full)
            if fst.st_size > MAX_FILE_SIZE:
                raise ValueError(f"File exceeds maximum size {MAX_FILE_SIZE}: {f_full}")

            total_entries += 1
            if total_entries > MAX_ENTRIES:
                raise ValueError(
                    f"Inventory exceeds maximum entries limit: {MAX_ENTRIES}"
                )
            total_size += fst.st_size
            if total_size > MAX_TOTAL_UNCOMPRESSED_SIZE:
                raise ValueError(
                    f"Total uncompressed size exceeds maximum {MAX_TOTAL_UNCOMPRESSED_SIZE}"
                )

            rel_path = f_full.relative_to(root).as_posix()
            if len(rel_path) > MAX_PATH_LEN or "\\" in rel_path:
                raise ValueError(f"Invalid path in inventory: {rel_path}")

            hasher = hashlib.sha256()
            with f_full.open("rb") as fp:
                bytes_read = 0
                while True:
                    chunk = fp.read(CHUNK_SIZE)
                    if not chunk:
                        break
                    bytes_read += len(chunk)
                    if bytes_read > MAX_FILE_SIZE:
                        raise ValueError(
                            f"File exceeded max size during hashing: {f_full}"
                        )
                    hasher.update(chunk)

            mode_str = f"{stat.S_IMODE(fst.st_mode):04o}"
            items.append(
                {
                    "path": rel_path,
                    "size": fst.st_size,
                    "sha256": hasher.hexdigest(),
                    "mode": mode_str,
                }
            )

    items.sort(key=lambda x: x["path"])
    return items


def write_manifest(root: Path, metadata: dict) -> None:
    """Write root/manifest.json with schema_version 1 and files inventory excluding manifest."""
    root = Path(root)
    _check_no_symlink_ancestors(root, label="Root")
    if not root.is_dir():
        raise ValueError(f"Root must be an existing non-symlink directory: {root}")
    _check_dir_mode_and_xattrs(root, label="Root directory")

    inv = inventory(root)
    files = [item for item in inv if item["path"] != "manifest.json"]

    data = dict(metadata)
    data["schema_version"] = 1
    data["files"] = files

    encoded = (json.dumps(data, indent=2, sort_keys=True) + "\n").encode("utf-8")
    if len(encoded) > MAX_MANIFEST_SIZE:
        raise ValueError(f"Manifest size {len(encoded)} exceeds limit {MAX_MANIFEST_SIZE}")

    manifest_path = root / "manifest.json"
    if os.path.islink(manifest_path):
        raise ValueError(f"manifest.json cannot be a symlink: {manifest_path}")

    tmp_path = root / f".manifest.json.tmp.{os.getpid()}"
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    fd = os.open(tmp_path, flags, 0o644)
    try:
        with os.fdopen(fd, "wb") as fp:
            fp.write(encoded)
        os.replace(tmp_path, manifest_path)
    finally:
        if os.path.islink(tmp_path) or tmp_path.exists():
            try:
                tmp_path.unlink()
            except OSError:
                pass


def verify_manifest(root: Path) -> None:
    """Validate existing manifest.json schema, no duplicate paths, and exact inventory match."""
    root = Path(root)
    _check_no_symlink_ancestors(root, label="Root")
    if not root.is_dir():
        raise ValueError(f"Root must be an existing non-symlink directory: {root}")
    _check_dir_mode_and_xattrs(root, label="Root directory")

    manifest_path = root / "manifest.json"
    if os.path.islink(manifest_path):
        raise ValueError(f"manifest.json cannot be a symlink: {manifest_path}")
    if not manifest_path.is_file():
        raise FileNotFoundError(f"manifest.json not found: {manifest_path}")

    st = manifest_path.lstat()
    if st.st_size > MAX_MANIFEST_SIZE:
        raise ValueError(f"manifest.json exceeds maximum size {MAX_MANIFEST_SIZE}")
    if st.st_mode & (stat.S_ISUID | stat.S_ISGID | stat.S_ISVTX):
        raise ValueError("manifest.json has unsafe mode (suid/sgid/sticky)")
    if st.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
        raise ValueError("manifest.json has unsafe mode (group/world writable)")
    if getattr(st, "st_nlink", 1) > 1:
        raise ValueError(f"manifest.json has hardlinks (st_nlink={st.st_nlink})")
    _check_no_xattrs(manifest_path)

    def _reject_duplicate_keys_pairs_hook(
        pairs: list[tuple[str, Any]]
    ) -> dict[str, Any]:
        obj: dict[str, Any] = {}
        for key, value in pairs:
            if key in obj:
                raise ValueError(f"Duplicate JSON key rejected: {key!r}")
            obj[key] = value
        return obj

    try:
        with manifest_path.open("r", encoding="utf-8") as fp:
            data = json.load(fp, object_pairs_hook=_reject_duplicate_keys_pairs_hook)
    except (json.JSONDecodeError, UnicodeDecodeError) as e:
        raise ValueError(f"Failed to parse manifest.json: {e}") from e

    if not isinstance(data, dict):
        raise ValueError("Manifest root must be a JSON object")
    if data.get("schema_version") != 1:
        raise ValueError(f"Unsupported schema_version: {data.get('schema_version')}")

    manifest_files = data.get("files")
    if not isinstance(manifest_files, list):
        raise ValueError("Manifest 'files' field must be a list")
    if len(manifest_files) > MAX_ENTRIES:
        raise ValueError(f"Manifest exceeds maximum entries limit: {MAX_ENTRIES}")

    total_manifest_size = 0
    seen_paths: set[str] = set()
    manifest_map: dict[str, dict] = {}
    for idx, entry in enumerate(manifest_files):
        if not isinstance(entry, dict):
            raise ValueError(f"Manifest files entry {idx} is not an object")
        for k in ("path", "size", "sha256", "mode"):
            if k not in entry:
                raise ValueError(f"Manifest files entry {idx} missing key '{k}'")
        p = entry["path"]
        if not isinstance(p, str) or not p:
            raise ValueError(f"Invalid path in manifest entry {idx}")
        if (
            p.startswith("/")
            or "\\" in p
            or any(seg in ("..", ".", "") for seg in p.split("/"))
        ):
            raise ValueError(f"Unsafe path in manifest entry {idx}: {p!r}")
        if p == "manifest.json":
            raise ValueError("manifest.json cannot be listed in manifest files")
        if p in seen_paths:
            raise ValueError(f"Duplicate path in manifest: {p}")
        seen_paths.add(p)

        s = entry["size"]
        if not isinstance(s, int) or s < 0 or s > MAX_FILE_SIZE:
            raise ValueError(f"Invalid size for {p}: {s}")
        total_manifest_size += s
        if total_manifest_size > MAX_TOTAL_UNCOMPRESSED_SIZE:
            raise ValueError(
                f"Total uncompressed size exceeds maximum {MAX_TOTAL_UNCOMPRESSED_SIZE}"
            )

        sha = entry["sha256"]
        if (
            not isinstance(sha, str)
            or len(sha) != 64
            or not all(c in "0123456789abcdefABCDEF" for c in sha)
        ):
            raise ValueError(f"Invalid sha256 for {p}: {sha}")

        mode = entry["mode"]
        if (
            not isinstance(mode, str)
            or len(mode) != 4
            or not all(c in "01234567" for c in mode)
        ):
            raise ValueError(f"Invalid mode for {p}: {mode}")

        manifest_map[p] = {
            "path": p,
            "size": s,
            "sha256": sha.lower(),
            "mode": mode,
        }

    actual_inventory = inventory(root)
    actual_files = [item for item in actual_inventory if item["path"] != "manifest.json"]
    actual_map = {item["path"]: item for item in actual_files}

    missing = set(manifest_map.keys()) - set(actual_map.keys())
    if missing:
        raise ValueError(
            f"Files listed in manifest but missing on disk: {sorted(missing)}"
        )

    extra = set(actual_map.keys()) - set(manifest_map.keys())
    if extra:
        raise ValueError(
            f"Unexpected extra files on disk not in manifest: {sorted(extra)}"
        )

    for p, expected in manifest_map.items():
        actual = actual_map[p]
        if actual["size"] != expected["size"]:
            raise ValueError(
                f"Size mismatch for {p}: manifest={expected['size']}, disk={actual['size']}"
            )
        if actual["sha256"].lower() != expected["sha256"]:
            raise ValueError(
                f"SHA-256 mismatch for {p}: manifest={expected['sha256']}, disk={actual['sha256']}"
            )
        if actual["mode"] != expected["mode"]:
            raise ValueError(
                f"Mode mismatch for {p}: manifest={expected['mode']}, disk={actual['mode']}"
            )


def main(argv: list[str] | None = None) -> int:
    """Minimal CLI command dispatcher."""
    import argparse

    parser = argparse.ArgumentParser(description="Staging and package validation tools")
    subparsers = parser.add_subparsers(dest="command", required=True)

    val_parser = subparsers.add_parser(
        "validate-tree", help="Validate directory tree against its manifest.json"
    )
    val_parser.add_argument("root", type=Path, help="Path to staging root directory")

    args = parser.parse_args(argv)
    if args.command == "validate-tree":
        try:
            verify_manifest(args.root)
            print(f"OK: {args.root} verified against manifest.json")
            return 0
        except Exception as e:
            print(f"ERROR: {e}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
