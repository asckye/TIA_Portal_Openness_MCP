# 响应信封与吞异常治理设计（P2-01、P2-03）

[重构计划](refactor-plan.md) · [引擎拆分设计](engine-decomposition.md) · [适配器合并设计](adapter-merge.md) · [验证分层](validation.md)

本页是 P2-01（统一响应信封构造器）和 P2-03（审计空 `catch`）的设计结论。路径缩写：`T.` =
`tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.`，`P.` =
`tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/Portal.`，`L/` = `tools/tiaportal-mcp/src/TiaMcp.Logic/`。
数字为 2026-10-03 的源码统计，带“约”的为启发式估算。

## 前置事实

1. **P0-06 返回快照不证明逐字节一致。** `Snapshot-ToolResponses.py` 的 `canonical()` 使用 `sort_keys=True`，并先解析内层文本再
   重新序列化，键顺序、转义和数字格式都会丢失。“P0-06 0 差异”是必要条件，不是充分条件。已合并的 P2-02、P3-02、P3-04 没有改动
   响应构造代码；P2-01 动手前必须先加强快照（下文 E6）。
2. **同一个 POCO 有两种序列化**：直接调用走 SDK 默认选项（camelCase、省略 null）；经 `CallTool` 时内层结果用 `BridgeJson`
   （PascalCase、写出 null）再作为字符串放进 `Message`。构造器不得改动 `Responses.cs` 的 POCO 类型。
3. **织入不只覆盖西门子成员**：还会插桩 `object-dispatch`（`ToString/Equals/GetHashCode`）、`interface`、`reflection` 和
   `enumeration-input`（向外部程序集传 `IEnumerable`）。构造器和日志辅助的参数不得包含 `IEnumerable`，也不得对调用方对象调用
   `ToString()`。
4. **部分失败响应带堆栈**：`P.HmiOperation.cs` 的 `RunHmiStepTool`（约 220 处调用）、`T.OfflineAnalysis.cs`、
   `T.BaseLeftovers.cs` 把 `ex.ToString()` 写进 `meta.error`，阶段 3 搬迁必然改变其中的方法帧（决策 D1）。
5. **纯注释改动可逐字节证明**：引擎 Release 为 `DebugType none`，`IncludeSourceRevisionInInformationalVersion=false`，仓库中没有
   `CallerLineNumber`，只改注释时产物应完全相同。
6. **stderr 与调用日志已有消费者**：`Test-LocalStability.py` 检查 stderr 中的特定文本并配对解析 `calls-*.jsonl`；GetState 的
   `meta.journalHealth.failedWrites` 在 P0-06 中；Studio 桥接进程的 stderr 显示在 GUI 活动日志。

## P2-01 统一响应信封

### 现状

488 个工具都返回 `ResponseMessage` 派生的 POCO。全局计数：`["timestamp"]` 278、`["success"]` 329、`["ok"]` 247、
`new JsonObject` 1,336、`new ResponseMessage` 191、`throw new McpException` 360、`throw new PortalException` 539。

meta 的构造方式共 13 种：内联 `{timestamp, success}`（B1）；先取时间戳、原生调用后补键（B2，如 Connect）；success 位于中间或
末尾（B3）；Portal 步骤执行器 `RunHmiStepTool` 等（B4，先占位 `success:false`，结束时原位覆盖）；离线分析、PLCSIM、批量、
报告类执行器（B5a–d，其中批量为 success 在前、UTC 时间戳）；meta 工厂（B6）；只有 `ok`（B7）；失败且无 meta（B8）；data 内
字符串时间戳（B9）；包装层生成（B10，不纳入构造器）。

时间戳有 6 种编码（`JsonValue<DateTime>` 本地时间、`ToString("O")`、`DateTime.UtcNow`、`DateTimeOffset.UtcNow.ToString("o")`、
`DateTimeOffset.Now.ToString("o")`、`createdUtc` 自定义格式），序列化路径有 4 条（SDK 默认、`BridgeJson`、失败后附加 preflight
的 `DisciplineJson`、默认 `ToJsonString()`）。`CLI` 探针、`RunCapabilitySelfTest`、批量与 `ToolBridgeStatus` 读取
`meta.success`；`TryCanonicalizeEnumArgument` 解析失败文本，因此失败文本是功能性的。

