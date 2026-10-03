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
      for key in ('data', 'Data')),
    {'tool': 'GetOpcUaConfig', 'path': ['data', 'timestamp'], 'reason': 'GetOpcUaConfig DateTime.Now, direct serialization'},
    {'tool': 'GetOpcUaConfig', 'path': ['Data', 'timestamp'], 'reason': 'GetOpcUaConfig DateTime.Now, bridge serialization'}]


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
HARDWARE = {'devicePathJson': '["DomainOfflineFixture"]', 'itemPathJson': '["CPU"]'}
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
           for provider in ('opcua', 's7-readonly')],
    'Library': [('ReadLibraryOverview', 'read', {}), ('ReadLibraryType', 'read', {'typePath': 'Type1'})]
        + actions('ManageLibraryType', 'update delete updateLibrary updateProject', typePath='Type1')
        + [('CheckLibraryUpdates', 'read', {})]
        + actions('SynchronizeLibrary', 'updateLibrary updateProject harmonizeProject cleanUp', selectionJson='[]')
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
    'VersionControl': [('GetVersionControlWorkspaces', 'read', {}),
        ('CreateVersionControlWorkspace', 'create', {'workspaceName': 'Offline', 'folderPath': str(Path(__file__).resolve().parents[2])}),
        ('GetVersionControlStatus', 'read', {}),
        ('SyncVersionControlWorkspace', 'export', {'direction': 'ProjectToWorkspace'}),
        ('SyncVersionControlWorkspace', 'import-refusal', {'direction': 'WorkspaceToProject'}),
        ('ConnectProjectToWorkspace', 'map', {})],
    'Sivarc': [('ReadSivarcRuleTree', category, {'category': category})
               for category in ('screens', 'tags', 'advancedTags', 'alarms', 'copies', 'textLists')]
        + actions('ManageSivarcRuleContainer', 'read create createFromType delete', category='screens', kind='table', path='Table1')
        + actions('ManageSivarcTableRule', 'read create createFromMasterCopy update delete', category='screens', tablePath='Table1')
        + [('ReadSivarcBlockDefinitions', 'read', {'softwarePath': PLC, 'blockPath': 'Block1'})]
        + actions('ManageSivarcBlockDefinition', 'read create update delete', softwarePath=PLC, blockPath='Block1', kind='tagDefinition')
        + [('ResolveSivarcExpression', 'resolve', {'softwarePath': PLC, 'blockPath': 'Block1', 'devicePathJson': '[]',
            'itemPathJson': '[]', 'libraryItemKind': 'masterCopy', 'libraryItemPath': 'Copy1', 'expression': 'Block.Name'})]
        + actions('ManageSivarcScreenLayout', 'export import', softwarePath=PLC, screenName='Screen1', filePath='C:/domain-offline.xml')
        + [('UpgradeSivarcDefinitions', 'upgrade', {'softwarePath': PLC}),
           ('GenerateSiVArc', 'generate', {'hmiDeviceName': 'HMI1', 'plcSoftwarePathsJson': '["PLC1"]', 'generationOptions': 'None'})],
    'ClassicHmiFolders': [('ReadClassicHmiScreenTree', kind, {'softwarePath': PLC, 'kind': kind})
                         for kind in ('all', 'screens', 'popups', 'templates', 'slideins')]
        + actions('ManageClassicHmiScreenObject', 'read export import delete', softwarePath=PLC,
                  objectKind='popup', objectPath='Screen1', filePath='C:/domain-offline.xml')
        + actions('ManageClassicHmiFolder', 'read create delete', softwarePath=PLC,
                  folderKind='scripts', folderPath='Folder1', newName='Folder2')
        + actions('ManageClassicHmiGraphic', 'list read export import delete', softwarePath=PLC,
                  name='Graphic1', filePath='C:/domain-offline.xml'),
    'MotionProDiagClassicHmi': [('ReadMotionAxisConfiguration', 'read', {'softwarePath': PLC, 'objectPath': 'Axis1'})]
        + actions('ManageMotionAxis', 'read connectIdent', softwarePath=PLC, objectPath='Axis1')
        + actions('ManageMotionAxis', 'addMasterValue removeMasterValue', softwarePath=PLC,
                  objectPath='Axis1', aspect='synchronousSetPoint', name='Axis2')
        + actions('ManageMotionAxis', 'createMapping updateMapping deleteMapping', softwarePath=PLC,
                  objectPath='Axis1', aspect='toMapping', name='Mapping1')
        + actions('ManageMotionAxis', 'connect disconnect', softwarePath=PLC, objectPath='Axis1', aspect='actor')
        + actions('ManagePlcSupervision', 'read readComposition createEntry deleteEntry setAttributes exportSettings importSettings',
                  softwarePath=PLC, filePath='C:/domain-offline.dat')
        + [('ReadClassicHmiScripts', 'read', {'softwarePath': PLC})]
        + actions('ManageClassicHmiScript', 'read export import delete createFolder deleteFolder setAttributes',
                  softwarePath=PLC, scriptPath='Script1', filePath='C:/domain-offline.xml')
        + actions('ManageClassicHmiCycle', 'read export import delete setAttributes',
                  softwarePath=PLC, cycleName='Cycle1', filePath='C:/domain-offline.xml')
        + actions('ManageClassicHmiTextGraphicList', 'read readEntries createEntry deleteEntry export import delete setAttributes',
                  softwarePath=PLC, listKind='text', listName='List1', filePath='C:/domain-offline.xml')
        + [('ReadClassicHmiGlobalization', 'read', {'softwarePath': PLC}),
           ('ReadClassicHmiFaceplates', 'read', {}),
           ('ExportPlcProDiagInfo', 'export', {'softwarePath': PLC, 'blockPath': 'Block1', 'directoryPath': 'C:/domain-offline'})],
    'Alarms': [(name, 'export', {'softwarePath': PLC, 'exportPath': 'C:/domain-offline.' + extension})
               for name, extension in (('ExportAlarmClasses', 'dat'), ('ExportAlarmTextLists', 'xlsx'), ('ExportAlarmInstanceTexts', 'xlsx'))]
        + [(name, 'import', {'softwarePath': PLC, 'importPath': 'C:/domain-offline.' + extension})
           for name, extension in (('ImportAlarmClasses', 'dat'), ('ImportAlarmTextLists', 'xlsx'))]
        + actions('ExchangePlcAlarmTextListsXlsx', 'export import', softwarePath=PLC, filePath='C:/domain-offline.xlsx')
        + [('ImportPlcAlarmInstanceTexts', 'import', {'softwarePath': PLC, 'filePath': 'C:/domain-offline.xlsx', 'culturesJson': '["en-US"]'})]
        + actions('ManagePlcAlarmTextList', 'read delete createFromMasterCopy', softwarePath=PLC, name='List1', libraryName='Library1', masterCopyPath='List1'),
    'OpcUa': [('GetOpcUaConfig', 'read', {'softwarePath': PLC})]
        + actions('ManageOpcUaInterface', 'read delete', softwarePath=PLC, interfaceName='Interface1')
        + [('SetOpcUaInterfaceEnabled', 'set', {'softwarePath': PLC, 'interfaceName': 'Interface1', 'enabled': True}),
           ('ExportOpcUaInterface', 'export', {'softwarePath': PLC, 'interfaceName': 'Interface1', 'exportPath': 'C:/domain-offline.xml'}),
           ('ImportOpcUaInterface', 'import', {'softwarePath': PLC, 'importPath': 'C:/domain-offline.xml'}),
           ('GenerateOpcUaModelledInterface', 'generate', {'softwarePath': PLC, 'interfaceName': 'Interface1', 'namespaceUri': 'urn:domain:offline', 'outputPath': 'C:/domain-offline.xml'})]
        + [('ReadOpcUaAccessControl', section, {'softwarePath': PLC, 'section': section}) for section in ('roles', 'restrictions')]
        + actions('ManageOpcUaAccessControl', 'createRole addStandardRole deleteRole setProjectRole setPermission setRestriction',
                  softwarePath=PLC, roleName='Role1', definedInNamespace='urn:domain:offline', projectRole='Role1', namespaceUri='urn:domain:offline'),
    'TechnologyObjects': [('GetTechnologyObjects', 'read', {'softwarePath': PLC}),
           ('ExportTechnologyObject', 'export', {'softwarePath': PLC, 'toName': 'Object1', 'exportPath': 'C:/domain-offline.xml'}),
           ('ExportTechnologyObjectsToDirectory', 'export', {'softwarePath': PLC, 'exportDir': 'C:/domain-offline'}),
           ('ImportTechnologyObject', 'import', {'softwarePath': PLC, 'folderPath': '', 'importPath': 'C:/domain-offline.xml'}),
           ('ImportTechnologyObjectsFromDirectory', 'import', {'softwarePath': PLC, 'folderPath': '', 'dir': 'C:/domain-offline'}),
           ('ReadTechnologyObjectTree', 'read', {'softwarePath': PLC})]
        + actions('ManageTechnologyObject', 'read create delete setParameter', softwarePath=PLC, objectPath='Object1'),
    'Devices': [
        ('GetProjectTree', 'read', {}),
        ('GetDeviceInfo', 'read', {'devicePath': PLC}),
        ('GetDeviceItemInfo', 'read', {'deviceItemPath': PLC}),
        ('GetDeviceItemTree', 'read', {'deviceItemPath': PLC}),
        ('SetDeviceItemAttribute', 'write', {'deviceItemPath': PLC, 'attributeName': 'Name', 'value': 'Item1'}),
        ('ValidateAutomationContext', 'read', {}),
        ('SetCpuCommonSettings', 'write', {'cpuPath': PLC, 'settingsJson': '{}'}),
        ('GetDevices', 'read', {}),
        ('AddDevice', 'write', {'orderNumber': '6ES7513-1AM03-0AB0', 'version': 'V3.0', 'deviceName': PLC}),
        ('AddDeviceWithFallback', 'write', {'preferredMlfb': '', 'preferredVersion': '', 'deviceName': PLC}),
        # Empty keywords refuse before scanning machine-local GSDML files.
        ('SearchInstalledGsdDevices', 'empty-keyword', {'keyword': ''}),
        ('SearchHardwareCatalog', 'read', {'keyword': 'CPU'}),
        ('AddGsdDeviceWithProbe', 'empty-keyword', {'keyword': '', 'deviceName': PLC}),
        ('AddHardwareCatalogDeviceWithProbe', 'write', {'keyword': 'CPU', 'deviceName': PLC})],
    'HardwareManagement': actions('ManageHardwareObject', 'deleteDevice deleteItem moveItem copyItem',
                                  devicePathJson='["Station1"]'),
    'HardwareAml': [
        ('ExportDeviceAml', 'export', {'devicePath': PLC, 'exportPath': 'C:/domain-offline.aml'}),
        ('ImportDeviceAml', 'import', {'filePath': 'C:/domain-offline.aml', 'logFilePath': 'C:/domain-offline.log'}),
        # Relative paths refuse before any file output; the offline executor still runs.
        ('BuildDeviceAmlDocument', 'relative-path', {'specJson': '{}', 'outputPath': 'domain-offline.aml'})],
    'Modules': [('GetDevicePlugLocations', 'read', {'deviceItemPath': PLC}),
                ('PlugDeviceItem', 'preview', {'deviceItemPath': PLC, 'orderNumber': '6ES7521-1BL00-0AB0', 'version': 'V2.0'})],
    'Addresses': [('GetDeviceItemIoAddresses', 'read', {'deviceItemPath': PLC}),
                  ('SetDeviceItemIoAddress', 'preview', {'deviceItemPath': PLC, 'ioType': 'Input', 'startAddress': 2})],
    'HardwareNetwork': [('ReadIoSystems', 'subnet', {'subnetName': 'PN/IE_1'}),
                        ('ReadIoSystems', 'interface', HARDWARE),
                        ('ReadNetworkDomains', 'read', {'subnetName': 'PN/IE_1'}),
                        ('ReadTransferAreas', 'read', HARDWARE),
                        ('ReadDeviceItemChannels', 'read', HARDWARE),
                        ('UpdateDeviceItemChannel', 'update', dict(HARDWARE, channelType='Digital',
                            channelIoType='Input', channelNumber=0, attributesJson='{"ChannelAddress":0}'))]
        + actions('ManageIoSystem', 'create delete update connect disconnect', **HARDWARE, name='IO1')
        + [(tool, kind + '/' + case, arguments) for kind in ('sync', 'mrp')
           for tool, case, arguments in actions('ManageNetworkDomain', 'create delete update addParticipant',
               subnetName='PN/IE_1', kind=kind, name='Domain1')]
        + actions('ManageTransferArea', 'create delete update createMappingRule updateMappingRule deleteMappingRule',
                  **HARDWARE, name='Area1', type='IN')
        + [(tool, 'multicast/' + case, arguments) for tool, case, arguments in actions('ManageTransferArea',
            'create createReceiver delete update', **HARDWARE, kind='multicast', name='Area1', type='DDX')]
        + actions('ManageDeviceUserGroup', 'read create rename deleteEmpty', groupPath='Group1', newName='Group2')
        + [(tool, family + '/' + case, arguments) for family, values in (
            ('webserver', 'read create delete setPassword setPermissions'),
            ('simpleWebserver', 'read setPassword setPermissions setActive rename'),
            ('opcUa', 'read create delete setPassword'))
           for tool, case, arguments in actions('ManageDeviceUsers', values, **HARDWARE, family=family,
               userName='User1', password='offline', newName='User2')]
        + actions('ManagePortInterconnection', 'read connect disconnect', **HARDWARE),
    'HardwareServices': [('ReadCommunicationConnections', 'read', HARDWARE),
                         ('ReadHardwareFeatures', 'read', HARDWARE)]
        + actions('ManageCommunicationConnection', 'create delete', **HARDWARE,
                  connectionType='S7Connection', connectionName='Connection1')
        + actions('ManageWatchForceTableWebAccess', 'read assign unassign', **HARDWARE,
                  softwarePath=PLC, tablePath='Table1')
        + actions('ExchangeSystemDiagnosticsSettings', 'export import', filePath='C:/domain-offline.dat')
        + [(tool, family + '/' + case, arguments) for family, values in (
            ('webApplications', 'read setDefault'), ('telecontrolDataPoints', 'read update delete export import'),
            ('certificateServices', 'read update setServiceGroupName createService deleteService'))
           for tool, case, arguments in actions('ManageDeviceServiceObjects', values, **HARDWARE,
               family=family, name='1', filePath='C:/domain-offline.xml')],
    'OnlineDownload': [(name, 'disconnected', {'softwarePath': PLC}) for name in (
        'GetOnlineState', 'GoOffline', 'CompareSoftwareToOnline', 'CheckDownloadReadiness', 'DownloadToPlc',
        'ReadTransferRoutes')]
        + [('GoOnline', target or 'standard', {'softwarePath': PLC, 'rhTarget': target})
           for target in ('', 'primary', 'backup')]
        + [('GoOfflineAll', 'disconnected', {}), ('ScanAccessibleDevices', 'disconnected', {}),
           ('UploadStationFromPlc', 'disconnected', {'targetIpAddress': '192.0.2.1'}),
           ('UploadDeviceParameters', 'disconnected', {'devicePathJson': '[]', 'itemPathJson': '[]',
                                                     'targetIpAddress': '192.0.2.1'}),
           ('DownloadPlcToFolder', 'disconnected', {'softwarePath': PLC,
                                                  'destinationDirectory': 'C:/domain-offline-card'})]
}


