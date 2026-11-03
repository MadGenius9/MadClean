"""Fixed evidence fixtures only; no servicing, PowerShell or Windows calls."""
import copy
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('component_publisher', Path(__file__).resolve().parents[1] / 'publish_windows_verification.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
c = p.component_store_evidence


class ComponentStoreEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='MadCleanComponentEvidence-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.context = ('b' * 40, '4321', 1)
        now = dt.datetime.now(dt.timezone.utc).isoformat()
        transport = {name: True for name in c.TRANSPORT_BOOLEANS}
        transport.update(hostExitCode=0, stderrCharacterCount=0, outputBoundExceeded=False,
                         stopSignalSucceeded=None, stopSignalNativeError=None,
                         identityStage='Completed', identityReason='Observed', nativeTrustResult=0)
        evidence = {name: True for name in c.EVIDENCE_BOOLEANS}
        evidence.update(stage='Completed', outcome='Completed', pipelineState='Completed', outputCount=1,
                        errorCount=0, warningCount=0, healthState='Healthy', managedExceptionHResult=None,
                        cancelEventObserved=False, beginStopCalled=False, endStopReturned=None)
        self.report = {
            'formatVersion': 1, 'sourceCommit': self.context[0], 'runId': self.context[1], 'runAttempt': 1,
            'startedUtc': now, 'observedUtc': now, 'state': 'passed', 'stage': 'Complete',
            **{name: True for name in c.TOP_BOOLEANS},
            'precancelState': 'Cancelled', 'resultState': 'Available', 'recordedFlag': 'NoRecordedCorruptionFlag',
            'transport': transport, 'evidence': evidence, 'fixtureExceptionHResult': None,
        }
        for name in ('precancelAuthorizationTransferred', 'sessionStopRequested', 'sessionPending', 'providerPending', 'sessionRetention', 'providerRetention'):
            self.report[name] = False
        self.save()

    def save(self):
        (self.root / 'report.json').write_text(json.dumps(self.report), encoding='utf-8')

    def collect(self):
        return c.collect(self.root, self.context, p.read_json, p.safe_path, p.reject_unless)

    def test_each_real_recorded_flag_qualifies_its_scope_without_a_pc_health_score(self):
        for native, flag in [('Healthy', 'NoRecordedCorruptionFlag'), ('Repairable', 'RecordedRepairable'), ('NonRepairable', 'RecordedNonRepairable')]:
            self.report['evidence']['healthState'] = native; self.report['recordedFlag'] = flag; self.save()
            actual = self.collect()
            self.assertEqual(native, actual['evidence']['healthState'])
            self.assertEqual(flag, actual['recordedFlag'])
        self.report['recordedFlag'] = 'Healthy'; self.save()
        with self.assertRaises(p.EvidenceError): self.collect()

    def test_host_exit_zero_cannot_replace_pipeline_endinvoke_or_cleanup(self):
        for target, field, wrong in [('evidence', 'endInvokeReturned', None), ('evidence', 'powerShellDisposed', False),
                                     ('evidence', 'runspaceClosed', None), ('evidence', 'cancelHandleDisposed', False),
                                     ('transport', 'ownershipReleased', None), ('transport', 'stdoutDrained', False),
                                     ('transport', 'stderrDrained', None), ('transport', 'nativeTrustResult', None)]:
            original = self.report[target][field]; self.report[target][field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): self.collect()
            self.report[target][field] = original

    def test_warning_uncertainty_preserves_raw_typed_observation_but_not_a_current_flag(self):
        for count in (None, 1):
            self.report['state'] = 'inconclusive'; self.report['resultState'] = 'Unknown'; self.report['recordedFlag'] = 'Unknown'
            self.report['evidence']['warningCount'] = count; self.save()
            self.assertEqual('Healthy', self.collect()['evidence']['healthState'])
            self.report['recordedFlag'] = 'NoRecordedCorruptionFlag'; self.save()
            with self.assertRaises(p.EvidenceError): self.collect()

    def test_stop_request_is_not_proof_of_servicing_cancellation_or_rollback(self):
        self.report.update(state='inconclusive', resultState='Cancelled', recordedFlag='Unknown', sessionStopRequested=True)
        self.report['transport'].update(stopSignalSucceeded=True)
        self.report['evidence'].update(outcome='PipelineStopped', pipelineState='Stopped', cancelEventObserved=True,
                                       beginStopCalled=True, endStopReturned=True)
        self.save()
        actual = self.collect()
        self.assertEqual('Unknown', actual['recordedFlag'])
        self.assertTrue(actual['evidence']['endStopReturned'])
        self.report['state'] = 'passed'; self.save()
        with self.assertRaises(p.EvidenceError): self.collect()

    def test_observed_exit_without_code_does_not_get_a_fabricated_success_code(self):
        self.report.update(state='inconclusive', resultState='Unknown', recordedFlag='Unknown')
        self.report['transport']['hostExitCode'] = None; self.save()
        actual = self.collect()
        self.assertTrue(actual['transport']['hostExitObserved'])
        self.assertIsNone(actual['transport']['hostExitCode'])
        self.report['state'] = 'passed'; self.save()
        with self.assertRaises(p.EvidenceError): self.collect()

    def test_precancel_requires_actual_no_transfer_and_cancelled_outcome(self):
        for field, wrong in [('precancelAuthorizationTransferred', True), ('precancelState', 'Unavailable')]:
            original = self.report[field]; self.report[field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): self.collect()
            self.report[field] = original

    def test_noncompleted_partial_source_does_not_invent_missing_boolean_facts(self):
        self.report.update(state='inconclusive', stage='Candidate', resultState=None, recordedFlag=None,
                           evidenceConsistent=None, transportConsistent=None, transport=None, evidence=None,
                           candidateAuthorizationReleased=None, sessionPending=None, providerPending=None,
                           sessionRetention=None, providerRetention=None, sessionStopRequested=None, ownedTeardown=None)
        self.save()
        actual = self.collect()
        self.assertIsNone(actual['transport'])
        self.assertIsNone(actual['candidateAuthorizationReleased'])

    def test_run_binding_and_exact_schema_prevent_nonce_or_provider_output_export(self):
        for field, wrong in [('sourceCommit', 'a' * 40), ('runId', '9876'), ('runAttempt', 2), ('runAttempt', True)]:
            original = self.report[field]; self.report[field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): self.collect()
            self.report[field] = original
        self.report['requestId'] = 'TEST-ONLY-NOT-A-REAL-NONCE'; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'component-report-schema'): self.collect()
        del self.report['requestId']
        self.report['transport']['eventName'] = 'TEST-ONLY-NOT-A-REAL-EVENT'; self.save()
        with self.assertRaisesRegex(p.EvidenceError, 'component-transport-schema'): self.collect()

    def test_wrong_scalar_types_and_unsupported_counts_never_become_evidence(self):
        for target, field, wrong in [('transport', 'hostExitCode', True), ('transport', 'stderrCharacterCount', -1),
                                     ('evidence', 'warningCount', 129), ('evidence', 'outputCount', 17),
                                     ('evidence', 'moduleBound', []), ('evidence', 'healthState', 'Clean')]:
            original = self.report[target][field]; self.report[target][field] = wrong; self.save()
            with self.assertRaises(p.EvidenceError): self.collect()
            self.report[target][field] = original

    def environment(self):
        return {'GITHUB_REPOSITORY': p.REPOSITORY, 'GITHUB_REF': p.SOURCE_REF, 'GITHUB_EVENT_NAME': 'push',
                'GITHUB_SHA': self.context[0], 'GITHUB_RUN_ID': self.context[1], 'GITHUB_RUN_ATTEMPT': '1'}

    def prepare(self, job='success', artifact='success'):
        with patch.object(p, 'validate_checkout', side_effect=lambda root: root), \
             patch.object(p, 'validate_report', return_value=({'passed': True}, {})), \
             patch.object(p, 'core_test_summary', return_value={'passed': True}), \
             patch.object(p.ci_diagnostics, 'collect', return_value=None):
            return p.prepare(self.root, self.root, 'success', 'success', self.environment(),
                             component_directory=self.root, component_download_result=artifact, component_result=job)

    def test_malformed_optional_d2_cannot_hide_independent_passive_and_core_results(self):
        (self.root / 'report.json').write_text('[' * 1100 + '0' + ']' * 1100)
        _, summary, files = self.prepare(job='failure')
        self.assertTrue(summary['passed'])
        self.assertFalse(summary['componentStoreStatusQualified'])
        self.assertEqual('component-evidence-rejected', summary['componentStoreStatusError'])
        self.assertIn('report.json', files)
        self.assertNotIn('component-store.json', files)

    def test_failed_job_cannot_be_overridden_by_a_success_report(self):
        _, summary, files = self.prepare(job='failure')
        self.assertTrue(summary['componentStoreStatusReportPresent'])
        self.assertFalse(summary['componentStoreStatusQualified'])
        self.assertIn('component-store.json', files)

    def test_failed_artifact_never_reads_or_admits_a_leftover_report(self):
        with patch.object(c, 'collect', side_effect=AssertionError('must not read')):
            _, summary, files = self.prepare(artifact='failure')
        self.assertFalse(summary['componentStoreStatusReportPresent'])
        self.assertNotIn('component-store.json', files)


if __name__ == '__main__':
    unittest.main()
