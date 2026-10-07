"""Run the same fixture cases through both host dispatch paths and enforce the TRX gate."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--engine-major', choices=('20', '21'), default='21')
    parser.add_argument('--results-directory', type=Path, default=ROOT / 'test-results/host-behavior-parity')
    args = parser.parse_args()
    directory = args.results_directory.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    trx = directory / 'host-behavior-parity.trx'
    summary = directory / 'host-behavior-parity.json'
    trx.unlink(missing_ok=True)
    summary.unlink(missing_ok=True)
    command = [args.dotnet, 'test', str(ROOT / 'tests/Engine/TiaMcpServer.LegacyHostTests/TiaMcpServer.LegacyHostTests.csproj'),
               '-c', 'Release', '--disable-build-servers', '-m:1', '--filter', 'FullyQualifiedName~BehaviorParityTests',
               '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(directory)]
    if args.no_build:
        command += ['--no-build', '--no-restore']
    elif args.engine_major == '20':
        command += ['-p:DefineConstants=TIA_V20']
    print('RUN host-behavior-parity: ' + subprocess.list2cmdline(command), flush=True)
    environment = os.environ.copy()
    environment['TIA_MCP_PARITY_ENGINE_RELEASE'] = args.engine_major
    exit_code = subprocess.run(command, cwd=ROOT, env=environment, check=False).returncode
    spec = importlib.util.spec_from_file_location('dotnet_suites', ROOT / 'scripts/checks/Test-DotnetSuites.py')
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    errors = []
    counts = {}
    if exit_code:
        errors.append(f'dotnet test exited {exit_code}')
    if trx.is_file():
        counts, trx_errors = gate.evaluate_trx(trx, 103, 0)
        errors.extend(trx_errors)
    else:
        errors.append('missing TRX')
    summary.write_text(json.dumps(dict(suite='host-behavior-parity', engineRelease=args.engine_major, minimumPassed=103, maximumSkipped=0,
                                      exitCode=exit_code, **counts, errors=errors), indent=2) + '\n', encoding='utf-8')
    if errors:
        print('FAIL host-behavior-parity: ' + '; '.join(errors))
        return 1
    print(f'COMPLETE: {counts["passed"]} host-behavior-parity checks passed; 0 failed, 0 skipped')
    return 0


if __name__ == '__main__':
    sys.exit(main())
