#!/usr/bin/env python3
"""Bounded advisory diagnostics worker. No model output authorizes an action."""
import argparse
import datetime as dt
import fcntl
import hashlib
import http.client
import ipaddress
import json
import multiprocessing
import os
from pathlib import Path
import re
import socket
import stat
import time
import unicodedata
from urllib.parse import urlsplit
import uuid
import zipfile

import receiver

MAX_INPUT = 192 * 1024
MAX_RESULT = 24 * 1024
MAX_PROVIDER = 256 * 1024
SCOPES = {'update_delivery', 'client_runtime', 'tunnel_and_policy', 'evidence_quality'}
VERDICTS = {'problem_found', 'no_problem_evidenced', 'insufficient_data'}
FACTS = {'feed_version', 'feed_source_sha', 'installed_version', 'installed_source_sha',
         'artifact_sha256', 'private_build_version', 'private_build_source_sha',
         'current_attempt_started_utc', 'current_attempt_ended_utc'}
TIMESTAMP = re.compile(r'\b\d{4}-\d{2}-\d{2}[T ][0-9:.]+(?:Z|[+-]\d{2}:?\d{2})?')
SECRET = re.compile(r'(?i)\b[a-z0-9_-]*(?:password|passwd|secret|token|credential|authorization|cookie|uuid|private[_-]?key|api[_-]?key|subscription)[a-z0-9_-]*["\x27]?\s*[:=]')
OPAQUE = re.compile(r'(?<![\w])[A-Za-z0-9+/_=-]{32,}(?![\w])')
URL = re.compile(r'(?i)\b[a-z][a-z0-9+.-]*://[^\s<>"\x27]+')
IP = re.compile(r'(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])|(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.]+')
PATH = re.compile(r'(?i)\b[A-Z]:[\\/][^\r\n\t"<>]*|\\\\[^\s"<>]+|(?<![\w./])/[A-Z0-9._~-][^\s"<>]*')
COMMAND = re.compile(r'(?i)\b(?:sudo|powershell|pwsh|cmd|curl|wget|bash|regedit|msiexec|systemctl|shutdown|reboot)\b|\$\(|&&|\|\|')
DOMAIN = re.compile(r'(?i)\b(?:[a-z0-9-]+\.)+(?:com|net|org|ru|io|local|internal|invalid|lan)\b')


class Invalid(ValueError):
    pass


class Unavailable(Exception):
    pass


class Transient(Exception):
    pass


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8')


def digest(value):
    return hashlib.sha256(value).hexdigest()


def timestamp(value):
    try:
        parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
        return parsed.timestamp() if parsed.tzinfo else None
    except (ValueError, AttributeError, OverflowError):
        return None


def scrub(text, names=(), aliases=None):
    """Mask whole sensitive lines and private blocks while preserving source line count."""
    aliases = {} if aliases is None else aliases
    def alias(kind, raw):
        key = (kind, raw)
        if key not in aliases:
            count = 1 + sum(existing[0] == kind for existing in aliases)
            aliases[key] = '[' + kind + '-' + str(count) + ']'
        return aliases[key]
    lines, private, sensitive_tail = [], False, False
    for line in text.splitlines():
        if '-----BEGIN ' in line and 'PRIVATE KEY' in line:
            private = True
        if TIMESTAMP.search(line):
            sensitive_tail = False
        if SECRET.search(line) or re.search(r'^\s*\{|^\s*\[\s*[\"\x27]', line):
            sensitive_tail = True
        if private or sensitive_tail or re.search(r'(?i)\bBearer\s+\S+', line):
            replacement = '[sensitive content omitted]'
        else:
            replacement = line
            for name in names:
                if name:
                    replacement = re.sub(r'(?<!\w)' + re.escape(name) + r'(?!\w)', '[device-or-user]', replacement, flags=re.I)
            replacement = URL.sub(lambda match: alias('url', match[0]), replacement)
            replacement = PATH.sub(lambda match: alias('path', match[0]), replacement)
            replacement = OPAQUE.sub('[opaque]', replacement)
            # Four-part versions adjacent to version/build labels are evidence, not IPs.
            def hide_ip(match):
                prefix = replacement[max(0, match.start()-32):match.start()]
                if '.' in match[0] and re.search(r'(?i)(?:version|build|v)\s*[:=]?\s*$', prefix):
                    return match[0]
                try:
                    ipaddress.ip_address(match[0])
                    return alias('ip', match[0])
                except ValueError:
                    return match[0]
            replacement = IP.sub(hide_ip, replacement)
            replacement = DOMAIN.sub(lambda match: alias('host', match[0]), replacement)
            # Additional common identity fields and raw configuration are never useful here.
            if re.search(r'(?i)(?:user(?:name)?|computer(?:name)?|device(?:name)?|login)\s*[:=]', replacement):
                replacement = '[identity content omitted]'
            if re.search(r'^\s*\{|^\s*\[\s*[\"\x27]|(?i:outbounds|inbounds|private_config)\s*[:=]', replacement):
                replacement = '[structured content omitted]'
        lines.append(''.join(c for c in replacement if unicodedata.category(c) not in ('Cc', 'Cf', 'Cs')))
        if private and '-----END ' in line and 'PRIVATE KEY' in line:
            private = False
    return '\n'.join(lines)


