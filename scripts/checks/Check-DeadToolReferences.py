"""面向 Agent 的死引用闸：工具描述和引导文案点名的工具必须真实注册。

为什么要有这道闸：`GetPlcForceTables` 的描述写着 "use SetForceTableEntry"，而
`SetForceTableEntry` 从 0.0.38 起就刻意不再注册（强制写值不许 AI 调）。安全下线做对了，
面向 Agent 的文字忘了同步 —— Agent 照着描述调，撞 "tool not found"，然后自己去找别的
路子绕，而撞上的恰恰是「强制写值」这种安全敏感操作。同类还查到 `GetAxisParameters`、
`GetCpuOnlineState` 两个从来不存在的名字。

这类漂移靠人记不住，只能靠对拍：拿引擎实际注册的名字，扫所有 Agent 能看到的文字。
（同 `SafetyTables.cs` 由 gen 脚本生成的思路：凡是「一份名单必须和代码保持一致」的地方，
都该走生成或加漂移检查。）

用法：
    python scripts/checks/Check-DeadToolReferences.py            # 0=干净 1=有死引用
    python scripts/checks/Check-DeadToolReferences.py --selftest # 哨兵：注入一个假名字，必须被抓到
"""
import re
import io
import os
import sys
import collections
import importlib.util
import json
import unittest
import argparse
from pathlib import Path

ROOT = str(Path(__file__).resolve().parents[2] / 'src/Engine')
LOGIC_ROOT = str(Path(ROOT).with_name('Logic'))
SHARED_ROOT = str(Path(ROOT).with_name('Shared'))
spec = importlib.util.spec_from_file_location('dead_reference_text', Path(__file__).with_name('Check-McpText.py'))
text_literals = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = text_literals
spec.loader.exec_module(text_literals)
# Historical registered names identify later renames even without a leading
# "Use" verb. These are comparison facts, never runtime aliases.
HISTORICAL_NAMES = {tool['name'] for path in (Path(ROOT).parents[1] / 'manifest/contracts/baseline').glob('*.json')
                    for tool in json.loads(path.read_text(encoding='utf-8'))['tools']}

