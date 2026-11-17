"""Bounded enrollment orchestration around the identity-specific analyzer."""
import bisect
import fcntl
import json
import os
from pathlib import Path
import re
import stat

from companies import Companies
import receiver


def _regular(path):
    descriptor = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
    if not stat.S_ISREG(os.fstat(descriptor).st_mode):
        os.close(descriptor)
        raise ValueError('regular enrollment file required')
    return descriptor


def run_once(root, config, run_one) -> dict:
    if 'company_prompt_file' not in config:
        return run_one(root, config)
    descriptor = None
    cursor_fd = None
    try:
        root = Path(root)
        descriptor = _regular(root / '.company-analysis.lock')
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return {'state': 'busy', 'processed': 0}
        legacy = config['corp_identity_sha256']
        identities = Companies(config['keys_file'], legacy).active_identities()
        if receiver.enabled_identity(config['keys_file'], legacy):
            identities = sorted(set(identities) | {legacy})
        if not identities:
            return {'state': 'idle', 'processed': 0}
        cursor_path = root / '.company-analysis.cursor'
        cursor_fd = _regular(cursor_path)
        raw = os.read(cursor_fd, 1025)
        cursor = ''
        if raw:
            value = json.loads(raw)
            if (len(raw) > 1024 or not isinstance(value, dict) or set(value) != {'identity'} or
                    not isinstance(value['identity'], str) or not re.fullmatch('[0-9a-f]{64}', value['identity'])):
                raise ValueError('invalid enrollment cursor')
            cursor = value['identity']
        start = bisect.bisect(identities, cursor)
        identities = identities[start:] + identities[:start]
        for identity in identities:
            selected = config
            if identity != legacy:
                selected = dict(config)
                selected['corp_identity_sha256'] = identity
                selected['prompt_file'] = config['company_prompt_file']
                selected['_prompt'] = config['_company_prompt']
                selected['_prompt_sha'] = config['_company_prompt_sha']
                selected['_facts'] = {}
                selected.pop('operator_context_file', None)
            summary = run_one(root, selected)
            if summary.get('processed') == 1:
                receiver.atomic_json(cursor_path, {'identity': identity})
                return {'state': summary['state'], 'processed': 1}
            if summary.get('state') == 'busy':
                return {'state': 'busy', 'processed': 0}
        return {'state': 'idle', 'processed': 0}
    except (OSError, ValueError, TypeError, KeyError, AttributeError, UnicodeError, RecursionError):
        return {'state': 'unavailable', 'processed': 0}
    finally:
        if cursor_fd is not None:
            os.close(cursor_fd)
        if descriptor is not None:
            os.close(descriptor)
