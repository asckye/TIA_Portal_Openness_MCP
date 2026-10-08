"""Evaluate release defines without building, restoring packages or loading Siemens APIs."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
EXPECTATION = Path('scripts/checks/tia-feature-expectations.json')
RELEASES = ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')


def project_cases(root):
    cases = []

    def add(path, properties=None):
        cases.append((path.relative_to(root).as_posix(), properties or {}))

    for pattern in ('src/Adapters/V*/*.csproj',
                    'src/Studio/Openness/V*/*.csproj',
                    'src/Engine/TiaMcp.Engine.V*.csproj'):
        for path in sorted(root.glob(pattern)):
            add(path)
    for name in ('src/PlcWorker/TiaMcp.PlcWorker.csproj',
                 'tests/Engine/TiaMcp.Engine.ApiCompileChecks/TiaMcp.Engine.ApiCompileChecks.csproj'):
        for release in RELEASES:
            add(root / name, {'TiaReleaseKey': release})
    # Keep intentional test defines covered, without moving them into the feature table.
    for path in sorted((root / 'tests').glob('**/*.csproj')):
        source = path.read_text(encoding='utf-8-sig')
        if '<DefineConstants>' in source or '<TiaMajor>' in source:
            add(path)
    suites = json.loads((root / 'tests/test-suites.json').read_text(encoding='utf-8-sig'))
    for name in ('offline', 'offline-v20'):
        properties = dict(arg.removeprefix('-p:').split('=', 1)
                          for arg in suites[name]['arguments'] if arg.startswith('-p:'))
        add(root / suites[name]['project'], properties)
    return sorted(cases, key=lambda case: (case[0], sorted(case[1].items())))


def evaluate(root, project, properties, dotnet):
    command = [dotnet, 'msbuild', str(root / project), '-nologo',
               '-getProperty:DefineConstants,TargetFramework,TargetFrameworks',
               '-p:Configuration=Release',
               *[f'-p:{key}={value}' for key, value in sorted(properties.items())]]
    result = subprocess.run(command, cwd=root, capture_output=True, text=True,
                            encoding='utf-8', errors='replace', timeout=120)
    if result.returncode:
        raise ValueError(f'{project} {properties}: evaluation exited {result.returncode}\n'
                         f'{result.stdout}{result.stderr}')
    try:
        return json.loads(result.stdout)['Properties']
    except (ValueError, KeyError) as exc:
        raise ValueError(f'{project}: invalid MSBuild property output: {result.stdout}') from exc


def capture(root, dotnet='dotnet'):
    rows = []
    for project, properties in project_cases(root):
        values = evaluate(root, project, properties, dotnet)
        frameworks = values['TargetFrameworks'].split(';') if values['TargetFrameworks'] else [values['TargetFramework']]
        for framework in frameworks:
            evaluated = values if not values['TargetFrameworks'] else evaluate(
                root, project, dict(properties, TargetFramework=framework), dotnet)
            rows.append(dict(project=project, properties=properties, targetFramework=framework,
                             defineConstants=sorted(set(filter(None, evaluated['DefineConstants'].split(';'))))))
    return rows


def check(root, dotnet='dotnet', output=None):
    actual = capture(root, dotnet)
    if output:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(actual, indent=2) + '\n', encoding='utf-8')
    expected = json.loads((root / EXPECTATION).read_text(encoding='utf-8'))
    def indexed(rows):
        return {(row['project'], tuple(sorted(row['properties'].items())), row['targetFramework']):
                set(row['defineConstants']) for row in rows}
    before, after = indexed(expected), indexed(actual)
    errors = []
    for key in sorted(before.keys() | after.keys()):
        label = f'{key[0]} {dict(key[1])} ({key[2]})'
        if key not in before or key not in after:
            errors.append(label + (': missing expectation' if key not in before else ': missing project evaluation'))
        elif before[key] != after[key]:
            errors.append(f'{label}: removed={sorted(before[key] - after[key])}, '
                          f'added={sorted(after[key] - before[key])}')
    return len(actual), errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, help='Save the evaluated table as review evidence')
    parser.add_argument('--capture', type=Path, help='Save a candidate expectation; review it before replacing the committed file')
    args = parser.parse_args()
    try:
        if args.capture:
            rows = capture(args.root, args.dotnet)
            args.capture.parent.mkdir(parents=True, exist_ok=True)
            args.capture.write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
            print(f'Captured {len(rows)} project/framework evaluations in {args.capture}.')
            return 0
        count, errors = check(args.root, args.dotnet, args.output)
    except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
        count, errors = 0, [str(exc)]
    for error in errors:
        print('[FAIL] ' + error)
    print(f'Checked {count} release define evaluations; {len(errors)} issue(s).')
    return bool(errors)


if __name__ == '__main__':
    sys.exit(main())
