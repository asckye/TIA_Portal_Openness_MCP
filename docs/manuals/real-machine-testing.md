# 真机测试

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本文写在真实 TIA Portal 上验证本项目的方法：VM 回归、两套原生测试脚本、原生调用诊断和工具示例库的维护；取代已归档的[原生调用诊断](../archive/development/native-call-diagnostics.md)、[独立原生生命周期测试](../archive/development/native-lifecycle-tests.md)和[统一工具示例](../archive/development/official-tool-usage.md)。

## 证据边界

- 构建、离线套件、契约基线和织入校验只证明接口形状，不证明原生行为；没有真机记录的项写 **NOT RUN**，行为族按 [ADR 0002](../standards/decisions/0002-native-behavior-by-family-acceptance.md) 处理。
- 只用一次性测试项目或副本；通用做法见通用规范[实机验证清单](https://github.com/asckye/engineering-standards/blob/v2.0/general/26-checklists.md#checklist-real)。
- 结果先记在[开发进度](../plan/progress.md#真机结果)的“真机结果”，复核后写入[真机台账](../reference/real-machine-ledger.md)。
- Codex、GitHub CI 和本地检查只运行脚本的 `--self-test`，从不运行 live 分支。

## VM 回归

依据 [ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md)：每个预发布和每批修复都在 VM 上按[预发布真机验证清单](../guides/beta-tester-checklist.md)回归。VM 地址、TIA 进程号和项目名从当前环境确认，不写进文档。

1. 在 VM 上解压待测包，先测 V21，再测其他已安装的版本。导出和临时文件写到 `GetSessionState` 返回的 `data.outputRoot` 下的 `<run>`（默认 `<包根>/exports/<run>`），不放桌面；MCP 写到输出根之外须工作台审批。
2. 审批开关由维护者决定（逐条批准或由维护者关闭）；端点令牌不打印、不复制。
3. 逐个工具测试：先用 `FindTools` 或 `GetToolUsage` 取参数；写操作先预览，再只对自己建的测试对象执行；没有测试 PLC 时，在线和下载类只预览。驱动、Safety、在线等高风险族放在最后，只读或预览。
4. 每项记为通过、缺陷、按设计拒绝（写明缺少的前提）或未测；照工具自己的示例调用仍失败时，算示例的缺陷。返回 `OUTCOME_UNKNOWN` 或 `SESSION_RESET_REQUIRED` 时停止其他原生调用，先记录再恢复会话。
5. 说明、分片结果、汇总和复测计划写在主检出的 `bin-build/vm-test/<runId>/`（不入库），每个目录只记录一个版本；格式见下节。结论登记进开发进度的“真机结果”，修复合并后在 VM 上复测对应项，并按实测更新示例（见“工具示例库”）。

### 评分使用的运行记录

每次运行必须保存 `run.json`，示例：

```json
{"release":"21","package":"4.0.0-beta.3","commit":"1dfe2067","date":"2026-11-02"}
```

同目录保存 `results.jsonl`；没有该文件时，评分依次读取按文件名排序的 `slice-*.md`、`lead-probe-results.md`、`lead-tools.md`。缺少或无法解析 `run.json` 的目录会列为忽略，当前运行按 `date` 选择，不按目录名推断。原始信封可能含用户工程数据，整个目录保持 Git 忽略，不复制到测试夹具或发布包。

JSONL 每行的 `tool` 为直接工具名；包装调用使用 `CallTool` 加 `arguments.name`，或 `CallTool:<工具名>`。执行状态为 `passed`、`failed`、`refused-as-designed`，其他状态不算执行。工具同时有通过和失败记录时，以最后执行记录是否通过区分通过或不确定；只有失败为缺陷，只有通过为通过，其余执行记录为按设计拒绝。目录中的本版本契约清单外名称不计入覆盖率。

错误质量统计另含 `(recovery)` 失败记录；`nativeMessage` 统计字段是否存在，值可以为空。历史记录中被截断的 `envelope` 只读取其保留下来的完整 JSON 标记，并将该维度标为 `partial`，不补写或猜测省略内容。

表格第一列为工具名（可有多个），第二列以 `PASS`、`BUG`、`EXPECTED-REFUSAL`、`EXPECTED REFUSAL`、`INCONCLUSIVE`、`NOT RUN`、`DEFERRED` 或 `SKIP` 开头；`LEAD` 等其他值不计。目录、示例、缺陷汇总、会话重置、副作用、通用问题、未测和计数章节不计入工具结果。执行结果优先于未执行，后面的记录优先；曾有缺陷的工具仍记缺陷。工具覆盖采用最新运行，错误质量独立采用该版本最新的 `results.jsonl`；只有所有有效运行都没有 JSONL 时，错误质量才为 `not measured`。可选的 `verified-examples-v<版本>.json` 按 `tools` 对象的成员数报告，文件缺失不新增缺失指标。运行 [质量评分](validation.md#质量评分) 不连接 VM 或 TIA。

## 生产 MCP 会话测试

`scripts/checks/Test-NativeMcpSession.py` 经 FoundationHost 的生产 STDIO 入口（`--transport stdio --release-key <主版本>`）和每会话 worker 测试会话与项目生命周期，只支持 V20/V21。

```powershell
python scripts/checks/Test-NativeMcpSession.py --exe '<包根>\runtime\v21\TiaMcp.FoundationHost.exe' --major 21 --output '<不存在的绝对目录>'
```

- 默认只显示计划，不启动进程、不建目录；在获准的测试环境加 `--run-live --confirm-new-portal` 才执行。写入时保持工作台打开逐条批准，或在 MCP 菜单关闭审批；客户端不绕过审批。
- 固定流程，不能改为附加用户项目：握手；`ConnectIsolatedPortal` 新建无界面 TIA；`CreateProject` 在本次目录建随机名项目；`GetSessionState` 核对 PID、启动时间、项目路径、版本和绑定代次；保存、关闭、重开后核对 PID 不变、代次已更新；关闭项目并断开。项目和日志保留。
- 每次调用先记录并刷盘，单次等待上限 195 秒，worker 期限见 [ADR 0008](../standards/decisions/0008-worker-timeouts-and-faults.md)。异常后不保存、不重试、不终止 TIA，只停止自己启动的 MCP；`nativeOutcomeUnknown` 需人工核对，超时不等于已回滚。
- 输出 `native-mcp-result.json`、`scenario.jsonl`、`host-stderr.log` 和 `diagnostics/`。
- 不覆盖 PLC/HMI 工具族的真实编辑、编译结果、HMI 变量依赖和原生崩溃根因。HMI 删除在另行获准的 Classic/Unified 测试项目中按“预览、删除、确认不存在、编译”验收。

## 独立原生生命周期测试

`tests/FoundationHost/TiaMcp.Engine.Native.Tests` 的 V20、V21 测试程序（x64/net48）直接调用 Openness，不经 MCP 分发；`scripts/checks/Test-NativeLifecycle.py` 监督整个测试进程。

- 场景：只建自己的 `WithoutUserInterface` 实例和唯一 scratch 目录，没有 Attach、项目路径、PLC 地址或 PID 参数。每轮建项目，提交一个设备组事务、回滚另一个，保存、关闭、重开并核对持久化，共 7 项断言；只关闭本轮的 `Project`，只释放本轮的 `TiaPortal`。
- 判定：进程正常退出、版本与轮次和 7 项断言正确、各阶段 `BEFORE`/`RETURNED`/`THREW` 日志完全配对、清理完成，才报告 PASSED。失败保留 scratch 和证据；监督器只终止它启动的程序，超时后原生结果未知，TIA 可能仍在运行。
- EXE 的 `--preflight` 只读安装注册表和 DLL 身份，不证明许可、Openness 用户组或防火墙已配置。
- 获准执行时需要匹配版本的 TIA、许可、Openness 用户组和测试 EXE 的防火墙许可，先跑 1 轮；`--timeout-seconds` 是总期限：

```powershell
python scripts/checks/Test-NativeLifecycle.py --runner tests/FoundationHost/TiaMcp.Engine.Native.Tests/V21/bin/Release/net48/TiaMcp.Engine.Native.Tests.V21.exe --major 21 --run-live --confirm-new-portal --output '<不存在的绝对目录>' --iterations 1 --timeout-seconds 600
```

输出 `supervisor-result.json`、`runner-output.log`、`native/native-result.json` 和 `native/native-events.jsonl`；报告缺失本身就是失败证据。依据为项目保存的 Siemens 指南 [openness-testing](../../reference/siemens-openness/skills/openness-testing/SKILL.md)、[session-and-project](../../reference/siemens-openness/skills/session-and-project/SKILL.md)、[threading-and-concurrency](../../reference/siemens-openness/skills/threading-and-concurrency/SKILL.md)；指南面向 V21+，V20 另按本地 PublicAPI 编译核对。

## 原生调用诊断

- 织入：`build-tools/native-call-weaver`（Mono.Cecil 0.11.6）在编译后给八版适配器 `TiaMcp.Adapter.<key>.dll` 的每个 Openness 调用点生成包装，并内嵌 `TiaMcp.NativeCallCoverage.json`（调用者、IL 位置、目标成员、调用点 ID）。缺少织入器时构建失败，没有关闭开关；`prepare` 对八版随包适配器逐版校验。`GetSessionState.journalHealth.nativeBoundaryCoverage` 报告 `build-instrumented` 或 `not-instrumented`。
- 覆盖直接调用（方法、构造器、属性、索引器、事件）、反射调用、集合接口与外部遍历，以及原生对象的 `ToString`/`Equals`/`GetHashCode`；未处理的函数指针、动态代码生成等新路径让构建失败。不改动或分发 PublicAPI，不注入 TIA；只能定位最后执行到哪个调用，看不到 TIA 内部，也不保证 TIA 不崩溃。
- 读取：`GetNativeInvocationLog`（`take: 100`），或在工作台“环境体检”页导出诊断包（脱敏日志、最近 24 小时匹配的 Windows 事件、进程和转储文件清单，不复制转储内容）。日志在 `data/logs/<发布键>/`，诊断在 `data/diagnostics/`；分享前检查隐私信息。

| 字段 | 含义 |
|---|---|
| `id` | 所属 MCP 请求的关联 ID |
| `nativeCallId`、`parentNativeCallId` | 本次边界调用及嵌套关系 |
| `phase` | `BEFORE`、`RETURNED` 或 `THREW` |
| `callSite`、`member`、`dispatch` | 调用点、成员和调用方式 |
| `objectType`、`objectId`、`objectPath`、`pathKind` | 对象类型、本进程内的弱引用身份、已观察到的访问链 |
| `binding` | 主版本、PID、启动时间、项目路径和绑定代次 |
| `threadId`、`apartment`、`mcpProcessId` | 线程、公寓模型和写记录的进程 |
| `exceptionType`、`hresult`、`exceptionChain` | 有界的异常类型链，不含消息 |

- 包装原样抛出异常，不重试、不换线程、不改参数。不记录密码、脚本正文、变量值、参数数组或返回内容，写日志不额外调用 Openness getter。`BEFORE` 写入后刷盘，日志 I/O 失败不阻止原操作，失败计数见 `journalHealth`。`merge-check` 另用假 API 夹具（`Test-NativeDiagnostics.py --fixture`）验证这些行为，以及进程中途退出时最后一条 `BEFORE` 已落盘。
- 按 `nativeCallId` 配对。缺终止记录时结合进程退出时间和 Windows 事件定位；日志轮转（约 10 MiB 加一个 `.previous`）或 I/O 故障也会造成缺失，不能单凭缺失认定某个 API 导致崩溃。依据：[crash-diagnosis 指南](../../reference/siemens-openness/skills/crash-diagnosis/SKILL.md)；worker 恢复见 [worker 指南](../guides/openness-worker-isolation.md)，进程租约见 [ADR 0011](../standards/decisions/0011-tia-process-lease-on-all-releases.md)。

## 工具示例库

工具参数、调用序列、结果解释和官方出处只在 `reference/tool-examples` 维护（`calls.json` 的 `plc-foundation`、`full-engine` 是按工具与 schema 选择的示例来源，八版对外 profile 均为 `plc-foundation`，另有 `sequences.json`、`metadata.json`、`languages/catalog.json` 及其源文件），由 `GetToolUsage` 按发布键返回；发现和错误中的内联示例、兼容入口读同一数据。规则见[设计规范](../standards/design.md)第 6.6 节。

| `GetToolUsage` 参数 | 返回 |
|---|---|
| 无参数 | 工具、语言、示例和参考文档的索引 |
| `toolName`、`operation` | 该版的 schema、参数来源、返回约定和调用示例 |
| `language`、`exampleId` | 编程示例；`exampleId: "sequence/<主题>"` 或 `exampleKind: "sequence"` 取调用序列 |
| `query`、`documentId`、`offset`、`limit` | 检索并分页读取官方原文 |

修改示例：

1. 用所选发布键的真实 schema；项目、PLC、组和路径放在绑定与参数来源中，路径指运行 TIA/MCP 的机器。
2. 按工具契约把导入、生成、编译和保存分为独立的序列步骤；导入或编译成功不代表已保存。
3. 如实区分完整源、片段和导出模块编辑，同步更新结果解释、版本与编码约束和官方出处；不为单个 API 问题或语言另写指南。
4. 依次运行 `generate-catalogs`、`generate-tool-usage`（生成 `src/Shared/ToolUsageData.json`，`-Check` 检查漂移），再构建并执行八版检索和功能检查。检查逐版取回每个工具和操作，核对 schema、参数来源、源文页面和哈希，并执行可离线运行的调用示例；结果写入[覆盖记录](../../manifest/tool-usage-coverage.json)，其中原生导入和编译为 NOT RUN。
5. VM 回归后按实测修正参数，核对结果记入真机台账，`validation` 按 `Real-machine evidence: V21 only (4.0.0-beta.4, 2026-11-05): <实际执行的 read、preview 或 apply 及结果>. Other releases: input schema checked offline; native execution NOT RUN.` 标明实测版本（仅注册于 V21 的工具写 `V21 (registered on V21 only; ...)`，仅预览写 `apply NOT RUN`，没有实机执行的行保留 `NOT RUN` 并说明原因，不写实机证据前缀），不包含请求 ID。

官方语料只作参考文本，不编译、不执行；不分发 Siemens SDK 二进制和示例项目归档：

| 来源 | 固定提交 | 许可 | 范围 |
|---|---|---|---|
| [Siemens TIA Portal AI extensions](https://github.com/siemens/tia-portal-ai-extensions) | `b5c7041648dc10f9225ef082306ade6c335b8771` | MIT，© Siemens AG，[许可证](../../reference/siemens-openness/LICENSE) | 32 组指南（36 个 Markdown 文件），在 `reference/siemens-openness` |
| [Siemens TIA Portal Openness Code Snippets](https://github.com/siemens/tia-portal-openness-code-snippets) | `4a8cc79d0666633e524e52f3335d99ff993f8830` | 源码按许可证第 2 节为 MIT，完整[许可证](../licenses/SiemensCodeSnippets-LICENSE.md)保留 | 41 个 C# 文件、8 个项目文件、README、许可证和 2 个构建文件，在 `reference/siemens-code-snippets` |

Code Snippets 的夹具选 V21 而 README 写 V20，两者都不证明旧版本兼容。语料不是 Siemens 发布过的全部示例；示例模板覆盖已注册的工具，不等于每个调用都针对具体目标完成或经过真机核对。动态属性必须先读取对象的自描述，示例不能让编造的属性名生效。
