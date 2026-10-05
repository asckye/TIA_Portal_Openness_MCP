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
    public_api_root = (args.public_api_root or (root / 'sdk' if (root / 'sdk').is_dir() else root)).resolve()
    executables = {}
    for override in args.exe:
        release, separator, path = override.partition('=')
        if not separator or release not in RELEASES or not path:
            raise ValueError('--exe must be RELEASE=PATH for a supported release')
        if release in executables:
            raise ValueError('Duplicate executable override for V' + release)
        executables[release] = Path(path).resolve()
    for release in args.releases:
        exe = executables.get(release, root / 'runtime' / ('v' + release) / (f'TiaMcp.Engine.V{release}.exe' if release in ('20', '21') else 'TiaMcp.FoundationHost.exe'))
        snapshot = {'release': release}
        if release in ('20', '21'):
            public_api = public_api_root / ('TIA_V' + release + '_PublicAPI') / ('V' + release)
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
            command = [str(exe), '--catalog']
            if release in executables:
                # Worktree output has no packaged release-key.txt marker.
                command.extend(['--release-key', release])
            result = subprocess.run(command, check=True, capture_output=True,
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
    if getattr(args, 'migration', None):
        return compare_migration(args)
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
        expected = {phase6_groups.mapped(name, members) for name in a}
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
    capture_parser = commands.add_parser('capture')
    capture_parser.add_argument('--repo-root', type=Path, required=True)
    capture_parser.add_argument('--harness', type=Path, required=True)
    capture_parser.add_argument('--output', type=Path, required=True)
    capture_parser.add_argument('--releases', nargs='+', choices=RELEASES, default=RELEASES)
    capture_parser.add_argument('--exe', action='append', default=[], metavar='RELEASE=PATH',
                                help='Override a release executable; repeat for multiple releases')
    capture_parser.add_argument('--public-api-root', type=Path,
                                help='Root containing TIA_V20_PublicAPI and TIA_V21_PublicAPI (default: <repo>/sdk, else the repo root)')
    capture_parser.set_defaults(run=capture)
    compare_parser = commands.add_parser('compare')
    compare_parser.add_argument('--baseline', type=Path, required=True)
    compare_parser.add_argument('--current', type=Path, required=True)
    compare_parser.add_argument('--migration', choices=list(__import__('phase6_groups').TASKS), help='Verify one phase-6 group migration: only its tools may change')
    compare_parser.add_argument('--releases', nargs='+', choices=RELEASES,
                                help='Compare only these releases (default: compare all releases strictly)')
    compare_parser.set_defaults(run=compare)
    args = parser.parse_args()
    try:
        return args.run(args)
    except (OSError, ValueError, KeyError, AssertionError, subprocess.SubprocessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
