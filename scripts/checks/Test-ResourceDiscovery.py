"""Exercise resource discovery against a real EXE over STDIO and HTTP; never connect to TIA."""
from offline_fixtures import HANG_GUARD_SECONDS, verify_owned_process_image
import argparse
import hashlib
import http.client
import io
from contextlib import contextmanager
import json
import os
from pathlib import Path
import queue
import re
import secrets
import shutil
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import sys
import xml.etree.ElementTree as ET
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mcp_results import envelope, successful
from offline_fixtures import fixture_directory
from tool_usage_checks import check_usage, unwrap_usage

_HTTP_PORT_LOCK = threading.Lock()
_HTTP_PORTS = set()


class PersistentHttp:
    def __init__(self, url, timeout=HANG_GUARD_SECONDS):
        self.url = url.rstrip('/')
        address = urllib.parse.urlsplit(self.url)
        # HTTPConnection connects directly and never reads proxy environment settings.
        self.connection = http.client.HTTPConnection(address.hostname, address.port, timeout=timeout)
        self.timeout = timeout
        self.response = None

    def drain(self):
        if self.response is not None:
            try:
                self.response.read()
            finally:
                self.response.close()
                self.response = None

    @contextmanager
    def response_context(self, response):
        try:
            yield response
        finally:
            self.drain()

    def request(self, path, method='GET', body=None, headers=None, timeout=None):
        self.drain()
        self.connection.timeout = self.timeout if timeout is None else timeout
        if self.connection.sock is not None:
            self.connection.sock.settimeout(self.connection.timeout)
        for attempt in range(2):
            reused = self.connection.sock is not None
            try:
                self.connection.request(method, path, body=body, headers=headers or {})
                response = self.connection.getresponse()
                break
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, http.client.RemoteDisconnected) as error:
                self.connection.close()
                # A peer may close an idle keep-alive socket without a Connection: close header.
                if not reused or attempt:
                    raise urllib.error.URLError(error) from error
            except OSError as error:
                self.connection.close()
                raise urllib.error.URLError(error) from error
        self.response = response
        if response.status >= 400:
            error = urllib.error.HTTPError(self.url + path, response.status, response.reason,
                                           response.headers, io.BytesIO(response.read()))
            self.drain()
            raise error
        return self.response_context(response)

    def close(self):
        if self.response is not None:
            self.response.close()
            self.response = None
        self.connection.close()


