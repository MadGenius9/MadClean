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

TOOLS = Path(__file__).parents[1]
SPEC = importlib.util.spec_from_file_location("read_local_repair_review_report", TOOLS / "read_local_repair_review_report.py")
reader = importlib.util.module_from_spec(SPEC)
sys.path.insert(0, str(TOOLS))
try:
    SPEC.loader.exec_module(reader)
finally:
    sys.path.pop(0)


class LocalRepairReviewReportTests(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 11, 12, 12, tzinfo=dt.timezone.utc)
        self.report = {"formatVersion": 1, "context": "local-repair-review",
                       "startedUtc": "2026-11-12T11:59:00Z", "completedUtc": "2026-11-12T12:00:00Z",
                       "state": "passed", "facts": dict.fromkeys(reader.neutral.FACTS, True),
                       "repair": dict.fromkeys(reader.REPAIR_FACTS, True), "exceptionHResult": None,
                       "windowsProviders": "unverified", "maintenance": "unverified", "physicalInput": "unverified"}

    def test_modes_and_native_coverage_cannot_be_substituted(self):
        self.assertEqual("passed", reader.validate(self.report, self.now)["state"])
        with self.assertRaises(ValueError):
            reader.neutral.validate(self.report, self.now)
        for field, value in (("context", "local-neutral-ui"), ("formatVersion", True),
                             ("maintenance", "passed"), ("physicalInput", "verified"),
                             ("windowsProviders", "passed"), ("exceptionHResult", True)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                reader.validate(dict(self.report, **{field: value}), self.now)
        report = dict(self.report, nativeCalls=0)
        with self.assertRaises(ValueError):
            reader.validate(report, self.now)

    def test_missing_prerequisites_cannot_support_later_control_claims(self):
        for key in ("closedGates", "controlsBound", "draftsObserved", "acknowledgmentsObserved", "confirmationBlocked"):
            report = copy.deepcopy(self.report)
            report["repair"][key] = None
            report["state"] = "inconclusive"
            with self.subTest(key=key), self.assertRaises(ValueError):
                reader.validate(report, self.now)
        for key in ("windowObserved", "initialNeutral", "navigationObserved", "windowClosed"):
            report = copy.deepcopy(self.report)
            report["facts"][key] = None
            report["state"] = "inconclusive"
            with self.subTest(key=key), self.assertRaises(ValueError):
                reader.validate(report, self.now)

    def test_partial_unknown_failed_and_exception_states_remain_distinct(self):
        report = copy.deepcopy(self.report)
        report["repair"] = dict.fromkeys(reader.REPAIR_FACTS, None)
        report["state"] = "inconclusive"
        self.assertEqual("inconclusive", reader.validate(report, self.now)["state"])
        report["repair"]["closedGates"] = False
        report["state"] = "failed"
        self.assertEqual("failed", reader.validate(report, self.now)["state"])
        report["state"] = "passed"
        with self.assertRaises(ValueError):
            reader.validate(report, self.now)
        report = dict(self.report, exceptionHResult=0, state="failed")
        self.assertEqual("failed", reader.validate(report, self.now)["state"])

    def test_types_time_precision_and_extra_fields_are_closed(self):
        for value in (1, "true", [], {}):
            report = copy.deepcopy(self.report)
            report["repair"]["closedGates"] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                reader.validate(report, self.now)
        for field, value in (("completedUtc", "2026-11-12T12:00:00.0000001Z"),
                             ("startedUtc", "2026-11-12T11:49:59.9999999Z"),
                             ("startedUtc", "2026-11-12T11:59:00-01:00")):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                reader.validate(dict(self.report, **{field: value}), self.now)
        report = copy.deepcopy(self.report)
        report["repair"]["confirmedRepair"] = True
        with self.assertRaises(ValueError):
            reader.validate(report, self.now)

    def test_duplicate_oversize_nonfiles_and_linked_ancestry_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "report.json"
            path.write_text('{"repair":{"closedGates":true,"closedGates":true}}')
            with self.assertRaises(ValueError): reader.read(path)
            path.write_text("x" * 16_385)
            with self.assertRaises(ValueError): reader.read(path)
            with self.assertRaises(ValueError): reader.read(root)
            path.write_text(json.dumps(self.report))
            linked = root / "linked"
            try:
                linked.symlink_to(root, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("This account cannot create an owned test link.")
            with self.assertRaises(ValueError): reader.read(linked / "report.json")

    def test_passive_reader_reports_scoped_consistency_without_exporting_path(self):
        now = dt.datetime.now(dt.timezone.utc)
        report = dict(self.report, startedUtc=(now - dt.timedelta(seconds=2)).isoformat(),
                      completedUtc=(now - dt.timedelta(seconds=1)).isoformat())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report))
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(0, reader.main([str(path)]))
            self.assertIn("Reported local repair-review state: passed", output.getvalue())
            self.assertIn("source authenticity", output.getvalue())
            self.assertIn("remain unverified", output.getvalue())
            self.assertNotIn(str(path), output.getvalue())

    def test_invalid_report_does_not_echo_untrusted_content(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text("untrusted-provider-detail")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(1, reader.main([str(path)]))
            self.assertNotIn("untrusted-provider-detail", output.getvalue())
            self.assertNotIn(str(path), output.getvalue())
