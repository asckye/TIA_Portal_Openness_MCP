"""Capture offline tool contracts and reject incompatible input schema changes.

capture starts each built runtime without TIA: V20/V21 through the release host harness over STDIO
(full and lite profiles), V14 SP1-V19 through `TiaMcp.FoundationHost.exe --catalog`. The foundation host needs
the ASP.NET Core 10 runtime; when the machine lacks it, point DOTNET_ROOT and DOTNET_ROOT_X64 at a
private runtime before running capture. compare exits 1 on any breaking change.
"""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from contextlib import contextmanager
import os
import queue
import secrets
import socket
import threading
import time
import urllib.request
import urllib.error


def engine_overrides(values, option):
    result = {}
    for value in values:
        release, separator, path = value.partition('=')
        if not separator or release not in ('20', '21') or not path or release in result:
            raise ValueError(option + ' must be unique RELEASE=PATH entries for 20/21')
        result[release] = Path(path).resolve()
    return result


@contextmanager
def engine_host_server(args, release, portal_root, profile, env_overrides):
    workers = engine_overrides(args.engine_worker, '--engine-worker')
    catalogs = engine_overrides(args.engine_catalog, '--engine-catalog')
    if (release in workers) != (release in catalogs):
        raise ValueError('EngineHost overrides require both worker and catalog for V' + release)
    transport = args.transport
    key = secrets.token_urlsafe(24)
    port = 0
    if transport == 'http':
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
    endpoint = f'http://127.0.0.1:{port}/mcp'
    command = [str(args.engine_host.resolve()), '--bundle-root', str(args.repo_root.resolve()),
               '--release-key', release, '--profile', profile, '--transport', transport]
    if release in workers:
        command += ['--engine-worker', str(workers[release]), '--engine-catalog', str(catalogs[release])]
    if portal_root is not None:
        command += ['--tia-portal-location', str(portal_root)]
    if transport == 'http':
        command += ['--http-prefix', f'http://127.0.0.1:{port}/', '--http-api-key', key]
    environment = dict(os.environ, **env_overrides)
    if release in workers:
        environment['TIA_MCP_ENGINE_WORKER_SDK_READY'] = '1'
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding='utf-8', env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
    output, errors = queue.Queue(), []
    def drain_stdout():
        for line in process.stdout:
            if line.strip():
                output.put(line)
        output.put(None)
    def drain_stderr():
        errors.extend(process.stderr)
    readers = [threading.Thread(target=drain_stdout, daemon=True), threading.Thread(target=drain_stderr, daemon=True)]
    for reader in readers:
        reader.start()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    session_id = None
    def http(body):
        nonlocal session_id
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream',
                   'Authorization': 'Bearer ' + key}
        if session_id:
            headers['Mcp-Session-Id'] = session_id
        request = urllib.request.Request(endpoint, json.dumps(body).encode('utf-8'), headers)
        with opener.open(request, timeout=25) as response:
            session_id = response.headers.get('Mcp-Session-Id', session_id)
            raw = response.read().decode('utf-8')
            if response.headers.get('Content-Type', '').startswith('text/event-stream'):
                raw = next(line[6:] for line in raw.splitlines() if line.startswith('data: '))
            return json.loads(raw) if raw else None
    def rpc(method, request_id=1, params=None, include_params=True, notification=False):
        message = {'jsonrpc': '2.0', 'method': method}
        if not notification:
            message['id'] = request_id
        if include_params:
            message['params'] = params or {}
        if transport == 'http':
            reply = http(message)
        else:
            process.stdin.write(json.dumps(message, ensure_ascii=False) + '\n')
            process.stdin.flush()
            if notification:
                return None
            while True:
                raw = output.get(timeout=25)
                if raw is None:
                    raise ValueError('EngineHost exited: ' + ''.join(errors))
                reply = json.loads(raw)
                if 'id' in reply:
                    break
        if not notification and (reply is None or reply.get('id') != request_id):
            raise ValueError('EngineHost response ID mismatch: ' + method)
        return reply
    try:
        if transport == 'http':
            deadline = time.monotonic() + 25
            while True:
                if process.poll() is not None:
                    raise ValueError('EngineHost exited: ' + ''.join(errors))
                try:
                    with opener.open(endpoint + '/health', timeout=2):
                        break
                except (urllib.error.URLError, TimeoutError):
                    if time.monotonic() >= deadline:
                        raise ValueError('EngineHost HTTP startup timed out')
                    time.sleep(.1)
        yield rpc, http, errors
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=3 if transport == 'stdio' else .2)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=5)
        for reader in readers:
            reader.join(timeout=5)
        process.stdout.close()
        process.stderr.close()


RELEASES = ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')
FORMAT_VERSION = 1
CONTRACT_FIELDS = {'formatVersion', 'release', 'profile', 'tools', 'behaviorCapabilities'}
ENGINE_CONTRACT_FIELDS = CONTRACT_FIELDS | {'liteTools'}
TOOL_FIELDS = {'name', 'inputSchema', 'outputSchema', 'descriptionSha256'}
CAPABILITY_FIELDS = {'family', 'state', 'l5', 'entries'}
FOUNDATION_UNAVAILABLE_BELOW_20 = {
    'ExportPlcBlockDocuments', 'ExportPlcBlocksDocuments',
    'ImportPlcBlockDocuments', 'ImportPlcBlocksDocuments',
}


