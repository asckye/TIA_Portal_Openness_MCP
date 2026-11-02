# 发布计划

状态：现行 · 最后核对：2026-11-01，master `27675208`（4.0.0-beta.3 准备提交，尚未发布）

[当前交接](../development/handoff.md) · [路线图](../development/roadmap.md) · [重构计划](../development/refactor-plan.md) · [架构决策记录](../development/decisions/README.md) · [测试者反馈台账](../development/tester-feedback.md)

本页按版本记录范围、任务状态和放行门槛，是 2026-10-22 之后新任务的唯一计划。阶段 0–8 的任务表留在[重构计划](../development/refactor-plan.md)，4.0 正式版后归档；当前源码事实和正在进行的分支见[当前交接](../development/handoff.md)；方向见[路线图](../development/roadmap.md)。`bin-build/refactor/tasks/followups.md`（不入库）只是收件箱：其中的条目在决定当天转写到本页或写成 ADR，任务说明只引用本页的编号。

状态：已合并（带提交号）· 进行中（有分支或正在实施）· 排队（已有任务说明，等待前置任务）· 未开始。

## 放行门槛

| 发布 | 门槛 | 依据 |
|---|---|---|
| 预发布 `4.0.0-beta.N` | GitHub 预发布，不设为最新版，3.3 更新器不提示；说明附 “Not verified on a real TIA Portal” 清单；每次发布前维护者确认；测试者按[预发布真机验证清单](../guides/beta-tester-checklist.md)报告，我们在 VM 上按同一清单做回归 | [ADR 0020](../development/decisions/0020-prereleases-tested-by-users.md) |
| 合并 | 本机 `check-all -Affected` 加受影响的检查通过；GitHub CI 不阻塞合并 | [ADR 0024](../development/decisions/0024-ci-does-not-block-merges.md) |
| 发布证据 | 最近一次通过的 nightly 加已验证的增量构建单元；nightly 门槛 45 分钟，失败或超时阻止下一次发布；发布不等 CI（`-NoWait`） | [ADR 0025](../development/decisions/0025-release-evidence-nightly-and-verified-build-units.md)、[ADR 0024](../development/decisions/0024-ci-does-not-block-merges.md)；实现见 RULES-1 |
| 4.0 正式版 | 全部 4.0 任务合并；check-all 通过，CI 结果在维护者确认前另行核对；由发布链生成 rc；维护者确认。不设公测期，不要求连续 20 次通过；起初不向 3.3 更新器提供 | [ADR 0023](../development/decisions/0023-release-4-0-final-criteria.md) |

## 4.0.0-beta.3（未发布）

2026-11-01 的发布运行已停止，GitHub 上没有 beta.3。master 上的准备提交 `27675208`（版本号、CHANGELOG 条目、带版本号的响应基线）保留；CHANGELOG 的 `[4.0.0-beta.3]` 条目和 [beta.3 发布说明草稿](../releases/v4.0.0-beta.3.md)在发布前按扩大后的范围补写。维护者 2026-11-01 决定：beta.3 包含原定 beta.4 的全部内容，加上项目审查的各项和工作台重新设计。

### 已合并（准备提交之前）

| 编号 | 内容 | 状态 |
|---|---|---|
| beta3-rename | 对外名称改为自动化工程工作台，维护者版权行，西门子商标声明 | 已合并（`77bdf060`、`1f37ab3d`、`1cad1df2`） |
| beta3-docs | 3.3 到 4.0 迁移指南、预发布真机验证清单、测试者反馈台账、Issue 表单 | 已合并（`f6bf1dea`） |
| SPD-1、SPD-2、SPD-4 | 重复工作只做一次、派生子集、`-Affected`、nightly 复用与阻断发布、`-FailFast` | 已合并（`a6ced5da`、`b7db27a0`、`bf04b534`、`c68c31d3`） |
| PREFLIGHT | 发布前检查伴随 Python 与跟踪文件的规范化字节；主机注册文件容忍并发读取 | 已合并（`7830383b`、`e57b64c5`） |
| XREF | 原生交叉引用 V14 SP1–V20 默认允许、V21 工作台会话开关、新工具 `AnalyzePlcCrossReferences` | 已合并（`4b17a042`、`f51c9f20`） |
| HMI-B3 | HMI 复核 B6–B8、D6：`PropertyName` 选择接口项、旧 Ensure/Apply/Bind 默认预览、路径预校验 | 已合并（`e5c3d365`） |

