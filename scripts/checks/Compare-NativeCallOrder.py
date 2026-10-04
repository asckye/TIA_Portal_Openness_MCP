"""Compare NativeCallWeaver verify inventories across engine, helper and CLI moves.

Discover disappeared source method families and match newly appearing service/tool/CLI
families by name. Compare the global Siemens member multiset and each moved body's
ordered direct sites, plus all categories for CLI and shared helpers, folding lambdas
and local functions into their source method.
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

SOURCES = {'TiaMcpServer.Siemens.Portal', 'TiaMcpServer.ModelContextProtocol.McpServer',
           'TiaMcpServer.Program', 'TiaMcpServer.Siemens.BaseLeftoversLogic'}
CLI_CLASSES = {'HmiTemplateBuilder', 'PlcHmiSyncXml', 'ReportBuilders', 'CliProbes'}
HELPER_CLASSES = {'CompilerDiagnostics', 'PlcCompilation', 'PlcProgramImport', 'OfflineToolExecution',
                  'XmlBuildResults', 'EngineeringFileNames', 'EngineeringLookupHints', 'OnlineToolPolicy',
                  'ScaffoldOperations', 'ToolJsonArguments', 'DocumentImportGuidance'}
HELPER_OWNERS = {'TiaMcpServer.ModelContextProtocol.' + name for name in HELPER_CLASSES}
RULE_OWNERS = {'TiaMcpServer.Siemens.' + name for name in
               ('HardwareUtilityRules', 'DeviceServiceObjectRules', 'ObjectIdentityRules',
                'ToolTransactionRules', 'EngineeringCredentialRules')}
HOST = 'TiaMcpServer.ModelContextProtocol.McpServer'
HELPER_METHODS = {'CompileAndDiagnoseCore', 'BuildCompileResponse', 'ReadIntProperty',
                  'ClassifyPlcXml', 'BuildPlcProgramImportResponse', 'ResolveCompareSide',
                  'DeleteAnalysisTempDir', 'RunOfflineAnalysisTool', 'BuildOfflineXmlBuilderReport',
                  'MakeSafeFileName', 'BuildBlockDidYouMean', 'BuildTypeDidYouMean',
                  'GetOnlineMonitoringSafetyPolicy', 'IsOnlineModeError', 'WithAutoOffline',
                  'ApplyScaffoldPlcElements', 'CompileScaffoldPlc', 'ApplyScaffoldHmi',
                  'ParseJsonObjectOrEmpty', 'CollectCompilerMessages', 'Count', 'ParseDeclared',
                  'CompilerMessageCollectResult.Summary', 'CompilerReferenceComparer.Equals',
                  'CompilerReferenceComparer.GetHashCode'}
PORTAL = 'TiaMcpServer.Siemens.Portal'
SERVICE = 'TiaMcpServer.Siemens.Services.ExampleService'
CLI_OWNERS = {'TiaMcpServer.Program'} | {
    prefix + name for prefix in ('TiaMcpServer.', 'TiaMcpServer.Cli.') for name in CLI_CLASSES}


def normalize_cli_types(value):
    # Nested DTOs/delegates move with their CLI class. Preserve their names and
    # generic signatures while ignoring only the enclosing class relocation.
    return re.sub(r'\b(?:' + '|'.join(map(re.escape, sorted(CLI_OWNERS, key=len, reverse=True))) +
                  r')(?=/|::|[>, )]|$)', '<cli>', value)


def destination(owner):
    return (owner in HELPER_OWNERS | RULE_OWNERS or owner.startswith('TiaMcpServer.Siemens.Services.') or
            owner.startswith('TiaMcpServer.ModelContextProtocol.') and owner.endswith('Tools') or
            any(owner == prefix + name for prefix in ('TiaMcpServer.', 'TiaMcpServer.Cli.')
                for name in CLI_CLASSES))


def normalize_helper_types(value):
    # These private diagnostic result/comparer types move with their implementation.
    value = re.sub(r'TiaMcpServer\.ModelContextProtocol\.(?:McpServer|CompilerDiagnostics)/'
                   r'(CompilerMessageCollectResult|CompilerReferenceComparer)', r'<compiler>/\1', value)
    return re.sub(r'TiaMcpServer\.Siemens\.(?:BaseLeftoversLogic|ToolTransactionRules)/ToolCall',
                  '<transaction>/ToolCall', value)


def all_categories(method):
    owner, name = method
    return (owner in CLI_OWNERS | HELPER_OWNERS | RULE_OWNERS or
            owner == 'TiaMcpServer.Siemens.BaseLeftoversLogic' or owner == HOST and name in HELPER_METHODS)


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
    lambda_state = re.search(r'/<<([^>]+)>b__(?:\d+_)?(\d+)>d(?:__\d+)?$', declared)
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
    elif lambda_state:
        name, ordinal = lambda_state.groups()
        body = 'lambda ' + ordinal + ' state ' + method
    elif '/<' in declared or method.startswith('<'):
        raise ValueError('Unmapped generated native body: ' + caller)
    else:
        name = declared[len(owner) + 1:] + '.' + method if '/' in declared else method
        body = 'body'
    # Retain overload signatures and closure-local indices, not compilation-wide IDs.
    parameters = normalize_helper_types(parameters)
    parameters = normalize_cli_types(parameters) if owner in CLI_OWNERS else parameters.replace(owner, '<owner>')
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
        # CLI parts and shared helpers can reach native objects indirectly.
        # Their reflection, enumeration and dispatch sites must retain order as well.
        if site['category'] == 'direct' or all_categories(method):
            bodies[body].append(site)
    return {method: [(body, row['category'] + ':' + row['opcode'] if all_categories(method) else row['opcode'],
                     normalize_cli_types(row['member']) if method[0] in CLI_OWNERS else normalize_helper_types(row['member']))
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

    def test_shared_helper_move_preserves_indirect_order(self):
        before = [dict(caller=f'void {HOST}::BuildCompileResponse(System.String,System.Object)',
                       offset=offset, opcode='callvirt', member=member, category='reflection')
                  for offset, member in ((10, 'PropertyInfo::GetValue'), (20, 'MethodInfo::Invoke'))]
        after = [dict(row, caller=row['caller'].replace(HOST,
                     'TiaMcpServer.ModelContextProtocol.PlcCompilation')) for row in before]
        self.assertEqual([], compare(before, after)[0])
        after[0]['offset'], after[1]['offset'] = after[1]['offset'], after[0]['offset']
        self.assertTrue(compare(before, after)[0])

    def test_program_moves_and_retained_host(self):
        for prefix in ('TiaMcpServer.', 'TiaMcpServer.Cli.'):
            for name in sorted(CLI_CLASSES):
                with self.subTest(destination=prefix + name):
                    before, after = self.inventories()
                    for row in before:
                        row['caller'] = row['caller'].replace(PORTAL, 'TiaMcpServer.Program')
                    for row in after:
                        row['caller'] = row['caller'].replace(SERVICE, prefix + name)
                    host = dict(before[0], caller='void TiaMcpServer.Program::Main(System.String[])')
                    errors, pairs, old, new = compare(before + [host], after + [host])
                    self.assertEqual([], errors)
                    self.assertEqual((1, 6, 6), (len(pairs), old, new))
                    after[-1]['offset'], after[-2]['offset'] = after[-2]['offset'], after[-1]['offset']
                    self.assertTrue(compare(before, after)[0])

    def test_unrelated_root_type_is_not_a_cli_destination(self):
        self.assertFalse(destination('TiaMcpServer.Unrelated'))
        self.assertFalse(destination('TiaMcpServer.Cli.Unrelated'))

    def test_cli_indirect_sites_and_nested_signatures(self):
        before = [dict(caller='void TiaMcpServer.Program::Report(TiaMcpServer.Program/Row)',
                       offset=offset, opcode='callvirt', member=member, category=category)
                  for offset, member, category in (
                      (10, 'void System.Collections.Generic.List`1<TiaMcpServer.Program/Row>::Add(!0)', 'interface'),
                      (20, 'System.Object System.Reflection.PropertyInfo::GetValue(System.Object)', 'reflection'))]
        after = copy.deepcopy(before)
        for row in after:
            row['caller'] = row['caller'].replace('TiaMcpServer.Program', 'TiaMcpServer.Cli.ReportBuilders')
            row['member'] = row['member'].replace('TiaMcpServer.Program', 'TiaMcpServer.HmiTemplateBuilder')
        errors, pairs, old, new = compare(before, after)
        self.assertEqual([], errors)
        self.assertEqual((1, 2, 0, 0), (len(pairs), len(pairs[0][2]), old, new))
        after[0]['offset'], after[1]['offset'] = after[1]['offset'], after[0]['offset']
        self.assertTrue(compare(before, after)[0])
        self.assertTrue(compare(before, after[:1])[0])

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
                ('/<>c__DisplayClass121_0/<<Read>b__1>d', 'MoveNext'),
                ('/<>c/<<Read>b__121_1>d', 'MoveNext'),
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
