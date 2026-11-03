# 0025 发布证据采用最近一次通过的 nightly 与已验证的增量构建单元

- 状态：已采纳（实现见任务 RULES-1）
- 日期：2026-10-30（增量证据）；2026-11-01（nightly 门槛与采纳规则）
- 决策者：维护者
- 置信度：中
- 相关：[ADR 0019](0019-incremental-check-all.md)、[0024](0024-ci-does-not-block-merges.md)；[验证分层](../../manuals/validation.md)；[发布流程](../../manuals/release.md#采纳同一提交的-check-all-与-ci-证据-adoptevidence)；任务 SPD-3、SPD-5、SPD-6、RULES-1

## 背景

2026-10-27 的规则要求发布证据来自同一提交上冷启动、不用缓存的完整运行（`-Profile nightly -Clean -NoBuildCache`），并受 30 分钟门槛约束；增量复用只用于日常合并。2026-10-30 维护者批准发布证据可以来自经过验证的增量构建（带输入哈希的缓存）。但实现仍只采纳冷运行；唯一一次 nightly 在 30 分钟时被终止，SPD-6b 的回放推算 nightly 需要 37–40 分钟。beta.2 的发布证据还用过一次性的时间门槛例外。

## 选项

1. 保留同提交冷运行和 30 分钟门槛：证据最直接；每次发布前要跑一次 30–47 分钟的冷全集，门槛与 nightly 的工作量冲突。
2. 发布证据采用最近一次通过的 nightly 加已验证的增量构建单元；nightly 门槛改为 45 分钟。
3. 把 nightly 中无 SDK 的 Foundation 部分完全交给 CI：nightly 更短；本机证据少一块。

## 决定

采用选项 2（维护者 2026-11-01：“按你的建议来”），撤销 2026-10-27“增量绝不作为发布证据”的条款。

- 发布证据：最近一次通过的 nightly（完整冷全集），加上从该 nightly 之后改动部分的已验证增量构建单元（SPD-3 的冷验证标识，输入哈希一致才复用）。
- nightly 门槛由 30 分钟改为 45 分钟；nightly 失败或超时阻止下一次发布，直到相关提交上有更晚的成功 nightly。
- 合并门（`check-all -Affected`）的结果不能作为发布证据。
- 无 SDK 的 Foundation 部分以后可以移到 CI（选项 3），届时另行决定。
- 发布只核对、打包和上传（SPD-5：合并时准备，发布按哈希发布已封存的候选）。

## 后果

- 正面：发布不再等待一次完整冷运行；nightly 门槛与其工作量一致，不再需要一次性例外。
- 负面与取舍：增量单元的正确性依赖输入哈希覆盖完整；门槛放宽到 45 分钟属于维护者批准的阈值变化，其他检查、断言和超时不变。
- 后续工作：RULES-1 在 SPD-3/SPD-5 合并后实现：采纳规则、机器级重型作业锁、`regen-all` 和 Codex 沙箱可跑子集。实现合并前，发布工具仍只采纳同提交的冷 nightly 运行，nightly/release 门槛已由 SLIM-1 round 2 改为 45 分钟（[验证分层](../../manuals/validation.md)描述的是当前实现）。
- 验证方式：发布工具测试覆盖采纳最近通过的 nightly、拒绝失败或超时的 nightly、拒绝输入哈希不一致的构建单元；nightly 报告记录 45 分钟门槛。

**maintainer decision 2026-11-03（整套精简，SLIM-1）**：批准删除延迟门禁/基准、HTTP 稳定性作业、独占安静阶段、suite-output-parity 及重复编译配置；V14 SP1–V19 只保留 contract 捕获，V20/V21 保留功能 response 捕获。构建改用单一交付配置的 MSBuild 增量图，移除逐单元 portable-cache Prepare/Complete 热路径；保留 rounds 1–4 与旧发布证据验证。新作业清单和目标见[验证分层](../validation.md)；SLIM-1 round 2 已落实 nightly/release 45 分钟门槛，merge 仍为 30 分钟。
