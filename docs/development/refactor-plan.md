# 重构计划

[当前交接](handoff.md) · [路线图](roadmap.md) · [验证分层](validation.md) · [版本框架](unified-version-framework.md) · [引擎拆分设计](engine-decomposition.md) · [适配器合并设计](adapter-merge.md) · [响应与异常设计](response-and-errors.md) · [运行时布局与清理](runtime-layout.md) · [工具开发](tool-development.md)

本页是重构的唯一计划和任务清单。Claude 负责架构决策、任务说明、验收与合并；Codex 按任务说明在独立
worktree 中实现；维护者负责决策点、真机授权和发布。机器路径、会话 ID 和执行日志不写入本页。

## 目标

| # | 目标 | 现状依据 |
|---|---|---|
| G1 | 降低维护成本 | `McpServer` 静态类 93 个文件/488 个工具；`Portal` 87 个 partial、约 3.1 万行；`Program` 5 个 partial、8.8 千行；全引擎仅 1 个接口 |
| G2 | 合并三套 Openness 实现 | V20/V21 `Siemens/Portal`、V14 SP1–V19 `PlcFoundation`、Studio `TiaOpenness.Openness` 各自实现连接、导入导出、编译与 VCI |
| G3 | 简化构建与验证 | 1,056 个源文件哈希在每次 push 时严格校验；门禁匹配精确测试数；无解决方案文件；30 个控制台测试工程链接 216 个源文件 |
| G4 | 整理桌面端 | 配置器靠运行时改写 XAML 嵌入；两套主题引擎；两套本地化；`MainViewModel` 1,017 行并直接弹对话框 |
| G5 | 契约与错误模型 | 292 个 `string …Json` 参数；1,353 处手拼 `JsonObject`；抛 `McpException` 与返回 `success=false` 并存；206 个空 `catch` |
| G6 | 版本号单一来源 | 发布时在约 8 个文件中替换字符串；LegacyHost 健康检查硬编码 `3.1.0` |
| G7 | 运行时与仓库布局解耦 | 已完成：运行时资源由安装布局定位，生态目录嵌入完整引擎；旧仓库探测仅作为兼容回退保留到 4.0 |
| G8 | 职责分离 | `Runtime/`（Sharp7、OPC UA、PLCSIM Advanced、Web API）编译进 Openness 引擎；`Program` partial 承载报告和模板逻辑 |
| G9 | 会话绑定安全 | 其他客户端改绑工程后，`Guard.MatchPlcName` 的“单 PLC 工程匹配任意名称”规则会把请求静默解析到错误 PLC |
| G10 | 清除噪音 | 3 套 worker 协议（未接线的预览协议已在 P4-A 删除）、3 套 JSON 库、`#if COMMERCIAL` 死代码、166 条 `// 2.x.y:` 历史注释、`*Leftovers` 文件、注释中过时的工具数 |

## 约束

1. **兼容先行。** 阶段 0–5 保持 MCP 对外接口完全兼容：工具名称、参数名与类型、返回结构、错误文本语义不变，
   由兼容快照（P0-02）守护。破坏性变更集中在阶段 6，作为 4.0 发布并提供迁移表。
2. **哈希只在发布时校验。** 日常 push 的 CI 不再严格比对源码哈希；`Release.ps1`、`Package-Release.py`
   和发布后验包保持完整校验。manifest 只由构建生成，任何时候都不手改。
3. **原生语义不变。** 改动西门子调用路径的任务必须在报告中列出改动前后的调用序列；
   涉及原生路径的阶段在完成时用真机基线（P0-03）回放。编译、模拟对象和传输测试不证明原生语义。
4. **仅维护 master。** 每个任务在临时分支/worktree 中完成，验收后合并回 master，合并后删除分支。
   每次合并后 master 必须能构建、离线测试通过、兼容快照无差异。
5. 仓库其他规则见 [CLAUDE.md](../../CLAUDE.md)：显式 `git add` 路径、英文提交信息、不加 AI 署名、
   C# 无 BOM UTF-8 与 LF、运行二进制和 SDK 不进 Git。

## 任务流程

1. Claude 写任务说明：范围（允许修改的路径）、目标结构、不变量、验收命令、预期结果。
2. 从当前 master 建 worktree 和 `refactor/<任务 ID>` 分支，Codex 以 `workspace-write` 沙箱执行，
   改动留在 worktree，不提交（worktree 的 Git 元数据不在沙箱可写范围内）。
3. Claude 在 worktree 中复跑验收命令、审查 diff 后在任务分支提交；不合格则带具体问题退回同一 Codex 会话。
4. 验收通过后变基到 master、快进合并，更新本页任务状态。
5. 阶段结束时跑完整八版本构建（L3）；涉及原生路径的阶段再做真机回放（L5）。

### 验收层级

| 层 | 内容 | 何时 |
|---|---|---|
| L0 | `Check-Repository.py --no-binaries`、`Check-DeadToolReferences.py` | 每个任务 |
| L1 | 离线控制台套件（`dotnet run`，迁移后为 `dotnet test`）及受影响工程的测试 | 每个任务 |
| L2 | 用 V20/V21 PublicAPI 编译完整引擎；受影响的 Foundation/Studio 适配器编译 | 改动 C# 的任务 |
| L3 | `Build-MultiVersion.ps1 -Test` 完整八版本构建 | 阶段结束、发布前 |
| L4 | 兼容快照对比：工具清单、输入 schema、离线可执行示例的返回结构 | 阶段 0–5 每个改动 C# 的任务 |
| L5 | VM 真机基线回放（只读工具 + 测试工程上的读写往返） | 阶段 3、4 结束；当前暂缓，见下 |

真机实测暂缓期间：阶段 3 只做不改变西门子调用顺序与线程归属的结构调整；阶段 4 改动原生路径的任务可以开发并合并到 master，
但在 L5 完成前不进入任何发布版本。

## 阶段与任务

状态：`todo` / `doing` / `review` / `done` / `blocked`。

### 阶段 0：基线

