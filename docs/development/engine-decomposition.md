# 完整引擎拆分设计（重构阶段 3）

[重构计划](refactor-plan.md) · [版本框架](unified-version-framework.md) · [验证分层](validation.md)

本页是 P3-01 的设计结论。路径相对 `src/Engine`；`P.` 表示 `Siemens/Portal/Portal.`，
`T.` 表示 `ModelContextProtocol/Tools/McpServer.`。数字为 2026-10-03 的源码统计。

## 现状

- `McpServer`：静态 partial 类，93 个文件声明 488 个工具；`T.Profile` 的 `GetAllTools`/`GetLiteTools`、
  `T.ToolBridge` 和 `McpServer.cs` 以 `typeof(McpServer).GetMethods(Public|Static)` 扫描注册；
  `T.ArgDiagnostics` 的 `WrapTools` 叠加版本准入、参数诊断、串行化和响应保护，隔离模式用 `ProxyTool`。
- `Portal`：87 个 partial、约 3.1 万行的有状态单例；工具约 440 处调用。工具文件与 Portal partial 基本一一对应。
- `Program`：单文件 CLI 宿主；报告、探针和 HMI 模板逻辑分别位于 `Cli/` 下的四个内部静态类，仍在引擎程序集。CLI 通过 `EngineServices` 取得所属工具或服务实例，不再经过静态工具转发。
- `#if TIA_V20` 共 62 处，全部位于 Portal 方法体内，V20/V21 方法签名一致。
- 原生调用织入只插桩 `TiaMcpServer.exe` 自身（`@(IntermediateAssembly)`），所有调用西门子 API 的代码必须留在引擎程序集。

## 领域划分

| # | 领域 | Portal 部分（行） | 工具（行 / 个） |
|---|---|---|---|
| 1 | 内核：会话与工程 | P.cs、Binding、Helpers（解析部分）、HmiOperation、HmiEditAccess、SoftwareLookup、Software、ProjectExchange、BaseLeftovers：4,337 | 1,909 / 30 |
| 2 | 工具基础设施 | — | Profile、ArgDiagnostics、SerializedCalls、ToolBridge、CallDiscipline、ToolUsage、Guides、Batch、VersionPolicy、Patch：1,841 / 10 |
| 3 | PLC 程序（块、类型、组、文档、外部源、交叉引用、编译、删除、审计） | 3,599 | 5,161 / 94 |
| 4 | PLC 数据（变量/监控表、报警、OPC UA、工艺对象与映射） | 2,572 | 1,018 / 30 |
| 5 | 硬件与网络 | 5,212 | 1,483 / 54 |
| 6 | 在线与下载 | 1,248 | 427 / 12 |
| 7 | 安全与 Safety | 1,864 | 197 / 15 |
| 8 | Unified HMI（含迁移读取、工厂视图） | 5,594 | 1,532 / 94 |
| 9 | Classic HMI、Motion/ProDiag | 1,082 | 147 / 13 |
| 10 | 库与版本控制 | 1,990 | 1,203 / 25 |
| 11 | 可选包（Startdrive、DCC、CFC、SiVArc、TestSuite、Teamcenter、软件单元等） | 3,472 | 879 / 74 |
| 12 | 非 Openness：运行时通道、离线报告、CLI | `Runtime/` 2,903 | 2,127 / 37 |

各领域都依赖内核：`RunHmiStepTool`（实际是通用响应信封，约 60 个 partial 使用）、`AcquireHmiEditAccess`
（写锁，约 45 个）、`IsProjectNull`（132 处）、`GetSoftwareContainer`（53 处）、`GetPlcSoftware`（42 处）、
`ResolvePlcService`、`GetDeviceItemByPath`、`ResolveHmiSoftwareOrThrow`。

共享状态：`_portal`、`_project`、`_session` 及绑定字段只由 P.cs、Binding（`_project`/`_session` 另有
ProjectExchange 与 ProjectSecurity）写入；软件容器缓存、`_hmiReadFault`、`_ambientExclusiveAccess` 等跨 2–3 个
partial。领域私有状态（Teamcenter 连接、`migrationPages`、三个锁对象、`LastExportedFile` 等）随领域迁移。

## 目标结构

- **内核**：保留 `Portal` 类作为会话单例，显式实现新的内部接口 `IEngineeringSession`（工程/门户句柄、
  `RunStep`、`AcquireWriteAccess`、`VerifyBinding`、软件/PLC/硬件/库/HMI 解析、`AdoptProject`/`ReleaseProject`）。
  保留现有成员名，HttpTests 的 IL 与字段检查不受影响。服务不得直接写会话字段。
- **领域服务**：每对现有文件拆为一个服务类和一个 `[McpServerToolType]` 实例工具类；方法签名、返回类型、
  属性、参数与默认值原样迁移。先不为领域服务定义接口，需要替身测试时再加。纯辅助函数（`Safe`、`Names`、
  `Page`、`TryGetPropertyValue` 等）改为静态辅助类。
- **生命周期**：服务和工具类均为单例且不实现 `IDisposable`（SDK 每次调用后会释放目标对象）；
  `migrationPages` 留在内核，工程关闭前释放。
- **注册**：新增 `ToolCatalog` 扫描全部 `[McpServerToolType]` 类型（静态 `McpServer` 与实例类），检查名称唯一，
  替换现有 4 处 `typeof(McpServer)` 扫描；实例方法用 `McpServerTool.Create(MethodInfo, factory, options)`，
  目标对象来自 `RequestContext.Services` 或引擎根容器。不得使用 `Delegate.CreateDelegate` 或表达式编译
  （织入工具的调度审计会拒绝）。当前 SDK 为 ModelContextProtocol 0.3.0-preview.4，已支持实例工具。
- **装饰与隔离**：`WrapTools`、`ProxyTool` 不变；`ToolBridge` 调用改为按 `method.IsStatic` 选择目标。
  隔离父进程不注册会话，父进程本地执行的控制工具不依赖会话。
- **迁移期静态门面**：`EngineServices` 保存宿主容器（CLI 模式自建）；stdio、HTTP、worker 子进程与 CLI
  共用 `EngineRegistration.AddEngine`。步骤 16 已移除 `McpServer` 上的会话入口及工具转发；CLI 直接解析
  所属实例，隔离父容器不向 standalone 容器回退。
- **工具类位置**：保持在 `ModelContextProtocol/Tools/` 平铺（`Test-VersionCatalogWiring.py` 非递归扫描该目录）。
- **测试**：HttpTests 加载织入后的 V20/V21 发布程序，不能改为编译期引用；新增 `EngineSurface` 查找辅助，
  按工具名跨 `[McpServerToolType]` 类型查找，按成员名跨 `Portal` 与服务查找，IL 检查带声明类型。

## 每一步的验收

P3-04 的接口沿用 `RunHmiStepTool` / `AcquireHmiEditAccess`，对应上文的 `RunStep` / `AcquireWriteAccess`；
`Portal` 显式转发现有成员，工程句柄沿用 `CurrentProject`，另以只读接口属性暴露 `CurrentPortal` / `CurrentSession`。
`AdoptProject(project, missingProjectMessage)` 和 `ReleaseProject()` 分别保留归档取回与会话提交后的原有状态转换；
前者接收原调用点的空结果错误文本，后者仅清理已被原生提交关闭的句柄及软件缓存，不增加绑定或健康状态重置。

除 [重构计划](refactor-plan.md) 的 L0–L2 外：

1. 兼容快照与基线比较为 0 breaking、0 compatible、0 info，lite 名单一致。`tools/list` 顺序不属于契约；
   `ToolCatalog` 引入时按名称确定排序，此后不再变化。
2. 织入覆盖清单中西门子成员的多重集合（`sites[].member`）前后一致；迁移方法还须逐一比较方法体及其
   lambda 的有序调用点，不能只靠全局多重集合证明顺序不变。
3. 引用具体引擎源码路径的 8 个检查脚本和 3 个测试工程同步更新。
4. P0-06 的离线返回结构快照无差异。

真机实测暂缓期间，阶段 3 只做满足以上条件的纯迁移；行为变更（含 G9）单独评审。

## 迁移顺序

J 表示需要设计判断，M 表示可按说明机械执行。

1. P0-06 离线返回结构快照（FindTools、ListToolCategories、PreflightToolCall、断开时的 GetState、离线构造器等）。
2. **J** `ToolCatalog`、`EngineServices`、`EngineRegistration`，不迁移工具；同步 `Generate-ToolsListFromAssembly.ps1`、
   `Test-MigrationReadAssembly.ps1`、`Test-VersionCatalogWiring.py` 与 `ToolBridgeFixtures`。
3. **M** HttpTests `EngineSurface` 辅助。
4. **J** 内核接口：提取静态辅助，把 `ExactPlcForEngineering`、`ExactEngineeringHardware`、
   `ExactOpenEngineeringLibrary`、`ResolveHmiSoftwareOrThrow` 收入内核，加入 `AdoptProject`/`ReleaseProject`。
5. **M** 试点（不调用西门子 API）：OfflineSuites、Ecosystem、V21Ecosystem、Git、PlcTemplates、QualityAudit、
   ImportOrder、Guides、ToolUsage。
6. **M** 第一个 Portal 领域：CFC。
7. **M** 可选包（先叶子）：TestSuite、Step7Leftovers、V20Options、Optional/SpecializedExchange、SoftwareUnit、
   DCC → Teamcenter → Startdrive。
8. **M** 安全与 Safety；库与 VCI（VCI 静态状态迁入服务）；SiVArc。
9. **M/J** PLC 数据（TechnologyMapping 是后续依赖）。
10. **M/J** Classic HMI、Motion/ProDiag。
11. **J** 硬件与网络（需要 CLI 转发）。
12. **J** 在线与下载。
13. **J** PLC 程序（工具最多，含 IL 检查与 CLI）。
14. **J** Unified HMI（先完成 P1-05 对 partial `Portal` 测试夹具的改写）。
15. **J** 会话、工程和诊断工具迁入 `SessionTools`、`ProjectSessionTools`、`DiagnosticsTools`；领域迁移合并后的 `McpServer` 只保留基础设施工具与静态转发（运行时通道按第 16 步迁出）。基础设施工具名单及并行迁移期间的余项见下文“步骤 15 的静态工具边界”。
16. **done** `Program` 拆为 CLI 宿主，`Runtime/` 拆为独立程序集，删除 `McpServer` 的 `_portal`、`_services`、`Portal` 与 CLI 工具转发。
17. **J** G9。

