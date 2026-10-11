# P8-31 分阶段生成执行设计

状态：现行（设计）· P8-31e1–e4 已实施，e5、e6、h1、j1 未实施，任务状态以 [重构计划](../refactor-plan.md) 为准 · 最后核对：2026-10-11，master `3a227657`

[设计规范索引](../design-spec/README.md) · [架构决策记录](../decisions/README.md) · [生成框架方案](../phase8-generation-framework.md) · [架构概览](../design-spec/architecture.md)

本设计写于 2026-10-10，调研依据是 master `3300186b` 和 P8-31e 的实施提交 `2fc60da1`。维护者于 2026-10-10 按推荐回答了第 11 节的 Q1–Q14；e1–e4 已合并到 master（`b0f0eda9`、`6127d939`）。2026-10-11 移入仓库时删除了工作树路径、本地报告引用和以 P8-31e 工作树为准的行号，代码位置改为 master 上的文件、类型和方法名；设计判断未改写。[生成框架方案](../phase8-generation-framework.md) §6.3、§6.5–6.7 中与本设计冲突的内容以本设计为准（修订属于 P8-31j1）。调研阶段只读：未构建、未测试，未连接 TIA、引擎或宿主进程，所有原生行为仍按 `current / NOT RUN` 处理。

## 0 结论摘要

- **阻塞点**：生产策略下，八个版本的所有行为族都是 `current`（`src/Logic/ModelContextProtocol/ToolProfiles.resx` 的 `behaviorPolicies`）。完整 PLC 生成实际缺的只有外部源的三个工具：`ImportPlcExternalSource`、`GenerateBlocksFromExternalSource`，以及清理时要用的 `DeletePlcExternalSource`。八个版本都走同一个 `PlcExternalSourceWorkflowPolicy`：预览哈希包含进程号、工程文件和目标源清单；生成步骤还包含会话内的源标识 `SourceIdentity` 和全部块/类型的 `ModifiedDate`。apply 时重算哈希，必须与预览相等。`current` 下的编译和保存没有 `expectedPlanHash`，P8-31e 已经能执行。它们和 XML/S7DCL 导入的候选形态（`mode=preview/apply`，会话内存放预览）只出现在 SafeV4 测试策略下，作为同一机制的第二组规则处理。
- **推荐 D3（A 加三项强化）**。Apply 逐步执行。凡是需要预览哈希的写步骤，执行前先调用该工具**自己的预览**：走同一条 `CallToolCore` 路径，只读，读取当时的真实状态。预览结果按逐工具规则投影后，与计划里已审批的“预期投影”比较：
  - 决定性字段必须一致，例如目标、文件 SHA-256、源名、要新建的对象、不得覆盖的对象；
  - 见证字段只检查约束，例如会话标识、清单哈希、时间戳。

  比较通过后，才把预览哈希填进唯一的迟填参数 `expectedPlanHash`，用覆盖最终参数的单步授权执行；工具 apply 时照常重算哈希。三项强化是：运行期间的**会话写栅栏**、跨步骤的**运行事实链**（导入结果 → 生成预览 → 清理预览）、写入后的**效果核对**。任何一处不符就停止，状态记为 `diverged`，不写入。重新审批通过重新规划完成，每次 Apply 仍只审批一次，符合生成框架方案中维护者的 Q6 决定。
- **不推荐 B（每阶段审批一次）**。N 个源的导入和生成会互相改变对方的预览哈希，按阶段审批会退化成每步审批，又回到缺口 G6。
- **不推荐 C（工具接受计划授权，替代预览哈希）**。它要改八版适配器策略、worker 协议、引擎移植路径和 V4 候选会话，等于在每个写工具里开第二条准入路径；而旧版的生成步骤恰恰靠这个哈希防止覆盖已有块。
- **新建工程**推荐在同一次运行内换绑。`CreateProject` 的工程文件路径由 `MutationIdentityPolicy` 确定（`<dir>\<name>\<name>.ap<major>`），计划按这个路径预先绑定后续步骤。工程建成后核对四项：同一进程、路径一致、绑定纪元前进、读回为空工程；都通过才采用新绑定继续。
- **归属**：每个生成对象记录“计划期期望指纹 + 执行后的原生见证”。V20/V21 的见证用 `GetPlcObjectFingerprints`；旧版用块/类型信息里的修改时间，编译、保存后是否稳定需要 L5 确认。`<project>.tiagen.json` 由新的宿主 FILE 工具 `PublishGenerationRecord` 写入。它是计划里可见的最后一步，同样要预览、核对前一份记录的哈希、走单步授权并进审计。数据目录里的运行日志仍是事实来源，记录可以由日志重建。
- **部分运行后重新规划**：规划时读取记录和运行日志。已归属且见证未变的对象记为 `skip(owned-unchanged)`；结果未知的对象一律记为冲突，可以显式 adopt。计划种子加入“谱系”，即该工程最近一次运行的 id 和终态。这样任何一次运行之后重新规划，都会得到新的 planId；没有运行时，同样的输入仍得到同样的哈希。
- **维护者决定**（2026-10-10，第 11 节，全部按推荐）：
  - §6.3 的“计划内参数全部预先确定”改为“决定性参数全部预先确定，`expectedPlanHash` 等见证由工具预览迟填”（Q2）；
  - 发散后只停止，重新规划后再审批，不就地续批（Q3）；
  - 新建工程在同次运行内换绑（Q4）；
  - 记录文件放在工程文件夹内，与 `.apXX` 同级，待 L5 确认（Q5）；
  - 编译成功后删除本运行导入的外部源（Q6）；
  - 旧版见证用块/类型信息的修改时间，待 L5 确认稳定性（Q7）。

## 1 现状与缺口

本节描述设计时（P8-31e 合并前）的执行路径与缺口；之后由哪个子任务修正、实施到什么程度，见第 10.1 节与重构计划。

### 1.1 P8-31e 的执行路径

| 环节 | 代码 | 要点 |
|---|---|---|
| 规划 | `src/Shared/Host/GenerationExecutionTools.cs` | 只接受已绑定的已有工程；读回 → `GenerationPlanner.Build` → `PrepareArguments` |
| 参数绑定 | 同上 | 按目标工具 schema 填 `expectedProjectFile`、`confirm`、`mode`。schema 有 `expectedPlanHash` 的步骤整阶段标为不可用，依赖它的阶段连带不可用 |
| 执行 | 同上 | 一次 `PendingApproval{Kind="plan"}`；批准后写已消费声明；每步 `Check`（身份 + 10 分钟过期）、参数摘要核对；`GenerationStepApproval.Enter`；`DispatchGenerationStep` → `CallToolCore` |
| 单步授权 | `src/Shared/ApprovalProtocol.cs`；`src/Shared/ApprovalPipe.cs` | AsyncLocal，进程内；按发布键、工具名和**完整参数**的规范摘要 `InvocationArgumentsDigest` 匹配，不符即 denied |
| Foundation 准入 | `src/FoundationHost/FoundationV4Tool.cs` | 写调用先用 preview 做内部预检，再 `ApprovalClient.Wait`，批准后重算摘要 |
| 引擎准入 | `src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs` | `PrecheckBeforeApproval` → `WaitForApproval` → 进入 lane 后复核摘要 → `beforeDispatch` |

### 1.2 写工具的三种复核形态