| ID | 任务 | 状态 |
|---|---|---|
| P0-01 | 提交现有统一工作台改动，作为重构起点 | done |
| P0-02 | 兼容快照：从编译产物导出 V20/V21 全部工具及八版 Foundation 目录的名称、参数、类型与输入 schema；加入对比脚本 | done |
| P0-03 | 真机行为基线：在 VM 测试工程上录制只读工具响应（剔除时间戳等易变字段）及少量读写往返（维护者决定暂不进行真机实测） | deferred |
| P0-04 | 新增 `.slnx` 解决方案，覆盖全部可构建工程；不改任何工程文件 | done |
| P0-06 | 离线返回结构快照：离线可执行工具的规范化返回，加上全部工具在直接调用与 CallTool 桥接两条路径上的调用前拒绝（`manifest/history/contracts-v3/responses`） | done |
| P0-05 | 删除确认无引用的死代码：`#if COMMERCIAL` 分支、`TiaMcpServer.PlcFoundation` 并行构建路径（其宏定义与正式路径不一致）；未接线的预览协议随后在 P4-A 删除 | done |

### 阶段 1：构建与验证

| ID | 任务 | 状态 |
|---|---|---|
| P1-01 | CI 改为只校验 manifest 结构与版本一致性，不再严格比对源码哈希；发布流程不变 | done |
| P1-02 | 构建门禁从精确测试数改为“0 失败 + 数量下限”（3126/2840、45、31、8/9 等） | done |
| P1-03 | 版本号单一来源（根 `Version.props`，显式导入，不影响第三方工程），`Release.ps1` 只改一处；修正 LegacyHost 硬编码版本 | done |
| P1-04 | 抽出纯逻辑库 `TiaMcp.Logic`（net48;net8.0，`InternalsVisibleTo`，命名空间不变、纯重命名）；测试与 LegacyHost 改为项目引用。原生调用织入只覆盖 `TiaMcp.Engine.V20.exe` / `TiaMcp.Engine.V21.exe`，凡可能反射/枚举 Openness 对象的文件留在引擎，引擎插桩点不得减少。`openness-shared` 仍为链接源码目录（csc 启动器与 net461 适配器无法使用工程引用）。分三步：S1 构建器与 MCP 逻辑；S2 `Siemens/*Logic.cs`；S3 版本目录与引导（`TiaVersionCatalog` 等）。LegacyHostTests 改造并入 P4-01 | done |
| P1-05 | 测试迁移到 xunit 并改为按最低数量表的 trx 门禁；恢复 10 个从未运行的测试工程；逐步去掉 `partial class` 注入。迁移设计已随 P1-05/P1-06 完成归档；HttpTests 保持加载织入程序，字符串反射并入引擎拆分步骤 3 | done |
| P1-06 | 适配器诊断测试从 `src` 移到 `tests/TiaMcp.Adapters.DiagnosticsTests`；当时两个 TransportFixture 协议不同，未合并或改名；预览夹具随后在 P4-A 删除 | done |
| P1-07 | 目录整理第一批：设计验收记录曾单独维护，后由 4.0 工作台实现取代；启动器归入 `src/Studio/Launcher`；Glass 资源归入 `src/Studio/Gui`；`manifest` 中带日期的历史证据移到 `manifest/history/`。G7 解耦及源码目录整理已完成 | done |

### 阶段 2：公共层（兼容）

| ID | 任务 | 状态 |
|---|---|---|
| P2-01 | 统一响应信封构造器替代手写 `["success"]`/`["timestamp"]`；明确抛异常与返回失败的规则；返回结构逐字节兼容。设计见[响应信封与吞异常治理](response-and-errors.md)（族规则、构造器、E1–E6 离线证明、P2-01a–e）；须先加强 P0-06（原始文本哈希）。P2-01a 完成：快照格式 3（原始文本哈希）、序列化黄金字节与执行器表征（HttpTests `response-golden-only`，各 124 项）；P2-01bc 完成：`ResponseMeta`/`ResponseClock` 与 249 项逐字节比对，`Inventory-ResponseEnvelopes.py` 只减不增（timestamp 245、success 306）；P2-01d 完成：执行器与 meta 工厂原地改用构造器（黄金样本与全类别插桩 0 差异，245→236、306→295）；P2-01e0 完成：`Check-EnvelopeRewrite.py`（E4，三种封闭模板，自测接入 CI）与反射、生态、Git、用法试点 7 处（两版离线 4768、响应快照与织入全类别 0 差异）；P2-01e1–e3 完成：PLC、HMI/硬件/在线、会话/运行时/诊断三组 43 个文件 175 处内联调用点改用构造器（E4 全部匹配封闭模板；68 个领域两版各 3748 项逐字节、两版离线 4768、响应快照与织入全类别 0 差异），手写基线收紧为 timestamp 87、success 113；其余形状（多个动态尾字段、字符串时间戳、后写判定等）保持手写并按需标注 `// envelope: legacy-<variant>`，统一留到阶段 6 | done |
| P2-02 | 合并重复辅助函数：`RequireOneOf`（15 处）、`ParseObject`（9 处）、SHA-256 `Hash`（13 处）、两份 `InvocationJournal`/`NativeCallDiagnostics`（日志与诊断两份分支随 P2-04 一并决定） | done |
| P2-03 | 审计空 `catch`：保留的写明原因，其余改为记录或上抛。设计见[响应信封与吞异常治理](response-and-errors.md)（218 个空 catch、类别、标注与只减不增检查、P2-03a–z）；L5 之前原生路径不改为上抛。P2-03a 完成：`Check-SwallowedExceptions.py` 与基线（205 个空、194 个丢弃型，只减不增）；P2-03c 完成：引擎拆分领域外 88 处补写原因（只改注释，产物逐字节相同），基线 399 → 311；P2-03d 完成：Studio 与 Foundation 74 处，基线 → 237；P2-03b 完成：`SwallowedExceptions.Note` 与三个进程统一的 `TIA_MCP_LOG_SWALLOWED=1` 开关（默认不输出）；P3-18 完成：引擎领域内其余吞异常全部写明原因或改为记录，`swallowed-exceptions-baseline.json` 清零，只减不增检查继续在 CI 中运行 | done |
| P2-04 | 确定 JSON 库策略（考虑 net461 worker）：引擎、宿主与 Studio 客户端统一一套，协议层只保留一个序列化边界。P2-04a 完成：Studio 桌面端与桥接改用 System.Text.Json（单一选项工厂，PascalCase、字符串枚举、日期往返与原 Newtonsoft 一致；29 个 DTO、24 个 RPC 方法黄金测试；Studio 产物不再含 Newtonsoft）；P2-04b 完成：Foundation worker 的 DTO 结果与失败证据改用单一 System.Text.Json 编解码（枚举仍为数值、宿主 STJ 解码一致；适配器契约 673 项含黄金样本）；P2-04c 完成：引擎与八版适配器共用无 JSON 库的 InvocationJournal 和原生诊断写入层，保留引擎日志与 Health 字节、验证旧 Newtonsoft 日志读取兼容并提供输出及关联 ID 接口（第 H 步接线），适配器移除 Newtonsoft，TiaMcp.Runtime 的 S7 Web API 客户端保留第三方 Newtonsoft 依赖 | done |
| P2-05 | 清除历史注释、`*Leftovers` 文件与过时注释；确定用户可见文案的语言策略。设计见[运行时布局与清理](runtime-layout.md)：新增 MCP 文本一律英文（维护者 2026-10-03 决定），现有文本冻结到 4.0；`*Leftovers` 随领域迁移消解。P2-05a 完成：`Check-CommentHygiene.py` 与 `Check-McpText.py` 只减不增（版本号注释 266、受约束中文字面量 316）；P2-05e 完成：Studio 剩余硬编码中文迁入 `Loc`（62 个词条，客户端配置 384 份逐字节不变）；P2-05b 完成：领域外注释改写，注释基线 348 → 248（只改注释，14 个产物逐字节相同）；P3-18 完成：领域内注释改写、`*Leftovers` 文件与迁移辅助垫片消解，`comment-hygiene-baseline.json` 清零；受约束中文字面量按语言策略冻结到 4.0 | done |
| P2-06 | 写子进程 stdin 时显式使用无 BOM UTF-8，不再依赖进程级 `Console.InputEncoding`（.NET Framework 在 65001 代码页下会写 BOM）：隔离 worker（`WorkerConnection`）、`src/Shared/LocalProcess.Run`（Python 伴随桥接）等；HttpTests 与生态程序集检查已同步引擎启动设置 | done |

