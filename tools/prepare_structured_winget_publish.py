"""Prepare one owned published Smoke SDK query candidate; never launch or distribute it."""
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import uuid

_spec = importlib.util.spec_from_file_location('madclean_publish_scope_helpers', Path(__file__).with_name('prepare_published_startup.py'))
_scope = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_scope)
PROJECT = Path('tools/MadClean.WindowsSmoke/MadClean.WindowsSmoke.csproj')
DESCRIPTOR = 'structured-winget-published-descriptor.json'
MARKER = '.madclean-structured-winget-owner'
PREFIX = 'structured-winget-published-'
EXE = 'MadClean.WindowsSmoke.exe'


def _context(environment):
    source = _scope._source_commit(environment.get('GITHUB_SHA'))
    run = environment.get('GITHUB_RUN_ID')
    attempt = environment.get('GITHUB_RUN_ATTEMPT')
    if not isinstance(run, str) or re.fullmatch(r'[1-9][0-9]{0,31}', run) is None:
        raise ValueError('A canonical run identifier is required.')
    if not isinstance(attempt, str) or re.fullmatch(r'[1-9][0-9]{0,8}', attempt) is None:
        raise ValueError('A canonical attempt is required.')
    return source, run, int(attempt)


def _prepare(root, environment, runner):
    source, run, attempt = _context(environment)
    _scope._ancestry(root)
    for relative in (PROJECT, Path('global.json'), Path('tools/ci_command.py')):
        _scope._ancestry((root / relative).parent)
        _scope._ordinary(root / relative, False)
    artifacts = root / 'artifacts'
    if not os.path.lexists(artifacts):
        artifacts.mkdir()
    _scope._ancestry(artifacts)
    descriptor = artifacts / DESCRIPTOR
    if os.path.lexists(descriptor):
        raise FileExistsError('An existing descriptor cannot be replaced.')
    scope_id, marker = uuid.uuid4().hex, uuid.uuid4().hex
    owned = artifacts / (PREFIX + scope_id)
    if len(str(owned / 'publish' / EXE)) > _scope.MAX_PATH_CHARACTERS:
        raise ValueError('Owned publish path exceeds the bound.')
    owned.mkdir()
    _scope._ancestry(owned)
    _scope._exclusive_bytes(owned / MARKER, marker.encode('ascii'))
    publish, cache = owned / 'publish', owned / 'bundle-cache'
    publish.mkdir(); cache.mkdir()
    _scope._validate_tree(owned)
    command = _scope._publish_command(publish)
    command[2] = str(PROJECT)  # Fixed existing harness; no CLI/project override.
    code = runner('structured-winget-publish', command, root, environment)
    if code != 0:
        return code
    _scope._validate_tree(owned)
    if _scope._ordinary(publish / EXE, False).st_size <= 0:
        raise ValueError('The owned apphost is absent.')
    metadata_size = _scope._ordinary(publish / 'Microsoft.Management.Deployment.winmd', False).st_size
    if not 0 < metadata_size <= 1048576:
        raise ValueError('The required bounded SDK metadata sidecar is absent or invalid.')
    marker_path = owned / MARKER
    if _scope._ordinary(marker_path, False).st_size != 32:
        raise ValueError('Owned marker changed.')
    with marker_path.open('rb') as ownership:
        if ownership.read(33) != marker.encode('ascii'):
            raise ValueError('Owned marker changed.')
    if any(cache.iterdir()):
        raise ValueError('Owned publish cache changed.')
    data = {'sourceCommit': source, 'runId': run, 'runAttempt': attempt, 'scopeId': scope_id, 'ownerMarker': marker}
    raw = json.dumps(data, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    if len(raw) > 512:
        raise ValueError('Descriptor exceeds the bound.')
    _scope._ancestry(artifacts)
    _scope._exclusive_bytes(descriptor, raw)
    return 0


def main():
    if os.name != 'nt' or len(sys.argv) != 1:
        print('Published SDK preparation requires Windows and no arguments.', file=sys.stderr)
        return 1
    try:
        root = Path.cwd()
        _scope._ancestry(root)
        if not root.is_absolute() or root.drive.startswith('\\\\') or Path(__file__).absolute().parent.parent != root:
            raise ValueError('Use the existing local repository root.')
        _scope._ordinary(root / 'tools/ci_command.py', False)
        _scope._ordinary(root / 'tools/ci_diagnostics.py', False)
        spec = importlib.util.spec_from_file_location('madclean_sdk_publish_capture', root / 'tools/ci_command.py')
        capture = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(capture)
        return _prepare(root, dict(os.environ), capture.run)
    except (OSError, ValueError, ImportError):
        print('Published SDK preparation failed; no descriptor replaced or executable launched.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
