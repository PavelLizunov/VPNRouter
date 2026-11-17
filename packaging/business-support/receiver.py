#!/usr/bin/env python3
"""Private diagnostic storage behind the existing HTTPS ingress; stdlib only."""
import argparse
import contextlib
import fcntl
import hashlib
import hmac
import http.server
import importlib.util
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import stat
import struct
import tempfile
import threading
import time
import unicodedata
import zipfile
from urllib.parse import urlsplit, parse_qs

_category_spec = importlib.util.spec_from_file_location(
    "_vpnrouter_corp_categories", Path(__file__).with_name("categories.py"))
corp_categories = importlib.util.module_from_spec(_category_spec)
_category_spec.loader.exec_module(corp_categories)

_company_spec = importlib.util.spec_from_file_location(
    "_vpnrouter_corp_companies", Path(__file__).with_name("companies.py"))
corp_companies = importlib.util.module_from_spec(_company_spec)
_company_spec.loader.exec_module(corp_companies)

MAX_BODY = 24 * 1024 * 1024
MAX_EXPANDED = 20 * 1024 * 1024
QUOTA = 512 * 1024 * 1024
MAX_CONCURRENT_REQUESTS = 4
UPLOAD_HEADROOM = MAX_CONCURRENT_REQUESTS * (MAX_BODY + 4096)
RETENTION = 7 * 86400
ENTRY = re.compile(r"(?:README\.txt|summary\.json|(?:app|core|launch|legacy-launch|crash)-[0-9]{2}\.txt)")
MAX_RECEIPT = 65536
UNSAFE_ANALYSIS = re.compile(
    r"[<>`|;&$\\]|[a-z][a-z0-9+.-]*://|\bwww\.|"
    r"\b(?:[a-z0-9-]+\.)+[a-z]{2,63}\b|"
    r"\b(?:powershell|pwsh|cmd|curl|wget|sudo|bash|sh|regedit|msiexec)\b", re.I)


def public_analysis(value):
    """Expose bounded plain prose only; never return arbitrary receipt fields."""
    unavailable = {"version": 1, "state": "unavailable"}
    if (not isinstance(value, dict) or type(value.get("version")) is not int or
            value["version"] != 1 or value.get("state") not in ("unavailable", "pending", "ready")):
        return unavailable
    result = {"version": 1, "state": value["state"]}
    for field in ("message_ru", "message_en"):
        if field not in value:
            continue
        message = value[field]
        if (not isinstance(message, str) or not 0 < len(message) <= 300 or
                any(unicodedata.category(char) in ("Cc", "Cf", "Cs") for char in message) or
                len(message.encode("utf-16-le")) > 600 or not message.strip() or
                UNSAFE_ANALYSIS.search(message)):
            return unavailable
        result[field] = message.strip()
    if result["state"] == "ready" and len(result) == 2:
        return unavailable
    return result


def validate_archive(path):
    """Never extract caller paths; validate bounded contents before private storage."""
    # The official collector emits ordinary ZIPs without archive comments/ZIP64.
    # Bound the central directory before ZipFile allocates its entry objects.
    if (os.fstat(path.fileno()).st_size if hasattr(path, "fileno") else Path(path).stat().st_size) < 22:
        raise ValueError("invalid ZIP footer")
    with (contextlib.nullcontext(path) if hasattr(path, "fileno") else open(path, "rb")) as source:
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
                    entry.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) or
                    stat.S_ISLNK(entry.external_attr >> 16)):
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
        return summary


def client_reported_device(summary):
    """Optional untrusted display names, never employee identity or storage paths."""
    device = summary.get("device")
    if not isinstance(device, dict):
        return {}
    result = {}
    for field in ("computer_name", "user_name"):
        value = device.get(field)
        if not isinstance(value, str) or len(value) > 128:
            continue
        if any(unicodedata.category(char) in ("Cc", "Cf", "Cs") for char in value):
            continue
        # Match the Windows collector's limit of 128 UTF-16 code units.
        if len(value.encode("utf-16-le")) > 256:
            continue
        value = value.strip()
        if value:
            result[field] = value
    return result


