"""Check the real MCP proxy/SDK path with offline child hosts and test-only faults.
No production bootstrap, Openness initialization, ConnectPortal or project opens.
"""
import argparse
import ctypes
from ctypes import wintypes
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
import time
from pathlib import Path

spec = importlib.util.spec_from_file_location('resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)
require = resources.require


def content(reply):
    return reply['result'], resources.envelope(reply)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    parser.add_argument('--transport', choices=('both', 'stdio', 'http'), default='both')
    parser.add_argument('--output', type=Path, help='Optional directory for raw responses, including malformed engine guard replies')
    args = parser.parse_args()
    for key in ('exe', 'public_api', 'host_harness'):
        setattr(args, key, getattr(args, key).resolve())
    if args.output: args.output.mkdir(parents=True, exist_ok=False)
    passed = 0
    def check(value, message):
        nonlocal passed
        require(value, message)
        passed += 1

    for transport in (('stdio', 'http') if args.transport == 'both' else (args.transport,)):
        for profile in ('full', 'lite'):
            with resources.server(args.exe, args.public_api, args.major, transport, profile, args.host_harness, args.public_api,
                                  isolate=True, env_overrides={'TIA_MCP_MAX_RESPONSE_CHARS': '2000'}) as (rpc, http, logs):
                rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'isolation-test', 'version': '1'}})
                rpc('notifications/initialized', notification=True)
                number = 0
                def call(name, arguments=None):
                    nonlocal number
                    number += 1
                    reply = rpc('tools/call', str(number), {'name': name, 'arguments': arguments or {}})
                    if args.output:
                        (args.output / f'{transport}-{profile}-{number:03}-{name}.json').write_text(json.dumps(reply, indent=2), encoding='utf-8')
                    return content(reply)
                state = call('GetOpennessWorkerStatus')[1]['data']['evidence']['worker']
                check(state['state'] == 'NotStarted' and state['workerPid'] is None, 'Diagnostics launched worker')
                roster = rpc('tools/list', 'list')['result']['tools']
                import xml.etree.ElementTree as ET
                catalog = json.loads(ET.parse(Path(__file__).resolve().parents[2] / 'src/Logic/ModelContextProtocol/ToolProfiles.resx').find(".//data[@name='Catalog']/value").text)['releases'][str(args.major)]
                expected_full = len(catalog)
                expected_lite = sum('lite' in row['profiles'] for row in catalog)
                check(len(roster) == (expected_full if profile == 'full' else expected_lite), 'Version-aware tool catalog drift')
                if profile == 'full':
                    check({'GetPlcBlockEditCapabilities', 'AnalyzePlcReferences', 'PatchPlcBlockDocument', 'ImportPlcBlockVerified'} <= {t['name'] for t in roster}, 'PLC editing tools missing from runtime catalog')
                check(rpc('resources/list', 'resources')['result'] == {'resources': []}, 'Resources changed')
                result, payload = call('GetSessionState')
                check(not result.get('isError') and payload['data']['isConnected'] is False and payload['ok'], 'Offline worker state mismatch')
                state = call('GetOpennessWorkerStatus')[1]['data']['evidence']['worker']
                check(state['state'] == 'Ready' and state['workerPid'] > 0, 'Worker did not initialize')
                before = state['generation']
                # Large response is stored in the worker; the matching paging tool must reach the same child.
                _, large = call('FindTools', {'query': '', 'limit': 100})
                export = large['data']['export']['id']
                pieces, offset = [], 0
                for page in range(200):
                    _, value = call('GetExportContent', {'exportId': export, 'offset': offset})
                    pieces.append(value['data']['text'])
                    if (value['meta']['paging']['nextOffset'] is None):
                        break
                    offset = value['meta']['paging']['nextOffset']
                check((value['meta']['paging']['nextOffset'] is None) and len(''.join(pieces)) > 2000, 'Worker pagination lost data')
                json.loads(''.join(pieces))
                _, preview = call('RestartOpennessWorker')
                check(preview['data']['evidence']['dryRun'] and call('GetOpennessWorkerStatus')[1]['data']['evidence']['worker']['generation'] == before, 'Preview restarted worker')
                _, reset = call('RestartOpennessWorker', {'confirmRestart': True})
                check(reset['ok'] and reset['data']['evidence']['worker']['explicitBindingRequired'], 'Reset did not invalidate bindings')
                _, fresh = call('FindTools', {'query': '', 'limit': 100})
                check(fresh['data']['export']['id'] != export, 'New worker reused an old export identity')
                check(call('GetExportContent', {'exportId': export})[0].get('isError') is True, 'Old export survived restart')
                result, payload = call('CallTool', {'name': 'SaveProject'})
                check(result.get('isError') and payload['error']['code'] == 'PROJECT_NOT_BOUND'
                      and payload['meta']['outcome'] == 'rejected-before-operation'
                      and payload['meta']['execution'] == 'not-started'
                      and not payload['meta']['requiresSessionReset'], 'Recovery allowed unbound write')
                check(payload['data']['evidence']['worker']['explicitBindingRequired']
                      and not payload['data']['evidence']['automaticReplay'], 'Recovery guard lost binding/replay evidence')
                check(rpc('ping', 'ping')['result'] == {}, 'Host did not survive reset')
            print(f'PASS V{args.major} {transport} {profile}: child state, paging, reset, stale handles, write guard', flush=True)

    # Exercise the real proxy's V4 error mapping without requiring an HTTP listener.
    # These faults exist only in the harness child factory; no native call is made.
    fault_transport = 'http' if args.transport == 'http' else 'stdio'
    for mode, unknown, code in (('hello-crash', False, 'RESOURCE_UNAVAILABLE'),
                                ('crash', True, 'OUTCOME_UNKNOWN'),
                                ('fault-after-call', True, 'OUTCOME_UNKNOWN'),
                                ('logical-error', False, 'INVALID_ARGUMENT')):
        with resources.server(args.exe, args.public_api, args.major, fault_transport, 'lite', args.host_harness, args.public_api,
                              isolate=True, env_overrides={'TIA_MCP_TEST_WORKER_FAULT': mode}) as (rpc, http, logs):
            rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'guard-test', 'version': '1'}})
            reply = rpc('tools/call', 'guard', {'name': 'GetSessionState'})
            if args.output:
                (args.output / f'{fault_transport}-{mode}-guard.json').write_text(json.dumps(reply, indent=2), encoding='utf-8')
            result, payload = content(reply)
            check(result['isError'] and not payload['ok'] and payload['error']['code'] == code
                  and payload['meta']['outcome'] == ('unknown' if unknown else 'rejected-before-operation')
                  and payload['meta']['execution'] == ('unknown' if unknown else 'not-started')
                  and payload['meta']['requiresSessionReset'] == unknown, f'{mode}: guard lost its V4 dispatch boundary')
            check(not payload['data']['evidence']['automaticReplay'], f'{mode}: guard enabled automatic replay')
            if mode != 'logical-error':
                result, payload = content(rpc('tools/call', 'reset-required', {'name': 'GetSessionState'}))
                check(result['isError'] and payload['error']['code'] == 'SESSION_RESET_REQUIRED'
                      and payload['meta']['outcome'] == 'rejected-before-operation'
                      and payload['meta']['execution'] == 'not-started' and payload['meta']['requiresSessionReset'],
                      f'{mode}: reset-required guard lost its V4 envelope')
            check(rpc('ping', 'ping')['result'] == {}, f'{mode}: host did not survive guard failure')
        print(f'PASS V{args.major} {fault_transport} {mode}: V4 guard and reset boundary', flush=True)

    if args.transport == 'stdio':
        print(f'COMPLETE: {passed} isolated STDIO checks passed; concurrent HTTP and parent-exit fixtures not run')
        return 0

    # This variable is consumed ONLY by HttpTests.exe's fake child factory, never by production.
    with resources.server(args.exe, args.public_api, args.major, 'http', 'lite', args.host_harness, args.public_api,
                          isolate=True, env_overrides={'TIA_MCP_TEST_WORKER_FAULT': 'hang'}) as (rpc, http, logs):
        rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'fault-test', 'version': '1'}})
        def state():
            return content(rpc('tools/call', 'status', {'name': 'GetOpennessWorkerStatus'}))[1]['data']['evidence']['worker']
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(rpc, 'tools/call', 'hang', {'name': 'GetSessionState'})
            until = time.monotonic() + 5
            while state()['state'] != 'Ready':
                require(time.monotonic() < until, 'Fake child did not start')
                time.sleep(.05)
            started = time.monotonic()
            check(state()['activeTool'] == 'GetSessionState' and time.monotonic() - started < 2, 'HTTP control blocked behind worker')
            check(rpc('ping', 'live-ping')['result'] == {}, 'Ping blocked behind worker')
            reset = content(rpc('tools/call', 'busy-reset', {'name': 'RestartOpennessWorker', 'arguments': {'confirmRestart': True}}))[1]
            check(reset['ok'] is False and reset['error']['code'] == 'PRECONDITION_FAILED',
                  'Busy reset interrupted a tool or incorrectly required a project connection')
            result, payload = content(pending.result(timeout=15))
            check(result['isError'] and payload['meta']['outcome'] == 'unknown', 'Timeout falsely reported safe failure')
        check(state()['state'] == 'Faulted', 'Timeout not latched')
        result, payload = content(rpc('tools/call', 'retry', {'name': 'GetSessionState'}))
        check(result['isError'] and payload['meta']['execution'] == 'not-started', 'Faulted request replayed')
        check(rpc('tools/list', 'post-fault')['result']['tools'], 'Discovery unavailable after worker failure')
        check(content(rpc('tools/call', 'journal', {'name': 'CallTool', 'arguments': {'name': 'GetNativeInvocationLog', 'arguments': {'take': 2}}}))[1]['ok'], 'Host diagnostics inaccessible after fault')
    # Hold a Windows process handle before killing our own test host, so PID reuse
    # cannot turn this into a check against an unrelated process.
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    owned_hosts = []
    handle = None
    try:
        with resources.server(args.exe, args.public_api, args.major, 'http', 'lite', args.host_harness, args.public_api,
                              isolate=True, process_observer=owned_hosts.append) as (rpc, http, logs):
            rpc('initialize', 'parent-init', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'parent-exit-test', 'version': '1'}})
            rpc('tools/call', 'start-child', {'name': 'GetSessionState'})
            worker = content(rpc('tools/call', 'child-id', {'name': 'GetOpennessWorkerStatus'}))[1]['data']['evidence']['worker']
            handle = kernel.OpenProcess(0x00100000, False, worker['workerPid'])  # SYNCHRONIZE only
            check(bool(handle) and kernel.WaitForSingleObject(handle, 0) == 258, 'Child not alive before parent exit')
            owned_hosts[0].kill()
            owned_hosts[0].wait(timeout=5)
            check(kernel.WaitForSingleObject(handle, 5000) == 0, 'Owned child survived abrupt host exit')
    finally:
        if handle:
            kernel.CloseHandle(handle)
    print(f'COMPLETE: {passed} isolated MCP checks passed; no TIA connection attempted')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
