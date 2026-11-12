import copy
import contextlib
import datetime as dt
import importlib.util
import json
import io
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("read_local_ui_report", Path(__file__).parents[1] / "read_local_ui_report.py")
reader = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(reader)


class LocalUiReportTests(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 11, 11, 12, tzinfo=dt.timezone.utc)
        self.report = {"formatVersion": 1, "context": "local-neutral-ui",
                       "startedUtc": "2026-11-11T11:59:00Z", "completedUtc": "2026-11-11T12:00:00Z",
                       "state": "passed", "facts": dict.fromkeys(reader.FACTS, True),
                       "exceptionHResult": None, "windowsProviders": "unverified",
                       "maintenance": "unverified", "physicalInput": "unverified"}

    def test_pass_is_only_neutral_ui_and_never_provider_or_action_proof(self):
        self.assertEqual(self.report, reader.validate(self.report, self.now))
        for field in ("windowsProviders", "maintenance", "physicalInput"):
            altered = dict(self.report, **{field: "passed"})
            with self.subTest(field=field), self.assertRaises(ValueError):
                reader.validate(altered, self.now)

    def test_missing_failed_and_exception_facts_cannot_be_claimed_passed(self):
        for value, expected in ((None, "inconclusive"), (False, "failed")):
            report = copy.deepcopy(self.report)
            report["facts"]["navigationObserved"] = value
            with self.assertRaises(ValueError): reader.validate(report, self.now)
            report["state"] = expected
            self.assertEqual(expected, reader.validate(report, self.now)["state"])
        self.report.update(exceptionHResult=0)
        with self.assertRaises(ValueError): reader.validate(self.report, self.now)
        self.report["state"] = "failed"
        self.assertEqual("failed", reader.validate(self.report, self.now)["state"])

    def test_scope_fields_types_and_dependencies_are_closed(self):
        for field, value in (("formatVersion", True), ("formatVersion", 2), ("context", "windows-ci"),
                             ("exceptionHResult", True), ("exceptionHResult", 2**31)):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                reader.validate(dict(self.report, **{field: value}), self.now)
        for key in ("windowObserved", "initialNeutral", "windowClosed"):
            report = copy.deepcopy(self.report)
            report["facts"][key] = None
            report["state"] = "inconclusive"
            with self.subTest(fact=key), self.assertRaises(ValueError): reader.validate(report, self.now)
        for value in (1, "true", [], {}):
            report = copy.deepcopy(self.report)
            report["facts"]["navigationObserved"] = value
            with self.assertRaises(ValueError): reader.validate(report, self.now)

    def test_time_checks_preserve_dotnet_tick_precision_and_duration_bound(self):
        report = dict(self.report, startedUtc="2026-11-11T11:50:00Z")
        self.assertEqual("passed", reader.validate(report, self.now)["state"])
        for field, value in (("startedUtc", "2026-11-11T11:49:59.9999999Z"),
                             ("completedUtc", "2026-11-11T12:00:00.0000001Z"),
                             ("startedUtc", "2026-11-11T12:00:01Z"),
                             ("startedUtc", "2026-11-11T11:59:00-06:00"),
                             ("startedUtc", "0001-01-01T00:00:00Z")):
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                reader.validate(dict(report, **{field: value}), self.now)

    def test_read_rejects_duplicate_fields_oversize_and_nonfiles(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text('{"formatVersion":1,"formatVersion":1}')
            with self.assertRaises(ValueError): reader.read(path)
            path.write_text("x" * 16_385)
            with self.assertRaises(ValueError): reader.read(path)
            with self.assertRaises(ValueError): reader.read(Path(directory))
            with self.assertRaises(ValueError): reader.read("//server/share/report.json")

    def test_linked_reports_are_rejected_without_relying_on_json_contents(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(self.report))
            linked = Path(directory) / "linked.json"
            try: linked.symlink_to(path)
            except (OSError, NotImplementedError): self.skipTest("Symbolic links cannot be created by this test account.")
            with self.assertRaises(ValueError): reader.read(linked)

    def test_reader_round_trip_reports_only_scoped_state_and_observation_time(self):
        now = dt.datetime.now(dt.timezone.utc)
        report = dict(self.report, startedUtc=(now - dt.timedelta(minutes=1)).isoformat(),
                      completedUtc=(now - dt.timedelta(seconds=1)).isoformat())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report))
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(0, reader.main([str(path)]))
            self.assertIn("Local neutral UI report: passed", output.getvalue())
            self.assertIn("source/build authenticity", output.getvalue())
            self.assertIn("remain unverified", output.getvalue())
            self.assertNotIn(str(path), output.getvalue())