class MutationLock:
    """Threads and cooperating processes serialize mutations on the directory inode."""
    def __init__(self, root):
        self.root = root
        self.local = threading.RLock()
        self.state = threading.local()

    def __enter__(self):
        self.local.acquire()
        depth = getattr(self.state, "depth", 0)
        descriptor = None
        try:
            if depth == 0:
                descriptor = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                fcntl.flock(descriptor, fcntl.LOCK_EX)
                self.state.fd = descriptor
            self.state.depth = depth + 1
            return self
        except BaseException:
            if descriptor is not None:
                os.close(descriptor)
            self.local.release()
            raise

    def __exit__(self, *_):
        self.state.depth -= 1
        if self.state.depth == 0:
            os.close(self.state.fd)
        self.local.release()


def enabled_identity(key_file, identity):
    try:
        records = json.loads(Path(key_file).read_text())
        return records.get(identity, {}).get("enabled") is True
    except (OSError, ValueError, TypeError, AttributeError):
        return False


def private_file(path, root, limit=65536):
    path = Path(path).resolve()
    if path == Path(root).resolve() or Path(root).resolve() in path.parents:
        raise ValueError("private configuration overlaps reports")
    with open(path, "rb", opener=lambda p, f: os.open(p, f | os.O_NOFOLLOW | os.O_NONBLOCK)) as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise ValueError("regular private file required")
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("private file too large")
    return data


def load_receipt(root, report_id, identity, now=None):
    if not re.fullmatch(r"[0-9a-f]{64}", report_id):
        return None
    try:
        path = Path(root) / (report_id + ".json")
        with open(path, "rb", opener=lambda p, f: os.open(p, f | os.O_NOFOLLOW | os.O_NONBLOCK)) as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or (now or time.time()) - info.st_mtime > RETENTION:
                return None
            raw = stream.read(MAX_RECEIPT + 1)
        if len(raw) > MAX_RECEIPT:
            return None
        value = json.loads(raw)
        archive = Path(root) / (report_id + ".zip")
        if (not isinstance(value, dict) or value.get("identity") != identity or
                value.get("report_id") != report_id or archive.is_symlink() or not archive.is_file() or
                (now or time.time()) - archive.stat().st_mtime > RETENTION):
            return None
        return value
    except (OSError, ValueError, UnicodeError, RecursionError):
        return None


def update_receipt(root, report_id, value, quota=QUOTA):
    path = Path(root) / (report_id + ".json")
    info = path.stat()
    raw = json.dumps(value, ensure_ascii=True).encode()
    if len(raw) > MAX_RECEIPT:
        raise ValueError("receipt too large")
    used = sum(p.stat().st_size for p in Path(root).iterdir()
               if not p.is_symlink() and p.is_file())
    # A separate analyzer cannot see the receiver's in-memory reservations.
    # Keep capacity for every bounded streaming request, including its metadata.
    if used + len(raw) + UPLOAD_HEADROOM > quota:
        raise OSError("storage quota")
    atomic_json(path, value, preserved_ns=(info.st_atime_ns, info.st_mtime_ns))


def report_summary(receipt):
    job = receipt.get("diagnosis")
    job = job if isinstance(job, dict) else {}
    result = job.get("result") or {}
    assessments = result.get("assessments", [])
    verdicts = [a.get("verdict") for a in assessments if isinstance(a, dict)]
    verdict = ("problem_found" if "problem_found" in verdicts else
               "insufficient_data" if "insufficient_data" in verdicts else
               "no_problem_evidenced" if verdicts else None)
    device = client_reported_device({"device": receipt.get("client_reported_device")})
    return {"report_id": receipt["report_id"], "received_utc": receipt.get("received_utc"),
            "bytes": receipt.get("bytes"), "device_label": device.get("computer_name"),
            "analysis_state": job.get("state", "received"), "verdict": verdict,
            "summary_ru": result.get("owner_explanation"), "model": job.get("requested_model"),
            "updated_utc": job.get("updated_utc")}