# Older hardware tools mix thrown MCP errors, failure POCOs and empty inventories.
# Require a tool-specific terminal marker before comparing every response byte.
HARDWARE_TERMINALS = {
    'GetProjectTree': 'Failed retrieving project tree',
    'GetDeviceInfo': 'Device not found',
    'GetDeviceItemInfo': 'Device item not found',
    'GetDeviceItemTree': 'Device item not found',
    'SetDeviceItemAttribute': 'Project is null',
    'ValidateAutomationContext': 'Automation context invalid',
    'SetCpuCommonSettings': 'Project is null',
    'GetDevices': 'Devices retrieved',
    'AddDevice': 'No project is open',
    'AddDeviceWithFallback': 'Failed to add device',
    'SearchInstalledGsdDevices': 'Keyword is empty',
    'SearchHardwareCatalog': 'HardwareCatalog is not available',
    'AddGsdDeviceWithProbe': 'Keyword is empty',
    'AddHardwareCatalogDeviceWithProbe': 'HardwareCatalog is not available',
    'ExportDeviceAml': 'No project is open',
    'BuildDeviceAmlDocument': 'Absolute output file path required',
    'GetDevicePlugLocations': '的槽位：要么没有连接项目',
    'PlugDeviceItem': 'NotConnected',
    'GetDeviceItemIoAddresses': '的地址：要么没有连接项目',
    'SetDeviceItemIoAddress': '的地址：要么没有连接项目',
}


