"""Compare disconnected domain responses over full/lite, direct/isolated STDIO.

Use pre-move/current HttpTests and EXEs. Compare UTF-8 response text, masking the
declared envelope/watch-probe timestamps and D1 exception stack frames. Version-hidden tools must remain hidden; their direct
registration refusal and lite bridge refusal are compared as well. Never connects
to TIA. Extend CASES when migrating another domain; source coverage is mandatory.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import unittest


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


resources = load('resources', 'Test-ResourceDiscovery.py')
snapshots = load('snapshots', 'Snapshot-ToolResponses.py')
snapshots.RAW_MASK_RULES = [*snapshots.RAW_MASK_RULES,
    {'tool': '*', 'path': ['Meta', 'timestamp'], 'reason': 'CallTool POCO envelope wall clock'},
    *({'tool': name, 'path': [key, 'timestamp'], 'reason': 'Watch-table read/probe DateTime.Now.ToString("O")'}
      for name in ('ReadPlcWatchTableCurrentValuesReadOnly', 'ProbePlcMonitorOnlineCapabilities')
      for key in ('data', 'Data'))]


ERROR_PATHS = {('"meta"', '"error"'), ('"Meta"', '"error"')}
# RunHmiStepTool writes ex.ToString() here. The local .NET Framework SDK uses
# the zh-CN frame prefix; keep this as narrow as the English "at " prefix.
STACK_FRAME = re.compile(r'^(?:[^\S\r\n]+(?:at |在 )|[^\S\r\n]*--- End of)')
STRING_UNIT = re.compile(r'\\u[0-9a-fA-F]{4}|\\["\\/bfnrt]|[^\\]')


def error_tokens(text):
    # Walk literal JSON tokens so replacing error spans cannot reformat the
    # surrounding response. Escaped property names are deliberately not masked.
    try:
        json.loads(text)
        tokens = iter(snapshots.RAW_TOKEN.finditer(text))

        def value(token, path):
            raw = token.group()
            if raw == '{':
                key = next(tokens)
                while key.group() != '}':
                    next(tokens)  # colon
                    yield from value(next(tokens), path + (key.group(),))
                    key = next(tokens)  # comma or closing brace
                    if key.group() != '}':
                        key = next(tokens)
            elif raw == '[':
                item = next(tokens)
                index = 0
                while item.group() != ']':
                    yield from value(item, path + (index,))
                    index += 1
                    item = next(tokens)  # comma or closing bracket
                    if item.group() != ']':
                        item = next(tokens)
            elif path in ERROR_PATHS and raw.startswith('"'):
                yield path, token

        return list(value(next(tokens), ()))
    except (ValueError, StopIteration, RuntimeError, RecursionError):
        return []


def mask_error_string(raw):
    # Decode only to recognize line boundaries/prefixes. Retained characters,
    # including their original JSON escapes, are copied from the raw token.
    body = raw[1:-1]
    units = list(STRING_UNIT.finditer(body))
    decoded = ''.join(json.loads('"' + unit.group() + '"') if unit.group().startswith('\\')
                      else unit.group() for unit in units)
    separators = list(re.finditer(r'\r\n|\r|\n', decoded))
    for index in reversed(range(len(separators))):
        separator = separators[index]
        end = separators[index + 1].start() if index + 1 < len(separators) else len(units)
        if STACK_FRAME.match(decoded[separator.end():end]):
            raw_start = units[separator.start()].start()
            raw_end = units[end].start() if end < len(units) else len(body)
            body = body[:raw_start] + body[raw_end:]
    return '"' + body + '"'


def mask_error_frames(text):
    for _, token in reversed(error_tokens(text)):
        text = text[:token.start()] + mask_error_string(token.group()) + text[token.end():]
    return text


def error_first_lines(text):
    return [('.'.join(json.loads(key) for key in path),
             re.split(r'\r\n|\r|\n', json.loads(token.group()), maxsplit=1)[0])
            for path, token in error_tokens(text)]


def actions(tool, values, **arguments):
    return [(tool, action, dict(arguments, action=action)) for action in values.split()]


PLC = 'DomainOfflineFixture'
CASES = {
    'Cfc': actions('ExchangeCfcCharts', 'export selectiveExport import exportInstructionData',
        softwarePath=PLC, filePath='C:/cfc-offline-fixture.xml.zip', modelVersion='V2.0', chartNamesJson='["Chart1"]')
        + actions('ManageCfcChartProtection', 'read add change remove', softwarePath=PLC,
                  chartName='Chart1', currentPassword='offline', newHashedPassword='offline'),
    'TestSuite': [('ReadTestSuiteCases', category, {'category': category})
                  for category in ('styleGuide', 'application', 'system')]
        + actions('ExchangeTestSuiteCase', 'export import importTestSets delete',
                  category='application', name='Case1', filePath='C:/domain-offline.tst')
        + [('RunTestSuiteCase', category, {'category': category, 'name': 'Case1'})
           for category in ('styleGuide', 'application', 'system')]
        + actions('ManageTestSuiteCase', 'read rename setScope copyScope createFromMasterCopy showInEditor',
                  category='styleGuide', name='Case1'),
    'V20Options': actions('ManageSinumerikArchive', 'archive retrieve fAddressArchive', filePath='C:/domain-offline.dsf')
        + [('ImportSinumerikAlarmTexts', 'import', {'devicePathJson': '[]', 'filesJson': '["C:/domain-offline.ts"]'})]
        + actions('ManageSinumerikSafetyMode', 'read set', devicePathJson='[]')
        + [('InitializeSimotionScripting', 'initialize', {}), ('ExportScadaData', 'export', {'filePath': 'C:/domain-offline.zip'})],
    'OptionalEngineering': [('ReadSiVArcRules', 'read', {'category': 'screens'})]
        + actions('ManageSiVArcRule', 'create update delete', category='screens', collectionPathJson='[]', name='Rule1'),
    'SpecializedExchange': actions('ExchangePlcSupervisions', 'export import importSettings',
                                  softwarePath=PLC, filePath='C:/domain-offline.xlsx'),
    'SoftwareUnitDeep': [('ReadPlcSoftwareUnits', 'read', {'softwarePath': PLC})]
        + actions('ManagePlcSoftwareUnit', 'list read create createFromMasterCopy delete update createRelation deleteRelation',
                  softwarePath=PLC, name='Unit1')
        + actions('ManagePlcDocuments', 'list read export import createFromMasterCopy createFromLibraryType',
                  softwarePath=PLC, name='Document1')
        + [('ReadPlcChecksums', 'read', {'softwarePath': PLC}),
           ('ReadPlcObjectFingerprints', 'read', {'softwarePath': PLC, 'objectKind': 'block', 'objectPath': 'Block1'})]
        + actions('ManagePlcBlockWriteProtection', 'read define protect unprotect change remove',
                  softwarePath=PLC, blockPath='Block1')
        + actions('ManageProjectCompilationSettings', 'read update'),
    'Dcc': [('ReadDccCharts', 'read', {'devicePathJson': '[]', 'itemPathJson': '[]'})]
        + actions('ManageDccChart', 'read readSequence create update delete export exportAll import optimizeSequence showEditor',
                  devicePathJson='[]', itemPathJson='[]', chartName='Chart1')
        + actions('ManageDccBlock', 'read create update delete setAsPredecessor',
                  devicePathJson='[]', itemPathJson='[]', chartPath='Chart1', blockName='Block1')
        + actions('ManageDccPin', 'read update connect disconnect publish unpublish updateParameter',
                  devicePathJson='[]', itemPathJson='[]', chartPath='Chart1', blockName='Block1', pinName='Pin1')
        + actions('ManageDccChartInterface', 'read create update delete',
                  devicePathJson='[]', itemPathJson='[]', chartPath='Chart1', interfaceName='Interface1')
        + actions('ManageDccChartPartition', 'read create update delete',
                  devicePathJson='[]', itemPathJson='[]', chartPath='Chart1', partitionName='Partition1')
        + actions('ManageDcbLibraries', 'read import')
        + [('ReadDccObject', 'read', {'devicePathJson': '[]', 'itemPathJson': '[]'})],
    'Teamcenter': actions('ManageTeamcenterConnection', 'read connect connectSso disconnect')
        + actions('ManageTeamcenterDataset', 'checkout checkin cancelCheckout search download')
        + actions('ManageTeamcenterWorkflow', 'readCustomAttributes save saveWithProxyObject saveToItem saveToItemWithProxyObject '
                  'saveAsNewItem saveAsNewItemWithProxyObject saveAsNewRevision saveAsNewRevisionWithProxyObject'),
    'Startdrive': [('ReadDriveObjects', 'read', {'devicePathJson': '[]', 'itemPathJson': '[]'}),
                  ('ReadDriveParameters', 'read', {'devicePathJson': '[]', 'itemPathJson': '[]'})]
        + actions('ManageStartdriveParameter', 'read write', devicePathJson='[]', itemPathJson='[]',
                  driveObjectNumber=0, parameter='p2051[0]')
        + actions('ManageDriveTelegrams', 'read check insert erase changeNumber changeSize connectTechnologyObject',
                  devicePathJson='[]', itemPathJson='[]')
        + actions('ManageDriveFunctions', 'read changeDriveObjectType changeActivationState activateFunction deactivateFunction '
                  'setSIAxisType setMotorCode setSimoGearMlfb updateCheckSums setMotorType readMotorConfiguration '
                  'projectMotorConfiguration setEquivalentCircuitDiagramData setEncoder readEncoderConfiguration '
                  'setEncoderType projectEncoderConfiguration', devicePathJson='[]', itemPathJson='[]')
        + actions('ManageDriveSecurity', 'read activateUmac deactivateUmac activateEncryption deactivateEncryption',
                  devicePathJson='[]', itemPathJson='[]')
        + actions('ManageTechnologyExtensions', 'read activate deactivate readPackages install installAndGetIdentifier uninstall')
        + actions('ManageDriveHardwareModule', 'read changeType setPositionNumber', devicePathJson='[]', itemPathJson='[]')
        + actions('ManageDriveSafetyAcceptanceTest', 'read setActive resetTestFunctions createProtocol',
                  devicePathJson='[]', itemPathJson='[]')
        + [('ReadOnlineDriveParameters', 'read', {'devicePathJson': '[]', 'itemPathJson': '[]'})]
        + actions('ManageOnlineDriveFunctions', 'read performFactoryReset performRamToRomCopy changeActivationState',
                  devicePathJson='[]', itemPathJson='[]'),
    'SafetyManagement': actions('ManagePlcSafety',
        'read createRuntimeGroup deleteRuntimeGroup updateRuntimeGroup updateSettings generateGlobalFIOStatusBlock '
        'cleanSystemGeneratedObjects generateBaseId login logoff setPassword revokePassword', softwarePath=PLC)
        + actions('ManageSafetyGlobalSettings', 'read update')
        + [('ReadSafetyBlockSignatures', 'read', {'softwarePath': PLC}),
           ('ExportSafetyPrintout', 'export', {'softwarePath': PLC, 'filePath': 'C:/domain-offline.pdf'})],
    'SafetyValidation': [('ReadSafetyActivationTests', 'read', {})]
        + actions('ManageSafetyActivationTest',
                  'read create createFromTest createFromMasterCopy rename setAuthor changeEvaluationDevice '
                  'checkValidity generateReport export import delete', name='Test1')
        + actions('ManageSafetyActivationTestGroup', 'read create createFromMasterCopy rename delete')
        + actions('ManageSafetyFunction', 'read create createFrom update resetTestResult checkValidity setTrace checkTraceValidity export import delete',
                  activationTest='Test1')
        + actions('ManageSafetyFunctionCondition', 'read create update checkValidity delete', activationTest='Test1', safetyFunction='Function1'),
    'CertificateManagement': actions('ManagePlcCertificate', 'list read template create import export delete assign unassign',
                                   devicePathJson='["Device1"]', itemPathJson='["CPU1"]'),
    'SecurityDeep': actions('ManageSyslogServers', 'read create update delete assignModule unassignModule')
        + [('ManageSyslogServers', 'plc/' + action, dict(scope='plc', action=action))
           for action in ('read', 'update', 'createServer', 'deleteServer')]
        + actions('ManagePasswordPolicy', 'read update')
        + [('ManageUmcUsers', kind + '/' + action, dict(kind=kind, action=action))
           for kind in ('user', 'group') for action in
           ('read', 'createOffline', 'importFromServer', 'rename', 'activate', 'deactivate', 'delete', 'assignRole', 'unassignRole')]
        + [('ManageUmcUsers', 'server/' + action, dict(kind='server', action=action))
           for action in ('read', 'checkConsistency', 'synchronize')],
    'ProjectSecurity': [('ReadProjectUserManagement', category, {'category': category}) for category in
        ('users', 'anonymousUser', 'systemRoles', 'customRoles', 'engineeringRights', 'customDeviceRights',
         'umcUsers', 'umcUserGroups', 'passwordPolicy', 'deviceRights', 'roleDeviceRights')]
        + actions('ManageProjectUserManagement',
                  'createUser deleteUser setUserPassword activateUser deactivateUser assignRole unassignRole '
                  'createRole deleteRole assignEngineeringRight unassignEngineeringRight assignDeviceRight unassignDeviceRight '
                  'createDeviceRight deleteDeviceRight activateAnonymousUser deactivateAnonymousUser', name='User1')
        + [('ReadProjectProtection', 'read', {})]
        + actions('ManageMultiuserSession', 'read listServerProjects readLockState listLocalSessions connectServer disconnectServer commit',
                  serverName='Server1', projectName='Project1', host='offline.invalid', port=443, commitComment='Offline preview')
        + [('CompareLibraries', 'read', {'leftLibraryName': 'Library1', 'rightLibraryName': 'Library2'}),
           ('ReadProjectSettings', 'read', {})]
        + [('CompareProjects', kind, {'kind': kind, 'softwarePath': PLC})
           for kind in ('software', 'softwareToLibrary', 'hardware')],
    'PlcTables': [(name, 'disconnected', {'softwarePath': PLC}) for name in (
            'GetPlcTagTables', 'GetPlcWatchTables', 'GetPlcForceTables', 'ProbePlcMonitorOnlineCapabilities')]
        + [('ExportPlcTagTable', 'disconnected', dict(softwarePath=PLC, tagTableName='Tags', exportPath='C:/domain-tags.xml')),
           ('ImportPlcTagTable', 'disconnected', dict(softwarePath=PLC, folderPath='', importPath='C:/domain-tags.xml')),
           ('ImportPlcTagTablesFromDirectory', 'disconnected', dict(softwarePath=PLC, folderPath='', dir='C:/domain-tables')),
           ('SetWatchTableModifyValue', 'disconnected', dict(softwarePath=PLC, tableName='Watch', address='%M0.0', modifyValue='TRUE')),
           ('ExportPlcWatchTable', 'disconnected', dict(softwarePath=PLC, watchTableName='Watch', exportPath='C:/domain-watch.xml')),
           ('ExportPlcWatchTablesToDirectory', 'disconnected', dict(softwarePath=PLC, dir='C:/domain-tables')),
           ('ReadPlcWatchTableCurrentValuesReadOnly', 'disconnected', dict(softwarePath=PLC, watchTableName='Watch')),
           ('MonitorWatchTableLiveS7', 'disconnected', dict(softwarePath=PLC, watchTableName='Watch', ip='192.0.2.1')),
           ('ImportPlcWatchTableOffline', 'disconnected', dict(softwarePath=PLC, filePath='C:/domain-watch.xml'))]
        + [('ReadPlcTagTableConstants', kind, dict(softwarePath=PLC, tablePath='Tags', kind=kind))
           for kind in ('all', 'user', 'system')]
        + actions('ManagePlcTableEntries', 'read createComment deleteEntry deleteTable',
                  softwarePath=PLC, tableKind='watch', tablePath='Watch', entryIndex=0, confirmDelete=True)
        + [('ManagePlcTableEntries', 'force-read', dict(softwarePath=PLC, tableKind='force', tablePath='Force'))]
        + [('PlanOnlineReadOnlyMonitoring', mode, dict(softwarePath=PLC, tagPathsJson='["DB_HMI.MotorRun"]', mode=mode))
           for mode in ('current-values', 'watch-table-export-plan')]
        + [('PlanOnlineReadOnlyDataProvider', provider, dict(provider=provider, endpoint='opc.tcp://192.0.2.1:4840',
                                                          tagPathsJson='["DB_HMI.MotorRun"]'))
           for provider in ('opcua', 's7-readonly')]
}


def table_reply(reply, profile, name):
    """Preserve each table tool's existing throw/POCO/plan family, without connecting."""
    resources.require('result' in reply, f'{name}: missing tools/call result: {reply}')
    result = reply['result']
    raw = result['content'][0]['text']
    throwing = name in {'GetPlcTagTables', 'GetPlcWatchTables', 'GetPlcForceTables',
                        'ExportPlcTagTable', 'ImportPlcTagTable', 'ExportPlcWatchTable'}
    if profile == 'lite':
        bridge = json.loads(raw)
        resources.require(bridge.get('meta', {}).get('bridgeSuccess') is (not throwing),
                          f'{name}: unexpected bridge status: {raw}')
        raw = bridge['message']
    else:
        resources.require(bool(result.get('isError')) is throwing, f'{name}: unexpected error family: {raw}')
    if name.startswith('PlanOnlineReadOnly'):
        value = json.loads(raw)
        resources.require(value.get('ok', value.get('Ok')) is True and
                          value.get('data', value.get('Data', {})).get('readOnly') is True,
                          f'{name}: offline plan failed: {raw}')
    else:
        resources.require(any(marker in raw for marker in ('Project is null', 'No project open',
            'No project is open', 'PLC software not found', 'Failed exporting PLC', 'PlcSoftware not found')),
            f'{name}: did not reach the disconnected guard: {raw}')
    return raw