# 白名单：形状像工具名、但**不是**本服务器的工具，因此不该被判死引用。
# 每条必须写明它到底是什么 —— 没有理由的白名单等于把闸门关掉。
ALLOWED = {
    'CompileUnit': 'SW.Blocks.CompileUnit SimaticML type in a quoted native import error',
    'CreateSignatures': 'Diagnostic result field containing reflected native Create overloads',
    'DeleteAll': 'Download ResetModule prompt answer enum value',
    'OpenSession': 'Underlying local-session opening operation in ProjectSecurityService; not an MCP tool',
    'ReadDriveParameter': 'Native Startdrive read-parameter type',
    'RunToNextSyncPoint': 'Native S7-PLCSIM Advanced simulation stepping method',
    # Openness / .NET 的 API 名，描述里是在讲底层调用，不是让 Agent 去调工具
    'GetService': 'Openness IEngineeringObject.GetService<T>()',
    'ReadAccess': 'Openness PlcProtectionAccessLevel.ReadAccess（ManagePlcProtection 的 accessLevel 枚举值，不是工具）',
    'GetAttribute': 'Openness IEngineeringObject.GetAttribute()',
    'GetAttributeInfos': 'Openness IEngineeringObject.GetAttributeInfos()',
    'GetNodeId': 'Openness HmiUnified OpcUaAlarm.GetNodeId(displayName)，由 ImportUnifiedOpcUaAlarms 暴露',
    'GetSupportedFileFormats': 'Openness Workspace.GetSupportedFileFormats()',
    'GenerateLoadable': 'Native PLC loadable-file API method, exposed by GeneratePlcLoadableFile',
    'CreateFromDocuments': 'Native library type composition API method, exposed by ImportLibraryTypeDocuments',
    'ImportAlarmTexts': 'V20 native SinumerikAlarmTextProvider.AlarmTextImporter.ImportAlarmTexts method, exposed by ImportSinumerikAlarmTexts',
    'ReadWrite': 'Global library open-mode enum value, not an MCP tool',
    'ReadOnly': 'Global library open-mode enum value (ManageGlobalLibrary openMode), not an MCP tool',
    'DeleteUnusedTypes': 'Openness CleanUpMode enum value (SynchronizeLibrary cleanUpMode), not an MCP tool',
    'SetOnlyHigherUpdatedVersionAsDefault': 'Openness ForceUpdateMode enum value (ManageLibraryType / SynchronizeLibrary forceUpdateMode), not an MCP tool',
    'ConnectObject': 'Openness Workspace.ConnectObject()',
    'ImportDocumentOptions': 'Openness 枚举类型名（importOption 参数的取值来源）',
    'DownloadProvider': 'Openness DownloadProvider 类型名',
    'AddSignalBoard': "TIA 里那个操作的俗称，描述原文是「这就是 'InsertDeviceItem' / 'AddSignalBoard' 操作」",
    # 本仓工具族的通配写法与非工具标识符
    'BuildPlc': 'BuildPlc* 工具族的通配前缀（BuildPlcUdtXml / BuildPlcObXml / …）',
    'CompileError': '错误码取值，不是工具名',
    'RunOut': 'EnsureStartStopUnifiedHmi 建的 HMI 变量名',
    # 明确写着「本服务器没有这个工具」的说明性提及
    'DeleteDb': '描述原文即「没有单独的 DeleteDb/DeleteGlobalDb/DeleteFunctionBlock，用 DeletePlcBlock」',
    'DeleteGlobalDb': '同上',
    'DeleteFunctionBlock': '同上',
    # 2.7.18 新工具族描述里点名的原生成员，都在说明底层调用，不是 MCP 工具名
    # 2.7.33 Base 收尾工具族描述里点名的原生成员，都在说明底层调用
    'AttachTime': 'Openness TiaPortalSession.AttachTime 属性名，由 ReadPortalInfo 读出',
    'CompileProvider': 'Openness 类型名（PublicAPI 里是 internal），描述在说明它不可用',
    'DownloadToBackup': 'Openness RHDownloadProvider.DownloadToBackup()，由 DownloadToPlc rhTarget=backup 封装',
    'DownloadToPrimary': 'Openness RHDownloadProvider.DownloadToPrimary()，由 DownloadToPlc rhTarget=primary 封装',
    'ExportDataPoints': 'Openness TelecontrolManagement.ExportDataPoints()，由 ManageDeviceServiceObjects export 封装',
    'ImportDataPoints': 'Openness TelecontrolManagement.ImportDataPoints()，由 ManageDeviceServiceObjects import 封装',
    'GetIdentifier': 'Openness ObjectIdentifierProvider.GetIdentifier()，由 ReadObjectIdentifier 封装',
    'OpenWithUpgrade': 'Openness ProjectComposition.OpenWithUpgrade()，由 OpenProject 封装',
    'ApplyConfiguration': 'Openness ConnectionConfiguration.ApplyConfiguration(ConfigurationAddress|ConfigurationTargetInterface)，由 GoOnline pgPcInterface / DownloadToPlc 的路由选择封装（2.7.49）',
    # 2.7.32 安全/UMC 工具族描述里点名的原生成员（ManageUmcUsers），都在说明底层调用
    'CreateOfflineUmcUser': 'Openness UmcUserComposition.CreateOfflineUmcUser(name)，由 ManageUmcUsers createOffline 封装',
    'CreateOfflineUmcUserGroup': 'Openness UmcUserGroupComposition.CreateOfflineUmcUserGroup()，由 ManageUmcUsers createOffline 封装',
    'GetUserByName': 'Openness UmcServer.GetUserByName()，由 ManageUmcUsers importFromServer 封装',
    'GetUserGroupByName': 'Openness UmcServer.GetUserGroupByName()，由 ManageUmcUsers importFromServer 封装',
    'SetName': 'Openness UmcUser/UmcUserGroup.SetName()，由 ManageUmcUsers rename 封装',
    'CheckConsistency': 'Openness UmcServerConfigurator.CheckConsistency()，由 ManageUmcUsers kind=server 封装',
    'GetAccessibleDevices': 'Openness ConfigurationPcInterface.GetAccessibleDevices()，由 ScanAccessibleDevices 封装',
    'GetFingerprintData': 'Openness FingerprintDataProvider.GetFingerprintData()，由 ReadPlcBlockFingerprints 封装',
    'GetFingerprints': 'Openness FingerprintProvider.GetFingerprints()，由 ReadPlcObjectFingerprints 封装',
    'GetLinkedTags': 'Openness PlcTagProvider.GetLinkedTags()，由 ReadDeviceItemChannels includeLinkedTags 封装',
    'CreateFromFile': 'Openness PlcExternalSourceComposition.CreateFromFile()，由 ManagePlcExternalSources createFromFile 封装',
    'GenerateBlocksFromSource': 'Openness PlcExternalSource.GenerateBlocksFromSource()，由 ManagePlcExternalSources generateBlocks 封装',
    'ExportProDIAGInfo': 'Openness CodeBlock.ExportProDIAGInfo()，由 ExportPlcProDiagInfo 封装',
    'ImportScreenOverview': 'Openness HmiTarget.ImportScreenOverview()，由 ManageClassicHmiScreenObject objectKind=overview 封装',
    'ImportScreenGlobalElements': 'Openness HmiTarget.ImportScreenGlobalElements()，由 ManageClassicHmiScreenObject objectKind=globalElements 封装',
    'CreateOptions': 'Openness SiVArc CreateOptions 枚举（Replace / Rename），由 ManageSivarcTableRule createOption 封装',
    'GetExpressionResolver': 'Openness Sivarc.GetExpressionResolver()，由 ResolveSivarcExpression 封装',
    'GetLayoutFields': 'Openness ScreenRule / ScreenRuleGroup.GetLayoutFields()，由 ManageSivarcTableRule read 回报',
    'SetAttributes': 'Openness IEngineeringObject.SetAttributes()，由 ManageSivarcTableRule deviceSelectionJson 封装',
    'ImportInstanceTextsFromXlsx': 'Openness PlcAlarmTextProvider.ImportInstanceTextsFromXlsx()，由 ImportPlcAlarmInstanceTexts 封装',
    'GetCreationInfos': 'Openness IEngineeringComposition.GetCreationInfos()，动态组合接口',
    'CloseAndCommit': 'Openness LocalSession.CloseAndCommit()，由 ManageMultiuserSession 的 commit 动作封装',
    'ListRange': 'Openness PlcAlarmTextlist.ListRange 属性名',
    'MoveToParkingLot': 'Openness CaxImportOptions 枚举值',
    'ReadRolePermissions': 'Openness OPC UA NamespacePermission 布尔属性名',
    'ReadOperatingMode': 'S7 Web 服务器 API 方法 Plc.ReadOperatingMode，由 ReadPlcWebDiagnostics 封装',
    'ReadTag': 'WinCC Unified Open Pipe 消息名',
    'WriteTag': 'WinCC Unified Open Pipe 消息名',
    'ReadAlarm': 'WinCC Unified Open Pipe 消息名',
    'ReadConfig': 'WinCC Unified Open Pipe 消息名（仅经 UnifiedOpenPipeRequest 原始请求可达）',
    'WriteConfig': 'WinCC Unified Open Pipe 消息名（仅经 UnifiedOpenPipeRequest 原始请求可达）',
    # 2.7.30 硬件网络深层工具族描述里点名的原生成员 / 类型 / 枚举，都在说明底层调用，不是 MCP 工具名
    'CreateIoSystem': 'Openness IoController.CreateIoSystem(name)，由 ManageIoSystem 的 create 动作封装',
    'ConnectToIoSystem': 'Openness IoConnector.ConnectToIoSystem(ioSystem)，由 ManageIoSystem 的 connect 动作封装',
    'ConnectToPort': 'Openness NetworkPort.ConnectToPort(partner)，由 ManagePortInterconnection 的 connect 动作封装',
    'CreateFrom': 'Openness DeviceUserGroupComposition.CreateFrom(MasterCopy)，描述原文即 "not exposed"',
    'SetAttribute': 'Openness IEngineeringObject.SetAttribute()',
    'SyncDomainOwner': 'Openness HW.Features.SyncDomainOwner 服务类型名',
    'SyncDomains': 'Openness SyncDomainOwner.SyncDomains 导航器名',
    'SyncDomainComposition': 'Openness HW.SyncDomainComposition 类型名',
    'SyncRole': 'Openness IoController/IoConnector 的动态属性名（SyncRole 枚举）',
    # 2.7.31 库深层工具族描述里点名的原生成员，都在说明底层调用，不是 MCP 工具名
    'FindType': 'Openness ILibrary.FindType(Guid)，由 ReadLibraryType 的 guid 参数封装',
    'FindVersion': 'Openness ILibrary.FindVersion(Guid)，由 ReadLibraryType 的 guid 参数封装',
    'GetGlobalLibraryInfos': 'Openness GlobalLibraryComposition.GetGlobalLibraryInfos()，由 ManageGlobalLibrary 的 infos 动作封装',
    'GetSupportedExportFormats': 'Openness LibraryType.GetSupportedExportFormats()，由 ReadLibraryType 读出',
    'SetForUpdate': 'Openness LibraryType.SetForUpdate 属性名',
    # 2.7.39 Startdrive / DCC 工具族描述里点名的原生成员 / 枚举值，都在说明底层调用，不是 MCP 工具名
    'CreateProtocol': 'Openness SafetyAcceptanceTestReport.CreateProtocol()，由 ManageDriveSafetyAcceptanceTest createProtocol 封装',
    'ReadParameters': 'Openness DriveObject / OnlineDriveObject.ReadParameters 导航器名，由 ReadDriveParameters / ReadOnlineDriveParameters 读出',
    'GetChartSequence': 'Openness DriveControlChartComposition.GetChartSequence()，由 ReadDccCharts / ManageDccChart readSequence 封装',
    'GetRunSequence': 'Openness DriveControlChart.GetRunSequence()，由 ReadDccCharts / ManageDccChart readSequence 封装',
    'MoveInRuntimeSequence': 'Openness Statement.MoveInRuntimeSequence(uint)，由 ManageDccChart / ManageDccBlock 的 sequenceIndex 封装',
    'ImportDcbLibrary': 'Openness DcbLibraryImporter.ImportDcbLibrary()，由 ManageDcbLibraries import 封装',
    'RenameOnConflict': 'Openness DccImportOptions 枚举值（importOptions 参数取值）',
    # 2.7.42 SafetyValidation / Test Suite / Teamcenter / CFC 工具族描述里点名的原生成员 / 枚举值，都在说明底层调用，不是 MCP 工具名
    'CheckValidity': 'Openness SafetyValidation.TestValidity.CheckValidity()，由 ManageSafetyActivationTest / ManageSafetyFunction / ManageSafetyFunctionCondition 的 checkValidity 动作封装',
    'ExportOptions': 'Openness Siemens.Engineering.ExportOptions 枚举类型名（exportOptions 参数取值）',
    'ExportSetting': 'Openness DocumentInfoOptions 枚举值（documentInfoOptions 参数取值）',
    'GetScope': 'Openness TestSuite TestCase.GetScope()，由 ReadTestSuiteCases 读出',
    'SetScope': 'Openness TestSuite RuleSet / TestCase / SystemTestCase.SetScope()，由 ManageTestSuiteCase setScope 封装',
    'SaveToFile': 'Openness TestSuite RuleSet / TestCase / SystemTestCase.SaveToFile()，由 ExchangeTestSuiteCase export 封装',
    'ConnectSSO': 'Openness TeamcenterConnectionProvider.ConnectSSO()，由 ManageTeamcenterConnection connectSso 封装',
    'GetTeamcenterCustomAttributes': 'Openness TcGatewayWorkflowProvider.GetTeamcenterCustomAttributes()，由 ManageTeamcenterWorkflow readCustomAttributes 封装',
    'SaveWithProxyObject': 'Openness TcGatewayWorkflowProvider.SaveWithProxyObject()，由 ManageTeamcenterWorkflow saveWithProxyObject 封装',
    'SetValue': 'Openness TeamcenterProperty.SetValue(string, ErrorCallback)，由 ManageTeamcenterWorkflow customAttributesJson 封装',
    'AddChartProtection': 'Openness CFC ChartProvider.AddChartProtection()，由 ManageCfcChartProtection add 封装',
    'GetChartProtection': 'Openness CFC ChartProvider.GetChartProtection()，由 ManageCfcChartProtection read 封装',
    'ExportInstructionData': 'Openness CFC ChartProviderS7.ExportInstructionData()，由 ExchangeCfcCharts exportInstructionData 封装',
}

