"""Validate all campaign inputs offline against a built engine's actual V4 schemas."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)


def applicability(step, catalogs):
    declared = step.get('applicability', {})
    result = {}
    for release in ('20', '21'):
        available = step['tool'] in catalogs[release]
        entry = declared.get(release, {'applicable': True, 'reason': 'Tool is registered in this release.'})
        if type(entry.get('applicable')) is not bool or not entry.get('reason'):
            raise ValueError(f'{step["tool"]}: V{release} applicability needs a boolean and reason')
        if entry['applicable'] != available:
            raise ValueError(f'{step["tool"]}: V{release} applicability disagrees with the catalog')
        result[release] = entry
    return result


def check_summary(summary, total, exit_code):
    counts = [summary[key] for key in ('passed', 'failed', 'notApplicable')]
    if any(type(value) is not int or value < 0 for value in counts) or sum(counts) != total or summary['total'] != total:
        raise ValueError('Campaign counts do not account for every input')
    if len(summary['results']) != total or not 0 <= summary['explicitRejections'] <= summary['passed']:
        raise ValueError('Campaign result rows/rejections disagree with counts')
    expected = 0 if summary['failed'] == 0 else 1
    if exit_code != expected:
        raise ValueError('Harness exit status disagrees with campaign failures')
    return expected


def self_test():
    catalogs = {'20': {'Shared'}, '21': {'Shared', 'Modern'}}
    assert all(e['applicable'] for e in applicability({'tool': 'Shared'}, catalogs).values())
    modern = {'tool': 'Modern', 'applicability': {'20': {'applicable': False, 'reason': 'V21-only tool.'}}}
    assert not applicability(modern, catalogs)['20']['applicable']
    for step in ({'tool': 'Modern'}, {'tool': 'Unknown'}, {'tool': 'Shared', 'applicability': {'20': {'applicable': False, 'reason': 'hidden'}}}):
        try:
            applicability(step, catalogs)
        except ValueError:
            pass
        else:
            raise AssertionError('Invalid applicability was accepted')
    summary = dict(passed=1, failed=1, notApplicable=1, total=3, explicitRejections=1, results=[{}, {}, {}])
    assert check_summary(summary, 3, 1) == 1
    for total, code in ((3, 0), (2, 1)):
        try:
            check_summary(summary, total, code)
        except ValueError:
            pass
        else:
            raise AssertionError('Invalid counts/exit status were accepted')
    print('COMPLETE: 8 campaign runner self-tests passed; 0 failed')
    return 0


def validator_command(major, catalog, inputs, result):
    return validator_base() + [str(major), str(catalog), str(inputs), str(result)]


def validator_base():
    return ['dotnet', 'run', '--file', str(Path(__file__).with_suffix('.cs')), '-c', 'Release',
            '--no-build', '--no-restore', '--']


def validator_environment():
    # dotnet run --file --no-build looks for the program that build-solutions/check-all compiled below TEMP.
    # check-all gives each job a private TEMP and records the original one; only the validator process uses it.
    environment = dict(os.environ)
    system_temp = environment.get('TIA_MCP_CHECK_SYSTEM_TEMP_ROOT')
    if system_temp:
        environment['TEMP'] = environment['TMP'] = system_temp
    return environment


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--validator-self-test', action='store_true', help='Run the kept-validator self-tests of Test-CampaignInputs.cs')
    parser.add_argument('--exe', type=Path)
    parser.add_argument('--public-api', type=Path)
    parser.add_argument('--plc-worker', type=Path)
    parser.add_argument('--major', type=int, choices=(20, 21))
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.validator_self_test:
        return subprocess.run(validator_base() + ['--self-test'], check=False, env=validator_environment()).returncode
    # P8-04l: the engine harness route (script-inputs-only) is retired; the kept validator checks the host's served schemas.
    if any(getattr(args, key) is None for key in ('exe', 'public_api', 'major', 'output', 'plc_worker')):
        parser.error('--exe, --public-api, --major, --output and --plc-worker are required')
    for key in ('exe', 'public_api', 'output'):
        if getattr(args, key) is not None:
            setattr(args, key, getattr(args, key).resolve())
    args.output.mkdir(parents=True, exist_ok=False)
    versions = json.loads((ROOT / 'manifest/version-tools.json').read_text(encoding='utf-8'))
    catalogs = {key: set(versions['releases'][key]['tools']) for key in ('20', '21')}
    steps = []
    for source in sorted((ROOT / 'scripts/diagnostics/campaign/plans').glob('plan_*.json')):
        copy = args.output / source.name
        copy.write_bytes(source.read_bytes())
        plan = json.loads(copy.read_text(encoding='utf-8'))
        for index, step in enumerate(plan):
            # These exact fixtures retain the unsupported-action and wrong-type
            # rejection coverage; native failure expectations are not exemptions.
            negative = step.get('expect') == 'error' and (
                'V4 rejects this unadvertised action' in step.get('note', '') or
                step.get('note', '').startswith('V4 schema rejection:'))
            steps.append(dict(step, label=f'{source.name}:{index + 1}', negativeInput=negative,
                              applicability=applicability(step, catalogs)))
    with resources.server(args.exe, args.public_api, args.major, 'stdio', 'full',
                          None, args.public_api,
                          plc_worker=args.plc_worker,
                          env_overrides={'TIA_MCP_MAX_RESPONSE_CHARS': '2000000'}) as (rpc, _, _):
        tools = rpc('tools/list', 'catalog')['result']['tools']
    if {tool['name'] for tool in tools} != catalogs[str(args.major)]:
        raise ValueError('Actual release catalog disagrees with the generated applicability catalog')
    catalog, inputs = args.output / 'tools.json', args.output / 'inputs.json'
    catalog.write_text(json.dumps(tools), encoding='utf-8')
    inputs.write_text(json.dumps(steps), encoding='utf-8')
    result = args.output / 'result.json'
    command = validator_command(args.major, catalog, inputs, result)
    process = subprocess.run(command, check=False, env=validator_environment())
    summary = json.loads(result.read_text(encoding='utf-8'))
    code = check_summary(summary, len(steps), process.returncode)
    summary.update(resources.plc_fixture_evidence(args.plc_worker, args.exe))
    result.write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
    print(f'V{args.major}: {summary["passed"]} passed, {summary["failed"]} failed, '
          f'{summary["notApplicable"]} not applicable, {summary["explicitRejections"]} explicit rejections; '
          f'{summary["total"]} total')
    return code


if __name__ == '__main__':
    sys.exit(main())
