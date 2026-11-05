# 开发进度

状态：现行 · 最后核对：2026-11-04，master `871d54da`

本页记录已核对的主线状态、待集成工作和测试者反馈；范围、顺序与放行门槛见[开发计划](plan.md)。

## 当前状态

- 公开版本记录：最新正式版为 [v3.3.0](../releases/v3.3.0.md)；最新预发布为 [v4.0.0-beta.3](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.3)（2026-11-04，`1d57c277`），此前为 [v4.0.0-beta.2](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.2)（2026-10-30，`7e9c4c65`）和 [v4.0.0-beta.1](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.1)（2026-10-22，`bc5dcc5a`）。预发布不设为 latest，3.3 更新器不提示。
- beta.3 已发布，CHANGELOG 和[发布说明](../releases/v4.0.0-beta.3.md)已更新。GitHub CI 的修复（CI-FIX、CI-FIX2）在发布之后合并，结果以最新一次 CI 为准。
- PIPE-INT/SLIM-1 已合并（`eee7b372`），VMFIX-2 已合并（`93371695`、`b769d196`）。旧文档中的“仍等这两项合并”已不适用。
- `prepare` 只构建、打包并检查 ZIP（PREP-SLIM），2026-11-04 实测约 6 分钟；`publish-prepared` 公开已封存的候选，beta.3 实测 6.9 秒。
- 合并门为 `merge-check`：普通 `dotnet` 构建和测试，含 38 个注册套件和两组传输检查；涉及共享代码的合并在 2026-11-04 实测约 21–28 分钟。PLC 模拟检查已删除，发布候选前手动运行 `merge-check -All`；没有 nightly，GitHub CI 不阻塞合并（[ADR 0031](../standards/decisions/0031-release-path.md)）。
- 架构：八版 FoundationHost，每个 MCP 会话一个 PLC worker；入口 `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`，worker 位于同目录 `worker/`。V20/V21 使用 MTA，V14 SP1–V19 使用 STA；MTA-1 的统一仍列在后续计划。引擎运行文件已退役，源目录残留已由 Q2/Q3 删除。各版工具数见[生成的目录](../reference/version-tool-catalog.md)。
- 品牌为自动化工程工作台；程序名、插件 ID、MCP 服务名 `tia-portal`、包名和仓库地址保持原有标识。GitHub 仓库改名仍在正式版计划内，未执行。

机器地址、PID、worktree 和运行中的检查必须从当前环境确认，不从本页推断。本页不以旧分支提交号证明当前分支是否完成或已验证。

## 待集成工作

以下是 2026-11-04 决定的顺序，不是完成比例；具体任务定义和状态在[计划](plan.md)。

| 阶段 | 已知状态 | 下一步 |
|---|---|---|
| NEW-REL-B、SEC-1 | 已合并（提交见[计划](plan.md)） | 无 |
| Q1–Q9、CPM | 已合并（提交见[计划](plan.md)） | 无 |
| NEW-REL-2/3、THIRD-1、主机测试精简 | 已合并（提交见[计划](plan.md)） | 无 |
| DOCS-1 → DOCS-2 → DOCS-3 | 已合并（`edbf4ef8`） | 无 |
| REORG-2、REORG-3、TIDY-1–6 | 已合并（`b0b4d9a7`、`edbf4ef8`） | 无 |
| beta.3 准备与公开 | 已发布（`1d57c277`） | 无 |
| PREP-SLIM、CI-FIX、CI-FIX2、TRANSPORT-FAST | 已合并（提交见[计划](plan.md)） | 看 GitHub CI 结果 |
| RELAY-1、UI-INT、UI-2c、P8-84 | 已合并（`d635fbb6`、`0f58eabc`、`400ad350`、`21a8a59a`、`32a8aa1c`、`699e989b`，集成修订 `ccf9b263`） | 真机复验仍待执行 |
| APR-05 | 进行中：关闭写操作审批的确认词输入与核对尚未实现 | 在现有确认面板增加确认词；普通审批不加输入框 |
| P8-04n1（N1-PORT） | 已合并（`fb3bb356`、`89ef43c4`、`1a4add6d`）：八版公开 profile 均为 `plc-foundation`，默认精简清单 | 不代表八版原生行为已验证 |
| VMFIX-3-PORT | 已合并（`8fe022bd`、`871d54da`）：确定失败与未知结果分开，未知结果仍要求重置 | 按计划复验真实导入和失败读回 |
| OUTROOT-1、DES-PORT、REVIEW-PORT、SCORE-PORT、CI-MSG | 进行中；DES-PORT 分 A/B/C，依次独立合并，取代 DES-INT；REVIEW-PORT、SCORE-PORT 取代旧任务 | 按计划中的 2026-11-04 决定实施，不能提前标记完成 |
| beta.4 其余工作 | VMFIX-4/5、DES-4 待执行；产品分支合并后执行 TIDY-7、`src/EngineHost` 改名和长行合并门；HANG-GUARD 等待任务说明 | 待编写说明的清单见计划 |

REVIEW-PORT 会把漏洞扫描带入新工具；目前不能宣称新准备命令已覆盖它。APR-05 正在增加关闭写操作审批时的确认词要求，当前代码尚未实现；该要求不用于每次普通批准。

## 真机结果

