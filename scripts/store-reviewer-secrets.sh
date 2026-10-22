#!/usr/bin/env bash
# Run interactively on mariogk-dev: bash scripts/store-reviewer-secrets.sh
# Requires Python 3, OpenSSL, OCI CLI and the authenticated HOMELAB_BROWSER profile.
# Each explicit run replaces vendor credentials; existing generated tokens stay intact.
set +x
set -euo pipefail
umask 077
ulimit -c 0
if (( $# )); then printf '%s\n' 'Run without arguments; credentials are prompted silently.' >&2; exit 1; fi
exec python3 -I - <<'PY'
import base64
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import subprocess
import sys
import termios

# Public, vendor-issued OCIDs from the regional handoff and existing operator cache.
VAULT = 'ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq'
KEY = 'ocid1.key.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljr2bdwyvlua7oi6gpg7gl72ln6t2l2fb63tl2cvhllvokc4l2afeeq'
COMPARTMENT = 'ocid1.compartment.oc1..aaaaaaaaypdff62jbchkojfli5i22ybzm3qnptwwsbg3aa2t5ali6te52nzq'
PREFIX = 'gru-1-ts-reviewer-'
NAMES = ('github-private-key', 'glm-api-key', 'webhook-secret', 'context-api-token', 'operator-token')


def interrupt(*_):
    raise KeyboardInterrupt


def oci(*args, payload=None):
    command = ['oci', '--config-file', str(Path.home() / '.oci/config'),
               '--cli-rc-file', '/dev/null', '--profile', 'HOMELAB_BROWSER',
               '--auth', 'security_token', '--region', 'sa-saopaulo-1',
               '--connection-timeout', '10', '--read-timeout', '30', '--no-retry',
               '--output', 'json', *args]
    # CLI JSON reads a private pipe; credentials never enter argv or files.
    if payload is not None:
        command += ['--from-json', 'file:///dev/stdin']
    environment = {k: v for k, v in os.environ.items()
                   if not k.startswith('OCI_CLI_') and k not in ('PYTHONINSPECT', 'PYTHONSTARTUP')}
    result = subprocess.run(command, input=json.dumps(payload) if payload is not None else '',
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            text=True, timeout=120, env=environment, check=True)
    return json.loads(result.stdout)


def existing_secrets():
    vault = oci('kms', 'management', 'vault', 'get', '--vault-id', VAULT)['data']
    if vault['compartment-id'] != COMPARTMENT or vault['lifecycle-state'] != 'ACTIVE':
        raise ValueError
    endpoint = vault['management-endpoint']
    if not re.fullmatch(r'https://[a-z0-9-]+-management\.kms\.sa-saopaulo-1\.oraclecloud\.com', endpoint):
        raise ValueError
    key = oci('kms', 'management', 'key', 'get', '--endpoint', endpoint, '--key-id', KEY)['data']
    if key['lifecycle-state'] != 'ENABLED' or key['key-shape']['algorithm'] != 'AES':
        raise ValueError
    entries = oci('vault', 'secret', 'list', '--compartment-id', COMPARTMENT,
                  '--vault-id', VAULT, '--all')['data']
    existing = {}
    for entry in entries:
        name = entry['secret-name']
        if name not in {PREFIX + suffix for suffix in NAMES}:
            continue
        if name in existing or entry['lifecycle-state'] != 'ACTIVE':
            raise ValueError
        response = oci('vault', 'secret', 'get', '--secret-id', entry['id'])
        metadata = response['data']
        if (metadata['vault-id'] != VAULT or metadata['lifecycle-state'] != 'ACTIVE'
                or not metadata.get('current-version-number') or not response.get('etag')):
            raise ValueError
        existing[name] = (entry['id'], response['etag'])
    return existing


def vendor_credentials():
    # Keep echo disabled for the entire multiline paste; getpass per line can drop pasted input.
    with open('/dev/tty', encoding='ascii') as terminal:
        previous = termios.tcgetattr(terminal.fileno())
        hidden = list(previous)
        hidden[3] &= ~(termios.ECHO | termios.ECHONL)
        try:
            termios.tcsetattr(terminal.fileno(), termios.TCSAFLUSH, hidden)
            print('Paste unencrypted GitHub App RSA PEM (hidden), including BEGIN/END lines:',
                  file=sys.stderr, flush=True)
            lines = []
            for _ in range(400):
                line = terminal.readline(8193)
                if not line.endswith('\n') or len(line) > 8192:
                    raise ValueError
                lines.append(line.rstrip('\r\n'))
                if lines[-1] in ('-----END RSA PRIVATE KEY-----', '-----END PRIVATE KEY-----'):
                    break
            else:
                raise ValueError
            if lines[0] not in ('-----BEGIN RSA PRIVATE KEY-----', '-----BEGIN PRIVATE KEY-----'):
                raise ValueError
            pem = '\n'.join(lines) + '\n'
            if len(pem) > 25000:
                raise ValueError
            # Only the public modulus is captured; OpenSSL never outputs the private key.
            checked = subprocess.run(['openssl', 'rsa', '-check', '-noout', '-modulus', '-passin', 'pass:'],
                                     input=pem, text=True, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, timeout=15, check=True)
            modulus = re.search(r'^Modulus=([0-9A-Fa-f]+)$', checked.stdout, re.MULTILINE)
            if modulus is None or int(modulus[1], 16).bit_length() < 2048:
                raise ValueError
            print('GLM API key (hidden):', file=sys.stderr, flush=True)
            line = terminal.readline(8193)
            if not line.endswith('\n') or len(line) > 8192:
                raise ValueError
            api_key = line.rstrip('\r\n')
            if not api_key or any(not 33 <= ord(char) <= 126 for char in api_key):
                raise ValueError
            return pem, api_key
        finally:
            termios.tcsetattr(terminal.fileno(), termios.TCSAFLUSH, previous)
            print(file=sys.stderr, flush=True)


def store(suffix, value, existing):
    name = PREFIX + suffix
    payload = {'secretContentContent': base64.b64encode(value.encode('ascii')).decode('ascii'),
               'secretContentStage': 'CURRENT'}
    if name in existing:
        secret_id, etag = existing[name]
        payload.update(secretId=secret_id, ifMatch=etag)
        operation = 'update-base64'
        args = ['--force']
    else:
        payload.update(compartmentId=COMPARTMENT, vaultId=VAULT, keyId=KEY, secretName=name)
        operation = 'create-base64'
        args = []
    oci('vault', 'secret', operation, *args, '--wait-for-state', 'ACTIVE',
        '--max-wait-seconds', '60', '--wait-interval-seconds', '3', payload=payload)
    print('Stored: ' + name, flush=True)


def main():
    phase = 'prerequisite checks'
    try:
        if any(shutil.which(tool) is None for tool in ('oci', 'openssl')):
            raise ValueError
        for sig in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            signal.signal(sig, interrupt)
        phase = 'Vault/key/secret metadata checks (existing HOMELAB_BROWSER access required)'
        existing = existing_secrets()  # Metadata only; no secret-bundle APIs.
        print('This run replaces both vendor credentials and preserves existing generated tokens.', flush=True)
        phase = 'hidden credential input and local RSA validation (RSA >= 2048 bits required)'
        pem, api_key = vendor_credentials()
        phase = 'vendor credential writes'
        store(NAMES[0], pem, existing)
        store(NAMES[1], api_key, existing)
        phase = 'first-party token creation'
        for suffix in NAMES[2:]:
            if PREFIX + suffix in existing:
                print('Preserved: ' + PREFIX + suffix, flush=True)
            else:
                store(suffix, secrets.token_hex(32), existing)
        return 0
    except (Exception, KeyboardInterrupt):
        print('Stopped during ' + phase + '. No values or OCI errors printed; no automatic retry. '
              'Earlier writes may have succeeded; reruns preserve existing first-party tokens.', file=sys.stderr)
        return 1


sys.exit(main())
PY
