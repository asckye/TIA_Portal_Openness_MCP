"""Exercise real MCP STDIO/HTTP hosts against a separate synthetic worker. No TIA calls."""
from offline_fixtures import HANG_GUARD_SECONDS, verify_owned_process_image
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, nullcontext, redirect_stderr
import ctypes
try:
    from ctypes import wintypes
except ValueError:  # some Python builds cannot load wintypes outside Windows
    wintypes = None
from tool_usage_checks import check_usage
import json
import importlib.util
import http.server
import io
import os
from pathlib import Path
from ported_families import RELEASES
import queue
import re
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from call_latency import Samples
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
KEYS = list(RELEASES)
USAGE_REPORTS = {}
LATENCIES = None
CHECK_PROFILE = 'full'
INIT = {'protocolVersion': '2025-03-26', 'capabilities': {}, 'clientInfo': {'name': 'foundation-transport-test', 'version': '1'}}
spec = importlib.util.spec_from_file_location('transport_resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)


def fixture_exit_description(exit_code, stderr_tail=''):
    if exit_code == 86:
        line = next((line for line in stderr_tail.splitlines() if line.startswith('FIXTURE_UNHANDLED_EXCEPTION ')), stderr_tail)
        return 'fixture crashed with an unhandled exception (exit 86): ' + line
    return f'exit {exit_code}' + (': ' + stderr_tail if stderr_tail else '')


def log_tail(log):
    log.flush()
    return Path(log.name).read_text('utf-8', errors='replace')[-4000:]


class Stdio:
    def __init__(self, command, env, log):
        self.p = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, encoding='utf-8', env=env)
        self.lines = queue.Queue()
        def read():
            for line in self.p.stdout:
                self.lines.put(line)
        threading.Thread(target=read, daemon=True).start()
        self.seq = 0
        self.log = log

    def call(self, method, params):
        self.seq += 1
        self.p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': self.seq, 'method': method, 'params': params}) + '\n')
        self.p.stdin.flush()
        while True:
            reply = json.loads(self.lines.get(timeout=HANG_GUARD_SECONDS))
            if reply.get('id') == self.seq:
                assert 'error' not in reply, reply
                return reply['result']

    def close(self):
        self.p.stdin.close()
        self.p.wait(timeout=HANG_GUARD_SECONDS)
        assert self.p.returncode == 0, fixture_exit_description(self.p.returncode, log_tail(self.log))


