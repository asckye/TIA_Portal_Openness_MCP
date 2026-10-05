"""Verify real Studio adapter hellos and unbound, read-only RPCs using local SDK copies."""
import argparse
import hashlib
import json
from pathlib import Path
import secrets
import subprocess
import threading
from queue import Queue, Empty


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def smoke(bridge, sdk_root, key, relative, shared_adapter_paths=False):
    nonce = secrets.token_hex(32)
    adapter = bridge.parent / 'adapters' / ('v' + key) / (
        f'TiaMcp.Adapter.{key}.dll' if shared_adapter_paths else 'TiaOpenness.Openness.dll')
    expected = dict(protocol=2, releaseKey=key, workerSha256=digest(bridge),
                    adapterSha256=digest(adapter), nonce=nonce, bindingEpoch=0, bound=False)
    child = subprocess.Popen([str(bridge), '--openness-version', key, '--nonce', nonce,
                              '--public-api', str(sdk_root / relative)], cwd=bridge.parent,
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             creationflags=subprocess.CREATE_NO_WINDOW)
    expected['pid'] = child.pid
    frames, diagnostics = Queue(), []
    threading.Thread(target=lambda: [frames.put(line) for line in child.stdout], daemon=True).start()
    threading.Thread(target=lambda: diagnostics.extend(child.stderr.readlines()), daemon=True).start()

    def receive():
        try:
            line = frames.get(timeout=15)
        except Empty:
            raise AssertionError('Bridge did not reply: ' + b''.join(diagnostics).decode('utf-8'))
        assert line.endswith(b'\n') and not line.startswith(b'\xef\xbb\xbf')
        return json.loads(line)

    checks = 0
    try:
        assert receive() == dict(jsonrpc='2.0', method='hello', params=expected)
        checks += 1
        for request_id, method in enumerate(('session.state', 'ping', 'doctor.run'), 1):
            request = dict(jsonrpc='2.0', id=request_id, method=method, params={}, bindingEpoch=0)
            child.stdin.write((json.dumps(request) + '\n').encode('utf-8'))
            child.stdin.flush()
            reply = receive()
            assert reply['jsonrpc'] == '2.0' and reply['id'] == request_id
            assert reply['bindingEpochBefore'] == reply['bindingEpochAfter'] == 0
            assert 'error' not in reply, reply
            result = reply['result']
            if method == 'session.state':
                assert result['Connected'] is False and result['Mode'] == 'Openness' and result['OpenProject'] is None
            elif method == 'ping':
                assert result == dict(pong=True, mode='Openness', decision='direct Openness V' + key)
            else:
                assert result['MachineName'] and any(c['Id'] == 'ENV-NETFX' for c in result['Checks'])
            checks += 1
        child.stdin.close()
        assert child.wait(timeout=5) == 0, diagnostics
        checks += 1
    finally:
        if child.poll() is None:
            child.kill()
            child.wait(timeout=5)
        child.stdout.close()
        child.stderr.close()
    print(f'PASS Studio {key}: {checks} checks; native session never created')
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bridge', type=Path, required=True)
    parser.add_argument('--public-api-root', type=Path, required=True)
    parser.add_argument('--shared-adapter-paths', action='store_true')
    args = parser.parse_args()
    count = sum(smoke(args.bridge.resolve(), args.public_api_root.resolve(), key, relative, args.shared_adapter_paths) for key, relative in (
        ('14sp1', 'TIA_V14SP1_PublicAPI/V14 SP1'),
        ('16', 'TIA_V16_PublicAPI/V16'),
        ('21', 'TIA_V21_PublicAPI/V21/net48')))
    print(f'Passed: {count}; Failed: 0')


if __name__ == '__main__':
    main()
