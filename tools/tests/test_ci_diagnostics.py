"""Source-bound development-log fixtures only; no commands, remote operations or production files."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("ci_diagnostics_under_test", Path(__file__).resolve().parents[1] / "ci_diagnostics.py")
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class CiDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="MadCleanCiDiagnostics-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "downloaded"
        self.root.mkdir()
        self.context = ("a" * 40, "12345", 2)

    def stage(self, label="build", text="Project.cs(12,4): error CS0103: The name does not exist.\nBuild FAILED.\n", **changes):
        metadata = {"formatVersion": 1, "label": label, "sourceCommit": self.context[0], "runId": self.context[1],
                    "runAttempt": self.context[2], "exitCode": 1, "truncated": False, "scope": c.CAPTURE_SCOPE}
        metadata.update(changes)
        (self.root / (label + ".json")).write_text(json.dumps(metadata), encoding="utf-8")
        (self.root / (label + ".log")).write_text(text, encoding="utf-8")
        return metadata

    def collect(self):
        return c.collect(self.root, self.context, "success")

    def test_actual_exit_and_compiler_context_are_retained_without_pass_claims_or_printing(self):
        self.stage()
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(captured):
            result = self.collect()
        self.assertEqual("", captured.getvalue())
        self.assertEqual(1, result["stages"][0]["exitCode"])
        self.assertIn("error CS0103", result["stages"][0]["excerpt"])
        self.assertIn("not a test count", result["scope"])
        self.assertNotIn("passed", result)
        self.assertEqual({"label", "exitCode", "truncated", "excerpt"}, set(result["stages"][0]))

    def test_failed_skipped_or_cancelled_download_never_reads_even_valid_leftovers(self):
        self.stage()
        with patch.object(c, "_safe", side_effect=AssertionError("No filesystem probe allowed")):
            for outcome in ("failure", "cancelled", "skipped"):
                self.assertIsNone(c.collect(self.root, self.context, outcome))

    def test_no_directory_or_no_pairs_does_not_invent_a_successful_stage(self):
        self.assertIsNone(c.collect(None, self.context, "success"))
        self.assertIsNone(c.collect(self.root / "missing", self.context, "success"))
        self.assertIsNone(self.collect())

    def test_empty_output_with_zero_exit_is_explicitly_not_runtime_or_test_proof(self):
        self.stage(text="", exitCode=0)
        stage = self.collect()["stages"][0]
        self.assertEqual(0, stage["exitCode"])
        self.assertIn("No command output", stage["excerpt"])
        self.assertIn("does not establish tests", stage["excerpt"])
        self.assertNotIn("passed", stage)

    def test_source_run_or_attempt_mismatch_rejects_metadata_before_log_content(self):
        for changes in ({"sourceCommit": "b" * 40}, {"runId": "54321"}, {"runAttempt": 1}):
            self.stage(**changes)
            original = c._read
            def guarded(path, maximum):
                self.assertNotEqual(".log", path.suffix)
                return original(path, maximum)
            with patch.object(c, "_read", side_effect=guarded), self.assertRaisesRegex(ValueError, "^ci-metadata-context$"):
                self.collect()

    def test_unexpected_metadata_keys_and_scope_cannot_export_private_payloads(self):
        for changes in ({"providerOutput": "private-payload"}, {"scope": "private-provider-data"}):
            self.stage(**changes)
            with self.assertRaises(ValueError) as raised:
                self.collect()
            self.assertNotIn("private", str(raised.exception))

    def test_unknown_or_mismatched_label_is_rejected_without_reading_arbitrary_filenames(self):
        self.stage()
        metadata = json.loads((self.root / "build.json").read_text())
        metadata["label"] = "arbitrary-private-file"
        (self.root / "build.json").write_text(json.dumps(metadata))
        with self.assertRaisesRegex(ValueError, "^ci-metadata-label$"):
            self.collect()

    def test_unlisted_files_are_not_opened_or_exported(self):
        self.stage()
        (self.root / "private-output.log").write_text("TEST-ONLY private output")
        (self.root / "private-output.json").write_text("malformed unrelated metadata")
        result = self.collect()
        self.assertEqual(["build"], [stage["label"] for stage in result["stages"]])
        self.assertNotIn("TEST-ONLY private output", json.dumps(result))

    def test_missing_one_half_of_a_stage_is_not_source_bound_evidence(self):
        self.stage()
        (self.root / "build.json").unlink()
        with self.assertRaisesRegex(ValueError, "^ci-incomplete-stage$"):
            self.collect()

    def test_oversized_log_or_metadata_is_refused_instead_of_reading_unbounded_output(self):
        self.stage()
        (self.root / "build.log").write_bytes(b"x" * (c.MAX_LOG_BYTES + 1))
        with self.assertRaisesRegex(ValueError, "^ci-file-size$"):
            self.collect()
        self.stage()
        (self.root / "build.json").write_bytes(b" " * (c.MAX_METADATA_BYTES + 1))
        with self.assertRaisesRegex(ValueError, "^ci-file-size$"):
            self.collect()

    def test_bad_utf8_ansi_controls_and_bidirectional_text_are_removed_before_scrubbing(self):
        self.stage()
        (self.root / "build.log").write_bytes(b"\x1b[31merror CS0001\x1b[0m\x00\naccess_tok\xffen=private-token\n" + "\u202esecret-hidden".encode())
        excerpt = self.collect()["stages"][0]["excerpt"]
        self.assertIn("error CS0001", excerpt)
        self.assertNotIn("\x1b", excerpt)
        self.assertNotIn("\x00", excerpt)
        self.assertNotIn("\u202e", excerpt)
        self.assertNotIn("\ufffd", excerpt)
        self.assertNotIn("private-token", excerpt)

    def test_credentials_queries_fragments_headers_assignments_and_known_tokens_are_redacted(self):
        values = ["user-secret", "query-secret", "fragment-secret", "header-secret", "api-secret", "cli-secret", "ghp_1234567890abcdefgh"]
        self.stage(text=("https://user:user-secret@api.nuget.org/v3/index.json?unknown=query-secret#fragment-secret\n"
                         "Authorization: Bearer header-secret\nAPI_KEY=api-secret\ncommand --password cli-secret\n"
                         "standalone ghp_1234567890abcdefgh\n"))
        excerpt = self.collect()["stages"][0]["excerpt"]
        for value in values:
            self.assertNotIn(value, excerpt)
        self.assertIn("api.nuget.org/v3/index.json", excerpt)
        self.assertIn("[redacted", excerpt)

    def test_username_only_url_credentials_and_signed_query_parameters_are_removed(self):
        self.stage(text="https://username-secret@github.com/owner/repo?sig=signed-secret&other=private-value\n")
        excerpt = self.collect()["stages"][0]["excerpt"]
        for value in ("username-secret", "signed-secret", "private-value"):
            self.assertNotIn(value, excerpt)

    def test_private_key_blocks_are_never_included_even_when_capture_ends_mid_block(self):
        self.stage(text="error CS0001\n-----BEGIN PRIVATE KEY-----\nprivate-key-body\n")
        excerpt = self.collect()["stages"][0]["excerpt"]
        self.assertNotIn("private-key-body", excerpt)
        self.assertIn("error CS0001", excerpt)

    def test_large_multiple_stage_excerpts_share_a_total_budget_and_disclose_omission(self):
        for label in c.LABELS:
            self.stage(label, text="\n".join("error CS0001: " + "x" * 600 for _ in range(150)))
        result = self.collect()
        self.assertEqual(len(c.LABELS), len(result["stages"]))
        self.assertLessEqual(sum(len(stage["excerpt"].encode()) for stage in result["stages"]), c.MAX_EXCERPT_BYTES)
        self.assertTrue(all(stage["truncated"] for stage in result["stages"]))
        self.assertTrue(all(len(stage["excerpt"].splitlines()) <= c.MAX_LINES for stage in result["stages"]))

    def test_traceback_is_preferred_over_verbose_unittest_error_labels(self):
        self.stage(text="\n".join([f"test_case_{i} ... ERROR" for i in range(100)])+
            "\nTraceback (most recent call last):\n  File \"test.py\", line 1\nEvidenceError: unexpected-worktree\nFAILED (errors=100)\n")
        excerpt=self.collect()['stages'][0]['excerpt']
        self.assertIn('EvidenceError: unexpected-worktree',excerpt)
        self.assertLessEqual(len(excerpt.splitlines()),c.MAX_LINES)
    def test_capture_truncation_remains_true_even_for_a_short_excerpt(self):
        self.stage(text="error CS0001\n", truncated=True)
        self.assertTrue(self.collect()["stages"][0]["truncated"])

    def test_metadata_numeric_types_are_not_coerced_into_apparently_valid_exit_or_attempt(self):
        for changes in ({"exitCode": True}, {"exitCode": "0"}, {"exitCode": 1.5}, {"runAttempt": True}, {"truncated": 1}, {"formatVersion": True}):
            self.stage(**changes)
            with self.assertRaises(ValueError):
                self.collect()

    def test_duplicate_metadata_keys_and_non_json_numeric_constants_are_refused(self):
        self.stage()
        for text in ('{"formatVersion":1,"formatVersion":1}', '{"exitCode":NaN}'):
            (self.root / "build.json").write_text(text)
            with self.assertRaises(ValueError):
                self.collect()

    @unittest.skipUnless(hasattr(os, "symlink"), "Requires symbolic links")
    def test_symlink_log_or_ancestor_is_not_followed(self):
        self.stage()
        outside = Path(self.temporary.name) / "outside.log"
        outside.write_text("TEST-ONLY outside data")
        (self.root / "build.log").unlink()
        try:
            (self.root / "build.log").symlink_to(outside)
        except OSError:
            self.skipTest("Symlink creation unavailable")
        with self.assertRaisesRegex(ValueError, "^ci-reparse-path$"):
            self.collect()
        self.assertEqual("TEST-ONLY outside data", outside.read_text())
        alias = Path(self.temporary.name) / "alias"
        alias.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "^ci-reparse-path$"):
            c.collect(alias, self.context, "success")


if __name__ == "__main__":
    unittest.main()
