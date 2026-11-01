# 开发流程

状态：现行 · 最后核对：2026-11-01，master `902a86fe`

[文档目录](../README.md) · [验证分层](../development/validation.md) · [发布流程](../development/release-workflow.md) · [测试策略](../standards/testing.md) · [当前交接](../development/handoff.md)

本文说明一项改动从提出到进入 master 的过程：谁做什么、改动需要哪些设计产物、合并前后运行哪一档检查。命令细节见 [验证分层](../development/validation.md)，发布步骤见 [发布流程](../development/release-workflow.md)。

本文描述的三档验证、机器级重型作业锁、`regen-all` 和 Codex 沙箱可跑子集已于 2026-11-01 决定，由 RULES-1 实现，尚未全部落地；第 4 节逐项写明现状。

## 1 角色

| 角色 | 负责 | 不做 |
|---|---|---|
| 维护者 | 决定范围、优先级和设计取舍；批准 ADR、阈值或断言的改变、发布 | 不需要亲自运行检查 |
| Claude | 研究与诊断；写任务说明和 ADR 草稿；审查 diff；在主机上运行检查；提交、变基并推送到 master；更新交接与计划 | 不写新功能代码（合并时的一行变基修正、文档笔误除外） |
| Codex | 在独立 worktree 的沙箱中按任务说明实现代码和测试，构建并列出需要在主机上运行的检查 | 不执行 `git add`、`git commit`、`git stash`、推送或合并；不连接 TIA、PLC、虚拟机或外部网络服务 |

依据：[重构计划](../development/refactor-plan.md)“任务流程”、[AGENTS.md](../../AGENTS.md)、ADR 待办 B37（见 [架构决策记录](../development/decisions/README.md)）。

## 2 任务生命周期

1. 记录：维护者提出或审查中发现的事项，当天写入任务表（[重构计划](../development/refactor-plan.md)）或 ADR。
2. 诊断与任务说明：Claude 先定位原因，再写任务说明。任务说明写明根因、涉及的文件和函数、预期设计、允许修改的路径、修改前失败而修改后通过的测试、验证命令和时间预算。
3. 实现：从当前 master 建 worktree 和 `refactor/<任务 ID>` 分支，Codex 实现并构建，改动留在 worktree 中。
4. 审查：Claude 审查 diff，在 worktree 中运行任务涉及的检查。检查失败时先定位原因，再带具体问题退回同一 Codex 会话，不反复重跑完整门禁。断言、阈值或检查的改变先请维护者批准。
5. 提交与合并：Claude 用明确的 `git add` 路径在任务分支上提交，变基到 master，运行合并门（第 4.1 节），通过后快进推送到 master，删除任务分支。
6. 收尾：更新任务状态和 [当前交接](../development/handoff.md)；GitHub CI 的失败按缺陷处理，在 master 上向前修复。

## 3 变更级别与设计产物

| 级别 | 典型改动 | 需要的产物 |
|---|---|---|
| L0 | 文档措辞、笔误、注释 | 无；按 [文档写作规范](../development/design-spec/documentation-style.md) 写 |
| L1 | 不改变 MCP 契约的缺陷修复、重构、测试补充 | 任务说明 |
| L2 | 新工具、契约变化（工具名、参数、返回结构、错误语义）、新功能 | 任务说明，加 [工具规格](../templates/tool-spec.md) 或设计文档（`docs/development/design/`） |
| L3 | 改变进程或部署结构、MCP 契约政策、安全或数据完整性边界、发布与质量门禁规则、外部基线；推翻已有 ADR | 以上产物，加 ADR（[文档写作规范](../development/design-spec/documentation-style.md) 第 14.1 节） |

### 3.1 开始条件（DoR）

- 任务说明写明范围、预期设计、验证命令和时间预算。
- L2 的工具规格或设计文档已经审查；L3 的 ADR 已由维护者采纳。
- 依赖的分支已经合并，worktree 从当前 master 创建。

### 3.2 完成条件（DoD）

- 代码、测试、生成文件和文档在同一个任务中更新；生成文件只由正式写入方产生（[测试策略](../standards/testing.md) 第 4 节）。
- 合并门通过，任务涉及的检查通过。
- MCP 契约变化的差异已逐项解释；破坏性变化写入发布说明和迁移表。
- 新增 MCP 文本为英文；用户可见的变化写入 `CHANGELOG.md` 的 `[Unreleased]` 段或版本说明草稿。版本条目只由 `release` 命令在发布提交中写入，最新版本条目必须等于 `Version.props` 的版本。
- 交接和任务状态已更新。

## 4 验证

检查分三档。检查只在三档与 CI 之间移动或按身份复用，不删除、不放宽；删除或放宽须维护者批准。

