#!/usr/bin/env python3
"""Private diagnostic storage behind the existing HTTPS ingress; stdlib only."""
import argparse
import hashlib
import hmac
import http.server
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import struct
import tempfile
import threading
import time
import zipfile

MAX_BODY = 24 * 1024 * 1024
MAX_EXPANDED = 20 * 1024 * 1024
QUOTA = 512 * 1024 * 1024
RETENTION = 7 * 86400
ENTRY = re.compile(r"(?:README\.txt|summary\.json|(?:app|core|launch|legacy-launch|crash)-[0-9]{2}\.txt)")


def validate_archive(path):
    """Never extract caller paths; validate bounded contents before private storage."""
    # The official collector emits ordinary ZIPs without archive comments/ZIP64.
    # Bound the central directory before ZipFile allocates its entry objects.
    if Path(path).stat().st_size < 22:
        raise ValueError("invalid ZIP footer")
    with open(path, "rb") as source:
        source.seek(-22, os.SEEK_END)
        signature, disk, directory_disk, disk_count, count, directory_size, offset, comment = struct.unpack("<4s4H2LH", source.read(22))
    if (signature != b"PK\x05\x06" or disk or directory_disk or comment or
            disk_count != count or not 2 <= count <= 20 or directory_size > 131072 or
            offset == 0xFFFFFFFF):
        raise ValueError("unsupported ZIP directory")
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [e.filename for e in entries]
        if not 2 <= len(entries) <= 20 or len(set(names)) != len(names):
            raise ValueError("invalid entries")
        if "summary.json" not in names or "README.txt" not in names:
            raise ValueError("missing manifest")
        total = 0
        summary = None
        for entry in entries:
            if (not ENTRY.fullmatch(entry.filename) or entry.flag_bits & 1 or
                    entry.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)):
                raise ValueError("unsupported entry")
            if entry.file_size > 2 * 1024 * 1024:
                raise ValueError("entry too large")
            if entry.filename == "summary.json" and entry.file_size > 65536:
                raise ValueError("manifest too large")
            content = bytearray() if entry.filename == "summary.json" else None
            with archive.open(entry) as stream:
                while True:
                    chunk = stream.read(65536)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > MAX_EXPANDED:
                        raise ValueError("archive too large")
                    if content is not None:
                        content.extend(chunk)
                        if len(content) > 65536:
                            raise ValueError("manifest too large")
            if content is not None:
                summary = json.loads(content.decode("utf-8-sig"))
        if not isinstance(summary, dict) or summary.get("format") != "vpnrouter-business-support/1":
            raise ValueError("wrong report format")


class Store:
    def __init__(self, root, key_file, trusted_proxy, quota=QUOTA):
        self.root = Path(root)
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.root.is_symlink():
            raise ValueError("linked storage refused")
        self.root.chmod(0o700)
        self.key_file = Path(key_file)
        self.proxy = str(ipaddress.ip_address(trusted_proxy))
        self.quota = quota
        self.lock = threading.Lock()
        self.reserved = 0
        self.attempts = {}
        self.active = set()
        self.clock = time.time
        self.next_prune = 0
        self.prune(startup=True)

    def owned_files(self):
        return [p for p in self.root.iterdir() if
                re.fullmatch(r"(?:[0-9a-f]{64}\.(?:zip|json)|incoming-[a-z0-9_]{8})", p.name)
                and not p.is_symlink() and p.is_file()]

    def prune(self, startup=False):
        with self.lock:
            now = self.clock()
            if not startup and now < self.next_prune:
                return
            for path in self.owned_files():
                age = now - path.stat().st_mtime
                incoming = path.name.startswith("incoming-")
                if path not in self.active and ((incoming and (startup or age > 60)) or
                                                (not incoming and age > RETENTION)):
                    path.unlink()
            self.next_prune = now + 60

    def identity(self, authorization):
        if not authorization.startswith("Bearer ") or len(authorization) > 160:
            return None
        digest = hashlib.sha256(authorization[7:].encode()).hexdigest()
        try:
            keys = json.loads(self.key_file.read_text())
            for expected, record in keys.items():
                if hmac.compare_digest(digest, expected) and record.get("enabled") is True:
                    return digest, str(record["label"])[:128]
        except (OSError, ValueError, KeyError, TypeError):
            pass
        return None

    def reserve(self, identity, size):
        self.prune()
        with self.lock:
            now = self.clock()
            self.attempts = {k: v for k, v in self.attempts.items() if now - v[0] < 3600}
            start, count = self.attempts.get(identity, (now, 0))
            if count >= 10:
                return False
            self.attempts[identity] = (start, count + 1)
            used = sum(p.stat().st_size for p in self.owned_files())
            if used + self.reserved + size + 4096 > self.quota or shutil.disk_usage(self.root).free < MAX_BODY * 4:
                return False
            self.reserved += size + 4096
            return True

    def release(self, size):
        with self.lock:
            self.reserved -= size + 4096


