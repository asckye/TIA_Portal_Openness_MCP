"""Exercise real MCP STDIO/HTTP hosts against a separate synthetic worker. No TIA calls."""
import argparse
from contextlib import nullcontext
from tool_usage_checks import check_usage
import json
import os
from pathlib import Path
import queue
import re
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
KEYS = ['14sp1', '15.1', '16', '17', '18', '19']
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
    return json.loads(result['content'][0]['text'])


def exercise(client, key, logfile, expected_before):
    initialized = client.call('initialize', INIT)
    assert 'GetToolUsage' in initialized.get('instructions', '')
    tools = client.call('tools/list', {})['tools']
    names = {t['name'] for t in tools}
    USAGE_REPORTS.append(check_usage(lambda name, args: tool(client, name, args), tools, key))
    assert {'Connect', 'Disconnect', 'GetProjectTree', 'PlanArtifactImportOrder'} <= names
    assert ('GetPlcWatchTables' in names) == (key != '14sp1')
    assert ('SearchHardwareCatalog' in names) == (key == '19')
    assert 'ExportAsDocuments' not in names
    tool(client, 'Bootstrap')
    plan = tool(client, 'PlanArtifactImportOrder', {'artifactsJson': json.dumps([{'Id': 'FB_生产', 'Dependencies': ['UDT_数据']}, {'Id': 'UDT_数据'}])})
    assert plan['Valid'] and plan['Order'] == ['UDT_数据', 'FB_生产'], plan
    cycle = tool(client, 'PlanArtifactImportOrder', {'artifactsJson': '[{"Id":"A","Dependencies":["A"]}]'})
    assert not cycle['Valid'] and not cycle['Order']
    assert len(logfile.read_text('utf-8').splitlines()) == expected_before if logfile.exists() else expected_before == 0
    attached = tool(client, 'Connect', {'processId': 31415})
    assert '31415' in json.dumps(attached)
    assert '生产线' in json.dumps(tool(client, 'GetProjectTree'), ensure_ascii=False)
    assert tool(client, 'Disconnect')['WorkerAcknowledged'] is True
    client.close()
    return len(names)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--temp-root', type=Path, help='New worktree directory for retained fixture logs; avoids restricted system TEMP directories')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    counts = {}
    if args.temp_root:
        args.temp_root.mkdir(parents=True, exist_ok=False)
    with (nullcontext(args.temp_root.resolve()) if args.temp_root else tempfile.TemporaryDirectory(prefix='tia-foundation-wire-')) as temp:
        temp = Path(temp)
        for key in KEYS:
            logfile = temp / f'{key}.jsonl'
            env = dict(os.environ, TIA_FIXTURE_LOG=str(logfile))
            command = [str(ROOT / f'runtime/v{key}/TiaMcpServer.exe'), '--worker-exe', str(args.fixture.resolve()), '--public-api', str(temp)]
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
        with socket.socket() as port:
            port.bind(('127.0.0.1', 0)); number = port.getsockname()[1]
        url = f'http://127.0.0.1:{number}'
        logfile = temp / 'http.jsonl'
        env = dict(os.environ, TIA_FIXTURE_LOG=str(logfile))
        command = [str(ROOT / 'runtime/v19/TiaMcpServer.exe'), '--worker-exe', str(args.fixture.resolve()), '--public-api', str(temp), '--transport', 'http', '--http-prefix', url + '/', '--http-api-key', 'fixture-key']
        with (args.output / 'http.log').open('w', encoding='utf-8') as stderr:
            p = subprocess.Popen(command, env=env, stderr=stderr, stdout=stderr)
            try:
                first, second = Http(url), Http(url)
                deadline = time.monotonic() + 15
                while True:
                    try:
                        with first.request('/mcp/health', auth=False) as response:
                            assert json.load(response)['releaseKey'] == '19'
                        break
                    except urllib.error.URLError:
                        if time.monotonic() >= deadline: raise
                        time.sleep(.1)
                try:
                    first.request('/mcp/ready', auth=False)
                    raise AssertionError('Unauthenticated readiness accepted')
                except urllib.error.HTTPError as error:
                    assert error.code == 401
                with first.request('/mcp/ready') as response:
                    assert json.load(response)['mcpHostReady']
                exercise(first, '19', logfile, 0)
                # Wait for session deletion to close the first synthetic child.
                deadline = time.monotonic() + 10
                while '"exit"' not in logfile.read_text('utf-8'):
                    assert time.monotonic() < deadline
                    time.sleep(.05)
                exercise(second, '19', logfile, len(logfile.read_text('utf-8').splitlines()))
                assert first.sid and second.sid and first.sid != second.sid
                starts = [json.loads(line) for line in logfile.read_text('utf-8').splitlines() if '"start"' in line]
                assert len(starts) == 2 and starts[0]['pid'] != starts[1]['pid']
                assert starts[0]['args'][3] != starts[1]['args'][3]
            finally:
                p.terminate(); p.wait(10)
    (args.output / 'tool-usage.json').write_text(json.dumps(USAGE_REPORTS, indent=2) + '\n', encoding='utf-8')
    (args.output / 'result.json').write_text(json.dumps({'stdioToolCounts': counts, 'httpSessions': 2, 'unicodeRoundTrip': True, 'workerProtocol': 2, 'workerArguments': 'exact release keys and distinct launch nonces', 'nativeTiaExecuted': False}, indent=2), 'utf-8')
    print('PASS: six STDIO releases, two isolated HTTP sessions, dependency planning and Chinese worker roundtrip; native TIA NOT RUN')


if __name__ == '__main__':
    main()
