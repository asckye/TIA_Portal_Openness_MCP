# 验证

状态：现行 · 最后核对：2026-11-05，beta.4 候选基线 `13eb7725`

本文规定合并检查、发布前完整检查和发布后真机验证的职责，依据 [ADR 0031](../standards/decisions/0031-release-path.md)。

## 合并检查

合并门 `merge-check` 使用普通 `dotnet build` 和 `dotnet test` 构建、测试受影响的项目，再检查生成内容和仓库规则。FAST-GATE 目标约 10 分钟；2026-11-05 宽范围改动实测 731 秒（约 12 分钟），优化前约 21 分钟。从仓库根目录运行以下命令。

```powershell
dotnet run --project build-tools/release -- merge-check
```

命令按 merge-base、工作树和未跟踪文件选择普通构建与完整注册套件。`-DryRun` 显示路径选择原因，`-All` 选择全集。合并门不读取证据或结果缓存。Foundation 的共享计时历史来自主 checkout 的 `bin-build/check-timings`，只决定分片分配，未知测试回退到哈希分桶，数量仍由合并后的 TRX 判定并拒绝重复测试。其他分片调用可通过 `-TimingHistory <目录>` 指定历史，默认使用当前 worktree。Foundation 分片保留注册总数门槛和派生子集自己的门槛。只有纯 `src/FoundationHost/`、`src/Logic/` 命中可以按 D1 不选 GUI。

构建完成后，适配器输入、worker 隔离与 updater 并行执行，随后单独执行原生诊断，避免其 Python 进程与 updater 的捆绑进程探测重叠。全部结束后启动套件池，Foundation 优先入池。池结束后执行独占作业，再让 GUI 与两组传输检查并行。GUI 使用四个分片进程，先独占运行鼠标和焦点敏感分片，再并行运行其余三个布局等分片，敏感分片失败后仍完整运行并汇总普通分片，Foundation 等其他套件的各通道继续并发。每个进程使用独立的 TEMP、数据根和审批配置，管道名称从分片独立的设置路径派生。GUI 合并后的 TRX 保留注册总数门槛及四个派生子集自己的门槛。日志、各作业耗时和带开始、结束偏移的 `timeline.tsv` 写在 `bin-build/merge-check/`，耗时随改动范围和主机负载变化。传输检查始终使用 `full` 档，每组最多并行检查 8 个版本，各版本先完成 stdio 再进入 HTTP。GUI 和各组传输的并发上限还受合并门的 `-MaxParallelism` 限制（设为 1 时串行检查），传输脚本单独调用时默认仍为 4。

任一传输检查出现 WinError 10055 时，本轮失败并写入主 checkout 的 `bin-build/transport-parallelism-4.txt`。主 checkout 与共享计时历史使用同一个 `git rev-parse --path-format=absolute --git-common-dir` 定位结果，后续所有 worktree 的合并门将两组传输检查各限制为最多 4 个版本。该标记独立于任务 worktree 及每轮重建的日志目录。标记生效且本轮选择传输检查时，合并门输出一行提示，列出标记的完整路径及清除方式。维护者审查 socket 证据后，可删除提示中的文件以恢复 8 个版本。

`merge-check` 将本次测试指标和各成功作业的耗时与主 checkout 历史中同指标、同单位最近至多 20 条记录的中位数比较，至少有 5 条历史且本次值严格超过中位数的 2 倍时，在摘要和 `bin-build/merge-check/trend-notice.txt` 中列出 `Slower than usual` 提示、指标、当前值、中位数与比值。提示及历史缺失、坏行或读写失败均不改变退出码，异常以单行说明，提示文件首行记录提交和 UTC 时间，`prepare` 摘要会输出该文件并标明来源提交是否为当前 HEAD，旧文件的来源标为未知。

GitHub CI 保留独立检查，但不阻塞合并，也不代替本机检查。不再设置 nightly；每个发布候选前由主机手动运行 `merge-check -All`，覆盖全部 38 个注册套件及源码检查。审查时核对实际运行的项目、失败与阻塞项，而非只看命令退出或旧结果缓存。

Codex 沙箱只构建受影响的 Debug 项目并运行涉及的测试类；例如把 `<测试类全名>` 替换为实际改动的类名：

```powershell
dotnet build tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/TiaMcp.WorkerChannel.Tests.csproj -c Debug
dotnet test tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/TiaMcp.WorkerChannel.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~<测试类全名>"
```

主导代理在主机运行 `merge-check`、`prepare`、解决方案构建和注册套件；注册套件的正式运行使用 `test-suites -Suite <名称>`，保留注册的参数、平台差异、最低通过数和最大跳过数。未迁移的控制台工程使用 `dotnet run --project <工程> -c Release`；`dotnet test` 不执行其控制台断言。需要 Siemens 程序集的单工程构建传入该版 `-p:SiemensEngineeringDirectory=<绝对路径>`；不要给八版工程统一传入某一版 SDK。

