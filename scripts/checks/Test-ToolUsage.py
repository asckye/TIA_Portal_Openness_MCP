"""Retrieve and execute allowlisted offline examples on all eight releases over STDIO."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path

from tool_usage_checks import check_usage

ROOT = Path(__file__).resolve().parents[2]


def helper(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def coverage(records, output):
    """Generate the usage manifest from this run, never from stale transport reports."""
    # Use the V4 contracts: version-tools.json still omits the two render tools
    # on Foundation. This audit must not bless or rewrite an unrelated manifest.
    matrix = {key: json.loads((ROOT / f'manifest/contracts/v4/baseline/{key}.json').read_text('utf-8'))['tools']
              for key in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')}
    source = ROOT / 'src/Shared/ToolUsageData.json'
    catalog = json.loads(source.read_text('utf-8'))
    by_release = {}
    for record in records:
        key = record['releaseKey']
        assert not record['exampleCallsExecuted'] and record['allSourceTextRetrievedAndHashChecked']
        assert record['registeredToolCount'] == record['checkedToolCount'] == len(matrix[key]), key
        assert {row['toolName'] for row in record['tools']} == {row['name'] for row in matrix[key]}, key
        assert key not in by_release
        by_release[key] = record
    assert set(by_release) == set(matrix)
    union = set().union(*({row['name'] for row in tools} for tools in matrix.values()))
    assert union == set(catalog['tools'])
    result = {
        'basis': 'Actual STDIO MCP tools/list and GetToolUsage responses for every tool/operation on all eight releases. Parameterized calls, input origins, result contracts, language files, sequence calls and all pinned source hashes checked. Allowlisted in-memory call examples executed; target-bound native example operations NOT executed. HTTP/network services were not used.',
        'catalogSha256': hashlib.sha256(source.read_text('utf-8').replace('\r\n', '\n').encode()).hexdigest(),
        'uniqueTools': len(union), 'documents': len(catalog['documents']),
        'versionToolPairs': sum(len(tools) for tools in matrix.values()),
        'offlineCallExecutions': sum(len(row['offlineCallExamplesExecuted']) for row in by_release.values()),
        'indexedExampleBlocksOrMethods': sum(len(doc['examples']) for doc in catalog['documents']),
        'programmingExamples': len(catalog['examples']), 'callSequences': len(catalog['sequences']),
        'languages': [language['id'] for language in catalog['languages']],
        'relatedOfficialPatternMappings': sum(row['relationship'] == 'related-api-patterns' for row in catalog['tools'].values()),
        'noDirectVendoredExampleMappings': sum(row['relationship'] == 'no-direct-official-example' for row in catalog['tools'].values()),
        'sources': catalog['sources'], 'nativeTiaExecuted': False, 'releases': by_release}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print(f'PASS: {len(by_release)} releases, {result["versionToolPairs"]} tool/release lookups, '
          f'{result["offlineCallExecutions"]} offline call examples; native TIA NOT RUN', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--foundation-host', type=Path, required=True)
    parser.add_argument('--engine-host', type=Path, help='Use the unified Foundation engine host for V20/V21; engine paths select SDK fixture workers with sibling catalogs')
    parser.add_argument('--engine-v20', type=Path, required=True)
    parser.add_argument('--engine-v21', type=Path, required=True)
    parser.add_argument('--harness', type=Path, required=True)
    parser.add_argument('--public-api-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True, help='New worktree directory for this run')
    parser.add_argument('--coverage-output', type=Path, required=True)
    args = parser.parse_args()
    snapshots = helper('Snapshot-ToolResponses')
    resources = snapshots.resources
    if args.engine_host:
        args.repo_root, args.transport = ROOT, 'stdio'
        args.engine_worker = [f'{key}={exe.resolve()}' for key, exe in (('20', args.engine_v20), ('21', args.engine_v21))]
        args.engine_catalog = [f'{key}={exe.resolve().parent / "tool-catalog.json"}' for key, exe in (('20', args.engine_v20), ('21', args.engine_v21))]
    evidence = args.evidence.resolve()
    evidence.relative_to(ROOT)
    evidence.mkdir(parents=True, exist_ok=False)
    records = []
    for release in snapshots.RELEASES:
        case = evidence / release
        case.mkdir()
        env = {'TEMP': str(case), 'TMP': str(case), 'TIA_MCP_DATA_DIRECTORY': str(case / 'data'),
               'TIA_MCP_MAX_RESPONSE_CHARS': '2000000'}
        if release in ('20', '21'):
            major = int(release)
            api = args.public_api_root / f'TIA_V{major}_PublicAPI' / f'V{major}'
            if major == 21:
                api /= 'net48'
            installation = resources.sdk_only_installation(api, major, case)
            exe = args.engine_v20 if major == 20 else args.engine_v21
            options = {'harness': args.harness.resolve(), 'public_api': api.resolve()}
            env.update(snapshots.capture_readiness_overrides(args.harness.resolve(), False))
            profile = 'full'
        else:
            exe, installation, options, profile = args.foundation_host, case, {}, 'plc-foundation'
        server = (snapshots.contracts.engine_host_server(args, release, installation, profile, env)
                  if args.engine_host and release in ('20', '21') else
                  resources.server(exe.resolve(), installation, release, 'stdio', profile, env_overrides=env, **options))
        with server as (rpc, _, _):
            tools = snapshots.initialize(rpc)

            def call(name, arguments):
                # check_usage is the shared literal allowlist; it never executes native examples.
                reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
                resources.require('result' in reply and not reply['result'].get('isError'), str(reply))
                return resources.envelope(reply)

            record = check_usage(call, tools, release, verify_documents=True)
            records.append(record)
            (case / 'tool-usage.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
            print(f'PASS V{release}: {record["checkedToolCount"]} tools, '
                  f'{record["operationExampleCount"]} operations, all reference hashes and offline examples', flush=True)
    coverage(records, args.coverage_output)


if __name__ == '__main__':
    main()
