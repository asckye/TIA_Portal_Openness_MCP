"""Soak actual MCP host methods without connecting to TIA or changing machine setup.

Whitelisted local reads, blocked cross references, injected input failures and
recovery are exercised over STDIO / concurrent HTTP, in full / lite profiles.
This cannot establish native TIA stability or recovery from a native crash.
"""
import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import ctypes
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import statistics
import time
import urllib.error
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('resource_discovery', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)
require = resources.require


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def process_sample(process):
    """Read only the owned child PID; no system-wide process enumeration."""
    if hasattr(process, 'poll'):
        require(process.poll() is None, 'Owned test host exited unexpectedly')
    from ctypes import wintypes
    class Counters(ctypes.Structure):
        _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
            'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
            'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    psapi = ctypes.WinDLL('psapi', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    handle = kernel.OpenProcess(0x410, False, process.pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        exit_code = wintypes.DWORD()
        require(kernel.GetExitCodeProcess(handle, ctypes.byref(exit_code)) and exit_code.value == 259,
                'Owned host/worker exited unexpectedly')  # STILL_ACTIVE
        counters = Counters(); counters.cb = ctypes.sizeof(counters)
        handles = wintypes.DWORD()
        if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel.GetProcessHandleCount(handle, ctypes.byref(handles)):
            raise ctypes.WinError(ctypes.get_last_error())
        return {'privateBytes': counters.PrivateUsage, 'workingSetBytes': counters.WorkingSetSize,
                'handleCount': handles.value}
    finally:
        kernel.CloseHandle(handle)


def document(reply, unwrap=False):
    if 'error' in reply:
        return {'_error': True, '_text': json.dumps(reply['error'])}
    result = reply['result']
    text = '\n'.join(c.get('text', '') for c in result.get('content', []) if c.get('type') == 'text')
    if result.get('isError'):
        return {'_error': True, '_text': text}
    value = json.loads(text)
    if unwrap:
        if value.get('meta', {}).get('bridgeSuccess') is not True:
            return {'_error': True, '_text': json.dumps(value)}
        value = bridge_document(value)
    return value


def bridge_document(value):
    # The existing CallTool bridge serializes CLR envelope names in PascalCase;
    # the SDK's direct result uses camelCase. JsonObject metadata keeps its keys.
    return {key[:1].lower() + key[1:]: item for key, item in json.loads(value['message']).items()}


def cases(major, source):
    target = {'objectKind': 'Block', 'objectPath': '__soak_missing__', 'softwarePath': '__soak_no_plc__'}
    xref = {'softwarePath': '__soak_no_plc__', 'objectPath': '__soak_missing__'}
    return [
        ('state', 'GetState', {}, 'state'),
        ('compatibility', 'ReadOpennessCompatibility', {}, 'compatibility'),
        ('format_preflight', 'InspectSimaticSdCompatibility', {'filePath': str(source), 'tiaMajor': major}, 'success'),
        ('xref_block_refused', 'GetCrossReferences', xref, 'xref'),
        ('xref_tag_refused', 'GetCrossReferences', dict(xref, objectKind='Tag'), 'xref'),
        ('xref_unit_refused', 'GetCrossReferences', dict(xref, unitName='__unit__'), 'xref'),
        ('reflect_invoke_refused', 'InvokeService', dict(target, serviceTypeSuffix='CrossReferenceService', methodName='GetCrossReferences', allowWrite=True), 'reflection_refused'),
        ('reflect_suffix_refused', 'DescribeService', dict(target, serviceTypeSuffix='Service'), 'reflection_refused'),
        ('reflect_object_refused', 'InvokeObject', dict(target, methodName='GetCrossReferences', allowWrite=True), 'reflection_refused'),
        ('journal', 'ReadNativeInvocationLog', {'take': 2}, 'success'),
        ('journal_bad_range', 'ReadNativeInvocationLog', {'take': 0}, 'error'),
        ('journal_bad_type', 'ReadNativeInvocationLog', {'take': 'not_an_integer'}, 'error'),
        ('format_missing_file', 'InspectSimaticSdCompatibility', {'filePath': str(source.with_name('missing.s7dcl')), 'tiaMajor': major}, 'error'),
        ('format_bad_version', 'InspectSimaticSdCompatibility', {'filePath': str(source), 'tiaMajor': 99}, 'error'),
        ('bridge_success', 'CallTool', {'name': 'ReadOpennessCompatibility'}, 'bridge_success'),
        ('bridge_recursion', 'CallTool', {'name': 'CallTool'}, 'bridge_error'),
        ('bridge_bad_json', 'CallTool', {'name': 'GetState', 'argumentsJson': '{'}, 'bridge_error'),
        ('bridge_bad_shape', 'CallTool', {'name': 'GetState', 'argumentsJson': []}, 'bridge_error'),
        ('bridge_missing_args', 'CallTool', {'name': 'InspectSimaticSdCompatibility'}, 'bridge_error'),
        ('bridge_unknown_tool', 'CallTool', {'name': '__soak_unknown__'}, 'bridge_error'),
        ('unknown_tool', '__soak_unknown__', {}, 'error'),
    ]


