"""Compare two NativeCallWeaver verify inventories for the CFC service move.

Keep multiplicity globally and IL-offset order within each moved method body.
Fold compiler-generated lambda bodies into their source method, retaining each
lambda's local ordinal so equal global multisets cannot hide reordered bodies.
Compiler-wide closure/type numbers, site IDs and absolute IL offsets may change.
This is a static site-order proof, not a live trace of conditional execution.
"""
import argparse
from collections import Counter, defaultdict
import copy
import json
from pathlib import Path
import re
import unittest

BEFORE = 'TiaMcpServer.Siemens.Portal'
AFTER = 'TiaMcpServer.Siemens.Services.CfcService'
METHODS = ('RequireChartProvider', 'CfcInventoryRow', 'CfcPreflight', 'RequireCfcCharts',
           'ExchangeCfcCharts', 'ManageCfcChartProtection')


def family(site, owner):
    caller = site['caller']
    match = re.search(r' ' + re.escape(owner) + r'(?P<nested>/[^ ]+)?::(?P<method>[^ (]+)\(', caller)
    if not match:
        return None
    method = match['method']
    if not match['nested'] and method in METHODS:
        return method, -1
    generated = re.fullmatch(r'<([^>]+)>b__(?:\d+_)?(\d+)', method)
    if match['nested'] and generated and generated[1] in METHODS:
        return generated[1], int(generated[2])
    return None


def sequences(sites, owner):
    bodies = defaultdict(list)
    found = set()
    for site in sites:
        key = family(site, owner)
        if key is not None:
            found.add(key[0])
            if site['category'] == 'direct':
                bodies[key].append(site)
        elif site['category'] == 'direct' and (' ' + owner + '/') in site['caller']:
            # Portal has other domains; the new service must have no unaccounted native body.
            if owner == AFTER:
                raise ValueError('Unmapped CFC lambda: ' + site['caller'])
    if found != set(METHODS):
        raise ValueError('Missing moved method families: ' + str(sorted(set(METHODS) - found)))
    result = {method: [] for method in METHODS}
    for (method, ordinal), rows in sorted(bodies.items()):
        result[method].extend((ordinal, row['opcode'], row['member'])
                              for row in sorted(rows, key=lambda row: row['offset']))
    return result


def compare(before, after):
    old_members = Counter(site['member'] for site in before if site['category'] == 'direct')
    new_members = Counter(site['member'] for site in after if site['category'] == 'direct')
    errors = []
    if old_members != new_members:
        errors.append(f'Siemens multiset changed: removed={old_members-new_members}, added={new_members-old_members}')
    old, new = sequences(before, BEFORE), sequences(after, AFTER)
    for method in METHODS:
        if old[method] != new[method]:
            errors.append(f'{method}: ordered Siemens sites changed\nbefore={old[method]}\nafter={new[method]}')
    return errors, old, new, sum(old_members.values()), sum(new_members.values())


class SelfTests(unittest.TestCase):
    def inventories(self):
        before = [dict(caller=f'void {BEFORE}::{name}()', offset=0, opcode='call',
                       member='Other::Member()', category='enumeration-input') for name in METHODS]
        for ordinal in (0, 1, 2):
            for offset in (10, 20):
                before.append(dict(caller=f'void {BEFORE}/<>c__DisplayClass121_0::<ManageCfcChartProtection>b__{ordinal}()',
                    offset=offset, opcode='callvirt', member=f'void Siemens.Engineering.Chart::M{offset}()', category='direct'))
        after = copy.deepcopy(before)
        for row in after:
            row['caller'] = row['caller'].replace(BEFORE, AFTER).replace('Class121_', 'Class8_')
            row['offset'] += 5
        return before, after

    def test_move_and_offset_changes(self):
        self.assertEqual([], compare(*self.inventories())[0])

    def test_reordering_with_same_multiset(self):
        before, after = self.inventories()
        after[-1]['offset'], after[-2]['offset'] = after[-2]['offset'], after[-1]['offset']
        self.assertTrue(compare(before, after)[0])

    def test_missing_or_duplicate_site(self):
        for duplicate in (False, True):
            before, after = self.inventories()
            if duplicate:
                after.append(copy.deepcopy(after[-1]))
            else:
                after.pop()
            self.assertTrue(compare(before, after)[0])

    def test_reassignment_between_lambdas(self):
        before, after = self.inventories()
        after[-1]['caller'] = after[-1]['caller'].replace('b__2', 'b__1')
        self.assertTrue(compare(before, after)[0])

    def test_missing_family(self):
        before, after = self.inventories()
        with self.assertRaises(ValueError):
            compare(before, after[1:])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--current', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return int(not result.wasSuccessful())
    if not args.baseline or not args.current:
        parser.error('--baseline and --current are required')
    errors, before, after, old_count, new_count = compare(
        json.loads(args.baseline.read_text(encoding='utf-8'))['sites'],
        json.loads(args.current.read_text(encoding='utf-8'))['sites'])
    print(f'Siemens member multiset: before={old_count}, after={new_count}')
    for method in METHODS:
        print(f'{method}: before={len(before[method])}, after={len(after[method])}, equal={before[method] == after[method]}')
        for ordinal, opcode, member in after[method]:
            print(f'  {"body" if ordinal == -1 else "lambda " + str(ordinal)}: {opcode} {member}')
    for error in errors:
        print('FAIL ' + error)
    print(f'COMPLETE: {7-len(errors)} checks passed; {len(errors)} failed')
    return int(bool(errors))


if __name__ == '__main__':
    raise SystemExit(main())
