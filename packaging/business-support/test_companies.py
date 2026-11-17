"""Focused tests for durable company credential bindings."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("companies", Path(__file__).with_name("companies.py"))
companies = importlib.util.module_from_spec(spec)
spec.loader.exec_module(companies)

A = "00000000-0000-4000-8000-000000000001"
B = "00000000-0000-4000-8000-000000000002"


class CompanyRegistryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.keys = Path(self.temp.name) / "keys.json"
        self.legacy = hashlib.sha256(b"old credential").hexdigest()
        self.keys.write_text(json.dumps({self.legacy: {"label": "old", "enabled": True}}))
        self.endpoint = "https://example.invalid/api/reports"
        self.registry = companies.Companies(self.keys, self.legacy, A, self.endpoint)

    def test_bootstrap_preserves_legacy_and_fails_without_full_token(self):
        self.assertEqual(self.registry.resolve(A), self.legacy)
        before = self.keys.read_bytes()
        with self.assertRaises(ValueError):
            self.registry.provision(A, "old")
        self.assertEqual(self.keys.read_bytes(), before)

    def test_repeat_restart_and_distinct_identity(self):
        result = self.registry.provision(B, "new")
        restarted = companies.Companies(self.keys, self.legacy, A, self.endpoint)
        self.assertEqual(result, restarted.provision(B, "different label"))
        identity = hashlib.sha256(result["support"]["token"].encode()).hexdigest()
        self.assertNotEqual(identity, self.legacy)
        self.assertEqual(restarted.resolve(B), identity)
        self.assertTrue(json.loads(self.keys.read_text())[identity]["enabled"])

    def test_uncertain_key_write_retains_credential_without_recreating_key(self):
        original_write = companies._write
        def fail_key(fd, name, value):
            if name == self.keys.name:
                raise OSError("simulated interruption")
            return original_write(fd, name, value)
        with mock.patch.object(companies, "_write", side_effect=fail_key):
            with self.assertRaises(OSError):
                self.registry.provision(B, "new")
        registry_path = self.keys.with_name(self.keys.name + ".companies.json")
        saved = json.loads(registry_path.read_text())[B]["token"]
        with self.assertRaises(ValueError):
            self.registry.provision(B, "new")
        self.assertEqual(json.loads(registry_path.read_text())[B]["token"], saved)
        self.assertNotIn(hashlib.sha256(saved.encode()).hexdigest(), json.loads(self.keys.read_text()))

    def test_completed_removed_key_is_not_recreated(self):
        result = self.registry.provision(B, "new")
        identity = hashlib.sha256(result["support"]["token"].encode()).hexdigest()
        keys = json.loads(self.keys.read_text())
        del keys[identity]
        self.keys.write_text(json.dumps(keys))
        with self.assertRaises(ValueError):
            self.registry.provision(B, "new")
        self.assertNotIn(identity, json.loads(self.keys.read_text()))

    def test_existing_opaque_policy_id_is_supported(self):
        policy = "old_Policy-identifier.0123456789"
        result = self.registry.provision(policy, "existing wire identity")
        self.assertEqual(result["policy_id"], policy)
        self.assertEqual(self.registry.resolve(policy), hashlib.sha256(result["support"]["token"].encode()).hexdigest())

    def test_rejects_invalid_input_and_symlink_registry(self):
        with self.assertRaises(ValueError):
            self.registry.provision("../invalid/company", "new")
        with self.assertRaises(ValueError):
            companies.endpoint_value("https://example.invalid/path?token=secret")
        path = self.keys.with_name(self.keys.name + ".companies.json")
        path.unlink()
        path.symlink_to(self.keys)
        with self.assertRaises(OSError):
            self.registry.resolve(A)


if __name__ == "__main__":
    unittest.main()