VERB = re.compile(
    r'^(Get|Set|Add|Import|Export|Create|Delete|Compile|Download|Sync|Analyze'
    r'|Build|Write|Read|Ensure|Find|List|Describe|Invoke|Generate|Apply|Bind'
    r'|Move|Rename|Save|Open|Close|Connect|Run|Check|Validate|Preview|Preflight|Scaffold|Attach)[A-Z]')
# A qualified member (`CrossReferenceService.GetCrossReferences`, a native journal label) names an
# API, never an MCP tool, even when a historical tool had the same name.
TOK = re.compile(r'(?<![.\w])([A-Z][A-Za-z0-9]{3,})\b')


def load(root):
    src = {}
    for dp, dirs, fs in os.walk(root):
        dirs[:] = [d for d in dirs if d.lower() not in text_literals.lexer.SKIP_DIRS]
        for f in fs:
            if f.endswith('.cs'):
                p = os.path.join(dp, f)
                src[p] = io.open(p, encoding='utf-8-sig', errors='replace').read()
    return src


def guidance_literals(source):
    """Decode ordinary/verbatim/raw/interpolated literals, excluding comments."""
    tokens, _ = text_literals.lexer.Lexer(source).scan()
    def visit(tokens, inherited=False):
        ranges = text_literals.sink_ranges(tokens, text_literals.lexer.matching_pairs(tokens))
        i = 0
        while i < len(tokens):
            token = tokens[i]
            if token.kind != 'literal':
                i += 1
                continue
            description = inherited or any(lo < i < hi and kind == 'description' for lo, hi, kind in ranges)
            text = text_literals.literal_parts(token)[0]
            for nested in (token.expressions,):
                if nested: yield from visit(list(nested), description)
            # A split spelling in a constant concatenation is still one hint.
            while i + 2 < len(tokens) and tokens[i + 1].value == '+' and tokens[i + 2].kind == 'literal':
                i += 2
                text += text_literals.literal_parts(tokens[i])[0]
                if tokens[i].expressions: yield from visit(list(tokens[i].expressions), description)
            yield token.start, text, description
            i += 1
    yield from visit(tokens)


