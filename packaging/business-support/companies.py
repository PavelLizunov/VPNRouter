"""Durable private policy-to-upload-identity bindings."""
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import stat
import unicodedata
from urllib.parse import urlsplit

MAX_REGISTRY = 4 * 1024 * 1024


def policy_id(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", value):
        raise ValueError("invalid policy id")
    return value


def label_value(value):
    if (not isinstance(value, str) or not value.strip() or len(value) > 128 or
            any(unicodedata.category(c) in ("Cc", "Cf", "Cs") for c in value)):
        raise ValueError("invalid label")
    return value


def endpoint_value(value):
    if not isinstance(value, str) or len(value) > 2048 or any(ord(c) <= 32 or ord(c) == 127 for c in value):
        raise ValueError("invalid endpoint")
    try:
        parsed = urlsplit(value)
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None or
                parsed.password is not None or parsed.fragment or parsed.query or
                not parsed.netloc.isascii() or "\\" in parsed.netloc or parsed.port == 0):
            raise ValueError("invalid endpoint")
    except ValueError:
        raise ValueError("invalid endpoint") from None
    return value


def _directory(path):
    # Walk from the root without following any path component.
    path = Path(os.path.abspath(path))
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in path.parts[1:]:
            next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        return fd
    except BaseException:
        os.close(fd)
        raise


def _read(fd, name, missing=None):
    try:
        file_fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
    except FileNotFoundError:
        if missing is not None:
            return missing
        raise
    with os.fdopen(file_fd, "rb") as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise ValueError("regular file required")
        raw = stream.read(MAX_REGISTRY + 1)
    if len(raw) > MAX_REGISTRY:
        raise ValueError("registry too large")
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate registry key")
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique)


def _write(fd, name, value):
    raw = json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode()
    if len(raw) > MAX_REGISTRY:
        raise ValueError("registry too large")
    temporary = ".companies-" + secrets.token_hex(16)
    file_fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd)
    try:
        with os.fdopen(file_fd, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        # Refuse an existing symlink, including dangling links.
        try:
            existing = os.stat(name, dir_fd=fd, follow_symlinks=False)
            if not stat.S_ISREG(existing.st_mode):
                raise ValueError("regular file required")
        except FileNotFoundError:
            pass
        os.replace(temporary, name, src_dir_fd=fd, dst_dir_fd=fd)
        os.fsync(fd)
    finally:
        try:
            os.unlink(temporary, dir_fd=fd)
        except FileNotFoundError:
            pass


class Companies:
    def __init__(self, keys, legacy_identity, legacy_policy=None, endpoint=None):
        self.keys = Path(keys)
        self.legacy_identity = legacy_identity
        self.endpoint = endpoint_value(endpoint) if endpoint is not None else None
        if legacy_policy is not None:
            policy_id(legacy_policy)
            with self.locked() as fd:
                registry = self._registry(fd)
                existing = registry.get(legacy_policy)
                if existing is not None and existing["identity"] != legacy_identity:
                    raise ValueError("legacy policy binding conflict")
                if any(k != legacy_policy and v["identity"] == legacy_identity for k, v in registry.items()):
                    raise ValueError("legacy identity binding conflict")
                if existing is None:
                    registry[legacy_policy] = {"identity": legacy_identity, "token": None, "pending": False}
                    _write(fd, self.registry_name, registry)

    @property
    def registry_name(self):
        return self.keys.name + ".companies.json"

    @contextlib.contextmanager
    def locked(self):
        fd = _directory(self.keys.parent)
        lock_fd = None
        try:
            lock_fd = os.open(self.keys.name + ".lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600, dir_fd=fd)
            if not stat.S_ISREG(os.fstat(lock_fd).st_mode):
                raise ValueError("regular lock required")
            fcntl.flock(lock_fd, fcntl.LOCK_EX)
            yield fd
        finally:
            if lock_fd is not None:
                os.close(lock_fd)
            os.close(fd)

    def _registry(self, fd):
        registry = _read(fd, self.registry_name, {})
        if not isinstance(registry, dict):
            raise ValueError("invalid registry")
        seen = set()
        for key, entry in registry.items():
            policy_id(key)
            if (not isinstance(entry, dict) or set(entry) != {"identity", "token", "pending"} or type(entry["pending"]) is not bool or
                    not isinstance(entry["identity"], str) or not re.fullmatch(r"[0-9a-f]{64}", entry["identity"]) or
                    entry["identity"] in seen):
                raise ValueError("invalid binding")
            token = entry["token"]
            if token is not None and (not isinstance(token, str) or
                    not re.fullmatch(r"[A-Za-z0-9._~+/=-]{32,512}", token) or
                    hashlib.sha256(token.encode("ascii")).hexdigest() != entry["identity"]):
                raise ValueError("invalid credential")
            seen.add(entry["identity"])
        return registry

    def resolve(self, policy):
        policy_id(policy)
        with self.locked() as fd:
            entry = self._registry(fd).get(policy)
            return entry["identity"] if entry else None

    def provision(self, policy, label):
        policy_id(policy)
        label_value(label)
        if self.endpoint is None:
            raise ValueError("upload endpoint unavailable")
        with self.locked() as fd:
            registry = self._registry(fd)
            keys = _read(fd, self.keys.name)
            if not isinstance(keys, dict):
                raise ValueError("invalid keys")
            entry = registry.get(policy)
            if entry is None:
                token = secrets.token_urlsafe(32)
                identity = hashlib.sha256(token.encode()).hexdigest()
                if identity in keys or any(v["identity"] == identity for v in registry.values()):
                    raise ValueError("identity collision")
                entry = {"identity": identity, "token": token, "pending": True}
                registry[policy] = entry
                _write(fd, self.registry_name, registry)
            identity, token = entry["identity"], entry["token"]
            if token is None:
                raise ValueError("existing credential unavailable")
            if identity not in keys:
                if identity == self.legacy_identity or not entry["pending"]:
                    raise ValueError("legacy key unavailable")
                # Consume creation intent before attempting the separate key commit.
                # A later missing key can be an explicit revocation, even when an
                # earlier response was lost. Retain the token and fail closed.
                entry["pending"] = False
                _write(fd, self.registry_name, registry)
                keys[identity] = {"label": label, "enabled": True}
                _write(fd, self.keys.name, keys)
            elif not isinstance(keys[identity], dict) or keys[identity].get("enabled") is not True:
                raise ValueError("identity disabled")
            if entry["pending"]:
                entry["pending"] = False
                _write(fd, self.registry_name, registry)
            return {"schema": 1, "policy_id": policy,
                    "support": {"endpoint": self.endpoint, "token": token}}
