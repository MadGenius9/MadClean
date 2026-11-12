"""Synthetic CI-artifact fixtures only; no provider, live SDK or remote Git operations."""
import copy
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

TOOLS = Path(__file__).parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, TOOLS / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


evidence = load('synthetic_scoped_evidence', 'local_observation_evidence.py')
sdk = load('synthetic_sdk_producer', 'inspect_journal_sdk.py')


class ScopedObservationEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='synthetic-ci-observation-fixture-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.context = ('a' * 40, '123', 1)
        self.source_ref = 'refs/heads/recovery/validated-20261112'
        self.time = dt.datetime.now(dt.timezone.utc).isoformat()
        self.repair = {'formatVersion': 1, 'context': 'local-repair-review', 'startedUtc': self.time,
                       'completedUtc': self.time, 'state': 'passed',
                       'facts': {key: True for key in evidence._neutral.FACTS},
                       'repair': {key: True for key in evidence._repair.REPAIR_FACTS},
                       'exceptionHResult': None, 'windowsProviders': 'unverified',
                       'maintenance': 'unverified', 'physicalInput': 'unverified'}
        self.history = {'formatVersion': 1, 'context': 'local-update-history', 'startedUtc': self.time,
                        'completedUtc': self.time, 'state': 'passed',
                        'facts': {'queryTaskSettled': True, 'observationBudgetExpired': False,
                                  'providerState': 'Partial', 'responseShapeValid': True, 'responseAccepted': True,
                                  'totalBefore': 1, 'requestedCount': 1, 'returnedEntryCount': 1,
                                  'fieldProjectionObserved': True, 'exceptionHResult': None,
                                  'queryTaskSettledWhenBudgetObserved': None},
                        **{key: 'unverified' for key in evidence._history.SCOPES}}
        self.sdk = {'formatVersion': 1, 'context': 'installed-windows-sdk-journal-reference',
                    'observedUtc': self.time, 'state': 'reference-only',
                    **{key: 'unverified' for key in ('sourceAuthenticity', 'abi', 'osAvailability', 'namespaceSecurity', 'nativeQualification')},
                    'reportTruncated': False, 'sourceIssue': None, 'versions': []}
        fixture = b'// synthetic fixture only; not a Microsoft declaration\n#if FIXTURE_TARGET\nHANDLE CreateDirectory2W(void);\n#endif\n'
        headers = []
        for header in sdk.HEADERS:
            headers.append({'header': header, 'reason': None, 'sha256': hashlib.sha256(fixture).hexdigest(),
                            'byteCount': len(fixture), **sdk.parse_header(fixture)})
        self.sdk['versions'] = [{'sdkVersion': '10.0.999.0', 'state': 'reference-only', 'headers': headers}]
        self.paths = {
            'localRepairReview': self.root / 'local-repair-review-validation' / ('1' * 32) / 'report.json',
            'localUpdateHistory': self.root / 'local-update-history-validation' / ('2' * 32) / 'report.json',
            'sdkJournalReference': self.root / 'sdk-journal-reference' / 'report.json'}
        for scope, report in (('localRepairReview', self.repair), ('localUpdateHistory', self.history), ('sdkJournalReference', self.sdk)):
            self.save(scope, report)

    def save(self, scope, report):
        path = self.paths[scope]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report), encoding='utf-8')

    def collect(self):
        return evidence.collect(self.root, self.context, self.source_ref, 'success')

    def test_each_scope_preserves_inner_limits_actual_text_hash_and_trusted_outer_identity(self):
        before = {str(path.relative_to(self.root)): path.read_bytes() for path in self.root.rglob('*') if path.is_file()}
        observations, files = self.collect()
        self.assertEqual({'local-repair-review.json', 'local-update-history.json', 'sdk-journal-reference.json'}, set(files))
        for scope, (_, output, _) in evidence.SCOPES.items():
            observed = observations[scope]
            wrapper = json.loads(files[output])
            self.assertTrue(observed['present'])
            self.assertIsNone(observed['error'])
            self.assertEqual(self.source_ref, wrapper['sourceRef'])
            self.assertEqual(self.context, (wrapper['sourceCommit'], wrapper['runId'], wrapper['runAttempt']))
            self.assertEqual(hashlib.sha256(self.paths[scope].read_bytes()).hexdigest(), wrapper['reportSha256'])
            self.assertEqual(wrapper['reportSha256'], observed['originalReportSha256'])
            self.assertEqual('unverified', wrapper['sourceAuthenticity'])
            self.assertNotIn('qualified', wrapper)
        sdk_inner = json.loads(files['sdk-journal-reference.json'])['report']
        self.assertEqual(self.sdk, sdk_inner)
        self.assertIn('HANDLE CreateDirectory2W(void);', [line['text'] for line in sdk_inner['versions'][0]['headers'][0]['fragments'][0]['lines']])
        self.assertEqual(before, {str(path.relative_to(self.root)): path.read_bytes() for path in self.root.rglob('*') if path.is_file()})

    def test_unsuccessful_download_never_opens_any_artifact_path(self):
        for result in ('failure', 'cancelled', 'skipped'):
            with self.subTest(result=result), mock.patch.object(evidence, '_safe', side_effect=AssertionError('must not read')):
                observations, files = evidence.collect(self.root, self.context, self.source_ref, result)
                self.assertEqual({}, files)
                self.assertTrue(all(not value['present'] and value['error'] is None for value in observations.values()))

    def test_missing_root_is_distinct_from_an_unsafe_artifact_root(self):
        observations, files = evidence.collect(self.root / 'absent', self.context, self.source_ref, 'success')
        self.assertFalse(files)
        self.assertTrue(all(value['error'] is None for value in observations.values()))
        file = self.root / 'not-a-directory'
        file.write_text('synthetic fixture only')
        observations, files = evidence.collect(file, self.context, self.source_ref, 'success')
        self.assertFalse(files)
        self.assertTrue(all(value['error'] == 'unsafe-artifact-root' for value in observations.values()))

    def test_bad_history_cannot_suppress_independent_ui_or_sdk_fragments(self):
        self.history['facts']['observationBudgetExpired'] = True
        self.save('localUpdateHistory', self.history)  # The unchanged passed label contradicts expiry.
        observations, files = self.collect()
        self.assertEqual('scope-evidence-rejected', observations['localUpdateHistory']['error'])
        self.assertEqual({'local-repair-review.json', 'sdk-journal-reference.json'}, set(files))
        self.assertTrue(observations['sdkJournalReference']['present'])

    def test_multiple_guid_reports_are_ambiguous_even_with_identical_bytes(self):
        extra = self.paths['localRepairReview'].parent.parent / ('3' * 32)
        extra.mkdir()
        (extra / 'report.json').write_bytes(self.paths['localRepairReview'].read_bytes())
        observations, files = self.collect()
        self.assertEqual('scope-evidence-rejected', observations['localRepairReview']['error'])
        self.assertNotIn('local-repair-review.json', files)
        self.assertIn('local-update-history.json', files)

    def test_noncanonical_guid_and_extra_leaf_files_are_not_selected(self):
        original = self.paths['localRepairReview'].parent
        for name in ('0' * 32, 'A' * 32, 'not-a-guid'):
            destination = original.with_name(name)
            original.rename(destination)
            observations, _ = self.collect()
            self.assertEqual('scope-evidence-rejected', observations['localRepairReview']['error'])
            destination.rename(original)
        (original / 'private-extra.txt').write_text('synthetic fixture must not export')
        observations, files = self.collect()
        self.assertFalse(observations['localRepairReview']['present'])
        self.assertNotIn(b'private-extra', b''.join(files.values()))

    def test_symbolic_report_is_not_followed(self):
        path = self.paths['sdkJournalReference']
        path.unlink()
        target = self.root / 'private-synthetic.json'
        target.write_text('PRIVATE-SYNTHETIC-MUST-NOT-EXPORT')
        try:
            path.symlink_to(target)
        except OSError:
            self.skipTest('Owned fixture link creation unavailable.')
        observations, files = self.collect()
        self.assertEqual('scope-evidence-rejected', observations['sdkJournalReference']['error'])
        self.assertNotIn(b'PRIVATE-SYNTHETIC', b''.join(files.values()))

    def test_oversized_scope_does_not_suppress_other_reports(self):
        self.paths['sdkJournalReference'].write_bytes(b' ' * (524288 + 1))
        observations, files = self.collect()
        self.assertEqual('scope-evidence-rejected', observations['sdkJournalReference']['error'])
        self.assertEqual({'local-repair-review.json', 'local-update-history.json'}, set(files))

    def test_duplicate_properties_and_lossy_or_nonfinite_json_are_rejected(self):
        path = self.paths['sdkJournalReference']
        for data in (b'{"formatVersion":1,"formatVersion":1}', b'{"fixture":NaN}', b'\xff'):
            with self.subTest(data=data):
                path.write_bytes(data)
                observations, files = self.collect()
                self.assertFalse(observations['sdkJournalReference']['present'])
                self.assertEqual('scope-evidence-rejected', observations['sdkJournalReference']['error'])
                self.assertIn('local-repair-review.json', files)

    def test_original_hash_includes_actual_bom_and_whitespace(self):
        path = self.paths['localUpdateHistory']
        original = b'\xef\xbb\xbf' + path.read_bytes() + b'\r\n '
        path.write_bytes(original)
        observations, files = self.collect()
        wrapper = json.loads(files['local-update-history.json'])
        self.assertEqual(hashlib.sha256(original).hexdigest(), wrapper['reportSha256'])
        self.assertTrue(observations['localUpdateHistory']['present'])

    def test_negative_history_and_unknown_sdk_remain_accepted_unknown_evidence(self):
        self.history['facts'].update(providerState='Unavailable', responseAccepted=False,
                                     totalBefore=None, requestedCount=None, returnedEntryCount=None, fieldProjectionObserved=None)
        self.history['state'] = 'inconclusive'
        self.save('localUpdateHistory', self.history)
        self.sdk.update(versions=[], state='unknown', sourceIssue='sdk-root-unavailable')
        self.save('sdkJournalReference', self.sdk)
        observations, files = self.collect()
        self.assertEqual('inconclusive', observations['localUpdateHistory']['reportedState'])
        self.assertEqual('unknown', observations['sdkJournalReference']['reportedState'])
        self.assertEqual('unverified', json.loads(files['local-update-history.json'])['report']['currentInstallation'])
        self.assertEqual('unverified', json.loads(files['sdk-journal-reference.json'])['report']['abi'])

    def test_sdk_wrong_scope_unbounded_fragments_and_false_qualification_are_rejected(self):
        mutations = (
            lambda value: value.update(nativeQualification='passed'),
            lambda value: value.update(sourceAuthenticity='verified'),
            lambda value: value.update(extraEnvironment='PRIVATE'),
            lambda value: value['versions'].append(copy.deepcopy(value['versions'][0])),
            lambda value: value['versions'][0]['headers'][0].update(header='../../private.h'),
            lambda value: value['versions'][0]['headers'][0].update(sha256='not-a-hash'),
            lambda value: value['versions'][0]['headers'][0]['symbols'][0].update(definitionState='verified'),
            lambda value: value['versions'][0]['headers'][0]['fragments'][0]['lines'][0].update(text='X' * 513),
            lambda value: value['versions'][0]['headers'][0]['fragments'][0]['lines'][0].update(text='\ud800'),
            lambda value: value['versions'][0]['headers'][0]['fragments'][0]['lines'].extend([{'line': 100, 'text': 'synthetic'}] * 28),
            lambda value: value['versions'][0]['headers'][0]['fragments'][0]['preprocessorContext'].extend([{'line': 1, 'text': '#if synthetic'}] * 17),
            lambda value: value['versions'][0]['headers'][0]['fragments'][0]['preprocessorContext'][0].update(text='#if CONTRADICTORY_SYNTHETIC_GUARD'),
            lambda value: value['versions'][0]['headers'][0]['symbols'][0].update(matchingLineCount=0, matchingLinesOmitted=0, state='unknown-not-observed'),
            lambda value: value.update(sourceIssue='PRIVATE-EXCEPTION-PATH'),
        )
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                value = copy.deepcopy(self.sdk)
                mutate(value)
                self.save('sdkJournalReference', value)
                observations, files = self.collect()
                self.assertEqual('scope-evidence-rejected', observations['sdkJournalReference']['error'])
                self.assertEqual({'local-repair-review.json', 'local-update-history.json'}, set(files))

    def test_sdk_report_truncation_stays_explicit_and_unknown_header_shape_is_closed(self):
        header = self.sdk['versions'][0]['headers'][0]
        header['fragmentsOmittedForReportBound'] = len(header['fragments'])
        header['fragments'] = []
        self.save('sdkJournalReference', self.sdk)
        self.assertFalse(self.collect()[0]['sdkJournalReference']['present'])
        self.sdk['reportTruncated'] = True
        self.sdk['versions'][0]['headers'][1] = {'header': sdk.HEADERS[1], 'state': 'unknown',
            'reason': 'source-unreadable', 'sha256': None, 'byteCount': None, 'fragments': [],
            'fragmentsOmittedForReportBound': 0}
        self.save('sdkJournalReference', self.sdk)
        observations, files = self.collect()
        self.assertTrue(observations['sdkJournalReference']['present'])
        self.assertTrue(json.loads(files['sdk-journal-reference.json'])['report']['reportTruncated'])

    def test_invalid_ci_context_ref_or_outcome_is_refused_before_any_path_read(self):
        for context, ref, outcome in ((('bad', '123', 1), self.source_ref, 'success'),
                                      (self.context, 'refs/heads/main', 'success'),
                                      ((self.context[0], '123', True), self.source_ref, 'success'),
                                      (self.context, self.source_ref, 'neutral')):
            with self.subTest(context=context, ref=ref), mock.patch.object(evidence, '_safe', side_effect=AssertionError('must not read')):
                with self.assertRaises(ValueError):
                    evidence.collect(self.root, context, ref, outcome)

    def test_loader_uses_exact_sibling_neutral_and_restores_prior_import_binding(self):
        sentinel = object()
        with mock.patch.dict(sys.modules, {'read_local_ui_report': sentinel}):
            loaded = load('another_synthetic_scoped_evidence', 'local_observation_evidence.py')
            self.assertIs(sentinel, sys.modules['read_local_ui_report'])
            self.assertIs(loaded._neutral, loaded._repair.neutral)
            self.assertIs(loaded._neutral, loaded._history.neutral)


if __name__ == '__main__':
    unittest.main()
