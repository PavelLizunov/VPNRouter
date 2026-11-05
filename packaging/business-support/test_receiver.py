import hashlib
import http.client
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest import mock
import zipfile

spec = importlib.util.spec_from_file_location("receiver", Path(__file__).with_name("receiver.py"))
receiver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(receiver)


def report(extra=None, summary=None):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("README.txt", "synthetic support report")
        archive.writestr("summary.json", json.dumps(summary or {"format": "vpnrouter-business-support/1"}))
        archive.writestr("app-01.txt", "VPN connected; synthetic fixture only")
        if extra:
            for name, value in extra.items():
                archive.writestr(name, value)
    return output.getvalue()


class DeviceMetadataTests(unittest.TestCase):
    def test_optional_or_malformed_device_objects_are_omitted(self):
        self.assertEqual(receiver.client_reported_device({}), {})
        for device in (None, "fixture-pc", [], 42, True, {}):
            with self.subTest(device=device):
                self.assertEqual(receiver.client_reported_device({"device": device}), {})

    def test_names_are_allowlisted_bounded_and_reject_control_or_malformed_data(self):
        invalid = (None, 42, True, [], {}, "", " \t ", "x" * 129,
                   "line\nname", "nul\x00name", "bidi\u202ename", "tag\U000e0001name",
                   "unpaired\ud800", "\U0001f600" * 65)
        for value in invalid:
            with self.subTest(value=value):
                self.assertEqual(receiver.client_reported_device({"device": {
                    "computer_name": value, "user_name": "valid-user", "identity": "ignored"
                }}), {"user_name": "valid-user"})
                self.assertEqual(receiver.client_reported_device({"device": {
                    "computer_name": "valid-pc", "user_name": value
                }}), {"computer_name": "valid-pc"})
        self.assertEqual(receiver.client_reported_device({"device": {
            "computer_name": "x" * 128, "user_name": "\U0001f600" * 64,
            "employee": "ignored"
        }}), {"computer_name": "x" * 128, "user_name": "\U0001f600" * 64})


class ReceiverTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.keys = self.root / "keys.json"
        self.client = self.root / "client.json"
        receiver.issue_identity(self.keys, "https://example.invalid/api/reports", self.client, "fixture")
        self.token = json.loads(self.client.read_text())["token"]
        self.store = receiver.Store(self.root / "reports", self.keys, "127.0.0.1")
        self.server = receiver.Receiver(("127.0.0.1", 0), self.store)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)
        self.temporary.cleanup()

    def post(self, body, token=None, path="/api/reports", headers=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            fields = {"Authorization": "Bearer " + (token or self.token), "Content-Type": "application/zip"}
            fields.update(headers or {})
            connection.request("POST", path, body=body, headers=fields)
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_valid_report_is_private_persisted_before_receipt_and_deduplicated(self):
        payload = report()
        status, receipt = self.post(payload)
        self.assertEqual(status, 201)
        self.assertTrue(receipt["stored"])
        digest = hashlib.sha256(payload).hexdigest()
        identity = hashlib.sha256(self.token.encode()).hexdigest()
        report_id = hashlib.sha256((identity + digest).encode()).hexdigest()
        self.assertEqual(receipt["report_id"], report_id)
        stored = self.store.root / (report_id + ".zip")
        self.assertEqual(stored.read_bytes(), payload)
        metadata = json.loads((self.store.root / (report_id + ".json")).read_text())
        self.assertEqual(metadata["employee"], "fixture")
        self.assertEqual(metadata["identity"], identity)
        self.assertNotIn("client_reported_device", metadata)
        self.assertEqual(set(receipt), {"stored", "report_id"})
        self.assertNotIn(self.token, json.dumps(metadata))
        self.assertEqual(stored.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.post(payload)[0], 200)
        self.assertEqual(len(list(self.store.root.glob("*.zip"))), 1)
        self.assertEqual(self.store.reserved, 0)
        self.assertFalse(self.store.active)
        self.assertFalse(list(self.store.root.glob("incoming-*")))

    def test_device_names_are_private_receipt_metadata_and_duplicate_keeps_receipt(self):
        payload = report(summary={"format": "vpnrouter-business-support/1", "device": {
            "computer_name": "  Fixture Workstation  ", "user_name": "fixture-\u0438\u043c\u044f"
        }})
        status, public_receipt = self.post(payload)
        self.assertEqual(status, 201)
        self.assertEqual(set(public_receipt), {"stored", "report_id"})
        metadata_path = self.store.root / (public_receipt["report_id"] + ".json")
        original_metadata = metadata_path.read_bytes()
        metadata = json.loads(original_metadata)
        self.assertEqual(metadata["client_reported_device"], {
            "computer_name": "Fixture Workstation", "user_name": "fixture-\u0438\u043c\u044f"
        })
        self.assertEqual(metadata["employee"], "fixture")
        self.assertEqual(metadata["identity"], hashlib.sha256(self.token.encode()).hexdigest())
        self.assertEqual(self.post(payload), (200, public_receipt))
        self.assertEqual(metadata_path.read_bytes(), original_metadata)
        self.assertEqual(len(list(self.store.root.glob("*.zip"))), 1)
        self.assertEqual(self.store.reserved, 0)

    def test_invalid_optional_device_fields_do_not_reject_legacy_report_format(self):
        status, receipt = self.post(report(summary={"format": "vpnrouter-business-support/1", "device": {
            "computer_name": ["malformed"], "user_name": "line\nuser", "extra": "ignored"
        }}))
        self.assertEqual(status, 201)
        metadata = json.loads((self.store.root / (receipt["report_id"] + ".json")).read_text())
        self.assertNotIn("client_reported_device", metadata)
        self.assertEqual(set(receipt), {"stored", "report_id"})

    def test_client_device_cannot_override_authenticated_employee_identity_or_paths(self):
        owner_file = self.root / "owner-file.txt"
        owner_file.write_text("preserve owner file")
        payload = report(summary={
            "format": "vpnrouter-business-support/1", "employee": "spoofed-employee",
            "identity": "0" * 64, "report_id": "../owner-file.txt", "path": "../owner-file.txt",
            "device": {"computer_name": "../owner-file.txt", "user_name": "spoofed-client-user",
                       "employee": "spoofed-employee", "identity": "0" * 64,
                       "report_id": "../owner-file.txt", "path": "../owner-file.txt"}
        })
        status, receipt = self.post(payload)
        self.assertEqual(status, 201)
        identity = hashlib.sha256(self.token.encode()).hexdigest()
        digest = hashlib.sha256(payload).hexdigest()
        expected_id = hashlib.sha256((identity + digest).encode()).hexdigest()
        self.assertEqual(receipt, {"stored": True, "report_id": expected_id})
        metadata = json.loads((self.store.root / (expected_id + ".json")).read_text())
        self.assertEqual(metadata["employee"], "fixture")
        self.assertEqual(metadata["identity"], identity)
        self.assertEqual(metadata["report_id"], expected_id)
        self.assertEqual(metadata["sha256"], digest)
        self.assertEqual(metadata["client_reported_device"], {
            "computer_name": "../owner-file.txt", "user_name": "spoofed-client-user"
        })
        self.assertNotIn("spoofed-employee", json.dumps(metadata))
        self.assertEqual(owner_file.read_text(), "preserve owner file")
        self.assertEqual({p.name for p in self.store.root.iterdir()}, {expected_id + ".zip", expected_id + ".json"})
        self.assertEqual((self.store.root / (expected_id + ".zip")).read_bytes(), payload)

    def test_credentials_are_hashed_revocable_and_never_in_receipt(self):
        self.assertNotIn(self.token, self.keys.read_text())
        self.assertEqual(self.client.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.post(report(), token="invalid")[0], 401)
        self.keys.write_text("{}")
        self.assertEqual(self.post(report())[0], 401)
        self.assertFalse(list(self.store.root.glob("*.zip")))

    def test_equal_archives_from_different_employees_have_distinct_private_attribution(self):
        payload = report()
        _, first = self.post(payload)
        second_client = self.root / "other-client.json"
        receiver.issue_identity(self.keys, "https://example.invalid/api/reports", second_client, "other-employee")
        token = json.loads(second_client.read_text())["token"]
        status, second = self.post(payload, token=token)
        self.assertEqual(status, 201)
        self.assertNotEqual(first["report_id"], second["report_id"])
        metadata = json.loads((self.store.root / (second["report_id"] + ".json")).read_text())
        self.assertEqual(metadata["employee"], "other-employee")
        self.assertNotIn(token, json.dumps(metadata))

    def test_reports_have_no_public_download_endpoint(self):
        _, receipt = self.post(report())
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request("GET", "/" + receipt["report_id"] + ".zip")
            response = connection.getresponse()
            self.assertEqual(response.status, 404)
            self.assertNotIn(b"synthetic support report", response.read())
        finally:
            connection.close()

    def test_invalid_archives_paths_and_manifests_are_refused(self):
        for payload in (b"not-a-zip", report({"../config.yaml": "sensitive"}),
                        report({"raw-config.json": "sensitive"}),
                        report(summary={"format": "other-format"})):
            self.assertEqual(self.post(payload)[0], 400)
        self.assertFalse(list(self.store.root.glob("*.zip")))
        self.assertFalse(list(self.store.root.glob("incoming-*")))
        self.assertEqual(self.store.reserved, 0)

    def test_oversized_compressed_entry_is_refused(self):
        self.assertEqual(self.post(report({"crash-01.txt": "x" * (2 * 1024 * 1024 + 1)}))[0], 400)

    def test_size_type_quota_and_rate_limits_are_enforced(self):
        self.assertEqual(self.post(b"x", headers={"Content-Length": str(receiver.MAX_BODY + 1)})[0], 413)
        self.assertEqual(self.post(b"x", headers={"Content-Type": "text/plain"})[0], 415)
        self.store.quota = 1
        self.assertEqual(self.post(report())[0], 429)
        self.store.quota = receiver.QUOTA
        key = self.store.identity("Bearer " + self.token)[0]
        self.store.attempts[key] = (__import__("time").time(), 10)
        self.assertEqual(self.post(report())[0], 429)
        self.assertEqual(self.store.reserved, 0)

    def test_direct_backend_access_and_other_routes_are_refused(self):
        self.store.proxy = "192.0.2.1"
        self.assertEqual(self.post(report())[0], 403)
        self.assertEqual(self.post(report(), path="/unexpected")[0], 404)

    def test_issuance_preserves_existing_identities_on_failure_and_concurrency(self):
        original = self.keys.read_bytes()
        with self.assertRaises(FileExistsError):
            receiver.issue_identity(self.keys, "https://example.invalid/api/reports", self.client, "second")
        self.assertEqual(self.keys.read_bytes(), original)
        with mock.patch.object(receiver, "atomic_json", side_effect=OSError("synthetic write failure")):
            with self.assertRaises(OSError):
                receiver.issue_identity(self.keys, "https://example.invalid/api/reports", self.root / "failed.json", "failed")
        self.assertEqual(self.keys.read_bytes(), original)
        self.assertFalse((self.root / "failed.json").exists())
        errors = []
        def issue(index):
            try:
                receiver.issue_identity(self.keys, "https://example.invalid/api/reports", self.root / f"client-{index}.json", f"employee-{index}")
            except Exception as error:
                errors.append(type(error).__name__)
        workers = [threading.Thread(target=issue, args=(i,)) for i in range(8)]
        for worker in workers:
            worker.start()
        for worker in workers:
            worker.join(timeout=3)
        self.assertFalse(errors)
        self.assertEqual(len(json.loads(self.keys.read_text())), 9)
        with self.assertRaises(ValueError):
            receiver.issue_identity(self.keys, "https://example.invalid/api/reports", self.keys, "overlap")

    def test_idle_sweep_and_restart_remove_only_owned_expired_reports_and_orphans(self):
        old_report = self.store.root / ("a" * 64 + ".zip")
        old_meta = old_report.with_suffix(".json")
        for path in (old_report, old_meta):
            path.write_bytes(b"old")
            __import__("os").utime(path, (1, 1))
        untouched = self.store.root / "owner-file.txt"
        untouched.write_text("keep")
        # service_actions runs even when no client uploads anything.
        self.store.next_prune = 0
        self.server.service_actions()
        self.assertFalse(old_report.exists())
        self.assertFalse(old_meta.exists())
        self.assertTrue(untouched.exists())
        orphan = self.store.root / "incoming-12345678"
        orphan.write_bytes(b"interrupted upload")
        fresh = receiver.Store(self.store.root, self.keys, "127.0.0.1")
        self.assertFalse(orphan.exists())
        self.assertTrue(untouched.exists())
        self.assertEqual(fresh.reserved, 0)

    def test_absolute_deadline_releases_slow_upload_resources(self):
        self.server.upload_timeout = 0.15
        connection = __import__("socket").create_connection(self.server.server_address, timeout=2)
        try:
            headers = ("POST /api/reports HTTP/1.1\r\nHost: fixture\r\nAuthorization: Bearer " + self.token +
                       "\r\nContent-Type: application/zip\r\nContent-Length: 1000\r\n\r\n")
            connection.sendall(headers.encode() + b"x")
            for _ in range(8):
                time.sleep(0.04)
                try:
                    connection.sendall(b"x")
                except OSError:
                    break
            with connection.makefile("rb") as response:
                self.assertIn(b"408", response.read())
        finally:
            connection.close()
        self.assertEqual(self.store.reserved, 0)
        self.assertFalse(self.store.active)
        self.assertFalse(list(self.store.root.glob("incoming-*")))


if __name__ == "__main__":
    unittest.main()