def scan(src, extra_text=None):
    """返回 {疑似死引用名: [出处]}。extra_text 供哨兵注入用。"""
    names = set()
    prompts = set()
    for s in src.values():
        names |= set(re.findall(r'McpServerTool\(Name\s*=\s*"([A-Za-z0-9_]+)"', s))
        prompts |= set(re.findall(r'McpServerPrompt\(Name\s*=\s*"([A-Za-z0-9_]+)"', s))
    items = list(src.items())
    if extra_text:
        items.append(('<sentinel>', extra_text))
    bad = collections.defaultdict(list)
    for p, s in items:
        for start, text, description in guidance_literals(s):
            location = os.path.basename(p) + ':' + str(s[:start].count('\n') + 1)
            mentioned = set(TOK.findall(text))
            directed = set(re.findall(r'\b(?:[Uu]se|[Cc]all|[Ii]nvoke|[Rr]un|[Tt]ry|[Ss]ee|via)\s+(?:the\s+)?([A-Z][A-Za-z0-9]+)', text))
            candidates = mentioned if description else (mentioned & HISTORICAL_NAMES) | directed
            for t in candidates:
                if t in names or t in prompts or t in ALLOWED or (t not in HISTORICAL_NAMES and not VERB.match(t)):
                    continue
                bad[t].append(location)
            for tool, parameter in (('CallTool', 'argumentsJson'), ('PreviewToolCall', 'argumentsJson'),
                                    ('RunReadOnlyToolBatch', 'operationsJson'), ('PreviewToolBatch', 'operationsJson')):
                if re.search(r'\b' + tool + r'\s*\([^)]*\b' + parameter + r'\b', text):
                    bad[tool + '.' + parameter].append(location)
    return names, bad