服务注册按约定扫描，不集中编辑同一注册文件，步骤 7–10 可以并行。P1-04 移动的 `*Logic.cs` 与 HttpTests
查找有交叉，阶段 3 在 P1-04 三步完成后开始。

步骤 5 的 30 个试点工具已迁入以下平铺实例类；`Ecosystem` 与 `EcosystemFiles` 合为同一领域。
它们由 `EngineRegistration` 通过 `ToolCatalog` 自动注册为单例，不实现 `IDisposable`，也不解析工程会话。
既有领域算法仍复用 `TiaMcp.Logic` 和共享代码；本步保留工具方法体及纯静态辅助函数，不增加无状态服务转发层。

| 原试点文件（`McpServer.` 前缀） | 实例工具类 | 工具数 |
|---|---|---|
| `Ecosystem.cs`、`EcosystemFiles.cs` | [EcosystemTools](../../src/Engine/ModelContextProtocol/Tools/EcosystemTools.cs) | 3 |
| `V21Ecosystem.cs` | [V21EcosystemTools](../../src/Engine/ModelContextProtocol/Tools/V21EcosystemTools.cs) | 4 |
| `GitWorkflow.cs` | [GitWorkflowTools](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs) | 1 |
| `PlcTemplates.cs` | [TemplateTools](../../src/Engine/ModelContextProtocol/Tools/TemplateTools.cs) | 2 |
| `QualityAudit.cs` | [QualityAuditTools](../../src/Engine/ModelContextProtocol/Tools/QualityAuditTools.cs) | 1 |
| `ImportOrder.cs` | [ImportOrderTools](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs) | 1 |
| `Guides.cs` | [GuideTools](../../src/Engine/ModelContextProtocol/Tools/GuideTools.cs) | 1 |
| `ToolUsage.cs` | [ToolUsageTools](../../src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs) | 1 |
| `PlcSoftware.OfflineSuites.cs` | [OfflineSuiteTools](../../src/Engine/ModelContextProtocol/Tools/OfflineSuiteTools.cs) | 16 |

试点工具直接使用 [OfflineToolExecution](../../src/Engine/ModelContextProtocol/Tools/OfflineToolExecution.cs)
与 [XmlBuildResults](../../src/Engine/ModelContextProtocol/Tools/XmlBuildResults.cs)；目录查询和工具构造仍由 MCP 宿主提供。
`GuideTools` 通过构造器注入 `ToolUsageTools`，指南入口不再经过静态转发。
这些试点工具没有 CLI 静态调用点；CLI 的同名报告命令直接使用既有构造器。
HttpTests 的 `engineering-api-only` 检查真实实例归属、单例生命周期和指南依赖注入；
[Test-PilotTools.py](../../scripts/checks/Test-PilotTools.py) 通过 STDIO 覆盖九个领域的直接、桥接及隔离子进程调用。
传入 `--baseline-exe` 可逐项比较迁移前后的 `tools/list` 序列；`ToolCatalog` 按名称排序，SDK 的线上枚举序列
仍以实际宿主输出为准，不在本步调整。

### Portal 领域迁移样板

CFC 是第一个样板：[CfcService](../../src/Engine/Siemens/Services/CfcService.cs)
放在 `Siemens/Services/`，命名空间为 `TiaMcpServer.Siemens.Services`，类名以 `Service` 结尾；
[CfcTools](../../src/Engine/ModelContextProtocol/Tools/CfcTools.cs) 保持在工具目录平铺。
服务通过构造器接收 `IEngineeringSession`，原方法体只把 `RunHmiStepTool`、`AcquireHmiEditAccess`、
`ExactPlcForEngineering` 改为接口调用，静态辅助继续用 `EngineeringSessionHelpers`，纯逻辑保留在 Logic。
本次没有新增内核接口成员，也没有直接写会话字段或 CLI 转发。`EngineRegistration` 在包含会话时按上述
命名空间和类名约定注册非抽象服务，`IEngineeringSession` 解析为已注册的 `Portal` 单例；隔离父进程不注册
会话或领域服务。工具仍由 `ToolCatalog` 发现。服务和工具都为非 `IDisposable` 的单例。

迁移前先单独补吞异常原因并缩减基线，保存注释补丁，以去注释代码和 Release EXE 字节一致性验收。
随后原样移动方法，将服务加入 HttpTests 的 `EngineSurface` 服务名单；`CfcShapeChecks` 和
`EngineeringApiShapeTests` 保留原断言，并验证真实声明类型、共享会话、单例和工具到服务的 IL 调用。
[Compare-NativeCallOrder.py](../../scripts/checks/Compare-NativeCallOrder.py) 接收两份 `NativeCallWeaver verify`
清单（`--baseline before.json --current after.json`），比较全局 Siemens 成员多重集合，并按同名方法自动匹配
从 `Portal` / `McpServer` / `Program` 消失、在服务、工具或具名 CLI 类型中出现的方法族。折入 lambda、局部函数及其迭代器后的
各方法体按 IL offset 排序，保留局部编号和重载签名，忽略迁移造成的全局闭包编号。
未匹配方法报错；有意保留的例外须逐项传入 `--allow-unmatched TYPE::METHOD`，过期例外同样报错。
没有织入点的方法不在清单中，另由源码比较和工具归属检查覆盖。
CLI 方法还逐一比较反射、接口调用、对象分派及枚举输入点的顺序；嵌套 DTO/委托只归一化外层 CLI 类名。
它证明静态调用点顺序；方法体原样迁移的源码检查另保证参数、lambda 所在位置与线程调度不变，不能替代真机轨迹。

[Test-DomainTools.py](../../scripts/checks/Test-DomainTools.py) 用迁移前后各自的 HttpTests（`--baseline-harness` /
`--host-harness`）和 EXE（`--baseline-exe` / `--exe`），以可重复的 `--domain <name>` 选择领域，在
full/lite、直接/桥接及隔离子进程中覆盖该领域的全部工具，并核对源码中的工具名单。
`--domain Cfc` 覆盖两个工具的八种操作；均须到达未连接会话的工程前置检查，返回文本除 `meta.timestamp`（桥接为 `Meta.timestamp`）外
逐字节相同。契约、响应快照、原生清单及全部离线门禁仍按本页验收要求运行。

[Compare-CfcNativeCalls.py](../../scripts/checks/Compare-CfcNativeCalls.py) 和
[Test-CfcTools.py](../../scripts/checks/Test-CfcTools.py) 保留为兼容入口。

### CLI 宿主与命令实现

步骤 16 的 CLI 拆分保留 [Program.cs](../../src/Engine/Program.cs)
中的参数分派、引擎路由、stdio/HTTP/隔离宿主启动及启动诊断。动词表与动词分派仍由
[CliCommands.cs](../../src/Engine/Cli/CliCommands.cs) 提供。
原有四个 `Program` partial 的方法原样归入以下内部静态类，命名空间仍为 `TiaMcpServer`：

| 实现 | 文件 |
|---|---|
| HMI 模板与绑定验证 | [HmiTemplateBuilder.cs](../../src/Engine/Cli/HmiTemplateBuilder.cs) |
| PLC/HMI 同步、XML 与结构化文本生成 | [PlcHmiSyncXml.cs](../../src/Engine/Cli/PlcHmiSyncXml.cs) |
| CLI 探针与工程验证命令 | [CliProbes.cs](../../src/Engine/Cli/CliProbes.cs) |
| 离线分析、预检查与报告构造 | [ReportBuilders.cs](../../src/Engine/Cli/ReportBuilders.cs) |

跨类调用显式限定声明类型，仅共享成员改为 `internal`；结构化文本委托随 XML 生成器迁移。
这些类全部留在受原生调用织入的引擎程序集中。CLI 通过 `EngineServices.Get<T>()` 调用所属工具或服务；
服务生命周期、Openness 调用参数、顺序和线程调度保持不变。

### 共享辅助与会话余项收尾

P3-18 删除迁移期嵌套 `*ToolSupport` 和 HMI 编译转发。目录枚举、工具构造与描述读取仍由 `McpServer`
提供；编译与编译诊断分别由 `PlcCompilation`、`CompilerDiagnostics` 承载；XML 构建响应、PLC 批量导入响应、
离线执行、文件名、名称建议、在线策略、工程编排和 JSON 参数解析分别使用具名内部静态辅助类。
辅助类仍在引擎程序集，工具直接调用其实现。HttpTests 通过显式辅助类型清单定位黄金响应的执行器。

最后的会话余项分布到 `Portal.HmiOperation.cs`（响应钩子）、`Portal.Diagnostics.cs`（门户诊断）、
`Portal.SessionResolvers.cs`（硬件实用程序解析）、`Portal.ObjectIdentity.cs`（对象标识与编辑器）和
`Portal.Transactions.cs`（事务与环境独占访问）。原 Base 校验拆为 `HardwareUtilityRules`、`DeviceServiceObjectRules`、
`ObjectIdentityRules`、`ToolTransactionRules`、`EngineeringCredentialRules`；原 Step7 校验拆为 Logic 中的
`ExternalSourceRules`、`PlcTableRules`、`AlarmTextListRules`、`ProDiagExportRules`。成员不跨程序集迁移。

`Compare-NativeCallOrder.py` 包含这些工具辅助类，并对其反射、接口、对象分派与枚举输入点一并比较有序清单。
删除的辅助层不再参与调用；保留实现中的原生调用、参数与线程归属不变。

### PLC 块、编辑与用户组

[PlcBlocksTools](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) 承载
P3-13a 的 27 个工具：块读取、导入导出、编译诊断、逻辑描述、保护/快照/指纹、离线编辑、验证导入、删除和 PLC 用户组。
[PlcBlocksService](../../src/Engine/Siemens/Services/PlcBlocksService.cs) 承载这些工具独有的
Portal 实现，包括批量块导出、删除、保护/快照/指纹、验证导入及组管理；`_blockGroupDeleteGate` 随服务实例迁移。
两者按约定注册为非 `IDisposable` 单例。`ManageUnifiedHmiGroup` 留在原混合文件。