## 自动审查检查

这些检查由 `build-tools/release` 实现；保留现有分析器棘轮，不另建基线或分析器作业。

| 检查 | 执行位置 | 选择条件 | 合并门成本 |
|---|---|---|---|
| `check-loosening -Base <ref>` | `merge-check` 的并行阶段，失败阻塞 | 所有非纯文档计划，包括 `-All` | 仅差异扫描，通常数秒 |
| `complexity-report -Base <ref> -Output <file>` | 同一并行阶段，始终退出 0 | 同上；写入 `bin-build/merge-check/complexity-report.md` | 仅变更的 C# 文件，通常数秒 |
| 57 条新增轻量安全规则 | 每次编译，由现有分析器棘轮判定 | 始终启用，保留原有 5 条安全规则 | 编译成本由主机测量 |
| 38 条数据流规则 | `prepare` 构建，属性 `TiaSecurityAnalysisProfile=release` | 仅物理 `src/` 源文件，包含链接源文件 | 不增加合并门成本 |
| `check-vulnerable-packages [-Output <file>]` | 完整 `prepare` 首步、独立 CI 工作流 | `-BuildOnly` 跳过；合并门不联网 | 不增加合并门成本 |

纯文档计划仍只运行原来的三个检查。复杂度报告按方法超过 60 行或估算圈复杂度超过 15、文件超过 600 行列出，新增超限项排在前面；它是 Roslyn 语法估算，包含条件编译分支和注释行，不是新门禁。不带 `-Base` 时扫描已跟踪的 C# 文件，排除 `third_party/`、`reference/`、`archive/`，供后续评分使用。

### 检查变更审批

`check-loosening` 对比 merge-base 与当前工作树，包含已提交、暂存、未暂存和未跟踪文件。它检查测试删除、跳过、断言删除、`#if false`、测试编译排除、套件过滤缩小、套件最低通过数和最大跳过数，以及五份棘轮基线增长。C# 测试按成员名或相同方法体跨文件匹配，正常跨项目移动和只改方法名不会报告删除；改为非源码扩展名或移到未被测试工程编译的目录则按文件删除报告。测试工程只取 tests/test-suites.json 注册的项目，范围静态读取工程文件和可解析的本地导入、默认源码目录、显式 Include/Remove/Exclude。整份测试或检查源码文件删除只产生一项报告。项目/props 文件移动不视为测试源码删除，注册套件消失仍报告。`InlineData`、`MemberData` 属性和直接 `TheoryData` 初始化行、Add 调用按断言检查；行重排不算删除。tests/ 与 scripts/ 中的 C# 方法体及顶层程序 `Check(...)`、`check(...)` 按断言检查，src/ 中的同名调用不按断言检查。Python 的函数体和全局语句中的 assert、require/check 调用也检查。只有顶层关系式的常量上限或下限收紧才免报；嵌套表达式内部的数字变化仍报告。对于下限型 Check，逐个比较所有直接数值参数，任意位置的数值降低或数值参数消失都报告。正向断言保留原条件并新增合取条件不误报，删除合取条件会报告；平台覆盖值按原有继承值比较，新增覆盖也不能降低门槛。数值检测只覆盖维护的明确位置：`MergeCheck.CheckResult`、`PlcReleaseChecks.BuildReleaseCountRules`、`PrepareChecks.RequirePreparedFoundationApproval` 的下限，`SuiteHang.Timeout`、`PublicationBudget.RemainingMilliseconds` 和 xUnit `Timeout` 的上限；Python 检查脚本中命名的 timeout、min/max（含 minimum/maximum）、threshold/margin/budget 参数及其命令行默认值也检查。C# 数值按成员身份及出现顺序比较，删除尾部数值也报告门槛删除；修改错误消息不再掩盖数值放宽。

实现者不能批准自己的发现。任务说明应在实现前列出计划修改的断言、阈值或用例及原因。主导审查者将当前发现逐项与该说明核对，确认没有超出已批准范围后记录；`-Reason` 必填且不得为空，应写任务说明或维护者决定的名称。它是审查引用，不代表工具能验证决定内容。示例：

```powershell
dotnet run --project build-tools/release -- check-loosening -Base master -RecordApprovals -Task TASK-ID -Reason "TASK-ID.md：维护者批准的断言变更" -MaintainerDate 2026-11-04
```

