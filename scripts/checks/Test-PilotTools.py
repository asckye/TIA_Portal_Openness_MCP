"""Exercise migrated pilot domains through the real SDK, bridge and isolated STDIO host.

Uses the TiaMcp.Engine.Harness protocol host without Openness initialization. No TIA connection,
network transport, Git mutation or companion execution is requested.
"""
import argparse
from contextlib import contextmanager
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import uuid

spec = importlib.util.spec_from_file_location('resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)


@contextmanager
def scratch_directory():
    parent = Path(tempfile.gettempdir()).resolve()
    directory = parent / ('tia-pilot-' + uuid.uuid4().hex)
    # Keep inherited ACLs: mkdtemp's mode=0700 prevents sandboxed children from reading the fixture.
    directory.mkdir()
    try:
        yield str(directory)
    finally:
        resources.require(directory.resolve().parent == parent, 'Unsafe pilot scratch path')
        shutil.rmtree(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--baseline-exe', type=Path, help='Optional pre-move EXE for exact tools/list sequence comparison')
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    passed = 0

    def check(condition, message):
        nonlocal passed
        resources.require(condition, message)
        passed += 1

    with scratch_directory() as directory:
        calls = {
            'GetOpennessGuidance': {'query': 'Openness', 'limit': 1},
            'GetV21EcosystemCatalog': {'limit': 1},
            'ManagePlcGitRepository': {'repositoryPath': str(root), 'action': 'status'},
            'BuildPlcAliasAlarmLad': {'blockName': 'Pilot', 'blockNumber': 1,
                'rows': [{'source': ['InputTag'], 'destination': ['OutputTag']}]},
            'AuditEngineeringExports': {'directoryPath': directory},
            'PlanArtifactImportOrder': {'artifacts': [{'id': 'A'}]},
            'GetToolUsage': {'toolName': 'PlanArtifactImportOrder', 'exampleKind': 'sequence'},
            'BuildClassicHmiTagTable': {'table': {'name': 'Pilot', 'tags': [{'name': 'Ready', 'dataType': 'Bool'}]}},
        }
        for isolated in (False, True):
            for profile in ('full', 'lite'):
                baseline_names = None
                if args.baseline_exe:
                    with resources.server(args.baseline_exe.resolve(), args.public_api.resolve(), args.major, 'stdio', profile,
                            args.host_harness.resolve(), args.public_api.resolve(), isolate=isolated) as (rpc, _, _):
                        rpc('initialize', params={'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'pilot-baseline', 'version': '1'}})
                        rpc('notifications/initialized', notification=True)
                        baseline_names = [tool['name'] for tool in rpc('tools/list')['result']['tools']]
                with resources.server(args.exe.resolve(), args.public_api.resolve(), args.major, 'stdio', profile,
                        args.host_harness.resolve(), args.public_api.resolve(), isolate=isolated,
                        env_overrides={'TIA_MCP_MAX_RESPONSE_CHARS': '2000000'}) as (rpc, _, logs):
                    rpc('initialize', params={'protocolVersion': '2024-11-05', 'capabilities': {},
                        'clientInfo': {'name': 'pilot-tools', 'version': '1'}})
                    rpc('notifications/initialized', notification=True)
                    names = [tool['name'] for tool in rpc('tools/list')['result']['tools']]
                    check(len(names) == len(set(names)), 'tools/list contains duplicate names')
                    if baseline_names is not None:
                        check(names == baseline_names, 'tools/list sequence differs from the pre-move EXE')

                    def call(name, arguments):
                        reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
                        resources.require('result' in reply, str(reply))
                        return reply['result'], resources.envelope(reply)

                    for name, arguments in calls.items():
                        if profile == 'full':
                            result, value = call(name, arguments)
                        else:
                            result, value = call('CallTool', {'name': name, 'arguments': arguments})
                        check(not result.get('isError') and value['ok'],
                              f'{profile} isolated={isolated} {name}: {value}')

                    _, preflight = call('PreviewToolCall', {'name': 'PlanArtifactImportOrder',
                        'arguments': calls['PlanArtifactImportOrder']})
                    check(not preflight['ok'] and preflight['error']['code'] == 'PROJECT_NOT_BOUND', 'Preview did not resolve the tool before the disconnected guard')
                    _, missing = call('CallTool', {'name': 'PlanArtifactImportOrder', 'arguments': {}})
                    check(not missing['ok'] and missing['error']['code'] == 'INVALID_ARGUMENT',
                          'Bridge lost required parameter diagnostics')
                    _, duplicate = call('CallTool', {'name': 'PlanArtifactImportOrder',
                        'arguments': {'artifacts': [], 'Artifacts': []}})
                    check(not duplicate['ok'] and duplicate['error']['code'] == 'INVALID_ARGUMENT',
                          'Bridge lost duplicate argument admission')
                    if profile == 'full':
                        result, missing = call('PlanArtifactImportOrder', {})
                        check(result.get('isError') is True and missing['error']['code'] == 'INVALID_ARGUMENT',
                              'Direct instance call lost argument diagnostics')
                    if isolated:
                        _, status = call('GetOpennessWorkerStatus', {})
                        check(status['data']['evidence']['worker']['state'] == 'Ready', 'Pilot tool never reached the isolated child')
                print(f'PASS V{args.major} {profile} isolated={isolated}: eight pilot domains and admission checks', flush=True)
    print(f'COMPLETE: {passed} pilot dispatch checks passed; no native calls or network transport')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