def load_config(path, root):
    value = json.loads(receiver.private_file(path, root))
    if value.get('schema') != 1 or not re.fullmatch('[0-9a-f]{64}', value.get('corp_identity_sha256', '')):
        raise Invalid('configuration')
    for field in ('keys_file', 'prompt_file'):
        if field not in value:
            raise Invalid('configuration')
        receiver.private_file(value[field], root)
    try:
        key = receiver.private_file(value['model_api_key_file'], root, 512).decode('ascii').strip()
    except OSError:
        key = None
    if key is not None and not re.fullmatch(r'[A-Za-z0-9._~+/=-]{1,512}', key):
        raise Invalid('configuration')
    value['_key'] = key
    prompt = receiver.private_file(value['prompt_file'], root, 32768).decode('utf-8')
    # The deployed private prompt is trusted operator configuration, never ZIP text.
    if not prompt.strip():
        raise Invalid('configuration')
    value['_prompt'] = prompt
    value['_prompt_sha'] = digest(prompt.encode())
    if value.get('wire_api') not in ('responses', 'chat_completions'):
        raise Invalid('configuration')
    if 'thinking_mode' in value and (value['wire_api'] != 'chat_completions' or
                                     value['thinking_mode'] not in ('enabled', 'disabled')):
        raise Invalid('thinking mode')
    if not isinstance(value.get('model_name'), str) or not 1 <= len(value['model_name']) <= 128 or scrub(value['model_name']) != value['model_name']:
        raise Invalid('configuration')
    parsed = urlsplit(value.get('model_api_url', ''))
    if not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.scheme not in ('http', 'https'):
        raise Invalid('configuration')
    if parsed.scheme == 'http':
        try:
            address = ipaddress.ip_address(parsed.hostname)
        except ValueError:
            raise Invalid('configuration') from None
        if value.get('allow_private_http') is not True or not (address.is_private or address.is_loopback):
            raise Invalid('configuration')
    parsed.port  # Validate malformed ports before any job claim.
    value['_url'] = parsed
    for field, default, low, high in (('timeout_seconds', 90, 1, 120), ('lease_seconds', 300, 130, 3600),
                                     ('max_attempts', 3, 1, 10), ('poll_seconds', 15, 1, 3600),
                                     ('retry_seconds', 30, 1, 3600)):
        number = value.get(field, default)
        if type(number) not in (int, float) or not low <= number <= high:
            raise Invalid('configuration')
        value[field] = number
    facts = {}
    if value.get('operator_context_file'):
        context = json.loads(receiver.private_file(value['operator_context_file'], root, 16384))
        for field, item in context.get('trusted_operator_facts', {}).items():
            if field in FACTS and isinstance(item, str) and 0 < len(item) <= 128:
                if field.endswith('_sha') or field.endswith('_sha256'):
                    if not re.fullmatch('[0-9a-f]{40,64}', item):
                        raise Invalid('operator context')
                elif field.endswith('_utc'):
                    if timestamp(item) is None:
                        raise Invalid('operator context')
                elif not re.fullmatch(r'[A-Za-z0-9.+_-]{1,64}', item):
                    raise Invalid('operator context')
                facts[field] = item
    value['_facts'] = facts
    return value


