"""Engine-harness successors on FoundationHost, SDK-ready PlcWorker and adapters."""
import argparse
from contextlib import contextmanager
import importlib.util
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import selectors
import socket
import subprocess
import sys
import threading
import xml.etree.ElementTree as ET

from offline_fixtures import fixture_directory

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


resources = load('Test-ResourceDiscovery')
MODES = ('http', 'descriptor-catalog-only', 'check-modes-self-test', 'concurrency-only', 'staging-sessions-only',
         'hmi-only', 'hmi-snapshot-only', 'global-script-only', 'graphic-selection-only', 'runtime-settings-only',
         'native-export-only', 'test-migration-read-assembly', 'test-ecosystem-assembly', 'software-lookup-only',
         'example-library-only', 'process-leases-only', 'native-diagnostics-only', 'test-download-route', 'test-match-plc-name')


def run(command, **kwargs):
    result = subprocess.run([str(value) for value in command], capture_output=True, text=True, encoding='utf-8',
                            errors='replace', cwd=ROOT, **kwargs)
    if result.returncode:
        raise AssertionError(f'{command[0]} exited {result.returncode}\n{result.stdout}\n{result.stderr}')
    return result.stdout


def file_app_environment(env=None):
    # dotnet run --file --no-build looks for the program that build-solutions/check-all compiled below TEMP.
    # check-all gives each job a private TEMP and records the original one; only the file-app process uses it.
    environment = dict(os.environ if env is None else env)
    system_temp = environment.get('TIA_MCP_CHECK_SYSTEM_TEMP_ROOT')
    if system_temp:
        environment['TEMP'] = environment['TMP'] = system_temp
    return environment


def shape(args, *values, env=None, **kwargs):
    return run(['dotnet', 'run', '--file', Path(__file__).with_suffix('.cs'), '-c', 'Release',
                '--no-build', '--no-restore', '--', *values], env=file_app_environment(env), **kwargs)


def call_tool(rpc, name, arguments, staged=True):
    reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
    resources.require('result' in reply and 'structuredContent' in reply['result'], f'No V4 result: {reply}')
    value = reply['result']['structuredContent']
    resources.require(value['schemaVersion'] == 4 and all(key in value for key in ('ok', 'data', 'error', 'meta'))
                      and bool(reply['result'].get('isError')) != value['ok'], 'Invalid V4 envelope')
    data = value.get('data') or {}
    # Two staged shapes: a usage export (data.content) and the host's parking of any large V4 reply
    # (data.text with meta.paging, McpServer.Exports). Reassemble the complete logical reply from offset 0.
    head = data.get('content', data.get('text')) if staged and data.get('export') else None
    if head is not None:
        resources.require('content' in data or value['meta']['paging']['offset'] == 0, 'Staged reply does not start at offset 0')
        pieces = [head]
        offset = value['meta']['paging']['nextOffset']
        while offset is not None:
            page = call_tool(rpc, 'GetExportContent', {'exportId': data['export']['id'], 'offset': offset}, staged=False)
            resources.require(page['ok'] and page['meta']['paging']['offset'] == offset, 'Staged reply page is incomplete')
            pieces.append(page['data']['text'])
            offset = page['meta']['paging']['nextOffset']
        value = json.loads(''.join(pieces))
    return value


@contextmanager
def bridge(args, rpc, metadata):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            try:
                request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                command, inputs = request['command'], request['arguments']
                if command == 'CallTool':
                    result = call_tool(rpc, inputs['name'], inputs['arguments'])
                    (args.output / f'call-{self.server.calls:03d}.json').write_text(json.dumps(result), encoding='utf-8')
                    self.server.calls += 1
                elif command == 'ToolMetadata':
                    result = metadata
                elif command in ('Compare', 'Decode'):
                    result = json.loads(shape(args, 'rpc', input=json.dumps(request)))
                else:
                    raise ValueError('Unknown harness bridge command: ' + command)
            except Exception as error:
                result = {'exception': str(error)}
            body = json.dumps(result).encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    server.calls = 0
    wake, stop = socket.socketpair()
    def serve():
        with selectors.DefaultSelector() as ready:
            ready.register(server, selectors.EVENT_READ)
            ready.register(wake, selectors.EVENT_READ)
            while True:
                events = ready.select()
                if any(key.fileobj is wake for key, _ in events):
                    return
                server._handle_request_noblock()
    thread = threading.Thread(target=serve, daemon=True)
    thread.start()
    try:
        yield f'http://127.0.0.1:{server.server_port}/'
    finally:
        stop.sendall(b'stop')
        thread.join()
        server.server_close()
        wake.close()
        stop.close()