def run_profile(args, transport, profile, run_dir):
    run_dir.mkdir(parents=True, exist_ok=False)
    source = run_dir / 'soak.s7dcl'
    source.write_text('FUNCTION "Soak" : Void\nBEGIN\nEND_FUNCTION\n', encoding='utf-8-sig')
    diagnostics = run_dir / 'diagnostics'
    owned = []
    latencies, samples, worker_samples = [], [], []
    worker_process = None
    counts = Counter()
    sequence = 0
    started = time.monotonic()
    with resources.server(args.exe, args.public_api, args.major, transport, profile,
            args.host_harness, args.public_api, process_observer=owned.append, isolate=args.isolate_openness,
            evidence_directory=run_dir,
            env_overrides={'TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES': '0',
                           'TIA_MCP_DIAGNOSTICS_DIRECTORY': str(diagnostics)}) as (rpc, http, logs):
        hello = rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {},
                    'clientInfo': {'name': 'local-stability', 'version': '1'}})
        require('result' in hello, 'Initialization failed')
        rpc('notifications/initialized', notification=True)
        roster = rpc('tools/list', 'roster')['result']['tools']
        names = {item['name'] for item in roster}
        require(len(names) == len(roster), 'Duplicate registered tools')
        expected_count = args.full_tool_count if profile == 'full' else args.lite_tool_count
        require(len(names) == expected_count, f'{profile} roster changed: {len(names)} != {expected_count}')

        def execute(case, request_id):
            label, name, arguments, expectation = case
            unwrap = profile == 'lite' and name not in names and name != '__soak_unknown__'
            request = {'name': 'CallTool', 'arguments': {'name': name, 'argumentsJson': arguments}} if unwrap else {'name': name, 'arguments': arguments}
            begin = time.monotonic()
            reply = rpc('tools/call', request_id, request)
            elapsed = time.monotonic() - begin
            value = document(reply, unwrap)
            meta = value.get('meta', {})
            if expectation == 'error':
                require(value.get('_error') is True, label + ': input error accepted')
            elif expectation == 'reflection_refused':
                require(value.get('_error') is True and 'cannot be accessed through reflection' in value['_text'], label + ': reflection policy did not refuse before target lookup')
            elif expectation == 'bridge_error':
                require(meta.get('bridgeSuccess') is False and meta.get('success') is False, label + ': bridge failure masked')
            elif expectation == 'bridge_success':
                if meta.get('bridgeSuccess') is not True or meta.get('operationSuccess') is not True:
                    (run_dir / 'failed-response.json').write_text(json.dumps({'case': label, 'requestId': request_id, 'reply': reply}, ensure_ascii=False, indent=2), encoding='utf-8')
                require(meta.get('bridgeSuccess') is True and meta.get('operationSuccess') is True, label + ': bridge failed')
                require(bridge_document(value)['meta']['engineMajor'] == args.major, 'Wrong bridge engine version')
            elif expectation == 'xref':
                require(meta.get('success') is False and meta.get('queried') is False and meta.get('complete') is False and meta.get('status') == 'notQueried', label + ': refusal reported as completed/empty')
            else:
                require(meta.get('success') is True, label + ': expected success')
                if expectation == 'state':
                    require(value['isConnected'] is False and value['project'] == '-', 'Test attached to a project unexpectedly')
                if expectation == 'compatibility':
                    require(meta['engineMajor'] == args.major and meta['nativeCrossReferencesEnabled'] is False and meta['reflectionCrossReferencesAllowed'] is False, 'Wrong engine/policy')
            return label, elapsed

        # Warm every case once before measuring the steady sequence, including JIT/error paths.
        scenario = cases(args.major, source)
        for index, case in enumerate(scenario):
            execute(case, 'warm-' + str(index))
        samples.append(process_sample(owned[0]))
        if args.isolate_openness:
            state = document(rpc('tools/call', 'worker-state', {'name': 'ReadOpennessWorkerStatus', 'arguments': {}}))['meta']['worker']
            require(state['state'] == 'Ready' and state['workerPid'] != owned[0].pid, 'Worker isolation not active')
            worker_process = SimpleNamespace(pid=state['workerPid'])
            worker_samples.append(process_sample(worker_process))
        concurrency = args.concurrency if transport == 'http' else 1
        with ThreadPoolExecutor(max_workers=concurrency) as pool:
            for batch in range(args.rounds):
                pending = []
                for case in scenario:
                    sequence += 1
                    # Unicode and unique string IDs exercise HTTP request-to-response isolation.
                    pending.append(pool.submit(execute, case, f'压测-{sequence}'))
                for future in pending:
                    label, elapsed = future.result(timeout=60)
                    counts[label] += 1; latencies.append(elapsed)
                # A success after each error batch checks gate release / host recovery.
                require(rpc('ping', f'ping-{batch}').get('result') == {}, 'Ping failed after input errors')
                recovered = document(rpc('tools/call', f'recovery-{batch}', {'name': 'GetState', 'arguments': {}}))
                require(recovered['meta']['success'] is True and recovered['isConnected'] is False, 'State failed after error batch')
                unknown = rpc('__soak_unknown_method__', f'unknown-{batch}')
                require(unknown.get('error', {}).get('code') == -32601, 'Unknown method not rejected')
                empty = rpc('resources/list', f'resources-{batch}')
                require(empty.get('result') == {'resources': []}, 'Resources unavailable after errors')
                if transport == 'http':
                    try:
                        http({'jsonrpc': '2.0', 'id': f'unauthorized-{batch}', 'method': 'ping'}, authorized=False)
                        raise AssertionError('Unauthorized request accepted')
                    except urllib.error.HTTPError as error:
                        require(error.code == 401, 'Authentication returned wrong status')
                if (batch + 1) % 10 == 0 or batch + 1 == args.rounds:
                    samples.append(process_sample(owned[0]))
                    if worker_process:
                        worker_samples.append(process_sample(worker_process))
                    print(f'PASS V{args.major} {transport} {profile}: {batch + 1}/{args.rounds} rounds', flush=True)
        sample = process_sample(owned[0])
        require(sample['privateBytes'] < args.max_private_mib * 1024 * 1024, 'Host exceeded private memory bound')
        require(max(s['privateBytes'] for s in samples) < args.max_private_mib * 1024 * 1024, 'Sampled private memory exceeded bound')
        require(max(s['handleCount'] for s in samples) - samples[0]['handleCount'] <= args.max_handle_growth, 'Sampled handles exceeded growth bound')
        if worker_samples:
            require(max(s['privateBytes'] for s in worker_samples) < args.max_private_mib * 1024 * 1024, 'Worker exceeded private memory bound')
            require(max(s['handleCount'] for s in worker_samples) - worker_samples[0]['handleCount'] <= args.max_handle_growth, 'Worker handles exceeded growth bound')
    # The context shuts down only this owned test host. It never stops a TIA process.
    entries = [(path.name, json.loads(line)) for path in diagnostics.glob('calls-*.jsonl') for line in path.read_text(encoding='utf-8').splitlines()]
    require(entries, 'Invocation journal was not written')
    outstanding = Counter()
    stacks = {}
    forwarded, executed = set(), set()
    max_nesting = 0
    for process_log, row in entries:
        require(not row['tool'].startswith('native:'), 'A native stage was entered during a local-only soak')
        worker_dispatch = row['tool'].startswith('worker:')
        # Host controls intentionally remain independent of the blocked worker queue.
        # Each process's tool gate and the host dispatch gate must serialize separately.
        stack = stacks.setdefault((process_log, worker_dispatch), [])
        key = (process_log, row['id'], row['tool'])
        if row['phase'] == 'BEFORE':
            require(not stack or len(stack) == 1 and stack[0][2] == 'CallTool', 'Concurrent top-level tool invocations entered the serialized gate')
            stack.append(key)
            max_nesting = max(max_nesting, len(stack))
            outstanding[key] += 1
            (forwarded if worker_dispatch else executed).add((row['id'], row['tool'][7:] if worker_dispatch else row['tool']))
        else:
            require(row['phase'] in ('RETURNED', 'THREW') and outstanding[key] > 0, 'Uncorrelated journal completion')
            require(stack and stack.pop() == key, 'Tool invocations overlapped or completed out of order')
            outstanding[key] -= 1
    require(not any(outstanding.values()), 'Incomplete invocation after all responses returned')
    if args.isolate_openness:
        require(forwarded and forwarded <= executed, 'Host/child invocation correlation lost')
    log_text = ''.join(logs)
    (run_dir / 'host-stderr.log').write_text(log_text, encoding='utf-8')
    require('Invocation journal unavailable:' not in log_text, 'Invocation journal write failed')
    require(not any(word in log_text for word in ('StackOverflowException', 'OutOfMemoryException', 'Unhandled exception')), 'Fatal host diagnostic')
    ordered = sorted(latencies)
    return {'transport': transport, 'profile': profile, 'concurrency': concurrency,
        'toolCount': len(names), 'workerSamples': worker_samples, 'measuredToolCalls': len(latencies), 'warmupToolCalls': len(scenario),
        'recoveryToolCalls': args.rounds, 'protocolAndAuthChecks': args.rounds * (4 if transport == 'http' else 3),
        'cases': dict(counts), 'elapsedSeconds': round(time.monotonic() - started, 3),
        'latencyMilliseconds': {'median': round(statistics.median(latencies) * 1000, 3),
            'p95': round(ordered[math.ceil(len(ordered) * .95) - 1] * 1000, 3), 'max': round(max(latencies) * 1000, 3)},
        'processSamples': samples, 'journalEntries': len(entries), 'unmatchedJournalEntries': 0,
        'maxInvocationNesting': max_nesting,
        'unexpectedExits': 0, 'unexpectedFailures': 0, 'tiaConnected': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    parser.add_argument('--rounds', type=int, default=50)
    parser.add_argument('--concurrency', type=int, default=8)
    parser.add_argument('--isolate-openness', action='store_true', help='Exercise the supervised child host; still no TIA initialization/connection')
    parser.add_argument('--full-tool-count', type=int, default=486)
    parser.add_argument('--lite-tool-count', type=int, default=62)
    parser.add_argument('--max-private-mib', type=int, default=512)
    parser.add_argument('--max-handle-growth', type=int, default=128)
    parser.add_argument('--output', type=Path, required=True, help='Fresh directory for evidence; existing directories are refused')
    args = parser.parse_args()
    require(os.name == 'nt', 'Windows .NET Framework test host required')
    require(1 <= args.rounds <= 10000 and 1 <= args.concurrency <= 32, 'Rounds 1..10000; concurrency 1..32')
    for name in ('exe', 'public_api', 'host_harness', 'output'):
        setattr(args, name, getattr(args, name).resolve())
    args.output.mkdir(parents=True, exist_ok=False)
    report = {'schemaVersion': 1, 'major': args.major, 'startedAtUtc': datetime.now(timezone.utc).isoformat(),
        'runtimeSha256': sha(args.exe), 'harnessSha256': sha(args.host_harness), 'scriptSha256': sha(Path(__file__)),
        'resourceHelperSha256': sha(Path(resources.__file__)),
        'scope': 'Actual MCP host methods / SDK dispatch with local and refused calls only; no TIA connection, no native crash/hang injection, no production bootstrap, no long-duration leak proof.',
        'rounds': args.rounds, 'isolatedWorker': args.isolate_openness, 'bounds': {'maxPrivateMiB': args.max_private_mib, 'maxHandleGrowth': args.max_handle_growth}, 'runs': []}
    try:
        for transport in ('stdio', 'http'):
            for profile in ('full', 'lite'):
                report['runs'].append(run_profile(args, transport, profile, args.output / f'{transport}-{profile}'))
        report['status'] = 'passed'
        print(f"COMPLETE: V{args.major} {sum(r['measuredToolCalls'] + r['warmupToolCalls'] + r['recoveryToolCalls'] for r in report['runs'])} local tool calls passed; no TIA connection attempted", flush=True)
    except Exception as error:
        report['status'] = 'failed'; report['failure'] = str(error)
        raise
    finally:
        report['finishedAtUtc'] = datetime.now(timezone.utc).isoformat()
        (args.output / 'result.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
