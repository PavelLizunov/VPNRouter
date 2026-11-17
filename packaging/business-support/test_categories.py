"""Behavioral contract for per-company category metadata, using only stdlib."""

import copy
import json
from pathlib import Path
import re
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor

from categories import CategoryError, CategoryStore, MAX_BODY


DEFAULT_CATEGORIES = [
    {"id": "sites", "name": "Сайты"},
    {"id": "telephony", "name": "Телефония"},
]
MAX_SAFE_INTEGER = 9007199254740991


class SerializedLock:
    """A reusable context manager, also usable as a context-manager factory."""

    def __init__(self):
        self.mutex = threading.RLock()

    def __call__(self):
        return self

    def __enter__(self):
        self.mutex.acquire()
        return self

    def __exit__(self, *exception):
        self.mutex.release()


class CategoryStoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.identity = "a1" * 32
        self.path = self.root / ("categories-" + self.identity + ".json")
        self.lock = SerializedLock()
        self.store = CategoryStore(self.root, self.identity, self.lock)

    def body(self, revision=0, categories=None, assignments=None):
        return {
            "expected_revision": revision,
            "categories": copy.deepcopy(
                DEFAULT_CATEGORIES if categories is None else categories
            ),
            "assignments": copy.deepcopy([] if assignments is None else assignments),
        }

    def assert_error(self, status, operation):
        with self.assertRaises(CategoryError) as caught:
            operation()
        self.assertEqual(status, caught.exception.status)

    def test_absent_company_has_default_categories_and_revision_zero(self):
        expected = {
            "schema": 1,
            "revision": 0,
            "categories": DEFAULT_CATEGORIES,
            "assignments": [],
        }
        self.assertEqual(expected, self.store.state())
        other = CategoryStore(self.root, "b2" * 32, self.lock)
        self.assertEqual(expected, other.state())

    def test_save_persists_metadata_without_changing_policy_or_report_files(self):
        policies = self.root / "policies.json"
        report = self.root / (self.identity + ".json")
        policies.write_bytes(b'{"policy":"keep"}')
        report.write_bytes(b'{"report":"keep"}')
        request = self.body(assignments=[
            {"source": "site", "value": "example.test", "category": "sites"},
        ])
        saved = self.store.save(request)
        self.assertEqual(1, saved["schema"])
        self.assertEqual(1, saved["revision"])
        self.assertEqual(request["categories"], saved["categories"])
        self.assertEqual(request["assignments"], saved["assignments"])
        self.assertEqual(saved, CategoryStore(self.root, self.identity, self.lock).state())
        self.assertTrue(self.path.is_file())
        self.assertIsNone(re.fullmatch(r"[0-9a-f]{64}\.json", self.path.name))
        self.assertEqual(b'{"policy":"keep"}', policies.read_bytes())
        self.assertEqual(b'{"report":"keep"}', report.read_bytes())

    def test_noop_preserves_revision_but_stale_noop_still_conflicts(self):
        self.assertEqual(0, self.store.save(self.body())["revision"])
        request = self.body(categories=[{"id": "work", "name": "Work"}])
        saved = self.store.save(request)
        request["expected_revision"] = 1
        self.assertEqual(saved, self.store.save(request))
        request["expected_revision"] = 0
        self.assert_error(409, lambda: self.store.save(request))
        self.assertEqual(saved, self.store.state())

    def test_shared_lock_makes_competing_compare_and_swap_writers_exclusive(self):
        start = threading.Barrier(2)

        def write(name):
            store = CategoryStore(self.root, self.identity, self.lock)
            start.wait(timeout=5)
            try:
                return ("saved", store.save(self.body(categories=[
                    {"id": "work", "name": name},
                ])))
            except CategoryError as error:
                return ("error", error.status)

        with ThreadPoolExecutor(max_workers=2) as workers:
            futures = [workers.submit(write, name) for name in ("First", "Second")]
            results = [future.result(timeout=10) for future in futures]
        successes = [value for kind, value in results if kind == "saved"]
        errors = [value for kind, value in results if kind == "error"]
        self.assertEqual([409], errors)
        self.assertEqual(1, len(successes))
        self.assertEqual(1, successes[0]["revision"])
        self.assertEqual(successes[0], self.store.state())

    def test_request_and_nested_objects_require_exact_keys_and_container_types(self):
        valid = self.body(assignments=[
            {"source": "site", "value": "example.test", "category": "sites"},
        ])
        invalid = [None, [], {}, {**valid, "extra": True}]
        for key in valid:
            invalid.append({name: value for name, value in valid.items() if name != key})
        invalid.extend([
            {**valid, "categories": {}},
            {**valid, "assignments": {}},
            self.body(categories=[{"id": "work", "name": "Work", "extra": 0}]),
            self.body(categories=[{"id": "work"}]),
            self.body(categories=[None]),
            self.body(assignments=[{"source": "site", "value": "a"}]),
            self.body(assignments=[{**valid["assignments"][0], "extra": 0}]),
            self.body(assignments=[None]),
        ])
        for request in invalid:
            with self.subTest(request=request):
                self.assert_error(400, lambda: self.store.save(request))
        self.assertEqual(0, self.store.state()["revision"])

    def test_revisions_are_safe_integers_and_increment_cannot_overflow(self):
        for revision in (True, False, -1, 0.0, "0", None, MAX_SAFE_INTEGER + 1):
            with self.subTest(revision=revision):
                self.assert_error(400, lambda: self.store.save(self.body(revision)))
        maximum = {
            "schema": 1, "revision": MAX_SAFE_INTEGER,
            "categories": DEFAULT_CATEGORIES, "assignments": [],
        }
        self.path.write_text(json.dumps(maximum), encoding="utf-8")
        self.assertEqual(maximum, self.store.save(self.body(MAX_SAFE_INTEGER)))
        change = self.body(MAX_SAFE_INTEGER, categories=[])
        self.assert_error(400, lambda: self.store.save(change))
        self.assertEqual(maximum, self.store.state())

    def test_category_ids_obey_grammar_and_are_unique(self):
        invalid_ids = ("", "Upper", "1first", "has_space ", "a_b", "а", "a" * 41, None, True)
        for category_id in invalid_ids:
            with self.subTest(category_id=category_id):
                request = self.body(categories=[{"id": category_id, "name": "Work"}])
                self.assert_error(400, lambda: self.store.save(request))
        duplicate = self.body(categories=[
            {"id": "same", "name": "One"}, {"id": "same", "name": "Two"},
        ])
        self.assert_error(400, lambda: self.store.save(duplicate))
        category = {"id": "a" + "0-" * 19 + "z", "name": "Valid"}
        self.assertEqual([category], self.store.save(self.body(categories=[category]))["categories"])

    def test_category_names_accept_unicode_but_reject_controls_and_casefold_duplicates(self):
        invalid_names = (
            "", " ", " leading", "trailing ", "x" * 65, "é" * 65,
            "line\nbreak", "a\x00b", "a\x7fb", "a\u200db", "a\ud800b", None, 1,
        )
        for name in invalid_names:
            with self.subTest(name=repr(name)):
                request = self.body(categories=[{"id": "work", "name": name}])
                self.assert_error(400, lambda: self.store.save(request))
        duplicate = self.body(categories=[
            {"id": "one", "name": "Straße"}, {"id": "two", "name": "STRASSE"},
        ])
        self.assert_error(400, lambda: self.store.save(duplicate))
        name = "\U0001f30d" * 64
        self.assertEqual(name, self.store.save(self.body(categories=[
            {"id": "world", "name": name},
        ]))["categories"][0]["name"])

    def test_assignment_fields_enforce_ascii_values_and_existing_category(self):
        base = {"source": "site", "value": "example.test", "category": "sites"}
        changes = [
            {"source": "other"}, {"source": None}, {"source": True},
            {"value": ""}, {"value": " leading"}, {"value": "trailing "},
            {"value": "has space"}, {"value": "a\tb"}, {"value": "a\x00b"},
            {"value": "a\x7fb"}, {"value": "é"}, {"value": "x" * 254},
            {"value": None}, {"value": 123},
            {"category": "missing"}, {"category": True}, {"category": 1},
        ]
        for change in changes:
            with self.subTest(change=change):
                request = self.body(assignments=[{**base, **change}])
                self.assert_error(400, lambda: self.store.save(request))

    def test_assignment_duplicates_use_source_and_value_and_allow_null_category(self):
        assignment = {"source": "site", "value": "example.test", "category": "sites"}
        duplicate = [{**assignment}, {**assignment, "category": None}]
        self.assert_error(400, lambda: self.store.save(self.body(assignments=duplicate)))
        assignments = [
            {**assignment, "category": None},
            {**assignment, "source": "phone", "category": "telephony"},
            {"source": "phone", "value": "+7(123)-456:ext=8", "category": None},
        ]
        self.assertEqual(assignments, self.store.save(self.body(assignments=assignments))["assignments"])

    def test_maximum_counts_and_value_length_are_accepted_then_excess_is_rejected(self):
        categories = [{"id": "c" + str(i), "name": "x" * 62 + f"{i:02d}"} for i in range(32)]
        assignments = [
            {"source": "site", "value": f"{i:03d}" + "x" * 250, "category": "c0"}
            for i in range(576)
        ]
        saved = self.store.save(self.body(categories=categories, assignments=assignments))
        self.assertEqual(32, len(saved["categories"]))
        self.assertEqual(576, len(saved["assignments"]))
        too_many_categories = categories + [{"id": "extra", "name": "Extra"}]
        self.assert_error(400, lambda: self.store.save(self.body(
            1, too_many_categories, assignments,
        )))
        too_many_assignments = assignments + [
            {"source": "phone", "value": "extra", "category": None},
        ]
        self.assert_error(400, lambda: self.store.save(self.body(
            1, categories, too_many_assignments,
        )))
        self.assertEqual(saved, self.store.state())

    def test_corrupt_existing_metadata_fails_closed_without_overwrite(self):
        state = {"schema": 1, "revision": 1, "categories": DEFAULT_CATEGORIES, "assignments": []}
        corrupt = [
            b"{broken", b"\xff", b"null", b"{}",
            json.dumps({**state, "schema": 2}).encode(),
            json.dumps({**state, "revision": True}).encode(),
            json.dumps({**state, "revision": MAX_SAFE_INTEGER + 1}).encode(),
            json.dumps({**state, "categories": [{"id": "Bad", "name": "Bad"}]}).encode(),
            json.dumps({**state, "assignments": [
                {"source": "site", "value": "x", "category": "missing"},
            ]}).encode(),
        ]
        for raw in corrupt:
            with self.subTest(raw=raw):
                self.path.write_bytes(raw)
                self.assert_error(503, self.store.state)
                self.assert_error(503, lambda: self.store.save(self.body()))
                self.assertEqual(raw, self.path.read_bytes())

    def test_stored_raw_byte_limit_includes_json_whitespace(self):
        self.assertEqual(262144, MAX_BODY)
        state = {"schema": 1, "revision": 0, "categories": DEFAULT_CATEGORIES, "assignments": []}
        raw = json.dumps(state).encode("utf-8")
        at_limit = raw + b" " * (MAX_BODY - len(raw))
        self.path.write_bytes(at_limit)
        self.assertEqual(state, self.store.state())
        oversized = at_limit + b" "
        self.path.write_bytes(oversized)
        self.assert_error(503, self.store.state)
        self.assert_error(503, lambda: self.store.save(self.body(categories=[])))
        self.assertEqual(oversized, self.path.read_bytes())

    def test_nonregular_and_symlink_metadata_paths_are_not_followed_or_replaced(self):
        self.path.mkdir()
        self.assert_error(503, self.store.state)
        self.assert_error(503, lambda: self.store.save(self.body(categories=[])))
        self.assertTrue(self.path.is_dir())
        self.path.rmdir()
        target = self.root / "untouched.json"
        raw = b'{"do":"not overwrite"}'
        target.write_bytes(raw)
        self.path.symlink_to(target)
        self.assert_error(503, self.store.state)
        self.assert_error(503, lambda: self.store.save(self.body(categories=[])))
        self.assertTrue(self.path.is_symlink())
        self.assertEqual(raw, target.read_bytes())
        self.path.unlink()
        self.path.symlink_to(self.root / "missing.json")
        self.assert_error(503, self.store.state)
        self.assert_error(503, lambda: self.store.save(self.body(categories=[])))
        self.assertTrue(self.path.is_symlink())
        self.assertFalse((self.root / "missing.json").exists())

    def test_company_identity_cannot_escape_or_alias_its_metadata_filename(self):
        invalid = ("", "a" * 63, "a" * 65, "A" * 64, "g" * 64, "../" + "a" * 64,
                   "a" * 64 + "/x", "a" * 63 + "\n", None, 1)
        for identity in invalid:
            with self.subTest(identity=identity):
                def access():
                    CategoryStore(self.root, identity, self.lock).state()
                self.assert_error(400, access)
        self.assertEqual([], list(self.root.iterdir()))


if __name__ == "__main__":
    unittest.main()
