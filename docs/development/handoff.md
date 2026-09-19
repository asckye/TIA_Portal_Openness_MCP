# 接续工作交接（2.7.31 之后，2026-09-18）

[文档目录](../README.md) · [路线图 §2.0](roadmap.md#20-官方-api-全量对齐计划) · [发布流程](release-workflow.md) · [覆盖清单](../reference/openness-coverage.md)

换机器继续"官方 Openness API 全量对齐"计划时先读这一页。它记录**当前停在哪**、**下一步做什么**、**每个阶段的固定动作**、**发布闸门**和**只在真机上学到的 API 事实**——这些都不在代码里，也不在提交历史里。

## 1. 现状（2.7.31 已发布）

- 最新 tag `v2.7.31`（2026-09-18，desktop-ivrlcht 构建）。
- 工具 386 个、默认 lite 56；离线套件 1550 项；引擎 API 形状检查 V20 1291 / V21 1388（`Build-Release.ps1` 第 73 行硬编码这两个数）。
- 审计（V21）：类型 1,217 = 专用引用 288 / 仅类型名 91 / 动态覆盖 325 / 完全未触及 513；未封装功能类型 239 / 929（Base 61 / 194、Step7 44 / 140、经典 WinCC 24 / 59、`WinCC.Extension` 2 / 11、选件包 108 / 525，Unified 0，Library 0）。
- 阶段 3 ③-② 库深层（2.7.31）：`ReadLibraryOverview` / `ReadLibraryType` / `ManageLibraryType` / `CheckLibraryUpdates` / `SynchronizeLibrary` / `CompareLibraryObjects` + `ManageGlobalLibrary`（infos / openInfo / archive）与 `ImportLibraryTypeDocuments`（版本导入）扩展。**2.7.31 真机重跑已于 2026-09-18 完成**：2.7.30 的三处修复（组合刷新、就地捕获已释放代理、通道 accessMode）全部验证通过；全局库头/母版树、`ReadLibraryType` 三种入口、`ManageLibraryType` 预览、`CompareLibraryObjects` 三种对象、`ManageGlobalLibrary infos/openInfo` 通过。**暴露 4 处要在 2.7.32 修的问题**：`ProjectLibrary` 没有 `Name`（工程库上的 `ReadLibraryOverview` / `CheckLibraryUpdates` / `SynchronizeLibrary` 与原有 `CompareLibraries` 全部在读头时失败）；`SystemGlobalLibrary.TypeFolder` 为 null 未守卫（NRE）；`UpdateCheck` 对系统库触发 TIA `NonRecoverableException` 并被连接保护当作断连；`close` 对系统库报 "not found" 而非说明 API 没有 `Close`。记录见 `docs/releases/v2.7.31.md#真机结果`。
- 阶段 3 ③-① 硬件网络深层（2.7.30）：13 个强类型工具（`ReadIoSystems` / `ManageIoSystem`、`ReadNetworkDomains` / `ManageNetworkDomain`、`ReadTransferAreas` / `ManageTransferArea`、`ReadDeviceItemChannels` / `UpdateDeviceItemChannel`、`ReadDeviceAddressing` / `UpdateDeviceAddress`、`ManageDeviceUserGroup`、`ManageDeviceUsers`、`ManagePortInterconnection`）。**2.7.30 真机重跑已于 2026-09-18 完成**：全部到达真实对象，读取与可复原写入通过；暴露两处引擎缺陷（域组合代理在 Create/Delete 后陈旧；`EngineeringObjectDisposedException` 误触发连接失败保护）与一处改进（通道属性名缺 AccessMode），记录见 `docs/releases/v2.7.30.md#真机结果`。
- 阶段 1 Safety（2.7.25）、阶段 2 WinCC Unified（2.7.26–2.7.29）已收口。**2.7.29 真机重跑已于 2026-09-18 完成**（变量导出核对通过；文本/系统文本列表读取与原生导出、`Validate`、画面组建删通过），登记表 `scripts/diagnostics/openness-dynamic-coverage.json` 已回写；仍未真机验证的只剩 `Cpm.*` 与 `HmiConnections.*`（工程无对象）以及 `HmiOpcUaAlarm` / `LoggingTags` 的 `Create`（组合到达但为空）。记录见 `docs/releases/v2.7.29.md#验证`。

## 2. 下一步（按顺序）

1. ~~部署 2.7.31 到虚拟机并真机验证~~ **2026-09-18 完成**（虚拟机已跑 2.7.31.0；结果与缺陷见 `docs/releases/v2.7.31.md#真机结果`）。**2.7.32 第一件事是修库工具的 4 处真机缺陷**（与 ③-③ 同一个发布）：
   - `Portal.LibraryDeep.cs` / `Portal.ProjectSecurity.cs`（`CompareLibraries`）：库名统一走一个 `LibraryLabel(object)` 助手——`GlobalLibrary.Name`，`ProjectLibrary` 用固定标签 "ProjectLibrary" + 所属工程名（PublicAPI 里 `ProjectLibrary` 只有 `MasterCopyFolder` / `Parent` / `TypeFolder`）。
   - `ReadLibraryOverview` / `ResolveSelection`（`EngineeringLibraryFolder`）：`TypeFolder` 为 null 时输出 `typeFolder=null` + `typeFolderNote`（系统库只有母版），选择集解析对 null 根给明确 `ArgumentException`。
   - `CheckLibraryUpdates`：库没有 `TypeFolder`（`SystemGlobalLibrary`）时在调用 TIA 前拒绝，避免 `NonRecoverableException` 触发连接保护。
   - `ManageGlobalLibrary close/save/saveAs/archive`：目标是 `SystemGlobalLibrary` 时报 "Close/Save/Archive exist only on UserGlobalLibrary in the Openness API"，`openInfo` 的返回里对系统库注明无法经 API 关闭。
   - 加离线测试钉住：`LibraryDeepLogic` 的标签/守卫纯逻辑；形状检查加 `ProjectLibrary` 无 `Name`、`UserGlobalLibrary.Close` 的成员断言。
   - 2.7.32 部署后重跑：工程库 `ReadLibraryOverview`（26 个顶层文件夹：PLC / LGF / LBC / _SICAR_Types_PLC / LSafe / …）、`CheckLibraryUpdates` 两种 mode、`SynchronizeLibrary` 三种预览、`CompareLibraries`（工程库 vs `Buttons-and-Switches`，需先 `openInfo`）、系统库 `ReadLibraryOverview includeTypes=true`。
2. **阶段 3 剩余子批次**（Base 未封装 61 类型 / 194 成员，全部在 `Siemens.Engineering.Base.dll`，V20/V21 都有）：
   - ~~③-① 硬件网络深层~~ **2.7.30 完成**（HW 里只剩 `CertificateSupportedService`、`Telecontrol*DataPoint`、`WebApplicationConfiguration`、`CatalogEntry`、`HardwareUtility` / `ModuleInformationProvider` / `OpcUaExportProvider` / `CardReaderPscProvider`、`Watch/ForceTableAccessRule`、`StructuredData` / `TableData`，可并入 ③-④ 收尾）。
   - ~~③-② 库~~ **2.7.31 完成**（Library 命名空间功能类型归零；`LibraryCompareResult*` 经 `CompareLibraries` 反射到达，登记为动态覆盖，真机待验）。
   - ③-③ 用户管理与安全（2.7.32，起点已备好）：
     - 缺口（2.7.31 审计）：`Security` 6 类型 / 23 成员——`SyslogServerProvider.Servers` → `SyslogServerComposition.Create` → `SyslogServer`（Address / Port / Tls / Comment / Name / AssignedModules / Delete）；`CertificateTemplate`（Signature / SubjectCommonName / SubjectAlternativeNames / Usage / ValidFrom / ValidUntil）+ `SubjectAlternativeNameComposition.Create` / `SubjectAlternativeName`（Type / Value / Delete）+ `CertificateComposition.Create(template) / Import`；`PlcPasswordPolicyService.PasswordPolicyEnabled` 与 `LegacyPlcPasswordPolicyService`（MinimumLength 等 5 项）。`Umac` 3 类型 / 10 成员——`UmcUser`（Activate / Deactivate / Delete / DomainId / IsActive / SetName）、`UmcCredentials`（Name / SetPassword）、`SystemDeviceFunctionRight.Comment`；另有仅点名未用的 `UmcServer.GetUserByName / GetUserGroupByName`、`UmcUserComposition.Create / CreateOfflineUmcUser`、`UmcUserGroupComposition.Create / CreateOfflineUmcUserGroup`、`UmcUserInfo` / `UmcUserGroupInfo`、`PasswordPolicyConfigurator`（8 项）、`EngineeringFunctionRightAssociation.Add/Remove`、`RoleAssociation.Add/Remove`、`CustomRoleComposition.Create`。
     - 已有工具别重做：`ReadProjectUserManagement` / `ManageProjectUserManagement`（工程用户/角色/权利 15 种动作）、`ReadProjectProtection`、`ManagePlcCertificate`（`LocalCertificateManager`）、`ManagePlcBlockProtection`；③-③ 是补 UMC 服务器侧用户/组、Syslog、证书模板、密码策略。
     - 官方页面（`https://docs.tia.siemens.cloud/api/khub/maps/gpR5ZkKnLSuzVoGX1ovoKg/topics/<id>/content` 直接取正文）："Functions for UMAC Global Users and UMC Server" 目录 `HHAe5KUz11B0PKDlSxUKhw`；Adding an UMC User `RaSbPMQe6yPvxCsBzVctNw`、Adding an UMC User Group `bFz~hPbhWv6kGdzaCDeySA`、Managing offline UMC User And User Groups `hymB8NODTv2Be0KT264V0Q`、Retrieving an UMC User from a UMC Server `EjA_EV4WL9pZS~L~mKJcDA`、Retrieving an UMC User Group `nS5ctWbfgpnlgzOAXC8qiw`、Finding an UMC User `gxlEAZbcmONryfAu~77BmQ`、Finding an UMC User group `j1~yVTInnGjJ4sX14nzROw`、Getting all UMC Users `d2TQ7PskB2Ym0yzvRylVqQ`、Getting all UMC groups `wY7Eq0XFHOvye0hgV0uarQ`、Activating/Deactivating `4c6G7j0mO0bdsl3FCrXNHg`、Checking state `m03R3RCcbujsOfE9ylBx8g`、Deleting `pd0zXQFPZbhoo5A1zcFFMg`、Assigning role to UMC User `zMSK6yo9V17NlHjKnDXsVg` / User Group `ppLE5OTpSkRdXBsfsnl~XA`、Removing role `0klneLoq1N76kAjqK3lReg`、Getting assigned roles `vYH2QmzwcsQkUu1lQ5IqZQ`、Authentication to connect to a UMC Server `PVFJJmFJBQqKACi3rTNi1A`、Synchronizing UMC user `tq6RQzc55iri0FRacjGKMQ`、Setting password policies for UMAC `3MwpthV7Ol6LEUkK8tjfjQ`、Setting password policy for PLC `4Wov24Ut~GV88CySQ3dUVQ`、Accessing Anonymous user `C38M0Qpc12CcZF4hrTJtBQ`、Device Function Rights `_4fXrVoziD4ewTDOgEfG3w`、Engineering Function Rights `izQBWtGgEdJJvfvC~XlxPw`、Managing certificate `cSHjp~ggDchSBvrL2zgyWQ`、Managing dynamic certificate settings `4dAdhWtWlHDplZkfGghnNQ`、Downloading User Management data `KqHGmbijjSjYOaWIBbTUuQ`。Syslog 页面按标题搜不到，用 `clustered-search` 搜正文 "SysLogServerConfiguration"（`HW.Features.SysLogConfigurationManager` 在 2.7.30 真机上 CPU 项有此服务）。
     - 真机约束：虚拟机工程没有 UMC 服务器，UMC 用户/组只能验 offline 用户（`CreateOfflineUmcUser`）与本地读取；密码走 `PlcBlockServicesLogic.ToSecureString`，绝不回显。
   - ③-④ Base 收尾：`Compare` 结果元素、`CrossReference` 的 `SourceObject` / `ReferenceObject` 深层字段、下载/上载/在线/编译结果消息与配置类、`TiaPortalSetting(sFolder)`、Multiuser 零头、HW 零头（见 ③-① 行）。
   - 每个子批次一个发布（③-③ 为 2.7.32）；先在浏览器里把官方章节逐页读完（`https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows`，`get_page_text` 可直接取正文），再对照 `TIA_V21_PublicAPI\V21\net48\Siemens.Engineering.Base.xml` 核成员签名（V21 的 `net48` 目录没有单体 `Siemens.Engineering.dll/.xml`，Base 类型在 `Siemens.Engineering.Base.*`；V20 仍是 `Siemens.Engineering.xml`）。
3. 之后按路线图：阶段 4 Step7（软件单元、`PlcDocument*`、校验和/仿真设置提供者）→ 5 经典 WinCC 文件夹层次 → 6 选件包（只做形状检查）。

剩余缺口一览（2.7.31 审计，未封装功能类型 / 成员；壳子类型不计）：

| 层次 | 剩余 | 内容 |
|---|---|---|
| 核心 | 129 / 393 | Base 61 / 194、Step7 44 / 140、经典 WinCC 24 / 59 |
| ├ ③-③ 安全 / UMC | 9 / 33 | 见上 |
| ├ ③-④ Base 收尾 | ~52 / 161 | `Siemens.Engineering` 根命名空间 10 / 28（`TiaPortalSession` / `Transaction`、`TiaPortalProduct`、`AttributeConfiguration`、`UmacCredentials`…）、Download / Upload / Online / Compiler 结果消息与配置 17 / 41、Connection 3 / 7、Multiuser 3 / 7、Settings 2 / 5、VersionControl 2 / 2、Compare 1 / 5、CrossReference 2 / 15、HW 零头 13 / 40 |
| ├ 阶段 4 Step7 | 44 / 140 | `SW.Units` 3 / 18、`SW` 11 / 14（`PlcDocument*`、`PlcChecksumProvider`…）、`SW.ExternalSources` 4 / 13、`SW.Blocks` 4 / 11、TO 映射 3 / 36 等 |
| └ 阶段 5 经典 WinCC | 24 / 59 | `Hmi.Screen` 17 / 38（弹出/滑入画面文件夹层次）、`Hmi.RuntimeScripting` 3 / 10、`Hmi.Tag` 3 / 8、`Hmi.Globalization` 1 / 3 |
| WinCC.Extension | 2 / 11 | `ConstValue`、`NullableDateTime` |
| 选件包（只做形状检查） | 108 / 525 | SiVArc 33 / 198、Startdrive 35 / 127、DCC 14 / 69、SafetyValidation 9 / 43、TestSuite 9 / 44、Teamcenter 7 / 37、CFC 1 / 7 |
| **合计** | **239 / 929** | 2.7.30 为 253 / 1,006，2.7.29 为 267 / 1,067 |

## 3. 每个阶段的固定动作

| 步骤 | 位置 / 命令 |
|---|---|
| 纯逻辑（无 Siemens 依赖）：参数门控、JSON 拆分、路径规则 | `tools/tiaportal-mcp/src/TiaMcpServer/Siemens/<Family>Logic.cs`（Unified 的放 `Siemens/Hmi/`）；把它加进 `tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj` 的 `<Compile Include=…>` |
| 门户实现 | `Siemens/Portal/Portal.<Family>.cs`，用 `RunHmiStepTool(name, meta => {...}, requiresProject)`、`ExactPlcForEngineering`、`ResolvePlcService<T>`、`AcquireHmiEditAccess()`、`EngineeringScalarProperties`、`EngineeringGroupOperations`、`EngineeringObjectAddress`（属性路径 JSON `[{property,name}]`）、`NativeFileOutput.Plan/Verify`、`PortalException(PortalErrorCode.*)`；V20/V21 差异用 `#if TIA_V20` |
| 工具注册 | `ModelContextProtocol/Tools/McpServer.<Family>.cs`：`[McpServerTool(Name=...), Description("[L2][Domain][OP] ...")]`；描述里点名的其他工具必须真实存在（`scripts/checks/Check-DeadToolReferences.py`，例外名加进其 `ALLOWED`） |
| 离线测试 | `tests/TiaMcpServer.Tests/<Family>Tests.cs`，在 `Program.cs` 注册；`dotnet run -c Release` 于该目录。**不能用 Windows 盘符字面量**（CI 在 ubuntu 上跑），要 `Path.Combine(Path.GetTempPath(), …)` |
| 引擎 API 形状检查（逐成员核对 PublicAPI） | `tests/TiaMcpServer.HttpTests/<Family>ShapeChecks.cs`，在其 `Program.cs` 的 `engineering-api-only` 分支注册；跑 `tests/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe <引擎exe> engineering-api-only <PublicAPI目录>`，两版都跑，把 `COMPLETE: N` 写回 `scripts/build/Build-Release.ps1` 第 73 行 `if($major -eq 21){…}else{…}` |
| 覆盖审计回写 | `.\scripts\diagnostics\Audit-OpennessCoverage.ps1 -PublicApiDirectory <V21\net48> -Version V21 -OutputDirectory .\bin-build\audits\<名> -SummaryMarkdown .\bin-build\audits\<名>\V21-summary.md`；把统计表与"动态覆盖的类型"段整段替换进 `docs/reference/openness-coverage.md`，更新标题/缺口结构段的数字。"未封装功能类型"= `V21-types.csv` 里 `status=UNTOUCHED` 且类型名不以 `Composition/Association/EventArgs/Exception/Result/Info` 结尾者，按程序集求和 |
| 动态覆盖登记 | 反射/泛型到达、类型名不出现在源码里的类型，登记进 `scripts/diagnostics/openness-dynamic-coverage.json`（`pattern` / `tool` / `mechanism` / `verified`）；`verified` 必须如实写真机证据或 "shape checks only" |
| 文档 | `CHANGELOG.md`、`docs/releases/vX.Y.Z.md`、`docs/development/roadmap.md`（§2.0 表 + §5 已完成段，编号顺延）、`docs/reference/capabilities.md`、`docs/reference/openness-coverage.md`、`docs/README.md` 当前发布链接；工具数手改 `README.md` / `README.zh-CN.md` / `tools/tiaportal-mcp/skill/SKILL.md`（`tool-matrix.md` 与 `manifest/*` 由构建生成） |
| 版本号 | 两个 `.csproj`（`AssemblyVersion` / `FileVersion` / `InformationalVersion`）、`tools/mcp-configurator/Configurator.cs`、`.claude-plugin/plugin.json` |

## 4. 构建与发布闸门（新机器要先满足）

- 需要：Windows、.NET SDK 8 或 10（desktop-ivrlcht 用 10.0.401，net48 目标包正常）、Python 3.10+、Git，以及**放在仓库根的 PublicAPI 副本**（Siemens 授权组件，不可分发，`.gitignore` 已忽略这两个目录名）：`TIA_V20_PublicAPI\V20`（2000.4.401.2）与 `TIA_V21_PublicAPI\V21\net48`（2100.0.121.1，含 `Siemens.Engineering.Safety.dll`）。不需要安装 TIA Portal。
- `dotnet build … -p:SiemensEngineeringDirectory=D:\…` 要在 PowerShell 里跑：Git Bash 会改写反斜杠路径，Openness NuGet 的 targets 找不到目录就回落到 "Package" 解析，结果是 456 个 CS0246。
- 统一构建：
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Release.ps1 -V20ReferenceRoot <repo>\TIA_V20_PublicAPI\V20 -V21ReferenceRoot <repo>\TIA_V21_PublicAPI\V21\net48 -Python <python.exe 完整路径>
  ```
  约 4–15 分钟；它重建两版引擎、跑离线套件与形状检查、重建 `TiaMcpConfigurator.exe`、重生成 `manifest/*.json` 与 `docs/reference/tool-matrix.md`。若后台运行并用 `*>` 记日志，日志是 UTF-16——用 `tr -d '\0'` 再 grep。**构建期间不要再改 `src` / `tests` 里的任何文件**：源码哈希在最后一步（`delivery.log` 的 Validate-Bundle）才算，中途改一行就 "Source changed after validation"，整轮白跑（2.7.30 第一轮就是这样）。
- 手工单版构建（调试用）：V21 `dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V21.csproj -c Release -p:SiemensEngineeringDirectory=<V21\net48>`；V20 需另给 `-p:SiemensEngineeringDirectory=<V20> -p:BaseIntermediateOutputPath=<src>\obj-v20\ -p:MSBuildProjectExtensionsPath=<src>\obj-v20\`（绝对路径、以 `\` 结尾；在 PowerShell 里跑，Git Bash 会把尾部反斜杠吃掉）。
- **哈希闸门**：`manifest/release-build.json` 记录 `tools/tiaportal-mcp/src` 与 `tests`（含离线测试 .cs）的源码哈希；任何改动后不重跑 Build-Release，`validate-bundle` CI 与 `Package-Release.py` 都会报 "Source changed after validation"。不要手改哈希。只改文档/登记表不触发。
- 提交模式：`Release X.Y.Z (1/3)` 源码 + 版本 + CHANGELOG + 发布说明 + 文档；`(2/3)` `runtime/v20/TiaMcpServer.exe`；`(3/3)` `runtime/v21/TiaMcpServer.exe` + `TiaMcpConfigurator.exe` + `manifest/*` + `tool-matrix.md`。然后 `python scripts/build/Package-Release.py --git "<git.exe>"` 本地干跑，`git push origin master`，再推注解 tag `vX.Y.Z` → "Publish complete release" 工作流上传 ZIP。
- 不要 `git add -A`：仓库根可能有本地大压缩包。提交、PR、Release 正文**不加任何 AI 署名行**（维护者明确要求）。
- 本地等价于 CI 的检查：`validate` 工作流 = `python scripts/checks/Check-Repository.py` + `./scripts/checks/Validate-Bundle.ps1 -Strict`（PowerShell）+ `Build-Configurator.ps1 -Test`；`offline-checks` = `dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release` + `python scripts/checks/Check-DeadToolReferences.py`。推 master 前四个都过；推 tag 前确认两条工作流已绿。
- CI 状态无 `gh` 时：`curl -s https://api.github.com/repos/asckye/TIA_Portal_Openness_MCP/actions/runs?per_page=6`；发布产物：`.../releases/tags/vX.Y.Z`（ZIP + .sha256 两个资产）。
- 遇到过一次 `.git/refs/heads/master` 被写成 41 个空白字符（`git status` 报 "No commits yet"、push 报 "cannot be resolved to branch"）：提交对象与 reflog 都完好，`rm .git/refs/heads/master` 后 `git update-ref refs/heads/master <reflog 里最后的 sha>` 即恢复，`git fsck` 干净。再遇到照此处理，不要重新 clone。
- 给 AI 助手：含反斜杠的补丁脚本（Windows 路径、`\"` 转义）不要经 Bash heredoc 传，先用 Write 写到临时目录再执行；文档补丁一律先 `assert s.count(old)==1` 再替换。

## 5. 真机验证约定

- 虚拟机 HTTP 端口 `8765`（Bearer 鉴权，用户 `SIEMENS`，TIA V21），**地址随宿主机网络变**：一台机器上是 `192.168.0.172`，desktop-ivrlcht 上是 `10.10.10.56`——以本机 `~/.claude.json` 里 `tia-portal-vm` 的 `url` 为准。服务器端路径（`C:\Users\SIEMENS\...`）在虚拟机上，宿主机不可访问。
- 宿主机若设了 `HTTP_PROXY`（desktop-ivrlcht 是 `127.0.0.1:7897`），MCP 客户端会把局域网请求送进代理，表现为 "Version negotiation probe timed out after 5000ms"（curl 经代理同样 5 秒 502，`--noproxy '*'` 直连立即 401/200）。把虚拟机地址加进 `NO_PROXY`，或临时用直连脚本经 `CallTool` 调用。
- 默认 lite 只暴露 56 个工具，Unified 等全量工具经 `CallTool(name, argumentsJson)` 调用；大响应（>20,000 字符）会被服务器切成 `GetExport(exportId, offset)` 分页——验证时给小的 `limit`（2–5）而不是翻页。
- 直连探针（不经 MCP 客户端，绕过代理）：`python scripts/diagnostics/Probe-McpServer.py tools [关键字]` / `call <lite 工具> '{...}'` / `bridge <任意工具> @args.json`，连接信息默认取 `~/.claude.json` 的 `tia-portal-vm`（或 `--url` / `--token`、`TIA_MCP_URL` / `TIA_MCP_TOKEN`）。`bridge` 会把 `CallTool` 包装的内层 JSON 解开打印。每次调用是独立 MCP 会话，引擎的 Portal 状态跨会话保留。
- 换引擎 exe 后必须重启虚拟机上的服务进程，否则 `server:` 行仍报旧版本。工具返回 "HMI operation blocked after a connection failure"（`GetState` 里 `snapshotReadsBlocked=true`）时，再调一次 `AttachToOpenProject` 即解封；这是 HMI 快照的保守保护，已释放代理在 2.7.31 起由各工具就地处理。
- 工程 `AutomaticDipCoatingMachine`（V21），PLC `+S1-K1`（1510SP F V4.1），HMI `HMI_RT_1`（Unified）。**永不保存工程**；临时对象（画面、变量、组）用完即删；改过的设置（报警类颜色、记录大小、GlobalSettings）复原；先 `dryRun` 再实做。
- 工程事实（硬件）：设备 `ET 200SP station_1` 在组 `+S1` 下（`devicePathJson=["ET 200SP station_1"]` 单名即可），CPU 项路径 `["+S1-K1"]`（**不经**机架 `机架_0`），PROFINET 接口 `["+S1-K1","PROFINET 接口_1"]`，其端口 `["+S1-K1","PROFINET 接口_1","端口_3"]` 与 `["BA 2xRJ45","Port_1"]`；IO 模块 `["+S1-K3","+S1-K3"]`（K3 DI16、K4 DQ16、K5 AI2、K6 AI4 RTD）、`F-DI 8x24VDC HF_1`；OPC UA 子模块 `["+S1-K1","OPC UA_1"]`；4 台 SINAMICS G120C（`SINAMICS G_5` → `["+S1-7Q1","PROFINET 接口"]` 等）是 IO 设备；子网 `PN/IE_1`，IO 系统 "PROFINET IO-System" #100；无拓扑互连、无传输区、无 MRP 环。
- 工程事实：`HmiSoftware.Connections` 为空（集成连接 `HMI_Connection_1` 只在硬件网络里）；无工厂视图；报警审计类为空；语言 en-US / zh-CN / de-DE；变量表 `Default tag table` 可放临时变量（`EnsureUnifiedHmiTag`，参数名是 `hmiSoftwarePath`）。

## 6. 只在真机上学到的 Openness 事实

（从 API 上核实的：`WebserverUserPermissions` 值为独立单比特但没有 `[Flags]`，组合值 `ToString()` 是数字，引擎按位解码；`SimpleWebserverUserComposition` 无 `Create`、`SimpleWebserverUser` 无 `Delete`；`Subnet.Nodes` / `Subnet.IoSystems` 是关联；`ISyncDomainParticipant` 由 `NetworkInterface` 与 `IoSystem` 实现；`TransferAreaType.F_CD` 仅 V21；`Address.AssignProcessImageToOrganizationBlock` 仅 V20。官方文档站的正文可经 `https://docs.tia.siemens.cloud/api/khub/maps/<mapId>/topics/<contentId>/content` 直接取 HTML，`clustered-search` API 按标题搜页面——比逐页导航快得多。）

- `Create<T>` / `Create` 返回的代理与 `Find()` 不同，`ReferenceEquals` 恒假——创建后按名称查回 + 计数核对。
- `HmiSlider` / `HmiToggleSwitch` / `HmiCircleSegment` / `HmiEllipseSegment` 隐藏基类 `EventHandlers`，`Type.GetProperty` 抛歧义——取最派生声明（`UnifiedUiModelLogic.FindProperty`）；`HmiLabel` 没有 `EventHandlers`；`HmiButton` 事件是 `Tapped` 不是 `Click`。
- `Object` 类型属性写 JSON 数字 `0` 后读回是字符串 `"0"`——跨类型比较（`EngineeringScalarProperties.SameValue`）。
- `HmiThresholdComposition.Create()` 在 PublicAPI 里，但 TIA V21 对普通变量拒绝 "New thresholds are not supported"；替代值只对外部变量可设，内部变量报"已禁用的字段不支持设置"；`Font.Weight` 只接受 `Bold` / `Normal`。
- `HmiTagComposition.Export(dir, name)` 回报的 `FileInfo` 没有 `.hmi.yml` 扩展名（文件本身写出）；脚本模块导出正确回报 `Name.hmi.yml` + `Name.hmi.js`。`ShiftModel` 这张 15 变量的表导出只有 `ShiftModel.hmi.yml`（4,400 B）一个文件，没有 `NameData.yml` 等副文件。
- 硬件（2.7.30 真机）：变更前取到的 HW 组合代理是陈旧的——`MrpDomainComposition.Create` 后在旧代理上按名查不到，`Delete` 后旧代理枚举抛 `EngineeringObjectDisposedException`；从 owner 重新导航即正常。空的临时同步域会被 TIA 释放。SINAMICS 接口暴露两个 IoConnector（文档说一个），未连接的那个所有 PN 属性不支持。1510SP F V4.1 上 `WebserverUserManagement` / `SimpleWebserverUserManagement` 为 null（Web 用户由 UMAC 接管），`OpcUaUserManagement` 在 `OPC UA_1` 子模块。`MrpInstancesOwner` 只在组态了 MRP 环的接口项上出现。ET 200SP DI 模块通道的 `ChannelActivated` / `InputDelay` 只读；AI 通道 `Smoothing` 写 1 被拒、写 0 同值可通。S7-1500 地址不支持 `Context` / `IsochronousMode` / `InterruptObNumber`。
- `HmiTextList.Export` / `HmiSystemTextList.Export` 各写两个文件：`<名>.hmi.yml` + `<名>.TextLibrary.hmi.yml`，后者带整个文本库（`PowerState` 的两个文件 554 KB + 599 KB）。
- `HmiTag.Validate()` 在 `ShiftModel` 的变量上返回 3 条 `HmiValidationResult`（`Connection` / `DataType` / `PlcTag`），错误文本按 TIA 界面语言本地化（zh-CN），`Warnings` 为空；"HMI_Connection_1 不存在"与 `HmiSoftware.Connections` 为空一致。
- `HmiScreenGroupComposition.Create(name)` 后 `deleteEmpty` 对空组生效，按名查回 "Exact named object not found"。
- 变量对象路径：`[{TagTableGroups,组},{Groups,子组},{TagTables,表},{Tags,变量}]`；`Default tag table` 在该工程为空，临时变量要用它但真实变量在 `/_SICAR_HMI-tags/...`。
- V20 画面对象目录 37 个、V21 43 个；`HmiScreenItemBaseComposition` 在 `UI.Base` 命名空间。
- 下载提示 `SafetyProgram` 应答 `ConsistentDownload`；`SafetyAdministration.ProgramSignatures` 与 `SafetyBaseIdProvider` 仅 V21。
- 库（2.7.31 真机）：`ProjectLibrary` 没有 `Name`（只有 `MasterCopyFolder` / `Parent` / `TypeFolder`）；`SystemGlobalLibrary.TypeFolder` 为 null（系统库只有母版，`Buttons-and-Switches` 的母版树在 `FunctionButtons/Rectangular` 等文件夹）；对系统库调 `UpdateCheck` 抛 `NonRecoverableException`（会被连接保护当作断连，需要 `AttachToOpenProject`，且打开的全局库句柄随之丢失）；`Close` / `Save` / `SaveAs` / `Archive` / `CleanUpLibrary` 只在 `UserGlobalLibrary` 上，`Open(GlobalLibraryInfo)` 打开的系统库无法经 API 关闭；`GetGlobalLibraryInfos` 在虚拟机上列出 4 个系统库（Buttons-and-Switches / Long Functions / Monitoring-and-control-objects / Documentation templates），没有用户全局库。工程库顶层 26 个类型文件夹（PLC / LGF / LBC / _SICAR_Types_PLC / Open Library V19 / LUC / LSicar / LAF / LAxisCtrl / LSafe / _LibrarySafety / LPD / … / LSyslog），`LGF/00 Bit logic operations` 下 7 个 `CodeBlockLibraryType`（`LGF_BitSet` 3.0.1 等，`originalLibrary=LGF_V5_4_0_TiaLib_V21`）可作比较/读取样本。`DetailedCompareResult` 的 `Original Library` 行状态是 `NotCompared`。