class Receiver(http.server.ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, store, upload_timeout=30):
        self.store = store
        self.upload_timeout = upload_timeout
        self.slots = threading.BoundedSemaphore(4)
        super().__init__(address, Handler)

    def service_actions(self):
        self.store.prune()

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "VPNRouterSupport"

    def setup(self):
        super().setup()
        self.connection.settimeout(10)

    def log_message(self, *_):
        pass  # Never log credentials, report contents or caller-controlled paths.

    def reply(self, code, payload):
        body = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)
        self.close_connection = True

    def do_GET(self):
        self.reply(200 if self.path == "/health" else 404,
                   {"ok": True} if self.path == "/health" else {"error": "not found"})

    def do_POST(self):
        store = self.server.store
        if self.path != "/api/reports":
            self.reply(404, {"error": "not found"})
            return
        if self.client_address[0] != store.proxy:
            self.reply(403, {"error": "ingress required"})
            return
        identity = store.identity(self.headers.get("Authorization", ""))
        if identity is None:
            self.reply(401, {"error": "upload identity required"})
            return
        length = self.headers.get("Content-Length", "")
        if self.headers.get("Transfer-Encoding") or not length.isascii() or not length.isdigit():
            self.reply(411, {"error": "content length required"})
            return
        size = int(length) if len(length) <= 10 else MAX_BODY + 1
        if not 0 < size <= MAX_BODY:
            self.reply(413, {"error": "report too large"})
            return
        if self.headers.get_content_type() != "application/zip":
            self.reply(415, {"error": "ZIP required"})
            return
        if not store.reserve(identity[0], size):
            self.reply(429, {"error": "upload budget exceeded"})
            return
        temporary = None
        try:
            digest = hashlib.sha256()
            with tempfile.NamedTemporaryFile(dir=store.root, prefix="incoming-", delete=False) as stream:
                temporary = Path(stream.name)
                with store.lock:
                    store.active.add(temporary)
                remaining = size
                deadline = time.monotonic() + self.server.upload_timeout
                while remaining:
                    left = deadline - time.monotonic()
                    if left <= 0:
                        raise TimeoutError("upload deadline exceeded")
                    self.connection.settimeout(min(10, left))
                    chunk = self.rfile.read1(min(65536, remaining))
                    if not chunk:
                        raise ValueError("incomplete request")
                    stream.write(chunk)
                    digest.update(chunk)
                    remaining -= len(chunk)
                stream.flush()
                os.fsync(stream.fileno())
            validate_archive(temporary)
            # Identical archives from different employees remain attributable.
            report_id = hashlib.sha256((identity[0] + digest.hexdigest()).encode()).hexdigest()
            destination = store.root / (report_id + ".zip")
            with store.lock:
                duplicate = destination.exists()
                metadata = store.root / (report_id + ".json")
                if not metadata.exists():
                    atomic_json(metadata, {"identity": identity[0], "employee": identity[1],
                                          "report_id": report_id, "sha256": digest.hexdigest(),
                                          "received_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                                          "bytes": size})
                if not duplicate:
                    os.replace(temporary, destination)
                    store.active.discard(temporary)
                    temporary = None
                    sync_directory(store.root)
            response = (200 if duplicate else 201, {"stored": True, "report_id": report_id})
        except (ValueError, zipfile.BadZipFile, EOFError, UnicodeError):
            response = (400, {"error": "invalid diagnostic archive"})
        except (TimeoutError, socket.timeout):
            response = (408, {"error": "upload timed out"})
        except OSError:
            response = (503, {"error": "storage unavailable"})
        finally:
            try:
                if temporary is not None:
                    with store.lock:
                        store.active.discard(temporary)
                        try:
                            temporary.unlink(missing_ok=True)
                        except OSError:
                            pass  # Accounted for and recovered by the next sweep.
            finally:
                store.release(size)
        self.reply(*response)


def sync_directory(path):
    directory = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def atomic_json(path, value):
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix="incoming-", delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        temporary = None
        sync_directory(path.parent)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def issue_identity(keys, endpoint, output, label):
    """Provision a new upload-only employee identity; never print the credential."""
    from urllib.parse import urlsplit
    parsed = urlsplit(endpoint)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.fragment:
        raise ValueError("HTTPS endpoint required")
    key_path, client_path = Path(keys), Path(output)
    lock_path = key_path.with_suffix(key_path.suffix + ".lock")
    if client_path.resolve() in (key_path.resolve(), lock_path.resolve()):
        raise ValueError("registry and client paths must differ")
    key_path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    client_path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    import fcntl  # Receiver and credential provisioning target the Linux backend.
    descriptor = os.open(lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "a+") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        records = json.loads(key_path.read_text()) if key_path.exists() else {}
        token = secrets.token_urlsafe(32)
        records[hashlib.sha256(token.encode()).hexdigest()] = {"label": label[:128], "enabled": True}
        created = False
        try:
            with open(client_path, "x", encoding="utf-8", opener=lambda p, f: os.open(p, f | os.O_NOFOLLOW, 0o600)) as stream:
                created = True
                json.dump({"endpoint": endpoint, "token": token}, stream)
                stream.flush()
                os.fsync(stream.fileno())
            atomic_json(key_path, records)
        except BaseException:
            # An existing owner file is never overwritten or removed.
            if created:
                current = json.loads(key_path.read_text()) if key_path.exists() else {}
                if hashlib.sha256(token.encode()).hexdigest() not in current:
                    client_path.unlink(missing_ok=True)
            raise


def main():
    parser = argparse.ArgumentParser()
    actions = parser.add_subparsers(dest="action", required=True)
    serve = actions.add_parser("serve")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=18912)
    serve.add_argument("--reports", required=True)
    serve.add_argument("--keys", required=True)
    serve.add_argument("--trusted-proxy", required=True)
    issue = actions.add_parser("issue")
    issue.add_argument("--keys", required=True)
    issue.add_argument("--endpoint", required=True)
    issue.add_argument("--output", required=True)
    issue.add_argument("--label", required=True)
    args = parser.parse_args()
    if args.action == "issue":
        issue_identity(args.keys, args.endpoint, args.output, args.label)
    else:
        store = Store(args.reports, args.keys, args.trusted_proxy)
        with Receiver((args.host, args.port), store) as server:
            server.serve_forever()


if __name__ == "__main__":
    main()
