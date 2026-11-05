# 验证

状态：现行 · 最后核对：2026-11-04，master `ef1988bc`

本文规定合并检查、发布前完整检查和发布后真机验证的职责，依据 [ADR 0031](../standards/decisions/0031-release-path.md)。

## 合并检查

合并门为 `merge-check`：使用普通 `dotnet build` 和 `dotnet test` 构建、测试受影响的项目，再检查生成内容和仓库规则；目标约 10 分钟，不把性能目标当作功能正确性的替代。从仓库根目录运行：

```powershell
dotnet run --project build-tools/release -- merge-check
```

命令按 merge-base、工作树和未跟踪文件选择普通构建与完整注册套件；`-DryRun` 显示路径选择原因，`-All` 选择全集。合并门不读取证据或结果缓存；Foundation 分片保留注册总数门槛和派生子集自己的门槛。只有纯 `src/FoundationHost/`、`src/Logic/` 命中可以按 D1 不选 GUI。日志和各作业耗时写在 `bin-build/merge-check/`；首次冷构建及宽范围改动可能超过十分钟。 传输检查始终使用 `full` 档，每次最多并行检查 4 个版本，并遵循合并门的 `-MaxParallelism` 设置（设为 1 时串行检查）。

GitHub CI 保留独立检查，但不阻塞合并，也不代替本机检查。不再设置 nightly；每个发布候选前由主机手动运行 `merge-check -All`，覆盖全部 38 个注册套件及源码检查。审查时核对实际运行的项目、失败与阻塞项，而非只看命令退出或旧结果缓存。

Codex 沙箱只构建受影响的 Debug 项目并运行涉及的测试类；例如把 `<测试类全名>` 替换为实际改动的类名：

```powershell
dotnet build tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/TiaMcp.WorkerChannel.Tests.csproj -c Debug
dotnet test tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/TiaMcp.WorkerChannel.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~<测试类全名>"
```

主导代理在主机运行 `merge-check`、`prepare`、解决方案构建和注册套件；注册套件的正式运行使用 `test-suites -Suite <名称>`，保留注册的参数、平台差异、最低通过数和最大跳过数。未迁移的控制台工程使用 `dotnet run --project <工程> -c Release`；`dotnet test` 不执行其控制台断言。需要 Siemens 程序集的单工程构建传入该版 `-p:SiemensEngineeringDirectory=<绝对路径>`；不要给八版工程统一传入某一版 SDK。

## 发布前交付包检查

交付包入口是 `prepare`，步骤见[发布流程](release.md)，目标约 15 分钟。它构建八版交付物、生成 ZIP，在隔离目录测试提取包，并对 checkout 的运行文件与 ZIP 做字节核对。它不构建或运行注册套件。任一交付检查失败都不得写入可发布候选；每项作业运行到底并汇总结果，不因先前失败漏掉后续作业。

### 注册套件与数量门槛

套件名称、项目、过滤条件、构建属性及数量门槛的权威来源是 [`tests/test-suites.json`](../../tests/test-suites.json)。`merge-check -All` 读取全部条目，Foundation 使用现有分片执行器；所有结果要求零失败、通过数达到 `minimumPassed`、跳过数不超过 `maximumSkipped`。平台例外只采用注册表已声明的值，不临时放宽。

下表直接摘录核对的 master 注册表，共 38 个套件。经批准的测试精简会更新注册表，本表不授权删减或调整门槛。

| 套件 | 最低通过数 | 最大跳过数（Windows） |
|---|---:|---:|
| `operator-host` | 68 | 0 |
| `generation-planning` | 260 | 0 |
| `generation-model` | 129 | 0 |
| `release-tool` | 720 | 0 |
| `kept-code` | 5,155 | 0 |
| `version-policy` | 10 | 0 |
| `write-guard` | 8 | 0 |
| `crash-evidence` | 7 | 0 |
| `updater` | 33 | 0 |
| `foundation` | 27,820 | 1 |
| `foundation-api` | 28,812 | 1 |
| `software-read` | 258 | 0 |
| `special-export-shape` | 12 | 0 |
| `device-add` | 116 | 0 |
| `hardware-catalog` | 57 | 0 |
| `diagnostic-membership` | 10 | 0 |
| `prompt-registration` | 195 | 0 |
| `adapter-contracts` | 1,850 | 0 |
| `worker-channel` | 275 | 0 |
| `engine-host` | 159 | 0 |
| `shared-session` | 6 | 0 |
| `shared-pipeline` | 6 | 0 |
| `worker-robustness` | 570 | 0 |
| `shared-lifecycle` | 6 | 0 |
| `source-contracts` | 140 | 0 |
| `workbench-control-protocol` | 94 | 0 |
| `workbench-control` | 96 | 0 |
| `workbench-control-host` | 98 | 0 |
| `mcp-panel` | 48 | 0 |
| `workbench-vci-prefill` | 63 | 0 |
| `vci-read-only-binding` | 13 | 0 |
| `plugin-launcher` | 32 | 0 |
| `plc-exchange-worker-v20` | 128 | 0 |
| `plc-exchange-worker-v21` | 128 | 0 |
| `shared-lifecycle-plc` | 46 | 0 |
| `worker-progress-foundation` | 3 | 0 |
| `studio-core` | 553 | 0 |
| `studio-gui` | 2,958 | 4 |

