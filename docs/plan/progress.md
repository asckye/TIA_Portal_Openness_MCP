# 当前开发交接

状态：现行 · 最后核对：2026-11-01，master `27675208`（4.0.0-beta.3 准备提交，beta.3 尚未发布）

[发布计划](../status/release-plan.md) · [路线图](roadmap.md) · [重构计划](refactor-plan.md) · [验证分层](validation.md) · [发布流程](release-workflow.md) · [架构决策记录](decisions/README.md) · [测试者反馈台账](tester-feedback.md)

本页只记录当前源码事实、正在进行的分支和接续方法。范围与任务状态见[发布计划](../status/release-plan.md)，阶段 0–8 的历史任务见[重构计划](refactor-plan.md)，决定见 [ADR](decisions/README.md)。机器地址、进程号和运行中的检查从当前环境确认，不从本页推断。

## 发布

- 最新正式版：[v3.3.0](../releases/v3.3.0.md)。
- 4.0 预发布（GitHub 预发布，不设为最新版，3.3 更新器不提示）：
  - [v4.0.0-beta.2](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.2)，2026-10-30，`7e9c4c65`，[说明](../releases/v4.0.0-beta.2.md)，记录 `manifest/publication-v4.0.0-beta.2.json`。采纳了 `db6b51fd` 的冷 check-all 证据（377 通过，2825 s），用过一次维护者批准的 30 分钟门槛例外。
  - [v4.0.0-beta.1](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v4.0.0-beta.1)，2026-10-22，`bc5dcc5a`，[说明](../releases/v4.0.0-beta.1.md)，记录 `manifest/publication-v4.0.0-beta.1.json`。
- 4.0.0-beta.3 未发布：2026-11-01 的发布运行已停止，GitHub 上没有 beta.3。master 的 `27675208` 是准备提交（版本号、CHANGELOG 条目、带版本号的响应基线），[发布说明草稿](../releases/v4.0.0-beta.3.md)和 CHANGELOG 条目在发布前按扩大后的范围补写。维护者 2026-11-01 把原定 beta.4 的内容、项目审查的各项和工作台重新设计并入 beta.3，T-IA 批次与生态小项改为 beta.4，见[发布计划](../status/release-plan.md)。
- 门槛：预发布交给测试者，我们在 VM 上按[预发布真机验证清单](../guides/beta-tester-checklist.md)回归（[ADR 0020](decisions/0020-prereleases-tested-by-users.md)）；4.0 正式版条件见 [ADR 0023](decisions/0023-release-4-0-final-criteria.md)；合并门与 CI 见 [ADR 0024](decisions/0024-ci-does-not-block-merges.md)；发布证据见 [ADR 0025](decisions/0025-release-evidence-nightly-and-verified-build-units.md)。

## 当前源码

- 八个版本都使用 FoundationHost，每个 MCP 会话一个 PlcWorker（[ADR 0005](decisions/0005-v20-v21-on-foundation-host.md)、[0007](decisions/0007-one-worker-per-mcp-session.md)、[0022](decisions/0022-engine-retirement-and-eight-release-unification.md)）。入口为 `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`，worker 为 `runtime/v<key>/worker/TiaMcp.PlcWorker.<key>.exe`；worker 的负责线程在 V20/V21 为 MTA，在 V14 SP1–V19 为 STA（`src/Adapters/Policy/PlcLifecyclePolicy.cs`，线程模型仍待决定）。
- 引擎程序和构建、启动引擎的路线已删除（P8-04l 第 1 阶段，`287bbac2`）；宿主以退出码 64 拒绝 `--engine-*` 选项。Q1 已移除生成器的引擎读取；Q2 已删除 `src/Engine` 残余源码及引擎专属测试、脚本，保留代码的测试转入 KeptCode。宿主引擎客户端、目录和死条件分支由 Q3 继续处理。宿主命令行子集为 `doctor`、`audit verify`、`schema`、`version`、`config`、`install-plc-tools`、`help`。交付记录为 `manifest/multi-version-build.json`；`manifest/release-build.json` 不再交付。
- 工具按各版官方 API 注册，数量不拉平（[ADR 0012](decisions/0012-old-release-tool-registration.md)、[0014](decisions/0014-tool-counts-follow-official-api.md)）；各版数量见[生成的工具目录](../reference/version-tool-catalog.md)。master 上已有、将随 beta.3 发布的八版 READ 工具 `AnalyzePlcCrossReferences`（`f51c9f20`）；原生交叉引用在 V14 SP1–V20 默认允许，V21 由工作台会话开关打开（`4b17a042`）。
- 2026-10-19 官方 API 审计的修复 P8-81a–g 已全部合并；HMI 复核 P8-96a 与 HMI-B3（`e5c3d365`）已合并，旧 Ensure/Apply/Bind 工具默认预览。
- 检查：合并门为 `check-all -Affected`（SPD-4，`bf04b534`），开发和合并运行可加 `-FailFast`（`c68c31d3`）；完整冷全集由 `nightly` 命令运行，失败或超时阻止下一次发布。发布工具目前只采纳同一提交上冷启动、不用构建缓存的 nightly 运行，nightly 门槛仍为 30 分钟；按 ADR 0025 改为“最近一次通过的 nightly 加已验证构建单元、45 分钟门槛”由 RULES-1 实现。GitHub CI 照常运行，不阻塞合并；发布加 `-NoWait`。
- 品牌：对外名称为自动化工程工作台（Automation Engineering Workbench）；程序文件名、插件 ID、MCP 服务名 `tia-portal`、交付包名和仓库地址不变，GitHub 仓库改名排在 4.0 正式版。

