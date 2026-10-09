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
    'SaveAsProject': ('FILE', 'SESSION', 'TIA Save As switches the open project and MCP binding (P7-07b finding 48).'),
}
MERGED = {'GetAuthoringGuide', 'GetRecipe'}
# P7-04 additions to the V20/V21 product, absent from the frozen engine baseline.
# These are offline project operations/readers; none contacts a controller/runtime.
NEW_FOUNDATION_TOOLS = {
    'CreatePlcTag': ('CreatePlcTag', 'WRITE', 'Creates a tag in the bound offline PLC project.'),
    'CreatePlcTagTable': ('CreatePlcTagTable', 'WRITE', 'Creates a tag table in the bound offline PLC project.'),
    'CreatePlcUserConstant': ('CreatePlcUserConstant', 'WRITE', 'Creates a user constant in the bound offline PLC project.'),
    'GetPortalConnectionReadiness': ('DiagnosePortalConnectReadiness', 'READ', 'Passive connection prerequisites; does not attach.'),
    'ListPlcSystemConstants': ('ReadPlcSystemConstants', 'READ', 'Reads system constant declarations from the bound project.'),
    'ListPlcTags': ('ReadPlcTags', 'READ', 'Reads tag declarations from the bound project.'),
    'ListPlcUserConstants': ('ReadPlcUserConstants', 'READ', 'Reads user constant declarations from the bound project.'),
    'PlanPlcExternalSourceImport': ('PlanPlcExternalSourceImport', 'OFFLINE', 'Plans an external source import; no import execution.'),
}
WITHDRAWN = {'ConnectProject': 'ConnectToProject'}
REBASED_SOURCES = {
    'ExportPlcBlockDocuments': 'ExportAsDocuments', 'ExportPlcBlocksDocuments': 'ExportBlocksAsDocuments',
    'ImportPlcBlockDocuments': 'ImportFromDocuments', 'ImportPlcBlocksDocuments': 'ImportBlocksFromDocuments',
}


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def operation_classifier(source, metadata=None):
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

    declared = dict(re.findall(r'\["([^"]+)"\] = new Classification\("[^"]+", "[^"]+", "([A-Z-]+)",', metadata or ''))

    def classify(name, description=None):
        if name in declared:
            return declared[name]
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


def compare_operation(old, actual, baseline, additions=None):
    if additions and old in additions:
        assert old not in baseline, ('new name collides with frozen baseline', old)
        assert actual == additions[old]['operation'], (old, actual, additions[old]['operation'])
        return
    expected = baseline[old]
    if old in EXCEPTIONS:
        previous, expected, reason = EXCEPTIONS[old]
        assert previous == baseline[old] and previous != expected and reason.strip(), old
    assert actual == expected, (old, baseline[old], '->', actual, 'expected', expected)