审批保存到 [`approved-check-changes.json`](../../build-tools/release/baselines/approved-check-changes.json)，格式为 `{"format":2,"tasks":[]}`；每组包含 `task`、`maintainerDate`、`reason`，以及具有精确 `path`、`kind`、`identifier` 的 `items`。用 `-ApprovalOutput <file>` 可先写到草稿。记录命令不自动批准或提交：审查生成项后，只提交审批文件，英文提交主题必须以 `Record maintainer approval` 开头。检查单独列出新增审批及来源违规。审批文件的每次修改，无论是否同时修改代码，都必须来自这种独立审批提交；未提交编辑、只有 JSON 的普通提交、混合提交均不能批准发现，删除或只改格式也报告来源违规。记录命令拒绝当前差异中不存在的新增审批项（包括手改的未提交审批和已有输出草稿中的额外项）；已提交且来源有效的历史审批仍保留。缺少 `-Reason` 时不写入文件，也没有默认批准理由。不支持格式 1、通配符或整类豁免。

### 安全规则和漏洞扫描

默认分析器配置为 `merge`；`release` 增加数据流规则，跨过程方法与 lambda 调用链均限制为 1。配置及当前 profile 参与增量编译输入，分析器收据记录 profile；旧四字段收据按 `merge` 处理。仅发布配置发出的警告在合并配置中休眠，不报缺失或 STALE；发布构建仍拒绝新增警告。

分析器基线仍只减不增。首次启用一组新规则时，维护者可使用 `check-analyzer-warnings -InitializeRules <逗号分隔规则> -ReviewData <审查文件> -Base <ref>`，并传入对应的 `-DiagnosticsDirectory`；命令拒绝基线已有的规则、原来已启用的规则、曾初始化的规则及未审查的新增项。初始化成功后在同一基线文件的 `initializedRules` 中记录规则；零行规则也记录，后续缩减基线保留该历史，删除初始化记录会触发审查。发布专属规则只接受包含物理 src 的 release 报告，不能用 merge 报告初始化；每条审查行都要求对应源文件的匹配 profile。`-UpdateBaseline -Complete`、`-Initialize` 和禁止 `-AllowGrowth` 的原有语义保留。本次不自动记录新警告。

漏洞扫描运行当前解决方案的 `dotnet list ... package --vulnerable --include-transitive --format json --output-version 1 --source https://api.nuget.org/v3/index.json`，清除继承的离线恢复配置，要求返回源包含该 URL。项目只有 `path`、没有 `frameworks` 是有效的干净结果。未批准的 High/Critical 退出 1；Low/Moderate 只报告。网络、恢复、超时或无效报告写 `not run: <首条错误>` 并退出 2，不能报告为通过；缓存可返回有效咨询数据，不强制证明实时联网。单次上限 300 秒。

### 重新生成内容的审查提示

`tia-feature-expectations.json`、契约 baseline 和 golden 数据可能由正式工具有意重建。这些文件的每项差异在控制台及 JSON 的 `regeneratedFiles` 中列为 `regenerated: review the diff`，不作为 `check-loosening` 的阻塞发现，也不写入检查变更审批。使用对应正式生成或捕获工具后，仍须逐项审查差异并通过原有生成器、契约与功能检查。

### 已知限制

这是语法检查，不能证明所有改写保持或削弱了检查，也不能判定重新生成的基线或 golden 内容是否正确、是否确实来自正式工具；重新生成提示不能替代审查。仍有以下边界，独立 AI 审查必须核对：

- 自定义测试属性、别名和反射注册；除列明的 Assert、Check 等形式外的自定义断言包装；动态 MemberData、循环生成或跨方法构造的 TheoryData 无法展开为真实用例集合。
- 测试源范围只读取可直接解析的本地 XML 导入及路径；条件 MSBuild、无法展开的属性、SDK/包内导入和生成源码可能使推断与真实编译不同。它不替代真实构建及发现测试。
- 数值比较只识别支持的字面量形式：C# 的已列成员按数字出现顺序对应，签名、数字数量或顺序同时改写仍需人工核对。Python 的 if 分支阈值仍不作语义判定，只检查命名上限/下限、timeout/growth/default 及断言表达式；普通常量别名、任意控制流中的阈值和间接计算不作语义推导；Python 限制名称改写、删除或重排后无法保证对应到原参数。更严格判断只支持数字在比较右侧的简单关系式，复杂逻辑可能报告需审查。
- Python 使用文本扫描覆盖函数和全局语句，不执行代码；跨文件或嵌套同名函数、名称碰撞、字符串中的类似语法仍需复核。
- 未注册 gate 项目内部的移动与限额源文件重命名已记入主导审查者收件箱，本轮不处理。
- 非测试文件删除、拆分文件，以及从 `MergeCheck.cs` 或 `PrepareChecks.cs` 计划中删掉作业不保证检出。初始化 profile 核对不证明全仓源码都已构建，主导审查者仍须提供完整构建报告。

