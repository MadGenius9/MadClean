import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("ci_summary", Path(__file__).resolve().parents[1] / "write_ci_readiness_summary.py")
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)
CONTEXT = {"source": "a" * 40, "run": "100", "attempt": 1}


def evidence(passed=90, inconclusive=10, failed=0):
    state = "failed" if failed else "inconclusive" if inconclusive else "passed"
    latest = {"formatVersion": 1, "sourceCommit": CONTEXT["source"], "runId": "100", "runAttempt": 1,
              "buildResult": "success", "verificationResult": "success" if state == "passed" else "failure",
              "passed": state == "passed", "stageOutcomes": {key: "success" for key, _ in r.STAGES}}
    report = {"formatVersion": 7, "sourceCommit": CONTEXT["source"], "runId": "100", "runAttempt": 1,
              "passed": state == "passed", "state": state, "checks": []}
    for status, count in (("passed", passed), ("inconclusive", inconclusive), ("failed", failed)):
        report["checks"].extend({"name": "private observation", "state": status, "detail": "private value"} for _ in range(count))
    return latest, report


class CiReadinessSummaryTests(unittest.TestCase):
    def test_partial_native_qualification_is_distinguished_from_build_failure(self):
        text = r.render_summary(*evidence(), **CONTEXT)
        self.assertIn("Build and unit-test stages passed; Windows qualification is incomplete", text)
        self.assertIn("90 passed, 10 inconclusive, 0 failed", text)
        self.assertIn("not release approval", text)

    def test_context_cannot_be_borrowed_from_another_run_source_or_attempt(self):
        for document in (0, 1):
            for field, value in (("sourceCommit", "b" * 40), ("runId", "101"), ("runAttempt", 2), ("runAttempt", True)):
                data = evidence()
                data[document][field] = value
                with self.subTest(document=document, field=field), self.assertRaises(ValueError):
                    r.render_summary(*data, **CONTEXT)

    def test_empty_excessive_unknown_and_hidden_failed_checks_are_rejected(self):
        for checks, state in (([], "passed"), ([{"state": "passed", "name": "n", "detail": "d"}] * 201, "passed"),
                              ([{"state": "unknown", "name": "n", "detail": "d"}], "inconclusive"),
                              ([{"state": "failed", "name": "n", "detail": "d"}], "inconclusive")):
            latest, report = evidence()
            report.update(checks=checks, state=state, passed=state == "passed")
            with self.subTest(state=state, count=len(checks)), self.assertRaises(ValueError):
                r.render_summary(latest, report, **CONTEXT)

    def test_failed_checks_take_precedence_over_inconclusive(self):
        text = r.render_summary(*evidence(2, 3, 1), **CONTEXT)
        self.assertIn("Windows qualification found failed checks", text)
        self.assertIn("2 passed, 3 inconclusive, 1 failed", text)

    def test_report_pass_flag_and_overall_pass_cannot_overstate_evidence(self):
        for mutate in (lambda a, b: b.update(passed=True), lambda a, b: a.update(passed=True)):
            latest, report = evidence()
            mutate(latest, report)
            with self.assertRaises(ValueError):
                r.render_summary(latest, report, **CONTEXT)

    def test_overall_readiness_false_is_preserved_even_with_all_native_checks_passed(self):
        latest, report = evidence(5, 0, 0)
        latest["passed"] = False
        text = r.render_summary(latest, report, **CONTEXT)
        self.assertIn("overall Windows qualification is not established", text)
        self.assertNotIn("Windows qualification passed", text)

    def test_cancelled_unit_stage_never_claims_build_and_unit_pass(self):
        latest, report = evidence()
        latest["stageOutcomes"]["core-tests"] = "cancelled"
        text = r.render_summary(latest, report, **CONTEXT)
        self.assertIn("have not all passed", text)
        self.assertIn("| Core tests | cancelled |", text)

    def test_arbitrary_provider_text_cannot_reach_markdown(self):
        latest, report = evidence()
        payload = "\n## FORGED PASS\nhttps://secret.invalid/token"
        latest["extra"] = payload
        report["checks"][0].update(name=payload, detail=payload)
        report["extra"] = payload
        self.assertNotIn(payload, r.render_summary(latest, report, **CONTEXT))
        latest["stageOutcomes"]["build"] = payload
        with self.assertRaises(ValueError):
            r.render_summary(latest, report, **CONTEXT)

    def test_missing_unknown_stage_and_unsupported_format_do_not_become_zero_counts(self):
        for mutation in (lambda a, b: a["stageOutcomes"].pop("build"),
                         lambda a, b: a["stageOutcomes"].update(build="unknown"),
                         lambda a, b: b.update(formatVersion=True)):
            data = evidence()
            mutation(*data)
            with self.assertRaises(ValueError):
                r.render_summary(*data, **CONTEXT)

    def test_cli_displays_valid_incomplete_evidence_without_changing_failure_status(self):
        with tempfile.TemporaryDirectory() as folder:
            latest_path, report_path = Path(folder) / "latest.json", Path(folder) / "report.json"
            latest, report = evidence()
            latest_path.write_text(json.dumps(latest))
            report_path.write_text(json.dumps(report))
            args = ["--latest", str(latest_path), "--report", str(report_path), "--source", CONTEXT["source"], "--run", "100", "--attempt", "1"]
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = r.main(args)
            self.assertEqual(0, code)
            self.assertIn("90 passed, 10 inconclusive, 0 failed", output.getvalue())
            report_path.write_text('{"formatVersion":7,"formatVersion":7}')
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = r.main(args)
            self.assertEqual(2, code)
            self.assertEqual(r.UNAVAILABLE + "\n", output.getvalue())

    def test_cli_shows_failed_stage_when_published_report_is_absent(self):
        with tempfile.TemporaryDirectory() as folder:
            latest_path, report_path = Path(folder) / "latest.json", Path(folder) / "report.json"
            latest, _ = evidence()
            latest.update(buildResult="failure", verificationResult="skipped", reportPresent=False)
            latest["stageOutcomes"].update({"publisher-tests": "failure", "core-tests": "skipped", "publish": "skipped"})
            latest_path.write_text(json.dumps(latest), encoding="utf-8")
            args = ["--latest", str(latest_path), "--report", str(report_path), "--source", CONTEXT["source"],
                    "--run", CONTEXT["run"], "--attempt", "1"]
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = r.main(args)
            self.assertEqual(0, code)
            self.assertIn("| Build | success |", output.getvalue())
            self.assertIn("| Publisher tests | failure |", output.getvalue())
            self.assertIn("| Windows qualification job | skipped |", output.getvalue())
            self.assertIn("Windows check counts are unavailable.", output.getvalue())

            report_path.write_text("null", encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = r.main(args)
            self.assertEqual(2, code)
            self.assertEqual(r.UNAVAILABLE + "\n", output.getvalue())
            report_path.unlink()

            latest["reportPresent"] = True
            latest_path.write_text(json.dumps(latest), encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = r.main(args)
            self.assertEqual(2, code)
            self.assertEqual(r.UNAVAILABLE + "\n", output.getvalue())

    def test_input_limits_malformed_json_and_missing_paths_refuse(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "report.json"
            for value in (b" " * (r.MAX_BYTES + 1), b'{"checks":NaN}', b"not json"):
                path.write_bytes(value)
                with self.assertRaises(ValueError):
                    r.read_json(path)
            with self.assertRaises(OSError):
                r.read_json(Path(folder) / "missing.json")


if __name__ == "__main__":
    unittest.main()
