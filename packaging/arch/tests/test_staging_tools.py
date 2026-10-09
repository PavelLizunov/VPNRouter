"""Comprehensive unit tests for packaging/arch/staging_tools.py.

Covers:
- Safe archive extraction with whole-archive pre-validation
- Rejection of malicious paths, traversal, duplicates, prefix file conflicts
- Rejection of symlinks, hardlinks, FIFOs, special devices, sparse, pax xattrs, SUID/SGID/sticky
- Guaranteed no partial materialization on last-member defect
- Monkeypatchable resource limits
- ELF x86-64 header inspection (ET_EXEC/ET_DYN, architecture, endianness)
- Inventory generation and validation (octal modes, sha256, paths)
- Manifest write and verify roundtrip, tampering, duplicates, extras, missing files
- Minimal CLI validate-tree command
"""

from __future__ import annotations

import hashlib
import gzip
import io
import json
import os
from pathlib import Path
import stat
import struct
import tarfile
import sys
import tempfile
import unittest
from unittest import mock

# Ensure packaging/arch is in sys.path without colliding with site-packages 'packaging'
ARCH_DIR = Path(__file__).resolve().parent.parent
if str(ARCH_DIR) not in sys.path:
    sys.path.insert(0, str(ARCH_DIR))

import staging_tools


def _create_tar_bytes(
    entries: list[tuple[tarfile.TarInfo, bytes | None]],
    gzip_compress: bool = True,
) -> bytes:
    bio = io.BytesIO()
    mode = "w:gz" if gzip_compress else "w"
    with tarfile.open(fileobj=bio, mode=mode) as tf:
        for ti, data in entries:
            if data is not None:
                ti.size = len(data)
                tf.addfile(ti, io.BytesIO(data))
            else:
                tf.addfile(ti)
    return bio.getvalue()


def _make_raw_tar_header(
    name: str = "PaxHeader/test",
    typeflag: bytes = b"x",
    size: int = 100 * 1024 * 1024,
) -> bytes:
    hdr = bytearray(512)
    nb = name.encode("ascii")
    hdr[:len(nb)] = nb
    hdr[100:108] = b"0000644\x00"
    hdr[108:116] = b"0000000\x00"
    hdr[116:124] = b"0000000\x00"
    hdr[124:136] = f"{size:011o} ".encode("ascii")
    hdr[136:148] = b"00000000000\x00"
    hdr[156:157] = typeflag
    hdr[257:263] = b"ustar\x00"
    hdr[263:265] = b"00"
    hdr[148:156] = b"        "
    chksum = sum(hdr)
    hdr[148:156] = f"{chksum:06o}\x00 ".encode("ascii")
    return bytes(hdr)


def _make_elf_header(
    magic: bytes = b"\x7fELF",
    e_class: int = 2,       # ELFCLASS64
    e_data: int = 1,        # ELFDATA2LSB
    ident_version: int = 1, # EV_CURRENT
    e_type: int = 3,        # ET_DYN
    e_machine: int = 62,    # EM_X86_64
    e_version: int = 1,     # EV_CURRENT
) -> bytes:
    # 64-byte ELF header layout
    hdr = bytearray(64)
    hdr[:4] = magic
    hdr[4] = e_class
    hdr[5] = e_data
    hdr[6] = ident_version
    hdr[16:18] = struct.pack("<H", e_type)
    hdr[18:20] = struct.pack("<H", e_machine)
    hdr[20:24] = struct.pack("<I", e_version)
    return bytes(hdr)