def check_coverage(domains):
    root = Path(__file__).resolve().parents[2] / 'tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools'
    for domain in domains:
        source = (root / (domain + 'Tools.cs')).read_text(encoding='utf-8-sig')
        actual = set(re.findall(r'\[McpServerTool\(Name\s*=\s*"(\w+)"', source))
        covered = {name for name, _, _ in CASES[domain]}
        resources.require(actual == covered, f'{domain} fixture coverage differs: actual={actual}, covered={covered}')


def capture(args, exe, harness, profile, isolated):
    responses = {}
    with resources.server(exe.resolve(), args.public_api.resolve(), args.major, 'stdio', profile,
            harness.resolve(), args.public_api.resolve(), isolate=isolated) as (rpc, _, _):
        rpc('initialize', params={'protocolVersion': '2024-11-05', 'capabilities': {},
            'clientInfo': {'name': 'domain-tools', 'version': '1'}})
        rpc('notifications/initialized', notification=True)
        names = [tool['name'] for tool in rpc('tools/list')['result']['tools']]
        reached_child = False
        for domain in args.domain:
            for name, case, arguments in CASES[domain]:
                hidden = args.major == 20 and (name in ('ManagePlcBlockWriteProtection', 'ManageDriveSafetyAcceptanceTest')
                                               or domain == 'SafetyValidation')
                version_action = args.major == 20 and (
                    (name == 'ManagePlcDocuments' and arguments['action'] in ('createFromMasterCopy', 'createFromLibraryType'))
                    or (name == 'ManageDcbLibraries' and arguments['action'] == 'import')
                    or (name == 'ManageDriveHardwareModule' and arguments['action'] in ('changeType', 'setPositionNumber'))
                    or (name == 'ManagePlcSafety' and arguments['action'] == 'generateBaseId'))
                if profile == 'full':
                    resources.require((name in names) != hidden, f'{name}: unexpected version registration')
                params = {'name': name, 'arguments': arguments} if profile == 'full' else {
                    'name': 'CallTool', 'arguments': {'name': name.lower(), 'argumentsJson': json.dumps(arguments)}}
                reply = rpc('tools/call', params=params)
                if domain == 'PlcTables':
                    raw = table_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if (hidden or version_action) and profile == 'full':
                    resources.require('error' in reply or reply.get('result', {}).get('isError'),
                                      f'Unavailable tool/action unexpectedly ran: {reply}')
                    raw = json.dumps(reply.get('error', reply.get('result')), ensure_ascii=False)
                else:
                    resources.require('result' in reply and not reply['result'].get('isError'), str(reply))
                    raw = reply['result']['content'][0]['text']
                    if profile == 'lite':
                        bridge = json.loads(raw)
                        resources.require(bridge.get('meta', {}).get('bridgeSuccess') is (not hidden and not version_action),
                                          f'{name}: unexpected bridge status: {raw}')
                        if not hidden and not version_action:
                            raw = bridge['message']
                    value = json.loads(raw)
                    meta = value.get('meta', value.get('Meta', {}))
                    if hidden or version_action:
                        resources.require('V20' in raw and ('unavailable' in raw or 'requires' in raw),
                                          f'{name}: missing version refusal: {raw}')
                    elif domain == 'V20Options' and args.major == 21:
                        resources.require(meta.get('tool') == name and meta.get('operationSuccess') is False
                                          and 'absent from the supplied V21 SDK' in raw,
                                          f'{name}: missing V21 unsupported response: {raw}')
                        reached_child = True
                    elif name in ('ManageSafetyGlobalSettings', 'ManageMultiuserSession', 'CompareLibraries', 'ReadProjectSettings'):
                        resources.require(value.get('message', value.get('Message')) == name + ' failed'
                            and meta.get('tool') == name and meta.get('status') == 'InvalidState'
                            and meta.get('operationSuccess') is False
                            and re.split(r'\r\n|\r|\n', meta.get('error', ''), maxsplit=1)[0]
                                == 'TiaMcpServer.Siemens.PortalException: Connect to TIA first.',
                            f'{name}/{case} did not reach the disconnected portal guard: {raw}')
                        reached_child = True
                    else:
                        resources.require(value.get('message', value.get('Message')) == 'Project is null'
                            and meta.get('tool') == name and meta.get('status') == 'InvalidState'
                            and meta.get('operationSuccess') is False,
                            f'{name}/{case} did not reach the disconnected guard: {raw}')
                        reached_child = True
                responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
        if isolated and reached_child:
            reply = rpc('tools/call', params={'name': 'ReadOpennessWorkerStatus', 'arguments': {}})
            status = json.loads(reply['result']['content'][0]['text'])
            resources.require(status['meta']['worker']['state'] == 'Ready', 'Domain calls never reached the isolated child')
    return names, responses