跨领域成员继续留在内核：`GetBlock`、`GetBlocks`、`GetBlockRootGroup`、`GetBlockPath`、`GetType`、`GetTypes`、
块/类型组路径解析，以及 `ResolveSingleByName`、`GetBlocksRecursive`、`GetPlcSoftware`、`GetSoftwareContainer`。
`ImportBlock` 仍供 XML 构造工具使用，`ImportBlocksFromDirectory` 仍供库播种使用；`ExportBlock` / `ExportBlockToTemp`
仍供离线文档比较的导出路径使用。`LastExportedFile` 与 `ResolveExportFile` 为块/类型导出共享，
`PrepareXmlForImport` / `UnwrapImportError` 为块、类型或变量表导入共享，均留在内核。

会话接口新增只读 `LastExportedFile`、`RegexChars`，以及 `CompileSoftware`、`ExportBlock`、`ExportBlockToTemp`、
`GetBlock`、`GetBlockRootGroup`、`GetBlocks`、`ImportBlock`、`ImportBlocksFromDirectory`、三参数 `ImportTechnologyObject`、
`ImportType`、`GetBlockPath`、`GetType`、`GetTypes`、`GetPlcBlockGroupPath`、`GetPlcTypeGroupPath`、`GetPlcBlockGroupByPath`、
六参数 `GetCrossReferences`、`TryFlattenCrossReferenceResult`、`RecoverableAuditError`、`GetBindingIdentity`、
`ResolvePlcTagTableGroup`、`CrossReferenceRefusal`、`EnumerateReflectedProperty`、`ReadReflectedString`；均显式转发原成员。
变量表清单复用现有带 `out diagnostics` 的 `GetPlcTagTables`，丢弃诊断，保持原单参数重载的行为。

步骤 16 后 CLI 直接解析 `PlcBlocksTools` 调用：`GetBlocks`、`ExportBlock`、`ImportBlocksFromDirectory`、
`ImportPlcProgramFromDirectory`、`CompileAndDiagnosePlc` 和内部 `ExportBlocksToTemp`。
`CompileAndDiagnoseCore` 仍与 HMI 编译共享；`ClassifyPlcXml`、`BuildPlcProgramImportResponse` 仍与 XML 构造工具共享，
通过同文件的 `PlcBlockToolSupport` 适配器复用。路径建议、离线分析和编译响应构造也复用原有共享实现。

`EngineSurface` 加入服务名单，`PlcBlockServicesShapeChecks` 核对 27 个工具的实例归属、共享会话、服务单例和 CLI 实例解析。
`Test-DomainTools.py --domain PlcBlocks` 覆盖全部工具的 full/lite、直接/隔离 STDIO 路径；`ExportBlocks` 的离线成功响应含
实际耗时，故字节比对使用缺参拒绝用例，不扩大时间字段屏蔽范围。原生调用顺序、参数和线程调度保持不变，
同名的工具/服务 `ExportBlocks` 调用序列分别核对；`PatchPlcBlockDocument` 无原生调用点，以源码方法体比较补证。
### PLC 软件、反射、审计与离线构造

P3-13c 将 34 个工具迁入以下实例类；注册和单例生命周期沿用领域约定。

| 工具类 | 工具数 | 服务或共享依赖 |
|---|---:|---|
| `PlcSoftwareTools` | 3 | `PlcSoftwareService` 保存软件树遍历；查询与编译通过 `IEngineeringSession` |
| `ReflectionTools` | 7 | `ReflectionService` 保存描述、属性读取和调用桥；包含 `DescribeObjectProperty` |
| `EngineeringAuditTools` | 2 | `EngineeringAuditService` 保存作用域清单与文档操作 |
| `EngineeringDiagnosticsTools` | 3 | 无会话依赖；原始诊断日志在隔离父进程中也可调用 |
| `SoftwareUnitManagementTools` | 1 | `SoftwareUnitManagementService` 保存单元对象 Access 操作 |
| `ExportTools` | 5 | 无会话依赖；复用响应保护层共享的 `ExportStore` |
| `OfflineAnalysisTools` | 3 | 复用共享比较/清理辅助；块路径模式仍通过内核导出，文件模式无需会话 |
| `XmlBuilderTools` | 9 | 无会话依赖，沿用试点的实例工具类，不增设服务转发层 |
| `PlcBuildTools` | 1 | `IEngineeringSession` 的导入与编译接口；领域私有构造辅助随工具迁移 |

`EngineeringDiagnosticsTools`、`ExportTools`、`XmlBuilderTools` 没有 Portal 依赖；
`OfflineAnalysisTools` 的共享比较辅助也被 PLC 编辑工具调用，保留在 `McpServer`，不增加无状态服务层。
`RunOfflineAnalysisTool` 和 `BuildOfflineXmlBuilderReport` 仍供试点工具共用。

新增内核接口成员为 `PlcLookupPathsSuffix`（只读）、`CompileSoftware`、`ResolvePlcForListing`、
`GetTreePrefix`、`ResolveObject`、`DenyCrossReferenceReflection`、`CoerceReflectionValue`、
`ValidateUnitKind`、`PlcScopes`、`ScopedObjects`、`ImportBlock`、`ImportType`。均显式转发现有实现；
沿用已有 `GetPlcSoftware`、`AvailablePlcPathsSuffix`、`ImportPlcTagTable`、`DescribeMembers`、
`TryGetName`、`FindTypeBySuffix`、`TryGetService` 等接口，没有重复增加别名。

以下共享成员保留在内核：

- `CompileSoftware` / `ResolveCompileService`：块导入、诊断编译和构建后编译复用。
- PLC 解析、`ResolvePlcForListing`、`GetTreePrefix`：会话、块清单和工程树共用。
- `ResolveObject` / `_plcLookupPathsSuffix`：解析中维护共享查询提示；服务不写会话字段。
- `TryGetName`、`DescribeMembers`、`FindTypeBySuffix`、`TryGetService`、`CoerceReflectionValue` 以及
  `Portal.Software.Reflection.cs` 的导入/导出、显式反射和子组解析辅助：HMI、技术对象等领域复用。
- `DenyCrossReferenceReflection`：交叉引用领域同样调用；审计中的 `RecoverableAuditError`、
  `ValidateUnitKind`、`PlcScopes`、`ScopedObjects`、`ReadPlcConsistency`、`ExactCrossReferenceTarget`、
  `VerifyLastDocumentImport` 仍支持交叉引用、下载、删除和文档交换。
- `LastExportedFile`：`McpServer.Blocks.cs`、`McpServer.Types.cs` 读取，块/类型导出写入。
  `ExportStore` 则由所有工具的响应保护层读写，仍为共享基础设施。
- `ExportBlockDocumentForAnalysis`：PLC 文档生成复用；`Portal.CausalTrace.cs` 的两个追踪入口由 Runtime
  工具调用，保持原位。`ImportBlock` / `ImportType` 留在内核，构建工具不依赖块领域的具体类。

CLI 使用的 `DescribeObjectProperty`、`GetObjectProperty`、`ListObjectChildren`、`InvokeObject`（两个重载）
由 CLI 直接解析 `ReflectionTools` 调用。Patch、ScaffoldOperations 直接解析 `PlcBuildTools`；
`ProjectSessionTools` 通过构造器注入所需工具。共享工具辅助 `MakeSafeFileName`、`BuildCompileResponse`、
`ReadIntProperty`、`ClassifyPlcXml`、`BuildPlcProgramImportResponse`、`ResolveCompareSide`、
`DeleteAnalysisTempDir` 原实现保留，迁出工具经 `PlcSoftwareToolSupport` 访问。

迁移前后的原生序列保持一致：工具 → 原 Portal 方法内的调用体，变为实例工具 → 服务内同一调用体 →
`IEngineeringSession` → 原共享内核辅助。没有新增原生调用、参数改写或线程调度。
`DescribeObjectProperty` 在工具与服务中同名，原生顺序检查对这两个方法族分别明确配对，其余由脚本自动匹配。
`InvokeOnInstance` 通过服务实例访问共享内核辅助，HttpTests 的反射调用改由 `EngineSurface.Invoke` 解析目标，
原有断言全部保留。`EngineSurface.InvokeUninitialized` 给迁出的服务注入未初始化的 Portal，保持原有 guard
测试的空内核条件；两者均不执行构造函数，也不获取原生资源。
### HMI 离线替身边界

需要离线替身的领域服务接收不含 Siemens 类型的窄会话接口，不因此为每个服务增加接口。
[IHmiToolSession](../../src/Engine/Siemens/IHmiToolSession.cs) 由 `Portal` 显式转发，
与 `IEngineeringSession` 解析为同一个会话单例；五个服务和工具类仍按 `EngineRegistration` 约定注册，均不实现 `IDisposable`。

| 成员 | 用途 |
|---|---|
| `CurrentProject`（`object?`） | 工程身份、归档和库读取；保持原有空工程检查及反射访问 |
| `RunHmiStepTool` | 复用响应信封、工程前置检查和故障后的阻断 |
| `AcquireHmiEditAccess` | 复用原写访问租约及释放顺序 |
| `ResolveHmiSoftwareOrThrow`（返回 `object`） | 复用 HMI 路径解析及原错误语义 |
| `RecordHmiReadFault` | 记录连接故障并通过原内核实现失效软件缓存 |
| `HmiReadFault`（只读属性） | 图形选择续页读取共享故障标记 |
| `MigrationPages`（只读属性） | 共享分页和释放游标；实例仍由 `Portal` 持有，在原关闭流程中释放 |

`HmiInspection`（8）、`MigrationRead`（8）、`RuntimeSettings`（2）、`GraphicSelection`（2）、`GlobalScriptEdit`（1）
各自对应 `Siemens/Services/<Domain>Service.cs` 和 `ModelContextProtocol/Tools/<Domain>Tools.cs`。
`GraphicSelectionService` 复用 `MigrationReadService.MigrationPage`；两个写操作锁分别随设置和脚本服务迁移。
读取健康重置、空工程消息和缓存失效留在内核，不为服务未直接调用的操作扩展接口。

离线套件直接构造服务并注入 `FakeHmiToolSession`，不再链接这五个 `Portal` 文件及 `Portal.HmiOperation.cs`。
替身模拟会话响应和故障状态，生产信封继续由 HttpTests 的黄金字节检查覆盖。
`ToolBridgeFixtures` 中仅保留非 partial 的注册用 `Portal` 占位类和 `IEngineeringSession`，
用于链接真实 `EngineRegistration` 及维护工具的根目录检查；HMI 行为测试不使用此占位类。
`EngineBundleLayoutTests` 不再声明 `Portal`。既有断言、输入和预期文本保留，offline 最低数量不变。