def prepare(root, receipt, config):
    path = Path(root) / (receipt['report_id'] + '.zip')
    with open(path, 'rb', opener=lambda p, f: os.open(p, f | os.O_NOFOLLOW | os.O_NONBLOCK)) as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size != receipt.get('bytes') or info.st_size > receiver.MAX_BODY:
            raise Invalid('archive size')
        sha = hashlib.sha256()
        for chunk in iter(lambda: source.read(65536), b''):
            sha.update(chunk)
        if sha.hexdigest() != receipt.get('sha256'):
            raise Invalid('archive hash')
        # Validate and read the same open inode, avoiding path replacement between checks.
        source.seek(0)
        summary = receiver.validate_archive(source)
        source.seek(0)
        names = list(receiver.client_reported_device(summary).values()) + ([config['_key']] if config['_key'] else [])
        packet = {'schema': 'corp-packet/1', 'report_id': receipt['report_id'],
                  'received_utc': receipt.get('received_utc'), 'input_sha256': receipt['sha256'],
                  'prompt_version': 'corp-diagnosis/1', 'trusted_operator_facts': config['_facts'], 'files': []}
        references, aliases = {}, {}
        generated = timestamp(summary.get('generatedUtc'))
        received = timestamp(receipt.get('received_utc'))
        analyzed_at = time.time()
        attempt = timestamp(config['_facts'].get('current_attempt_started_utc'))
        ended = timestamp(config['_facts'].get('current_attempt_ended_utc'))
        with zipfile.ZipFile(source) as archive:
            for entry in sorted(archive.infolist(), key=lambda e: e.filename):
                if entry.filename == 'README.txt':
                    continue  # Boilerplate never supplies diagnostic evidence.
                if entry.filename == 'summary.json':
                    # Whitelist collector observations; never send arbitrary uploaded JSON.
                    safe = {k: summary[k] for k in ('format', 'generatedUtc', 'collection', 'is64Bit') if k in summary}
                    # JSON has no original line correspondence: expose allowlisted observations.
                    packet['collector'] = {k: scrub(v, names, aliases) if isinstance(v, str) else v
                                           for k, v in safe.items() if isinstance(v, (str, bool))}
                    continue
                content = archive.read(entry).decode('utf-8-sig', errors='strict')
                raw_lines = content.splitlines()
                clean_lines = scrub(content, names, aliases).split('\n') if raw_lines else []
                if len(raw_lines) != len(clean_lines):
                    raise Invalid('line mapping')
                selected = set(range(len(clean_lines))) if len(content.encode()) <= 16000 else set(range(min(40, len(clean_lines))))
                if len(content.encode()) > 16000:
                    selected.update(range(max(0, len(clean_lines)-80), len(clean_lines)))
                    for index, line in enumerate(clean_lines):
                        if re.search(r'(?i)error|fatal|exception|failed|policy|acknowledg|version|startup', line):
                            selected.update(range(max(0, index-4), min(len(clean_lines), index+9)))
                    boundaries = set(range(min(40, len(clean_lines)))) | set(range(max(0, len(clean_lines)-80), len(clean_lines)))
                    selected = boundaries | set(sorted(selected - boundaries)[:230])
                lines, last_time = [], None
                for index, clean in enumerate(clean_lines):
                    match = TIMESTAMP.search(raw_lines[index])
                    if match:
                        last_time = timestamp(match[0])
                    scope = 'unknown'
                    if last_time is not None and received is not None and last_time < max(received, analyzed_at) - 86400:
                        scope = 'historical'
                    if (last_time is not None and attempt is not None and ended is not None and
                            received is not None and generated is not None and
                            analyzed_at - 86400 <= received <= analyzed_at + 300 and
                            abs(generated - received) <= 3600 and attempt <= last_time <= ended and
                            received - 86400 <= attempt <= ended <= received + 300):
                        scope = 'current'
                    if index not in selected:
                        continue
                    if len(clean) > 2000:
                        clean = clean[:2000] + ' [line truncated]'
                    lines.append({'line': index+1, 'text': clean, 'time_scope': scope})
                    references[(entry.filename, index+1)] = scope
                packet['files'].append({'file': entry.filename, 'lines': lines,
                                        'total_lines': len(raw_lines), 'omitted_ranges': omitted(len(raw_lines), selected)})
    if len(encode(packet)) > MAX_INPUT:
        raise Invalid('packet budget')
    return packet, references, names


