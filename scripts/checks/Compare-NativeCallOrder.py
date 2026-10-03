"""Compare NativeCallWeaver verify inventories across Portal/McpServer domain moves.

Discover disappeared source method families and match newly appearing service/tool
families by name. Compare the global Siemens member multiset and each moved body's
ordered direct sites, folding lambdas and local functions into their source method.
Only compiler-wide closure ordinals and absolute IL offsets are ignored. This is
a static site-order check, not a live trace; methods without inventory sites need
the separate source/dispatch checks used by the domain migration template.
"""
import argparse
from collections import Counter, defaultdict
import copy
import json
from pathlib import Path
import re
import unittest

SOURCES = {'TiaMcpServer.Siemens.Portal', 'TiaMcpServer.ModelContextProtocol.McpServer'}
PORTAL = 'TiaMcpServer.Siemens.Portal'
SERVICE = 'TiaMcpServer.Siemens.Services.ExampleService'


def destination(owner):
    return (owner.startswith('TiaMcpServer.Siemens.Services.') or
            owner.startswith('TiaMcpServer.ModelContextProtocol.') and owner.endswith('Tools'))


def family(caller):
    match = re.search(r' (TiaMcpServer\.[^ ]+)::([^ (]+)\((.*)\)$', caller)
    if not match:
        return None
    declared, method, parameters = match.groups()
    owner = declared.split('/')[0]
    if owner not in SOURCES and not destination(owner):
        return None
    generated = re.fullmatch(r'<([^>]+)>b__(?:\d+_)?(\d+)', method)
    local = re.fullmatch(r'<([^>]+)>g__(.+)\|(?:\d+_)?(\d+)', method)
    state = re.fullmatch(re.escape(owner) + r'/<([^>]+)>d__\d+', declared)
    local_state = re.search(r'/<<([^>]+)>g__(.+)\|(?:\d+_)?(\d+)>d(?:__\d+)?$', declared)
    if generated:
        name, ordinal = generated.groups()
        body = 'lambda ' + ordinal
    elif local:
        name, function, ordinal = local.groups()
        body = 'local ' + function + ' ' + ordinal
    elif state:
        name, body = state[1], 'state ' + method
    elif local_state:
        name, function, ordinal = local_state.groups()
        body = 'local ' + function + ' ' + ordinal + ' state ' + method
    elif '/<' in declared or method.startswith('<'):
        raise ValueError('Unmapped generated native body: ' + caller)
    else:
        name = declared[len(owner) + 1:] + '.' + method if '/' in declared else method
        body = 'body'
    # Retain overload signatures and closure-local indices, not compilation-wide IDs.
    parameters = parameters.replace(owner, '<owner>')
    parameters = re.sub(r'<>c__DisplayClass\d+_', '<>c__DisplayClass#_', parameters)
    return (owner, name), (body, parameters)


def sequences(sites):
    families = defaultdict(lambda: defaultdict(list))
    for site in sites:
        key = family(site['caller'])
        if key is None:
            continue
        method, body = key
        bodies = families[method]  # Non-direct sites still establish method existence.
        if site['category'] == 'direct':
            bodies[body].append(site)
    return {method: [(body, row['opcode'], row['member'])
                     for body, rows in sorted(bodies.items())
                     for row in sorted(rows, key=lambda item: item['offset'])]
            for method, bodies in families.items()}


def label(method):
    return '::'.join(method)


def compare(before, after, allowed=()):
    old_members = Counter(site['member'] for site in before if site['category'] == 'direct')
    new_members = Counter(site['member'] for site in after if site['category'] == 'direct')
    errors = []
    if old_members != new_members:
        errors.append(f'Siemens multiset changed: removed={old_members-new_members}, added={new_members-old_members}')
    old, new = sequences(before), sequences(after)
    disappeared = {key for key in old.keys() - new.keys() if key[0] in SOURCES}
    appeared = {key for key in new.keys() - old.keys() if destination(key[0])}
    allowed = set(allowed)
    used = set()
    pairs = []
    for source in sorted(disappeared):
        targets = sorted(key for key in appeared if key[1] == source[1])
        if len(targets) != 1:
            if label(source) not in allowed:
                errors.append(f'{label(source)}: expected one destination, found {list(map(label, targets))}')
            continue
        target = targets[0]
        if target in used:
            errors.append(f'{label(target)}: matched more than one source')
            continue
        used.add(target)
        pairs.append((source, target, old[source], new[target]))
        if old[source] != new[target]:
            errors.append(f'{label(source)} -> {label(target)}: ordered Siemens sites changed\n'
                          f'before={old[source]}\nafter={new[target]}')
    for target in sorted(appeared - used):
        if label(target) not in allowed:
            errors.append('Unmatched destination: ' + label(target))
    for item in sorted(allowed - {label(key) for key in disappeared | appeared}):
        errors.append('Unused unmatched exception: ' + item)
    return errors, pairs, sum(old_members.values()), sum(new_members.values())