### 库、VCI 与 SiVArc

| 领域 | 服务 / 工具类 | 工具数 |
|---|---|---|
| 库 | [LibraryService](../../src/Engine/Siemens/Services/LibraryService.cs) / [LibraryTools](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) | 12 |
| VCI | [VersionControlService](../../src/Engine/Siemens/Services/VersionControlService.cs) / [VersionControlTools](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) | 5 |
| SiVArc | [SivarcService](../../src/Engine/Siemens/Services/SivarcService.cs) / [SivarcTools](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) | 9 |

库生命周期、类型和主副本工具从混合工具文件一并迁入；SiVArc 包含混合文件中的 `GenerateSiVArc`。
VCI 的 `_vciOwnerProject`、`_vciCached`、`_vciKeepAlive` 成为服务实例字段，保留原有获取、保活和失效清理顺序。
三个领域按既有约定注册为非 `IDisposable` 单例，没有需要保留的 CLI 静态入口。

跨领域辅助仍留在内核：`LibraryLabel` / `LibraryRef`、`EngineeringLibraryFolder`、`ExactMasterCopyPlcSource`、
`MultilingualJson`、`LibraryPathOf`、`ExactLibraryType` / `ExactTypeVersion` / `ExactMasterCopy`，以及 SiVArc 的
`SivarcFamily` / `SivarcFamilies` / `Family` / `RequireSivarc` / `SivarcShape` 和 `OptionPackageLibraryTypeKind`。
其中 SiVArc 家族表仍供 `ExactSiVArcRoot` 使用；嵌套类型 `SivarcFamily` 改为 internal，以便接口返回原对象。

本步新增的会话接口成员为 `LibraryRef`、`EngineeringLibraryFolder`、`ExactMasterCopyPlcSource`、`LibraryPathOf`、
`OptionPackageLibraryTypeKind`、`ExactEngineeringDevice`、`ExactDeviceItem`、`ResolveHmiScreenOrThrow`、`ExactNameList`、
`GetSivarcFamily`（转发 `Family`）、`RequireSivarc`、`SivarcShape`；均显式转发现有实现。
`DomainShapeChecks` 验证工具归属、共享会话、单例与调用关系，并验证 VCI 保活列表不跨实例共享、断开时清理旧引用。
`Test-DomainTools.py` 的 `Library`、`VersionControl`、`Sivarc` 用例覆盖全部工具，比较 full/lite 与直接/隔离调用的响应字节，
只屏蔽原有时间戳和 D1 允许变化的堆栈帧。原生调用的顺序、参数和线程归属不变，真机验收仍未执行。
### 报警、OPC UA 与工艺对象

| 领域 | 服务 / 工具类 | 工具数 |
|---|---|---|
| 报警 | [AlarmsService](../../src/Engine/Siemens/Services/AlarmsService.cs) / [AlarmsTools](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) | 8 |
| OPC UA | [OpcUaService](../../src/Engine/Siemens/Services/OpcUaService.cs) / [OpcUaTools](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) | 8 |
| 工艺对象 | [TechnologyObjectsService](../../src/Engine/Siemens/Services/TechnologyObjectsService.cs) / [TechnologyObjectsTools](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) | 7 |

报警服务包括 `Step7Leftovers` 中的 XLSX 交换以及 `PlcBlockServices` 中的实例文本导入、文本列表管理；
OPC UA 服务包括 `HardwareServices` 中的访问控制；工艺对象工具包括 `EngineeringManagement` 中的管理入口。
这些工具没有 CLI 静态调用点。通用 PLC 程序批量导入仍使用 `Portal.ImportTechnologyObject`，因此其两个重载
保留在 [Portal.Software.TechnologyObjects.cs](../../src/Engine/Siemens/Portal/Portal.Software.TechnologyObjects.cs)，
工艺对象服务通过会话接口复用带 `overwrite` 和 `importedNames` 的重载。

[Portal.TechnologyMapping.cs](../../src/Engine/Siemens/Portal/Portal.TechnologyMapping.cs)
保留 Motion 和 Startdrive 使用的 `ParameterRow`、`TechnologyObjectRow`、`TypedMotionView`、`InterfaceRow`、
`MappingRow`、`ConnectTyped`、`DisconnectTyped`、`IsConnectedTyped`，以及它们依赖的
`ModuleRef`、`ChannelRef`、`AxisEncoderInterfaceRow`、`TorqueInterfaceRow`、`MeasuringInputRow`、`OutputCamRow`、
`AssociationNames`、`ToMappingRow`、`DbMemberMappingRow`。树读取及其专用 `TechnologyGroupRow` 迁入服务。

会话接口新增九个成员：只读 `Logger`、`AvailablePlcPathsSuffix`、五参数 `ImportTechnologyObject`、
`TryExportEngineeringObject`、`TrySetProperty`、`TryInvokeMethodByName`、`ParameterRow`、`TechnologyObjectRow`、
`TypedMotionView`；显式实现转发原有内核成员，保持原日志类别、反射回退与原生调用顺序。
`TechnologyMappingShapeChecks` 检查三组单例的会话共享、23 个工具的声明类型与服务调用，以及共享成员归属。
`Test-DomainTools.py` 的三个领域覆盖所有工具和操作；保留各入口的断开错误语义，额外仅屏蔽
`GetOpcUaConfig` 的 `data.timestamp` / 桥接 `Data.timestamp` 时钟字段。

### 首批可选包领域

| 领域 | 服务 / 工具类 | 工具数 |
|---|---|---|
| TestSuite | [TestSuiteService](../../src/Engine/Siemens/Services/TestSuiteService.cs) / [TestSuiteTools](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) | 4 |
| V20Options | [V20OptionsService](../../src/Engine/Siemens/Services/V20OptionsService.cs) / [V20OptionsTools](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) | 5 |
| OptionalEngineering | [OptionalEngineeringService](../../src/Engine/Siemens/Services/OptionalEngineeringService.cs) / [OptionalEngineeringTools](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) | 2 |
| SpecializedExchange | [SpecializedExchangeService](../../src/Engine/Siemens/Services/SpecializedExchangeService.cs) / [SpecializedExchangeTools](../../src/Engine/ModelContextProtocol/Tools/SpecializedExchangeTools.cs) | 1 |
| SoftwareUnitDeep | [SoftwareUnitDeepService](../../src/Engine/Siemens/Services/SoftwareUnitDeepService.cs) / [SoftwareUnitDeepTools](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) | 7 |

Motion、HMI、库及 SiVArc 生成工具已归入所属实例工具类；
`ManagePlcSoftwareUnit` 归入 `SoftwareUnitDeepTools`。
这 19 个工具无 CLI 静态调用点；服务只通过 `IEngineeringSession` 读取会话，不写会话字段。

跨领域成员保持在内核，接口显式转发现有方法体：

- `RequireHardwareUtility<T>` 读取当前工程的 `HwUtilities`，保留在 `Portal.SessionResolvers.cs`，
  由 V20Options 和原硬件工具共用。
- `ExactSiVArcRoot` 保留在 `Portal.OptionalEngineering.cs`，继续复用 SiVArc 领域的 `Family` / `RequireSivarc`。
- `RequireUnitProvider`、`ExactUnit`、`OptionalUnit`、`BlockRootOf`、`TypeRootOf`、`ExactObjectUnder`、
  `DocumentMessages`、`DocumentExportRow`、`DocumentImportRow` 保留在 `Portal.SoftwareUnitDeep.cs`，
  供软件单元管理、审计、STEP 7、SiVArc、块及 TestSuite 复用；`LinkedTagRows` 仍由硬件领域调用。
- `ResolveSoftwareContainerUncached`、`ExactMasterCopy`、`ExactLibraryType`、`ExactTypeVersion`、`MultilingualJson`
  原有实现不动，新增接口转发供服务调用。`RelationExists` 和 `UnitRow` 因调用内核而成为服务实例方法。

V20Options 的原生实现仅在 `TIA_V20` 分支编译；V21 仍注册五个工具并保留其不支持错误。
通用领域测试也比较这些拒绝响应，不屏蔽 `meta.error` 中的异常堆栈。类迁移会改变这类堆栈的类型名及
编译器闭包编号，因此常规契约/响应快照零差异不足以证明全部领域响应逐字节一致；此项须单独审查。

### DCC、Teamcenter 与 Startdrive

| 领域 | 服务 / 工具类 | 工具数 |
|---|---|---|
| DCC | [DccService](../../src/Engine/Siemens/Services/DccService.cs) / [DccTools](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) | 8 |
| Teamcenter | [TeamcenterService](../../src/Engine/Siemens/Services/TeamcenterService.cs) / [TeamcenterTools](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) | 3 |
| Startdrive | [StartdriveService](../../src/Engine/Siemens/Services/StartdriveService.cs) / [StartdriveTools](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) | 11 |

这些工具没有 CLI 静态调用点，继续按约定注册为单例。Teamcenter 的 `_teamcenterConnection` 与
`_teamcenterConnectionLabel` 随方法迁入服务，不增加连接、断开或清理动作。

跨领域实现继续保留在内核，新增八个 `IEngineeringSession` 转发：

- `Portal.Startdrive.cs`：`ExactDriveItem`、`ExactDriveContainer`、接收路径的 `ExactDriveObject`，
  以及其私有辅助 `SafeDriveObjectNumber`、接收容器的 `ExactDriveObject`；DCC 和 Startdrive 共用解析。
- `Portal.Dcc.cs`：`DccContainerSummary`、`DcbLibraryRow`；供 Startdrive 摘要及 DCC 库视图共用。
- 原硬件与工艺对象实现：`AddressRow`、`ExactTechnology`、`InterfaceRow`；其方法体原样保留。

`TelegramRow` 和 `DriveObjectRow` 改为服务实例辅助方法，以通过会话接口访问上述共享成员。
`ManageStartdriveParameter` 的解析、单值读取、写入和读回顺序不变；
[`p2051[0]` 崩溃事项](../reference/real-machine-ledger.md)仍待原生复测。
通用领域测试的 `Dcc`、`Teamcenter`、`Startdrive` 用例覆盖全部 22 个工具及 V20 的工具/操作拒绝。
原 `ConnectionRow` 的两个重载分别迁入 DCC 和 Teamcenter；原生清单比对须显式列出这三个同名方法族的
`--allow-unmatched` 例外，并另按重载及其 lambda 核对全部调用点，不能只检查全局成员多重集合。
### Safety 与安全领域