| 形态 | 典型工具 | 生产策略 | 预览存放 | apply 校验 | 预览哈希内容 |
|---|---|---|---|---|---|
| 无哈希（`dryRun`/`confirm`/`expectedProjectFile`，或无 dryRun） | `CreatePlcTag`、`CreatePlcTagTable`、`CompilePlcDiagnostics`、`SaveProject`、`CreateProject`；无 dryRun 的 `CreatePlcBlockGroup`、`MovePlcBlockToGroup` | 八版 `current` | 无 | 工具自身前置检查与工程身份 | 无 |
| 无状态哈希（`dryRun` + `expectedPlanHash`） | 外部源导入/生成/删除（八版同一 `PlcExternalSourceWorkflowPolicy`，V20/V21 走移植路径）；导出、批量导入、S7DCL 文档导入 | `current` | 不存放，apply 时重算 | `confirm`、`expectedProjectFile`、重算哈希相等（`src/Adapters/Native/Plc/PlcExternalSourceWorkflowPolicy.cs`） | 导入：操作、版本、工程文件、PID、`softwarePath`、`groupPath`、源名，加文件路径、文件 SHA-256 和现有源名清单。生成：同样的身份字段，加 `SourceIdentity`、`GenerationOption`，以及全部块/类型的 kind、path、name、typeName、language、isConsistent、`ModifiedDate` |
| 会话候选（`mode=preview/apply`） | P6-SOURCE/COMPILE/CLOSE/IMPORT/EXPORT/SESSION/DEVICE 候选 | 仅 SafeV4 测试策略（`TiaMcpTestPolicy`） | 会话内 `plans` 字典，每族上限 128，消费后不移出 | 必须是本会话存过、未消费、重算相等的预览（`src/Logic/V4/SourceCandidate.cs`；`CompileCandidate.cs`；`SaveCloseCandidate.cs`） | 统一的 V4 `Plan`：identity（PID、进程启动时间、工程、绑定纪元、文件）、inputHashes、inventoryHash、operations、warnings（`src/Logic/V4/Plan.cs`） |

为什么规划期算不出这些哈希：

- `SourceIdentity = externalDeleteIdentities.Get(source)`（`src/Adapters/Native/Plc/PlcExternalSourceWorkflow.cs`）是 worker 会话内的不透明标识，源导入之前根本不存在。删除预览的 `targetIdentity` 用的是同一个标识空间（`PlcExternalSourceDelete.cs`），这一点后面的事实链会用到。
- 生成预览的 `ObjectsBefore` 含每个块/类型的 `ModifiedDate`，前一个生成步骤就会改变它。规划期的 READ 工具（`GetSoftwareTree`）只给名称和路径。
- 导入预览的现有源名清单会随同一运行内前面的导入而变化。清单可以预测，但要得到哈希字符串，必须在规划器里复刻适配器的公式（见 3.4 D1）。
- 候选形态的 `inventoryHash` 是整个观测结果的哈希，而且 apply 要求本会话存过这份预览，规划期无法替代。

### 1.3 与本设计相关的 P8-31e 其他问题

| # | 问题 | 依据 | 建议 |
|---|---|---|---|
| 1 | 规划器把块分组当作 `ImportPlcExternalSource` 的 `groupPath`，八版策略只接受空串。生成的块落在程序块根组 | `src/Logic/Generation/GenerationPlanner.cs`；`PlcExternalSourceWorkflowPolicy.cs` | 源一律导入根组；块分组用后续步骤 `MovePlcBlockToGroup`（八版都有，`autoCreateGroup=false`，分组由 plcStructure 步骤建）。UDT 没有移动工具，旧版类型分组只做检查 |
| 2 | 每步 `Check` 都检查 10 分钟过期，长运行会在中途以 partial 停止 | `GenerationExecutionTools.cs` | 过期只约束审批前和首个写入前（4.9） |
| 3 | 已消费声明只按 planId 记录：第 1 步在操作前被拒、工程没变时，重新规划得到同一 planId，会被拒绝 | `src/Logic/Generation/GenerationRunStore.cs` | 谱系种子（第 8 节） |
| 4 | 重新规划会把上次生成的对象全部报成冲突：观测到的块只有名称和分组，期望对象带代码/接口指纹，比较时视为“未核实字段”；源同理 | `src/Logic/Generation/ProjectModelComparison.cs` | 归属记录（第 6、8 节）。没有它，§6.5 的“新计划只含缺失步骤”不成立 |
| 5 | SafeV4 候选会话的 `plans` 上限 128，消费后也不移除：同一会话里同族预览超过 128 次就返回 `LIMIT_EXCEEDED` | `SourceCandidate.cs`；`CompileCandidate.cs` | 消费后移除，或改为有界 LRU（e6） |
| 6 | 步骤 request 审计行的 `planHash` 字段有两种含义：有工具哈希时写工具哈希，否则写生成计划哈希 | `src/Shared/AuditInvocation.cs` | 步骤行一律写工具自己的哈希（没有就留空），生成计划哈希只放在父 Apply 行和运行记录里 |

## 2 必须保持的安全性质

| 编号 | 性质 |
|---|---|
| S1 | 每个写入都由**该工具自己的**前置检查，在写入前一刻对照真实状态检查；预览哈希相等仍由工具自己判断，执行器只能多拒，不能少拒 |
| S2 | 人审批的是实际会发生的事：目标、内容、要新建或可能被覆盖的对象、调用的原生操作、写入的文件 |
| S3 | 结果未知即停，锁定会话，不自动重试，不重放；运行记录给出核对建议 |
| S4 | 每一步都进审计（request/start/end，带 planId/stepId/runId/parentRequestId），运行记录能逐步重建 |
| S5 | 没有旁门：每步都经 `CallToolCore` 和各宿主的准入、版本门禁、lane、调用日志；授权不能从 MCP 参数、管道元数据或批次中伪造 |
| S6 | 每次 Apply 只审批一次（维护者决定）；需要阶段之间检查时，按阶段分几次 Apply |
| S7 | 版本可用性在规划时标注，不可用的阶段整体跳过 |

**决定性字段与见证字段**：决定性字段改变了“将发生什么”，人必须事先看到，例如目标 PLC、源文件内容哈希、源名、要新建的对象名、禁止覆盖的对象、原生调用种类、写入的文件路径。见证字段只证明“状态没变”，本身不承载决定，例如工具预览哈希、会话内源标识、清单哈希、修改时间、绑定纪元。D3 只允许见证字段迟填或迟到，而且必须满足计划声明的约束。

## 3 方案比较

### 3.1 A：分阶段执行，工具预览后比对再执行

机制：执行到某个写步骤时，先用审批过的参数派生预览参数（`dryRun=true` 或 `mode=preview`，去掉 `expectedPlanHash`），经 `CallToolCore` 调用。预览结果按该工具的投影规则提取字段，与计划步骤的预期投影比较；一致就把预览返回的哈希填进 `expectedPlanHash`，再以单步授权执行。

- 优点：
  - 不改任何写工具，工具自己的预检和哈希相等检查原样保留（S1）；
  - 每次 Apply 一次审批（S6）；
  - 生产策略下八版用同一套外部源规则，因为是同一个策略类；
  - 与已有的 `ApplyToolBatch` 先存预览、执行前复核的做法（`src/Shared/Host/McpServer.Batch.cs`、`StablePreview`）一致。
- 缺点：
  - 每个写步骤多一次预览，加上宿主内部预检和工具 apply 时的重算，原生读取约为原来的 2–3 倍；
  - 执行器要维护逐工具的投影规则，与结果形态耦合，需要用真实策略驱动的测试兜底；
  - 步骤之间有监管空隙，例如导入后、生成前被别人换了同名源；
  - 迟填与 §6.3 的字面原则冲突，需要维护者修订原则。