def allocate_http_port():
    """Never hand two hosts in this process the same loopback port."""
    with _HTTP_PORT_LOCK:
        while True:
            with socket.socket() as sock:
                sock.bind(('127.0.0.1', 0))
                port = sock.getsockname()[1]
            if port not in _HTTP_PORTS:
                _HTTP_PORTS.add(port)
                return port


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sdk_only_installation(public_api: Path, major, temp_root: Path) -> Path:
    """Create a TIA-free installation layout so the real EXE can resolve SDK assemblies."""
    release = str(major)
    install_suffix = {'14sp1': '14', '15.1': '15_1'}.get(release, release)
    api_suffix = '14 SP1' if release == '14sp1' else release
    installation = temp_root / "sdk-only-fixtures" / f"Portal V{install_suffix}"
    destination = installation / "PublicAPI" / f"V{api_suffix}"
    if release == '21':
        destination /= "net48"
    require(public_api.is_dir(), f"V{major} PublicAPI directory is missing: {public_api}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(public_api, destination)
    return installation


def self_test():
    with fixture_directory("sdk-only-installation-test-") as scratch:
        root = Path(scratch)
        public_api = root / "public-api"
        public_api.mkdir()
        (public_api / "Siemens.Engineering.dll").write_bytes(b"test assembly")
        for major in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21'):
            installation = sdk_only_installation(public_api, major, root / f"v{major}")
            api = installation / "PublicAPI" / ("V14 SP1" if major == "14sp1" else f"V{major}")
            if major == "21":
                api /= "net48"
            require((api / "Siemens.Engineering.dll").read_bytes() == b"test assembly",
                    f"V{major} SDK-only fixture did not copy the public API")
            require(not list(installation.rglob("*.exe")),
                    f"V{major} SDK-only fixture unexpectedly contains an executable")
    print("PASS SDK-only fixture layouts for all eight releases contain copied API assemblies and no TIA executable")


def prompt_requirements(root):
    """Derive dependencies from the prompt source, including shared rules and forwarding methods."""
    from engine_sources import block_after
    source = (root / 'src/EngineHost/ModelContextProtocol/McpPrompts.cs').read_text(encoding='utf-8-sig')
    universe = set()
    for path in (root / 'manifest/contracts/v4/baseline').glob('*.json'):
        universe.update(tool['name'] for tool in json.loads(path.read_text(encoding='utf-8'))['tools'])
    shared = set(re.findall(r'\b[A-Z][A-Za-z0-9]+\b', block_after(source, 'private static string WithV4Rules'))) & universe
    matches = list(re.finditer(r'\[McpServerPrompt\(Name = "([^"]+)"\).*?public static string (\w+)\(', source, re.S))
    bodies = {match[2]: source[match.end():matches[index + 1].start() if index + 1 < len(matches) else len(source)]
              for index, match in enumerate(matches)}
    required = {name: set(re.findall(r'\b[A-Z][A-Za-z0-9]+\b', text)) & universe | shared
                for name, text in bodies.items()}
    for _ in bodies:
        for name, text in bodies.items():
            for forwarded in set(re.findall(r'\b(\w+)\(', text)) & bodies.keys():
                required[name].update(required[forwarded])
    return {match[1]: required[match[2]] for match in matches}


def check_prompts(rpc, names, root):
    requirements = prompt_requirements(root)
    expected = {name for name, tools in requirements.items() if tools <= names}
    actual = rpc('prompts/list')['result']['prompts']
    require(len(actual) == len({prompt['name'] for prompt in actual}), 'Duplicate prompt names')
    require({prompt['name'] for prompt in actual} == expected, 'Prompt inventory differs from source dependencies')
    for prompt in actual:
        arguments = {argument['name']: (False if argument['name'] == 'preservePath' else 1 if argument['name'] in ('fbNumber', 'dbNumber') else 'fixture')
                     for argument in prompt.get('arguments', [])}
        reply = rpc('prompts/get', params={'name': prompt['name'], 'arguments': arguments})
        require('error' not in reply and reply['result']['messages'], 'Registered prompt failed to render: ' + prompt['name'] + ': ' + str(reply))
    return len(actual)


def foundation_host_executable(exe=None):
    override = os.environ.get('TIA_MCP_TEST_HOST_PATH')
    if override:
        return Path(override)
    if exe is not None:
        exe = Path(exe).resolve()
        if exe.name == 'TiaMcp.FoundationHost.exe':
            return exe
        packaged = exe.parent.parent / 'TiaMcp.FoundationHost.exe'
        if exe.parent.name == 'worker' and packaged.is_file():
            return packaged
    return Path(__file__).resolve().parents[2] / 'src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe'


def plc_fixture_evidence(worker, exe):
    host = Path(exe) if Path(exe).name == 'TiaMcp.FoundationHost.exe' else foundation_host_executable(exe)
    worker_hash = hashlib.sha256(worker.read_bytes()).hexdigest()
    return {'hostSha256': hashlib.sha256(host.read_bytes()).hexdigest(), 'sdkWorkerSha256': worker_hash,
            'runtimeSha256': worker_hash, 'workerModel': 'foundation-host-plc-worker'}


def host_command(exe, portal_root, major, transport, profile, public_api=None, *,
                 plc_worker=None, bundle_root=None, port=0, key=''):
    """One launch contract for the shared server and transport lifetime probes."""
    exe = Path(exe).resolve()
    if exe.name != 'TiaMcp.FoundationHost.exe':
        exe = foundation_host_executable(exe)
    args = [str(exe), '--bundle-root', str(bundle_root or Path(__file__).resolve().parents[2]),
            '--release-key', str(major)]
    if profile in ('full', 'lite'):
        args += ['--profile', profile]
    if plc_worker is not None:
        require(public_api is not None, 'PLC worker requires its PublicAPI')
        args += ['--plc-worker', '--worker-exe', str(plc_worker.resolve()), '--public-api', str(public_api.resolve())]
    if portal_root is not None:
        args += ['--tia-portal-location', str(Path(portal_root).resolve())]
    args += ['--transport', transport, '--logging', '1']
    if transport == 'http':
        args += ['--http-prefix', f'http://127.0.0.1:{port}/', '--http-api-key', key]
    return args


@contextmanager
def server(exe, portal_root, major, transport, profile, harness=None, public_api=None,
           *, env_overrides=None, process_observer=None, evidence_directory=None,
           plc_worker=None, sdk_ready=True,
           rpc_timeout=HANG_GUARD_SECONDS, startup_timeout=HANG_GUARD_SECONDS, stdio_exit_timeout=3, sdk_installation=True):
    # HTTP configuration watchers must not observe other checks' mutable tree.
    with fixture_directory('host-content-root-') as content_root:
        inputs = dict(env_overrides=env_overrides, process_observer=process_observer,
                      evidence_directory=evidence_directory, content_root=Path(content_root), plc_worker=plc_worker, sdk_ready=sdk_ready,
                      rpc_timeout=rpc_timeout, startup_timeout=startup_timeout, stdio_exit_timeout=stdio_exit_timeout)
        # SDK workers resolve their assemblies from a TIA-free installation layout. The synthetic protocol worker of the
        # six old releases (sdk_installation=False) accepts exactly the four protocol-2 launch arguments, so it gets none.
        if plc_worker is not None and sdk_installation:
            require(public_api is not None, 'SDK worker checks require --public-api')
            with fixture_directory('engine-protocol-sdk-') as scratch:
                portal_root = sdk_only_installation(public_api, major, Path(scratch))
                with _server(exe, portal_root, major, transport, profile, harness, public_api, **inputs) as value:
                    yield value
        else:
            with _server(exe, portal_root, major, transport, profile, harness, public_api, **inputs) as value:
                yield value


@contextmanager
def _server(exe, portal_root, major, transport, profile, harness=None, public_api=None,
           *, env_overrides=None, process_observer=None, evidence_directory=None, content_root=None,
           plc_worker=None, sdk_ready=True,
           rpc_timeout=HANG_GUARD_SECONDS, startup_timeout=HANG_GUARD_SECONDS, stdio_exit_timeout=3):
    exe = Path(exe).resolve()
    if portal_root is not None: portal_root = Path(portal_root).resolve()
    port = 0
    if transport == 'http':
        port = allocate_http_port()
    key = secrets.token_urlsafe(24)
    foundation = True
    bundle_root = (env_overrides or {}).get('TIA_MCP_BUNDLE_ROOT') or os.environ.get('TIA_MCP_BUNDLE_ROOT') or str(Path(__file__).resolve().parents[2])
    args = host_command(exe, portal_root, major, transport, profile, public_api,
                        plc_worker=plc_worker, bundle_root=bundle_root, port=port, key=key)
    env = dict(os.environ, TIA_MCP_PROFILE=profile)
    if plc_worker is not None:
        env['TIA_MCP_PLC_WORKER_SDK_READY'] = '1' if sdk_ready else '0'
    if env_overrides:
        env.update(env_overrides)
    process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding='utf-8',
                               env=env, cwd=content_root, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    output, errors = queue.Queue(), []

    def drain_stdout():
        for line in process.stdout:
            if line.strip():
                output.put(line)
        output.put(None)

    def drain_stderr():
        for line in process.stderr:
            errors.append(line)

    readers = [threading.Thread(target=drain_stdout, daemon=True),
               threading.Thread(target=drain_stderr, daemon=True)]
    for reader in readers:
        reader.start()
    client = PersistentHttp(f'http://127.0.0.1:{port}', timeout=rpc_timeout)

    session_id = None
    def http(body, authorized=True):
        nonlocal session_id
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream' if foundation else 'application/json'}
        if authorized:
            headers['Authorization'] = 'Bearer ' + key
        if foundation and session_id:
            headers['Mcp-Session-Id'] = session_id
        with client.request('/mcp', 'POST', json.dumps(body).encode('utf-8'), headers) as response:
            session_id = response.headers.get('Mcp-Session-Id', session_id)
            raw = response.read().decode('utf-8')
            if response.headers.get('Content-Type', '').startswith('text/event-stream'):
                raw = next(line[6:] for line in raw.splitlines() if line.startswith('data: '))
            return json.loads(raw) if raw else None

    def rpc(method, request_id=1, params=None, include_params=True, notification=False):
        body = {'jsonrpc': '2.0', 'method': method}
        if not notification:
            body['id'] = request_id
        if include_params:
            body['params'] = {} if params is None else params
        if transport == 'http':
            reply = http(body)
        else:
            process.stdin.write(json.dumps(body, ensure_ascii=False) + '\n')
            process.stdin.flush()
            if notification:
                return None
            deadline = time.monotonic() + rpc_timeout
            while True:
                raw = output.get(timeout=max(0.1, deadline - time.monotonic()))
                require(raw is not None, 'Host exited before returning a response: ' + ''.join(errors))
                reply = json.loads(raw)
                if 'id' in reply:
                    break
                require(time.monotonic() < deadline, 'Only notifications received')
        if not notification:
            require(reply is not None and reply.get('id') == request_id,
                    f'{method}: original request ID was not preserved')
        return reply

    try:
        if process_observer:
            process_observer(process)
        if transport == 'http':
            deadline = time.monotonic() + startup_timeout
            while True:
                require(process.poll() is None, 'HTTP server exited at startup: ' + ''.join(errors))
                try:
                    with client.request('/mcp' + ('/health' if foundation else '/ready'),
                                        headers={'Authorization': 'Bearer ' + key} if foundation else {'X-API-Key': key},
                                        timeout=2) as response:
                        if json.load(response).get('status' if foundation else 'mcpHostReady'):
                            break
                except (urllib.error.URLError, TimeoutError):
                    pass
                require(time.monotonic() < deadline, 'HTTP host did not become ready')
                time.sleep(0.1)
        yield rpc, http, errors
    finally:
        client.close()
        process.stdin.close()
        try:
            process.wait(timeout=stdio_exit_timeout if transport == 'stdio' else 0.2)
        except subprocess.TimeoutExpired:
            # Only the test's own MCP process is stopped; no TIA connection was made.
            verify_owned_process_image(process)
            process.terminate()
            process.wait(timeout=5)
        for reader in readers:
            reader.join(timeout=5)
        if evidence_directory is not None:
            Path(evidence_directory, 'host-stderr.log').write_text(''.join(errors), encoding='utf-8')
        process.stdout.close()
        process.stderr.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True, type=Path)
    parser.add_argument('--portal-root', required=True, type=Path)
    parser.add_argument('--major', required=True, choices=('14sp1', '15.1', '16', '17', '18', '19', '20', '21'))
    parser.add_argument('--host-harness', type=Path, help='Compatibility argument; MCP always runs in FoundationHost')
    parser.add_argument('--public-api', type=Path)
    parser.add_argument('--plc-worker', type=Path)
    parser.add_argument('--usage-output', type=Path)
    parser.add_argument('--transport', nargs='+', choices=('stdio', 'http'), default=('stdio', 'http'))
    args = parser.parse_args()
    if args.plc_worker is not None:
        require(args.public_api is not None, 'SDK worker capture requires --public-api')
        with fixture_directory('resource-sdk-worker-') as scratch:
            args.portal_root = sdk_only_installation(args.public_api, args.major, Path(scratch))
            run(args)
    else:
        run(args)