| 测试 | 结果 | 处理状态 |
|---|---|---|
| v4.0.0-beta.2，V21 VM，2026-10-31 至 11-01 | 原记录称 511 个工具：通过 213、失败 93、未测 192、按设计拒绝 12；TIA 重启 4 次 | 分类合计为 510，与原总数相差 1，待台账核对；相关任务见计划 |
| beta.3 测试包（`1dfe2067`），V21 VM，2026-11-02 至 11-03 | 512 个工具：通过 293、缺陷 103、按设计拒绝 60、不确定 1、未测 55；去重缺陷 59 个（HIGH 20）；会话中断 18 次，TIA 重启 2 次 | R1 对应 VMFIX-2 已合并，但这不是修复后复测结果；其余修复与复测待执行 |

原始记录在维护者本机 `bin-build/refactor/vm-test/`，尚待 FB-VM 登记。本次未连接 VM，也未新增真机结果。调查列出的无任务缺陷和测试缺口已在计划中标为提议，不能当作已分配或已修复。离线检查不能证明原生行为；验收边界以[真机台账](../reference/real-machine-ledger.md)为准。

## 测试者反馈

预发布测试者的报告、维护者在真机上看到的现象和我们按[预发布真机验证清单](../guides/beta-tester-checklist.md)做的 VM 回归都先记在本节（[ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md)）。复核前不写进[真机验收台账](../reference/real-machine-ledger.md)：`generate-prerelease-notes` 把台账四节（`未发布改动的验收边界`、`V20/V21 host + worker acceptance (P6-54)`、`4.0 安全策略验收计划（P6-R2）`、`4.0 发布前仍需的真实机器步骤`）的表格行写进预发布说明并逐字节核对，`generate-catalogs` 统计台账的 `current | NOT RUN` 行。

处理流程：

1. 收到 Issue（[表单](../../.github/ISSUE_TEMPLATE)）后在下表加一行，编号 `FB-NNN` 顺延，状态“待复核”。
2. 核对包名和 SHA-256、TIA 版本和 Update、发布键、工具参数、完整结果信封和证据；缺项时改为“待补充”，在 Issue 中列出。
3. 定性：缺陷建任务并写明编号（“已确认”）；符合预期标“通过”；与已有行相同标“重复”并指向原编号；该版本无此能力或不是本项目问题标“不适用”。
4. 修复合并后写明提交和首个含修复的版本，状态“已修复，待复测”；复测通过后“已关闭”。
5. 满足台账[“新记录应包含什么”](../reference/real-machine-ledger.md#新记录应包含什么)的通过结果由维护者写入台账（不动生成块和上述四节，除非同时重新生成下一个预发布的说明），本行写台账位置；在 Issue 中回复结论后关闭。

| 编号 | 收到日期 | 来源 | 包和 TIA 版本 | 清单项 | 工具 | 现象 | 状态 | 处理与去向 |
|---|---|---|---|---|---|---|---|---|
| FB-001 | 2026-10-26 | 维护者真机测试（截图） | v4.0.0-beta.1；TIA 版本未记录 | 不适用 | `EnsureUnifiedHmiTagTable`、`EnsureUnifiedHmiTag` | 以 `/组/子组/名称` 给出的 Unified 变量表在根目录建成以完整路径为名的表 | 已修复，待复测 | P8-81e、P8-81-int2，v4.0.0-beta.2 发布说明 “Tester reports”；复测见清单 V19–V21 行 |
| FB-002 | 2026-10-26 前后 | 测试者的 AI 客户端 | v4.0.0-beta.1；TIA 版本未记录 | T12 | 未知（某个接受对象路径的 Unified 工具） | 删除预览中对象路径写成字符串数组而不是属性步骤数组，随后 TIA 进程退出 | 待补充 | v4.0.0-beta.2 起格式错误路径在任何 TIA 调用前拒绝（P8-81-int2）；根因未确认，等测试者提供工具名、版本、参数和崩溃证据 |
| FB-003 | 2026-10-26 前后 | 测试者报告 | v4.0.0-beta.1；V20/V21 | 不适用 | `SetUnifiedHmiButtonEventScriptCode` | V20/V21 上返回结果未知（绑定器参数异常） | 已修复，待复测 | P8-81-int2 合并的宿主修复，v4.0.0-beta.2 发布说明 “Tester reports” |

清单项结果（每项按版本记最新结论和 `FB` 编号）：

| 清单项 | 已收到结果 | 仍缺的版本 |
|---|---|---|
| T01、T02、T04–T09（每个版本） | 无 | 14sp1、15.1、16、17、18、19、20、21 |
| T03 编译仅更改或全部重建 | 无 | 14sp1、15.1、16、17、18、19、20、21 |
| T10 工作台项目页 | 无 | 14sp1、16、21 |
| T11 V21 原生交叉引用 | 无 | 21 |
| T12 删除预览格式错误路径 | 无 | 19、20、21 |
| T13 HMI 面板容器 | 无 | 19、20、21 |

## 接续开发先读

1. [CLAUDE.md](../../CLAUDE.md) 和 [AGENTS.md](../../AGENTS.md)：分工、共用规则和合并规则。
2. [标准说明](../standards/README.md)：采用的规范范围和项目偏差。
3. [开发计划](plan.md)：各版范围、任务状态、放行门槛和待决定事项。
4. [验证](../manuals/validation.md)和[发布流程](../manuals/release.md)：检查分层、合并门和发布步骤。

修改工具示例后依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`，捕获工具定义契约前先构建；生成器都有逐字节检查模式，生成的哈希只由正式工具写入。发布后在本页记下版本、标签、候选摘要与验证结果；新发布路径不再生成受跟踪的 publication 构建记录。构建或离线结果都不授权连接 TIA、PLC、VM 或外部服务。