- 代码改动：plan 步骤增加 `review` 块（schema、模型、规划器）；执行循环；规则注册表；单步授权改为覆盖最终参数并核对迟填槽。

### 3.2 B：每阶段审批一次（改变身份的阶段之后重新审批）

机制：只执行一个阶段就停下，再对剩余部分重新规划。为了让下一阶段的参数带上真实的 `expectedPlanHash`，规划时必须对下一阶段的写工具做预览，把哈希写进参数，下一次 Apply 再审批。

- 优点：审批过的参数逐字节就是最终参数，符合 §6.3 的字面要求；执行器不需要投影规则。
- 缺点：
  - **会退化**：每次导入都会改变源清单，而源清单在下一次导入的哈希里；每次生成都会改变 `ObjectsBefore`，而它在下一次生成的哈希里。同一集合上的步骤只能各自成一个阶段，N 个源大约要 2N 次审批，回到缺口 G6。
  - 人在卡片上看到的仍是一串哈希，可读性不比 A 的预期投影好。
  - 规划期一样要做写工具预览，而且需要补一套“续规划”协议。
  - 不在同一会话里连续进行，事实链更长。
- 代码改动：续规划入口（带上次运行 id）；规划期预览写工具并嵌入哈希；按集合拆阶段；工作台多张卡片串联显示。

### 3.3 C：写工具接受“计划授权”，替代预览哈希

机制：工具在宿主内部授权存在时，跳过 `expectedPlanHash` 相等检查，改为检查调用方传入的结构性期望（如 `expectedInputSha256`、`forbidExisting`、`expectedSourceName`），其余前置检查照旧。

- 优点：每步只预览一次（宿主预检），执行器不需要投影规则；结构期望写进工具契约，可以复用。
- 缺点：
  - 每个写工具都多出第二条准入路径。旧版工具在 PlcWorker 进程里，授权必须跨进程传给 worker。威胁模型要求“MCP 客户端不能经参数、元数据或批次自批”，这条路径一出错就成了旁门；
  - 旧版生成没有碰撞检查，只能靠 `ObjectsBefore` 哈希防覆盖（结果 DTO 写明 `MayOverwriteExistingBlocks => true`、`SourceContentReviewed => false`）。去掉哈希就必须在适配器里新写原生侧的防覆盖逻辑，属于靠近原生边界的策略改动，八版都要 L5 复验；
  - 改动范围大：`PlcExternalSourceWorkflowPolicy`（编进 8 个 worker）、`src/FoundationHost/WorkerProtocol.cs`、`FoundationTools.cs` 定义、八版契约基线（新增参数）、V20/V21 移植路径、`src/Logic/V4/*Candidate*.cs` 都要动。
- 结论：违背“绝不绕开工具自己的预检”的本意（哈希相等本身就是预检的一部分），成本也最高。

### 3.4 D：其他做法

| 变体 | 内容 | 结论 |
|---|---|---|
| D1 哈希预测 | 在规划器里复刻各工具的哈希公式，规划期直接算出 `expectedPlanHash` | 导入可行（输入都可预测）；生成（`SourceIdentity`、`ModifiedDate`）和候选（`inventoryHash`）不可行。还与八版内部公式强耦合，**否决** |
| D2 组合批量工具 | 新增一个经过复核的工具，一次请求完成 N 个源的导入加生成。预览用“计划中的源”占位，SafeV4 的 `SourceSession` 已有类似模拟（`SourceCandidate.cs`） | 会引入新的公共工具和新的原生调用序列，需要 L5；哈希仍依赖当时的清单，Apply 时还是要预览。4.0 **不做**，以后可以用来减少步骤数 |
| **D3 A + 强化（推荐）** | A，再加：①会话写栅栏（同一会话里非本运行的写调用一律拒绝）；②运行事实链（后续步骤的预期引用前面步骤的结果）；③写后效果核对（生成的对象必须与声明一致）；④可选的规划期试预览（第 11 节 Q10） | 补上 A 在步骤之间的空隙，并且能发现“工具成功了但效果超出声明”这种情况 |

### 3.5 对照

| | A | B | C | **D3** |
|---|---|---|---|---|
| S1 工具自身预检 + 哈希 | 保留 | 保留 | 哈希被替代 | 保留 |
| S2 人看到的是实际发生的事 | 预期投影 | 哈希（可读性差） | 结构期望 | 预期投影 + 禁止集 + 效果声明 |
| S3 结果未知即停 | 是 | 是 | 是 | 是 |
| S5 无旁门 | 是 | 是 | 每个工具多一条准入 | 是 |
| S6 每次 Apply 审批一次 | 是 | 退化为约 2N 次 | 是 | 是 |
| 步骤之间被插入写入 | 只有工具 apply 能发现 | 同左 | 同左 | 写栅栏 + 事实链 |
| 改动面 | 执行器、Logic、schema | 规划器、续规划、工作台 | 8 个 worker、协议、契约、引擎 | A + 宿主准入里的栅栏 |
| 原生读取次数 | 约 2–3 倍 | 约 2 倍 | 约 1 倍 | 约 2–3 倍 |

## 4 推荐方案（D3）详细设计

### 4.1 计划步骤的 `review` 块

计划增加两个对象：`observed`，即规划期观测到的、按站点分组的源名集合和块/类型键集合，是预测的基准；每个步骤增加可选的 `review`。两者都在计划哈希的覆盖范围内，所以审批覆盖它们。示例（V19，已有工程）：

```json
{
  "id": "s00012", "phase": "plcProgram", "key": "PLC1/externalSource/source_3f…_9a….scl", "op": "create",
  "tool": "ImportPlcExternalSource",
  "arguments": { "softwarePath": "PLC1", "groupPath": "", "filePath": "D:/…/generation/artifacts/staging/gen-…/source_3f…_9a….scl",
                 "dryRun": false, "confirm": true, "expectedProjectFile": "D:/Projects/Line1/Line1.ap19" },
  "argumentDigest": "sha256:…",
  "review": {
    "mode": "tool-preview", "rule": "external-source-import/1", "lateArguments": ["expectedPlanHash"],
    "expect": { "softwarePath": "PLC1", "filePath": "…", "inputSha256": "…", "byteCount": 2048,
                "sourceName": "source_3f…_9a….scl", "sourcesBefore": "observed+run" },
    "effect": { "sourcesAdded": ["source_3f…_9a….scl"] }
  }
}
```

```json
{
  "id": "s00013", "tool": "GenerateBlocksFromExternalSource", "…": "…",
  "review": {
    "mode": "tool-preview", "rule": "external-source-generate/1", "lateArguments": ["expectedPlanHash"],
    "expect": { "softwarePath": "PLC1", "sourceName": { "fact": "s00012.sourceName" },
                "generationOption": "None", "objectKeysBefore": "observed+run" },
    "witness": { "sourceIdentity": "capture-as-fact", "objectsBefore.modifiedDate": "ignore", "objectsBefore.isConsistent": "ignore" },
    "forbid": { "objectKeysBefore": ["block:FB_MotorDol", "type:UDT_MotorHmi"] },
    "effect": { "generated": ["block:FB_MotorDol", "type:UDT_MotorHmi"], "others": "forbidden" }
  }
}
```

V14 SP1 的 `generationOption` 为 `parameterless`，效果按 `observedChanges` 核对（`src/FoundationHost/FoundationV4Result.cs`）。新建工程的 `CreateProject` 步骤用 `"mode": "transition"`（第 5 节）。记录步骤用 `"rule": "generation-record/1"`（第 6 节）。全部步骤的迟填参数只有 `expectedPlanHash` 一个，其他参数（含源名、文件路径）都在规划时确定。

