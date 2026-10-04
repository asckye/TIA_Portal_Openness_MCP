"""Compare woven VCI paths without loading Siemens or executing native code.

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
import subprocess
import sys
import unittest
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
ENGINE = 'TiaMcpServer.Siemens.Services.VersionControlService'
STUDIO = 'TiaOpenness.Openness.OpennessVersionControl'
PRIMITIVE = 'TiaOpenness.Openness.VersionControlPrimitives'
LOCAL_PRIMITIVE = 'TiaMcpServer.Siemens.LocalVci.VersionControlPrimitives'
BORROWED = 'TiaMcp.Adapters.PlcServices'
sys.setrecursionlimit(20000)
TOOLS = ('GetVersionControlWorkspaces', 'CreateVersionControlWorkspace', 'GetVersionControlStatus',
         'SyncVersionControlWorkspace', 'ConnectProjectToWorkspace')
ACCEPTANCE_RULE = dict(
    id='expanded-native-paths-exact-dedup-v1',
    paths='Default and switched per-tool expanded native call graphs must equal the baseline.',
    default='The full-category member multiset must equal the baseline after collapsing each direct VCI member into one local primitive site; no other delta is allowed.',
    shared='Engine VCI direct sites move to deduplicated adapter primitives; only engine-required new members may be gained, with no change outside VCI.',
    evidence='Generate per-member counts and declaring-method moves; physical equality before deduplication is informational.')


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
    return row.get('owner', row.get('caller', '')).startswith(prefix) or (' ' + prefix) in row.get('caller', '')


def native_counts(sites):
    return Counter(row['member'] for row in sites if row['category'] == 'direct')


def member_counts(sites):
    return Counter((row['category'], row['opcode'], row['member']) for row in sites)


def moved_members(before, after, prefixes):
    # Keep every category and every original/current declaring caller in the
    # generated evidence; repeated sites in one method retain their count.
    selected = lambda rows: [row for row in rows if any(owner(row, prefix) for prefix in prefixes)]
    old, new = selected(before), selected(after)
    keys = sorted(member_counts(old).keys() | member_counts(new).keys())
    return [dict(category=category, opcode=opcode, member=member,
        before=dict(sorted(Counter(row['caller'] for row in old
            if (row['category'], row['opcode'], row['member']) == (category, opcode, member)).items())),
        after=dict(sorted(Counter(row['caller'] for row in new
            if (row['category'], row['opcode'], row['member']) == (category, opcode, member)).items())))
        for category, opcode, member in keys]


def expanded_engine_inventory(document, sites):
    methods = Paths([document]).methods
    result = member_counts([row for row in sites if not owner(row, LOCAL_PRIMITIVE)])

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
            elif target in methods and methods[target]['owner'].startswith(LOCAL_PRIMITIVE):
                counts.update(inline(target, (*active, key)))
        return counts

    # Expand each original service call site once, not every path from every
    # public tool; existing engine helper methods already have their own sites.
    for method in methods.values():
        if not method['owner'].startswith(ENGINE):
            continue
        for instruction in method['il']:
            target = normalize(instruction['operand'].get('definition') or '')
            if instruction['flow'] == 'Call' and target in methods and methods[target]['owner'].startswith(LOCAL_PRIMITIVE):
                result.update(inline(target))
    return result


def expected_local_inventory(sites):
    moved = member_counts([row for row in sites if owner(row, ENGINE) and row['category'] == 'direct'])
    return member_counts(sites) - moved + Counter({key: 1 for key in moved})


def compare_default_inventory(before, after):
    # Derive the only allowed delta from the baseline, never from current sites.
    old, current, expected = member_counts(before), member_counts(after), expected_local_inventory(before)
    moves = moved_members(before, after, (ENGINE, LOCAL_PRIMITIVE))
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
    errors = [] if expected == current else ['Default physical inventory differs from the exact VCI deduplication delta']
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


def compare_paths(before, after, prefix):
    errors, evidence = [], []
    originals = sorted(key for key, method in before.methods.items() if method['owner'].split('/')[0] == prefix)
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


def all_methods(before, after, raw=False, exclude=()):
    select = lambda doc: {m['name']: m['raw' if raw else 'semantic'] for m in doc['methods']
        if not any(m['owner'].startswith(prefix) for prefix in exclude)}
    old, new = select(before), select(after)
    return [key for key in sorted(old.keys() | new.keys()) if old.get(key) != new.get(key)], len(old), len(new)


def dump(assembly, inventory, directory):
    directory = Path(directory).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    tool = ROOT / 'bin-build/vci-il-reader'
    tool.mkdir(parents=True, exist_ok=True)
    cecil = ROOT / 'tools/native-call-weaver/bin/Release/net8.0/Mono.Cecil.dll'
    if not cecil.is_file():
        raise ValueError('Build NativeCallWeaver first: ' + str(cecil))
    project = tool / 'Reader.csproj'
    if not project.exists():
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework>'
            '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit>'
            '</PropertyGroup><ItemGroup><Compile Include="' + escape(str(ROOT / 'scripts/checks/VciIlReader.cs')) + '" />'
            '<Reference Include="Mono.Cecil"><HintPath>' + escape(str(cecil)) + '</HintPath></Reference>'
            '</ItemGroup></Project>', encoding='utf-8')
    reader = tool / 'bin/Release/net8.0/Reader.dll'
    if not reader.exists() or reader.stat().st_mtime < max(project.stat().st_mtime,
            (ROOT / 'scripts/checks/VciIlReader.cs').stat().st_mtime, cecil.stat().st_mtime):
        subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '-m:1', '-nr:false',
                        '-p:RestoreSources=' + str(tool), '-v:q'], check=True)
    output = directory / (Path(assembly).name + '.il.json')
    subprocess.run(['dotnet', str(reader), str(Path(assembly).resolve()),
                    str(Path(inventory).resolve()), str(output)], check=True)
    return output


class SelfTests(unittest.TestCase):
    @staticmethod
    def method(name, instructions, states=(), handlers=()):
        return dict(name=name, owner=name.split('::')[0], semantic=name, il=[dict(offset=i, **row)
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

    def test_default_non_vci_body_change_is_failure(self):
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
        row = dict(caller=ENGINE + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        moved = dict(row, caller=LOCAL_PRIMITIVE + '::Read()')
        self.assertEqual(member_counts([row]), member_counts([moved]))
        self.assertNotEqual(member_counts([row, row]), member_counts([moved]))
        self.assertNotEqual(member_counts([row]), member_counts([dict(moved, category='interface-dispatch')]))
        evidence = moved_members([row, row], [moved], (ENGINE, LOCAL_PRIMITIVE))
        self.assertEqual(2, evidence[0]['before'][row['caller']])
        self.assertEqual(1, evidence[0]['after'][moved['caller']])

    def test_expanded_inventory_retains_each_engine_call_site(self):
        primitive = self.method(LOCAL_PRIMITIVE + '::Read()', [self.call('Siemens::Read()', True), self.ret()])
        host = self.method(ENGINE + '::Run()', [self.call(primitive['name'], definition=primitive['name']),
            self.call(primitive['name'], definition=primitive['name']), self.ret()])
        site = dict(caller=primitive['name'], category='direct', opcode='callvirt', member='Siemens::Read()')
        physical = member_counts([site])
        expanded = expanded_engine_inventory(dict(methods=[host, primitive]), [site])
        self.assertEqual(1, sum(physical.values()))
        self.assertEqual(2, sum(expanded.values()))

    def test_default_exact_dedup_delta_is_accepted(self):
        site = dict(caller=ENGINE + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        moved = dict(site, caller=LOCAL_PRIMITIVE + '::Read()')
        untouched = dict(site, caller='Other::Run()', category='interface-dispatch')
        errors, report = compare_default_inventory([site, site, untouched], [moved, untouched])
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
        site = dict(caller=ENGINE + '::Run()', category='direct', opcode='callvirt', member='Siemens::Read()')
        moved = dict(site, caller=LOCAL_PRIMITIVE + '::Read()')
        untouched = dict(site, caller='Other::Run()', category='enumeration-input', member='Enumerable::ToList()')
        before, valid = [site, site, untouched], [moved, untouched]
        mutations = dict(extra=valid + [dict(moved, member='Siemens::Unexpected()')],
            missing=[untouched], duplicated=valid + [moved], unrelated_missing=[moved],
            unrelated_extra=valid + [untouched], changed_category=[dict(moved, category='interface-dispatch'), untouched],
            changed_dispatch=[dict(moved, opcode='call'), untouched])
        for mutation, after in mutations.items():
            with self.subTest(mutation=mutation):
                errors, report = compare_default_inventory(before, after)
                self.assertTrue(errors)
                self.assertFalse(report['defaultDeduplicatedInventoryEqual'])
                self.assertTrue(report['defaultUnexpectedPhysicalDelta'])

    def test_option_branch_moved(self):
        host = self.method('Host::Run()', [
            dict(op='ldarg.0', flow='Next', operand=dict(kind='parameter', index=0)),
            dict(op='brfalse', flow='Cond_Branch', operand=dict(kind='branch', target=3)),
            self.call('Siemens::Write()', True), self.ret()])
        host['arguments'] = [dict(name='dryRun', type='System.Boolean'), dict(name='changedOnly', type='System.Boolean')]
        old = Paths([dict(methods=[host])]).build(host['name'])
        host['il'][0]['op'] = 'ldarg.1'
        self.assertNotEqual(old, Paths([dict(methods=[host])]).build(host['name']))

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
    if args.evidence_from:
        if not args.output:
            parser.error('--evidence-from requires --output')
        releases = {}
        for release in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21'):
            proof = read(args.evidence_from / ('proof-v' + release + '.json'))
            if proof.get('acceptanceRule') != ACCEPTANCE_RULE:
                raise ValueError('Rerun proof with the current acceptance rule for V' + release)
            if proof.get('errors') != [] and not args.include_failed:
                raise ValueError('Cannot publish failing proof for V' + release)
            if release in ('20', '21') and (not proof.get('defaultEnginePaths') or not proof.get('enginePaths')):
                raise ValueError('Incomplete engine proof for V' + release)
            if release in ('20', '21') and not proof.get('errors') and (
                    proof.get('defaultDeduplicatedInventoryEqual') is not True or
                    proof.get('defaultExpandedFullInventoryEqual') is not True or
                    proof.get('defaultUnexpectedPhysicalDelta') != []):
                raise ValueError('Default exact dedup delta was not verified for V' + release)
            releases[release] = proof
        failures = sum(bool(proof['errors']) for proof in releases.values())
        write(args.output, dict(generator='scripts/checks/Compare-VciNativePaths.py --evidence-from',
            scope='Static woven IL; labelled native paths, default bodies and exact physical native deltas. No live execution.',
            acceptanceRule=ACCEPTANCE_RULE, accepted=failures == 0, releases=releases))
        print(f'COMPLETE: evidence generated for 8 releases; {failures} failed')
        return int(bool(failures))
    if args.dump:
        if not args.inventory or not args.output:
            parser.error('--dump requires --inventory and --output directory')
        print(dump(args.dump, args.inventory, args.output))
        return 0
    if not args.baseline_adapter or not args.current_adapter or not args.output:
        parser.error('comparison requires --baseline-adapter, --current-adapter and --output')
    old_adapter, new_adapter = read(args.baseline_adapter), read(args.current_adapter)
    old_docs, new_docs = [old_adapter], [new_adapter]
    report, errors = dict(acceptanceRule=ACCEPTANCE_RULE), []
    if args.baseline_engine:
        old_engine, default_engine, new_engine = map(read, (args.baseline_engine, args.default_engine, args.shared_engine))
        old_docs.append(old_engine)
        new_docs.append(new_engine)
        differences, old_count, new_count = all_methods(old_engine, default_engine,
                                                       exclude=(ENGINE, LOCAL_PRIMITIVE))
        raw_differences, _, _ = all_methods(old_engine, default_engine, raw=True, exclude=(ENGINE, LOCAL_PRIMITIVE))
        report['defaultMethods'] = dict(before=old_count, after=new_count, differences=differences,
            rawBodyDifferences=len(raw_differences), comparison='IL with metadata references resolved to member/type identities; opcodes, operands, locals and exception regions unchanged')
        errors += ['Default method body differs: ' + name for name in differences]
        old_inv, default_inv = map(read, (args.baseline_engine_inventory, args.default_engine_inventory))
        failures, inventory_report = compare_default_inventory(old_inv['sites'], default_inv['sites'])
        errors += failures
        report.update(inventory_report)
        report['defaultExpandedFullInventoryEqual'] = member_counts(old_inv['sites']) == expanded_engine_inventory(default_engine, default_inv['sites'])
        if not report['defaultExpandedFullInventoryEqual']:
            errors.append('Default expanded full-category weave member multiset changed')
    old, new = Paths(old_docs), Paths(new_docs)
    if args.baseline_engine:
        default_paths = Paths([default_engine, new_adapter])
        failures, rows = compare_paths(old, default_paths, ENGINE)
        errors += ['Default path: ' + failure for failure in failures]
        report['defaultEnginePaths'] = rows
        unused_local = {normalize(s['caller']) for s in default_inv['sites'] if owner(s, LOCAL_PRIMITIVE)
            and s['category'] == 'direct'} - default_paths.reached
        errors += ['Unexplained local primitive: ' + method for method in sorted(unused_local)]
        failures, rows = compare_paths(old, new, ENGINE)
        errors += failures
        report['enginePaths'] = rows
        for tool in TOOLS:
            if not any('::' + tool + '(' in row['method'] for row in rows):
                errors.append('VCI tool was not checked: ' + tool)
    if any(owner(m, STUDIO) for m in old_adapter['methods']):
        failures, rows = compare_paths(old, new, STUDIO)
        errors += failures
        report['studioPaths'] = rows
    else:
        report['studioPaths'] = 'VCI not compiled on this release'
    old_sites, new_sites = [read(p)['sites'] for p in (args.baseline_adapter_inventory, args.current_adapter_inventory)]
    unused = sorted({normalize(s['caller']) for s in new_sites if owner(s, PRIMITIVE) and s['category'] == 'direct'} - new.reached)
    if unused:
        errors += ['Unexplained native primitive (not reached by either host): ' + method for method in unused]
    # Every pre-existing body outside this domain remains unchanged. Instrumented
    # wrappers are checked through their exact caller/member inventory instead.
    def unchanged_methods(old_document, new_document, allowed):
        existing = {m['name']: m for m in new_document['methods']}
        return [m['name'] for m in old_document['methods'] if '__TiaMcpNativeCall' not in m['owner']
            and not any(m['owner'].startswith(prefix) for prefix in allowed)
            and (m['name'] not in existing or m['semantic'] != existing[m['name']]['semantic'])]
    errors += ['Non-VCI adapter body changed: ' + method for method in unchanged_methods(old_adapter, new_adapter, (STUDIO, BORROWED))]
    outside = lambda sites, prefixes: Counter((s['caller'], s['category'], s['opcode'], s['member']) for s in sites
        if not any(owner(s, prefix) for prefix in prefixes))
    if outside(old_sites, (STUDIO, PRIMITIVE)) != outside(new_sites, (STUDIO, PRIMITIVE)):
        errors.append('Non-VCI adapter inventory changed')
    if args.baseline_engine:
        old_engine_sites, new_engine_sites = [read(p)['sites'] for p in (args.baseline_engine_inventory, args.shared_engine_inventory)]
        if outside(old_engine_sites, (ENGINE,)) != outside(new_engine_sites, (ENGINE,)):
            errors.append('Non-VCI engine inventory changed')
        errors += ['Non-VCI engine body changed: ' + method for method in unchanged_methods(old_engine, new_engine, (ENGINE,))]
        if any(owner(s, ENGINE) and s['category'] == 'direct' for s in new_engine_sites):
            errors.append('Direct Siemens VCI sites remain in the engine')
        # A member moved into a primitive may not retain another physical copy
        # in either VCI host. Existing Studio-only sites are outside this rule.
        primitives = native_counts([s for s in new_sites if owner(s, PRIMITIVE)])
        domain = native_counts([s for s in new_sites if owner(s, STUDIO) or owner(s, PRIMITIVE)])
        errors += ['Duplicated shared primitive member: ' + member for member in primitives if domain[member] != 1]
        engine_before, engine_after = native_counts(old_engine_sites), native_counts(new_engine_sites)
    else:
        engine_before = engine_after = Counter()
    adapter_before, adapter_after = native_counts(old_sites), native_counts(new_sites)
    allowed_gains = native_counts([s for s in old_engine_sites if owner(s, ENGINE)]) if args.baseline_engine else Counter()
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
          f'{len(report["studioPaths"]) if isinstance(report["studioPaths"], list) else 0} Studio methods; {len(errors)} failed')
    return int(bool(errors))


if __name__ == '__main__':
    raise SystemExit(main())
