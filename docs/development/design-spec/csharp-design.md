# C# 与 .NET API 设计规范

状态：现行 · 最后核对：2026-10-11，master `3a227657`

[设计规范索引](README.md) · [C# 偏差清单](deviations/csharp.md)

维护者于 2026-10-11 决定以 Microsoft 设计准则为基线编写项目自己的 C# 规范（[ADR 0015](../decisions/0015-microsoft-design-spec-baseline.md)）。新代码必须遵守本规范；现有代码与本规范不一致之处逐条列入 [C# 偏差清单](deviations/csharp.md)，每条的状态（4.0 前修改、4.0 后修改或有意偏差）已按 2026-10-11 的决定登记，4.0 前的修改由 P8-62 实施。

度量快照：master `3a227657`（2026-10-11）。计数方法与口径见 [偏差清单 §0](deviations/csharp.md#0-度量方法)。

---

## 1 范围与优先级

### 1.1 适用范围

| 范围 | 适用 | 说明 |
|---|---|---|
| `src/**/*.cs`、`build-tools/**/*.cs` | 全部规则 | 产品代码与构建/发布工具（1,151 个文件，约 18.7 万行） |
| `tests/**/*.cs`、`scripts/**/*.cs` | 全部规则，§12 列出的放宽项除外 | 测试、夹具、诊断与检查程序（523 个文件） |
| `third_party/**`、`reference/**`、`sdk/**` | 不适用 | 第三方与官方示例保留原样式和许可证（CLAUDE.md） |
| 生成文件（嵌入目录、`ToolUsageData.json`、契约快照等） | 不适用 | 只能由仓库生成器重写，永不手改（B-03） |

### 1.2 规则来源与优先级

冲突时按下表从上到下裁决：

| 顺序 | 来源 | 管辖内容 |
|---|---|---|
| 1 | 外部契约与安全不变量 | MCP V4 契约（工具名、参数名与类型、返回结构、错误码，以 `manifest/contracts/v4/baseline/*.json` 为准）、worker 协议 2 线格式、Siemens Openness 调用顺序/参数/线程归属、文件格式与 manifest。C# 规则不得为满足命名或风格而改变这些内容。 |
| 2 | 仓库规则 | [CLAUDE.md](../../../CLAUDE.md)、[AGENTS.md](../../../AGENTS.md)、`docs/development/*`。本规范把其中与 C# 有关的部分收为“项目规则”，标记 **[P]**。 |
| 3 | 本规范的 MUST/SHOULD/MAY 条目 | 以 Microsoft 为基线，含对 Microsoft 条目的项目化取舍（每条取舍都在偏差清单中有对应编号）。 |
| 4 | Microsoft 来源 | 见下。 |
| 5 | 所在文件的既有风格 | 仅作过渡：偏差处理完成前，修改既有文件时沿用该文件的风格（与 runtime 风格第 9 条及 AGENTS.md “保持周围代码的命名、注释密度和写法”一致）；**新文件必须遵守本规范**。 |

Microsoft 来源之间的分工：

- **[.NET Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)（FDG）**：管 API 的形状（命名、类型、成员、可扩展性、异常、使用、模式）。注意：learn.microsoft.com 上的 FDG 页面是 2008 年第 2 版的转载，每页都声明第 3 版已全面修订、部分内容可能过时。本规范以在线页面为准，过时之处（例如默认参数、`[Serializable]`）在条目中写明项目做法。
- **[.NET runtime C# coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)**：管实现代码的写法（大括号、字段命名、`var`、`this.` 等）。
- **[C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)** 与 **[identifier names](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names)**：docs 团队约定，采纳自 runtime 与 Roslyn；与 runtime 冲突时（例如 docs 的 65 字符行宽只针对网页示例）以本规范条目为准。
- 可执行映射：.NET SDK 自带的代码样式规则（IDExxxx，经 `.editorconfig`）与 Microsoft.CodeAnalysis.NetAnalyzers 质量规则（CAxxxx）。构建是离线的，不引入新的 NuGet 分析器包（例如 StyleCop、VS Threading Analyzers）。

### 1.3 术语

- **MUST / MUST NOT**：新代码必须满足；现有代码的不一致属于偏差，必须在偏差清单中登记并给出状态。
- **SHOULD / SHOULD NOT**：默认做法；不遵守时在评审中说明理由。
- **MAY**：允许。
- **[P]**：项目规则（超出 Microsoft 准则或对其取舍），来源文档写在“依据”列。
- **线上契约（wire contract）**：对 MCP 客户端、worker/bridge 进程或文件可见的名称与形状。
- **工具列**：可执行的规则 ID 与建议严重性，格式为“规则 ID：4.0 前严重性 → 目标严重性”。“无”表示没有 SDK 内置规则，依赖评审或项目检查。严重性取值为 editorconfig 的 `error`/`warning`/`suggestion`/`silent`/`none`。各规则对应的现存违规估计见 §13。

### 1.4 程序集分级

FDG 面向可复用的公共类库，本仓库没有第三方 C# 调用方（外部消费者是 MCP 客户端、工作台用户与 worker 进程）。因此按程序集分级应用：

| 级别 | 程序集 | 应用方式 |
|---|---|---|
| A 契约/共享库 | `TiaMcp.Adapters.Contracts`、`TiaMcp.WorkerChannel`、`TiaMcp.Logic`、`TiaOpenness.Contracts`、`TiaMcp.BuildCommon`、`TiaMcp.OnlineChannels` | 跨进程、跨版本或被多个宿主引用，FDG 的公共 API 规则完整适用（线上 DTO 例外见 U-01）。 |
| B 应用程序集 | 两个引擎（`TiaMcp.Engine.V20`、`TiaMcp.Engine.V21`）、FoundationHost、EngineHost、PlcWorker、八个适配器、Studio（Gui/Core/Bridge/Client/Openness）、Updater、WriteGuard、release tool、native-call-weaver | 公共表面最小化；FDG 的类型/成员/异常规则作为 SHOULD；runtime 风格与 C# 约定完整适用。 |
| C 测试与脚本 | `tests/**`、`scripts/**` | 同 B，另按 §12 放宽。 |

MCP 工具入口方法（带 `[McpServerTool]`）是一个独立表面：C# 方法名不上线（工具名由 `Name = "..."` 给出），**参数名、参数类型与默认值上线**，由 V4 契约基线冻结。

### 1.5 .NET Framework 4.8 与 .NET 10 的适用说明

| 问题 | 项目做法 |
|---|---|
| 语言版本 | 所有工程 `LangVersion latest`。Microsoft 的 [语言版本说明](https://learn.microsoft.com/dotnet/csharp/language-reference/configure-language-version) 不支持在 net48 上使用高于默认（C# 7.3）的版本；项目有意偏差（偏差清单 CS-25 记录）。只使用纯编译器特性；需要运行时支持的特性（默认接口成员、`static abstract` 接口成员、ref struct 泛型等）在面向 net48 的程序集中 MUST NOT 使用。 |
| 需要特性类型的语言功能 | `init`、`required`、可空分析特性（`NotNullWhen` 等）在 net48 BCL 中不存在。需要时在共享源码中提供 `internal` polyfill（每个程序集只一份），不得引入新包；当前仓库没有此类 polyfill。 |
| BCL API 差异 | `ArgumentNullException.ThrowIfNull`、`ObjectDisposedException.ThrowIf`、`Lock` 类型、`string.Contains(char)` 等仅在新运行时可用。多目标代码用 `#if NET`/`NETFRAMEWORK` 或共享辅助方法，不得用发布版本宏代替框架宏。 |
| 全球化 | net48 使用 NLS，.NET 10 在 Windows 上使用 ICU（[说明](https://learn.microsoft.com/dotnet/core/extensions/globalization-icu)）。同一份多目标代码中的文化敏感比较可能得出不同结果，这是 U-07/U-08 为 MUST 的原因。 |
| 分析器 | SDK 只对 .NET 5+ 目标默认启用 NetAnalyzers；net48/netstandard2.0 目标需要显式 `EnableNETAnalyzers=true`（见 §13）。 |
| 序列化 | STJ 在 net48 上以包形式随包发布（版本由宿主固定），在 net10 上为框架内置；两者的默认行为相同时才可共享 DTO（U-12）。 |

---

## 2 命名

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| N-01 | MUST | 命名空间、类型、方法（含局部函数）、属性、事件、公共字段、枚举成员、常量用 PascalCase；参数与局部变量用 camelCase。 | 统一可读性，与 BCL 一致。 | [FDG 大小写](https://learn.microsoft.com/dotnet/standard/design-guidelines/capitalization-conventions)；[identifier names](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names#naming-conventions)；runtime 第 12、13 条 | IDE1006：suggestion → warning |
| N-02 | MUST NOT | 标识符中使用下划线、连字符或其他非字母数字字符；例外：N-08 的字段前缀、§12 的测试方法名、互操作名称。不得用匈牙利命名法，不得出现连续两个下划线。 | 下划线保留给字段前缀与编译器生成名。 | [FDG 通用命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/general-naming-conventions#word-choice) | CA1707（仅对外可见）：warning；IDE1006（非公共）：suggestion → warning |
| N-03 | MUST | 缩写：两字母首字母缩写全大写（`IO`、`UI`、`IP`、`DB`、`OB`、`FB`、`FC`），三字母及以上按单词处理（`Xml`、`Json`、`Plc`、`Hmi`、`Tia`、`Mcp`、`Udt`、`Scl`、`Vci`、`Opc`）。按 FDG 常用词表写作：`Id`、`Ok`、`FileName`、`UserName`、`LogOn`、`Metadata`、`Endpoint`、`Callback`、`Canceled`。**[P] 例外**：直接映射 Siemens Openness 类型或成员的标识符沿用厂商拼写（如 `IoSystem`、`IoConnector`、`GlobalDB`、`InstanceDB`）；已冻结的线上名称（如 V4 `ErrorCode.IoFailed`、`ErrorCode.Cancelled`）不改。 | 缩写规则可预测；厂商拼写让 Openness 映射代码可直接对照 API 文档。 | [FDG 大小写](https://learn.microsoft.com/dotnet/standard/design-guidelines/capitalization-conventions#capitalizing-compound-words-and-common-terms)；[FDG 缩写](https://learn.microsoft.com/dotnet/standard/design-guidelines/general-naming-conventions#using-abbreviations-and-acronyms) | 无内置规则（评审） |
| N-04 | MUST | 接口以 `I` 开头；泛型类型参数为 `T` 或 `T` 开头的描述性名称。 | 区分接口与类、类型参数与类型。 | [FDG 类型命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-classes-structs-and-interfaces)；[identifier names](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names#type-parameter-naming-guidelines) | CA1715：warning；IDE1006：warning |
| N-05 | MUST | 后缀：特性 `Attribute`、异常 `Exception`、事件参数 `EventArgs`、事件处理委托 `EventHandler`、集合 `Collection`/`Dictionary`；MUST NOT 使用 `Enum`、`Flags`、`Ex`、`Impl` 一类的错误后缀；新版本 API 用数字后缀（迁移期的 `XxxV4` 合规），不用 `Ex`。 | 名称表明类别，便于检索和映射。 | [FDG 类型命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-classes-structs-and-interfaces)；[FDG 新版本 API 命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/general-naming-conventions#naming-new-versions-of-existing-apis)；[异常最佳实践](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#end-exception-class-names-with-exception) | CA1710：suggestion → warning；CA1711：suggestion |
| N-06 | MUST | 普通枚举用单数名，标志枚举用复数名；标志枚举的零值命名为 `None`。 | 读法与语义一致。 | [FDG 枚举设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/enum) | CA1714、CA1717、CA1008：suggestion |
| N-07 | MUST | 新代码的命名空间为 `TiaMcp.<组件>[.<功能>]`，与程序集根命名空间加文件夹路径一致；MUST NOT 新增 `TiaMcpServer.*`、`TiaMcpConfigurator` 或在 `src/Shared` 中新增 `TiaOpenness.Shared`。**[P] 例外**：工作台源码（`src/Studio`）保留产品命名空间 `TiaOpenness.*`；其余代码中的 `TiaMcpServer.*`、`TiaOpenness.Shared`、`TiaMcpConfigurator` 在 4.0 后迁到 `TiaMcp.*`（维护者 2026-10-11 决定，偏差清单 CS-30）。不得出现与类型同名的命名空间，不得新增 `Helper`、`Node`、`Message` 一类泛名类型。 | FDG 要求稳定、与版本无关的产品名作为第二级；本项目无公司前缀（开源项目），以产品名 `TiaMcp` 作为第一级是项目取舍。 | [FDG 命名空间](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-namespaces)；[identifier names](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names#naming-conventions) | IDE0130：silent（链接源码和多根命名空间会误报）；根前缀由项目检查（建议新增） |
| N-08 | MUST | 字段：`private`/`internal` 实例字段 `_camelCase`；可变静态字段 `s_camelCase`；`[ThreadStatic]` 字段 `t_camelCase`；常量 PascalCase。**[P] 取舍**：保存不可变或“常量性”对象（`Regex`、冻结集合、序列化选项、只读表）的 `static readonly` 字段 MAY 使用 PascalCase。公共字段只用于 `static readonly` 预定义实例与常量（见 M-01）。 | `_`/`s_` 区分作用域，避免构造函数中 `this.x = x`；`static readonly` 常量性字段与 `const` 同样读作值，沿用现有 650 处写法。 | [identifier names：camel case](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names#camel-case)；runtime 第 3 条；[FDG 字段命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-type-members#names-of-fields)（只约束公共字段） | IDE1006：suggestion → warning（CS-01/CS-02 清理后） |
| N-09 | SHOULD | 方法名为动词或动词短语；属性名为名词、名词短语或形容词，集合属性用复数；布尔属性用肯定式，`Is`/`Can`/`Has` 仅在增益时使用；事件用动词时态区分前后（`Closing`/`Closed`），不用 `Before`/`After`。 | 名称表达行为与数据的区别。 | [FDG 成员命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-type-members) | 无 |
| N-10 | MUST | 返回可等待类型（`Task`、`Task<T>`、`ValueTask`、`ValueTask<T>`）的方法以 `Async` 结尾；同步方法不得以 `Async` 结尾。**[P] 例外**：MCP 工具入口方法（由 `[McpServerTool(Name=...)]` 分派，从不被 C# 代码按名调用）SHOULD 遵守，但不强制。 | TAP 约定，调用方能看出需要 await。 | [TAP 命名](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#naming-parameters-and-return-types) | IDE1006（只能覆盖带 `async` 修饰符的方法）：suggestion → warning |
| N-11 | MUST | 取消参数命名为 `cancellationToken` 并放在最后；进度参数为 `IProgress<T> progress`。重写基类方法时沿用基类参数名（如 `BackgroundService.ExecuteAsync(CancellationToken stoppingToken)`）。 | TAP 约定；参数名一致利于命名实参。 | [TAP 取消](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#cancellation-optional)；[FDG 参数设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design) | CA1068（位置）：warning；CA1725（重写参数名）：suggestion；名称本身无内置规则 |
| N-12 | MUST [P] | MCP 工具入口方法必须显式写 `[McpServerTool(Name = "...")]`；工具方法的 C# 参数名、类型与默认值就是线上参数，只能随 V4 契约基线（`Snapshot-ToolContracts`）变化。新增 MCP 可见文本一律英文。 | 工具名与 C# 名解耦后，C# 重命名不影响客户端；参数则不然。 | [tool-development](../tool-development.md)；[response-and-errors](../response-and-errors.md) | 项目检查：工具契约快照、`ratchet -Kind mcp-text` |
| N-13 | SHOULD | 标识符不只靠大小写区分，不与常见语言关键字冲突，不以类型名作标识符（`GetInt`）。 | 跨语言可用性、可读性。 | [FDG 大小写：大小写敏感](https://learn.microsoft.com/dotnet/standard/design-guidelines/capitalization-conventions#case-sensitivity)；[FDG 通用命名](https://learn.microsoft.com/dotnet/standard/design-guidelines/general-naming-conventions#avoiding-language-specific-names) | CA1708、CA1716、CA1720：suggestion |
| N-14 | SHOULD [P] | 文件以其主要类型命名；`partial` 分部文件命名为 `Type.Part.cs`；一个文件一个顶级类型。允许的例外：同一契约的线上 DTO 族集中在 `*Contracts.cs`；只服务于主类型的小型 `private`/`file` 辅助类型；XAML 代码隐藏。 | 文件导航与评审；与仓库现有 `McpServer.*.cs`、`Portal.*.cs` 分部命名一致。Microsoft 来源没有逐文件规则，这是项目规则。 | runtime 第 15 条（字段在类型顶部）及 [repository-layout](../repository-layout.md) | 无内置规则（StyleCop SA1402/SA1649 属于不允许的新包）；可在 release tool 中加只收紧的检查 |

---

## 3 类型设计

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| T-01 | MUST | 类型默认 `internal`；只有被其他程序集按设计使用时才 `public`。B 级应用程序集只为框架要求（WPF 绑定与 XAML、MCP SDK、反射夹具）保留 `public`。`InternalsVisibleTo` 只授予测试程序集和文档化的宿主配对。 | 公共表面越小，重构越安全。 | [FDG 类型设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/type)；[友元程序集](https://learn.microsoft.com/dotnet/standard/assembly/friend) | 无 |
| T-02 | MUST | `internal`/`private` 类除非需要派生，必须 `sealed` 或 `static`。 | 明确的扩展意图；也有利于 JIT 去虚化。 | runtime 第 19 条 | CA1852：suggestion → warning（有 `InternalsVisibleTo` 的程序集中该规则不报告） |
| T-03 | MUST [P] | A 级程序集中的公共 DTO、异常、特性、值对象 `sealed`。FDG 建议没有理由不要密封类，但本仓库没有第三方派生场景，项目取舍为“默认密封，按设计开放”。 | 防止无意的继承契约。 | [FDG 密封](https://learn.microsoft.com/dotnet/standard/design-guidelines/sealing)（项目取舍） | 无内置规则（CA1852 只覆盖 internal） |
| T-04 | MUST | 结构只用于小型、逻辑上单值、不可变的类型，声明为 `readonly struct`，实现 `IEquatable<T>`；MUST NOT 定义可变值类型。 | 值语义与复制语义一致。 | [FDG 类与结构的选择](https://learn.microsoft.com/dotnet/standard/design-guidelines/choosing-between-class-and-struct)；[FDG 结构设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/struct) | IDE0250：suggestion；CA1815：suggestion |
| T-05 | SHOULD | 静态类只放无状态的算法或扩展方法；避免 `Helper`/`Utils`/`Manager` 一类杂物桶。 | 聚合度。 | [FDG 静态类](https://learn.microsoft.com/dotnet/standard/design-guidelines/static-class) | CA1052：suggestion |
| T-06 | MUST | 枚举用于封闭集合（V4 `ErrorCode`、`Outcome` 等）；开放集合（发布键、TIA 版本、对象名）用字符串或类型化值对象。普通枚举提供零值；底层类型 `int`；标志枚举用 2 的幂、加 `[Flags]`、零值 `None`；不加保留值、哨兵值。 | 防止无效值与兼容问题。 | [FDG 枚举设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/enum) | CA1008、CA1027、CA1028、CA2217：suggestion |
| T-07 | MUST NOT | 用公共嵌套类型做逻辑分组；在接口中嵌套类型。嵌套类型只用于外层类型的实现细节。 | 嵌套类型与外层强耦合，命名和使用都不便。 | [FDG 嵌套类型](https://learn.microsoft.com/dotnet/standard/design-guidelines/nested-types) | CA1034：suggestion → warning（A 级） |
| T-08 | MUST | 跨版本边界用接口（`IOpennessAdapter` 及各 facet）；扩展面不可用时为 `null`（现有做法）。每个抽象至少有一个实现、一个使用方和测试。面向 net48 的程序集不得使用默认接口成员。给已有 facet 接口增加成员时，八个适配器与所有宿主同一提交内实现 [P]。 | 抽象必须被验证；net48 运行时不支持默认接口成员。 | [FDG 抽象](https://learn.microsoft.com/dotnet/standard/design-guidelines/abstractions-abstract-types-and-interfaces)；[FDG 接口设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/interface)；[adapter-merge](../adapter-merge.md) | 编译器（CS8701） |
| T-09 | SHOULD | 值对象与 V4 契约类型不可变：只读属性 + 构造函数校验（范例 [`src/Logic/V4/Envelope.cs`](../../../src/Logic/V4/Envelope.cs)、[`Error.cs`](../../../src/Logic/V4/Error.cs)）。线上 DTO 可按序列化器需要保留 setter（U-01）。 | 不变量集中在构造处。 | [FDG 属性设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/property) | 无 |
| T-10 | SHOULD NOT [P] | 新增 `partial` 分部来扩大已有的大类型（`Portal` 53 个分部声明、`PlcFoundationEngine` 49 个）。`partial` 用于 XAML、生成代码和文档化的拆分步骤。 | 引擎拆分设计的目标是领域服务，而不是更多分部。 | [engine-decomposition](../engine-decomposition.md) | 无 |
| T-11 | MUST NOT | 在两个程序集中重复定义同一 DTO、枚举或异常（例如 Studio DTO 的两份定义）。共享类型放在 A 级契约程序集中。 | 同一技术内的类型名冲突与双份维护。 | [FDG 命名空间：类型名冲突](https://learn.microsoft.com/dotnet/standard/design-guidelines/names-of-namespaces#namespaces-and-type-name-conflicts) | 无 |
| T-12 | MUST NOT | 公共基类型以 `Base` 结尾；抽象类型提供公共构造函数。 | 名称描述职责而非实现层次。 | [FDG 抽象基类](https://learn.microsoft.com/dotnet/standard/design-guidelines/base-classes-for-implementing-abstractions)；[FDG 抽象类](https://learn.microsoft.com/dotnet/standard/design-guidelines/abstract-class) | CA1012：suggestion |

---

## 4 成员设计

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| M-01 | MUST NOT | 在公共类型上提供 `public`/`protected` 实例字段。用属性。`internal` 类型上的数据载体 SHOULD 用属性或 `record`；`public` 字段只允许出现在私有嵌套的解析/状态容器中。 | 封装；字段改属性会改变二进制契约和序列化行为（STJ 默认忽略字段）。 | [FDG 字段设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/field)；runtime 第 3 条 | CA1051：suggestion → warning |
| M-02 | MUST NOT | 用 `public static readonly` 字段暴露可变实例（数组、`List<T>`、字典）。用 `IReadOnlyList<T>`、`ReadOnlyCollection<T>` 或在 net10 专用代码中用 `FrozenSet`/`ImmutableArray`。 | `readonly` 只防替换不防修改；共享静态数组可被任何调用方改写。 | [FDG 字段设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/field)；[FDG 数组](https://learn.microsoft.com/dotnet/standard/design-guidelines/arrays) | 无 |
| M-03 | MUST | 属性 getter 廉价、无副作用；MUST NOT 从 getter 抛异常（索引器除外）；不提供只写属性或 setter 可见性高于 getter；属性不得每次返回新数组副本。 | 属性被当作字段使用。 | [FDG 属性设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/property) | CA1065：warning；CA1044：suggestion；CA1819：suggestion → warning（A 级，DTO 例外） |
| M-04 | MUST | 校验公共、受保护和显式实现成员的参数：空值抛 `ArgumentNullException`，其他抛 `ArgumentException` 及其子类，并设置 `ParamName`（E-03）。校验可以在下层私有方法中完成，但整个公共表面都要覆盖。 | 早失败，错误可定位。 | [FDG 参数设计：校验参数](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design#validating-arguments) | CA1510–CA1513（仅 net8+ 目标）：suggestion；CA1062：none（与可空分析重复） |
| M-05 | SHOULD | 新的公共 API 有两个及以上布尔参数时改用枚举或请求对象。**[P] 例外**：MCP 工具参数与镜像它们的契约 facet（`dryRun`、`confirm`、`overwrite`、`preservePath` 等）属于线上形状，保持布尔。 | 调用点可读，可扩展到第三个值。 | [FDG 参数设计：枚举与布尔](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design#choosing-between-enum-and-boolean-parameters) | 无 |
| M-06 | SHOULD NOT | 使用 `out`/`ref` 参数，Try 模式（`bool TryX(..., out T value)`）除外；`out` 参数放在最后；不按引用传递引用类型。 | 多返回值用元组或结果类型更清楚。 | [FDG 参数设计：参数传递](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design#parameter-passing) | CA1021、CA1045：none（评审） |
| M-07 | MUST [P] | 默认参数：MCP 工具方法 MUST 用默认参数表达可选的 JSON 参数（线上契约）；`internal`/`private` 方法 MAY 使用；A 级接口的新成员 SHOULD NOT 带默认值（调用方编译时取接口上的默认值，实现上的默认值被忽略，易产生分歧），改用重载或请求对象。FDG 主张用重载代替默认参数（理由是 CLS 兼容），本仓库无跨语言调用方，故作上述取舍。 | 平衡线上契约与接口演进。 | [FDG 成员重载](https://learn.microsoft.com/dotnet/standard/design-guidelines/member-overloading)（项目取舍） | 无 |
| M-08 | MUST | 重载：同义参数同名、同序；只把最长重载设为 virtual；不以 `ref`/`out` 区分重载；可选参数允许传 `null`。重写或实现接口时参数名与声明一致。 | 可预测的重载解析和命名实参。 | [FDG 成员重载](https://learn.microsoft.com/dotnet/standard/design-guidelines/member-overloading)；[FDG 参数设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design) | CA1725：suggestion |
| M-09 | MUST | 公共事件使用 `EventHandler`/`EventHandler<TEventArgs>`，参数 `(object sender, TEventArgs e)`；非 sealed 类用 `protected virtual OnXxx(e)` 引发；不传 `null` 作为非静态事件的 sender 或事件数据。内部回调 MAY 使用 `Action`/`Func` 委托（C# 约定）。 | 与 BCL 和 WPF 一致。 | [FDG 事件设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/event)；[C# 约定：委托](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#delegates) | CA1003：suggestion |
| M-10 | MUST | 构造函数只做最少工作，不调用虚成员；参数名与对应属性同名（camelCase）。主构造函数参数 camelCase（record 为 PascalCase）。 | 对象在构造期间处于部分初始化状态。 | [FDG 构造函数设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/constructor)；[identifier names：主构造函数](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names#primary-constructor-parameters) | CA2214：warning |
| M-11 | MUST | 相等性：重写 `Equals` 必须同时重写 `GetHashCode` 并实现 `IEquatable<T>`；运算符 `==`/`!=` 成对重载且语义与 `Equals` 一致；可变引用类型避免重载相等运算符。 | 哈希集合正确性。 | [FDG 相等运算符](https://learn.microsoft.com/dotnet/standard/design-guidelines/equality-operators)；[FDG 运算符重载](https://learn.microsoft.com/dotnet/standard/design-guidelines/operator-overloads) | 编译器 CS0659/CS0661；CA1815、CA2231：suggestion |
| M-12 | MUST | 参数名、成员名的字符串用 `nameof(...)`。 | 重命名安全。 | runtime 第 14 条 | CA1507：warning |
| M-13 | SHOULD | 扩展方法放在名为 `XxxExtensions` 的静态类中，不扩展 `object`，不用来伪装成实例 API 的核心成员。 | 可发现性。 | [FDG 扩展方法](https://learn.microsoft.com/dotnet/standard/design-guidelines/extension-methods) | 无 |
| M-14 | SHOULD | 静态成员通过声明类型访问（`ClassName.Member`），不通过派生类型限定。 | 可读，避免未来名称遮蔽。 | [C# 约定：静态成员](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#static-members) | 无 |

---

## 5 可扩展性

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| X-01 | MUST | `virtual`、`protected` 成员只在有明确扩展场景时引入；`protected` 成员按公共契约对待；sealed 类型上不声明 `protected`/`virtual` 成员。 | 每个扩展点都是需要测试和维护的契约。 | [FDG 虚成员](https://learn.microsoft.com/dotnet/standard/design-guidelines/virtual-members)；[FDG 受保护成员](https://learn.microsoft.com/dotnet/standard/design-guidelines/protected-members)；[FDG 密封](https://learn.microsoft.com/dotnet/standard/design-guidelines/sealing) | 编译器 CS0549（sealed 中的 virtual）；CA1852 |
| X-02 | SHOULD | 通知类回调用事件，组合类回调用委托参数（`Func`/`Action`）；回调在调用方线程同步执行时在文档注释中说明（与 TH-07 一致）。 | 回调执行上下文是契约的一部分。 | [FDG 事件与回调](https://learn.microsoft.com/dotnet/standard/design-guidelines/events-and-callbacks) | 无 |
| X-03 | MUST | 设计用于派生的基类提供 `protected` 构造函数，不提供 `public` 构造函数（抽象类）。 | 防止误实例化。 | [FDG 抽象类](https://learn.microsoft.com/dotnet/standard/design-guidelines/abstract-class) | CA1012：suggestion |
| X-04 | MUST [P] | 宿主差异通过显式选项或策略对象注入共享原语（`ThreadPolicy`、行为策略），不通过子类重写原生调用序列。 | 适配器合并原则：统一位置与契约，不统一可观察行为。 | [FDG 可扩展性设计](https://learn.microsoft.com/dotnet/standard/design-guidelines/designing-for-extensibility)；[adapter-merge：目标契约](../adapter-merge.md#目标契约) | 无 |

---

## 6 异常与错误报告

### 6.1 分层模型（项目化）

| 层 | 位置 | 错误表达 |
|---|---|---|
| L1 组件内部 | 领域服务、适配器、Logic、WorkerChannel | 按 FDG 抛异常；可预期的输入拒绝 MAY 用结果类型（如 V4 `InputContract.Validate` 返回带 `Error` 的结果）。 |
| L2 工具边界 | `*ToolContract.Run/RunAsync`、`FoundationV4Tool`、`HostFailurePolicy.Classify`、`HostBehavior` | 把异常与原生结果证据转换为 V4 信封（`ok=false`、`error.code`、`meta.outcome`/`execution`/`requiresSessionReset`）。异常类型 → 错误码的映射只在这里维护。 |
| L3 进程通道 | WorkerChannel 协议 2、Studio 桥接进程 | 错误作为数据：`error.data = {outcome, evidence}`；通道故障使会话失效（poison），不重放。 |

对 MCP 客户端：V4 工具不以抛异常的方式报告业务失败；SDK 层的 `McpException` 只保留在冻结的旧族（F2、F5）中，直到 4.0 清理（[response-and-errors](../response-and-errors.md)）。

### 6.2 规则

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| E-01 | MUST | 组件内部以异常报告执行失败；新的内部 API 不返回错误码或 `bool + LastError` 组合。例外：Try 模式；L1 校验边界的结果类型；V4 信封本身（它是线上协议，不是内部 API）。 | 失败不能被静默忽略。 | [FDG 抛出异常](https://learn.microsoft.com/dotnet/standard/design-guidelines/exception-throwing) | 无 |
| E-02 | MUST [P] | 异常类型按语义选择，并只在 `HostFailurePolicy`/`HostBehavior` 中映射到 V4：参数错误 → `ArgumentException` 族（分派前 → `INVALID_ARGUMENT`，`details.parameter` 取自 `ParamName`）；对象状态不允许 → `InvalidOperationException`；分派前的安全拒绝 → `AdapterPreconditionException`；取消 → `OperationCanceledException`（仅在分派前或只读调用中归为取消）；IO → `IOException` 族；协议/数据格式 → `InvalidDataException`/`JsonException`。新增异常类型 MUST 同时在映射处登记并加测试。 | 错误语义集中、可测试。 | [FDG 标准异常类型](https://learn.microsoft.com/dotnet/standard/design-guidelines/using-standard-exception-types)；[异常最佳实践：预定义类型](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#use-predefined-exception-types)；`src/Shared/HostFailurePolicy.cs` | 无 |
| E-03 | MUST | 抛 `ArgumentException` 及子类时设置 `ParamName`（用 `nameof`）。V4 参数错误必须填写 `details.parameter`，而 `Message` 文本保持不变：进入线上文本的路径用不追加参数名的项目辅助方法设置参数名（维护者 2026-10-11 决定，CS-39）。注意：设置后 `Message` 会追加参数名（.NET Framework 追加换行和 `Parameter name: x`，.NET 追加 ` (Parameter 'x')`）；凡是 `Message` 进入线上文本的地方（旧族消息、`evidence`、`TryCanonicalizeEnumArgument` 解析的拒绝文本），转换前须确认快照差异。 | V4 错误详情能指出参数；FDG 明确要求设置。 | [FDG 标准异常类型](https://learn.microsoft.com/dotnet/standard/design-guidelines/using-standard-exception-types#argumentexception-argumentnullexception-and-argumentoutofrangeexception) | CA2208：warning（只发现错位或无效的参数名，不发现缺失） |
| E-04 | MUST NOT | 抛出 `Exception`、`SystemException`、`ApplicationException`、`NullReferenceException`、`IndexOutOfRangeException`、`AccessViolationException`、`OutOfMemoryException`、`StackOverflowException`、`COMException`、`SEHException`、`ExecutionEngineException`；MUST NOT 从 `ApplicationException` 派生。 | 保留类型表示运行时错误或缺陷。 | [FDG 标准异常类型](https://learn.microsoft.com/dotnet/standard/design-guidelines/using-standard-exception-types) | CA2201：warning（产品）/ suggestion（测试）；CA1058：warning |
| E-05 | MUST | 自定义异常只在调用方或映射处需要区分时引入；名称以 `Exception` 结尾；`sealed`；基类反映语义（参数 → `ArgumentException`，状态 → `InvalidOperationException`，IO → `IOException`）；附加数据用强类型属性。A 级公共异常 SHOULD 提供三个标准构造函数；携带必需数据（代码、outcome）的异常 MAY 只提供带必需数据的构造函数 [P 取舍]。新代码不再引入用于控制流的私有异常（`InputFailure`、`Rejected` 一类），改用结果类型；必须使用时为 `private sealed` 且以 `Exception` 结尾。 | 类型即契约；FDG 不鼓励以异常做正常控制流。 | [异常最佳实践：自定义异常](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#custom-exception-types)；[FDG 抛出异常](https://learn.microsoft.com/dotnet/standard/design-guidelines/exception-throwing) | CA1710：suggestion → warning；CA1032：suggestion；CA1064：none（允许非公共异常） |
| E-06 | MUST [P] | 只捕获能处理的具体异常。`catch (Exception)` 或 `catch { }` 只允许在边界（工具契约、worker 分派器、进程入口、UI 命令、清理/收尾），能加过滤器时加 `when`。每个吞掉异常的 `catch` 必须在 catch 行或块首标注 `/* swallow(<类别>): <原因> */`，类别取自封闭清单（cleanup / teardown / logging-failure / probe-optional / enumerate-optional / native-fallback / parse-fallback / env-probe / ui / fail-open-guard / privacy），需要时调用 `SwallowedExceptions.Note`。 | FDG 不鼓励捕获 `Exception`；本仓库以注释+棘轮逐个治理（当前基线 0）。 | [FDG 标准异常类型](https://learn.microsoft.com/dotnet/standard/design-guidelines/using-standard-exception-types#exception-and-systemexception)；[C# 约定：语言准则](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#language-guidelines)；[response-and-errors P2-03](../response-and-errors.md) | 项目棘轮 `ratchet -Kind swallowed`（error）；CA1031：none（与棘轮重复，见 §13.4） |
| E-07 | MUST | 在 catch 内重新抛出用 `throw;`；跨位置重新抛出用 `ExceptionDispatchInfo.Capture(...).Throw()`；包装时把原异常作为 `innerException`。 | 保留堆栈。 | [异常最佳实践：正确重新抛出](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#capture-and-rethrow-exceptions-properly) | CA2200：warning |
| E-08 | MUST NOT | 从 `finally`、异常过滤器、`Dispose`、`Equals`/`GetHashCode`/`ToString`、静态构造函数、相等运算符抛出异常。 | 这些位置的异常会掩盖原始错误或终止进程。 | [FDG 抛出异常](https://learn.microsoft.com/dotnet/standard/design-guidelines/exception-throwing)；[异常最佳实践](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#dont-raise-exceptions-from-unexpected-places) | CA1065、CA2219：warning |
| E-09 | MUST | 异常消息：英文完整句子，以句号结尾，不以参数值拼接调用方机密；`SwallowedExceptions.Note` 不读取 `ex.Message`；新代码不把 `ex.ToString()` 写入线上字段（`meta.error` 只有首行“类型: 消息”是兼容契约，决策 D1）。已冻结的旧族消息不润色 [P]。 | 可读、可诊断、隐私。 | [异常最佳实践：语法](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#use-proper-grammar)；[tool-development：文案语言](../tool-development.md) | 项目棘轮 `ratchet -Kind mcp-text`；CA1303：none（与“MCP 响应不做本地化”的决定冲突） |
| E-10 | MUST [P] | 原生调用已分派后的未类型化失败一律归为 `Unknown`（会话需复位），不得报告为干净失败，不得重试或重放；分派前的拒绝明确报告 `RejectedBeforeNative`。 | 写操作的实际结果无法从异常推断。 | [adapter-merge：Worker 协议](../adapter-merge.md#worker-协议)；`HostFailurePolicy.Classify` | 项目测试（worker-channel、HostFailurePolicy） |
| E-11 | SHOULD | 不把 `Exception.Data` 字典用作分类契约（如 `workerOutcomeUnknown`、`nativeResultEvidence`）；新代码用强类型属性或标记接口。 | 字符串键无编译期检查。 | [异常最佳实践：按需提供属性](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#provide-more-properties-as-needed) | 无 |
| E-12 | SHOULD | 频繁失败的路径（反射探测可选成员、解析回退）用 Tester-Doer 或 Try 模式，不靠异常。 | 异常开销与日志噪音。 | [FDG 异常与性能](https://learn.microsoft.com/dotnet/standard/design-guidelines/exceptions-and-performance) | 无 |
| E-13 | MUST | 返回 Task 的方法在进入异步部分前同步校验参数；其余异常存入返回的任务。 | 用法错误立即暴露。 | [异常最佳实践：同步抛出参数异常](https://learn.microsoft.com/dotnet/standard/exceptions/best-practices-for-exceptions#throw-argument-validation-exceptions-synchronously)；[TAP 异常](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#exceptions) | 无 |
| E-14 | MUST [P] | 状态不可恢复时使会话失效（poison/fault）并要求显式重连；`Environment.Exit`/`FailFast` 只在进程入口和文档化的故障注入夹具中使用。 | FDG 建议不安全时终止进程；本项目以会话为隔离单元，不连带结束 TIA 或其他客户端。 | [FDG 抛出异常](https://learn.microsoft.com/dotnet/standard/design-guidelines/exception-throwing)（项目取舍）；[runtime-layout](../runtime-layout.md) | 无 |
| E-15 | MUST [P] | 冻结的旧错误族（F1–F6）在 4.0 移除前不互相转换；新工具只走 V4 工具契约。 | 兼容先行。 | [tool-development：抛异常与返回失败](../tool-development.md#抛异常与返回失败) | 契约/响应快照 |

---

## 7 使用规范

### 7.1 集合与数组

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-01 | MUST | A 级公共 API 的签名不使用 `List<T>`、`Dictionary<,>`、`HashSet<T>`、`ArrayList`、`Hashtable`；只读结果用 `IReadOnlyList<T>`/`IReadOnlyCollection<T>`/`IReadOnlyDictionary<,>`/`ReadOnlyCollection<T>`，可写集合用 `Collection<T>`，参数用最少特化的类型（多为 `IEnumerable<T>`）。**[P] 例外**：线上 DTO 与生成模型（JSON 直接绑定的类型）MAY 使用数组、`List<T>` 与 setter；这类类型不含行为，只放在契约/模型文件中。 | 公共 API 不承诺实现结构；DTO 形状由序列化器决定。 | [FDG 集合](https://learn.microsoft.com/dotnet/standard/design-guidelines/guidelines-for-collections) | CA1002：suggestion → warning（A 级，排除 DTO 文件） |
| U-02 | MUST NOT | 从返回集合的方法或集合属性返回 `null`；返回空集合或 `Array.Empty<T>()`。线上 JSON 中 `null` 与 `[]` 的区别由契约决定，不由此规则改变。 | 调用方无需判空。 | [FDG 集合：属性与返回值](https://learn.microsoft.com/dotnet/standard/design-guidelines/guidelines-for-collections#collection-properties-and-return-values) | CA1825：suggestion |
| U-03 | MUST NOT | 在非 DTO 类型上提供可设置的集合属性；属性每次返回数组副本。 | 防止整体替换和隐藏开销。 | [FDG 集合](https://learn.microsoft.com/dotnet/standard/design-guidelines/guidelines-for-collections#collection-properties-and-return-values) | CA2227：suggestion；CA1819 见 M-03 |
| U-04 | MUST [P] | 织入程序集（引擎 EXE、适配器）中，响应构造器和日志辅助方法的参数不含 `IEnumerable`；向 Siemens 程序集传递可枚举对象的代码只能留在织入程序集内。 | native-call-weaver 把“向外部程序集传 `IEnumerable`”插桩为 enumeration-input 类别，移动或改签名会改变覆盖。 | [response-and-errors：前置事实 3](../response-and-errors.md#前置事实)；[adapter-merge](../adapter-merge.md) | weave 清单比较（项目检查） |
| U-05 | MUST | 以字符串为键的字典/集合显式指定比较器（标识符、路径用 `StringComparer.Ordinal` 或 `OrdinalIgnoreCase`）。 | 默认比较器语义不可见。 | [字符串最佳实践](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-strings) | 无 |

### 7.2 字符串与全球化

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-06 | MUST | `StartsWith`/`EndsWith`/`IndexOf(string)`/`LastIndexOf(string)`/`Compare`/`CompareTo` 显式传 `StringComparison`；标识符、路径、协议令牌、XML 名称用 `Ordinal`/`OrdinalIgnoreCase`。 | 无参重载是文化敏感的；net48（NLS）与 net10（ICU）可能不同。 | [字符串最佳实践](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-strings)；[ICU 全球化](https://learn.microsoft.com/dotnet/core/extensions/globalization-icu) | CA1310：warning；CA1307、CA1309：suggestion |
| U-07 | MUST | 机器可读数据的解析与格式化使用 `CultureInfo.InvariantCulture`；大小写转换用 `ToUpperInvariant`/`ToLowerInvariant`。 | 输出与运行机器区域设置无关。 | [字符串最佳实践](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-strings) | CA1305、CA1311：warning；CA1304：suggestion |
| U-08 | MUST [P] | 源码中的中文只能作为数据出现（TIA 原文、工程对象名、示例名、Studio 本地化资源以外的已审查数据），受 MCP 中文棘轮约束。runtime 风格建议非 ASCII 字符写成 `\uXXXX`，项目不采纳：源码统一为无 BOM UTF-8，转义会让已审查的数据 allowlist 难以人工核对。 | 可审查性。 | runtime 第 16 条（项目不采纳）；[tool-development：MCP 文案语言](../tool-development.md#mcp-文案语言) | 项目棘轮 `ratchet -Kind mcp-text` |
| U-09 | SHOULD | 短字符串拼接用插值；循环内构造大文本用 `StringBuilder`；多行文本 MAY 用原始字符串字面量。 | 可读与性能。 | [C# 约定：字符串数据](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#string-data) | 无 |

### 7.3 序列化

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-10 | MUST | 新代码使用 System.Text.Json；不新增 Newtonsoft.Json 使用；MUST NOT 使用 `BinaryFormatter`、`NetDataContractSerializer` 等不安全序列化器，不给新类型加 `[Serializable]`。 | 单一序列化边界（P2-04）；不安全反序列化。 | [STJ 概述](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/overview)；[BinaryFormatter 安全指南](https://learn.microsoft.com/dotnet/standard/serialization/binaryformatter-security-guide)；[FDG 序列化](https://learn.microsoft.com/dotnet/standard/design-guidelines/serialization)（已过时，按此条执行） | CA2300–CA2302：warning；SYSLIB0011（net9+ 为错误） |
| U-11 | SHOULD | `JsonSerializerOptions` 缓存为 `static readonly` 并复用，不在每次调用中新建。 | 每个新实例会重建元数据缓存。 | [STJ 选项实例复用](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/configure-options) | CA1869：suggestion → warning |
| U-12 | MUST [P] | 响应 POCO、V4 类型和 worker DTO 的成员顺序、命名策略、null 处理、数值 CLR 类型、时间戳编码是线上契约；只能随契约/响应快照（格式 3 原始哈希、`TIA_MCP_UPDATE_GOLDEN=1` 等正式工具）一起变化。字段改属性、改 `IncludeFields`、换序列化选项都属于此类变化。 | 快照证明兼容。 | [tool-development](../tool-development.md)；[response-and-errors](../response-and-errors.md) | 契约/响应快照 |
| U-13 | SHOULD [P] | V4 线上类型显式写 `[JsonPropertyOrder]`，只读属性 + 构造函数校验（范例 `src/Logic/V4/Envelope.cs`）。 | 顺序不依赖声明顺序或反射顺序。 | 项目范例 | 无 |

### 7.4 XML

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-14 | MUST | 处理工程导出、用户文件等不受信任 XML 时禁止 DTD 处理并不解析外部实体（`DtdProcessing.Prohibit`，.NET Framework 上 `XmlResolver = null`）；新代码优先 LINQ to XML 或 `XmlReader`；公共 API 不以 `XmlDocument`/`XmlNode` 作参数或返回值。 | XXE 与实体扩展攻击；FDG 推荐流式或 XNode 类型。 | [FDG System.Xml 用法](https://learn.microsoft.com/dotnet/standard/design-guidelines/system-xml-usage)；[CA3075](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca3075) | CA3075、CA3077：warning |

### 7.5 IDisposable 与资源

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-15 | MUST | 拥有可释放对象的类型实现 `IDisposable`；sealed 类型实现简单、幂等的 `Dispose()`；非 sealed 类型实现 `Dispose()` + `protected virtual Dispose(bool)`；只有直接持有非托管句柄才写终结器，优先 `SafeHandle`；`Dispose` 不抛异常；释放后的成员抛 `ObjectDisposedException`。 | 确定性释放。 | [实现 Dispose](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-dispose)；[FDG Dispose 模式](https://learn.microsoft.com/dotnet/standard/design-guidelines/dispose-pattern) | CA1063、CA1001：warning；CA1816、CA2213：suggestion；CA2000：none（误报多） |
| U-16 | MUST NOT [P] | MCP 工具类与领域服务单例实现 `IDisposable`（SDK 每次调用后会释放目标对象）。 | 单例被提前释放。 | [engine-decomposition：生命周期](../engine-decomposition.md#目标结构) | 无 |
| U-17 | MUST [P] | `TiaPortal`、`Project` 等 Openness 对象只由拥有它的会话在其所属线程上释放；进程租约与断开语义不变（断开不保存、不关闭、不结束进程）。 | 原生语义不变。 | [unified-version-framework](../unified-version-framework.md#foundation-session-and-engineering-contracts) | 无 |
| U-18 | SHOULD | 用 `using` 声明或语句代替只调用 `Dispose` 的 `try/finally`。 | 简洁、异常安全。 | [C# 约定：try-catch 与 using](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#try-catch-and-using-statements-in-exception-handling) | IDE0063：suggestion |

### 7.6 异步

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-19 | MUST NOT | 在可能运行于 UI 线程或同步上下文的代码中同步等待异步操作（`.Result`、`.Wait()`、`GetAwaiter().GetResult()`）。允许：进程入口与 CLI 主流程、专用所属线程上包装同步原生 API 的调用、SDK 同步回调；这些位置写注释说明。 | 死锁与线程池饥饿。 | [C# 约定：语言准则](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#language-guidelines)；[TAP](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#target-environment) | CA1849：suggestion（只覆盖 async 方法内部；VSTHRD 规则属于不允许的新包） |
| U-20 | SHOULD | A 级库与宿主中不触碰 UI 的 `await` 使用 `ConfigureAwait(false)`；WPF UI 代码在回到 UI 前不使用。 | 避免捕获上下文。 | [C# 约定：语言准则](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#language-guidelines)；[CA2007](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2007) | CA2007：none（应用）/ suggestion（WorkerChannel、Logic、Contracts） |
| U-21 | MUST NOT | `async void`，事件处理器除外；`ICommand.Execute` 一类必须为 void 的包装必须捕获并报告异常。 | 未观察的异常会结束进程。 | [TAP](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap) | 无内置规则（VSTHRD100 属于新包） |
| U-22 | SHOULD | 把收到的 `CancellationToken` 传给支持取消的被调方法。 | 取消可传递。 | [TAP 取消](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#cancellation-optional) | CA2016：suggestion |

### 7.7 时间、静态状态与其他

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| U-23 | MUST | 线上、日志、持久化时间戳用 `DateTimeOffset.UtcNow`（V4 `Meta` 已如此）；时长用 `Stopwatch`；`DateTime.Now` 只用于面向用户的本地显示或文件名。冻结的旧 meta 时间戳编码在旧族移除前不改（棘轮 `timestamp_now_assignments` 只减不增）。 | 时区与夏令时无关。 | [DateTime 与 DateTimeOffset 的选择](https://learn.microsoft.com/dotnet/standard/datetime/choosing-between-datetime) | 项目棘轮 `ratchet -Kind envelopes`（只覆盖手写赋值） |
| U-24 | SHOULD NOT | 引入可变静态状态。必需的进程级配置钩子只在启动时设置一次并说明线程安全；可变静态字段按 N-08 命名 `s_`。 | 多会话宿主中的竞争与测试隔离。 | [托管线程最佳实践](https://learn.microsoft.com/dotnet/standard/threading/managed-threading-best-practices) | CA2211：warning（仅可见字段） |
| U-25 | MUST | 自定义特性以 `Attribute` 结尾、`sealed`、标注 `[AttributeUsage]`。 | 查找性能与正确使用。 | [FDG 特性](https://learn.microsoft.com/dotnet/standard/design-guidelines/attributes) | CA1018：warning；CA1813：suggestion |
| U-26 | MUST [P] | 版本条件编译只使用 [`TiaFeatures.props`](../../../src/Shared/TiaFeatures.props) 中的特性符号、SDK 框架符号（`NET`、`NETFRAMEWORK`、`NET48`）和文档化的构建角色符号；不新增临时版本宏。 | 单一特性表，由 `Check-TiaFeatures.py` 核对。 | [unified-version-framework：编译器特性表](../unified-version-framework.md#compiler-feature-table) | `Check-TiaFeatures.py` |
| U-27 | SHOULD | `HttpClient` 长期复用（或通过工厂），不在每次调用中新建并释放。 | 套接字耗尽与 DNS 刷新。 | [HttpClient 使用准则](https://learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines) | 无 |

---

## 8 可空引用类型

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| NR-01 | MUST | 所有新工程 `<Nullable>enable</Nullable>`；现有禁用或仅注解的工程列入偏差清单（CS-47）。文件级 `#nullable disable` 只用于链接进禁用工程的旧源码，并在同一行写明原因。 | 在编译期发现空引用。 | [可空引用类型](https://learn.microsoft.com/dotnet/csharp/nullable-references)；[迁移策略](https://learn.microsoft.com/dotnet/csharp/nullable-migration-strategies) | 编译器 CS86xx；目标：`WarningsAsErrors=nullable`（A 级先行） |
| NR-02 | SHOULD | 只在已证明非空的位置使用 `!`；DTO 不以 `= null!` 糊过初始化，改用构造函数、`required`（net48 需 polyfill）或非空默认值。 | `!` 关闭了分析。 | [可空引用类型](https://learn.microsoft.com/dotnet/csharp/nullable-references) | 无 |
| NR-03 | MUST | 多目标代码需要可空特性（`NotNullWhen`、`MemberNotNull` 等）时，在共享源码中提供 `internal` polyfill，一个程序集只编入一份。 | net48 BCL 没有这些特性。 | [可空静态分析特性](https://learn.microsoft.com/dotnet/csharp/language-reference/attributes/nullable-analysis) | 无 |
| NR-04 | MUST [P] | 可空注解不得改变线上形状：JSON 中“缺失”与 `null` 的区别由契约快照决定。 | 契约先行。 | U-12 | 契约快照 |

---

## 9 线程、STA/MTA 与 Openness 调用 [P]

Microsoft 基础：[托管与非托管线程（单元）](https://learn.microsoft.com/dotnet/standard/threading/managed-and-unmanaged-threading-in-windows)、[托管线程最佳实践](https://learn.microsoft.com/dotnet/standard/threading/managed-threading-best-practices)、[WPF 线程模型](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/threading-model)。以下规则来自 [AGENTS.md](../../../AGENTS.md)、[adapter-merge](../adapter-merge.md)、[unified-version-framework](../unified-version-framework.md) 与当前代码。

当前单元归属（以代码为准）：

| 宿主 | 所属线程 | 位置 |
|---|---|---|
| PLC worker，V14 SP1–V19 | 进程主线程，`[STAThread]` | `src/PlcWorker/Program.cs`，`PlcLifecyclePolicy.OwnerApartment` |
| PLC worker，V20/V21（P8-04j2 起随包发布并默认使用） | `Main` 中新建的专用 MTA 线程 | 同上 |
| 引擎兼容副本，V20/V21（不随包发布，只供仓库交叉检查，P8-04l 删除） | MTA（`[MTAThread]`），Foundation 适配器与引擎代码共用这一线程，原生调用串行；`VerifyBinding` 拒绝非 MTA 线程 | `src/Engine/Program.cs`、`Portal.Binding.cs` |
| Studio 桥接进程（迁移期遗留） | 进程 STA 主线程；`StudioThreadGuard` 拒绝跨线程与重入 | `src/Studio/Bridge/Program.cs`、`src/Adapters/Policy/StudioThreadGuard.cs` |

| ID | 级别 | 规则 | 理由 | 依据 | 工具 |
|---|---|---|---|---|---|
| TH-01 | MUST | 不改变 Openness 调用的顺序、参数和线程归属，除非任务明确要求；有改动时在报告中列出前后调用序列。 | 编译、模拟对象与传输测试都不能证明原生语义。 | AGENTS.md；[refactor-plan 约束 3](../refactor-plan.md#约束) | `Compare-NativeCallOrder.py`、weave 清单比较 |
| TH-02 | MUST | 原生调用前核对所属线程与单元；不符时抛异常并保证“未发起原生调用”（`RejectedBeforeNative`）。 | Openness 对象有线程亲和性；拒绝必须发生在原生入口之前才可安全报告。 | [托管与非托管线程](https://learn.microsoft.com/dotnet/standard/threading/managed-and-unmanaged-threading-in-windows)；`PlcLifecyclePolicy`、`Portal.VerifyBinding` | 项目测试 |
| TH-03 | MUST NOT | 适配器切换单元或封送调用：不用 `Task.Run`、`ConfigureAwait`、`Dispatcher`、`SynchronizationContext` 把原生调用移到其他线程；宿主以 `ThreadPolicy` 选择策略，适配器执行策略并拒绝重入。 | 线程归属是宿主契约，适配器只执行。 | [adapter-merge：目标契约](../adapter-merge.md#目标契约) | 无 |
| TH-04 | MUST | 原生调用开始后的超时与取消一律视为 outcome `Unknown`：会话失效，不重放、不自动重启；取消只在进入原生代码前和批量项之间检查；进度回调在所属线程同步发出，不延长调用预算。 | 无法中止进行中的原生调用，也无法判断其结果。 | [adapter-merge：Worker 协议](../adapter-merge.md#worker-协议) | worker-channel 测试 |
| TH-05 | MUST | 接触 Openness 对象的代码留在受织入的程序集中（引擎 EXE、适配器）；工具分派不得使用 `Delegate.CreateDelegate` 或表达式编译。 | 原生调用诊断只插桩这些程序集；调度审计拒绝动态委托。 | [engine-decomposition](../engine-decomposition.md#目标结构)；[repository-layout](../repository-layout.md) | 织入校验 |
| TH-06 | MUST | 原生句柄不跨契约或进程边界传递；对象用规范转义路径寻址。 | 句柄只在所属线程和进程内有效。 | [adapter-merge：目标契约](../adapter-merge.md#目标契约) | 契约程序集的引用禁止目标 |
| TH-07 | MUST | 每个 TIA 进程只由一个 MCP 会话持有 TIA 进程租约；共用同一 MCP 会话的参与方（AI 客户端与附着的工作台面板）写入前先检查会话状态。 | 第二个会话会与 TIA 进程租约冲突；同一会话的参与方共用一条 Openness 通道。 | [runtime-layout：TIA 进程租约](../runtime-layout.md) | 进程租约 |
| TH-08 | MUST | WPF：只在 UI 线程更新绑定对象；跨线程用 `Dispatcher`；UI 线程不阻塞等待 bridge 调用。 | WPF 对象有调度器亲和性，阻塞会冻结界面。 | [WPF 线程模型](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/threading-model) | 无 |
| TH-09 | MUST | 锁对象为 `private readonly object`（net10 专用代码 MAY 用 `System.Threading.Lock`）；不锁 `this`、`typeof(...)`、字符串或弱标识对象；跨 `await` 的互斥用 `SemaphoreSlim`。 | 公开可见的锁对象会被外部代码锁住造成死锁。 | [托管线程最佳实践](https://learn.microsoft.com/dotnet/standard/threading/managed-threading-best-practices) | CA2002：warning |

---

## 10 格式与布局

格式规则的共同理由：统一格式降低评审噪音，并能交给 `dotnet format` 自动执行。各条另有理由时写在规则中。

| ID | 级别 | 规则 | 依据 | 工具 |
|---|---|---|---|---|
| S-01 | MUST [P] | C# 源码为无 BOM 的 UTF-8、仓库内 LF；4 空格缩进，不用制表符；无行尾空白；文件以换行结尾。理由：生成器与快照逐字节比较，Windows 与 Ubuntu CI 必须得到相同字节。 | CLAUDE.md、AGENTS.md、`.gitattributes`；[C# 约定：样式](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#style-guidelines)；runtime 第 2、8 条 | editorconfig `charset`/`end_of_line`/`indent_style`/`trim_trailing_whitespace`；IDE0055；`RepositoryChecks` |
| S-02 | MUST | Allman 大括号：每个大括号独占一行。 | runtime 第 1 条；[C# 约定：样式](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#style-guidelines) | IDE0055（`csharp_new_line_before_open_brace = all`） |
| S-03 | SHOULD | 单语句 `if`：不使用同一行形式（`if (x) return;`）；省略大括号时语句体独占下一行，且同一 if/else 链所有分支都单行；任一分支用了大括号或跨行则全部加大括号。理由：条件与动作分行便于调试断点与差异审阅。 | runtime 第 18 条 | IDE2001（实验性）：suggestion；IDE0011（`when_multiline`）：suggestion |
| S-04 | SHOULD | 每行一条语句、一个声明。 | [C# 约定：布局](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#layout-conventions) | IDE0055（`csharp_preserve_single_line_statements = false`）；多声明无内置规则 |
| S-05 | SHOULD | 控制流关键字后加空格（`if (`、`for (`、`catch (`），括号内不加多余空格。 | runtime 第 8 条；VS 默认格式 | IDE0055 |
| S-06 | MUST | `using` 指令放在命名空间之外；`System*` 在前，按字母排序；不保留未使用的 `using`。理由：命名空间内的 `using` 按相对名称解析，依赖新增同名命名空间后可能悄然改变绑定。 | runtime 第 6 条；[C# 约定](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#place-the-using-directives-outside-the-namespace-declaration) | IDE0065：warning；IDE0055（排序）；IDE0005：suggestion（构建中需 `GenerateDocumentationFile`） |
| S-07 | MUST | 新文件使用 file-scoped 命名空间；既有块级命名空间文件只在整体重写时转换。 | [C# 约定](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#file-scoped-namespace-declarations) | IDE0161：suggestion |
| S-08 | SHOULD | `var` 只在类型从右侧显而易见时使用（`new`、强制转换、字面量、`as`）；`foreach` 循环变量写显式类型；LINQ 查询结果与匿名类型用 `var`；目标类型 `new()` 只在左侧写明类型时使用。理由：评审差异中看不到类型推断结果。 | runtime 第 10 条；[C# 约定：隐式类型局部变量](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#implicitly-typed-local-variables) | IDE0007/IDE0008：suggestion（不升级，见 CS-17） |
| S-09 | MUST | 用语言关键字（`string`、`int`、`object`）而非 BCL 类型名，类型引用与静态调用都如此；优先 `int` 而非无符号类型。 | runtime 第 11 条；[C# 约定](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#language-guidelines) | IDE0049：warning |
| S-10 | SHOULD | 不写不必要的 `this.`。 | runtime 第 4 条 | IDE0003：suggestion |
| S-11 | MUST | 总是写访问修饰符且放在最前；修饰符顺序固定（`static readonly`）。 | runtime 第 3、5 条 | IDE0040：warning；IDE0036：warning |
| S-12 | SHOULD [P] | 行长：新代码每行不超过 120 字符，字符串数据、`[Description(...)]` 文本与 URL 除外；现有代码不为行长重新排版（维护者 2026-10-11 决定，CS-19）。Microsoft 来源无通用行宽（docs 的 65 字符只针对网页示例）。理由：并排差异与终端评审可读。 | 项目规则 | 无内置规则（`max_line_length` 不被 Roslyn 执行）；4.0 前不加行长检查，以后如需强制只加只收紧的棘轮 |
| S-13 | SHOULD | 注释：独占一行，首字母大写，句号结尾，`//` 后一个空格；A 级公共 API 写 XML 文档注释。**[P]** 吞异常标注必须在 catch 行或块首（E-06）；注释不写产品版本号、维护者归属、旧阶段叙事和迁移墓碑；改写时保留原注释语言。 | [C# 约定：注释](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#comment-style)；[runtime-layout：注释](../runtime-layout.md) | 项目棘轮 `ratchet -Kind comments`；无内置注释格式规则 |
| S-14 | SHOULD | 字段声明在类型顶部；成员之间至少一个空行，不出现连续两个空行。 | runtime 第 7、15 条；[C# 约定：布局](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#layout-conventions) | IDE2000（实验性）：suggestion |
| S-15 | SHOULD NOT [P] | 使用 `#region`；用类型拆分或分部文件组织代码。理由：折叠区域掩盖类型过大的问题。 | 项目规则（Microsoft 来源无） | 无 |
| S-16 | MAY | 单表达式成员用表达式主体；目标类型 `new()`、集合表达式、主构造函数、raw string 可用（net48 工程也可用，都是编译器特性）。 | [C# 约定](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#language-guidelines) | IDE0021–IDE0027、IDE0090、IDE0300 系列：silent（见 §13.4 envelope 冲突） |
| S-17 | SHOULD | 二元运算符换行时运算符放在新行行首；表达式用括号表明分组。 | [C# 约定：样式](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions#style-guidelines) | IDE0048：suggestion |

---

## 11 工程与仓库规则 [P]

这些规则的理由是保持构建可重复、发布证据可信和检查只收紧；来源文档写在“依据”列。

| ID | 级别 | 规则 | 依据 | 工具 |
|---|---|---|---|---|
| B-01 | MUST | 新工程为 SDK 风格，显式 `TargetFramework(s)`（net48 和/或 net10.0），`LangVersion latest`，`Nullable enable`；`ImplicitUsings` 只在仅面向 net10 的工程中启用。 | 当前工程惯例 | 无 |
| B-02 | MUST NOT | 新增受跟踪的 PowerShell、batch、cmd 文件；构建、发布和检查自动化维护在 `build-tools/release`（C#），用 `dotnet run --project build-tools/release -- <command>` 调用。 | CLAUDE.md、AGENTS.md | `RepositoryChecks` |
| B-03 | MUST NOT | 手改生成文件（`manifest/*.json` 哈希、`manifest/tools-list.json`、`src/Shared/ToolUsageData.json`、`docs/reference/tool-matrix.md`、嵌入目录等）；按 `generate-catalogs` → `generate-tool-usage` 顺序生成，再构建与捕获。 | AGENTS.md | 生成器 `-Check` 逐字节检查 |
| B-04 | MUST NOT | 删除、跳过、过滤、放宽或重设任何检查、断言、阈值与门禁；棘轮基线只减不增；功能按设计改变时用正式工具重新生成基线并逐项报告差异。 | AGENTS.md | 各棘轮与套件下限 |
| B-05 | MUST | 移动或重命名源文件时同步更新所有 `<Compile Include>` 链接、测试工程与文档路径；增删文件后运行 `validate-bundle -Strict -NoBinaries -SkipSourceHashes` 与 `Check-Repository.py --no-binaries`。 | AGENTS.md | 两个检查的必需文件清单 |
| B-06 | MUST | 契约程序集不引用 Siemens 或 JSON 库；`TiaMcp.Logic` 不引用 Siemens 或 MCP SDK；Siemens 引用只出现在适配器、引擎和 worker 中。 | repository-layout、契约工程中的引用禁止目标 | MSBuild 目标 `RejectNativeAndJsonDependencies` |
| B-07 | MUST | 共享构建设置以显式导入的 props 提供（与 `TiaPublicApi.props`、`TiaFeatures.props` 一致），不使用隐式的全仓库导入。注意：`src/Engine`、`src/Studio`、`src/PlcWorker` 各有自己的 `Directory.Build.props`，它们不会继承根目录的同名文件。 | unified-version-framework | 无 |
| B-08 | MUST | 第三方源码保留原样式与许可证，不受本规范约束；`.editorconfig` 中对 `third_party/**`、`reference/**` 关闭全部诊断。 | CLAUDE.md | editorconfig |

---

## 12 测试代码（C 级）的放宽

| ID | 级别 | 规则 | 依据 | 工具 |
|---|---|---|---|---|
| C-01 | MAY | 测试方法名用 `方法_场景_预期` 形式的下划线；测试方法不加 `Async` 后缀。 | [单元测试最佳实践](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices) | editorconfig 测试段：CA1707 none，IDE1006 async 规则 none |
| C-02 | MAY | 测试替身与夹具使用公共字段、可变静态计数器、全局命名空间的控制台入口。 | 项目惯例 | 测试段：CA1051、CA2211 none |
| C-03 | SHOULD | 失败用断言框架或套件自带的检查辅助（`CheckSuite`）报告；需要抛异常时用 `InvalidOperationException` 或专用异常，不用 `Exception`。 | [FDG 标准异常类型](https://learn.microsoft.com/dotnet/standard/design-guidelines/using-standard-exception-types) | 测试段：CA2201 suggestion |
| C-04 | MUST [P] | 测试只能收紧：不删、不跳过、不放宽断言与下限；需要 Siemens 程序集的测试遵守 TH 规则。 | AGENTS.md | `test-suites` 注册表中的通过数下限与跳过数上限 |

---

## 13 .editorconfig 与分析器配置

维护者 2026-10-11 决定：4.0 前在仓库中加入 `.editorconfig` 与分析器配置，两者列入发布源码清单（`build-tools/release/source-roots.json`），先启用 13.3 末尾的 **4.0 规则集**，其余规则先以 suggestion 观察，随偏差清单的处理顺序逐条升级（P8-62，CS-55）。13.1 与 13.2 是落地时的起点，规则以仓库中的配置文件为准。所有工具都随 .NET SDK 提供：代码样式规则（IDE）、Microsoft.CodeAnalysis.NetAnalyzers（CA）与 `dotnet format`，不需要新 NuGet 包。参考：[代码分析概述](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview)、[配置文件](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/configuration-files)、[代码样式规则选项](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/code-style-rule-options)、[命名规则](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/naming-rules)、[C# 格式选项](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/csharp-formatting-options)、[代码分析 MSBuild 属性](https://learn.microsoft.com/dotnet/core/project-sdk/msbuild-props#code-analysis-properties)、[dotnet format](https://learn.microsoft.com/dotnet/core/tools/dotnet-format)。

### 13.1 MSBuild 设置

建议新增 `src/Shared/CodeAnalysis.props`，由根 `Directory.Build.props`、`src/Engine/Directory.Build.props`、`src/Studio/Directory.Build.props`、`src/PlcWorker/Directory.Build.props` 显式导入（B-07；嵌套的 `Directory.Build.props` 不继承根文件）：

```xml
<Project>
  <PropertyGroup>
    <!-- net48 / netstandard2.0 目标默认不运行 NetAnalyzers，必须显式开启。 -->
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>10.0</AnalysisLevel>
    <AnalysisMode>Default</AnalysisMode>
    <!-- 让 .editorconfig 中的 IDE 规则在 dotnet build 中报告。 -->
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <!-- 不设置 TreatWarningsAsErrors；只对已清零的规则逐条设为 error（只收紧）。 -->
  </PropertyGroup>
</Project>
```

注意事项：

- 2026-10-11 的快照中没有任何工程设置 `TreatWarningsAsErrors`、`AnalysisLevel` 或 `NoWarn`，也没有 `.editorconfig`。面向 net10 的工程已经在运行 SDK 默认启用的分析器（如 CA1416、CA2200、CA2013 等默认 warning），但现有警告数需要完整构建才能得出（编写本规范时没有构建）；启用前先用一次完整构建记录警告基线。
- IDE0005 要在构建中报告，需要 `GenerateDocumentationFile=true`，这会同时触发 CS1591（缺少 XML 注释），需另行 `NoWarn`。建议 IDE0005 只在 IDE 与 `dotnet format` 中使用。
- 新增的根 `.editorconfig` 和 props 是受跟踪文件：按 B-05 运行 `validate-bundle` 与 `Check-Repository.py`，并把它们加入发布源码清单（`build-tools/release/source-roots.json`，2026-10-11 决定）。
- 有 `InternalsVisibleTo` 的程序集（Contracts、Logic、EngineHost、Studio Core 等）中，CA1852 不报告。
- 规则严重性只在 `.editorconfig` 中维护，不在各工程中散布 `NoWarn`。

### 13.2 .editorconfig 片段

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

[*.cs]
indent_style = space
indent_size = 4

#### 格式（IDE0055）####
csharp_new_line_before_open_brace = all
csharp_new_line_before_else = true
csharp_new_line_before_catch = true
csharp_new_line_before_finally = true
csharp_space_after_keywords_in_control_flow_statements = true
csharp_preserve_single_line_blocks = true
csharp_preserve_single_line_statements = false
dotnet_sort_system_directives_first = true
dotnet_separate_import_directive_groups = false
dotnet_diagnostic.IDE0055.severity = suggestion

#### 语言风格 ####
csharp_using_directive_placement = outside_namespace:warning            # IDE0065
csharp_style_namespace_declarations = file_scoped:suggestion            # IDE0161
csharp_prefer_braces = when_multiline:suggestion                        # IDE0011
csharp_style_allow_embedded_statements_on_same_line_experimental = false:suggestion  # IDE2001
dotnet_style_allow_multiple_blank_lines_experimental = false:suggestion # IDE2000
csharp_style_var_for_built_in_types = false:suggestion                  # IDE0008
csharp_style_var_when_type_is_apparent = true:suggestion                # IDE0007
csharp_style_var_elsewhere = false:suggestion                           # IDE0008
dotnet_style_predefined_type_for_locals_parameters_members = true:warning  # IDE0049
dotnet_style_predefined_type_for_member_access = true:warning           # IDE0049
dotnet_style_qualification_for_field = false:suggestion                 # IDE0003
dotnet_style_qualification_for_property = false:suggestion
dotnet_style_qualification_for_method = false:suggestion
dotnet_style_qualification_for_event = false:suggestion
dotnet_style_require_accessibility_modifiers = always:warning           # IDE0040
csharp_preferred_modifier_order = public,private,protected,internal,file,static,extern,new,virtual,abstract,sealed,override,readonly,unsafe,required,volatile,async:warning  # IDE0036
dotnet_style_readonly_field = true:suggestion                           # IDE0044
csharp_prefer_simple_using_statement = true:suggestion                  # IDE0063
dotnet_style_parentheses_in_arithmetic_binary_operators = always_for_clarity:suggestion  # IDE0048
dotnet_style_namespace_match_folder = true:silent                       # IDE0130
csharp_style_implicit_object_creation_when_type_is_apparent = true:silent  # IDE0090（见 13.4）
dotnet_style_prefer_collection_expression = false:silent               # IDE0300 系列（见 13.4）
csharp_style_expression_bodied_methods = when_on_single_line:silent
csharp_style_expression_bodied_properties = true:silent
dotnet_diagnostic.IDE0005.severity = suggestion

#### 命名（IDE1006）####
dotnet_naming_style.pascal.capitalization = pascal_case
dotnet_naming_style.camel.capitalization = camel_case
dotnet_naming_style.i_pascal.capitalization = pascal_case
dotnet_naming_style.i_pascal.required_prefix = I
dotnet_naming_style.t_pascal.capitalization = pascal_case
dotnet_naming_style.t_pascal.required_prefix = T
dotnet_naming_style.underscore_camel.capitalization = camel_case
dotnet_naming_style.underscore_camel.required_prefix = _
dotnet_naming_style.s_camel.capitalization = camel_case
dotnet_naming_style.s_camel.required_prefix = s_
dotnet_naming_style.async_pascal.capitalization = pascal_case
dotnet_naming_style.async_pascal.required_suffix = Async

dotnet_naming_symbols.interfaces.applicable_kinds = interface
dotnet_naming_symbols.type_parameters.applicable_kinds = type_parameter
dotnet_naming_symbols.constants.applicable_kinds = field, local
dotnet_naming_symbols.constants.required_modifiers = const
dotnet_naming_symbols.static_readonly_fields.applicable_kinds = field
dotnet_naming_symbols.static_readonly_fields.applicable_accessibilities = private, internal, private_protected
dotnet_naming_symbols.static_readonly_fields.required_modifiers = static, readonly
dotnet_naming_symbols.static_fields.applicable_kinds = field
dotnet_naming_symbols.static_fields.applicable_accessibilities = private, internal, private_protected
dotnet_naming_symbols.static_fields.required_modifiers = static
dotnet_naming_symbols.instance_fields.applicable_kinds = field
dotnet_naming_symbols.instance_fields.applicable_accessibilities = private, internal, private_protected
dotnet_naming_symbols.async_methods.applicable_kinds = method, local_function
dotnet_naming_symbols.async_methods.required_modifiers = async
dotnet_naming_symbols.types_and_members.applicable_kinds = namespace, class, struct, enum, delegate, method, property, event, local_function
dotnet_naming_symbols.public_fields.applicable_kinds = field
dotnet_naming_symbols.public_fields.applicable_accessibilities = public, protected, protected_internal
dotnet_naming_symbols.parameters_locals.applicable_kinds = parameter, local

# 规则按特异性排序：接口/类型参数/常量 → static readonly（项目取舍：PascalCase）→ 可变 static（s_）→ 实例字段（_）
dotnet_naming_rule.interfaces.symbols = interfaces
dotnet_naming_rule.interfaces.style = i_pascal
dotnet_naming_rule.interfaces.severity = warning
dotnet_naming_rule.type_parameters.symbols = type_parameters
dotnet_naming_rule.type_parameters.style = t_pascal
dotnet_naming_rule.type_parameters.severity = warning
dotnet_naming_rule.constants.symbols = constants
dotnet_naming_rule.constants.style = pascal
dotnet_naming_rule.constants.severity = warning
dotnet_naming_rule.static_readonly_fields.symbols = static_readonly_fields
dotnet_naming_rule.static_readonly_fields.style = pascal
dotnet_naming_rule.static_readonly_fields.severity = suggestion
dotnet_naming_rule.static_fields.symbols = static_fields
dotnet_naming_rule.static_fields.style = s_camel
dotnet_naming_rule.static_fields.severity = suggestion
dotnet_naming_rule.instance_fields.symbols = instance_fields
dotnet_naming_rule.instance_fields.style = underscore_camel
dotnet_naming_rule.instance_fields.severity = suggestion
dotnet_naming_rule.async_methods.symbols = async_methods
dotnet_naming_rule.async_methods.style = async_pascal
dotnet_naming_rule.async_methods.severity = suggestion
dotnet_naming_rule.public_fields.symbols = public_fields
dotnet_naming_rule.public_fields.style = pascal
dotnet_naming_rule.public_fields.severity = warning
dotnet_naming_rule.types_and_members.symbols = types_and_members
dotnet_naming_rule.types_and_members.style = pascal
dotnet_naming_rule.types_and_members.severity = warning
dotnet_naming_rule.parameters_locals.symbols = parameters_locals
dotnet_naming_rule.parameters_locals.style = camel
dotnet_naming_rule.parameters_locals.severity = warning

#### 质量规则（CA）####
# 4.0 前即可设为 warning（现存违规为 0 或个位数）
dotnet_diagnostic.CA1018.severity = warning
dotnet_diagnostic.CA1058.severity = warning
dotnet_diagnostic.CA1063.severity = warning
dotnet_diagnostic.CA1065.severity = warning
dotnet_diagnostic.CA1068.severity = warning
dotnet_diagnostic.CA1311.severity = warning
dotnet_diagnostic.CA1507.severity = warning
dotnet_diagnostic.CA1715.severity = warning
dotnet_diagnostic.CA2002.severity = warning
dotnet_diagnostic.CA2200.severity = warning
dotnet_diagnostic.CA2201.severity = warning
dotnet_diagnostic.CA2208.severity = warning
dotnet_diagnostic.CA2211.severity = warning
dotnet_diagnostic.CA2214.severity = warning
dotnet_diagnostic.CA2219.severity = warning
dotnet_diagnostic.CA2300.severity = warning
dotnet_diagnostic.CA2301.severity = warning
dotnet_diagnostic.CA2302.severity = warning
dotnet_diagnostic.CA3075.severity = warning
dotnet_diagnostic.CA3077.severity = warning
# 有一定存量：先 suggestion，清理后升 warning
dotnet_diagnostic.CA1310.severity = suggestion
dotnet_diagnostic.CA1305.severity = suggestion
dotnet_diagnostic.CA1869.severity = suggestion
dotnet_diagnostic.CA1707.severity = suggestion
dotnet_diagnostic.CA1710.severity = suggestion
dotnet_diagnostic.CA1852.severity = suggestion
dotnet_diagnostic.CA1001.severity = suggestion
dotnet_diagnostic.CA1051.severity = suggestion
dotnet_diagnostic.CA1034.severity = suggestion
dotnet_diagnostic.CA1819.severity = suggestion
dotnet_diagnostic.CA1002.severity = suggestion
dotnet_diagnostic.CA2227.severity = suggestion
dotnet_diagnostic.CA1307.severity = suggestion
dotnet_diagnostic.CA1309.severity = suggestion
dotnet_diagnostic.CA1304.severity = suggestion
dotnet_diagnostic.CA1003.severity = suggestion
dotnet_diagnostic.CA1008.severity = suggestion
dotnet_diagnostic.CA1012.severity = suggestion
dotnet_diagnostic.CA1027.severity = suggestion
dotnet_diagnostic.CA1028.severity = suggestion
dotnet_diagnostic.CA1032.severity = suggestion
dotnet_diagnostic.CA1044.severity = suggestion
dotnet_diagnostic.CA1052.severity = suggestion
dotnet_diagnostic.CA1708.severity = suggestion
dotnet_diagnostic.CA1711.severity = suggestion
dotnet_diagnostic.CA1714.severity = suggestion
dotnet_diagnostic.CA1716.severity = suggestion
dotnet_diagnostic.CA1717.severity = suggestion
dotnet_diagnostic.CA1720.severity = suggestion
dotnet_diagnostic.CA1725.severity = suggestion
dotnet_diagnostic.CA1813.severity = suggestion
dotnet_diagnostic.CA1815.severity = suggestion
dotnet_diagnostic.CA1816.severity = suggestion
dotnet_diagnostic.CA1825.severity = suggestion
dotnet_diagnostic.CA1849.severity = suggestion
dotnet_diagnostic.CA2016.severity = suggestion
dotnet_diagnostic.CA2213.severity = suggestion
dotnet_diagnostic.CA2217.severity = suggestion
dotnet_diagnostic.CA2231.severity = suggestion
# 明确关闭：与项目决定或棘轮冲突，或误报过多
dotnet_diagnostic.CA1031.severity = none   # 与 swallowed 棘轮重复（13.4）
dotnet_diagnostic.CA1303.severity = none   # 与“MCP 响应不做本地化”的决定冲突
dotnet_diagnostic.CA1064.severity = none   # 允许非公共异常（E-05）
dotnet_diagnostic.CA1062.severity = none   # 与可空分析重复
dotnet_diagnostic.CA1021.severity = none
dotnet_diagnostic.CA1045.severity = none
dotnet_diagnostic.CA1822.severity = none   # 大量“可设为 static”提示，无契约价值
dotnet_diagnostic.CA2000.severity = none   # 误报多
dotnet_diagnostic.CA2007.severity = none   # 应用代码不适用（U-20）

# A 级库：异步库代码要求 ConfigureAwait
[src/{WorkerChannel,Logic,Adapters.Contracts}/**.cs]
dotnet_diagnostic.CA2007.severity = suggestion

# 测试与脚本（§12）
[{tests,scripts}/**.cs]
dotnet_diagnostic.CA1707.severity = none
dotnet_diagnostic.CA2201.severity = suggestion
dotnet_diagnostic.CA1051.severity = none
dotnet_diagnostic.CA2211.severity = none
dotnet_naming_rule.async_methods.severity = none

# 第三方与官方示例保持原样（B-08）。放在文件末尾：同一文件匹配多个段时后出现的段生效；
# 按规则 ID 设置的严重性优先于全局设置，因此另用 generated_code 让分析器与样式规则跳过这些文件。
[{third_party,reference,sdk}/**.cs]
generated_code = true
dotnet_analyzer_diagnostic.severity = none
```

### 13.3 启用后的现存违规估计

“生产”指 `src/` 与 `build-tools/`；“测试”指 `tests/` 与 `scripts/`。数字来自本次源码扫描（方法见偏差清单 §0），带“≈”的为启发式估计，带“≥”的为已测子集，“未测”需要编译才能得到。规则 ID 对应的偏差编号见最后一列。

| 规则 | 建议 4.0 前 | 目标 | 生产估计 | 测试估计 | 说明 | 偏差 |
|---|---|---|---|---|---|---|
| IDE1006 实例字段 `_camel` | suggestion | warning | 827 个字段名 / 193 文件 | ≈220 | 现有 `_camel` 454 个 | CS-01 |
| IDE1006 可变 static `s_` | suggestion | warning | 32 / 18 文件 | ≈34 | | CS-02 |
| IDE1006 static readonly PascalCase | suggestion | warning | 6（4 个 `_x`、2 个 camel） | ≈4 | 650 个已合规（项目取舍） | CS-03 |
| IDE1006 async 后缀（带 `async` 修饰符） | suggestion | warning | 111 / 49 文件 | ≈489 | 另有 32 个非 async 的 Task 方法规则管不到 | CS-06 |
| IDE1006 类型/成员/参数大小写 | warning | warning | ≈15（下划线属性 11、小写方法 2、大写参数 2） | ≈21 | | CS-04 |
| IDE1006 接口 `I`、类型参数 `T`、常量 | warning | warning | 0 | 1 | | 无 |
| IDE0040 访问修饰符 | warning | warning | ≈22（方法 16、字段 3、类型 2、属性 1） | ≈11 | 多数在 native-call-weaver（改动会改变织入器指纹） | CS-24 |
| IDE0036 修饰符顺序 | warning | warning | 0（`readonly static` 已测为 0；其他顺序未测） | 未测 | | 无 |
| IDE0049 关键字类型 | warning | warning | 64 / 8 文件 | 6 | `dotnet format` 可自动修复 | CS-08 |
| IDE0003 多余 `this.` | suggestion | suggestion | ≈52（`this.` 214 处，其中 162 处为必要的同名赋值） | ≈7 | 随 CS-01 消失 | CS-16 |
| IDE0065 using 位置 | warning | warning | 3 文件 | 1 | | CS-09 |
| IDE0055 using 排序 | suggestion | warning | 190 文件 System 非首；131 文件 System 组未排序 | ≈65 | | CS-09 |
| IDE0055 关键字空格 | suggestion | warning | 1,354 行 / 93 文件 | ≈850 | 集中在 Adapters 808、FoundationHost 430 | CS-11 |
| IDE0055 大括号换行 | suggestion | warning | 67 行 / 28 文件 | ≈57 | | CS-15 |
| IDE0055 一行多语句 | suggestion | warning | 5,547 行 / 652 文件（不含自动属性访问器） | ≈5,200 | 另有 3,466 行 `{ a; b; }` 式单行块 | CS-13 |
| IDE2001 同行嵌入语句（实验性） | suggestion | suggestion | ≥9,151 行 / 828 文件（只计单行 if） | ≥1,297 | 实验性规则，修复能力需先在一个工程上试 | CS-12 |
| IDE2000 连续空行（实验性） | suggestion | suggestion | 209 处 / 89 文件 | 未测 | | CS-22 |
| IDE0161 file-scoped 命名空间 | suggestion | suggestion | 931 文件 | 168 | 只约束新文件（S-07） | CS-10 |
| IDE0007/0008 `var` | suggestion | suggestion | ≈11,100 个非显而易见的 `var` + 2,863 个 `foreach (var` | ≈6,300 + 1,273 | 不升级（有意偏差） | CS-17 |
| IDE0130 命名空间与文件夹 | silent | silent | ≥560 文件（`TiaMcpServer`/`TiaOpenness.Shared`/`TiaMcpConfigurator` 根） | 未测 | 链接源码会误报 | CS-30 |
| IDE0005 未用 using | suggestion | suggestion | 未测 | 未测 | 构建中报告需 `GenerateDocumentationFile` | 无 |
| CA1310 | suggestion | warning | ≈80 / 42 文件 | ≈169 | 先在一个工程上验证 `dotnet format analyzers` 的修复效果 | CS-45 |
| CA1305 | suggestion | warning | ≥123（`Parse` 119、`string.Format` 4；`ToString`/`Convert` 未测） | ≥51 | | CS-45 |
| CA1311 | warning | warning | 0 | 0 | 全部已用 Invariant | 无 |
| CA1307/CA1309 | suggestion | suggestion | 未测（多于 CA1310）/ 0 | 未测 | | CS-45 |
| CA1869 | suggestion | warning | 66（78 处新建中 12 处为静态字段） | ≈30 | 含每次工具调用路径 `PlcToolContract.Map` | CS-46 |
| CA1051 | suggestion | warning | 244 / 12 文件（对外可见类型） | ≈70 | internal 类型上另有 ≈269 个公共字段（规则不报） | CS-33 |
| CA1034 | suggestion | warning | 64 / 25 文件 | ≈26 | | CS-37 |
| CA1819 | suggestion | warning（排除 DTO） | 115（Contracts 107） | 1 | 大部分是线上 DTO（U-01 例外） | CS-36 |
| CA2227 | suggestion | suggestion | 142（Logic 生成模型 83、Contracts 32） | 2 | 大部分是 DTO/模型 | CS-36 |
| CA1002 | suggestion | warning（排除 DTO） | ≤266 个 `public List<` 成员（含 internal 类型中的成员） | 未测 | | CS-36 |
| CA1710 | suggestion | warning | 21 个异常类名（8 个对外可见） | 0 | | CS-38 |
| CA1032 | suggestion | suggestion | 47/49 个自定义异常无无参构造函数（28 个公共） | 未测 | 项目取舍允许只带必需数据的构造函数 | CS-38 |
| CA1063 | warning | warning | 2（`Portal`、`BlocksView`） | 2 | | CS-48 |
| CA1001、CA2213、CA1816 | suggestion | warning | 未测 | 未测 | | CS-48 |
| CA2201 | warning（产品） | warning | 2 | 229 + 4 个 `ApplicationException` | 测试中为 suggestion | CS-57、CS-71 |
| CA2200 | warning | warning | 0（3 处 `throw error;` 抛的是新建异常） | ≈6（未逐个核对） | | 无 |
| CA2208 | warning | warning | 未测（缺失 `ParamName` 的 1,691 处不在此规则范围） | 未测 | | CS-39 |
| CA1068 | warning | warning | 7 / 6 文件 | 3 | | CS-07 |
| CA1849 | suggestion | suggestion | 未测（80 处同步阻塞中位于 async 方法内的子集） | 未测 | | CS-42 |
| CA2016 | suggestion | suggestion | 未测 | 未测 | | 无 |
| CA3075/CA3077 | warning | warning | ≤6（引擎 CLI 中的 `XmlDocument`） | 未测 | | CS-52 |
| CA2300–2302 / SYSLIB0011 | warning | error | 0 | 0 | | 无 |
| CA2211 | warning | warning | 0 | 1 | | 无 |
| CA1852 | suggestion | warning | 7（有 IVT 的程序集不报告） | ≈13 | | CS-32 |
| CA1707 | suggestion | warning | 0（对外可见） | 615（测试关闭） | | CS-70 |
| CA1715、CA1018、CA1058、CA1065、CA2214、CA2219、CA1507、CA2002 | warning | warning | 0 或未测 | 未测 | 预计为个位数 | 无 |
| CA1031 | none | none | ≈1,549（`catch (Exception)` 1,209 + 通用 `catch` 340） | ≈111 | 由 swallowed 棘轮治理 | CS-41 |

**4.0 规则集**（维护者 2026-10-11 决定，P8-62 实施；启用后现存违规为 0 或可在一次小提交内清零）：IDE0040、IDE0049、IDE0065、IDE1006（接口/类型参数/常量/类型与成员大小写/公共字段）、CA1018、CA1058、CA1063、CA1065、CA1068、CA1311、CA1507、CA1715、CA2002、CA2200、CA2201（产品）、CA2208、CA2211、CA2214、CA2219、CA2300–CA2302、CA3075、CA3077。其余先以 suggestion 观察，随偏差清单的处理顺序逐条升级。

### 13.4 与现有棘轮和源码检查的冲突

`build-tools/release` 中的四个棘轮都基于 `CSharpLexer` 的令牌指纹，路径和行号只是信息字段，因此纯空白格式化不会使它们增长。仍需注意：

| 棘轮/检查 | 现状 | 冲突点 | 处理建议 |
|---|---|---|---|
| swallowed（`SwallowedExceptions.cs`，基线 0） | 指纹 = try 体、catch 子句（含过滤器）、catch 体的规范化令牌哈希；未标注且分类为 discarding 的 catch 进入基线 | CA1031 与棘轮重复且会对 ≈1,549 处已审查的 catch 报警；C# 约定“注释独占一行”与“swallow 标注必须在 catch 行或块首”冲突；标识符重命名（CS-01、CS-06）改变 catch 体令牌，未标注的 catch 是否算“handled”取决于日志接收者名单（`logger`/`_logger`/`log`/`_log`/`Logger`/`Log`/`_activity`） | CA1031 设为 none；S-13 已给 swallow 标注开例外；重命名提交后运行 `ratchet -Kind swallowed` 确认 0 新增 |
| comments（`CommentHygiene.cs`，基线 0） | 按注释文本内容与类别指纹 | 没有分析器改写注释文本；IDE 重命名若勾选“包含注释”会改写注释中的标识符 | 重命名时不改注释，或改后运行 `ratchet -Kind comments` |
| mcp-text（`McpText.cs`，基线 0 + 435 条已审查数据） | 按字面量文本指纹，先反转义再计数中文 | CA1303 要求把字面量移入资源，与“MCP 响应不做本地化”的决定冲突；runtime 第 16 条的 `\uXXXX` 转义不会绕过棘轮，但会让已审查的 allowlist 难以人工核对 | CA1303 设为 none；不采纳第 16 条（U-08） |
| envelopes（`ResponseEnvelopes.cs`，`timestamp_now_assignments` 85、`success_assignments` 112，只减不增） | 令牌模式计数；清单另统计 `new JsonObject`、B1–B9 变体 | IDE0090（`new()`）与 IDE0300 系列（集合表达式）的自动修复会改变 `new JsonObject` 等令牌模式，影响清单分类（不影响两个棘轮计数） | IDE0090、IDE0300 系列保持 silent，不批量自动修复 |
| E4 `EnvelopeRewrite.cs`（按差异检查） | 以基线 SHA 比较改动片段，非模板改动一律拒绝 | 任何格式化或重命名与信封内联转换混在同一差异中都会被拒绝 | 格式化与重命名单独提交；信封任务的基线 SHA 取在格式化提交之后 |
| 源码文本检查（`Test-*Sources.py` 等） | 用正则读取 C# 源码（例如按 `public ... Name(` 收集方法名、非递归扫描工具目录） | 方法重命名、拆分行、移动文件会使正则失配 | 重命名/格式化任务同时运行这些检查；按 B-04 只修正检查的定位方式，不放宽断言 |
| Harness 反射与织入清单 | 测试/脚本中有 332 处按字符串名反射（`GetField("...")`、`GetMethod("...")`）；weave 清单按声明方法与类型全名归并 | 字段、方法、类型、命名空间重命名与密封都可能使反射查找失败或改变 E5 多重集合；修改 native-call-weaver 源码会改变织入器指纹（`instrumenterSha256`），要求从原始编译输出重新织入 | 重命名前先列出受影响的反射名；用正式工具重录 weave 基线并逐项报告差异 |
| 源码哈希 | 只在发布时校验 | 任何格式化都会改变源码哈希 | 不手改 manifest；发布流程重新生成 |

---

## 14 偏差的登记与例外

- 现有代码的偏差在 [C# 偏差清单](deviations/csharp.md) 中以 `CS-nn` 编号登记，记录规则、范围、计数、外部影响、修改风险、建议与状态；状态的取值与决定记录方式见 [设计规范索引](README.md)。
- 新代码需要不遵守 MUST 规则时，在评审说明中引用规则 ID 与理由；若属于长期例外，在偏差清单中新增条目，不在代码中散布抑制。确需在代码中抑制分析器时，用 `[SuppressMessage(..., Justification = "...")]` 或 editorconfig 的路径段，并写明偏差编号。
- 本规范随偏差处理结果更新；规则的严重性升级只收紧，不放宽（B-04）。