### 4.2 执行算法

```text
批准后：fence.Acquire(run)；facts = {}
对每个选中的步骤 k（依赖顺序）：
  若已请求取消 → 状态 cancelled，停止（不打断正在执行的步骤）
  Check(binding)；若上一步是 transition，则使用新绑定
  若 step.review.mode == tool-preview：
     previewArgs = Derive(step.arguments)        // dryRun=true 或 mode=preview，去掉 expectedPlanHash
     P = CallToolCore(step.tool, previewArgs)    // 只读、不审批、写调用日志；运行记录记 step-preview
     P 不是 ok → 停止：rejected-before-operation（preview-refused）
     proj = Rules[step.review.rule].Project(P)
     verdict = Rules[...].Compare(step.review, proj, plan.observed, facts)
     固定字段不符或出现禁止项 → 停止：diverged（不写），记录 expected/observed/diff
     finalArgs = step.arguments ∪ { expectedPlanHash: proj.hash }
  否则 finalArgs = step.arguments
  断言：Canonical(finalArgs − lateArguments) == Canonical(step.arguments)，且摘要 == step.argumentDigest
  using grant = GenerationStepApproval.Enter(approval, step.tool, finalArgs, step.review.lateArguments, previewRequestId)
  R = CallToolCore(step.tool, finalArgs, beforeDispatch: binding + 产物 + fence 复核)
  outcome == unknown 或派发异常 → MarkSharedSessionUncertain；停止：unknown
  outcome != succeeded → 停止：outcome（工具在 apply 时发现哈希变化，就是 rejected-before-operation）
  effect = Rules[...].Effect(R)；不符 → 停止：effect-diverged（已写，partial）
  facts += Rules[...].Facts(R)            // 如 sourceName、generatedKeys
最后：fence.Release(run)；写 end 事件
```

宿主内部预检（`FoundationV4Tool.cs`、`McpServer.Approval.cs`）照常运行，不省略。

### 4.3 逐工具等价规则（4.0 范围：`current` 形态）

| 规则 | 固定（必须与计划一致） | 预测（按 `observed` 与事实计算，必须相等） | 见证（只检查约束） | 禁止 | 效果核对（写后） |
|---|---|---|---|---|---|
| `external-source-import/1` | `status=planned`；`projectFile`（规范化后）= 计划工程；`processId` = 当前绑定 PID；`softwarePath` = 规划时解析出的准确路径；`groupPath=""`；`filePath` = 产物绝对路径；`inputSha256`、`byteCount` = 产物；`requestedSourceName` = 计划源名 | 现有源名集合 = observed ∪ 本运行已导入 − 本运行已删除（大小写不敏感） | `planHash`（迟填） | 计划源名已存在（工具自己也会拒绝） | `executed=true`；`sourceName` = 请求的源名；`sourceNamesAfter` = before ∪ {源名}。记事实 `sourceName` |
| `external-source-generate/1` | `projectFile`、`processId`、`softwarePath`；`sourceName` = 事实（同一运行内的导入步骤）；`generationOption`；`resultBasis` | `objectsBefore` 的键集合（kind:path）= observed ∪ 本运行已生成对象 | `sourceIdentity` 非空：首次出现时记为事实，此后必须相等；已有对象的 `modifiedDate`、`isConsistent`、`typeName`、`programmingLanguage`；`planHash` | 本步声明的对象（规划器从自己生成的 SCL 得出）出现在 `objectsBefore` 中，即有覆盖风险 | V15.1+：`generatedObjects` 键集合 = 声明集合，`observedChanges` ⊆ 声明集合；V14 SP1：`observedChanges` 键集合 = 声明集合。记事实 `generatedKeys` |
| `external-source-delete/1`（清理阶段） | `sourceName` = 事实；`groupPath=""`；工程与进程 | 现有源集合同上 | `targetIdentity` = 生成预览记下的 `sourceIdentity`（同一标识空间） | 目标源没有本运行的导入事实 | 删除后验证已不存在；记事实 `deleted` |
| `project-create/1`（transition） | 预览 `projectFile` = 预测文件 | 不适用 | 不适用 | 不适用 | 第 5 节换绑核对 |
| `generation-record/1` | `recordPath` = 计划路径；`previousRecordHash` = 规划时读到的值（或 `absent`） | 记录对象集合 = 本运行成功步骤的事实 | 记录内容哈希、`planHash` | 路径是重解析点，或已有记录被改过 | 文件存在，哈希等于预览值 |

没有预览哈希的工具（`CreatePlcTag`、`CreatePlcTagTable`、`CreatePlcBlockGroup`、`MovePlcBlockToGroup`、编译、保存）不做执行器预览，依靠宿主预检和工具自身检查。效果核对按结果字段可选进行，例如 `MovePlcBlockToGroup` 之后块路径应为 `组/名`。

SafeV4 候选形态（e6）共用一条 `v4-candidate/1` 规则：
- `plan.identity` 必须等于当前绑定（PID、启动时间、工程、纪元）；
- `plan.tool` 等于步骤工具；
- `plan.inputHashes` 等于产物哈希；
- `plan.operations` 的数量、tool、target、arguments 固定，只有声明的见证键可以迟到（`sourceIdentity`、`targetId`、`softwareId`）；
- `inventoryHash` 作为见证；
- `warnings` 只允许白名单中的项。

### 4.4 运行事实链

- 事实只来自本运行内成功步骤的结果和预览，按 `stepId.字段` 引用，写入运行记录（`fact` 事件），供后续步骤的预期和禁止集使用。
- 生成只能针对**本运行导入**的源。上次运行遗留、已归属的源，先由本次计划删除（有事实链：删除预览里的标识要与记录一致），再重新导入，监管链不跨运行。
- 禁止覆盖由两层保证：生成预览时检查禁止集，执行时工具重算 `ObjectsBefore` 哈希。只要预览与执行之间出现任何新对象，工具就会拒绝。

### 4.5 会话写栅栏与取消

- 运行期间，在同一 MCP 会话（V20/V21 是 `EngineHostPipeline` 的会话状态，旧版是 Foundation 宿主的会话）里，没有当前运行授权的写调用一律拒绝，返回 `PRECONDITION_FAILED`（`generation-run-active`，带 runId）。读调用不受影响。
- 工作台面板经 P8-21c 附着到同一会话时同样受栅栏约束。跨会话由 TIA 进程租约保证：V20/V21 已有，旧版由 P8-21b 加上。
- 准入位置：`CallToolCore` 和 `FoundationV4Tool.InvokeCoreAsync` 判定为写之后、审批之前。这只会多拒，不放宽任何检查。
- 取消：工作台或 MCP 可以请求取消，只在步骤之间生效，状态记为 `cancelled`（不是 unknown），已完成的写入保留。

### 4.6 单步授权

- `GenerationStepApproval.Enter` 改为接收最终参数、迟填槽清单和预览 requestId，并核对以下各点：
  - 去掉迟填槽后的规范参数等于审批过的参数，其摘要等于 `step.argumentDigest`；
  - 迟填槽恰好是 `review.lateArguments`；
  - `expectedPlanHash` 的值来自本运行、本步骤的那次预览。
- 授权摘要按最终参数计算，所以宿主在 `Wait` 中匹配到的就是将要执行的参数。
- 授权仍然只在进程内（AsyncLocal）、一次只对一步，嵌套写调用会被拒绝。
- 关闭审批时照常执行，预览比对、栅栏和效果核对照样进行。

### 4.7 停止类型与重新审批

