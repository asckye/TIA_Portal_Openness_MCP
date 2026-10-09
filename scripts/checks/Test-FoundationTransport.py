"""Exercise real MCP STDIO/HTTP hosts against a separate synthetic worker. No TIA calls."""
import argparse
from contextlib import nullcontext
import ctypes
from ctypes import wintypes
from tool_usage_checks import check_usage
import json
import hashlib
import os
from pathlib import Path
import queue
import re
import socket
import subprocess
import tempfile
import threading
import time
import ported_families
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
KEYS = ['14sp1', '15.1', '16', '17', '18', '19', '20', '21']
USAGE_REPORTS = []
INIT = {'protocolVersion': '2025-03-26', 'capabilities': {}, 'clientInfo': {'name': 'foundation-transport-test', 'version': '1'}}


class Stdio:
    def __init__(self, command, env, log):
        self.p = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, encoding='utf-8', env=env)
        self.lines = queue.Queue()
        def read():
            for line in self.p.stdout:
                self.lines.put(line)
        threading.Thread(target=read, daemon=True).start()
        self.seq = 0

    def call(self, method, params):
        self.seq += 1
        self.p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': self.seq, 'method': method, 'params': params}) + '\n')
        self.p.stdin.flush()
        while True:
            reply = json.loads(self.lines.get(timeout=15))
            if reply.get('id') == self.seq:
                assert 'error' not in reply, reply
                return reply['result']

    def close(self):
        self.p.stdin.close()
        self.p.wait(timeout=15)
        assert self.p.returncode == 0