def omitted(count, selected):
    result, start = [], None
    for index in range(count + 1):
        if index < count and index not in selected:
            if start is None:
                start = index + 1
        elif start is not None:
            result.append([start, index])
            start = None
    return result


def request_model(config, packet):
    if config['_key'] is None:
        raise Unavailable()
    messages = [{'role': 'system', 'content': config['_prompt']},
                {'role': 'user', 'content': encode(packet).decode()}]
    request = {'model': config['model_name']}
    if config['wire_api'] == 'responses':
        request.update(input=messages, store=False, max_output_tokens=6000)
    else:
        request.update(messages=messages, max_tokens=6000, response_format={'type': 'json_object'})
        if 'thinking_mode' in config:
            request['thinking'] = {'type': config['thinking_mode']}
    url = config['_url']
    connection_cls = http.client.HTTPSConnection if url.scheme == 'https' else http.client.HTTPConnection
    connection = connection_cls(url.hostname, url.port, timeout=config['timeout_seconds'])
    deadline = time.monotonic() + config['timeout_seconds']
    try:
        headers = {'Authorization': 'Bearer ' + config['_key'], 'Content-Type': 'application/json',
                   'User-Agent': 'VPNRouter-Corp-Diagnostics/1.0'}
        if url.hostname == 'opencode.ai':
            headers['X-Opencode-Session'] = digest(encode({'report': packet.get('report_id'),
                                                         'input': packet.get('input_sha256')}))
        connection.request('POST', url.path or '/', body=encode(request), headers=headers)
        connection.sock.settimeout(max(.01, deadline - time.monotonic()))
        response = connection.getresponse()
        if response.status in (408, 429) or response.status >= 500:
            raise Transient()
        if response.status != 200:
            raise Unavailable()
        chunks, size = [], 0
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise Transient()
            if connection.sock:
                connection.sock.settimeout(remaining)
            chunk = response.read1(min(65536, MAX_PROVIDER + 1 - size))
            if not chunk:
                break
            size += len(chunk)
            if size > MAX_PROVIDER:
                raise Invalid('provider budget')
            chunks.append(chunk)
        envelope = json.loads(b''.join(chunks))
        returned = envelope.get('model')
        if returned is not None and (not isinstance(returned, str) or len(returned) > 128 or scrub(returned) != returned):
            raise Invalid('returned model')
        if config['wire_api'] == 'responses':
            output = envelope.get('output')
            if (envelope.get('status', 'completed') != 'completed' or not isinstance(output, list) or
                    not 1 <= len(output) <= 8 or any(not isinstance(item, dict) or item.get('type') not in {'message', 'reasoning'} for item in output)):
                raise Invalid('provider output')
            messages = [item for item in output if item['type'] == 'message']
            if len(messages) != 1 or messages[0].get('status', 'completed') != 'completed':
                raise Invalid('provider messages')
            content = messages[0].get('content')
            if not isinstance(content, list) or len(content) != 1 or content[0].get('type') != 'output_text':
                raise Invalid('provider content')
            text = content[0].get('text')
        else:
            choices = envelope.get('choices')
            if not isinstance(choices, list) or len(choices) != 1 or choices[0].get('finish_reason') != 'stop':
                raise Invalid('provider choices')
            message = choices[0]['message']
            if message.get('tool_calls') or message.get('function_call'):
                raise Invalid('provider tools')
            text = message.get('content')
        if not isinstance(text, str) or len(text.encode()) > MAX_RESULT:
            raise Invalid('result budget')
        return json.loads(text), returned
    except (socket.timeout, TimeoutError, ConnectionError, http.client.HTTPException, OSError):
        raise Transient() from None
    finally:
        connection.close()


