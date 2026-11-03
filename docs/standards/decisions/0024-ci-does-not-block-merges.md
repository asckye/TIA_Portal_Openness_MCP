# 0024 合并以本机受影响的检查为门，GitHub CI 不阻塞合并和发布

- 状态：已采纳
- 日期：2026-10-30（合并门与 CI 角色）；2026-11-01（写成 ADR）
- 决策者：维护者
- 置信度：高
- 相关：[ADR 0019](0019-incremental-check-all.md)、[0023](0023-release-4-0-final-criteria.md)、[0025](0025-release-evidence-nightly-and-verified-build-units.md)；[验证分层](../validation.md)；[发布流程](../release-workflow.md)；任务 SPD-4、RULES-1

## 背景

此前的规则是：合并需要干净的套件和 GitHub CI 通过，发布命令默认等待 CI（`-CiTimeoutMinutes` 默认 50 分钟）。CI 的 offline-checks 约 35 分钟、validate-bundle 约 41 分钟，等待 CI 让合并和发布都慢了半小时以上；本机已经能运行同样的检查，并有 WSL 复现 Ubuntu 任务。维护者 2026-10-30 抱怨开发、尤其是发布要花几天，批准了提速规则。

## 选项

1. 合并和发布都等 CI：多一层独立机器的确认；每次合并和发布多等 30–50 分钟。
2. 合并以本机受影响的检查为门，完整冷全集放到每日 nightly；CI 照常运行但不阻塞合并和发布。

## 决定

采用选项 2（维护者 2026-10-30 批准提速规则，2026-11-01 确认）。

- 合并门：本机 `check-all -Affected`（受影响作业；未选作业只有与最近一次成功 nightly 的作业身份和产物哈希相同才复用）加任务涉及的检查通过。目标少于 10 分钟。
- 完整冷全集每日在 nightly 运行，合并不等待它；nightly 失败或超时阻止下一次发布（ADR 0025）。
- GitHub CI 照常在每次推送上运行，不阻塞合并；发布使用 `-NoWait`，不等待 CI。CI 失败按缺陷处理，在 master 上向前修复。
- ADR 0023 要求 4.0 正式版的 CI 在发布提交上通过：发布命令仍用 `-NoWait`，CI 结果在维护者确认前另行核对。
- 检查只在合并、nightly 与 CI 之间移动或按身份复用，不删除、不放宽；删除或放宽仍须维护者批准。

## 后果

- 正面：合并和发布不再等待远程 CI；本机反馈更快。
- 负面与取舍：CI 发现的问题可能在合并之后才看到，需要有人读 CI 结果；`release -CiTimeoutMinutes` 的默认等待在实现改为默认不等待之前仍然存在，发布时须显式加 `-NoWait`。
- 后续工作：RULES-1 把三档验证（受影响、nightly、发布只核对）写进发布工具，并把不等待 CI 设为发布默认值；`CLAUDE.md`、`AGENTS.md` 已按本决定更新。
- 验证方式：`check-all -Affected` 的 `affected.json` 记录选择与复用依据；发布记录中的 CI 等待步骤显示 `-NoWait`。

**maintainer decision 2026-11-03（整套精简，SLIM-1）**：批准删除延迟门禁/基准、HTTP 稳定性作业、独占安静阶段、suite-output-parity 及重复编译配置；V14 SP1–V19 只保留 contract 捕获，V20/V21 保留功能 response 捕获。构建改用单一交付配置的 MSBuild 增量图，移除逐单元 portable-cache Prepare/Complete 热路径；保留 rounds 1–4 与旧发布证据验证。新作业清单和目标见[验证分层](../validation.md)；SLIM-1 round 2 已落实 nightly/release 45 分钟门槛，merge 仍为 30 分钟。