HARDWARE_THROWS = {
    'GetProjectTree', 'GetDeviceInfo', 'GetDeviceItemInfo', 'GetDeviceItemTree',
    'AddDevice', 'SearchInstalledGsdDevices', 'SearchHardwareCatalog',
    'AddGsdDeviceWithProbe', 'AddHardwareCatalogDeviceWithProbe', 'ExportDeviceAml',
    'GetDevicePlugLocations', 'PlugDeviceItem', 'GetDeviceItemIoAddresses', 'SetDeviceItemIoAddress',
}


CASES['PlcBlocks'] = [
    ('GetBlockInfo', 'read', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('GetBlocks', 'read', {'softwarePath': PLC}),
    ('GetBlocksWithHierarchy', 'read', {'softwarePath': PLC}),
    ('ExportBlock', 'export', {'softwarePath': PLC, 'blockPath': 'Group/Block1', 'exportPath': 'C:/domain-offline'}),
    ('ImportBlock', 'import', {'softwarePath': PLC, 'groupPath': '', 'importPath': 'C:/domain-offline.xml'}),
    ('ImportBlocksFromDirectory', 'import', {'softwarePath': PLC, 'groupPath': '', 'dir': 'C:/domain-offline'}),
    ('ImportPlcProgramFromDirectory', 'import', {'softwarePath': PLC, 'sourceDir': 'C:/domain-offline'}),
    ('CompileAndDiagnosePlc', 'compile', {'softwarePath': PLC}),
    ('RepairAndReimportBlock', 'import', {'softwarePath': PLC, 'importPath': 'C:/domain-offline.xml'}),
    ('ExportBlocks', 'missing-required-argument', {'exportPath': 'C:/domain-offline'}),
    ('DescribeBlockLogic', 'read', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('UpdatePlcProgram', 'preview', {'softwarePath': PLC}),
    ('ReadPlcBlockFingerprints', 'preview', {'softwarePath': PLC, 'targetIpAddress': '192.0.2.1'}),
    ('ReadPlcBlockEditCapabilities', 'read', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('AnalyzePlcReferences', 'missing-directory', {'directory': 'C:/domain-offline'}),
    ('PatchPlcBlockDocument', 'missing-file', {'filePath': 'C:/domain-offline.xml', 'changesJson': '[]', 'expectedFingerprint': 'none'}),
    ('ImportPlcBlockVerified', 'preview', {'softwarePath': PLC, 'blockPath': 'Group/Block1', 'importPath': 'C:/domain-offline.xml', 'evidenceDirectory': 'C:/domain-evidence'}),
    ('DeletePlcBlock', 'preview', {'softwarePath': PLC, 'blockPath': 'Group/Block1'}),
    ('DeletePlcTagTable', 'preview', {'softwarePath': PLC, 'tagTableName': 'Table1'}),
    ('DeletePlcType', 'preview', {'softwarePath': PLC, 'typePath': 'Type1'}),
    ('CreatePlcTypeGroup', 'preview', {'softwarePath': PLC, 'groupPath': 'Group'}),
    ('DeleteEmptyPlcBlockGroup', 'preview', {'softwarePath': PLC, 'groupPath': 'Group'}),
    ('CreatePlcBlockGroup', 'create', {'softwarePath': PLC, 'groupPath': 'Group'}),
    ('MoveBlockToGroup', 'move', {'softwarePath': PLC, 'blockName': 'Block1', 'targetGroupPath': 'Group'})
] + actions('ManagePlcBlockProtection', 'read protect unprotect', softwarePath=PLC, blockPath='Group/Block1') \
  + actions('ManagePlcDataBlockSnapshot', 'read createSnapshot loadSnapshotAsActualValues loadStartValuesAsActualValues exportSnapshot', softwarePath=PLC, blockPath='Group/Block1') \
  + actions('ManagePlcUserGroup', 'create rename deleteEmpty', softwarePath=PLC, family='blocks', groupPath='Group')


def plc_block_reply(reply, profile, name):
    resources.require('result' in reply, f'{name}: missing tools/call result: {reply}')
    raw = reply['result']['content'][0]['text']
    if profile == 'lite':
        bridge = json.loads(raw)
        resources.require(isinstance(bridge.get('meta', {}).get('bridgeSuccess'), bool),
                          f'{name}: missing bridge status: {raw}')
        raw = bridge['message']
    resources.require(any(marker in raw for marker in (
        'Project is null', 'No project', 'no project', 'No TIA project', 'Block not found',
        'Block root group not found', 'No blocks found', 'not found', 'not exist', 'does not exist',
        'required', 'Required', 'must not be empty', 'empty', 'Import failed',
        'Analysis failed', 'analysis failed', 'failed', 'Failed')),
        f'{name}: did not reach its existing offline refusal: {raw}')
    return raw


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


THROWING_GUARDS = {
    'GetTechnologyObjects': 'GetTechnologyObjects: no project is open. Call Connect + OpenProject (or AttachToOpenProject) first.',
    'ImportTechnologyObject': "Failed importing technology object from 'C:/domain-offline.xml' [InvalidState]: No project is open."
}
SIMPLE_GUARDS = {'ExportAlarmClasses', 'ImportAlarmClasses', 'ExportAlarmTextLists', 'ImportAlarmTextLists',
                 'ExportAlarmInstanceTexts', 'GetOpcUaConfig', 'SetOpcUaInterfaceEnabled', 'ExportOpcUaInterface',
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
                hidden = args.major == 20 and (name in ('ManagePlcBlockWriteProtection', 'ManageDriveSafetyAcceptanceTest',
                                                        'ManageSivarcScreenLayout',
                                                        'ManageClassicHmiGraphic',
                                                        'ReadCommunicationConnections', 'ManageCommunicationConnection')
                                               or domain == 'SafetyValidation')
                version_action = args.major == 20 and (
                    (name == 'ManagePlcDocuments' and arguments['action'] in ('createFromMasterCopy', 'createFromLibraryType'))
                    or (name == 'ManageDcbLibraries' and arguments['action'] == 'import')
                    or (name == 'ManageDriveHardwareModule' and arguments['action'] in ('changeType', 'setPositionNumber'))
                    or (name == 'ManagePlcSafety' and arguments['action'] == 'generateBaseId')
                    or (name == 'ManageDeviceServiceObjects' and (arguments['family'] == 'webApplications'
                        or (arguments['family'] == 'telecontrolDataPoints' and arguments['action'] in ('read', 'update', 'delete')))))
                if profile == 'full':
                    resources.require((name in names) != hidden, f'{name}: unexpected version registration')
                params = {'name': name, 'arguments': arguments} if profile == 'full' else {
                    'name': 'CallTool', 'arguments': {'name': name.lower(), 'argumentsJson': json.dumps(arguments)}}
                reply = rpc('tools/call', params=params)
                if domain == 'PlcBlocks':
                    raw = plc_block_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                if domain == 'PlcTables':
                    raw = table_reply(reply, profile, name)
                    reached_child = True
                    responses[domain + '/' + name + '/' + case] = snapshots.mask_raw_text(raw, name).encode('utf-8')
                    continue
                throwing = name in THROWING_GUARDS
                disconnected_error = domain == 'OnlineDownload' and name in (
                    'GetOnlineState', 'GoOnline', 'GoOffline', 'CompareSoftwareToOnline')
                if throwing and profile == 'full':
                    resources.require(reply.get('result', {}).get('isError') is True, f'{name}: expected MCP error: {reply}')
                    raw = reply['result']['content'][0]['text']
                    resources.require(THROWING_GUARDS[name] in raw, f'{name}: missing disconnected guard: {raw}')
                    reached_child = True
                elif name in HARDWARE_TERMINALS:
                    resources.require('result' in reply, f'{name}: missing tool result: {reply}')
                    resources.require(bool(reply['result'].get('isError')) is (profile == 'full' and name in HARDWARE_THROWS),
                                      f'{name}: unexpected MCP error status: {reply}')
                    raw = reply['result']['content'][0]['text']
                    if profile == 'lite':
                        bridge = json.loads(raw)
                        resources.require(bridge.get('meta', {}).get('bridgeSuccess') is (name not in HARDWARE_THROWS),
                                          f'{name}: unexpected bridge status: {raw}')
                        raw = bridge['message']
                    # Match decoded JSON too: System.Text.Json escapes the existing Chinese errors.
                    try:
                        decoded = json.dumps(json.loads(raw), ensure_ascii=False)
                    except ValueError:
                        decoded = raw
                    resources.require(HARDWARE_TERMINALS[name] in decoded,
                                      f'{name}/{case} did not reach its offline terminal: {raw}')
                    reached_child = True
                elif disconnected_error:
                    resources.require('result' in reply, str(reply))
                    raw = reply['result']['content'][0]['text']
                    if profile == 'full':
                        resources.require(reply['result'].get('isError') is True, str(reply))
                    else:
                        bridge = json.loads(raw)
                        resources.require(bridge.get('meta', {}).get('bridgeSuccess') is False
                                          and bridge['meta'].get('operationStatus') == 'notCompleted', raw)
                    resources.require('no project is open' in raw, f'{name}: missing disconnected refusal: {raw}')
                    reached_child = True
                elif (hidden or version_action) and profile == 'full':
                    resources.require('error' in reply or reply.get('result', {}).get('isError'),
                                      f'Unavailable tool/action unexpectedly ran: {reply}')
                    raw = json.dumps(reply.get('error', reply.get('result')), ensure_ascii=False)
                else:
                    resources.require('result' in reply and not reply['result'].get('isError'), str(reply))
                    raw = reply['result']['content'][0]['text']
                    if profile == 'lite':
                        bridge = json.loads(raw)
                        resources.require(bridge.get('meta', {}).get('bridgeSuccess') is (not hidden and not version_action and not throwing),
                                          f'{name}: unexpected bridge status: {raw}')
                        if not hidden and not version_action and not throwing:
                            raw = bridge['message']
                    value = json.loads(raw)
                    meta = value.get('meta', value.get('Meta', {}))
                    if hidden or version_action:
                        resources.require('V20' in raw and ('unavailable' in raw or 'requires' in raw),
                                          f'{name}: missing version refusal: {raw}')
                    elif domain == 'OnlineDownload' and name in ('GoOfflineAll', 'CheckDownloadReadiness', 'DownloadToPlc'):
                        get = lambda key: value.get(key, value.get(key[0].upper() + key[1:]))
                        if name == 'CheckDownloadReadiness':
                            resources.require(get('ready') is False and get('issues') == ['No project open.'], raw)
                        else:
                            resources.require(get('message') == 'No project open.'
                                              and get('ok') is (name == 'GoOfflineAll'), raw)
                        if name == 'GoOfflineAll':
                            resources.require(get('data') == {'message': 'No project open.', 'allOffline': True, 'plcs': []}, raw)
                        reached_child = True
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
                    elif domain == 'VersionControl':
                        message = value.get('message', value.get('Message', ''))
                        expected = ('commercial-tier operation' if case == 'import-refusal' else 'No project is open.')
                        resources.require(meta.get('success') is False and expected in message,
                                          f'{name}/{case}: missing disconnected/refused VCI response: {raw}')
                        reached_child = True
                    elif name in ('ReadLibraryOverview', 'ReadLibraryType', 'CompareLibraryObjects', 'ManageGlobalLibrary'):
                        error_type = ('System.InvalidOperationException' if name == 'ManageGlobalLibrary'
                                      else 'TiaMcpServer.Siemens.PortalException')
                        resources.require(value.get('message', value.get('Message')) == name + ' failed'
                                          and meta.get('error', '').splitlines()[0] == error_type + ': Connect to TIA first.'
                                          and meta.get('tool') == name and meta.get('operationSuccess') is False,
                                          f'{name}: missing disconnected portal response: {raw}')
                    elif throwing:
                        resources.require(THROWING_GUARDS[name] in value['message'], f'{name}: missing disconnected guard: {raw}')
                        reached_child = True
                    elif name in SIMPLE_GUARDS:
                        resources.require(value.get('message', value.get('Message')) == 'No project open.',
                                          f'{name}: missing disconnected guard: {raw}')
                        reached_child = True
                    elif name == 'GenerateOpcUaModelledInterface':
                        resources.require(value.get('message', value.get('Message')) == 'Generation failed: No project is bound.'
                            and meta.get('success') is False and meta.get('imported') is False,
                            f'{name}: missing disconnected guard: {raw}')
                        reached_child = True
                    elif name in ('ImportTechnologyObjectsFromDirectory', 'ExportTechnologyObjectsToDirectory'):
                        expected = 'Project is null' if name.startswith('Import') else 'No project open.'
                        failures = value.get('failed', value.get('Failed'))
                        resources.require(value.get('imported', value.get('Imported')) == [] and len(failures) == 1
                            and failures[0].get('error', failures[0].get('Error')) == expected and meta.get('success') is False,
                            f'{name}: missing disconnected guard: {raw}')
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
