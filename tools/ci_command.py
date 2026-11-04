"""Capture bounded, scrubbed development command output on disposable CI runners."""
import json
import importlib.util
import os
from pathlib import Path
import re
import subprocess
import sys

LABELS = {'restore','build','publisher-tests','core-tests','publish','smoke','native-abi','shortcut-control','startup-publish','update-discovery','structured-winget-publish','structured-winget-published','app-update-ui','published-app-updates'}
MAX_LOG = 262_144

_spec = importlib.util.spec_from_file_location("madclean_capture_scrubber", Path(__file__).with_name("ci_diagnostics.py"))
_scrubber = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_scrubber)
scrub = _scrubber.scrub

def run(label, command, root, environment):
    if label not in LABELS or not command or command[0] not in {'dotnet','python','python3',sys.executable}:
        raise ValueError('Unsupported development CI command.')
    output = root / 'artifacts' / 'ci-logs'
    output.mkdir(parents=True,exist_ok=True)
    # Drain the child but retain only bounded bytes in memory. Scrub the whole captured text
    # before writing any console/artifact output, including multiline private-key blocks.
    captured = bytearray()
    truncated = False
    process = subprocess.Popen(command,cwd=root,env=environment,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL)
    for raw in iter(lambda:process.stdout.read(65536),b''):
        remaining = MAX_LOG-len(captured)
        if remaining > 0: captured.extend(raw[:remaining])
        if len(raw)>remaining: truncated=True
    process.stdout.close()
    code = process.wait()
    text = scrub(captured.decode('utf-8',errors='replace'))
    encoded = text.encode('utf-8')
    if len(encoded)>MAX_LOG: truncated=True
    text = encoded[:MAX_LOG].decode('utf-8',errors='ignore')
    (output/(label+'.log')).write_text(text,encoding='utf-8',newline='\n')
    sys.stdout.write(text); sys.stdout.flush()
    metadata = {'formatVersion':1,'label':label,'sourceCommit':environment.get('GITHUB_SHA','Unknown'),
        'runId':environment.get('GITHUB_RUN_ID','Unknown'),'runAttempt':int(environment.get('GITHUB_RUN_ATTEMPT','0')),
        'exitCode':code,'truncated':truncated,'scope':'Scrubbed development compiler/test output only; not production inventory.'}
    (output/(label+'.json')).write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf-8')
    return code

def main():
    if len(sys.argv)<4 or sys.argv[2]!='--':
        print('Usage: ci_command.py <fixed-stage> -- <development command>',file=sys.stderr); return 1
    try: return run(sys.argv[1],sys.argv[3:],Path.cwd(),dict(os.environ))
    except (OSError,ValueError):
        print('Development CI command could not be started/captured.',file=sys.stderr); return 1
if __name__=='__main__': raise SystemExit(main())
