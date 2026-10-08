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
import re
import statistics
import time
import urllib.error
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[2]
TARGET_DISPATCHERS = {'calltool', 'runreadonlytoolbatch', 'previewtoolbatch', 'applytoolbatch'}
LOCAL_LANE_BOUND = 8
WORKER_PIPE_BOUND = 8  # OpennessWorkerSupervisor forwarding gate


def openness_lane_classifier():
    """Same source as ToolDispatchLanes: ToolTaxonomy.UsesOpennessLane and DispatchesTargets."""
    source = (ROOT / 'src/Logic/ModelContextProtocol/ToolTaxonomy.cs').read_text(encoding='utf-8')
    def names(field):
        body = re.search(r'HashSet<string> ' + field + r' = new HashSet<string>\([^)]*\)\s*\{(.*?)\};', source, re.S)
        if body is None:
            raise AssertionError('ToolTaxonomy.' + field + ' not found')
        return {name.lower() for name in re.findall(r'"([^"]+)"', body.group(1))}
    without_tia, session_readers = names('WithoutTia'), names('SessionReaders')
    def lane(tool):
        name = tool.lower()
        if name in TARGET_DISPATCHERS:
            return None
        return 'openness' if name not in without_tia or name in session_readers else 'local'
    return lane
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
        return {'_protocolCode': reply['error']['code']}
    result = reply['result']
    blocks = [c for c in result.get('content', []) if c.get('type') == 'text']
    require(len(blocks) == 1, 'Expected one V4 text block')
    value = json.loads(blocks[0]['text'])
    require(value.get('schemaVersion') == 4 and result.get('structuredContent') == value,
            'V4 structuredContent/text mismatch')
    require(bool(result.get('isError')) == (value['ok'] is False), 'V4 isError mismatch')
    # CallTool now returns the target envelope unchanged; no message decoding.
    return value