class TestSafeExtract(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_safe_extract_success(self) -> None:
        ti_dir = tarfile.TarInfo("./")
        ti_dir.type = tarfile.DIRTYPE
        ti_dir.mode = 0o755

        ti_sub = tarfile.TarInfo("sub/")
        ti_sub.type = tarfile.DIRTYPE
        ti_sub.mode = 0o755

        ti_file = tarfile.TarInfo("sub/hello.txt")
        ti_file.type = tarfile.REGTYPE
        ti_file.mode = 0o644
        data_file = b"hello safe extraction"

        ti_bin = tarfile.TarInfo("run.sh")
        ti_bin.type = tarfile.REGTYPE
        ti_bin.mode = 0o755
        data_bin = b"#!/bin/sh\necho ok"

        archive_bytes = _create_tar_bytes([
            (ti_dir, None),
            (ti_sub, None),
            (ti_file, data_file),
            (ti_bin, data_bin),
        ])
        archive_path = self.tmp / "test.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest_path = self.tmp / "extracted"
        staging_tools.safe_extract(archive_path, dest_path, sha)

        self.assertTrue(dest_path.is_dir())
        self.assertEqual((dest_path / "sub" / "hello.txt").read_bytes(), data_file)
        self.assertEqual((dest_path / "run.sh").read_bytes(), data_bin)

        # Check permissions: dirs 0755, regular 0644, executable 0755
        self.assertEqual(stat.S_IMODE(dest_path.stat().st_mode), 0o755)
        self.assertEqual(stat.S_IMODE((dest_path / "sub").stat().st_mode), 0o755)
        self.assertEqual(stat.S_IMODE((dest_path / "sub" / "hello.txt").stat().st_mode), 0o644)
        self.assertEqual(stat.S_IMODE((dest_path / "run.sh").stat().st_mode), 0o755)

    def test_safe_extract_sha_mismatch(self) -> None:
        ti = tarfile.TarInfo("foo.txt")
        ti.type = tarfile.REGTYPE
        archive_bytes = _create_tar_bytes([(ti, b"content")])
        archive_path = self.tmp / "test.tar.gz"
        archive_path.write_bytes(archive_bytes)

        dest_path = self.tmp / "extracted"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest_path, "0" * 64)
        self.assertIn("SHA-256 mismatch", str(ctx.exception))
        self.assertFalse(dest_path.exists())

    def test_safe_extract_dest_exists(self) -> None:
        ti = tarfile.TarInfo("foo.txt")
        ti.type = tarfile.REGTYPE
        archive_bytes = _create_tar_bytes([(ti, b"content")])
        archive_path = self.tmp / "test.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest_path = self.tmp / "already_exists"
        dest_path.mkdir()

        with self.assertRaises(FileExistsError):
            staging_tools.safe_extract(archive_path, dest_path, sha)

    def test_safe_extract_dest_symlink_ancestor(self) -> None:
        real_parent = self.tmp / "real_parent"
        real_parent.mkdir()
        symlink_parent = self.tmp / "symlink_parent"
        os.symlink(real_parent, symlink_parent)

        ti = tarfile.TarInfo("foo.txt")
        ti.type = tarfile.REGTYPE
        archive_bytes = _create_tar_bytes([(ti, b"content")])
        archive_path = self.tmp / "test.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest_path = symlink_parent / "target"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest_path, sha)
        self.assertIn("ancestor is a symlink", str(ctx.exception))
        self.assertFalse(dest_path.exists())

    def test_malicious_path_absolute(self) -> None:
        ti = tarfile.TarInfo("/etc/passwd")
        ti.type = tarfile.REGTYPE
        archive_bytes = _create_tar_bytes([(ti, b"evil")])
        archive_path = self.tmp / "evil.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Absolute path rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_malicious_path_backslash(self) -> None:
        ti = tarfile.TarInfo("sub\\evil.txt")
        ti.type = tarfile.REGTYPE
        archive_bytes = _create_tar_bytes([(ti, b"evil")])
        archive_path = self.tmp / "evil.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("backslash", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_malicious_path_traversal(self) -> None:
        for bad_path in ("../escape.txt", "sub/../../escape.txt", "a/../b"):
            ti = tarfile.TarInfo(bad_path)
            ti.type = tarfile.REGTYPE
            archive_bytes = _create_tar_bytes([(ti, b"evil")])
            archive_path = self.tmp / "evil.tar.gz"
            archive_path.write_bytes(archive_bytes)
            sha = hashlib.sha256(archive_bytes).hexdigest()

            dest = self.tmp / "dest"
            with self.assertRaises(ValueError):
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertFalse(dest.exists())

    def test_malicious_duplicate_normalized_paths(self) -> None:
        ti1 = tarfile.TarInfo("./foo.txt")
        ti1.type = tarfile.REGTYPE
        ti2 = tarfile.TarInfo("foo.txt")
        ti2.type = tarfile.REGTYPE

        archive_bytes = _create_tar_bytes([(ti1, b"one"), (ti2, b"two")])
        archive_path = self.tmp / "dup.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Duplicate normalized path", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_prefix_file_conflict_file_then_child(self) -> None:
        ti_file = tarfile.TarInfo("conflict")
        ti_file.type = tarfile.REGTYPE
        ti_child = tarfile.TarInfo("conflict/child.txt")
        ti_child.type = tarfile.REGTYPE

        archive_bytes = _create_tar_bytes([(ti_file, b"parent"), (ti_child, b"child")])
        archive_path = self.tmp / "conflict.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Prefix file conflict", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_prefix_file_conflict_child_then_file(self) -> None:
        ti_child = tarfile.TarInfo("conflict/child.txt")
        ti_child.type = tarfile.REGTYPE
        ti_file = tarfile.TarInfo("conflict")
        ti_file.type = tarfile.REGTYPE

        archive_bytes = _create_tar_bytes([(ti_child, b"child"), (ti_file, b"parent")])
        archive_path = self.tmp / "conflict.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Prefix file conflict", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_prefix_file_conflict_dir_then_file(self) -> None:
        ti_dir = tarfile.TarInfo("samename")
        ti_dir.type = tarfile.DIRTYPE
        ti_file = tarfile.TarInfo("samename")
        ti_file.type = tarfile.REGTYPE

        archive_bytes = _create_tar_bytes([(ti_dir, None), (ti_file, b"content")])
        archive_path = self.tmp / "conflict.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError):
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertFalse(dest.exists())

    def test_malicious_link_rejected(self) -> None:
        # Symlink
        ti_sym = tarfile.TarInfo("sym.txt")
        ti_sym.type = tarfile.SYMTYPE
        ti_sym.linkname = "target.txt"

        archive_bytes = _create_tar_bytes([(ti_sym, None)])
        archive_path = self.tmp / "sym.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Link rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

        # Hardlink
        ti_lnk = tarfile.TarInfo("hard.txt")
        ti_lnk.type = tarfile.LNKTYPE
        ti_lnk.linkname = "target.txt"

        archive_bytes = _create_tar_bytes([(ti_lnk, None)])
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Link rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_malicious_fifo_and_device_rejected(self) -> None:
        for m_type in (tarfile.FIFOTYPE, tarfile.CHRTYPE, tarfile.BLKTYPE):
            ti = tarfile.TarInfo("special")
            ti.type = m_type
            archive_bytes = _create_tar_bytes([(ti, None)])
            archive_path = self.tmp / "special.tar.gz"
            archive_path.write_bytes(archive_bytes)
            sha = hashlib.sha256(archive_bytes).hexdigest()

            dest = self.tmp / "dest"
            with self.assertRaises(ValueError) as ctx:
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertIn("Special file/device/FIFO rejected", str(ctx.exception))
            self.assertFalse(dest.exists())

    def test_malicious_suid_sgid_sticky_rejected(self) -> None:
        for mode, name in ((0o4755, "suid"), (0o2755, "sgid"), (0o1755, "sticky")):
            ti = tarfile.TarInfo(f"{name}.sh")
            ti.type = tarfile.REGTYPE
            ti.mode = mode
            archive_bytes = _create_tar_bytes([(ti, b"echo unsafe")])
            archive_path = self.tmp / f"{name}.tar.gz"
            archive_path.write_bytes(archive_bytes)
            sha = hashlib.sha256(archive_bytes).hexdigest()

            dest = self.tmp / f"dest_{name}"
            with self.assertRaises(ValueError) as ctx:
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertIn("Unsafe mode (suid/sgid/sticky)", str(ctx.exception))
            self.assertFalse(dest.exists())

    def test_malicious_sparse_and_pax_xattrs_rejected(self) -> None:
        # Sparse
        ti_sparse = tarfile.TarInfo("sparse.dat")
        ti_sparse.type = getattr(tarfile, "GNUTYPE_SPARSE", b"S")
        archive_bytes = _create_tar_bytes([(ti_sparse, None)])
        archive_path = self.tmp / "sparse.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_sparse"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Sparse file rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

        # PAX xattr
        ti_pax = tarfile.TarInfo("xattr.txt")
        ti_pax.type = tarfile.REGTYPE
        ti_pax.pax_headers = {"SCHILY.xattr.user.test": "payload"}
        archive_bytes = _create_tar_bytes([(ti_pax, b"data")])
        archive_path = self.tmp / "pax.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_pax"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("PAX extended attribute rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_no_partial_materialization_on_last_member_defect(self) -> None:
        # Multiple valid files followed by one malicious member at the end
        entries = []
        for i in range(5):
            ti = tarfile.TarInfo(f"valid_{i}.txt")
            ti.type = tarfile.REGTYPE
            entries.append((ti, f"valid data {i}".encode()))

        # Last member is malicious
        ti_bad = tarfile.TarInfo("../escape.txt")
        ti_bad.type = tarfile.REGTYPE
        entries.append((ti_bad, b"bad"))

        archive_bytes = _create_tar_bytes(entries)
        archive_path = self.tmp / "last_bad.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "never_created_dest"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Path traversal '..' rejected", str(ctx.exception))

        # Invariant: destination must never exist, zero files materialized
        self.assertFalse(dest.exists())

    def test_monkeypatch_limits(self) -> None:
        # Monkeypatch MAX_FILE_SIZE
        orig_max_file = staging_tools.MAX_FILE_SIZE
        try:
            staging_tools.MAX_FILE_SIZE = 10
            ti = tarfile.TarInfo("big.bin")
            ti.type = tarfile.REGTYPE
            archive_bytes = _create_tar_bytes([(ti, b"0123456789extra")])
            archive_path = self.tmp / "limit.tar.gz"
            archive_path.write_bytes(archive_bytes)
            sha = hashlib.sha256(archive_bytes).hexdigest()

            dest = self.tmp / "dest_limit"
            with self.assertRaises(ValueError) as ctx:
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertIn("exceeds maximum", str(ctx.exception))
            self.assertFalse(dest.exists())
        finally:
            staging_tools.MAX_FILE_SIZE = orig_max_file

        # Monkeypatch MAX_ENTRIES
        orig_max_entries = staging_tools.MAX_ENTRIES
        try:
            staging_tools.MAX_ENTRIES = 2
            entries = [
                (tarfile.TarInfo("f1.txt"), b"1"),
                (tarfile.TarInfo("f2.txt"), b"2"),
                (tarfile.TarInfo("f3.txt"), b"3"),
            ]
            archive_bytes = _create_tar_bytes(entries)
            archive_path = self.tmp / "limit_entries.tar.gz"
            archive_path.write_bytes(archive_bytes)
            sha = hashlib.sha256(archive_bytes).hexdigest()

            dest = self.tmp / "dest_limit_entries"
            with self.assertRaises(ValueError) as ctx:
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertIn("maximum entries limit", str(ctx.exception))
            self.assertFalse(dest.exists())
        finally:
            staging_tools.MAX_ENTRIES = orig_max_entries

    def test_huge_pax_header_declaration_minimal_tar(self) -> None:
        # 1024-byte archive (512 header + 512 zeros) declaring 100 MiB PAX header
        tar_bytes = _make_raw_tar_header(name="PaxHeader/test", typeflag=b"x", size=100 * 1024 * 1024) + b"\x00" * 512
        archive_path = self.tmp / "huge_pax.tar"
        archive_path.write_bytes(tar_bytes)
        sha = hashlib.sha256(tar_bytes).hexdigest()

        dest = self.tmp / "dest_huge_pax"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Tar metadata header", str(ctx.exception))
        self.assertIn("exceeds maximum", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_huge_global_pax_header_declaration_minimal_tar(self) -> None:
        # Global PAX header declaring 100 MiB
        tar_bytes = _make_raw_tar_header(name="GlobalHead/test", typeflag=b"g", size=100 * 1024 * 1024) + b"\x00" * 512
        archive_path = self.tmp / "huge_global_pax.tar"
        archive_path.write_bytes(tar_bytes)
        sha = hashlib.sha256(tar_bytes).hexdigest()

        dest = self.tmp / "dest_huge_global_pax"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Tar metadata header", str(ctx.exception))
        self.assertIn("exceeds maximum", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_huge_gnu_longlink_and_longname_declaration_minimal_tar(self) -> None:
        # GNU LongLink (b"K") and LongName (b"L")
        for flag in (b"L", b"K"):
            tar_bytes = _make_raw_tar_header(name="././@LongLink", typeflag=flag, size=100 * 1024 * 1024) + b"\x00" * 512
            archive_path = self.tmp / f"huge_gnu_{flag.decode()}.tar"
            archive_path.write_bytes(tar_bytes)
            sha = hashlib.sha256(tar_bytes).hexdigest()

            dest = self.tmp / f"dest_huge_gnu_{flag.decode()}"
            with self.assertRaises(ValueError) as ctx:
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertIn("Tar metadata header", str(ctx.exception))
            self.assertIn("exceeds maximum", str(ctx.exception))
            self.assertFalse(dest.exists())

    def test_compressed_zeros_bomb_limit_monkeypatch(self) -> None:
        zeros_gz = gzip.compress(b"\x00" * 50000)
        archive_path = self.tmp / "zeros.tar.gz"
        archive_path.write_bytes(zeros_gz)
        sha = hashlib.sha256(zeros_gz).hexdigest()

        dest = self.tmp / "dest_zeros"
        orig_limit = staging_tools.MAX_TOTAL_UNCOMPRESSED_SIZE
        try:
            staging_tools.MAX_TOTAL_UNCOMPRESSED_SIZE = 5000
            with self.assertRaises(ValueError) as ctx:
                staging_tools.safe_extract(archive_path, dest, sha)
            self.assertIn("exceeds maximum 5000", str(ctx.exception))
            self.assertFalse(dest.exists())
        finally:
            staging_tools.MAX_TOTAL_UNCOMPRESSED_SIZE = orig_limit

    def test_root_dot_regular_file_rejected(self) -> None:
        ti = tarfile.TarInfo(".")
        ti.type = tarfile.REGTYPE
        archive_bytes = _create_tar_bytes([(ti, b"evil content")])
        archive_path = self.tmp / "root_reg.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_root_reg"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("Root entry '.' cannot be a regular file", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_dir_size_greater_than_zero_rejected(self) -> None:
        ti = tarfile.TarInfo("somedir")
        ti.type = tarfile.DIRTYPE
        ti.size = 512
        archive_bytes = _create_tar_bytes([(ti, None)])
        archive_path = self.tmp / "dir_size.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_dir_size"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("size > 0", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_pax_safe_keys_accepted_including_git_comment(self) -> None:
        ti = tarfile.TarInfo("file.txt")
        ti.type = tarfile.REGTYPE
        ti.pax_headers = {
            "comment": "commit 05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a",
            "mtime": "1700000000.5",
            "atime": "1700000000.1",
            "ctime": "1700000000.2",
            "path": "file.txt",
        }
        archive_bytes = _create_tar_bytes([(ti, b"safe pax content")])
        archive_path = self.tmp / "safe_pax.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_safe_pax"
        staging_tools.safe_extract(archive_path, dest, sha)
        self.assertTrue((dest / "file.txt").is_file())
        self.assertEqual((dest / "file.txt").read_bytes(), b"safe pax content")

    def test_pax_unknown_key_denied(self) -> None:
        ti = tarfile.TarInfo("file.txt")
        ti.type = tarfile.REGTYPE
        ti.pax_headers = {"dangerous_unknown_key": "injected"}
        archive_bytes = _create_tar_bytes([(ti, b"content")])
        archive_path = self.tmp / "bad_pax.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_bad_pax"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("PAX header key rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_pax_sparse_xattrs_denied(self) -> None:
        ti = tarfile.TarInfo("file.txt")
        ti.type = tarfile.REGTYPE
        ti.pax_headers = {"GNU.sparse.numblocks": "1"}
        archive_bytes = _create_tar_bytes([(ti, b"content")])
        archive_path = self.tmp / "sparse_pax.tar.gz"
        archive_path.write_bytes(archive_bytes)
        sha = hashlib.sha256(archive_bytes).hexdigest()

        dest = self.tmp / "dest_sparse_pax"
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(archive_path, dest, sha)
        self.assertIn("PAX sparse extended attribute rejected", str(ctx.exception))
        self.assertFalse(dest.exists())

    def test_unsupported_compression_bz2_and_xz(self) -> None:
        bz2_bytes = b"BZh91AY&SYfake" + b"\x00" * 500
        bz2_path = self.tmp / "test.tar.bz2"
        bz2_path.write_bytes(bz2_bytes)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(bz2_path, self.tmp / "d_bz2", hashlib.sha256(bz2_bytes).hexdigest())
        self.assertIn("Unsupported compression: bz2", str(ctx.exception))

        xz_bytes = b"\xfd7zXZ\x00fake" + b"\x00" * 500
        xz_path = self.tmp / "test.tar.xz"
        xz_path.write_bytes(xz_bytes)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.safe_extract(xz_path, self.tmp / "d_xz", hashlib.sha256(xz_bytes).hexdigest())
        self.assertIn("Unsupported compression: xz", str(ctx.exception))


class TestValidateElfX8664(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.tmp = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_valid_elf_exec_and_dyn(self) -> None:
        for e_type in (2, 3):  # ET_EXEC=2, ET_DYN=3
            bin_path = self.tmp / f"test_type_{e_type}"
            bin_path.write_bytes(_make_elf_header(e_type=e_type))
            # Must succeed without raising
            staging_tools.validate_elf_x86_64(bin_path)

    def test_invalid_elf_headers(self) -> None:
        # File too short
        short_file = self.tmp / "short.bin"
        short_file.write_bytes(b"short")
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(short_file)
        self.assertIn("too small", str(ctx.exception))

        # Bad magic
        bad_magic = self.tmp / "bad_magic.bin"
        bad_magic.write_bytes(_make_elf_header(magic=b"NOPE"))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(bad_magic)
        self.assertIn("invalid magic", str(ctx.exception))

        # 32-bit ELF (class 1)
        elf_32 = self.tmp / "elf_32.bin"
        elf_32.write_bytes(_make_elf_header(e_class=1))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(elf_32)
        self.assertIn("Not a 64-bit ELF", str(ctx.exception))

        # Big-endian (data 2)
        elf_be = self.tmp / "elf_be.bin"
        elf_be.write_bytes(_make_elf_header(e_data=2))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(elf_be)
        self.assertIn("Not a little-endian ELF", str(ctx.exception))

        # Wrong architecture (AArch64 = 183)
        elf_arm = self.tmp / "elf_arm.bin"
        elf_arm.write_bytes(_make_elf_header(e_machine=183))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(elf_arm)
        self.assertIn("Invalid ELF machine", str(ctx.exception))

        # Invalid type (ET_REL = 1)
        elf_rel = self.tmp / "elf_rel.bin"
        elf_rel.write_bytes(_make_elf_header(e_type=1))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(elf_rel)
        self.assertIn("Invalid ELF type", str(ctx.exception))

        # Invalid e_version (offset 20 != 1)
        elf_bad_ever = self.tmp / "elf_bad_ever.bin"
        elf_bad_ever.write_bytes(_make_elf_header(e_version=2))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(elf_bad_ever)
        self.assertIn("Invalid ELF e_version", str(ctx.exception))

        # Invalid ident_version (byte 6 != 1)
        elf_bad_iver = self.tmp / "elf_bad_iver.bin"
        elf_bad_iver.write_bytes(_make_elf_header(ident_version=2))
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(elf_bad_iver)
        self.assertIn("Invalid ELF ident version", str(ctx.exception))

        # Symlink rejected
        real_bin = self.tmp / "real.bin"
        real_bin.write_bytes(_make_elf_header())
        sym_bin = self.tmp / "sym.bin"
        os.symlink(real_bin, sym_bin)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.validate_elf_x86_64(sym_bin)
        self.assertIn("cannot be a symlink", str(ctx.exception))


class TestInventoryAndManifest(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_inventory_and_manifest_roundtrip(self) -> None:
        sub = self.root / "pkg"
        sub.mkdir()
        f1 = sub / "app.dll"
        f1.write_bytes(b"app binary data")
        os.chmod(f1, 0o644)

        f2 = self.root / "entry.sh"
        f2.write_bytes(b"#!/bin/sh\nrun")
        os.chmod(f2, 0o755)

        inv = staging_tools.inventory(self.root)
        self.assertEqual(len(inv), 2)
        self.assertEqual(inv[0]["path"], "entry.sh")
        self.assertEqual(inv[0]["mode"], "0755")
        self.assertEqual(inv[0]["size"], len(b"#!/bin/sh\nrun"))
        self.assertEqual(inv[1]["path"], "pkg/app.dll")
        self.assertEqual(inv[1]["mode"], "0644")

        # Write manifest
        metadata = {
            "source_commit": "05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a",
            "architecture": "x86_64",
        }
        staging_tools.write_manifest(self.root, metadata)

        manifest_file = self.root / "manifest.json"
        self.assertTrue(manifest_file.is_file())
        with manifest_file.open("r") as fp:
            data = json.load(fp)
        self.assertEqual(data["schema_version"], 1)
        self.assertEqual(data["source_commit"], "05c0bcaee08a5defb1ceeb1cdaab83b3bc1a2a8a")
        # Ensure manifest.json is NOT in files list
        self.assertEqual([f["path"] for f in data["files"]], ["entry.sh", "pkg/app.dll"])

        # Verify manifest passes
        staging_tools.verify_manifest(self.root)

    def test_inventory_rejects_links_and_unsafe_modes(self) -> None:
        # Symlink in directory
        real_file = self.root / "real.txt"
        real_file.write_bytes(b"real")
        sym_file = self.root / "sym.txt"
        os.symlink(real_file, sym_file)

        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("Symlink file rejected", str(ctx.exception))
        sym_file.unlink()

        # Symlink directory
        sub_dir = self.root / "sub"
        sub_dir.mkdir()
        sym_dir = self.root / "symdir"
        os.symlink(sub_dir, sym_dir)

        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("Symlink directory rejected", str(ctx.exception))
        sym_dir.unlink()

        # Symlink root
        sym_root = self.root.parent / "sym_root"
        os.symlink(self.root, sym_root)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(sym_root)
        self.assertIn("symlink", str(ctx.exception).lower())
        sym_root.unlink()

        # Unsafe mode file (sticky bit 0o1755)
        sticky_file = self.root / "sticky.bin"
        sticky_file.write_bytes(b"binary")
        os.chmod(sticky_file, 0o1755)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("unsafe mode", str(ctx.exception).lower())
        sticky_file.unlink()

        # SUID/SGID mocked on lstat
        mock_file = self.root / "mock_suid.bin"
        mock_file.write_bytes(b"bin")
        orig_lstat = Path.lstat
        try:
            def fake_lstat(path_obj):
                real_st = orig_lstat(path_obj)
                if path_obj.name == "mock_suid.bin":
                    # inject SUID
                    class FakeStat:
                        st_mode = real_st.st_mode | stat.S_ISUID
                        st_size = real_st.st_size
                    return FakeStat()
                return real_st

            Path.lstat = fake_lstat
            with self.assertRaises(ValueError) as ctx:
                staging_tools.inventory(self.root)
            self.assertIn("unsafe mode", str(ctx.exception).lower())
        finally:
            Path.lstat = orig_lstat
        mock_file.unlink()

        # Non-regular file (FIFO) in directory
        fifo_path = self.root / "test.fifo"
        try:
            os.mkfifo(fifo_path)
            with self.assertRaises(ValueError) as ctx:
                staging_tools.inventory(self.root)
            self.assertIn("Non-regular file rejected", str(ctx.exception))
        finally:
            if fifo_path.exists():
                fifo_path.unlink()

    def test_manifest_schema_and_size_bounds(self) -> None:
        f = self.root / "file.txt"
        f.write_bytes(b"hello")
        os.chmod(f, 0o644)
        staging_tools.write_manifest(self.root, {})

        manifest_path = self.root / "manifest.json"
        with manifest_path.open("r") as fp:
            data = json.load(fp)

        # Invalid schema_version
        bad_ver = dict(data)
        bad_ver["schema_version"] = 2
        manifest_path.write_text(json.dumps(bad_ver), encoding="utf-8")
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Unsupported schema_version", str(ctx.exception))

        # Monkeypatch MAX_MANIFEST_SIZE
        orig_max_manifest = staging_tools.MAX_MANIFEST_SIZE
        try:
            staging_tools.MAX_MANIFEST_SIZE = 10
            with self.assertRaises(ValueError) as ctx:
                staging_tools.write_manifest(self.root, {})
            self.assertIn("exceeds limit", str(ctx.exception))

            with self.assertRaises(ValueError) as ctx:
                staging_tools.verify_manifest(self.root)
            self.assertIn("exceeds maximum size", str(ctx.exception))
        finally:
            staging_tools.MAX_MANIFEST_SIZE = orig_max_manifest

    def test_manifest_tamper_detection(self) -> None:
        f = self.root / "file.txt"
        f.write_bytes(b"original content")
        os.chmod(f, 0o644)
        staging_tools.write_manifest(self.root, {})

        # 1. Content tamper (sha mismatch)
        f.write_bytes(b"tampered content")
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("SHA-256 mismatch", str(ctx.exception))

        # Restore content, change mode
        f.write_bytes(b"original content")
        os.chmod(f, 0o755)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Mode mismatch", str(ctx.exception))

    def test_manifest_extras_and_missing(self) -> None:
        f1 = self.root / "f1.txt"
        f1.write_bytes(b"file 1")
        os.chmod(f1, 0o644)
        staging_tools.write_manifest(self.root, {})

        # Extra file on disk
        extra = self.root / "extra.txt"
        extra.write_bytes(b"unaccounted")
        os.chmod(extra, 0o644)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Unexpected extra files on disk", str(ctx.exception))
        extra.unlink()

        # Missing file on disk
        f1.unlink()
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Files listed in manifest but missing on disk", str(ctx.exception))

    def test_manifest_duplicate_paths(self) -> None:
        f = self.root / "f.txt"
        f.write_bytes(b"abc")
        os.chmod(f, 0o644)
        staging_tools.write_manifest(self.root, {})

        # Manually corrupt manifest with duplicate path
        manifest_path = self.root / "manifest.json"
        with manifest_path.open("r") as fp:
            data = json.load(fp)
        data["files"].append(dict(data["files"][0]))
        manifest_path.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Duplicate path in manifest", str(ctx.exception))

    def test_cli_validate_tree(self) -> None:
        f = self.root / "file.txt"
        f.write_bytes(b"content")
        os.chmod(f, 0o644)
        staging_tools.write_manifest(self.root, {})

        # CLI validation succeeds
        with mock.patch("sys.stdout", new=io.StringIO()), mock.patch(
            "sys.stderr", new=io.StringIO()
        ):
            ret = staging_tools.main(["validate-tree", str(self.root)])
            self.assertEqual(ret, 0)

            # CLI validation fails on extra file
            extra = self.root / "extra.txt"
            extra.write_bytes(b"extra")
            ret_fail = staging_tools.main(["validate-tree", str(self.root)])
            self.assertEqual(ret_fail, 1)

    def test_inventory_rejects_hardlinks(self) -> None:
        f1 = self.root / "f1.txt"
        f1.write_bytes(b"data")
        os.chmod(f1, 0o644)
        f2 = self.root / "f2.txt"
        os.link(f1, f2)
        try:
            with self.assertRaises(ValueError) as ctx:
                staging_tools.inventory(self.root)
            self.assertIn("Hardlinked file rejected", str(ctx.exception))
        finally:
            f2.unlink()

    def test_inventory_rejects_xattrs(self) -> None:
        f1 = self.root / "f1.txt"
        f1.write_bytes(b"data")
        os.chmod(f1, 0o644)
        with mock.patch("os.listxattr", return_value=["security.capability"]):
            with self.assertRaises(ValueError) as ctx:
                staging_tools.inventory(self.root)
            self.assertIn("Extended attributes rejected", str(ctx.exception))

    def test_inventory_rejects_unsafe_group_and_world_writable(self) -> None:
        f1 = self.root / "f1.txt"
        f1.write_bytes(b"data")

        # Group writable file
        os.chmod(f1, 0o664)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("unsafe mode (group/world writable)", str(ctx.exception))

        # World writable file
        os.chmod(f1, 0o646)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("unsafe mode (group/world writable)", str(ctx.exception))

        os.chmod(f1, 0o644)

        # Group writable dir
        sub = self.root / "subdir"
        sub.mkdir()
        os.chmod(sub, 0o775)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("unsafe mode (group/world writable)", str(ctx.exception))

        # World writable dir
        os.chmod(sub, 0o757)
        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(self.root)
        self.assertIn("unsafe mode (group/world writable)", str(ctx.exception))

    def test_inventory_and_manifest_reject_symlink_ancestor_and_root(self) -> None:
        real_parent = self.root / "real_parent"
        real_parent.mkdir()
        symlink_parent = self.root / "symlink_parent"
        os.symlink(real_parent, symlink_parent)

        target_dir = symlink_parent / "target"
        target_dir.mkdir()
        (target_dir / "f.txt").write_bytes(b"data")
        os.chmod(target_dir / "f.txt", 0o644)

        with self.assertRaises(ValueError) as ctx:
            staging_tools.inventory(target_dir)
        self.assertIn("or ancestor is a symlink", str(ctx.exception))

        with self.assertRaises(ValueError) as ctx:
            staging_tools.write_manifest(target_dir, {})
        self.assertIn("or ancestor is a symlink", str(ctx.exception))

        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(target_dir)
        self.assertIn("or ancestor is a symlink", str(ctx.exception))

    def test_inventory_rejects_exceeding_max_entries_and_total_size(self) -> None:
        f1 = self.root / "f1.txt"
        f1.write_bytes(b"12345")
        os.chmod(f1, 0o644)
        f2 = self.root / "f2.txt"
        f2.write_bytes(b"67890")
        os.chmod(f2, 0o644)

        orig_entries = staging_tools.MAX_ENTRIES
        orig_size = staging_tools.MAX_TOTAL_UNCOMPRESSED_SIZE
        try:
            staging_tools.MAX_ENTRIES = 1
            with self.assertRaises(ValueError) as ctx:
                staging_tools.inventory(self.root)
            self.assertIn("maximum entries limit", str(ctx.exception))

            staging_tools.MAX_ENTRIES = orig_entries
            staging_tools.MAX_TOTAL_UNCOMPRESSED_SIZE = 8
            with self.assertRaises(ValueError) as ctx:
                staging_tools.inventory(self.root)
            self.assertIn("Total uncompressed size exceeds maximum", str(ctx.exception))
        finally:
            staging_tools.MAX_ENTRIES = orig_entries
            staging_tools.MAX_TOTAL_UNCOMPRESSED_SIZE = orig_size

    def test_manifest_rejects_duplicate_json_keys(self) -> None:
        f = self.root / "f.txt"
        f.write_bytes(b"data")
        os.chmod(f, 0o644)

        manifest_path = self.root / "manifest.json"

        # Duplicate root key
        manifest_path.write_text("""{
            "schema_version": 1,
            "schema_version": 1,
            "files": [{"path": "f.txt", "size": 4, "sha256": "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7", "mode": "0644"}]
        }""")
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Duplicate JSON key rejected: 'schema_version'", str(ctx.exception))

        # Duplicate metadata key
        manifest_path.write_text("""{
            "schema_version": 1,
            "source_commit": "abc",
            "source_commit": "def",
            "files": [{"path": "f.txt", "size": 4, "sha256": "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7", "mode": "0644"}]
        }""")
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Duplicate JSON key rejected: 'source_commit'", str(ctx.exception))

        # Duplicate file entry key
        manifest_path.write_text("""{
            "schema_version": 1,
            "files": [{"path": "f.txt", "path": "f.txt", "size": 4, "sha256": "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7", "mode": "0644"}]
        }""")
        with self.assertRaises(ValueError) as ctx:
            staging_tools.verify_manifest(self.root)
        self.assertIn("Duplicate JSON key rejected: 'path'", str(ctx.exception))

    def test_write_manifest_atomic_temp_excl_no_symlink_follow(self) -> None:
        f = self.root / "f.txt"
        f.write_bytes(b"data")
        os.chmod(f, 0o644)

        tmp_sym = self.root / f".manifest.json.tmp.{os.getpid()}"
        outside_target = self.root.parent / "outside_secret.txt"
        os.symlink(outside_target, tmp_sym)
        try:
            with self.assertRaises((ValueError, FileExistsError, OSError)):
                staging_tools.write_manifest(self.root, {})
            # Target must never have been created or overwritten
            self.assertFalse(outside_target.exists())
        finally:
            if tmp_sym.is_symlink() or tmp_sym.exists():
                tmp_sym.unlink()


if __name__ == "__main__":
    unittest.main()