### 保留检查的执行位置

| 范围 | 检查与判定 |
|---|---|
| 提取的交付包 | `Test-ReleaseSmoke.py`、`Test-RelocatedBundle.py`：环境初始化、写入拒绝、更新器、启动器、搬迁及只读安装行为 |
| 捆绑运行时 | 宿主与工作台确实加载随包 .NET；不借用开发机器上的运行时证明交付完整 |
| 写入保护 | 随包 `TiaMcp.WriteGuard.exe` 拒绝未知 V4 工具，读工具通过；默认审批对八个版本均在派发前拒绝无工作台的写请求 |
| 原生边界 | 八版适配器的 weaver 校验；`native-mcp-session-self-test` 至少 8 项，`nativelifecycle-self-test` 至少 1 项（必须大于 0），保留现有判定 |
| 合并门：适配器输入与 worker 隔离 | `src/Adapters/build/Test-AdapterInputs.cs`、`Test-WorkerIsolation.cs`：核对八版输入选择与 worker 隔离，必须产生通过的结果记录 |
| 合并门：原生诊断夹具 | `Test-NativeDiagnostics.py --fixture <诊断测试程序>`：行为检查至少 36 项、拒绝检查至少 5 项，`nativeTiaExecuted` 必须为 false |
| 合并门：V20/V21 worker | `native-safety`、`usage`（`check_usage`）和 `campaign-inputs`；构建 ApiCompileChecks、Adapters.Diagnostics.Tests 与 Native.Tests。Worker、适配器、工具目录或相应检查变更以及宽范围计划时选择 |
| 合并门：传输与行为 | `foundation-transport`、`foundation-transport-plc` 在套件之后运行，使用源码宿主、独立数据目录和 V20/V21 SDK worker 夹具。FoundationHost、EngineHost、WorkerChannel、PlcWorker、Shared、传输脚本或夹具变更，以及宽范围计划时选择；HTTP 使用本机回环合成服务 |
| 合并门：工作台配置与生态 | `configuration-page` 控制台检查保留至少 157 项；`Test-V21Ecosystem.py --self-test` 保留解码器、PDF 及伴随 PLC Tools 依赖 |
| 契约与交付字节 | 八版 `Snapshot-ToolContracts.py` 实际捕获后比较工具定义；核对测试过的 `runtime/` 与 ZIP 中的文件一致，包括随包更新器 |
| 暂存布局 | 交付规则、必需文件、包资源、测试策略程序集拒绝、Openness 用户组运行时及程序集依赖闭包 |
| 分析器 | 使用本次构建的报告运行分析器棘轮，不接受新增警告或过期报告 |

仓库 lint 和检查工具自身的测试属于合并门，不重复加入交付包检查。审批和契约所需的 harness 与 V20/V21 SDK worker 夹具仍由 `prepare` 构建；`runtime/verification` 的冻结 weaver 仍用于八版包内适配器检查。以上保留项没有因迁移而获准降低通过数、增加跳过数或过滤失败。

## 已批准移除的检查机制

维护者在 2026-11-03 批准以下移除，工具清理已由 NEW-REL-3 执行。

- 延迟、耗时及性能增长门槛；性能数据用于趋势报告。功能测试的短时限后续改为防挂起保护，由计划中的 HANG-GUARD 任务实施，不能自行放宽现有断言。
- HTTP 稳定性压测（包括 c8/c16 和重复轮次）。普通 HTTP 功能、传输、审批及故障恢复检查保留。
- 独占静默阶段及围绕它的 CPU/内存调度。
- 套件普通输出与隔离输出的 `suite-output-parity` 比较。
- 全部八版逐工具响应捕获、响应基线及 `response-verify`。工具定义契约快照和功能测试保留，不能把删除响应捕获解释成允许改变 MCP 契约。
- nightly、旧证据采纳、构建收据和哈希链比较。准备阶段仍验证源码未漂移、包摘要和测试字节一致。

