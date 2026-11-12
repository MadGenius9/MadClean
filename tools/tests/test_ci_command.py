import contextlib
import importlib.util
import io
from pathlib import Path
import json
import os
import sys
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('ci_command',Path(__file__).resolve().parents[1]/'ci_command.py')
c=importlib.util.module_from_spec(spec); spec.loader.exec_module(c)
class CommandCaptureTests(unittest.TestCase):
    def test_nonzero_status_and_stderr_are_retained_without_secret_values(self):
        with tempfile.TemporaryDirectory(prefix='MadCleanCICommand-') as directory:
            root=Path(directory); env=dict(os.environ,GITHUB_SHA='a'*40,GITHUB_RUN_ID='100',GITHUB_RUN_ATTEMPT='1')
            console=io.StringIO()
            with contextlib.redirect_stdout(console):
                status=c.run('build',[sys.executable,'-c','import sys; print("error TEST-ONLY token=private-fixture",file=sys.stderr); sys.exit(7)'],root,env)
            self.assertEqual(7,status)
            log=(root/'artifacts/ci-logs/build.log').read_text()
            self.assertIn('error TEST-ONLY',log); self.assertNotIn('private-fixture',log+console.getvalue())
            metadata=json.loads((root/'artifacts/ci-logs/build.json').read_text())
            self.assertEqual(7,metadata['exitCode']); self.assertEqual('a'*40,metadata['sourceCommit'])
    def test_multiline_and_token_secrets_never_reach_console_or_uploaded_capture(self):
        payload = 'error before\n-----BEGIN PRIVATE KEY-----\nsecret-line\n-----END PRIVATE KEY-----\n--password secret-argument\n"password": "secret-json"\nghp_testsecretfixture123456\nhttps://example.com/error?opaque=secret-query\n'
        with tempfile.TemporaryDirectory(prefix='MadCleanCICommand-') as directory:
            root=Path(directory); console=io.StringIO()
            with contextlib.redirect_stdout(console):
                c.run('build',[sys.executable,'-c','print('+repr(payload)+')'],root,dict(os.environ))
            retained=(root/'artifacts/ci-logs/build.log').read_text()+console.getvalue()
            for secret in ('secret-line','secret-argument','secret-json','testsecretfixture','secret-query'):
                self.assertNotIn(secret,retained)
            self.assertIn('error before',retained)
    def test_capture_is_bounded_while_the_command_can_finish(self):
        with tempfile.TemporaryDirectory(prefix='MadCleanCICommand-') as directory:
            root=Path(directory)
            with contextlib.redirect_stdout(io.StringIO()): status=c.run('build',[sys.executable,'-c','print("x"*300000)'],root,dict(os.environ))
            self.assertEqual(0,status); self.assertLessEqual((root/'artifacts/ci-logs/build.log').stat().st_size,c.MAX_LOG)
            self.assertTrue(json.loads((root/'artifacts/ci-logs/build.json').read_text())['truncated'])
    def test_shortcut_control_stage_retains_its_independent_compiler_outcome(self):
        with tempfile.TemporaryDirectory(prefix='MadCleanControlCapture-') as directory:
            root=Path(directory)
            with contextlib.redirect_stdout(io.StringIO()):
                status=c.run('shortcut-control',[sys.executable,'-c','print("TEST-ONLY compiler failure"); raise SystemExit(9)'],root,dict(os.environ))
            metadata=json.loads((root/'artifacts/ci-logs/shortcut-control.json').read_text())
            self.assertEqual(9,status)
            self.assertEqual('shortcut-control',metadata['label'])
            self.assertEqual(9,metadata['exitCode'])
            self.assertFalse((root/'artifacts/ci-logs/native-abi.json').exists())
    def test_sdk_version_and_resolver_failure_survive_capture_and_diagnostic_projection(self):
        for code in (0, 7):
            with self.subTest(code=code), tempfile.TemporaryDirectory(prefix='MadCleanSdkCapture-') as directory:
                root=Path(directory)
                env=dict(os.environ,GITHUB_SHA='a'*40,GITHUB_RUN_ID='100',GITHUB_RUN_ATTEMPT='1')
                with contextlib.redirect_stdout(io.StringIO()):
                    status=c.run('sdk',[sys.executable,'-c','print("10.0.403"); print("token=fixture-secret"); raise SystemExit('+str(code)+')'],root,env)
                result=c._scrubber.collect(root/'artifacts/ci-logs',('a'*40,'100',1),'success')
                self.assertEqual(code,status)
                self.assertEqual('sdk',result['stages'][0]['label'])
                self.assertEqual(code,result['stages'][0]['exitCode'])
                self.assertIn('10.0.403',result['stages'][0]['excerpt'])
                self.assertNotIn('fixture-secret',json.dumps(result))

    def test_journal_compiler_failure_has_its_own_source_bound_diagnostic(self):
        with tempfile.TemporaryDirectory(prefix='MadCleanJournalAbiCapture-') as directory:
            root=Path(directory)
            env=dict(os.environ,GITHUB_SHA='a'*40,GITHUB_RUN_ID='100',GITHUB_RUN_ATTEMPT='1')
            with contextlib.redirect_stdout(io.StringIO()):
                status=c.run('journal-native-abi',[sys.executable,'-c','print("error SDK declarations unavailable"); raise SystemExit(4)'],root,env)
            result=c._scrubber.collect(root/'artifacts/ci-logs',('a'*40,'100',1),'success')
            self.assertEqual(4,status)
            self.assertEqual('journal-native-abi',result['stages'][0]['label'])
            self.assertEqual(4,result['stages'][0]['exitCode'])
            self.assertIn('SDK declarations unavailable',result['stages'][0]['excerpt'])
            self.assertFalse((root/'artifacts/ci-logs/native-abi.json').exists())

    def test_non_catalogued_label_and_executable_are_not_run(self):
        with self.assertRaises(ValueError): c.run('../outside',[sys.executable,'-c','raise Exception()'],Path('/unused'),{})
        with self.assertRaises(ValueError): c.run('build',['unsupported-program'],Path('/unused'),{})
    def test_url_authorization_and_query_credentials_are_scrubbed(self):
        self.assertNotIn('private',c.scrub('https://user:private@example.com/a?access_token=private Authorization: Bearer private'))
if __name__=='__main__': unittest.main()