SafetyManagement、SafetyValidation、SecurityDeep、CertificateManagement、ProjectSecurity 各自迁入
`Siemens/Services/<Domain>Service.cs` 与 `ModelContextProtocol/Tools/<Domain>Tools.cs`，合计 20 个工具。
SafetyManagement 的四个工具和 ManagePlcCertificate 已归入各自工具类；
其余工具来自同名 partial。没有 CLI 静态调用点；服务和工具沿用约定注册的非 IDisposable 单例。

`Portal.ProjectSecurity.cs` 保留两个安全服务共用的 RequireUmac、FindOrdinal、ExactUmacRole、UmacRow。
新增的 IEngineeringSession 成员全部显式转发原内核实现：这四项，以及 ExactMasterCopyPlcSource、
GetBlocksRecursive、HardwareOwnerPath、RequireHardwareService、DynamicAttributes、SetDynamicAttributes、
FindOnFresh、CountOnFresh、LibraryRef、EnumerateGroupDevices。原硬件、库与 PLC 调用方保持不变。
调用这些接口的领域辅助方法随之成为实例方法；Safety 登录/注销、UMC 认证事件订阅与凭据设置的顺序保持不变。
ManageMultiuserSession 仍在 CloseAndCommit 后调用 ReleaseProject，由内核清理会话字段。
Portal.cs 中调用 EngineeringCredentialRules.ValidateUmacCredentials 的工程打开路径保留在会话内核，本步不新增其接口转发。

BaseLeftovers 中的 ManageDeviceServiceObjects 同时处理 Web 应用、遥控点与动态证书配置，连同其
CertificateServiceRow / CertificateConfigurationRow 保留在硬件领域；本步不拆分该工具的方法体。
Test-DomainTools 覆盖以上所有工具的 full/lite、直接/隔离调用，并保留 V20 generateBaseId 的版本拒绝；
门户级工具比较未连接门户的错误首行，其他工具比较未绑定工程的原有拒绝响应。
### 硬件设备、AML、模块与地址

P3-11a 将 22 个工具迁入五个单例领域；网络工具及硬件服务留给 P3-11b。
网络工具已并入 `HardwareNetworkTools`；共享网络解析成员仍留在 `Portal.Devices.cs`；
`ImportDeviceAml` 从 HardwareServices 混合文件迁入 AML 领域，`ManageHardwareObject`
从 EngineeringManagement 混合文件迁出。现存 BaseLeftovers / Step7Leftovers 中没有本次设备、AML、模块或地址成员；
`ManageHardwareUtilities` 和 `ManageDeviceServiceObjects` 仍属于硬件服务。

| 领域 | 服务 / 工具类 | 工具数 |
|---|---|---|
| Devices | [DevicesService](../../src/Engine/Siemens/Services/DevicesService.cs) / [DevicesTools](../../src/Engine/ModelContextProtocol/Tools/DevicesTools.cs) | 14 |
| HardwareManagement | [HardwareManagementService](../../src/Engine/Siemens/Services/HardwareManagementService.cs) / [HardwareManagementTools](../../src/Engine/ModelContextProtocol/Tools/HardwareManagementTools.cs) | 1 |
| HardwareAml | [HardwareAmlService](../../src/Engine/Siemens/Services/HardwareAmlService.cs) / [HardwareAmlTools](../../src/Engine/ModelContextProtocol/Tools/HardwareAmlTools.cs) | 3 |
| Modules | [ModulesService](../../src/Engine/Siemens/Services/ModulesService.cs) / [ModulesTools](../../src/Engine/ModelContextProtocol/Tools/ModulesTools.cs) | 2 |
| Addresses | [AddressesService](../../src/Engine/Siemens/Services/AddressesService.cs) / [AddressesTools](../../src/Engine/ModelContextProtocol/Tools/AddressesTools.cs) | 2 |

`ModulesService` 通过构造器接收同一容器的 `DevicesService` 单例以复用目录查询；模块地址读取复用
`AddressesService.ReadAddresses`，订货号格式化复用 DevicesService 的静态辅助。四个领域结果 DTO
（`IoAddressInfo`、`PlugLocationInfo`、`PluggedItemInfo`、`PlugResult`）随所属服务迁移，序列化字段不变。

内核保留设备解析、枚举与其他领域仍使用的成员：`GetProjectTree` 及其树遍历辅助、`GetDevices` /
`GetDevicesRecursive`、`GetDevice` / `GetDeviceByPath`、`GetDeviceItem` / `GetDeviceItemByPath`、
`GetDeviceItemTree` / `BuildDeviceItemTree` / `GetTreePrefixStatic`、`ExactEngineeringDevice` / `ExactDeviceItem`、
`EnumerateAllDevices`、`FindHardwareCatalogEntries`、`ValidateAutomationContext`、`PortalMajorVersion`，
以及共享 HMI 属性和错误辅助、网络读回辅助。`PortalMajorVersion` 只放宽内部可见性，常量值不变。

`IEngineeringSession` 新增 17 项，仅转发现有成员：只读 `Logger`；`GetProjectTree`、`GetDevices`、
`GetDevice`、`GetDeviceItem`、`GetDeviceItemTree`、`GetDeviceByPath`、`FindHardwareCatalogEntries`、
`IsAttributeWritable`、`CoerceAttributeValue`、`BuildDeviceItemNetworkReadbackJson`、`ExactEngineeringDevice`、
`EnumerateAllDevices`、`GetPlcSoftwareNamesForDesktop`、`ValidateAutomationContext`、`FormatExceptionDetail`、
`ToJsonArray`。保留原日志类别，不新增会话写入。

CLI 直接解析 [DevicesTools](../../src/Engine/ModelContextProtocol/Tools/DevicesTools.cs)
调用八个原有入口：`GetProjectTree`、`GetDeviceInfo`、`ValidateAutomationContext`、`AddDevice`、
`AddDeviceWithFallback`、`SearchInstalledGsdDevices`、`SearchHardwareCatalog`、`AddHardwareCatalogDeviceWithProbe`。
工具签名、描述、返回及错误首行不变。`Test-DomainTools.py` 覆盖全部 22 个工具，包含旧式抛异常、
失败 POCO、空设备列表与 AML 离线拒绝；关键词为空的 GSD 用例在扫描本机文件前拒绝。

同名工具与服务的原生调用清单分别按声明类型交给 `Compare-NativeCallOrder.py`，避免名称匹配歧义；
完整清单仍比较全局 Siemens 调用多重集合。每版 39 个服务方法族、12 个工具方法族的有序序列相同，
无原生调用点的方法另做去注释源码比较。其他织入类别仅规范化上述 DTO 的声明类型。
### 硬件网络与服务

`HardwareNetworkTools` / `Siemens/Services/HardwareNetworkService.cs` 承接 IO 系统、同步/MRP 域、传输区、
通道、设备用户组/用户和端口互连共 11 个工具；`HardwareServicesTools` /
`Siemens/Services/HardwareServicesService.cs` 承接通信连接、监控/强制表 Web 访问、系统诊断设置、
硬件特性共 5 个工具，并接收 `*BaseLeftovers.cs` 的 `ManageDeviceServiceObjects`、
`TelecontrolRow`、`CertificateServiceRow` 和 `CertificateConfigurationRow`。17 个工具均无 `Program*.cs` /
`Cli/` 静态调用点，因此没有 `McpServer` 转发；服务和工具依照约定注册为共享同一会话的非 IDisposable 单例。

`ReadDeviceAddressing` 与 `UpdateDeviceAddress` 已并入 `AddressesService` / `AddressesTools`；
`Portal.HardwareNetwork.cs` 仅保留共享辅助，原工具 partial 已删除；
`ImportDeviceAml` 已由硬件设备任务迁入 `HardwareAmlService` / `HardwareAmlTools`。
`ReadOpcUaAccessControl`、`ManageOpcUaAccessControl` 及其专用辅助由 `OpcUaService` / `OpcUaTools` 承接，
不在硬件服务领域重复注册；`GetOpcUaServerInterfaceGroup` 是 `OpcUaService` 的私有辅助，不经内核接口转发。
设备及设备项解析器仍在内核，新服务通过已有 `IEngineeringSession.ExactEngineeringHardware` 访问。

以下共享实现保持在内核，只增加 `IEngineeringSession` 的显式转发，不改变调用参数或线程：

- `Portal.HardwareNetwork.cs`：`RequireDeviceItem`、`RequireHardwareService<T>`、`HardwareOwnerPath`、
  `DynamicAttributes`、`SetDynamicAttributes`、`ApplyScalarsAndAttributes`、`FindOnFresh`、`CountOnFresh`、
  `AddressRow`、`ExactChannel`；它们仍被地址、安全、Startdrive、Motion/ProDiag 或工艺映射调用。
- `Portal.HardwareServices.cs`：`ServiceProvider`，仍被 PLC 保护和地址工具调用。
- `Portal.SoftwareUnitDeep.cs`：`LinkedTagRows`。

仅服务内部需要上述接口的行构造器改为实例方法，原方法体按接口访问替换后保持一致。
`Test-DomainTools.py` 的 `HardwareNetwork` / `HardwareServices` 用例覆盖全部迁移工具、full/lite、
直接及隔离子进程路径，并比较 V20 的通信连接和设备服务对象版本拒绝。
HttpTests 保留全部既有断言，另检查两个领域的工具归属、共享会话、单例、生命周期和工具到服务的 IL 调用。
### 在线、下载与设备传输

`OnlineDownloadService` 与 `OnlineDownloadTools` 合并承载在线、下载及设备传输的 12 个工具，
包括迁入的 `ReadTransferRoutes`。
原 `Portal.Online.cs`、`Portal.DeviceTransfer.cs` 与 `McpServer.DeviceTransfer.cs` 已删除；
下载提示处理位于 [OnlineDownloadService](../../src/Engine/Siemens/Services/OnlineDownloadService.cs)，
工具位于 [OnlineDownloadTools](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs)。
原 `McpServer.PlcSoftware.Online.cs` 的五个硬件工具已迁入 Addresses、Devices、HardwareNetwork、HardwareServices；
跨领域的 `IsOnlineModeError`、
`WithAutoOffline` 保持原入口，后者通过容器取得同一服务，保留下线后仅重试一次的行为。
这 12 个工具没有 `Program*.cs` / `Cli/` 静态调用点，无需 CLI 转发。

