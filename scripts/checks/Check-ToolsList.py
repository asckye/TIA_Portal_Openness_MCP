"""Check the guard roster and operation stability without building or contacting TIA.

The released operation baseline is frozen from 3.3.0. Runtime sourceName links
each current/final V4 name to it; taxonomy rules are read from C#, not duplicated.
"""
import argparse
import collections
import json
from pathlib import Path
import re
import runpy
import sys
import unittest
import xml.etree.ElementTree as ET

from engine_sources import EngineSources

ROOT = Path(__file__).resolve().parents[2]
BASELINE = Path(__file__).with_name('write-guard-operations-v3.3.0.json')
# sourceName: (released operation, corrected operation, reason).
EXCEPTIONS = {
    'GetExport': ('FILE', 'READ', 'Reads a parked response in memory; no file I/O.'),
    'ListExports': ('FILE', 'READ', 'Lists in-memory response handles; no file I/O.'),
    'ClearExports': ('FILE', 'WRITE', 'Deletes in-memory response handles, not files.'),
    'DeleteExport': ('FILE', 'WRITE', 'Deletes an in-memory response handle, not a file.'),
}
MERGED = {'GetAuthoringGuide', 'GetRecipe'}


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def operation_classifier(source):
    """Accept only the taxonomy's small, ordered name-rule grammar; changes fail closed."""
    sessions = set(re.findall(r'"([^"]+)"', re.search(r'SessionNames = \{([^}]+)\}', source)[1]))
    body = source.split('if (SessionNames.Contains(name))', 1)[1]
    rules = re.findall(r'if \((.*?)\) return \("([A-Z-]+)", true\);', body)
    predicates = []
    for condition, operation in rules:
        alternatives = []
        for term in condition.split(' || '):
            match = re.fullmatch(r'(Starts|Has)\(("[^"]+"(?:, "[^"]+")*)\)', term)
            if not match:
                raise AssertionError('Unsupported ToolTaxonomy rule: ' + term)
            alternatives.append((match[1], re.findall(r'"([^"]+)"', match[2])))
        predicates.append((alternatives, operation))
    remainder = re.sub(r'if \((.*?)\) return \("([A-Z-]+)", true\);', '', body)
    remainder = re.sub(r'//[^\n]*', '', remainder)
    assert re.fullmatch(r'\s*return \("SESSION", true\);\s*return \("UNSPECIFIED", true\);\s*}\s*}\s*}\s*', remainder), 'Unparsed ToolTaxonomy logic'

    def classify(name, description):
        tag = re.match(r'^\s*\[L\d\]\[(?:Category:)?[^\]]+\](?:\[([A-Za-z-]+)\])?', description)
        if tag and tag[1]:
            return tag[1].upper()
        if name in sessions:
            return 'SESSION'
        for alternatives, operation in predicates:
            if any(any(name.startswith(p) if kind == 'Starts' else p in name for p in parts)
                   for kind, parts in alternatives):
                return operation
        return 'UNSPECIFIED'
    return classify


def compare_names(rows, expected, label):
    counts = collections.Counter(row['name'] for row in rows)
    assert all(count == 1 for count in counts.values()), label + ': duplicate names'
    actual = set(counts)
    assert actual == expected, (label, 'tool list is out of date',
                                'missing', sorted(expected - actual), 'extra', sorted(actual - expected))


def compare_operation(old, actual, baseline):
    expected = baseline[old]
    if old in EXCEPTIONS:
        previous, expected, reason = EXCEPTIONS[old]
        assert previous == baseline[old] and previous != expected and reason.strip(), old
    assert actual == expected, (old, baseline[old], '->', actual, 'expected', expected)


