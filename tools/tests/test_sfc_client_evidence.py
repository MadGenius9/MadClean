"""Synthetic lifecycle evidence only; no SFC client, Windows file, UI or network access."""
import copy
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('sfc_client_publisher', Path(__file__).resolve().parents[1] / 'publish_windows_verification.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
c = p.sfc_client_evidence


class SfcClientEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.artifact = self.root / 'artifact'
        self.artifact.mkdir()
        self.context = ('a' * 40, '123', 1)
        now = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=1)
        self.report = {name: True for name in c.FACTS}
        self.report.update(formatVersion=1, sourceCommit=self.context[0], runId='123', runAttempt=1,
            startedUtc=(now - dt.timedelta(seconds=60)).isoformat(), observedUtc=now.isoformat(),
            state='passed', stage='Completed', productionExecutionEnabled=False,
            resultState='Available', integrity='Unknown', clientExitCode=0,
            identityCheckedUtc=(now - dt.timedelta(seconds=50)).isoformat(),
            identityExpiresUtc=(now + dt.timedelta(seconds=70)).isoformat(),
            attemptedUtc=(now - dt.timedelta(seconds=45)).isoformat(),
            clientStartedUtc=(now - dt.timedelta(seconds=40)).isoformat(),
            clientExitedUtc=(now - dt.timedelta(seconds=5)).isoformat(),
            completedUtc=(now - dt.timedelta(seconds=3)).isoformat(),
            providerPending=False, requiresRetention=False)
        self.save()

    def save(self):
        (self.artifact / 'report.json').write_text(json.dumps(self.report))

    def validate(self):
        return c.validate_sfc_client_report(self.report, self.context)

    def collect(self):
        return c.collect(self.artifact, self.context, p.read_json, p.safe_path)

    def prepare(self, outcome='success', downloaded='success', passive=True):
        environment = {'GITHUB_REPOSITORY': p.REPOSITORY, 'GITHUB_REF': p.SOURCE_REF,
            'GITHUB_EVENT_NAME': 'push', 'GITHUB_SHA': self.context[0], 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1'}
        with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
             patch.object(p, 'validate_report', return_value=({'passed': True}, {}) if passive else (None, {})), \
             patch.object(p, 'core_test_summary', return_value={'passed': True}), \
             patch.object(p.ci_diagnostics, 'collect', return_value=None):
            return p.prepare(self.root, self.root, 'success', 'success', environment,
                sfc_client_directory=self.artifact, sfc_client_download_result=downloaded, sfc_client_result=outcome)

    def test_qualified_lifecycle_keeps_integrity_unknown(self):
        value = self.collect()
        self.assertEqual(('passed', 0, 'Unknown'), (value['state'], value['clientExitCode'], value['integrity']))
        _, summary, files = self.prepare()
        self.assertTrue(summary['sfcClientLifecycleQualified'])
        self.assertIn('sfc-client.json', files)
        self.assertNotIn('healthy', summary)

    def test_every_actual_pass_fact_and_time_is_required(self):
        for name in c.PASS_FACTS | c.TIMES:
            with self.subTest(name=name):
                changed = copy.deepcopy(self.report)
                changed[name] = None
                with self.assertRaises(ValueError): c.validate_sfc_client_report(changed, self.context)
        for name, wrong in [('providerPending', True), ('requiresRetention', True), ('clientExitCode', 1),
                            ('productionExecutionEnabled', True), ('integrity', 'Healthy'), ('stage', 'Client')]:
            with self.subTest(name=name):
                changed = copy.deepcopy(self.report); changed[name] = wrong
                with self.assertRaises(ValueError): c.validate_sfc_client_report(changed, self.context)

    def test_nonzero_actual_exit_is_retained_without_qualification_or_health_claim(self):
        self.report.update(state='inconclusive', clientExitCode=23)
        self.save()
        self.assertEqual(23, self.collect()['clientExitCode'])
        _, summary, files = self.prepare()
        self.assertFalse(summary['sfcClientLifecycleQualified'])
        self.assertEqual('Unknown', json.loads(files['sfc-client.json'])['integrity'])

    def test_missing_actual_exit_time_can_remain_inconclusive_after_real_cleanup(self):
        self.report.update(state='inconclusive', resultState='Unknown', clientExitedUtc=None)
        self.assertEqual('Unknown', self.validate()['resultState'])

    def test_expired_actual_start_is_historical_only_never_a_pass(self):
        self.report['identityExpiresUtc'] = self.report['clientStartedUtc']
        with self.assertRaises(ValueError): self.validate()
        self.report['state'] = 'inconclusive'
        self.assertEqual('inconclusive', self.validate()['state'])

    def test_unstarted_and_pending_reports_preserve_null_observations(self):
        value = copy.deepcopy(self.report)
        value.update(state='inconclusive', stage='Identity', identityAccepted=False,
            identityCheckedUtc=None, identityExpiresUtc=None, resultState=None, clientExitCode=None,
            attemptedUtc=None, completedUtc=None, clientStartedUtc=None, clientExitedUtc=None,
            **{name: None for name in c.FACTS - {'identityAccepted', 'administratorObserved', 'cleanupConfirmed', 'providerPending', 'requiresRetention'}})
        self.assertIsNone(c.validate_sfc_client_report(value, self.context)['clientStartObserved'])
        value['cleanupConfirmed'] = False
        value['providerPending'] = True
        value['requiresRetention'] = True
        self.assertIsNone(c.validate_sfc_client_report(value, self.context)['clientExitCode'])

    def test_impossible_drain_exit_and_live_cleanup_facts_are_rejected(self):
        for changes in [dict(clientStartObserved=False), dict(clientStartAttempted=False),
                        dict(administratorObserved=False), dict(clientExitObserved=False),
                        dict(stdoutDrained=False), dict(stderrDrained=None),
                        dict(providerPending=True), dict(requiresRetention=True)]:
            with self.subTest(changes=changes):
                changed = copy.deepcopy(self.report); changed.update(state='inconclusive', **changes)
                with self.assertRaises(ValueError): c.validate_sfc_client_report(changed, self.context)
        unstarted = copy.deepcopy(self.report)
        unstarted.update(state='inconclusive', clientStartObserved=False, clientStartAttempted=False,
            administratorObserved=False, clientStartedUtc=None, clientExitObserved=None,
            clientExitedUtc=None, clientExitCode=None)
        with self.assertRaisesRegex(ValueError, 'sfc-client-drain'):
            c.validate_sfc_client_report(unstarted, self.context)

    def test_private_fields_wrong_types_source_and_unknown_states_are_rejected(self):
        for name, wrong in [('formatVersion', True), ('runAttempt', True), ('clientExitCode', False),
                            ('identityAccepted', 1), ('runId', '124'), ('sourceCommit', 'b' * 40),
                            ('resultState', 'UpToDate'), ('stage', 'private/path'), ('state', [])]:
            with self.subTest(name=name):
                changed = copy.deepcopy(self.report); changed[name] = wrong
                with self.assertRaises(ValueError): c.validate_sfc_client_report(changed, self.context)
        changed = copy.deepcopy(self.report); changed['processId'] = 123
        with self.assertRaisesRegex(ValueError, 'sfc-client-schema'): c.validate_sfc_client_report(changed, self.context)

    def test_report_times_cannot_borrow_a_stale_or_reversed_identity(self):
        for name, wrong in [('observedUtc', '2999-01-01T00:00:00Z'),
                            ('clientExitedUtc', self.report['attemptedUtc']),
                            ('identityExpiresUtc', self.report['clientStartedUtc']),
                            ('identityCheckedUtc', '2000-01-01T00:00:00Z'),
                            ('attemptedUtc', '2026-11-04T01:00:00-06:00'),
                            ('completedUtc', self.report['attemptedUtc'])]:
            with self.subTest(name=name):
                changed = copy.deepcopy(self.report); changed[name] = wrong
                with self.assertRaises(ValueError): c.validate_sfc_client_report(changed, self.context)

    def test_failed_cancelled_skipped_jobs_and_downloads_cannot_be_qualified(self):
        for outcome in ('success', 'failure', 'cancelled', 'skipped'):
            for downloaded in ('success', 'failure', 'cancelled', 'skipped'):
                with self.subTest(outcome=outcome, downloaded=downloaded):
                    _, summary, files = self.prepare(outcome, downloaded)
                    self.assertEqual(outcome == downloaded == 'success', summary['sfcClientLifecycleQualified'])
                    self.assertEqual(downloaded == 'success', 'sfc-client.json' in files)

    def test_invalid_optional_artifact_cannot_suppress_independent_passive_evidence(self):
        self.report['rawOutput'] = 'PRIVATE SYNTHETIC VALUE'; self.save()
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertTrue(summary['reportPresent'])
        self.assertTrue(summary['coreTestsPresent'])
        self.assertFalse(summary['sfcClientLifecycleQualified'])
        self.assertEqual('sfc-client-evidence-rejected', summary['sfcClientLifecycleError'])
        self.assertNotIn('sfc-client.json', files)
        self.assertNotIn('PRIVATE SYNTHETIC VALUE', str(summary))

    def test_fixed_artifact_rejects_extra_payload_duplicate_keys_and_oversize(self):
        extra = self.artifact / 'other.json'; extra.write_text('{}')
        with self.assertRaisesRegex(ValueError, 'sfc-client-artifact'): self.collect()
        extra.unlink()
        path = self.artifact / 'report.json'
        path.write_text('{"formatVersion":1,"formatVersion":1}')
        with self.assertRaises(ValueError): self.collect()
        path.write_text(' ' * 16385)
        with self.assertRaises(ValueError): self.collect()

    def test_symlink_payload_is_not_read(self):
        path = self.artifact / 'report.json'
        target = self.root / 'private.json'; target.write_text(json.dumps(self.report))
        path.unlink()
        try: path.symlink_to(target)
        except OSError: self.skipTest('This host cannot create an owned symlink')
        with self.assertRaises(ValueError): self.collect()


if __name__ == '__main__':
    unittest.main()