在线连接、路由选择、凭据及提示策略均为调用局部对象；没有由断开连接或工程关闭清理的在线字段或锁。
认证事件与 `HandlerScope` 随领域迁入服务，仍在原调用的 `using` 范围内解绑和释放凭据。
服务与工具按现有约定注册为非 `IDisposable` 单例，共享 `IEngineeringSession`。

新增的内核接口成员只转发现有实现，不写会话状态：

- `Logger` 保留原 `ILogger<Portal>` 实例及日志类别。
- `AvailablePlcPathsSuffix`、`GetAllPlcSoftware` 继续共用 PLC 解析诊断和项目枚举。
- `ReadPlcConsistency`、`RecoverableAuditError` 继续共用 PLC 程序审计的一致性读取和错误筛选。
- `ReadReflectedString`、`EnumerateReflectedProperty` 留在 `Portal.Download.cs`，供 PLC 块服务与传输路由共用；
  使用它们的七个领域路由辅助方法改为实例方法，通过接口访问内核。

迁移保持原生调用顺序、参数、条件分支与 lambda 位置。`GoOnline` 仍依次解析 PLC/服务、绑定认证事件、
选择并应用路由、调用原版本对应的在线重载；`DownloadToPlc` 仍依次解析服务/配置、绑定事件和提示委托、
选择路由、进入创建地址/RH/普通下载分支、构造结果；上载仍在原写访问范围内绑定事件并调用原生方法。
`Compare-NativeCallOrder.py` 分别对 Siemens 领域和工具领域清单进行比较，以区分两层同名的
`GoOfflineAll`；两份清单覆盖完整调用点，V20/V21 各 38 个迁移方法族（含 lambda）顺序一致，
全局 Siemens 直接调用成员多重集合也一致。该静态证明不替代真机验收。

## G9：单 PLC 工程的模糊匹配

维护者于 2026-10-03 批准的 G9 行为调整由 `IEngineeringSession.ResolvePlc(path, PlcAccess)` 实施。
`GetPlcSoftware`、兼容入口 `ResolvePlcSoftwareFuzzy`、`ExactPlcForEngineering` 和列表解析都进入此内核；
导入、删除、下载和在线状态变更入口记录 `Write`，普通读取记录 `Read`。访问意图只用于诊断，匹配规则相同；
`ExactPlcForEngineering` 原有的写入前确认 Offline 检查仍在解析之后执行。通用反射入口的 Software
解析也进入内核，其 HMI 回退不能返回已经被内核拒绝的 PLC；`allowWrite` 决定反射调用的访问意图。

**精确**表示整个输入和候选名去除首尾空白后，使用 `OrdinalIgnoreCase` 比较；不解释正则、子串或相似字符。
**别名**表示 PLC 自身的软件名、拥有软件的 CPU `DeviceItem` 名、包含该 CPU 的硬件祖先名，以及恰好拥有一个
PLC 的设备/站点名。设备组只用于限定路径，组名本身不代表 PLC。多个结构候选即为歧义，包括软件名与另一 PLC
的站点别名同名；不会优先返回第一次遇到的候选。容器以对象身份去重，不能按软件名称合并两个 PLC。

内核使用独立的严格 PLC 缓存（Trim + OrdinalIgnoreCase）；命中后确认 Software 仍为 PLC 即返回，未命中才在
现有设备/设备组树上完整、有界地扫描，不读取块或类型组。只有完整、唯一且成功的解析才进入此缓存；它与通用
软件容器缓存在工程实例变化、HMI 缓存失效及断开连接时同步清理。内核不从通用缓存选择 PLC，但仍将已验证的
容器写回通用缓存，确保随后的 `ResolvePlcService` 对规范化名称/结构别名使用同一 CPU。未完成扫描仍报告
`OpennessError`，不能将部分结果当作唯一结果。`Portal.Software.cs` 的旧枚举 catch 保持不变；它不再承担
内核的匹配职责。类型化备用枚举使用已有的严格枚举器，`Guard.MatchPlcName` 只保留精确匹配和空串选唯一 PLC。
没有工程时沿用各入口现有错误；空串有零个或多个 PLC 时返回未解析，由原有入口产生其失败响应。

以下“之前”区分通用 `GetPlcSoftware`（含 Guard 回退）与旧精确容器入口。示例树为
`Line / ET 200SP station_1 / CPU_1`，软件名为 `PLC_1`；重复站点位于另一组。

| 输入形式与例子 | 之前 | G9 之后（读写相同） |
|---|---|---|
| 软件全名 `PLC_1`、IEC 名 `+S1-K1`、中文名 `5T车` | 大小写不敏感的字面匹配 | 保留，必须唯一 |
| 大小写/首尾空白 ` plc_1 ` | 通用入口经 Guard 成功；精确容器不去空白 | 统一为精确匹配 |
| CPU 自身名称 `CPU_1` | 结构别名 | 保留，必须唯一 |
| 硬件祖先名称 `Rack_1`（其下仅一个 PLC） | 裸名遍历继承别名 | 保留，必须唯一 |
| 站点名 `ET 200SP station_1`（实际拥有该 PLC） | 实际结构存在时可经容器解析；仅传软件名给 Guard 的旧测试靠单 PLC 猜测 | 只接受真实结构别名；Guard 单独收到站点名返回 null |
| 分组限定 CPU `Line/CPU_1`，可嵌套 `Area/Line/CPU_1` | 组/硬件路径；组名大小写敏感 | 保留，整体大小写不敏感 |
| 站点/CPU `ET 200SP station_1/CPU_1` | 顶层设备可成功，CPU 可在机架下 | 保留结构关系，不能忽略错误尾部 |
| 分组限定站点或站点/CPU `Line/ET 200SP station_1[/CPU_1]` | 原路径遍历支持；多个软件时可能取第一个 | 只接受唯一结构候选 |
| 分组/软件名 `Line/PLC_1`（软件名不同于 CPU） | 原硬件路径不保证成功；通用入口可能通过单 PLC/子串回退 | 作为完整结构限定别名接受 |
| 完整硬件/软件链 `Line/ET 200SP station_1/CPU_1/PLC_1` | 原路径遍历可能忽略多余尾部 | 精确对应完整结构链时接受 |
| 名称内含 `/`，如 `S7-1500/ET200MP station_1` | 容器拆分斜线；通用入口可能只靠单 PLC 回退 | 整个真实名称也是结构别名，歧义仍拒绝 |
| 空串、空白或内部 null | Guard 选唯一 PLC；旧空串硬件路径另有取首个容器的旁路 | 只选唯一 PLC，零个/多个保留失败返回 |
| 单 PLC 下错误名 `WrongPLC` | 通用入口选唯一 PLC | 未找到，附可用 PLC 路径 |
| 短子串 `PLC`、长包含串 `prefix_PLC_1_suffix` | 通用入口唯一子串双向匹配 | 未找到，附可用 PLC 路径 |
| 错误限定路径 `Wrong/PLC_1` 或 `Line/CPU_1/garbage` | 可能被单 PLC、子串或忽略尾部的旁路接受 | 内核拒绝 |
| 重复名称/别名 `Station` 对应两个 PLC | 裸容器路径报告歧义；旧枚举会按软件名去重 | `InvalidParams`，列出两个完整候选路径 |

当前完整引擎的 `GetSoftwareContainer` 没有 URI 百分号或反斜线转义解码规则；这些字符一直按字面解释。
G9 不引入 Foundation 的另一套路径语法，已有字面字符名称仍有效。LegacyHost/Foundation 另使用规范转义路径：
读取允许唯一别名，写入核对 `ExactPath`，没有 Guard 的单 PLC/子串规则，本次不改动它。

错误继续使用现有响应类型和错误码。新的歧义文本为
`Ambiguous PLC software name '<input>': <candidate paths>. Use a group-qualified device/software path.`；
未找到保留调用入口的前缀和错误类型，追加 ` Available PLC paths: <paths>`，例如
`Exact PLC software not found: WrongPLC Available PLC paths: Line/ET 200SP station_1/CPU_1/PLC_1`。
无候选时不追加空路径列表。工具 schema 与描述未改变。P0-06 的调用前拒绝快照不经过此解析器；
它和模拟硬件树测试均不能代替真实 TIA 验收，真实 TIA 验收仍待维护者执行。

### PLC 类型、文档与外部源

步骤 13 的类型、文档交换、外部源及生成工具按以下归属注册为单例：

| 工具类 | 领域服务 | 工具数 |
|---|---|---|
| `TypesTools` | `TypesService` | 6 |
| `DocumentsTools` | `DocumentsService` | 4 |
| `PlcExternalSourcesTools` | `PlcExternalSourcesService` | 8 |
| `PlcDocumentationTools` | 复用离线逻辑与共享会话导出接口 | 3 |
| `NativeExchangeTools` | `NativeExchangeService` | 8 |

文档交换成员从 `Portal.Blocks.cs` 迁入 `Siemens/Services/DocumentsService.cs`，包括单次及批量导入、
导出的诊断状态和 `VerifyLastDocumentImport`；`McpServer.Documents.cs` 保留反射工具。
`Portal.Step7Leftovers.cs` / `McpServer.Step7Leftovers.cs` 中剩余的外部源、系统组成员迁入
`PlcExternalSourcesService` / `PlcExternalSourcesTools`，这两个 Leftovers 文件删除。
`Portal.PlcTagEditing.cs` 的标签定义操作与 `Portal.PlcNativeFiles.cs` 的生成操作合入 `NativeExchangeService`。
服务不实现 `IDisposable`，通过 `IEngineeringSession` 访问同一个内核会话。