### 现行“抛异常 / 返回失败”规则（阶段 0–5 维持）

规则按工具所属的族决定，不按错误种类决定：

| 族 | 约数 | 行为 |
|---|---|---|
| F1 准入（包装层） | 全部 | 返回 `isError:true` 纯文本，附 `preflight` |
| F2 手写 POCO 工具 | 183 | 成功返回 POCO + `{timestamp, success:true}`；执行失败抛 `McpException`；业务结果为假时返回 `success=false` |
| F3 执行器工具 | 231 | 业务失败不抛，返回 `meta.success=false` 与 `operationSuccess`、`status`、`error` |
| F4 桥接 | 4 | 从不抛，内层异常转成 `"X failed: …"` |
| F5 导出句柄 | 5 | 句柄缺失或拒绝覆盖时抛 `McpException(InvalidParams)`，成功时 `meta.ok=true` |
| F6 旧 Portal 方法 | — | 失败只在 Message 文本里，没有 meta |

新代码沿用所在文件的族，不在族之间转换；统一放到阶段 6。

### 构造器

`ResponseMeta` 与 `ResponseClock` 放在 `TiaMcp.Logic`（不接触 Openness、不织入），按上述变体分别提供方法（`Stamp`、`Basic`、
`StampThen`、`Step`/`Complete`/`Failed`、`LegacyBatch`、`Bridge`、`RoundTripStamp`）。硬约束：参数中没有 `IEnumerable`；不对
调用方对象调用 `ToString`；保留数值的 CLR 类型；`Meta` 仍是 `JsonObject`；不动 POCO；时间戳在原来的程序位置取得；带
`ex.ToString()` 的执行器保留方法名、声明类型和签名；不合模板的调用点保持手写并标记 `// envelope: legacy-<variant>`。

### 离线证明

| 层 | 内容 |
|---|---|
| E1 | HttpTests 在织入后的 V20/V21 EXE 中，用 4 种序列化选项写出样例对象，保存真实字节 |
| E2 | 每个构造器变体与从调用点复制的字面初始化器在固定时钟下逐字节相同 |
| E3 | HttpTests 用反射调用真实二进制中的执行器，传入合成动作（成功、失败、各类异常、阻断），改动前在 master 录制，改动后 0 差异 |
| E4 | `Check-EnvelopeRewrite.py --base <sha>`：每个改动片段必须匹配封闭的旧→新模板，其余行不变 |
| E5 | 织入清单中**全部类别**的成员多重集合（按声明方法归并 lambda）不变，比只看西门子成员更严 |
| E6 | P0-06 增加格式 3：解码前记录每个文本块的原始 SHA-256，只屏蔽已列明的时间戳路径；从改动前的 master 重录基线 |

离线无法证明：原生成功路径的实际取值、时间戳取值、堆栈帧（D1）、`McpErrorCode` 是否对客户端可见（T0 确认）。合并门槛是
E2–E6 全部 0 差异，不满足的调用点保持手写，不另设构建开关。

### 任务

| 任务 | 内容 |
|---|---|
| P2-01a（T0） | E1 黄金样本；E6 快照格式 3 并重录基线；本页族规则写入工具开发文档 |
| P2-01b（T1） | `Inventory-ResponseEnvelopes.py` 计数并作为只减不增的检查 |
| P2-01c（T2） | `ResponseMeta`/`ResponseClock` 与 E2 测试 |
| P2-01d（T3） | 执行器与 meta 工厂（B4–B6）原位改用构造器，不改名；E3/E5/E6 0 差异 |
| P2-01e…（T4…） | 内联调用点随阶段 3 各领域迁移前转换：先“迁移前清理”提交，再“纯迁移”提交 |

T0–T3 应在阶段 3 第 5 步之前完成（第 4 步的内核接口已保留 `RunHmiStepTool` 原名，可原位改造）。

## P2-03 吞异常治理

### 现状

三个源码根目录共 1,425 个 `catch`，其中空 `catch` 218 个（175 个没有任何注释）；去掉 WorkerProtocol 后 205 个。另有约 171 个
非空但丢弃异常的宽泛 `catch`。最集中的文件：`P.Devices.cs` 24、`P.Software.UnifiedHmiHelpers.cs` 20、`P.Software.UnifiedHmi.cs` 8。