def check(manifest_path):
    generator = runpy.run_path(str(ROOT / 'scripts/generate/Generate-ToolUsage.py'))
    rosters, mapping = generator['registered_rosters'](ROOT)
    manifest = read(manifest_path)
    expected = rosters['20'] | rosters['21']
    compare_names(manifest['tools'], expected, 'V20/V21 union')
    assert manifest['toolCount'] == len(expected), 'toolCount differs from roster'
    listed = {row['name']: row for row in manifest['tools']}
    runtime = json.loads(ET.parse(ROOT / 'src/Logic/ModelContextProtocol/ToolProfiles.resx')
                         .find("./data[@name='Catalog']/value").text)
    baseline = read(BASELINE)['operations']
    assert set(baseline) == set(mapping), '3.3.0 operation baseline coverage differs'
    assert set(EXCEPTIONS) <= set(baseline), 'unused exception'
    taxonomy = (ROOT / 'src/Logic/ModelContextProtocol/ToolTaxonomy.cs').read_text(encoding='utf-8-sig')
    classify = operation_classifier(taxonomy)
    descriptions = {}
    for source in EngineSources(ROOT).sources.values():
        for match in re.finditer(r'\[McpServerTool\(Name\s*=\s*"([^"]+)"', source):
            # Only the leading tags are needed; do not parse prose or method bodies.
            description = re.match(r'\)\s*,\s*Description\(\s*"([^"\n]*)', source[match.end():])
            assert description, ('Missing adjacent Description', match[1])
            assert match[1] not in descriptions, ('Duplicate source registration', match[1])
            descriptions[match[1]] = description[1]
    assert set(descriptions) == expected, 'source descriptions differ from roster'
    seen = set()
    for key in ('20', '21'):
        rows = runtime['releases'][key]
        compare_names([{'name': row['currentName']} for row in rows], rosters[key], 'runtime V' + key)
        for row in rows:
            old, current, final = row['sourceName'], row['currentName'], row['name']
            assert mapping[old] == current, ('Stale runtime sourceName', row)
            description = descriptions[current]
            source_operation = classify(current, description)
            assert listed[current]['operation'] == source_operation, (current, 'stale manifest operation')
            compare_operation(old, source_operation, baseline)
            compare_operation(old, classify(final, description), baseline)
            seen.add(old)
    # The two removed guide aliases share GetToolUsage; their READ category is still checked.
    for old in MERGED:
        compare_operation(old, listed[mapping[old]]['operation'], baseline)
    assert seen | MERGED == set(baseline), ('Uncompared baseline tools', set(baseline) - seen - MERGED)
    print(f'Tool list: V20={len(rosters["20"])}, V21={len(rosters["21"])}, union={len(expected)}; names match.')
    print(f'Operation comparison: {len(baseline)} released tools, {len(seen)} runtime mappings, '
          f'{len(MERGED)} merged guides, {len(EXCEPTIONS)} justified exceptions; 0 unexplained differences.')


class Checks(unittest.TestCase):
    def test_stale_and_duplicate_names(self):
        for rows in ([{'name': 'Old'}], [], [{'name': 'New'}, {'name': 'New'}]):
            with self.assertRaises(AssertionError): compare_names(rows, {'New'}, 'sentinel')
        compare_names([{'name': 'New'}], {'New'}, 'sentinel')

    def test_category_downgrade(self):
        with self.assertRaises(AssertionError): compare_operation('Renamed', 'WRITE', {'Renamed': 'ONLINE-WRITE'})

    def test_rules_and_unknown_grammar(self):
        source = (ROOT / 'src/Logic/ModelContextProtocol/ToolTaxonomy.cs').read_text(encoding='utf-8-sig')
        classify = operation_classifier(source)
        self.assertEqual(classify('DownloadSentinel', '[L1][PLC-Online]'), 'ONLINE-WRITE')
        self.assertEqual(classify('Anything', '[L2][PLC-Online][ONLINE-WRITE]'), 'ONLINE-WRITE')
        with self.assertRaises(AssertionError): operation_classifier(source.replace('Starts("Compile", "Run")', 'OtherRule(name)'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, default=ROOT / 'manifest/tools-list.json')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        sys.exit(not unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(Checks)).wasSuccessful())
    try:
        check(args.manifest)
    except (AssertionError, KeyError, ValueError) as error:
        print('FAIL:', error)
        sys.exit(1)