共享成员仍留在内核：类型查找、清单、导入供删除、反射及程序批量导入使用；`ExportType` 和共享
`LastExportedFile` 仍由内核维护，避免服务写共享导出状态；类型临时导出、批量导出及引用目录播种迁入类型服务。
`GetCrossReferences` 的全部重载、拒绝策略及辅助函数仍供删除路径复用；`ExactNameList` 仍供多个服务使用。
PLC 组解析、组路径、块清单、块和 HMI 批量导入，以及分析用块文档导出均保持内核归属。
`Portal.ProjectExchange.cs` 的语言、归档取回和项目文本操作保留在内核，归档取回继续使用 `AdoptProject`；
本步没有新增会话字段写入，也不改变 `ReleaseProject`。

会话接口新增 18 个转发成员：`GetType`、`GetTypes`、`ExportType`、`ImportType`、只读 `LastExportedFile`、
`GetBlocks`、`GetPlcBlockGroupByPath`、`GetPlcBlockGroupPath`、`GetPlcTypeGroupPath`、`ImportBlocksFromDirectory`、
`ImportHmiScreensFromDirectory`、`ImportHmiTagTablesFromDirectory`、带查询状态及单元参数的 `GetCrossReferences`、
`ExportBlockDocumentForAnalysis`、`ManageProjectLanguage`、`RetrieveProjectArchive`、`ExportProjectTexts`、`ImportProjectTexts`。
CLI 直接解析 `DocumentsTools` 调用 `ExportAsDocuments` / `ImportFromDocuments`，
解析 `PlcExternalSourcesTools` 调用 `ImportPlcExternalSource` / `GenerateBlocksFromExternalSource`。

`Test-DomainTools.py` 的五个对应领域覆盖所有 29 个工具的 full/lite、直接/隔离分派。
`ExportTypes` 和 `ExportBlocksAsDocuments` 在断开时也返回可变耗时，故使用重复参数拒绝用例；文档导入使用
无效选项拒绝用例。其方法体及原生调用顺序另由源码和织入清单核对，不增加耗时屏蔽规则。
`ExportAsDocuments` 的旧式纯文本错误含堆栈，按 D1 仅屏蔽堆栈帧，保留首行、其他明细及 preflight 后缀。

### 硬件、Motion 与库的剩余工具

P3-11c 将 25 个剩余工具并入六个现有服务/工具类，不新增领域；当前工具数为 HardwareNetwork 19、
Addresses 5、Devices 15、HardwareServices 11、MotionProDiagClassicHmi 12、Library 18。
网络计划与四个库/模板离线分析器的原工具方法体留在相应实例工具类，继续直接调用既有纯逻辑分析器。
`Portal.PlcProtection.cs` 的全部成员迁入 `HardwareServicesService`，原文件删除；
`Portal.MotionExchange.cs` 保留共享的 `ExactTechnology`，两个操作迁入 Motion 服务。
`Portal.Software.LibrarySeed.cs` 保留 `SeedProjectFromReference` 及共享的 `MakeSafeFileName`、
`TryListNamesFromCollection`、`TryFindByNameInCollection`，供 PLC 程序领域继续迁移；全局库探测、导入及其私有辅助迁入 Library 服务。

本步新增以下 16 个 `IEngineeringSession` 成员，均显式转发现有实现；新增前已检查现有接口及 master：

- `GetDeviceItemNetworkInfo`、`ProbeConnectDeviceNodesToSubnet`：分别仍由网络读回、CLI 探测使用。
- `BuildDeviceNodesJson`、`NormalizeAttrName`、`TraverseDeviceItems`：由多个硬件领域共享。
- `FindNetworkNodes`、`IsIndustrialEthernetNode`、`FormatNodeInfo`、`BuildHardwareHmiConnectionCandidates`、
  `BuildDirectHardwareHmiConnectionCandidates`：仍供内核 CLI/HMI 探测使用；`NetworkNodeInfo` 仅改为 internal。
- `TryReadInterestingAttributes`：供硬件暴露探测和库主副本导入使用。
- `GetPutGetAccess`、`FindPutGetAttribute`、`AttrValueIsEnabled`：读取入口仍供运行时 S7 读取前置检查使用，设置入口迁入服务。
- `GetBlock`、`FindExistingByName`：复用原 PLC 块解析和 Unified HMI 名称查找，不替换为语义不同的解析器。

CLI 直接解析 `HardwareNetworkTools` 调用 `ProbeHardwareHmiConnectionOwnerCandidates` /
`ProbeHardwareHmiConnectionWhitelistedServices`，解析 `LibraryTools` 调用
`ProbeGlobalLibrary` / `ImportMasterCopyFromGlobalLibrary`；对应静态转发已删除。
六个服务均已在 `EngineSurface` 名单和约定注册中，未重复添加；HttpTests 的领域名单覆盖新增工具、会话共享和 CLI 实例归属。

原生调用顺序、参数与线程归属不变；方法体通过去注释 token 比较，工具属性、参数与默认值原样保留。
同名工具和服务按声明类型分别输入原生调用顺序检查器，完整织入清单另比较全部类别的成员多重集合。
`Test-DomainTools.py` 覆盖六个领域的全部工具，包括断开前置检查、离线分析以及 full/lite、直接/隔离路径；
只屏蔽已列明的时间戳与 D1 允许变化的错误堆栈。真机验收未执行。

### Unified HMI core

P3-14b 将 51 个工具迁入 `UnifiedHmi`（22）、`UnifiedObjectServices`（12）、`UnifiedUiModel`（7）、
`UnifiedScreenItems`（2）、`UnifiedEngineering`（3）、`UnifiedExchange`（3）、`UnifiedEvents`（1）和
`UnifiedHmiGroups`（1）。每个领域的 `<Domain>Service.cs` 位于 `Siemens/Services/`，对应的
`<Domain>Tools.cs` 位于 `ModelContextProtocol/Tools/`，按 `EngineRegistration` 约定注册为非可释放单例。
`UnifiedObjectServicesService` 包括原 `Portal.PlantViews.cs`、`Portal.UnifiedLogging.cs` 中的领域方法。
混合文件中的 `ManageUnifiedEvent`、`ManageUnifiedHmiGroup` 一并迁移；其他领域成员保持原位。

共享辅助留在 `Portal.Software.UnifiedHmiHelpers.cs`：`TrySetProperty`、`ResolveHmiScreenOrThrow`、
`TryGetHmiTagRoot`、`TryGetHmiTagTablesCollection`、`TryFindHmiTagTable`、`EnumerateHmiTagTablesRecursive`、
`FindExistingByName`、`TryGetEngineeringAttribute`、`SummarizeHmiObjectReadback`、`IsAttributeWritable`、
`CoerceAttributeValue`、`CoerceReflectionValue`、`CoerceColor`、`ToJsonArray`、`FormatExceptionDetail`。
`FillUnifiedHmiPartnerNetworkInfo` 和其结果类型 `UnifiedHmiPlcPartnerInfo` 也留在内核，避免把共享硬件遍历的
私有 `NetworkNodeInfo` 类型扩散到服务。后者只将可见性改为 internal，字段与读写顺序不变。
`ExactUnifiedRoot`、`UnifiedCollection` 由多个 Unified 服务共用，分别保留在
`Portal.UnifiedObjectServices.cs`、`Portal.UnifiedEngineering.cs`。
`Portal.UnifiedScreenItems.cs` 保留 `UnifiedAssembly` 静态字段：原有内核初始化时机影响
`ListUnifiedHmiApiTypes` 对已加载程序集的枚举，不能推迟到屏幕服务第一次调用时。

`IEngineeringSession` 新增 `FindExistingByName`、`TryGetEngineeringAttribute`、`SummarizeHmiObjectReadback`、
`CoerceReflectionValue`、`ExactUnifiedRoot`、`UnifiedCollection`、`FillUnifiedHmiPartnerNetworkInfo`、
`TryGetHmiTagRoot`、`TryGetHmiTagTablesCollection`、`TryFindHmiTagTable` 和只读 `UnifiedAssembly`；
显式实现仅转发现有成员。其余会话解析、属性写入与错误格式化均复用原接口。

CLI 直接解析 `UnifiedHmiTools` 调用以下 13 个入口：
`EnsureUnifiedHmiScreen`、`EnsureUnifiedHmiTagTable`、`EnsureUnifiedHmiTag`、`EnsureUnifiedHmiConnection`、
`EnsureUnifiedHmiScreenItem`、`ApplyUnifiedHmiScreenDesignJson`、`BindUnifiedHmiButtonPressedTag`、
`EnsureUnifiedHmiButtonEventHandler`、`DescribeUnifiedHmiButtonEventScript`、`SetUnifiedHmiButtonEventScriptCode`、
`BuildUnifiedHmiButtonActionScript`、`EnsureUnifiedHmiButtonAction`、`BindUnifiedHmiTagDynamization`。
`UnifiedHmiDomainShapeChecks` 验证完整工具归属、单例、共享会话、调用关系及静态转发的移除；已有 HTTP 断言保留。
`Test-DomainTools.py` 覆盖全部 51 个工具的 full/lite、直接/隔离调用，只屏蔽明确的时间戳及 D1 堆栈帧。
原生调用顺序、参数与线程归属保持不变；上述离线证明不替代真机验收。
### 运行时通道与实例工具（P3-16a）

20 个工具分别迁入 [RuntimeTools](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs)（7）、
[RuntimeChannelTools](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs)（8）和
[PlcSimAdvancedTools](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs)（5）。
工具仍在引擎内，按约定注册为非 IDisposable 单例，方法签名、描述、响应及拒绝顺序保持原样。
这 20 个工具没有 CLI 静态调用点，因此没有保留 McpServer 转发。

只有 RuntimeTools 注入 IEngineeringSession：ReadPlcLiveValuesS7 的可选 PUT/GET 预检继续调用
GetPutGetAccess；TraceTagCause、TraceTagCauseLive 继续调用内核的同名方法。接口新增这三个原实现的显式转发；
工程读取、块导出、S7 读取的顺序、参数和线程归属不变。其他两个工具类没有会话依赖。
RuntimeMeta 由 RuntimeChannelTools 保留为内部静态辅助，供 PLCSIM 执行器复用。

[TiaMcp.Runtime](../../src/Runtime/TiaMcp.Runtime.csproj) 为不织入的 net48 协议程序集，
保持 TiaMcpServer.Runtime 命名空间，引用原版本 Sharp7、Workstation.UaClient、官方 HTTP Web API 客户端及 Logic。
官方 Siemens.Simatic.S7.Webserver.API 是协议客户端；此程序集不引用 Siemens.Engineering、PLCSIM 或引擎。
独立还原时显式保持引擎已有的 DependencyInjection 版本，不引入新的依赖版本。

