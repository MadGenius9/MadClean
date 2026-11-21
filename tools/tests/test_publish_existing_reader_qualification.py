"""Owned local Git evidence fixtures only; remote pushes are intercepted, never run."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "existing_reader_publisher", Path(__file__).resolve().parents[1] / "publish_existing_reader_qualification.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def report():
    cases = []
    for label in ("valid", "missing-lease", "missing-journal", "extra-file", "extra-directory", "case-mismatch", "missing-lf"):
        observation = {key: "Confirmed" for key in ("prepare", "directory", "lease", "journal", "exercise", "release")}
        observation.update(stopObserved=False, names="Complete")
        if label == "missing-lease":
            observation.update(lease="Refused", journal="NotEntered", exercise="NotEntered", names="NotObserved")
        elif label == "missing-journal":
            observation.update(journal="Refused", exercise="NotEntered", names="NotObserved")
        elif label != "valid":
            observation.update(exercise="Refused", names="Complete" if label == "missing-lf" else "Refused")
        cases.append({"label": label, "state": "passed", "setupConfirmed": True, "observation": observation,
                      "sampleVerified": label == "valid", "resourcesReleased": True, "hasPendingWork": False,
                      "requiresRetention": False, "boundaryMatched": True, "independentComparison": True,
                      "teardownConfirmed": True})
    return {"formatVersion": 1, "context": "existing-journal-reader-qualification", "sourceCommit": "a" * 40,
            "sourceRef": p.SOURCE_REF, "runId": "100", "runAttempt": 1, "job": "windows-existing-journal-reader",
            "startedUtc": "2026-11-21T00:00:00+00:00", "completedUtc": "2026-11-21T00:00:01+00:00",
            "state": "passed", "timeoutObserved": False, "ownershipSettled": True, "windowsBuild": 26100,
            "processX64": True, "cases": cases, "sourceAuthenticity": "unverified", "reparseCoverage": "unverified"}


class ExistingReaderPublisherTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="MadCleanExistingReaderPublisher-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name) / "verification"
        self.input = Path(directory.name) / "report.json"
        self.root.mkdir()
        self.git("init", "-q", "-b", p.BRANCH)
        self.git("remote", "add", "origin", f"https://github.com/{p.REPOSITORY}.git")
        self.latest = b'{"ordinary":"latest remains unchanged"}\n'
        self.ordinary = b'{"ordinary":"report remains unchanged"}\n'
        (self.root / "latest.json").write_bytes(self.latest)
        (self.root / "report.json").write_bytes(self.ordinary)
        self.git("add", "--", "latest.json", "report.json")
        self.commit("Initial owned fixture")
        self.env = {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted",
                    "GITHUB_REPOSITORY": p.REPOSITORY, "GITHUB_REF": p.SOURCE_REF,
                    "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_JOB": p.JOB,
                    "MADCLEAN_EXISTING_READER_QUALIFICATION": "1", "GITHUB_SHA": "a" * 40,
                    "GITHUB_RUN_ID": "100", "GITHUB_RUN_ATTEMPT": "1"}
        self.data = report()
        self.save()

    def git(self, *arguments):
        return subprocess.run(["git", "-C", str(self.root), *arguments], check=True,
                              text=True, capture_output=True).stdout.strip()

    def commit(self, message):
        return self.git("-c", "user.name=Owned fixture", "-c", "user.email=fixture@example.invalid",
                        "commit", "-qm", message)

    def save(self):
        self.input.write_text(json.dumps(self.data), encoding="utf-8")

    def unchanged(self, action):
        before = self.git("rev-parse", "HEAD")
        files = {x.name: x.read_bytes() for x in self.root.iterdir() if x.is_file() and not x.is_symlink()}
        with self.assertRaises((p.PublicationError, OSError)):
            action()
        self.assertEqual(before, self.git("rev-parse", "HEAD"))
        self.assertEqual(files, {x.name: x.read_bytes() for x in self.root.iterdir() if x.is_file() and not x.is_symlink()})

    def test_valid_evidence_changes_only_its_owned_file_and_does_not_push_by_default(self):
        real = p._git
        calls = []
        def observed(root, *arguments):
            calls.append(arguments)
            self.assertNotEqual("push", arguments[0])
            return real(root, *arguments)
        with patch.object(p, "_git", side_effect=observed):
            outcome = p.publish(self.root, self.input, environment=self.env)
        self.assertEqual({"changed": True, "pushed": False, "state": "passed"}, outcome)
        self.assertEqual(self.data, json.loads((self.root / p.TARGET).read_text()))
        self.assertEqual(p.TARGET, self.git("diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"))
        self.assertEqual(self.latest, (self.root / "latest.json").read_bytes())
        self.assertEqual(self.ordinary, (self.root / "report.json").read_bytes())
        self.assertEqual("", self.git("status", "--porcelain"))
        self.assertEqual(1, sum(x[:1] == ("add",) for x in calls))

    def test_timeout_remains_inconclusive_even_with_late_completed_cases(self):
        self.data.update(state="inconclusive", timeoutObserved=True)
        self.save()
        result = p.publish(self.root, self.input, environment=self.env)
        published = json.loads((self.root / p.TARGET).read_text())
        self.assertEqual("inconclusive", result["state"])
        self.assertTrue(published["timeoutObserved"])
        self.assertEqual(self.data["cases"], published["cases"])

    def test_unexpected_acceptance_remains_failed_without_rewriting_its_facts(self):
        case = self.data["cases"][-1]  # A missing-LF seed unexpectedly accepted by the tested reader.
        case.update(state="failed", sampleVerified=True, boundaryMatched=False)
        case["observation"]["exercise"] = "Confirmed"
        self.data["state"] = "failed"
        self.save()
        result = p.publish(self.root, self.input, environment=self.env)
        self.assertEqual("failed", result["state"])
        self.assertEqual(self.data, json.loads((self.root / p.TARGET).read_text()))

    def test_identical_report_is_noop_without_an_extra_commit_or_push(self):
        p.publish(self.root, self.input, environment=self.env)
        before = self.git("rev-parse", "HEAD")
        result = p.publish(self.root, self.input, environment=self.env, push=True)
        self.assertEqual({"changed": False, "pushed": False, "state": "passed"}, result)
        self.assertEqual(before, self.git("rev-parse", "HEAD"))

    def test_context_mismatches_refuse_before_git_or_file_mutation(self):
        for key, value in (("GITHUB_EVENT_NAME", "push"), ("GITHUB_JOB", "windows-existing-journal-reader"),
                           ("GITHUB_REPOSITORY", "another/repository"), ("GITHUB_REF", "refs/heads/main"),
                           ("GITHUB_ACTIONS", "false"), ("RUNNER_ENVIRONMENT", "self-hosted"),
                           ("MADCLEAN_EXISTING_READER_QUALIFICATION", "true"), ("GITHUB_RUN_ID", "0"),
                           ("GITHUB_RUN_ATTEMPT", "01"), ("GITHUB_SHA", "not-a-sha")):
            with self.subTest(key=key):
                environment = dict(self.env, **{key: value})
                with patch.object(p, "_git") as git:
                    self.unchanged(lambda: p.publish(self.root, self.input, environment=environment))
                    git.assert_not_called()

    def test_report_source_run_attempt_mismatches_do_not_overwrite_existing_target(self):
        p.publish(self.root, self.input, environment=self.env)
        for key, value in (("sourceCommit", "b" * 40), ("runId", "101"), ("runAttempt", 2)):
            with self.subTest(key=key):
                changed = copy.deepcopy(self.data)
                changed[key] = value
                self.input.write_text(json.dumps(changed))
                self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))

    def test_private_fields_and_duplicate_keys_never_reach_the_checkout(self):
        self.data["path"] = "private-provider-data"
        self.save()
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))
        self.data.pop("path")
        self.input.write_text(json.dumps(self.data)[:-1] + ',"state":"passed"}')
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))

    def test_linked_target_does_not_change_its_destination(self):
        outside = self.root.parent / "outside.json"
        outside.write_bytes(b"owned outside sentinel")
        try:
            (self.root / p.TARGET).symlink_to(outside)
        except OSError:
            self.skipTest("Symbolic link creation unavailable")
        self.git("add", "--", p.TARGET)
        self.commit("Owned link refusal fixture")
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))
        self.assertEqual(b"owned outside sentinel", outside.read_bytes())

    def test_wrong_checkout_branch_or_nested_destination_does_not_receive_files(self):
        self.git("switch", "-qc", "other")
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))
        self.git("switch", "-q", p.BRANCH)
        nested = self.root / "nested"
        nested.mkdir()
        self.unchanged(lambda: p.publish(nested, self.input, environment=self.env))
        self.assertFalse((nested / p.TARGET).exists())

    def test_report_inside_checkout_and_unexpected_origin_are_refused(self):
        inside = self.root / "inside.json"
        inside.write_bytes(self.input.read_bytes())
        self.git("add", "--", "inside.json")
        self.commit("Owned report-location fixture")
        self.unchanged(lambda: p.publish(self.root, inside, environment=self.env))
        self.git("remote", "set-url", "origin", "https://github.com/another/repository.git")
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))

    def test_oversize_report_is_rejected_before_checkout_mutation(self):
        self.input.write_bytes(b" " * (p.MAX_REPORT_BYTES + 1))
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))

    def test_dirty_checkout_is_not_committed_alongside_the_report(self):
        (self.root / "latest.json").write_bytes(b"owned uncommitted change")
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env))
        self.assertFalse((self.root / p.TARGET).exists())

    def test_explicit_push_is_one_fixed_nonforce_operation(self):
        real = p._git
        pushes = []
        def intercepted(root, *arguments):
            if arguments[0] == "push":
                pushes.append(arguments)
                return ""
            return real(root, *arguments)
        with patch.object(p, "_git", side_effect=intercepted):
            result = p.publish(self.root, self.input, environment=self.env, push=True)
        self.assertTrue(result["pushed"])
        self.assertEqual([("push", "origin", "HEAD:refs/heads/" + p.BRANCH)], pushes)

    def test_uncertain_push_is_not_repeated_and_local_commit_remains_inspectable(self):
        real = p._git
        pushes = []
        before = self.git("rev-parse", "HEAD")
        def interrupted(root, *arguments):
            if arguments[0] == "push":
                pushes.append(arguments)
                raise p.PublicationError("git-outcome-uncertain")
            return real(root, *arguments)
        with patch.object(p, "_git", side_effect=interrupted):
            with self.assertRaisesRegex(p.PublicationError, "git-outcome-uncertain"):
                p.publish(self.root, self.input, environment=self.env, push=True)
        self.assertEqual(1, len(pushes))
        self.assertNotEqual(before, self.git("rev-parse", "HEAD"))
        self.assertEqual(self.latest, (self.root / "latest.json").read_bytes())
        self.assertTrue((self.root / p.TARGET).exists())

    def test_missing_primary_may_publish_only_conservative_initial_snapshot(self):
        fallback = self.input.with_name("report-timeout.json")
        initial = report()
        initial.update(state="inconclusive", timeoutObserved=True, ownershipSettled=False, cases=[])
        fallback.write_text(json.dumps(initial))
        self.input.unlink()
        result = p.publish(self.root, self.input, environment=self.env, fallback_report=fallback)
        self.assertEqual("inconclusive", result["state"])
        self.assertEqual(initial, json.loads((self.root / p.TARGET).read_text()))

    def test_invalid_present_primary_never_falls_back_to_a_different_observation(self):
        fallback = self.input.with_name("report-timeout.json")
        initial = report()
        initial.update(state="inconclusive", ownershipSettled=False, cases=[])
        fallback.write_text(json.dumps(initial))
        self.input.write_text("invalid-json")
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env, fallback_report=fallback))

    def test_fallback_cannot_claim_completed_case_or_passed_qualification(self):
        fallback = self.input.with_name("report-timeout.json")
        fallback.write_text(json.dumps(report()))
        self.input.unlink()
        self.unchanged(lambda: p.publish(self.root, self.input, environment=self.env, fallback_report=fallback))


if __name__ == "__main__":
    unittest.main()