def _provider_child(channel, config, packet):
    """Only bounded JSON crosses the child boundary; never exceptions or logs."""
    try:
        result, returned = request_model(config, packet)
        envelope = {'state': 'ready', 'result': result, 'returned_model': returned}
        raw = encode(envelope)
        if len(raw) > MAX_RESULT + 2048:
            raw = b'{"state":"unavailable"}'
    except Transient:
        raw = b'{"state":"transient"}'
    except BaseException:
        raw = b'{"state":"unavailable"}'
    try:
        channel.send_bytes(raw)
    finally:
        channel.close()


def bounded_request_model(config, packet):
    # A socket timeout cannot bound libc DNS. A single disposable child can.
    context = multiprocessing.get_context('fork')
    reader, writer = context.Pipe(duplex=False)
    process = context.Process(target=_provider_child, args=(writer, config, packet), daemon=True)
    deadline = time.monotonic() + config['timeout_seconds']
    try:
        process.start()
        writer.close()
        if not reader.poll(max(0, deadline - time.monotonic())):
            raise Transient()
        try:
            # Drain before joining: a pipe-sized result must never block child exit.
            raw = reader.recv_bytes(MAX_RESULT + 2048)
            envelope = json.loads(raw)
        except (EOFError, OSError, ValueError):
            raise Unavailable() from None
        if time.monotonic() > deadline:
            raise Transient()
        if envelope.get('state') == 'transient':
            raise Transient()
        if envelope.get('state') != 'ready':
            raise Unavailable()
        return envelope['result'], envelope['returned_model']
    finally:
        reader.close()
        writer.close()
        if process.pid is not None:
            if process.is_alive():
                process.terminate()
            process.join(timeout=.5)
            if process.is_alive():
                process.kill()
                process.join()


def validate_result(value, packet, refs, names=()):
    if not isinstance(value, dict) or len(encode(value)) > MAX_RESULT:
        raise Invalid('result')
    required = {'schema', 'assessments', 'confirmed', 'probable', 'unknown', 'chronology',
                'owner_explanation', 'next_steps_ru', 'public_analysis'}
    if set(value) != required or value['schema'] != 'corp-diagnosis/1':
        raise Invalid('result fields')

    def prose(text, maximum):
        if (not isinstance(text, str) or not 0 < len(text) <= maximum or
                any(unicodedata.category(c) in ('Cc', 'Cf', 'Cs') for c in text) or
                '<' in text or '>' in text or '`' in text or
                COMMAND.search(text) or scrub(text, names) != text):
            raise Invalid('unsafe prose')
        return text

    def evidence(items):
        if not isinstance(items, list) or not 1 <= len(items) <= 12:
            raise Invalid('evidence')
        scopes = []
        for item in items:
            if not isinstance(item, dict):
                raise Invalid('reference')
            if item.get('kind') == 'log' and set(item) == {'kind', 'file', 'line'}:
                if type(item['line']) is not int or (item['file'], item['line']) not in refs:
                    raise Invalid('log reference')
                scopes.append(refs[(item['file'], item['line'])])
            elif item.get('kind') == 'operator_fact' and set(item) == {'kind', 'field'}:
                prefix = 'trusted_operator_facts.'
                if not isinstance(item['field'], str) or not item['field'].startswith(prefix) or item['field'][len(prefix):] not in packet['trusted_operator_facts']:
                    raise Invalid('operator reference')
                scopes.append('current')
            elif item.get('kind') == 'packet' and set(item) == {'kind', 'field'}:
                fields = {'received_utc', 'report_id', 'input_sha256', 'prompt_version', 'files'}
                fields.update('collector.' + key for key, scalar in packet.get('collector', {}).items()
                              if type(scalar) in (str, bool, int))
                if not isinstance(item['field'], str) or item['field'] not in fields:
                    raise Invalid('packet reference')
                scopes.append('unknown')
            else:
                raise Invalid('reference')
        return scopes

    assessments = value['assessments']
    if not isinstance(assessments, list) or not 1 <= len(assessments) <= 4:
        raise Invalid('assessments')
    seen = set()
    for item in assessments:
        if not isinstance(item, dict) or set(item) != {'scope', 'verdict', 'confidence', 'time_scope', 'explanation_ru', 'evidence'}:
            raise Invalid('assessment fields')
        if item['scope'] not in SCOPES or item['scope'] in seen or item['verdict'] not in VERDICTS or item['confidence'] not in {'high', 'medium', 'low'} or item['time_scope'] not in {'current', 'unknown'}:
            raise Invalid('assessment values')
        seen.add(item['scope'])
        prose(item['explanation_ru'], 2000)
        scopes = evidence(item['evidence'])
        if item['time_scope'] == 'current' and 'current' not in scopes:
            raise Invalid('unsupported current scope')
        if item['scope'] in {'client_runtime', 'tunnel_and_policy'} and item['verdict'] != 'insufficient_data':
            # Operator versions and the receive time cannot prove runtime/tunnel state.
            if item['time_scope'] != 'current' or not any(e.get('kind') == 'log' and refs.get((e['file'], e['line'])) == 'current' for e in item['evidence']):
                raise Invalid('unsupported runtime conclusion')
    for field in ('confirmed', 'probable', 'unknown', 'chronology'):
        if not isinstance(value[field], list) or len(value[field]) > 12:
            raise Invalid('findings')
        for item in value[field]:
            keys = {'statement', 'evidence'} | (set() if field == 'chronology' else {'time_scope'})
            if not isinstance(item, dict) or set(item) != keys:
                raise Invalid('finding fields')
            prose(item['statement'], 1500)
            scopes = evidence(item['evidence'])
            if field != 'chronology':
                if item['time_scope'] not in {'current', 'historical', 'unknown'}:
                    raise Invalid('finding scope')
                if item['time_scope'] in {'current', 'historical'} and item['time_scope'] not in scopes:
                    raise Invalid('finding scope evidence')
    prose(value['owner_explanation'], 2000)
    if not isinstance(value['next_steps_ru'], list) or len(value['next_steps_ru']) > 3:
        raise Invalid('next steps')
    for step in value['next_steps_ru']:
        prose(step, 300)
        if receiver.UNSAFE_ANALYSIS.search(step):
            raise Invalid('unsafe next step')
    short = receiver.public_analysis(value['public_analysis'])
    if short != value['public_analysis'] or short['state'] != 'ready':
        raise Invalid('public analysis')
    for field in ('message_ru', 'message_en'):
        if field in short:
            prose(short[field], 300)
    return value


