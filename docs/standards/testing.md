# 测试策略

状态：现行 · 最后核对：2026-11-01，master `902a86fe`

[文档目录](../README.md) · [验证分层](../development/validation.md) · [开发流程](../process/development-workflow.md) · [安全标准](security.md)

本文说明测什么、测到什么程度、哪些结果算证据。具体命令、参数和输出目录见 [验证分层](../development/validation.md)；本文不复制命令手册。什么时候运行哪一档检查见 [开发流程](../process/development-workflow.md) 第 4 节。

## 1 原则

- 开发机没有 TIA Portal。离线检查证明构建、协议、契约和宿主行为，不证明原生行为；原生验收状态以 [真机台账](../reference/real-machine-ledger.md) 为准，未运行时写 `NOT RUN`。
- 测试不连接 TIA Portal、PLC、虚拟机或任何外部网络服务。测试可以使用自己在 127.0.0.1 上启动的合成服务（HTTP 夹具、命名管道夹具），注册套件、preflight 和传输检查不能因此跳过（[AGENTS.md](../../AGENTS.md)）。
- 检查只收紧不放宽：删除、跳过、过滤、放宽或重设任何检查、测试、断言、阈值和门禁，都须维护者批准（ADR 待办 B36，见 [架构决策记录](../development/decisions/README.md)）。
- 生成文件和基线只由正式写入方更新，差异逐项审查（第 4 节）。
- 替换代码路径时补对等测试；新命令至少真实运行一次；夹具使用真实格式的数据。

## 2 测试分层

| 层 | 内容 | 证据位置 | 证明什么 | 不证明什么 |
|---|---|---|---|---|
| 注册的离线套件 | xUnit / VSTest 工程，按 `tests/test-suites.json` 运行 | 运行目录中的 TRX 与汇总报告 | 宿主、worker 通道、审批、生成、发布工具、工作台等的离线行为 | 原生 TIA 行为 |
| 契约捕获 | 八个发布键的工具名称、输入 schema 和 lite 名单 | `manifest/contracts/v4/baseline` | 输入契约没有意外的破坏性变化 | 返回结构与原生语义 |
| SDK / PublicAPI 形状 | `foundation-api` 与带 `publicApiRoot` 的套件使用 `sdk/` 中的 PublicAPI；生成的接口片段按各版本官方 XSD 校验 | 套件 TRX | 编译与 API 形状、片段与 XSD 一致 | 整个文档或原生导入成功 |
| 生成器与仓库检查 | 生成器的 `-Check`、`check-repository`、死引用、分析器警告棘轮、注释与 MCP 中文门禁 | 命令输出与基线文件 | 生成文件、链接、随包路径、工具名引用和警告没有漂移 | 运行时行为 |
| 示例检查 | `GetToolUsage` 检索全部工具与操作，执行返回的离线示例 | `manifest/tool-usage-coverage.json` | 每个注册工具和操作都有示例，离线示例可执行 | 示例参数在真实项目中正确 |
| 宿主进程与稳定性 | 对 FoundationHost 与会话 worker 做正常调用与错误后恢复，包括假 worker 的超时、异常退出、错误响应和迟到响应 | 稳定性输出目录与构建记录 | 宿主的故障处理与恢复 | Siemens 内部的可靠性 |
| 工作台界面 | Studio Core 与 GUI 套件的两种路径模式（`studio-*-false`、`studio-*-true`） | 套件 TRX | 界面逻辑、布局、主题和无障碍断言 | 真实 TIA 项目上的操作 |
| 真机验收 | 在匹配版本与许可的 TIA Portal 上读写往返、编译和显式保存 | 真机台账 | 原生行为 | 其他版本和其他项目 |

真机验收不由我们在发布前执行；预发布由测试者验证，结果记入真机台账（[ADR 0020](../development/decisions/0020-prereleases-tested-by-users.md)）。2026-11-01 维护者另决定按真机核对清单在虚拟机上做回归，该修订随 S0 文档批次写入 ADR 0020。

## 3 套件注册表

`tests/test-suites.json` 是注册套件的唯一来源。每个套件登记：

| 字段 | 含义 |
|---|---|
| `project` | 测试工程路径 |
| `minimumPassed` | 最少通过数；低于它即失败 |
| `maximumSkipped` | 最多跳过数；超过它即失败 |
| `arguments` | 传给测试运行器的参数，例如 `--filter` 表达式 |
| `platforms` | 可选；按平台覆盖 `minimumPassed` 与 `maximumSkipped`，必须写 `reason` |
| `splitFrom` | 可选；从哪个原套件拆出 |
| `companions` | 可选；必须同时运行的伙伴套件 |
| `publicApiRoot` | 可选；为 `true` 时传入 PublicAPI 根目录 |

