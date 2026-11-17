"""Spec-derived enrollment checks using synthetic receipts and no network."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).parent))
import analyzer
import company_analysis
import companies
import receiver


def diagnosis():
    return {'schema': 'corp-diagnosis/1', 'assessments': [{
        'scope': 'client_runtime', 'verdict': 'insufficient_data', 'confidence': 'low',
        'time_scope': 'unknown', 'explanation_ru': 'Нет свежих данных.',
        'evidence': [{'kind': 'packet', 'field': 'received_utc'}]}],
        'confirmed': [], 'probable': [], 'unknown': [], 'chronology': [],
        'owner_explanation': 'Для проверки нужны свежие данные.',
        'next_steps_ru': ['Отправьте диагностику после повторной попытки.'],
        'public_analysis': {'version': 1, 'state': 'ready', 'message_ru': 'Нужны свежие данные.'}}


class CompanyEnrollmentTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name)
        self.root = self.base / 'reports'
        self.root.mkdir()
        self.legacy = 'a' * 64
        self.keys = self.base / 'keys.json'
        receiver.atomic_json(self.keys, {self.legacy: {'enabled': True}})
        self.prompt = self.private('legacy.txt', 'LEGACY PRIVATE PROMPT')
        self.generic = self.private('company.txt', 'GENERIC COMPANY PROMPT')
        self.facts = self.private('facts.json', json.dumps({
            'trusted_operator_facts': {'feed_version': '9.8.7'}}))
        self.key = self.private('model.key', 'synthetic-provider-key')
        self.config_path = self.base / 'config.json'
        self.settings = {
            'schema': 1, 'corp_identity_sha256': self.legacy, 'keys_file': str(self.keys),
            'model_name': 'synthetic-model', 'model_api_url': 'http://127.0.0.1:9/v1/responses',
            'allow_private_http': True, 'wire_api': 'responses',
            'model_api_key_file': str(self.key), 'prompt_file': str(self.prompt),
            'operator_context_file': str(self.facts), 'company_prompt_file': str(self.generic)}
        self.selected = []
        self.packets = []
        self.serial = 0

    def private(self, name, value):
        path = self.base / name
        path.write_text(value)
        path.chmod(0o600)
        return path

    def config(self):
        receiver.atomic_json(self.config_path, self.settings)
        return analyzer.load_config(self.config_path, self.root)

    def report(self, identity):
        self.serial += 1
        output = io.BytesIO()
        with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
            archive.writestr('README.txt', 'Synthetic company report')
            archive.writestr('summary.json', json.dumps({
                'format': 'vpnrouter-business-support/1', 'generatedUtc': receiver.utc_now()}))
            archive.writestr('app-01.txt', 'Synthetic report number %s' % self.serial)
        payload = output.getvalue()
        digest = hashlib.sha256(payload).hexdigest()
        report_id = hashlib.sha256((identity + digest).encode()).hexdigest()
        (self.root / (report_id + '.zip')).write_bytes(payload)
        path = self.root / (report_id + '.json')
        receiver.atomic_json(path, {'report_id': report_id, 'identity': identity,
            'received_utc': receiver.utc_now(), 'sha256': digest, 'bytes': len(payload),
            'employee': 'synthetic employee', 'untouched_extension': {'keep': True}})
        return path

    def companies(self):
        return companies.Companies(self.keys, self.legacy,
                                   endpoint='https://example.invalid/api/reports')

    def provision(self, policy):
        support = self.companies().provision(policy, policy)['support']
        return hashlib.sha256(support['token'].encode('ascii')).hexdigest()

    def provider(self, config, packet):
        self.packets.append((config.copy(), packet))
        return diagnosis(), 'synthetic-model-revision'

    def run_one(self, root, config):
        self.selected.append(config.copy())
        return analyzer.run_once(root, config, self.provider)

    def test_legacy_retains_context_and_company_uses_only_generic_context(self):
        company = self.provision('synthetic-company-one')
        self.provision('synthetic-company-two')
        self.report(self.legacy)
        self.report(company)
        config = self.config()
        self.assertEqual(config['_company_prompt'], self.generic.read_text())
        self.assertEqual(config['_company_prompt_sha'],
                         hashlib.sha256(self.generic.read_bytes()).hexdigest())
        for _ in range(3):
            company_analysis.run_once(self.root, config, self.run_one)
        by_identity = {c['corp_identity_sha256']: c for c, _ in self.packets}
        self.assertEqual(set(by_identity), {self.legacy, company})
        legacy = by_identity[self.legacy]
        self.assertEqual(legacy['_facts'], {'feed_version': '9.8.7'})
        for field in ('_prompt', '_facts', 'prompt_file', 'operator_context_file'):
            self.assertEqual(legacy[field], config[field])
        generic = by_identity[company]
        self.assertEqual(generic['_prompt'], config['_company_prompt'])
        self.assertFalse(generic['_facts'])
        self.assertNotIn('operator_context_file', generic)
        self.assertEqual(Path(generic['prompt_file']), self.generic)
        packet_text = json.dumps(next(p for c, p in self.packets
                                      if c['corp_identity_sha256'] == company))
        self.assertNotIn('LEGACY PRIVATE', packet_text)
        self.assertEqual(next(p for c, p in self.packets
                              if c['corp_identity_sha256'] == company)['trusted_operator_facts'], {})

    def test_optional_prompt_absence_preserves_legacy_only_and_ready_report(self):
        company = self.provision('synthetic-company-one')
        self.provision('synthetic-company-two')
        self.settings.pop('company_prompt_file')
        self.report(company)
        ready = self.report(self.legacy)
        config = self.config()
        self.assertEqual(company_analysis.run_once(self.root, config, self.run_one)['processed'], 1)
        original = ready.read_bytes()
        for _ in range(2):
            self.assertEqual(company_analysis.run_once(self.root, config, self.run_one)['processed'], 0)
        self.assertEqual(ready.read_bytes(), original)
        self.assertEqual(len(self.packets), 1)
        self.assertEqual(self.packets[0][0]['corp_identity_sha256'], self.legacy)

    def test_registry_is_reread_and_inactive_or_foreign_keys_are_excluded(self):
        active = self.provision('synthetic-company-one')
        disabled = self.provision('synthetic-company-two')
        pending, foreign = 'b' * 64, 'c' * 64
        keys = json.loads(self.keys.read_text())
        keys[disabled]['enabled'] = False
        keys[pending] = {'enabled': True}
        keys[foreign] = {'enabled': True}
        receiver.atomic_json(self.keys, keys)
        registry_path = self.keys.with_name(self.keys.name + '.companies.json')
        registry = json.loads(registry_path.read_text())
        registry['synthetic-pending'] = {'identity': pending, 'token': None, 'pending': True}
        receiver.atomic_json(registry_path, registry)
        self.assertEqual(self.companies().active_identities(), [active])
        config = self.config()
        for identity in (active, disabled, pending, foreign):
            self.report(identity)
        for _ in range(3):
            company_analysis.run_once(self.root, config, self.run_one)
        self.assertEqual({c['corp_identity_sha256'] for c, _ in self.packets}, {active})
        later = self.provision('synthetic-company-later')
        self.report(later)
        for _ in range(3):
            company_analysis.run_once(self.root, config, self.run_one)
        self.assertEqual({c['corp_identity_sha256'] for c, _ in self.packets}, {active, later})
        self.assertEqual(self.companies().active_identities(), sorted([active, later]))

    def test_continuous_legacy_backlog_does_not_starve_company_next_call(self):
        company = self.provision('synthetic-company-one')
        self.provision('synthetic-company-two')
        config = self.config()
        self.report(self.legacy)
        self.assertEqual(company_analysis.run_once(self.root, config, self.run_one)['processed'], 1)
        self.assertEqual(self.packets[-1][0]['corp_identity_sha256'], self.legacy)
        self.report(self.legacy)
        self.report(company)
        self.assertEqual(company_analysis.run_once(self.root, config, self.run_one)['processed'], 1)
        self.assertEqual(self.packets[-1][0]['corp_identity_sha256'], company)

    def test_busy_orchestration_returns_without_overlapping_provider_calls(self):
        company = self.provision('synthetic-company-one')
        self.provision('synthetic-company-two')
        self.report(self.legacy)
        self.report(company)
        config = self.config()
        entered, release, finished = threading.Event(), threading.Event(), threading.Event()
        outcomes, errors, provider_calls = [], [], []

        def provider(selected, packet):
            provider_calls.append(selected['corp_identity_sha256'])
            entered.set()
            if not release.wait(5):
                raise RuntimeError('test provider barrier timed out')
            return diagnosis(), 'synthetic-model-revision'

        def run_one(root, selected):
            return analyzer.run_once(root, selected, provider)

        def first():
            try:
                outcomes.append(company_analysis.run_once(self.root, config, run_one))
            except BaseException as error:
                errors.append(error)

        def second():
            try:
                outcomes.append(company_analysis.run_once(self.root, config, run_one))
            except BaseException as error:
                errors.append(error)
            finally:
                finished.set()

        worker = threading.Thread(target=first, daemon=True)
        contender = threading.Thread(target=second, daemon=True)
        worker.start()
        try:
            self.assertTrue(entered.wait(3), 'first provider must reach barrier')
            contender.start()
            self.assertTrue(finished.wait(1), 'busy orchestration must return promptly')
            self.assertEqual(errors, [])
            self.assertEqual(outcomes, [{'state': 'busy', 'processed': 0}])
            self.assertEqual(len(provider_calls), 1)
        finally:
            release.set()
            worker.join(5)
            if contender.ident is not None:
                contender.join(5)
        self.assertFalse(worker.is_alive())
        self.assertFalse(contender.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(len(provider_calls), 1)
        self.assertEqual(sorted(result['processed'] for result in outcomes), [0, 1])


if __name__ == '__main__':
    unittest.main()