class SelfTests(unittest.TestCase):
    @staticmethod
    def response(error, key='meta'):
        return json.dumps({key: {'error': error}}, ensure_ascii=False)

    def test_frame_only_difference_passes(self):
        for key in ('meta', 'Meta'):
            before = self.response('Type: message\r\n   at Portal.Old()\r\n   at Common()', key)
            after = self.response('Type: message\n   at Service.New()\n--- End of previous location ---\n   at Extra()', key)
            self.assertEqual(mask_error_frames(before), mask_error_frames(after))

    def test_localized_frame_only_difference_passes(self):
        before = self.response('Type: message\r\n   在 Portal.Old()')
        after = self.response('Type: message\r\n   在 Service.New()')
        self.assertEqual(mask_error_frames(before), mask_error_frames(after))

    def test_first_line_difference_fails(self):
        before = self.response('Type: before\n   at Old()')
        after = self.response('Type: after\n   at New()')
        self.assertNotEqual(mask_error_frames(before), mask_error_frames(after))

    def test_non_frame_difference_fails(self):
        for line in ('details', '   details', 'at Unindented()', '   at\tUnrecognized()', '',
                     ' ---> InnerException: message', '--- Other boundary ---'):
            before = self.response('Type: message\n   at Old()\n' + line)
            after = self.response('Type: message\n   at New()\n' + line + ' changed')
            self.assertNotEqual(mask_error_frames(before), mask_error_frames(after), repr(line))

    def test_non_frame_bytes_preserved(self):
        before = '{ "meta" : { "error": "Type: \\u006dessage\\r\\n   at Old()\\nDetails: \\/" }, "x": 1.0 }'
        expected = '{ "meta" : { "error": "Type: \\u006dessage\\nDetails: \\/" }, "x": 1.0 }'
        self.assertEqual(mask_error_frames(before), expected)
        self.assertNotEqual(mask_error_frames(before), expected.replace('\\u006d', 'm'))
        self.assertNotEqual(mask_error_frames(before), expected.replace('1.0', '1'))

    def test_escaped_newlines_and_literal_backslashes(self):
        raw = r'{"meta":{"error":"Type: \\n literal\u000d\u000a\u0020\u0020at Old()"}}'
        self.assertEqual(mask_error_frames(raw), r'{"meta":{"error":"Type: \\n literal"}}')

    def test_only_known_error_paths_masked(self):
        raw = r'{"data":{"meta":{"error":"Type: message\n   at Keep()"}},"error":"Type: message\n   at Keep()","Meta":{"error":"Type: message\n   at Remove()"},"m\u0065ta":{"error":"Type: message\n   at Keep()"}}'
        self.assertEqual(mask_error_frames(raw), raw.replace(r'\n   at Remove()', ''))

    def test_first_line_never_masked(self):
        for first_line in ('   at First()', '--- End of first line ---', '   在 First()'):
            raw = self.response(first_line)
            self.assertEqual(mask_error_frames(raw), raw)

    def test_invalid_json_unchanged(self):
        raw = r'{"meta":{"error":"Type: message\n   at Keep()"}} trailing'
        self.assertEqual(mask_error_frames(raw), raw)

    def test_all_declared_tools_covered(self):
        check_coverage(CASES)

    def test_table_reply_preserves_disconnected_error_family(self):
        reply = {'result': {'isError': True, 'content': [{'text': 'PLC software not found'}]}}
        self.assertEqual(table_reply(reply, 'full', 'GetPlcWatchTables'), 'PLC software not found')
        with self.assertRaises(AssertionError):
            table_reply(reply, 'full', 'MonitorWatchTableLiveS7')

    def test_table_reply_rejects_unrelated_failure(self):
        reply = {'result': {'content': [{'text': '{"message":"Unexpected constructor failure"}'}]}}
        with self.assertRaises(AssertionError):
            table_reply(reply, 'full', 'MonitorWatchTableLiveS7')

    def test_watch_timestamp_mask_is_narrow(self):
        raw = '{"data":{"timestamp":"2026-10-03T12:34:56.123+08:00","other":"2026-10-03T12:34:56.123+08:00"}}'
        masked = snapshots.mask_raw_text(raw, 'ProbePlcMonitorOnlineCapabilities')
        self.assertIn('"timestamp":"<string:timestamp>"', masked)
        self.assertIn('"other":"2026-10-03T12:34:56.123+08:00"', masked)
        self.assertEqual(snapshots.mask_raw_text(raw, 'GetPlcWatchTables'), raw)

    def test_unique_cases(self):
        for domain, cases in CASES.items():
            self.assertTrue(cases, domain)
            self.assertEqual(len(cases), len({(name, case) for name, case, _ in cases}), domain)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--domain', choices=tuple(CASES), action='append', default=[])
    parser.add_argument('--exe', type=Path)
    parser.add_argument('--baseline-exe', type=Path)
    parser.add_argument('--public-api', type=Path)
    parser.add_argument('--host-harness', type=Path)
    parser.add_argument('--baseline-harness', type=Path)
    parser.add_argument('--major', type=int, choices=(20, 21))
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return int(not result.wasSuccessful())
    if not args.domain or any(getattr(args, name) is None for name in (
            'exe', 'baseline_exe', 'public_api', 'host_harness', 'baseline_harness', 'major')):
        parser.error('--domain, both EXEs/harnesses, --public-api and --major are required')
    args.domain = list(dict.fromkeys(args.domain))
    check_coverage(args.domain)
    passed = failed = 0
    for isolated in (False, True):
        for profile in ('full', 'lite'):
            before_names, before = capture(args, args.baseline_exe, args.baseline_harness, profile, isolated)
            after_names, after = capture(args, args.exe, args.host_harness, profile, isolated)
            resources.require(before_names == after_names and len(after_names) == len(set(after_names)),
                'tools/list changed or contains duplicate names')
            passed += 1
            for name, raw in before.items():
                before_text, after_text = raw.decode('utf-8'), after[name].decode('utf-8')
                before_masked = mask_error_frames(before_text).encode('utf-8')
                after_masked = mask_error_frames(after_text).encode('utf-8')
                if after_masked != before_masked:
                    failed += 1
                    print(f'FAIL V{args.major} {profile} isolated={isolated} {name}: response bytes changed\n'
                          f'before={raw.decode("utf-8")}\nafter={after[name].decode("utf-8")}', flush=True)
                else:
                    passed += 1
                    print(f'PASS V{args.major} {profile} isolated={isolated} {name}: ' + hashlib.sha256(before_masked).hexdigest(), flush=True)
                    if raw != after[name]:
                        first_lines = error_first_lines(before_text)
                        resources.require(first_lines == error_first_lines(after_text), 'D1 first lines changed')
                        for path, first_line in first_lines:
                            print(f'D1 V{args.major} {profile} isolated={isolated} {name}: {path} '
                                  f'firstLineEqual=true firstLine={json.dumps(first_line, ensure_ascii=False)}', flush=True)
    print(f'COMPLETE: {passed} domain dispatch/byte checks passed; {failed} failed; '
          'only declared timestamps and D1 meta/Meta.error stack frames masked; first lines/non-frame bytes preserved; no TIA connection')
    return int(bool(failed))


if __name__ == '__main__':
    raise SystemExit(main())
