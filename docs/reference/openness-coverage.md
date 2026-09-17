# 官方 Openness API 覆盖清单（V21，2026-09-17）

[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [路线图](../development/roadmap.md) · [Openness 限制](../troubleshooting/openness-limitations.md)

本页回答"西门子官方 Openness API 有、而本项目没有专用封装的是什么"。数据由 [scripts/diagnostics/Audit-OpennessCoverage.ps1](../../scripts/diagnostics/Audit-OpennessCoverage.ps1) 对本机 V21 PublicAPI 的 18 个官方 XML 文档（不含 AddIn.*）与引擎源码逐成员对照生成；官方 XML 与 DLL 不随仓库分发。

## 口径（先读这个，再看数字）

- 分母是 **4,490 个领域成员**（1,239 个方法 + 3,251 个属性），已剔除每个类型都有的 `ToString`/`Equals`/`GetHashCode`/`Parent`/`GetAttribute(s)`/`SetAttribute(s)`/集合枚举等 10,294 个样板成员、构造函数和显式接口实现。
- 判定是**词法**的：方法记为"已引用"须同时满足所属类型简名出现在源码、且 `.方法名(` 出现在源码。这只证明"有专用代码路径"，不证明 MCP 工具已完整封装该能力；反过来，**"未触及"不等于"不可用"**——`DescribeObject` / `InvokeObject` / `InvokeService` 等通用反射工具可以动态到达任何公开成员，v2.7.14–15 新增的许多 Unified 工具就是按字符串访问组合与属性实现的，词法扫描看不到它们。
- 因此下面分两层：**统计表**如实给出词法结果；**确认缺口**只列同时满足"词法未触及 + 工具清单中无对应工具 + 人工核对源码确认"三个条件的项。
- 结果（V21）：方法 1,239 个中已引用 169、仅类型名 111、未引用 959；属性 3,251 个中已引用 257、仅类型名 426、未引用 2,568；有领域成员的类型 1,217 个中有专用引用 136、仅类型名 51、完全未触及 1,030。属性侧数字被"通用属性读写"显著低估，方法侧更接近真实封装度。

## 确认缺口（官方有入口、本项目无专用工具）

按对自动化工程的价值排序。"官方入口"给出 XML 中的精确标识，便于实现时直接定位。

| 优先级 | 能力域 | 官方入口（V21 XML） | 现状 |
|---|---|---|---|
| P1 | **下载提示应答不全（缺陷）** | `Siemens.Engineering.Download.Configurations.*`：43 种具体提示类型 | `DownloadToPlc` 委托只应答 16 种；`UserManagementDownload` 在 V20/V21 均为 `CurrentSelection` 选择型，委托却按复选框处理，等于未应答，触发时下载中止。详见路线图 E7 |
| P1 | **设备上载 / 在线可达设备扫描** | `T:Siemens.Engineering.Upload.StationUploadProvider`、`Upload.Configurations.*`；`M:Siemens.Engineering.Connection.ConfigurationPcInterface.GetAccessibleDevices` | 0 工具、0 源码引用。棕地"PLC 里到底是什么"工作流完全缺失 |
| P1 | **DB 快照 / 实际值往返** | `M:Siemens.Engineering.SW.Blocks.Interface.ValueService.CreateSnapshot`、`LoadSnapshotAsActualValues`、`LoadStartValuesAsActualValues` | 0 工具（现有 `Snapshot` 命中的都是 HMI 画面快照）。下载前后保留配方/设定值无法自动化 |
| P1 | **下载到 Windows 文件夹（存储卡镜像 / PLCSIM Advanced 目标）** | `DownloadProvider.Download(DirectoryInfo, …)`、`Download.Configurations.TargetForSoftware`、`OverwriteOnMemoryCard` | 0 工具 |
| P2 | **块保护 / 指纹** | `T:Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider`（Protect/Unprotect）；`Siemens.Engineering.FingerprintData.*`（4 类型） | 0 工具、0 引用。现有 `Protect` 命中均为 Safety/在线口令，不是块保护 |
| P2 | **PLC 程序升级** | `M:Siemens.Engineering.SW.PlcSoftware.UpdateProgram` | 0 引用 |
| P2 | **OPC UA 访问控制** | `Siemens.Engineering.SW.OpcUa.AccessControl.*`（7 类型 31 成员） | 0 引用；现有 OPC UA 工具只覆盖接口开关与导入导出 |
| P2 | **工程用户/角色/功能权限（UMAC）与高级保护** | `Siemens.Engineering.Umac.*`（31 类型 107 成员）、`Siemens.Engineering.AdvancedProtection.*`、`Online.Security.*` | 2 类型有引用（只读探测），无管理工具 |
| P2 | **库/工程比较** | `Siemens.Engineering.Library.Compare.*`（6 类型）、`Siemens.Engineering.Compare.*`（3 类型） | 0 引用；现有 `Compare` 工具是在线比较与图形选择比较 |
| P2 | **PLC 报警实例文本导入** | `M:Siemens.Engineering.SW.Alarm.PlcAlarmTextProvider.ImportInstanceTextsFromXlsx`；`SW.Alarm.TextLists.*` 4/5 类型未触及 | 只有导出半边 |
| P2 | **Unified 事件与部件对象模型** | `Siemens.Engineering.HmiUnified.UI.Events.*`（96 类型 243 成员，0 引用）、`UI.Parts.*`（85 类型 370 成员，0 引用）、`UI.Features.*`（13 类型） | 事件只有按钮事件一个工具（`EnsureUnifiedHmiButtonEventHandler`）；`PropertyEventHandlers` 仅 1 处引用；列/工具栏/阈值等部件无专用编辑 |
| P2 | **通信连接（S7/ISO/TCP/UDP/PtP…）** | `Siemens.Engineering.HW.CommunicationConnections.*`（10 类型 90 成员，如 `S7Connection`） | 7 类型未触及，唯一命中是 HMI 连接。（此前在线文档核对未能定位的章节，XML 证实存在于此命名空间） |
| P2 | **监视/强制表 Web 访问规则** | `T:Siemens.Engineering.HW.Features.WatchAndForceTableAccessManager` | 0 引用 |
| P3 | **系统诊断设置导入导出** | `T:Siemens.Engineering.HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsDataProvider` | 0 引用 |
| P3 | **多用户 / Project Server** | `Siemens.Engineering.Multiuser.*`（14 类型 47 成员） | 1 类型有引用（本地会话附着），无会话/提交/锁管理 |
| P3 | **Motion 工艺对象原生对象模型** | `Siemens.Engineering.SW.TechnologicalObjects.Motion.*`（23 类型 95 成员，0 引用） | 3 个工具经反射做凸轮/硬件映射，无轴对象专用封装 |
| P3 | **ProDiag 监督对象** | `Siemens.Engineering.SW.Supervision.*`（6 类型） | 只有 XLSX 交换工具 |
| P3 | **经典 HMI**：VB 脚本、全球化、文本/图形列表、面板 | `Siemens.Engineering.Hmi.RuntimeScripting.*`（8 类型）、`Hmi.Globalization`、`Hmi.TextGraphicList`、`Hmi.Faceplate` | 0 引用 |
| 选件 | Startdrive、SiVArc、DCC、CFC、Test Suite、Safety Validation、Teamcenter | `MC.Drives.*`（~90 类型，1 工具）、`SiVArc.*`（72 类型，3 工具）、`MC.Drives.Dcc.*`（2 工具）、`TestSuite.*`（3 工具）、`SafetyValidation.*`（14 类型，0）、`TeamcenterGateway.*`（12 类型，0） | 有许可才有意义；按需求排期 |

## 已核实为"官方无 API"的项（保持明确拒绝）

独立 RUN/STOP、清除强制、读诊断缓冲区、按块选择性下载、LED/模块在线健康。V21 XML 与在线文档均无对应成员；RUN/STOP 仅作为下载配置 `StopModules`/`StartModules` 附带发生。替代通道见 [Openness 限制](../troubleshooting/openness-limitations.md)。

## 统计表（脚本生成）

| 程序集 | 命名空间 | 类型数 | 有专用引用 | 仅类型名 | 完全未触及 | 成员数 | 已引用成员 |
|---|---|---:|---:|---:|---:|---:|---:|
| Base | Siemens.Engineering | 59 | 12 | 6 | 41 | 185 | 39 |
| Base | Siemens.Engineering.AdvancedProtection | 1 | 0 | 0 | 1 | 3 | 0 |
| Base | Siemens.Engineering.Compare | 3 | 0 | 0 | 3 | 7 | 0 |
| Base | Siemens.Engineering.Compiler | 5 | 2 | 0 | 3 | 14 | 5 |
| Base | Siemens.Engineering.Connection | 14 | 4 | 0 | 10 | 34 | 9 |
| Base | Siemens.Engineering.CrossReference | 8 | 2 | 0 | 6 | 28 | 6 |
| Base | Siemens.Engineering.CustomIdentity | 2 | 0 | 0 | 2 | 4 | 0 |
| Base | Siemens.Engineering.Download | 5 | 2 | 0 | 3 | 21 | 5 |
| Base | Siemens.Engineering.Download.Configurations | 31 | 1 | 14 | 16 | 32 | 1 |
| Base | Siemens.Engineering.FingerprintData | 4 | 0 | 0 | 4 | 6 | 0 |
| Base | Siemens.Engineering.HW | 63 | 13 | 1 | 49 | 204 | 44 |
| Base | Siemens.Engineering.HW.CommunicationConnections | 10 | 2 | 1 | 7 | 90 | 3 |
| Base | Siemens.Engineering.HW.CustomDataTypes | 3 | 0 | 0 | 3 | 4 | 0 |
| Base | Siemens.Engineering.HW.Extensions | 1 | 1 | 0 | 0 | 2 | 2 |
| Base | Siemens.Engineering.HW.Features | 43 | 2 | 3 | 38 | 111 | 2 |
| Base | Siemens.Engineering.HW.HardwareCatalog | 1 | 0 | 0 | 1 | 7 | 0 |
| Base | Siemens.Engineering.HW.Systemdiagnostics.Settings | 2 | 0 | 0 | 2 | 3 | 0 |
| Base | Siemens.Engineering.HW.Utilities | 5 | 0 | 0 | 5 | 9 | 0 |
| Base | Siemens.Engineering.Library | 11 | 5 | 0 | 6 | 83 | 14 |
| Base | Siemens.Engineering.Library.Compare | 6 | 0 | 0 | 6 | 15 | 0 |
| Base | Siemens.Engineering.Library.MasterCopies | 8 | 2 | 0 | 6 | 20 | 5 |
| Base | Siemens.Engineering.Library.Types | 13 | 3 | 0 | 10 | 65 | 22 |
| Base | Siemens.Engineering.Multiuser | 14 | 1 | 0 | 13 | 47 | 3 |
| Base | Siemens.Engineering.Online | 2 | 1 | 0 | 1 | 15 | 5 |
| Base | Siemens.Engineering.Online.Configurations | 5 | 1 | 0 | 4 | 13 | 1 |
| Base | Siemens.Engineering.Online.Security | 2 | 0 | 0 | 2 | 2 | 0 |
| Base | Siemens.Engineering.Private | 1 | 0 | 0 | 1 | 9 | 0 |
| Base | Siemens.Engineering.Security | 12 | 3 | 0 | 9 | 41 | 6 |
| Base | Siemens.Engineering.Settings | 4 | 0 | 0 | 4 | 7 | 0 |
| Base | Siemens.Engineering.Umac | 31 | 2 | 0 | 29 | 107 | 3 |
| Base | Siemens.Engineering.Upload | 5 | 0 | 0 | 5 | 16 | 0 |
| Base | Siemens.Engineering.Upload.Configurations | 5 | 0 | 0 | 5 | 6 | 0 |
| Base | Siemens.Engineering.VersionControl | 11 | 5 | 1 | 5 | 36 | 20 |
| CFC | Siemens.Engineering.SW.FunctionCharts | 2 | 0 | 1 | 1 | 8 | 0 |
| DCC | Siemens.Engineering.MC.Drives.Dcc | 25 | 0 | 1 | 24 | 97 | 0 |
| DCC | Siemens.Engineering.MC.Drives.Dcc.DccExceptions | 39 | 0 | 0 | 39 | 39 | 0 |
| Safety | Siemens.Engineering.Safety | 12 | 1 | 0 | 11 | 54 | 2 |
| Safety | Siemens.Engineering.Safety.Download.Configurations | 1 | 0 | 0 | 1 | 1 | 0 |
| SafetyValidation | Siemens.Engineering.SafetyValidation | 14 | 0 | 0 | 14 | 60 | 0 |
| Sivarc | Siemens.Engineering.SiVArc | 72 | 6 | 1 | 65 | 314 | 6 |
| Startdrive | Siemens.Engineering.MC.Drives | 26 | 1 | 3 | 22 | 95 | 2 |
| Startdrive | Siemens.Engineering.MC.Drives.DFI | 16 | 0 | 0 | 16 | 51 | 0 |
| Startdrive | Siemens.Engineering.MC.Drives.SecurityObjects | 2 | 0 | 0 | 2 | 4 | 0 |
| Startdrive | Siemens.Engineering.SW.TechnologicalObjects.Motion | 23 | 0 | 2 | 21 | 95 | 0 |
| Step7 | Siemens.Engineering.Cax | 4 | 2 | 0 | 2 | 18 | 12 |
| Step7 | Siemens.Engineering.SW | 18 | 3 | 0 | 15 | 40 | 11 |
| Step7 | Siemens.Engineering.SW.Alarm | 8 | 1 | 1 | 6 | 18 | 2 |
| Step7 | Siemens.Engineering.SW.Alarm.Exceptions | 1 | 0 | 0 | 1 | 1 | 0 |
| Step7 | Siemens.Engineering.SW.Alarm.TextLists | 5 | 0 | 1 | 4 | 13 | 0 |
| Step7 | Siemens.Engineering.SW.Blocks | 19 | 5 | 4 | 10 | 72 | 24 |
| Step7 | Siemens.Engineering.SW.Blocks.Exceptions | 1 | 0 | 0 | 1 | 1 | 0 |
| Step7 | Siemens.Engineering.SW.Blocks.Interface | 4 | 1 | 0 | 3 | 8 | 1 |
| Step7 | Siemens.Engineering.SW.ExternalSources | 6 | 1 | 0 | 5 | 21 | 3 |
| Step7 | Siemens.Engineering.SW.Loader | 1 | 1 | 0 | 0 | 2 | 2 |
| Step7 | Siemens.Engineering.SW.OpcUa | 9 | 4 | 2 | 3 | 42 | 12 |
| Step7 | Siemens.Engineering.SW.OpcUa.AccessControl | 7 | 0 | 0 | 7 | 31 | 0 |
| Step7 | Siemens.Engineering.SW.Supervision | 6 | 0 | 1 | 5 | 13 | 0 |
| Step7 | Siemens.Engineering.SW.Tags | 11 | 5 | 0 | 6 | 62 | 26 |
| Step7 | Siemens.Engineering.SW.TechnologicalObjects | 8 | 1 | 0 | 7 | 27 | 4 |
| Step7 | Siemens.Engineering.SW.TechnologicalObjects.Ident | 1 | 0 | 0 | 1 | 2 | 0 |
| Step7 | Siemens.Engineering.SW.Types | 11 | 2 | 1 | 8 | 41 | 14 |
| Step7 | Siemens.Engineering.SW.Units | 8 | 2 | 0 | 6 | 27 | 2 |
| Step7 | Siemens.Engineering.SW.WatchAndForceTables | 11 | 3 | 0 | 8 | 44 | 10 |
| TeamcenterGateway | Siemens.Engineering.TeamcenterGateway | 12 | 0 | 0 | 12 | 51 | 0 |
| TestSuite | Siemens.Engineering.TestSuite | 4 | 0 | 1 | 3 | 15 | 0 |
| TestSuite | Siemens.Engineering.TestSuite.ApplicationTest | 7 | 1 | 0 | 6 | 24 | 5 |
| TestSuite | Siemens.Engineering.TestSuite.StyleGuide | 4 | 1 | 0 | 3 | 15 | 3 |
| TestSuite | Siemens.Engineering.TestSuite.SystemTest | 5 | 1 | 0 | 4 | 17 | 3 |
| WinCC | Siemens.Engineering.Hmi.Communication | 2 | 2 | 0 | 0 | 5 | 4 |
| WinCC | Siemens.Engineering.Hmi.Cycle | 2 | 1 | 0 | 1 | 6 | 3 |
| WinCC | Siemens.Engineering.Hmi.Faceplate | 1 | 0 | 0 | 1 | 1 | 0 |
| WinCC | Siemens.Engineering.Hmi.Globalization | 3 | 0 | 0 | 3 | 6 | 0 |
| WinCC | Siemens.Engineering.Hmi.RuntimeScripting | 8 | 0 | 0 | 8 | 21 | 0 |
| WinCC | Siemens.Engineering.Hmi.Screen | 26 | 2 | 0 | 24 | 63 | 6 |
| WinCC | Siemens.Engineering.Hmi.Tag | 9 | 5 | 0 | 4 | 28 | 14 |
| WinCC | Siemens.Engineering.Hmi.TextGraphicList | 4 | 0 | 0 | 4 | 10 | 0 |
| WinCC.Extension | Siemens.Engineering.Hmi | 3 | 1 | 0 | 2 | 28 | 2 |
| WinCCUnified | Siemens.Engineering.HmiUnified | 1 | 1 | 0 | 0 | 21 | 5 |
| WinCCUnified | Siemens.Engineering.HmiUnified.Common | 5 | 0 | 0 | 5 | 13 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.Cpm | 14 | 3 | 0 | 11 | 70 | 4 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiAlarm | 6 | 0 | 0 | 6 | 32 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon | 2 | 0 | 0 | 2 | 22 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiAudit | 3 | 0 | 0 | 3 | 9 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiConnections | 5 | 1 | 0 | 4 | 21 | 5 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiLogging | 6 | 0 | 0 | 6 | 10 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon | 6 | 0 | 2 | 4 | 28 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiOpcUaAlarm | 2 | 0 | 0 | 2 | 10 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.HmiTags | 12 | 4 | 0 | 8 | 74 | 18 |
| WinCCUnified | Siemens.Engineering.HmiUnified.LoggingTags | 2 | 0 | 0 | 2 | 22 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.RuntimeSettings | 12 | 1 | 0 | 11 | 82 | 1 |
| WinCCUnified | Siemens.Engineering.HmiUnified.Scripts | 2 | 1 | 0 | 1 | 7 | 3 |
| WinCCUnified | Siemens.Engineering.HmiUnified.TextGraphicList | 6 | 0 | 0 | 6 | 14 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI | 1 | 0 | 0 | 1 | 4 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Base | 11 | 0 | 0 | 11 | 53 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Controls | 15 | 0 | 2 | 13 | 120 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Dynamization | 6 | 1 | 0 | 5 | 16 | 4 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Dynamization.Flashing | 1 | 0 | 0 | 1 | 4 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Dynamization.Script | 3 | 1 | 1 | 1 | 12 | 2 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Dynamization.Tag | 7 | 0 | 0 | 7 | 20 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Events | 96 | 0 | 0 | 96 | 243 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Features | 13 | 0 | 0 | 13 | 75 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Parts | 85 | 0 | 0 | 85 | 370 | 0 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.ScreenGroup | 2 | 1 | 0 | 1 | 6 | 4 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Screens | 3 | 1 | 0 | 2 | 33 | 3 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Shapes | 20 | 1 | 1 | 18 | 129 | 2 |
| WinCCUnified | Siemens.Engineering.HmiUnified.UI.Widgets | 19 | 3 | 0 | 16 | 133 | 7 |


完整的 1,030 个未触及类型及其成员清单体积过大，不入库；在放置了 PublicAPI 的机器上运行下列命令即可重新生成到被忽略的 `bin-build/audits/` 目录（`-SummaryMarkdown` 输出含逐类型列表）：

```powershell
.\scripts\diagnostics\Audit-OpennessCoverage.ps1 -PublicApiDirectory .\TIA_V21_PublicAPI\V21\net48 -Version V21 -SummaryMarkdown .\bin-build\audits\V21-summary.md
```

V21 Update 或新版本安装后重跑并 diff 本页统计表，即为路线图要求的"再核对"步骤。
