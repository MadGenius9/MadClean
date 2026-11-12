import contextlib
import copy
import datetime as dt
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

TOOLS = Path(__file__).parents[1]
SPEC = importlib.util.spec_from_file_location("read_local_update_history_report", TOOLS / "read_local_update_history_report.py")
reader = importlib.util.module_from_spec(SPEC)
sys.path.insert(0, str(TOOLS))
try:
    SPEC.loader.exec_module(reader)
finally:
    sys.path.pop(0)


class LocalUpdateHistoryReportTests(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 11, 12, 12, tzinfo=dt.timezone.utc)
        self.report = {"formatVersion": 1, "context": "local-update-history",
                       "startedUtc": "2026-11-12T11:00:00Z", "completedUtc": "2026-11-12T12:00:00Z",
                       "state": "passed", "facts": {
                           "queryTaskSettled": True, "observationBudgetExpired": False,
                           "providerState": "Partial", "responseShapeValid": True, "responseAccepted": True,
                           "totalBefore": 300, "requestedCount": 128, "returnedEntryCount": 127,
                           "fieldProjectionObserved": True, "exceptionHResult": None,
                           "queryTaskSettledWhenBudgetObserved": None},
                       "windowsServiceCompletion": "unverified", "currentInstallation": "unverified",
                       "maintenance": "unverified", "physicalInput": "unverified", "sourceAuthenticity": "unverified"}

    def accepted(self, report=None, state="passed"):
        self.assertEqual(state, reader.validate(report or self.report, self.now)["state"])

    def test_bounded_short_response_is_positive_history_only(self):
        self.accepted()
        self.assertEqual("unverified", self.report["currentInstallation"])
        self.assertEqual("unverified", self.report["windowsServiceCompletion"])

    def test_empty_history_and_empty_short_response_need_no_projection(self):
        for total, requested in ((0, 0), (12, 12), (300, 128)):
            report = copy.deepcopy(self.report)
            report["facts"].update(totalBefore=total, requestedCount=requested,
                                   returnedEntryCount=0, fieldProjectionObserved=False)
            self.accepted(report)
            report["facts"]["fieldProjectionObserved"] = True
            report["state"] = "failed"
            self.accepted(report, "failed")

    def test_offline_and_other_negative_provider_states_are_inconclusive(self):
        for provider in ("Unknown", "Unavailable", "Unsupported", "Failed", "Cancelled"):
            report = copy.deepcopy(self.report)
            report["state"] = "inconclusive"
            report["facts"].update(providerState=provider, responseAccepted=False,
                                   totalBefore=None, requestedCount=None, returnedEntryCount=None,
                                   fieldProjectionObserved=None)
            self.accepted(report, "inconclusive")

    def test_missing_observations_cannot_be_promoted(self):
        report = copy.deepcopy(self.report)
        report["state"] = "inconclusive"
        report["facts"] = {key: None for key in report["facts"]}
        report["facts"]["observationBudgetExpired"] = False
        self.accepted(report, "inconclusive")
        report["state"] = "passed"
        with self.assertRaises(ValueError): reader.validate(report, self.now)

    def test_incomplete_positive_observations_remain_inconclusive(self):
        for key in ("totalBefore", "requestedCount", "fieldProjectionObserved"):
            report = copy.deepcopy(self.report); report["facts"][key] = None
            report["state"] = "inconclusive"
            with self.subTest(key=key): self.accepted(report, "inconclusive")
        report = copy.deepcopy(self.report)
        report["facts"].update(returnedEntryCount=None, fieldProjectionObserved=None)
        report["state"] = "inconclusive"
        self.accepted(report, "inconclusive")

    def test_total_still_bounds_returned_rows_when_requested_observation_is_missing(self):
        report = copy.deepcopy(self.report)
        report["facts"].update(totalBefore=0, requestedCount=None, returnedEntryCount=1)
        report["state"] = "failed"
        self.accepted(report, "failed")

    def test_budget_snapshot_preserves_pending_versus_settled_observations(self):
        for snapshot in (False, True, None):
            report = copy.deepcopy(self.report)
            report["facts"].update(observationBudgetExpired=True, queryTaskSettledWhenBudgetObserved=snapshot)
            report["state"] = "inconclusive"
            with self.subTest(snapshot=snapshot): self.accepted(report, "inconclusive")
        for snapshot in (False, True):
            report = copy.deepcopy(self.report)
            report["facts"]["queryTaskSettledWhenBudgetObserved"] = snapshot
            report["state"] = "failed"
            self.accepted(report, "failed")
        report = copy.deepcopy(self.report)
        report["facts"] = dict.fromkeys(report["facts"], None)
        report["facts"].update(observationBudgetExpired=True, queryTaskSettled=False,
                               queryTaskSettledWhenBudgetObserved=True)
        report["state"] = "failed"
        self.accepted(report, "failed")

    def test_late_positive_does_not_rewrite_expired_observation_budget(self):
        report = copy.deepcopy(self.report)
        report["facts"]["observationBudgetExpired"] = True
        report["state"] = "inconclusive"
        self.accepted(report, "inconclusive")
        report["state"] = "passed"
        with self.assertRaises(ValueError): reader.validate(report, self.now)
        report["facts"]["exceptionHResult"] = -2147467259
        report["state"] = "failed"
        self.accepted(report, "failed")

    def test_impossible_positive_and_counts_are_failed_even_after_deadline(self):
        for changes in ({"providerState": "Available"}, {"providerState": "Unrecognized"},
                        {"responseShapeValid": False}, {"exceptionHResult": 0},
                        {"requestedCount": 127}, {"returnedEntryCount": 128, "requestedCount": 100},
                        {"queryTaskSettled": False}, {"providerState": "Unsupported"}):
            with self.subTest(changes=changes):
                report = copy.deepcopy(self.report)
                report["facts"].update(changes, observationBudgetExpired=True)
                report["state"] = "failed"
                self.accepted(report, "failed")

    def test_schema_is_closed_and_metadata_fields_are_never_accepted(self):
        for key in ("entries", "title", "updateId", "description", "url", "query", "exceptionHResult"):
            for nested in (False, True):
                report = copy.deepcopy(self.report)
                (report["facts"] if nested else report)[key] = "private-provider-value"
                with self.subTest(key=key, nested=nested), self.assertRaises(ValueError):
                    reader.validate(report, self.now)
        for key in ("queryTaskSettled", "providerState"):
            report = copy.deepcopy(self.report); del report["facts"][key]
            with self.assertRaises(ValueError): reader.validate(report, self.now)

    def test_no_native_current_installation_or_authentication_claim_is_accepted(self):
        for key in ("windowsServiceCompletion", "currentInstallation", "maintenance", "physicalInput", "sourceAuthenticity"):
            with self.subTest(key=key), self.assertRaises(ValueError):
                reader.validate(dict(self.report, **{key: "verified"}), self.now)
        for key, value in (("context", "local-neutral-ui"), ("formatVersion", True), ("formatVersion", 2)):
            with self.assertRaises(ValueError): reader.validate(dict(self.report, **{key: value}), self.now)

    def test_boolean_integer_confusion_and_numeric_bounds_are_rejected(self):
        cases = {"queryTaskSettled": [0, "true"], "queryTaskSettledWhenBudgetObserved": [0, "false"],
                 "observationBudgetExpired": [None, 0],
                 "responseAccepted": [1], "responseShapeValid": [0], "fieldProjectionObserved": [1],
                 "totalBefore": [True, -1, 2147483648, 1.0], "requestedCount": [False, -1, 129],
                 "returnedEntryCount": [True, -1, 129], "exceptionHResult": [True, -2147483649, 2147483648],
                 "providerState": [[], {}, 1, "partial", "private-provider-value"]}
        for key, values in cases.items():
            for value in values:
                report = copy.deepcopy(self.report); report["facts"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                    reader.validate(report, self.now)

    def test_times_are_explicit_utc_ordered_and_not_future_to_100ns(self):
        for key, value in (("startedUtc", "2026-11-12T11:00:00"),
                           ("startedUtc", "2026-11-12T11:00:00-01:00"),
                           ("startedUtc", "2026-11-12T12:00:01Z"),
                           ("completedUtc", "2026-11-12T12:00:00.0000001Z"),
                           ("completedUtc", "2026-02-30T12:00:00Z"),
                           ("completedUtc", "2026-11-12T12:00:00Z\nprivate")):
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                reader.validate(dict(self.report, **{key: value}), self.now)
        self.accepted(dict(self.report, completedUtc="2026-11-12T12:00:00.0000000+00:00"))
        with self.assertRaises(ValueError): reader.validate(self.report, self.now.replace(tzinfo=None))

    def test_local_file_loader_refuses_duplicate_trailing_oversize_empty_and_nonregular(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); path = root / "report.json"
            for data in ('{"facts":{"totalBefore":0,"totalBefore":1}}',
                         '{"state":"passed","state":"passed"}', json.dumps(self.report) + " {}",
                         " " * 16385, ""):
                path.write_text(data)
                with self.subTest(data=data[:50]), self.assertRaises(ValueError): reader.read(path)
            with self.assertRaises(ValueError): reader.read(root)
            with self.assertRaises(OSError): reader.read(root / "missing.json")
            with self.assertRaises(ValueError): reader.read("//remote/report.json")

    def test_symlink_file_and_ancestor_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); path = root / "report.json"; path.write_text(json.dumps(self.report))
            try:
                (root / "linked.json").symlink_to(path)
                (root / "linked-directory").symlink_to(root, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("Owned symlink creation is unavailable.")
            with self.assertRaises(ValueError): reader.read(root / "linked.json")
            with self.assertRaises(ValueError): reader.read(root / "linked-directory" / "report.json")

    def test_history_file_bound_is_8192_and_safe_loader_limit_cannot_be_widened(self):
        now = dt.datetime.now(dt.timezone.utc)
        report = dict(self.report, startedUtc=(now - dt.timedelta(seconds=2)).isoformat(),
                      completedUtc=(now - dt.timedelta(seconds=1)).isoformat())
        data = json.dumps(report)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(data + " " * (8192 - len(data)), encoding="utf-8")
            self.assertEqual("passed", reader.read(path)["state"])
            path.write_text(data + " " * (8193 - len(data)), encoding="utf-8")
            with self.assertRaises(ValueError): reader.read(path)
            for limit in (True, 0, -1, 16385, 8192.0):
                with self.subTest(limit=limit), self.assertRaises(ValueError):
                    reader.neutral.read_document(path, maximum_bytes=limit)

    def test_cli_is_passive_and_prints_fixed_scoped_output_with_consistent_exit_codes(self):
        for state, code in (("passed", 0), ("failed", 1), ("inconclusive", 2)):
            report = copy.deepcopy(self.report); report["state"] = state
            if state == "failed": report["facts"]["exceptionHResult"] = -2147467259
            if state == "inconclusive": report["facts"]["observationBudgetExpired"] = True
            now = dt.datetime.now(dt.timezone.utc)
            report.update(startedUtc=(now - dt.timedelta(seconds=2)).isoformat(),
                          completedUtc=(now - dt.timedelta(seconds=1)).isoformat())
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "private-report-name.json"; path.write_text(json.dumps(report))
                output = io.StringIO()
                with mock.patch("subprocess.Popen", side_effect=AssertionError("No process execution")), \
                     mock.patch("socket.socket", side_effect=AssertionError("No network")), \
                     contextlib.redirect_stdout(output):
                    self.assertEqual(code, reader.main([str(path)]))
                text = output.getvalue()
                self.assertIn("state: " + state, text); self.assertIn("remain unverified", text)
                self.assertNotIn(str(path), text); self.assertNotIn("-2147467259", text)
                self.assertIn("Provider state: Partial.", text); self.assertNotIn("127", text)

    def test_cli_keeps_failed_provider_distinct_from_unobserved(self):
        report = copy.deepcopy(self.report)
        report["state"] = "inconclusive"
        report["facts"].update(providerState="Failed", responseAccepted=False,
                               totalBefore=None, requestedCount=None, returnedEntryCount=None,
                               fieldProjectionObserved=None)
        output = io.StringIO()
        with mock.patch.object(reader, "read", return_value=report), contextlib.redirect_stdout(output):
            self.assertEqual(2, reader.main(["owned-report.json"]))
        self.assertIn("Provider state: Failed.", output.getvalue())

    def test_cli_does_not_replace_pending_budget_snapshot_with_final_settlement(self):
        for snapshot, label in ((False, "pending"), (True, "settled"), (None, "unobserved")):
            report = copy.deepcopy(self.report)
            report["state"] = "inconclusive"
            report["facts"].update(observationBudgetExpired=True, queryTaskSettledWhenBudgetObserved=snapshot)
            output = io.StringIO()
            with mock.patch.object(reader, "read", return_value=report), contextlib.redirect_stdout(output):
                self.assertEqual(2, reader.main(["owned-report.json"]))
            self.assertIn("Query task at first observed budget expiry: " + label + ".", output.getvalue())

    def test_invalid_input_never_echoes_untrusted_metadata_or_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "private-path.json"
            path.write_text(json.dumps(dict(self.report, title="private-provider-value")))
            output = io.StringIO()
            with contextlib.redirect_stdout(output): self.assertEqual(1, reader.main([str(path)]))
            self.assertNotIn("private-provider-value", output.getvalue()); self.assertNotIn(str(path), output.getvalue())


if __name__ == "__main__":
    unittest.main()
