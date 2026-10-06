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
| P6-50～P6-53：仓库脚本改为 C# | 进行中；4.0 不再包含 PowerShell、批处理和 cmd 文件 |
| P6-54：V20/V21 宿主 + worker 运行方式 | 代码完成（含 P6-54b：打包后无 TIA 照常启动并报告原因）；L5 真机验收待做，默认隔离待验收后切换 |
| P6-55：Foundation 可用性与审计修复 | 完成；来自 V14 SP1/V15.1/V16 真机验收 |
| P6-56：无 TIA 发布机的 V20/V21 响应快照 | 待启动；发布链 08/09 前置 |

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
## 维护

工具参数、语言示例、结果解读和调用顺序统一维护在 `reference/tool-examples`，由生成的 `GetToolUsage` 目录提供；
提出调用前先读取所连接版本的 schema。原来的配方功能改用 `GetToolUsage` 的 `exampleKind: "sequence"` 或
`exampleId: "sequence/<topic>"`；编写指南类主题使用其 language、query 和示例选择器。

发布历史记在 CHANGELOG 和各版本发布说明中；API 成员覆盖和逐版功能缺口记在 [Openness 覆盖](../reference/openness-coverage.md)。
不要从 API 签名、工具名称、相邻版本、离线测试或构建结果推断支持情况。

## 4.0 之后

- 维护者决定仓库只保留 C#：4.0 发布后把自有 Python（CI 与发布检查、虚拟机验收与诊断工具、生成器、打包、vci-watch）
  分批改为 C#，生成结果须逐字节一致。插件生态工具调用的第三方 Python 包及其两个桥接脚本保留。
