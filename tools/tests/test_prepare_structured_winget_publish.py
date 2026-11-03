import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'prepare_structured_winget_publish.py'
spec = importlib.util.spec_from_file_location('prepare_structured_winget_publish', MODULE)
prep = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prep)


class PublishedSdkPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / prep.PROJECT).parent.mkdir(parents=True)
        (self.root / prep.PROJECT).write_text('<Project/>')
        (self.root / 'global.json').write_text('{}')
        (self.root / 'tools/ci_command.py').write_text('# inert fixture')
        self.environment = {'GITHUB_SHA': 'a' * 40, 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1'}
        self.calls = []

    def success(self, label, command, root, environment):
        self.calls.append((label, command))
        (Path(command[-1]) / prep.EXE).write_bytes(b'Inert fixture; never executable.')
        (Path(command[-1]) / 'Microsoft.Management.Deployment.winmd').write_bytes(b'Inert metadata shape fixture; never loaded.')
        return 0

    def test_exact_existing_harness_locked_self_contained_profile_and_bound_context(self):
        self.assertEqual(0, prep._prepare(self.root, self.environment, self.success))
        label, command = self.calls[0]
        self.assertEqual('structured-winget-publish', label)
        self.assertEqual(str(prep.PROJECT), command[2])
        for value in ('-p:RestoreLockedMode=true', '-p:MadCleanPackage=true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false'):
            self.assertIn(value, command)
        descriptor = json.loads((self.root / 'artifacts' / prep.DESCRIPTOR).read_bytes())
        self.assertEqual(('a' * 40, '123', 1), (descriptor['sourceCommit'], descriptor['runId'], descriptor['runAttempt']))
        owned = self.root / 'artifacts' / (prep.PREFIX + descriptor['scopeId'])
        self.assertEqual(descriptor['ownerMarker'].encode('ascii'), (owned / prep.MARKER).read_bytes())
        self.assertEqual([], list((owned / 'bundle-cache').iterdir()))
        self.assertFalse(any(owned.rglob('*.zip')))

    def test_build_success_without_required_metadata_never_authorizes_launch(self):
        def missing(label, command, root, environment):
            self.success(label, command, root, environment)
            (Path(command[-1]) / 'Microsoft.Management.Deployment.winmd').unlink()
            return 0
        with self.assertRaises(FileNotFoundError):
            prep._prepare(self.root, self.environment, missing)
        self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR).exists())

    def test_empty_metadata_sidecar_never_authorizes_launch(self):
        def empty(label, command, root, environment):
            self.success(label, command, root, environment)
            (Path(command[-1]) / 'Microsoft.Management.Deployment.winmd').write_bytes(b'')
            return 0
        with self.assertRaises(ValueError):
            prep._prepare(self.root, self.environment, empty)
        self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR).exists())

    def test_stale_descriptor_is_preserved_before_any_publish(self):
        artifacts = self.root / 'artifacts'; artifacts.mkdir()
        descriptor = artifacts / prep.DESCRIPTOR; descriptor.write_bytes(b'Untrusted old report')
        with self.assertRaises(FileExistsError):
            prep._prepare(self.root, self.environment, self.success)
        self.assertEqual(b'Untrusted old report', descriptor.read_bytes())
        self.assertEqual([], self.calls)

    def test_run_context_rejection_has_no_output_side_effect(self):
        for key, value in [('GITHUB_RUN_ID', '01'), ('GITHUB_RUN_ATTEMPT', '0'), ('GITHUB_RUN_ID', '../secret'), ('GITHUB_SHA', 'g' * 40)]:
            environment = dict(self.environment); environment[key] = value
            with self.assertRaises(ValueError):
                prep._prepare(self.root, environment, self.success)
            self.assertFalse((self.root / 'artifacts').exists())

    def test_failed_publish_never_establishes_descriptor(self):
        def failed(*args):
            self.success(*args)
            return 9
        self.assertEqual(9, prep._prepare(self.root, self.environment, failed))
        self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR).exists())
        self.assertEqual(1, len(list((self.root / 'artifacts').glob(prep.PREFIX + '*'))))

    def test_changed_marker_or_nonempty_cache_refuses_authorization(self):
        for change in ('marker', 'cache'):
            def corrupt(label, command, root, environment):
                self.success(label, command, root, environment)
                owned = Path(command[-1]).parent
                if change == 'marker':
                    (owned / prep.MARKER).write_bytes(b'changed')
                else:
                    (owned / 'bundle-cache' / 'unexpected').write_bytes(b'inert')
                return 0
            with self.assertRaises(ValueError):
                prep._prepare(self.root, self.environment, corrupt)
            self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR).exists())
