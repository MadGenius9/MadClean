"""Synthetic report boundaries only: no executable, UI, package or network actions."""
import copy
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('published_app_publisher', Path(__file__).resolve().parents[1] / 'publish_windows_verification.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
c = p.published_app_update_evidence


class PublishedAppUpdateEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.context = ('a' * 40, '123', 1)
        now = dt.datetime.now(dt.timezone.utc)
        self.report = {'formatVersion': 1, 'sourceCommit': self.context[0], 'runId': '123', 'runAttempt': 1,
            'startedUtc': (now - dt.timedelta(seconds=30)).isoformat(), 'observedUtc': now.isoformat(), 'state': 'passed',
            **{name: True for name in c.FACTS}, 'requestedUpdateCommandInvocations': 1,
            'resultState': 'Partial', 'itemCount': 3, 'availableRows': 1, 'unknownRows': 2,
            'checkedUtc': (now - dt.timedelta(seconds=5)).isoformat(), 'exitCode': 0,
            'requiresRetention': False, 'stage': 'completed', 'nativeError': None}
        self.save()

    def save(self):
        (self.root / 'report.json').write_text(json.dumps(self.report))

    def collect(self):
        return c.collect(self.root, self.context, p.read_json, p.safe_path, p.reject_unless)

    def test_actual_query_proof_retains_partial_and_unknown_rows(self):
        value = self.collect()
        self.assertEqual(('Partial', 1, 2), (value['resultState'], value['availableRows'], value['unknownRows']))
        self.assertNotIn('upToDate', value)
        self.report.update(itemCount=0, availableRows=0, unknownRows=0); self.save()
        self.assertEqual('passed', self.collect()['state'])  # A completed scoped query, not all-software coverage.

    def test_each_runtime_and_teardown_fact_is_required_for_pass(self):
        for name in c.FACTS:
            self.report[name] = None; self.save()
            with self.subTest(name=name):
                with self.assertRaises(p.EvidenceError): self.collect()
            self.report[name] = True
        for name, wrong in [('exitCode', 2), ('nativeError', 5), ('requiresRetention', True),
                            ('requestedUpdateCommandInvocations', 0), ('resultState', 'Unknown')]:
            old = self.report[name]; self.report[name] = wrong; self.save()
            with self.subTest(name=name):
                with self.assertRaises(p.EvidenceError): self.collect()
            self.report[name] = old

    def test_unknown_failed_query_preserves_absent_counts_without_pass(self):
        self.report.update(state='inconclusive', resultState='Failed', itemCount=None, availableRows=None, unknownRows=None)
        self.save(); self.assertIsNone(self.collect()['itemCount'])
        self.report.update(itemCount=0, availableRows=0, unknownRows=0); self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'published-app-unchecked-rows'): self.collect()

    def test_rejects_malformed_and_private_payloads_before_publication(self):
        for name, wrong in [('itemCount', True), ('availableRows', 257), ('state', []), ('exitCode', False),
                            ('runAttempt', True), ('stage', 'private/path'), ('resultState', 'UpToDate')]:
            old = self.report[name]; self.report[name] = wrong; self.save()
            with self.subTest(name=name):
                with self.assertRaises(p.EvidenceError): self.collect()
            self.report[name] = old
        self.report['packageName'] = 'PRIVATE TEST FIXTURE'; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'published-app-schema'): self.collect()

    def test_unobserved_query_cannot_retain_checked_time_or_claim_settlement(self):
        self.report.update(state='inconclusive', resultObserved=None, freshResultObserved=None, queryOwnershipSettled=None)
        self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'published-app-result-observed'):
            self.collect()
        self.report.update(resultState=None, checkedUtc=None, itemCount=None, availableRows=None, unknownRows=None)
        self.save(); self.assertIsNone(self.collect()['resultState'])
        self.report['queryOwnershipSettled'] = True; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'published-app-order'):
            self.collect()

    def test_fresh_check_time_and_real_source_context_cannot_be_borrowed(self):
        for name, wrong in [('checkedUtc', '2000-01-01T00:00:00Z'), ('runId', '124'), ('sourceCommit', 'b' * 40),
                            ('observedUtc', '2999-01-01T00:00:00Z')]:
            old = self.report[name]; self.report[name] = wrong; self.save()
            with self.subTest(name=name):
                with self.assertRaises(p.EvidenceError): self.collect()
            self.report[name] = old

    def test_failed_job_never_becomes_qualified_and_bad_optional_report_cannot_hide_other_evidence(self):
        environment = {'GITHUB_REPOSITORY': p.REPOSITORY, 'GITHUB_REF': p.SOURCE_REF, 'GITHUB_EVENT_NAME': 'push',
            'GITHUB_SHA': self.context[0], 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1'}
        for outcome in ('success', 'failure', 'cancelled', 'skipped'):
            with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
                 patch.object(p, 'validate_report', return_value=(None, {})), \
                 patch.object(p.ci_diagnostics, 'collect', return_value=None):
                _, summary, files = p.prepare(self.root, self.root, 'success', 'success', environment,
                    published_app_directory=self.root, published_app_download_result='success', published_app_result=outcome)
            self.assertEqual(outcome == 'success', summary['publishedAppUpdateQualified'])
            self.assertIn('published-app-update.json', files)
        self.report['unknownRows'] = 1; self.save()
        with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
             patch.object(p, 'validate_report', return_value=({'passed': True}, {})), \
             patch.object(p, 'core_test_summary', return_value={'passed': True}), \
             patch.object(p.ci_diagnostics, 'collect', return_value=None):
            _, summary, files = p.prepare(self.root, self.root, 'success', 'success', environment,
                published_app_directory=self.root, published_app_download_result='success', published_app_result='success')
        self.assertTrue(summary['passed'])
        self.assertFalse(summary['publishedAppUpdateQualified'])
        self.assertEqual('published-app-evidence-rejected', summary['publishedAppUpdateError'])
        self.assertNotIn('published-app-update.json', files)


if __name__ == '__main__':
    unittest.main()