`PlcWorker` 不在解决方案中，缺少发布键时不能直接恢复；其依赖通过 `Logic` 和 `KeptCode.Tests` 扫描。合并门不联网，CI 在每次推送 master、每周及手动触发时扫描，但不阻塞合并；完整 `prepare` 的新扫描负责发布阻塞。规则编译开销由主机测量，不增加计时门槛。

## 质量评分

主导代理可在 `prepare` 前或收到真机结果后按需运行：

```powershell
dotnet run --project build-tools/release -- scorecard
dotnet run --project build-tools/release -- scorecard -Base v4.0.0-beta.2 -Output bin-build/scorecard-review -ReviewedSample 4
```

该命令只读源码和已有结果，不启动测量作业，不进入 `merge-check` 或 `prepare`，不阻塞合并或发布。除选项错误返回 64 外返回 0；输出失败会打印原因。可用 `-PublicApiRoot <目录>` 指定本地八版 SDK，`-Previous <scorecard.json>` 指定比较报告，`-Seed <文本>` 指定抽样种子。分档是待批准的建议，见 [ScorecardBands.cs](../../build-tools/release/ScorecardBands.cs)。报告不设总分；每个维度采用其已测指标最低分，有缺失项则标为 `partial`。

默认输出为主检出的 `bin-build/scorecard/<yyyyMMdd-HHmmss>-<sha>/`，包括 `scorecard.md`、`scorecard.json` 和 `sample.txt`；两个报告均记录耗时，默认比较该目录下最近的已有报告。主检出通过 Git 的公共目录定位，失败时使用当前根目录。`sample.txt` 从基准（默认最近的 `v*` 标签）到 HEAD 变动的 `src/**/*.cs` 按 SHA-256 确定十个文件，默认种子为发布标签；独立 AI 审查者按名字清楚、注释解释原因、方法单一职责和周边一致性评分，再以 `-ReviewedSample` 填入平均分。命令本身不调用 AI。

`merge-check` 完成时向主检出的 `bin-build/scorecard/merge-check-runs.jsonl` 追加 UTC、提交、分支、范围、耗时、失败作业和输出目录；写入失败仅打印一行，不改变退出码。评分读取其中最新且仍存在的输出目录，缺失时尝试当前检出的 `bin-build/merge-check/`。删除 worktree 后其中的 TRX 不再可读，应记为 `not measured`。这些文件全部忽略入库。

真机记录位置和格式见[真机测试](real-machine-testing.md#评分使用的运行记录)。没有来源或不可读为 `not measured`，其他版本有有效记录而当前版本没有为 `no evidence`。主检出 `bin-build/vm-acceptance/4.0/` 的旧开发版记录仅展示行数、涉及工具数和日期范围，标为 `stale`；不提高评分。官方 API 覆盖率来自 SDK 文档成员与适配器织入清单的交集，不证明工具可达或实机可用，反射调用和通用样板 API 不计入，100% 不是目标。性能仅报告合成传输夹具的中位耗时变化，不评分。

命名诊断读取当前运行的 SARIF；运行不是宽范围代码检查或缺少范围记录时，指标及可读性维度标为 `partial`，不把只重建一个项目当作完整测量。验证耗时的窄范围、宽范围中位数均排除纯文档运行。抽样排除已删除文件；文档日期盘点包含 `docs/README.md`。死引用检查失败时，该项分数为 0。

## 发布前交付包检查

交付包入口是 `prepare`，步骤见[发布流程](release.md)，2026-11-04 实测约 6 分钟。它构建八版交付物、生成 ZIP，在隔离目录测试提取包，并对 checkout 的运行文件与 ZIP 做字节核对。它不构建或运行注册套件。任一交付检查失败都不得写入可发布候选；每项作业运行到底并汇总结果，不因先前失败漏掉后续作业。

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
| 分析器 | 使用本次构建及对应 profile 的报告运行现有棘轮；`prepare` 使用 `release`，不接受新增警告或过期报告 |

仓库 lint 和检查工具自身的测试属于合并门，不重复加入交付包检查。审批和契约所需的 harness 与 V20/V21 SDK worker 夹具仍由 `prepare` 构建；`runtime/verification` 的冻结 weaver 仍用于八版包内适配器检查。以上保留项没有因迁移而获准降低通过数、增加跳过数或过滤失败。

## 已批准移除的检查机制

维护者在 2026-11-03 批准以下移除，工具清理已由 NEW-REL-3 执行。

- 延迟、耗时及性能增长门槛。HANG-GUARD 已将批准清单中的短时限改为 120 秒防挂起保护，故意触发超时的预算、功能断言和数量门槛继续保留。测试耗时以 `TREND` 和 JSONL 记录，`merge-check` 汇总到 `bin-build/merge-check/trend.jsonl`，并带 UTC 时间和 HEAD 追加到主 checkout 的 `bin-build/trend.jsonl`，不按性能值判定通过或失败。
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