def duplicate_names(src, extra_text=None):
    """返回 {小写工具名: [(拼写, 出处)]}，只含不分大小写后撞在一起的注册。

    2.7.38 真机：新工具 `ManageSivarcRule` 与旧工具 `ManageSiVArcRule` 只差大小写，而 CallTool 的
    工具映射（McpServer.ToolBridge.cs 的 AllToolMethods）建在 OrdinalIgnoreCase 上，两个键合并成一个，
    lite 模式下 CallTool("ManageSivarcRule") 派发到旧工具——新工具在真机上根本不可达。名字必须不分
    大小写唯一，这里对拍。"""
    items = list(src.items())
    if extra_text:
        items.append(('<sentinel>', extra_text))
    seen = collections.defaultdict(list)
    for p, s in items:
        for m in re.finditer(r'McpServerTool\(Name\s*=\s*"([A-Za-z0-9_]+)"', s):
            line = s[:m.start()].count('\n') + 1
            seen[m.group(1).lower()].append((m.group(1), os.path.basename(p) + ':' + str(line)))
    return {k: v for k, v in seen.items() if len(v) > 1}


def migration_names(root):
    # Read the reviewed appendix without executing generators: a stale example or
    # guidance string must not prevent this repair tool from starting.
    document = (root / 'docs/development/phase6-review.md').read_text('utf-8-sig')
    appendix = document.split('A. ', 1)[-1].split('B. ', 1)[0]
    return dict(re.findall(r'^\| `([^`]+)` \| `([^`]+)` \|', appendix, re.M))


