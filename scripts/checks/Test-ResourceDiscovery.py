"""Exercise resource discovery against a real EXE over STDIO and HTTP; never connect to TIA."""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import queue
import secrets
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
from tool_usage_checks import check_usage


def require(condition, message):
    if not condition:
        raise AssertionError(message)


@contextmanager
def server(exe, portal_root, major, transport, profile, harness=None, public_api=None,
           *, env_overrides=None, process_observer=None, isolate=False, evidence_directory=None):
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    endpoint = f'http://127.0.0.1:{port}/mcp'
    key = secrets.token_urlsafe(24)
    args = [str(exe), '--tia-major-version', str(major), '--tia-portal-location',
            str(portal_root), '--transport', transport, '--logging', '1']
    if transport == 'http':
        args += ['--http-prefix', f'http://127.0.0.1:{port}/', '--http-api-key', key]
    if isolate:
        args += ['--isolate-openness', '--worker-timeout-seconds', '10']
    if harness:
        require(public_api is not None, '--public-api is required with --host-harness')
        args = [str(harness), str(exe), 'protocol-host', str(public_api)] + args[1:]
    env = dict(os.environ, TIA_MCP_PROFILE=profile)
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

    def http(body, authorized=True):
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json'}
        if authorized:
            headers['Authorization'] = 'Bearer ' + key
        request = urllib.request.Request(endpoint, json.dumps(body).encode('utf-8'), headers)
        with opener.open(request, timeout=20) as response:
            raw = response.read()
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
                require(raw is not None, 'Host exited before returning a response: ' + ''.join(errors[-8:]))
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
                require(process.poll() is None, 'HTTP server exited at startup: ' + ''.join(errors[-8:]))
                try:
                    request = urllib.request.Request(endpoint + '/ready',
                                                     headers={'X-API-Key': key})
                    with opener.open(request, timeout=2) as response:
                        if json.load(response).get('mcpHostReady'):
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
    parser.add_argument('--host-harness', type=Path, help='Load the EXE host methods without changing local Openness group membership')
    parser.add_argument('--public-api', type=Path)
    parser.add_argument('--usage-output', type=Path)
    args = parser.parse_args()
    passed = 0
    usage_records = []
    for transport in ('stdio', 'http'):
        for profile in ('full', 'lite'):
            label = f'V{args.major} {transport} {profile}'
            with server(args.exe.resolve(), args.portal_root.resolve(), args.major,
                        transport, profile, args.host_harness, args.public_api) as (rpc, http, logs):
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
                    for number, request_id in enumerate((7, '资源发现', 7)):
                        response = rpc(method, request_id, include_params=number != 0)
                        require('error' not in response, f'{method}: {response.get("error")}')
                        require(response.get('result') == {field: []},
                                f'{method}: expected empty list, no continuation cursor')
                        passed += 1
                tools = rpc('tools/list')['result']['tools']
                require(any(tool['name'] == 'GetState' for tool in tools), 'Tools disappeared')
                # The caller must receive native-use guidance over the real protocol,
                # including lite clients that discover the parameter tool through FindTools.
                require('startdrive-bico' in initialized['result'].get('instructions', ''),
                        'BICO guidance missing from MCP initialize instructions')
                passed += 1

                def call_guide_tool(name, arguments):
                    reply = rpc('tools/call', params={'name': name, 'arguments': arguments})
                    require('result' in reply and not reply['result'].get('isError'), str(reply))
                    decoded = json.loads(reply['result']['content'][0]['text'])
                    if decoded.get('meta', {}).get('truncated'):
                        pieces = [decoded['message']]
                        page_meta = decoded['meta']
                        while not page_meta['eof']:
                            page = call_guide_tool('GetExport', {'exportId': page_meta['exportId'], 'offset': page_meta['nextOffset']})
                            pieces.append(page['message'])
                            page_meta = page['meta']
                        decoded = json.loads(''.join(pieces))
                    return decoded

                require('GetToolUsage' in initialized['result'].get('instructions', ''), 'Usage instructions missing')
                usage_report = check_usage(call_guide_tool, tools, args.major,
                    exhaustive=transport == 'stdio' and profile == 'full',
                    verify_documents=transport == 'stdio' and profile == 'full')
                usage_records.append(dict(usage_report, transport=transport, profile=profile))
                passed += 1
                guide = call_guide_tool('GetAuthoringGuide', {'topic': 'startdrive-bico'})
                guide_text = guide['message']
                require(guide['meta']['success'] and
                        f'/v{args.major}/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters' in guide_text and
                        'p2051[0]' in guide_text and 'dryRun' in guide_text,
                        'Official-version BICO guide or crash/read semantics missing')
                passed += 1
                found = call_guide_tool('FindTools', {'query': 'ManageStartdriveParameter', 'limit': 1})
                found_text = json.dumps(found)
                require('ManageStartdriveParameter' in found_text and 'startdrive-bico' in found_text and
                        'p1070[0]' in found_text, 'FindTools omitted the guide or curated call example')
                passed += 1
                planned_args = {'devicePathJson': '["fixture-drive"]', 'itemPathJson': '["fixture-cu"]',
                                'driveObjectNumber': 0, 'driveObjectIndex': 0,
                                'parameter': 'p2051[0]', 'action': 'read', 'dryRun': True}
                preflight = call_guide_tool('PreflightToolCall', {'name': 'ManageStartdriveParameter',
                                                               'argumentsJson': planned_args})
                detail = preflight['meta']
                require(detail['ok'] and detail['nativeReadsPossible'] and
                        detail['authoringGuideTopic'] == 'startdrive-bico' and
                        any('p2051[0]' in note and 'dryRun=true' in note for note in detail['precautions']),
                        'Preflight hides native reads or the known crash report')
                passed += 1
                recipe = call_guide_tool('GetRecipe', {'topic': 'startdrive-bico-read'})
                require(recipe['meta']['success'] and 'PreflightToolCall' in json.dumps(recipe) and
                        'NOT RUN' in json.dumps(recipe), 'BICO recipe omitted preflight or native acceptance limits')
                passed += 1
                bridged = call_guide_tool('CallTool', {'name': 'GetAuthoringGuide',
                                                     'argumentsJson': {'topic': 'startdrive-bico'}})
                # CallTool preserves the legacy nested ResponseMessage envelope.
                require(json.loads(bridged['message'])['Message'] == guide_text,
                        'CallTool did not deliver the same embedded BICO guide')
                passed += 1
                state = rpc('tools/call', params={'name': 'GetState', 'arguments': {}})
                require('result' in state and not state['result'].get('isError') and
                        state['result'].get('content'), 'GetState failed after resource discovery')
                passed += 1
                unknown = rpc('unknown/resource-discovery-test')
                if profile == 'full':
                    planned = rpc('tools/call', params={'name': 'PlanArtifactImportOrder', 'arguments': {'artifactsJson': '[{"Id":"FB","Dependencies":["UDT"]},{"Id":"UDT"}]'}})
                    plan = json.loads(planned['result']['content'][0]['text'])['meta']['plan']
                    require(plan['Valid'] and plan['Order'] == ['UDT', 'FB'], 'Shared dependency planner failed through real MCP transport')
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
            log_text = ''.join(logs)
            require(not any(method in line and ('failed' in line or 'not available' in line or
                        'no handler' in line) for line in log_text.splitlines()
                        for method in ('resources/list', 'resources/templates/list')),
                    'Resource discovery still produced unavailable-handler warnings')
            passed += 1
            print('PASS ' + label + ': resources, templates, capabilities, repeated IDs, tools and logs')
    if args.usage_output:
        args.usage_output.write_text(json.dumps(usage_records, indent=2) + '\n', encoding='utf-8')
    print(f'COMPLETE: {passed} resource discovery checks passed; no TIA connection attempted')


if __name__ == '__main__':
    main()
