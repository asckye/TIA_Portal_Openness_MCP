# 完整引擎拆分设计（重构阶段 3）

[重构计划](refactor-plan.md) · [版本框架](unified-version-framework.md) · [验证分层](validation.md)

本页是 P3-01 的设计结论。路径相对 `tools/tiaportal-mcp/src/TiaMcpServer`；`P.` 表示 `Siemens/Portal/Portal.`，
`T.` 表示 `ModelContextProtocol/Tools/McpServer.`。数字为 2026-10-03 的源码统计。

## 现状

- `McpServer`：静态 partial 类，93 个文件声明 488 个工具；`T.Profile` 的 `GetAllTools`/`GetLiteTools`、
  `T.ToolBridge` 和 `McpServer.cs` 以 `typeof(McpServer).GetMethods(Public|Static)` 扫描注册；
  `T.ArgDiagnostics` 的 `WrapTools` 叠加版本准入、参数诊断、串行化和响应保护，隔离模式用 `ProxyTool`。
- `Portal`：87 个 partial、约 3.1 万行的有状态单例；工具约 440 处调用。工具文件与 Portal partial 基本一一对应。
- `Program`：5 个 partial，约 8.8 千行 CLI、报告和 HMI 模板逻辑；CLI 静态调用 88 个工具方法。
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
  共用 `EngineRegistration.AddEngine`。CLI 调用的 88 个工具方法在 `McpServer` 上保留无属性的静态转发，
  直到 `Program` 拆出。
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
15. **J** 会话工具迁入 `SessionTools`，`McpServer` 只剩基础设施与转发。
16. **J** `Program` 拆为 CLI 宿主，`Runtime/` 拆为独立程序集，删除 `_portal`、`_services` 与转发。
17. **J** G9。

服务注册按约定扫描，不集中编辑同一注册文件，步骤 7–10 可以并行。P1-04 移动的 `*Logic.cs` 与 HttpTests
查找有交叉，阶段 3 在 P1-04 三步完成后开始。

步骤 5 的 30 个试点工具已迁入以下平铺实例类；`Ecosystem` 与 `EcosystemFiles` 合为同一领域。
它们由 `EngineRegistration` 通过 `ToolCatalog` 自动注册为单例，不实现 `IDisposable`，也不解析工程会话。
既有领域算法仍复用 `TiaMcp.Logic` 和共享代码；本步保留工具方法体及纯静态辅助函数，不增加无状态服务转发层。

| 原试点文件（`McpServer.` 前缀） | 实例工具类 | 工具数 |
|---|---|---|
| `Ecosystem.cs`、`EcosystemFiles.cs` | [EcosystemTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/EcosystemTools.cs) | 3 |
| `V21Ecosystem.cs` | [V21EcosystemTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/V21EcosystemTools.cs) | 4 |
| `GitWorkflow.cs` | [GitWorkflowTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/GitWorkflowTools.cs) | 1 |
| `PlcTemplates.cs` | [TemplateTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/TemplateTools.cs) | 2 |
| `QualityAudit.cs` | [QualityAuditTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/QualityAuditTools.cs) | 1 |
| `ImportOrder.cs` | [ImportOrderTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/ImportOrderTools.cs) | 1 |
| `Guides.cs` | [GuideTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/GuideTools.cs) | 1 |
| `ToolUsage.cs` | [ToolUsageTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/ToolUsageTools.cs) | 1 |
| `PlcSoftware.OfflineSuites.cs` | [OfflineSuiteTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/OfflineSuiteTools.cs) | 16 |

[McpServer.PilotTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.PilotTools.cs)
保留指南入口使用的无属性 `GetToolUsage` 转发，并临时暴露对其他领域私有共享辅助方法的调用。
这些试点工具没有 CLI 静态调用点；CLI 的同名报告命令直接使用既有构造器。
HttpTests 的 `engineering-api-only` 检查真实实例归属、单例生命周期和指南转发；
[Test-PilotTools.py](../../scripts/checks/Test-PilotTools.py) 通过 STDIO 覆盖九个领域的直接、桥接及隔离子进程调用。
传入 `--baseline-exe` 可逐项比较迁移前后的 `tools/list` 序列；`ToolCatalog` 按名称排序，SDK 的线上枚举序列
仍以实际宿主输出为准，不在本步调整。

### Portal 领域迁移样板

CFC 是第一个样板：[CfcService](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/CfcService.cs)
放在 `Siemens/Services/`，命名空间为 `TiaMcpServer.Siemens.Services`，类名以 `Service` 结尾；
[CfcTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/CfcTools.cs) 保持在工具目录平铺。
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
从 `Portal` / `McpServer` 消失、在服务或工具类型中出现的方法族。折入 lambda、局部函数及其迭代器后的
各方法体按 IL offset 排序，保留局部编号和重载签名，忽略迁移造成的全局闭包编号。
未匹配方法报错；有意保留的例外须逐项传入 `--allow-unmatched TYPE::METHOD`，过期例外同样报错。
没有织入点的方法不在清单中，另由源码比较和工具归属检查覆盖。
它证明静态调用点顺序；方法体原样迁移的源码检查另保证参数、lambda 所在位置与线程调度不变，不能替代真机轨迹。
[Test-DomainTools.py](../../scripts/checks/Test-DomainTools.py) 用迁移前后各自的 HttpTests（`--baseline-harness` /
`--host-harness`）和 EXE（`--baseline-exe` / `--exe`），以可重复的 `--domain <name>` 选择领域，在
full/lite、直接/桥接及隔离子进程中覆盖该领域的全部工具，并核对源码中的工具名单。
`--domain Cfc` 覆盖两个工具的八种操作；均须到达未连接会话的工程前置检查，返回文本除 `meta.timestamp`（桥接为 `Meta.timestamp`）外
逐字节相同。契约、响应快照、原生清单及全部离线门禁仍按本页验收要求运行。

[Compare-CfcNativeCalls.py](../../scripts/checks/Compare-CfcNativeCalls.py) 和
[Test-CfcTools.py](../../scripts/checks/Test-CfcTools.py) 保留为兼容入口。

### 首批可选包领域

| 领域 | 服务 / 工具类 | 工具数 |
|---|---|---|
| TestSuite | [TestSuiteService](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/TestSuiteService.cs) / [TestSuiteTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/TestSuiteTools.cs) | 4 |
| V20Options | [V20OptionsService](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/V20OptionsService.cs) / [V20OptionsTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/V20OptionsTools.cs) | 5 |
| OptionalEngineering | [OptionalEngineeringService](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/OptionalEngineeringService.cs) / [OptionalEngineeringTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) | 2 |
| SpecializedExchange | [SpecializedExchangeService](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/SpecializedExchangeService.cs) / [SpecializedExchangeTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/SpecializedExchangeTools.cs) | 1 |
| SoftwareUnitDeep | [SoftwareUnitDeepService](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/SoftwareUnitDeepService.cs) / [SoftwareUnitDeepTools](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) | 7 |

`McpServer.SpecializedEngineering.cs` 保留 Motion、HMI、库及 SiVArc 生成工具；
`ManagePlcSoftwareUnit` 从 `McpServer.EngineeringManagement.cs` 并入 SoftwareUnitDeepTools。
这 19 个工具无 CLI 静态调用点；服务只通过 `IEngineeringSession` 读取会话，不写会话字段。

跨领域成员保持在内核，接口显式转发现有方法体：

- `RequireHardwareUtility<T>` 读取当前工程的 `HwUtilities`，保留在 `Portal.BaseLeftovers.cs`，
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
