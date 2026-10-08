"""Exercise the build-instrumented fake API, not a TIA/Openness process."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--fixture', required=True, type=Path)
    p.add_argument('--weaver', required=True, type=Path)
    p.add_argument('--output', required=True, type=Path)
    args = p.parse_args()
    fixture, weaver, output = args.fixture.resolve(), args.weaver.resolve(), args.output.resolve()
    require(fixture.name == 'TiaMcp.Engine.Diagnostics.Tests.exe', 'Only the fake diagnostics fixture may be run')
    output.mkdir(parents=True, exist_ok=False)
    def run(command, name, expected=0):
        env = dict(os.environ, TIA_MCP_DIAGNOSTICS_DIRECTORY=str(output / name))
        result = subprocess.run(command, env=env, capture_output=True, timeout=60)
        (output / (name + '.stdout.txt')).write_bytes(result.stdout)
        (output / (name + '.stderr.txt')).write_bytes(result.stderr)
        require(result.returncode == expected, f'{name}: exit {result.returncode}; inspect retained output')
        return result.stdout.decode('utf-8', errors='replace')
    text = run([str(fixture)], 'behavior')
    found = re.search(r'COMPLETE: (\d+) instrumented diagnostic checks passed', text)
    require(found is not None and int(found[1]) >= 26, 'Incomplete fixture suite')
    golden = json.loads((output / 'behavior' / 'encoding-golden.json').read_text(encoding='utf-8'))
    require(len(golden) == 12 and all(json.loads(row['old']) == json.loads(row['new']) for row in golden),
            'Python journal readers must decode old Newtonsoft and shared rows equally')
    run([str(fixture), 'abrupt'], 'abrupt', 23)
    rows = [json.loads(line) for file in (output / 'abrupt').glob('calls-*.jsonl') for line in file.read_text(encoding='utf-8').splitlines()]
    pending = [row for row in rows if row.get('nativeCallId') and row['phase'] == 'BEFORE']
    require(len(pending) == 1 and 'ExitNow' in pending[0]['member'], 'Abrupt exit did not retain the exact BEFORE boundary')
    require(not any(row.get('nativeCallId') and row['phase'] != 'BEFORE' for row in rows), 'False completion after abrupt exit')
    before = hashlib.sha256(fixture.read_bytes()).hexdigest()
    run(['dotnet', str(weaver), 'weave', str(fixture)], 'idempotent')
    require(before == hashlib.sha256(fixture.read_bytes()).hexdigest(), 'Repeated weaving changed the binary')
    text = run(['dotnet', str(weaver), 'self-test', str(fixture)], 'rejections')
    require('5 diagnostic coverage rejection checks passed' in text, 'Coverage rejection sentinels incomplete')
    record = {'behaviorChecks': int(found[1]), 'abruptExitChecks': 2, 'idempotenceChecks': 1, 'rejectionChecks': 5,
              'encodingChecks': len(golden), 'fixtureSha256': before, 'weaverSha256': hashlib.sha256(weaver.read_bytes()).hexdigest(), 'nativeTiaExecuted': False}
    (output / 'result.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
    print('COMPLETE: diagnostic fixture, abrupt-exit, idempotence and coverage rejection checks passed; no TIA connection')


if __name__ == '__main__':
    main()