def validate_contract_snapshot(snapshot, path):
    release = snapshot.get('release')
    expected_fields = ENGINE_CONTRACT_FIELDS if release in ('20', '21') else CONTRACT_FIELDS
    if set(snapshot) != expected_fields:
        raise ValueError(f'{path}: contract fields differ; missing={sorted(expected_fields - set(snapshot))}, '
                         f'unknown={sorted(set(snapshot) - expected_fields)}')
    if type(snapshot['formatVersion']) is not int or snapshot['formatVersion'] != FORMAT_VERSION:
        raise ValueError(f'{path}: expected contract formatVersion {FORMAT_VERSION}')
    expected_profile = 'full-engine' if release in ('20', '21') else 'plc-foundation'
    if snapshot['profile'] != expected_profile:
        raise ValueError(f'{path}: invalid contract profile')
    if not isinstance(snapshot['tools'], list) or not isinstance(snapshot['behaviorCapabilities'], list):
        raise ValueError(f'{path}: tools and behaviorCapabilities must be arrays')
    if [tool.get('name') for tool in snapshot['tools'] if isinstance(tool, dict)] != sorted(
            tool.get('name') for tool in snapshot['tools'] if isinstance(tool, dict)):
        raise ValueError(f'{path}: tools must be sorted by name')
    for tool in snapshot['tools']:
        if not isinstance(tool, dict) or set(tool) != TOOL_FIELDS:
            actual = set(tool) if isinstance(tool, dict) else set()
            raise ValueError(f'{path}: tool fields differ; missing={sorted(TOOL_FIELDS - actual)}, '
                             f'unknown={sorted(actual - TOOL_FIELDS)}')
        if not isinstance(tool['name'], str) or not tool['name']:
            raise ValueError(f'{path}: tool name must be nonempty text')
        if not isinstance(tool['inputSchema'], dict):
            raise ValueError(f'{path}: {tool["name"]} inputSchema must be an object')
        if tool['outputSchema'] is not None and not isinstance(tool['outputSchema'], dict):
            raise ValueError(f'{path}: {tool["name"]} outputSchema must be an object or null')
        if not isinstance(tool['descriptionSha256'], str) or not re.fullmatch(r'[0-9a-f]{64}', tool['descriptionSha256']):
            raise ValueError(f'{path}: {tool["name"]} descriptionSha256 must be lowercase SHA-256')
    for row in snapshot['behaviorCapabilities']:
        if not isinstance(row, dict) or set(row) != CAPABILITY_FIELDS:
            actual = set(row) if isinstance(row, dict) else set()
            raise ValueError(f'{path}: capability fields differ; missing={sorted(CAPABILITY_FIELDS - actual)}, '
                             f'unknown={sorted(actual - CAPABILITY_FIELDS)}')
        if (not isinstance(row['family'], str) or not isinstance(row['state'], str)
                or not isinstance(row['l5'], str) or not isinstance(row['entries'], list)
                or any(not isinstance(name, str) or not name for name in row['entries'])
                or len(row['entries']) != len(set(row['entries']))
                or row['entries'] != sorted(row['entries'])):
            raise ValueError(f'{path}: invalid behavior capability record')
    if 'liteTools' in snapshot and (not isinstance(snapshot['liteTools'], list)
                                    or any(not isinstance(name, str) or not name for name in snapshot['liteTools'])
                                    or snapshot['liteTools'] != sorted(snapshot['liteTools'])):
        raise ValueError(f'{path}: liteTools must be an array of nonempty tool names')

def tool_records(tools):
    names = [tool['name'] for tool in tools]
    if not names or len(names) != len(set(names)):
        raise ValueError('Tool catalog must be nonempty with unique names')
    return sorted(({'name': tool['name'], 'inputSchema': tool['inputSchema'], 'outputSchema': tool.get('outputSchema'),
                    'descriptionSha256': hashlib.sha256(
                        tool.get('description', '').encode('utf-8')).hexdigest()}
                   for tool in tools), key=lambda tool: tool['name'])


def capture(args):
    spec = importlib.util.spec_from_file_location('resource_discovery',
        Path(__file__).with_name('Test-ResourceDiscovery.py'))
    resources = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(resources)
    snapshots = {}
    root = args.repo_root.resolve()
    public_api_root = (args.public_api_root or (root / 'sdk' if (root / 'sdk').is_dir() else root)).resolve()
    executables = {}
    for override in args.exe:
        release, separator, path = override.partition('=')
        if not separator or release not in RELEASES or not path:
            raise ValueError('--exe must be RELEASE=PATH for a supported release')
        if release in executables:
            raise ValueError('Duplicate executable override for V' + release)
        executables[release] = Path(path).resolve()
    # The SDK-only installation copies the whole PublicAPI tree (schemas, parameter PDFs); a short
    # temporary root keeps it under MAX_PATH when the release candidate's output path is deep.
    fixture_root = Path(tempfile.mkdtemp(prefix='tc-sdk-'))
    if args.harness is None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
    requested_host = args.engine_host
    for release in args.releases:
        exe = executables.get(release, root / 'runtime' / ('v' + release) / 'TiaMcp.FoundationHost.exe')
        snapshot = {'formatVersion': FORMAT_VERSION, 'release': release}
        if release in ('20', '21'):
            args.engine_host = requested_host or exe
            public_api = public_api_root / ('TIA_V' + release + '_PublicAPI') / ('V' + release)
            if release == '21':
                public_api /= 'net48'
            portal_root = (resources.sdk_only_installation(public_api, int(release), fixture_root)
                           if args.harness is None or args.engine_host else public_api)
            rosters = {}
            for profile in ('full', 'lite'):
                server = (engine_host_server(args, release, portal_root, profile, {"TIA_MCP_MAX_RESPONSE_CHARS": "2000000"})
                          if args.engine_host else resources.server(exe, portal_root, int(release), 'stdio', profile,
                                      args.harness.resolve() if args.harness else None, public_api,
                                      env_overrides={"TIA_MCP_MAX_RESPONSE_CHARS": "2000000"}))
                with server as (rpc, _, logs):
                    reply = rpc('initialize', params={'protocolVersion': '2024-11-05',
                        'capabilities': {}, 'clientInfo': {'name': 'contract-snapshot', 'version': '1'}})
                    resources.require('result' in reply, f'Initialize failed: {reply}')
                    rpc('notifications/initialized', notification=True)
                    tools = []
                    cursor = None
                    seen = set()
                    while True:
                        reply = rpc('tools/list', params={} if cursor is None else {'cursor': cursor})
                        resources.require('result' in reply, f'Tools/list failed: {reply}')
                        tools.extend(reply['result']['tools'])
                        cursor = reply['result'].get('nextCursor')
                        if cursor is None:
                            break
                        resources.require(cursor not in seen, 'Repeated tools/list cursor')
                        seen.add(cursor)
                    rosters[profile] = tool_records(tools)
                    reply = rpc('tools/call', params={'name': 'GetToolUsage', 'arguments': {}})
                    result = reply['result']
                    body = result.get('structuredContent') or json.loads(result['content'][0]['text'])
                    table = body['data']['behaviorCapabilities']
                    if 'behaviorCapabilities' in snapshot and snapshot['behaviorCapabilities'] != table:
                        raise ValueError('Full/lite behavior capabilities disagree')
                    snapshot['behaviorCapabilities'] = table
                    disclosures = {tool['name']: tool.get('description', '') for tool in tools}
                    for row in table:
                        for entry in row['entries']:
                            if entry in disclosures and row['state'] == 'current':
                                text = disclosures[entry]
                                if 'behaviorPolicy=current' not in text or 'V4 native acceptance is pending' not in text:
                                    raise ValueError('Missing D1 description disclosure: ' + entry)
            snapshot.update(profile='full-engine', tools=rosters['full'],
                            liteTools=[tool['name'] for tool in rosters['lite']])
            if not set(snapshot['liteTools']) <= {tool['name'] for tool in snapshot['tools']}:
                raise ValueError('Lite roster contains tools absent from full profile')
        else:
            command = [str(exe), '--catalog']
            if release in executables:
                # Worktree output has no packaged release-key.txt marker.
                command.extend(['--release-key', release])
            result = subprocess.run(command, check=True, capture_output=True,
                                    text=True, encoding='utf-8', timeout=60)
            catalog = json.loads(result.stdout)
            snapshot.update(profile='plc-foundation', tools=tool_records(catalog['tools']),
                            behaviorCapabilities=catalog['behaviorCapabilities'])
            descriptions = {tool['name']: tool.get('description', '') for tool in catalog['tools']}
            for row in snapshot['behaviorCapabilities']:
                for entry in row['entries']:
                    if row['state'] == 'current' and ('behaviorPolicy=current' not in descriptions[entry]
                            or 'V4 native acceptance is pending' not in descriptions[entry]):
                        raise ValueError('Missing Foundation D1 disclosure: ' + entry)
        snapshots[release] = snapshot
        print(f'Captured V{release}: {len(snapshot["tools"])} tools', flush=True)
    # An HTTP host is terminated; its engine worker exits when its channel closes and can hold
    # the fixture SDK files for a moment longer.
    deadline = time.monotonic() + 30
    while True:
        try:
            shutil.rmtree(fixture_root)
            break
        except PermissionError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(.5)
    # Do not leave a partially captured baseline when a host fails.
    args.output.mkdir(parents=True, exist_ok=True)
    for release, snapshot in snapshots.items():
        (args.output / (release + '.json')).write_text(
            json.dumps(snapshot, ensure_ascii=False, sort_keys=True, indent=2) + '\n',
            encoding='utf-8', newline='\n')
    return 0


