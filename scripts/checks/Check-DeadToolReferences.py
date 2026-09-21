"""面向 Agent 的死引用闸：工具描述里点名的工具，必须真的注册过。

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
from pathlib import Path

ROOT = str(Path(__file__).resolve().parents[2] / 'tools/tiaportal-mcp/src/TiaMcpServer')

# 白名单：形状像工具名、但**不是**本服务器的工具，因此不该被判死引用。
# 每条必须写明它到底是什么 —— 没有理由的白名单等于把闸门关掉。
ALLOWED = {
    # Openness / .NET 的 API 名，描述里是在讲底层调用，不是让 Agent 去调工具
    'GetService': 'Openness IEngineeringObject.GetService<T>()',
    'ReadAccess': 'Openness PlcProtectionAccessLevel.ReadAccess（ManagePlcProtection 的 accessLevel 枚举值，不是工具）',
    'GetAttribute': 'Openness IEngineeringObject.GetAttribute()',
    'GetAttributeInfos': 'Openness IEngineeringObject.GetAttributeInfos()',
    'GetNodeId': 'Openness HmiUnified OpcUaAlarm.GetNodeId(displayName)，由 ImportUnifiedOpcUaAlarms 暴露',
    'GetSupportedFileFormats': 'Openness Workspace.GetSupportedFileFormats()',
    'GenerateLoadable': 'Native PLC loadable-file API method, exposed by GeneratePlcLoadableFile',
    'CreateFromDocuments': 'Native library type composition API method, exposed by ImportLibraryTypeDocuments',
    'ReadWrite': 'Global library open-mode enum value, not an MCP tool',
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
    r'|Move|Rename|Save|Open|Close|Connect|Run|Check|Validate|Preflight|Scaffold|Attach)[A-Z]')
STR = r'"[^"]*"'          # 描述文案里没有转义引号，简单形态足够
LIT = re.compile(r'Description\(\s*((?:@?' + STR + r'\s*\+?\s*)+)\)', re.S)
PIECE = re.compile(STR)
TOK = re.compile(r'\b([A-Z][A-Za-z0-9]{3,})\b')


def load(root):
    src = {}
    for dp, _, fs in os.walk(root):
        for f in fs:
            if f.endswith('.cs'):
                p = os.path.join(dp, f)
                src[p] = io.open(p, encoding='utf-8-sig', errors='replace').read()
    return src


def scan(src, extra_text=None):
    """返回 {疑似死引用名: [出处]}。extra_text 供哨兵注入用。"""
    names = set()
    for s in src.values():
        names |= set(re.findall(r'McpServerTool\(Name\s*=\s*"([A-Za-z0-9_]+)"', s))
    items = list(src.items())
    if extra_text:
        items.append(('<sentinel>', extra_text))
    bad = collections.defaultdict(list)
    for p, s in items:
        for m in LIT.finditer(s):
            text = ' '.join(x[1:-1] for x in PIECE.findall(m.group(1)))
            line = s[:m.start()].count('\n') + 1
            for t in set(TOK.findall(text)):
                if t in names or t in ALLOWED or not VERB.match(t):
                    continue
                bad[t].append(os.path.basename(p) + ':' + str(line))
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


def main():
    src = load(ROOT)
    if not src:
        print('找不到源码目录 %s —— 请在仓库根目录运行。' % ROOT)
        return 2

    # 哨兵 2：注入一个只差大小写的重名注册，闸门必须抓到。
    dup_sentinel = '[McpServerTool(Name = "GETSTATE"), Description("sentinel")]'
    if 'getstate' not in duplicate_names(src, extra_text=dup_sentinel):
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
        print('[PASS] 工具描述里点名的工具全部真实注册（哨兵已验证闸门有效）。')
        return 0
    print('[FAIL] 下列名字在 [Description] 文案里被点名，但没有任何 [McpServerTool] 注册它：')
    for t, locs in sorted(bad.items()):
        print('  %-38s %2d 处  %s' % (t, len(locs), ', '.join(sorted(set(locs))[:4])))
    print('修法：要么改文案说清事实与替代路径，要么把工具真的注册上。'
          '若它本就不是工具名，加进本脚本的 ALLOWED 并写明理由。')
    return 1


if __name__ == '__main__':
    if '--selftest' in sys.argv:
        src = load(ROOT)
        _, caught = scan(src, extra_text='[Description("Use GetNonexistentSentinelTool first.")]')
        ok = 'GetNonexistentSentinelTool' in caught
        print('哨兵自检：' + ('PASS（假名字被抓到）' if ok else 'FAIL（假名字没被抓到）'))
        sys.exit(0 if ok else 1)
    sys.exit(main())