class Store:
    def __init__(self, root, key_file, trusted_proxy, quota=QUOTA, inbox_config=None):
        self.root = Path(root)
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.root.is_symlink():
            raise ValueError("linked storage refused")
        self.root.chmod(0o700)
        self.key_file = Path(key_file)
        self.proxy = str(ipaddress.ip_address(trusted_proxy))
        self.quota = quota
        self.lock = MutationLock(self.root)
        self.inbox = None
        self.require_company_header = False
        self.companies = None
        if inbox_config:
            config = json.loads(private_file(inbox_config, self.root))
            identity = config.get("corp_identity_sha256", "")
            token = private_file(config["service_token_file"], self.root, 512).decode("ascii").strip()
            if (config.get("schema") != 1 or not re.fullmatch(r"[0-9a-f]{64}", identity) or
                    not re.fullmatch(r"[A-Za-z0-9._~+/=-]{32,512}", token)):
                raise ValueError("invalid inbox configuration")
            self.companies = corp_companies.Companies(
                self.key_file, identity, config.get("legacy_policy_id"), config.get("upload_endpoint"))
            self.inbox = (identity, token)
            strict = config.get("require_company_header", False)
            if type(strict) is not bool:
                raise ValueError("invalid company header mode")
            self.require_company_header = strict
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

    def receipt(self, report_id, identity):
        """Read only the existing bounded receipt belonging to this upload key."""
        try:
            path = self.root / (report_id + ".json")
            with open(path, "rb", opener=lambda p, f: os.open(
                    p, f | os.O_NOFOLLOW | os.O_NONBLOCK)) as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                    return None
                raw = stream.read(MAX_RECEIPT + 1)
            if len(raw) > MAX_RECEIPT:
                return None
            receipt = json.loads(raw)
            archive = self.root / (report_id + ".zip")
            if (isinstance(receipt, dict) and receipt.get("identity") == identity and
                    receipt.get("report_id") == report_id and
                    not archive.is_symlink() and archive.is_file()):
                return {"stored": True, "report_id": report_id,
                        "analysis": public_analysis(receipt.get("analysis"))}
        except (OSError, ValueError, UnicodeError, RecursionError):
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
        self.slots = threading.BoundedSemaphore(MAX_CONCURRENT_REQUESTS)
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

    def authenticated_identity(self):
        store = self.server.store
        if self.client_address[0] != store.proxy:
            self.reply(403, {"error": "ingress required"})
            return None
        identity = store.identity(self.headers.get("Authorization", ""))
        if identity is None:
            self.reply(401, {"error": "upload identity required"})
        return identity

    def internal(self, retry=False, delete=False):
        store = self.server.store
        if not self.path.startswith("/internal/"):
            return False
        capability = store.inbox
        authorizations = self.headers.get_all("Authorization", [])
        if capability is None:
            self.reply(404, {"error": "not found"})
            return True
        identity, token = capability
        if (len(authorizations) != 1 or
                not hmac.compare_digest(authorizations[0].encode(), ("Bearer " + token).encode())):
            self.reply(401, {"error": "service identity required"})
            return True
        if not enabled_identity(store.key_file, identity):
            self.reply(401, {"error": "service identity required"})
            return True
        parsed = urlsplit(self.path)
        company_headers = self.headers.get_all("X-Corp-Company", [])
        if parsed.path == "/internal/corp-companies/provision":
            if company_headers:
                self.reply(400, {"error": "invalid company"})
            else:
                self.provision_company(parsed)
            return True
        if not company_headers and store.require_company_header:
            self.reply(400, {"error": "company required"})
            return True
        if len(company_headers) > 1:
            self.reply(400, {"error": "invalid company"})
            return True
        if company_headers:
            try:
                identity = store.companies.resolve(company_headers[0]) if store.companies else None
            except ValueError:
                self.reply(400, {"error": "invalid company"})
                return True
            except (OSError, RecursionError):
                self.reply(503, {"error": "storage unavailable"})
                return True
            if identity is None:
                self.reply(404, {"error": "company not found"})
                return True
        if not enabled_identity(store.key_file, identity):
            self.reply(401, {"error": "service identity required"})
            return True
        if parsed.path in ("/internal/corp-categories/state", "/internal/corp-categories/save"):
            self.category_request(parsed, identity)
            return True
        base = "/internal/corp-diagnostics/reports"
        match = re.fullmatch(base + r"/([0-9a-f]{64})(/retry)?", parsed.path)
        with store.lock:
            if parsed.path == base and not retry and not delete:
                query = parse_qs(parsed.query, keep_blank_values=True)
                try:
                    if set(query) - {"limit", "cursor"} or any(len(v) != 1 for v in query.values()):
                        raise ValueError()
                    limit = int(query.get("limit", ["50"])[0])
                    if not 1 <= limit <= 100:
                        raise ValueError()
                    reports = [load_receipt(store.root, p.stem, identity) for p in store.root.glob("*.json")]
                    reports = sorted((r for r in reports if r), key=lambda r: (str(r.get("received_utc", "")), r["report_id"]), reverse=True)
                    cursor = query.get("cursor", [None])[0]
                    if cursor is not None:
                        indices = [i for i, r in enumerate(reports) if r["report_id"] == cursor]
                        if not indices:
                            raise ValueError()
                        reports = reports[indices[0] + 1:]
                    selected = reports[:limit]
                    self.reply(200, {"schema": 1, "reports": [report_summary(r) for r in selected],
                                     "next_cursor": selected[-1]["report_id"] if len(reports) > limit else None})
                except (ValueError, TypeError):
                    self.reply(400, {"error": "invalid pagination"})
                return True
            receipt = load_receipt(store.root, match[1], identity) if match and not parsed.query else None
            if receipt is None or bool(match[2]) != retry:
                self.reply(404, {"error": "not found"})
                return True
            if delete:
                if (self.headers.get_content_type() != "application/json" or
                        self.headers.get("Transfer-Encoding") or
                        self.headers.get("Content-Length") != "2" or self.rfile.read(2) != b"{}"):
                    self.reply(400, {"error": "empty JSON required"})
                    return True
                archive = store.root / (match[1] + ".zip")
                metadata = store.root / (match[1] + ".json")
                try:
                    freed = archive.stat().st_size + metadata.stat().st_size
                    # Remove the archive first: even a partial failure invalidates
                    # the pair, so a late worker cannot recreate its receipt.
                    archive.unlink()
                    metadata.unlink()
                except OSError:
                    self.reply(503, {"error": "storage unavailable"})
                else:
                    self.reply(200, {"schema": 1, "state": "deleted", "freed_bytes": freed})
                return True
            job = receipt.get("diagnosis") or {}
            if retry:
                if (self.headers.get("Transfer-Encoding") or self.headers.get("Content-Length") != "2" or self.rfile.read(2) != b"{}"):
                    self.reply(400, {"error": "empty JSON required"})
                    return True
                if job.get("state") == "running" and job.get("lease_until", 0) > time.time():
                    self.reply(409, {"error": "analysis active"})
                    return True
                job.update(state="pending", generation=job.get("generation", 0) + 1, attempts=0,
                           lease_until=0, next_attempt=0, updated_utc=utc_now())
                receipt["diagnosis"] = job
                try:
                    update_receipt(store.root, match[1], receipt, store.quota)
                    self.reply(202, {"schema": 1, "state": "pending"})
                except (OSError, ValueError):
                    self.reply(503, {"error": "storage unavailable"})
            else:
                self.reply(200, {"schema": 1, "report": report_summary(receipt),
                                 "analysis": job.get("result"), "provenance": job.get("provenance")})
        return True

    def provision_company(self, parsed):
        if self.command != "POST":
            self.reply(405, {"error": "method not allowed"})
            return
        try:
            lengths = self.headers.get_all("Content-Length", [])
            if (self.path != parsed.path or self.headers.get_all("Transfer-Encoding") or
                    self.headers.get_content_type() != "application/json" or len(lengths) != 1 or
                    not lengths[0].isascii() or not lengths[0].isdigit() or len(lengths[0]) > 4 or
                    not 0 < int(lengths[0]) <= 4096):
                raise ValueError("invalid request")
            raw = self.rfile.read(int(lengths[0]))
            if len(raw) != int(lengths[0]):
                raise ValueError("incomplete request")
            def unique(pairs):
                result = {}
                for key, value in pairs:
                    if key in result:
                        raise ValueError("duplicate field")
                    result[key] = value
                return result
            body = json.loads(raw, object_pairs_hook=unique)
            if not isinstance(body, dict) or set(body) != {"policy_id", "label"}:
                raise ValueError("invalid request")
            corp_companies.policy_id(body["policy_id"])
            corp_companies.label_value(body["label"])
        except (OSError, ValueError, TypeError, UnicodeError, RecursionError):
            self.reply(400, {"error": "invalid request"})
            return
        try:
            if self.server.store.companies is None:
                raise ValueError("provisioning unavailable")
            result = self.server.store.companies.provision(body["policy_id"], body["label"])
        except (OSError, ValueError, TypeError, RecursionError):
            self.reply(503, {"error": "provisioning unavailable"})
            return
        self.reply(200, result)

    def category_request(self, parsed, identity):
        if self.command != "POST":
            self.reply(405, {"message": "Для работы с категориями отправьте запрос сохранения из интерфейса."})
            return
        try:
            lengths = self.headers.get_all("Content-Length", [])
            expected = self.headers.get_all("X-Corp-Expected-Length", [])
            if (self.path != parsed.path or self.headers.get_all("Transfer-Encoding") or
                    self.headers.get_content_type() != "application/json" or
                    len(lengths) != 1 or len(expected) != 1):
                raise ValueError("invalid metadata request")
            for length in (lengths[0], expected[0]):
                if (not length.isascii() or not length.isdigit() or len(length) > 10 or
                        int(length) > corp_categories.MAX_BODY):
                    raise ValueError("invalid metadata length")
            size = int(lengths[0])
            if size != int(expected[0]):
                raise ValueError("metadata length mismatch")
            raw = self.rfile.read(size)
            if len(raw) != size:
                raise ValueError("incomplete metadata request")
            body = corp_categories.decode(raw)
            if not isinstance(body, dict) or (parsed.path.endswith("/state") and body != {}):
                raise ValueError("invalid metadata object")
        except (OSError, ValueError, TypeError, RecursionError):
            self.reply(400, {"message": "Не удалось прочитать запрос категорий. Обновите страницу и повторите попытку."})
            return
        try:
            store = self.server.store
            metadata = corp_categories.CategoryStore(store.root, identity, store.lock)
            result = metadata.state() if parsed.path.endswith("/state") else metadata.save(body)
            self.reply(200, result)
        except corp_categories.CategoryError as error:
            self.reply(error.status, {"message": str(error)})

    def do_GET(self):
        if self.internal():
            return
        if self.path == "/health":
            self.reply(200, {"ok": True})
            return
        match = re.fullmatch(r"/api/reports/([0-9a-f]{64})/analysis", self.path)
        if match:
            identity = self.authenticated_identity()
            if identity is None:
                return
            receipt = self.server.store.receipt(match[1], identity[0])
            if receipt is not None:
                self.reply(200, receipt)
                return
        self.reply(404, {"error": "not found"})

    def do_DELETE(self):
        if self.internal(delete=True):
            return
        self.reply(404, {"error": "not found"})

    def do_POST(self):
        if self.internal(retry=True):
            return
        store = self.server.store
        if self.path != "/api/reports":
            self.reply(404, {"error": "not found"})
            return
        identity = self.authenticated_identity()
        if identity is None:
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
            summary = validate_archive(temporary)
            # Identical archives from different employees remain attributable.
            report_id = hashlib.sha256((identity[0] + digest.hexdigest()).encode()).hexdigest()
            destination = store.root / (report_id + ".zip")
            with store.lock:
                duplicate = destination.exists()
                metadata = store.root / (report_id + ".json")
                if not metadata.exists():
                    receipt = {"identity": identity[0], "employee": identity[1],
                               "report_id": report_id, "sha256": digest.hexdigest(),
                               "received_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                               "bytes": size}
                    device = client_reported_device(summary)
                    if device:
                        receipt["client_reported_device"] = device
                    used = sum(p.stat().st_size for p in store.owned_files())
                    if used + len(json.dumps(receipt).encode()) > store.quota:
                        raise OSError("storage quota")
                    atomic_json(metadata, receipt)
                if not duplicate:
                    os.replace(temporary, destination)
                    store.active.discard(temporary)
                    temporary = None
                    sync_directory(store.root)
            payload = {"stored": True, "report_id": report_id}
            if "support-analysis-v1" in [part.strip() for part in self.headers.get(
                    "X-VPNRouter-Capabilities", "").split(",")]:
                payload = store.receipt(report_id, identity[0]) or dict(
                    payload, analysis={"version": 1, "state": "unavailable"})
            response = (200 if duplicate else 201, payload)
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


def utc_now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def sync_directory(path):
    directory = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def atomic_json(path, value, preserved_ns=None):
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix="incoming-", delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream)
            stream.flush()
            os.fsync(stream.fileno())
        if preserved_ns is not None:
            os.utime(temporary, ns=preserved_ns)
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
    serve.add_argument("--inbox-config")
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
        store = Store(args.reports, args.keys, args.trusted_proxy, inbox_config=args.inbox_config)
        with Receiver((args.host, args.port), store) as server:
            server.serve_forever()


if __name__ == "__main__":
    main()
