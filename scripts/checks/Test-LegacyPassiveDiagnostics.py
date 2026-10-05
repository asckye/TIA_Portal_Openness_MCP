#!/usr/bin/env python3
"""Exercise real host MCP stdio only; never launch a worker/TIA. Requires built net10 host."""
import argparse, json, os, pathlib, queue, subprocess, tempfile, threading, sys
from contextlib import nullcontext
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from mcp_results import envelope

p = argparse.ArgumentParser()
p.add_argument('--dotnet', required=True)
p.add_argument('--host', required=True)
p.add_argument('--temp-root', type=pathlib.Path)
a = p.parse_args()
checks = 0
if a.temp_root: a.temp_root.mkdir(parents=True, exist_ok=False)
with (nullcontext(str(a.temp_root.resolve())) if a.temp_root else tempfile.TemporaryDirectory(prefix='passive-host-')) as directory:
    root = pathlib.Path(directory)
    marker = root / 'FORBIDDEN_WORKER_LAUNCH'
    worker = root / 'worker-sentinel'
    worker.write_text('#!/bin/sh\ntouch "' + str(marker) + '"\nexit 77\n')
    worker.chmod(0o700)
    for release in ['14sp1', '15.1', '16', '17', '18', '19', '20', '21']:
        for enabled in [False, True]:
            cmd = [a.dotnet, a.host, '--release-key', release, '--worker-exe', str(worker), '--public-api', directory]
            if not enabled:
                cmd.append('--offline')
            env = dict(os.environ, DOTNET_GENERATE_ASPNET_CERTIFICATE='false')
            with tempfile.TemporaryFile(mode='w+') as errors:
                proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors, text=True, env=env)
                lines = queue.Queue()
                def pump():
                    for line in proc.stdout: lines.put(line)
                    lines.put(None)
                threading.Thread(target=pump, daemon=True).start()
                sequence = 0
                def rpc(method, params):
                    global sequence, checks
                    sequence += 1
                    proc.stdin.write(json.dumps(dict(jsonrpc='2.0', id=sequence, method=method, params=params)) + '\n')
                    proc.stdin.flush()
                    line = lines.get(timeout=15)
                    assert line is not None, 'host exited'
                    response = json.loads(line)
                    assert response.get('id') == sequence, response
                    checks += 1
                    return response
                try:
                    assert 'result' in rpc('initialize', dict(protocolVersion='2025-03-26', capabilities={}, clientInfo=dict(name='passive-host-test', version='1')))
                    proc.stdin.write(json.dumps(dict(jsonrpc='2.0', method='notifications/initialized')) + '\n'); proc.stdin.flush()
                    roster = rpc('tools/list', {})['result']['tools']
                    for name in ['InitializeEnvironment', 'RunCapabilitySelfTest']:
                        reply = rpc('tools/call', dict(name=name, arguments={}))['result']
                        assert not reply.get('isError', False), reply
                        value = envelope(reply)
                        assert value['ok'], value
                        result = value['data']
                        assert result['selectedRelease']['key'] == release
                        assert result['registeredToolCount'] == len(roster)
                        assert {x['name'] for x in result['registeredTools']} == {x['name'] for x in roster}
                        assert result['checks']['passed'] and not result['upstreamResponseCompatible'] and not result['nativeCertified']
                        assert result['host']['nativeCallsDisabledByConfiguration'] == (not enabled)
                        assert set(result['probes'].values()) == {'not-probed'}
                        assert not any(result['sideEffects'].values())
                        assert len(json.dumps(result)) < 32768 and directory not in json.dumps(result)
                        checks += 8
                    for args in [dict(connectIfNeeded=True), dict(includeProjectTree=True), dict(inspectPortalProcesses=True), dict(selfConnect=True), dict(expectedPlcSoftwarePath='SECRET_CANARY')]:
                        reply = rpc('tools/call', dict(name='RunCapabilitySelfTest', arguments=args))
                        assert reply.get('error', {}).get('code') == -32602 or reply.get('result', {}).get('isError') is True, reply
                        assert 'SECRET_CANARY' not in json.dumps(reply)
                    assert not marker.exists(), 'worker was launched'
                finally:
                    proc.stdin.close()
                    try: proc.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        proc.terminate(); proc.wait(timeout=5)
                        raise AssertionError('host did not stop on EOF')
                    assert proc.returncode == 0, 'host failed'
print(f'{checks} stdio checks passed across 8 exact release keys and both native-session configurations. Host only; no worker launched.')