| 类型 | 时点 | 是否已写 | 运行状态 | Apply 结果 |
|---|---|---|---|---|
| preview-refused | 预览返回非 ok | 否 | `rejected-before-operation` | 第 1 步时为 rejected，否则为 partial |
| diverged | 投影的固定字段或预测字段不符，或出现禁止项 | 否 | `diverged` | 同上 |
| apply-stale | 工具 apply 时重算哈希不等（预览与执行之间有变化） | 否（工具拒绝） | `rejected-before-operation` | 同上 |
| effect-diverged | 工具成功，但效果超出或少于声明 | 是 | `effect-diverged` | partial，写入保留 |
| transition-diverged | 新建工程后身份或空工程核对不符 | 是 | `diverged` | partial |
| unknown | 结果未知或派发异常 | 可能 | `unknown` | unknown，会话锁定 |
| cancelled | 步骤之间取消 | 不适用 | `cancelled` | 第 1 步前为 rejected，否则为 partial |

重新审批：首版**只停止**。Apply 结果和运行记录给出 `nextAction: "replan"` 和差异证据，重新规划（第 8 节）会得到只含剩余步骤的新计划，再走一次审批。就地续批作为第 11 节 Q3 的备选。

### 4.8 运行记录与审计

- 运行日志新增事件：
  - `step-preview`：stepId、预览 requestId、投影、工具哈希、判定；
  - `divergence`：类型、期望、观测、差异；
  - `fact`；
  - `transition`：新旧绑定摘要和核对项；
  - `fence`：acquire/release；
  - `cancel`。
- 预览是只读调用，按现有规则不进审计链，只进调用日志，用 requestId 与运行记录关联。
- 写步骤的审计行沿用 P8-31e 的可选字段；`planHash` 改为只写工具自己的哈希（1.3 #6）。

### 4.9 过期与长运行

- 计划缓存的 10 分钟期限只用于“Apply 时计划是否新鲜”和“审批返回后、首个写入前”。批准后，运行由写栅栏、逐步身份复核和工具哈希约束，不再按计划过期中断。
- 另设运行上限，建议默认 4 小时或 10000 步，可以配置（Q11）。

### 4.10 两类宿主与 SafeV4

- 执行器和规则都在共享代码里（Logic 和 `src/Shared/Host`），旧版 Foundation 宿主与 V20/V21 组合宿主各自经自己的准入派发，规则相同。
- 一致性测试使用有状态的夹具工程，用真实的 `PlcExternalSourceWorkflowPolicy` 驱动假清单，走真实的 `FoundationV4Tool` 和 `CallToolCore` 路径。P8-31e 的实施报告承认当时只测了公共执行器。
- SafeV4 族启用前需完成 e6：候选规则；候选会话消费后移除 `plans`；同一 PLC 编译只出现一次，避免相同哈希被判为已消费。

## 5 新建工程

| 选项 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| 分两次运行 | 先单独执行 `CreateProject`（一次普通审批），worker 绑定新工程后，再以已有工程模式规划和 Apply | 简单；第二次规划读的是真实的空工程 | 两次审批；第一次审批只看到“建工程”，看不到整体 |
| **同次运行换绑（推荐）** | 计划第 1 步是 `CreateProject`，后续步骤按预测的工程文件预先绑定；建成后做 transition 核对并采用新绑定 | 一次审批看到全部；生成失败时，工程是本运行建的，归属清楚 | 执行器要实现换绑规则 |

推荐做法：

1. **规划前提**：已附着 Portal，worker 未绑定工程。`CreateProject` 要求 `lifecycle.RequireUnbound()`（`src/Adapters/Native/Session/PlcFoundationEngine.cs`）。规划用的 `Target()` 增加“已连接、未绑定”的变体，现在这种情况直接报 PortalRequired。
2. **预测身份**：`plan.target.projectIdentity` 改为预测的工程文件，取代现在的 `"new:" + dir + "/" + name`（`GenerationPlanner.cs`）。路径规则与 `MutationIdentityPolicy`（`src/Adapters/Native/Session/MutationIdentityPolicy.cs`）一致，在 Logic 中实现，并用奇偶测试对照适配器里的 `PlcLifecyclePolicy.CreationFile`。后续步骤的 `expectedProjectFile` 都绑定这个路径。规划期的观测模型为空。
3. **执行与换绑**：`CreateProject` 不需要哈希，宿主预检用 preview 校验父目录和名称。执行成功后依次核对：
   - 同一 `processId` 和 `processStartUtc`；
   - `projectPath` 等于预测文件（规范化后不区分大小写）；
   - 绑定 `generation`/纪元前进；
   - 读回 `GetProjectTree` 没有设备，即等于计划假定的空模型。

   全部通过后，运行状态改用新绑定；任何一项不符都是 `transition-diverged`。
4. **失败处理**：建成的工程保留，运行结果列出它的路径。没有删除工程的工具，删除属于用户操作。之后以已有工程模式重新规划，worker 已经绑定这个工程。
5. 已有工程的备份步骤（`ArchiveSavedProject`，仅 20/21）在新建模式下不出现。

## 6 生成对象归属与 `.tiagen.json`

### 6.1 见证

| 对象 | V20/V21 | V14 SP1–V19 |
|---|---|---|
| 块、UDT | `GetPlcObjectFingerprints`（离线 FingerprintProvider：Code/Interface/Properties/Comments 等） | `GetPlcBlockInfo`/`GetPlcTypeInfo` 的 `ModifiedDate` 和可读的修改时间属性。需要 L5 确认编译、保存、重新打开后是否稳定；不稳定时退回“导出 XML 去时间戳后的哈希”（V15.1+；V14 SP1 的 SCL XML 只有接口）或不做见证（Q7） |
| 变量表、变量、常量 | 内容读回即可（名称、类型、地址），不需要见证 | 同左 |
| 分组 | 路径 | 同左 |
| 外部源 | 名称 + 记录中的内容哈希。工程内的源内容读不回，只能“由记录归属”；按 4.4 的做法会被清理，或删除后重新导入 | 同左 |
| 硬件 | 订货号、固件、槽位、名称（`GetProjectTree`） | 同左（有限） |

见证在保存之后统一采集，由 `PublishGenerationRecord` 的预览经现有 READ 工具读取。这样能避开编译对时间戳的影响，结论以 L5 为准。

### 6.2 记录内容（`tiamcp.generation-record/1`）

```json
{
  "schema": "tiamcp.generation-record/1",
  "project": { "file": "D:/Projects/Line1/Line1.ap21", "release": "21" },
  "previousRecordHash": "sha256:…|null",
  "runs": [ { "runId": "…", "planId": "gen-…", "planHash": "sha256:…", "package": { "id": "tiamcp.basic", "version": "1.0.0", "hash": "sha256:…" },
              "machineHash": "sha256:…", "status": "succeeded|partial|…", "startedUtc": "…", "endedUtc": "…",
              "parentRequestId": "…", "approvalRequestId": "…", "actor": "mcp|workbench" } ],
  "objects": [ { "key": "PLC1/block/FB_MotorDol", "path": "20_Units/U01/FB_MotorDol",
                 "expectedFingerprint": "sha256:…", "artifact": { "path": "staging/gen-…/source_….scl", "sha256": "…" },
                 "witness": { "kind": "plc-object-fingerprints|modified-date|content", "tool": "GetPlcObjectFingerprints", "values": { "…": "…" } },
                 "state": "owned|unknown|adopted", "createdBy": { "runId": "…", "stepId": "s00013", "requestId": "…" } } ],
  "recordHash": "sha256:…"
}
```