class Http:
    def __init__(self, url, key='fixture-key'):
        self.url, self.sid, self.seq, self.key = url, None, 0, key
        self.http = resources.PersistentHttp(url)

    def request(self, path, method='GET', body=None, auth=True):
        headers = {'Accept': 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-03-26'}
        if auth:
            headers['Authorization'] = 'Bearer ' + self.key
        if self.sid:
            headers['Mcp-Session-Id'] = self.sid
        if body is not None:
            headers['Content-Type'] = 'application/json'
        return self.http.request(path, method, None if body is None else json.dumps(body).encode(), headers)

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
        try:
            with self.request('/mcp', 'DELETE') as response:
                assert response.status in (200, 202, 204)
        finally:
            self.http.close()


def self_test():
    class Server(http.server.ThreadingHTTPServer):
        daemon_threads = True
        connections = 0

        def get_request(self):
            connection = super().get_request()
            self.connections += 1
            return connection

    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *args):
            pass

        def do_GET(self):
            body = b'{"ok": true}'
            self.send_response(401 if self.path == '/unauthorized' else 200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            if self.path == '/close':
                self.send_header('Connection', 'close')
            self.end_headers()
            self.wfile.write(body)
            if self.path == '/silent-close':
                self.close_connection = True

        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            body = ('data: ' + json.dumps({'jsonrpc': '2.0', 'id': request['id'],
                                         'result': {'ok': True}}) + '\n\n: drained\n\n').encode()
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.send_header('Mcp-Session-Id', 'fixture-session')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_DELETE(self):
            self.send_response(204)
            self.send_header('Content-Length', '0')
            self.end_headers()

    with Server(('127.0.0.1', 0), Handler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        client = Http(f'http://127.0.0.1:{server.server_port}')
        try:
            for _ in range(10):
                with client.request('/json') as response:
                    assert json.load(response) == {'ok': True}
            assert server.connections == 1, ('Expected one TCP connection for ten requests', server.connections)
            try:
                client.request('/unauthorized')
                raise AssertionError('Expected HTTPError')
            except urllib.error.HTTPError as error:
                assert error.code == 401
                assert json.load(error) == {'ok': True}
            with client.request('/json') as response:
                assert response.read(1) == b'{'
            assert client.call('ping', {}) == {'ok': True}
            assert client.sid == 'fixture-session'
            with client.request('/json') as response:
                assert json.load(response) == {'ok': True}
            assert server.connections == 1, 'Errors and partially read responses must reuse the connection'
            with client.request('/close') as response:
                assert json.load(response) == {'ok': True}
            with client.request('/json') as response:
                assert json.load(response) == {'ok': True}
            assert server.connections == 2, 'Expected reconnection after server close'
            with client.request('/silent-close') as response:
                assert json.load(response) == {'ok': True}
            with client.request('/json') as response:
                assert json.load(response) == {'ok': True}
            assert server.connections == 3, 'Expected reconnection after an unannounced server close'
            client.close()
            assert client.http.connection.sock is None, 'Client close must close its socket'
        finally:
            client.http.close()
            server.shutdown()
            thread.join(timeout=15)
            assert not thread.is_alive()
    print('COMPLETE: 7 HTTP client self-tests passed; 0 failed (reuse, HTTPError, drain, SSE, announced/unannounced reconnect, close)')
    transport_scheduling_self_test()


def transport_scheduling_self_test():
    events = []
    slow_stdio = threading.Event()
    fast_http = threading.Event()

    def fake_stdio(key):
        events.append((key, 'stdio'))
        if key == 'slow':
            slow_stdio.set()
            assert fast_http.wait(HANG_GUARD_SECONDS), 'Fast HTTP must not wait for every stdio release'

    def fake_http(key):
        assert (key, 'stdio') in events, 'Each release must run stdio before HTTP'
        if key == 'fast':
            assert slow_stdio.wait(HANG_GUARD_SECONDS)
            fast_http.set()
        events.append((key, 'http'))

    run_transports(['slow', 'fast'], 2, fake_stdio, fake_http)
    assert events.index(('fast', 'http')) < events.index(('slow', 'http'))
    assert set(events) == {('slow', 'stdio'), ('fast', 'stdio'), ('slow', 'http'), ('fast', 'http')}
    events.clear()
    run_transports(['first', 'second'], 1, fake_stdio)
    assert events == [('first', 'stdio'), ('second', 'stdio')]

    releases = list(KEYS)
    together = threading.Barrier(8)
    completed = []

    def check_eight(key):
        together.wait(HANG_GUARD_SECONDS)
        completed.append(key)

    run_releases(releases, 8, check_eight)
    assert sorted(completed) == sorted(releases), 'All eight releases must complete'
    completed.clear()

    def check_failure(key):
        completed.append(key)
        raise RuntimeError('fixture failure ' + key)

    with redirect_stderr(io.StringIO()) as errors:
        try:
            run_releases(releases, 8, check_failure)
            raise AssertionError('Release failures must propagate')
        except RuntimeError as error:
            assert str(error) == 'fixture failure ' + releases[0]
    assert sorted(completed) == sorted(releases), 'A failure must not skip another release'
    assert all('FAILED release ' + key + ':' in errors.getvalue() for key in releases)

    class ExhaustedConnection:
        sock = None
        closed = False

        def request(self, *args, **kwargs):
            error = OSError('fixture socket buffer exhaustion')
            error.winerror = 10055
            raise error

        def close(self):
            self.closed = True

    client = Http('http://127.0.0.1:1')
    client.http.connection = ExhaustedConnection()
    try:
        client.request('/mcp/health', auth=False)
        raise AssertionError('Socket exhaustion must fail outside readiness retries')
    except RuntimeError as error:
        assert 'WinError 10055' in str(error)
        assert client.http.connection.closed
    print('COMPLETE: 5 transport scheduling self-tests passed; 0 failed (per-release order, stdio-only, eight releases, failure collection, socket exhaustion)')


def tool(client, name, args=None):
    result = client.call('tools/call', {'name': name, 'arguments': args or {}})
    assert not result.get('isError'), result
    body = json.loads(result['content'][0]['text'])
    assert body == result['structuredContent']
    assert body['schemaVersion'] == 4 and body['ok'] and body['error'] is None
    assert body['meta']['tool'] == name and body['meta']['outcome'] == 'succeeded'
    if body['data'].get('export') and ('text' in body['data'] or 'content' in body['data']):
        fragments = [body['data'].get('text', body['data'].get('content'))]
        handle = body['data']['export']['id']
        offset = body['meta']['paging']['nextOffset']
        while offset is not None:
            page = tool(client, 'GetExportContent', {'exportId': handle, 'offset': offset})
            fragments.append(page['data']['text'])
            following = page['meta']['paging']['nextOffset']
            assert following is None or following > offset
            offset = following
        body = json.loads(''.join(fragments))
        assert body['schemaVersion'] == 4 and body['ok'] and body['error'] is None
    return body


def exercise(client, key, logfile, expected_before):
    if LATENCIES is not None:
        LATENCIES.instrument(client, key, 'http' if isinstance(client, Http) else 'stdio')
    initialized = client.call('initialize', INIT)
    assert 'GetToolUsage' in initialized.get('instructions', '')
    assert {'tools', 'resources', 'prompts'} <= set(initialized['capabilities'])
    assert client.call('resources/list', {}) == {'resources': []}
    assert client.call('resources/templates/list', {}) == {'resourceTemplates': []}
    tools = client.call('tools/list', {})['tools']
    names = {t['name'] for t in tools}
    client._tool_inventory = tools
    # check_usage reads the V4 envelope (Foundation plan fields are camelCase since P6-59).
    # The release's usage audit requires every receipt to have retrieved and hash-checked the source texts, as Test-ToolUsage.py does.
    if CHECK_PROFILE != 'merge' or isinstance(client, Http):
        USAGE_REPORTS[key].append(check_usage(lambda name, args: tool(client, name, args), tools, key, verify_documents=True))
    assert 'CallTool' in names and 'Bootstrap' not in names and 'Connect' not in names
    for name in names:
        refused = client.call('tools/call', {'name': name, 'arguments': {'__invalid': True}})
        body = json.loads(refused['content'][0]['text'])
        assert refused['isError'] and body == refused['structuredContent']
        assert body['meta']['outcome'] == 'rejected-before-operation' and body['meta']['execution'] == 'not-started'
        assert body['error']['code'] == 'INVALID_ARGUMENT'
    for name in ('BuildPlcUdt', 'BuildPlcGlobalDb'):
        usage = tool(client, 'GetToolUsage', {'toolName': name})['data']
        built = tool(client, name, usage['example']['request']['params']['arguments'])
        assert built['data']['xml'] and not built['data']['schemaValidated'] and not built['data']['importValidated']
        assert any(w['code'] == 'CANDIDATE_ONLY' for w in built['meta']['warnings'])
    for name in ('BuildPlcTagTable', 'BuildStructuredText', 'BuildFlgNetCall',
                 'BuildPlcFcBlock', 'BuildPlcFbBlock', 'BuildPlcLadFcBlock'):
        assert name not in names, (key, name, 'V21-only builder must be withheld')
        refused = client.call('tools/call', {'name': 'GetToolUsage', 'arguments': {'toolName': name}})
        body = json.loads(refused['content'][0]['text'])
        assert refused['isError'] and body == refused['structuredContent']
        assert body['error']['code'] == 'INVALID_ARGUMENT' and body['meta']['execution'] == 'not-started'
    assert {'ConnectPortal', 'DisconnectPortal', 'GetProjectTree', 'PlanArtifactImportOrder'} <= names
    assert ('ListPlcWatchTables' in names) == (key != '14sp1')
    assert ('SearchHardwareCatalog' in names) == (key in ('18', '19'))
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
    if LATENCIES is not None:
        LATENCIES.instrument(client, key, 'http' if isinstance(client, Http) else 'stdio')
    capabilities = client.call('initialize', INIT)['capabilities']
    if ready:
        # SDK/synthetic readiness is observed on the first cached worker operation.
        tool(client, 'GetSessionState')
    # Per-session tool collections must not drop the resource and prompt capabilities of the engine host.
    assert {'tools', 'resources', 'prompts'} <= set(capabilities), capabilities
    assert client.call('resources/list', {}) == {'resources': []}
    assert client.call('resources/templates/list', {}) == {'resourceTemplates': []}
    tools = client.call('tools/list', {})['tools']
    client._tool_inventory = tools
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
    # PROCESSENTRY32W; c_ulong is wintypes.DWORD, spelled out so the module also imports outside Windows.
    _fields_ = [('dwSize', ctypes.c_ulong), ('cntUsage', ctypes.c_ulong), ('th32ProcessID', ctypes.c_ulong),
                ('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', ctypes.c_ulong), ('cntThreads', ctypes.c_ulong),
                ('th32ParentProcessID', ctypes.c_ulong), ('pcPriClassBase', ctypes.c_long), ('dwFlags', ctypes.c_ulong),
                ('szExeFile', ctypes.c_wchar * 260)]


def exercise_isolation(client, first, key, full=False, ready=False):
    if LATENCIES is not None:
        LATENCIES.instrument(client, key, 'http')
    initialized = client.call('initialize', INIT)
    assert 'tools' in initialized['capabilities']
    if full:
        assert {'tools', 'resources', 'prompts'} <= set(initialized['capabilities'])
    tools = client.call('tools/list', {})['tools']
    assert tools == first._tool_inventory, 'Session tool catalogs differ'
    if full:
        # SDK fixtures never enter Openness. Preserve admission checks for the three lifecycle tools.
        for name in ('ConnectPortal', 'GetProjectTree', 'DisconnectPortal'):
            reply = client.call('tools/call', {'name': name, 'arguments': {'__invalid': True}})
            body = json.loads(reply['content'][0]['text'])
            assert reply['isError'] and body == reply['structuredContent']
            assert body['meta']['execution'] == 'not-started'
            assert body['error']['code'] in ('INVALID_ARGUMENT', 'RESOURCE_UNAVAILABLE', 'UNSUPPORTED_CAPABILITY')
        if ready:
            tool(client, 'GetSessionState')
        result = client.call('tools/call', {'name': 'InitializeEnvironment', 'arguments': {}})
        assert json.loads(result['content'][0]['text'])['data']['ready'] == ready
    else:
        attached = tool(client, 'ConnectPortal', {'processId': 31415})
        assert '31415' in json.dumps(attached)
        assert '生产线' in json.dumps(tool(client, 'GetProjectTree'), ensure_ascii=False)
        assert tool(client, 'DisconnectPortal')['data']['workerAcknowledged'] is True
        client.close()
    assert first.sid and client.sid and first.sid != client.sid
    return len(tools)


def worker_handles(host_pid, worker_name):
    """Open a wait handle on every session worker the host started."""
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
            if entry.th32ParentProcessID == host_pid and entry.szExeFile.lower() == worker_name.lower():
                handle = kernel.OpenProcess(0x00101000, False, entry.th32ProcessID)
                assert handle, ('Cannot query/wait on worker', entry.th32ProcessID, ctypes.get_last_error())
                handles.append(handle)
            more = kernel.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    return kernel, handles


def worker_nonce(handle):
    # Query only the two already-owned session worker handles; no WMI or foreign-process probe.
    query = ctypes.WinDLL('ntdll').NtQueryInformationProcess
    query.argtypes = [wintypes.HANDLE, wintypes.ULONG, ctypes.c_void_p, wintypes.ULONG, ctypes.POINTER(wintypes.ULONG)]
    query.restype = wintypes.LONG
    required = wintypes.ULONG()
    query(handle, 60, None, 0, ctypes.byref(required))  # ProcessCommandLineInformation
    assert required.value > 0, 'Worker command line query returned no size'
    buffer = ctypes.create_string_buffer(required.value)
    assert query(handle, 60, buffer, len(buffer), ctypes.byref(required)) == 0, 'Worker command line query failed'
    class UnicodeString(ctypes.Structure):
        _fields_ = [('length', wintypes.USHORT), ('maximum', wintypes.USHORT), ('buffer', ctypes.c_void_p)]
    text = ctypes.cast(buffer, ctypes.POINTER(UnicodeString)).contents
    command = ctypes.wstring_at(text.buffer, text.length // 2)
    match = re.search(r'--native-session\s+\S+\s+(?:"[^"]*"|\S+)\s+([0-9a-f]{64})(?:\s|$)', command)
    assert match, 'Missing protocol-2 launch nonce on owned worker'
    return match.group(1)


class SharedStdio:
    def __init__(self, rpc):
        self.rpc, self.sequence = rpc, 0

    def call(self, method, params):
        self.sequence += 1
        reply = self.rpc(method, self.sequence, params)
        assert 'error' not in reply, reply
        return reply['result']

    def close(self):
        # The shared context owns stdin, wait, image verification and cleanup.
        pass


def plc_transport_inputs(args, key, temp, workers):
    full = key in ('20', '21')
    if full:
        assert key in workers and args.public_api_root, '20/21 require an explicit SDK fixture worker and PublicAPI root'
        api = args.public_api_root / f'TIA_V{key}_PublicAPI' / f'V{key}'
        if key == '21':
            api /= 'net48'
        return workers[key], api, full
    assert args.fixture, 'Old-release transport checks require the synthetic worker'
    return args.fixture.resolve(), temp, full


def resources_helper_overrides(values):
    # Use the snapshot parser's strict, duplicate-refusing RELEASE=PATH contract.
    spec = importlib.util.spec_from_file_location('transport_contracts', Path(__file__).with_name('Snapshot-ToolContracts.py'))
    contracts = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(contracts)
    return contracts.engine_overrides(values, '--plc-sdk-worker')


def run_releases(releases, max_parallel, check):
    """Wait for every release and propagate failure after all results have been observed."""
    failures = []
    with ThreadPoolExecutor(max_workers=max_parallel) as pool:
        futures = [(key, pool.submit(check, key)) for key in releases]
        for key, future in futures:
            try:
                future.result()
            except Exception as error:
                print(f'FAILED release {key}:', file=sys.stderr)
                traceback.print_exception(error, file=sys.stderr)
                failures.append(error)
    if failures:
        raise failures[0]


def run_transports(releases, max_parallel, check_stdio, check_http=None):
    def run_release(key):
        check_stdio(key)
        if check_http is not None:
            check_http(key)
    run_releases(releases, max_parallel, run_release)


def plc_transports(args, temp, counts):
    """All eight releases use the same launcher, transports and session lifetime checks."""
    evidence = {}
    workers = resources_helper_overrides(args.plc_sdk_worker)

    def check_release(key):
        worker, api, full = plc_transport_inputs(args, key, temp, workers)
        evidence_directory = args.output / key
        evidence_directory.mkdir(exist_ok=True)
        host = (args.host_exe or ROOT / f'runtime/v{key}/TiaMcp.FoundationHost.exe').resolve()
        logfile = temp / f'{key}.jsonl'
        data = temp / ('data-' + key)
        data.mkdir()
        env = dict(TIA_FIXTURE_LOG=str(logfile), TIA_MCP_DATA_DIRECTORY=str(data), TiaPortalLocation='')
        if full:
            # The worker leaves through its normal path on a closed channel without TIA (no WER report holds its files).
            done = subprocess.run([str(worker), '--native-session', key, str(api.resolve()), '0' * 64],
                                  input=b'', capture_output=True, timeout=120,
                                  env=dict(os.environ, **env, TIA_MCP_PLC_WORKER_SDK_READY='0'))
            assert done.returncode == 0, (key, fixture_exit_description(done.returncode, done.stderr.decode('utf-8', 'replace')[-2000:]))
        # Retain the no-TIA readiness assertion, then also exercise SDK-ready dispatch.
        # Old releases keep their original launch: no TIA installation location reaches the synthetic worker.
        portal = api if full else None
        for ready in ((False, True) if full else (False,)):
            owned = []
            with resources.server(host, portal, key, 'stdio', 'full', public_api=api, plc_worker=worker,
                                  sdk_ready=ready, env_overrides=env, evidence_directory=evidence_directory,
                                  rpc_timeout=HANG_GUARD_SECONDS, startup_timeout=60, stdio_exit_timeout=HANG_GUARD_SECONDS,
                                  process_observer=owned.append, sdk_installation=full) as (rpc, _, stderr_lines):
                client = SharedStdio(rpc)
                counts[key] = exercise_engine(client, key, ready=ready) if full else exercise(client, key, logfile, 0)
            assert owned[0].returncode == 0, fixture_exit_description(owned[0].returncode, '\n'.join(stderr_lines))
        if full:
            evidence[key] = resources.plc_fixture_evidence(worker, host)
        else:
            records = [json.loads(line) for line in logfile.read_text('utf-8').splitlines()]
            worker_args = records[0]['args']
            assert len(worker_args) == 4 and worker_args[:2] == ['--native-session', key], worker_args
            assert re.fullmatch('[0-9a-f]{64}', worker_args[3]), worker_args
            assert Path(worker_args[2]).samefile(temp), worker_args
            calls = [r for r in records if r['stage'] == 'call']
            assert [r['operation'] for r in calls] == ['Attach', 'ReadProjectTree', 'Disconnect']
            assert [r['Id'] for r in calls] == [1, 2, 3], calls
            assert all(r['Method'] == 'adapter.' + r['operation'] for r in calls), calls
        if args.stdio_only:
            return
        if full:
            unready = []
            with resources.server(host, api, key, 'http', 'full', public_api=api, plc_worker=worker,
                                  sdk_ready=False, env_overrides=env, rpc_timeout=HANG_GUARD_SECONDS, startup_timeout=60,
                                  process_observer=unready.append), ExitStack() as clients:
                # Keep the no-TIA HTTP readiness and schema refusals as well as the ready session proof below.
                command = unready[0].args
                url = command[command.index('--http-prefix') + 1].rstrip('/')
                secret = command[command.index('--http-api-key') + 1]
                unready_first = Http(url, secret)
                clients.callback(unready_first.http.close)
                exercise_engine(unready_first, key, ready=False)
                unready_second = Http(url, secret)
                clients.callback(unready_second.http.close)
                if CHECK_PROFILE == 'merge':
                    exercise_isolation(unready_second, unready_first, key, full=True)
                    unready_second.close()
                else:
                    exercise_engine(unready_second, key, ready=False)
        logfile = temp / f'http-{key}.jsonl'
        env['TIA_FIXTURE_LOG'] = str(logfile)
        owned = []
        with resources.server(host, portal, key, 'http', 'full', public_api=api, plc_worker=worker,
                              sdk_ready=full, env_overrides=env, process_observer=owned.append,
                              evidence_directory=evidence_directory, rpc_timeout=HANG_GUARD_SECONDS, startup_timeout=60,
                              sdk_installation=full) as (_, _, _), ExitStack() as clients:
            process = owned[0]
            command = process.args
            url = command[command.index('--http-prefix') + 1].rstrip('/')
            secret = command[command.index('--http-api-key') + 1]
            first, second = Http(url, secret), Http(url, secret)
            clients.callback(first.http.close)
            clients.callback(second.http.close)
            with first.request('/mcp/health', auth=False) as response:
                health = json.load(response)
                assert health['releaseKey'] == key
                assert health['fileVersion'] and health['profile'] == 'plc-foundation'
            try:
                first.request('/mcp/ready', auth=False)
                raise AssertionError('Unauthenticated readiness accepted')
            except urllib.error.HTTPError as error:
                assert error.code == 401
            with first.request('/mcp/ready') as response:
                ready = json.load(response)
                assert ready['mcpHostReady']
                if full:
                    assert ready == {'mcpHostReady': True, 'releaseKey': key, 'nativeAcceptance': 'NOT RUN'}
            for client in (first, second):
                if full:
                    if CHECK_PROFILE == 'merge' and client is second:
                        exercise_isolation(client, first, key, full=True, ready=True)
                    else:
                        exercise_engine(client, key, close=False, ready=True)
                    tool(client, 'GetSessionState')
                else:
                    before = len(logfile.read_text('utf-8').splitlines()) if logfile.exists() else 0
                    if CHECK_PROFILE == 'merge' and client is second:
                        exercise_isolation(client, first, key)
                    else:
                        exercise(client, key, logfile, before)
                    deadline = time.monotonic() + HANG_GUARD_SECONDS
                    while sum(json.loads(line)['stage'] == 'exit' for line in logfile.read_text('utf-8').splitlines()) < (1 if client is first else 2):
                        assert time.monotonic() < deadline
                        time.sleep(.05)
            assert first.sid and second.sid and first.sid != second.sid
            if full:
                kernel, handles = worker_handles(process.pid, worker.name)
                try:
                    kernel.GetProcessId.argtypes = [wintypes.HANDLE]
                    pids = [kernel.GetProcessId(handle) for handle in handles]
                    assert len(pids) == 2 and len(set(pids)) == 2, ('Expected two session workers', pids)
                    nonces = [worker_nonce(handle) for handle in handles]
                    assert len(set(nonces)) == 2, 'Session workers reused a launch nonce'
                    first.close()
                    deadline = time.monotonic() + HANG_GUARD_SECONDS
                    while sum(kernel.WaitForSingleObject(handle, 0) == 0 for handle in handles) != 1:
                        assert time.monotonic() < deadline, 'Session deletion did not terminate exactly one worker'
                        time.sleep(.05)
                    verify_owned_process_image(process)
                    process.kill(); process.wait(10)
                    assert all(kernel.WaitForSingleObject(handle, 5000) == 0 for handle in handles), 'PLC worker outlived the killed host'
                finally:
                    for handle in handles:
                        kernel.CloseHandle(handle)
            else:
                starts = [json.loads(line) for line in logfile.read_text('utf-8').splitlines() if '"start"' in line]
                assert len(starts) == 2 and starts[0]['pid'] != starts[1]['pid']
                assert starts[0]['args'][3] != starts[1]['args'][3]
    run_releases(args.releases, args.max_parallel, check_release)
    return {key: evidence[key] for key in args.releases if key in evidence}


def main():
    global LATENCIES, CHECK_PROFILE, USAGE_REPORTS
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=Path)
    parser.add_argument('--plc-worker', action='store_true', help='Exercise opt-in PLC workers on 20/21 as well as all six old releases')
    parser.add_argument('--plc-sdk-worker', action='append', default=[], metavar='RELEASE=PATH')
    parser.add_argument('--public-api-root', type=Path)
    parser.add_argument('--releases', nargs='+', choices=KEYS, default=KEYS)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--temp-root', type=Path, help='New worktree directory for retained fixture logs; avoids restricted system TEMP directories')
    parser.add_argument('--host-exe', type=Path, help='Fresh worktree Foundation host used for each exact release')
    parser.add_argument('--stdio-only', action='store_true', help='Run no HTTP/socket checks when network services are prohibited')
    parser.add_argument('--check-profile', choices=('merge', 'full'), default='full')
    parser.add_argument('--max-parallel', type=int, default=4, help='Maximum concurrent releases (default: 4)')
    args = parser.parse_args()
    if args.max_parallel < 1:
        parser.error('--max-parallel must be at least 1')
    # Each worker owns one release's list; flatten only after all workers have finished.
    USAGE_REPORTS = {key: [] for key in args.releases}
    CHECK_PROFILE = args.check_profile
    LATENCIES = Samples('transport', CHECK_PROFILE)
    # P8-04l: V20/V21 run only on the PLC worker (the engine transport branch is retired).
    if not args.plc_worker and set(args.releases) & {'20', '21'}:
        parser.error('V20/V21 transport checks require --plc-worker')
    args.output.mkdir(parents=True, exist_ok=True)
    counts = {}
    worker_evidence = {}
    if args.temp_root:
        args.temp_root.mkdir(parents=True, exist_ok=False)
    with (nullcontext(args.temp_root.resolve()) if args.temp_root else tempfile.TemporaryDirectory(prefix='tia-foundation-wire-')) as temp:
        temp = Path(temp)
        if args.plc_worker:
            worker_evidence = plc_transports(args, temp, counts)
        releases = [key for key in args.releases if key not in ('20', '21') and not args.plc_worker]

        def check_stdio(key):
            (temp / ('data-' + key)).mkdir()
            logfile = temp / f'{key}.jsonl'
            env = dict(os.environ, TIA_FIXTURE_LOG=str(logfile), TIA_MCP_DATA_DIRECTORY=str(temp / ('data-' + key)))
            command = [str((args.host_exe or ROOT / f'runtime/v{key}/TiaMcp.FoundationHost.exe').resolve()), '--bundle-root', str(ROOT), '--release-key', key, '--profile', 'full', '--worker-exe', str(args.fixture.resolve()), '--public-api', str(temp)]
            with (args.output / f'stdio-{key}.log').open('w', encoding='utf-8') as stderr:
                client = Stdio(command, env, stderr)
                try:
                    counts[key] = exercise(client, key, logfile, 0)
                finally:
                    if client.p.poll() is None:
                        verify_owned_process_image(client.p)
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
        def check_http(key):
            number = resources.allocate_http_port()
            url = f'http://127.0.0.1:{number}'
            logfile = temp / f'http-{key}.jsonl'
            env = dict(os.environ, TIA_FIXTURE_LOG=str(logfile), TIA_MCP_DATA_DIRECTORY=str(temp / ('data-' + key)))
            command = [str((args.host_exe or ROOT / f'runtime/v{key}/TiaMcp.FoundationHost.exe').resolve()), '--bundle-root', str(ROOT), '--release-key', key, '--profile', 'full', '--worker-exe', str(args.fixture.resolve()), '--public-api', str(temp), '--transport', 'http', '--http-prefix', url + '/', '--http-api-key', 'fixture-key']
            with ExitStack() as clients, (args.output / f'http-{key}.log').open('w', encoding='utf-8') as stderr:
                p = subprocess.Popen(command, env=env, cwd=temp, stderr=stderr, stdout=stderr)
                try:
                    first, second = Http(url), Http(url)
                    clients.callback(first.http.close)
                    clients.callback(second.http.close)
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
                                raise AssertionError('HTTP host not listening: ' + fixture_exit_description(p.poll(), log))
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
                    deadline = time.monotonic() + HANG_GUARD_SECONDS
                    while '"exit"' not in logfile.read_text('utf-8'):
                        assert time.monotonic() < deadline
                        time.sleep(.05)
                    if CHECK_PROFILE == 'merge':
                        exercise_isolation(second, first, key)
                    else:
                        exercise(second, key, logfile, len(logfile.read_text('utf-8').splitlines()))
                    assert first.sid and second.sid and first.sid != second.sid
                    starts = [json.loads(line) for line in logfile.read_text('utf-8').splitlines() if '"start"' in line]
                    assert len(starts) == 2 and starts[0]['pid'] != starts[1]['pid']
                    assert starts[0]['args'][3] != starts[1]['args'][3]
                finally:
                    verify_owned_process_image(p)
                    p.terminate(); p.wait(10)
        run_transports(releases, args.max_parallel, check_stdio, None if args.stdio_only else check_http)
    usage_reports = [report for key in args.releases for report in USAGE_REPORTS[key]]
    counts = {key: counts[key] for key in args.releases}
    (args.output / 'tool-usage.json').write_text(json.dumps(usage_reports, indent=2) + '\n', encoding='utf-8')
    report = {'stdioToolCounts': counts, 'httpSessions': 0 if args.stdio_only else len(args.releases) * 2,
              'unicodeRoundTrip': True, 'workerProtocol': 2,
              'workerArguments': 'exact release keys and distinct launch nonces', 'nativeTiaExecuted': False}
    if args.plc_worker:
        report['plcWorkers'] = worker_evidence
        report['httpSessions'] += 0 if args.stdio_only else 2 * len(set(args.releases) & {'20', '21'})
    report['checkProfile'] = CHECK_PROFILE
    (args.output / 'result.json').write_text(json.dumps(report, indent=2), 'utf-8')
    LATENCIES.finish(args.output / 'call-latency.json')
    print(f'COMPLETE: {sum(counts.values()) * (1 if args.stdio_only else 3)} Foundation transport checks passed; {len(counts)} releases; native TIA NOT RUN')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    else:
        main()
