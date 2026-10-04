"""Compare a configured domain's woven paths without executing native code.

The IL reader resolves metadata only. This checker expands adapter calls, local
functions, statically bound LINQ delegates and iterator/async MoveNext bodies.
It compares labelled control-flow graphs (including catch/finally edges), not a
sorted bag of calls. Cycles retain one copy of their loop body. Pure branches
whose two continuations are equivalent disappear; branch direction is retained
where it affects a native boundary. This is static evidence, not a live trace.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import unittest
import uuid
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
sys.setrecursionlimit(20000)


class Domain:
    def __init__(self, data):
        if data['schemaVersion'] != 1:
            raise ValueError('Unsupported domain configuration version')
        self.name, self.label = data['domain'], data['label']
        self.engine = tuple(data['engine']['types'])
        self.method_scopes = data['engine'].get('methodScopes', {})
        if not isinstance(self.method_scopes, dict) or not self.method_scopes.keys() <= set(self.engine):
            raise ValueError('Method scopes must name configured engine types')
        for type_name, entries in self.method_scopes.items():
            if not isinstance(entries, list) or not entries:
                raise ValueError('Method scope include lists must not be empty')
            signatures = set()
            for entry in entries:
                signature = entry.get('signature', '')
                if not entry.get('method') or not signature.endswith(')') or '(' not in signature \
                        or method_name(signature) != entry['method'] or not owner(dict(caller=signature), type_name):
                    raise ValueError('Method scope requires a matching method name and full IL signature')
                if signature in signatures:
                    raise ValueError('Duplicate method scope signature: ' + signature)
                signatures.add(signature)
        self.engine_releases = data['engine']['releases']
        self.releases = data['adapter']['releases']
        self.hosts = data['hosts']
        self.adapter_hosts = tuple(t for host in self.hosts for t in host['types'])
        self.mutable_adapter = tuple(data['adapter']['mutableTypes'])
        self.primitives = tuple(p['adapterType'] for p in data['primitives'])
        self.local_primitives = tuple(p['engineNamespace'] + '.' + p['adapterType'].rsplit('.', 1)[-1]
                                      for p in data['primitives'])
        self.tools = data['tools']
        self.evidence = ROOT / data['evidence']
        if not self.evidence.resolve().is_relative_to(ROOT):
            raise ValueError('Evidence output must be inside this worktree')
        if not re.fullmatch(r'[a-z][a-z0-9-]*', self.name):
            raise ValueError('Invalid domain name')
        if not self.engine or not self.primitives or not self.tools or not self.releases:
            raise ValueError('Domain types, primitives, tools and releases must not be empty')
        if len(set(self.releases)) != len(self.releases) or not set(self.engine_releases) <= set(self.releases):
            raise ValueError('Invalid engine/adapter releases')
        if not set(self.releases) <= {'14sp1', '15.1', '16', '17', '18', '19', '20', '21'} or not set(self.engine_releases) <= {'20', '21'}:
            raise ValueError('Unsupported build release')
        host_names = [h['name'] for h in self.hosts]
        if len(set(host_names)) != len(host_names) or any(n in ('engine', 'defaultEngine') for n in host_names):
            raise ValueError('Host names must be distinct from engine report keys')
        for host in self.hosts:
            if not host['types'] or not set(host['releases']) <= set(self.releases):
                raise ValueError('Invalid host types/releases')
        for tool in self.tools:
            if not tool['entryMethods'] or any(entry['type'] not in self.engine for entry in tool['entryMethods']):
                raise ValueError('Tool entry methods must belong to configured engine types')

    @property
    def acceptance_rule(self):
        rule = dict(
            id='expanded-native-paths-exact-dedup-v1',
            paths='Default and switched per-tool expanded native call graphs must equal the baseline.',
            default=f'The full-category member multiset must equal the baseline after collapsing each direct {self.label} member into one local primitive site; no other delta is allowed.',
            shared=f'Engine {self.label} direct sites move to deduplicated adapter primitives; only engine-required new members may be gained, with no change outside {self.label}.',
            evidence='Generate per-member counts and declaring-method moves; physical equality before deduplication is informational.')
        if self.method_scopes:
            rule['methodScopes'] = self.method_scopes
        return rule


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')


def normalize(name):
    name = re.sub(r'<>c__DisplayClass\d+_', '<>c__DisplayClass#_', name)
    name = re.sub(r'(>b__|>g__[^|]+\|)\d+_(\d+)', r'\1#_\2', name)
    # Iterator overloads can have the same source name but distinct state types.
    # Their ordinals are stable here; never collapse those identities.
    return name


def owner(row, prefix):
    name = row.get('owner') or row.get('caller', '').split('::')[0].rsplit(' ', 1)[-1]
    return name == prefix or name.startswith(prefix + '/')


def owned(row, prefixes):
    return any(owner(row, prefix) for prefix in prefixes)


def method_name(signature):
    return signature.split('::')[-1].split('(')[0]


def matches_entry(method, entry):
    return method['owner'] == entry['type'] and method_name(method['name']) == entry['method'] \
        and ('signature' not in entry or method['name'] == entry['signature'])


class EngineScope:
    def __init__(self, domain, document, counterparts=()):
        self.domain = domain
        self.methods = {m['name']: m for m in document['methods']}
        self.selected = set()
        for type_name, entries in domain.method_scopes.items():
            for entry in entries:
                matches = [m['name'] for m in document['methods'] if matches_entry(m, dict(entry, type=type_name))]
                known = any(matches_entry(m, dict(entry, type=type_name)) for doc in counterparts for m in doc['methods'])
                if len(matches) > 1 or (not matches and not known):
                    raise ValueError('Scoped method missing or ambiguous: ' + entry['signature'])
                self.selected.update(matches)
        # Follow only compiler-generated children for ownership, never ordinary
        # callees. A shared closure constructor belongs to every referring root.
        generated = {key for key, m in self.methods.items()
            if ('<' in m['owner'] or method_name(key).startswith('<')) and '__TiaMcpNativeCall' not in m['owner']}
        parents = {}
        for root, method in self.methods.items():
            if root in generated or '__TiaMcpNativeCall' in method['owner'] or not owned(method, tuple(domain.method_scopes)):
                continue
            pending, visited = [root], set()
            while pending:
                key = pending.pop()
                if key in visited:
                    continue
                visited.add(key)
                current = self.methods[key]
                children = {row['operand'].get('definition') for row in current.get('il') or []} & generated
                children.update(name for name, child in self.methods.items() if child['owner'] in current.get('states', []))
                for child in children:
                    parents.setdefault(child, set()).add(root)
                pending.extend(children - visited)
        self.selected.update(key for key, roots in parents.items() if roots <= self.selected)
        self.entries = {key for key, method in self.methods.items() if self(method) and '__TiaMcpNativeCall' not in method['owner']}
        if domain.method_scopes:
            for tool in domain.tools:
                for entry in tool['entryMethods']:
                    matches = [key for key, method in self.methods.items() if matches_entry(method, entry)]
                    if len(matches) != 1:
                        raise ValueError('Tool entry missing or ambiguous: ' + tool['name'])
                    self.entries.update(matches)

    def __call__(self, row):
        return owned(row, tuple(t for t in self.domain.engine if t not in self.domain.method_scopes)) \
            or (row.get('name') or row.get('caller')) in self.selected

    def body(self, row):
        # Woven wrappers are verified by the weaver and the exact caller/member
        # inventory; moving a selected native site removes its old wrapper.
        return self(row) or ('__TiaMcpNativeCall' in row['owner'] and owned(row, tuple(self.domain.method_scopes)))


def outside(sites, prefixes=(), scope=lambda row: False):
    return Counter((s['caller'], s['category'], s['opcode'], s['member']) for s in sites
        if not owned(s, prefixes) and not scope(s))


def native_counts(sites):
    return Counter(row['member'] for row in sites if row['category'] == 'direct')


def member_counts(sites):
    return Counter((row['category'], row['opcode'], row['member']) for row in sites)


def moved_members(before, after, prefixes, scopes=(lambda row: False, lambda row: False)):
    # Keep every category and every original/current declaring caller in the
    # generated evidence; repeated sites in one method retain their count.
    old, new = ([row for row in rows if owned(row, prefixes) or scope(row)]
                for rows, scope in zip((before, after), scopes))
    keys = sorted(member_counts(old).keys() | member_counts(new).keys())
    return [dict(category=category, opcode=opcode, member=member,
        before=dict(sorted(Counter(row['caller'] for row in old
            if (row['category'], row['opcode'], row['member']) == (category, opcode, member)).items())),
        after=dict(sorted(Counter(row['caller'] for row in new
            if (row['category'], row['opcode'], row['member']) == (category, opcode, member)).items())))
        for category, opcode, member in keys]


def expanded_engine_inventory(document, sites, domain, scope=None):
    methods = Paths([document]).methods
    result = member_counts([row for row in sites if not owned(row, domain.local_primitives)])

    def inline(key, active=()):
        if key in active:
            raise ValueError('Recursive local primitive: ' + key)
        counts = Counter()
        for instruction in methods[key]['il']:
            if instruction['flow'] != 'Call':
                continue
            operand = instruction['operand']
            native = operand.get('native')
            target = normalize(operand.get('definition') or '')
            if native:
                counts[(native['category'], native['opcode'], native['member'])] += 1
            elif target in methods and owned(methods[target], domain.local_primitives):
                counts.update(inline(target, (*active, key)))
        return counts

    # Expand each original service call site once, not every path from every
    # public tool; existing engine helper methods already have their own sites.
    reached = None
    if domain.method_scopes:
        paths = Paths([document])
        for key in scope.entries:
            paths.build(key)
        reached = paths.reached
    for key, method in methods.items():
        if owned(method, domain.local_primitives) or (key not in reached if reached is not None else not owned(method, domain.engine)):
            continue
        for instruction in method['il']:
            target = normalize(instruction['operand'].get('definition') or '')
            if instruction['flow'] == 'Call' and target in methods and owned(methods[target], domain.local_primitives):
                result.update(inline(target))
    return result


def expected_local_inventory(sites, domain, scope=None):
    selected = scope or (lambda row: owned(row, domain.engine))
    moved = member_counts([row for row in sites if selected(row) and row['category'] == 'direct'])
    return member_counts(sites) - moved + Counter({key: 1 for key in moved})


def compare_default_inventory(before, after, domain, scopes=None):
    # Derive the only allowed delta from the baseline, never from current sites.
    scopes = scopes or (lambda row: owned(row, domain.engine),) * 2
    old, current, expected = member_counts(before), member_counts(after), expected_local_inventory(before, domain, scopes[0])
    prefixes = domain.local_primitives
    moves = moved_members(before, after, prefixes, scopes)
    report = dict(defaultFullInventoryEqual=old == current,
        defaultDeduplicatedInventoryEqual=expected == current,
        defaultPhysicalSiteCounts=dict(before=len(before), after=len(after)),
        defaultExpectedPhysicalDelta=[dict(category=category, opcode=opcode, member=member,
            before=count, after=expected[(category, opcode, member)])
            for (category, opcode, member), count in sorted(old.items())
            if count != expected[(category, opcode, member)]],
        defaultUnexpectedPhysicalDelta=[dict(category=category, opcode=opcode, member=member,
            expected=expected[key], actual=current[key])
            for key in sorted(expected.keys() | current.keys())
            for category, opcode, member in [key] if expected[key] != current[key]],
        defaultExpectedMemberMoves=moves)
    errors = [] if expected == current else [f'Default physical inventory differs from the exact {domain.label} deduplication delta']
    # Check ownership as well as totals: one domain must never pay for another
    # domain's missing or extra copy of the same Siemens member.
    selected_before = [s for s in before if owned(s, prefixes) or scopes[0](s)]
    selected_after = [s for s in after if owned(s, prefixes) or scopes[1](s)]
    if expected_local_inventory(selected_before, domain, scopes[0]) != member_counts(selected_after):
        errors.append('Default domain inventory differs from its exact deduplication delta')
    if outside(before, prefixes, scopes[0]) != outside(after, prefixes, scopes[1]):
        errors.append('Default non-domain inventory changed')
    return errors, report


class Graph:
    def __init__(self):
        self.nodes = {0: ('return', {})}
        self.next_id = 1

    def node(self, label, edges=None):
        number = self.next_id
        self.next_id += 1
        self.nodes[number] = (label, edges or {})
        return number

    def skip(self, number):
        visited = set()
        while self.nodes[number][0] == 'epsilon':
            if number in visited:
                return 0  # A cycle without a boundary contributes no native site.
            visited.add(number)
            number = self.nodes[number][1]['next']
        return number

    def canonical(self, root):
        # Repeated bisimulation refinement also removes branch/merge scaffolding
        # introduced by a transparent primitive's argument and return handling.
        while True:
            reachable = set()
            pending = [self.skip(root)]
            while pending:
                item = self.skip(pending.pop())
                if item in reachable:
                    continue
                reachable.add(item)
                pending.extend(self.nodes[item][1].values())
            colors = {n: self.nodes[n][0] for n in reachable}
            while True:
                signatures = {n: (self.nodes[n][0], tuple((key, colors[self.skip(value)])
                    for key, value in sorted(self.nodes[n][1].items()))) for n in reachable}
                palette = {signature: str(index) for index, signature in enumerate(sorted(set(signatures.values())))}
                new = {n: palette[signature] for n, signature in signatures.items()}
                old_to_new, new_to_old = {}, {}
                stable = True
                for n in reachable:
                    if old_to_new.setdefault(colors[n], new[n]) != new[n] or new_to_old.setdefault(new[n], colors[n]) != colors[n]:
                        stable = False
                        break
                if stable:
                    colors = new
                    break
                colors = new
            collapsed = False
            for n in reachable:
                label, edges = self.nodes[n]
                if label.startswith('branch:') and len({colors[self.skip(v)] for v in edges.values()}) == 1:
                    self.nodes[n] = ('epsilon', {'next': next(iter(edges.values()))})
                    collapsed = True
            if not collapsed:
                break
        # Serialize the quotient graph in edge order, independent of IL offsets,
        # method boundaries, local slots and compiler-generated closure ordinals.
        ids = {}
        rows = []
        def visit(n):
            n = self.skip(n)
            color = colors[n]
            if color in ids:
                return ids[color]
            result = len(ids)
            ids[color] = result
            rows.append(None)
            label, edges = self.nodes[n]
            rows[result] = [label, [[key, visit(target)] for key, target in sorted(edges.items())]]
            return result
        visit(root)
        return rows


class Paths:
    def __init__(self, documents):
        self.methods = {}
        for document in documents:
            for method in document['methods']:
                if method.get('il') is not None and '__TiaMcpNativeCall' not in method['owner']:
                    key = normalize(method['name'])
                    if key in self.methods:
                        # Empty closure constructors are identical apart from their
                        # compiler-wide class ordinal. Never merge different bodies.
                        if '::.ctor(' in key and self.methods[key]['semantic'] == method['semantic']:
                            continue
                        raise ValueError('Ambiguous method: ' + key)
                    self.methods[key] = method
        self.reached = set()
        self.boundaries = {}

    def has_boundary(self, key, active=()):
        if key in self.boundaries:
            return self.boundaries[key]
        if key in active:
            return False
        method = self.methods[key]
        result = any(row['operand'].get('native') for row in method['il']) or bool(method.get('states'))
        if not result:
            result = any(self.has_boundary(target, (*active, key)) for row in method['il']
                if (target := normalize(row['operand'].get('definition') or '')) in self.methods)
        self.boundaries[key] = result
        return result

    def callbacks(self, method):
        result = {}
        targets = []
        for instruction in method['il']:
            operand = instruction['operand']
            if instruction['op'] in ('ldftn', 'ldvirtftn'):
                target = operand.get('definition')
                if target:
                    targets.append(normalize(target))
            if instruction['flow'] == 'Call' and operand.get('kind') == 'method':
                native = operand.get('native')
                name = native['member'] if native else operand['name']
                # Bind at the consuming LINQ call, including cached noncapturing
                # delegates. Select's body remains attached to its deferred operator.
                linq = 'System.Linq.Enumerable::' in name and ('System.Func' in name or 'System.Action' in name)
                bound_invoke = '::Invoke(' in name and ('System.Func' in name or 'System.Action' in name) and targets
                if linq or bound_invoke:
                    if not targets:
                        raise ValueError('Unresolved LINQ delegate: ' + method['name'] + ' -> ' + name)
                    result[instruction['offset']] = [targets[-1]]
        return result

    def build(self, name):
        graph = Graph()
        active = {}

        def expand(key, continuation, outer_handlers=()):
            key = normalize(key)
            method = self.methods[key]
            self.reached.add(key)
            if key in active:
                # Typed navigator iterators recurse down child groups. Retain a
                # labelled back edge and the caller continuation, rather than
                # truncating the native body or unrolling an unbounded tree.
                return graph.node('recursive:' + key, {'body': active[key], 'return': continuation})
            entry = graph.node('epsilon')
            active[key] = entry
            il = method['il']
            slots = {row['offset']: graph.node('epsilon') for row in il}
            callbacks = self.callbacks(method)
            handlers = method.get('handlers', [])
            for index in range(len(il)-1, -1, -1):
                instruction = il[index]
                operand = instruction['operand']
                offset = instruction['offset']
                here = slots[offset]
                after = slots[il[index+1]['offset']] if index+1 < len(il) else continuation
                catches = [(handler['kind'] + ':' + str(handler['type']), slots[handler['target']])
                    for handler in handlers if handler['start'] <= offset < handler['end']]
                catches += list(outer_handlers)
                # Inner handlers take precedence over equal outer catch types.
                exceptional = {}
                for kind, target in catches:
                    exceptional.setdefault('exception:' + kind, target)
                op, flow = instruction['op'], instruction['flow']
                if flow == 'Return' or op == 'endfinally':
                    graph.nodes[here] = ('epsilon', {'next': continuation})
                elif flow == 'Throw':
                    graph.nodes[here] = ('branch:throw', exceptional or {'unhandled': 0})
                elif flow == 'Branch':
                    graph.nodes[here] = ('epsilon', {'next': slots[operand['target']]})
                elif flow == 'Cond_Branch':
                    if operand['kind'] == 'switch':
                        edges = {str(i): slots[target] for i, target in enumerate(operand['targets'])}
                        edges['default'] = after
                        label = 'switch'
                    else:
                        target = slots[operand['target']]
                        if op.startswith('brfalse'):
                            label, edges = 'truth', {'true': after, 'false': target}
                        elif op.startswith('brtrue'):
                            label, edges = 'truth', {'true': target, 'false': after}
                        else:
                            label, edges = op.removesuffix('.s'), {'true': target, 'false': after}
                    # Distinguish host options such as dryRun and changedOnly even
                    # when moving a boundary between them leaves the CFG shape equal.
                    if index and label == 'truth':
                        producer = il[index-1]
                        argument = None
                        if re.fullmatch(r'ldarg\.\d', producer['op']):
                            argument = int(producer['op'][-1]) - int(method.get('HasThis', False))
                        elif producer['op'] in ('ldarg', 'ldarg.s') and producer['operand']['kind'] == 'parameter':
                            argument = producer['operand']['index']
                        arguments = method.get('arguments', [])
                        if argument is not None and 0 <= argument < len(arguments) and arguments[argument]['type'] == 'System.Boolean':
                            label += ':option=' + arguments[argument]['name']
                    graph.nodes[here] = ('branch:' + label, edges)
                elif flow == 'Call' and operand['kind'] == 'method':
                    next_node = after
                    for callback in reversed(callbacks.get(offset, [])):
                        next_node = expand(callback, next_node, catches)
                    native = operand.get('native')
                    target = normalize(operand.get('definition') or '')
                    if native:
                        label = json.dumps([native['category'], native['opcode'], native['member']], separators=(',', ':'))
                        graph.nodes[here] = (label, {'next': next_node, **exceptional})
                    elif target in self.methods and self.has_boundary(target):
                        graph.nodes[here] = ('epsilon', {'next': expand(target, next_node, catches)})
                    else:
                        graph.nodes[here] = ('epsilon', {'next': next_node})
                else:
                    graph.nodes[here] = ('epsilon', {'next': after})
            root = slots[il[0]['offset']] if il else continuation
            for state in reversed(method.get('states', [])):
                candidates = [n for n, m in self.methods.items() if m['owner'] == state and '::MoveNext()' in n]
                if len(candidates) != 1:
                    raise ValueError('Unresolved state machine: ' + state)
                # State-machine bodies are separate CFGs, with their own loop/catch
                # edges; returning the iterator/task itself performs no native work.
                root = expand(candidates[0], root, outer_handlers)
            graph.nodes[entry] = ('epsilon', {'next': root})
            del active[key]
            return entry

        root = expand(name, 0)
        return graph.canonical(root)


def compare_paths(before, after, prefix, entries=None):
    errors, evidence = [], []
    originals = sorted(key for key, method in before.methods.items() if method['owner'].split('/')[0] == prefix
        and (entries is None or key in entries))
    for key in originals:
        if key not in after.methods:
            errors.append('Missing original method: ' + key)
            continue
        old, new = before.build(key), after.build(key)
        equal = old == new
        evidence.append(dict(method=key, equal=equal, beforeNodes=len(old), afterNodes=len(new),
                             expandedGraphSha256=hashlib.sha256(json.dumps(new, separators=(',', ':')).encode()).hexdigest()))
        if not equal:
            first = next((i for i, pair in enumerate(zip(old, new)) if pair[0] != pair[1]), min(len(old), len(new)))
            errors.append(f'{key}: expanded branch graph changed at {first}: '
                          f'{old[first:first+1]} -> {new[first:first+1]}')
    if not originals:
        errors.append('No methods checked for ' + prefix)
    return errors, evidence


def compare_host_paths(before, after, types, scope=None):
    errors, evidence = [], []
    for prefix in types:
        entries = {normalize(key) for key in scope.entries} if scope is not None else None
        failures, rows = compare_paths(before, after, prefix, entries)
        errors += failures
        evidence += rows
    return errors, evidence


def check_tools(domain, rows):
    errors = []
    for tool in domain.tools:
        for entry in tool['entryMethods']:
            matches = [row for row in rows if owner(dict(caller=row['method']), entry['type'])
                and '::' + entry['method'] + '(' in row['method']
                and ('signature' not in entry or row['method'] == normalize(entry['signature']))]
            if len(matches) != 1:
                errors.append('Tool entry missing or ambiguous: ' + tool['name'] + ' -> '
                              + entry['type'] + '::' + entry['method'])
    return errors


def all_methods(before, after, raw=False, exclude=(), scopes=(lambda row: False, lambda row: False)):
    select = lambda doc, scope: {m['name']: m['raw' if raw else 'semantic'] for m in doc['methods']
        if not owned(m, exclude) and not scope(m)}
    old, new = select(before, scopes[0]), select(after, scopes[1])
    return [key for key in sorted(old.keys() | new.keys()) if old.get(key) != new.get(key)], len(old), len(new)


def dump(assembly, inventory, directory, config):
    directory = Path(directory).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    tool = ROOT / 'bin-build/shared-native-il-reader'
    tool.mkdir(parents=True, exist_ok=True)
    cecil = ROOT / 'tools/native-call-weaver/bin/Release/net8.0/Mono.Cecil.dll'
    if not cecil.is_file():
        raise ValueError('Build NativeCallWeaver first: ' + str(cecil))
    project = tool / 'Reader.csproj'
    if not project.exists():
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework>'
            '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit>'
            '</PropertyGroup><ItemGroup><Compile Include="' + escape(str(ROOT / 'scripts/checks/SharedNativeIlReader.cs')) + '" />'
            '<Reference Include="Mono.Cecil"><HintPath>' + escape(str(cecil)) + '</HintPath></Reference>'
            '</ItemGroup></Project>', encoding='utf-8')
    reader = tool / 'bin/Release/net8.0/Reader.dll'
    if not reader.exists() or reader.stat().st_mtime < max(project.stat().st_mtime,
            (ROOT / 'scripts/checks/SharedNativeIlReader.cs').stat().st_mtime, cecil.stat().st_mtime):
        subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '-m:1', '-nr:false',
                        '-p:RestoreSources=' + str(tool), '-v:q'], check=True)
    output = directory / (Path(assembly).name + '.il.json')
    subprocess.run(['dotnet', str(reader), str(Path(assembly).resolve()),
                    str(Path(inventory).resolve()), str(output), str(config.resolve())], check=True)
    return output


class SelfTests(unittest.TestCase):
    @staticmethod
    def config(name='sample'):
        return dict(schemaVersion=1, domain=name, label=name.upper(),
            engine=dict(types=[name + '.Engine'], releases=['20']),
            adapter=dict(releases=['19', '20'], mutableTypes=[name + '.Borrowed']),
            hosts=[dict(name='foundation', types=[name + '.Foundation'], releases=['19', '20'])],
            primitives=[dict(adapterType=name + '.Primitives', engineNamespace=name + '.Local')],
            expansionTypes=[name + '.Helper'], expansionNamespaces=[],
            tools=[dict(name='PublicTool', entryMethods=[dict(type=name + '.Engine', method='Run')])],
            evidence='bin-build/shared-native-self-test/' + name + '.json')

    def setUp(self):
        self.domain = Domain(self.config())

    @staticmethod
    def method(name, instructions, states=(), handlers=()):
        body = json.dumps([instructions, states, handlers], sort_keys=True)
        return dict(name=name, owner=name.split('::')[0].rsplit(' ', 1)[-1], semantic=body, raw=body, il=[dict(offset=i, **row)
            for i, row in enumerate(instructions)], states=states, handlers=handlers)

    @staticmethod
    def call(member, native=False, category='direct', definition=None):
        return dict(op='call', flow='Call', operand=dict(kind='method', name=member, definition=definition,
            native=dict(member=member, opcode='callvirt', category=category) if native else None))

    @staticmethod
    def ret():
        return dict(op='ret', flow='Return', operand=dict(kind='value', value=None))

    def test_real_expansion_and_mutations(self):
        before = self.method('Host::Run()', [self.call('Siemens::Read()', True), self.call('Siemens::Write()', True), self.ret()])
        caller = self.method('Host::Run()', [self.call('Adapter::Run()', definition='Adapter::Run()'), self.ret()])
        for events, equal in ((('Read', 'Write'), True), (('Write', 'Read'), False), (('Read',), False),
                              (('Read', 'Write', 'Write'), False)):
            target = self.method('Adapter::Run()', [*(self.call('Siemens::' + event + '()', True) for event in events), self.ret()])
            old = Paths([dict(methods=[before])]).build(before['name'])
            new = Paths([dict(methods=[caller, target])]).build(caller['name'])
            self.assertEqual(equal, old == new)

    def test_bound_delegate_expansion(self):
        callback = self.method('Host::<Run>b__0()', [self.call('Siemens::Read()', True), self.ret()])
        host = self.method('Host::Run()', [dict(op='ldftn', flow='Next', operand=dict(kind='method', definition=callback['name'])),
            self.call('System.Linq.Enumerable::FirstOrDefault(System.Func)', True, 'enumeration-input'), self.ret()])
        old = Paths([dict(methods=[host, callback])]).build(host['name'])
        changed = self.method(callback['name'], [self.call('Siemens::Other()', True), self.ret()])
        new = Paths([dict(methods=[host, changed])]).build(host['name'])
        self.assertNotEqual(old, new)
        self.assertTrue(any('Siemens::Read' in row[0] for row in old))

    def test_state_machine_and_catch_expansion(self):
        outer = self.method('Host::Run()', [self.ret()], states=['State'])
        state = self.method('State::MoveNext()', [self.call('Siemens::Read()', True), self.ret(),
            self.call('Siemens::Recover()', True), self.ret()], handlers=[dict(kind='Catch', type='Exception', start=0, end=1, target=2)])
        old = Paths([dict(methods=[outer, state])]).build(outer['name'])
        state['il'][2]['operand']['native']['member'] = 'Siemens::WrongRecovery()'
        new = Paths([dict(methods=[outer, state])]).build(outer['name'])
        self.assertNotEqual(old, new)
        self.assertTrue(any('exception:Catch:Exception' in str(row) for row in old))

    def test_bound_delegate_invoke_expansion(self):
        callback = self.method('Host::<Run>b__0()', [self.call('Siemens::Read()', True), self.ret()])
        host = self.method('Host::Run()', [dict(op='ldftn', flow='Next', operand=dict(kind='method', definition=callback['name'])),
            self.call('System.Action::Invoke()'), self.ret()])
        graph = Paths([dict(methods=[host, callback])]).build(host['name'])
        self.assertTrue(any('Siemens::Read' in row[0] for row in graph))

    def test_unresolved_delegate_fails_closed(self):
        host = self.method('Host::Run()', [self.call('System.Linq.Enumerable::FirstOrDefault(System.Func)', True), self.ret()])
        with self.assertRaisesRegex(ValueError, 'Unresolved LINQ delegate'):
            Paths([dict(methods=[host])]).build(host['name'])

    def test_default_non_domain_body_change_is_failure(self):
        old = dict(methods=[dict(name='Other::Write()', owner='Other', raw='before')])
        new = dict(methods=[dict(name='Other::Write()', owner='Other', raw='after')])
        self.assertEqual(['Other::Write()'], all_methods(old, new, raw=True)[0])

    def test_metadata_relocation_does_not_hide_instruction_changes(self):
        old = dict(methods=[dict(name='Other::Write()', owner='Other', raw='old token', semantic='same IL')])
        new = dict(methods=[dict(name='Other::Write()', owner='Other', raw='new token', semantic='same IL')])
        self.assertEqual([], all_methods(old, new)[0])
        new['methods'][0]['semantic'] = 'different opcode or operand'
        self.assertEqual(['Other::Write()'], all_methods(old, new)[0])

    def test_full_category_members_and_generated_move_counts(self):
        row = dict(caller=self.domain.engine[0] + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        moved = dict(row, caller=self.domain.local_primitives[0] + '::Read()')
        self.assertEqual(member_counts([row]), member_counts([moved]))
        self.assertNotEqual(member_counts([row, row]), member_counts([moved]))
        self.assertNotEqual(member_counts([row]), member_counts([dict(moved, category='interface-dispatch')]))
        evidence = moved_members([row, row], [moved], (self.domain.engine[0], self.domain.local_primitives[0]))
        self.assertEqual(2, evidence[0]['before'][row['caller']])
        self.assertEqual(1, evidence[0]['after'][moved['caller']])

    def test_expanded_inventory_retains_each_engine_call_site(self):
        primitive = self.method(self.domain.local_primitives[0] + '::Read()', [self.call('Siemens::Read()', True), self.ret()])
        host = self.method(self.domain.engine[0] + '::Run()', [self.call(primitive['name'], definition=primitive['name']),
            self.call(primitive['name'], definition=primitive['name']), self.ret()])
        site = dict(caller=primitive['name'], category='direct', opcode='callvirt', member='Siemens::Read()')
        physical = member_counts([site])
        expanded = expanded_engine_inventory(dict(methods=[host, primitive]), [site], self.domain)
        self.assertEqual(1, sum(physical.values()))
        self.assertEqual(2, sum(expanded.values()))

    def test_default_exact_dedup_delta_is_accepted(self):
        site = dict(caller=self.domain.engine[0] + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        moved = dict(site, caller=self.domain.local_primitives[0] + '::Read()')
        untouched = dict(site, caller='Other::Run()', category='interface-dispatch')
        errors, report = compare_default_inventory([site, site, untouched], [moved, untouched], self.domain)
        self.assertEqual([], errors)
        self.assertFalse(report['defaultFullInventoryEqual'])
        self.assertTrue(report['defaultDeduplicatedInventoryEqual'])
        self.assertEqual([], report['defaultUnexpectedPhysicalDelta'])
        self.assertEqual(2, report['defaultExpectedPhysicalDelta'][0]['before'])
        self.assertEqual(1, report['defaultExpectedPhysicalDelta'][0]['after'])
        move = report['defaultExpectedMemberMoves'][0]
        self.assertEqual({site['caller']: 2}, move['before'])
        self.assertEqual({moved['caller']: 1}, move['after'])

    def test_default_unexpected_extra_or_missing_member_fails(self):
        site = dict(caller=self.domain.engine[0] + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        moved = dict(site, caller=self.domain.local_primitives[0] + '::Read()')
        untouched = dict(site, caller='Other::Run()', category='enumeration-input', member='Enumerable::ToList()')
        before, valid = [site, site, untouched], [moved, untouched]
        mutations = dict(extra=valid + [dict(moved, member='Siemens::Unexpected()')],
            missing=[untouched], duplicated=valid + [moved], unrelated_missing=[moved],
            unrelated_extra=valid + [untouched], changed_category=[dict(moved, category='interface-dispatch'), untouched],
            changed_dispatch=[dict(moved, opcode='call'), untouched])
        for mutation, after in mutations.items():
            with self.subTest(mutation=mutation):
                errors, report = compare_default_inventory(before, after, self.domain)
                self.assertTrue(errors)
                self.assertFalse(report['defaultDeduplicatedInventoryEqual'])
                self.assertTrue(report['defaultUnexpectedPhysicalDelta'])

    def test_domain_deltas_cannot_cancel(self):
        other = Domain(self.config('second'))
        site = dict(caller=self.domain.engine[0] + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        foreign = dict(site, caller=other.local_primitives[0] + '::Read()')
        before = [site, site, foreign]
        local = dict(site, caller=self.domain.local_primitives[0] + '::Read()')
        self.assertEqual([], compare_default_inventory(before, [local, foreign], self.domain)[0])
        # Both mutations satisfy the global exact dedup delta. Their owners do
        # not: a missing copy in one domain pays for an extra in the other.
        for after in ([local, local], [foreign, foreign]):
            self.assertEqual(expected_local_inventory(before, self.domain), member_counts(after))
            errors, _ = compare_default_inventory(before, after, self.domain)
            self.assertIn('Default domain inventory differs from its exact deduplication delta', errors)
            self.assertIn('Default non-domain inventory changed', errors)
            self.assertTrue(compare_default_inventory(before, after, other)[0])

    def test_type_prefix_does_not_swallow_another_domain(self):
        self.assertTrue(owner(dict(caller='System.Void First/Closure::Run()'), 'First'))
        self.assertFalse(owner(dict(caller='System.Void FirstOther::Run()'), 'First'))

    def test_configured_tool_entry_is_required(self):
        self.assertEqual([], check_tools(self.domain, [dict(method=self.domain.engine[0] + '::Run()')]))
        self.assertTrue(check_tools(self.domain, [dict(method='Other::Run()')]))
        self.assertTrue(check_tools(self.domain, [dict(method=self.domain.engine[0] + '::Run(System.Int32)'),
                                                 dict(method=self.domain.engine[0] + '::Run()')]))

    def test_second_domain_config_end_to_end(self):
        # Exercise the actual CLI and JSON loader with Foundation, multiple
        # engine services/primitives and a public tool whose method differs.
        config = self.config('second')
        config['engine']['types'].append('second.OtherEngine')
        config['primitives'].append(dict(adapterType='second.MorePrimitives', engineNamespace='second.Local'))
        config['tools'].append(dict(name='AnotherTool', entryMethods=[dict(type='second.OtherEngine', method='Execute')]))
        domain = Domain(config)
        old_engine, default_engine, shared_engine, primitives, local = [], [], [], [], []
        old_sites, default_sites, new_sites = [], [], []
        for engine, primitive, local_type, method in zip(domain.engine, domain.primitives, domain.local_primitives, ('Run', 'Execute')):
            member = 'Siemens::' + method + '()'
            entry = engine + '::' + method + '()'
            target, local_target = primitive + '::Read()', local_type + '::Read()'
            old_engine.append(self.method(entry, [self.call(member, True), self.call(member, True), self.ret()]))
            default_engine.append(self.method(entry, [self.call(local_target, definition=local_target), self.call(local_target, definition=local_target), self.ret()]))
            shared_engine.append(self.method(entry, [self.call(target, definition=target), self.call(target, definition=target), self.ret()]))
            primitives.append(self.method(target, [self.call(member, True), self.ret()]))
            local.append(self.method(local_target, [self.call(member, True), self.ret()]))
            row = dict(caller=entry, category='direct', opcode='callvirt', member=member)
            old_sites += [row, row]
            default_sites.append(dict(row, caller=local_target))
            new_sites.append(dict(row, caller=target))
        host = self.method('second.Foundation::Read()', [self.ret()])
        # Hosts without the domain surface remain explicit in evidence.
        config['hosts'][0]['releases'] = ['20']
        scratch = ROOT / 'bin-build/shared-native-self-test'
        scratch.mkdir(parents=True, exist_ok=True)
        path = (scratch / uuid.uuid4().hex).resolve()
        path.mkdir()
        try:
            write(path / 'domain.json', config)
            docs = dict(baseline_engine=old_engine, default_engine=default_engine + local,
                        shared_engine=shared_engine, baseline_adapter=[host], current_adapter=[host] + primitives)
            inventories = dict(baseline_engine=old_sites, default_engine=default_sites, shared_engine=[],
                               baseline_adapter=[], current_adapter=new_sites)
            arguments = [sys.executable, __file__, '--config', str(path / 'domain.json')]
            compare = arguments + ['--release', '20', '--output', str(path / 'proof-v20.json')]
            for name, methods in docs.items():
                write(path / (name + '.json'), dict(methods=methods))
                write(path / (name + '-inventory.json'), dict(sites=inventories[name]))
                compare += ['--' + name.replace('_', '-'), str(path / (name + '.json')),
                            '--' + name.replace('_', '-') + '-inventory', str(path / (name + '-inventory.json'))]
            result = subprocess.run(compare, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            proof = read(path / 'proof-v20.json')
            self.assertEqual([], proof['errors'])
            self.assertEqual(2, len(proof['enginePaths']))
            self.assertEqual(1, len(proof['foundationPaths']))
            write(path / 'empty.json', dict(methods=[], sites=[]))
            older = arguments + ['--release', '19', '--output', str(path / 'proof-v19.json')]
            for flag in ('baseline-adapter', 'current-adapter', 'baseline-adapter-inventory', 'current-adapter-inventory'):
                older += ['--' + flag, str(path / 'empty.json')]
            result = subprocess.run(older, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual('SECOND not compiled on this release', read(path / 'proof-v19.json')['foundationPaths'])
            result = subprocess.run(arguments + ['--evidence-from', str(path), '--output', str(path / 'evidence.json')], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertTrue(read(path / 'evidence.json')['accepted'])
            # Shared totals also cannot conceal a loss in a different domain.
            foreign = dict(new_sites[0], caller='first.Primitives::Read()')
            write(path / 'baseline_adapter-inventory.json', dict(sites=[foreign]))
            self.assertEqual(0, (native_counts(new_sites) - native_counts([foreign]))[foreign['member']])
            result = subprocess.run(compare, capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn('Non-domain adapter inventory changed', read(path / 'proof-v20.json')['errors'])
            # Failed evidence is never silently accepted, even when requested.
            publish = arguments + ['--evidence-from', str(path), '--output', str(path / 'failed.json')]
            result = subprocess.run(publish, capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((path / 'failed.json').exists())
            result = subprocess.run(publish + ['--include-failed'], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(read(path / 'failed.json')['accepted'])
        finally:
            if not path.is_relative_to(scratch.resolve()):
                raise ValueError('Self-test directory leaves its scratch root')
            shutil.rmtree(path)

    def test_option_branch_moved(self):
        host = self.method('Host::Run()', [
            dict(op='ldarg.0', flow='Next', operand=dict(kind='parameter', index=0)),
            dict(op='brfalse', flow='Cond_Branch', operand=dict(kind='branch', target=3)),
            self.call('Siemens::Write()', True), self.ret()])
        host['arguments'] = [dict(name='dryRun', type='System.Boolean'), dict(name='changedOnly', type='System.Boolean')]
        old = Paths([dict(methods=[host])]).build(host['name'])
        host['il'][0]['op'] = 'ldarg.1'
        self.assertNotEqual(old, Paths([dict(methods=[host])]).build(host['name']))

    def scoped_config(self):
        config = self.config()
        config['engine']['methodScopes'] = {'sample.Engine': [dict(method='Run', signature='System.Void sample.Engine::Run()')]}
        config['tools'][0]['entryMethods'] = [dict(type='sample.Engine', method='Run', signature='System.Void sample.Engine::Run()')]
        return config

    def scoped_proof(self, mutation=None):
        config = self.scoped_config()
        # The public tool can itself be outside the migration's include list.
        config['tools'][0]['entryMethods'] = [dict(type='sample.Engine', method='Tool')]
        domain = Domain(config)
        selected, untouched, tool = ('System.Void sample.Engine::' + name for name in ('Run()', 'Run(System.Int32)', 'Tool()'))
        target, local = 'System.Void sample.Primitives::Read()', 'System.Void sample.Local.Primitives::Read()'
        native = self.call('Siemens::Read()', True)
        helper = self.method(untouched, [native, self.call('Siemens::Other()', True), self.ret()])
        entry = self.method(tool, [self.call(selected, definition=selected), self.call(untouched, definition=untouched), self.ret()])
        old = self.method(selected, [native, native, self.ret()])
        default = self.method(selected, [self.call(local, definition=local), self.call(local, definition=local), self.ret()])
        shared = self.method(selected, [self.call(target, definition=target), self.call(target, definition=target), self.ret()])
        primitive, local_primitive = (self.method(name, [native, self.ret()]) for name in (target, local))
        host = self.method('sample.Foundation::Read()', [self.ret()])
        site = dict(caller=selected, member='Siemens::Read()', category='direct', opcode='callvirt')
        untouched_sites = [dict(site, caller=untouched), dict(site, caller=untouched, member='Siemens::Other()')]
        docs = dict(baseline_engine=[old, helper, entry], default_engine=[default, helper, entry, local_primitive],
            shared_engine=[shared, helper, entry], baseline_adapter=[host], current_adapter=[host, primitive])
        inventories = dict(baseline_engine=[site, site] + untouched_sites,
            default_engine=[dict(site, caller=local)] + untouched_sites, shared_engine=untouched_sites,
            baseline_adapter=[], current_adapter=[dict(site, caller=target)])
        if mutation == 'unscoped':
            for variant in ('default_engine', 'shared_engine'):
                docs[variant][1] = self.method(untouched, [self.call('Siemens::Changed()', True), self.ret()])
        elif mutation == 'direct':
            docs['shared_engine'][0] = self.method(selected, [native, self.call(target, definition=target), self.ret()])
            inventories['shared_engine'] = untouched_sites + [site]
        elif mutation == 'new-helper':
            helper_name = 'System.Void sample.Engine::NewHelper()'
            config['engine']['methodScopes']['sample.Engine'].append(dict(method='NewHelper', signature=helper_name))
            for variant in ('default_engine', 'shared_engine'):
                instructions = [dict(op=row['op'], flow=row['flow'], operand=row['operand']) for row in docs[variant][0]['il']]
                docs[variant].append(self.method(helper_name, instructions))
                docs[variant][0] = self.method(selected, [self.call(helper_name, definition=helper_name), self.ret()])
        scratch = ROOT / 'bin-build/shared-native-self-test'
        scratch.mkdir(parents=True, exist_ok=True)
        path = (scratch / uuid.uuid4().hex).resolve()
        path.mkdir()
        try:
            write(path / 'domain.json', config)
            args = [sys.executable, __file__, '--config', str(path / 'domain.json'), '--release', '20', '--output', str(path / 'proof.json')]
            for name, methods in docs.items():
                write(path / (name + '.json'), dict(methods=methods))
                write(path / (name + '-inventory.json'), dict(sites=inventories[name]))
                args += ['--' + name.replace('_', '-'), str(path / (name + '.json')),
                    '--' + name.replace('_', '-') + '-inventory', str(path / (name + '-inventory.json'))]
            result = subprocess.run(args, capture_output=True, text=True)
            self.assertTrue((path / 'proof.json').exists(), result.stdout + result.stderr)
            proof = read(path / 'proof.json')
            self.assertEqual(bool(proof['errors']), bool(result.returncode))
        finally:
            if not path.is_relative_to(scratch.resolve()):
                raise ValueError('Self-test directory leaves its scratch root')
            shutil.rmtree(path)
        return proof, docs, domain

    def test_scoped_method_migrates_and_unscoped_overload_is_unchanged(self):
        proof, _, _ = self.scoped_proof()
        self.assertEqual([], proof['errors'])
        self.assertEqual(2, proof['defaultMethods']['before'])
        self.assertEqual(2, len(proof['enginePaths']))
        delta = proof['defaultExpectedPhysicalDelta'][0]
        self.assertEqual((3, 2), (delta['before'], delta['after']))

    def test_scoped_proof_rejects_unscoped_method_change(self):
        proof, _, _ = self.scoped_proof('unscoped')
        for prefix in ('Default method body differs:', 'Non-domain engine body changed:'):
            self.assertTrue(any(error.startswith(prefix) and 'Run(System.Int32)' in error for error in proof['errors']))

    def test_scoped_proof_rejects_remaining_direct_site(self):
        proof, _, _ = self.scoped_proof('direct')
        self.assertIn('Direct Siemens domain sites remain in the engine', proof['errors'])

    def test_scoped_new_helper_is_explicit_and_expanded(self):
        proof, _, _ = self.scoped_proof('new-helper')
        self.assertEqual([], proof['errors'])

    def test_scoped_tool_graph_follows_unscoped_method(self):
        proof, docs, _ = self.scoped_proof()
        paths = Paths([dict(methods=docs['shared_engine']), dict(methods=docs['current_adapter'])])
        graph = paths.build('System.Void sample.Engine::Tool()')
        self.assertIn('System.Void sample.Engine::Run(System.Int32)', paths.reached)
        self.assertTrue(any('Siemens::Other()' in row[0] for row in graph))
        changed, _, _ = self.scoped_proof('unscoped')
        self.assertTrue(any('Tool()' in error and 'expanded branch graph changed' in error for error in changed['errors']))

    def test_scoped_generated_methods_follow_their_declaring_overload(self):
        domain = Domain(self.scoped_config())
        selected, other = 'System.Void sample.Engine::Run()', 'System.Void sample.Engine::Run(System.Int32)'
        callback = 'System.Void sample.Engine/<>c::<Run>b__0_0()'
        local = 'System.Void sample.Engine::<Run>g__Local|0_0()'
        foreign = 'System.Void sample.Engine/<>c::<Run>b__1_0()'
        constructor = 'System.Void sample.Engine/<>c__DisplayClass0_0::.ctor()'
        state = 'sample.Engine/<Run>d__0'
        generated = [self.method(name, [self.ret()]) for name in (callback, local, foreign, constructor, state + '::MoveNext()', state + '::Dispose()')]
        root = self.method(selected, [self.call(local, definition=local), self.call(constructor, definition=constructor),
            dict(op='ldftn', flow='Next', operand=dict(definition=callback))], states=[state])
        unscoped = self.method(other, [self.call(constructor, definition=constructor),
            dict(op='ldftn', flow='Next', operand=dict(definition=foreign))])
        document = dict(methods=[root, unscoped] + generated)
        scope = EngineScope(domain, document)
        for name in (selected, callback, local, state + '::MoveNext()', state + '::Dispose()'):
            self.assertTrue(scope(dict(caller=name)), name)
        for name in (other, foreign, constructor):
            self.assertFalse(scope(dict(caller=name)), name)
        changed = json.loads(json.dumps(document))
        changed['methods'][4]['semantic'] = 'changed unscoped lambda'
        self.assertEqual([foreign], all_methods(document, changed, scopes=(scope, EngineScope(domain, changed)))[0])

    def test_scoped_wrapper_moves_are_checked_by_inventory(self):
        domain = Domain(self.scoped_config())
        root = self.method('System.Void sample.Engine::Run()', [self.ret()])
        wrapper = self.method('System.Void sample.Engine/__TiaMcpNativeCall_123::Call_123()', [self.ret()])
        before, after = dict(methods=[root, wrapper]), dict(methods=[root])
        scopes = EngineScope(domain, before), EngineScope(domain, after)
        self.assertFalse(scopes[0](wrapper))
        self.assertEqual([], all_methods(before, after, scopes=tuple(scope.body for scope in scopes))[0])
        site = dict(caller=root['name'], member='Siemens::Read()', category='direct', opcode='callvirt')
        self.assertTrue(compare_default_inventory([site], [], domain, scopes)[0])

    def test_mixed_whole_type_and_method_scopes_expand_without_wrappers(self):
        config = self.scoped_config()
        config['engine']['types'].append('sample.OtherEngine')
        domain = Domain(config)
        root = self.method('System.Void sample.Engine::Run()', [self.ret()])
        other = self.method('System.Void sample.OtherEngine::Run()', [self.ret()])
        wrapper = self.method('System.Void sample.OtherEngine/__TiaMcpNativeCall_123::Call_123()', [self.ret()])
        document = dict(methods=[root, other, wrapper])
        scope = EngineScope(domain, document)
        self.assertTrue(scope(other))
        self.assertNotIn(wrapper['name'], scope.entries)
        self.assertEqual(Counter(), expanded_engine_inventory(document, [], domain, scope))

    def test_method_scope_fails_closed_on_invalid_or_missing_signature(self):
        for entries in ([], [dict(method='Run')], [dict(method='Other', signature='System.Void sample.Engine::Run()')]):
            config = self.scoped_config()
            config['engine']['methodScopes']['sample.Engine'] = entries
            with self.assertRaises(ValueError):
                Domain(config)
        domain = Domain(self.scoped_config())
        with self.assertRaisesRegex(ValueError, 'Scoped method missing'):
            EngineScope(domain, dict(methods=[self.method('System.Void sample.Engine::Run(System.Int32)', [self.ret()])]))

    @staticmethod
    def graph(events, branch=False):
        graph = Graph()
        nodes = [graph.node(event) for event in events]
        for i, node in enumerate(nodes):
            graph.nodes[node][1]['next'] = nodes[i+1] if i+1 < len(nodes) else 0
        root = nodes[0] if nodes else 0
        if branch:
            root = graph.node('branch:truth', {'true': root, 'false': 0})
        return graph, root

    def test_equal_with_transparent_calls(self):
        a, root = self.graph(['read', 'write'], True)
        b, other = self.graph(['read', 'write'], True)
        other = b.node('epsilon', {'next': other})
        self.assertEqual(a.canonical(root), b.canonical(other))

    def test_reordered(self):
        a, root = self.graph(['read', 'write'])
        b, other = self.graph(['write', 'read'])
        self.assertNotEqual(a.canonical(root), b.canonical(other))

    def test_missing(self):
        a, root = self.graph(['read', 'write'])
        b, other = self.graph(['read'])
        self.assertNotEqual(a.canonical(root), b.canonical(other))

    def test_extra(self):
        a, root = self.graph(['read'])
        b, other = self.graph(['read', 'write'])
        self.assertNotEqual(a.canonical(root), b.canonical(other))

    def test_branch_moved(self):
        a, root = self.graph(['read'], True)
        b, other = self.graph(['read'], True)
        b.nodes[other][1]['true'], b.nodes[other][1]['false'] = b.nodes[other][1]['false'], b.nodes[other][1]['true']
        self.assertNotEqual(a.canonical(root), b.canonical(other))

    def test_loop_count_and_categories(self):
        a, root = self.graph(['direct:read', 'interface:next'])
        a.nodes[2][1]['next'] = root
        b, other = self.graph(['direct:read', 'interface:next'])
        b.nodes[2][1]['next'] = other
        self.assertEqual(a.canonical(root), b.canonical(other))
        b.nodes[2] = ('direct:next', b.nodes[2][1])
        self.assertNotEqual(a.canonical(root), b.canonical(other))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--config', type=Path, help='Per-domain shared-native JSON configuration')
    parser.add_argument('--release', help='Exact release key for comparison')
    parser.add_argument('--validate-config', action='store_true')
    parser.add_argument('--evidence-from', type=Path, metavar='PROOF_DIRECTORY')
    parser.add_argument('--include-failed', action='store_true', help='Publish failing proof evidence explicitly; exit status remains nonzero')
    parser.add_argument('--dump', type=Path, metavar='ASSEMBLY')
    parser.add_argument('--inventory', type=Path)
    parser.add_argument('--output', type=Path, required=False)
    parser.add_argument('--baseline-engine', type=Path)
    parser.add_argument('--default-engine', type=Path)
    parser.add_argument('--shared-engine', type=Path)
    parser.add_argument('--baseline-adapter', type=Path)
    parser.add_argument('--current-adapter', type=Path)
    parser.add_argument('--baseline-engine-inventory', type=Path)
    parser.add_argument('--default-engine-inventory', type=Path)
    parser.add_argument('--shared-engine-inventory', type=Path)
    parser.add_argument('--baseline-adapter-inventory', type=Path)
    parser.add_argument('--current-adapter-inventory', type=Path)
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return int(not result.wasSuccessful())
    if not args.config:
        parser.error('--config is required outside self-tests')
    domain = Domain(read(args.config))
    if args.validate_config:
        print('PASS domain configuration: ' + domain.name)
        return 0
    if args.evidence_from:
        args.output = args.output or domain.evidence
        releases = {}
        for release in domain.releases:
            proof = read(args.evidence_from / ('proof-v' + release + '.json'))
            if proof.get('acceptanceRule') != domain.acceptance_rule:
                raise ValueError('Rerun proof with the current acceptance rule for V' + release)
            if proof.get('errors') != [] and not args.include_failed:
                raise ValueError('Cannot publish failing proof for V' + release)
            if release in domain.engine_releases and (not proof.get('defaultEnginePaths') or not proof.get('enginePaths')):
                raise ValueError('Incomplete engine proof for V' + release)
            if release in domain.engine_releases and not proof.get('errors') and (
                    proof.get('defaultDeduplicatedInventoryEqual') is not True or
                    proof.get('defaultExpandedFullInventoryEqual') is not True or
                    proof.get('defaultUnexpectedPhysicalDelta') != []):
                raise ValueError('Default exact dedup delta was not verified for V' + release)
            releases[release] = proof
        failures = sum(bool(proof['errors']) for proof in releases.values())
        write(args.output, dict(generator='scripts/checks/Compare-SharedNativePaths.py --evidence-from',
            scope='Static woven IL; labelled native paths, default bodies and exact physical native deltas. No live execution.',
            acceptanceRule=domain.acceptance_rule, accepted=failures == 0, releases=releases))
        print(f'COMPLETE: evidence generated for {len(releases)} releases; {failures} failed')
        return int(bool(failures))
    if args.dump:
        if not args.inventory or not args.output:
            parser.error('--dump requires --inventory and --output directory')
        print(dump(args.dump, args.inventory, args.output, args.config))
        return 0
    if not args.baseline_adapter or not args.current_adapter or not args.output:
        parser.error('comparison requires --baseline-adapter, --current-adapter and --output')
    if args.release not in domain.releases:
        parser.error('--release must name a configured adapter release')
    if bool(args.baseline_engine) != (args.release in domain.engine_releases):
        parser.error('Engine inputs must match the configured engine release surface')
    old_adapter, new_adapter = read(args.baseline_adapter), read(args.current_adapter)
    old_docs, new_docs = [old_adapter], [new_adapter]
    report, errors = dict(acceptanceRule=domain.acceptance_rule), []
    if args.baseline_engine:
        old_engine, default_engine, new_engine = map(read, (args.baseline_engine, args.default_engine, args.shared_engine))
        documents = (old_engine, default_engine, new_engine)
        old_scope, default_scope, new_scope = (EngineScope(domain, doc, documents) for doc in documents)
        old_docs.append(old_engine)
        new_docs.append(new_engine)
        differences, old_count, new_count = all_methods(old_engine, default_engine,
                                                       exclude=domain.local_primitives, scopes=(old_scope.body, default_scope.body))
        raw_differences, _, _ = all_methods(old_engine, default_engine, raw=True,
            exclude=domain.local_primitives, scopes=(old_scope.body, default_scope.body))
        report['defaultMethods'] = dict(before=old_count, after=new_count, differences=differences,
            rawBodyDifferences=len(raw_differences), comparison='IL with metadata references resolved to member/type identities; opcodes, operands, locals and exception regions unchanged')
        errors += ['Default method body differs: ' + name for name in differences]
        old_inv, default_inv = map(read, (args.baseline_engine_inventory, args.default_engine_inventory))
        failures, inventory_report = compare_default_inventory(old_inv['sites'], default_inv['sites'], domain, (old_scope, default_scope))
        errors += failures
        report.update(inventory_report)
        if any(default_scope(s) and s['category'] == 'direct' for s in default_inv['sites']):
            errors.append('Direct Siemens domain sites remain in the default engine')
        report['defaultExpandedFullInventoryEqual'] = member_counts(old_inv['sites']) == expanded_engine_inventory(default_engine, default_inv['sites'], domain, default_scope)
        if not report['defaultExpandedFullInventoryEqual']:
            errors.append('Default expanded full-category weave member multiset changed')
    old, new = Paths(old_docs), Paths(new_docs)
    if args.baseline_engine:
        default_paths = Paths([default_engine, new_adapter])
        failures, rows = compare_host_paths(old, default_paths, domain.engine, old_scope)
        errors += ['Default path: ' + failure for failure in failures]
        report['defaultEnginePaths'] = rows
        unused_local = {normalize(s['caller']) for s in default_inv['sites'] if owned(s, domain.local_primitives)
            and s['category'] == 'direct'} - default_paths.reached
        errors += ['Unexplained local primitive: ' + method for method in sorted(unused_local)]
        failures, rows = compare_host_paths(old, new, domain.engine, old_scope)
        errors += failures
        report['enginePaths'] = rows
        errors += check_tools(domain, rows)
    for host in domain.hosts:
        key = host['name'] + 'Paths'
        if args.release in host['releases']:
            failures, rows = compare_host_paths(old, new, host['types'])
            errors += failures
            report[key] = rows
        else:
            if any(owned(m, host['types']) for doc in (old_adapter, new_adapter) for m in doc['methods']):
                errors.append('Host unexpectedly compiled: ' + host['name'])
            report[key] = domain.label + ' not compiled on this release'
    old_sites, new_sites = [read(p)['sites'] for p in (args.baseline_adapter_inventory, args.current_adapter_inventory)]
    unused = sorted({normalize(s['caller']) for s in new_sites if owned(s, domain.primitives) and s['category'] == 'direct'} - new.reached)
    if unused:
        errors += ['Unexplained native primitive (not reached by either host): ' + method for method in unused]
    # Every pre-existing body outside this domain remains unchanged. Instrumented
    # wrappers are checked through their exact caller/member inventory instead.
    def unchanged_methods(old_document, new_document, allowed=(), scope=lambda row: False):
        existing = {m['name']: m for m in new_document['methods']}
        return [m['name'] for m in old_document['methods'] if '__TiaMcpNativeCall' not in m['owner']
            and not owned(m, allowed) and not scope(m)
            and (m['name'] not in existing or m['semantic'] != existing[m['name']]['semantic'])]
    errors += ['Non-domain adapter body changed: ' + method for method in unchanged_methods(old_adapter, new_adapter, domain.adapter_hosts + domain.mutable_adapter)]
    if outside(old_sites, domain.adapter_hosts + domain.primitives) != outside(new_sites, domain.adapter_hosts + domain.primitives):
        errors.append('Non-domain adapter inventory changed')
    if args.baseline_engine:
        old_engine_sites, new_engine_sites = [read(p)['sites'] for p in (args.baseline_engine_inventory, args.shared_engine_inventory)]
        if outside(old_engine_sites, scope=old_scope) != outside(new_engine_sites, scope=new_scope):
            errors.append('Non-domain engine inventory changed')
        if domain.method_scopes:
            differences, _, _ = all_methods(old_engine, new_engine, scopes=(
                lambda m: old_scope(m) or '__TiaMcpNativeCall' in m['owner'],
                lambda m: new_scope(m) or '__TiaMcpNativeCall' in m['owner']))
        else:
            differences = unchanged_methods(old_engine, new_engine, scope=old_scope)
        errors += ['Non-domain engine body changed: ' + method for method in differences]
        if any(new_scope(s) and s['category'] == 'direct' for s in new_engine_sites):
            errors.append('Direct Siemens domain sites remain in the engine')
        # A member moved into a primitive may not retain another physical copy
        # in any domain host. Existing host-only sites are outside this rule.
        primitives = native_counts([s for s in new_sites if owned(s, domain.primitives)])
        domain_counts = native_counts([s for s in new_sites if owned(s, domain.adapter_hosts + domain.primitives)])
        errors += ['Duplicated shared primitive member: ' + member for member in primitives if domain_counts[member] != 1]
        engine_before, engine_after = native_counts(old_engine_sites), native_counts(new_engine_sites)
    else:
        engine_before = engine_after = Counter()
    adapter_before, adapter_after = native_counts(old_sites), native_counts(new_sites)
    allowed_gains = native_counts([s for s in old_engine_sites if old_scope(s)]) if args.baseline_engine else Counter()
    errors += ['Unexplained adapter native gain: ' + member for member, count in (adapter_after - adapter_before).items()
               if member not in allowed_gains or count > allowed_gains[member]]
    members = sorted(engine_before.keys() | engine_after.keys() | adapter_before.keys() | adapter_after.keys())
    report['expectedNativeDelta'] = [dict(member=member,
        engine=engine_after[member]-engine_before[member], adapter=adapter_after[member]-adapter_before[member],
        union=engine_after[member]+adapter_after[member]-engine_before[member]-adapter_before[member])
        for member in members if engine_before[member] != engine_after[member] or adapter_before[member] != adapter_after[member]]
    report['nativeCounts'] = dict(engineBefore=sum(engine_before.values()), engineAfter=sum(engine_after.values()),
        adapterBefore=sum(adapter_before.values()), adapterAfter=sum(adapter_after.values()))
    report['errors'] = errors
    write(args.output, report)
    for error in errors:
        print('FAIL ' + error)
    print(f'COMPLETE: {len(report.get("enginePaths", []))} engine methods; '
          f'{sum(len(report[h["name"] + "Paths"]) for h in domain.hosts if isinstance(report[h["name"] + "Paths"], list))} host methods; {len(errors)} failed')
    return int(bool(errors))


if __name__ == '__main__':
    raise SystemExit(main())
