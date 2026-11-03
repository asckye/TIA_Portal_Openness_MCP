# 开发进度

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本页记录当前状态、进行中的分支和测试者反馈，取代原当前交接和已归档的[测试者反馈台账](../archive/development/tester-feedback.md)。

范围、任务状态和放行门槛见[开发计划](plan.md)。机器地址、进程号、worktree 状态和运行中的检查从当前环境确认，不从本页推断。

## 当前状态

- 发布：最新正式版 [v3.3.0](../releases/v3.3.0.md)。4.0 预发布（GitHub 预发布，不设为最新版，3.3 更新器不提示）：[v4.0.0-beta.2](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.2)（2026-10-30，`7e9c4c65`，[说明](../releases/v4.0.0-beta.2.md)，记录 `manifest/publication-v4.0.0-beta.2.json`）；[v4.0.0-beta.1](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.1)（2026-10-22，`bc5dcc5a`，[说明](../releases/v4.0.0-beta.1.md)，记录 `manifest/publication-v4.0.0-beta.1.json`）。
- 4.0.0-beta.3 未发布：master 已含定稿的 CHANGELOG 条目和[发布说明](../releases/v4.0.0-beta.3.md)；还差 PIPE-INT（含 SLIM-1）和 VMFIX-2 两次合并，然后 nightly、维护者确认、发布，目标 2026-11-03。
- 架构：八个版本都用 FoundationHost，每个 MCP 会话一个 PlcWorker，一份会话状态（[ADR 0022](../standards/decisions/0022-engine-retirement-and-eight-release-unification.md)、[0029](../standards/decisions/0029-authoritative-session-state.md)）；入口 `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`，worker `runtime/v<key>/worker/TiaMcp.PlcWorker.<key>.exe`，负责线程在 V20/V21 为 MTA、在 V14 SP1–V19 为 STA（MTA-1 统一）。引擎程序已删除（`287bbac2`），宿主以退出码 64 拒绝 `--engine-*`，`src/Engine` 残留由 Q2 删除。工具按各版官方 API 注册，数量见[生成的工具目录](../reference/version-tool-catalog.md)。
- 检查：合并门 `check-all -Affected`，完整冷全集由 `nightly` 运行，失败或超时阻止下一次发布；RULES-1 合并前发布工具只采纳同一提交的冷 nightly，代码门槛 30 分钟（ADR 0025 为 45 分钟）。上一次主机门禁 348 个作业、112 分钟，SLIM-1 目标合并门少于 20 分钟、nightly 少于 30 分钟。GitHub CI 不阻塞合并，发布加 `-NoWait`。
- 品牌：对外名称为自动化工程工作台；程序文件名、插件 ID、MCP 服务名 `tia-portal`、交付包名和仓库地址不变，仓库改名排在 4.0 正式版（RENAME-GH）。

### 真机结果

| 测试 | 结果 | 去向 |
|---|---|---|
| v4.0.0-beta.2，V21 VM，2026-10-31 至 11-01 | 511 个工具：通过 213、失败 93、未测 192、按设计拒绝 12；TIA 重启 4 次 | VMFIX-1、DES-1、DES-5（beta.3）；DES-2..4（beta.4） |
| beta.3 测试包（`1dfe2067`），V21 VM，2026-11-02 至 11-03 | 512 个工具：通过 293、缺陷 103、按设计拒绝 60、不确定 1、未测 55；去重缺陷 59 个（HIGH 20）；会话中断 18 次（R1–R5），TIA 重启 2 次 | R1 由 VMFIX-2（beta.3）修复，其余由 VMFIX-3/4/5（beta.4）修复后复测 |

原始记录在维护者本机 `bin-build/refactor/vm-test/`（不入库），待登记进下文台账（FB-VM）。离线检查守的是接口形状，不证明原生行为；做验收判断前先读[真机验收台账](../reference/real-machine-ledger.md)。

## 进行中

worktree 在 `D:\Code\TIA_wt\<编号>`，分支 `refactor/<编号>`；每项在主机上运行受影响的检查后再推送，合并后删除分支。

| 分支 | 内容 | 状态 | 下一步 |
|---|---|---|---|
| `refactor/PIPE-INT` | PIPE-INT 第 1–4 轮加 SLIM-1 | `92d58d94` 加未提交的 SLIM-1；两个保留测试的失败待主机确认 | 主机门禁 → 提交 → 合并（beta.3） |
| `refactor/VMFIX-2` | R1 会话中断等 VM 修复 | `3a588b36`，第 2 轮 | 变基 → 主机门禁 → 合并 → nightly → 确认 → 发布 beta.3 |
| `refactor/VMFIX-3` | 写入结果如实 | `867efd36`，第 4 轮运行中 | 审查；VMFIX-2 合并后变基 |
| VMFIX-4、VMFIX-5a、VMFIX-5b | 会话生命周期；其余 HIGH 缺陷 | 任务说明已写 | VMFIX-2 合并后启动 |
| `refactor/Q1`…`Q7`、`CPM`、`Q8` | 代码精简第一组，叠在 PIPE-INT 上（顶端 `e53590d9`） | 已提交 | beta.3 发布后按开发计划的顺序合并 |
| `refactor/Q9` | 发布键从范围派生 | `99977ba3` | Q8 之后合并，再启动 Q6a、Q10a、Q10b |
| `refactor/REVIEW-1` | 自动审查检查（基于 Q8） | `427456ad`，第 3 轮排队 | SLIM-1 合并后变基 |
| `refactor/DES-INT` | DES-2、DES-3 | `5b74969a`，第 2 轮完成 | beta.4 合并，再启动 DES-4 |
| `refactor/N1-INT`、`UI-INT`、`UI-2c`、`RELAY-1`、`P8-84` | P8-04n1；UI-2b 加 UI-2d；审批页；远程中继；进程选择器 | `5c800dc0`、`390fb6e4`、`6942199f`、`0e4f5d5c`、`e9cca2d8` | beta.4 合并 |
| `refactor/DOCS-1` | 仓库整理批次 1 | 移动与链接已提交，内容合并进行中 | beta.3 发布后合并 |
| `refactor/RULES-1`、`SPD-6b` | 流水线项 | `4d3704fe`、`0d9cd054` | SLIM-1 合并后变基或重新评估 |
| `refactor/P8-10d`、`P8-10e`、`P8-10f` | 发布链 Python 改 C# | 未验证的 WIP | 随整理批次 2 |

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
| T10 工作台工程操作页 | 无 | 14sp1、16、21 |
| T11 V21 原生交叉引用 | 无 | 21 |
| T12 删除预览格式错误路径 | 无 | 19、20、21 |
| T13 HMI 面板容器 | 无 | 19、20、21 |

## 接续开发先读

1. [CLAUDE.md](../../CLAUDE.md) 和 [AGENTS.md](../../AGENTS.md)：分工、共用规则和合并规则。
2. [标准说明](../standards/README.md)：采用的手册部分和项目偏差。
3. [开发计划](plan.md)：各版范围、任务状态、放行门槛和待决定事项。
4. [验证](../manuals/validation.md)和[发布流程](../manuals/release.md)：检查分层、合并门和发布步骤。

修改工具示例后依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`，捕获契约或响应前先构建；生成器都有逐字节检查模式，生成的哈希只由正式工具写入。发布后在本页记下发布并更新 `manifest/publication-vX.Y.Z.json`。构建或离线结果都不授权连接 TIA、PLC、VM 或外部服务。
