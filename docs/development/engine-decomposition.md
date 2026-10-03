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

除 [重构计划](refactor-plan.md) 的 L0–L2 外：

1. 兼容快照与基线比较为 0 breaking、0 compatible、0 info，lite 名单一致。`tools/list` 顺序不属于契约；
   `ToolCatalog` 引入时按名称确定排序，此后不再变化。
2. 织入覆盖清单中西门子成员的多重集合（`sites[].member`）前后一致；这就是纯迁移不改变调用序列的证据。
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

## G9：单 PLC 工程的模糊匹配

`TiaMcp.Logic/Siemens/Guard.cs` 第 63–64 行的规则（单 PLC 工程中任意名称都解析到唯一 PLC）只经由
`Portal.GetPlcSoftware` → `ResolvePlcSoftwareFuzzy`（P.Software.cs 第 44–76 行）在精确解析失败后生效。
这 42 处调用包括下载、在线、删除、PLC 表、OPC UA、报警和工艺对象等写入或在线路径；HTTP 会话共享同一
Portal，其他客户端改绑后，请求的名称可能静默解析到对方工程的 PLC。

修复放在步骤 4 之后，由内核统一的 `ResolvePlc(path, Read|Write)` 完成：非空名称不再适用该规则，写入只接受
精确或别名匹配，错误名称返回 NotFound 与可用 PLC 路径提示。维护者已决定（2026-10-03）：空 `softwarePath`（12 个参数默认空串）
仍表示“唯一 PLC”；非空名称在读取和写入中都只接受精确或别名匹配，不保留唯一子串匹配。修复不改 schema，但改变错误语义，需要发布说明，
`SoftwareLookupRuntimeTests.cs` 第 66 行的期望随之反转。