def rewrite_guidance(source, renames, registered):
    # ALLOWED names are reviewed API names that a historical tool shared; each occurrence is decided by hand.
    active = {old: new for old, new in renames.items()
              if old != new and old not in registered and new in registered and old not in ALLOWED}
    if not active: return source, []
    pattern = re.compile(r'(?<![.\w])(?:' + '|'.join(re.escape(n) for n in sorted(active, key=len, reverse=True)) + r')\b')
    tokens, _ = text_literals.lexer.Lexer(source).scan()
    eligible = []

    def visit(items, inherited=False):
        ranges = text_literals.sink_ranges(items, text_literals.lexer.matching_pairs(items))
        for i, token in enumerate(items):
            if token.kind != 'literal': continue
            decoded = text_literals.literal_parts(token)[0]
            sink = inherited or any(lo < i < hi and kind in ('description', 'exception', 'message', 'meta') for lo, hi, kind in ranges)
            directed = bool(re.search(r'\b(?:[Uu]se|[Cc]all|[Ii]nvoke|[Rr]un|[Tt]ry|[Ss]ee|via)\s+', decoded))
            # Nontrivial prose covers prompts, hints and return/refusal text. A
            # bare identifier, enum/schema discriminator or lookup key is data.
            prose = bool(re.search(r'\s', decoded)) and bool(pattern.search(decoded))
            if decoded.lstrip().startswith(('{', '[')):
                try:
                    json.loads(decoded)
                    prose = False
                except ValueError:
                    pass  # A prose example with braces is not a serialized data object.
            # A literal that is only an identifier is data or code (a reflection lookup, a key), never prose.
            if (sink or directed or prose) and not re.fullmatch(r'\s*[A-Za-z_]\w*\s*', decoded):
                eligible.append((token.start, token.end, tuple(token.expressions)))
            if token.expressions: visit(list(token.expressions), sink)
    visit(tokens)
    events, edits = [], []
    for match in pattern.finditer(source):
        start, end = match.span()
        allowed = any(lo <= start and end <= hi and not any(t.start <= start < t.end for t in nested)
                      for lo, hi, nested in eligible)
        events.append((start, match[0], active[match[0]], allowed))
        if allowed: edits.append((start, end, active[match[0]]))
    # A decoded escape or concatenation may spell a name without a contiguous
    # source span. Keep it reviewable rather than changing quoting/expressions.
    for start, decoded, _ in guidance_literals(source):
        for match in pattern.finditer(decoded):
            if not any(position >= start and source.count('\n', start, position) == 0 and old == match[0]
                       for position, old, _, _ in events):
                events.append((start, match[0], active[match[0]], False))
    for start, end, value in reversed(edits): source = source[:start] + value + source[end:]
    return source, events


