# 0040 没有就绪的 TIA 环境时，会启动 worker 的工具在审批前拒绝

- 状态：已采纳
- 日期：2026-11-22
- 决策者：维护者
- 置信度：高
- 相关：补充 [ADR 0004](0004-precheck-before-approval.md)（先预检再审批）；[ADR 0007](0007-one-worker-per-mcp-session.md)；任务 RDY-RESTART、RC2-LOW

## 背景

ADR 0004 规定写请求先用预览预检，预检拒绝时不请求审批。rc.1 准备期间发现，没有安装 TIA 或 Openness 环境不完整时，`RestartOpennessWorker` 在 V20/V21 上先请求工作台审批，批准后才因 worker 无法启动而失败；其他会启动 worker 的工具也可能如此。用户批准了注定失败的请求。

## 选项

1. 保持现状，批准后再失败：实现不变；审批卡出现不可执行的请求。
2. 把环境就绪当作预检的一部分，在审批前检查：与 ADR 0004 的目的一致；审批前多一次本机安装检查，不调用 TIA。

## 决定

采用选项 2，八个版本相同。会启动 PLC worker 的工具在审批前检查 Openness 环境；环境未就绪时返回 `RESOURCE_UNAVAILABLE`（条件 `tia-openness-environment`），附原因和修复方法，不生成审批请求。只预览、只处理文件、不依赖 Openness 的工具照常可用。独立运行的模拟器重启沿用自己的准入规则（RDY-RESTART，随 rc.1 发布）；其余会启动 worker 的移植工具由 RC2-LOW 补齐（rc.2）。

## 后果

- 正面：审批卡只出现能执行的请求；没有 TIA 的机器上，错误直接说明缺什么。
- 负面与取舍：哪些工具不依赖 Openness 由宿主中审核过的名单决定，新工具默认受此检查。
- 验证方式：`PackagedNoTiaReadinessTests`（审批前拒绝、未生成审批请求）与就绪测试的隔离审批设置。
