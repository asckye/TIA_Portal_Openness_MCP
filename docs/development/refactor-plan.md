# 重构计划

[当前交接](handoff.md) · [路线图](roadmap.md) · [验证分层](validation.md) · [版本框架](unified-version-framework.md) · [引擎拆分设计](engine-decomposition.md) · [测试迁移设计](test-migration.md) · [适配器合并设计](adapter-merge.md) · [响应与异常设计](response-and-errors.md) · [运行时布局与清理](runtime-layout.md) · [工具开发](tool-development.md)

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
| G7 | 运行时与仓库布局解耦 | 引擎通过探测 `scripts/ecosystem/plc_tools_bridge.py` 定位仓库根，运行时读取 `reference/`、`templates/` |
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
| P0-06 | 离线返回结构快照：离线可执行工具的规范化返回，加上全部工具在直接调用与 CallTool 桥接两条路径上的调用前拒绝（`manifest/contracts/responses`） | done |
| P0-05 | 删除确认无引用的死代码：`#if COMMERCIAL` 分支、`TiaMcpServer.PlcFoundation` 并行构建路径（其宏定义与正式路径不一致）；未接线的预览协议随后在 P4-A 删除 | done |

### 阶段 1：构建与验证

| ID | 任务 | 状态 |
|---|---|---|
| P1-01 | CI 改为只校验 manifest 结构与版本一致性，不再严格比对源码哈希；发布流程不变 | done |
| P1-02 | 构建门禁从精确测试数改为“0 失败 + 数量下限”（3126/2840、45、31、8/9 等） | done |
| P1-03 | 版本号单一来源（根 `Version.props`，显式导入，不影响第三方工程），`Release.ps1` 只改一处；修正 LegacyHost 硬编码版本 | done |
| P1-04 | 抽出纯逻辑库 `TiaMcp.Logic`（net48;net8.0，`InternalsVisibleTo`，命名空间不变、纯重命名）；测试与 LegacyHost 改为项目引用。原生调用织入只覆盖 `TiaMcpServer.exe`，凡可能反射/枚举 Openness 对象的文件留在引擎，引擎插桩点不得减少。`openness-shared` 仍为链接源码目录（csc 启动器与 net461 适配器无法使用工程引用）。分三步：S1 构建器与 MCP 逻辑；S2 `Siemens/*Logic.cs`；S3 版本目录与引导（`TiaVersionCatalog` 等）。LegacyHostTests 改造并入 P4-01 | done |
| P1-05 | 测试迁移到 xunit 并改为按最低数量表的 trx 门禁；恢复 10 个从未运行的测试工程；逐步去掉 `partial class` 注入。见[测试迁移设计](test-migration.md)；HttpTests 保持加载织入程序，字符串反射并入引擎拆分步骤 3 | done |
| P1-06 | 适配器诊断测试从 `src` 移到 `tests/TiaMcp.Adapters.DiagnosticsTests`；当时两个 TransportFixture 协议不同，未合并或改名；预览夹具随后在 P4-A 删除 | done |
| P1-07 | 目录整理第一批：设计验收页归入 `docs/development/design-qa.md`；启动器归入 `tools/tia-openness-studio/src/TiaOpenness.Launcher`；Glass 资源归入 `tools/tia-openness-studio/src/TiaOpenness.Gui`；`manifest` 中带日期的历史证据移到 `manifest/history/`。整体目录重组在阶段 4 之后、G7 解耦之后进行 | done |

### 阶段 2：公共层（兼容）