### 阶段 3：拆分完整引擎（兼容）

| ID | 任务 | 状态 |
|---|---|---|
| P3-01 | 设计：见[完整引擎拆分设计](engine-decomposition.md)（12 个领域、内核接口、`ToolCatalog`/依赖注入、17 步迁移顺序与验收） | done |
| P3-02 | 步骤 2：`ToolCatalog`（工具按名称排序）、`EngineServices`、`EngineRegistration`，ToolBridge 夹具改为测试用工具类型；不迁移工具 | done |
| P3-03 | 步骤 3：HttpTests `EngineSurface` 查找辅助（按工具名跨全部 `[McpServerToolType]` 类型、按成员名跨 `Portal` 与服务查找），只改测试；engineering-api 各新增 22 项自检 | done |
| P3-04 | 步骤 4：内核接口 `IEngineeringSession`；四个精确解析器收入内核；`AdoptProject`/`ReleaseProject` 统一会话字段写入；纯静态辅助外移到 `EngineeringSessionHelpers` | done |
| P3-05 | 步骤 5：试点领域（生态、Git、模板、质量审计、导入顺序、指南、工具用法、离线套件，约 30 个工具）迁入实例工具类；先补吞异常原因、再纯迁移，CLI 保留静态转发；30 个工具迁入 9 个实例类，契约与原始响应 0 差异，`Test-PilotTools.py` 覆盖完整/精简与隔离路径 | done |
| P3-06 | 步骤 6：第一个 Portal 领域 CFC 迁为服务类 + 工具类，作为后续领域的样板；逐方法比对西门子调用序列（6 个方法 V20/V21 一致），`Siemens/Services/CfcService` + `CfcTools`，样板写入引擎拆分设计 | done |
| P3-07 | 步骤 7：可选包领域（P3-07a：TestSuite、V20Options、OptionalEngineering、SpecializedExchange、SoftwareUnitDeep；P3-07b：DCC、Teamcenter、Startdrive），比对脚本通用化。P3-07a 完成：5 个服务类 + 工具类，逐方法西门子调用序列 V20 31 个、V21 26 个一致，`Compare-NativeCallOrder.py` 与 `Test-DomainTools.py`（228 项，错误文本按 D1 只比首行）通用化P3-07b 完成：DCC、Teamcenter、Startdrive 3 个服务 + 工具类（22 个工具），Teamcenter 连接状态迁入服务；`ConnectionRow` 两个重载分属 DCC 与 Teamcenter，另行核对调用顺序 | done |
| P3-08 | 步骤 8：P3-08a 安全与 Safety（5 个服务、20 个工具）；P3-08b 库、VCI（静态状态迁入服务实例）、SiVArc（26 个工具）。与 P3-07b、P3-09a 叠加验证：逐方法西门子调用顺序 V21 224、V20 200 一致，同名歧义 3 族另行核对；领域工具 1356 项逐字节一致；契约与返回快照 0 差异 | done |
| P3-09 | 步骤 9：P3-09a PLC 变量表与监控表（17 个工具）；P3-09b 报警、OPC UA、工艺对象（23 个工具，`TechnologyMapping` 共享成员留在内核） | done |
| P3-10 | 步骤 10：Classic HMI、Motion/ProDiag（14 个工具）。与 P3-09b 叠加验证：逐方法西门子调用顺序 V21 280、V20 255 一致，同名歧义 6 族另行核对；领域工具 380 项逐字节一致；契约与返回快照 0 差异 | done |
| P3-11 | 步骤 11 硬件：P3-11a 设备、模块、地址、AML（22 个工具，保留 8 个 CLI 转发）；P3-11b 网络与硬件服务（17 个工具；叠加时发现与 P3-09b 重复迁移的两个 OPC UA 访问控制工具，已从 P3-11b 删除）。步骤 12：P3-12 在线、下载与设备传输（12 个工具）。三者叠加验证：逐方法西门子调用顺序 V21 401、V20 375 一致，同名歧义 11 族另行核对；领域工具 504 项逐字节一致；device-add 124、hardware-catalog 56、foundation 6610、foundation-api 7671；契约与返回快照 0 差异。P3-11c 完成：按文件拆分遗漏的 25 个工具并入已有的 6 个领域服务（领域工具 912 项一致） | done |
| P3-13 | 步骤 13 PLC 程序：P3-13a 块与组（27 个工具）、P3-13b 类型/文档/外部源/原生交换（29 个）、P3-13c 软件信息/反射/审计/导出缓存/XML 构建（34 个）。叠加验证：逐方法西门子调用顺序 V21 221、V20 220 一致，同名歧义 12 族另行核对；领域工具 464 项逐字节一致；foundation 6610、foundation-api 7671；契约与返回快照 0 差异 | done |
| P3-14 | 步骤 14 Unified HMI：P3-14a 离线测试覆盖的 21 个 HMI 工具（服务依赖不含西门子类型的窄接口 `IHmiToolSession`，测试替身取代 partial `Portal`）；P3-14b Unified HMI 核心 51 个；P3-14c HMI 交换、描述与变量删除 21 个。叠加验证：逐方法西门子调用顺序 248 个一致，同名歧义 21 族另行核对；领域工具 292 项逐字节一致；契约与返回快照 0 差异 | done |
| P3-15 | 步骤 15：P3-15a 会话、工程与诊断工具 24 个迁入 `SessionTools`、`ProjectSessionTools`、`DiagnosticsTools`（经 `IEngineeringSession` 访问内核，CLI 保留无属性转发）；`McpServer` 只剩基础设施工具（ToolBridge、Batch、Worker、GetEnvironmentDiagnostics、CheckProductUpdate）与转发 | done |
| P3-16 | 步骤 16：P3-16a 运行时通道（S7、OPC UA、Web API、Unified Open Pipe）迁入 `TiaMcp.Runtime`，PLCSIM Advanced 与环境诊断因可反射西门子对象留在织入的引擎，20 个运行时工具迁入实例工具类；P3-16b `Program` 只保留入口，报告、探针、HMI 模板与 PLC/HMI 同步 XML 拆为 `Cli/` 下的具名类（CLI 输出 78 项、生成文件 340 项逐字节一致；未处理异常的堆栈类名变化按 D1 接受）；P3-16c 删除静态会话入口 `McpServer.Portal`、83 个 CLI 转发与 9 个空 partial，CLI 与基础设施改从 `EngineServices` 取实例，内核到服务的定位器转发改为服务注入（领域工具 424 项、CLI 78 项与生成文件 340 项一致）。阶段 3 完成 | done |
| P3-17 | 源码契约检查改为按成员名定位（`scripts/checks/engine_sources.py`），随迁移失效的检查已修复，纯源码检查接入 CI `source-contracts` 任务 | done |
| P3-xx | 会话层去掉静态服务定位器；G9 修复：非空 PLC 名称在读写中都只接受精确或别名匹配，否则返回 NotFound 与可用路径；空名称仍选唯一 PLC（维护者 2026-10-03 决定），在引擎拆分步骤 4 之后实施，需发布说明；P3-G9 已合并：内核 `ResolvePlc(path, Read|Write)`、结构别名、已验证结果缓存，CHANGELOG 与[真机验收清单](../reference/real-machine-ledger.md)已登记，真机验收前不发布 | done |
| P3-xx | `Program` 中的报告、探针、HMI 模板逻辑移出；`Runtime/` 通道拆为独立程序集（由 P3-16a/P3-16b 完成） | done |
| P3-xx | 运行时资源改由安装布局优先定位。实现见[运行时布局与清理](runtime-layout.md)（G7-1…7，原有探测保留为兼容回退到 4.0）。G7-1、G7-2 完成：P0-06 加入参考资料工具，`BundleLayout.cs` 与 `Check-BundleLayout.py`；G7-3 完成：引擎查找伴随与参考文件改用解析器（布局矩阵 37 项，仓库外交付包原始响应 0 差异；质量 PDF 一项因本机缺少 reportlab 未验证）；G7-4 完成：安装根、CLI 交付包根与同级引擎查找改用解析器，原探测保留为回退（布局矩阵 56 项，仓库外交付包 9 项比对仅时间字段不同）；G7-5 完成：Studio 交付包根、引擎路径、更新检查、桥接与适配器目录改用解析器，worktree 的 `.git` 文件不再被当作安装包；C# 5 启动器保留原探测，由 `Check-BundleLayout.py` 核对其路径（Core 118、GUI 1407、配置 197，仓库外重定位 1921 份结果一致）；G7-6 完成：V21 生态目录由仓库 JSON 直接嵌入引擎，交付包缺少该文件时仍可查询（生态检查两版各 75 项，含 HTTP）。G7-7 完成：文档改为现行布局说明，合并 CHANGELOG，关闭 G7 并解除 P1-07 的 G7 前置阻塞，整体目录重组仍待阶段 4 完成 | done |