def schema_changes(old, new, path='$'):
    if old == new:
        return []
    if not isinstance(old, dict) or not isinstance(new, dict):
        return [('breaking', path + ': schema changed')]
    changes = []
    for key in sorted(old.keys() | new.keys()):
        a, b = old.get(key), new.get(key)
        if a == b and (key in old) == (key in new):
            continue
        location = path + '/' + key
        if key in ('properties', 'patternProperties', '$defs', 'definitions'):
            a, b = a or {}, b or {}
            for name in sorted(a.keys() | b.keys()):
                if name not in b:
                    changes.append(('breaking', location + '/' + name + ': removed'))
                elif name not in a:
                    kind = 'compatible' if key == 'properties' and name not in new.get('required', []) else 'breaking'
                    changes.append((kind, location + '/' + name + ': added'))
                else:
                    changes.extend(schema_changes(a[name], b[name], location + '/' + name))
        elif key == 'required':
            added, removed = set(b or []) - set(a or []), set(a or []) - set(b or [])
            if added:
                changes.append(('breaking', location + ': newly required ' + ', '.join(sorted(added))))
            if removed:
                changes.append(('compatible', location + ': no longer required ' + ', '.join(sorted(removed))))
        elif key == 'additionalProperties':
            a = old.get(key, True)
            b = new.get(key, True)
            if a == b:
                continue
            if b is True or a is False:
                changes.append(('compatible', location + ': relaxed'))
            elif isinstance(a, dict) and isinstance(b, dict):
                changes.extend(schema_changes(a, b, location))
            else:
                changes.append(('breaking', location + ': tightened'))
        elif key in ('description', 'title', '$comment', 'examples'):
            changes.append(('info', location + ': annotation changed'))
        elif key == 'default':
            # A different default changes behavior for every client that omits the argument.
            changes.append(('breaking', location + ': default changed'))
        elif key in ('minimum', 'exclusiveMinimum', 'minLength', 'minItems', 'minProperties',
                     'maximum', 'exclusiveMaximum', 'maxLength', 'maxItems', 'maxProperties'):
            relaxed = key not in new or (key in old and (
                b < a if key.startswith(('min', 'exclusiveMin')) else b > a))
            changes.append(('compatible' if relaxed else 'breaking', location + ': constraint changed'))
        elif key == 'uniqueItems':
            changes.append(('breaking' if b else 'compatible', location + ': constraint changed'))
        elif key not in new and key in ('pattern', 'format', 'multipleOf', 'const'):
            changes.append(('compatible', location + ': constraint removed'))
        else:
            # type, enum and items changes are breaking by the task's contract;
            # unknown/composite constraints are conservatively breaking as well.
            changes.append(('breaking', location + ': changed'))
    return changes


def load_snapshots(directory):
    snapshots = {}
    for path in sorted(directory.glob('*.json')):
        snapshot = json.loads(path.read_text(encoding='utf-8'))
        release = snapshot['release']
        if path.stem != release or release in snapshots:
            raise ValueError(f'Invalid release file: {path}')
        names = [tool['name'] for tool in snapshot['tools']]
        if not names or len(names) != len(set(names)):
            raise ValueError(f'Empty or duplicate tool roster: {path}')
        snapshots[release] = snapshot
    if not snapshots:
        raise ValueError(f'No contract snapshots in {directory}')
    return snapshots


