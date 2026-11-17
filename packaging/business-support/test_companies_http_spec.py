"""Independent HTTP acceptance tests for the frozen Corp company contract."""
import hashlib
import http.client
import importlib.util
import json
from datetime import datetime, timezone
from pathlib import Path
import tempfile
import threading
import unittest

spec = importlib.util.spec_from_file_location("receiver", Path(__file__).with_name("receiver.py"))
receiver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(receiver)

SERVICE = "synthetic-service-token-0123456789"
LEGACY = "synthetic-legacy-upload-token-0123456789"
ENDPOINT = "https://support.example.invalid/api/reports"
REPORTS = "/internal/corp-diagnostics/reports"
PROVISION = "/internal/corp-companies/provision"


class CompaniesHttpSpec(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.reports = self.root / "reports"
        self.reports.mkdir()
        self.keys = self.root / "keys.json"
        self.service_file = self.root / "service-token"
        self.config = self.root / "inbox.json"
        self.private_write(self.keys, json.dumps({self.digest(LEGACY): {"label": "legacy", "enabled": True}}))
        self.private_write(self.service_file, SERVICE)
        self.write_config(False)
        self.start()

    def tearDown(self):
        self.stop()
        self.temp.cleanup()

    @staticmethod
    def digest(token):
        return hashlib.sha256(token.encode()).hexdigest()

    @staticmethod
    def private_write(path, data):
        path.write_text(data, encoding="utf-8")
        path.chmod(0o600)

    def write_config(self, strict):
        self.private_write(self.config, json.dumps({
            "schema": 1, "corp_identity_sha256": self.digest(LEGACY),
            "service_token_file": str(self.service_file), "legacy_policy_id": "legacy.policy",
            "upload_endpoint": ENDPOINT, "require_company_header": strict,
        }))

    def start(self):
        self.store = receiver.Store(self.reports, self.keys, trusted_proxy="127.0.0.1", inbox_config=self.config)
        self.server = receiver.Receiver(("127.0.0.1", 0), self.store)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def stop(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)

    def request(self, method, path, *, company=None, body=None, token=SERVICE, extra=()):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.putrequest(method, path)
            connection.putheader("Authorization", "Bearer " + token)
            if company is not None:
                connection.putheader("X-Corp-Company", company)
            for name, value in extra:
                connection.putheader(name, value)
            if body is not None:
                connection.putheader("Content-Type", "application/json")
                connection.putheader("Content-Length", str(len(body)))
            connection.endheaders(body)
            response = connection.getresponse()
            data = response.read()
            return response.status, json.loads(data) if data else None
        finally:
            connection.close()

    def provision(self, policy, label="Company"):
        body = json.dumps({"policy_id": policy, "label": label}).encode()
        status, data = self.request("POST", PROVISION, body=body)
        self.assertIn(status, (200, 201))
        self.assertEqual(data["schema"], 1)
        self.assertEqual(data["policy_id"], policy)
        self.assertEqual(data["support"]["endpoint"], ENDPOINT)
        self.assertTrue(data["support"]["token"])
        return data

    def receipt(self, token, report_id):
        archive = b"synthetic report archive"
        (self.reports / (report_id + ".zip")).write_bytes(archive)
        (self.reports / (report_id + ".json")).write_text(json.dumps({
            "identity": self.digest(token), "report_id": report_id,
            "received_utc": datetime.now(timezone.utc).isoformat(),
            "bytes": len(archive), "diagnosis": {"state": "received"},
        }), encoding="utf-8")

    def test_provision_opaque_ids_is_idempotent_across_restart(self):
        first = self.provision("a.policy_1-2")
        second = self.provision("B" * 64)
        self.assertNotEqual(first["support"]["token"], second["support"]["token"])
        self.assertEqual(self.provision("a.policy_1-2"), first)
        self.stop()
        self.start()
        self.assertEqual(self.provision("a.policy_1-2"), first)
        self.assertEqual(self.provision("B" * 64), second)

    def test_authentication_company_headers_and_unknown_never_fall_back(self):
        self.provision("company.a")
        body = json.dumps({"policy_id": "company.b", "label": "B"}).encode()
        self.assertEqual(self.request("POST", PROVISION, body=body, token="wrong")[0], 401)
        for policy in ("", "x" * 65, "contains space", "bad/slash"):
            status, _ = self.request("POST", PROVISION, body=json.dumps({"policy_id": policy, "label": "B"}).encode())
            self.assertTrue(400 <= status < 500)
        self.assertEqual(self.request("GET", REPORTS, company="company.a", token="wrong")[0], 401)
        for company in ("unknown", "bad space"):
            self.assertTrue(400 <= self.request("GET", REPORTS, company=company)[0] < 500)
        status, _ = self.request("GET", REPORTS, company="company.a", extra=(("X-Corp-Company", "legacy.policy"),))
        self.assertTrue(400 <= status < 500)
        status, _ = self.request("GET", REPORTS, company="company.a", extra=(("Authorization", "Bearer " + SERVICE),))
        self.assertTrue(400 <= status < 500)
        self.assertEqual(self.request("GET", REPORTS)[0], 200)

    def test_disabled_root_service_identity_blocks_all_companies_and_provision(self):
        self.provision("company.a")
        keys = json.loads(self.keys.read_text())
        keys[self.digest(LEGACY)]["enabled"] = False
        self.private_write(self.keys, json.dumps(keys))
        self.assertEqual(self.request("GET", REPORTS, company="company.a")[0], 401)
        body = json.dumps({"policy_id": "company.b", "label": "B"}).encode()
        self.assertEqual(self.request("POST", PROVISION, body=body)[0], 401)

    def test_strict_header_gate_keeps_provision_available(self):
        self.stop()
        self.write_config(True)
        self.start()
        self.assertEqual(self.request("GET", REPORTS)[0], 400)
        self.provision("strict.company")
        self.assertEqual(self.request("GET", REPORTS, company="strict.company")[0], 200)

    def category(self, company, action, value):
        body = json.dumps(value, ensure_ascii=False).encode()
        return self.request("POST", "/internal/corp-categories/" + action, company=company, body=body,
                            extra=(("X-Corp-Expected-Length", str(len(body))),))

    def test_category_state_cas_and_assignments_are_company_scoped(self):
        self.provision("company.a")
        self.provision("company.b")
        status, original = self.category("company.a", "state", {})
        self.assertEqual(status, 200)
        self.assertEqual(original["revision"], 0)
        categories = original["categories"] + [{"id": "office", "name": "Работа"}]
        body = {"expected_revision": 0, "categories": categories,
                "assignments": [{"source": "site", "value": "example.invalid", "category": "office"}]}
        status, saved = self.category("company.a", "save", body)
        self.assertEqual(status, 200)
        self.assertEqual(saved["revision"], 1)
        self.assertEqual(self.category("company.a", "save", body)[0], 409)
        status, untouched = self.category("company.b", "state", {})
        self.assertEqual(status, 200)
        self.assertEqual(untouched, original)
        self.assertEqual(self.category("unknown.company", "save", body)[0], 404)
        self.assertEqual(self.category("company.a", "state", {})[1], saved)

    def test_reports_list_and_detail_are_owned_and_foreign_mutations_are_hidden(self):
        a = self.provision("company.a")["support"]["token"]
        b = self.provision("company.b")["support"]["token"]
        aid, bid = "a" * 64, "b" * 64
        self.receipt(a, aid)
        self.receipt(b, bid)
        for company, own, foreign in (("company.a", aid, bid), ("company.b", bid, aid)):
            status, listing = self.request("GET", REPORTS, company=company)
            self.assertEqual(status, 200)
            ids = {entry["report_id"] for entry in listing["reports"]}
            self.assertIn(own, ids)
            self.assertNotIn(foreign, ids)
            self.assertEqual(self.request("GET", REPORTS + "/" + own, company=company)[0], 200)
            self.assertEqual(self.request("GET", REPORTS + "/" + foreign, company=company)[0], 404)
            self.assertEqual(self.request("POST", REPORTS + "/" + foreign + "/retry", company=company, body=b"{}")[0], 404)
            self.assertEqual(self.request("DELETE", REPORTS + "/" + foreign, company=company, body=b"{}")[0], 404)
        self.assertTrue((self.reports / (aid + ".zip")).exists())
        self.assertTrue((self.reports / (bid + ".zip")).exists())


if __name__ == "__main__":
    unittest.main()