| 类别 | 说明 | 处理 |
|---|---|---|
| C1 清理临时文件 | | 注释 |
| C2 进程、IO、通道收尾 | 退出阶段控制台可能已关闭 | 注释或限流记录 |
| C3 日志自身失败 | 记录会递归 | 必须保持为空，注释 |
| C4 反射探测可选成员 | 失败是常态，循环中可能成百上千次 | 注释；L5 之前不改为上抛 |
| C5 枚举可能不可用的原生集合 | `P.Software.cs` 的 PLC 查找遍历并入 G9 | 注释；L5 之前不改为上抛 |
| C6 原生写入或动作失败被吞（风险最高） | `P.Download.cs` `ApplyConfiguration`、`T.PlcSoftware.Online.cs` `GoOfflineAll` 等 | 有证明时可改为只记录；上抛需 L5 或阶段 6 |
| C7 JSON/文本解析兜底 | | 注释 |
| C8 环境、注册表、安装探测 | | 注释 |
| C9 UI | | 注释；可用 WPF 测试验证 |
| C10 “不得改坏结果”的放行守卫 | | 注释 |
| 隐私 | `Isolation/WorkerConnection.cs` 故意丢弃可能含工具输入的 stderr | 必须保持 |

### 日志与检查

- 辅助方法 `openness-shared/SwallowedExceptions.cs`：`Note(string site, Exception ex)` 只记录位置、异常类型全名和 `HResult`，
  不读取 `ex.Message`（可能是原生 getter 或含工程数据），不调用 `ex.ToString()`；每个位置每进程首次记录、之后只计数；自身绝不抛出。
- 输出默认关闭；引擎宿主接到 `ILogger` Debug 级别（类别 `TiaMcpServer.Swallowed`）；Studio 桥接进程和 Foundation worker 用
  `TIA_MCP_LOG_SWALLOWED=1` 开启。永远不写入 `calls-*.jsonl`、`InvocationJournal` 计数器或任何 meta 字段。
- 每个保留的 `catch` 在同一行标注 `/* swallow(<类别>): <原因> */`，类别取自封闭清单：cleanup / teardown / logging-failure /
  probe-optional / enumerate-optional / native-fallback / parse-fallback / env-probe / ui / fail-open-guard / privacy。
- `scripts/checks/Check-SwallowedExceptions.py` 检查标注，基线 `scripts/checks/swallowed-exceptions-baseline.json` 只许减少，
  指纹与路径无关（阶段 3 搬文件无需改基线），接入仓库检查和离线流程；WorkerProtocol 豁免（阶段 4 删除）。

### 任务

| 任务 | 内容 |
|---|---|
| P2-03a（T0） | 检查脚本、基线、本策略 |
| P2-03b（T1） | 辅助方法与引擎日志接线；P0-06 0 差异、LocalStability 通过、织入清单不变 |
| P2-03c（T2） | 只加注释：Logic、openness-shared、LegacyHost、Adapters、Isolation、HTTP、CLI、Runtime（约 45 处）；产物 SHA-256 不变 |
| P2-03d（T3） | Studio 与 PlcFoundation 只加注释，在阶段 4 第 D、G 步之前完成 |
| P2-03e…（T4…） | 随阶段 3 各领域的“迁移前清理”提交：C4/C5 注释，C6 可改为只记录；只允许 `catch` 体变化并匹配模板，织入清单不变 |
| P2-03z（T5–T6） | 列出需 L5 或阶段 6 的上抛候选；阶段 3 结束后基线清零 |

## 决策

- **D1 `meta.error` 中的堆栈帧是否属于兼容契约**：维护者已决定（2026-10-03）：不属于。只有首行“类型: 消息”是兼容契约，
  堆栈中的方法帧允许随阶段 3 的迁移变化；E3 比较执行器表征时只比较首行。
- **D2–D7 采用建议值**：P0-06 增加原始文本哈希（D2）；P2-01 先做构造器、执行器与只减不增检查，内联调用点只在领域迁移前且能证明
  等价时转换（D3）；吞异常默认不输出，引擎仅 Debug 级别（D4）；L5 之前原生路径不改为上抛（D5）；WorkerProtocol 豁免（D6）；
  维持按族的抛/返规则（D7）。