### 阶段 4：合并三套实现（兼容，需要真机）

| ID | 任务 | 状态 |
|---|---|---|
| P4-01 | 设计按版本的类型化适配器契约和唯一 worker 协议；以现有“同一源码按精确 SDK 编译 8 次”的 Studio/Foundation 适配器为基础；决定未接线的预览协议采用或删除。设计见[按版本的类型化适配器设计](adapter-merge.md)（契约、协议 2、A–J 迁移步骤）；D4（worker 改 net48）、D7（删除 WorkerProtocol，先移植规则）已决定，其余随对应步骤决定 | done |
| P4-A | 第 A 步：删除未接线的预览协议、七套测试与两个夹具；移除旧 Decode 和门禁豁免，补齐协议 2 测试并保存最终规则映射 | done |
| P4-B | 第 B 步：`TiaMcp.Adapters.Contracts`（原样迁移 DTO、错误类型、黄金 JSON 测试；适配器内 `TiaVersionCatalog` 改为 internal）；48 个类型原样迁移，`adapter-contracts` 套件 232 项 | done |
| P4-C | 第 C 步：版本特性集中到一张表，证明各项目 DefineConstants 与织入清单不变；`src/Shared/TiaFeatures.props` + `Check-TiaFeatures.py`（validate 流程） | done |
| P4-02 | Foundation worker 迁移到共享适配器（第 D 步完成：46 个源码原样移入 `TiaMcp.Adapters/Native`、`Policy`，`OpennessAdapter` 以委托实现会话、程序、数据接口；第 E 步：worker 改 net48（P4-E1 完成，三个旧版本冒烟通过，真机验收已登记）与协议 2（P4-E2 完成：`TiaMcp.WorkerChannel`、握手与不重放规则、`worker-channel` 套件 98 项，预览规则逐条映射；P4-A 完成：删除 `TiaMcp.WorkerProtocol.*`（约 10,700 行、6,676 个断言），删除前补 155 项规则测试，`worker-channel` 253 项）） | doing |
| P4-03 | Studio 桥接进程迁移到共享适配器（第 F 步完成：桥接改用 `TiaMcp.WorkerChannel` 协议 2 的 Studio profile，方法名、DTO 与 UI 错误文本不变，已处理错误保持会话；Core 108、GUI 1388、配置 197、三版桥接冒烟 15、worker-channel 253；真机验收前不发布；第 G 步：基于共享适配器重写会话，构建开关后）。第 G 步第 1 部分（P4-G1）完成：Studio 原生代码（会话、PLC/HMI 导航、VCI、扩展）作为扩展面并入各版织入的 `TiaMcp.Adapter.<key>`，契约增加 Studio 接口与 DTO、STA 守卫；Studio 仍加载 `StudioOpenness.V*`（1926 个方法体 IL 不变），行为不变；第 2 部分（P4-G2）完成：`TiaOpenness.Core` 增加基于适配器扩展面的第二个 `ITiaSession`，由构建开关 `TiaSharedAdapterPaths`（共享 props，默认 false）选择；默认变体输出与测试不变，开关变体打包 `bridge/adapters/v<key>/TiaMcp.Adapter.<key>.dll`（Core 391、GUI 1407、三版桥接冒烟 15），CI 构建并测试两种变体；开关在真机验收（台账）前保持 false，`StudioOpenness.V*` 待第 G3 步删除 | doing |
| P4-04 | V20/V21 引擎的 PLC 路径迁移到共享适配器；HMI、设备等 V20+ 专有能力保留在 V20/V21 专属适配器；第 H 步完成：引擎引用同版织入适配器、固定共用 PublicAPI 目录、接通同文件同关联 ID 日志并加入未启用的 PLC 借用入口，打包复验及跨程序集查找检查已接入，原生调用不变；P4-I1：五个 VCI 工具统一调用单份原语源码，默认引擎本地链接、开启开关后调用适配器，领域外 IL 保持相同，VCI 按展开后的分支调用图和生成的完整类别迁移/增减清单验收，真机项目已入台账；P4-I2：监控/强制表及工艺对象类型化调用已抽取为单份原语，默认/共享变体与八版适配器按同变体基线及宿主方法范围证明通过（accepted: true），11 项形状断言保留并通过，真机台账已登记；P4-I3：硬件目录条目读取与设备创建接入单份原语，保留引擎候选重试和 Foundation 单次精确创建策略，同开关基线的领域外 IL、调用图与精确去重证明通过（accepted: true），device-add/hardware-catalog 原断言全过，真机项目已入台账；P4-I4b：文档与外部源已接入单份原语及借用工程入口，保留宿主策略，离线快照无差异，八版静态证明通过，Foundation 仅迁移八个方法且领域外 IL 保持相同，真机待验项目已登记，开关保持默认 false；P4-I4a：块服务与软件编译已共用原语并借用当前工程，Foundation/Studio 按迁移方法限域且软件读取 257 项断言通过，两种 V20/V21 引擎调用图、非域 IL 与精确原生差量通过；Studio 反射缓存回调按无原生调用的框架回调记录后，八版[静态证明](evidence/p4-i4a-native-evidence.json)通过（accepted: true），真机项目已入台账 | doing |

