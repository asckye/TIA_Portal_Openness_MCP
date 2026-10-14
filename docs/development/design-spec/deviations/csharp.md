# C# 偏差清单

状态：现行 · 最后核对：2026-10-11，master `3a227657`

规范：[C# 与 .NET API 设计规范](../csharp-design.md)。本清单记录现有代码与规范的差异、证据和处理状态，不修改代码。状态列按维护者 2026-10-11 的决定填写：默认采用“建议”列，另有决定的条目（CS-19、CS-30、CS-39、CS-55）在建议中注明；“4.0 前 - P8-62”的条目由任务 P8-62 实施。状态取值与决定的记录方式见 [设计规范索引](../README.md)。

## 摘要

### 按状态统计

| 状态 | A 机械重命名/格式 | B 设计问题 | C 测试与脚本 | 合计 |
|---|---|---|---|---|
| 4.0 前 - P8-62 | 3 | 8 | 0 | **11** |
| 4.0 后 | 14 | 14 | 1 | **29** |
| 有意偏差（已接受） | 8 | 7 | 4 | **19** |
| 合计 | 25 | 29 | 5 | **59** |

“4.0 前”中 CS-39 的决定已经做出（V4 错误填写 `details.parameter`，消息文本不变），转换按工具族分批进行；CS-42 先审查再分批处理。“4.0 后”中 CS-31 在 4.0 前先做一次不改结构的线程安全审查（P8-62）。

### 最重要的 10 条

| # | ID | 问题 | 量级 | 建议 |
|---|---|---|---|---|
| 1 | CS-39 | `ArgumentException` 不设 `ParamName`：V4 `INVALID_ARGUMENT` 的 `details.parameter` 大多为空；补齐会改变 `Message` 文本（两种运行时追加的格式还不同） | 1,691 / 286 文件 | 已决定：V4 错误填写 `details.parameter`，消息文本不变；4.0 前按工具族转换（P8-62） |
| 2 | CS-45 | 文化敏感的字符串比较与数字解析出现在 net48/net10 多目标代码中（NLS 与 ICU 结果可能不同） | ≈80 + ≥123 | 4.0 前修改（机械、可工具修复；P8-62） |
| 3 | CS-42 | 同步阻塞异步（`.Result`/`.Wait()`/`GetAwaiter().GetResult()`），含 Studio UI 与 HTTP 宿主路径 | 80 / 37 文件 | 4.0 前审查 UI 与 HTTP 宿主中的子集（P8-62） |
| 4 | CS-38 | 异常类型命名与层次：21 个异常类名不以 `Exception` 结尾（8 个公共）；`AdapterPreconditionException` 以 `ArgumentException` 表达状态前提；5 个未密封 | 21 + 5 + 1 | 4.0 前（异常类型名出现在 V4 evidence 与 `meta.error` 首行，只能借 4.0 破坏窗口改；P8-62） |
| 5 | CS-55 | 没有 `.editorconfig`，没有任何分析器配置；net48 目标未运行 NetAnalyzers | 仓库级 | 4.0 前加入配置（列入发布源码清单）并记录警告基线，先启用 4.0 规则集（规范 §13；P8-62） |
| 6 | CS-30 | 命名空间根不统一：`TiaMcpServer.*`、`TiaOpenness.*`、`TiaMcpConfigurator` 与 `TiaMcp.*` 并存 | ≈770 文件 | 4.0 后一次性机械迁移并重录织入/反射基线；Studio 保留 `TiaOpenness.*`（2026-10-11 决定） |
| 7 | CS-31 | 可变静态状态（进程级钩子、环境诊断状态、桥接目录）位于多会话宿主中 | 32 / 18 文件 | 4.0 前审查线程安全（P8-62），4.0 后改为一次性初始化或注入 |
| 8 | CS-40 | 以 `Exception.Data["..."]` 字符串键传递分类信息（如 `workerOutcomeUnknown` 决定 outcome） | 76 处 | 4.0 后改为强类型属性，先补行为测试 |
| 9 | CS-47 | Studio 与 Updater 工程关闭可空分析，另有 20 个 `#nullable disable` 文件和 213 个 `= null!`/`default!` | 45 文件 + 20 文件 | 4.0 后（Studio 阶段） |
| 10 | CS-01 | 私有/内部实例字段两种写法并存（无前缀 827 个，`_` 前缀 454 个） | 827 / 193 文件 | 4.0 后按工程机械重命名；新代码立即用 `_camelCase` |

---

## 0 度量方法

- 范围：Git 跟踪的 `*.cs`。“生产”= `src/`（1,096 个文件）+ `build-tools/`（55 个），约 18.7 万行；“测试”= `tests/`（498 个）+ `scripts/`（25 个）。`third_party/`、`reference/` 不计。
- 工具：临时 Python 扫描器（会话 scratchpad 中，不入库），先剥离注释与字符串/字符字面量内容（含逐字、插值、原始字符串），再按大括号层级识别类型、字段、方法、属性与事件，最后用正则统计；另用只读的文件列举与文本搜索核对个别数字。仓库内没有运行构建、测试或任何写操作。
- 精度：结构类计数（字段命名、访问修饰符、类型数）为启发式，误差估计在 ±5% 以内；条件编译分支按源码文本计（同一成员在 `#if` 两侧各计一次）；“≈”为估计，“≥”为已测子集，“未测”需要编译才能得到。字段命名按字段名计数（`int a, b;` 计 2）。
- 文件数指包含至少一处的文件。

---

## 1 机械性重命名与格式（CS-01–CS-25）

| ID | 规则 | 位置与数量（生产；测试另列） | 示例 | 外部影响 | 修改风险 | 建议 | 状态 |
|---|---|---|---|---|---|---|---|
| CS-01 | N-08 实例字段 `_camelCase` | 无前缀的 `private`/`internal` 实例字段名 827 个 / 193 文件（Adapters 166、FoundationHost 166、Engine 124、Shared 101、Studio 96、Logic 92、WorkerChannel 37、PlcWorker 20、EngineHost 12、BuildTools 12）；同类 `_camel` 454 个。另有 233 个 `internal` 实例字段用 PascalCase（数据载体，见 CS-33）。测试：无前缀 ≈220 | `src/WorkerChannel/ChannelClient.cs:10` `private readonly object gate` | 无 | 中：测试/脚本中有 332 处按字符串名反射（如 `tests/Engine/TiaMcp.Engine.Harness/CfcShapeChecks.cs:62` `GetField("_session")`）；重命名改变 catch 体令牌（swallowed 棘轮需复跑） | **4.0 后**：按工程用 IDE 重命名一次完成，先列出被反射引用的字段；新代码立即执行 | 4.0 后 |
| CS-02 | N-08 可变静态 `s_` | 非公共可变静态字段 32 个 / 18 文件：`_x` 10、camel 12、Pascal 10。测试 ≈34 | `src/EngineHost/ModelContextProtocol/Tools/McpServer.Profile.cs:37` `_profileOverride`；`src/Shared/Host/EnvironmentDoctor.cs:17` `ready` | 无 | 低 | **4.0 后**，与 CS-31 一起处理 | 4.0 后 |
| CS-03 | N-08（identifier names 要求静态字段 `s_`） | 非公共 `static readonly` 字段 650 个 / 220 文件用 PascalCase；另有 6 个用 `_x`/camel | `build-tools/common/TiaMcp.BuildCommon/CSharpLexer.cs:19` `LiteralStart`；不一致：`src/Logic/ModelContextProtocol/ExportStore.cs:73` `_lock` | 无 | 改则 650 处纯噪音 | **保留**：规范 N-08 已作项目取舍（常量性 `static readonly` 用 PascalCase）；6 个不一致者随 CS-02 处理 | 有意偏差（已接受） |
| CS-04 | N-01、N-02 | `_` 开头的属性 11 个 / 7 文件；小写开头的方法 2 个；PascalCase 参数 2 个（Logic） | `src/Engine/Siemens/Portal/Portal.cs:78` 属性 `_projectOpenedByUs`；`build-tools/release/ReleaseCommands.cs:760` `strictRecordHash`；`src/Updater/Updater.cs:652` `deliveryFilePath` | 无 | 低；`Portal` 成员可能被 Harness 字段检查引用，改前核对 | **4.0 前** | 4.0 前 - P8-62 |
| CS-05 | N-03 缩写与常用词 | 两字母缩写写成单词：`Db` 28 处 / 18 文件、`Io` 39 / 17、`Ip` 18 / 10、`Ui` 9 / 8、`Fb` 5、`Ob` 1；三字母全大写枚举成员 `UDT` 2；`OK`/`OKCancel` 2；`Cancelled` 3；`Login` 7 | `src/Adapters/Native/Plc/PlcSoftwareEditing.cs:17` `CreatePlcInstanceDb`；`src/Adapters.Contracts/HardwareAddressingContracts.cs:8` `IoType`；`src/Logic/V4/Error.cs:9` `IoFailed`、`Cancelled`；`src/Studio/Gui/Services/IDialogService.cs:3` `OKCancel`；`src/Adapters.Contracts/Studio/Enums.cs:7` `UDT` | 有：V4 `ErrorCode` 经命名策略成为线上错误码；契约 DTO 属性名（`IoType`、`IpAddress`、`DbMemberPath`）进入 worker 协议 JSON；Studio 枚举以字符串跨 bridge | 高（线上名称） | **保留**：N-03 规定映射 Siemens API 的标识符沿用厂商拼写（Siemens 自身用 `IoSystem`、`IoConnector`）、冻结线上名不改；只有 Studio 内部的 `DialogButtons.OKCancel` 可在 4.0 后改为 `OkCancel` | 有意偏差（已接受） |
| CS-06 | N-10 `Async` 后缀 | 带 `async` 修饰符但无后缀的方法 111 个 / 49 文件（FoundationHost、Shared、Studio、Engine 为主）；返回 Task 的非 async 方法 32 个 / 20 文件（多为 MCP 工具入口 `XxxV4`）。测试 489（C 级放宽） | `src/EngineHost/ModelContextProtocol/Tools/McpServer.Approval.cs:83` `PrecheckBeforeApproval`；`src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs:650` `PlcBlocksTools.ExportBlocks` | 无（工具名由 `Name=` 决定） | 中：方法名进入织入清单（按声明方法归并）与 Harness 反射；`Test-*Sources.py` 用正则收集方法名 | **4.0 后**：先改非工具方法；工具入口按 N-10 豁免 | 4.0 后 |
| CS-07 | N-11 取消参数 | `CancellationToken` 参数不叫 `cancellationToken` 115 处 / 32 文件（`token`、`ct` 等；`BackgroundService` 重写的 `stoppingToken` 合规）；不在最后 7 处 | `src/EngineHost/EngineHostPipeline.cs:32` `Status(..., token)` | 无 | 低（命名实参调用点需同步） | **4.0 后** | 4.0 后 |
| CS-08 | S-09 关键字类型 | BCL 类型名代替关键字 64 处 / 8 文件（Studio 33、Updater 22、Logic 9）。测试 6 | `src/Logic/Generation/GenerationPlanner.cs:199` | 无 | 无；IDE0049 可自动修复 | **4.0 前** | 4.0 前 - P8-62 |
| CS-09 | S-06 using | `System*` 不在最前 190 文件；`System` 组未排序 131 文件；`using` 在命名空间内 3 文件 | `src/Shared/PlcToolContract.cs:1`（`ModelContextProtocol` 在 `System` 前，`System.Collections.Generic` 在 `TiaMcpServer.Siemens` 后） | 无 | 低；不得与信封 E4 任务混在同一差异中 | **4.0 后**（工具一次性，单独提交） | 4.0 后 |
| CS-10 | S-07 file-scoped 命名空间 | 块级命名空间 931 文件；file-scoped 207 文件（FoundationHost、Studio、build-tools） | `src/Logic/V4/Envelope.cs:6` | 无 | 整文件缩进变化，破坏 blame、与并行任务冲突 | **保留**：既有文件不转换，新文件用 file-scoped（S-07） | 有意偏差（已接受） |
| CS-11 | S-05 关键字空格 | `if(`/`for(`/`catch(` 等 1,354 行 / 93 文件（Adapters 808、FoundationHost 430、Engine 80）。测试 ≈850 | `src/FoundationHost/BatchDocumentExportContract.cs:14` `if(payload is not JsonObject result)` | 无 | 低；纯空白不影响令牌指纹 | **4.0 后**（`dotnet format whitespace`，单独提交） | 4.0 后 |
| CS-12 | S-03 单行 if | `if (...) 语句;` 同行形式 9,151 行 / 828 文件。测试 ≈1,297 | `src/WorkerChannel/ChannelClient.cs:61` `if (!verified) throw Poison(...)` | 无 | 中：改动量大；IDE2001 为实验性规则 | **4.0 后**：工具能可靠修复时一次性处理，否则转为有意偏差，只约束新代码 | 4.0 后 |
| CS-13 | S-04 一行一条语句 | 一行多条语句 5,547 行 / 652 文件（不含自动属性访问器）；另有 3,466 行 `{ a; b; }` 式单行块 | `src/WorkerChannel/ChannelClient.cs:34` `input = workerOutput; output = workerInput; identity = expected;` | 无 | 中（同 CS-12） | **4.0 后**，与 CS-11/CS-12 同一格式化提交 | 4.0 后 |
| CS-14 | S-04 一行一个声明 | 一条语句声明多个字段 125 处 / 60 文件 | `src/WorkerChannel/ChannelClient.cs:11` `private readonly Stream input, output;` | 无 | 低 | **4.0 后** | 4.0 后 |
| CS-15 | S-02 Allman | 行尾左大括号 67 行 / 28 文件 | `src/Adapters/Native/Engineering/EngineeringServices.cs:46` | 无 | 低 | **4.0 后** | 4.0 后 |
| CS-16 | S-10 `this.` | `this.x = x` 162 处 / 110 文件（字段无前缀导致的必要限定）；其余不必要的 `this.` ≈52 | `src/WorkerChannel/ChannelClient.cs:35` | 无 | 低 | **4.0 后**，随 CS-01 自然消失 | 4.0 后 |
| CS-17 | S-08 `var` | `var` 声明 14,643 个，其中右侧显而易见（`new`、转换、字面量、`as`、`default`）约 3,495 个，其余 ≈11,100；`foreach (var` 2,863 个，显式类型 216 个 | `src/WorkerChannel/ChannelClient.cs:80` `var response = await Bounded(...)` | 无 | 高改动量、零功能价值 | **保留**：既有代码不转换；新代码按 S-08（SHOULD），IDE0007/0008 只到 suggestion | 有意偏差（已接受） |
| CS-18 | S-13 注释独占一行 | 行尾 `//` 注释 310 行 / 141 文件；另有 571 个强制位于 catch 行的 `/* swallow(...) */` 标注 | `src/WorkerChannel/ChannelClient.cs:74` | 无 | 低 | **保留**：swallow 标注由项目规则要求；其余行尾注释价值低，新代码避免 | 有意偏差（已接受） |
| CS-19 | S-12 行长（项目提案） | >120 字符 25,764 行 / 1,041 文件（占生产行约 14%）；>200 字符 4,787 行 / 640 文件，其中 589 行是 `[Description(...)]`；>400 字符 543 行 | `src/Adapters.Contracts/IOpennessAdapter.cs:81`（433 字符的接口签名） | 无 | 高改动量 | **保留**现状（2026-10-11 决定：新代码 SHOULD 每行不超过 120 字符，现有代码不重新排版）；如需强制，4.0 后加一个只收紧的行长棘轮 | 有意偏差（已接受） |
| CS-20 | S-15 `#region` | 38 处 / 25 文件（Engine 33） | `src/Adapters/Native/Plc/PlcB5Alarms.cs:40` | 无 | 低 | **4.0 后** | 4.0 后 |
| CS-21 | N-14 文件布局 | 含多个顶级类型的文件 249 个（共 1,132 个类型）；文件名与其中任何类型都不同的文件 198 个（多为 `*Contracts.cs` DTO 族与按主题拆分的 `partial`） | `build-tools/release/BuildOutputCache.cs`（8 个顶级类型）；`src/Adapters.Contracts/Studio/Enums.cs:50` 在 `Enums.cs` 中定义异常；`build-tools/release/BranchGate.cs` 只含 `partial ReleaseCommands` | 无 | 移动文件需同步 `<Compile Include>`、必需文件清单与文档（B-05） | **保留**合同族与主题分部（N-14 例外）；误导性的个案（异常放在 `Enums.cs`、单文件 5 个以上无关类型）4.0 后整理 | 有意偏差（已接受） |
| CS-22 | S-14 空行 | 连续两个以上空行 209 处 / 89 文件 | `src/Shared/PlcToolContract.cs:23-24` | 无 | 无 | **4.0 后**（格式化提交） | 4.0 后 |
| CS-23 | S-01 行尾空白 | 5 行 / 4 文件 | `src/Engine/Siemens/Portal/Portal.cs:714`；`src/Adapters/Native/Plc/PlcIntegrity.cs:230` | 无 | 无 | **4.0 前** | 4.0 前 - P8-62 |
| CS-24 | S-11 访问修饰符 | 未写访问修饰符：方法 16、字段 3、类型 2、属性 1（多在 `build-tools/native-call-weaver`） | `build-tools/native-call-weaver/Weaver.cs:12` `static string Fingerprint => ...` | 无 | 中：改动 weaver 源码会改变织入器指纹（`instrumenterSha256`），需从原始编译输出重新织入全部产物 | **4.0 后**（与下一次必要的 weaver 改动合并） | 4.0 后 |
| CS-25 | §1.5 语言版本 | 所有 net48 工程 `LangVersion latest`/`Latest`（引擎、适配器、PlcWorker、Contracts、Logic、WorkerChannel、OnlineChannels 等），Microsoft 不支持在 net48 上使用高于 C# 7.3 的版本 | `src/Engine/TiaMcp.Engine.V21.csproj`；`src/Adapters/build/Adapter.Common.props:28` | 无 | 回退版本会使大量代码无法编译 | **保留**：只用纯编译器特性，禁止需要运行时支持的特性（§1.5） | 有意偏差（已接受） |

---

## 2 设计问题（CS-30–CS-58）

| ID | 规则 | 位置与数量 | 示例 | 外部影响 | 修改风险 | 建议 | 状态 |
|---|---|---|---|---|---|---|---|
| CS-30 | N-07 命名空间 | 命名空间根：`TiaMcp.*` 674 文件；`TiaMcpServer.*` 519（Engine 269、Logic 112、Shared 100、测试 31）；`TiaOpenness.*` 247（Studio 129、`src/Shared` 中的 `TiaOpenness.Shared` 31、Adapters 6）；`TiaMcpConfigurator` 8。`src/Shared` 一个目录下有 5 个根；EngineHost 的 5 个文件用 `TiaMcp.FoundationHost` | `src/Shared/HostFailurePolicy.cs:5` `namespace TiaOpenness.Shared`；`src/Studio/Gui/Configuration/ConfigCore.cs` `namespace TiaMcpConfigurator` | 不改 MCP 契约；类型全名出现在织入清单、Harness 反射、日志类别（`TiaMcpServer.Swallowed`）、XAML `clr-namespace` 映射和 `meta.error` 堆栈（D1 允许变化） | 高：全仓库改动，需重录织入基线与反射名 | **4.0 后**一次性机械迁移；新代码只用 `TiaMcp.*`；2026-10-11 决定：`src/Studio` 保留 `TiaOpenness.*` 产品命名空间，其余根在 4.0 后迁到 `TiaMcp.*` | 4.0 后；`src/Studio` 的 `TiaOpenness.*` 为有意偏差（已接受） |
| CS-31 | U-24 静态状态 | 非公共可变静态字段 32 个 / 18 文件，其中有进程级钩子（`InvocationJournal.BindingSnapshot`、`EngineeringScalarProperties.ValueRenderer`）、环境诊断状态（`EnvironmentDoctor` 的 `ready`/`cause`/`fixEn`/`fixZh`/`groupOk`）、桥接目录（`McpServer.ToolBridge` 的 `_bridgeCatalog`/`_bridgeInvoker`）、测试钩子（`ExportStore.NowUtc`）；另有迁移期静态门面 `EngineServices` 与静态 `McpServer` | `src/Shared/Host/EnvironmentDoctor.cs:17`；`src/Adapters/Diagnostics/InvocationJournal.cs:21`；`src/Shared/Host/McpServer.ToolBridge.cs:28` | 无 | 中：涉及启动顺序与多会话 HTTP 宿主 | **4.0 后**改为一次性初始化或实例注入；**4.0 前**先审查这些字段在多会话宿主中的线程安全（不改结构） | 4.0 后；4.0 前的线程安全审查：P8-62 |
| CS-32 | T-01、T-02、T-03 公共表面与密封 | 生产公共类型 1,251 / 2,361（Engine EXE 164、Studio 211、适配器 124）；未密封的公共类 238 个（Engine 134，多为 `ResponseMessage` 派生的响应 POCO；Studio 57；Contracts 24；Logic 12）；未密封的非公共类 7 个 | `src/Adapters.Contracts/HardwareDeviceContracts.cs:9` `public class HardwareGsdCandidate`；`src/Shared/PlcToolContract.cs:18` `public class ResponseBlockLogic : ResponseMessage` | 无（密封与改为 internal 不改变 JSON）；Harness 加载织入后的 EXE 用反射查找类型 | 中：反射可见性、WPF 绑定对公共类型的要求 | **4.0 后**：旧响应 POCO 随旧族移除一并处理；Contracts DTO 密封；Studio 按 WPF 需要保留 | 4.0 后 |
| CS-33 | M-01 公共字段 | 对外可见类型上的公共实例字段 244 个 / 12 文件（`S7LiveReader` 54、`S7WebApiChannel` 46、`PlcDocumentationLogic` 39、`HardwareAmlLogic` 33、`RuntimeChannelsLogic` 19、`PlcSimAdvancedChannel` 15 等）；`internal` 类型上的公共字段另 ≈269（如 `DccLogic` 的请求类）；`internal readonly` PascalCase 字段 233 个 | `src/OnlineChannels/S7LiveReader.cs:22` `public bool Connected;`；`src/Logic/Siemens/DccLogic.cs:44` | 可能：STJ 默认不序列化字段、Newtonsoft 与 `IncludeFields = true`（Studio `BridgeJson`、`HostObservations`、`EnvironmentDoctor`）会序列化；字段改属性可能增减输出 | 中：须逐类型用响应快照验证 | **4.0 后**，逐类型并附快照证据 | 4.0 后 |
| CS-34 | M-07 默认参数 | 带默认值的接口方法 50 个（Contracts 30，均在 `IOpennessAdapter` 各 facet；Engine 14；Studio 5）；带默认参数的公共方法 431 个（1,036 个参数） | `src/Adapters.Contracts/IOpennessAdapter.cs:54` `OpenProject(string path, bool dryRun = true)` | facet 参数与 MCP 工具参数一一对应 | 改为重载会使 facet 成员数成倍增加，八个适配器都要改 | **保留**：M-07 取舍；新增 A 级接口成员不带默认值 | 有意偏差（已接受） |
| CS-35 | M-05 布尔参数 | 两个及以上 `bool` 参数的公共方法 97 个（Contracts 20、Adapters 52）；全部方法 602 个 | `src/Adapters.Contracts/IOpennessAdapter.cs:72` `IOpennessAdapter.ExportBlocksAsDocuments(..., bool recursive, ..., bool preservePath, ..., bool dryRun)` | 与线上布尔参数（`dryRun`、`confirm`、`overwrite`）一致 | 中 | **保留**：M-05 例外；新的内部 API 用请求对象或枚举 | 有意偏差（已接受） |
| CS-36 | U-01、U-03、M-03 集合成员 | 返回数组的公共属性 115 个（Contracts 107）；可设置的集合属性 142 个（Logic 生成模型 83、Contracts 32）；可设置的数组属性 114 个；公共 `List`/`Dictionary`/`HashSet` 属性 190 个、返回值 78 个、参数 23 个 | `src/Adapters.Contracts/CandidateCompile.cs:12` `public string[] DevicePath { get; set; }`；`src/Logic/Generation/Models/AlarmsPart.cs:8` | 有：多数是 worker 协议 DTO 与生成计划 JSON 模型 | 高（序列化形状） | **保留**：U-01 的线上 DTO/模型例外；非 DTO 的公共 `List` 返回值（Adapters 38 个）在 4.0 后随适配器重构收紧 | 有意偏差（已接受） |
| CS-37 | T-07 嵌套类型 | 对外可见的嵌套类型 64 个 / 25 文件（Logic 30、Adapters 21） | `src/Adapters/Native/Plc/PlcServices.cs:35` `WatchTechnologySurface` | 无 | 中：类型全名进入织入清单与反射 | **4.0 后** | 4.0 后 |
| CS-38 | N-05、E-05 异常类型 | 不以 `Exception` 结尾的异常类 21 个（公共 8 个：`ChannelFailure`、`ChannelFault`、`DeviceCreationRejection`、`PlcExportRejection`、`PlcImportRejection`、`SessionCandidateRejection`、`SourceRejection`、`PlcBatchImportKnownFailure`）；未密封 5 个（`PortalException` 与 Studio 的 4 个）；`AdapterPreconditionException : ArgumentException` 在 `isArgument=false` 时表达状态前提；49 个自定义异常中 47 个没有无参构造函数 | `src/WorkerChannel/ChannelMessage.cs:57`；`src/Logic/V4/PlcImport.cs:12`；`src/Logic/Siemens/Portal/PortalException.cs:6`；`src/Adapters.Contracts/AdapterPreconditionException.cs:7` | 有：`HostBehavior.FailureEvidence` 把异常类型名写入 V4 evidence；`meta.error` 首行“类型: 消息”是兼容契约（D1）；worker 回报 `reportedExceptionType` | 中 | **4.0 前**：公共异常的改名与 `PortalException` 密封放进 4.0 破坏窗口，同步 evidence 快照与迁移表；构造函数按 E-05 取舍不补 | 4.0 前 - P8-62 |
| CS-39 | E-03 `ParamName` | 产品代码 `throw new ArgumentException(...)` 1,875 处，其中只传消息、无 `ParamName` 的 1,691 处 / 286 文件（Logic 604、Engine 449、Adapters 321、Shared 129、FoundationHost 115） | `src/Logic/V4/V4Validation.cs:15` `throw new ArgumentException(message)` | 有：`HostFailurePolicy.Parameter` 取 `ParamName` 填 V4 `details.parameter`；设置后 `Message` 追加参数名（net48 与 net10 格式不同），旧族消息与 evidence 文本随之变化 | 中 | **4.0 前**：2026-10-11 决定 V4 错误填写 `details.parameter`，`Message` 文本保持不变；P8-62 提供不追加参数名的参数异常辅助方法并更新 E-03，按工具族分批转换（与 P8-60 的 API-04 协调），每批附快照差异 | 4.0 前 - P8-62 |
| CS-40 | E-11 `Exception.Data` | 76 处 `.Data["..."]`（`foundationRequestSent` 10、`attemptedPath` 8、`workerRequestSent` 5、`workerOutcomeUnknown` 4、`nativeResultEvidence` 4、`foundationSessionPoisoned` 4 等），部分被分类逻辑读取 | `src/Shared/HostFailurePolicy.cs:37` 读取 `error.Data["workerOutcomeUnknown"]`；`src/FoundationHost/EngineWorkerClient.cs:149` 写入 | 间接：决定 V4 `outcome` | 中：分类错误会把未知结果报成失败 | **4.0 后**：先补分类行为测试，再改为强类型属性或标记接口 | 4.0 后 |
| CS-41 | E-06 捕获通用异常 | `catch (Exception ...)` 1,209 处（其中 354 处带 `when`），通用 `catch` 340 处；571 处吞异常已标注；swallowed 棘轮基线为 0 | `src/Shared/PlcToolContract.cs:45`（工具边界，合规） | 无 | 无 | **保留**：FDG 不鼓励捕获 `Exception`，项目以 E-06 的标注与棘轮逐个治理；不启用 CA1031 | 有意偏差（已接受） |
| CS-42 | U-19 同步阻塞 | `GetAwaiter().GetResult()` 52、`).Result` 15、`.Wait()` 13，共 80 处 / 37 文件（FoundationHost 17、Engine 14、OnlineChannels 14、BuildTools 12、Shared 11、EngineHost 7、Studio 4） | `src/EngineHost/WorkerToolInvoker.cs:162` `.Result`；`src/EngineHost/ModelContextProtocol/Tools/McpServer.Approval.cs:193`；`src/Studio/Gui/Configuration/ConfigCore.cs:248` | 无 | Studio UI 死锁、HTTP 宿主线程池饥饿 | **4.0 前**审查 Studio 与 FoundationHost/EngineHost HTTP 路径中的子集；入口、CLI 与专用所属线程上的保留并加注释；其余 4.0 后 | 4.0 前 - P8-62 |
| CS-43 | U-21 `async void` | 非事件处理器的 `async void` 2 个；事件处理器 9 个（允许） | `src/Studio/Gui/Common/Mvvm.cs:40` `AsyncCommand.Execute`（只有 `try/finally`，异常会抛到 Dispatcher）；`src/Studio/Gui/WorkbenchControl.cs:94` `UpdateControlDetail` | 无 | 低 | **4.0 前**：两处加异常捕获与报告 | 4.0 前 - P8-62 |
| CS-44 | U-23 时间戳 | `DateTime.Now` 313 处 / 94 文件（Engine 141、Shared 105、Logic 44），多为旧 meta 时间戳（`ResponseMeta.Basic(DateTime.Now, ...)`）、文件名戳与用 `DateTime.Now` 相减计时；envelopes 棘轮的手写 `timestamp_now_assignments` 为 85（只减不增） | `src/Shared/DocumentsTools.cs:50`；`src/Shared/DocumentsTools.cs:106`（时长） | 旧族 meta 时间戳编码是冻结的响应字节；V4 `Meta` 已用 UTC，`PlcToolContract.Map` 会去掉内层 `timestamp` | 中 | **保留**至旧族移除；计时改 `Stopwatch` 在 4.0 后进行；新代码按 U-23 | 有意偏差（已接受） |
| CS-45 | U-06、U-07 全球化 | 文化敏感的 `StartsWith`/`EndsWith`/`IndexOf`/`Compare` ≈80 处 / 42 文件（Engine 23、Logic 16、BuildTools 13、Adapters 9、Shared 8、Studio 6、Contracts 3）；无区域参数的 `int/double/DateTime.Parse/TryParse` 119 处 / 63 文件，`string.Format` 4 处；`ToLower()`/`ToUpper()` 无（已全部使用 Invariant） | `src/Adapters.Contracts/WatchTableEntryXml.cs:23` `text.StartsWith("%")`；`src/Adapters.Contracts/HardwareCapabilities.cs:10` `int.Parse(release)` | 无（预期行为一致；风险在于非英语区域机器与 NLS/ICU 差异） | 低：机械修改；可先在一个工程上试用 `dotnet format analyzers --diagnostics CA1310 CA1305`，修复不了的手改 | **4.0 前** | 4.0 前 - P8-62 |
| CS-46 | U-11 选项缓存 | `new JsonSerializerOptions` 78 处，其中 66 处在每次调用时新建（63 文件），包括每次 PLC 工具调用都会执行的 `PlcToolContract.Map` | `src/Shared/PlcToolContract.cs:91` | 无（选项不变则字节不变） | 低：须证明缓存后的选项完全相同 | **4.0 后** | 4.0 后 |
| CS-47 | NR-01 可空 | 关闭可空的产品工程：Studio Bridge、Client、Contracts、Openness ×8（`src/Studio/Directory.Build.props:15`）、Updater；Studio Core 仅 `annotations`；涉及 45 个文件约 6.5 千行。启用可空的工程中另有 20 个 `#nullable disable` 文件（Adapters `Native/Studio` 6、Contracts `Studio` 4、`src/Shared/BundleLayout.cs`、`DataLocations.cs`、`LocalProcess.cs` 等）；`= null!`/`default!` 213 处 | `src/Shared/BundleLayout.cs:1`；`src/Adapters.Contracts/PlcSoftwareContracts.cs:31` | 无 | 中：链接到多个工程的共享文件要同时满足各工程设置 | **4.0 后**（随 Studio 阶段） | 4.0 后 |
| CS-48 | U-15 Dispose 模式 | 未密封且没有 `Dispose(bool)` 的可释放类型 2 个；没有终结器（合规） | `src/Engine/Siemens/Portal/Portal.cs:39`；`src/Studio/Gui/Views/BlocksView.xaml.cs:11` | 无 | 低（`Portal` 无派生类，测试中只有 partial 片段） | **4.0 后**：两者改为 `sealed` | 4.0 后 |
| CS-49 | M-09 事件委托 | 以 `Action` 作为事件类型 2 处（internal 接口与实现） | `src/FoundationHost/HostSession.cs:18` `event Action? StateChanged` | 无 | 无 | **保留**：internal 回调，C# 约定允许 | 有意偏差（已接受） |
| CS-50 | T-11 重复类型 | Studio DTO/枚举定义两份：`src/Adapters.Contracts/Studio/{Dto,Enums,VersionControl}.cs`（`TiaMcp.Adapters.Contracts.Studio`，292 行，`#nullable disable`）与 `src/Studio/Contracts/Models/{Dto,Enums,VersionControl}.cs`（`TiaOpenness.Contracts.Models`，437 行）；`SessionPreconditionException` 也定义两次 | `src/Adapters.Contracts/Studio/Enums.cs:50`；`src/Studio/Contracts/Models/Enums.cs:58` | Studio bridge 内部协议（同版本发布） | 中 | **4.0 后**（Studio 阶段合并为一份） | 4.0 后 |
| CS-51 | M-02、U-02 | 公共 `static readonly` 可变数组 18 个 / 13 文件（Logic 13）；返回集合类型却 `return null` 的方法 8 个 | `src/Adapters.Contracts/WatchTableEntryXml.cs:105` `public static readonly string[] ReadOnlyEntryAttributes`；`src/Adapters/Native/Libraries/LibraryService.cs:1401` | 无 | 低 | **4.0 前**（改为 `IReadOnlyList<T>`/空集合；`return null` 若作“无效”信号则改为 Try 模式） | 4.0 前 - P8-62 |
| CS-52 | U-14 XML | `new XmlDocument` 6 处（引擎 CLI：`ReportBuilders`、`PlcHmiSyncXml`、`CliProbes`）；其余 XML 处理用 `XDocument`/`XmlReader`（21 处显式 `DtdProcessing`、26 处 `XmlResolver = null`） | `src/Engine/Cli/ReportBuilders.cs:1813` | 无 | 低（net48 4.5.2 起 `XmlDocument` 默认不解析外部实体） | **4.0 后**：确认输入来源与安全设置，按 CA3075 处理 | 4.0 后 |
| CS-53 | U-10 序列化 | 生产代码中仍有 1 个文件使用 Newtonsoft.Json（`JsonConvert` 1 处）；测试 10 个文件 | `src/OnlineChannels/S7WebApiChannel.cs` | 视该通道输出是否进入响应而定 | 低 | **4.0 后**（P2-04 单一序列化边界） | 4.0 后 |
| CS-54 | U-26 条件编译 | 两个按发布版本定义的符号不在 `TiaFeatures.props` 中：`OPCUA_NO_EXTENSION`（14sp1）、`OPCUA_NO_UNITS`（15.1），定义在 `src/PlcWorker/TiaMcp.PlcWorker.csproj:19-20`，已登记在 `tia-feature-expectations.json` | 同左 | 无 | 无 | **保留**：服务于第三方 `SiemensOpcUaModelled` 源码的构建角色符号，已由期望文件覆盖；在规范中登记 | 有意偏差（已接受） |
| CS-55 | §13 工具化 | 仓库没有 `.editorconfig`；没有任何工程设置 `EnableNETAnalyzers`、`AnalysisLevel`、`TreatWarningsAsErrors`、`EnforceCodeStyleInBuild` 或 `NoWarn`；net48 与 netstandard2.0 目标因此不运行 NetAnalyzers；现有警告数未知 | 仓库根目录 | 无 | 低（先以 suggestion 引入）；新增文件需按 B-05 运行必需文件检查 | **4.0 前**：按规范 §13 加入配置，先记录一次完整构建的警告基线，只把已清零的规则设为 warning/error；2026-10-11 决定：配置文件列入发布源码清单，先启用 §13.3 的 4.0 规则集 | 4.0 前 - P8-62 |
| CS-56 | E-05 控制流异常 | 用于控制流的私有/内部异常 13 个，其中 `InputFailure` 在 6 个工具契约文件中各写一份，另有 `Rejection`、`Rejected`×2、`Stopped`、`GenerationCallRejection`、`GenerationPersistenceUnknown` 等 | `src/Shared/EngineeringToolContract.cs:38`；`src/Shared/HardwareToolContract.cs:32`；`src/Shared/DccTools.cs:20`；`src/Shared/PlcToolContract.cs:30` | 无 | 低 | **4.0 后**：合并为一个共享的内部类型或改为结果类型 | 4.0 后 |
| CS-57 | E-04 | 产品代码抛 `System.Exception` 2 处 | `src/Engine/ModelContextProtocol/Tools/McpServer.Patch.cs:25`；`src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs:246`（`?? throw new Exception("spec parsed to null")`） | 有：异常类型名进入 evidence；改为 `ArgumentException`/`JsonException` 后分派前的分类会变为 `Argument` | 低 | **4.0 前**（借 4.0 窗口，附快照差异） | 4.0 前 - P8-62 |
| CS-58 | U-27 HttpClient | 产品代码新建 `HttpClient` 4 处，其中更新检查每次调用都新建 | `src/Shared/Host/McpServer.Maintenance.cs:44` `var client = new HttpClient();`；`src/OnlineChannels/S7WebApiChannel.cs:369`（每个通道一个） | 无 | 低 | **4.0 后** | 4.0 后 |

---

## 3 测试与脚本（CS-70–CS-74）

| ID | 规则 | 位置与数量 | 示例 | 外部影响 | 修改风险 | 建议 | 状态 |
|---|---|---|---|---|---|---|---|
| CS-70 | N-02 | 含下划线的测试方法名 615 个 / 94 文件 | `tests/**` | 无 | 无 | **保留**（C-01 允许） | 有意偏差（已接受） |
| CS-71 | E-04、C-03 | `throw new Exception` 229 处 / 75 文件；`ApplicationException` 4 处 | `tests/**` 控制台套件 | 无 | 低；不得借机放宽断言（B-04） | **4.0 后**改用套件检查辅助或 `InvalidOperationException` | 4.0 后 |
| CS-72 | M-01、U-24 | 测试替身中的公共字段 183 个 / 39 文件；可变静态字段 34 个 | `tests/Adapters/TiaMcp.Adapters.CompileCandidate.Tests/NativeStandIns.cs:62` | 无 | 无 | **保留**（C-02） | 有意偏差（已接受） |
| CS-73 | N-07 | 全局命名空间的测试文件 212 个（控制台测试入口为主） | `tests/**` | 无 | 无 | **保留**（C-02） | 有意偏差（已接受） |
| CS-74 | N-10 | 无 `Async` 后缀的 Task 测试方法 489 个 | `tests/**` | 无 | 无 | **保留**（C-01） | 有意偏差（已接受） |

---

## 4 已符合的规则（参照）

以下规则在生产代码中测得 0 处或接近 0 处偏差，列出以说明覆盖范围：

| 规则 | 测量结果 |
|---|---|
| S-01 编码与换行 | BOM 0、CRLF 0、制表符缩进 0（行尾空白 5 行见 CS-23） |
| U-10 不安全序列化 | `BinaryFormatter` 0、`[Serializable]` 0 |
| U-15 终结器 | 终结器 0、`GC.SuppressFinalize` 不需要 |
| TH-09 锁对象 | `lock (this)`、`lock (typeof(...))` 0 |
| E-04 保留异常 | `NotImplementedException` 0、`ApplicationException` 0、`NullReferenceException`/`IndexOutOfRangeException` 0（`Exception` 2 处见 CS-57） |
| E-07 重新抛出 | `throw ex;` 形式的丢栈重抛 0（3 处 `throw error;` 抛的是新建异常） |
| U-07 大小写转换 | 文化敏感的 `ToLower()`/`ToUpper()` 0；`StringComparison.CurrentCulture/InvariantCulture` 0 |
| 编译抑制 | `#pragma warning disable` 0 |
| N-04、N-05、N-06 | 接口 `I` 前缀生产 100%；`Enum`/`Flags` 后缀 0；特性后缀偏差 0 |
| N-08 常量 | 468 个常量全部 PascalCase；`readonly static` 顺序 0 |
| N-12 工具名 | 546 个 `[McpServerTool]` 全部显式 `Name =`；`XxxV4` 数字后缀符合 FDG 新版本命名 |
| S-15/其他 | `goto` 0；`dynamic` 2（Logic） |
| 棘轮 | swallowed 基线 0；comments 基线 0；mcp-text 基线 0（另有 435 条已审查数据）；envelopes `timestamp_now_assignments` 85、`success_assignments` 112 |
| T-08 | 面向 net48 的程序集未使用默认接口成员（编译器会拒绝） |

---

## 5 建议的处理顺序

1. **4.0 前（P8-62），零外部影响**：CS-55（配置与警告基线）→ CS-23、CS-08、CS-04、CS-51、CS-43、CS-45（小而机械，或纠正潜在缺陷）。
2. **4.0 前（P8-62），附快照证据**：CS-39（已决定：V4 填 `details.parameter`，消息文本不变）、CS-38 与 CS-57（异常类型名进入 evidence）、CS-42（UI 与 HTTP 宿主中的阻塞调用）、CS-31 的线程安全审查。
3. **4.0 后，一次性格式化**（单独提交，不与信封 E4 任务混合，完成后复跑全部棘轮与源码检查）：CS-09、CS-11、CS-12、CS-13、CS-14、CS-15、CS-22。
4. **4.0 后，重命名**（先列出反射与源码正则依赖，重录织入基线）：CS-01、CS-02、CS-16、CS-06、CS-07、CS-37，最后 CS-30。
5. **4.0 后，设计改进**：CS-40、CS-46、CS-33、CS-32、CS-48、CS-50、CS-47、CS-52、CS-53、CS-56、CS-58、CS-20、CS-24、CS-71。