def run(args):
    passed = 0
    usage_records = []
    for transport in args.transport:
        for profile in ('full', 'lite'):
            label = f'V{args.major} {transport} {profile}'
            with server(args.exe.resolve(), args.portal_root.resolve(), args.major,
                        transport, profile, args.host_harness, args.public_api,
                        plc_worker=args.plc_worker) as (rpc, http, logs):
                initialized = rpc('initialize', params={'protocolVersion': '2024-11-05',
                    'capabilities': {}, 'clientInfo': {'name': 'resource-discovery-test', 'version': '1'}})
                require('result' in initialized, 'Initialize failed')
                capabilities = initialized['result']['capabilities']
                require({'tools', 'resources', 'prompts'} <= set(capabilities), 'Discovery capability is missing')
                require(not capabilities['resources'].get('subscribe') and
                        not capabilities['resources'].get('listChanged'),
                        'Unsupported resource subscriptions/notifications advertised')
                rpc('notifications/initialized', notification=True)
                passed += 1
                for method, field in (('resources/list', 'resources'),
                                      ('resources/templates/list', 'resourceTemplates')):
                    for number, request_id in enumerate((7, 'resource-discovery', 7)):
                        response = rpc(method, request_id, include_params=number != 0)
                        require('error' not in response, f'{method}: {response.get("error")}')
                        require(response.get('result') == {field: []},
                                f'{method}: expected empty list, no continuation cursor')
                        passed += 1
                tools = rpc('tools/list')['result']['tools']
                require(any(tool['name'] == 'GetSessionState' for tool in tools), 'Tools disappeared')
                check_prompts(rpc, {row['currentName'] for row in json.loads(ET.parse(Path(__file__).resolve().parents[2] / 'src/Logic/ModelContextProtocol/ToolProfiles.resx').find(".//data[@name='Catalog']/value").text)['releases'][str(args.major)]}, Path(__file__).resolve().parents[2])
                instructions = initialized['result'].get('instructions', '')
                require('GetToolUsage' in instructions and len(instructions) < 1200,
                        'Initialization should point to the unified library concisely')
                passed += 1

                def call_guide_tool(name, arguments):
                    reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
                    require('result' in reply and not reply['result'].get('isError'), str(reply))
                    decoded = envelope(reply)
                    require(reply['result'].get('structuredContent') == decoded and decoded.get('schemaVersion') == 4 and decoded.get('ok'),
                            'Usage retrieval must return the matching V4 envelope')
                    if decoded.get('schemaVersion') == 4 and decoded.get('data', {}).get('export') and ('text' in decoded['data'] or 'content' in decoded['data']):
                        pieces = [decoded['data'].get('text', decoded['data'].get('content'))]
                        handle = decoded['data']['export']['id']
                        offset = decoded['meta']['paging']['nextOffset']
                        while offset is not None:
                            page = call_guide_tool('GetExportContent', {'exportId': handle, 'offset': offset})
                            pieces.append(page['data']['text'])
                            offset = page['meta']['paging']['nextOffset']
                        decoded = json.loads(''.join(pieces))
                    return decoded

                require('GetToolUsage' in initialized['result'].get('instructions', ''), 'Usage instructions missing')
                usage_report = check_usage(call_guide_tool, tools, args.major,
                    exhaustive=transport == 'stdio' and profile == 'full',
                    verify_documents=transport == 'stdio' and profile == 'full')
                usage_records.append(dict(usage_report, transport=transport, profile=profile))
                if args.plc_worker is not None:
                    usage_records[-1].update(plc_fixture_evidence(args.plc_worker, args.exe))
                passed += 1
                guide = unwrap_usage(call_guide_tool('GetToolUsage', {'language': 'scl', 'exampleKind': 'language'}))
                require(guide['examples'], 'Merged language guide is empty')
                passed += 1
                found = call_guide_tool('FindTools', {'query': 'ManageStartdriveParameter', 'limit': 1})
                require('GetToolUsage' in json.dumps(found), 'Discovery omitted example route')
                passed += 1
                direct = unwrap_usage(call_guide_tool('GetToolUsage', {'toolName': 'ManageStartdriveParameter', 'operation': 'read'}))
                require(direct['example']['request']['params']['arguments']['action'] == 'read', 'Merged BICO topic changed action')
                passed += 1
                sequence = unwrap_usage(call_guide_tool('GetToolUsage', {'exampleId': 'sequence/connect-project', 'exampleKind': 'sequence'}))['examples'][0]
                require(sequence['steps'] and all(key in sequence for key in ('purpose', 'preconditions', 'notes')), 'Merged recipe lost its details')
                passed += 1
                bridged = unwrap_usage(call_guide_tool('CallTool', {'name': 'GetToolUsage', 'arguments': {'language': 'scl', 'exampleKind': 'language'}}))
                require(bridged == guide, 'Bridge did not deliver the same examples')
                passed += 1
                state = rpc('tools/call', params={'name': 'GetSessionState', 'arguments': {}})
                require('result' in state and not state['result'].get('isError') and
                        state['result'].get('content'), 'GetSessionState failed after resource discovery')
                passed += 1
                unknown = rpc('unknown/resource-discovery-test')
                if profile == 'full':
                    planned = rpc('tools/call', params={'name': 'PlanArtifactImportOrder', 'arguments': {'artifacts': [{'id': 'FB', 'dependencies': ['UDT']}, {'id': 'UDT'}]}})
                    data = envelope(planned)['data']
                    plan = data.get('plan', data)
                    require(plan.get('valid', plan.get('Valid')) and plan.get('order', plan.get('Order')) == ['UDT', 'FB'], 'Shared dependency planner failed through real MCP transport')
                    passed += 1
                require(unknown.get('error', {}).get('code') == -32601,
                        'Unknown method no longer returns MethodNotFound')
                passed += 1
                if transport == 'http':
                    try:
                        http({'jsonrpc': '2.0', 'id': 1, 'method': 'resources/list'}, authorized=False)
                        raise AssertionError('Resource discovery bypassed HTTP authentication')
                    except urllib.error.HTTPError as error:
                        require(error.code == 401, 'Expected HTTP 401 for resource discovery')
                    passed += 1
            print('PASS ' + label + ': resources, templates, capabilities, repeated IDs and tools')
    if args.usage_output:
        args.usage_output.write_text(json.dumps(usage_records, indent=2) + '\n', encoding='utf-8')
    print(f'COMPLETE: {passed} resource discovery checks passed; no TIA connection attempted')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    else:
        main()
