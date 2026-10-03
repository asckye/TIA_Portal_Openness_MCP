"""Capture offline tool contracts and reject incompatible input schema changes.

capture starts each built runtime without TIA: V20/V21 through the release host harness over STDIO
(full and lite profiles), V14 SP1-V19 through `TiaMcpServer.exe --catalog`. The foundation host needs
the ASP.NET Core 8 runtime; when the machine lacks it, point DOTNET_ROOT and DOTNET_ROOT_X64 at a
private runtime before running capture. compare exits 1 on any breaking change.
"""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys


RELEASES = ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')


def tool_records(tools):
    names = [tool['name'] for tool in tools]
    if not names or len(names) != len(set(names)):
        raise ValueError('Tool catalog must be nonempty with unique names')
    return sorted(({'name': tool['name'], 'inputSchema': tool['inputSchema'],
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
    for release in args.releases:
        exe = root / 'runtime' / ('v' + release) / 'TiaMcpServer.exe'
        snapshot = {'release': release}
        if release in ('20', '21'):
            public_api = root / ('TIA_V' + release + '_PublicAPI') / ('V' + release)
            if release == '21':
                public_api /= 'net48'
            rosters = {}
            for profile in ('full', 'lite'):
                with resources.server(exe, public_api, int(release), 'stdio', profile,
                                      args.harness.resolve(), public_api) as (rpc, _, logs):
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
            snapshot.update(profile='full-engine', tools=rosters['full'],
                            liteTools=[tool['name'] for tool in rosters['lite']])
            if not set(snapshot['liteTools']) <= {tool['name'] for tool in snapshot['tools']}:
                raise ValueError('Lite roster contains tools absent from full profile')
        else:
            result = subprocess.run([str(exe), '--catalog'], check=True, capture_output=True,
                                    text=True, encoding='utf-8', timeout=60)
            snapshot.update(profile='plc-foundation', tools=tool_records(json.loads(result.stdout)['tools']))
        snapshots[release] = snapshot
        print(f'Captured V{release}: {len(snapshot["tools"])} tools', flush=True)
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
    baseline, current = load_snapshots(args.baseline), load_snapshots(args.current)
    total = Counter()
    for release in sorted(baseline.keys() | current.keys()):
        changes = []
        if release not in current or release not in baseline:
            changes.append(('breaking' if release not in current else 'compatible', 'release removed' if release not in current else 'release added'))
        else:
            old, new = baseline[release], current[release]
            if old['profile'] != new['profile']:
                changes.append(('breaking', 'engine profile changed'))
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    capture_parser = commands.add_parser('capture')
    capture_parser.add_argument('--repo-root', type=Path, required=True)
    capture_parser.add_argument('--harness', type=Path, required=True)
    capture_parser.add_argument('--output', type=Path, required=True)
    capture_parser.add_argument('--releases', nargs='+', choices=RELEASES, default=RELEASES)
    capture_parser.set_defaults(run=capture)
    compare_parser = commands.add_parser('compare')
    compare_parser.add_argument('--baseline', type=Path, required=True)
    compare_parser.add_argument('--current', type=Path, required=True)
    compare_parser.set_defaults(run=compare)
    args = parser.parse_args()
    try:
        return args.run(args)
    except (OSError, ValueError, KeyError, AssertionError, subprocess.SubprocessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
