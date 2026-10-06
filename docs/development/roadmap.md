# 路线图与待办（v3.3.0）

[当前交接](handoff.md) · [Openness 覆盖](../reference/openness-coverage.md) ·
[版本工具矩阵](../reference/version-tools.md) · [重构计划](refactor-plan.md)

最新已发布版本为 v3.3.0。4.0 的契约硬切换、工具迁移、工作台审批与审计功能以及产品布局调整已在当前源码实现：
阶段 6 的 P6-01～P6-41、P6-44～P6-48 已合并，P6-42 发布候选验证和 P6-43 发布说明进行中。4.0 的真机验收尚未进行。

## 发布候选工作

| 工作 | 当前状态 |
|---|---|
| P6-42：八版构建、离线套件、重定位、交付包校验和台账复核 | 进行中；记录实际纳入与延期的行为族 |
| P6-43：4.0 发布说明 | 进行中；给出旧→新工具名/参数与产品入口对照、V4 契约、安全政策、目录变化和已验收证据 |
| 真机验收 | 待进行。逐版基线/候选结果记入真机台账，离线和静态检查不能代替 |

MCP 的 `WRITE` / `ONLINE-WRITE` 调用默认开启审批：写调用派发前须在工作台批准；拒绝、超时或工作台未连接都在原生调用前拒绝。
审计记录保存在所配置的数据根目录。

## 仍需处理的工程边界

- 拟纳入发布的每个行为族，须完成对应版本的原生验收；没有验收证据的族保持 `current / NOT RUN`。
- 保留已知 Openness 观察结论：`p2051[0]` BICO 读取崩溃和 Unified 库脚本改名崩溃尚未解决；PLC 原生交叉引用仍受限。
  见 [Openness 限制](../troubleshooting/openness-limitations.md)。
- 其他共享离线构建器在文档说明处仍输出 V21 候选 XML；改扩展名或版本标记不会转换格式。
- Foundation 绑定快照仍是生命周期集成边界；不得把仅凭 PID 的附着描述为已验证的进程启动身份。
- 八版能力复核保持在[功能矩阵](../reference/openness-coverage.md)与
  [机器可读功能证据](../../reference/version-feature-matrix.json)中更新。
- 未打开工程时调用 `SaveProject` 目前返回 `OUTCOME_UNKNOWN` 并要求重置会话，但原生保存并未执行；应改为操作前拒绝
  （`PRECONDITION_FAILED` / `rejected-before-operation`）。

## 3.3 时期遗留的改进

- 缺少 .NET Framework 4.8 时，启动前给出中文提示。
- 桥接进程的 stderr 诊断按控制台代码页（GBK）写出却按 UTF-8 读取，工作台日志中的中文会乱码。
- 新 worker 首次附着超时时，TIA 通常在等待 Openness 访问确认（“全部选是”）；超时消息与工作台日志应提示这一点，
  而不只是“Worker timed out; native outcome is unknown.”。

## 维护

工具参数、语言示例、结果解读和调用顺序统一维护在 `reference/tool-examples`，由生成的 `GetToolUsage` 目录提供；
提出调用前先读取所连接版本的 schema。原来的配方功能改用 `GetToolUsage` 的 `exampleKind: "sequence"` 或
`exampleId: "sequence/<topic>"`；编写指南类主题使用其 language、query 和示例选择器。

发布历史记在 CHANGELOG 和各版本发布说明中；API 成员覆盖和逐版功能缺口记在 [Openness 覆盖](../reference/openness-coverage.md)。
不要从 API 签名、工具名称、相邻版本、离线测试或构建结果推断支持情况。
