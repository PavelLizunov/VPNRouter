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


class InboxTests(unittest.TestCase):
    setUp = ReceiverTests.setUp
    tearDown = ReceiverTests.tearDown
    post = ReceiverTests.post

    def internal(self, path='', method='GET', token=None, body=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request(method, '/internal/corp-diagnostics/reports' + path, body=body,
                               headers={'Authorization': 'Bearer ' + (token or 's' * 32),
                                        'Content-Type': 'application/json'})
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def enable(self):
        self.store.inbox = (hashlib.sha256(self.token.encode()).hexdigest(), 's' * 32)

    def test_capability_is_distinct_from_upload_and_revocation_is_live(self):
        self.assertEqual(self.internal()[0], 404)
        self.enable()
        self.assertEqual(self.internal(token=self.token)[0], 401)
        self.assertEqual(self.internal()[0], 200)
        self.keys.write_text('{}')
        self.assertEqual(self.internal()[0], 401)

    def test_list_detail_retry_are_scoped_bounded_and_preserve_mtime(self):
        self.enable()
        _, public = self.post(report())
        report_id = public['report_id']
        metadata_path = self.store.root / (report_id + '.json')
        original = json.loads(metadata_path.read_text())
        before = metadata_path.stat().st_mtime_ns
        code, listing = self.internal('?limit=1')
        self.assertEqual(code, 200)
        self.assertEqual(len(listing['reports']), 1)
        self.assertEqual(set(listing['reports'][0]), {'report_id', 'received_utc', 'bytes', 'device_label',
                         'analysis_state', 'verdict', 'summary_ru', 'model', 'updated_utc'})
        self.assertNotIn('identity', json.dumps(listing))
        self.assertEqual(self.internal('/' + report_id)[0], 200)
        self.assertEqual(self.internal('/' + 'f' * 64), (404, {'error': 'not found'}))
        self.assertEqual(self.internal('?cursor=' + 'f' * 64)[0], 400)
        self.assertEqual(self.internal('?limit=101')[0], 400)
        self.assertEqual(self.internal('/' + report_id + '/retry', 'POST', body=b'{}')[0], 202)
        self.assertEqual(metadata_path.stat().st_mtime_ns, before)
        changed = json.loads(metadata_path.read_text())
        for key, value in original.items():
            self.assertEqual(changed[key], value)
        changed['diagnosis'].update(state='running', lease_until=time.time() + 300)
        receiver.update_receipt(self.store.root, report_id, changed)
        self.assertEqual(self.internal('/' + report_id + '/retry', 'POST', body=b'{}')[0], 409)
        # Existing upload responses retain their byte-level JSON field contract.
        self.assertEqual(self.post(report())[1], public)

    def test_delete_removes_live_pair_and_returns_actual_freed_bytes(self):
        self.enable()
        _, public = self.post(report())
        report_id = public['report_id']
        untouched = self.store.root / 'keep.txt'
        untouched.write_bytes(b'foreign fixture')
        before = sum(p.stat().st_size for p in self.store.root.iterdir())
        expected = sum((self.store.root / (report_id + extension)).stat().st_size
                       for extension in ('.zip', '.json'))
        self.assertEqual(self.internal('/' + report_id, 'DELETE', body=b'{}'),
                         (200, {'schema': 1, 'state': 'deleted', 'freed_bytes': expected}))
        after = sum(p.stat().st_size for p in self.store.root.iterdir())
        self.assertEqual(before - after, expected)
        self.assertGreater(expected, 0)
        self.assertEqual(list(self.store.root.iterdir()), [untouched])
        self.assertEqual(untouched.read_bytes(), b'foreign fixture')
        self.assertEqual(self.internal('/' + report_id), (404, {'error': 'not found'}))
        self.assertEqual(self.internal('/' + report_id, 'DELETE', body=b'{}'),
                         (404, {'error': 'not found'}))
        self.assertEqual(self.internal()[1]['reports'], [])

    def test_delete_requires_enabled_service_capability_and_is_not_public(self):
        _, public = self.post(report())
        path = '/' + public['report_id']
        self.assertEqual(self.internal(path, 'DELETE', body=b'{}')[0], 404)
        self.enable()
        self.assertEqual(self.internal(path, 'DELETE', token=self.token, body=b'{}')[0], 401)
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request('DELETE', '/api/reports' + path, body=b'{}',
                               headers={'Authorization': 'Bearer ' + self.token})
            response = connection.getresponse()
            self.assertEqual(response.status, 404)
            response.read()
        finally:
            connection.close()
        self.keys.write_text('{}')
        self.assertEqual(self.internal(path, 'DELETE', body=b'{}')[0], 401)
        self.assertTrue((self.store.root / (public['report_id'] + '.zip')).exists())

    def test_delete_foreign_missing_and_expired_are_same_not_found(self):
        self.enable()
        foreign = self.root / 'foreign-client.json'
        receiver.issue_identity(self.keys, 'https://example.invalid/api/reports', foreign, 'foreign')
        token = json.loads(foreign.read_text())['token']
        _, public = self.post(report(), token=token)
        archive = self.store.root / (public['report_id'] + '.zip')
        original = archive.read_bytes()
        expected = (404, {'error': 'not found'})
        self.assertEqual(self.internal('/' + public['report_id'], 'DELETE', body=b'{}'), expected)
        self.assertEqual(self.internal('/' + 'f' * 64, 'DELETE', body=b'{}'), expected)
        _, own = self.post(report())
        load_receipt = receiver.load_receipt
        expired_now = time.time() + receiver.RETENTION + 10
        def expired(root, report_id, identity):
            return load_receipt(root, report_id, identity, now=expired_now)
        with mock.patch.object(receiver, 'load_receipt', side_effect=expired):
            self.assertEqual(self.internal('/' + own['report_id'], 'DELETE', body=b'{}'), expected)
        self.assertEqual(archive.read_bytes(), original)
        self.assertTrue((self.store.root / (own['report_id'] + '.json')).exists())

    def test_delete_rejects_bad_paths_and_bodies_without_changing_pair(self):
        self.enable()
        _, public = self.post(report())
        path = '/' + public['report_id']
        before = {p.name: p.read_bytes() for p in self.store.root.iterdir()}
        for bad in ('/../keys.json', '/%2e%2e/keys.json', path + '/retry',
                    path + '?extra=1', '', '/' + 'A' * 64):
            with self.subTest(path=bad):
                self.assertEqual(self.internal(bad, 'DELETE', body=b'{}')[0], 404)
        for body in (b'', b'[]', b'null', b'{"all":true}', b'{ }'):
            with self.subTest(body=body):
                self.assertEqual(self.internal(path, 'DELETE', body=body)[0], 400)
        self.assertEqual({p.name: p.read_bytes() for p in self.store.root.iterdir()}, before)

    def test_delete_refuses_symlinked_archive_without_touching_target(self):
        self.enable()
        _, public = self.post(report())
        report_id = public['report_id']
        archive = self.store.root / (report_id + '.zip')
        foreign = self.root / 'foreign.zip'
        foreign.write_bytes(archive.read_bytes())
        original = foreign.read_bytes()
        archive.unlink()
        archive.symlink_to(foreign)
        self.assertEqual(self.internal('/' + report_id, 'DELETE', body=b'{}'),
                         (404, {'error': 'not found'}))
        self.assertTrue(archive.is_symlink())
        self.assertEqual(foreign.read_bytes(), original)
        self.assertTrue((self.store.root / (report_id + '.json')).exists())

    def test_delete_partial_io_failure_is_explicit_and_cannot_publish_analysis(self):
        self.enable()
        _, public = self.post(report())
        report_id = public['report_id']
        metadata = self.store.root / (report_id + '.json')
        archive = self.store.root / (report_id + '.zip')
        with mock.patch.object(Path, 'unlink', side_effect=OSError('synthetic unlink failure')):
            self.assertEqual(self.internal('/' + report_id, 'DELETE', body=b'{}'),
                             (503, {'error': 'storage unavailable'}))
        self.assertTrue(archive.exists())
        self.assertTrue(metadata.exists())
        unlink = Path.unlink
        def fail_metadata(path, *args, **kwargs):
            if path == metadata:
                raise OSError('synthetic unlink failure')
            return unlink(path, *args, **kwargs)
        with mock.patch.object(Path, 'unlink', fail_metadata):
            self.assertEqual(self.internal('/' + report_id, 'DELETE', body=b'{}'),
                             (503, {'error': 'storage unavailable'}))
        self.assertFalse(archive.exists())
        self.assertTrue(metadata.exists())
        self.assertIsNone(receiver.load_receipt(self.store.root, report_id, self.store.inbox[0]))
        # A failed second unlink leaves no tombstone or renamed copy; the
        # ordinary retention sweep also clears the remaining bounded receipt.
        self.store.clock = lambda: time.time() + receiver.RETENTION + 10
        self.store.prune()
        self.assertFalse(list(self.store.root.iterdir()))

    def test_foreign_reports_and_missing_archive_indistinguishable(self):
        self.enable()
        foreign = self.root / 'foreign-client.json'
        receiver.issue_identity(self.keys, 'https://example.invalid/api/reports', foreign, 'foreign')
        foreign_token = json.loads(foreign.read_text())['token']
        _, foreign_public = self.post(report(), token=foreign_token)
        self.assertEqual(self.internal('/' + foreign_public['report_id'])[0], 404)
        self.assertEqual(self.internal('?cursor=' + foreign_public['report_id'])[0], 400)
        _, own = self.post(report())
        (self.store.root / (own['report_id'] + '.zip')).unlink()
        self.assertEqual(self.internal('/' + own['report_id'])[0], 404)
        self.assertEqual(self.internal()[1]['reports'], [])


class ArchiveTypeTests(unittest.TestCase):
    def test_symlink_entries_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'fixture.zip'
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('README.txt', 'synthetic')
                archive.writestr('summary.json', '{"format":"vpnrouter-business-support/1"}')
                entry = zipfile.ZipInfo('app-01.txt')
                entry.create_system = 3
                entry.external_attr = 0o120777 << 16
                archive.writestr(entry, '/private/file')
            with self.assertRaises(ValueError):
                receiver.validate_archive(path)


if __name__ == "__main__":
    unittest.main()
