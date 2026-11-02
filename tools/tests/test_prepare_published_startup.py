import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

MODULE = Path(__file__).resolve().parents[1] / 'prepare_published_startup.py'
spec = importlib.util.spec_from_file_location('prepare_published_startup', MODULE)
prep = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prep)


class PublishedStartupPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'src' / 'MadClean.App').mkdir(parents=True)
        (self.root / 'tools').mkdir()
        (self.root / 'global.json').write_text('{}', encoding='ascii')
        (self.root / prep.APP_PROJECT).write_text('<Project/>', encoding='ascii')
        (self.root / 'tools' / 'ci_command.py').write_text('# owned test placeholder', encoding='ascii')
        self.environment = {'GITHUB_SHA': 'A' * 40}
        self.calls = []

    def success(self, label, command, root, environment):
        self.calls.append((label, command, root, environment))
        output = Path(command[-1])
        self.assertEqual(command[-2], '-o')
        self.assertEqual(output.parent.parent, self.root / 'artifacts')
        (output / 'MadClean.exe').write_bytes(b'Explicit inert unit fixture; never executed.')
        (output / 'MadClean.pdb').write_bytes(b'Ordinary owned sidecar.')
        return 0

    def test_success_publishes_descriptor_only_for_valid_owned_output(self):
        self.assertEqual(prep._prepare(self.root, self.environment, self.success), 0)
        descriptor = self.root / 'artifacts' / prep.DESCRIPTOR_NAME
        raw = descriptor.read_bytes()
        self.assertLess(len(raw), 512)
        data = json.loads(raw)
        self.assertEqual(set(data), {'SourceCommit', 'ScopeId', 'OwnerMarker'})
        self.assertEqual(data['SourceCommit'], 'a' * 40)
        scope = self.root / 'artifacts' / ('published-startup-' + data['ScopeId'])
        self.assertEqual((scope / prep.MARKER_NAME).read_bytes(), data['OwnerMarker'].encode('ascii'))
        self.assertEqual(len(data['OwnerMarker']), 32)
        self.assertEqual(list((scope / 'bundle-cache').iterdir()), [])
        label, command, root, environment = self.calls[0]
        self.assertEqual(label, 'startup-publish')
        self.assertEqual(command[:3], ['dotnet', 'publish', str(prep.APP_PROJECT)])
        self.assertIn('-p:RestoreLockedMode=true', command)
        self.assertIn('-p:PublishSingleFile=true', command)
        self.assertIn('-p:PublishTrimmed=false', command)
        self.assertIn('-p:IncludeNativeLibrariesForSelfExtract=true', command)
        self.assertIn('-p:MadCleanPackage=true', command)
        self.assertEqual(root, self.root)
        self.assertEqual(environment, self.environment)
        self.assertFalse(any(scope.rglob('*.zip')))

    def test_stale_descriptor_is_preserved_and_prevents_any_publish_or_new_scope(self):
        artifacts = self.root / 'artifacts'; artifacts.mkdir()
        descriptor = artifacts / prep.DESCRIPTOR_NAME
        descriptor.write_bytes(b'Untrusted stale descriptor sentinel')
        with self.assertRaises(FileExistsError):
            prep._prepare(self.root, self.environment, self.success)
        self.assertEqual(descriptor.read_bytes(), b'Untrusted stale descriptor sentinel')
        self.assertEqual(list(artifacts.iterdir()), [descriptor])
        self.assertEqual(self.calls, [])

    def test_bad_source_or_missing_required_project_causes_no_artifact_creation(self):
        for source in (None, '', 'a' * 39, 'g' * 40, 'a' * 40 + '\n', '$(untrusted)'):
            with self.assertRaises(ValueError):
                prep._prepare(self.root, {'GITHUB_SHA': source}, self.success)
            self.assertFalse((self.root / 'artifacts').exists())
        (self.root / prep.APP_PROJECT).unlink()
        with self.assertRaises(FileNotFoundError):
            prep._prepare(self.root, self.environment, self.success)
        self.assertFalse((self.root / 'artifacts').exists())
        self.assertEqual(self.calls, [])

    def test_failed_publish_retains_owned_scope_and_never_writes_descriptor(self):
        def failed(*arguments):
            self.success(*arguments)
            return 13
        self.assertEqual(prep._prepare(self.root, self.environment, failed), 13)
        artifacts = self.root / 'artifacts'
        self.assertFalse((artifacts / prep.DESCRIPTOR_NAME).exists())
        scopes = list(artifacts.glob('published-startup-*'))
        self.assertEqual(len(scopes), 1)
        self.assertTrue((scopes[0] / 'publish' / 'MadClean.exe').is_file())
        self.assertEqual(len((scopes[0] / prep.MARKER_NAME).read_bytes()), 32)

    def test_exit_zero_without_executable_or_changed_marker_never_certifies_preparation(self):
        def missing(label, command, root, environment):
            return 0
        with self.assertRaises(FileNotFoundError):
            prep._prepare(self.root, self.environment, missing)
        self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR_NAME).exists())
        def changed(*arguments):
            self.success(*arguments)
            output = Path(arguments[1][-1])
            (output.parent / prep.MARKER_NAME).write_bytes(b'x' * 32)
            return 0
        with self.assertRaises(ValueError):
            prep._prepare(self.root, self.environment, changed)
        self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR_NAME).exists())

    def test_bundle_cache_must_stay_empty_until_startup_fixture(self):
        def changed(*arguments):
            self.success(*arguments)
            output = Path(arguments[1][-1])
            (output.parent / 'bundle-cache' / 'unexpected').write_bytes(b'x')
            return 0
        with self.assertRaises(ValueError):
            prep._prepare(self.root, self.environment, changed)
        self.assertFalse((self.root / 'artifacts' / prep.DESCRIPTOR_NAME).exists())

    def test_exclusive_descriptor_write_cannot_truncate_existing_bytes(self):
        target = self.root / 'descriptor'
        target.write_bytes(b'Keep original')
        with self.assertRaises(FileExistsError):
            prep._exclusive_bytes(target, b'Replacement')
        self.assertEqual(target.read_bytes(), b'Keep original')

    def test_oversize_and_deep_owned_output_are_rejected_without_unbounded_traversal(self):
        output = self.root / 'output'; output.mkdir()
        with mock.patch.object(prep, 'MAX_ENTRIES', 2):
            for name in ('one', 'two', 'three'):
                (output / name).write_bytes(b'x')
            with self.assertRaises(ValueError):
                prep._validate_tree(output)
        nested = self.root / 'nested'; nested.mkdir(); current = nested
        for index in range(prep.MAX_DEPTH + 1):
            current = current / str(index); current.mkdir()
        with self.assertRaises(ValueError):
            prep._validate_tree(nested)

    def test_symlink_in_ancestry_or_owned_publish_is_not_followed(self):
        if not hasattr(os, 'symlink'):
            self.skipTest('Symlink creation unsupported.')
        outside = self.root / 'outside'; outside.mkdir()
        link = self.root / 'link'
        try:
            link.symlink_to(outside, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest('Symlink creation unavailable without additional permission.')
        with self.assertRaises(ValueError):
            prep._ancestry(link)
        with self.assertRaises(ValueError):
            prep._validate_tree(self.root)

    def test_no_arguments_or_non_windows_path_can_start_publish(self):
        with mock.patch.object(prep.sys, 'argv', ['prepare_published_startup.py', '--output=arbitrary']), mock.patch.object(prep, '_prepare') as run:
            self.assertEqual(prep.main(), 1)
            run.assert_not_called()
        if os.name != 'nt':
            with mock.patch.object(prep.sys, 'argv', ['prepare_published_startup.py']), mock.patch.object(prep, '_prepare') as run:
                self.assertEqual(prep.main(), 1)
                run.assert_not_called()


if __name__ == '__main__':
    unittest.main()