`expectedFingerprint` 就是规划器为期望对象计算的 `ProjectModelComparison.Fingerprint`。`recordHash` 是除 `recordHash` 本身以外内容的规范 JSON 哈希。

### 6.3 何时写、如何审批

- 计划在 `save` 阶段之后增加 `record` 阶段，只有一步：调用新的宿主 FILE 工具 `PublishGenerationRecord`（八版，预览默认）。
  - 参数：`planId` 或 `runId`（二选一）、`recordPath`、`dryRun`、`expectedPlanHash`。
  - planId 与运行一一对应，由已消费声明保证，所以计划期参数可以完全确定。
- 预览做三件事：读取运行日志；对本运行成功创建的对象逐个采集见证；读取现有记录并计算前一记录哈希。然后生成新记录，返回由路径、前一哈希、新内容哈希和工程身份组成的哈希。
- apply 时重算并比对哈希，然后原子写入：同目录临时文件加替换，拒绝重解析点；如果前一记录在此期间被改过就拒绝。
- 在运行中，这一步和其他步骤一样走 `review`（规则 `generation-record/1`）和单步授权。审批卡显示路径和覆盖条件。
- 单独调用这个工具（例如记录写入失败后补发）时走普通审批。
- 失败处理：运行的 TIA 写入已经完成，记录步骤失败，运行为 partial。数据目录里的日志保留全部事实，下次规划仍然能用（6.5）。

### 6.4 位置

维护者已决定写在“工程目录旁”，但这句话有两种理解：

- (a) 工程文件夹内、与 `.apXX` 同级：`D:\Projects\Line1\Line1.tiagen.json`，随工程移动或复制；
- (b) 工程文件夹旁边：`D:\Projects\Line1.tiagen.json`。

推荐 (a)。待 L5 确认 TIA 打开、归档/恢复（`ArchiveSavedProject`、`RetrieveProjectArchive`）和 VCI 工作区不受影响、也不报错；如果受影响就退回 (b)（Q5）。

### 6.5 事实来源

`<data>/generation/runs/*.jsonl` 是事实来源，`.tiagen.json` 是对外发布的投影。规划时两者都读：
- 记录存在且 `recordHash` 校验通过时优先用记录；
- 记录缺失或校验失败时，用该工程身份对应的运行日志重建候选归属；凡是没有写后见证的对象，一律按冲突处理。

## 7 清理与保留

| 对象 | 规则 |
|---|---|
| 未执行计划的暂存产物（`generation/artifacts/staging/<planId>`） | 计划过期 24 小时后，在下一次规划时自动清理。只清理没有被任何运行引用的目录；这是框架自己的数据目录临时文件，不涉及工程 |
| 已执行运行的产物 | 作为证据保留，直到显式清理：新增 `ManageGenerationArtifacts`（list/cleanup，FILE，预览加审批），或并入记录工具 |
| 运行日志、`.consumed` 声明 | 与审计链一样不自动删除 |
| 工程内的外部源 | 推荐在 `cleanup` 阶段（`compile` 成功之后、`save` 之前）用 `DeletePlcExternalSource` 删除本运行导入的源，走事实链核对标识；内容哈希和产物留在数据目录（Q6） |
| 备份 `.zap21` | 保留，路径写入运行记录和记录文件 |

## 8 部分运行后的重新规划

分类（已有工程模式；Q8 已决定“只补缺”）：

| 观测 | 记录或日志 | 分类 |
|---|---|---|
| 不存在 | 任意 | `create`。对结果未知的对象也可以，因为新的预览和工具预检会检查碰撞 |
| 存在 | `owned`，见证未变，且期望指纹 = 记录中的期望指纹 | `skip(owned-unchanged)` |
| 存在 | `owned`，见证已变 | 冲突 `modified-after-generation`，不覆盖 |
| 存在 | `owned`，期望指纹变了（包或机器描述改了） | 冲突 `regeneration-required`，以后由显式的 `update` 处理 |
| 存在 | `unknown`（该步结果未知） | 冲突 `unknown-outcome`。人核对后，在 `PlanProjectGeneration` 的新参数 `adopt: [key…]` 中列出，下一次记录标为 `adopted` 并补见证 |
| 存在 | 无记录 | 沿用现有比较：可核实且一致时跳过，否则冲突 |
| 遗留的已归属外部源 | `owned` | 规划为删除后重新导入（4.4） |

- **谱系**：计划种子加入该工程身份最近一次运行的 `{runId, status}`。任何运行之后再规划都会得到新的 planId，同时保持“没有运行时，同输入同哈希”。这样也解决了 1.3 #3：已消费声明仍按 planId，但同一份计划不会在运行后再次出现。
- **结果未知之后**：会话按现有规则锁定，重连后绑定纪元变化，计划自然失效。规划不需要额外解锁，只按上表把未知对象记为冲突。
- **验收**：部分运行后重新规划只含缺失步骤；全部成功后立即重新规划得到 0 步；人工改动一个生成对象后，重新规划报告冲突、不覆盖（§7 框架整体验收）。

## 9 工作台审批抽屉

| | A / D3（推荐） | B | C |
|---|---|---|---|
| 卡片数量 | 每次 Apply 一张 | 每阶段一张，显示“第 k/n 阶段”，附上一阶段的结果链接 | 每次 Apply 一张 |
| 步骤表增加的列 | “执行前核对”（工具预览 / 无预览 / 工程切换）；“执行时填入”（`expectedPlanHash`）；“不得覆盖”（对象计数，可展开）；“效果声明” | 真实的工具哈希（每步） | “以计划授权替代预览哈希”的警示；工具结构期望 |
| 规则区 | 发散即停、需重新规划；结果未知即停、不重试；运行期间本会话其他写调用会被拒绝；只在步骤之间可以取消；运行上限 | 本阶段之后需再次审批 | 工具在授权下跳过预览哈希 |
| 文件与工程 | 记录路径和覆盖条件（前一哈希）；外部源清理是否开启；备份路径；新建工程的预测路径和“将切换到新工程” | 同左（分在各阶段） | 同左 |
| 结果卡（`ApprovalClient.Complete`） | 运行状态、runId；发散时显示期望与观测的差异；“重新规划”入口（经 P8-21c 操作端，否则提示交给 AI） | 阶段结果 | 运行状态 |

推荐布局在 P8-31e 现有计划视图（差异树 + 步骤只读表 + 完整 JSON）的基础上扩展：

- 头部：计划 ID/哈希、版本、工程（或“将新建：<路径>”）、包 id@版本、机器哈希、发起方（人/AI）。
- 新增“执行规则”和“文件与工程”两个折叠区。
- 结果卡的详细内容由工作台经操作端调用 `GetGenerationRun` 读取，审批协议不变；只扩展 `PlanJson` 的内容，帧上限 1 MiB 不变（Q13）。

## 10 任务拆分

### 10.1 子任务

子任务状态以 [重构计划](../refactor-plan.md) 的 P8-31 行为准（2026-10-11：e1–e4 已合并，e5、e6、h1、j1 未实施）。

