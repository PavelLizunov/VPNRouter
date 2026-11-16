"""Synthetic worker/receiver contracts; no external service or private fixture."""
import hashlib
import io
import json
import os
import fcntl
from pathlib import Path
import sys
import threading
import time
import tempfile
import unittest
from unittest import mock
import zipfile

sys.path.insert(0, str(Path(__file__).parent))
import analyzer
import receiver


def result(evidence=None):
    evidence = evidence or [{'kind': 'packet', 'field': 'received_utc'}]
    return {'schema': 'corp-diagnosis/1', 'assessments': [{
        'scope': 'client_runtime', 'verdict': 'insufficient_data', 'confidence': 'low',
        'time_scope': 'unknown', 'explanation_ru': 'Нет свежих данных.', 'evidence': evidence}],
        'confirmed': [], 'probable': [], 'unknown': [], 'chronology': [],
        'owner_explanation': 'Для проверки нужны свежие данные.',
        'next_steps_ru': ['Отправьте диагностику после повторной попытки.'],
        'public_analysis': {'version': 1, 'state': 'ready', 'message_ru': 'Нужны свежие данные.'}}


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.root = self.base / 'reports'
        self.root.mkdir()
        self.identity = 'a' * 64
        self.keys = self.base / 'keys.json'
        self.keys.write_text(json.dumps({self.identity: {'enabled': True}}))
        self.key = self.base / 'model.key'
        self.key.write_text('synthetic-provider-key')
        self.prompt = self.base / 'prompt.txt'
        self.prompt.write_text('Synthetic system instruction; no tools.')
        self.config_path = self.base / 'config.json'
        self.config_path.write_text(json.dumps({
            'schema': 1, 'corp_identity_sha256': self.identity, 'keys_file': str(self.keys),
            'model_name': 'synthetic-model', 'model_api_url': 'http://127.0.0.1:9/v1/responses',
            'allow_private_http': True, 'wire_api': 'responses',
            'model_api_key_file': str(self.key), 'prompt_file': str(self.prompt)}))
        self.config = analyzer.load_config(self.config_path, self.root)
        self.receipt = self.write_report()

    def write_report(self, content='2001-01-01T01:00:00Z fatal crash\n  at Synthetic.Frame()', identity=None):
        out = io.BytesIO()
        with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
            z.writestr('README.txt', 'synthetic')
            z.writestr('summary.json', json.dumps({'format': 'vpnrouter-business-support/1',
                          'generatedUtc': receiver.utc_now(), 'device': {'user_name': 'FixtureUser'}}))
            z.writestr('crash-01.txt', content)
        payload = out.getvalue()
        sha = hashlib.sha256(payload).hexdigest()
        identity = identity or self.identity
        report_id = hashlib.sha256((identity + sha).encode()).hexdigest()
        (self.root / (report_id + '.zip')).write_bytes(payload)
        receipt = {'report_id': report_id, 'identity': identity, 'employee': 'private-label',
                   'received_utc': receiver.utc_now(), 'sha256': sha, 'bytes': len(payload),
                   'untouched_extension': {'keep': True}}
        receiver.atomic_json(self.root / (report_id + '.json'), receipt)
        return receipt

    def metadata(self):
        return json.loads((self.root / (self.receipt['report_id'] + '.json')).read_text())

    def test_ready_idempotent_preserves_original_fields_and_retention(self):
        path = self.root / (self.receipt['report_id'] + '.json')
        before = path.stat().st_mtime_ns
        calls = []
        def provider(config, packet):
            calls.append(packet)
            return result(), 'synthetic-model-revision'
        self.assertEqual(analyzer.run_once(self.root, self.config, provider)['state'], 'ready')
        metadata = self.metadata()
        for key, value in self.receipt.items():
            self.assertEqual(metadata[key], value)
        self.assertEqual(path.stat().st_mtime_ns, before)
        self.assertEqual(metadata['analysis'], result()['public_analysis'])
        self.assertEqual(metadata['diagnosis']['provenance']['returned_model'], 'synthetic-model-revision')
        self.assertEqual(analyzer.run_once(self.root, self.config, provider)['state'], 'idle')
        self.assertEqual(len(calls), 1)

    def test_historical_crash_does_not_prove_current_runtime(self):
        packet, refs, names = analyzer.prepare(self.root, self.receipt, self.config)
        self.assertEqual(refs[('crash-01.txt', 1)], 'historical')
        self.assertEqual(refs[('crash-01.txt', 2)], 'historical')
        candidate = result([{'kind': 'log', 'file': 'crash-01.txt', 'line': 1}])
        candidate['assessments'][0].update(verdict='problem_found', time_scope='current')
        with self.assertRaises(analyzer.Invalid):
            analyzer.validate_result(candidate, packet, refs, names)
        candidate['assessments'][0].update(verdict='insufficient_data', time_scope='unknown')
        analyzer.validate_result(candidate, packet, refs, names)

    def test_packet_preserves_empty_and_trailing_blank_source_lines(self):
        for content in ('', '\n', 'first\n\n', 'first\n\n\n'):
            with self.subTest(content=repr(content)):
                receipt = self.write_report(content=content)
                packet, refs, _ = analyzer.prepare(self.root, receipt, self.config)
                entry = next(f for f in packet['files'] if f['file'] == 'crash-01.txt')
                expected = content.splitlines()
                self.assertEqual([line['text'] for line in entry['lines']], expected)
                self.assertEqual([line['line'] for line in entry['lines']], list(range(1, len(expected) + 1)))
                self.assertEqual(len(refs), len(expected))

    def test_invalid_evidence_and_short_output_never_publish(self):
        for candidate in (result([{'kind': 'log', 'file': '../secret', 'line': 1}]), result()):
            if candidate['assessments'][0]['evidence'][0]['kind'] == 'packet':
                candidate['public_analysis']['message_ru'] = 'curl https://example.invalid'
            packet, refs, names = analyzer.prepare(self.root, self.receipt, self.config)
            with self.assertRaises(analyzer.Invalid):
                analyzer.validate_result(candidate, packet, refs, names)

    def test_timeout_is_bounded_and_retries_keep_previous_public_result(self):
        old = result()['public_analysis']
        path = self.root / (self.receipt['report_id'] + '.json')
        receipt = self.metadata()
        receipt['analysis'] = old
        receiver.atomic_json(path, receipt)
        def timeout(*args):
            raise analyzer.Transient()
        for index in range(3):
            outcome = analyzer.run_once(self.root, self.config, timeout)
            metadata = self.metadata()
            self.assertEqual(metadata['analysis'], old)
            metadata['diagnosis']['next_attempt'] = 0
            receiver.update_receipt(self.root, self.receipt['report_id'], metadata)
        self.assertEqual(outcome['state'], 'failed')
        self.assertEqual(analyzer.run_once(self.root, self.config, timeout)['state'], 'idle')

    def test_deleted_and_superseded_jobs_are_not_recreated_or_overwritten(self):
        def supersede(config, packet):
            metadata = self.metadata()
            metadata['diagnosis']['generation'] += 1
            metadata['diagnosis']['state'] = 'pending'
            receiver.update_receipt(self.root, self.receipt['report_id'], metadata)
            return result(), None
        self.assertEqual(analyzer.run_once(self.root, self.config, supersede)['state'], 'discarded')
        self.assertNotIn('analysis', self.metadata())
        def delete(config, packet):
            (self.root / (self.receipt['report_id'] + '.json')).unlink()
            return result(), None
        self.assertEqual(analyzer.run_once(self.root, self.config, delete)['state'], 'discarded')
        self.assertFalse((self.root / (self.receipt['report_id'] + '.json')).exists())

    def test_archive_hash_and_identity_are_checked_before_provider(self):
        called = []
        def provider(*args):
            called.append(True)
            return result(), None
        archive = self.root / (self.receipt['report_id'] + '.zip')
        archive.write_bytes(b'replaced')
        self.assertEqual(analyzer.run_once(self.root, self.config, provider)['state'], 'unavailable')
        self.assertFalse(called)
        self.keys.write_text('{}')
        self.assertEqual(analyzer.run_once(self.root, self.config, provider)['processed'], 0)

    def test_scrub_preserves_stack_lines_versions_and_removes_sensitive_values(self):
        raw = ('2026-11-16T10:00:00Z version 2.50.8.1\n'
               'FixtureUser opened https://user:password@example.invalid/sub\n'
               'address 192.0.2.7\npassword: short\n  continued-secret\n'
               '2026-11-16T10:01:00Z error\n  at Example.Frame()')
        clean = analyzer.scrub(raw, ['FixtureUser'])
        self.assertEqual(len(clean.splitlines()), len(raw.splitlines()))
        self.assertIn('2.50.8.1', clean)
        self.assertIn('at Example.Frame()', clean)
        for secret in ('FixtureUser', 'password@example', '192.0.2.7', 'short', 'continued-secret'):
            self.assertNotIn(secret, clean)

    def test_private_config_and_public_http_rejected(self):
        for url in ('http://example.invalid/v1', 'https://user:secret@example.invalid/v1',
                    'https://example.invalid/v1?key=secret', 'https://example.invalid/v1#fragment'):
            config = json.loads(self.config_path.read_text())
            config['model_api_url'] = url
            self.config_path.write_text(json.dumps(config))
            with self.assertRaises((ValueError, analyzer.Invalid)):
                analyzer.load_config(self.config_path, self.root)
        with self.assertRaises(ValueError):
            receiver.private_file(self.root / (self.receipt['report_id'] + '.json'), self.root)

    def test_missing_provider_visible_and_configuration_recovery_requeues(self):
        self.key.unlink()
        unavailable = analyzer.load_config(self.config_path, self.root)
        self.assertEqual(analyzer.run_once(self.root, unavailable)['state'], 'unavailable')
        self.assertEqual(self.metadata()['diagnosis']['state'], 'unavailable')
        self.key.write_text('synthetic-provider-key')
        available = analyzer.load_config(self.config_path, self.root)
        self.assertEqual(analyzer.run_once(self.root, available, lambda *args: (result(), None))['state'], 'ready')

    def test_process_lock_prevents_parallel_provider_requests(self):
        descriptor = os.open(self.root / '.analyzer.lock', os.O_RDWR | os.O_CREAT, 0o600)
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertEqual(analyzer.run_once(self.root, self.config)['state'], 'busy')
        finally:
            os.close(descriptor)
        self.assertNotIn('diagnosis', self.metadata())

    def test_expired_lease_recovers_and_expired_reports_do_not_run(self):
        metadata = self.metadata()
        metadata['diagnosis'] = {'state': 'running', 'lease_until': 1, 'attempts': 1,
                                'generation': 1, 'revision': 'obsolete'}
        receiver.update_receipt(self.root, self.receipt['report_id'], metadata)
        self.assertEqual(analyzer.run_once(self.root, self.config, lambda *args: (result(), None))['state'], 'ready')
        for suffix in ('.json', '.zip'):
            os.utime(self.root / (self.receipt['report_id'] + suffix), (1, 1))
        self.assertEqual(analyzer.run_once(self.root, self.config)['state'], 'idle')

    def test_clock_observations_are_citable_but_not_current_runtime_proof(self):
        packet, refs, names = analyzer.prepare(self.root, self.receipt, self.config)
        candidate = result([{'kind': 'packet', 'field': 'collector.generatedUtc'},
                            {'kind': 'packet', 'field': 'received_utc'}])
        analyzer.validate_result(candidate, packet, refs, names)
        candidate['assessments'][0].update(time_scope='current', verdict='problem_found')
        with self.assertRaises(analyzer.Invalid):
            analyzer.validate_result(candidate, packet, refs, names)

    def test_responses_reasoning_metadata_allowed_tools_and_redirects_rejected(self):
        class Socket:
            def settimeout(self, seconds):
                pass
        class Response:
            status = 200
            def __init__(self, envelope):
                self.body = analyzer.encode(envelope)
            def read1(self, count):
                value, self.body = self.body[:count], self.body[count:]
                return value
        class Connection:
            sock = Socket()
            def __init__(self, response):
                self.response = response
                self.request_fields = None
            def request(self, *args, **kwargs):
                self.request_fields = kwargs
            def getresponse(self):
                return self.response
            def close(self):
                pass
        envelope = {'model': 'synthetic-returned-model', 'status': 'completed', 'output': [
            {'type': 'reasoning', 'summary': []}, {'type': 'message', 'content': [
                {'type': 'output_text', 'text': json.dumps(result())}]}]}
        connection = Connection(Response(envelope))
        with mock.patch.object(analyzer.http.client, 'HTTPConnection', return_value=connection):
            candidate, model = analyzer.request_model(self.config, {})
        self.assertEqual(candidate, result())
        self.assertEqual(model, 'synthetic-returned-model')
        self.assertFalse(json.loads(connection.request_fields['body'])['store'])
        envelope['output'].insert(0, {'type': 'function_call', 'name': 'untrusted'})
        with mock.patch.object(analyzer.http.client, 'HTTPConnection', return_value=Connection(Response(envelope))):
            with self.assertRaises(analyzer.Invalid):
                analyzer.request_model(self.config, {})
        response = Response({})
        response.status = 302
        with mock.patch.object(analyzer.http.client, 'HTTPConnection', return_value=Connection(response)):
            with self.assertRaises(analyzer.Unavailable):
                analyzer.request_model(self.config, {})

    def test_aliases_preserve_relationships_and_scrub_is_idempotent(self):
        aliases = {}
        first = analyzer.scrub('peer 192.0.2.7 then 192.0.2.8', aliases=aliases)
        second = analyzer.scrub('peer 192.0.2.7', aliases=aliases)
        self.assertEqual(first, 'peer [ip-1] then [ip-2]')
        self.assertEqual(second, 'peer [ip-1]')
        self.assertEqual(analyzer.scrub(first), first)
        self.assertEqual(analyzer.scrub(second), second)

    def test_total_deadline_terminates_provider_child(self):
        config = dict(self.config, timeout_seconds=.05)
        def stalled_provider(*args):
            time.sleep(5)
            return result(), None
        before = time.monotonic()
        with mock.patch.object(analyzer, 'request_model', side_effect=stalled_provider):
            with self.assertRaises(analyzer.Transient):
                analyzer.bounded_request_model(config, {})
        self.assertLess(time.monotonic() - before, 2)
        self.assertFalse(analyzer.multiprocessing.active_children())

    def test_failed_directory_lock_entry_releases_thread_lock(self):
        lock = receiver.MutationLock(self.root)
        with mock.patch.object(receiver.os, 'open', side_effect=OSError('synthetic')):
            with self.assertRaises(OSError):
                with lock:
                    pass
        acquired = threading.Event()
        def acquire():
            with lock:
                acquired.set()
        thread = threading.Thread(target=acquire, daemon=True)
        thread.start()
        thread.join(timeout=1)
        self.assertTrue(acquired.is_set())

    def test_private_unix_and_unc_paths_are_aliased_before_model_input(self):
        paths = ['/root/private-vpn/config.json', r'\\fileserver\profiles\Alice\config.json']
        aliases = {}
        for path in paths:
            clean = analyzer.scrub('Failed loading ' + path, aliases=aliases)
            self.assertNotIn(path, clean)
            self.assertIn('[path-', clean)
            self.assertEqual(analyzer.scrub(clean), clean)

    def test_every_displayed_prose_field_rejects_generated_commands(self):
        packet, refs, names = analyzer.prepare(self.root, self.receipt, self.config)
        for field in ('owner_explanation', 'confirmed', 'probable', 'unknown', 'chronology', 'assessments'):
            candidate = result()
            command = 'Выполните sudo reboot'
            if field == 'owner_explanation':
                candidate[field] = command
            elif field == 'assessments':
                candidate[field][0]['explanation_ru'] = command
            else:
                item = {'statement': command, 'evidence': [{'kind': 'packet', 'field': 'received_utc'}]}
                if field != 'chronology':
                    item['time_scope'] = 'unknown'
                candidate[field] = [item]
            with self.subTest(field=field), self.assertRaises(analyzer.Invalid):
                analyzer.validate_result(candidate, packet, refs, names)

    def test_retained_old_report_cannot_prove_current_runtime(self):
        observed = time.time()
        receipt = self.write_report(content=receiver.utc_now() + ' fatal crash')
        config = dict(self.config, _facts={
            'current_attempt_started_utc': analyzer.dt.datetime.fromtimestamp(observed - 60, analyzer.dt.timezone.utc).isoformat(),
            'current_attempt_ended_utc': analyzer.dt.datetime.fromtimestamp(observed + 60, analyzer.dt.timezone.utc).isoformat()})
        with mock.patch.object(analyzer.time, 'time', return_value=observed + 6 * 86400):
            packet, refs, names = analyzer.prepare(self.root, receipt, config)
        self.assertEqual(refs[('crash-01.txt', 1)], 'historical')
        candidate = result([{'kind': 'log', 'file': 'crash-01.txt', 'line': 1}])
        candidate['assessments'][0].update(verdict='problem_found', time_scope='current')
        with self.assertRaises(analyzer.Invalid):
            analyzer.validate_result(candidate, packet, refs, names)

    def test_receipt_growth_preserves_capacity_reserved_for_streaming_uploads(self):
        store = receiver.Store(self.root, self.keys, '127.0.0.1')
        self.assertTrue(store.reserve(self.identity, receiver.MAX_BODY))
        incoming = self.root / 'incoming-aaaaaaaa'
        incoming.write_bytes(b'partial upload')
        changed = dict(self.receipt, diagnosis={'state': 'pending', 'generation': 1})
        used = sum(p.stat().st_size for p in self.root.iterdir() if p.is_file())
        required = len(json.dumps(changed, ensure_ascii=True).encode())
        before = (self.root / (self.receipt['report_id'] + '.json')).read_bytes()
        try:
            with store.lock, self.assertRaises(OSError):
                receiver.update_receipt(self.root, self.receipt['report_id'], changed,
                                        used + required + receiver.UPLOAD_HEADROOM - 1)
            self.assertEqual((self.root / (self.receipt['report_id'] + '.json')).read_bytes(), before)
            with store.lock:
                receiver.update_receipt(self.root, self.receipt['report_id'], changed,
                                        used + required + receiver.UPLOAD_HEADROOM)
        finally:
            store.release(receiver.MAX_BODY)


if __name__ == '__main__':
    unittest.main()
