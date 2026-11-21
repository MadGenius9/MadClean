import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

from test_publish_existing_reader_qualification import report

spec = importlib.util.spec_from_file_location("existing_reader_evidence", Path(__file__).resolve().parents[1] / "read_existing_reader_qualification.py")
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)


class ExistingReaderEvidenceTests(unittest.TestCase):
    def test_source_bound_complete_matrix_is_accepted_without_changing_facts(self):
        data = report()
        self.assertEqual(data, r.validate_report(copy.deepcopy(data), expected_source="a" * 40, expected_run="100", expected_attempt=1))

    def test_earlier_refusal_cannot_be_reported_as_expected_namespace_rejection(self):
        data = report()
        data["cases"][3]["observation"].update(directory="Refused", lease="NotEntered", journal="NotEntered", exercise="NotEntered", names="NotObserved")
        with self.assertRaises(ValueError):
            r.validate_report(data)

    def test_names_complete_does_not_prove_negative_case_or_sample_success(self):
        for label_index in (3, 4, 5):
            data = report()
            data["cases"][label_index]["observation"]["names"] = "Complete"
            with self.subTest(case=label_index), self.assertRaises(ValueError):
                r.validate_report(data)

    def test_timeout_cannot_be_rewritten_as_on_time_pass_after_late_settlement(self):
        data = report()
        data["timeoutObserved"] = True
        with self.assertRaises(ValueError):
            r.validate_report(data)
        data["state"] = "inconclusive"
        self.assertTrue(r.validate_report(data)["timeoutObserved"])

    def test_setup_unavailable_or_cleanup_unknown_cannot_qualify_a_case(self):
        for key, value in (("setupConfirmed", False), ("teardownConfirmed", None), ("resourcesReleased", False), ("hasPendingWork", True), ("requiresRetention", True), ("independentComparison", None)):
            data = report()
            data["cases"][0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                r.validate_report(data)

    def test_partial_initial_snapshot_remains_inconclusive_with_ownership_unknown(self):
        data = report()
        data.update(state="inconclusive", cases=[], ownershipSettled=False, timeoutObserved=True)
        self.assertEqual("inconclusive", r.validate_report(data)["state"])
        data["state"] = "passed"
        with self.assertRaises(ValueError):
            r.validate_report(data)

    def test_source_run_attempt_mismatch_is_rejected(self):
        for expected in ({"expected_source": "b" * 40}, {"expected_run": "101"}, {"expected_attempt": 2}):
            with self.subTest(expected=expected), self.assertRaises(ValueError):
                r.validate_report(report(), **expected)

    def test_missing_reordered_repeated_cases_and_continuing_after_refusal_do_not_pass(self):
        for mutation in (lambda x: x.pop(), lambda x: x.reverse(), lambda x: x.__setitem__(1, x[0]), lambda x: x[0].update(state="inconclusive")):
            data = report()
            mutation(data["cases"])
            with self.assertRaises(ValueError):
                r.validate_report(data)

    def test_private_fields_unsupported_source_claims_and_invalid_timestamps_refuse(self):
        for mutation in (lambda d: d.update(path="private"), lambda d: d["cases"][0].update(journal="private"), lambda d: d.update(sourceAuthenticity="verified"), lambda d: d.update(completedUtc="2026-11-20T00:00:00Z"), lambda d: d.update(startedUtc="2026-11-21T00:00:00+01:00"), lambda d: d.update(runAttempt=True)):
            data = report()
            mutation(data)
            with self.assertRaises(ValueError):
                r.validate_report(data)

    def test_sub_microsecond_reverse_order_is_not_rounded_into_valid_time(self):
        data = report()
        data.update(startedUtc="2026-11-21T00:00:00.0000002Z", completedUtc="2026-11-21T00:00:00.0000001Z")
        with self.assertRaises(ValueError):
            r.validate_report(data)

    def test_duplicate_keys_and_oversized_files_are_not_read_as_valid_evidence(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "report.json"
            path.write_text('{"formatVersion":1,"formatVersion":1}')
            with self.assertRaises(ValueError):
                r.load_report(path)
            path.write_bytes(b" " * (r.MAX_BYTES + 1))
            with self.assertRaises(ValueError):
                r.load_report(path)

    def test_failed_unexpected_acceptance_is_preserved_not_promoted_or_hidden(self):
        data = report()
        data["state"] = "failed"
        case = data["cases"][-1]
        case.update(state="failed", sampleVerified=True, boundaryMatched=False)
        case["observation"]["exercise"] = "Confirmed"
        self.assertEqual("failed", r.validate_report(data)["state"])
        data["state"] = "inconclusive"
        with self.assertRaises(ValueError):
            r.validate_report(data)

    def test_known_unexpected_acceptance_survives_later_uncertain_cleanup(self):
        data = report()
        data.update(state="inconclusive", ownershipSettled=False)
        case = data["cases"][-1]
        case.update(state="inconclusive", sampleVerified=True, resourcesReleased=False, requiresRetention=True,
                    boundaryMatched=False, independentComparison=None, teardownConfirmed=None)
        case["observation"].update(exercise="Confirmed", release="Uncertain")
        with self.assertRaises(ValueError):
            r.validate_report(data)
        case["state"] = data["state"] = "failed"
        self.assertEqual("failed", r.validate_report(data)["state"])

    def test_observed_mutation_does_not_hide_earlier_native_boundary_or_later_retention(self):
        data = report()
        data.update(state="failed", ownershipSettled=False)
        case = data["cases"][-1]
        case.update(state="failed", independentComparison=False, requiresRetention=True, teardownConfirmed=None)
        self.assertTrue(r.validate_report(data)["cases"][-1]["boundaryMatched"])
        case["state"] = data["state"] = "inconclusive"
        with self.assertRaises(ValueError):
            r.validate_report(data)


if __name__ == "__main__":
    unittest.main()
