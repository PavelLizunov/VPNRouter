"""Bounded corporate rule metadata, separate from routing feeds and reports."""
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import unicodedata

MAX_BODY = 262144
MAX_REVISION = 9007199254740991
IDENTIFIER = re.compile(r"[a-z][a-z0-9-]{0,39}")


class CategoryError(Exception):
    def __init__(self, status, message):
        self.status = status
        super().__init__(message)


def strict_object(pairs):
    """JSON objects must not silently replace an earlier field."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field")
        result[key] = value
    return result


def reject_constant(_):
    raise ValueError("non-finite JSON number")


def decode(raw):
    return json.loads(raw.decode("utf-8"), object_pairs_hook=strict_object,
                      parse_constant=reject_constant)


def valid_revision(value):
    return type(value) is int and 0 <= value <= MAX_REVISION


def vectors(categories, assignments):
    if not isinstance(categories, list) or len(categories) > 32:
        raise ValueError("invalid categories")
    if not isinstance(assignments, list) or len(assignments) > 576:
        raise ValueError("invalid assignments")
    ids, names, result_categories = set(), set(), []
    for item in categories:
        if not isinstance(item, dict) or set(item) != {"id", "name"}:
            raise ValueError("invalid category fields")
        identifier, name = item["id"], item["name"]
        if (not isinstance(identifier, str) or not IDENTIFIER.fullmatch(identifier) or
                identifier in ids):
            raise ValueError("invalid category identifier")
        if (not isinstance(name, str) or not 0 < len(name) <= 64 or
                name != name.strip() or
                any(unicodedata.category(char) in ("Cc", "Cf", "Cs") for char in name) or
                len(name.encode("utf-8")) > 256 or name.casefold() in names):
            raise ValueError("invalid category name")
        ids.add(identifier)
        names.add(name.casefold())
        result_categories.append({"id": identifier, "name": name})
    keys, result_assignments = set(), []
    for item in assignments:
        if not isinstance(item, dict) or set(item) != {"source", "value", "category"}:
            raise ValueError("invalid assignment fields")
        source, value, category = item["source"], item["value"], item["category"]
        if (source not in ("site", "phone") or not isinstance(value, str) or
                not 0 < len(value) <= 253 or
                any(not 33 <= ord(char) <= 126 for char in value)):
            raise ValueError("invalid rule key")
        if category is not None and (not isinstance(category, str) or category not in ids):
            raise ValueError("unknown category")
        key = (source, value)
        if key in keys:
            raise ValueError("duplicate rule key")
        keys.add(key)
        result_assignments.append({"source": source, "value": value, "category": category})
    return result_categories, result_assignments


def encode(value):
    raw = json.dumps(value, ensure_ascii=False, separators=(",", ":"),
                     allow_nan=False).encode("utf-8")
    if len(raw) > MAX_BODY:
        raise ValueError("metadata too large")
    return raw


class CategoryStore:
    def __init__(self, root: Path, identity: str, lock):
        if not isinstance(identity, str) or not re.fullmatch(r"[0-9a-f]{64}", identity):
            raise CategoryError(400, "Не удалось определить организацию. Обновите страницу.")
        self.root = Path(root)
        self.path = self.root / ("categories-" + identity + ".json")
        self.lock = lock

    def _read(self):
        try:
            descriptor = os.open(self.path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        except FileNotFoundError:
            return {"schema": 1, "revision": 0, "categories": [
                {"id": "sites", "name": "Сайты"},
                {"id": "telephony", "name": "Телефония"}], "assignments": []}
        with os.fdopen(descriptor, "rb") as stream:
            if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                raise ValueError("regular metadata file required")
            raw = stream.read(MAX_BODY + 1)
        if len(raw) > MAX_BODY:
            raise ValueError("metadata too large")
        state = decode(raw)
        if (not isinstance(state, dict) or
                set(state) != {"schema", "revision", "categories", "assignments"} or
                type(state["schema"]) is not int or state["schema"] != 1 or
                not valid_revision(state["revision"])):
            raise ValueError("invalid metadata state")
        categories, assignments = vectors(state["categories"], state["assignments"])
        return {"schema": 1, "revision": state["revision"],
                "categories": categories, "assignments": assignments}

    def _write(self, raw):
        temporary = None
        try:
            descriptor, name = tempfile.mkstemp(dir=self.root, prefix="categories-pending-")
            temporary = Path(name)
            with os.fdopen(descriptor, "wb") as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                    raise OSError("regular metadata file required")
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.path)
            temporary = None
            directory = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)

    def state(self) -> dict:
        try:
            with self.lock:
                return self._read()
        except (OSError, ValueError, TypeError, RecursionError) as error:
            raise CategoryError(503, "Категории недоступны. Повторите попытку позже; если ошибка сохраняется, обратитесь в поддержку.") from error

    def save(self, body: dict) -> dict:
        try:
            if (not isinstance(body, dict) or
                    set(body) != {"expected_revision", "categories", "assignments"} or
                    not valid_revision(body["expected_revision"])):
                raise ValueError("invalid save fields")
            categories, assignments = vectors(body["categories"], body["assignments"])
            encode(body)
        except (ValueError, TypeError, RecursionError) as error:
            raise CategoryError(400, "Проверьте названия категорий и назначения правил, затем повторите сохранение.") from error
        try:
            with self.lock:
                state = self._read()
                if state["revision"] != body["expected_revision"]:
                    raise CategoryError(409, "Категории изменены в другом окне. Обновите страницу и повторите изменения.")
                if state["categories"] == categories and state["assignments"] == assignments:
                    return state
                if state["revision"] == MAX_REVISION:
                    raise CategoryError(400, "Не удалось сохранить новую версию категорий. Обратитесь в поддержку.")
                result = {"schema": 1, "revision": state["revision"] + 1,
                          "categories": categories, "assignments": assignments}
                self._write(encode(result))
                return result
        except (OSError, ValueError, TypeError, RecursionError) as error:
            raise CategoryError(503, "Не удалось сохранить категории. Повторите попытку позже; если ошибка сохраняется, обратитесь в поддержку.") from error