def example_library(args, rpc):
    # Program.cs example-library-only, including both pre-dispatch JSON parser
    # rejections on the kept parser and all 13 language codes.
    print(shape(args, 'parser-self-test'), end='')
    passed = 2

    def check(ok, message):
        nonlocal passed
        resources.require(ok, message)
        passed += 1

    def usage(**arguments):
        inputs = dict(toolName='', query='', language='', exampleId='', exampleKind='all', limit=80)
        inputs.update(arguments)
        return call_tool(rpc, 'GetToolUsage', inputs)

    def selected(value):
        return value['schemaVersion'] == 4 and value['meta']['releaseKey'] == args.release and 'data' in value

    index = usage()
    check(selected(index), 'Usage index release envelope')
    check(index['data']['toolCount'] > 0 and index['data']['tools'], 'Usage index registered tools')
    for language in ('scl', 'scl-sd', 'lad', 'fbd', 'mixed', 'db', 'udt', 's7res', 'stl', 'graph', 'hmi-javascript', 'hmi-vbscript', 'csharp'):
        value = usage(language=language)
        check(selected(value) and (value['data'].get('examples') or 'sourceDocuments' in value['data']), 'Language ' + language)
    value = usage(toolName='CreateDevice')
    check(selected(value), 'Per-tool release')
    example = value['data']['example']
    check(example['kind'] == 'parameterized-call-example' and example['request']['params']['arguments'], 'Typed example arguments')
    value = usage(language='scl', exampleKind='language')
    check(selected(value) and any(row['releaseMatches'] and row['profileMatches'] for row in value['data']['examples']), 'SCL release/profile')
    value = usage(exampleId='scl-add', exampleKind='language')
    row = value['data']['examples'][0]
    check(selected(value) and row['releaseMatches'] and row['profileMatches'] and row['files'], 'Complete SCL source')
    value = usage(exampleId='sequence/plc-scl-block', exampleKind='sequence')
    row = value['data']['examples'][0]
    check(selected(value) and row['releaseMatches'] and row['available'] and row['steps'], 'Registered PLC sequence')
    value = usage(query='openness-base')
    check(selected(value) and any(row['id'] == 'guides/skills/openness-base/SKILL.md' for row in value['data']['matches']), 'Official document search')
    print(f'COMPLETE: {passed} GetToolUsage index/example/language/sequence/reference checks; no native calls')


def native_reader(args, directory):
    old = {'text': '中文 😀 <>&\'`+ "quote" \n\x00', 'null': None, 'number': -2146233079}
    prior = directory / 'calls-fixture.jsonl.previous'
    prior.write_text(json.dumps(dict(nativeCallId='pair', phase='BEFORE', binding=old), ensure_ascii=False) + '\n', encoding='utf-8')
    os.utime(prior, (1, 1))
    encoded = json.dumps(dict(nativeCallId='pair', phase='RETURNED', binding=old))
    for text, escape in (('<', '\\u003C'), ('>', '\\u003E'), ('&', '\\u0026'), ("'", '\\u0027'), ('`', '\\u0060'), ('+', '\\u002B')):
        encoded = encoded.replace(text, escape)
    (directory / 'calls-fixture.jsonl').write_text(encoded + '\n{partial', encoding='utf-8')
    (directory / 'calls-fixture.jsonl.unrelated').write_text('{"phase":"unrelated"}\n', encoding='utf-8')
    def read(take):
        # The old harness used the one-shot CLI's unwrapped AllTools view. Keep
        # that subject so the reader doesn't ingest its own transport projection.
        command = resources.host_command(args.host, None, args.release, 'stdio', 'full', args.api,
                                         plc_worker=args.worker, bundle_root=ROOT) + ['--host-command', 'CallTool']
        value = json.loads(run(command, input=json.dumps({'name': 'GetNativeInvocationLog', 'arguments': {'take': take}}),
                               env={**os.environ, 'TIA_MCP_DIAGNOSTICS_DIRECTORY': str(directory)}))
        resources.require(value['ok'], 'Native journal read failed')
        return value['data']['evidence']
    meta = read(100)
    rows = meta['records']
    resources.require(len(rows) == 2 and [row['phase'] for row in rows] == ['BEFORE', 'RETURNED']
                      and all(row['nativeCallId'] == 'pair' for row in rows) and rows[0]['binding'] == rows[1]['binding']
                      and meta['filesRead'] == 2, 'Rotated native pair was not read')
    resources.require(meta['malformedLines'] == 1 and 'unrelated' not in json.dumps(meta), 'Native reader hid truncation or read unrelated files')
    rows = read(1)['records']
    resources.require(len(rows) == 1 and rows[0]['phase'] == 'RETURNED' and rows[0]['nativeCallId'] == 'pair', 'Bounded native reader window')
    print('COMPLETE: 3 native journal reader checks passed; no native call executed')