| ID | 任务 | 状态 |
|---|---|---|
| P2-01 | 统一响应信封构造器替代手写 `["success"]`/`["timestamp"]`；明确抛异常与返回失败的规则；返回结构逐字节兼容。设计见[响应信封与吞异常治理](response-and-errors.md)（族规则、构造器、E1–E6 离线证明、P2-01a–e）；须先加强 P0-06（原始文本哈希）。P2-01a 完成：快照格式 3（原始文本哈希）、序列化黄金字节与执行器表征（HttpTests `response-golden-only`，各 124 项）；P2-01bc 完成：`ResponseMeta`/`ResponseClock` 与 249 项逐字节比对，`Inventory-ResponseEnvelopes.py` 只减不增（timestamp 245、success 306）；P2-01d 完成：执行器与 meta 工厂原地改用构造器（黄金样本与全类别插桩 0 差异，245→236、306→295） | doing |
| P2-02 | 合并重复辅助函数：`RequireOneOf`（15 处）、`ParseObject`（9 处）、SHA-256 `Hash`（13 处）、两份 `InvocationJournal`/`NativeCallDiagnostics`（日志与诊断两份分支随 P2-04 一并决定） | done |
| P2-03 | 审计空 `catch`：保留的写明原因，其余改为记录或上抛。设计见[响应信封与吞异常治理](response-and-errors.md)（218 个空 catch、类别、标注与只减不增检查、P2-03a–z）；L5 之前原生路径不改为上抛。P2-03a 完成：`Check-SwallowedExceptions.py` 与基线（205 个空、194 个丢弃型，只减不增）；P2-03c 完成：引擎拆分领域外 88 处补写原因（只改注释，产物逐字节相同），基线 399 → 311；P2-03d 完成：Studio 与 Foundation 74 处，基线 → 237；P2-03b 完成：`SwallowedExceptions.Note` 与三个进程统一的 `TIA_MCP_LOG_SWALLOWED=1` 开关（默认不输出） | doing |
| P2-04 | 确定 JSON 库策略（考虑 net461 worker）：引擎、宿主与 Studio 客户端统一一套，协议层只保留一个序列化边界 | todo |
| P2-05 | 清除历史注释、`*Leftovers` 文件与过时注释；确定用户可见文案的语言策略。设计见[运行时布局与清理](runtime-layout.md)：新增 MCP 文本一律英文（维护者 2026-10-03 决定），现有文本冻结到 4.0；`*Leftovers` 随领域迁移消解。P2-05a 完成：`Check-CommentHygiene.py` 与 `Check-McpText.py` 只减不增（版本号注释 266、受约束中文字面量 316）；P2-05e 完成：Studio 剩余硬编码中文迁入 `Loc`（62 个词条，客户端配置 384 份逐字节不变）；P2-05b 完成：领域外注释改写，注释基线 348 → 248（只改注释，14 个产物逐字节相同） | doing |
| P2-06 | 写子进程 stdin 时显式使用无 BOM UTF-8，不再依赖进程级 `Console.InputEncoding`（.NET Framework 在 65001 代码页下会写 BOM）：隔离 worker（`WorkerConnection`）、`openness-shared/LocalProcess.Run`（Python 伴随桥接）等；HttpTests 与 `Test-EcosystemAssembly.ps1` 已同步引擎启动设置 | done |

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
| P3-15 | 步骤 15：P3-15a 会话、工程与诊断工具 24 个迁入 `SessionTools`、`ProjectSessionTools`、`DiagnosticsTools`（经 `IEngineeringSession` 访问内核，CLI 保留无属性转发）；`McpServer` 只剩基础设施工具（ToolBridge、Batch、Worker、Doctor、CheckForUpdate）与转发 | done |
| P3-16 | 步骤 16：P3-16a 运行时通道（S7、OPC UA、Web API、Unified Open Pipe）迁入 `TiaMcp.Runtime`，PLCSIM Advanced 与环境诊断因可反射西门子对象留在织入的引擎，20 个运行时工具迁入实例工具类；P3-16b `Program` 只保留入口，报告、探针、HMI 模板与 PLC/HMI 同步 XML 拆为 `Cli/` 下的具名类（CLI 输出 78 项、生成文件 340 项逐字节一致；未处理异常的堆栈类名变化按 D1 接受）；P3-16c 删除静态会话入口与 CLI 转发进行中 | doing |
| P3-17 | 源码契约检查改为按成员名定位（`scripts/checks/engine_sources.py`），随迁移失效的检查已修复，纯源码检查接入 CI `source-contracts` 任务 | done |
| P3-xx | 会话层去掉静态服务定位器；G9 修复：非空 PLC 名称在读写中都只接受精确或别名匹配，否则返回 NotFound 与可用路径；空名称仍选唯一 PLC（维护者 2026-10-03 决定），在引擎拆分步骤 4 之后实施，需发布说明；P3-G9 已合并：内核 `ResolvePlc(path, Read|Write)`、结构别名、已验证结果缓存，CHANGELOG 与[真机验收清单](../reference/real-machine-ledger.md)已登记，真机验收前不发布 | done |
| P3-xx | `Program` 中的报告、探针、HMI 模板逻辑移出；`Runtime/` 通道拆为独立程序集 | todo |
| P3-xx | 运行时资源改由安装布局定位，不再探测仓库结构。设计见[运行时布局与清理](runtime-layout.md)（G7-1…7，原有探测保留为兼容回退到 4.0）。G7-1、G7-2 完成：P0-06 加入参考资料工具，`BundleLayout.cs` 与 `Check-BundleLayout.py`；G7-3 完成：引擎查找伴随与参考文件改用解析器（布局矩阵 37 项，仓库外交付包原始响应 0 差异；质量 PDF 一项因本机缺少 reportlab 未验证）；G7-4 完成：安装根、CLI 交付包根与同级引擎查找改用解析器，原探测保留为回退（布局矩阵 56 项，仓库外交付包 9 项比对仅时间字段不同）；G7-5 完成：Studio 交付包根、引擎路径、更新检查、桥接与适配器目录改用解析器，worktree 的 `.git` 文件不再被当作安装包；C# 5 启动器保留原探测，由 `Check-BundleLayout.py` 核对其路径（Core 118、GUI 1407、配置 197，仓库外重定位 1921 份结果一致）；G7-6 完成：V21 生态目录由仓库 JSON 直接嵌入引擎，交付包缺少该文件时仍可查询（生态检查两版各 75 项，含 HTTP） | doing |

