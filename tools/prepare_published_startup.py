"""Prepare a temporary owned Windows publish for startup validation; never launch or distribute it."""
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
import uuid

MAX_ENTRIES = 512
MAX_DEPTH = 8
MAX_DESCRIPTOR_BYTES = 512
MAX_PATH_CHARACTERS = 258
MARKER_NAME = '.madclean-published-startup-owner'
DESCRIPTOR_NAME = 'published-startup-descriptor.json'
APP_PROJECT = Path('src/MadClean.App/MadClean.App.csproj')


def _ordinary(path, directory):
    if len(str(path)) > MAX_PATH_CHARACTERS:
        raise ValueError('Filesystem path exceeds the validation bound.')
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400):
        raise ValueError('Unsupported filesystem object.')
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise ValueError('Unexpected filesystem object type.')
    return info


def _ancestry(path):
    # Check each existing component rather than resolve() through a junction/symlink.
    if not path.is_absolute():
        raise ValueError('Absolute ordinary scope required.')
    for component in reversed((path, *path.parents)):
        _ordinary(component, True)


def _source_commit(value):
    if not isinstance(value, str) or re.fullmatch(r'[0-9a-fA-F]{40}', value) is None:
        raise ValueError('A validated source commit is required.')
    return value.lower()


def _canonical_guid(value):
    if not isinstance(value, str) or re.fullmatch(r'[0-9a-f]{32}', value) is None or uuid.UUID(hex=value).hex != value or int(value, 16) == 0:
        raise ValueError('A canonical owned identifier is required.')
    return value


def _descriptor(source, scope, marker):
    result = json.dumps({'SourceCommit': _source_commit(source), 'ScopeId': _canonical_guid(scope),
                         'OwnerMarker': _canonical_guid(marker)}, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    if len(result) >= MAX_DESCRIPTOR_BYTES:
        raise ValueError('Descriptor exceeds its fixed bound.')
    return result


def _exclusive_bytes(path, data):
    # The destination is never replaced, repaired or truncated, including stale links.
    with path.open('xb') as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    _ordinary(path, False)


def _validate_tree(root):
    _ancestry(root)
    pending = [(root, 0)]
    visited = 0
    while pending:
        folder, depth = pending.pop()
        if depth > MAX_DEPTH:
            raise ValueError('Owned scope depth bound exceeded.')
        _ordinary(folder, True)
        with os.scandir(folder) as entries:
            for entry in entries:
                visited += 1
                if visited > MAX_ENTRIES:
                    raise ValueError('Owned scope entry bound exceeded.')
                path = folder / entry.name
                if depth + 1 > MAX_DEPTH or len(str(path)) > MAX_PATH_CHARACTERS:
                    raise ValueError('Owned scope depth/path bound exceeded.')
                info = path.lstat()
                if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400):
                    raise ValueError('Owned scope contains an unsupported link.')
                if stat.S_ISDIR(info.st_mode):
                    if depth + 1 > MAX_DEPTH:
                        raise ValueError('Owned scope depth bound exceeded.')
                    pending.append((path, depth + 1))
                elif not stat.S_ISREG(info.st_mode):
                    raise ValueError('Owned scope contains an unsupported object.')


def _publish_command(output):
    return ['dotnet', 'publish', str(APP_PROJECT), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:MadCleanPackage=true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:RestoreLockedMode=true', '-o', str(output)]


def _prepare(root, environment, runner):
    source = _source_commit(environment.get('GITHUB_SHA'))
    _ancestry(root)
    for relative in (Path('global.json'), APP_PROJECT, Path('tools/ci_command.py')):
        _ancestry((root / relative).parent)
        _ordinary(root / relative, False)
    artifacts = root / 'artifacts'
    _ancestry(root)
    if not os.path.lexists(artifacts):
        artifacts.mkdir()
    _ancestry(artifacts)
    descriptor_path = artifacts / DESCRIPTOR_NAME
    if os.path.lexists(descriptor_path):
        raise FileExistsError('A descriptor already exists; no replacement is allowed.')
    scope_id, marker = uuid.uuid4().hex, uuid.uuid4().hex
    scope = artifacts / ('published-startup-' + scope_id)
    if len(str(scope / 'publish' / 'MadClean.exe')) > MAX_PATH_CHARACTERS:
        raise ValueError('Publish scope exceeds the validation path bound.')
    scope.mkdir()  # exist_ok=False: another existing object never becomes owned.
    _ancestry(scope)
    _exclusive_bytes(scope / MARKER_NAME, marker.encode('ascii'))
    publish, bundle = scope / 'publish', scope / 'bundle-cache'
    publish.mkdir(); bundle.mkdir()
    _validate_tree(scope)
    code = runner('startup-publish', _publish_command(publish), root, environment)
    if code != 0:
        return code
    _validate_tree(scope)
    executable = publish / 'MadClean.exe'
    _ancestry(publish)
    if _ordinary(executable, False).st_size <= 0:
        raise ValueError('No ordinary published executable was produced.')
    if _ordinary(scope / MARKER_NAME, False).st_size != 32:
        raise ValueError('The owned marker changed during publish.')
    with (scope / MARKER_NAME).open('rb') as ownership:
        if ownership.read(33) != marker.encode('ascii'):
            raise ValueError('The owned marker changed during publish.')
    if any(bundle.iterdir()):
        raise ValueError('The fresh bundle cache changed during preparation.')
    _ancestry(artifacts)
    _exclusive_bytes(descriptor_path, _descriptor(source, scope_id, marker))
    return 0


def main():
    # No CLI destination/option overrides and no execution path on Linux/cloud.
    if os.name != 'nt' or len(sys.argv) != 1:
        print('Published startup preparation requires Windows and no arguments.', file=sys.stderr)
        return 1
    try:
        root = Path.cwd()
        if not root.is_absolute() or root.drive.startswith('\\\\'):
            raise ValueError('A local repository working directory is required.')
        _ancestry(root)
        script = Path(__file__).absolute()
        _ancestry(script.parent)
        _ordinary(script, False)
        if script.parent.parent != root:
            raise ValueError('Run only from this repository root.')
        _ordinary(root / 'tools' / 'ci_command.py', False)
        _ordinary(root / 'tools' / 'ci_diagnostics.py', False)
        spec = importlib.util.spec_from_file_location('madclean_startup_ci_command', root / 'tools' / 'ci_command.py')
        capture = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(capture)
        return _prepare(root, dict(os.environ), capture.run)
    except (OSError, ValueError, ImportError):
        print('Published startup preparation failed; no descriptor was repaired or executable launched.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
