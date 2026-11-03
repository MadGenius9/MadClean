"""Inert source-bound evidence tests. No Windows Installer calls or real inventory."""
import copy
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('msi_publisher', Path(__file__).resolve().parents[1] / 'publish_windows_verification.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


class MsiRequestEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='MadCleanMsiEvidence-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.context = ('a' * 40, '1234', 1)
        now = dt.datetime.now(dt.timezone.utc).isoformat()
        self.report = {
            'formatVersion': 2, 'sourceCommit': self.context[0], 'runId': self.context[1], 'runAttempt': 1,
            'startedUtc': now, 'observedUtc': now, 'state': 'passed', 'stage': 'Complete',
            'installCode': 0, 'clientExitCode': 0, **{name: True for name in p.MSI_FACTS},
            'selectionSourceState': 'Available', 'bindingResultState': 'Available', 'bindingReportState': 'Bound',
            'currentSnapshotState': 'Available', 'bindingReason': 'Bound', 'bindingChangedFields': 0, 'selectionNoRemove': False, 'bindingNoRemove': False, 'bindingInstanceCount': 1, 'expectedMask': 3, 'observedContext': 2, 'previewPrepared': True, 'clientRequestState': 'ClientExited', 'failureKind': None, 'failureCode': None, 'databaseOperation': 'SummaryRead',
            'toolIdentityStage': 'Completed', 'toolIdentityReason': 'Observed', 'toolNativeTrustResult': 0, 'toolFileTrustResult': 0, 'toolCatalogTrustResult': None, 'toolCleanupTrustResult': None,
        }
        self.save()

    def save(self):
        (self.root / 'report.json').write_text(json.dumps(self.report), encoding='utf-8')

    def test_owned_success_requires_every_actual_fact_not_only_an_exit_code(self):
        self.assertEqual('passed', p.validate_msi_report(self.root, self.context)['state'])
        for field in p.MSI_FACTS:
            original = self.report[field]
            for missing in (None, False):
                self.report[field] = missing; self.save()
                with self.assertRaises(p.EvidenceError): p.validate_msi_report(self.root, self.context)
            self.report[field] = original

    def test_positive_qualification_requires_actual_trusted_tool_and_bound_current_source(self):
        for field, wrong in [('toolIdentityStage', None), ('toolIdentityStage', 'Signature'),
                             ('toolIdentityReason', 'NativeTrustNotEstablished'), ('toolNativeTrustResult', None),
                             ('toolNativeTrustResult', -1), ('bindingReportState', 'Unknown'), ('currentSnapshotState', 'Partial')]:
            original = self.report[field]; self.report[field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): p.validate_msi_report(self.root, self.context)
            self.report[field] = original
        self.report.update(state='inconclusive', stage='RequestUnsettled', toolIdentityStage='Signature',
                           toolIdentityReason='NativeTrustNotEstablished', toolNativeTrustResult=-1,
                           clientStarted=None, clientExited=None, clientExitCode=None, postCodeAbsent=None,
                           payloadAbsent=None, workerSettled=None, ownedTeardown=None, visibleConsentObserved=None, clientRequestState='Blocked')
        self.save()
        self.assertEqual('inconclusive', p.validate_msi_report(self.root, self.context)['state'])

    def test_changed_selection_exports_only_fixed_cause_bits_without_authorizing_a_request(self):
        self.report.update(state='inconclusive', stage='SelectionUnknown', selectionSourceState='Partial',
                           bindingResultState='Unavailable', bindingReportState='Blocked', currentSnapshotState='Partial',
                           bindingReason='SelectionChanged', bindingChangedFields=256, previewPrepared=False,
                           clientRequestState=None, clientStarted=None, clientExited=None, clientExitCode=None,
                           visibleConsentObserved=None, postCodeAbsent=None, payloadAbsent=None, workerSettled=None, ownedTeardown=None,
                           toolIdentityStage=None, toolIdentityReason=None, toolNativeTrustResult=None, toolFileTrustResult=None)
        self.save()
        observed = p.validate_msi_report(self.root, self.context)
        self.assertEqual('SelectionChanged', observed['bindingReason'])
        self.assertEqual(256, observed['bindingChangedFields'])
        self.assertFalse(observed['previewPrepared'])
        self.assertIsNone(observed['clientStarted'])
        for field, wrong in [('bindingChangedFields', 0), ('bindingChangedFields', 1024),
                             ('bindingChangedFields', True), ('bindingInstanceCount', 2), ('selectionNoRemove', 1)]:
            original = self.report[field]; self.report[field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): p.validate_msi_report(self.root, self.context)
            self.report[field] = original

    def test_unobserved_comparisons_remain_null_rather_than_claiming_no_changes(self):
        self.report.update(state='inconclusive', stage='SelectionUnknown', bindingReportState='Unknown',
                           bindingReason='SourceUnavailable', bindingChangedFields=None, bindingInstanceCount=0, observedContext=None,
                           currentSnapshotState='Partial', bindingResultState='Unknown', bindingNoRemove=None,
                           previewPrepared=False, clientRequestState=None, clientStarted=None, clientExited=None, clientExitCode=None,
                           visibleConsentObserved=None, postCodeAbsent=None, payloadAbsent=None, workerSettled=None, ownedTeardown=None,
                           toolIdentityStage=None, toolIdentityReason=None, toolNativeTrustResult=None, toolFileTrustResult=None)
        self.save()
        self.assertIsNone(p.validate_msi_report(self.root, self.context)['bindingChangedFields'])
        self.report['observedContext'] = 2; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'msi-binding-context-coherence'):
            p.validate_msi_report(self.root, self.context)

    def test_pre_release_v1_evidence_cannot_supply_v2_binding_proof(self):
        self.report['formatVersion'] = 1; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'msi-report-schema'):
            p.validate_msi_report(self.root, self.context)

    def test_pending_observations_are_not_filled_with_success_or_false_completion(self):
        self.report.update(state='inconclusive', stage='VisibleRequest', visibleConsentObserved=None,
                           clientStarted=None, clientExited=None, clientExitCode=None, postCodeAbsent=None,
                           payloadAbsent=None, workerSettled=None, ownedTeardown=None)
        self.save()
        observed = p.validate_msi_report(self.root, self.context)
        self.assertIsNone(observed['clientExited'])
        self.assertEqual('inconclusive', observed['state'])
        self.report['ownedTeardown'] = True; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'msi-teardown-coherence'):
            p.validate_msi_report(self.root, self.context)

    def test_observed_exit_without_retrievable_code_remains_historical_inconclusive(self):
        self.report.update(state='inconclusive', stage='PostStateUnverified', clientExitCode=None,
                           postCodeAbsent=None, payloadAbsent=None, ownedTeardown=None)
        self.save()
        observed = p.validate_msi_report(self.root, self.context)
        self.assertTrue(observed['clientExited'])
        self.assertIsNone(observed['clientExitCode'])
        self.assertEqual('inconclusive', observed['state'])

    def test_user_cancelled_exit_is_separate_from_registration_or_payload_absence(self):
        self.report.update(state='inconclusive', stage='PostStateUnverified', clientExitCode=1602,
                           postCodeAbsent=False, payloadAbsent=False, ownedTeardown=None)
        self.save()
        observed = p.validate_msi_report(self.root, self.context)
        self.assertEqual(1602, observed['clientExitCode'])
        self.assertFalse(observed['postCodeAbsent'])
        self.assertIsNone(observed['ownedTeardown'])

    def test_context_mismatch_and_unapproved_fields_cannot_export_product_data(self):
        for field, wrong in [('sourceCommit', 'b' * 40), ('runId', '5678'), ('runAttempt', 2), ('runAttempt', True)]:
            original = self.report[field]; self.report[field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): p.validate_msi_report(self.root, self.context)
            self.report[field] = original
        self.report['productCode'] = 'TEST-ONLY-NOT-AN-ACTUAL-ID'; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'msi-report-schema'): p.validate_msi_report(self.root, self.context)

    def test_duplicate_json_keys_and_wrong_scalar_types_are_rejected(self):
        (self.root / 'report.json').write_text(json.dumps(self.report)[:-1] + ',"state":"passed"}')
        with self.assertRaisesRegex(p.EvidenceError, 'duplicate-json-property'): p.validate_msi_report(self.root, self.context)
        for field, wrong in [('workerSettled', 1), ('installCode', True), ('clientExitCode', 2 ** 40), ('state', {})]:
            original = self.report[field]; self.report[field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): p.validate_msi_report(self.root, self.context)
            self.report[field] = original

    def test_rejected_optional_msi_payload_does_not_hide_independent_passive_evidence(self):
        self.report['clientExited'] = False; self.save()
        environment = {'GITHUB_REPOSITORY': p.REPOSITORY, 'GITHUB_REF': p.SOURCE_REF,
                       'GITHUB_EVENT_NAME': 'push', 'GITHUB_SHA': self.context[0],
                       'GITHUB_RUN_ID': self.context[1], 'GITHUB_RUN_ATTEMPT': '1'}
        with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
             patch.object(p, 'validate_report', return_value=({'passed': True}, {})), \
             patch.object(p, 'core_test_summary', return_value={'passed': True}), \
             patch.object(p.ci_diagnostics, 'collect', return_value=None):
            _, summary, files = p.prepare(self.root, self.root, 'success', 'success', environment,
                                         msi_directory=self.root, msi_download_result='success', msi_result='failure')
        self.assertTrue(summary['passed'])
        self.assertFalse(summary['msiRequestReportPresent'])
        self.assertFalse(summary['msiRequestQualified'])
        self.assertEqual('msi-evidence-rejected', summary['msiRequestError'])
        self.assertIn('report.json', files)
        self.assertNotIn('msi-request.json', files)

    def test_deeply_nested_optional_json_is_rejected_without_blocking_other_evidence(self):
        (self.root / 'report.json').write_text('[' * 1100 + '0' + ']' * 1100)
        environment = {'GITHUB_REPOSITORY': p.REPOSITORY, 'GITHUB_REF': p.SOURCE_REF,
                       'GITHUB_EVENT_NAME': 'push', 'GITHUB_SHA': self.context[0],
                       'GITHUB_RUN_ID': self.context[1], 'GITHUB_RUN_ATTEMPT': '1'}
        with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
             patch.object(p, 'validate_report', return_value=({'passed': True}, {})), \
             patch.object(p, 'core_test_summary', return_value={'passed': True}), \
             patch.object(p.ci_diagnostics, 'collect', return_value=None):
            _, summary, files = p.prepare(self.root, self.root, 'success', 'success', environment,
                                         msi_directory=self.root, msi_download_result='success', msi_result='failure')
        self.assertTrue(summary['passed'])
        self.assertEqual('msi-evidence-rejected', summary['msiRequestError'])
        self.assertNotIn('msi-request.json', files)

    def test_successful_report_does_not_override_failed_job_outcome(self):
        environment = {'GITHUB_REPOSITORY': p.REPOSITORY, 'GITHUB_REF': p.SOURCE_REF,
                       'GITHUB_EVENT_NAME': 'push', 'GITHUB_SHA': self.context[0],
                       'GITHUB_RUN_ID': self.context[1], 'GITHUB_RUN_ATTEMPT': '1'}
        with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
             patch.object(p, 'validate_report', return_value=(None, {})), \
             patch.object(p.ci_diagnostics, 'collect', return_value=None):
            _, summary, files = p.prepare(self.root, self.root, 'failure', 'failure', environment,
                                         msi_directory=self.root, msi_download_result='success', msi_result='failure')
        self.assertFalse(summary['msiRequestQualified'])
        self.assertTrue(summary['msiRequestReportPresent'])
        self.assertIn('msi-request.json', files)


if __name__ == '__main__':
    unittest.main()
