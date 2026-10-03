"""Compare disconnected CFC responses before/after the service move over STDIO.

Uses the real SDK, CallTool and isolated child through the HttpTests protocol host.
Only envelope timestamps are masked with the response-snapshot lexer; the tool response
text is otherwise compared as UTF-8 bytes. Never initializes or connects to TIA.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


resources = load('resources', 'Test-ResourceDiscovery.py')
snapshots = load('snapshots', 'Snapshot-ToolResponses.py')
# CallTool serializes the POCO with PascalCase; only its envelope clock is volatile.
snapshots.RAW_MASK_RULES = [*snapshots.RAW_MASK_RULES,
    {'tool': '*', 'path': ['Meta', 'timestamp'], 'reason': 'CallTool POCO envelope wall clock'}]


def capture(args, exe, harness, profile, isolated):
    responses = {}
    with resources.server(exe.resolve(), args.public_api.resolve(), args.major, 'stdio', profile,
            harness.resolve(), args.public_api.resolve(), isolate=isolated) as (rpc, _, _):
        rpc('initialize', params={'protocolVersion': '2024-11-05', 'capabilities': {},
            'clientInfo': {'name': 'cfc-tools', 'version': '1'}})
        rpc('notifications/initialized', notification=True)
        names = [tool['name'] for tool in rpc('tools/list')['result']['tools']]
        for name, actions in [('ExchangeCfcCharts', ('export', 'selectiveExport', 'import', 'exportInstructionData')),
                              ('ManageCfcChartProtection', ('read', 'add', 'change', 'remove'))]:
            for action in actions:
                arguments = {'softwarePath': 'CfcOfflineFixture', 'action': action}
                if name == 'ExchangeCfcCharts':
                    arguments.update(filePath='C:/cfc-offline-fixture.xml.zip', modelVersion='V2.0', chartNamesJson='["Chart1"]')
                else:
                    arguments.update(chartName='Chart1', currentPassword='offline', newHashedPassword='offline')
                params = {'name': name, 'arguments': arguments} if profile == 'full' else {
                    'name': 'CallTool', 'arguments': {'name': name.lower(), 'argumentsJson': json.dumps(arguments)}}
                reply = rpc('tools/call', params=params)
                resources.require('result' in reply and not reply['result'].get('isError'), str(reply))
                raw = reply['result']['content'][0]['text']
                if profile == 'lite':
                    bridge = json.loads(raw)
                    resources.require(bridge.get('meta', {}).get('bridgeSuccess') is True, raw)
                    raw = bridge['message']
                value = json.loads(raw)
                meta = value.get('meta', value.get('Meta', {}))
                resources.require(value.get('message', value.get('Message')) == 'Project is null' and meta.get('tool') == name
                    and meta.get('status') == 'InvalidState' and meta.get('operationSuccess') is False,
                    f'{name}/{action} did not reach the disconnected session guard: {raw}')
                responses[name + '/' + action] = snapshots.mask_raw_text(raw, name).encode('utf-8')
        if isolated:
            reply = rpc('tools/call', params={'name': 'ReadOpennessWorkerStatus', 'arguments': {}})
            status = json.loads(reply['result']['content'][0]['text'])
            resources.require(status['meta']['worker']['state'] == 'Ready', 'CFC calls never reached the isolated child')
    return names, responses


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--baseline-exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--baseline-harness', type=Path, required=True, help='Pre-move HttpTests for the pre-move engine surface')
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    args = parser.parse_args()
    passed = 0
    for isolated in (False, True):
        for profile in ('full', 'lite'):
            before_names, before = capture(args, args.baseline_exe, args.baseline_harness, profile, isolated)
            after_names, after = capture(args, args.exe, args.host_harness, profile, isolated)
            resources.require(before_names == after_names and len(after_names) == len(set(after_names)),
                'tools/list changed or contains duplicate names')
            passed += 1
            for name, raw in before.items():
                resources.require(after[name] == raw, f'{profile} isolated={isolated} {name}: response bytes changed')
                passed += 1
                print(f'PASS V{args.major} {profile} isolated={isolated} {name}: ' + hashlib.sha256(raw).hexdigest(), flush=True)
    print(f'COMPLETE: {passed} CFC dispatch/byte checks passed; only meta/Meta.timestamp masked; no TIA connection')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
