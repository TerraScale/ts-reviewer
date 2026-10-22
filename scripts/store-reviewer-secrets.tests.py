#!/usr/bin/env python3
"""Offline contracts for the private Vault provisioning script; no OCI calls."""
import ast
import base64
import contextlib
import io
import json
from pathlib import Path
import subprocess
import types
import unittest
from unittest.mock import patch


SOURCE = Path(__file__).with_name('store-reviewer-secrets.sh').read_text()
BODY = SOURCE.split("<<'PY'\n", 1)[1].rsplit('\nPY', 1)[0]
TREE = ast.parse(BODY)
# Load definitions only: executing the shell script or its entry point would prompt/write.
assert ast.unparse(TREE.body[-1]) == "sys.exit(main())"
TREE.body.pop()


class VaultContracts(unittest.TestCase):
    def setUp(self):
        self.module = types.ModuleType('private_vault_script')
        exec(compile(TREE, '<vault-script>', 'exec'), self.module.__dict__)
        self.m = self.module
        self.writes = []
        self.existing = {}
        self.failure = None

    def cli(self, command, **kwargs):
        self.assertEqual(kwargs['stderr'], subprocess.DEVNULL)
        self.assertTrue(kwargs['check'])
        self.assertNotIn('OCI_CLI_DEBUG', kwargs['env'])
        if '--from-json' in command:
            self.assertEqual(command[-2:], ['--from-json', 'file:///dev/stdin'])
            payload = json.loads(kwargs['input'])
            value = base64.b64decode(payload['secretContentContent']).decode('ascii')
            self.assertNotIn(value, command)
            self.assertNotIn(payload['secretContentContent'], command)
            if self.failure == len(self.writes):
                raise subprocess.CalledProcessError(1, command, output=value, stderr=value)
            self.writes.append((command, payload, value))
            data = {}
        elif 'list' in command:
            data = [{'secret-name': name, 'id': pair[0], 'lifecycle-state': 'ACTIVE'}
                    for name, pair in self.existing.items()]
        elif '--secret-id' in command:
            secret_id = command[command.index('--secret-id') + 1]
            etag = next(pair[1] for pair in self.existing.values() if pair[0] == secret_id)
            return types.SimpleNamespace(stdout=json.dumps({'etag': etag, 'data': {
                'vault-id': self.m.VAULT, 'lifecycle-state': 'ACTIVE', 'current-version-number': 1}}))
        elif 'vault' in command:
            data = {'compartment-id': self.m.COMPARTMENT, 'lifecycle-state': 'ACTIVE',
                    'management-endpoint': 'https://example-management.kms.sa-saopaulo-1.oraclecloud.com'}
        else:
            data = {'lifecycle-state': 'ENABLED', 'key-shape': {'algorithm': 'AES'}}
        return types.SimpleNamespace(stdout=json.dumps({'data': data}))

    def run_main(self):
        out, err = io.StringIO(), io.StringIO()
        with patch.object(self.m.subprocess, 'run', side_effect=self.cli), \
                patch.object(self.m.shutil, 'which', return_value='/offline/tool'), \
                patch.object(self.m.signal, 'signal'), \
                patch.object(self.m, 'vendor_credentials', return_value=('PRIVATE-PEM', 'API-KEY')), \
                patch.dict(self.m.os.environ, {'OCI_CLI_DEBUG': 'true'}), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            status = self.m.main()
        return status, out.getvalue(), err.getvalue()

    def test_creates_five_named_secrets_using_stdin(self):
        status, _, err = self.run_main()
        self.assertEqual((status, err), (0, ''))
        self.assertEqual([p['secretName'] for _, p, _ in self.writes],
                         [self.m.PREFIX + suffix for suffix in self.m.NAMES])
        self.assertEqual([v for _, _, v in self.writes[:2]], ['PRIVATE-PEM', 'API-KEY'])
        tokens = [v for _, _, v in self.writes[2:]]
        self.assertEqual(len(set(tokens)), 3)
        for token in tokens:
            self.assertRegex(token, r'^[0-9a-f]{64}$')
        for command, payload, _ in self.writes:
            self.assertIn('create-base64', command)
            self.assertEqual(payload['vaultId'], self.m.VAULT)
            self.assertEqual(payload['keyId'], self.m.KEY)
            self.assertEqual(payload['compartmentId'], self.m.COMPARTMENT)
            self.assertEqual(payload['secretContentStage'], 'CURRENT')

    def test_rerun_preserves_tokens_and_updates_vendors_with_etags(self):
        self.existing = {self.m.PREFIX + suffix: ('id-' + suffix, 'etag-' + suffix)
                         for suffix in self.m.NAMES}
        with patch.object(self.m.secrets, 'token_hex', side_effect=AssertionError('regenerated')):
            status, out, err = self.run_main()
        self.assertEqual((status, err), (0, ''))
        self.assertEqual(len(self.writes), 2)
        for (command, payload, _), suffix in zip(self.writes, self.m.NAMES):
            self.assertIn('update-base64', command)
            self.assertEqual(payload['secretId'], 'id-' + suffix)
            self.assertEqual(payload['ifMatch'], 'etag-' + suffix)
        for suffix in self.m.NAMES[2:]:
            self.assertIn('Preserved: ' + self.m.PREFIX + suffix, out)

    def test_partial_failure_does_not_print_credentials_or_oci_errors(self):
        self.failure = 1
        status, out, err = self.run_main()
        self.assertEqual(status, 1)
        self.assertEqual(len(self.writes), 1)
        self.assertIn('Earlier writes may have succeeded', err)
        for value in ('PRIVATE-PEM', 'API-KEY', 'CalledProcessError', 'Traceback'):
            self.assertNotIn(value, out + err)

    def test_rejects_weak_rsa_and_blank_api_key_and_restores_terminal(self):
        for bits, key in ((1024, 'vendor-key'), (2048, '')):
            with self.subTest(bits=bits, key=key):
                terminal = io.StringIO('-----BEGIN PRIVATE KEY-----\nfixture\n-----END PRIVATE KEY-----\n' + key + '\n')
                terminal.fileno = lambda: 123
                attrs = [0, 0, 0, 0, 0, 0, []]
                with patch('builtins.open', return_value=terminal), \
                        patch.object(self.m.termios, 'tcgetattr', return_value=attrs), \
                        patch.object(self.m.termios, 'tcsetattr') as restore, \
                        patch.object(self.m.subprocess, 'run', return_value=types.SimpleNamespace(
                            stdout='Modulus=' + format(1 << (bits - 1), 'x'))), \
                        contextlib.redirect_stderr(io.StringIO()):
                    with self.assertRaises(ValueError):
                        self.m.vendor_credentials()
                self.assertEqual(restore.call_args.args, (123, self.m.termios.TCSAFLUSH, attrs))


if __name__ == '__main__':
    unittest.main()