| 编号 | 对应 §7 行 | 内容 | 主要文件 | 验收 | 依赖 |
|---|---|---|---|---|---|
| P8-31e1 | e（合并前修正） | 1.3 的 #1、#2、#3、#6：外部源导入根组，加 `MovePlcBlockToGroup` 后续步骤；过期只在审批前和首写前检查；谱系种子和已消费语义；审计 `planHash` 语义 | `src/Logic/Generation/GenerationPlanner.cs`、`src/Shared/Host/GenerationExecutionTools.cs`、`src/Logic/Generation/GenerationRunStore.cs`、`src/Shared/AuditInvocation.cs`、生成规划与执行测试 | 黄金计划由规划器重新生成并逐项审查差异；新增过期、谱系、已消费用例；`generation-planning`、`engine-host` 最低数按新增用例上调，不放宽 | 无 |
| P8-31e2 | e | 分阶段执行核心：`observed` 与 `review` 块（schema、模型、规划器输出预期投影、禁止集、效果声明）；规则注册表（三条外部源规则）；4.2 的执行循环；停止类型；运行事件；单步授权覆盖最终参数并核对迟填槽 | 新增 `src/Logic/Generation/StepReview*.cs`、`RunFacts.cs`；`schemas/standards/v1/plan.schema.json`、`common.schema.json`（phase 增加 `cleanup`、`record`）；`src/Logic/Generation/Models`；规划器；执行器；`src/Shared/ApprovalProtocol.cs`；`GenerationRunStore.cs` | Logic：用真实 `PlcExternalSourceWorkflowPolicy` 加假清单生成八版预览体（含 V14 SP1 void 生成）作为规则黄金用例；执行器故障注入（10.2 第 1–8、11、16 项）；`generation-model`/`generation-planning`/`engine-host` 套件 | e1 |
| P8-31e3 | e | 会话写栅栏与步骤间取消 | `src/Shared/Host/McpServer.ToolBridge.cs`、`src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs`、`src/FoundationHost/FoundationV4Tool.cs`、`src/EngineHost/EngineHostPipeline.cs`、执行器 | 10.2 第 9、10 项；`shared-session`、`shared-pipeline`、`workbench-control-host` 不退化 | e2；与 P8-21c 协调（面板附着） |
| P8-31e4 | e | 新建工程同次运行换绑：未绑定规划、预测路径（Logic 实现并做奇偶测试）、transition 规则 | 执行器（`Target` 变体）、规划器、Logic 的路径规则（只引用适配器，不改它） | 10.2 第 12 项；两类宿主 | e2 |
| P8-31e5 | e | 归属与记录：`tiamcp.generation-record/1` schema 与模型；见证采集；`PublishGenerationRecord`（宿主 FILE，八版）；规划读取记录与日志并按第 8 节分类，新增 `adopt` 参数；`cleanup` 阶段；保留规则；`ManageGenerationArtifacts`（可并入） | 新增 `src/Logic/Generation/GenerationRecord.cs`、`schemas/standards/v1/generation-record.schema.json`；规划器、`ProjectModelComparison.cs`；执行器；`src/FoundationHost/GenerationTool.cs`、`src/Logic/ModelContextProtocol/ToolMetadata.cs`、`ToolProfiles.resx`；目录、契约、响应快照、示例都用生成器和捕获工具重建（八版） | 10.2 第 13、14 项；契约差异只有新增工具及其记录；八版 `GetToolUsage` 检索与功能检查 | e2；e4（新建工程的记录路径） |
| P8-31e6 | e（SafeV4） | `v4-candidate/1` 规则；候选会话消费后移除 `plans`（共享代码，需 grep 全部调用方） | `src/Logic/V4/*Candidate*.cs`、规则注册表 | 在 `TiaMcpTestPolicy` 构建下重跑同一套故障注入；`compile-candidate`、`compile-candidate-no-safety` 等候选套件 | e2；可推迟到 SafeV4 启用前 |
| P8-31h1 | h | 第 9 节的审批抽屉扩展和结果卡 | `src/Studio/Gui/Services/ApprovalService.cs`、`FeatureContracts.cs`、`ViewModels/FeaturePagesViewModel.cs`、`Views/ApprovalsDrawer.xaml(.cs)`、`Localization/Strings.FeaturePages.cs` | 视图模型测试，加 `WpfContext` 真实窗口：大计划渲染、迟填列、禁止集、发散结果卡；真实 Windows 账户下的审批往返（P8-31e 实施报告列出的遗留项） | e2、e5；P8-21c |
| P8-31j1 | j | 修订 §6.3、§6.5–6.7；`reference/tool-examples` 的 `sequence/project-generation` 和新工具示例；生成嵌入目录；八版检索与功能检查 | `docs/development/phase8-generation-framework.md`、`reference/tool-examples/*`、生成产物 | `Check-Repository.py --no-binaries`；核对链接、路径、适用版本和引用 | e2–e5 |

顺序：e1 → e2 → e3、e4、e5（e3 改共享准入，要与 P8-21c 错开）→ h1 → j1；e6 视 SafeV4 启用时间而定。各子任务按 `docs/development/validation.md` 构建并跑全部版本检查，新增用例进入现有注册套件并上调最低数，不放宽门禁。

### 10.2 故障注入（两类宿主：旧版 Foundation 宿主与 V20/V21 组合宿主，使用有状态夹具工程）

1. 预览被拒（例如源名已存在）：停在第 k 步，不写入，其余步骤为 `NOT_EXECUTED`。
2. 预览的固定字段不符：产物哈希、解析出的 `softwarePath`、源名、工程文件任一不同，均为 `diverged`，不写入。
3. 出现禁止项：生成预览的 `objectsBefore` 里已有将要创建的块（他人先建了），为 `diverged`，不覆盖。
4. 预览之后、执行之前，夹具插入对象：工具 apply 重算哈希不等，为 `rejected-before-operation`，不重试。
5. 效果超出声明（生成了未声明的对象）：`effect-diverged`，partial，停止。
6. 结果未知（apply 返回 unknown、派发抛异常、worker 退出）：unknown，会话锁定，不重放；重启宿主后 `GetGenerationRun` 显示该步为 unknown。
7. 事实链断裂：生成预览的源名或标识与导入事实不符，或删除预览的 `targetIdentity` 与生成时记下的标识不符，都为 `diverged`。
8. 迟填参数被篡改：用别的哈希或别的参数调用时，授权返回 denied；授权只接受“审批参数 + 本步骤预览哈希”。
9. 写栅栏：运行期间，同会话另一个 MCP 写调用和工作台面板写调用都被拒（类型化错误）；读调用照常；运行结束后恢复。
10. 步骤之间取消，状态为 `cancelled`；正在执行的步骤不受影响。
11. 运行超过 10 分钟不会因计划过期中断；审批返回前计划已过期时仍然拒绝。
12. 新建工程：`CreateProject` 被拒时为 rejected。建成后路径不同、进程变化、纪元未前进或读回非空，为 `transition-diverged`（partial）。
13. 记录：前一记录哈希不符时拒绝发布，运行为 partial；写入 I/O 失败时日志保留，可以单独补发；记录被篡改后 `recordHash` 校验失败，规划退回用运行日志。
14. 重新规划：部分运行后只含缺失步骤；未知步骤的对象为冲突，`adopt` 之后转为跳过；人工改动后为冲突；包升级后为冲突；谱系让 planId 改变；没有运行时同输入的 planId 稳定。
15. 两类宿主一致：同一份计划产生相同的步骤状态序列，运行记录除 requestId 和时间外相同。
16. 审批：每次 Apply 只有一次计划审批；被拒或超时不派发；关闭审批时照常做预览比对，并记录“审批已关闭”。
17. 大计划：审批帧超限在规划时就拒绝。SafeV4 下（e6）超过 128 次候选预览不阻塞运行。

### 10.3 L5 场景（阶段 8 末，由审查者执行，记入真机台账）