def run_once(root, config, model_request=bounded_request_model):
    root = Path(root)
    lock = receiver.MutationLock(root)
    if not receiver.enabled_identity(config['keys_file'], config['corp_identity_sha256']):
        return {'state': 'unavailable', 'processed': 0}
    # One bounded, fixed lock file, never report-derived. A second worker never waits.
    descriptor = os.open(root / '.analyzer.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
    try:
        if not stat.S_ISREG(os.fstat(descriptor).st_mode):
            raise Invalid('process lock')
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return {'state': 'busy', 'processed': 0}
        now = time.time()
        claimed = None
        with lock:
            for path in sorted(root.glob('*.json')):
                receipt = receiver.load_receipt(root, path.stem, config['corp_identity_sha256'], now)
                if not receipt:
                    continue
                job = receipt.get('diagnosis') or {}
                revision_fields = {'input': receipt.get('sha256'), 'prompt': config['_prompt_sha'],
                                   'model': config['model_name'], 'url': config['model_api_url'],
                                   'wire': config['wire_api'], 'facts': config['_facts'],
                                   'provider_available': config['_key'] is not None}
                if 'thinking_mode' in config:
                    revision_fields['thinking_mode'] = config['thinking_mode']
                revision = digest(encode(revision_fields))
                state = job.get('state', 'received')
                if state == 'running' and job.get('lease_until', 0) > now:
                    continue
                if job.get('revision') == revision and state in ('ready', 'failed', 'unavailable'):
                    continue
                if job.get('next_attempt', 0) > now:
                    continue
                if job.get('revision') != revision:
                    job.update(attempts=0, generation=job.get('generation', 0) + 1)
                if job.get('attempts', 0) >= config['max_attempts']:
                    job.update(state='failed', lease_until=0, updated_utc=receiver.utc_now())
                    receipt['diagnosis'] = job
                    receiver.update_receipt(root, path.stem, receipt)
                    continue
                job.update(state='running', revision=revision, attempts=job.get('attempts', 0)+1,
                           generation=job.get('generation', 0), run_id=uuid.uuid4().hex,
                           requested_model=config['model_name'], lease_until=now + config['lease_seconds'],
                           updated_utc=receiver.utc_now())
                receipt['diagnosis'] = job
                receiver.update_receipt(root, path.stem, receipt)
                claimed = (receipt, dict(job))
                break
        if claimed is None:
            return {'state': 'idle', 'processed': 0}
        receipt, claim = claimed
        result, returned = None, None
        state = 'unavailable'
        try:
            packet, refs, names = prepare(root, receipt, config)
            candidate, returned = model_request(config, packet)
            try:
                result = validate_result(candidate, packet, refs, names)
            except Invalid:
                if claim.get('result') is not None or claim['attempts'] >= min(2, config['max_attempts']):
                    raise
                state = 'pending'
            else:
                state = 'ready'
        except Transient:
            state = 'pending' if claim['attempts'] < config['max_attempts'] else 'failed'
        except (Invalid, ValueError, TypeError, KeyError, AttributeError, UnicodeError, zipfile.BadZipFile, EOFError, RecursionError):
            state = 'unavailable'
        except (Unavailable, OSError):
            state = 'unavailable'
        with lock:
            latest = receiver.load_receipt(root, receipt['report_id'], config['corp_identity_sha256'])
            if latest is None or not receiver.enabled_identity(config['keys_file'], config['corp_identity_sha256']):
                return {'state': 'discarded', 'processed': 1}
            job = latest.get('diagnosis') or {}
            if job.get('generation') != claim['generation'] or job.get('run_id') != claim['run_id']:
                return {'state': 'discarded', 'processed': 1}
            completed = receiver.utc_now()
            job.update(state=state, lease_until=0, next_attempt=time.time()+config['retry_seconds'] if state == 'pending' else 0,
                       updated_utc=completed)
            job['last_attempt_provenance'] = {'requested_model': config['model_name'], 'returned_model': returned,
                                 'prompt_sha256': config['_prompt_sha'], 'input_sha256': receipt['sha256'],
                                 'run_id': claim['run_id'], 'started_utc': claim['updated_utc'],
                                 'completed_utc': completed, 'prompt_version': 'corp-diagnosis/1', 'status': state}
            if result is not None or job.get('result') is None:
                job['provenance'] = dict(job['last_attempt_provenance'])
            if result is not None:
                job['result'] = result
                latest['analysis'] = result['public_analysis']
            latest['diagnosis'] = job
            try:
                receiver.update_receipt(root, receipt['report_id'], latest)
            except ValueError:
                job.pop('result', None)
                previous = (receipt.get('diagnosis') or {}).get('result')
                if previous is not None:
                    job['result'] = previous
                if 'analysis' in receipt:
                    latest['analysis'] = receipt['analysis']
                else:
                    latest.pop('analysis', None)
                state = 'unavailable'
                job['state'] = state
                job['last_attempt_provenance']['status'] = state
                previous_provenance = (receipt.get('diagnosis') or {}).get('provenance')
                job['provenance'] = (previous_provenance if previous is not None and previous_provenance
                                     else dict(job['last_attempt_provenance']))
                receiver.update_receipt(root, receipt['report_id'], latest)
        return {'state': state, 'processed': 1}
    finally:
        os.close(descriptor)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=('run-once', 'serve'))
    parser.add_argument('--reports', required=True)
    parser.add_argument('--config', required=True)
    args = parser.parse_args()
    while True:
        try:
            config = load_config(args.config, args.reports)
            summary = run_once(args.reports, config)
        except (OSError, ValueError, TypeError, KeyError, AttributeError, UnicodeError, RecursionError):
            summary = {'state': 'unavailable', 'processed': 0}
            config = {'poll_seconds': 15}
        print(json.dumps(summary), flush=True)
        if args.action == 'run-once':
            break
        time.sleep(config['poll_seconds'])


if __name__ == '__main__':
    main()
