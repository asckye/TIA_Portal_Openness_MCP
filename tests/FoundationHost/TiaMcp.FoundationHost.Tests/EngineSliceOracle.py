"""Compare opt-in host + real woven engine worker with frozen V21 slice records.

Requires an explicitly marked TiaMcpEngineWorkerSdkFixture engine build. Only stdio
and an SDK-only installation are used; no TIA, PLC, VM or HTTP service is started.
"""
import argparse
from contextlib import contextmanager
import importlib.util
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'scripts'))
sys.path.insert(0, str(ROOT / 'scripts/checks'))


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts/checks' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@contextmanager
def host(command, env, diagnostics_path):
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
    replies, errors = queue.Queue(), []

    def output():
        for line in process.stdout:
            if line.strip(): replies.put(json.loads(line))
        replies.put(None)

    def diagnostics():
        for line in process.stderr: errors.append(line)

    for target in (output, diagnostics): threading.Thread(target=target, daemon=True).start()
    sequence = 0

    def rpc(method, params=None, notification=False):
        nonlocal sequence
        sequence += 1
        frame = {'jsonrpc': '2.0', 'method': method, 'params': params or {}}
        if not notification: frame['id'] = sequence
        process.stdin.write(json.dumps(frame, ensure_ascii=False) + '\n'); process.stdin.flush()
        if notification: return None
        while True:
            reply = replies.get(timeout=30)
            if reply is None: raise AssertionError('Host exited: ' + ''.join(errors))
            if reply.get('id') == sequence: return reply

    try: yield rpc
    finally:
        process.stdin.close()
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        diagnostics_path.write_text(''.join(errors), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.worker = args.worker.resolve()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    snapshots = load('Snapshot-ToolResponses')
    baseline = json.loads((ROOT / 'manifest/contracts/v4/baseline/21.json').read_text(encoding='utf-8'))
    responses = json.loads((ROOT / 'manifest/contracts/v4/responses/21.json').read_text(encoding='utf-8'))
    names = {'GetSessionState', 'SaveProject', 'ManagePlcTagDefinition', 'GetExportContent', 'ListExportHandles', 'BuildPlcUdt'}
    portal = snapshots.resources.sdk_only_installation(args.public_api, 21, args.output)
    env = dict(os.environ, TIA_MCP_BUNDLE_ROOT=str(ROOT), TIA_MCP_ENGINE_WORKER_SDK_READY='1',
               TIA_MCP_DATA_DIRECTORY=str(args.output / 'data'), TIA_MCP_MAX_RESPONSE_CHARS='2000000',
               TEMP=str(args.output), TMP=str(args.output))
    catalog_path = args.output / 'catalog.json'
    subprocess.run([str(args.worker), '--bundle-root', str(ROOT), '--write-tool-catalog', str(catalog_path)], env=env, check=True)
    catalog = json.loads(catalog_path.read_text(encoding='utf-8'))
    by_name = {t['name']: t for t in baseline['tools']}
    tools = snapshots.contracts.tool_records(catalog['tools'])
    for tool in tools:
        if tool['name'] in names: assert tool == by_name[tool['name']], 'Catalog mismatch: ' + tool['name']
    command = ['dotnet', str(ROOT / 'src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.dll'),
               '--bundle-root', str(ROOT), '--release-key', '21', '--profile', 'full',
               '--engine-worker', str(args.worker), '--engine-catalog', str(catalog_path), '--tia-portal-location', str(portal)]
    actual, failures = [], []
    with host(command, env, args.output / 'host.log') as rpc:
        reply = rpc('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'engine-slice-oracle', 'version': '1'}})
        assert 'result' in reply
        rpc('notifications/initialized', notification=True)
        published = rpc('tools/list')['result']['tools']
        assert {t['name'] for t in published} == names
        assert snapshots.contracts.tool_records(published) == [t for t in baseline['tools'] if t['name'] in names]
        entries = {}
        # Reuse the production snapshot decoder, exact lexical masks and hashes.
        call = snapshots.recorder(rpc, entries, 'full', '21')
        for expected in responses['calls']:
            if expected['tool'] not in names or expected['profile'] != 'full': continue
            try: call(expected['tool'], expected['arguments'])
            except Exception as error: raise AssertionError(f"{expected['tool']} {expected['arguments']}: {error}") from error
        for key in sorted(entries):
            result = snapshots.compact(entries[key], False)
            expected = next(c for c in responses['calls'] if c['tool'] == result['tool'] and c['arguments'] == result['arguments'] and c['profile'] == result['profile'])
            if result != expected: failures.append({'tool': result['tool'], 'arguments': result['arguments'], 'actual': result, 'expected': expected})
            actual.append(result)
    (args.output / 'result.json').write_text(json.dumps({'contractsPassed': len(names), 'responsesPassed': len(actual) - len(failures), 'responsesFailed': len(failures), 'calls': actual, 'failures': failures}, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f"COMPLETE: {len(names)} contracts, {len(actual)-len(failures)} response/hash records passed, {len(failures)} failed")
    return int(bool(failures))


if __name__ == '__main__': raise SystemExit(main())
