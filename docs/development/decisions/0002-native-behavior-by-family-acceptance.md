# 0002 原生行为按 D1 行为族真机验收后才切换为 safe-v4

- 状态：已采纳
- 日期：2026-10-03
- 决策者：维护者
- 置信度：高
- 相关：[阶段 6 契约规范](../phase6-review.md) 第 4 节与第 6 节（决策 4、10）；[响应与错误](../response-and-errors.md)“V4 D1 行为能力披露”；[真机台账](../../reference/real-machine-ledger.md)；[V4 工具协议规范](../design-spec/v4-protocol.md) 第 3 节与第 9.3 节；[ADR 0013](0013-release-4-0-after-phases-7-and-8.md)

回溯记录（写于 2026-10-11）。

## 背景

D1 统一的显式安全政策（默认不覆盖、拒绝升级、精确创建、写操作预览后确认）会改变原生调用的行为。编译、模拟对象和传输测试都不能证明原生语义，只有真机验收能证明；而八个版本的 Openness API 各不相同。

## 选项

1. 4.0 一律切换到新政策：行为统一；但未经真机验证的原生行为可能损坏工程。
2. 按行为族逐版验收后切换，未验收的族保持 3.x 的原生行为并明确披露：安全；但 4.0 中新旧两种政策并存。

## 决定

采用选项 2。原生行为按 D1 行为族、逐个发布版本完成真机验收后才切换为 `safe-v4`；未验收的族保持 `current` 行为，在工具描述和 `meta.behaviorPolicy` 中标明，并在结果中附 `UNVERIFIED_BEHAVIOR` 警告。SafeV4 候选实现只在显式的 `TiaMcpTestPolicy` 测试构建中可选。

## 后果

- 正面：未经验证的原生行为不会以新政策的名义发布；客户端能从每个结果知道行为是否已经验收。
- 负面与取舍：4.0 的发布配置中八个版本全部为 `current / NOT RUN`；预览返回旧的数据形状，D1 候选协议（`mode`、`data.plan`）只在测试构建中可用（API-39）；`UNVERIFIED_BEHAVIOR` 出现在大量结果中。
- 后续工作：真机验收在阶段 8 结束后集中进行（ADR 0013），结果记入真机台账。
- 验证方式：`V4Validation` 要求 `behaviorPolicy == current` 时必须带 `UNVERIFIED_BEHAVIOR`；`ToolProfiles.resx` 的 `behaviorPolicies` 与契约快照中的 `behaviorCapabilities`。