| 档 | 何时 | 命令 | 结果能否作为发布证据 |
|---|---|---|---|
| 合并门 | 每次合并前，在主机上 | `check-all -Affected` 加任务涉及的检查 | 否 |
| nightly | 每日一次，无人值守 | `nightly`（完整冷全集） | 是 |
| 发布只核对 | 发布时 | `release` | 不适用 |

开发时可用 `check-all -Affected -Quick` 自检，报告标为 `quick`，不能作为合并或发布证明。开发和合并运行可以加 `-FailFast`；nightly 拒绝该开关。

### 4.1 合并门

- 现状：`check-all -Affected [-Base origin/master]` 已实现。快门禁始终执行，其余作业按改动路径选择；未映射路径、发布工具、套件注册表、全局构建规则、NuGet 或 SDK 改动回退为完整的合并档。未选作业只在与最近一次成功 nightly 的作业身份和产物哈希相同时复用。选择依据记在 `affected.json`。
- 目标少于 10 分钟（维护者 2026-10-30）。
- 合并在主机检查通过后直接推送，不等待 nightly，也不等待 GitHub CI。

### 4.2 nightly

- 现状：`nightly` 在 `bin-build/nightly/<日期>-<提交>` 建立独立检出，运行 `check-all -Profile nightly -Clean -NoBuildCache`。失败或超时阻止后续发布，直到相关提交上有更晚的成功 nightly。每日 03:00 的计划任务由维护者手工登记，工具不登记。
- 门槛：当前实现为 30 分钟。维护者 2026-11-01 决定改为 45 分钟（ADR 0025，随 S0 文档批次合并），由 RULES-1 实现。

### 4.3 发布只核对

- 现状：发布只采纳同一干净提交上冷启动、不用构建缓存的完整 nightly 运行（`release -AdoptEvidence`）；合并门、`quick` 和开发缓存的结果不能作为发布证据。
- 决定（维护者 2026-11-01，ADR 0025）：发布证据改为最近一次通过的 nightly，加上该 nightly 之后改动部分的已验证增量构建单元；发布只核对、打包和上传。由 RULES-1 在 SPD-3/SPD-5 合并后实现。

### 4.4 GitHub CI

- CI 在每次推送时照常运行，不阻塞合并（ADR 0024，随 S0 文档批次合并）。
- 现状：`release` 默认等待 CI（`-CiTimeoutMinutes`，默认 50 分钟）；按 ADR 0024 发布时显式加 `-NoWait`。RULES-1 把不等待设为默认值。
- CI 失败按缺陷处理，在 master 上向前修复。

### 4.5 已决定、尚未实现

| 事项 | 内容 | 任务 |
|---|---|---|
| 机器级重型作业锁 | `check-all`、`nightly`、发布和 `test-suites` 取同一把系统级锁，等待者看到持有者的命令、PID 和开始时间 | RULES-1 |
| `regen-all` | 一条命令依次运行全部生成器，并提供 `-Check` | RULES-1 |
| Codex 沙箱可跑子集 | 只运行沙箱中能运行的检查，其余标为只在主机运行 | RULES-1 |
| 收拢验证入口 | 其他临时门禁脚本退役或改为三档的别名，检查保留在三档之中 | RULES-1 |

## 5 提交与分支

- master 是唯一长期分支；任务分支审查并通过主机检查后变基推送到 master，合并后删除。
- 提交信息用英文祈使句，不加 AI 署名。
- 用明确的 `git add` 路径，不用 `git add -A`。
- 运行二进制、本地 SDK、用户项目、设计交接和密钥不进 Git。
- 不手改测试哈希、`manifest/*.json` 中的哈希和生成文件。
- 构建与发布自动化只在 `build-tools/release` 中用 C# 维护；仓库拒绝新增 PowerShell、batch 和 cmd 文件。
- 文档改动核对链接、路径、工具适用版本和引用，并运行 `python scripts/checks/Check-Repository.py --no-binaries`。

## 6 发布衔接

- 每次发布前由维护者确认；发布期间不改代码。
- 4.0 的版本以 GitHub 预发布 `v4.0.0-beta.N` 交给测试者，不设为最新版（[ADR 0020](../development/decisions/0020-prereleases-tested-by-users.md)）。
- 发布命令与步骤见 [发布流程](../development/release-workflow.md)；发布后更新交接与 `manifest/publication-v<版本>.json`。
- 4.0 正式版的放行条件由维护者 2026-10-30 决定（ADR 0023，随 S0 文档批次合并）。

## 7 并行与资源

- 同时运行的 Codex 任务数由维护者决定；有依赖的任务串行。
- 只结束自己启动、并核对过可执行文件路径的进程；不按进程名或模糊匹配结束进程。
- 主机负载高时，计时敏感的检查可能失败；机器级锁实现前，避免在 Codex 构建时运行完整门禁。