def main(fix=False):
    src = load(ROOT)
    src.update(load(LOGIC_ROOT))
    src.update(load(SHARED_ROOT))
    if not src:
        print('找不到源码目录 %s —— 请在仓库根目录运行。' % ROOT)
        return 2

    if fix:
        registered, _ = scan(src)
        renames = migration_names(Path(ROOT).parents[1])
        for path, source in sorted(src.items()):
            rewritten, events = rewrite_guidance(source, renames, registered)
            for start, old, new, changed in events:
                print(f"{'REWRITE' if changed else 'KEEP'} {path}:{source.count(chr(10), 0, start) + 1}: {old} -> {new}"
                      + ('' if changed else ' (data/code/comment or non-contiguous spelling; review required)'))
            if rewritten != source:
                Path(path).write_bytes(rewritten.encode('utf-8'))
                src[path] = rewritten

    # 哨兵 2：注入一个只差大小写的重名注册，闸门必须抓到。
    registered, _ = scan(src)
    sentinel_name = sorted(registered)[0].upper()
    dup_sentinel = f'[McpServerTool(Name = "{sentinel_name}"), Description("sentinel")]'
    if sentinel_name.lower() not in duplicate_names(src, extra_text=dup_sentinel):
        print('[FAIL] 重名哨兵没被抓到 —— 工具名唯一性检查自己坏了，它的 PASS 不可信。')
        return 2
    dups = duplicate_names(src)
    if dups:
        print('[FAIL] 下列工具名不分大小写后重复（CallTool 的映射不分大小写，后注册的会遮蔽先注册的）：')
        for k, v in sorted(dups.items()):
            print('  %-38s %s' % (k, ', '.join('%s (%s)' % x for x in v)))
        print('修法：给其中一个换名字；两个名字只差大小写的工具对 Agent 来说是同一个。')
        return 1

    # 哨兵：注入一个必然不存在的工具名，闸门必须抓到它。
    # 这条不是形式主义 —— 本仓吃过「检查自己坏了却全绿」的亏（字段读错致全假 PASS）。
    sentinel = '[McpServerTool(Name = "SentinelTool"), Description("Use GetNonexistentSentinelTool first.")]'
    _, caught = scan(src, extra_text=sentinel)
    if 'GetNonexistentSentinelTool' not in caught:
        print('[FAIL] 哨兵没被抓到 —— 这个检查自己坏了，它的 PASS 不可信。')
        return 2

    names, bad = scan(src)
    print('引擎注册工具：%d 个；扫描文件：%d 个' % (len(names), len(src)))
    if not bad:
        print('[PASS] 工具描述及源码引导文案无死引用或旧基础设施参数（哨兵已验证闸门有效）。')
        return 0
    print('[FAIL] 下列工具/参数在描述或源码引导文案中已失效：')
    for t, locs in sorted(bad.items()):
        print('  %-38s %2d 处  %s' % (t, len(locs), ', '.join(sorted(set(locs))[:4])))
    print('修法：要么改文案说清事实与替代路径，要么把工具真的注册上。'
          '若它本就不是工具名，加进本脚本的 ALLOWED 并写明理由。')
    return 1