### 阶段 5：桌面端

| ID | 任务 | 状态 |
|---|---|---|
| P5-01 | 配置器改为编译型 UserControl，去掉运行时 XAML 改写；去掉仅供测试的 .NET Framework 配置器构建（启动器仍由 csc 编译，`configurator-build.json` 格式不变） | done |
| P5-02 | 单一主题引擎与单一本地化方案 | done |
| P5-03 | 拆分 `MainViewModel`（会话、工程操作、VCI 子 ViewModel），对话框改为服务接口 | done |
| P5-04 | 配置页 9 个沿用旧词典、英文模式仍显示中文的菜单项（检查更新、运行更新、发布页等，见 `StringsTests` 的允许名单）补英文翻译，同步更新配置测试 | done |
| P5-05 | 工作台统一菜单栏（项目/视图/工具/帮助，语言与主题移入“视图”，配置页 `•••` 菜单并入“帮助”）；浅色/深色主题下所有面板跟随主题，禁止界面写死颜色并检查对比度；应用内对话框改为玻璃风格；修复选项面板无法显示 | done |
| P5-06 | 标题栏页面按钮并入“视图”菜单（工程操作 Ctrl+1、MCP 与客户端 Ctrl+2），标题旁显示当前页面 | done |

### 阶段 6：破坏性变更（4.0）

维护者于 2026-10-03 决定 **硬切 4.0**，重新提出的 1–11 项全部采用建议。实施契约见
[4.0 规范与生成表](phase6-review.md)，其中完整名称、类型输入、响应、lite 和布局清单是本阶段唯一目标。
阶段 4 第 I 步 P4-I1/I2/I3/I4a/I4b 已合并，`docs/development/evidence/p4-i*-native-evidence.json` 已再生成且静态 accepted=true。
`src/Shared/TiaSharedAdapterPaths.props` 仍默认 false；原生验收及 G3/J 仍为 NOT RUN/待验，不因静态证明而放行发布。
P6-02（`src/Logic/V4`）与 D334 源码目录整理已完成；产品入口与运行目录迁移仍待 P6-36～39。

P6-02 已完成，P6-01 已更新事实与路径待审查，其余为待实施任务；P6-R2 只交付规范/生成器。
每行的路径链接指向规范附表 H 的当前文件归属，附表 G 另固定领域工具所有权。`I` 表示第 I 步全部任务完成并合并（现已满足）；
`L5-X` 表示真机台账 P6-X 中该发布键的基线和候选行为均通过。原生策略任务可以先准备离线代码，
但在 L5-X 前不得切换发布能力；未验收族保持 current 行为。每次任务说明须列出精确路径，超出范围先停。

公共证明缩写：`C`=`scripts/generate/Generate-Phase6Plan.py` --self-test/--check、仓库/死引用及全部 source-contracts；
`T`=受影响离线套件按 `scripts/checks/Test-DotnetSuites.py` 的 TRX 最低数量门禁、0 失败；
`V`=八版实际名称/schema/响应快照符合生成映射，直接/CallTool/批次同形，版本/action 无扩张；
`N`=逐方法前后 Siemens 调用顺序/参数/线程与全部织入类别差量，加本族故障证据及 L5。
新增/移动/删除文件还必须运行 Strict/NoBinaries/SkipSourceHashes bundle 校验及三份必需文件清单核对。
C# 的每项任务均须编译其受影响精确 SDK 版本；完整八版集成另在 P6-42 重跑。