### 阶段 4：合并三套实现（兼容，需要真机）

| ID | 任务 | 状态 |
|---|---|---|
| P4-01 | 设计按版本的类型化适配器契约和唯一 worker 协议；以现有“同一源码按精确 SDK 编译 8 次”的 Studio/Foundation 适配器为基础；决定未接线的预览协议采用或删除。设计见[按版本的类型化适配器设计](adapter-merge.md)（契约、协议 2、A–J 迁移步骤）；D4（worker 改 net48）、D7（删除 WorkerProtocol，先移植规则）已决定，其余随对应步骤决定 | done |
| P4-A | 第 A 步：删除未接线的预览协议、七套测试与两个夹具；移除旧 Decode 和门禁豁免，补齐协议 2 测试并保存最终规则映射 | review |
| P4-B | 第 B 步：`TiaMcp.Adapters.Contracts`（原样迁移 DTO、错误类型、黄金 JSON 测试；适配器内 `TiaVersionCatalog` 改为 internal）；48 个类型原样迁移，`adapter-contracts` 套件 232 项 | done |
| P4-C | 第 C 步：版本特性集中到一张表，证明各项目 DefineConstants 与织入清单不变；`tools/openness-shared/TiaFeatures.props` + `Check-TiaFeatures.py`（validate 流程） | done |
| P4-02 | Foundation worker 迁移到共享适配器（第 D 步完成：46 个源码原样移入 `TiaMcp.Adapters/Native`、`Policy`，`OpennessAdapter` 以委托实现会话、程序、数据接口；第 E 步：worker 改 net48（P4-E1 完成，三个旧版本冒烟通过，真机验收已登记）与协议 2（P4-E2 完成：`TiaMcp.WorkerChannel`、握手与不重放规则、`worker-channel` 套件 98 项，预览规则逐条映射；P4-A 完成：删除 `TiaMcp.WorkerProtocol.*`（约 10,700 行、6,676 个断言），删除前补 155 项规则测试，`worker-channel` 253 项）） | doing |
| P4-03 | Studio 桥接进程迁移到共享适配器（第 F 步完成：桥接改用 `TiaMcp.WorkerChannel` 协议 2 的 Studio profile，方法名、DTO 与 UI 错误文本不变，已处理错误保持会话；Core 108、GUI 1388、配置 197、三版桥接冒烟 15、worker-channel 253；真机验收前不发布；第 G 步：基于共享适配器重写会话，构建开关后） | doing |
| P4-04 | V20/V21 引擎的 PLC 路径迁移到共享适配器；HMI、设备等 V20+ 专有能力保留在 V20/V21 专属适配器 | todo |

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

工具合并与改名、`string …Json` 参数改为强类型、统一返回格式、lite 工具集改为数据驱动、三个同名
`TiaMcpServer` 程序改名。开始前单独评审，并为每项变更提供迁移表。

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
