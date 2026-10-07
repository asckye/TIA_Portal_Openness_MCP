"""Offline dispatch and real STDIO/loopback HTTP measurements; never calls TIA."""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import socket
import subprocess
import threading
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[3]


class Stdio:
    def __init__(self, command, data):
        env = dict(os.environ, TIA_MCP_DATA_DIRECTORY=str(data.resolve()))
        data.mkdir(parents=True, exist_ok=True)
        self.errors = (data / 'stderr.log').open('w', encoding='utf-8')
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.errors, encoding='utf-8', cwd=ROOT, env=env)
        self.pending = {}
        self.lock = threading.Lock()
        self.counter = 0
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.process.stdout:
            try:
                row = json.loads(line)
                with self.lock:
                    future = self.pending.pop(row.get('id'), None)
                if future is not None:
                    future.set_result(row)
            except (ValueError, concurrent.futures.InvalidStateError):
                pass

    def call(self, method, params=None):
        future = concurrent.futures.Future()
        with self.lock:
            self.counter += 1
            row = dict(jsonrpc='2.0', id=self.counter, method=method)
            if params is not None:
                row['params'] = params
            self.pending[self.counter] = future
            self.process.stdin.write(json.dumps(row) + '\n')
            self.process.stdin.flush()
        return future.result(timeout=100)

    def notify(self, method):
        with self.lock:
            self.process.stdin.write(json.dumps(dict(jsonrpc='2.0', method=method, params={})) + '\n')
            self.process.stdin.flush()

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.errors.close()


INIT = dict(protocolVersion='2024-11-05', capabilities={}, clientInfo=dict(name='P6-58', version='1'))


def stats(values, seconds):
    values = sorted(values)
    return dict(p50Ms=values[len(values)//2], p95Ms=values[int(len(values)*.95)-1],
                maxMs=max(values), callsPerSecond=len(values)/seconds)


def measure(command, data, http=False, fake=False, baseline=False):
    port = 0
    if http:
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        if fake:
            command = command + ['http', str(port)]
        else:
            command = command + ['--transport', 'http', '--http-prefix', f'http://127.0.0.1:{port}/', '--http-api-key', 'p6-58-test-key']
    elif fake:
        command = command + ['stdio', '0']
    started = time.perf_counter()
    client = Stdio(command, data)
    try:
        sessions = [None] * 4
        counter = 0
        mutex = threading.Lock()

        def call(method, params=None, slot=0):
            nonlocal counter
            if not http:
                if method.startswith('notifications/'):
                    client.notify(method)
                    return {}
                return client.call(method, params)
            with mutex:
                counter += 1
                request_id = counter
            headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream',
                       'Authorization': 'Bearer p6-58-test-key', 'X-API-Key': 'p6-58-test-key'}
            if sessions[slot]:
                headers['Mcp-Session-Id'] = sessions[slot]
            payload = dict(jsonrpc='2.0', id=request_id, method=method)
            if method.startswith('notifications/'):
                del payload['id']
            if params is not None:
                payload['params'] = params
            req = urllib.request.Request(f'http://127.0.0.1:{port}/mcp', json.dumps(payload).encode(), headers)
            with urllib.request.urlopen(req, timeout=100) as response:
                sessions[slot] = response.headers.get('Mcp-Session-Id') or sessions[slot]
                text = response.read().decode('utf-8')
            if not text:
                return {}
            if text.startswith('event:') or text.startswith('data:'):
                text = next(line[5:].strip() for line in text.splitlines() if line.startswith('data:'))
            return json.loads(text)

        if http:
            deadline = time.monotonic() + 40
            while True:
                try:
                    with socket.create_connection(('127.0.0.1', port), timeout=.2):
                        break
                except OSError:
                    if client.process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('HTTP startup failed: ' + str(data / 'stderr.log'))
                    time.sleep(.02)
        for slot in range(4 if http else 1):
            assert 'result' in call('initialize', INIT, slot)
            call('notifications/initialized', {}, slot)
        startup = (time.perf_counter() - started) * 1000
        then = time.perf_counter()
        assert 'result' in call('tools/list', {})
        listing = (time.perf_counter() - then) * 1000
        local = dict(name='GetToolUsage', arguments={'toolName': 'GetSessionState'})
        assert 'result' in call('tools/call', local)
        with concurrent.futures.ThreadPoolExecutor(max_workers=9) as pool:
            held = pool.submit(call, 'tools/call', dict(name='GetSessionState', arguments={})) if fake else None
            if fake:
                time.sleep(.25)

            def timed(i):
                then = time.perf_counter()
                response = call('tools/call', local, i % 4)
                assert 'result' in response and not response['result'].get('isError', False), response
                return (time.perf_counter() - then) * 1000

            then = time.perf_counter()
            values = list(pool.map(timed, range(40)))
            metrics = stats(values, time.perf_counter() - then)
            if held:
                assert 'result' in held.result(timeout=100)
        if fake and not baseline:
            assert metrics['p95Ms'] < 100, metrics
        return dict(startupMs=startup, toolsListMs=listing, local=metrics, sessions=4 if http else 1,
                    concurrentClients=8, fakeSeconds=60 if fake else 0)
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--engine', type=Path, required=True)
    parser.add_argument('--foundation', type=Path, required=True)
    parser.add_argument('--harness', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--baseline', action='store_true')
    parser.add_argument('--stdio-only', action='store_true')
    parser.add_argument('--sequential', action='store_true', help='Measure startup without competing transport jobs')
    args = parser.parse_args()
    common = ['--bundle-root', str(ROOT)]
    engine = [str(args.engine.resolve())] + common + ['--no-isolate-openness', '--logging', '0']
    foundation = [str(args.foundation.resolve())] + common + ['--offline', '--release-key', '19']
    fixture = [str(args.harness.resolve()), str(args.engine.resolve()), 'performance-host']
    jobs = [('engine-stdio', engine, False, False), ('engine-http', engine, True, False),
            ('foundation-stdio', foundation, False, False), ('foundation-http', foundation, True, False),
            ('fake-stdio', fixture, False, True), ('fake-http', fixture, True, True)]
    result = {}
    if args.stdio_only:
        jobs = [job for job in jobs if not job[2]]
    with concurrent.futures.ThreadPoolExecutor(max_workers=1 if args.sequential else 6) as pool:
        futures = {pool.submit(measure, command, args.output.parent / (args.output.stem + '-' + name),
                               http, fake, args.baseline): name for name, command, http, fake in jobs}
        for future in concurrent.futures.as_completed(futures):
            name = futures[future]
            try:
                result[name] = future.result()
            except Exception as error:
                result[name] = dict(error=str(error), errorType=type(error).__name__)
            print(name, json.dumps(result[name]), flush=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    if any('error' in value for value in result.values()):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