### 待完成

合并顺序（维护者 2026-11-01）：`examples-v20v21` → SPD-3c → DES-1 → S0 文档 → VMFIX-1 → DES-5 / SU-1，然后 DES-2、RULES-1、RELAY-1、DES-3、DES-4、工作台 UI 各批。同时最多三个 Codex 任务；DES 系列按依赖串行。每批修复合并后在 VM 上复测对应的 beta.2 发现。

| 编号 | 内容 | 状态 | 决定 |
|---|---|---|---|
| EX-V20V21 | V20/V21 改用维护的调用示例，不再使用冻结的引擎值；重新生成工具档案与用法数据 | 进行中（`refactor/examples-v20v21`，`9f43d38d`，待合并） | 2026-11-01 |
| SPD-3、SPD-5 | 可复现构建、共享库只编译一次、已验证构建单元缓存；合并时准备发布（release-prepare），发布按哈希发布已封存的候选 | 进行中（`refactor/SPD-3c`，待本机验证与一次发布演练） | 2026-10-30 |
| SPD-3d | parity 按属性组 restore、并行计算完整闭包键与 afterKey；check 构建固定版本标记并跨提交复用未变单元 | 进行中（`refactor/SPD-3d`，待主机冷/热验收） | 2026-11-01 |
| DES-1 | 主机工具、旧工具和工作台共用一份会话状态；MCP 宿主生命周期在工作台可见、可重启 | 进行中（`refactor/DES-1`，`dc3123b1`） | 2026-11-01 |
| S0 | 文档状态刷新：发布计划、交接、路线图、重构计划状态、ADR 0020 修订与 0022–0025、文档矛盾与断链、用户文档的“引擎”措辞、`AGENTS.md`/`CLAUDE.md`、设计规范清理 | 进行中（待审查） | 2026-11-01 |
| VMFIX-1 | 读超时迫使重启 TIA、失败读取污染会话、`OUTCOME_UNKNOWN` 误用、L2 通用工具失败、`expectedProject` 语义、去掉设备白名单 | 进行中（`refactor/VMFIX-1`，`f5bd3798`） | 2026-11-01 |
| DES-5 | 大结果分页；`DecodePlcSimaticMl` 的 Python 依赖 | 进行中（Codex，`refactor/DES-5`） | 2026-11-01 |
| SU-1 | 软件单元（V16–V21）：导入、导出、目录导入的单元范围；`SWImportOptions` 与文化选项显式开启；树、列表、交叉引用含单元内容；`SetPlcUnitObjectAccess` 工具（预览加审批） | 进行中（`refactor/SU-1`） | 2026-10-31 |
| DES-2 | 一项功能一个实现（L2 工具转到 Foundation 实现）；安全移块（导入核实后再删除） | 排队（VMFIX-1 之后） | 2026-11-01 |
| RULES-1 | 三档验证（受影响、nightly、发布只核对）、nightly 45 分钟门槛、发布证据按 ADR 0025、机器级重型作业锁、`regen-all`、Codex 沙箱可跑子集；发布默认不等 CI | 排队（SPD-3/SPD-5 合并后） | 2026-11-01 |
| RELAY-1 | 代理安全的远程连接：随包本地 stdio 中继，私网地址绕过 HTTP(S)_PROXY、其他地址遵守 NO_PROXY；环境检查新增“代理与远程连接”项及一键修复 | 排队 | 2026-11-01 |
| DES-3 | 每个写入都能预览（`dryRun` 默认 true），多步写入失败时回滚；含下文“DES-3 措辞跟进” | 排队（DES-2 之后） | 2026-11-01 |
| DES-4 | 统一参数模型，参数错误指出具体字段，返回脱敏的原生消息与诊断编号（加 `innererror`），按请求号查调用日志；参数模型写 ADR | 排队（DES-3 之后） | 2026-11-01 |
| UI-v2 | 工作台按已采用的 [EplanMcp 工作台设计系统](https://claude.ai/artifact/Kbjk7iPWb7cMGxdVLbaQsg)重新设计：Apple HIG 基础，强调色 #0071E3，Segoe UI 加 Microsoft YaHei UI，字号 28/22/17/14/13/12，4 px 网格，侧栏 232 / 列表 320 / 详情三栏；界面用语“服务”“项目”。取代 ADR 0018、0021 的视觉部分和“4.0 保留 Ant 蓝、4.1 再重设计”的决定；与项目红线的冲突（审批与拒绝按钮等权重、高对比主题）按工作台规范处理。设计规范由 UI 设计代理起草，之后分批实施 | 进行中（规范起草中） | 2026-11-01 |
| P8-04n | 八版统一对外呈现：精简清单默认启用、服务器说明、提示词与资源、档案名、包装与审批路径、行为披露 | 未开始 | 2026-10-13 |
| P8-21f | 删除工作台重复的 TIA 适配代码（含 VCI 平铺工作区与 MCP `UngroupedDevices` 对齐） | 未开始 | 2026-10-08 |
| P8-84 | 工作台 TIA 进程选择器：多个 TIA 进程时由用户选择，按 PID 精确附着 | 未开始 | 2026-10-21 |
| FB-VM | beta.2 VM 发现登记进[测试者反馈台账](../development/tester-feedback.md)，修复后在 VM 复测 | 未开始 | 2026-11-01 |

SPD-3d 的 P-C 已于 2026-11-01 批准：合并和 nightly 的 check 构建统一使用固定 `check` 修订标记，构建单元及 parity 键不包含 HEAD；输入闭包、输出逐文件哈希、冷构建抽查和 deadline 均保留。release 构建仍写入提交号，独立构建发布二进制，不能把 check 输出当作 release 二进制。P-A（parity 派生自 build-once 的已验证节点键）作为后续优化评估，须覆盖普通布局专有 import。

### DES-3 措辞跟进

以下工具描述仍把当前产品称为“引擎”，或按旧规则描述批处理准入。生成的[工具矩阵](../reference/tool-matrix.md)只能在源码修改后重新生成，因此随 DES-3 一起改源码再运行生成器：

| 工具 | 源码 | 现有措辞 |
|---|---|---|
| `GetOpennessWorkerStatus` | `src/Shared/Host/McpServer.Worker.cs:14` | “FoundationHost owns one engine worker per MCP session” |
| `CheckProductUpdate` | `src/Shared/Host/McpServer.Maintenance.cs:61-62` | “compares this engine's version…”“The engine never replaces its own files” |
| `GetNativeInvocationLog` | `src/Shared/Host/EngineeringDiagnosticsTools.cs:62` | “instrument engine-owned Openness methods” |
| `GetOpennessCompatibility` | `src/Shared/Host/EngineeringDiagnosticsTools.cs:36` | “compiled engine capabilities” |
| `DeleteExportHandle`、`ListExportHandles` | `src/Shared/Host/ExportTools.cs:97`、`:48`（参数 `exportId` 在 `:30`） | “this engine session” |
| `ManageTeamcenterConnection` | `src/Shared/TeamcenterTools.cs:21` | “connection of this engine session” |
| `RunReadOnlyToolBatch`、`PreviewToolBatch` | `src/Shared/Host/McpServer.Batch.cs:24`、`:57` | “explicit [READ]/[WRITE] tools”，与 P8-82 审查后的 BatchRead/BatchWrite 标志规则不符 |

工作台界面中的“引擎设置”等字符串（`src/Studio/Gui/Localization/Strings.FeaturePages.cs`）按界面用语改为“服务”，随 UI-v2 处理。

## 4.0.0-beta.4

主题：T-IA 对照缺口批次和生态小项（维护者 2026-10-30 定为 4.0 范围，2026-11-01 定为 beta.4）。每项先由 Opus 核对官方 API，再写任务说明。

| 编号 | 内容 | 状态 |
|---|---|---|
| TIA-TEST | 持久 PLC 测试用例库（PLCSIM Advanced、批量运行、结果历史、官方 TIA Test Suite 用例）、FAT/SAT 报告、支持复现报告；测试库 JUnit 报告与稳定退出码 | 未开始 |
| TIA-SAFE | 机密保管库；下载安全（实物与 PLCSIM 确认、先保存并编译、兼容指南）；高风险确认；审批状态读取工具 | 未开始 |
| TIA-BATCH | 批量与递归的块、变量、UDT、HMI 变量操作；变量 CSV/Excel 交换，扩到 HMI 变量与报警、文本列表、DB 起始值 | 未开始 |
| TIA-GAPS | 监控/强制表缺口（`PlcWatchTableComposition.Create`、`PlcForceTableComposition.Import`）；Unified HMI 缺口（删除连接与变量表、建脚本模块、V21 图形列表、嵌套运行设置）；V17–V19 库 `CreateFrom`；工程概览、新建与打开选项；专题使用指南 | 未开始 |
| ECO-S | Siemens 官方 tia-portal-ai-extensions 技能进入 `GetToolUsage`（MIT，署名）；参数预设与强制预设；导出时剥离易变节点（可选编译成功后自动提交） | 未开始 |
| SELF | `ManagePlcDataBlockSnapshot` 的注册范围与官方 API 核对（ValueService 只在 V21 文档化）；`ManageProjectCompilationSettings` 描述改为 V17 起可用 | 未开始 |
| P8-97b | 单块、块组编译：先核对官方 API 是否支持，支持才加范围参数 | 未开始（API 核对后定） |

## 4.0 正式版（rc → 4.0.0）

门槛见上文。除 beta.3、beta.4 的全部任务外，还需完成：

| 编号 | 内容 | 状态 |
|---|---|---|
| P8-04l2 | Q1 移除生成器引擎读取；Q2 删除 `src/Engine` 残余源码、引擎专属测试/脚本/夹具与诊断链接，保留代码的测试转入 KeptCode；不保留残余源码、不要求引擎专属测试的继任测试（维护者 2026-11-02，ADR 0022）。宿主引擎客户端、目录和死条件分支另由 Q3 清理 | 残余源码清理完成（Q2，待审查与主机验证）；Q3 待执行 |
| P8-04l3 | 为 P8-04l 退役的 34 个引擎候选 check-all 作业补 PLC worker 版 | 未开始 |
| P8-10d/e/f | 发布链 Python 改 C#（快照、打包、验包），重新移植 10-21 以来的发布链修复，rc 证明字节一致 | 进行中（`refactor/P8-10d`、`refactor/P8-10e` 有未验证的 WIP） |
| P8-93b、P8-94b | 未被 check-all 覆盖的发布链检查并入 check-all；TiaMcp.Logic 统一构建属性，package-test 报告退役文件与日志排除 | 未开始 |
| SPD-6/6b | 按用例成本分片，压缩 nightly 时长（按 45 分钟门槛重新评估） | 进行中（`refactor/SPD-6b`） |
| DOCS | 文档重组（2026-10-22 批准的方案，批次 D0–D3a、D4a 和图 1–2；含 P8-04m、P8-63 剩余项与待补写的 ADR；方案中语言策略与 archify 的 ADR 编号改为 0026、0027）；从交付列表移除 `JetBrainsMono-OFL.txt`、`NotoSansSC-OFL.txt` | 未开始（S0 状态刷新属于 beta.3） |
| RENAME-GH | GitHub 仓库改名为 automation-engineering-workbench，同步 README、NOTICE、插件和发布工具中的仓库地址（执行前再次确认） | 未开始 |
| RC | 用完整发布链生成 rc；定稿 [4.0 发布说明](../releases/v4.0.0.md)；4.0 起初不向 3.3 更新器提供 | 未开始 |

## 4.1

| 范围 | 内容 | 决定 |
|---|---|---|
| P8-31f–i | 库与母版实例化、`CheckProjectStandard`、工作台生成模块、状态模型包；P8-31e6（SafeV4 候选规则，随 SafeV4 启用）、P8-31h1（审批抽屉扩展） | 2026-10-22 |
| P8-40 | 表格与 ECAD 导入、Classic VB 到 Unified JS、V20/V21 补深度、按版本输出的构建工具 | 2026-10-22 |
| P8-95a–g | EplanMCP 借鉴项：VM HTTP 加固、“工程内容是数据”的服务器说明与审批卡清理、防误点、退出确认与单实例、Claude Desktop（MSIX）配置路径、CI 加固、doctor/audit 的 `schemaVersion` | 2026-11-01 |
| T-IA 大项 | 块接口编辑与 SCL 体替换（带回滚）；硬件缺口（改 CPU 型号、子网删除与创建、插槽兼容、命名 CPU/网络/PN 属性、AML/GSD 预检）；异步作业与取消；离线快照与撤销；工程报告与交付物；PLCSIM Advanced 扩展；Classic HMI 动画与事件；GRAPH/FBD/STL 生成；流水线与工作计划；帮助与文档搜索；多工程会话；模式库内容；显示密码；VCI 工作区分组；一次调用导出整个工程到 Git | 2026-10-30 |
| 生态大项 | 整程序导出、磁盘编辑、只导入变化（先预览）；覆盖前自动快照与回滚；PLC 代码规则检查（规则目录、SARIF、评分）；SCL 单元测试库与运行器；CI 入口与 GitHub/GitLab 模板；Siemens 文档搜索；Web API 补全；生成后自检循环；AML 离线核对与 IO 变量生成；DB 注释生成 HMI 报警、导出全部 Unified 画面脚本；GRAPH 文本 DSL 与 STL 控制流 | 2026-10-30 |
| 其他 | 托盘与开机启动；非 Studio 命名空间统一；文档批次 D3b、D4b、D5（4.1.0 后删除重定向页） | 2026-10-22 |

许可规则（2026-10-30）：GPL、AGPL 和专有来源只借鉴思路；复制 Siemens 共享代码前逐份阅读其许可。

## 待维护者决定

| 编号 | 事项 |
|---|---|
| D-P1 | 4.0 正式版门槛是否另加“没有未决的 blocker 报告”（2026-10-22 的版本有，2026-10-30 的版本未提） |
| D-P6 | `transport-usage-v18` 每次应答等待 15 秒，满载时可能超时：提速、单独预算或保持（改阈值属放宽） |
| D-P7 | `ReleaseUnifiedReadCursor` 是否移出只读批处理 |
| D-P8 | `docs/reference/capabilities.md` 是否为预发布增加条目（发布命令会打印 WARNING） |
| D-P9 | HMI 审查遗留的 D2、D5、D7；B2、B9 是否已随 HMI-B3 完成 |
| D-P10 | 线程模型（V20/V21 MTA、旧版 STA）；是否用预览版 MCP SDK 与 Microsoft.Extensions 发布 4.0 |
| D-P12 | 任务编号规则：继续用 P8-xx，还是沿用 VMFIX、DES、SPD 等主题编号 |
| D-J1 | P8-31j1（生成框架方案的修订与示例）随文档重组在 4.0 完成，还是随 P8-31f–i 进入 4.1 |
| D-D4 | `docs/releases/v3.2.0.md`、`v3.3.0.md` 保留与否 |
| D-D6 | 4.1 是否把生成的工具矩阵并入版本工具目录 |
