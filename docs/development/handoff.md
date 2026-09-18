# 接续工作交接（2.7.29 之后，2026-09-18）

[文档目录](../README.md) · [路线图 §2.0](roadmap.md#20-官方-api-全量对齐计划) · [发布流程](release-workflow.md) · [覆盖清单](../reference/openness-coverage.md)

换机器继续"官方 Openness API 全量对齐"计划时先读这一页。它记录**当前停在哪**、**下一步做什么**、**每个阶段的固定动作**、**发布闸门**和**只在真机上学到的 API 事实**——这些都不在代码里，也不在提交历史里。

## 1. 现状（2.7.29 已发布）

- 最新 tag `v2.7.29`（提交 `6bedeb7`，ZIP 由 "Publish complete release" 工作流上传）；随后 `a5b6cc8` 修了 `offline-checks` 在 Linux runner 上从 2.7.25 起一直红的问题（测试里 `C:\...` 字面量），两个 CI 工作流现在都绿。
- 工具 367 个、默认 lite 56；离线套件 1446 项；引擎 API 形状检查 V20 990 / V21 1087（`Build-Release.ps1` 第 73 行硬编码这两个数）。
- 审计（V21）：类型 1,217 = 专用引用 238 / 仅类型名 101 / 动态覆盖 322 / 完全未触及 556；未封装功能类型 267 / 1,067（Base 89 / 332、Step7 44 / 140、经典 WinCC 24 / 59、`WinCC.Extension` 2 / 11、选件包 108 / 525，Unified 0）。
- 阶段 1 Safety（2.7.25）、阶段 2 WinCC Unified（2.7.26–2.7.29）已收口。动态覆盖登记表 `scripts/diagnostics/openness-dynamic-coverage.json` 里有 7 条 `verified` 写着 "shape checks only / NOT verified"——它们**没有**在真实工程上跑过。

## 2. 下一步（按顺序）

1. **部署 2.7.29 到虚拟机并重跑真机**（`runtime/v21/TiaMcpServer.exe` 2.7.29.0 替换后重启服务，`AttachToOpenProject`）：
   - `ExchangeUnifiedTags export`：`softwarePath=HMI_RT_1`，`tagTable=/_SICAR_HMI-tags/Shiftmodel/ShiftModel`，`fileName=ShiftModel`，`directory` 为 MCP 服务器上一个**不存在**的新目录（例如 `C:\Users\SIEMENS\Desktop\mcp_tag_export_2729`）。期望：`files[0].path` 以 `ShiftModel.hmi.yml` 结尾、带 `reportedPath`、`directoryListing` 列出副文件。2.7.28 在这里报 "Native export file missing"。
   - 登记表里"仅形状检查"各族补跑：`ReadUnifiedEngineeringObjects` 的 `textLists` / `graphicLists` / `systemTextLists` / `opcUaAlarmTypes`；`ExportUnifiedEngineeringList`（新目录）；`ManageUnifiedLoggingTag` 读；`ValidateUnifiedObject`（任一变量）；`ManageUnifiedHmiGroup`（`family=screens`，建一个临时组再删）。`Cpm`（工厂视图）与 `HmiConnections`（`HmiSoftware.Connections` 为空）在这个工程里**没有对象**，验证不了，登记表保持如实。
   - 通过后：更新登记表 `verified` 字段、`docs/releases/v2.7.29.md` 的"验证"表、`CHANGELOG.md` 2.7.29 条目末尾加一句真机记录（参照 2.7.27 / 2.7.28 的写法），提交信息形如 `Record the 2.7.29 real-project rerun; ...`（只改文档和登记表，不触发清单哈希闸门）。
2. **阶段 3：Base 硬件深层 / 库 / UMC / 安全**（起点 89 类型 / 332 成员，全部在 `Siemens.Engineering.dll`，V20/V21 都有）。建议子批次：
   - ③-① 硬件网络深层：`TransferArea` / `MulticastableTransferArea`、`IoSystem` / `IoConnector`、`SyncDomain`、`MrpInstance` / `MrpDomain`、`Channel`、`DeviceGroup`、`WebserverUser`（官方章节 "Functions for accessing the data of a device" → 网络/IO 系统/同步域/MRP/通道）。
   - ③-② 库：`GlobalLibrary` 打开/关闭/更新检查（`UpdateCheck`）、类型版本文件夹、`LibraryTypeVersion` 状态、实例/母版复制（官方 "Functions for libraries"）。
   - ③-③ 用户管理与安全：`UmcUser` / `UmcUserGroup` / `UmcCredentials`、`SyslogServer`、`CertificateTemplate`、`PlcPasswordPolicyService`（官方 "Functions for security"）。
   - ③-④ `Compare` 结果元素、`CrossReference` 的 `SourceObject` / `ReferenceObject` 深层字段。
   - 每个子批次一个发布（2.7.30 起）；先在浏览器里把官方章节逐页读完（`https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows`，`get_page_text` 可直接取正文），再对照 `TIA_V21_PublicAPI\V21\net48\Siemens.Engineering.xml` 核成员签名。
3. 之后按路线图：阶段 4 Step7（软件单元、`PlcDocument*`、校验和/仿真设置提供者）→ 5 经典 WinCC 文件夹层次 → 6 选件包（只做形状检查）。

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

- 需要：Windows、.NET SDK 8（含 net48 目标包）、Python 3.10+、Git，以及**放在仓库根的 PublicAPI 副本**（gitignored）：`TIA_V20_PublicAPI\V20`（2000.4.401.2）与 `TIA_V21_PublicAPI\V21\net48`（2100.0.121.1，含 `Siemens.Engineering.Safety.dll`）。不需要安装 TIA Portal。
- 统一构建：
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Release.ps1 -V20ReferenceRoot <repo>\TIA_V20_PublicAPI\V20 -V21ReferenceRoot <repo>\TIA_V21_PublicAPI\V21\net48 -Python <python.exe 完整路径>
  ```
  约 5–15 分钟；它重建两版引擎、跑离线套件与形状检查、重建 `TiaMcpConfigurator.exe`、重生成 `manifest/*.json` 与 `docs/reference/tool-matrix.md`。若后台运行并用 `*>` 记日志，日志是 UTF-16——用 `tr -d '\0'` 再 grep。
- 手工单版构建（调试用）：V21 `dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V21.csproj -c Release -p:SiemensEngineeringDirectory=<V21\net48>`；V20 需另给 `-p:SiemensEngineeringDirectory=<V20> -p:BaseIntermediateOutputPath=<src>\obj-v20\ -p:MSBuildProjectExtensionsPath=<src>\obj-v20\`（绝对路径、以 `\` 结尾；在 PowerShell 里跑，Git Bash 会把尾部反斜杠吃掉）。
- **哈希闸门**：`manifest/release-build.json` 记录 `tools/tiaportal-mcp/src` 与 `tests`（含离线测试 .cs）的源码哈希；任何改动后不重跑 Build-Release，`validate-bundle` CI 与 `Package-Release.py` 都会报 "Source changed after validation"。不要手改哈希。只改文档/登记表不触发。
- 提交模式：`Release X.Y.Z (1/3)` 源码 + 版本 + CHANGELOG + 发布说明 + 文档；`(2/3)` `runtime/v20/TiaMcpServer.exe`；`(3/3)` `runtime/v21/TiaMcpServer.exe` + `TiaMcpConfigurator.exe` + `manifest/*` + `tool-matrix.md`。然后 `python scripts/build/Package-Release.py --git "<git.exe>"` 本地干跑，`git push origin master`，再推注解 tag `vX.Y.Z` → "Publish complete release" 工作流上传 ZIP。
- 不要 `git add -A`：仓库根可能有本地大压缩包。提交、PR、Release 正文**不加任何 AI 署名行**（维护者明确要求）。
- CI 状态无 `gh` 时：`curl -s https://api.github.com/repos/asckye/TIA_Portal_Openness_MCP/actions/runs?per_page=6`。

## 5. 真机验证约定

- 虚拟机 `192.168.0.172:8765`（HTTP，用户 `SIEMENS`，TIA V21）；MCP 客户端配置名 `tia-portal-vm`。服务器端路径（`C:\Users\SIEMENS\...`）在虚拟机上，宿主机不可访问。
- 工程 `AutomaticDipCoatingMachine`（V21），PLC `+S1-K1`（1510SP F V4.1），HMI `HMI_RT_1`（Unified）。**永不保存工程**；临时对象（画面、变量、组）用完即删；改过的设置（报警类颜色、记录大小、GlobalSettings）复原；先 `dryRun` 再实做。
- 工程事实：`HmiSoftware.Connections` 为空（集成连接 `HMI_Connection_1` 只在硬件网络里）；无工厂视图；报警审计类为空；语言 en-US / zh-CN / de-DE；变量表 `Default tag table` 可放临时变量（`EnsureUnifiedHmiTag`，参数名是 `hmiSoftwarePath`）。

## 6. 只在真机上学到的 Openness 事实

- `Create<T>` / `Create` 返回的代理与 `Find()` 不同，`ReferenceEquals` 恒假——创建后按名称查回 + 计数核对。
- `HmiSlider` / `HmiToggleSwitch` / `HmiCircleSegment` / `HmiEllipseSegment` 隐藏基类 `EventHandlers`，`Type.GetProperty` 抛歧义——取最派生声明（`UnifiedUiModelLogic.FindProperty`）；`HmiLabel` 没有 `EventHandlers`；`HmiButton` 事件是 `Tapped` 不是 `Click`。
- `Object` 类型属性写 JSON 数字 `0` 后读回是字符串 `"0"`——跨类型比较（`EngineeringScalarProperties.SameValue`）。
- `HmiThresholdComposition.Create()` 在 PublicAPI 里，但 TIA V21 对普通变量拒绝 "New thresholds are not supported"；替代值只对外部变量可设，内部变量报"已禁用的字段不支持设置"；`Font.Weight` 只接受 `Bold` / `Normal`。
- `HmiTagComposition.Export(dir, name)` 回报的 `FileInfo` 没有 `.hmi.yml` 扩展名（文件本身写出）；脚本模块导出正确回报 `Name.hmi.yml` + `Name.hmi.js`。
- V20 画面对象目录 37 个、V21 43 个；`HmiScreenItemBaseComposition` 在 `UI.Base` 命名空间。
- 下载提示 `SafetyProgram` 应答 `ConsistentDownload`；`SafetyAdministration.ProgramSignatures` 与 `SafetyBaseIdProvider` 仅 V21。