def compare(args):
    if getattr(args, 'migration', None):
        return compare_text_migration(args) if args.migration == 'P6-26' else compare_migration(args)
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    total = Counter()
    releases = set(args.releases) if args.releases else baseline.keys() | current.keys()
    missing = releases - (baseline.keys() | current.keys())
    if missing:
        raise ValueError('Selected releases have no snapshots: ' + ', '.join(sorted(missing)))
    for release in sorted(releases):
        changes = []
        if release not in current or release not in baseline:
            changes.append(('breaking' if release not in current else 'compatible', 'release removed' if release not in current else 'release added'))
        else:
            old, new = baseline[release], current[release]
            if old['profile'] != new['profile']:
                changes.append(('breaking', 'engine profile changed'))
            if old.get('behaviorCapabilities') != new.get('behaviorCapabilities'):
                changes.append(('info' if 'behaviorCapabilities' not in old else 'breaking', 'behavior capability table changed'))
            a = {tool['name']: tool for tool in old['tools']}
            b = {tool['name']: tool for tool in new['tools']}
            for name in sorted(a.keys() | b.keys()):
                if name not in b:
                    changes.append(('breaking', name + ': tool removed'))
                elif name not in a:
                    changes.append(('compatible', name + ': tool added'))
                else:
                    changes.extend((kind, name + ' ' + detail) for kind, detail in
                                   schema_changes(a[name]['inputSchema'], b[name]['inputSchema']))
                    if 'outputSchema' not in a[name] and 'outputSchema' in b[name]:
                        changes.append(('info', name + ': outputSchema recorded'))
                    elif a[name].get('outputSchema') != b[name].get('outputSchema'):
                        changes.append(('breaking', name + ': outputSchema changed'))
                    if a[name]['descriptionSha256'] != b[name]['descriptionSha256']:
                        changes.append(('info', name + ': description hash changed'))
            for name in sorted(set(old.get('liteTools', [])) - set(new.get('liteTools', []))):
                changes.append(('breaking', name + ': lite entry removed'))
            for name in sorted(set(new.get('liteTools', [])) - set(old.get('liteTools', []))):
                changes.append(('compatible', name + ': lite entry added'))
        counts = Counter(kind for kind, _ in changes)
        total.update(counts)
        print(f'V{release}: breaking={counts["breaking"]} compatible={counts["compatible"]} info={counts["info"]}')
        for kind, detail in changes:
            print(f'  {kind}: {detail}')
    print(f'TOTAL: breaking={total["breaking"]} compatible={total["compatible"]} info={total["info"]}')
    return int(bool(total['breaking']))


def _recursive_refs_only(schema):
    """Exported schemas are inlined; only definitions on a reference cycle may stay as $ref (P6-07 rule 4)."""
    text = json.dumps(schema)
    if '"$ref"' not in text:
        return True
    definitions = schema.get('$defs', {}) if isinstance(schema, dict) else {}
    edges = {name: set(re.findall(r'"#/\$defs/([^"]+)"', json.dumps(body))) for name, body in definitions.items()}

    def on_cycle(start):
        seen, stack = set(), list(edges.get(start, ()))
        while stack:
            node = stack.pop()
            if node == start:
                return True
            if node not in seen:
                seen.add(node)
                stack.extend(edges.get(node, ()))
        return False
    return all(name in edges and on_cycle(name) for name in set(re.findall(r'"#/\$defs/([^"]+)"', text)))


def compare_text_migration(args):
    from snapshot_text_migration import differences, contract_text
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    failures = 0
    for release in args.releases or sorted(baseline.keys() | current.keys()):
        counts, problems = differences(baseline[release], current[release], contract_text)
        print(f'V{release} P6-26: changed-text={dict(sorted(counts.items()))}; unexpected={len(problems)}')
        for problem in problems: print('  unexpected: ' + problem)
        failures += len(problems)
    return int(failures != 0)


def compare_migration(args):
    """Phase-6 group proof: only the task's group may change; every other record stays byte-identical."""
    import xml.etree.ElementTree as ET
    import phase6_groups
    resource = Path(__file__).resolve().parents[2] / 'src/Logic/ModelContextProtocol/ToolProfiles.resx'
    runtime = json.loads(ET.parse(resource).find(".//data[@name='Catalog']/value").text)
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    failures = 0
    for release in args.releases or sorted(baseline.keys() & current.keys()):
        old, new = baseline[release], current[release]
        a, b = ({t['name']: t for t in snapshot['tools']} for snapshot in (old, new))
        members = phase6_groups.group(args.migration, release, a)
        expected = {phase6_groups.mapped(name, members) for name in a} | phase6_groups.additions(args.migration, release)
        if args.migration == 'P7-04' and release in ('20', '21'): expected -= phase6_groups.P7_04_REMOVED
        problems = []
        if set(b) != expected:
            problems.append('roster differs: ' + ', '.join(sorted(set(b) ^ expected)))
        untouched = [name for name in a if name not in members]
        subs = phase6_groups.rename_map(args.migration)
        changed_other = [name for name in untouched if name in b and a[name] != b[name]]
        # Guidance in other groups' tools may only change by this group's renames (Check-DeadToolReferences --fix).
        guidance = [name for name in changed_other if phase6_groups.renamed(a[name], subs) == b[name]]
        # Snapshots keep only a description hash; such changes are listed for the reviewer's source rename check.
        unhashed = lambda tool: {key: value for key, value in tool.items() if key != 'descriptionSha256'}
        described = [name for name in changed_other if name not in guidance
                     and unhashed(phase6_groups.renamed(a[name], subs)) == unhashed(b[name])]
        problems += [name + ': unmigrated contract changed' for name in changed_other if name not in guidance + described]
        if args.migration == 'P7-04b-followup':
            problems += [name + ': untouched record changed' for name in changed_other]
            if {k: v for k, v in old.items() if k != 'tools'} != {k: v for k, v in new.items() if k != 'tools'}:
                problems.append('snapshot metadata must stay unchanged')
            if release in ('20', '21'):
                try: _p7_04b_description_checks(a, b)
                except ValueError as error: problems.append(str(error))
        if args.migration == 'P7-04b':
            problems += [name + ': untouched record changed' for name in changed_other]
            for field in ('profile', 'liteTools'):
                if old.get(field) != new.get(field): problems.append(field + ': must stay unchanged')
            if release in ('20', '21'):
                fixture = Path(__file__).resolve().parents[2] / ('tests/FoundationHost/TiaMcp.EngineHost.Tests/Fixtures/EngineSource' + release + '.json')
                source = json.loads(fixture.read_text('utf-8'))
                original = {t['name']: t for t in source['tools']}
                original_families = {r['family']: set(r['entries']) for r in source['behaviorCapabilities']}
                expected_capabilities = [{**r, 'entries': sorted(set(r['entries']) | (original_families[r['family']] & phase6_groups.P7_04B_NAMES))}
                                         for r in old['behaviorCapabilities']]
                if new['behaviorCapabilities'] != expected_capabilities: problems.append('unreviewed capability change')
                problems += [name + ': pre-P7-04 record differs' for name in phase6_groups.P7_04B_NAMES
                             if b.get(name) != original.get(name)]
                if len(new.get('liteTools', [])) != 73: problems.append('lite must remain at 73 tools')
        if new.get('liteTools') is not None and release in runtime.get('releases', {}):
            expected_lite = {r['currentName'] for r in runtime['releases'][release] if 'lite' in r['profiles']}
            if set(new['liteTools']) != expected_lite or not expected_lite <= set(b):
                problems.append('lite differs from the generated runtime data')
        problems += [name + ': nonrecursive schema reference' for name, tool in b.items() if not _recursive_refs_only(tool['inputSchema'])]
        if args.migration == 'P6-07' and not problems:
            _p6_07_checks(b)
        changed = sum(a[name] != b.get(phase6_groups.mapped(name, members)) for name in members)
        print(f'V{release} {args.migration}: group={len(members)} changed={changed} untouched={len(untouched)} '
              f'(guidance renamed={len(guidance)}, description only={len(described)}); FAILED={len(problems)}')
        for name in guidance:
            print('  guidance renamed: ' + name)
        for name in described:
            print('  description only (check the source rename): ' + name)
        for problem in problems:
            print('  unexpected: ' + problem)
        failures += len(problems)
    return int(failures != 0)