def owns_evidence(path):
    path = path.resolve()
    if path.is_relative_to(ROOT):
        return True
    # -Clean runs this script and every executable in a detached source copy,
    # but deliberately retains the evidence in its originating worktree.
    clean = os.environ.get('TIA_MCP_CLEAN_CHECKOUT')
    original = os.environ.get('TIA_MCP_CHECK_ORIGINAL_ROOT')
    if not clean or not original or Path(clean).resolve() != ROOT:
        return False
    original = Path(original).resolve()
    if ROOT.parent != original / 'bin-build/c' or not re.fullmatch('[0-9a-f]{8}', ROOT.name):
        return False
    marker = ROOT / 'bin-build/check-all-snapshot.owner.json'
    if not marker.resolve().is_relative_to(ROOT):
        return False
    try:
        owner = json.loads(marker.read_text(encoding='utf-8'))
        return (Path(owner['original']).resolve() == original and (original / 'AGENTS.md').is_file()
                and path.is_relative_to(original) and not path.is_relative_to(original / 'bin-build/c'))
    except (OSError, ValueError, KeyError, TypeError):
        return False


def execute(args):
    resources.require(args.mode in MODES, 'No PLC successor for mode: ' + args.mode)
    resources.require(all(path.resolve().is_relative_to(ROOT) for path in (args.host, args.worker, args.harness)) and owns_evidence(args.output),
                      'Host, worker, harness and evidence must belong to this worktree')
    args.output.mkdir(parents=True, exist_ok=False)
    diagnostics = args.output / 'diagnostics'
    diagnostics.mkdir()
    env = {'TIA_MCP_DIAGNOSTICS_DIRECTORY': str(diagnostics), 'TIA_MCP_DATA_DIRECTORY': str(args.output / 'data')}
    if args.mode == 'test-ecosystem-assembly':
        # The engine fixture ran its companion command through the in-process nested dispatch, without the
        # Workbench approval gate. Through the host's CallTool that run is a write; the gate itself is tested
        # by Test-ReleaseApprovalGate.py, so this host runs with approval disabled, as the SDK guard fixture does.
        (args.output / 'data/config').mkdir(parents=True)
        (args.output / 'data/config/approval.settings').write_text('enabled=false\ntimeoutSeconds=120\n', encoding='utf-8')
    if args.mode == 'http':
        transport = load('Test-FoundationTransport')
        inputs = argparse.Namespace(releases=[args.release], plc_sdk_worker=[f'{args.release}={args.worker}'],
                                   public_api_root=args.public_api_root, host_exe=args.host, output=args.output,
                                   stdio_only=False, fixture=None)
        counts = {}
        with fixture_directory('plc-harness-transport-') as scratch:
            transport.plc_transports(inputs, Path(scratch), counts)
        print(f'COMPLETE: {sum(counts.values()) * 3} Foundation transport checks passed; no native TIA')
    else:
        with resources.server(args.host, args.api, args.release, 'stdio', 'full', public_api=args.api,
                              plc_worker=args.worker, env_overrides=env, evidence_directory=args.output) as (rpc, _, _):
            initialized = rpc('initialize', params={'protocolVersion': '2024-11-05', 'capabilities': {},
                                                   'clientInfo': {'name': 'plc-harness', 'version': '1'}})
            resources.require('result' in initialized, 'Host initialize failed')
            rpc('notifications/initialized', notification=True)
            state = call_tool(rpc, 'GetSessionState', {})
            resources.require(state['ok'], 'SDK-ready PlcWorker did not dispatch its cached state read')
            tools = rpc('tools/list')['result']['tools']
            tools_path, metadata_path = args.output / 'tools.json', args.output / 'metadata.json'
            tools_path.write_text(json.dumps(tools), encoding='utf-8')
            descriptor_output = shape(args, 'metadata', args.release, tools_path, metadata_path, args.worker, args.api,
                                      env=dict(os.environ, **env))
            metadata = json.loads(metadata_path.read_text('utf-8'))
            if args.mode == 'descriptor-catalog-only':
                print(descriptor_output, end='')
            elif args.mode == 'check-modes-self-test':
                outputs = [run(['dotnet', 'run', '--file', ROOT / 'scripts/checks/Test-CampaignInputs.cs', '-c', 'Release',
                                '--no-build', '--no-restore', '--', '--self-test'], env=file_app_environment()),
                           shape(args, 'parser-self-test'), shape(args, 'coverage-self-test')]
                for output in outputs:
                    print(output, end='')
                # The total is the sum of the three programs' own reported counts, never a constant.
                counts = [re.search(pattern, output) for pattern, output in zip((
                    r'COMPLETE: (\d+) kept-validator campaign self-tests passed', r'COMPLETE: (\d+) pre-dispatch JSON rejection checks passed',
                    r'COMPLETE: (\d+) descriptor coverage self-tests passed'), outputs)]
                resources.require(all(counts), 'A check-mode self-test did not report its complete count')
                print(f'COMPLETE: {sum(int(match.group(1)) for match in counts)} check mode self-tests passed')
            elif args.mode == 'example-library-only':
                example_library(args, rpc)
            elif args.mode in ('concurrency-only', 'staging-sessions-only'):
                suites(args)
            else:
                with bridge(args, rpc, metadata) as endpoint:
                    child_env = dict(os.environ, **env, TIA_MCP_PLC_HARNESS_RPC=endpoint,
                                     TIA_MCP_PLC_HARNESS_INVENTORY=str(args.adapter_inventory or ''),
                                     TIA_MCP_PLC_HARNESS_HOST=str(args.host))
                    print(run([args.harness, '--plc', args.worker, args.mode, args.api, metadata_path, diagnostics, args.release], env=child_env), end='')
                if args.mode == 'native-diagnostics-only':
                    reader_directory = args.output / 'native-reader'
                    reader_directory.mkdir()
                    native_reader(args, reader_directory)
                    guard_data = args.output / 'sdk-guard-data'
                    (guard_data / 'config').mkdir(parents=True)
                    (guard_data / 'config/approval.settings').write_text('enabled=false\ntimeoutSeconds=120\n', encoding='utf-8')
                    print(shape(args, 'guards', args.release, tools_path, metadata_path, args.worker, args.api,
                                env={**os.environ, **env, 'TIA_MCP_DATA_DIRECTORY': str(guard_data)}), end='')
                    suites(args)
    evidence = dict(mode=args.mode, release=args.release, nativeTiaExecuted=False,
                    **resources.plc_fixture_evidence(args.worker, args.host))
    (args.output / 'result.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')


