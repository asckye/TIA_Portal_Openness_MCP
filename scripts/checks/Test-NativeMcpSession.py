"""Opt-in production MCP session lifecycle test. Default: plan only, no process launch.

Creates its own headless TIA and new scratch project, never attaches to an existing
instance. Keep artifacts on success/failure. No GUI automation or PLC/HMI download.
"""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mcp_results import envelope, successful

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import uuid


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def parse(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--run-live', action='store_true')
    parser.add_argument('--confirm-new-portal', action='store_true')
    args = parser.parse_args(argv)
    require(args.output.is_absolute() and not args.output.exists(), 'Output must be a new absolute directory')
    require(args.exe.is_absolute() and args.exe.is_file(), 'EXE must be an existing absolute file')
    require(args.run_live == args.confirm_new_portal, 'Live execution requires BOTH --run-live and --confirm-new-portal')
    return args


def payload(reply):
    try:
        return successful(reply)
    except ValueError as error:
        raise RuntimeError(str(error)) from error


def sequence(call, output, major):
    name = 'McpNative_' + uuid.uuid4().hex[:12]
    call('ConnectIsolatedPortal')
    call('CreateProject', directoryPath=str(output), projectName=name)
    state = call('GetSessionState')
    binding = state['data']['evidence']['binding']['identity']
    project = Path(binding['projectPath'])
    require(state['data']['project'] == name and project.resolve().is_relative_to(output.resolve()), 'Created project escaped owned scratch directory')
    require(project.suffix.lower() == f'.ap{major}' and binding['tiaMajorVersion'] == major, 'Native engine/project version mismatch')
    require(binding['processId'] > 0 and binding['processStartUtc'] and binding['generation'], 'Incomplete binding identity')
    call('SaveProject')
    call('CloseProject')
    require(call('GetSessionState')['data']['project'] == '-', 'Closed project remained bound')
    call('OpenProject', path=str(project))
    rebound = call('GetSessionState')['data']['evidence']['binding']['identity']
    require(rebound['projectPath'] == binding['projectPath'] and rebound['processId'] == binding['processId'], 'Reopen selected another TIA/project')
    require(rebound['generation'] != binding['generation'], 'Reopen reused old binding generation')
    call('CloseProject')
    call('DisconnectPortal')
    require(call('GetSessionState')['data']['isConnected'] is False, 'DisconnectPortal retained a live session')


def run(args):
    args.output.mkdir()
    journal = args.output / 'diagnostics'
    env = dict(os.environ, TIA_MCP_PROFILE='full', TIA_MCP_DIAGNOSTICS_DIRECTORY=str(journal))
    command = [str(args.exe), '--transport', 'stdio', '--tia-major-version', str(args.major), '--isolate-openness', '--worker-timeout-seconds', '180']
    replies = queue.Queue()
    report = {'status': 'FAILED', 'nativeExecuted': False, 'utc': datetime.now(timezone.utc).isoformat(),
              'scope': 'production MCP: owned headless portal create/save/close/reopen/identity/disconnect only; no PLC/HMI family acceptance', 'completedTools': []}
    with (args.output / 'host-stderr.log').open('w', encoding='utf-8') as errors:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors,
                                   text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        def read():
            for line in process.stdout:
                if line.strip():
                    replies.put(line)
            replies.put(None)
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        index = 0
        def rpc(method, params):
            nonlocal index
            index += 1
            body = {'jsonrpc': '2.0', 'id': index, 'method': method, 'params': params}
            process.stdin.write(json.dumps(body) + '\n')
            process.stdin.flush()
            raw = replies.get(timeout=195)
            require(raw is not None, 'MCP exited; inspect retained journals')
            response = json.loads(raw)
            require(response.get('id') == index, 'Unexpected protocol response; no retry')
            return response
        def call(tool, **arguments):
            report['nativeExecuted'] = True
            with (args.output / 'scenario.jsonl').open('a', encoding='utf-8') as log:
                log.write(json.dumps({'tool': tool, 'phase': 'BEFORE'}) + '\n')
                log.flush()
                os.fsync(log.fileno())
            value = payload(rpc('tools/call', {'name': tool, 'arguments': arguments}))
            report['completedTools'].append(tool)
            return value
        try:
            initialized = rpc('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'native-mcp-session', 'version': '1'}})
            require('result' in initialized, 'MCP initialization failed')
            process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
            process.stdin.flush()
            sequence(call, args.output, args.major)
            report['status'] = 'PASSED'
        except Exception as error:
            report['error'] = str(error)
            report['nativeOutcomeUnknown'] = True
        finally:
            # Never send cleanup, Save, or retry after an uncertain native failure.
            # Stop only our MCP process; its worker watchdog handles its own exit.
            process.stdin.close()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            reader.join(timeout=5)
            process.stdout.close()
            (args.output / 'native-mcp-result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['status'] == 'PASSED' else 1


def self_test():
    from offline_fixtures import fixture_directory
    with fixture_directory('native-mcp-selftest-') as root:
        output = Path(root) / 'new'
        common = ['--exe', str(Path(sys.executable).resolve()), '--major', '21', '--output', str(output)]
        require(not parse(common).run_live and not output.exists(), 'Plan changed filesystem')
        for flag in ('--run-live', '--confirm-new-portal'):
            try:
                parse(common + [flag])
            except RuntimeError:
                pass
            else:
                raise AssertionError('One flag enabled native execution')
        require(parse(common + ['--run-live', '--confirm-new-portal']).run_live, 'Explicit opt-in refused')
        good = {'schemaVersion': 4, 'ok': True, 'data': {}, 'error': None, 'meta': {}}
        require(payload({'result': {'structuredContent': good}})['ok'], 'Structured result parsing')
        for reply in ({'error': {}}, {'result': {'isError': True}}, {'result': {'isError': True, 'structuredContent': dict(good, ok=False, error={'code': 'OUTCOME_UNKNOWN'})}}):
            try:
                payload(reply)
            except RuntimeError:
                pass
            else:
                raise AssertionError('Failure accepted as successful native tool call')
    print('COMPLETE: 8 native MCP safety checks passed; live TIA tests NOT RUN')
    return 0


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        sys.exit(self_test())
    options = parse(sys.argv[1:])
    if not options.run_live:
        print(json.dumps({'status': 'NOT RUN', 'output': str(options.output), 'requires': '--run-live --confirm-new-portal',
                          'scope': 'Own headless TIA, new scratch project. No attachment to existing projects.'}, indent=2))
    else:
        sys.exit(run(options))