class SelfTests(unittest.TestCase):
    def inventories(self):
        before = [dict(caller=f'void {PORTAL}::Read()', offset=0, opcode='call',
                       member='Other::Member()', category='enumeration-input')]
        for method in ('<Read>b__121_0', '<Read>b__121_1', '<Read>g__State|121_2'):
            for offset in (10, 20):
                before.append(dict(caller=f'void {PORTAL}/<>c::{method}()', offset=offset,
                    opcode='callvirt', member=f'void Siemens.Engineering.Chart::M{offset}()', category='direct'))
        after = copy.deepcopy(before)
        for row in after:
            row['caller'] = row['caller'].replace(PORTAL, SERVICE).replace('121_', '8_')
            row['offset'] += 5
        return before, after

    def test_move_and_offsets(self):
        errors, pairs, old, new = compare(*self.inventories())
        self.assertEqual([], errors)
        self.assertEqual((1, 6, 6), (len(pairs), old, new))

    def test_reordering_same_multiset(self):
        before, after = self.inventories()
        after[-1]['offset'], after[-2]['offset'] = after[-2]['offset'], after[-1]['offset']
        self.assertTrue(compare(before, after)[0])

    def test_missing_and_duplicate_site(self):
        for duplicate in (False, True):
            before, after = self.inventories()
            if duplicate:
                after.append(copy.deepcopy(after[-1]))
            else:
                after.pop()
            self.assertTrue(compare(before, after)[0])

    def test_reassignment_between_lambdas(self):
        before, after = self.inventories()
        after[1]['caller'] = after[1]['caller'].replace('8_0', '8_1')
        self.assertTrue(compare(before, after)[0])

    def test_missing_local_function(self):
        before, after = self.inventories()
        self.assertTrue(compare(before, after[:-2])[0])

    def test_unmatched_and_explicit_exception(self):
        before = [dict(caller=f'void {PORTAL}::Old()', offset=0, opcode='call',
                       member='Other::Member()', category='enumeration-input')]
        self.assertTrue(compare(before, [])[0])
        self.assertEqual([], compare(before, [], [PORTAL + '::Old'])[0])
        self.assertTrue(compare([], [], [PORTAL + '::Old'])[0])

    def test_ambiguous_destination(self):
        before, after = self.inventories()
        duplicate = dict(after[0], caller=after[0]['caller'].replace('ExampleService', 'OtherService'))
        self.assertTrue(compare(before, after + [duplicate])[0])

    def test_tool_move(self):
        before, after = self.inventories()
        for row in before:
            row['caller'] = row['caller'].replace(PORTAL, 'TiaMcpServer.ModelContextProtocol.McpServer')
        for row in after:
            row['caller'] = row['caller'].replace(SERVICE, 'TiaMcpServer.ModelContextProtocol.ExampleTools')
        self.assertEqual([], compare(before, after)[0])

    def test_retained_kernel_is_not_a_move(self):
        before, _ = self.inventories()
        self.assertEqual([], compare(before, before)[1])

    def test_unchanged_existing_service_is_not_a_destination(self):
        before, after = self.inventories()
        self.assertTrue(compare(before + after, after)[0])

    def test_unmapped_generated_body_is_error(self):
        before, after = self.inventories()
        after[1]['caller'] = after[1]['caller'].replace('<Read>b__8_0', '<Read>unknown')
        with self.assertRaisesRegex(ValueError, 'Unmapped generated'):
            compare(before, after)

    def test_nested_local_and_iterator_bodies(self):
        for declared, method in (
                ('/<>c__DisplayClass121_0', '<Read>g__Rows|1'),
                ('/<>c__DisplayClass121_0/<<Read>g__Rows|1>d', 'MoveNext'),
                ('/<Read>d__121', 'MoveNext'),
                ('/Nested', 'Get')):
            before = [dict(caller=f'void {PORTAL}{declared}::{method}()', offset=10,
                           opcode='call', member='void Siemens.Engineering.Project::Close()', category='direct')]
            after = [dict(before[0], caller=before[0]['caller'].replace(PORTAL, SERVICE).replace('121', '8'))]
            self.assertEqual([], compare(before, after)[0])
            self.assertEqual(1, len(compare(before, after)[1]))

    def test_overload_signatures_are_retained(self):
        before, after = self.inventories()
        for signature in ('System.String', 'System.Int32'):
            before.append(dict(caller=f'void {PORTAL}::Overload({signature})', offset=0,
                opcode='call', member=f'void Siemens.Engineering.Project::Read({signature})', category='direct'))
            after.append(dict(before[-1], caller=before[-1]['caller'].replace(PORTAL, SERVICE)))
        self.assertEqual([], compare(before, after)[0])
        after[-1]['caller'], after[-2]['caller'] = after[-2]['caller'], after[-1]['caller']
        self.assertTrue(compare(before, after)[0])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--current', type=Path)
    parser.add_argument('--allow-unmatched', action='append', default=[], metavar='TYPE::METHOD',
                        help='Explicit inventory-family exception; repeat for each reviewed method')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return int(not result.wasSuccessful())
    if not args.baseline or not args.current:
        parser.error('--baseline and --current are required')
    try:
        errors, pairs, old_count, new_count = compare(
            json.loads(args.baseline.read_text(encoding='utf-8-sig'))['sites'],
            json.loads(args.current.read_text(encoding='utf-8-sig'))['sites'], args.allow_unmatched)
    except (OSError, ValueError, KeyError) as error:
        print('FAIL ' + str(error))
        return 1
    print(f'Siemens member multiset: before={old_count}, after={new_count}')
    for source, target, before, after in pairs:
        print(f'{label(source)} -> {label(target)}: before={len(before)}, after={len(after)}, equal={before == after}')
        for body, opcode, member in after:
            print(f'  {body}: {opcode} {member}')
    for error in errors:
        print('FAIL ' + error)
    print(f'COMPLETE: moved={len(pairs)}, equal={sum(a == b for _, _, a, b in pairs)}; {len(errors)} failed')
    return int(bool(errors))


if __name__ == '__main__':
    raise SystemExit(main())