def _p7_04b_description_checks(old, new):
    """Only the six parameters' fallback descriptions may become explicit annotations."""
    import copy
    from phase6_groups import P7_04B_DESCRIPTIONS
    for name, parameters in P7_04B_DESCRIPTIONS.items():
        if name not in old or name not in new:
            raise ValueError(name + ': missing restored tool')
        original = copy.deepcopy(old[name])
        annotated = copy.deepcopy(new[name])
        for parameter in parameters:
            before = original['inputSchema']['properties'][parameter]
            after = annotated['inputSchema']['properties'][parameter]
            description = after.pop('description', None)
            if not isinstance(description, str) or not description.strip() or description == before.pop('description', None):
                raise ValueError(name + '.' + parameter + ': expected an updated nonempty description')
        if annotated != original:
            raise ValueError(name + ': changes beyond the six parameter descriptions')


def _p6_07_checks(b):
    for name in ('CallTool', 'PreviewToolCall'):
        properties = b[name]['inputSchema']['properties']
        assert properties['arguments']['type'] == 'object' and 'argumentsJson' not in properties
    for name in ('RunReadOnlyToolBatch', 'PreviewToolBatch'):
        operations = b[name]['inputSchema']['properties']['operations']
        assert operations['type'] == 'array' and operations['minItems'] == 1 and operations['maxItems'] == 50
    assert b['GetToolUsage']['inputSchema']['properties']['exampleKind']['enum'] == ['all', 'sequence', 'language']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('self-test', help='Exercise capability/output/schema negative cases').set_defaults(run=self_test)
    capture_parser = commands.add_parser('capture')
    capture_parser.add_argument('--repo-root', type=Path, required=True)
    capture_parser.add_argument('--engine-host', type=Path, help='Capture 20/21 through FoundationHost')
    capture_parser.add_argument('--engine-worker', action='append', default=[], metavar='RELEASE=PATH', help='SDK fixture engine worker built with TiaMcpEngineWorkerSdkFixture=true')
    capture_parser.add_argument('--engine-catalog', action='append', default=[], metavar='RELEASE=PATH')
    capture_parser.add_argument('--transport', choices=('stdio', 'http'), default='stdio', help='EngineHost transport')
    capture_parser.add_argument('--harness', type=Path,
                                help='Optional test host harness; by default V20/V21 run as the real EXE')
    capture_parser.add_argument('--output', type=Path, required=True)
    capture_parser.add_argument('--releases', nargs='+', choices=RELEASES, default=RELEASES)
    capture_parser.add_argument('--exe', action='append', default=[], metavar='RELEASE=PATH',
                                help='Override a release executable; repeat for multiple releases')
    capture_parser.add_argument('--public-api-root', type=Path,
                                help='Root containing TIA_V20_PublicAPI and TIA_V21_PublicAPI (default: <repo>/sdk, else the repo root)')
    capture_parser.set_defaults(run=capture)
    verify_parser = commands.add_parser('verify', help='Check the static V4 release inventory and generated rosters')
    verify_parser.add_argument('--repo-root', type=Path, default=Path(__file__).resolve().parents[2])
    verify_parser.add_argument('--baseline', type=Path, help='Contract directory (default: <repo-root>/manifest/contracts/v4/baseline)')
    verify_parser.set_defaults(run=verify)
    compare_parser = commands.add_parser('compare')
    compare_parser.add_argument('--baseline', type=Path, required=True)
    compare_parser.add_argument('--current', type=Path, required=True)
    compare_parser.add_argument('--migration', choices=[*__import__('phase6_groups').TASKS, 'P6-26'], help='Verify one phase-6 group migration: only its tools may change')
    compare_parser.add_argument('--releases', nargs='+', choices=RELEASES,
                                help='Compare only these releases (default: compare all releases strictly)')
    compare_parser.set_defaults(run=compare)
    args = parser.parse_args()
    try:
        return args.run(args)
    except (OSError, ValueError, KeyError, TypeError, AssertionError, subprocess.SubprocessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1


def exact_release_files(directory):
    expected = {release + '.json' for release in RELEASES}
    actual = {path.name for path in directory.iterdir()}
    if actual != expected or any(not path.is_file() or path.is_symlink() for path in directory.iterdir()):
        raise ValueError(f'{directory}: expected exactly eight release files; '
                         f'missing={sorted(expected - actual)}, extra={sorted(actual - expected)}')


def unique_names(names, label):
    if not isinstance(names, list) or any(not isinstance(name, str) or not name for name in names):
        raise ValueError(label + ': expected a list of tool names')
    if len(names) != len(set(names)):
        raise ValueError(label + ': duplicate tool names')
    return set(names)


def catalog_rosters(root):
    import xml.etree.ElementTree as ET
    resource = root / 'src/Logic/ModelContextProtocol/ToolProfiles.resx'
    catalog = json.loads(ET.parse(resource).find(".//data[@name='Catalog']/value").text)['releases']
    rosters = {}
    for release in RELEASES:
        rows = catalog[release]
        names = unique_names([row['currentName'] for row in rows], f'V{release} catalog')
        profile = 'full' if release in ('20', '21') else 'plc-foundation'
        full = {row['currentName'] for row in rows if profile in row['profiles']}
        lite = {row['currentName'] for row in rows if 'lite' in row['profiles']}
        if not full or full != names or (release not in ('20', '21') and lite):
            raise ValueError(f'V{release}: invalid generated full/lite catalog profiles')
        rosters[release] = full, lite
    return rosters


def verified_contracts(directory, root):
    exact_release_files(directory)
    snapshots = load_snapshots(directory)
    rosters = catalog_rosters(root)
    import xml.etree.ElementTree as ET
    catalog = json.loads(ET.parse(root / 'src/Logic/ModelContextProtocol/ToolProfiles.resx').find(".//data[@name='Catalog']/value").text)
    expected = {release: [{**{key: r[key] for key in ('family', 'state', 'l5')},
        'entries': sorted({e['entry'] for e in catalog['behaviorEntries'] if e['releaseKey'] == release and e['family'] == r['family']})}
        for r in catalog['behaviorPolicies'] if r['releaseKey'] == release] for release in RELEASES}
    contracts = current_parameters(root)
    source_engine = {name for (profile, name) in contracts if profile == 'full-engine'}
    policy = (root / 'src/EngineHost/SharedToolCatalog.cs').read_text('utf-8')
    removed = set(re.findall(r'"(\w+)"', policy.split('Removed =', 1)[1].split('};', 1)[0]))
    source_engine = (source_engine | foundation_registered_names(root, contracts)) - removed
    catalog_engine = rosters['20'][0] | rosters['21'][0]
    if source_engine != catalog_engine:
        raise ValueError('Full-engine registered source inventory differs from generated catalog: '
                         + f'missing={sorted(catalog_engine - source_engine)}, '
                         + f'unregistered={sorted(source_engine - catalog_engine)}')
    source_foundation = foundation_registered_names(root, contracts, RELEASES[:6])
    catalog_foundation = set().union(*(rosters[release][0] for release in RELEASES[:6]))
    if source_foundation != catalog_foundation:
        raise ValueError('Foundation registered source inventory differs from generated catalog: '
                         + f'missing={sorted(catalog_foundation - source_foundation)}, '
                         + f'unregistered={sorted(source_foundation - catalog_foundation)}')
    for release, snapshot in snapshots.items():
        validate_contract_snapshot(snapshot, directory / (release + '.json'))
        names = unique_names([tool['name'] for tool in snapshot['tools']], f'V{release} full roster')
        full, lite = rosters[release]
        if names != full:
            raise ValueError(f'V{release}: full roster differs from generated catalog: {sorted(names ^ full)}')
        if snapshot.get('behaviorCapabilities') != expected[release]:
            raise ValueError(f'V{release}: behavior table differs from ledger-derived catalog')
        for tool in snapshot['tools']:
            validate_output(tool)
            profile = 'plc-foundation' if tool['name'] in source_foundation | FOUNDATION_UNAVAILABLE_BELOW_20 else snapshot['profile']
            parameters = contracts.get((profile, tool['name']))
            if parameters is None and tool['name'] not in source_foundation:
                raise ValueError(f'V{release}: {tool["name"]} is absent from source registrations')
            if parameters is None:
                # These Foundation-host-only tools construct their own schemas in the
                # registered McpServerTool implementation; the PR snapshot gate
                # requires refreshing their baseline when those declarations change.
                continue
            properties = tool['inputSchema'].get('properties', {})
            if not isinstance(properties, dict):
                raise ValueError(f'V{release}: {tool["name"]} inputSchema.properties must be an object')
            unregistered = set(properties) - set(parameters)
            if unregistered:
                raise ValueError(f'V{release}: {tool["name"]} schema has unregistered parameters: '
                                 + ', '.join(sorted(unregistered)))
            for name, schema in properties.items():
                if not isinstance(schema, dict):
                    raise ValueError(f'V{release}: {tool["name"]}.{name} schema must be an object')
                if 'default' in schema and parameters[name] is not None \
                        and schema['default'] != parameters[name]:
                    raise ValueError(f'V{release}: {tool["name"]}.{name} default differs from source declaration')
        check_current_schemas(snapshot, contracts, root)
        expected_profile = 'full-engine' if release in ('20', '21') else 'plc-foundation'
        if snapshot['profile'] != expected_profile:
            raise ValueError(f'V{release}: invalid contract profile')
        if release in ('20', '21'):
            if unique_names(snapshot['liteTools'], f'V{release} lite roster') != lite:
                raise ValueError(f'V{release}: lite roster differs from generated catalog')
        elif 'liteTools' in snapshot:
            raise ValueError(f'V{release}: Foundation must not advertise a lite roster')
    return snapshots


def validate_output(tool):
    if 'outputSchema' not in tool or tool['outputSchema'] is not None and not isinstance(tool['outputSchema'], dict):
        raise ValueError(tool['name'] + ': missing or invalid actual outputSchema')


def current_parameters(root):
    from engine_sources import EngineSources, lexer
    params = {}
    sources = EngineSources(root)
    for path, text in sources.sources.items():
        tokens, pairs, masked, starts = sources._parse(path)
        for match in re.finditer(r'\[McpServerTool\(Name\s*=\s*"([^"\n]+)"', text):
            declaration = re.search(r'\bpublic\s+[\w<>?,.\[\] ]+?\s+\w+\s*\(', masked[match.end():])
            if not declaration: raise ValueError('Cannot find current method: ' + match[1])
            opening = starts[match.end() + declaration.end() - 1]
            closing = pairs[opening]
            args = text[tokens[opening].end:tokens[closing].start]
            args = re.sub(r'\[Description\((?:\s*"(?:\\.|[^"\\])*"\s*\+?)+\)\s*\]', '', args)
            fields = re.findall(r'(?:^|,)\s*[\w<>?.\[\]]+\s+(\w+)(?:\s*=\s*("(?:\\.|[^"\\])*"|true|false|-?\d+|null))?', args)
            params[('full-engine', match[1])] = {name: json.loads(default) if default else None for name, default in fields}
    text = (root / 'src/FoundationHost/FoundationTools.cs').read_text(encoding='utf-8-sig')
    wrapper = (root / 'src/FoundationHost/FoundationV4Tool.cs').read_text(encoding='utf-8-sig')
    renames = dict(re.findall(r'\["([^"\n]+)"\] = "([^"\n]+)"', wrapper))
    for line in text.splitlines():
        match = re.match(r'\s*new\("([^"\n]+)"', line)
        if not match: continue
        fields = {name: None for name in re.findall(r'(?:S|new Argument)\("([^"\n]+)"', line)}
        for name, default in re.findall(r'new Argument\("([^"\n]+)",\s*"[^"\n]+",\s*(?:true|false),\s*("(?:\\.|[^"\\])*"|true|false|-?\d+|null|Array.Empty<string>\(\))', line):
            fields[name] = [] if default == 'Array.Empty<string>()' else json.loads(default)
        if 'Dry()' in line:
            fields.update(dryRun=True, confirm=False, expectedProjectFile='')
        params[('plc-foundation', renames.get(match[1], match[1]))] = fields
    import ported_families
    for family in ported_families.families(root).values():
        for name in family['tools']:
            params[('plc-foundation', name)] = params[('full-engine', name)]
    return params


def foundation_registered_names(root, parameters=None, releases=RELEASES):
    parameters = parameters if parameters is not None else current_parameters(root)
    names = {name for (profile, name) in parameters if profile == 'plc-foundation'}
    import ported_families
    for family in ported_families.families(root).values():
        enabled = set().union(*(ported_families.additions(root, key) for key in releases))
        names.difference_update(set(family['tools']) - enabled)
    names.difference_update(FOUNDATION_UNAVAILABLE_BELOW_20)
    wrapper = (root / 'src/FoundationHost/FoundationV4Tool.cs').read_text(encoding='utf-8-sig')
    renames = dict(re.findall(r'\["([^"\n]+)"\]\s*=\s*"([^"\n]+)"', wrapper))
    for path in (root / 'src/FoundationHost').glob('*.cs'):
        text = path.read_text(encoding='utf-8-sig')
        names.update(renames.get(name, name) for name in re.findall(
            r'new\s+(?:Offline\w+Tool|PassiveDiagnosticTool|GenerationTool)\("([^"\n]+)"', text))
    for filename in ('ToolUsageTool.cs', 'ImportOrderTool.cs', 'OfflineSymbolManifestTools.cs'):
        text = (root / 'src/FoundationHost' / filename).read_text(encoding='utf-8-sig')
        names.update(renames.get(name, name) for name in re.findall(r'\bName\s*=\s*"([^"\n]+)"', text))
    control = (root / 'src/FoundationHost/WorkbenchControlTools.cs').read_text('utf-8-sig')
    names.update(re.findall(r'"([A-Za-z0-9]+)"', control.split('string[] Names = {', 1)[1].split('};', 1)[0]))
    return names


def candidate_parameters(root):
    fields = set()
    for path in (root / 'src/Logic/V4').glob('*Contract.cs'):
        text = path.read_text(encoding='utf-8-sig')
        if 'IBehaviorCandidateContract' not in text and 'Contract :' not in text: continue
        fields.update(re.findall(r'(?:(?:properties|p)\[|\{\s*\[|,\s*\[)"([^"\n]+)"\]\s*=\s*new JsonObject|(?:Text|Flag|String|Bool)\("([^"\n]+)"', text))
    return {a or b for a, b in fields}


def check_current_schemas(snapshot, contracts, root=Path(__file__).resolve().parents[2]):
    candidate_fields = candidate_parameters(root)
    by_name = {tool['name']: tool for tool in snapshot['tools']}
    for row in snapshot['behaviorCapabilities']:
        if row['state'] != 'current': continue
        for entry in row['entries']:
            properties = by_name[entry]['inputSchema']['properties']
            profile = 'plc-foundation' if entry in foundation_registered_names(root, contracts) | FOUNDATION_UNAVAILABLE_BELOW_20 else snapshot['profile']
            if (profile, entry) not in contracts: continue
            allowed = contracts[(profile, entry)]
            if set(properties) - set(allowed):
                raise ValueError(entry + ': current schema advertises unimplemented parameters: ' + ', '.join(sorted(set(properties) - set(allowed))))
            for name in candidate_fields & set(properties):
                # Both products' defaults come from their current source declarations.
                if 'default' in properties[name] and properties[name]['default'] != allowed[name]:
                    raise ValueError(entry + ': current schema advertises a candidate default for ' + name)


def self_test(args):
    import copy
    root = ROOT
    import ported_families
    availability = (root / 'src/Adapters.Contracts/PortedFamilies.cs').read_text(encoding='utf-8-sig')
    parsed = ported_families.parse(availability)
    assert parsed['F20']['tool_releases']['ListCommunicationConnections'] == ['21']
    snapshot = load_snapshots(root / 'manifest/contracts/v4/baseline')['21']
    snapshot['formatVersion'] = FORMAT_VERSION
    contracts = current_parameters(root)
    # The inventory table itself must not be able to bless a candidate parameter.
    if 'behaviorCapabilities' not in snapshot:
        import xml.etree.ElementTree as ET
        catalog = json.loads(ET.parse(root / 'src/Logic/ModelContextProtocol/ToolProfiles.resx').find(".//data[@name='Catalog']/value").text)
        snapshot['behaviorCapabilities'] = [{'state': 'current', 'entries': [e['entry'] for e in catalog['behaviorEntries'] if e['releaseKey'] == '21']}]
    check_current_schemas(snapshot, contracts)
    count = 0
    for parameter in sorted(candidate_parameters(root) - set(contracts[('full-engine', 'ImportPlcBlock')])):
        mutated = copy.deepcopy(snapshot)
        tool = next(t for t in mutated['tools'] if t['name'] == 'ImportPlcBlock')
        tool['inputSchema']['properties'][parameter] = {'type': 'string', 'default': 'preview'}
        try: check_current_schemas(mutated, contracts)
        except ValueError: count += 1
        else: raise AssertionError('Candidate schema negative case accepted: ' + parameter)
    mutated = copy.deepcopy(snapshot)
    tool = next(t for t in mutated['tools'] if t['name'] == 'CompilePlcSoftware')
    tool['inputSchema']['properties']['password']['default'] = 'candidate-default'
    try: check_current_schemas(mutated, contracts)
    except ValueError: count += 1
    else: raise AssertionError('Candidate default accepted')
    import shutil, uuid
    scratch = root / 'bin-build' / ('p6-35-contract-selftest-' + uuid.uuid4().hex)
    scratch.mkdir(parents=True)
    try:
        for path in (root / 'manifest/contracts/v4/baseline').glob('*.json'):
            source = json.loads(path.read_text(encoding='utf-8'))
            source['formatVersion'] = FORMAT_VERSION
            (scratch / path.name).write_text(json.dumps(source, ensure_ascii=False, sort_keys=True, indent=2) + '\n',
                                             encoding='utf-8', newline='\n')
        verified_contracts(scratch, root)
        baseline21 = json.loads((root / 'manifest/contracts/v4/baseline/21.json').read_text(encoding='utf-8'))
        baseline21['formatVersion'] = FORMAT_VERSION
        for field in ('behaviorCapabilities', 'outputSchema'):
            current = copy.deepcopy(load_snapshots(scratch)['21'])
            if field == 'outputSchema': current['tools'][0].pop(field)
            else: current[field][0]['state'] = 'safe-v4'
            (scratch / '21.json').write_text(json.dumps(current), encoding='utf-8')
            try: verified_contracts(scratch, root)
            except ValueError: count += 1
            else: raise AssertionError('Invalid capability/output snapshot accepted')
            (scratch / '21.json').write_text(json.dumps(baseline21, ensure_ascii=False, sort_keys=True, indent=2) + '\n',
                                             encoding='utf-8', newline='\n')
    finally:
        for path in scratch.iterdir(): path.unlink()
        scratch.rmdir()
    for tool in ({'name': 'sentinel'}, {'name': 'sentinel', 'outputSchema': []}):
        try: validate_output(tool)
        except ValueError: count += 1
        else: raise AssertionError('Invalid outputSchema accepted')
    validate_output({'name': 'sentinel', 'outputSchema': None})
    for field in ('formatVersion', 'tools', 'behaviorCapabilities'):
        invalid = copy.deepcopy(snapshot)
        if field == 'formatVersion':
            invalid[field] = FORMAT_VERSION + 1
        elif field == 'tools':
            invalid['tools'][0]['unexpected'] = True
        else:
            invalid['behaviorCapabilities'][0]['unexpected'] = True
        try: validate_contract_snapshot(invalid, 'negative.json')
        except ValueError: count += 1
        else: raise AssertionError('Invalid frozen contract format accepted: ' + field)
    invalid = copy.deepcopy(snapshot)
    invalid['tools'][0].pop('outputSchema')
    try: validate_contract_snapshot(invalid, 'negative.json')
    except ValueError: count += 1
    else: raise AssertionError('Missing contract field accepted: outputSchema')
    from phase6_groups import P7_04B_DESCRIPTIONS
    after = {t['name']: copy.deepcopy(t) for t in snapshot['tools'] if t['name'] in P7_04B_DESCRIPTIONS}
    before = copy.deepcopy(after)
    for name, parameters in P7_04B_DESCRIPTIONS.items():
        for parameter in parameters:
            before[name]['inputSchema']['properties'][parameter]['description'] = 'Fallback parameter description.'
            after[name]['inputSchema']['properties'][parameter]['description'] = 'Parameter description.'
    _p7_04b_description_checks(before, after)
    for change in ('default', 'empty', 'extra', 'missing'):
        invalid = copy.deepcopy(after)
        properties = invalid['RetrieveProjectArchive']['inputSchema']['properties']
        if change == 'default': properties['dryRun']['default'] = False
        elif change == 'empty': properties['dryRun']['description'] = ' '
        elif change == 'extra': invalid['RetrieveProjectArchive']['descriptionSha256'] = 'unreviewed'
        else: properties['dryRun'].pop('description')
        try: _p7_04b_description_checks(before, invalid)
        except ValueError: count += 1
        else: raise AssertionError('Unreviewed description migration accepted: ' + change)
    for invalid in (
            availability.replace('ToolReleases = new Dictionary<string, string[]>', 'ToolReleases = UnknownFactory'),
            availability.replace('["ListCommunicationConnections"]', '["UnknownPortedTool"]'),
            availability.replace('["ListCommunicationConnections"] = new[] { "21" }', '["ListCommunicationConnections"] = new[] { "21", "21" }'),
            availability.replace('["ListCommunicationConnections"] = new[] { "21" }', '["ListCommunicationConnections"] = new[] { "22" }')):
        try: ported_families.parse(invalid)
        except AssertionError: count += 1
        else: raise AssertionError('Unreviewed per-tool release override accepted')
    print(f'Contract negative self-tests: {count} passed, 0 failed.')
    return 0


ROOT = Path(__file__).resolve().parents[2]


def verify(args):
    root = args.repo_root.resolve()
    snapshots = verified_contracts(args.baseline or root / 'manifest/contracts/v4/baseline', root)
    print(f'V4 contracts verified: {len(snapshots)} releases, '
          f'{sum(len(snapshot["tools"]) for snapshot in snapshots.values())} tool records; 0 issues.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