| ID | 单次任务与文件边界 | 前置依赖 | 验收证明 | 状态 |
|---|---|---|---|---|
| P6-01 | 合并后冻结八版事实与任务路径清单；重跑 `scripts/generate/Generate-Phase6Plan.py`，固定新契约快照目录/生成规则；登记 I 与 L5 实际状态；[路径清单](phase6-review.md#phase6-path-p6-01) | 本规范；只读核对 I，不改服务 | 两次生成相同、C；当前目录与源码一致 | done |
| P6-02 | 已在 `src/Logic/V4` 实现 V4 信封、25 种错误详情、分页/批次/计划 DTO、MCP/CLI 映射和单一序列化/校验边界，补齐黄金字节、往返、文化与非法组合测试，现有工具不接线；[路径清单](phase6-review.md#phase6-path-p6-02) | 本规范（不依赖 01） | T；成功/拒绝/失败/partial/unknown 黄金字节，null/键序/关联 ID；无 Siemens 引用 | done |
| P6-03 | 新建 P/S/N/R/M/L/V/C/W 类型及校验适配层，ToolArguments 按目标 schema；保持原 parser 预算；[路径清单](phase6-review.md#phase6-path-p6-03) | 02 | T；双编码、重复字段、溢出、null/缺省、批次 allowlist/上限正反例 | done |
| P6-04 | 新建 B 构造 DTO 与按 kind 的联合、嵌套 call 字段；限定新 DTO/转换文件；[路径清单](phase6-review.md#phase6-path-p6-04) | 02 | T；Foundation 窄语法、输出版本/预算、XML 内容等价 | done |
| P6-05 | 新建 H 的 Classic/Unified/AML 类型，控件联合各自收窄；限定 H DTO 文件；[路径清单](phase6-review.md#phase6-path-p6-05) | 02 | T；布局/屏幕/包样本和非法属性，Classic 与 Unified 不互换 | done |
| P6-06 | 新建 D/X 领域联合与选择 DTO，列出每个 parser 的字段/枚举/schema；限定 D/X DTO 文件；[路径清单](phase6-review.md#phase6-path-p6-06) | 02 | T；全部 B 表项有具体类型，动作判别及 snapshot 身份/完整性保留 | done |
| P6-07 | 基础设施工具契约：桥接、批次、分页、指南合并、目录与 lite 数据接线；工具文件见附表 G；不改领域服务；[路径清单](phase6-review.md#phase6-path-p6-07) | 03–06 | T、V；合并映射逐 topic、CallTool 对象信封、60 项提案示例、完整目录仍可达 | done |
| P6-08 | Foundation 宿主名称/输入/结果适配（不改 EXE 名和 adapter 行为）；按 F7 保留 evidence、Executed、候选输出状态；[路径清单](phase6-review.md#phase6-path-p6-08) | 03–07、I | T foundation/transport、六版 V；schema 更窄、无新增 HMI/CallTool/lite；N 调用不变 | done |
| P6-09 | 离线构造、审计、模板、生态、Git、文档生成工具迁移；文件见附表 G；[路径清单](phase6-review.md#phase6-path-p6-09) | 03–07、I | T、V；生成文件内容不变、预算与输出版本限制，修改只在本组 | done |
| P6-10 | PLC 块/软件/类型/表工具名称/类型/信封迁移；同名服务及独占规则；[路径清单](phase6-review.md#phase6-path-p6-10) | 03–07、I | T、V；N 调用不变，compile 两入口不误合并 | done |
| P6-11 | PLC 文档/原生交换/外部源/补丁工具契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-11) | 03–07、I | T、V；partial/unknown 和 observation 保全；N 调用不变 | done |
| P6-12 | 设备/AML/模块/地址工具契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-12) | 03–07、I | T、V；创建仍披露 current；N 调用不变 | done |
| P6-13 | 硬件网络/服务工具契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-13) | 03–07、I | T、V；逐路径/属性限制；N 调用不变 | done |
| P6-14 | 证书/项目安全/Safety/安全扩展契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-14) | 03–07、I | T、V；权限/版本门禁与脱敏；N 调用不变 | done |
| P6-15 | 报警/OPC UA/工艺对象/软件单元契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-15) | 03–07、I | T、V；版本 action 与 native 值边界；N 调用不变 | done |
| P6-16 | Classic HMI 文件夹与 Motion/ProDiag 契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-16) | 03–07、I | T、V；目标联合互斥与能力拒绝；N 调用不变 | done |
| P6-17 | Unified HMI 核心、组、屏幕项、UI 模型契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-17) | 03–07、I | T、V；控件设计 DTO 与旧结果信息等价；N 调用不变 | done |
| P6-18 | HMI/Unified 交换与变量删除契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-18) | 03–07、I | T、V；逐项结果、导入导出边界；N 调用不变 | done |
| P6-19 | HMI 描述/检查/脚本/图形快照/对象服务/反射契约迁移；文件见 G；[路径清单](phase6-review.md#phase6-path-p6-19) | 03–07、I | T、V；游标身份/完整性与反射准入；N 调用不变 | done |
| P6-20 | CFC/TestSuite/V20Options/OptionalEngineering/SpecializedExchange 契约迁移；[路径清单](phase6-review.md#phase6-path-p6-20) | 03–07、I | T、V；两版能力差集；N 调用不变 | done |
| P6-21 | DCC/Startdrive/Teamcenter 契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-21) | 03–07、I | T、V；联合/动态属性；已知现场事故不标记已修复；N 调用不变 | done |
| P6-22 | 库/Sivarc/VCI 契约迁移；本组工具/服务；[路径清单](phase6-review.md#phase6-path-p6-22) | 03–07、I | T、V；大小写、范围、selection 限制；N 调用不变，保留 G3/J 开关门槛 | done |
| P6-23 | runtime 通道/PLCSIM/在线下载工具契约迁移；本组工具/服务及 `src/Runtime`、`src/Engine/Runtime` 适配边界；[路径清单](phase6-review.md#phase6-path-p6-23) | 03–07、I | T、V；未知写入不可变为重试；N 调用不变 | done |
| P6-24 | 会话/工程/诊断契约及公共 Portal、ToolCatalog、注册/项目文件串行集成；清除旧工具目录注册；[路径清单](phase6-review.md#phase6-path-p6-24) | 08–23、I | T、V 全量；生成所有权覆盖每个入口，拒绝旧名/旧参数/二次编码；N 调用不变 | done |
| P6-25 | CLI 和 Studio 的 V4 消费边界、错误展示、退出码；各自报告保留业务字段；[路径清单](phase6-review.md#phase6-path-p6-25) | 24 | T；CLI 0/2/3/4/5/64/70 黄金样本、Studio Loc 测试、partial/unknown 不显示成功 | done |
| P6-26 | 先删文本判定再统一英文 MCP 文本；错误码消费者/FindTools/稳定性与快照脚本同步；[路径清单](phase6-review.md#phase6-path-p6-26) | 25 | T、V、MCP 文案检查；无消息子串控制安全动作，原生文本作为脱敏数据 | done |
| P6-27 | D1 设备精确创建候选实现与本族能力切换：Devices/设备添加策略；[路径清单](phase6-review.md#phase6-path-p6-27) | 24、I；发布切换等待 L5-DEVICE | T、N；一次 create、冲突/失败后状态；台账 P6-DEVICE 逐适用版通过 | done |
| P6-28 | D1 PLC 块/类型/表导入安全策略候选与切换；原生导入及文件准备策略；[路径清单](phase6-review.md#phase6-path-p6-28) | 27、I；发布切换等待 L5-IMPORT | T、N；不修版本/BOM、覆盖拒绝、显式覆盖能力、批次中止/内容回读 | done |
| P6-29 | D1 PLC 导出/SD/批量发布安全策略候选与切换；原生导出及文件发布策略；[路径清单](phase6-review.md#phase6-path-p6-29) | 28、I；发布切换等待 L5-EXPORT | T、N；暂存、原文件保全、partial、文件系统故障、对象范围不扩大 | done |
| P6-30 | D1 connect/open 的进程/工程身份与升级副本策略候选与切换；[路径清单](phase6-review.md#phase6-path-p6-30) | 29、I；发布切换等待 L5-SESSION | T、N；PID 重用、绑定、拒绝升级/副本升级、不启动、不关闭外部工程 | done |
| P6-31 | D1 save/close 的借用/脏工程/LocalSession 策略候选与切换；[路径清单](phase6-review.md#phase6-path-p6-31) | 30、I；发布切换等待 L5-CLOSE | T、N；显式保存、独立 discard 确认、借用拒绝与所属线程 | done |
| P6-32 | D1 PLC 路径及外部源计划/生成/删除策略候选与切换；[路径清单](phase6-review.md#phase6-path-p6-32) | 31、I；发布切换等待 L5-SOURCE | T、N；G9 空/错误/歧义路径、删除核实、14sp1 observation、不重放 | done |
| P6-33 | D1 编译离线前提与 Safety 会话对称清理候选与切换；[路径清单](phase6-review.md#phase6-path-p6-33) | 32、I；发布切换等待 L5-COMPILE | T、N；不自动下线、清理失败、根/叶诊断、各入口目标范围 | done |
| P6-34 | D1 下载配置失败/自动下线及 VCI 失效句柄的显式路线策略候选与切换；[路径清单](phase6-review.md#phase6-path-p6-34) | 33、I；发布切换等待 L5-FALLBACK | T、N；逐中断点证明已执行/未知，读句柄刷新不能重放写入 | done |
| P6-35 | 汇总逐版/逐族行为能力与 V4 schema 缺省；未验收族保持 current，并重新生成实际发布快照；[路径清单](phase6-review.md#phase6-path-p6-35) | 26；已进入发布的 27–34 必须对应 L5 通过；其余明确延期 | T、V；schema 不广告未实现安全策略、UNVERIFIED_BEHAVIOR 可见；不改 G3/J 默认开关 | done |
| P6-36 | 三个 EXE/程序集输出名与根 TiaOpenness 启动器；集中修改 build/package/validate/织入/反射/必需清单；[路径清单](phase6-review.md#phase6-path-p6-36) | 25、I | T；八版产物身份/依赖/织入不漏；Strict 验包，新根启动器打开工作台 | done |
| P6-37 | 引擎/Foundation/CLI 的 bundle-root 与严格资源/同级路由；公共 BundleLayout 实现；[路径清单](phase6-review.md#phase6-path-p6-37) | 36 | T；CLI→环境→锚点矩阵，错误根不回退，Foundation release-key/worker 参数保留 | done |
| P6-38 | Studio 根定位、客户端配置/更新/桥接路径消费新产品表，删除任意布局探测；[路径清单](phase6-review.md#phase6-path-p6-38) | 37 | T Core/GUI/config；配置备份迁移、缺目标引擎拒绝、worktree 禁止更新；G3/J 不变 | done |
| P6-39 | 引擎/Studio 日志、Python 默认环境及 CLI 私人 workspace/fixture 显式输入；[路径清单](phase6-review.md#phase6-path-p6-39) | 38 | T；只读安装、LocalAppData 不可写、显式 Python、私人默认值消失、并发日志 | done |
| P6-40 | 更新 `reference/tool-examples`、`src/Engine/ModelContextProtocol/McpPrompts.cs`、`plugin/skill` 及现行文档；运行 `scripts/generate/Generate-ToolUsage.py`、`Generate-ToolCapabilityMatrix.cs`；[路径清单](phase6-review.md#phase6-path-p6-40) | 35、39、44–48 | 八版示例检索/schema 对齐、lite 每项实参例子；C；不手改嵌入 JSON 和 manifest 哈希 | done |
| P6-41 | 按规范第 7 节在 `manifest/contracts/v4` 建立逐发布键快照和 CI 检查；现有 Snapshot-ToolContracts/Responses 脚本生成，3.x 基线逐字节归档至 `manifest/history/contracts-v3`，禁止用改哈希掩盖差异；P6-41a 已提前完成 3.x 归档、归档守护与静态 V4 检查；[路径清单](phase6-review.md#phase6-path-p6-41) | 40 | C、T、V；清洁 checkout 两次生成相同；布局扫描命中逐项关闭或注明历史证据 | done |
| P6-42 | 发布候选完整八版构建/离线/重定位/严格验包及真实行为台账复核，列出实际纳入和延期族；[路径清单](phase6-review.md#phase6-path-p6-42) | 41；纳入族各自 L5，阶段 4 发布门槛也须满足 | L3、V、L5、只读安装；完整包启动/连接冒烟；未验收族不得误切 safe-v4 | todo |
| P6-43 | **最后任务：发布说明**生成旧→新工具名/参数/产品入口对照，类型/信封/安全策略/目录变更及实际验收状态；[路径清单](phase6-review.md#phase6-path-p6-43) | 42 | 对照表逐版与发布产物一致、链接可执行示例、刷新客户端工具缓存说明；仅文档，无程序转换层 | done |
| P6-44 | 工作台写操作审批：宿主按调用的写分类、待批队列与超时、当前用户命名管道、审批开关与 meta 披露；Studio 审批视图；ConfirmationRequiredDetails 拒绝原因（先改规范第 3 节）；见规范第 8 节；[路径清单](phase6-review.md#phase6-path-p6-44) | 25、39；D1 已切换族在 apply 处接入 | T 宿主/Studio Core/GUI、V；只读不排队、批次逐项、拒绝/超时/工作台未连接均为操作前拒绝、批准一次性且绑定 planHash、MCP 客户端不能自批；N 调用不变 | done |
| P6-45 | 工作台 AI 调用面板：读取调用日志实时显示、详情展开（截断/脱敏）、待批置顶、复制连接信息；[路径清单](phase6-review.md#phase6-path-p6-45) | 44 | T GUI/Core；轮转/并发/中断行、脱敏、中英文；不新增采集通道 | done |
| P6-46 | 哈希链审计日志与校验入口；调用日志保留上限/份数及时间窗口显示；[路径清单](phase6-review.md#phase6-path-p6-46) | 39、44 | T；篡改/断链/跨文件轮转检测，尾部截断限制写入文档；保留按配置生效 | done |
| P6-47 | 工作台环境体检页与一键诊断包；复用 doctor，补 Openness 首次确认提示与 data 可写检查；[路径清单](phase6-review.md#phase6-path-p6-47) | 38、39 | T GUI/Core/config；逐项结果与修复文本中英文、密钥脱敏、只读安装 | done |
| P6-48 | 梯形图出图与程序图册：抽出 RenderPlcVisualDiff 的布局/SVG 为共享逻辑，新增八版单块出图与整机图册工具、检查结论标注、工作台入口；见规范第 8 节；[路径清单](phase6-review.md#phase6-path-p6-48) | 24；自行导出时服从当时导出族状态 | T Logic/宿主/GUI、V；RenderPlcVisualDiff 输出逐字节不变；每版真实导出样本的渲染快照；HTML 自包含、不覆盖已有文件；N 仅复用既有只读导出调用 | done |
| P6-49 | 发布前修复：会话工具在原生调用前失败时不报未知结果、首次附加超时提示 Openness 确认、工作台子进程日志编码、.NET Framework 4.8 前提检查；第 2 轮把保存/另存/关闭纳入工作台审批；[路径清单](phase6-review.md#phase6-path-p6-49) | 44、47 | T、V；只改列出的响应快照调用；不改 D1 开关 | done |
| P6-50 | 随包脚本改为 C#：写入防护钩子改为 C# 程序、删除两个中文 .bat（改用命令行）、Install-PlcTools 改为 C# 命令、TIA 退出证据并入工作台诊断包、vci-watch 注册与 LibraryRenameProbe 运行器改写；[路径清单](phase6-review.md#phase6-path-p6-50) | 47 | T；钩子判定与旧脚本逐例一致、诊断包脱敏；随包无 .ps1/.bat/.cmd | done |
| P6-51 | 更新器改为 C#，替代 Update-Engine.ps1：运行中拒绝、下载与 SHA-256 校验、备份、含自身的替换、失败回滚、源码目录拒绝、旧文件清理；引擎与工作台的更新提示同步；[路径清单](phase6-review.md#phase6-path-p6-51) | 38、50 | T；本地假发布源全流程、中断回滚、3.3→4.0 升级路径 | todo |
| P6-52 | 开发检查去 PowerShell：反射类发布检查与工具清单生成改 C#，能力矩阵生成与覆盖审计改 C# 单文件程序，适配器构建检查改写；[路径清单](phase6-review.md#phase6-path-p6-52) | 42 | T、C；各检查断言数不少于原脚本，生成结果逐字节一致 | done |
| P6-53 | 构建发布链改 C#：Release、Build-Release、Run-ReleaseBuild、多版本构建、验包、预检等；CI 工作流；仓库检查禁止新增 .ps1/.bat/.cmd；[路径清单](phase6-review.md#phase6-path-p6-53) | 50、51、52 | 完整发布链从干净目录通过，产物清单与改写前一致 | todo |
| P6-54 | V20/V21 改为与 Foundation 相同的“宿主 + worker”运行方式（同一引擎 EXE 的 `--isolate-openness`，工具数量与功能不变）：缺少 TIA、版本不符或不在 Siemens TIA Openness 组时照常启动，由 Bootstrap/InitializeEnvironment/环境体检报告并对需要博途的调用返回 V4 错误；默认隔离的代码与退路参数就绪，发布默认保持关闭至真机验收通过；八版启动/重定位检查直接启动真实 EXE；文档、客户端配置与工作台显示同步；[路径清单](phase6-review.md#phase6-path-p6-54) | 49 | T、V；真实 EXE 无 TIA 启动、worker 测试替身仅测试构建可用；L5：V20/V21 隔离模式连接绑定、导入导出、编译、worker 超时重启后重新绑定，通过前不切默认 | todo |

并行边界：02 完成后 03/04/05/06 可各建独立 DTO/测试文件；项目公共引用由 02 预置、后续缺项由 07 集成。
07 完成且 I 合并后，08–23 按规范附表 G 的工具文件所有权并行。
`src/Engine/Siemens/Services` 中本领域同 stem Service 和独占规则归对应任务；
公共 `src/Engine/Siemens/Portal`、`src/Engine/EngineServices.cs`、注册/项目文件统一留给 24，
共享测试夹具和快照由 24 集成，领域任务只加本组测试文件。任一共享规则被两个领域引用时暂停并交 24，不能并行编辑。
27–34 涉及共享 adapter/安全政策，按表串行；36–39 为路径链也串行。36–39 可以与 27–34 并行，
但只改入口/布局/构建路径，不能改原生服务；汇合在 40。维护者未恢复真机授权时，契约/布局链可继续，
原生族发布切换等待台账，不能用时间经过或离线通过代替 L5。

## 待维护者决定

1. P0-03：恢复真机实测的时间、VM 上的 TIA 版本、测试工程和授权范围（当前暂缓）。
2. 重构期间是否暂停新功能和发版。

## 风险

| 风险 | 应对 |
|---|---|
| 原生语义静默回归 | 兼容快照只守接口；原生语义靠 P0-03 基线在阶段 3、4 结束时回放 |
| Codex 沙箱限制：无网络、PowerShell 受限语言模式、离线套件中 2 个临时目录文件替换用例在沙箱内失败（沙箱外全部通过） | 构建脚本和最终验收由 Claude 在沙箱外执行；Codex 只负责修改代码并运行 `dotnet build/test`，报告中区分已知沙箱失败 |
| 并行任务冲突 | 同一时刻只并行改动路径不重叠的任务；阶段 3 按领域分文件 |
| 阶段中途需要发版 | 按 [发布流程](release-workflow.md) 完整重建 manifest；不在阶段中间留半迁移状态 |