def suites(args):
    if getattr(args, 'engine_host_results', None) is not None:
        reference_engine_host(args)
        return
    tool = (getattr(args, 'release_tool', None) or ROOT / 'build-tools/release/bin/Release/net10.0/TiaMcp.ReleaseTool.dll').resolve()
    resources.require(tool.is_relative_to(ROOT) and tool.is_file(), 'Release tool must be built in this worktree: ' + str(tool))
    manifest = getattr(args, 'runtime_manifest', None)
    if manifest is not None:
        manifest = manifest.resolve()
        resources.require(owns_evidence(manifest) and manifest.is_file(), 'Runtime manifest must belong to this worktree: ' + str(manifest))
    print(run(['dotnet', tool, 'test-suites',
               '-Suite', 'engine-host', '-NoBuild', '-Shards', '1', '-MaxParallelism', '1',
               '-ResultsDirectory', args.output / 'engine-host', *(['-RuntimeManifest', manifest] if manifest else [])]), end='')


def reference_engine_host(args):
    """Accept only this run's complete, binary-bound registered gate."""
    def checked(path, digest=None):
        path = Path(path).resolve()
        resources.require(owns_evidence(path) and path.is_file(), 'Referenced engine-host result missing or foreign: ' + str(path))
        if digest is not None:
            resources.require(hashlib.sha256(path.read_bytes()).hexdigest() == digest, 'Referenced engine-host result stale: ' + str(path))
        return path
    reference = checked(args.engine_host_results)
    proof = json.loads(reference.read_text(encoding='utf-8'))
    run_root = args.output.resolve().parent
    resources.require(proof['schemaVersion'] == 1 and Path(proof['runRoot']).resolve() == run_root
                      and reference == run_root / 'suites/engine-host.reference.json', 'Referenced engine-host result belongs to another run')
    resources.require(getattr(args, 'runtime_manifest', None) is not None, 'Referenced engine-host result needs a runtime manifest')
    manifest = checked(args.runtime_manifest, proof['runtimeManifestSha256'])
    resources.require(manifest == Path(proof['runtimeManifest']).resolve(), 'Referenced engine-host runtime manifest differs')
    resources.require(bool(proof['binaries']), 'Referenced engine-host binaries missing')
    for binary in proof['binaries']:
        checked(binary['path'], binary['sha256'])
    summary_path = checked(proof['summary'], proof['summarySha256'])
    trx_path = checked(proof['trx'], proof['trxSha256'])
    resources.require(summary_path == run_root / 'suites/engine-host.json' and trx_path == run_root / 'suites/engine-host.trx',
                      'Referenced engine-host results belong to another run')
    summary = json.loads(summary_path.read_text(encoding='utf-8'))
    suite = json.loads((ROOT / 'tests/test-suites.json').read_text(encoding='utf-8'))['engine-host']
    platform = 'windows' if sys.platform == 'win32' else 'macos' if sys.platform == 'darwin' else 'linux'
    suite.update(suite.get('platforms', {}).get(platform, {}))
    resources.require(summary['suite'] == 'engine-host' and summary['errors'] == []
                      and summary['minimumPassed'] == suite['minimumPassed'] and summary['maximumSkipped'] == suite['maximumSkipped']
                      and summary['failed'] == 0 and summary['passed'] >= suite['minimumPassed']
                      and summary['skipped'] <= suite['maximumSkipped'], 'Referenced engine-host gate failed')
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    tree = ET.parse(trx_path)
    result = tree.find('t:ResultSummary', ns)
    counters = result.find('t:Counters', ns)
    rows = tree.findall('t:Results/t:UnitTestResult', ns)
    resources.require(result.get('outcome') in ('Completed', 'Passed')
                      and all(int(value) == 0 for key, value in counters.attrib.items() if key not in ('total', 'executed', 'passed', 'failed', 'notExecuted'))
                      and int(counters.get('failed')) == 0 and int(counters.get('passed')) == summary['passed']
                      and int(counters.get('executed')) == summary['passed']
                      and int(counters.get('total')) == summary['total'] == summary['passed'] + summary['skipped']
                      and int(counters.get('notExecuted')) in (0, summary['skipped'])
                      and len(rows) == summary['total'] and len({row.get('executionId') for row in rows}) == len(rows)
                      and sum(row.get('outcome') == 'Passed' for row in rows) == summary['passed']
                      and sum(row.get('outcome') == 'NotExecuted' for row in rows) == summary['skipped'], 'Referenced engine-host TRX failed')
    print(f"COMPLETE: {summary['passed']} engine-host checks passed (shared result: {trx_path})")
    (args.output / 'engine-host-reference.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')


def main():
    # The harness output is decoded with errors='replace'; a release run on a GBK console could not print the U+FFFD that
    # a non-UTF-8 byte becomes, and the mode failed after the harness had passed (beta.1 release run, 2026-10-21).
    sys.stdout.reconfigure(errors='replace')
    parser = argparse.ArgumentParser()
    parser.add_argument('--release', required=True, choices=('20', '21'))
    parser.add_argument('--mode', required=True, choices=MODES)
    for name in ('host', 'worker', 'harness', 'api', 'public-api-root', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--adapter-inventory', type=Path)
    parser.add_argument('--release-tool', type=Path)
    parser.add_argument('--runtime-manifest', type=Path)
    parser.add_argument('--engine-host-results', type=Path)
    args = parser.parse_args()
    for name in ('host', 'worker', 'harness', 'api', 'public_api_root', 'output'):
        setattr(args, name, getattr(args, name).resolve())
    execute(args)


if __name__ == '__main__':
    main()