def compare_foundation_operation(current, source, actual, baseline):
    reviewed_source, expected, reason = NEW_FOUNDATION_TOOLS[current]
    assert source == reviewed_source and reason.strip(), ('unreviewed Foundation mapping', current, source)
    assert source not in baseline and current not in baseline, ('new tool collides with frozen baseline', current)
    assert actual == expected, (current, actual, 'expected', expected)


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
    additions = generator['new_v4_tools'](ROOT)
    workbench = generator['workbench_tools'](ROOT)
    assert set(baseline) | set(additions) == set(mapping), '3.3.0 plus reviewed V4 coverage differs'
    assert set(EXCEPTIONS) <= set(baseline), 'unused exception'
    taxonomy = (ROOT / 'src/Logic/ModelContextProtocol/ToolTaxonomy.cs').read_text(encoding='utf-8-sig')
    metadata = (ROOT / 'src/Logic/ModelContextProtocol/ToolMetadata.cs').read_text(encoding='utf-8')
    declared = re.findall(r'\["([^"]+)"\] = new Classification\(', metadata)
    assert len(declared) == len(set(declared)) and set(declared) == expected | set(WITHDRAWN), 'explicit classification coverage differs from product/source rosters'
    classify = operation_classifier(taxonomy, metadata)
    descriptions = {}
    for source in EngineSources(ROOT).sources.values():
        for match in re.finditer(r'\[McpServerTool\(Name\s*=\s*"([^"]+)"', source):
            # Only the leading tags are needed; do not parse prose or method bodies.
            description = re.match(r'\)\s*,\s*Description\(\s*"([^"\n]*)', source[match.end():])
            assert description, ('Missing adjacent Description', match[1])
            assert match[1] not in descriptions, ('Duplicate source registration', match[1])
            descriptions[match[1]] = description[1]
    assert set(descriptions) == (expected - set(NEW_FOUNDATION_TOOLS) - set(workbench)) | set(WITHDRAWN), 'engine source descriptions differ from reviewed product roster'
    seen = set()
    seen_new = set()
    for key in ('20', '21'):
        rows = runtime['releases'][key]
        compare_names([{'name': row['currentName']} for row in rows], rosters[key], 'runtime V' + key)
        for row in rows:
            old, current, final = row['sourceName'], row['currentName'], row['name']
            description = descriptions.get(current)
            source_operation = classify(current, description)
            assert listed[current]['operation'] == source_operation, (current, 'stale manifest operation')
            if current in workbench:
                assert old == current == final and source_operation == workbench[current], ('Workbench operation changed', row)
                continue
            if current in NEW_FOUNDATION_TOOLS:
                compare_foundation_operation(current, old, source_operation, baseline)
                compare_foundation_operation(current, old, classify(final, description), baseline)
                seen_new.add(current)
                continue
            if current in REBASED_SOURCES:
                assert old == current, ('Stale Foundation document sourceName', row)
                old = REBASED_SOURCES[current]
            assert mapping[old] == current, ('Stale runtime sourceName', row)
            compare_operation(old, source_operation, baseline, additions)
            compare_operation(old, classify(final, description), baseline, additions)
            seen.add(old)
    # The two removed guide aliases share GetToolUsage; their READ category is still checked.
    for old in MERGED:
        compare_operation(old, listed[mapping[old]]['operation'], baseline)
    assert seen | MERGED | set(WITHDRAWN.values()) == set(baseline) | set(additions), ('Uncompared baseline tools', set(baseline) - seen - MERGED - set(WITHDRAWN.values()))
    assert seen_new == set(NEW_FOUNDATION_TOOLS), 'Reviewed Foundation addition coverage differs'
    for current, old in WITHDRAWN.items():
        assert old in baseline and mapping[old] == current and current not in listed, ('Withdrawn tool exposed', current)
    import importlib.util
    spec = importlib.util.spec_from_file_location('contract_snapshot', Path(__file__).with_name('Snapshot-ToolContracts.py'))
    snapshot = importlib.util.module_from_spec(spec); spec.loader.exec_module(snapshot)
    snapshot.verified_contracts(ROOT / 'manifest/contracts/v4/baseline', ROOT)
    contracts = {row['name']: row for row in read(ROOT / 'manifest/contracts/v4/baseline/21.json')['tools']}
    for name, entry in listed.items():
        assert set(entry['parameters']) == set(contracts[name]['inputSchema']['properties']), (name, 'guard parameter roster differs from product contract')
    print(f'Tool list: V20={len(rosters["20"])}, V21={len(rosters["21"])}, union={len(expected)}; names match.')
    print(f'Operation comparison: {len(baseline)} released tools, {len(seen)} runtime mappings, '
          f'{len(MERGED)} merged guides, {len(WITHDRAWN)} withdrawn, {len(seen_new)} reviewed Foundation additions, '
          f'{len(EXCEPTIONS)} justified exceptions; 0 unexplained differences.')


class Checks(unittest.TestCase):
    def test_reviewed_foundation_additions_and_unknown_names_fail_closed(self):
        for current, (source, expected, _) in NEW_FOUNDATION_TOOLS.items():
            compare_foundation_operation(current, source, expected, {})
            with self.assertRaises(AssertionError): compare_foundation_operation(current, source, 'ONLINE-WRITE', {})
            with self.assertRaises(AssertionError): compare_foundation_operation(current, source, expected, {source: expected})
        with self.assertRaises(KeyError): compare_foundation_operation('CreatePlcFuture', 'CreatePlcFuture', 'WRITE', {})

    def test_current_schema_rejects_candidate_only_parameters(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location('contract_snapshot', Path(__file__).with_name('Snapshot-ToolContracts.py'))
        snapshot = importlib.util.module_from_spec(spec); spec.loader.exec_module(snapshot)
        self.assertEqual(0, snapshot.self_test(None))

    def test_new_v4_operations_remain_offline_file_output(self):
        generator = runpy.run_path(str(ROOT / 'scripts/generate/Generate-ToolUsage.py'))
        additions = generator['new_v4_tools'](ROOT)
        for name in additions:
            expected = additions[name]['operation']
            compare_operation(name, expected, {}, additions)
            for wrong in set(('ONLINE-WRITE', 'READ', 'FILE', 'WRITE', 'OFFLINE')) - {expected}:
                with self.assertRaises(AssertionError): compare_operation(name, wrong, {}, additions)
        with self.assertRaises(KeyError): compare_operation('RenderPlcOther', 'FILE', {}, additions)

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
        self.assertEqual(classify('Anything', '[L2][PLC-Online][ONLINE-WRITE]'), 'UNSPECIFIED')
        metadata = '["Anything"] = new Classification("L2", "PLC-Online", "ONLINE-WRITE",'
        self.assertEqual(operation_classifier(source, metadata)('Anything', 'arbitrary translated prose'), 'ONLINE-WRITE')
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