## 真机结果

2026-10-31 至 11-01，我们用已发布的 v4.0.0-beta.2 在 V21 虚拟机上按测试清单测试：511 个工具中通过 213、失败 93、未测 192、按设计拒绝 12，TIA 重启 4 次。原始记录在维护者本机 `bin-build/vm-test/beta2-20261031/`（不入库），尚待登记进[测试者反馈台账](tester-feedback.md)。主要问题：读超时迫使重启 TIA、失败读取污染会话、`OUTCOME_UNKNOWN` 误用、重复的 L2 工具失败、参数约定不一致、`nativeMessage` 几乎总为空。维护者 2026-11-01 决定在 beta.3 中全部修完（VMFIX-1、DES-1..5），修好的项在 VM 上复测。离线检查守的是接口形状，不证明原生行为；做验收判断前先读[真机台账](../reference/real-machine-ledger.md)。

## 正在进行的分支

| 分支或任务 | 内容 | 状态 |
|---|---|---|
| `refactor/examples-v20v21` | V20/V21 改用维护的调用示例，不再使用冻结的引擎值；重新生成工具档案与用法数据 | `9f43d38d`（2 个提交），待审查合并 |
| `refactor/SPD-3c` | SPD-3/3b/3c（可复现构建、共享库只编译一次、已验证构建单元缓存）与 SPD-5（合并时准备发布、发布按哈希发布已封存的候选） | 领先 master 4 个提交，待在空闲机器上做本机验证和一次发布演练 |
| `refactor/DES-1` | 一份会话状态；MCP 宿主在工作台可见、可重启 | `dc3123b1`，待审查与主机测试 |
| S0 文档 | 本页、发布计划、ADR 0020 修订与 0022–0025、文档矛盾、`AGENTS.md`/`CLAUDE.md`、设计规范清理 | 待审查，未提交 |
| `refactor/VMFIX-1` | 修复 beta.2 真机发现（超时与 TIA 锁、会话污染、`OUTCOME_UNKNOWN`、L2 工具、`expectedProject`、设备白名单） | `f5bd3798`，待审查与主机测试 |
| `refactor/DES-5` | 大结果分页；`DecodePlcSimaticMl` 的 Python 依赖 | Codex 实施中 |
| `refactor/SU-1` | 软件单元（V16–V21）的导入导出范围、导入选项、单元内容读取、`SetPlcUnitObjectAccess` | 已建分支 |
| DES-2、DES-3、DES-4 | 一项功能一个实现与安全移块；每个写入可预览与回滚；统一参数模型与诊断 | 排队（任务说明已写），按顺序串行 |
| RULES-1 | 三档验证、nightly 45 分钟门槛、发布证据按 ADR 0025、机器级重型作业锁、`regen-all` | 排队（SPD-3c 合并后） |
| RELAY-1 | 代理安全的本地 stdio 中继与环境检查项 | 排队 |
| UI 设计 | 按已采用的 EplanMcp 工作台设计系统重新设计工作台；设计规范由 UI 设计代理起草，之后分批实施 | 规范起草中 |
| `refactor/SPD-6b` | 按用例成本分片，压缩 nightly | 落后 master，待按 45 分钟门槛重新评估（4.0 正式版前） |
| `refactor/P8-10d`、`refactor/P8-10e` | 发布链 Python 改 C# 批 4、5 | 只有未验证的 WIP，4.0 正式版前完成 |

## 接续顺序

1. 合并顺序（维护者 2026-11-01）：`examples-v20v21` → SPD-3c → DES-1 → S0 文档 → VMFIX-1 → DES-5 / SU-1，然后 DES-2、RULES-1、RELAY-1、DES-3、DES-4、工作台 UI 各批。每项都在主机上运行受影响的检查后再推送；SPD-3c 合并后做一次发布演练。
2. 同时最多三个 Codex 任务。每批修复合并后在 VM 上复测对应的 beta.2 发现，结果写进反馈台账。
3. beta.3 的其余项（P8-04n、P8-21f、P8-84、DES-3 措辞跟进、beta.2 VM 发现登记）见[发布计划](../status/release-plan.md)；全部完成后补写 CHANGELOG 条目和发布说明，再按 ADR 0025 的证据规则发布。发布期间不改代码。
4. beta.4（T-IA 批次与生态小项）和 4.0 正式版的工作按[发布计划](../status/release-plan.md)排期；文档重组（已批准方案的 D0–D1b）需要一个没有在途文档改动的窗口。

派发 Codex 前先查明根因，在任务说明中写明涉及的文件和函数、预期设计、修改前应失败而修改后应通过的测试、精确的验证命令；主机检查失败时先自己定位原因，再把具体修改说明交回同一 Codex 会话，不反复重跑完整门禁。

## 继续开发

先读[验证分层](validation.md)、[运行时布局](runtime-layout.md)和[设计规范](design-spec/README.md)。可编辑的工具示例在 `reference/tool-examples`，目录决定在 `reference/tool-catalog`。先运行 `generate-catalogs`，再 `generate-tool-usage`，再 `generate-tool-counts`；捕获契约或响应前先构建。迁移表改动运行 `generate-release-migration`。所有生成器都有逐字节检查模式。

打包按[发布流程](release-workflow.md)执行。生成的哈希只由正式工具写入。任何构建或离线结果都不授权连接 TIA、PLC、VM 或外部服务。
