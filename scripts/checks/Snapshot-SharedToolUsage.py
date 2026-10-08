"""Check usage on all eight releases through their actual FoundationHost pipelines."""
import argparse
import json
from pathlib import Path

from tool_usage_checks import check_usage
import importlib.util

ROOT = Path(__file__).resolve().parents[2]


def helper(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine-host', type=Path, required=True)
    parser.add_argument('--foundation-host', type=Path, help='Old-six FoundationHost (defaults to engine-host)')
    parser.add_argument('--engine-worker', action='append', required=True)
    parser.add_argument('--engine-catalog', action='append', required=True)
    parser.add_argument('--public-api-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--coverage-output', type=Path, required=True)
    args = parser.parse_args()
    args.repo_root, args.transport = ROOT, 'stdio'
    snapshots = helper('Snapshot-ToolResponses')
    contracts = helper('Snapshot-ToolContracts')
    resources = snapshots.resources
    args.evidence.resolve().relative_to(ROOT)
    args.evidence.mkdir(parents=True, exist_ok=False)
    records = []
    for release in snapshots.RELEASES:
        case = args.evidence.resolve() / release
        case.mkdir()
        env = {'TEMP': str(case), 'TMP': str(case), 'TIA_MCP_DATA_DIRECTORY': str(case / 'data'),
               'TIA_MCP_MAX_RESPONSE_CHARS': '2000000'}
        if release in ('20', '21'):
            major = int(release)
            api = args.public_api_root / f'TIA_V{major}_PublicAPI' / f'V{major}'
            if major == 21:
                api /= 'net48'
            installation = resources.sdk_only_installation(api, major, case)
            server = contracts.engine_host_server(args, release, installation, 'full', env)
        else:
            server = resources.server((args.foundation_host or args.engine_host).resolve(), case, release, 'stdio', 'plc-foundation', env_overrides=env)
        with server as (rpc, _, _):
            tools = snapshots.initialize(rpc)

            def call(name, arguments):
                reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
                resources.require('result' in reply and not reply['result'].get('isError'), str(reply))
                return resources.envelope(reply)

            record = check_usage(call, tools, release, verify_documents=True)
            records.append(record)
            (case / 'tool-usage.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
            print(f'PASS V{release}: {record["checkedToolCount"]} tools, '
                  f'{record["operationExampleCount"]} operations, reference hashes and offline examples', flush=True)
    helper('Test-ToolUsage').coverage(records, args.coverage_output)


if __name__ == '__main__':
    main()
