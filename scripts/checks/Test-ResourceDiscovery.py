"""Exercise resource discovery against a real EXE over STDIO and HTTP; never connect to TIA."""
import argparse
import hashlib
from contextlib import contextmanager
import json
import os
from pathlib import Path
import queue
import secrets
import shutil
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mcp_results import envelope, successful
from offline_fixtures import fixture_directory
from tool_usage_checks import check_usage, unwrap_usage


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sdk_only_installation(public_api: Path, major: int, temp_root: Path) -> Path:
    """Create a TIA-free installation layout so the real EXE can resolve SDK assemblies."""
    installation = temp_root / "sdk-only-fixtures" / f"Portal V{major}"
    destination = installation / "PublicAPI" / f"V{major}"
    if major == 21:
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
        for major in (20, 21):
            installation = sdk_only_installation(public_api, major, root / f"v{major}")
            api = installation / "PublicAPI" / f"V{major}"
            if major == 21:
                api /= "net48"
            require((api / "Siemens.Engineering.dll").read_bytes() == b"test assembly",
                    f"V{major} SDK-only fixture did not copy the public API")
            require(not list(installation.rglob("*.exe")),
                    f"V{major} SDK-only fixture unexpectedly contains an executable")
    print("PASS SDK-only fixture layouts for V20/V21 contain copied API assemblies and no TIA executable")


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


def engine_fixture_evidence(worker, exe=None):
    if worker is None:
        return {}
    host = foundation_host_executable(exe)
    return {'hostSha256': hashlib.sha256(host.read_bytes()).hexdigest(),
            'sdkWorkerSha256': hashlib.sha256(worker.read_bytes()).hexdigest(),
            'workerModel': 'combined-host-supervised-worker'}


@contextmanager
def server(exe, portal_root, major, transport, profile, harness=None, public_api=None,
           *, env_overrides=None, process_observer=None, evidence_directory=None,
           engine_worker=None, engine_catalog=None):
    require((engine_worker is None) == (engine_catalog is None), 'Both SDK worker and catalog are required')
    inputs = dict(env_overrides=env_overrides, process_observer=process_observer,
                  evidence_directory=evidence_directory, engine_worker=engine_worker, engine_catalog=engine_catalog)
    if engine_worker is not None:
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
           *, env_overrides=None, process_observer=None, evidence_directory=None,
           engine_worker=None, engine_catalog=None):
    port = 0
    if transport == 'http':
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
    endpoint = f'http://127.0.0.1:{port}/mcp'
    key = secrets.token_urlsafe(24)
    worker = engine_worker
    catalog = engine_catalog
    if exe.name != 'TiaMcp.FoundationHost.exe':
        worker = worker or exe.resolve()
        catalog = catalog or worker.with_name('tool-catalog.json')
        exe = foundation_host_executable(exe)
    foundation = True
    bundle_root = (env_overrides or {}).get('TIA_MCP_BUNDLE_ROOT') or os.environ.get('TIA_MCP_BUNDLE_ROOT') or str(Path(__file__).resolve().parents[2])
    args = [str(exe), '--bundle-root', bundle_root, '--release-key', str(major)]
    if worker is not None:
        require(catalog is not None and catalog.is_file(), 'A freshly exported engine worker catalog is required')
        args += ['--engine-worker', str(worker.resolve()), '--engine-catalog', str(catalog.resolve())]
    if portal_root is not None:
        args += ['--tia-portal-location', str(portal_root)]
    args += ['--transport', transport, '--logging', '1']
    if transport == 'http':
        args += ['--http-prefix', f'http://127.0.0.1:{port}/', '--http-api-key', key]
    env = dict(os.environ, TIA_MCP_PROFILE=profile)
    if engine_worker is not None:
        env['TIA_MCP_ENGINE_WORKER_SDK_READY'] = '1'
    if env_overrides:
        env.update(env_overrides)
    process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding='utf-8',
                               env=env, creationflags=subprocess.CREATE_NO_WINDOW)
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
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    session_id = None
    def http(body, authorized=True):
        nonlocal session_id
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream' if foundation else 'application/json'}
        if authorized:
            headers['Authorization'] = 'Bearer ' + key
        if foundation and session_id:
            headers['Mcp-Session-Id'] = session_id
        request = urllib.request.Request(endpoint, json.dumps(body).encode('utf-8'), headers)
        with opener.open(request, timeout=25) as response:
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
            deadline = time.monotonic() + 25
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
            deadline = time.monotonic() + 25
            while True:
                require(process.poll() is None, 'HTTP server exited at startup: ' + ''.join(errors))
                try:
                    request = urllib.request.Request(endpoint + ('/health' if foundation else '/ready'),
                                                     headers={'Authorization': 'Bearer ' + key} if foundation else {'X-API-Key': key})
                    with opener.open(request, timeout=2) as response:
                        if json.load(response).get('status' if foundation else 'mcpHostReady'):
                            break
                except (urllib.error.URLError, TimeoutError):
                    pass
                require(time.monotonic() < deadline, 'HTTP host did not become ready')
                time.sleep(0.1)
        yield rpc, http, errors
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=3 if transport == 'stdio' else 0.2)
        except subprocess.TimeoutExpired:
            # Only the test's own MCP process is stopped; no TIA connection was made.
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
    parser.add_argument('--major', required=True, type=int, choices=(20, 21))
    parser.add_argument('--host-harness', type=Path, help='Compatibility argument; MCP always runs in FoundationHost')
    parser.add_argument('--public-api', type=Path)
    parser.add_argument('--engine-worker', type=Path, help='Explicitly marked SDK-only worker for offline usage examples')
    parser.add_argument('--engine-catalog', type=Path)
    parser.add_argument('--usage-output', type=Path)
    parser.add_argument('--transport', nargs='+', choices=('stdio', 'http'), default=('stdio', 'http'))
    args = parser.parse_args()
    if (args.engine_worker is None) != (args.engine_catalog is None):
        parser.error('Both --engine-worker and --engine-catalog are required')
    if args.engine_worker is not None:
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
                        engine_worker=args.engine_worker, engine_catalog=args.engine_catalog) as (rpc, http, logs):
                initialized = rpc('initialize', params={'protocolVersion': '2024-11-05',
                    'capabilities': {}, 'clientInfo': {'name': 'resource-discovery-test', 'version': '1'}})
                require('result' in initialized, 'Initialize failed')
                capabilities = initialized['result']['capabilities']
                require('resources' in capabilities, 'Resource capability is missing')
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
                    if decoded.get('schemaVersion') == 4 and decoded.get('data', {}).get('export') and 'content' in decoded['data']:
                        pieces = [decoded['data']['content']]
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