def cases(major, source):
    target = {'objectKind': 'Block', 'objectPath': '__soak_missing__', 'softwarePath': '__soak_no_plc__'}
    xref = {'softwarePath': '__soak_no_plc__', 'objectPath': '__soak_missing__'}
    return [
        ('state', 'GetSessionState', {}, 'state'),
        ('compatibility', 'GetOpennessCompatibility', {}, 'compatibility'),
        ('format_preflight', 'InspectSimaticSdCompatibility', {'filePath': str(source), 'tiaMajor': major}, 'success'),
        ('xref_block_refused', 'GetPlcCrossReferences', xref, 'xref'),
        ('xref_tag_refused', 'GetPlcCrossReferences', dict(xref, objectKind='Tag'), 'xref'),
        ('xref_unit_refused', 'GetPlcCrossReferences', dict(xref, unitName='__unit__'), 'xref'),
        ('reflect_invoke_refused', 'InvokeService', dict(target, serviceTypeSuffix='CrossReferenceService', methodName='GetCrossReferences', allowWrite=True), 'reflection_refused'),
        ('reflect_suffix_refused', 'DescribeService', dict(target, serviceTypeSuffix='Service'), 'reflection_refused'),
        ('reflect_object_refused', 'InvokeObject', dict(target, methodName='GetCrossReferences', allowWrite=True), 'reflection_refused'),
        ('journal', 'GetNativeInvocationLog', {'take': 2}, 'success'),
        ('journal_bad_range', 'GetNativeInvocationLog', {'take': 0}, 'error'),
        ('journal_bad_type', 'GetNativeInvocationLog', {'take': 'not_an_integer'}, 'error'),
        ('format_missing_file', 'InspectSimaticSdCompatibility', {'filePath': str(source.with_name('missing.s7dcl')), 'tiaMajor': major}, 'error'),
        ('format_bad_version', 'InspectSimaticSdCompatibility', {'filePath': str(source), 'tiaMajor': 99}, 'error'),
        ('bridge_success', 'CallTool', {'name': 'GetOpennessCompatibility'}, 'bridge_success'),
        ('bridge_recursion', 'CallTool', {'name': 'CallTool'}, 'bridge_error'),
        ('bridge_bad_json', 'CallTool', {'name': 'GetSessionState', 'arguments': '{'}, 'bridge_error'),
        ('bridge_bad_shape', 'CallTool', {'name': 'GetSessionState', 'arguments': []}, 'bridge_error'),
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
        # The engine's own roster: the product roster (ToolProfiles) is FoundationHost's since P7-04,
        # so the first run of each profile sets the count every later run must keep.
        key = 'full_tool_count' if profile == 'full' else 'lite_tool_count'
        if getattr(args, key) is None: setattr(args, key, len(names))
        expected_count = getattr(args, key)
        require(len(names) == expected_count, f'{profile} roster changed: {len(names)} != {expected_count}')

        def execute(case, request_id):
            label, name, arguments, expectation = case
            unwrap = profile == 'lite' and name not in names and name != '__soak_unknown__'
            request = {'name': 'CallTool', 'arguments': {'name': name, 'arguments': arguments}} if unwrap else {'name': name, 'arguments': arguments}
            begin = time.monotonic()
            reply = rpc('tools/call', request_id, request)
            elapsed = time.monotonic() - begin
            value = document(reply, unwrap)
            meta = value.get('meta', {})
            error_codes = {
                'journal_bad_range': 'INTERNAL_ERROR', 'journal_bad_type': 'INVALID_ARGUMENT',
                'format_missing_file': 'INVALID_ARGUMENT', 'format_bad_version': 'INTERNAL_ERROR',
                'bridge_recursion': 'INVALID_ARGUMENT', 'bridge_bad_json': 'INVALID_ARGUMENT',
                'bridge_bad_shape': 'INVALID_ARGUMENT', 'bridge_missing_args': 'INVALID_ARGUMENT',
                'bridge_unknown_tool': 'TOOL_NOT_FOUND', 'reflect_suffix_refused': 'NATIVE_OPERATION_FAILED',
                'reflect_invoke_refused': 'OUTCOME_UNKNOWN', 'reflect_object_refused': 'OUTCOME_UNKNOWN',
            }
            if label == 'unknown_tool':
                require(value.get('_protocolCode') == -32602, label + ': wrong protocol code')
            elif label in error_codes:
                require(value.get('ok') is False and value['error']['code'] == error_codes[label],
                        label + ': wrong V4 error code: ' + json.dumps(value))
            elif expectation == 'xref':
                require(value['ok'] is False and value['error']['code'] == 'PRECONDITION_FAILED'
                        and meta['execution'] == 'not-started' and meta['completeness'] == 'none'
                        and value['data']['queried'] is False and value['data']['complete'] is False,
                        label + ': refusal reported as completed/empty')
            else:
                require(value.get('ok') is True and meta['outcome'] == 'succeeded', label + ': expected success')
                if expectation == 'state':
                    require(value['data']['isConnected'] is False and value['data']['project'] == '-', 'Test attached unexpectedly')
                    require(value['data']['evidence']['journalHealth']['failedWrites'] == 0, 'Invocation journal write failed')
                if expectation in ('compatibility', 'bridge_success'):
                    evidence = value['data']['evidence']
                    require(evidence['engineMajor'] == args.major and evidence['nativeCrossReferencesEnabled'] is False
                            and evidence['reflectionCrossReferencesAllowed'] is False, 'Wrong engine/policy')
            require(not any(w['code'] == 'DIAGNOSTIC_WRITE_FAILED' for w in meta.get('warnings', [])),
                    'Invocation journal write failed')
            return label, elapsed

        # Warm every case once before measuring the steady sequence, including JIT/error paths.
        scenario = cases(args.major, source)
        for index, case in enumerate(scenario):
            execute(case, 'warm-' + str(index))
        samples.append(process_sample(owned[0]))
        if args.isolate_openness:
            state = document(rpc('tools/call', 'worker-state', {'name': 'GetOpennessWorkerStatus', 'arguments': {}}))['data']['evidence']['worker']
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
                recovered = document(rpc('tools/call', f'recovery-{batch}', {'name': 'GetSessionState', 'arguments': {}}))
                require(recovered['ok'] is True and recovered['data']['isConnected'] is False and recovered['data']['evidence']['journalHealth']['failedWrites'] == 0, 'State failed after error batch')
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
        # Concurrent HTTP dispatch (P6-58) lets the thread pool grow, more so on a loaded machine, and that growth
        # levels off; a leak keeps rising with the call count. Judge the second half of the run, from its middle
        # sample, and print the series on failure.
        def handle_growth(series):
            baseline = series[len(series) // 2] if len(series) > 3 else series[1] if len(series) > 2 else series[0]
            later = series[series.index(baseline):]
            return max(s['handleCount'] for s in later) - baseline['handleCount']
        def handles(series):
            return [s['handleCount'] for s in series]
        require(handle_growth(samples) <= args.max_handle_growth, f'Sampled handles exceeded growth bound: {handles(samples)}')
        if worker_samples:
            require(max(s['privateBytes'] for s in worker_samples) < args.max_private_mib * 1024 * 1024, 'Worker exceeded private memory bound')
            require(handle_growth(worker_samples) <= args.max_handle_growth, f'Worker handles exceeded growth bound: {handles(worker_samples)}')
    # The context shuts down only this owned test host. It never stops a TIA process.
    rows = [(path.name, json.loads(line)) for path in diagnostics.glob('calls-*.jsonl') for line in path.read_text(encoding='utf-8').splitlines()]
    # Call-projection rows (Workbench call panel) are written at the transport boundary, outside the serialized gate.
    entries = [(name, row) for name, row in rows if not row.get('callProjection')]
    projections = [(name, row) for name, row in rows if row.get('callProjection')]
    require(entries, 'Invocation journal was not written')
    require(projections, 'Call-projection rows were not written')
    projection_phases = {}
    for name, row in projections:
        projection_phases.setdefault((name, row['id'], row['tool']), []).append(row['phase'])
    require(all(phases[0] == 'BEFORE' and phases[-1] in ('RETURNED', 'INTERRUPTED') and len(phases) == 2
                for phases in projection_phases.values()), 'Every call projection pairs one BEFORE with one terminal row')
    outstanding = Counter()
    active = {}
    forwarded, executed = set(), set()
    max_nesting = max_local = max_openness = 0
    lane_of = openness_lane_classifier()
    for process_log, row in entries:
        require(not row['tool'].startswith('native:'), 'A native stage was entered during a local-only soak')
        worker_dispatch = row['tool'].startswith('worker:')
        tool = row['tool'][7:] if worker_dispatch else row['tool']
        # Host controls intentionally remain independent of the blocked worker queue, so each process's tool
        # dispatch and the host's worker dispatch are checked separately. P6-58 lanes: Openness-lane tools never
        # overlap within one process (the soak has one TIA session per host); local tools run concurrently up to
        # the local lane bound; target dispatchers (CallTool, batches) take their targets' lanes. The isolation
        # parent forwards up to WORKER_PIPE_BOUND requests (the child asks for approval before its exclusive
        # portal lane), so its worker: rows are bounded, and the child's own journal must show no overlap.
        lanes = active.setdefault((process_log, worker_dispatch), {'openness': Counter(), 'local': Counter(), None: Counter()})
        # Journal labels are method names: a V4 implementation row (e.g. GetOpennessCompatibilityV4) nests
        # inside its tool's row with the same id. Lanes are held per call id.
        lane = lane_of(re.sub('V4$', '', tool))
        key = (process_log, row['id'], row['tool'])
        if row['phase'] == 'BEFORE':
            require(lane != 'openness' or worker_dispatch or not any(count and other[1] != row['id'] for other, count in lanes['openness'].items()),
                    'Openness-lane tool invocations overlapped')
            lanes[lane][key] += 1
            local = len({other[1] for other, count in lanes['local'].items() if count})
            opened = len({other[1] for other, count in lanes['openness'].items() if count})
            require(local <= LOCAL_LANE_BOUND, 'Local tool invocations exceeded the local lane bound')
            require(opened <= WORKER_PIPE_BOUND, 'Forwarded worker requests exceeded the supervisor bound')
            max_local, max_openness = max(max_local, local), max(max_openness, opened)
            max_nesting = max(max_nesting, sum(sum(keys.values()) for keys in lanes.values()))
            outstanding[key] += 1
            (forwarded if worker_dispatch else executed).add((row['id'], tool))
        else:
            require(row['phase'] in ('RETURNED', 'THREW') and outstanding[key] > 0, 'Uncorrelated journal completion')
            require(lanes[lane][key] > 0, 'Tool invocation completed outside its lane')
            lanes[lane][key] -= 1
            outstanding[key] -= 1
    require(not any(outstanding.values()), 'Incomplete invocation after all responses returned')
    if args.isolate_openness:
        require(forwarded and forwarded <= executed, 'Host/child invocation correlation lost')
    log_text = ''.join(logs)
    (run_dir / 'host-stderr.log').write_text(log_text, encoding='utf-8')
    ordered = sorted(latencies)
    return {'transport': transport, 'profile': profile, 'concurrency': concurrency,
        'toolCount': len(names), 'workerSamples': worker_samples, 'measuredToolCalls': len(latencies), 'warmupToolCalls': len(scenario),
        'recoveryToolCalls': args.rounds, 'protocolAndAuthChecks': args.rounds * (4 if transport == 'http' else 3),
        'cases': dict(counts), 'elapsedSeconds': round(time.monotonic() - started, 3),
        'latencyMilliseconds': {'median': round(statistics.median(latencies) * 1000, 3),
            'p95': round(ordered[math.ceil(len(ordered) * .95) - 1] * 1000, 3), 'max': round(max(latencies) * 1000, 3)},
        'processSamples': samples, 'journalEntries': len(entries), 'unmatchedJournalEntries': 0,
        'maxInvocationNesting': max_nesting, 'maxConcurrentLocal': max_local, 'maxConcurrentOpenness': max_openness,
        'unexpectedExits': 0, 'unexpectedFailures': 0, 'tiaConnected': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    parser.add_argument('--rounds', type=int, default=50)
    parser.add_argument('--transports', nargs='+', choices=('stdio', 'http'), default=('stdio', 'http'))
    parser.add_argument('--concurrency', type=int, default=8)
    parser.add_argument('--isolate-openness', action='store_true', help='Exercise the supervised child host; still no TIA initialization/connection')
    parser.add_argument('--full-tool-count', type=int, default=None, help="Defaults to the engine's own roster from the first run")
    parser.add_argument('--lite-tool-count', type=int, default=None)
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
        for transport in args.transports:
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