1. V21 整机，新建工程模式：示例机器 → 计划 → 一次审批 → 分阶段执行 → 编译 0 错误 → 保存 → 发布记录 → 立即重新规划得到 0 步。
2. V19、V17 已有工程的 PLC 部分：导入源 → 生成 → 移动分组 → 编译 → 清理外部源 → 保存 → 记录。建议另做一次 V14 SP1，验证 void 生成的观测路径。
3. 发散：规划后在 TIA 界面手动新建同名块，执行时为 `diverged`，不覆盖；重新规划报告冲突。
4. 结果未知：执行中让 worker 退出或断开 TIA → unknown → 重置会话 → 重新规划，未知步骤的对象为冲突 → adopt 或手动删除 → 继续。
5. 人工修改一个生成块后重新规划：报告冲突，见证检测成立。
6. 旧版见证的稳定性：V17、V19 上 `ModifiedDate` 在编译、保存、重新打开前后是否保持不变，决定 Q7。
7. 记录随工程移动：复制工程文件夹到新位置并打开，规划能识别记录；同时检查归档/恢复和 VCI 不受影响，决定 Q5。
8. 两类宿主各完整运行一次（V19 Foundation 宿主、V21 组合宿主），审计链校验通过，并能按 runId 聚合。
9. 工作台：真实账户下计划卡审批往返，结果卡能显示发散差异。

## 11 维护者决定（2026-10-10）

维护者于 2026-10-10 按推荐回答了全部 14 个问题。其中的结构性决定（D3、写栅栏、同次运行换绑、规划只用 READ 工具、运行上限等）待写成 ADR（[架构决策记录](../decisions/README.md) 待补写清单 B26）；Q5 与 Q7 的结论待第 10.3 节的 L5 场景确认。

| # | 问题 | 选项 | 推荐 | 决定 |
|---|---|---|---|---|
| Q1 | 完整 PLC 生成用哪种执行方式 | A 纯分阶段 · B 每阶段审批 · C 工具接受计划授权 · **D3 A + 写栅栏 + 事实链 + 效果核对** | **D3** | D3 |
| Q2 | §6.3“计划内参数全部预先确定”如何修订 | 改为“决定性参数全部预先确定；只有 `expectedPlanHash` 由工具预览迟填，见证字段按计划声明的约束核对” · 保持原文（只能选 B） | **前者** | 按推荐修订 §6.3 |
| Q3 | 发散后怎么办 | **只停止，重新规划后再审批** · 就地续批（工作台弹出带 `continuationOf` 和差异的续批卡） | **只停止**；续批以后再做 | 只停止，重新规划后再审批 |
| Q4 | 新建工程 | **同次运行换绑** · 分两次运行 | **换绑** | 同次运行换绑 |
| Q5 | 记录文件位置（细化已决定的“工程目录旁”） | **(a) 工程文件夹内，与 `.apXX` 同级** · (b) 工程文件夹旁 | **(a)**，L5 确认归档/恢复和 VCI 不受影响，否则改 (b) | (a)；待 L5 确认归档/恢复和 VCI 不受影响，否则改 (b) |
| Q6 | 工程内的外部源 | **编译成功后删除本运行导入的源，内容哈希和产物留在数据目录** · 保留在工程中 | **删除**，机器描述 `options` 可以关闭 | 删除本运行导入的源 |
| Q7 | 旧版（V14 SP1–V19）的块/类型见证 | **块/类型信息的修改时间（L5 确认稳定性）** · 导出 XML 去时间戳后的哈希 · 不做见证（旧版生成对象重新规划一律冲突） | **修改时间**，L5 不稳定时改为导出哈希 | 修改时间；待 L5 确认稳定性，不稳定时改为导出哈希 |
| Q8 | 写栅栏范围 | **同会话所有非本运行的写调用（含工作台面板）** · 只拦 MCP | **前者** | 同会话所有非本运行的写调用（含工作台面板） |
| Q9 | 计划 schema | **直接修订尚未发布的 `tiamcp.plan/1`** · 升级为 `tiamcp.plan/2` | **修订 /1**（4.0 尚未发布） | 修订 `tiamcp.plan/1` |
| Q10 | 规划期试预览 | 允许规划时对前置条件已满足的写工具做只读预览，提前发现拒绝（不作为审批依据） · **规划只用 READ 工具（P8-31e 现状）** | 首版**只用 READ**；试预览作为以后的改进 | 规划只用 READ 工具 |
| Q11 | 长运行 | **批准后不过期，另设总时长和步数上限（默认 4 小时、10000 步，可配置）** · 维持 10 分钟 | **前者** | 批准后不过期，4 小时与 10,000 步上限 |
| Q12 | SafeV4 规则范围 | **4.0 只做 `current` 形态，SafeV4 规则与候选会话清理在族启用前完成（e6）** · 一并完成 | **前者** | 4.0 只做 `current` 形态，SafeV4 规则在 e6 完成 |
| Q13 | 大计划的审批载荷 | **`PlanJson` 内联，1 MiB 帧，超限时要求按阶段或单元拆分** · 只传哈希，工作台读数据目录里的文件并核对哈希 | **内联**；引用方式以后再做 | `PlanJson` 内联 |
| Q14 | 结果未知的对象 | **冲突，人核对后显式 `adopt`** · 只能人工删除后重新生成 | **adopt** | 冲突，人核对后显式 `adopt` |

## 附 依据

- 设计：[生成框架方案](../phase8-generation-framework.md) §1.4、§6.1–6.7、§7、维护者决定；[工作台控制设计](../phase8-workbench-control.md) §1.1、§5.1–5.3、维护者决定；`AGENTS.md`；`CLAUDE.md`。
- P8-31e：`src/Shared/Host/GenerationExecutionTools.cs`、`src/Shared/Host/GenerationTools.cs`、`src/Logic/Generation/GenerationRunStore.cs`、`src/Logic/Generation/GenerationPlanner.cs`、`src/Shared/ApprovalProtocol.cs`、`src/Shared/ApprovalPipe.cs`、`src/Shared/AuditInvocation.cs`、`src/FoundationHost/GenerationTool.cs`、`src/FoundationHost/FoundationV4Tool.cs`、`tests/FoundationHost/TiaMcp.EngineHost.Tests/GenerationExecutionTests.cs`。
- 写工具复核：`src/Adapters/Native/Plc/PlcExternalSourceWorkflowPolicy.cs`、`PlcExternalSourceWorkflow.cs`、`PlcExternalSourceDelete.cs`、`PlcExternalSourceImportPolicy.cs`；`src/Adapters.Contracts/PlcExternalSourceWorkflowContracts.cs`；`src/Logic/V4/SourceCandidate.cs`、`SourceContract.cs`、`CompileCandidate.cs`、`SaveCloseCandidate.cs`、`PlcImport.cs`、`Plan.cs`、`HostBehavior.cs`、`BehaviorCapabilities.cs`；`src/FoundationHost/FoundationTools.cs`、`FoundationV4Result.cs`；`src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs`、`PlcExternalSourcesTools.cs`；`src/Shared/Host/McpServer.Batch.cs`、`McpServer.ToolBridge.cs`。
- 工程生命周期：`src/Adapters/Native/Session/PlcFoundationEngine.cs`、`MutationIdentityPolicy.cs`。
- 比较与模型：`src/Logic/Generation/ProjectModelComparison.cs`、`GenerationAvailability.cs`；`schemas/standards/v1/plan.schema.json`。
- 版本与策略：`manifest/contracts/v4/baseline/{14sp1,17,19,20,21}.json` 的输入 schema；`src/Logic/ModelContextProtocol/ToolProfiles.resx` 的 `behaviorPolicies`（八版全部为 `current`、L5 `NOT RUN`）。
