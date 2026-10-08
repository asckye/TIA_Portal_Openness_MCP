"""Compare disconnected domain responses over full/lite, direct/isolated STDIO.

Use pre-move/current TiaMcp.Engine.Harness and EXEs. Compare UTF-8 response text, masking the
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
import shutil
import unittest
import uuid


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
      for name in ('GetPlcWatchTableCurrentValuesReadOnly', 'ProbePlcMonitorOnlineCapabilities')
      for key in ('data', 'Data')),
    {'tool': 'GetPlcOpcUaConfiguration', 'path': ['data', 'timestamp'], 'reason': 'GetPlcOpcUaConfiguration DateTime.Now, direct serialization'},
    {'tool': 'GetPlcOpcUaConfiguration', 'path': ['Data', 'timestamp'], 'reason': 'GetPlcOpcUaConfiguration DateTime.Now, bridge serialization'}]


snapshots.RAW_MASK_RULES += [
    {'tool': 'EnsureUnifiedHmiButtonAction', 'path': [key, 'setMeta', 'timestamp'],
     'reason': 'SetUnifiedHmiButtonEventScriptCode nested step DateTime.Now'}
    for key in ('meta', 'Meta')]


snapshots.RAW_MASK_RULES += [
    {'tool': 'RunHmiActionScriptRecipeSafetySelfTest', 'path': [key, 'timestamp'],
     'reason': 'HmiActionScriptRecipeBuilder.RunSafetySelfTest wall clock'}
    for key in ('data', 'Data')]


ERROR_PATHS = {('"meta"', '"error"'), ('"Meta"', '"error"')}
# RunHmiStepTool writes ex.ToString() here. The local .NET Framework SDK uses
# the zh-CN frame prefix; keep this as narrow as the English "at " prefix.
STACK_FRAME = re.compile(r'^(?:[^\S\r\n]+(?:at |在 )|[^\S\r\n]*--- End of)')
STRING_UNIT = re.compile(r'\\u[0-9a-fA-F]{4}|\\["\\/bfnrt]|[^\\]')


def error_tokens(text, paths=ERROR_PATHS):
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
            elif path in paths and raw.startswith('"'):
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
HARDWARE = {'devicePath': ['DomainOfflineFixture'], 'itemPath': ['CPU']}
HARDWARE_V4 = {"devicePath": ["PlcOfflineFixture"], "itemPath": []}

CASES = {
    'HmiExchange': [(name, 'disconnected', dict(softwarePath=PLC, **arguments)) for name, arguments in (
        ('ListHmiScreens', {}), ('ListHmiTagTables', {}), ('ListHmiTags', {'tagTableName': 'Table'}),
        ('ListHmiConnections', {}), ('ExportHmiScreen', {'screenName': 'Main', 'exportPath': 'C:/domain-offline.xml'}),
        ('ExportHmiTagTable', {'tagTableName': 'Table', 'exportPath': 'C:/domain-offline.xml'}),
        ('ExportHmiConnection', {'connectionName': 'Connection', 'exportPath': 'C:/domain-offline.xml'}),
        ('ExportHmiProgram', {'exportDir': 'C:/domain-offline'}),
        ('ImportHmiScreen', {'folderPath': '', 'importPath': 'C:/domain-offline.xml'}),
        ('ImportHmiTagTable', {'folderPath': '', 'importPath': 'C:/domain-offline.xml'}),
        ('ImportHmiConnection', {'importPath': 'C:/domain-offline.xml'}),
        ('ImportHmiScreensFromDirectory', {'folderPath': '', 'dir': 'C:/domain-offline'}),
        ('ImportHmiTagTablesFromDirectory', {'folderPath': '', 'dir': 'C:/domain-offline'}))],
    'HmiDescribe': [(name, 'disconnected', dict(softwarePath=PLC, **arguments)) for name, arguments in (
        ('GetHmiProgramInfo', {}), ('DescribeHmiSoftware', {}), ('DescribeHmiScreen', {'screenName': 'Main'}),
        ('DescribeHmiTagTable', {'tagTableName': 'Table'}),
        ('DescribeHmiTag', {'tagTableName': 'Table', 'tagName': 'Tag'}), ('CompileHmiDiagnostics', {}),
        ('DescribeHmiScreenItem', {'screenName': 'Main', 'itemName': 'Item'}))],
    'HmiTagDeletion': [('DeleteHmiTag', 'disconnected', {
        'softwarePath': PLC, 'tagTablePath': 'Table', 'tagName': 'Tag'})],
    'HmiInspection': [
        ('ArchiveSavedProject', 'preview', {'archivePath': 'C:/domain-offline.zap21'}),
        ('GetUnifiedHmiButtonEvent', 'read', {'softwarePath': 'HMI', 'screenPath': '/Main', 'buttonName': 'Button1', 'eventType': 'Tapped'}),
        ('DeleteUnifiedHmiButtonEvent', 'preview', {'softwarePath': 'HMI', 'screenPath': '/Main', 'buttonName': 'Button1', 'eventType': 'Tapped'}),
        ('GetUnifiedHmiDynamization', 'read', {'softwarePath': 'HMI', 'screenPath': '/Main', 'itemName': 'Button1', 'propertyName': 'Left'}),
        ('DeleteUnifiedHmiDynamization', 'preview', {'softwarePath': 'HMI', 'screenPath': '/Main', 'itemName': 'Button1', 'propertyName': 'Left'}),
        ('DeleteEmptyUnifiedHmiScreenGroup', 'preview', {'softwarePath': 'HMI', 'groupPath': '/Group'}),
        ('GetHmiScreenSnapshot', 'read', {'softwarePath': 'HMI', 'screenPath': '/Main'}),
        ('ListHmiScreenPaths', 'read', {'softwarePath': 'HMI'})],
    'MigrationRead': [
        ('ListUnifiedGlobalScripts', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A'}),
        ('GetUnifiedGlobalScript', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'moduleName': 'Navigation'}),
        ('ListUnifiedTagDefinitions', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A'}),
        ('GetUnifiedScreenBranch', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'screenPath': '/Main'}),
        ('GetUnifiedLibraryType', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'typePath': '/Type1', 'version': '1.0.0'}),
        ('GetUnifiedFaceplateInstance', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'screenPath': '/Main', 'itemName': 'Faceplate1', 'typePath': '/Type1', 'version': '1.0.0'}),
        ('ListUnifiedLibraryFolderEntries', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A'}),
        ('ReleaseUnifiedReadCursor', 'release-missing', {'cursor': 'domain-offline'})],
    'RuntimeSettings': [
        ('GetUnifiedRuntimeSettings', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A'}),
        ('SetUnifiedRuntimeSettings', 'preview', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'changes': {'StartScreen': '/Main'}})],
    'GraphicSelection': [
        ('GetUnifiedGraphicSelection', 'read', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'screenPath': '/Main', 'itemNames': ['Button1']}),
        ('CompareUnifiedGraphicSelections', 'incomplete', {'beforePages': [], 'afterPages': []})],
    'GlobalScriptEdit': [
        ('SetUnifiedGlobalScript', 'preview', {'softwarePath': 'HMI', 'expectedProject': 'Project_A', 'moduleName': 'Navigation', 'scriptCode': 'export function Navigate() {}'})],
    'Cfc': actions('ExchangeCfcCharts', 'export selectiveExport import exportInstructionData',
        softwarePath=PLC, filePath='C:/cfc-offline-fixture.xml.zip', modelVersion='V2.0', chartNames=['Chart1'])
        + actions('ManageCfcChartProtection', 'read add change remove', softwarePath=PLC,
                  chartName='Chart1', currentPassword='offline', newHashedPassword='offline'),
    'TestSuite': [('ListTestSuiteCases', category, {'category': category})
                  for category in ('styleGuide', 'application', 'system')]
        + actions('ExchangeTestSuiteCase', 'export import importTestSets delete',
                  category='application', name='Case1', filePath='C:/domain-offline.tst')
        + [('RunTestSuiteCase', category, {'category': category, 'name': 'Case1'})
           for category in ('styleGuide', 'application', 'system')]
        + actions('ManageTestSuiteCase', 'read rename setScope copyScope createFromMasterCopy showInEditor',
                  category='styleGuide', name='Case1'),
    'V20Options': actions('ManageSinumerikArchive', 'archive retrieve fAddressArchive', filePath='C:/domain-offline.dsf')
        + [('ImportSinumerikAlarmTexts', 'import', {'devicePath': [], 'files': ['C:/domain-offline.ts']})]
        + actions('ManageSinumerikSafetyMode', 'read set', devicePath=[])
        + [('InitializeSimotionScripting', 'initialize', {}), ('ExportScadaData', 'export', {'filePath': 'C:/domain-offline.zip'})],
    'OptionalEngineering': [('ListSivarcRules', 'read', {'category': 'screens'})]
        + actions('ManageSivarcRule', 'create update delete', category='screens', collectionPath=[{'property': 'Tables', 'name': 'Table1'}, {'property': 'RuleGroups', 'name': 'Group1'}, {'property': 'Rules'}], name='Rule1'),
    'SpecializedExchange': actions('ExchangePlcSupervisions', 'export import importSettings',
                                  softwarePath=PLC, filePath='C:/domain-offline.xlsx'),
    'SoftwareUnitDeep': [('ListPlcSoftwareUnits', 'read', {'softwarePath': PLC})]
        + actions('ManagePlcSoftwareUnit', 'list read create createFromMasterCopy delete update createRelation deleteRelation',
                  softwarePath=PLC, name='Unit1')
        + actions('ManagePlcDocuments', 'list read export import createFromMasterCopy createFromLibraryType',
                  softwarePath=PLC, name='Document1')
        + [('GetPlcChecksums', 'read', {'softwarePath': PLC}),
           ('GetPlcObjectFingerprints', 'read', {'softwarePath': PLC, 'objectKind': 'block', 'objectPath': 'Block1'})]
        + actions('ManagePlcBlockWriteProtection', 'read define protect unprotect change remove',
                  softwarePath=PLC, blockPath='Block1')
        + actions('ManageProjectCompilationSettings', 'read update'),
    'Dcc': [('ListDccCharts', 'read', {'devicePath': [], 'itemPath': []})]
        + actions('ManageDccChart', 'read readSequence create update delete export exportAll import optimizeSequence showEditor',
                  devicePath=[], itemPath=[], chartName='Chart1')
        + actions('ManageDccBlock', 'read create update delete setAsPredecessor',
                  devicePath=[], itemPath=[], chartPath='Chart1', blockName='Block1')
        + actions('ManageDccPin', 'read update connect disconnect publish unpublish updateParameter',
                  devicePath=[], itemPath=[], chartPath='Chart1', blockName='Block1', pinName='Pin1')
        + actions('ManageDccChartInterface', 'read create update delete',
                  devicePath=[], itemPath=[], chartPath='Chart1', interfaceName='Interface1')
        + actions('ManageDccChartPartition', 'read create update delete',
                  devicePath=[], itemPath=[], chartPath='Chart1', partitionName='Partition1')
        + actions('ManageDcbLibraries', 'read import')
        + [('GetDccObject', 'read', {'devicePath': [], 'itemPath': []})],
    'Teamcenter': actions('ManageTeamcenterConnection', 'read connect connectSso disconnect')
        + actions('ManageTeamcenterDataset', 'checkout checkin cancelCheckout search download')
        + actions('ManageTeamcenterWorkflow', 'readCustomAttributes save saveWithProxyObject saveToItem saveToItemWithProxyObject '
                  'saveAsNewItem saveAsNewItemWithProxyObject saveAsNewRevision saveAsNewRevisionWithProxyObject'),
    'Startdrive': [('ListDriveObjects', 'read', {'devicePath': [], 'itemPath': []}),
                  ('GetDriveParameters', 'read', {'devicePath': [], 'itemPath': []})]
        + actions('ManageStartdriveParameter', 'read write', devicePath=[], itemPath=[],
                  driveObjectNumber=0, parameter='p2051[0]')
        + actions('ManageDriveTelegrams', 'read check insert erase changeNumber changeSize connectTechnologyObject',
                  devicePath=[], itemPath=[])
        + actions('ManageDriveFunctions', 'read changeDriveObjectType changeActivationState activateFunction deactivateFunction '
                  'setSIAxisType setMotorCode setSimoGearMlfb updateCheckSums setMotorType readMotorConfiguration '
                  'projectMotorConfiguration setEquivalentCircuitDiagramData setEncoder readEncoderConfiguration '
                  'setEncoderType projectEncoderConfiguration', devicePath=[], itemPath=[])
        + actions('ManageDriveSecurity', 'read activateUmac deactivateUmac activateEncryption deactivateEncryption',
                  devicePath=[], itemPath=[])
        + actions('ManageTechnologyExtensions', 'read activate deactivate readPackages install installAndGetIdentifier uninstall')
        + actions('ManageDriveHardwareModule', 'read changeType setPositionNumber', devicePath=[], itemPath=[])
        + actions('ManageDriveSafetyAcceptanceTest', 'read setActive resetTestFunctions createProtocol',
                  devicePath=[], itemPath=[])
        + [('GetOnlineDriveParameters', 'read', {'devicePath': [], 'itemPath': []})]
        + actions('ManageOnlineDriveFunctions', 'read performFactoryReset performRamToRomCopy changeActivationState',
                  devicePath=[], itemPath=[]),
    'SafetyManagement': actions('ManagePlcSafety',
        'read createRuntimeGroup deleteRuntimeGroup updateRuntimeGroup updateSettings generateGlobalFIOStatusBlock '
        'cleanSystemGeneratedObjects generateBaseId login logoff setPassword revokePassword', softwarePath=PLC)
        + actions('ManageSafetyGlobalSettings', 'read update')
        + [('GetSafetyBlockSignatures', 'read', {'softwarePath': PLC}),
           ('ExportSafetyPrintout', 'export', {'softwarePath': PLC, 'filePath': 'C:/domain-offline.pdf'})],
    'SafetyValidation': [('ListSafetyActivationTests', 'read', {})]
        + actions('ManageSafetyActivationTest',
                  'read create createFromTest createFromMasterCopy rename setAuthor changeEvaluationDevice '
                  'checkValidity generateReport export import delete', name='Test1')
        + actions('ManageSafetyActivationTestGroup', 'read create createFromMasterCopy rename delete')
        + actions('ManageSafetyFunction', 'read create createFrom update resetTestResult checkValidity setTrace checkTraceValidity export import delete',
                  activationTest='Test1')
        + actions('ManageSafetyFunctionCondition', 'read create update checkValidity delete', activationTest='Test1', safetyFunction='Function1'),
    'CertificateManagement': actions('ManagePlcCertificate', 'list read template create import export delete assign unassign',
                                   devicePath=['Device1'], itemPath=['CPU1']),
    'SecurityDeep': actions('ManageSyslogServers', 'read create update delete assignModule unassignModule')
        + [('ManageSyslogServers', 'plc/' + action, dict(scope='plc', action=action))
           for action in ('read', 'update', 'createServer', 'deleteServer')]
        + actions('ManagePasswordPolicy', 'read update')
        + [('ManageUmcUsers', kind + '/' + action, dict(kind=kind, action=action))
           for kind in ('user', 'group') for action in
           ('read', 'createOffline', 'importFromServer', 'rename', 'activate', 'deactivate', 'delete', 'assignRole', 'unassignRole')]
        + [('ManageUmcUsers', 'server/' + action, dict(kind='server', action=action))
           for action in ('read', 'checkConsistency', 'synchronize')],
    'ProjectSecurity': [('GetProjectUserManagement', category, {'category': category}) for category in
        ('users', 'anonymousUser', 'systemRoles', 'customRoles', 'engineeringRights', 'customDeviceRights',
         'umcUsers', 'umcUserGroups', 'passwordPolicy', 'deviceRights', 'roleDeviceRights')]
        + actions('ManageProjectUserManagement',
                  'createUser deleteUser setUserPassword activateUser deactivateUser assignRole unassignRole '
                  'createRole deleteRole assignEngineeringRight unassignEngineeringRight assignDeviceRight unassignDeviceRight '
                  'createDeviceRight deleteDeviceRight activateAnonymousUser deactivateAnonymousUser', name='User1')
        + [('GetProjectProtection', 'read', {})]
        + actions('ManageMultiuserSession', 'read listServerProjects readLockState listLocalSessions connectServer disconnectServer commit',
                  serverName='Server1', projectName='Project1', host='offline.invalid', port=443, commitComment='Offline preview')
        + [('CompareLibraries', 'read', {'leftLibraryName': 'Library1', 'rightLibraryName': 'Library2'}),
           ('GetProjectSettings', 'read', {})]
        + [('CompareProjects', kind, {'kind': kind, 'softwarePath': PLC})
           for kind in ('software', 'softwareToLibrary', 'hardware')],
    'PlcTables': [(name, 'disconnected', {'softwarePath': PLC}) for name in (
            'ListPlcTagTables', 'ListPlcWatchTables', 'ListPlcForceTables', 'ProbePlcMonitorOnlineCapabilities')]
        + [('ExportPlcTagTable', 'disconnected', dict(softwarePath=PLC, tagTableName='Tags', exportPath='C:/domain-tags.xml')),
           ('ImportPlcTagTable', 'disconnected', dict(softwarePath=PLC, folderPath='', importPath='C:/domain-tags.xml')),
           ('ImportPlcTagTablesFromDirectory', 'disconnected', dict(softwarePath=PLC, folderPath='', dir='C:/domain-tables')),
           ('SetPlcWatchTableModifyValue', 'disconnected', dict(softwarePath=PLC, tableName='Watch', address='%M0.0', modifyValue='TRUE')),
           ('ExportPlcWatchTable', 'disconnected', dict(softwarePath=PLC, watchTableName='Watch', exportPath='C:/domain-watch.xml')),
           ('ExportPlcWatchTablesToDirectory', 'disconnected', dict(softwarePath=PLC, dir='C:/domain-tables')),
           ('GetPlcWatchTableCurrentValuesReadOnly', 'disconnected', dict(softwarePath=PLC, watchTableName='Watch')),
           ('MonitorPlcWatchTableS7', 'disconnected', dict(softwarePath=PLC, watchTableName='Watch', ip='192.0.2.1')),
           ('ImportPlcWatchTableOffline', 'disconnected', dict(softwarePath=PLC, filePath='C:/domain-watch.xml'))]
        + [('GetPlcTagTableConstants', kind, dict(softwarePath=PLC, tablePath='Tags', kind=kind))
           for kind in ('all', 'user', 'system')]
        + actions('ManagePlcTableEntries', 'read createComment deleteEntry deleteTable',
                  softwarePath=PLC, tableKind='watch', tablePath='Watch', entryIndex=0, confirmDelete=True)
        + [('ManagePlcTableEntries', 'force-read', dict(softwarePath=PLC, tableKind='force', tablePath='Force'))]
        + [('PlanOnlineReadOnlyMonitoring', mode, dict(softwarePath=PLC, tagPaths=["DB_HMI.MotorRun"], mode=mode))
           for mode in ('current-values', 'watch-table-export-plan')]
        + [('PlanOnlineReadOnlyDataProvider', provider, dict(provider=provider, endpoint='opc.tcp://192.0.2.1:4840',
                                                          tagPaths=["DB_HMI.MotorRun"]))
           for provider in ('opcua', 's7-readonly')],
    'Library': [('GetLibraryOverview', 'read', {}), ('GetLibraryType', 'read', {'typePath': 'Type1'})]
        + actions('ManageLibraryType', 'update delete updateLibrary updateProject', typePath='Type1')
        + [('CheckLibraryUpdates', 'read', {})]
        + actions('SynchronizeLibrary', 'updateLibrary updateProject harmonizeProject cleanUp', selection=[{'folder': ''}])
        + [('CompareLibraryObjects', kind, {'kind': kind, 'leftPath': 'Left', 'rightPath': 'Right',
                                          'leftVersion': '1.0.0' if kind == 'version' else '',
                                          'rightVersion': '1.0.0' if kind == 'version' else ''})
           for kind in ('type', 'version', 'masterCopy')]
        + actions('ManageLibraryTypeVersion', 'read edit release setDefault deleteVersion updateInstances discard findInstances',
                  typePath='Type1', version='1.0.0')
        + [('CreateLibraryMasterCopy', kind, {'sourceKind': kind, 'sourcePath': 'Source', 'softwarePath': PLC})
           for kind in ('block', 'type', 'device', 'screen')]
        + actions('ManageLibraryMasterCopy', 'read copy compare delete', sourcePath='Copy1')
        + [('ImportLibraryTypeDocuments', 'import', {'filePath': 'C:/domain-offline.xml'})]
        + actions('ManageGlobalLibrary', 'list infos create open openInfo retrieve save saveAs close archive')
        + actions('ManageLibraryFolder', 'read create rename delete', folderKind='types', folderPath='Folder1'),
    'VersionControl': [('ListVersionControlWorkspaces', 'read', {}),
        ('CreateVersionControlWorkspace', 'create', {'workspaceName': 'Offline', 'folderPath': str(Path(__file__).resolve().parents[2])}),
        ('GetVersionControlStatus', 'read', {}),
        ('SynchronizeVersionControlWorkspace', 'export', {'direction': 'ProjectToWorkspace'}),
        ('SynchronizeVersionControlWorkspace', 'import-refusal', {'direction': 'WorkspaceToProject'}),
        ('ConnectProjectToWorkspace', 'map', {})],
    'Sivarc': [('GetSivarcRuleTree', category, {'category': category})
               for category in ('screens', 'tags', 'advancedTags', 'alarms', 'copies', 'textLists')]
        + actions('ManageSivarcRuleContainer', 'read create createFromType delete', category='screens', kind='table', path='Table1')
        + actions('ManageSivarcTableRule', 'read create createFromMasterCopy update delete', category='screens', tablePath='Table1')
        + [('ListSivarcBlockDefinitions', 'read', {'softwarePath': PLC, 'blockPath': 'Block1'})]
        + actions('ManageSivarcBlockDefinition', 'read create update delete', softwarePath=PLC, blockPath='Block1', kind='tagDefinition')
        + [('ResolveSivarcExpression', 'resolve', {'softwarePath': PLC, 'blockPath': 'Block1', 'devicePath': [],
            'itemPath': [], 'libraryItemKind': 'masterCopy', 'libraryItemPath': 'Copy1', 'expression': 'Block.Name'})]
        + actions('ManageSivarcScreenLayout', 'export import', softwarePath=PLC, screenName='Screen1', filePath='C:/domain-offline.xml')
        + [('UpgradeSivarcDefinitions', 'upgrade', {'softwarePath': PLC}),
           ('GenerateSivarc', 'generate', {'hmiDeviceName': 'HMI1', 'plcSoftwarePaths': ['PLC1'], 'generationOptions': 'None'})],
    'ClassicHmiFolders': [('GetClassicHmiScreenTree', kind, {'softwarePath': PLC, 'kind': kind})
                         for kind in ('all', 'screens', 'popups', 'templates', 'slideins')]
        + actions('ManageClassicHmiScreenObject', 'read export import delete', softwarePath=PLC,
                  objectKind='popup', objectPath='Screen1', filePath='C:/domain-offline.xml')
        + actions('ManageClassicHmiFolder', 'read create delete', softwarePath=PLC,
                  folderKind='scripts', folderPath='Folder1', newName='Folder2')
        + actions('ManageClassicHmiGraphic', 'list read export import delete', softwarePath=PLC,
                  name='Graphic1', filePath='C:/domain-offline.xml'),
    'MotionProDiagClassicHmi': [('GetMotionAxisConfiguration', 'read', {'softwarePath': PLC, 'objectPath': 'Axis1'})]
        + actions('ManageMotionAxis', 'read connectIdent', softwarePath=PLC, objectPath='Axis1')
        + actions('ManageMotionAxis', 'addMasterValue removeMasterValue', softwarePath=PLC,
                  objectPath='Axis1', aspect='synchronousSetPoint', name='Axis2')
        + actions('ManageMotionAxis', 'createMapping updateMapping deleteMapping', softwarePath=PLC,
                  objectPath='Axis1', aspect='toMapping', name='Mapping1')
        + actions('ManageMotionAxis', 'connect disconnect', softwarePath=PLC, objectPath='Axis1', aspect='actor')
        + actions('ManagePlcSupervision', 'read readComposition createEntry deleteEntry setAttributes exportSettings importSettings',
                  softwarePath=PLC, filePath='C:/domain-offline.dat')
        + [('ListClassicHmiScripts', 'read', {'softwarePath': PLC})]
        + actions('ManageClassicHmiScript', 'read export import delete createFolder deleteFolder setAttributes',
                  softwarePath=PLC, scriptPath='Script1', filePath='C:/domain-offline.xml')
        + actions('ManageClassicHmiCycle', 'read export import delete setAttributes',
                  softwarePath=PLC, cycleName='Cycle1', filePath='C:/domain-offline.xml')
        + actions('ManageClassicHmiTextGraphicList', 'read readEntries createEntry deleteEntry export import delete setAttributes',
                  softwarePath=PLC, listKind='text', listName='List1', filePath='C:/domain-offline.xml')
        + [('GetClassicHmiGlobalization', 'read', {'softwarePath': PLC}),
           ('ListClassicHmiFaceplates', 'read', {}),
           ('ExportPlcProDiagInfo', 'export', {'softwarePath': PLC, 'blockPath': 'Block1', 'directoryPath': 'C:/domain-offline'})],
    'Alarms': [(name, 'export', {'softwarePath': PLC, 'exportPath': 'C:/domain-offline.' + extension})
               for name, extension in (('ExportAlarmClasses', 'dat'), ('ExportAlarmTextLists', 'xlsx'), ('ExportAlarmInstanceTexts', 'xlsx'))]
        + [(name, 'import', {'softwarePath': PLC, 'importPath': 'C:/domain-offline.' + extension})
           for name, extension in (('ImportAlarmClasses', 'dat'), ('ImportAlarmTextLists', 'xlsx'))]
        + actions('ExchangePlcAlarmTextLists', 'export import', softwarePath=PLC, filePath='C:/domain-offline.xlsx')
        + [('ImportPlcAlarmInstanceTexts', 'import', {'softwarePath': PLC, 'filePath': 'C:/domain-offline.xlsx', 'cultures': ['en-US']})]
        + actions('ManagePlcAlarmTextList', 'read delete createFromMasterCopy', softwarePath=PLC, name='List1', libraryName='Library1', masterCopyPath='List1'),
    'OpcUa': [('GetPlcOpcUaConfiguration', 'read', {'softwarePath': PLC})]
        + actions('ManageOpcUaInterface', 'read delete', softwarePath=PLC, interfaceName='Interface1')
        + [('SetOpcUaInterfaceEnabled', 'set', {'softwarePath': PLC, 'interfaceName': 'Interface1', 'enabled': True}),
           ('ExportOpcUaInterface', 'export', {'softwarePath': PLC, 'interfaceName': 'Interface1', 'exportPath': 'C:/domain-offline.xml'}),
           ('ImportOpcUaInterface', 'import', {'softwarePath': PLC, 'importPath': 'C:/domain-offline.xml'}),
           ('GenerateOpcUaModelledInterface', 'generate', {'softwarePath': PLC, 'interfaceName': 'Interface1', 'namespaceUri': 'urn:domain:offline', 'outputPath': 'C:/domain-offline.xml'})]
        + [('GetOpcUaAccessControl', section, {'softwarePath': PLC, 'section': section}) for section in ('roles', 'restrictions')]
        + actions('ManageOpcUaAccessControl', 'createRole addStandardRole deleteRole setProjectRole setPermission setRestriction',
                  softwarePath=PLC, roleName='Role1', definedInNamespace='urn:domain:offline', projectRole='Role1', namespaceUri='urn:domain:offline'),
    'TechnologyObjects': [('ListTechnologyObjects', 'read', {'softwarePath': PLC}),
           ('ExportTechnologyObject', 'export', {'softwarePath': PLC, 'toName': 'Object1', 'exportPath': 'C:/domain-offline.xml'}),
           ('ExportTechnologyObjectsToDirectory', 'export', {'softwarePath': PLC, 'exportDir': 'C:/domain-offline'}),
           ('ImportTechnologyObject', 'import', {'softwarePath': PLC, 'folderPath': '', 'importPath': 'C:/domain-offline.xml'}),
           ('ImportTechnologyObjectsFromDirectory', 'import', {'softwarePath': PLC, 'folderPath': '', 'dir': 'C:/domain-offline'}),
           ('GetTechnologyObjectTree', 'read', {'softwarePath': PLC})]
        + actions('ManageTechnologyObject', 'read create delete setParameter', softwarePath=PLC, objectPath='Object1'),
    'Devices': [
        ('GetProjectTree', 'read', {}),
        ('GetDeviceInfo', 'read', {'devicePath': PLC}),
        ('GetDeviceItemInfo', 'read', {'deviceItemPath': PLC}),
        ('GetDeviceItemTree', 'read', {'deviceItemPath': PLC}),
        ('SetDeviceItemAttribute', 'write', {'deviceItemPath': PLC, 'attributeName': 'Name', 'value': 'Item1'}),
        ('ValidateAutomationContext', 'read', {}),
        ('SetPlcCpuSettings', 'write', {'cpuPath': PLC, 'settings': {'exactAttributes': {'Name': 'CPU'}}}),
        ('ListDevices', 'read', {}),
        ('CreateDevice', 'write', {'orderNumber': '6ES7513-1AM03-0AB0', 'version': 'V3.0', 'deviceName': PLC}),
        ('CreateHardwareDevice', 'write', {'preferredMlfb': '', 'preferredVersion': '', 'deviceName': PLC}),
        # Empty keywords refuse before scanning machine-local GSDML files.
        ('SearchInstalledGsdDevices', 'empty-keyword', {'keyword': ''}),
        ('SearchHardwareCatalog', 'read', {'keyword': 'CPU'}),
        ('CreateGsdDevice', 'empty-keyword', {'keyword': '', 'deviceName': PLC}),
        ('CreateHardwareCatalogDevice', 'write', {'keyword': 'CPU', 'deviceName': PLC})],
    'HardwareManagement': [('ManageHardwareObject', action, {'devicePath': ['Station1'], 'action': action,
        'itemPath': [] if action == 'deleteDevice' else ['CPU'], 'destinationDevicePath': ['Station2'],
        'destinationItemPath': [], 'position': 0}) for action in ('deleteDevice', 'deleteItem', 'moveItem', 'copyItem')],
    'HardwareAml': [
        ('ExportDeviceAml', 'export', {'devicePath': PLC, 'exportPath': 'C:/domain-offline.aml'}),
        ('ImportDeviceAml', 'import', {'filePath': 'C:/domain-offline.aml', 'logFilePath': 'C:/domain-offline.log'}),
        # Relative paths refuse before any file output; the offline executor still runs.
        ('BuildDeviceAmlDocument', 'relative-path', {'spec': {'projectName': 'P', 'devices': [{'name': 'D', 'typeIdentifier': 'System:Device.S71500', 'deviceItems': []}]}, 'outputPath': 'domain-offline.aml'})],
    'Modules': [('GetDevicePlugLocations', 'read', {'deviceItemPath': PLC}),
                ('PlugDeviceItem', 'preview', {'deviceItemPath': PLC, 'orderNumber': '6ES7521-1BL00-0AB0', 'version': 'V2.0'})],
    'Addresses': [('GetDeviceItemIoAddresses', 'read', {'deviceItemPath': PLC}),
                  ('SetDeviceItemIoAddress', 'preview', {'deviceItemPath': PLC, 'ioType': 'Input', 'startAddress': 2})],
    'HardwareNetwork': [('ListIoSystems', 'subnet', {'subnetName': 'PN/IE_1'}),
                        ('ListIoSystems', 'interface', HARDWARE_V4),
                        ('ListNetworkDomains', 'read', {'subnetName': 'PN/IE_1'}),
                        ('ListTransferAreas', 'read', HARDWARE_V4),
                        ('ListDeviceItemChannels', 'read', HARDWARE_V4),
                        ('SetDeviceItemChannel', 'update', dict(HARDWARE_V4, channelType='Digital',
                            channelIoType='Input', channelNumber=0, attributes={"ChannelAddress":0}))]
        + actions('ManageIoSystem', 'create delete update connect disconnect', **HARDWARE_V4, name='IO1')
        + [(tool, kind + '/' + case, arguments) for kind in ('sync', 'mrp')
           for tool, case, arguments in actions('ManageNetworkDomain', 'create delete update addParticipant',
               subnetName='PN/IE_1', kind=kind, name='Domain1')]
        + actions('ManageTransferArea', 'create delete update createMappingRule updateMappingRule deleteMappingRule',
                  **HARDWARE_V4, name='Area1', type='IN')
        + [(tool, 'multicast/' + case, arguments) for tool, case, arguments in actions('ManageTransferArea',
            'create createReceiver delete update', **HARDWARE_V4, kind='multicast', name='Area1', type='DDX')]
        + actions('ManageDeviceUserGroup', 'read create rename deleteEmpty', groupPath='Group1', newName='Group2')
        + [(tool, family + '/' + case, arguments) for family, values in (
            ('webserver', 'read create delete setPassword setPermissions'),
            ('simpleWebserver', 'read setPassword setPermissions setActive rename'),
            ('opcUa', 'read create delete setPassword'))
           for tool, case, arguments in actions('ManageDeviceUsers', values, **HARDWARE_V4, family=family,
               userName='User1', password='offline', newName='User2')]
        + actions('ManagePortInterconnection', 'read connect disconnect', **HARDWARE_V4),
    'HardwareServices': [('ListCommunicationConnections', 'read', HARDWARE_V4),
                         ('GetHardwareFeatures', 'read', HARDWARE_V4)]
        + actions('ManageCommunicationConnection', 'create delete', **HARDWARE_V4,
                  connectionType='S7Connection', connectionName='Connection1')
        + actions('ManageWatchForceTableWebAccess', 'read assign unassign', **HARDWARE_V4,
                  softwarePath=PLC, tablePath='Table1')
        + actions('ExchangeSystemDiagnosticsSettings', 'export import', filePath='C:/domain-offline.dat')
        + [(tool, family + '/' + case, arguments) for family, values in (
            ('webApplications', 'read setDefault'), ('telecontrolDataPoints', 'read update delete export import'),
            ('certificateServices', 'read update setServiceGroupName createService deleteService'))
           for tool, case, arguments in actions('ManageDeviceServiceObjects', values, **HARDWARE_V4,
               family=family, name='1', filePath='C:/domain-offline.xml')],
    'OnlineDownload': [(name, 'disconnected', {'softwarePath': PLC}) for name in (
        'GetOnlineState', 'DisconnectOnlinePlc', 'CompareSoftwareToOnline', 'CheckDownloadReadiness', 'DownloadPlc',
        'ListTransferRoutes')]
        + [('ConnectOnlinePlc', target or 'standard', {'softwarePath': PLC, 'rhTarget': target})
           for target in ('', 'primary', 'backup')]
        + [('DisconnectOnlinePlcs', 'disconnected', {}), ('ScanAccessibleDevices', 'disconnected', {}),
           ('UploadStationFromPlc', 'disconnected', {'targetIpAddress': '192.0.2.1'}),
           ('UploadDeviceParameters', 'disconnected', {'devicePath': [], 'itemPath': [],
                                                     'targetIpAddress': '192.0.2.1'}),
           ('DownloadPlcToFolder', 'disconnected', {'softwarePath': PLC,
                                                  'destinationDirectory': 'C:/domain-offline-card'})],
    'PlcSoftware': [(name, 'disconnected', {'softwarePath': PLC})
                      for name in ('GetSoftwareInfo', 'CompilePlcSoftware', 'GetSoftwareTree')],
    'Reflection': [
        ('DescribeObjectProperty', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC, 'propertyPath': 'Name'}),
        ('DescribeObject', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC}),
        ('GetObjectProperty', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC, 'propertyPath': 'Name'}),
        ('ListObjectChildren', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC, 'collectionProperty': 'Blocks'}),
        ('InvokeObject', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC, 'methodName': 'ToString'}),
        ('DescribeService', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC, 'serviceTypeSuffix': 'ICompilable'}),
        ('InvokeService', 'disconnected', {'objectKind': 'Software', 'objectPath': PLC, 'serviceTypeSuffix': 'ICompilable', 'methodName': 'ToString'})],
    'Export': [
        ('GetExportContent', 'missing', {'exportId': 'domain-missing'}),
        ('ListExportHandles', 'empty', {}),
        ('SaveExportContent', 'missing', {'exportId': 'domain-missing', 'outputPath': 'unused.xml'}),
        ('DeleteExportHandle', 'missing', {'exportId': 'domain-missing'}),
        ('ClearExportHandles', 'empty', {})],
    'EngineeringAudit': [('GetPlcBlockScopes', 'disconnected', {'softwarePath': PLC})]
        + actions('ManagePlcBlockDocuments', 'list read export import', softwarePath=PLC),
    'EngineeringDiagnostics': [('InspectSimaticSdCompatibility', 'invalid-version', {'filePath': '', 'tiaMajor': 19}),
           ('GetOpennessCompatibility', 'metadata', {}),
           ('GetNativeInvocationLog', 'invalid-count', {'take': 0})],
    'OfflineAnalysis': [
        ('ComparePlcBlockDocuments', 'invalid-page', {'offset': -1}),
        ('ScanPlcSourceAnnotations', 'invalid-page', {'directory': '', 'offset': -1}),
        ('ExtractPlcBlockMetrics', 'invalid-page', {'path': '', 'offset': -1})],
    'XmlBuilder': [(name, 'encoded-input', {parameter: '{'}) for name, parameter in (
        ('BuildClassicHmiScreen', 'design'), ('BuildPlcUdt', 'udt'),
        ('BuildPlcTagTable', 'tagTable'), ('BuildPlcGlobalDb', 'globalDb'),
        ('BuildStructuredText', 'structuredText'), ('BuildFlgNetCall', 'flgNet'),
        ('BuildPlcFcBlock', 'fcBlock'), ('BuildPlcFbBlock', 'fbBlock'),
        ('BuildPlcLadFcBlock', 'ladFcBlock'))],
    'PlcBuild': [('BuildAndImportPlcArtifact', 'invalid-kind', {'softwarePath': PLC, 'kind': 'invalid', 'spec': {'members': []}})],
    'SoftwareUnitManagement': [('SetPlcUnitObjectAccess', 'disconnected',
        {'softwarePath': PLC, 'unitName': 'Unit1', 'objectKind': 'block', 'objectPath': 'FC1', 'access': 'Published'})],
    'Types': [('GetPlcTypeInfo', 'read', {'softwarePath': PLC, 'typePath': 'Type1'}),
        ('ListPlcTypes', 'read', {'softwarePath': PLC}),
        ('ExportPlcType', 'export', {'softwarePath': PLC, 'typePath': 'Type1', 'exportPath': 'C:/domain-offline'}),
        ('ImportPlcType', 'import', {'softwarePath': PLC, 'groupPath': '', 'importPath': 'C:/domain-offline.xml'}),
        ('SeedProjectFromReference', 'seed', {'plcSoftwarePath': PLC, 'hmiSoftwarePath': 'HMI1', 'referenceDir': 'C:/domain-offline'}),
        # These two batch exporters return a variable elapsed duration even without a project.
        # Exercise their exact-name dispatch refusal without expanding the byte-mask allowlist.
        ('ExportPlcTypes', 'duplicate-argument', {'softwarePath': PLC, 'SoftwarePath': PLC, 'exportPath': 'C:/domain-offline'})],
    'Documents': [('ExportPlcBlockDocuments', 'export', {'softwarePath': PLC, 'blockPath': 'Block1', 'exportPath': 'C:/domain-offline'}),
        ('ExportPlcBlocksDocuments', 'duplicate-argument', {'softwarePath': PLC, 'SoftwarePath': PLC, 'exportPath': 'C:/domain-offline'}),
        ('ImportPlcBlockDocuments', 'invalid-option', {'softwarePath': PLC, 'groupPath': '', 'importPath': 'C:/domain-offline', 'fileNameWithoutExtension': 'Block1', 'importOption': 'invalid'}),
        ('ImportPlcBlocksDocuments', 'invalid-option', {'softwarePath': PLC, 'groupPath': '', 'importPath': 'C:/domain-offline', 'importOption': 'invalid'})],
    'PlcExternalSources': [('GetPlcCrossReferences', 'policy-refusal', {'softwarePath': PLC, 'objectPath': 'Block1'}),
        ('ListPlcExternalSources', 'read', {'softwarePath': PLC}),
        ('WritePlcSclSourceFile', 'empty-source', {'sclContent': ''}),
        ('ImportPlcExternalSource', 'import', {'softwarePath': PLC, 'groupPath': '', 'filePath': 'C:/domain-offline.scl'}),
        ('DeletePlcExternalSource', 'delete', {'softwarePath': PLC, 'externalSourceName': 'Source1'}),
        ('GenerateBlocksFromExternalSource', 'generate', {'softwarePath': PLC, 'externalSourceName': 'Source1'})]
        + actions('ManagePlcExternalSources', 'list read createGroup deleteGroup createFromFile createFromMasterCopy delete generateBlocks',
                  softwarePath=PLC, name='Source1', filePath='C:/domain-offline.scl')
        + [('ListPlcSystemGroups', 'read', {'softwarePath': PLC})],
    'PlcDocumentation': [('RenderPlcBlockDocument', 'missing-input', {}),
        ('GeneratePlcDocumentation', 'missing-output', {'directory': '', 'outputPath': ''}),
        ('AnalyzePlcSclSource', 'lint', {'sourceText': 'FUNCTION Test : Void\nBEGIN\nEND_FUNCTION'})],
    'NativeExchange': actions('ManageProjectLanguage', 'read activate deactivate setEditing setReference', culture='en-US')
        + [('CreatePlcInstanceDb', 'preview', {'softwarePath': PLC, 'fbPath': 'FB1', 'name': 'DB1'}),
           ('GeneratePlcSourceFromBlocks', 'preview', {'softwarePath': PLC, 'blockPaths': ['Block1'], 'filePath': 'C:/domain-offline.scl'}),
           ('GeneratePlcLoadableFile', 'preview', {'softwarePath': PLC, 'objectPaths': ['Block1'], 'objectKind': 'blocks', 'targetOption': '', 'filePath': 'C:/domain-offline.bin'}),
           ('RetrieveProjectArchive', 'preview', {'archivePath': 'C:/domain-offline.zap21', 'destinationDirectory': 'C:/domain-offline'}),
           ('ExportProjectTexts', 'preview', {'filePath': 'C:/domain-offline.xlsx', 'sourceCulture': 'en-US', 'targetCulture': 'de-DE'}),
           ('ImportProjectTexts', 'preview', {'filePath': 'C:/domain-offline.xlsx', 'updateSourceLanguage': False})]
        + actions('ManagePlcTagDefinition', 'read create update delete', softwarePath=PLC, tablePath='Table1', name='Tag1', kind='tag')
}


CASES['HardwareNetwork'] += [
    ('GetDeviceItemNetworkInfo', 'read', {'deviceItemPath': PLC}),
    ('ConnectDeviceNodesToProfinetSubnet', 'disconnected', {'firstRootPath': PLC, 'secondRootPath': 'HmiOfflineFixture'}),
    ('PlanHardwareNetworkConfiguration', 'invalid-plan', {'plan': {}}),
    ('EnsureSubnet', 'disconnected', {'anchorDeviceItemPath': PLC, 'subnetType': 'PROFINET', 'subnetName': 'PN_IE_1'}),
    ('AttachDeviceNodeToSubnet', 'disconnected', {'deviceItemPath': PLC, 'interfaceIndex': 0, 'subnetName': 'PN_IE_1'}),
    ('GetProjectTopology', 'read', {})]
CASES['HardwareNetwork'] += [(name, 'deep' if deep else 'direct',
    {'plcRootPath': PLC, 'hmiRootPath': 'HmiOfflineFixture', 'deepScan': deep})
    for name in ('ProbeHardwareHmiConnectionOwnerCandidates', 'ProbeHardwareHmiConnectionWhitelistedServices')
    for deep in (False, True)]
CASES['Addresses'] += [('GetDeviceAddressing', 'read', {'devicePath': ['Station1'], 'itemPath': ['CPU']}),
    ('SetDeviceAddress', 'preview', {'devicePath': ['Station1'], 'itemPath': ['CPU'], 'ioType': 'Input', 'startAddress': 0}),
    ('GetDeviceIpAddress', 'read', {'devicePath': PLC})]
CASES['Devices'] += [('GetDeviceAttributes', 'read', {'devicePath': PLC})]
CASES['Devices'] += [('SetPlcCpuSettings', 'shared-' + label, {'cpuPath': PLC, 'settings': settings})
    for label, settings in [('null', None), ('string', '{}'), ('empty', {'exactAttributes': {}}),
        ('unknown', {'exactAttributes': {'Name': 'CPU'}, 'unknown': True}),
        ('nested', {'exactAttributes': {'Name': {}}})]]
CASES['Devices'] += [('SetPlcCpuSettings', 'shared-case-duplicate',
    {'cpuPath': PLC, 'settings': {'exactAttributes': {'Name': 'CPU'}}, 'Settings': {}})]
CASES['HardwareAml'] += [('BuildDeviceAmlDocument', 'shared-' + label, {'spec': spec, 'outputPath': 'domain-offline.aml'})
    for label, spec in [('null', None), ('string', '{}'), ('unknown', {'projectName': 'P', 'devices': [], 'unknown': True})]]
CASES['Addresses'] += [('SetDeviceAddress', 'shared-' + field + '-' + label,
    {'devicePath': ['Station1'], 'itemPath': ['CPU'], 'ioType': 'Input', 'startAddress': 0, field: value})
    for field in ('properties', 'attributes') for label, value in [('null', None), ('string', '{}'), ('nested', {'Name': {}})]]
CASES['Addresses'] += [('GetDeviceAddressing', 'shared-' + label, {'devicePath': ['Station1'], 'itemPath': value})
    for label, value in [('path-null', None), ('path-string', '[]')]]
CASES['HardwareServices'] += actions('ManagePlcProtection',
    'read setAccessLevel setAccessPassword resetAccessPassword protectMasterSecret changeMasterSecret '
    'unprotectMasterSecret resetMasterSecret protectAllConfiguration unprotectAllConfiguration',
    **HARDWARE_V4, accessLevel='FullAccess', password='offline', newPassword='offline')
CASES['HardwareServices'] += [('CompileDevice', 'disconnected', HARDWARE_V4),
    ('GetPlcPutGetAccess', 'read', {'devicePath': PLC}),
    ('SetPlcPutGetAccess', 'enable', {'devicePath': PLC, 'enable': True}),
    ('SetPlcPutGetAccess', 'disable', {'devicePath': PLC, 'enable': False})]
CASES['HardwareServices'] += actions('ManageHardwareUtilities',
    'list findModuleTypes findContainerTypes normalizeTypeIdentifier exportOpcUa exportCardReaderPsc',
    **HARDWARE_V4, typeIdentifier='OfflineFixture', filePath='C:/domain-offline.xml')
CASES['MotionProDiagClassicHmi'] += actions('ExchangeMotionCamData',
    'import importBinary export exportBinary exportPoints', softwarePath=PLC,
    objectPath='Cam1', filePath='C:/domain-offline.cam', pointCount=1)
CASES['MotionProDiagClassicHmi'] += [(tool, kind + '/' + case, arguments)
    for kind in ('actor', 'sensor', 'torque') for tool, case, arguments in
    actions('ConfigureMotionHardwareConnection', 'read connect disconnect',
        softwarePath=PLC, objectPath='Axis1', interfaceKind=kind)]
# Missing paths are deterministic offline fixtures; no user library/template tree is read.
CASES['Library'] += [('ProbeGlobalLibrary', 'disconnected', {'libraryPath': ''}),
    ('ImportMasterCopyFromGlobalLibrary', 'disconnected', {'libraryPath': '', 'masterCopyName': 'Copy1',
        'hmiSoftwarePath': 'HmiOfflineFixture', 'screenName': 'Screen1'}),
    ('AnalyzeGlobalLibraryPackage', 'missing-path', {'libraryPath': 'C:/domain-offline-missing/library.al21'}),
    ('PlanGlobalLibraryTemplateReuse', 'missing-path', {'libraryPath': 'C:/domain-offline-missing/library.al21'}),
    ('AnalyzeHmiTemplateReference', 'missing-path', {'templateDirectory': '',
        'referenceProjectPath': '', 'referenceGlobalLibraryPath': ''}),
    ('AnalyzeUnifiedHmiTemplateLayout', 'empty-directory', {'templateDirectory': ''})]

snapshots.RAW_MASK_RULES += [
    {'tool': name, 'path': [key, 'timestamp'], 'reason': 'Global library probe DateTime.Now.ToString("O")'}
    for name in ('ProbeGlobalLibrary', 'ImportMasterCopyFromGlobalLibrary') for key in ('raw', 'Raw')]
snapshots.RAW_MASK_RULES += [
    {'tool': name, 'path': [key, 'timestamp'], 'reason': 'Offline template analysis DateTime.Now.ToString("O")'}
    for name in ('AnalyzeGlobalLibraryPackage', 'AnalyzeHmiTemplateReference', 'AnalyzeUnifiedHmiTemplateLayout')
    for key in ('data', 'Data')]
# Unified HMI: exact offline/disconnected fixtures for every moved tool.
CASES.update({'UnifiedHmi': [('SetUnifiedHmiRuntimeState', 'offline', {'hmiSoftwarePath': 'HMI_RT_1'}),
                ('EnsureUnifiedHmiScreen', 'offline', {'hmiSoftwarePath': 'HMI_RT_1', 'screenName': 'Main'}),
                ('EnsureUnifiedHmiTagTable', 'offline', {'hmiSoftwarePath': 'HMI_RT_1', 'tagTableName': 'Tags'}),
                ('EnsureUnifiedHmiTag',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1', 'tagTableName': 'Tags', 'tagName': 'Tag1'}),
                ('EnsureUnifiedHmiConnection', 'offline', {'hmiSoftwarePath': 'HMI_RT_1'}),
                ('EnsureUnifiedHmiScreenItem',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1', 'screenName': 'Main', 'itemName': 'Button1'}),
                ('GetUnifiedHmiTexts',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1', 'screenName': 'Main', 'itemName': 'Button1'}),
                ('ApplyUnifiedHmiScreenDesign',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1', 'screenName': 'Main', 'design': {'items': []}}),
                ('BuildUnifiedHmiThemeDesign', 'offline', {'theme': {'palette': {}}}),
                ('BuildUnifiedHmiLayoutDesign', 'offline', {'layout': {'items': []}}),
                ('ApplyUnifiedHmiTheme',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1', 'screenName': 'Main', 'theme': {'palette': {}}}),
                ('ApplyUnifiedHmiLayout',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1', 'screenName': 'Main', 'layout': {'items': []}}),
                ('BindUnifiedHmiButtonPressedTag',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'buttonName': 'Button1',
                  'tagName': 'Tag1'}),
                ('ListUnifiedHmiApiTypes', 'offline', {'nameContains': 'HmiButton', 'limit': 10}),
                ('EnsureUnifiedHmiButtonEventHandler',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'buttonName': 'Button1',
                  'eventType': 'Down'}),
                ('DescribeUnifiedHmiButtonEventScript',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'buttonName': 'Button1',
                  'eventType': 'Down'}),
                ('SetUnifiedHmiButtonEventScriptCode',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'buttonName': 'Button1',
                  'eventType': 'Down',
                  'scriptCode': 'export function Button1_OnDown(item, x, y, modifiers, trigger) {}'}),
                ('BuildUnifiedHmiButtonActionScript', 'offline', {'actionKind': 'set-bit', 'eventType': 'Down', 'targetTag': 'Tag1'}),
                ('RunHmiActionScriptRecipeSafetySelfTest', 'offline', {}),
                ('EnsureUnifiedHmiButtonAction',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'buttonName': 'Button1',
                  'eventType': 'Down',
                  'actionKind': 'set-bit',
                  'targetTag': 'Tag1'}),
                ('EnsureUnifiedHmiDynamization',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'itemName': 'Button1',
                  'propertyName': 'Visible'}),
                ('BindUnifiedHmiTagDynamization',
                 'offline',
                 {'hmiSoftwarePath': 'HMI_RT_1',
                  'screenName': 'Main',
                  'itemName': 'Button1',
                  'propertyName': 'Visible',
                  'tagName': 'Tag1'})],
 'UnifiedObjectServices': [('GetUnifiedPlantObject', 'offline', {}),
                           ('ManageUnifiedPlantNode', 'offline', {'plantPath': 'Plant/Node', 'action': 'create'}),
                           ('SetUnifiedPlantObject',
                            'offline',
                            {'plantPath': 'Plant/Node',
                             'objectPath': [],
                             'properties': {'Visible': True}}),
                           ('GetUnifiedObjectProperties', 'offline', {'softwarePath': 'HMI_RT_1'}),
                           ('SetUnifiedObjectProperties',
                            'offline',
                            {'softwarePath': 'HMI_RT_1',
                             'objectPath': [],
                             'properties': {'Visible': True}}),
                           ('SetUnifiedMultilingualProperty',
                            'offline',
                            {'softwarePath': 'HMI_RT_1',
                             'objectPath': [],
                             'property': 'Text',
                             'culture': 'en-US',
                             'rawText': 'Start'}),
                           ('ValidateUnifiedObject',
                            'offline',
                            {'softwarePath': 'HMI_RT_1', 'objectPath': []}),
                           ('GetUnifiedCrossReferences',
                            'offline',
                            {'softwarePath': 'HMI_RT_1', 'objectPath': []}),
                           ('ExportUnifiedEngineeringList',
                            'offline',
                            {'softwarePath': 'HMI_RT_1',
                             'category': 'textLists',
                             'name': 'Object1',
                             'destinationDirectory': 'C:/domain-unified-output'}),
                           ('ManageUnifiedLoggingTag',
                            'offline',
                            {'softwarePath': 'HMI_RT_1', 'tagPath': []}),
                           ('SetUnifiedLogDuration',
                            'offline',
                            {'softwarePath': 'HMI_RT_1',
                             'durationPath': [],
                             'kind': 'log',
                             'days': 0,
                             'hours': 0,
                             'minutes': 1,
                             'seconds': 0,
                             'hundredNanoseconds': 0}),
                           ('ManageUnifiedOpcUaAlarmType',
                            'offline',
                            {'softwarePath': 'HMI_RT_1',
                             'name': 'Object1',
                             'nodeId': 'ns=1;s=Alarm',
                             'connection': 'Connection1'})],
 'UnifiedUiModel': [('GetUnifiedObjectEvents', 'offline', {'softwarePath': 'HMI_RT_1', 'objectPath': []}),
                    ('ManageUnifiedObjectParts', 'offline', {'softwarePath': 'HMI_RT_1', 'objectPath': []}),
                    ('ManageUnifiedDynamization',
                     'offline',
                     {'softwarePath': 'HMI_RT_1', 'objectPath': [], 'propertyName': 'Visible'}),
                    ('ManageUnifiedScreenLayout', 'offline', {'softwarePath': 'HMI_RT_1', 'objectPath': []}),
                    ('ManageUnifiedListEntries',
                     'offline',
                     {'softwarePath': 'HMI_RT_1', 'category': 'textLists', 'listName': 'List1'}),
                    ('GetUnifiedAlarmCommon',
                     'offline',
                     {'softwarePath': 'HMI_RT_1', 'category': 'alarmClasses'}),
                    ('GetUnifiedAuditSettings',
                     'offline',
                     {'softwarePath': 'HMI_RT_1', 'category': 'auditTrails'})],
 'UnifiedScreenItems': [('DescribeUnifiedScreenItemType', 'offline', {'itemType': 'Button', 'depth': 1}),
                        ('ManageUnifiedScreenItem',
                         'offline',
                         {'softwarePath': 'HMI_RT_1', 'screenPath': 'Main'})],
 'UnifiedEngineering': [('ImportUnifiedEngineeringList',
                         'offline',
                         {'softwarePath': 'HMI_RT_1',
                          'category': 'textLists',
                          'filePath': 'C:/domain-unified.xml',
                          'expectedNames': []}),
                        ('ListUnifiedEngineeringObjects',
                         'offline',
                         {'softwarePath': 'HMI_RT_1', 'category': 'textLists'}),
                        ('ManageUnifiedEngineeringObject',
                         'offline',
                         {'softwarePath': 'HMI_RT_1',
                          'category': 'textLists',
                          'name': 'Object1',
                          'action': 'create'})],
 'UnifiedExchange': [('ExchangeUnifiedTags',
                      'offline',
                      {'softwarePath': 'HMI_RT_1', 'action': 'export', 'directory': 'C:/domain-unified-exchange'}),
                     ('ExchangeUnifiedScriptModules',
                      'offline',
                      {'softwarePath': 'HMI_RT_1', 'action': 'export', 'directory': 'C:/domain-unified-exchange'}),
                     ('ImportUnifiedOpcUaAlarms',
                      'offline',
                      {'softwarePath': 'HMI_RT_1', 'connectionName': 'Connection1'})],
 'UnifiedEvents': [('ManageUnifiedEvent',
                    'offline',
                    {'softwarePath': 'HMI_RT_1', 'objectPath': [], 'eventType': 'Down'})],
 'UnifiedHmiGroups': [('ManageUnifiedHmiGroup',
                       'offline',
                       {'softwarePath': 'HMI_RT_1',
                        'family': 'screens',
                        'groupPath': 'Screens',
                        'action': 'create'})]})


# Older hardware tools mix thrown MCP errors, failure POCOs and empty inventories.
# Require a tool-specific terminal marker before comparing every response byte.
HARDWARE_TERMINALS = {
    'GetDeviceItemNetworkInfo': 'Device item not found',
    'ConnectDeviceNodesToProfinetSubnet': 'Project is null',
    'PlanHardwareNetworkConfiguration': 'Hardware network plan has validation errors',
    'EnsureSubnet': 'Project is null',
    'AttachDeviceNodeToSubnet': 'Project is null',
    'ProbeHardwareHmiConnectionOwnerCandidates': 'Project is null',
    'ProbeHardwareHmiConnectionWhitelistedServices': 'Project is null',
    'GetProjectTopology': 'No project open.',
    'GetDeviceIpAddress': 'No project open.',
    'GetDeviceAttributes': 'No project open.',
    'GetPlcPutGetAccess': 'No project open.',
    'SetPlcPutGetAccess': 'No project open.',
    'ProbeGlobalLibrary': 'TIA Portal is not connected. Call ConnectPortal first.',
    'ImportMasterCopyFromGlobalLibrary': 'TIA Portal is not connected. Call ConnectPortal first.',
    'AnalyzeGlobalLibraryPackage': 'Global library directory not found.',
    'PlanGlobalLibraryTemplateReuse': 'Global library template reuse plan blocked because the library path was not found.',
    'AnalyzeHmiTemplateReference': 'HMI template/reference offline analysis completed with findings',
    'AnalyzeUnifiedHmiTemplateLayout': 'Unified HMI template layout offline QA completed',
    'GetProjectTree': 'Failed retrieving project tree',
    'GetDeviceInfo': 'Device not found',
    'GetDeviceItemInfo': 'Device item not found',
    'GetDeviceItemTree': 'Device item not found',
    'SetDeviceItemAttribute': 'Project is null',
    'ValidateAutomationContext': 'Automation context invalid',
    'SetPlcCpuSettings': 'Project is null',
    'ListDevices': 'Devices retrieved',
    'CreateDevice': 'No project is open',
    'CreateHardwareDevice': 'Failed to add device',
    'SearchInstalledGsdDevices': 'Keyword is empty',
    'SearchHardwareCatalog': 'HardwareCatalog is not available',
    'CreateGsdDevice': 'Keyword is empty',
    'CreateHardwareCatalogDevice': 'HardwareCatalog is not available',
    'ExportDeviceAml': 'No project is open',
    'BuildDeviceAmlDocument': 'Absolute output file path required',
    'GetDevicePlugLocations': '的槽位：要么没有连接项目',
    'PlugDeviceItem': 'NotConnected',
    'GetDeviceItemIoAddresses': '的地址：要么没有连接项目',
    'SetDeviceItemIoAddress': '的地址：要么没有连接项目',
}
# No runtime endpoint is opened: empty inputs are refused before channel calls.
# The CPU probe/state and PLCSIM entry points have no deterministic offline body
# refusal (PLCSIM probes the installed API and records elapsed time), so use the
# existing duplicate-argument gate before invocation, including through CallTool.
CASES['Runtime'] = [
    ('ProbeS7CpuIdentity', 'duplicate-argument', {'ip': '', 'IP': ''}),
    ('GetPlcLiveValuesS7', 'empty-addresses', {'ip': '', 'items': []}),
    ('SamplePlcLiveValuesS7', 'empty-addresses', {'ip': '', 'items': []}),
    ('TraceTagCause', 'duplicate-argument', {'softwarePath': '', 'SOFTWAREPATH': '', 'tag': ''}),
    ('TraceTagCauseLive', 'empty-ip', {'softwarePath': '', 'tag': '', 'ip': ''}),
    ('GetPlcLiveValuesOpcUa', 'empty-nodes', {'endpointUrl': '', 'nodeIds': []}),
    ('GetPlcRunStateS7', 'duplicate-argument', {'ip': '', 'IP': ''}),
]
CASES['RuntimeChannel'] = [
    ('GetPlcWebVars', 'empty-host', {'host': '', 'username': '', 'password': '', 'vars': []}),
    ('WritePlcWebVars', 'empty-host', {'host': '', 'username': '', 'password': '', 'writes': []}),
    ('GetPlcWebDiagnostics', 'empty-host', {'host': '', 'username': '', 'password': ''}),
    ('SetPlcWebOperatingMode', 'empty-host', {'host': '', 'username': '', 'password': '', 'mode': 'RUN'}),
    ('GetUnifiedRuntimeTags', 'empty-tags', {'tags': []}),
    ('WriteUnifiedRuntimeTags', 'empty-writes', {'writes': []}),
    ('GetUnifiedRuntimeAlarms', 'invalid-language', {'languageId': 0}),
    ('InvokeUnifiedOpenPipe', 'invalid-request', {'request': {}}),
]
CASES['PlcSimAdvanced'] = [
    (name, 'duplicate-argument', {'apiPath': '', 'APIPATH': ''}) for name in (
        'ListPlcSimAdvancedInstances', 'ManagePlcSimAdvancedInstance',
        'GetPlcSimAdvancedTags', 'WritePlcSimAdvancedTags', 'RunPlcSimAdvancedTestScenario')
]


def runtime_reply(reply, profile, name, case):
    resources.require('result' in reply, f'{name}: missing runtime refusal: {reply}')
    result = reply['result']
    raw = result['content'][0]['text']
    body = json.loads(raw)
    resources.require(body['schemaVersion'] == 4 and body['ok'] is False and result['isError'] is True, raw)
    resources.require(body['meta']['outcome'] == 'rejected-before-operation'
                      and body['meta']['execution'] == 'not-started', raw)
    resources.require(body['error']['code'] in ('INVALID_ARGUMENT', 'PROJECT_NOT_BOUND', 'PRECONDITION_FAILED'), raw)
    resources.require(result.get('structuredContent') == body, raw)
    return raw







HARDWARE_THROWS = {
    'GetDeviceItemNetworkInfo',
    'GetProjectTree', 'GetDeviceInfo', 'GetDeviceItemInfo', 'GetDeviceItemTree',
    'CreateDevice', 'SearchInstalledGsdDevices', 'SearchHardwareCatalog',
    'CreateGsdDevice', 'CreateHardwareCatalogDevice', 'ExportDeviceAml',
    'GetDevicePlugLocations', 'PlugDeviceItem', 'GetDeviceItemIoAddresses', 'SetDeviceItemIoAddress',
}


CASES['PlcBlocks'] = [
    ('GetPlcBlockInfo', 'read', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('ListPlcBlocks', 'read', {'softwarePath': PLC}),
    ('GetPlcBlockHierarchy', 'read', {'softwarePath': PLC}),
    ('ExportPlcBlock', 'export', {'softwarePath': PLC, 'blockPath': 'Group/Block1', 'exportPath': 'C:/domain-offline'}),
    ('ImportPlcBlock', 'import', {'softwarePath': PLC, 'groupPath': '', 'importPath': 'C:/domain-offline.xml'}),
    ('ImportPlcBlocksFromDirectory', 'import', {'softwarePath': PLC, 'groupPath': '', 'dir': 'C:/domain-offline'}),
    ('ImportPlcProgramFromDirectory', 'import', {'softwarePath': PLC, 'sourceDir': 'C:/domain-offline'}),
    ('CompilePlcDiagnostics', 'compile', {'softwarePath': PLC}),
    ('RepairAndReimportPlcBlock', 'import', {'softwarePath': PLC, 'importPath': 'C:/domain-offline.xml'}),
    ('ExportPlcBlocks', 'missing-required-argument', {'exportPath': 'C:/domain-offline'}),
    ('DescribePlcBlockLogic', 'read', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('SetPlcProgram', 'preview', {'softwarePath': PLC}),
    ('GetPlcBlockFingerprints', 'preview', {'softwarePath': PLC, 'targetIpAddress': '192.0.2.1'}),
    ('GetPlcBlockEditCapabilities', 'read', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('AnalyzePlcReferences', 'missing-directory', {'directory': 'C:/domain-offline'}),
    ('PatchPlcBlockDocument', 'invalid-changes', {'filePath': 'C:/domain-offline.xml', 'changes': [], 'expectedFingerprint': 'none'}),
    ('ImportPlcBlockVerified', 'preview', {'softwarePath': PLC, 'blockPath': 'Group/Block1', 'importPath': 'C:/domain-offline.xml', 'evidenceDirectory': 'C:/domain-evidence'}),
    ('DeletePlcBlock', 'preview', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('DeletePlcTagTable', 'preview', {'softwarePath': PLC, 'tagTableName': 'Table1'}),
    ('DeletePlcType', 'preview', {'softwarePath': PLC, 'typePath': 'Type1'}),
    ('CreatePlcTypeGroup', 'preview', {'softwarePath': PLC, 'groupPath': 'Group'}),
    ('DeleteEmptyPlcBlockGroup', 'preview', {'softwarePath': PLC, 'groupPath': 'Group'}),
    ('CreatePlcBlockGroup', 'create', {'softwarePath': PLC, 'groupPath': 'Group'}),
    ('MovePlcBlockToGroup', 'move', {'softwarePath': PLC, 'blockName': 'Block1', 'targetGroupPath': 'Group'})
] + actions('ManagePlcBlockProtection', 'read protect unprotect', softwarePath=PLC, blockPath='Group/Block1') \
  + actions('ManagePlcDataBlockSnapshot', 'read createSnapshot loadSnapshotAsActualValues loadStartValuesAsActualValues exportSnapshot', softwarePath=PLC, blockPath='Group/Block1') \
  + actions('ManagePlcUserGroup', 'create rename deleteEmpty', softwarePath=PLC, family='blocks', groupPath='Group')


# Connection/group tools use duplicate-argument rejection before their bodies;
# no case may attach, start TIA, or change Windows group membership.
CASES['Session'] = [
    (name, 'duplicate-argument', {'probe': True, 'PROBE': False})
    for name in ('ConnectPortal', 'ConnectIsolatedPortal', 'ListPortalProcessProjects', 'EnsureOpennessUserGroup')
] + [
    ('GetSessionState', 'disconnected', {}),
    ('DisconnectPortal', 'disconnected', {}),
    ('InitializeEnvironment', 'offline', {}),
    ('ConnectProject', 'invalid-identity', {'processId': -1, 'processStartUtc': 'invalid', 'projectPath': ''}),
    ('GetPortalInfo', 'disconnected', {'includeProcesses': False, 'includeSessions': False, 'includeProducts': False}),
]
CASES['ProjectSession'] = [
    ('GetProjectInfo', 'disconnected', {}),
    ('OpenProject', 'invalid-extension', {'path': 'offline.invalid'}),
    ('AttachOpenProject', 'empty-name', {'projectName': ''}),
    ('CreateProject', 'disconnected', {'directoryPath': 'C:/domain-offline', 'projectName': 'Offline'}),
    ('BuildProjectScaffold', 'preview', {'spec': '{"projectName":"Offline","directoryPath":"C:/domain-offline"}', 'dryRun': True}),
    ('SaveProject', 'disconnected', {}),
    ('SaveProjectCopy', 'disconnected', {'newProjectPath': 'C:/domain-offline.ap21'}),
    ('CloseProject', 'disconnected', {}),
    ('GetObjectIdentifier', 'disconnected', {}),
    ('ShowObjectInEditor', 'preview', {'dryRun': True}),
    ('RunToolTransaction', 'preview', {'calls': [{'name': 'CreatePlcTypeGroup', 'arguments': {'softwarePath': 'PLC_1', 'groupPath': 'Offline'}}], 'text': 'Offline preview', 'dryRun': True}),
]
# Both optional native probes stay disabled, including in the acceptance report.
CASES['Diagnostics'] = [
    ('RunCapabilitySelfTest', 'offline', {'connectIfNeeded': False, 'includeProjectTree': True, 'inspectPortalProcesses': False}),
    ('RunOnlineMonitoringSafetySelfTest', 'offline', {}),
    ('GenerateAcceptanceReport', 'offline-report', {'connectIfNeeded': False, 'includeProjectTree': True,
        'inspectPortalProcesses': False, 'title': 'Domain offline acceptance', 'outputDirectory': '<report-directory>'}),
    ('GenerateErrorReport', 'offline-report', {'errorCode': 'NotConnected', 'summary': 'Offline domain report',
        'detail': 'Literal clock-like data 2026-10-03T12:34:56+00:00 and 20261003_123456 must be preserved.',
        'outputDirectory': '<report-directory>'}),
]
# These fields are DateTime.Now in GenerateAcceptanceReport and its two self-tests.
snapshots.RAW_MASK_RULES += [
    {'tool': 'GenerateAcceptanceReport', 'path': [parent, meta, 'timestamp'],
     'reason': 'Nested diagnostic response DateTime.Now'}
    for parent, meta in (('selfTest', 'meta'), ('safetySelfTest', 'meta'), ('SelfTest', 'Meta'), ('SafetySelfTest', 'Meta'))
]
REPORT_TOOLS = {'GenerateAcceptanceReport', 'GenerateErrorReport'}
REPORT_CLOCK_PATHS = {(json.dumps(key),) for key in (
    'operationId', 'OperationId', 'markdownPath', 'MarkdownPath', 'jsonPath', 'JsonPath', 'GeneratedAt')}


def mask_report_json(raw, name):
    raw = snapshots.mask_raw_text(raw, name)
    if name not in REPORT_TOOLS:
        return raw
    # OperationId is DateTime.Now.ToString("yyyyMMdd_HHmmss"), not a random ID.
    # Only that clock value and its derived filename suffix are masked. Directory,
    # filename prefix/extension, payload timestamps and all other bytes are retained.
    for path, token in reversed(error_tokens(raw, REPORT_CLOCK_PATHS)):
        value = token.group()
        if path[0] in ('"operationId"', '"OperationId"'):
            value = re.sub(r'^"\d{8}_\d{6}"$', '"<clock:operationId>"', value)
        elif path[0] == '"GeneratedAt"':
            value = snapshots.RAW_TIMESTAMP.sub('<string:timestamp>', value)
        else:
            value = re.sub(r'_\d{8}_\d{6}(?=\.(?:md|json)"$)', '_<clock:operationId>', value)
        raw = raw[:token.start()] + value + raw[token.end():]
    return raw


def mask_report_markdown(raw):
    lines = raw.splitlines(keepends=True)
    resources.require(len(lines) > 3, 'Report Markdown header missing')
    # Only the two generated header rows; clock-like user details are never masked.
    lines[2], count = re.subn(r'^- OperationId: `\d{8}_\d{6}`', '- OperationId: `<clock:operationId>`', lines[2])
    resources.require(count == 1, 'Report OperationId header changed')
    lines[3], count = re.subn(r'^- GeneratedAt: `\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)`',
                            '- GeneratedAt: `<string:timestamp>`', lines[3])
    resources.require(count == 1, 'Report GeneratedAt header changed')
    return ''.join(lines)


def diagnostics_reply(reply, profile, name, arguments):
    raw = v4_reply(reply, name)
    value = resources.envelope(reply)['data']
    get = lambda obj, key: obj.get(key[0].lower() + key[1:], obj.get(key))
    if name in ('RunCapabilitySelfTest', 'GenerateAcceptanceReport'):
        selftest = value if name == 'RunCapabilitySelfTest' else get(value, 'SelfTest')
        items = {get(item, 'Id'): item for item in get(selftest, 'Items')}
        resources.require(get(items['tia.processes'], 'Status') == 'skip'
                          and get(items['tia.connection'], 'Status') == 'warn'
                          and get(selftest, 'ProjectTree') is None,
                          f'{name}: disconnected/native-probe guards changed: {raw}')
    artifacts = {}
    if name in REPORT_TOOLS:
        operation = get(value, 'OperationId')
        resources.require(re.fullmatch(r'\d{8}_\d{6}', operation) is not None, 'Report operation ID is not the expected clock')
        directory = Path(arguments['outputDirectory']).resolve()
        resources.require(Path(get(value, 'OutputDirectory')).resolve() == directory, 'Report output directory changed')
        prefix = 'tia_mcp_acceptance_' if name == 'GenerateAcceptanceReport' else 'tia_mcp_error_NotConnected_'
        for key, suffix in (('MarkdownPath', '.md'), ('JsonPath', '.json')):
            path = Path(get(value, key)).resolve()
            resources.require(path.parent == directory and path.name == prefix + operation + suffix,
                              f'{name}: report filename is not derived from its operation clock: {path}')
            content = path.read_bytes().decode('utf-8')
            artifacts[key] = (mask_report_markdown(content) if suffix == '.md' else mask_report_json(content, name)).encode('utf-8')
    return mask_report_json(raw, name), artifacts


SESSION_TERMINALS = {
    'GetSessionState': 'TIA-Portal MCP server state retrieved', 'DisconnectPortal': 'Disconnected from TIA-Portal',
    'InitializeEnvironment': 'RecommendedNextTool', 'ConnectProject': "An error occurred invoking 'ConnectProject'.",
    'GetPortalInfo': 'Portal diagnostics read; no modification.', 'GetProjectInfo': 'Open projects and sessions retrieved',
    'OpenProject': 'Invalid project file extension', 'AttachOpenProject': 'projectName is required',
    'CreateProject': 'Failed to create project', 'BuildProjectScaffold': "BuildProjectScaffold dryRun 'Offline': 0 ok, 0 failed",
    'SaveProject': 'Failed to save project', 'SaveProjectCopy': 'Failed saving local project',
    'CloseProject': 'Failed closing project', 'GetObjectIdentifier': 'Project is null',
    'ShowObjectInEditor': 'Project is null', 'RunToolTransaction': 'Transaction preview: 1 call(s) validated',
}
SESSION_THROWS = {'ConnectProject', 'OpenProject', 'AttachOpenProject', 'CreateProject',
                  'SaveProject', 'SaveProjectCopy', 'CloseProject'}


def v4_reply(reply, name):
    try:
        value = resources.envelope(reply)
    except ValueError as error:
        resources.require(False, f'{name}: {error}')
    resources.require(value['meta'].get('tool') in (name, 'CallTool'), f'{name}: wrong result identity')
    resources.require(value['meta'].get('outcome') in ('succeeded', 'rejected-before-operation',
                      'read-failed', 'failed', 'partial', 'unknown'), f'{name}: missing V4 outcome')
    if not value['ok']:
        resources.require(value['error'].get('code') not in (None, 'INTERNAL_ERROR'),
                          f'{name}: unrelated dispatch/internal failure: {value}')
    return reply['result']['content'][0]['text']


def session_reply(reply, profile, name, case):
    return v4_reply(reply, name)


def plc_v4_reply(reply, name):
    resources.require('result' in reply, f'{name}: missing tools/call result: {reply}')
    result = reply['result']
    content = result.get('content', [])
    resources.require(len(content) == 1 and 'text' in content[0], f'{name}: expected one JSON text block')
    raw = content[0]['text']
    value = json.loads(raw)
    resources.require(value.get('schemaVersion') == 4, f'{name}: missing V4 envelope: {raw}')
    resources.require(result.get('structuredContent') == value, f'{name}: structured/text content differ')
    resources.require(isinstance(value.get('ok'), bool) and result.get('isError') is (not value['ok']),
                      f'{name}: inconsistent MCP and domain verdicts')
    meta = value.get('meta', {})
    resources.require(meta.get('tool') == name, f'{name}: wrong envelope tool identity')
    resources.require(meta.get('outcome') in ('succeeded', 'failed', 'partial', 'unknown',
                      'rejected-before-operation', 'read-failed'), f'{name}: missing outcome')
    resources.require((value.get('error') is None) is value['ok'], f'{name}: inconsistent error')
    if name.startswith('PlanOnlineReadOnly'):
        resources.require(value['ok'] and value.get('data', {}).get('readOnly') is True,
                          f'{name}: offline read-only plan failed: {raw}')
    else:
        resources.require(value['ok'] is False, f'{name}: disconnected/invalid request cannot succeed')
    return raw


def plc_block_reply(reply, profile, name):
    return plc_v4_reply(reply, name)


SOFTWARE_REPLY_MARKERS = {
    'GetSoftwareInfo': 'Software not found', 'CompilePlcSoftware': 'Project is null',
    'GetSoftwareTree': 'no project is open',
    **{name: 'not found' for name in ('DescribeObjectProperty', 'DescribeObject', 'ListObjectChildren',
                                    'InvokeObject', 'DescribeService', 'InvokeService')},
    'GetObjectProperty': 'Null',
    'InspectSimaticSdCompatibility': ("An error occurred invoking 'InspectSimaticSdCompatibility'", 'tiaMajor must be 20/21'),
    'GetOpennessCompatibility': 'installedPatch', 'GetNativeInvocationLog': 'take',
    **{name: 'InvalidParams' for name in ('ComparePlcBlockDocuments', 'ScanPlcSourceAnnotations', 'ExtractPlcBlockMetrics')},
    **{name: 'INVALID_ARGUMENT' for name, _, _ in CASES['XmlBuilder']},
    'BuildAndImportPlcArtifact': 'INVALID_ARGUMENT'
}


def software_reply(reply, profile, name):
    return v4_reply(reply, name)



SOURCE_TOOL_GUARDS = {
    'GetPlcTypeInfo': 'Type not found', 'ListPlcTypes': 'No TIA project is open',
    'ExportPlcType': 'No project is open', 'ImportPlcType': 'No project is open',
    'SeedProjectFromReference': 'Project is null',
    'RenderPlcBlockDocument': 'Exactly one of filePath or blockPath',
    'GeneratePlcDocumentation': 'Absolute output file path required', 'AnalyzePlcSclSource': 'findingCount',
    'RetrieveProjectArchive': 'ConnectPortal to TIA first.'
}


def source_reply(reply, profile, name, case):
    return v4_reply(reply, name)


def table_reply(reply, profile, name):
    return plc_v4_reply(reply, name)


def unified_reply(reply, profile, name):
    return v4_reply(reply, name)
def hmi_reply(reply, profile, name):
    return v4_reply(reply, name)






PLC_EXCHANGE_DOMAINS = ('Documents', 'PlcExternalSources', 'NativeExchange', 'Export')


def plc_exchange_reply(reply, name):
    result = reply['result']
    resources.require(len(result['content']) == 1, f'{name}: expected one text envelope')
    raw = result['content'][0]['text']
    body = json.loads(raw)
    resources.require(body.get('schemaVersion') == 4 and result.get('structuredContent') == body,
                      f'{name}: inconsistent V4 envelope: {reply}')
    resources.require(bool(result.get('isError')) is not body['ok'], f'{name}: inconsistent error verdict: {reply}')
    expected_success = name in ('ListExportHandles', 'ClearExportHandles')
    resources.require(body['ok'] is expected_success, f'{name}: unexpected offline outcome: {reply}')
    if expected_success:
        resources.require(body['meta']['outcome'] == 'succeeded', raw)
    elif name in ('ListPlcExternalSources', 'RetrieveProjectArchive'):
        resources.require(body['meta']['outcome'] == 'read-failed'
                          and body['meta']['execution'] == 'read-only' and body['error'] is not None, raw)
    else:
        resources.require(body['meta']['outcome'] == 'rejected-before-operation'
                          and body['meta']['execution'] == 'not-started' and body['error'] is not None, raw)
    return raw


def check_coverage(domains):
    from engine_sources import EngineSources
    sources = EngineSources()
    for domain in domains:
        source = sources.type_text(domain + 'Tools')
        actual = set(re.findall(r'\[McpServerTool\(Name\s*=\s*"(\w+)"', source))
        covered = {name for name, _, _ in CASES[domain]}
        resources.require(actual == covered, f'{domain} fixture coverage differs: actual={actual}, covered={covered}')


THROWING_GUARDS = {
    'ListTechnologyObjects': 'ListTechnologyObjects: no project is open. Call ConnectPortal + OpenProject (or AttachOpenProject) first.',
    'ImportTechnologyObject': "Failed importing technology object from 'C:/domain-offline.xml' [InvalidState]: No project is open."
}
SIMPLE_GUARDS = {'ExportAlarmClasses', 'ImportAlarmClasses', 'ExportAlarmTextLists', 'ImportAlarmTextLists',
                 'ExportAlarmInstanceTexts', 'GetPlcOpcUaConfiguration', 'SetOpcUaInterfaceEnabled', 'ExportOpcUaInterface',
                 'ImportOpcUaInterface', 'ExportTechnologyObject'}


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
                if domain == 'Diagnostics' and name in REPORT_TOOLS:
                    arguments = dict(arguments, outputDirectory=str(args.report_directory / name))
                hidden = args.major == 20 and (name in ('ManagePlcBlockWriteProtection', 'ManageDriveSafetyAcceptanceTest',
                                                        'ManageSivarcScreenLayout',
                                                        'ManageClassicHmiGraphic',
                                                        'ListCommunicationConnections', 'ManageCommunicationConnection')
                                               or domain == 'SafetyValidation')
                version_action = args.major == 20 and (
                    (name == 'ManagePlcDocuments' and arguments['action'] in ('createFromMasterCopy', 'createFromLibraryType'))
                    or (name == 'ManageDcbLibraries' and arguments['action'] == 'import')
                    or (name == 'ManageDriveHardwareModule' and arguments['action'] in ('changeType', 'setPositionNumber'))
                    or (name == 'ManagePlcSafety' and arguments['action'] == 'generateBaseId')
                    or (name == 'ManagePlcProtection' and arguments['action'] in ('protectAllConfiguration', 'unprotectAllConfiguration'))
                    or (name == 'ManageDeviceServiceObjects' and (arguments['family'] == 'webApplications'
                        or (arguments['family'] == 'telecontrolDataPoints' and arguments['action'] in ('read', 'update', 'delete')))))
                if profile == 'full':
                    resources.require((name in names) != hidden, f'{name}: unexpected version registration')
                params = {'name': name, 'arguments': arguments} if profile == 'full' else {
                    'name': 'CallTool', 'arguments': {'name': name, 'arguments': arguments}}
                reply = rpc('tools/call', params=params)
                if domain in ('Devices', 'HardwareManagement', 'HardwareAml', 'Modules', 'Addresses'):
                    result = reply['result']
                    raw = result['content'][0]['text']
                    body = json.loads(raw)
                    resources.require(body['schemaVersion'] == 4 and body['ok'] is False and result['isError'] is True, raw)
                    resources.require((body['meta']['outcome'], body['meta']['execution']) ==
                                      (('read-failed', 'read-only') if name == 'ValidateAutomationContext'
                                       else ('rejected-before-operation', 'not-started')), raw)
                    resources.require(body['error']['code'] in ('PROJECT_NOT_BOUND', 'INVALID_ARGUMENT', 'PRECONDITION_FAILED', 'RESOURCE_UNAVAILABLE', 'NATIVE_OPERATION_FAILED'), raw)
                    resources.require(body['meta']['tool'] == ('CallTool' if profile == 'lite' and case.startswith('shared-') else name), raw)
                    if case.startswith('shared-'):
                        resources.require(body['error']['code'] == 'INVALID_ARGUMENT', raw)
                    reached_child = True
                    # Reuse the existing V4 envelope-only timestamp/requestId mask.
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, 'CallTool').encode('utf-8')
                    continue
                if domain in PLC_EXCHANGE_DOMAINS:
                    raw = plc_exchange_reply(reply, name)
                    reached_child |= case != 'duplicate-argument'
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain in ('Runtime', 'RuntimeChannel', 'PlcSimAdvanced', 'RuntimeSettings', 'OnlineDownload'):
                    raw = runtime_reply(reply, profile, name, case)
                    reached_child |= case != 'duplicate-argument'
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain in ('HmiExchange', 'HmiDescribe', 'HmiTagDeletion'):
                    raw = hmi_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain == 'Diagnostics':
                    raw, artifacts = diagnostics_reply(reply, profile, name, arguments)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = raw.encode('utf-8')
                    for artifact, content in artifacts.items():
                        responses[domain + '/' + name + '/' + artifact] = content
                    continue
                if domain in ('Session', 'ProjectSession'):
                    raw = session_reply(reply, profile, name, case)
                    reached_child |= case != 'duplicate-argument'
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    preflight = {'name': name, 'arguments': {'probe': True, 'PROBE': False}}
                    params = {'name': 'PreviewToolCall', 'arguments': preflight} if profile == 'full' else {
                        'name': 'CallTool', 'arguments': {'name': 'PreviewToolCall', 'arguments': preflight}}
                    preview = rpc('tools/call', params=params)
                    raw = v4_reply(preview, 'PreviewToolCall')
                    value = resources.envelope(preview)
                    resources.require(not value['ok'] and value['error']['code'] in ('INVALID_ARGUMENT', 'PROJECT_NOT_BOUND'),
                                      f'{name}: preview admission changed: {raw}')
                    responses[domain + '/' + name + '/preflight'] = snapshots.mask_raw_text(raw, 'PreviewToolCall').encode('utf-8')
                    continue
                if domain == 'PlcBlocks':
                    raw = plc_block_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain in ('Types', 'Documents', 'PlcExternalSources', 'PlcDocumentation', 'NativeExchange'):
                    raw = source_reply(reply, profile, name, case)
                    reached_child |= case != 'duplicate-argument'
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if name in SOFTWARE_REPLY_MARKERS:
                    raw = software_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain == 'PlcTables':
                    raw = table_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain == 'UnifiedHmi' or name == 'DescribeUnifiedScreenItemType':
                    raw = unified_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if 'error' in reply:
                    resources.require(hidden and profile == 'full' and reply['error']['code'] == -32602,
                                      f'{name}: unexpected protocol refusal: {reply}')
                    raw = json.dumps(reply['error'], ensure_ascii=False)
                else:
                    raw = v4_reply(reply, name)
                    value = resources.envelope(reply)
                    if hidden or version_action:
                        resources.require(not value['ok'] and value['error']['code'] in
                                          ('TOOL_NOT_FOUND', 'CAPABILITY_NOT_SUPPORTED', 'INVALID_ARGUMENT', 'PROJECT_NOT_BOUND'), raw)
                    reached_child = True
                responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
        if isolated and reached_child:
            reply = rpc('tools/call', params={'name': 'GetOpennessWorkerStatus', 'arguments': {}})
            status = resources.envelope(reply)
            resources.require(status['data']['evidence']['worker']['state'] == 'Ready', 'Domain calls never reached the isolated child')
    return names, responses


class SelfTests(unittest.TestCase):
    def test_report_mask_preserves_payload_and_filename_identity(self):
        raw = '{ "OperationId": "20261003_123456", "MarkdownPath": "C:/same/tia_mcp_error_NotConnected_20261003_123456.md", "Summary": "20261003_123456", "Detail": "2026-10-03T12:34:56+00:00", "GeneratedAt": "2026-10-03T12:34:56+00:00" }'
        expected = raw.replace('"OperationId": "20261003_123456"', '"OperationId": "<clock:operationId>"').replace(
            '_20261003_123456.md', '_<clock:operationId>.md').replace(
            '"GeneratedAt": "2026-10-03T12:34:56+00:00"', '"GeneratedAt": "<string:timestamp>"')
        self.assertEqual(mask_report_json(raw, 'GenerateErrorReport'), expected)
        self.assertEqual(mask_report_json(raw, 'GetSessionState'), raw)
        for before, after in [('C:/same/', 'D:/changed/'), ('NotConnected', 'Changed'), ('.md', '.txt'), ('Summary', 'Detail2')]:
            self.assertNotEqual(mask_report_json(raw.replace(before, after), 'GenerateErrorReport'), expected)

    def test_report_mask_does_not_hide_nested_or_non_clock_ids(self):
        for raw in ('{"OperationId":"random-id"}', '{"OperationId":"20261003_123456-changed"}',
                    '{"data":{"OperationId":"20261003_123456"}}', '{"GeneratedAt":"not-a-clock"}',
                    r'{"Operation\u0049d":"20261003_123456"}'):
            self.assertEqual(mask_report_json(raw, 'GenerateAcceptanceReport'), raw)

    def test_acceptance_nested_clocks_are_narrow(self):
        for parent, meta in (('selfTest', 'meta'), ('safetySelfTest', 'meta'), ('SelfTest', 'Meta'), ('SafetySelfTest', 'Meta')):
            raw = '{"' + parent + '":{"' + meta + '":{"timestamp":"2026-10-03T12:34:56+00:00","detail":"2026-10-03T12:34:56+00:00"}}}'
            expected = raw.replace('"timestamp":"2026-10-03T12:34:56+00:00"', '"timestamp":"<string:timestamp>"')
            self.assertEqual(mask_report_json(raw, 'GenerateAcceptanceReport'), expected)
            self.assertEqual(mask_report_json(raw, 'GenerateErrorReport'), raw)

    def test_report_markdown_masks_only_generated_header_rows(self):
        raw = '# Report\r\n\r\n- OperationId: `20261003_123456`\r\n- GeneratedAt: `2026-10-03T12:34:56+00:00`\r\nDetail\r\n- GeneratedAt: `2026-10-03T12:34:56+00:00`\r\n'
        expected = raw.replace('20261003_123456', '<clock:operationId>', 1).replace('2026-10-03T12:34:56+00:00', '<string:timestamp>', 1)
        self.assertEqual(mask_report_markdown(raw), expected)
        self.assertNotEqual(mask_report_markdown(raw.replace('Detail', 'Changed')), expected)
        with self.assertRaises(AssertionError):
            mask_report_markdown(raw.replace('- OperationId:', '- Other:'))

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

    @staticmethod
    def plc_failure(name):
        value = {'schemaVersion': 4, 'ok': False, 'data': {},
                 'meta': {'tool': name, 'outcome': 'read-failed'},
                 'error': {'code': 'NATIVE_OPERATION_FAILED'}}
        return {'result': {'isError': True, 'structuredContent': value,
                           'content': [{'text': json.dumps(value)}]}}

    def test_table_reply_preserves_disconnected_error_family(self):
        reply = self.plc_failure('ListPlcWatchTables')
        for profile in ('full', 'lite'):
            self.assertEqual(table_reply(reply, profile, 'ListPlcWatchTables'), reply['result']['content'][0]['text'])
        with self.assertRaises(AssertionError):
            table_reply(reply, 'full', 'MonitorPlcWatchTableS7')

    def test_table_reply_rejects_unrelated_failure(self):
        reply = {'result': {'content': [{'text': '{"message":"Unexpected constructor failure"}'}]}}
        with self.assertRaises(AssertionError):
            table_reply(reply, 'full', 'MonitorPlcWatchTableS7')

    def test_watch_timestamp_mask_is_narrow(self):
        raw = '{"data":{"timestamp":"2026-10-03T12:34:56.123+08:00","other":"2026-10-03T12:34:56.123+08:00"}}'
        masked = snapshots.mask_raw_text(raw, 'ProbePlcMonitorOnlineCapabilities')
        self.assertIn('"timestamp":"<string:timestamp>"', masked)
        self.assertIn('"other":"2026-10-03T12:34:56.123+08:00"', masked)
        self.assertEqual(snapshots.mask_raw_text(raw, 'ListPlcWatchTables'), raw)

    def test_unique_cases(self):
        for domain, cases in CASES.items():
            self.assertTrue(cases, domain)
            self.assertEqual(len(cases), len({(name, case) for name, case, _ in cases}), domain)

    def test_source_reply_preserves_error_family(self):
        reply = self.plc_failure('GetPlcTypeInfo')
        self.assertEqual(source_reply(reply, 'full', 'GetPlcTypeInfo', 'read'), reply['result']['content'][0]['text'])
        reply['result']['isError'] = False
        with self.assertRaises(AssertionError):
            source_reply(reply, 'full', 'GetPlcTypeInfo', 'read')

    def test_document_refusal_preserves_the_v4_error_verdict(self):
        body = {'schemaVersion': 4, 'ok': False, 'error': {'code': 'PRECONDITION_FAILED'},
                'meta': {'outcome': 'rejected-before-operation', 'execution': 'not-started'}}
        raw = json.dumps(body)
        reply = {'result': {'isError': True, 'content': [{'text': raw}], 'structuredContent': body}}
        self.assertEqual(plc_exchange_reply(reply, 'ExportPlcBlockDocuments'), raw)
        reply['result']['isError'] = False
        with self.assertRaises(AssertionError):
            plc_exchange_reply(reply, 'ExportPlcBlockDocuments')

    def test_unified_nested_timestamp_mask_is_narrow(self):
        raw = '{"meta":{"setMeta":{"timestamp":"2026-10-03T12:34:56+08:00"},"other":"2026-10-03T12:34:56+08:00"}}'
        masked = snapshots.mask_raw_text(raw, 'EnsureUnifiedHmiButtonAction')
        self.assertIn('"timestamp":"<string:timestamp>"', masked)
        self.assertIn('"other":"2026-10-03T12:34:56+08:00"', masked)
        self.assertEqual(snapshots.mask_raw_text(raw, 'EnsureUnifiedHmiScreen'), raw)

    def test_unified_self_test_masks_only_its_report_clock(self):
        for key in ('data', 'Data'):
            raw = json.dumps({key: {'timestamp': '2026-10-03T12:34:56+08:00',
                                   'sourceDate': '2026-10-03T12:34:56+08:00'}})
            masked = snapshots.mask_raw_text(raw, 'RunHmiActionScriptRecipeSafetySelfTest')
            self.assertIn('"timestamp": "<string:timestamp>"', masked)
            self.assertIn('"sourceDate": "2026-10-03T12:34:56+08:00"', masked)
            self.assertEqual(snapshots.mask_raw_text(raw, 'BuildUnifiedHmiButtonActionScript'), raw)

    def test_unified_connection_rejects_unrelated_dispatch_failure(self):
        reply = self.plc_failure('EnsureUnifiedHmiConnection')
        self.assertEqual(unified_reply(reply, 'full', 'EnsureUnifiedHmiConnection'), reply['result']['content'][0]['text'])
        reply['result']['structuredContent']['error']['code'] = 'INTERNAL_ERROR'
        reply['result']['content'][0]['text'] = json.dumps(reply['result']['structuredContent'])
        with self.assertRaises(AssertionError):
            unified_reply(reply, 'full', 'EnsureUnifiedHmiConnection')



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
    parser.add_argument('--source-only', action='store_true', help='Check all domain fixture inventories without builds or processes')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests))
        return int(not result.wasSuccessful())
    if args.source_only:
        domains = args.domain or list(CASES)
        check_coverage(domains)
        print(f'PASS: {len(domains)} domain fixture inventories match registered source tools; no engine execution.')
        return 0
    if not args.domain or any(getattr(args, name) is None for name in (
            'exe', 'baseline_exe', 'public_api', 'host_harness', 'baseline_harness', 'major')):
        parser.error('--domain, both EXEs/harnesses, --public-api and --major are required')
    args.domain = list(dict.fromkeys(args.domain))
    check_coverage(args.domain)
    if 'Diagnostics' in args.domain:
        scratch = (Path(__file__).resolve().parents[2] / 'bin-build').resolve()
        # Inherit worktree permissions, as in the other Windows check fixtures;
        # TemporaryDirectory's private ACL cannot be reopened in restricted sessions.
        args.report_directory = scratch / ('domain-diagnostics-' + uuid.uuid4().hex)
        args.report_directory.mkdir(parents=True)
        try:
            return compare_domains(args)
        finally:
            resources.require(args.report_directory.resolve().parent == scratch, 'Report directory escaped bin-build')
            shutil.rmtree(args.report_directory)
    return compare_domains(args)


def compare_domains(args):
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
