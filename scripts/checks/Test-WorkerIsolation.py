"""Check the real MCP proxy/SDK path with offline child hosts and test-only faults.
No production bootstrap, Openness initialization, Connect or project opens.
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
    result = reply.get('result', {})
    blocks = result.get('content', [])
    require(len(blocks) == 1 and blocks[0].get('type') == 'text', 'Unexpected content envelope')
    try:
        payload = json.loads(blocks[0]['text'])
    except json.JSONDecodeError:
        require(result.get('isError') is True, 'Successful result was not JSON')
        payload = {'_text': blocks[0]['text']}
    return result, payload


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    args = parser.parse_args()
    for key in ('exe', 'public_api', 'host_harness'):
        setattr(args, key, getattr(args, key).resolve())
    passed = 0
    def check(value, message):
        nonlocal passed
        require(value, message)
        passed += 1

    for transport in ('stdio', 'http'):
        for profile in ('full', 'lite'):
            with resources.server(args.exe, args.public_api, args.major, transport, profile, args.host_harness, args.public_api,
                                  isolate=True, env_overrides={'TIA_MCP_MAX_RESPONSE_CHARS': '2000'}) as (rpc, http, logs):
                rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'isolation-test', 'version': '1'}})
                rpc('notifications/initialized', notification=True)
                number = 0
                def call(name, arguments=None):
                    nonlocal number
                    number += 1
                    return content(rpc('tools/call', str(number), {'name': name, 'arguments': arguments or {}}))
                state = call('ReadOpennessWorkerStatus')[1]['meta']['worker']
                check(state['state'] == 'NotStarted' and state['workerPid'] is None, 'Diagnostics launched worker')
                roster = rpc('tools/list', 'list')['result']['tools']
                expected_full = 477 if args.major == 20 else 488  # 11 known V21-only tools are hidden on V20.
                check(len(roster) == (expected_full if profile == 'full' else 63), 'Version-aware tool catalog drift')
                if profile == 'full':
                    check({'ReadPlcBlockEditCapabilities', 'AnalyzePlcReferences', 'PatchPlcBlockDocument', 'ImportPlcBlockVerified'} <= {t['name'] for t in roster}, 'PLC editing tools missing from runtime catalog')
                check(rpc('resources/list', 'resources')['result'] == {'resources': []}, 'Resources changed')
                result, payload = call('GetState')
                check(not result.get('isError') and payload['isConnected'] is False and payload['meta']['success'], 'Offline worker state mismatch')
                state = call('ReadOpennessWorkerStatus')[1]['meta']['worker']
                check(state['state'] == 'Ready' and state['workerPid'] > 0, 'Worker did not initialize')
                before = state['generation']
                # Large response is stored in the worker; the matching paging tool must reach the same child.
                _, large = call('FindTools', {'query': '', 'limit': 100})
                export = large['meta']['exportId']
                pieces, offset = [], 0
                for page in range(200):
                    _, value = call('GetExport', {'exportId': export, 'offset': offset})
                    pieces.append(value['message'])
                    if value['meta']['eof']:
                        break
                    offset = value['meta']['nextOffset']
                check(value['meta']['eof'] and len(''.join(pieces)) > 2000, 'Worker pagination lost data')
                json.loads(''.join(pieces))
                _, preview = call('RestartOpennessWorker')
                check(preview['meta']['dryRun'] and call('ReadOpennessWorkerStatus')[1]['meta']['worker']['generation'] == before, 'Preview restarted worker')
                _, reset = call('RestartOpennessWorker', {'confirmRestart': True})
                check(reset['meta']['success'] and reset['meta']['worker']['explicitBindingRequired'], 'Reset did not invalidate bindings')
                _, fresh = call('FindTools', {'query': '', 'limit': 100})
                check(fresh['meta']['exportId'] != export, 'New worker reused an old export identity')
                check(call('GetExport', {'exportId': export})[0].get('isError') is True, 'Old export survived restart')
                result, payload = call('CallTool', {'name': 'SaveProject'})
                check(result.get('isError') and payload['meta']['nativeOutcomeUnknown'] is False, 'Recovery allowed unbound write')
                check(rpc('ping', 'ping')['result'] == {}, 'Host did not survive reset')
            print(f'PASS V{args.major} {transport} {profile}: child state, paging, reset, stale handles, write guard', flush=True)

    # This variable is consumed ONLY by HttpTests.exe's fake child factory, never by production.
    with resources.server(args.exe, args.public_api, args.major, 'http', 'lite', args.host_harness, args.public_api,
                          isolate=True, env_overrides={'TIA_MCP_TEST_WORKER_FAULT': 'hang'}) as (rpc, http, logs):
        rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'fault-test', 'version': '1'}})
        def state():
            return content(rpc('tools/call', 'status', {'name': 'ReadOpennessWorkerStatus'}))[1]['meta']['worker']
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(rpc, 'tools/call', 'hang', {'name': 'GetState'})
            until = time.monotonic() + 5
            while state()['state'] != 'Ready':
                require(time.monotonic() < until, 'Fake child did not start')
                time.sleep(.05)
            started = time.monotonic()
            check(state()['activeTool'] == 'GetState' and time.monotonic() - started < 2, 'HTTP control blocked behind worker')
            check(rpc('ping', 'live-ping')['result'] == {}, 'Ping blocked behind worker')
            reset = content(rpc('tools/call', 'busy-reset', {'name': 'RestartOpennessWorker', 'arguments': {'confirmRestart': True}}))[1]
            check(reset['meta']['success'] is False and 'prerequisite' not in reset['meta'].get('preflight', {}),
                  'Busy reset interrupted a tool or incorrectly required a project connection')
            result, payload = content(pending.result(timeout=15))
            check(result['isError'] and payload['meta']['nativeOutcomeUnknown'], 'Timeout falsely reported safe failure')
        check(state()['state'] == 'Faulted', 'Timeout not latched')
        result, payload = content(rpc('tools/call', 'retry', {'name': 'GetState'}))
        check(result['isError'] and payload['meta']['nativeOutcomeUnknown'] is False, 'Faulted request replayed')
        check(rpc('tools/list', 'post-fault')['result']['tools'], 'Discovery unavailable after worker failure')
        check(content(rpc('tools/call', 'journal', {'name': 'CallTool', 'arguments': {'name': 'ReadNativeInvocationLog', 'argumentsJson': {'take': 2}}}))[1]['meta']['bridgeSuccess'], 'Host diagnostics inaccessible after fault')
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
            rpc('tools/call', 'start-child', {'name': 'GetState'})
            worker = content(rpc('tools/call', 'child-id', {'name': 'ReadOpennessWorkerStatus'}))[1]['meta']['worker']
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