class GuidanceTests(unittest.TestCase):
    def test_fix_guidance_only(self):
        source = '''// Use OldTool here.
[Description("Use OldTool and OldToolSuffix.")]
void OldTool() { var key = "OldTool"; return $"Try OldTool {OldTool()} {"Use OldTool"}"; }
const string Prompt = @"Call OldTool before continuing.";
const string Hint = """See OldTool for details.""";
'''
        fixed, events = rewrite_guidance(source, {'OldTool': 'NewTool'}, {'NewTool'})
        self.assertIn('// Use OldTool here.', fixed)
        self.assertIn('var key = "OldTool"', fixed)
        self.assertIn('void OldTool()', fixed)
        self.assertIn('{OldTool()}', fixed)
        self.assertIn('OldToolSuffix', fixed)
        self.assertEqual(sum(changed for _, _, _, changed in events), 5)
        self.assertEqual(sum(not changed for _, _, _, changed in events), 4)
        self.assertEqual(rewrite_guidance(fixed, {'OldTool': 'NewTool'}, {'NewTool'})[0], fixed)

    def test_bare_identifiers_and_allowed_api_names_are_kept(self):
        source = ('[Description("Use OldTool.")] void M() { throw new Exception("OldTool"); '
                  'var m = t.GetMethods().Single(x => x.Name == "OldTool"); }')
        fixed, _ = rewrite_guidance(source, {'OldTool': 'NewTool'}, {'NewTool'})
        self.assertEqual(fixed.count('NewTool'), 1)
        self.assertEqual(fixed.count('"OldTool"'), 2)
        api = next(iter(ALLOWED))
        source = '[Description("Calls ' + api + ' natively.")] void M() {}'
        self.assertEqual(rewrite_guidance(source, {api: 'NewTool'}, {'NewTool'})[0], source)

    def test_qualified_api_members_are_not_tool_names(self):
        source = ('var r = InvocationJournal.Native("CrossReferenceService.GetCrossReferences", () => 1); '
                  '[Description("Calls Service.OldTool natively.")] void M() {}')
        renames = {'OldTool': 'NewTool', 'GetCrossReferences': 'GetPlcCrossReferences'}
        fixed, events = rewrite_guidance(source, renames, {'NewTool', 'GetPlcCrossReferences'})
        self.assertEqual((fixed, events), (source, []))
        _, bad = scan({'<qualified>': source})
        self.assertNotIn('OldTool', bad)

    def test_fix_requires_removed_old_and_registered_target(self):
        source = '[Description("Use OldTool.")]'
        for registered in ({'OldTool', 'NewTool'}, {'OldTool'}, set()):
            self.assertEqual(rewrite_guidance(source, {'OldTool': 'NewTool'}, registered), (source, []))

    def test_fix_reports_encoded_and_split_spellings_without_touching_data(self):
        for source in ('return "Use Old" + "Tool first.";', 'return "Use Old\\u0054ool first.";',
                       'const string schema = """{ "enum": ["OldTool"] }""";'):
            fixed, events = rewrite_guidance(source, {'OldTool': 'NewTool'}, {'NewTool'})
            self.assertEqual(fixed, source)
            self.assertTrue(events)
            self.assertFalse(any(changed for _, _, _, changed in events))
    def test_hints_outside_description_and_split_literals(self):
        for source in ('const string Instructions = "Use GetNonexistentSentinelTool first.";',
                       'return "Call Preflight" + "ToolCall(name, argumentsJson).";',
                       'throw new ArgumentException(@"GetAuthoringGuide(topic: ""lad"") supplies examples.");',
                       'return $"Try PreviewMissingTool for {value}.";',
                       'const string Hint = """GetRecipe(topic) returns the sequence.""";'):
            with self.subTest(source=source): self.assertTrue(scan({'shared.cs': source})[1])

    def test_current_names_native_apis_and_comments(self):
        source = '[McpServerTool(Name="GetToolUsage")] void Usage() {}\n' + \
            'return "Use GetToolUsage(language: \\"lad\\") or GetService<T>().";\n' + \
            '// Use GetNonexistentSentinelTool.\n'
        self.assertFalse(scan({'engine.cs': source})[1])

    def test_old_arguments_in_current_tool_guidance(self):
        for tool, parameter in (('CallTool', 'argumentsJson'), ('PreviewToolCall', 'argumentsJson'),
                                ('PreviewToolBatch', 'operationsJson'), ('RunReadOnlyToolBatch', 'operationsJson')):
            source = f'[McpServerTool(Name="{tool}")] void Tool() {{}} return "Use {tool}({parameter}).";'
            self.assertIn(tool + '.' + parameter, scan({'hint.cs': source})[1])


if __name__ == '__main__':
    if '--selftest' in sys.argv or '--self-test' in sys.argv:
        result = unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(GuidanceTests))
        sys.exit(0 if result.wasSuccessful() else 1)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fix', action='store_true', help='rewrite removed names in guidance literals using appendix A')
    args = parser.parse_args()
    sys.exit(main(args.fix))