维护者在 2026-11-04 批准移除 `host-behavior-parity` 作业及其最低通过数门槛。该作业重复运行已退役引擎路径的比较；`BehaviorParityTests` 仍由 `foundation-api` 套件执行，使用保留的夹具与 Foundation 路径，不运行已退役的引擎程序。

维护者在 2026-11-04 通过 PREP-SLIM 批准删除 `PlcReleaseJobs` 中的 PLC 模拟作业及专用 harness 模式、覆盖率、资源、生态和重复 PLC 契约捕获；这些作业不迁到合并门。八版包内契约捕获、审批、注册套件及数量门槛保留，真实行为在发布后验证。

主机测试精简仅限维护者批准的重复准入用例：版本重复的准入类用最旧与最新版本覆盖；审批、写安全、worker 故障恢复及协议测试保留完整覆盖。`StagedGeneration` 属于写安全，不能按普通准入用例裁减。迁移引擎测试时先保留仍有产品意义的安全与回滚测试。

## 生成内容、源码与文档检查

工具示例只在 `reference/tool-examples` 维护。示例变更后，从仓库根目录依次生成，再构建与检查八版检索及功能：

```powershell
dotnet run --project build-tools/release -- generate-catalogs
dotnet run --project build-tools/release -- generate-tool-usage
dotnet run --project build-tools/release -- generate-tool-counts
```

各生成器的 `-Check` 逐字节检查。迁移表使用 `generate-release-migration`。工具定义有意改变时，用正式捕获命令生成新契约并逐项审查差异；不手改哈希。规划 golden 的重生成方式见[说明](../../tests/Generation/TiaMcp.Generation.Planning.Tests/Golden/README.md)，`TIA_MCP_UPDATE_GOLDEN=1` 只用于有意改变的输出，完成后取消该变量并正常复测。

### 工具契约兼容检查

八个发布键分别通过 `Snapshot-ToolContracts.py capture --exe` 从对应宿主捕获工具定义，再用 `compare --baseline manifest/contracts/v4/baseline --current <捕获目录>` 检查。基线守护八版工具名称、输入与输出 schema、注解、描述哈希、行为能力及八版 lite 名单；格式 2 在每个文件的 `schemas` 表中保存输出 schema，由工具的 `outputSchemaSha256` 引用，加载时还原完整输出 schema，捕获文件与基线仍逐字节比较。删除工具或参数、新增必填参数以及类型、枚举、默认值变化均须审查，不能只运行离线 `verify` 代替实际捕获。逐工具响应基线的移除范围见上文；契约快照继续保留。

### 注释与 MCP 中文门禁

`check-ratchet -Kind comments`、`check-ratchet -Kind mcp-text` 和 `check-ratchet -Kind swallowed` 保留源码文本及异常处理棘轮。它们与 `check-script-tool-calls`、`check-dead-tool-references`、`check-bundle-layout` 一起检查仓库约束；不得手动放宽基线或忽略失败。

文档变更检查每个改动文件的链接、锚点、路径、命令参数与工具适用版本。新增、移动或删除文件还须执行：

```powershell
dotnet run --project build-tools/release -- check-repository -NoBinaries
```

仓库静态检查保留交付布局、工具调用、适配器边界、吞异常说明、注释与 MCP 文本、版本与生成内容漂移检查。分析器报告和完整程序集校验按受影响范围执行。静态包检查不等于二进制包已通过测试。

## 发布后验证真实行为

发布后工作流只验证 ZIP 与 sidecar 的 SHA-256。真实 TIA 行为由发布后的 VM 回归和测试者验证，使用[真机测试手册](real-machine-testing.md)及[预发布真机验证清单](../guides/beta-tester-checklist.md)，结果先记[测试者反馈](../plan/progress.md)，复核后写入真机台账。

检查实际对象、文件、读回、编译和保存结果；部分完成或结果未知时先检查项目现状，不能重放写入。离线套件通过、工具枚举和公开下载均不证明 Siemens API 的真实语义正确。连接 TIA、PLC 或 VM 需要独立授权，本机合成服务不扩大该授权。