class Http:
    def __init__(self, url):
        self.url, self.sid, self.seq = url, None, 0

    def request(self, path, method='GET', body=None, auth=True):
        headers = {'Accept': 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-03-26'}
        if auth:
            headers['Authorization'] = 'Bearer fixture-key'
        if self.sid:
            headers['Mcp-Session-Id'] = self.sid
        if body is not None:
            headers['Content-Type'] = 'application/json'
        req = urllib.request.Request(self.url + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
        return urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=15)

    def call(self, method, params):
        self.seq += 1
        with self.request('/mcp', 'POST', {'jsonrpc': '2.0', 'id': self.seq, 'method': method, 'params': params}) as response:
            self.sid = response.headers.get('Mcp-Session-Id') or self.sid
            if 'text/event-stream' in response.headers.get('Content-Type', ''):
                while True:
                    line = response.readline().decode('utf-8').strip()
                    if line.startswith('data:'):
                        reply = json.loads(line[5:])
                        if reply.get('id') == self.seq:
                            break
                    if not line and response.isclosed():
                        raise AssertionError('No MCP response')
            else:
                reply = json.load(response)
        assert 'error' not in reply, reply
        return reply['result']

    def close(self):
        with self.request('/mcp', 'DELETE') as response:
            assert response.status in (200, 202, 204)


def tool(client, name, args=None):
    result = client.call('tools/call', {'name': name, 'arguments': args or {}})
    assert not result.get('isError'), result
    body = json.loads(result['content'][0]['text'])
    assert body == result['structuredContent']
    assert body['schemaVersion'] == 4 and body['ok'] and body['error'] is None
    assert body['meta']['tool'] == name and body['meta']['outcome'] == 'succeeded'
    return body


def exercise(client, key, logfile, expected_before):
    initialized = client.call('initialize', INIT)
    assert 'GetToolUsage' in initialized.get('instructions', '')
    tools = client.call('tools/list', {})['tools']
    names = {t['name'] for t in tools}
    # check_usage reads the V4 envelope (Foundation plan fields are camelCase since P6-59).
    USAGE_REPORTS.append(check_usage(lambda name, args: tool(client, name, args), tools, key))
    assert 'CallTool' in names and 'Bootstrap' not in names and 'Connect' not in names
    for name in names:
        refused = client.call('tools/call', {'name': name, 'arguments': {'__invalid': True}})
        body = json.loads(refused['content'][0]['text'])
        assert refused['isError'] and body == refused['structuredContent']
        assert body['meta']['outcome'] == 'rejected-before-operation' and body['meta']['execution'] == 'not-started'
        assert body['error']['code'] == 'INVALID_ARGUMENT'
    for name in ('BuildPlcUdt', 'BuildPlcGlobalDb', 'BuildPlcTagTable', 'BuildStructuredText',
                 'BuildFlgNetCall', 'BuildPlcFcBlock', 'BuildPlcFbBlock', 'BuildPlcLadFcBlock'):
        usage = tool(client, 'GetToolUsage', {'toolName': name})['data']
        built = tool(client, name, usage['example']['request']['params']['arguments'])
        assert built['data']['xml'] and not built['data']['schemaValidated'] and not built['data']['importValidated']
        assert any(w['code'] == 'CANDIDATE_ONLY' for w in built['meta']['warnings'])
    assert {'ConnectPortal', 'DisconnectPortal', 'GetProjectTree', 'PlanArtifactImportOrder'} <= names
    assert ('ListPlcWatchTables' in names) == (key != '14sp1')
    assert ('SearchHardwareCatalog' in names) == (key == '19')
    assert 'ExportAsDocuments' not in names
    tool(client, 'InitializeEnvironment')
    plan = tool(client, 'PlanArtifactImportOrder', {'artifacts': [{'id': 'FB_生产', 'dependencies': ['UDT_数据']}, {'id': 'UDT_数据'}]})['data']
    assert plan['valid'] and plan['order'] == ['UDT_数据', 'FB_生产'], plan
    cycle = client.call('tools/call', {'name': 'PlanArtifactImportOrder', 'arguments': {'artifacts': [{'id': 'A', 'dependencies': ['A']}]}})
    assert cycle['isError'] and json.loads(cycle['content'][0]['text'])['error']['code'] == 'INVALID_ARGUMENT'
    assert len(logfile.read_text('utf-8').splitlines()) == expected_before if logfile.exists() else expected_before == 0
    attached = tool(client, 'ConnectPortal', {'processId': 31415})
    assert '31415' in json.dumps(attached)
    assert '生产线' in json.dumps(tool(client, 'GetProjectTree'), ensure_ascii=False)
    assert tool(client, 'DisconnectPortal')['data']['workerAcknowledged'] is True
    client.close()
    return len(names)


def exercise_engine(client, key, close=True, ready=False):
    capabilities = client.call('initialize', INIT)['capabilities']
    # Per-session tool collections must not drop the resource and prompt capabilities of the engine host.
    assert {'tools', 'resources', 'prompts'} <= set(capabilities), capabilities
    tools = client.call('tools/list', {})['tools']
    expected = json.loads((ROOT / f'manifest/contracts/v4/baseline/{key}.json').read_text('utf-8'))
    assert {t['name'] for t in tools} == {t['name'] for t in expected['tools']}
    for entry in tools:
        result = client.call('tools/call', {'name': entry['name'], 'arguments': {'__invalid': True}})
        body = json.loads(result['content'][0]['text'])
        assert result['isError'] and body == result['structuredContent']
        assert body['error']['code'] in ('INVALID_ARGUMENT', 'RESOURCE_UNAVAILABLE', 'UNSUPPORTED_CAPABILITY'), (entry['name'], body)
        assert body['meta']['execution'] == 'not-started', (entry['name'], body)
    result = client.call('tools/call', {'name': 'InitializeEnvironment', 'arguments': {}})
    body = json.loads(result['content'][0]['text'])
    assert body['schemaVersion'] == 4 and body['data']['ready'] == ready
    if close:
        client.close()
    return len(tools)


class ProcessEntry(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD), ('th32ProcessID', wintypes.DWORD),
                ('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
                ('th32ParentProcessID', wintypes.DWORD), ('pcPriClassBase', ctypes.c_long), ('dwFlags', wintypes.DWORD),
                ('szExeFile', ctypes.c_wchar * 260)]


def worker_handles(host_pid, worker_name=None):
    """Open a wait handle on every engine worker the host started."""
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    entry, handles = ProcessEntry(), []
    entry.dwSize = ctypes.sizeof(ProcessEntry)
    try:
        more = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            if entry.th32ParentProcessID == host_pid and (entry.szExeFile.lower().startswith('tiamcp.engine.')
                    or worker_name and entry.szExeFile.lower() == worker_name.lower()):
                handle = kernel.OpenProcess(0x00101000, False, entry.th32ProcessID)
                assert handle, ('Cannot query/wait on worker', entry.th32ProcessID, ctypes.get_last_error())
                handles.append(handle)
            more = kernel.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    return kernel, handles


def engine_worker_exits_cleanly(key, env, fixture_root=None):
    # Without TIA the worker must leave through its normal path; a crash there leaves WER holding its files.
    worker = (fixture_root / key / f'TiaMcp.Engine.V{key}.exe').resolve() if fixture_root else ROOT / f'runtime/v{key}/worker/TiaMcp.Engine.V{key}.exe'
    done = subprocess.run([str(worker), '--engine-worker', '--bundle-root', str(ROOT), '--tia-major-version', key],
                          input=b'', capture_output=True, env=dict(env, TIA_MCP_ENGINE_NONCE='0' * 64), timeout=120)
    assert done.returncode == 0, (key, done.returncode, done.stderr.decode('utf-8', 'replace')[-2000:])


def engine_transports(args, temp, counts):
    for key in (key for key in args.releases if key in ('20', '21')):
        data = temp / ('data-' + key)
        data.mkdir()
        env = dict(os.environ, TIA_MCP_DATA_DIRECTORY=str(data), TiaPortalLocation='')
        env.pop('TIA_MCP_ENGINE_WORKER_SDK_READY', None)
        engine_worker_exits_cleanly(key, env, args.sdk_fixture_root)
        command = [str((args.host_exe or ROOT / f'runtime/v{key}/TiaMcp.FoundationHost.exe').resolve()),
                   '--bundle-root', str(ROOT), '--release-key', key, '--profile', 'full']
        if args.sdk_fixture_root:
            directory = (args.sdk_fixture_root / key).resolve()
            command += ['--engine-worker', str(directory / f'TiaMcp.Engine.V{key}.exe'),
                        '--engine-catalog', str(directory / 'tool-catalog.json')]
        with (args.output / f'stdio-{key}.log').open('w', encoding='utf-8') as stderr:
            client = Stdio(command, env, stderr)
            try:
                counts[key] = exercise_engine(client, key)
            finally:
                if client.p.poll() is None:
                    client.p.terminate(); client.p.wait(10)
        if args.stdio_only:
            continue
        with socket.socket() as port:
            port.bind(('127.0.0.1', 0)); number = port.getsockname()[1]
        url = f'http://127.0.0.1:{number}'
        with (args.output / f'http-{key}.log').open('w', encoding='utf-8') as stderr:
            http_command = command.copy()
            if args.engine_fixture:
                fixture = args.engine_fixture.resolve()
                (fixture.parent / f'TiaMcp.Adapter.{key}.dll').write_text('offline session fixture', encoding='utf-8')
                catalog_source = (args.sdk_fixture_root / key / 'tool-catalog.json') if args.sdk_fixture_root else ROOT / f'runtime/v{key}/worker/tool-catalog.json'
                catalog = json.loads(catalog_source.read_text('utf-8'))
                migrated = {name for family in ported_families.families(ROOT).values() for name in family['tools']}
                catalog['tools'] = [row for row in catalog['tools'] if row['name'] not in migrated]
                catalog['descriptors'] = [row for row in catalog['descriptors'] if row['name'] not in migrated]
                catalog['unavailableTools'] = [row for row in catalog.get('unavailableTools', []) if row['tool']['name'] not in migrated]
                catalog['workerSha256'] = hashlib.sha256(fixture.read_bytes()).hexdigest()
                catalog_path = temp / f'engine-fixture-catalog-{key}.json'
                catalog_path.write_text(json.dumps(catalog), encoding='utf-8')
                http_command += ['--engine-worker', str(fixture), '--engine-catalog', str(catalog_path)]
                config = data / 'config'
                config.mkdir(exist_ok=True)
                (config / 'approval.settings').write_text('enabled=false\ntimeoutSeconds=1\n', encoding='utf-8')
            process = subprocess.Popen(http_command + ['--transport', 'http', '--http-prefix', url + '/', '--http-api-key', 'fixture-key'],
                                       env=env, stdout=stderr, stderr=stderr)
            try:
                deadline = time.monotonic() + 60
                while True:
                    try:
                        with Http(url).request('/mcp/health', auth=False) as response:
                            health = json.load(response)
                            assert health['releaseKey'] == key and health['fileVersion']
                            assert health['profile'] == 'full-engine'
                        break
                    except urllib.error.URLError:
                        assert process.poll() is None and time.monotonic() < deadline
                        time.sleep(.1)
                first, second = Http(url), Http(url)
                try:
                    first.request('/mcp/ready', auth=False)
                    raise AssertionError('Unauthenticated readiness accepted')
                except urllib.error.HTTPError as error:
                    assert error.code == 401
                with first.request('/mcp/ready') as response:
                    assert json.load(response) == {'mcpHostReady': True, 'releaseKey': key, 'nativeAcceptance': 'NOT RUN'}
                exercise_engine(first, key, close=False, ready=bool(args.engine_fixture))
                exercise_engine(second, key, close=False, ready=bool(args.engine_fixture))
                assert first.sid and second.sid and first.sid != second.sid
                kernel, handles = worker_handles(process.pid, args.engine_fixture.name if args.engine_fixture else None)
                try:
                    kernel.GetProcessId.argtypes = [wintypes.HANDLE]
                    pids = [kernel.GetProcessId(handle) for handle in handles]
                    assert len(pids) == 2 and len(set(pids)) == 2, ('Expected two session workers', pids)
                    if args.engine_fixture:
                        tool(first, 'ConnectPortal', {'processId': 123})
                        tool(first, 'DisconnectPortal')
                        tool(second, 'GetSessionState')
                        tool(second, 'ListPortalProcessProjects')
                    first.close()
                    deadline = time.monotonic() + 10
                    while sum(kernel.WaitForSingleObject(handle, 0) == 0 for handle in handles) != 1:
                        assert time.monotonic() < deadline, 'Session deletion did not terminate exactly one worker'
                        time.sleep(.05)
                    # The remaining session's worker is still bound to the host job.
                    process.kill(); process.wait(10)
                    assert all(kernel.WaitForSingleObject(handle, 5000) == 0 for handle in handles), 'Engine worker outlived the killed host'
                finally:
                    for handle in handles:
                        kernel.CloseHandle(handle)
            finally:
                process.terminate(); process.wait(10)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=Path)
    parser.add_argument('--releases', nargs='+', choices=KEYS, default=KEYS)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--temp-root', type=Path, help='New worktree directory for retained fixture logs; avoids restricted system TEMP directories')
    parser.add_argument('--host-exe', type=Path, help='Fresh worktree Foundation host used for each exact release')
    parser.add_argument('--sdk-fixture-root', type=Path, help='Fresh V20/V21 SDK fixture workers and catalogs; avoids stale packaged engines')
    parser.add_argument('--engine-fixture', type=Path, help='LegacyHostTests fixture executable for successful native-shaped engine HTTP calls without TIA; stdio still uses the real no-TIA worker')
    parser.add_argument('--stdio-only', action='store_true', help='Run no HTTP/socket checks when network services are prohibited')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    counts = {}
    if args.temp_root:
        args.temp_root.mkdir(parents=True, exist_ok=False)
    with (nullcontext(args.temp_root.resolve()) if args.temp_root else tempfile.TemporaryDirectory(prefix='tia-foundation-wire-')) as temp:
        temp = Path(temp)
        for key in (key for key in args.releases if key not in ('20', '21')):
            logfile = temp / f'{key}.jsonl'
            env = dict(os.environ, TIA_FIXTURE_LOG=str(logfile))
            command = [str((args.host_exe or ROOT / f'runtime/v{key}/TiaMcp.FoundationHost.exe').resolve()), '--release-key', key, '--worker-exe', str(args.fixture.resolve()), '--public-api', str(temp)]
            with (args.output / f'stdio-{key}.log').open('w', encoding='utf-8') as stderr:
                client = Stdio(command, env, stderr)
                try:
                    counts[key] = exercise(client, key, logfile, 0)
                finally:
                    if client.p.poll() is None:
                        client.p.terminate(); client.p.wait(10)
            records = [json.loads(line) for line in logfile.read_text('utf-8').splitlines()]
            worker_args = records[0]['args']
            assert len(worker_args) == 4 and worker_args[:2] == ['--native-session', key], worker_args
            assert re.fullmatch('[0-9a-f]{64}', worker_args[3]), worker_args
            # Windows can expand a runner's short TEMP name in Path.GetFullPath.
            # Verify the directory identity while keeping the release/argument checks exact.
            assert Path(worker_args[2]).samefile(temp), worker_args
            assert [r['operation'] for r in records if r['stage'] == 'call'] == ['Attach', 'ReadProjectTree', 'Disconnect']
            calls = [r for r in records if r['stage'] == 'call']
            assert [r['Id'] for r in calls] == [1, 2, 3], calls
            assert all(r['Method'] == 'adapter.' + r['operation'] for r in calls), calls
        for key in (key for key in args.releases if key not in ('20', '21') and not args.stdio_only):
            with socket.socket() as port:
                port.bind(('127.0.0.1', 0)); number = port.getsockname()[1]
            url = f'http://127.0.0.1:{number}'
            logfile = temp / f'http-{key}.jsonl'
            env = dict(os.environ, TIA_FIXTURE_LOG=str(logfile))
            command = [str((args.host_exe or ROOT / f'runtime/v{key}/TiaMcp.FoundationHost.exe').resolve()), '--release-key', key, '--worker-exe', str(args.fixture.resolve()), '--public-api', str(temp), '--transport', 'http', '--http-prefix', url + '/', '--http-api-key', 'fixture-key']
            with (args.output / f'http-{key}.log').open('w', encoding='utf-8') as stderr:
                p = subprocess.Popen(command, env=env, stderr=stderr, stdout=stderr)
                try:
                    first, second = Http(url), Http(url)
                    # Hosted runners can start the host slowly; an early exit fails at once with the host log.
                    deadline = time.monotonic() + 60
                    while True:
                        try:
                            with first.request('/mcp/health', auth=False) as response:
                                assert json.load(response)['releaseKey'] == key
                            break
                        except urllib.error.URLError:
                            if p.poll() is not None or time.monotonic() >= deadline:
                                stderr.flush()
                                log = (args.output / f'http-{key}.log').read_text('utf-8', errors='replace')[-4000:]
                                raise AssertionError(f'HTTP host not listening (exit={p.poll()}): {log}')
                            time.sleep(.1)
                    try:
                        first.request('/mcp/ready', auth=False)
                        raise AssertionError('Unauthenticated readiness accepted')
                    except urllib.error.HTTPError as error:
                        assert error.code == 401
                    with first.request('/mcp/ready') as response:
                        assert json.load(response)['mcpHostReady']
                    exercise(first, key, logfile, 0)
                    # Wait for session deletion to close the first synthetic child.
                    deadline = time.monotonic() + 10
                    while '"exit"' not in logfile.read_text('utf-8'):
                        assert time.monotonic() < deadline
                        time.sleep(.05)
                    exercise(second, key, logfile, len(logfile.read_text('utf-8').splitlines()))
                    assert first.sid and second.sid and first.sid != second.sid
                    starts = [json.loads(line) for line in logfile.read_text('utf-8').splitlines() if '"start"' in line]
                    assert len(starts) == 2 and starts[0]['pid'] != starts[1]['pid']
                    assert starts[0]['args'][3] != starts[1]['args'][3]
                finally:
                    p.terminate(); p.wait(10)
        engine_transports(args, temp, counts)
    (args.output / 'tool-usage.json').write_text(json.dumps(USAGE_REPORTS, indent=2) + '\n', encoding='utf-8')
    (args.output / 'result.json').write_text(json.dumps({'stdioToolCounts': counts, 'httpSessions': 0 if args.stdio_only else len(args.releases) * 2, 'unicodeRoundTrip': True, 'workerProtocol': 2, 'workerArguments': 'exact release keys and distinct launch nonces', 'nativeTiaExecuted': False}, indent=2), 'utf-8')
    print(f'COMPLETE: {sum(counts.values()) * (1 if args.stdio_only else 3)} Foundation transport checks passed; {len(counts)} releases; native TIA NOT RUN')


if __name__ == '__main__':
    main()