迁移前 V20/V21 的 NativeCallWeaver verify 清单在这六个文件上相同：

| 文件 | 原织入类别与数量 | 归属与理由 |
|---|---|---|
| S7LiveReader.cs | interface 10、object-dispatch 1 | 迁入 Runtime；只处理 Sharp7 和协议数据 |
| OpcUaLiveReader.cs | enumeration-input 4、interface 1、object-dispatch 2 | 迁入 Runtime；只处理 OPC UA 客户端和节点值 |
| S7WebApiChannel.cs | enumeration-input 3、interface 6、object-dispatch 4 | 迁入 Runtime；只处理 HTTP 客户端与 JSON 数据 |
| UnifiedOpenPipeChannel.cs | interface 2 | 迁入 Runtime；只处理本地命名管道与 JSON |
| PlcSimAdvancedChannel.cs | enumeration-input 29、object-dispatch 17、reflection 9、interface 15 | 保留引擎；反射、Activator 和接口调度可到达 Siemens PLCSIM Runtime |
| EnvironmentDoctor.cs | enumeration-input 6、interface 4 | 保留引擎；依赖 Engineering 探测、EngineRouter，执行程序集位置也决定其检查目录 |

引擎移除的 33 个点正好属于四个迁出的文件，严格 Siemens 直接成员多重集合不变。
新的外部程序集边界产生 5 个 enumeration-input 点：RuntimeTools 的 ReadItems、SampleItems、ReadNodes，
以及 Portal.CausalTrace 和 Portal.PlcWatchTables 中原有的两处 ReadItems。未禁用这些边界，也未改变调用参数。
PLCSIM 与 doctor 的全部 80 个原有点保留。逐站点清单与前后比较保存于任务的本地构建证据目录。

V20/V21 项目引用 Runtime；LegacyHost 不链接这些通道，无需新增引用。
Build-Release 复制整个协议依赖闭包并检查 Runtime DLL；Build-MultiVersion 同时检查既有完整引擎输出。
构建脚本现有的目录枚举自动把新源码及 DLL 纳入生成的 sourceFiles/runtimeFiles 清单，发布哈希仍只由脚本生成。
Package-Release、Validate-Bundle、Check-Repository 的必需文件清单同步要求四个通道源码、项目及两版 DLL。

Test-DomainTools 的三个运行时领域覆盖全部 20 个工具、full/lite 与普通/隔离路径。
空地址、空节点、空 host、空变量及无效请求在打开通道前拒绝；CPU 探测/状态、离线追踪和 PLCSIM 使用既有重复参数准入拒绝，
避免打开协议连接、探测本机模拟实例或扩大耗时屏蔽规则。HttpTests 另外核对实例单例、会话依赖与程序集归属。
### 会话与工程工具

[SessionTools](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)
承载 9 个会话工具；
[ProjectSessionTools](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs)
承载 11 个工程工具；
[DiagnosticsTools](../../src/Engine/ModelContextProtocol/Tools/DiagnosticsTools.cs)
承载 `RunCapabilitySelfTest`、`RunOnlineMonitoringSafetySelfTest`、`GenerateAcceptanceReport`、`GenerateErrorReport`。
三者直接接收 `IEngineeringSession`，由 `EngineRegistration` 按工具类型约定注册为
非 `IDisposable` 单例，不增加领域服务，也不接收具体 `Portal`。`Portal` 继续管理连接、工程句柄和事务状态，
工具不写内核字段；`IsolatedWorkerHost` 继续把这些工具代理到子进程，未增加本地控制入口。

`IEngineeringSession` 新增 29 个成员，`Portal.EngineeringSession.cs` 全部显式转发现有实现：

- 连接与状态：`ConnectPortal()`、`ConnectPortal(string?, bool, JsonObject?)`、`ConnectIsolatedPortal`、`ListPortalProcessProjects`、`DisconnectPortal`、
  `IsConnected`、`GetState`、`GetHmiReadHealth`、`GetPortalProcessHealth`、`ConnectToProject`、`LastConnectError`。
- 工程：`GetProjects`、`GetSessions`、`ForeignOpenProjectName`、`ProjectIsValid`、`IsLocalSession`、`OpenProject`、
  `OpenSession`、`AttachToOpenProject`、`CreateProject`、`SaveSession`、`SaveProject`、`SaveAsProject`、`CloseSession`、`CloseProject`。
- 诊断与事务：`ReadPortalInfo`、`ReadObjectIdentifier`、`ShowObjectInEditor`、`BeginTransaction`；后者返回已有
  `IEngineeringTransaction` 契约，具体事务及 ambient exclusive access 仍在内核。

以下入口已由 CLI 或工具编排直接调用其所属工具实例：`Connect`、`ConnectIsolated`、
`ListPortalProcessProjects`、`EnsureOpennessUserGroup`、`Disconnect`、`GetState`、`Bootstrap`、`ConnectToProject`、
`ReadPortalInfo`、`GetProjects`（MCP 名称仍为 `GetProject`）、`OpenProject`、`AttachToOpenProject`、`CreateProject`、
`ScaffoldProject`、`SaveProject`、`SaveAsProject`、`CloseProject`、`ReadObjectIdentifier`、`ShowObjectInEditor`、`RunToolsInTransaction`。
`McpServer.ScaffoldOperations.cs` 的三个辅助方法同时供 `ApplyProjectPatch` 使用，保留在原处；
`SessionToolSupport` 仅转发它们及工具名枚举，`PilotToolSupport` 继续提供共享工具目录访问。

`EngineSurface` 已自动发现实例工具；本步没有新服务需要加入它的服务名单。
`SessionToolChecks` 验证实例归属、共享内核、静态转发的移除、隔离代理分类、ToolBridge 和 batch 解析。
`Test-DomainTools.py --domain Session --domain ProjectSession` 覆盖全部 20 个工具的 full/lite、普通/隔离路径。
`Connect`、`ConnectIsolated`、进程枚举及用户组修复只验证工具体执行前的重复参数拒绝；其余使用断开状态、
无效输入或离线预览，不连接 TIA。`ReadToolBatch` 无 dry-run 参数，验证其离线读取；`ApplyToolBatch` 仅接受预览令牌，
验证无效令牌拒绝，并通过 `ValidateBatch` 验证迁移工具的写预览解析及强制 `dryRun=true`。未执行真实写批次。

CLI 自检、报告命令和离线验证套件直接解析 `DiagnosticsTools` 调用上述四个诊断工具。
诊断迁移仅额外增加无参 `ConnectPortal()` 接口转发；`GetState`、进程列表、`ValidateAutomationContext` 和
`GetProjectTree` 复用已有接口。安全自检中的 `typeof(Portal)` 只用于检查原有类型和反射防护方法，原样保留，
不读取或写入会话实例；不因其他领域先前的迁移改变自检结果。共享安全策略仍供 PLC 监视工具使用，留在
`McpServer`，经 `SessionToolSupport` 转发；七个诊断私有辅助方法随工具迁移。

`Test-DomainTools.py --domain Diagnostics` 对自检与验收报告固定 `connectIfNeeded=false`、
`inspectPortalProcesses=false`，报告写入工作树下临时目录并比较返回文本和 Markdown/JSON 文件字节。
只屏蔽明确的时钟值：响应 `Meta/meta.timestamp`、验收报告中 `SelfTest/selfTest` 和
`SafetySelfTest/safetySelfTest` 的时间戳、`DateTime.Now` 生成的 `OperationId/operationId`、
由该值组成的报告文件名末尾、Markdown 前部 `GeneratedAt` 行及错误报告 JSON 的 `GeneratedAt`。
目录、文件名前缀与扩展名、自检内容、错误正文中的日期或数字均不屏蔽；自检覆盖这些屏蔽边界。

### 步骤 15 的静态工具边界

会话、工程和诊断入口迁移后，`McpServer.cs` 不再声明 MCP 工具，仅保留共享辅助，不持有会话状态或 CLI 工具转发。
其他领域迁移合并后的静态基础设施工具为以下 12 个：

| 归属 | 工具 |
|---|---|
| ToolBridge | `ListToolCategories`、`FindTools`、`CallTool`、`PreflightToolCall`、`GetRecipe` |
| Batch | `ReadToolBatch`、`PreviewToolBatch`、`ApplyToolBatch` |
| Worker | `ReadOpennessWorkerStatus`、`RestartOpennessWorker` |
| Doctor | `Doctor` |
| Maintenance | `CheckForUpdate` |

这个边界是领域迁移完成后的目标，不表示单独应用本步就删除尚未合并的其他领域实现。
并行迁移中的硬件和 Unified HMI 工具仍由各自任务负责；`McpServer.Runtime.cs`、
`McpServer.RuntimeChannels.cs` 和 `McpServer.PlcSimAdvanced.cs` 的运行时工具按第 16 步迁出。
不得为了提前达到静态工具数量目标而在会话任务中删除这些入口或改动其行为。

### 步骤 16 完成：移除静态会话入口与 CLI 转发

`McpServer` 对外仅保留上述基础设施工具及其共享执行、响应和编排辅助；没有会话或容器字段。
`EngineServices` 是宿主容器和 CLI 延迟容器的唯一入口；两者都使用 `EngineRegistration.AddEngine`。
`GetIfInitialized` 在 standalone 尚未初始化时返回 null，宿主未注册会话时也返回 null，避免隔离父进程借用 CLI 会话。
内核的原生 `TiaPortal` 句柄仍由 `Portal` 实例独占；`McpHints` 的历史错误文本匹配保持不变。

`TypesService` 通过构造器注入 `HmiExchangeService`，删除两个 HMI 目录导入的内核转发及接口成员。
`Siemens/Portal` 和 `Siemens/Services` 内不再调用 `EngineServices.Get`，依赖图无反向会话依赖。
标签表先于画面导入，原生调用参数、顺序和线程保持原样。

HttpTests 保留原有断言数量，原静态转发存在性断言改为无转发、实例解析和单例归属检查；指南结果仍比较完整 Meta。
另验证容器与内核的断开状态一致、无会话宿主不回退、McpServer 无会话状态，以及 Types/HmiExchange 的注入关系。
静态转发移除后为空的九个 partial 文件已删除；其他 partial 仍承载共享实现或支持适配器。