规则：

- 失败数必须为零；重复的测试身份被拒绝。
- 用 `dotnet run --project build-tools/release -- test-suites -Suite <名称>` 运行并核对注册门禁；`dotnet test` 只用于开发时的快速运行。未注册的控制台工程用 `dotnet run --project <csproj> -c Release`，`dotnet test` 不执行它们的断言。
- 新增的测试工程在 `tests/test-suites.json` 中登记后，才由 `test-suites` 和 `check-all` 按门禁运行。
- 降低 `minimumPassed`、提高 `maximumSkipped` 或删除套件都属于放宽，须维护者批准。功能按设计减少用例时，在报告中列出删除的用例和原因，由维护者决定。
- `check-all` 的派生子集只用同轮超集 TRX，保留子集注册的最低通过数、最多跳过数和零失败要求（[验证分层](../development/validation.md)）。

## 4 基线与 golden

功能按设计改变时，用仓库内的正式工具重新生成受影响的基线，在报告和合并审查中逐项列出差异：

| 基线 | 正式写入方 |
|---|---|
| 契约基线 | `Snapshot-ToolContracts.py capture`，再用 `compare` 审查差异 |
| 规划 golden | 设置 `TIA_MCP_UPDATE_GOLDEN=1` 运行 `generation-planning` 套件；移除开关后再正常运行一次（[golden 说明](../../tests/Generation/TiaMcp.Generation.Planning.Tests/Golden/README.md)） |
| TIA 功能检查 | `check-tia-features -Capture` |
| 嵌入目录、工具用法、工具数量块、迁移表 | `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`、`generate-release-migration`，按此顺序运行，各自的 `-Check` 逐字节核对 |
| 分析器警告基线 | `check-analyzer-warnings -UpdateBaseline`，只能删除已消失的条目 |

- 不手改测试哈希、`manifest/*.json` 中的哈希和生成文件。
- 重新生成基线不授权修改阈值或断言。改阈值、改断言、删除或跳过检查仍须维护者批准。
- 把全部生成器与捕获合成一条 `regen-all` 命令已于 2026-11-01 决定，由 RULES-1 实现，尚未提供。

## 5 必须覆盖的失败路径

新代码和改动的代码必须测试下列失败路径中与它相关的部分。括号内是现有的同类测试，可作写法参考。

- 审批：拒绝、超时、工作台不可用都在派发前返回 `CONFIRMATION_REQUIRED`；读取不排队；默认开启审批的检查与关闭审批的功能检查都要有（[验证分层](../development/validation.md)“压力与故障检查”）。
- worker 与通道：超时、异常退出、错误响应、迟到响应、帧超限、未知或重复回复（`worker-channel`、`worker-robustness` 套件；故障由 `TIA_FIXTURE_FAULT` 注入）。
- 结果未知：返回 `OUTCOME_UNKNOWN`，会话锁定，不自动重放。
- 输入边界：路径越界、保留名、重解析点、数量与大小上限、格式错误（标准包与导入暂存的测试，见 [安全标准](security.md) SEC-08、SEC-09）。
- 敏感值：带凭据的参数不进入审批摘要、计划哈希、审计、调用日志和响应（SEC-06）。
- 篡改：被改动的记录、哈希或来源文件必须被对应的 `-Check` 或校验拒绝。
- 参数错误：`INVALID_ARGUMENT` 指出出错的参数（[V4 工具协议规范](../development/design-spec/v4-protocol.md) 第 5.1 节）。

## 6 稳定性与计时

- 计时敏感的测试方法逐项登记在 `tests/Shared/TimeSensitiveTests.cs`；Studio Core/Gui、Updater 和 ShippedTools 的分片框架使用这份清单。
- 单项测试保留五分钟挂起门槛；准备检查的每个作业另有整项超时（[验证分层](../development/validation.md)）。
- 出现偶发失败时保留证据并定位原因，不靠重跑把结果变绿。P8-91（master 上连续 20 次干净运行）用来证明稳定性，状态见 [当前交接](../development/handoff.md)。

## 7 测试中不做的事

- 不运行 live 测试分支，不解禁原生交叉引用。
- 不把开发结果缓存、`-Quick` 报告或合并门的结果当作发布证据（[开发流程](../process/development-workflow.md) 第 4 节）。
- 不在测试夹具中放真实项目、凭据、机器地址或未脱敏的现场日志。
- 不按进程名或模糊匹配结束进程；只结束自己在本 worktree 中启动、并核对过路径的进程。
