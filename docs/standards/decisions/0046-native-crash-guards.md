# 0046 已知会使 TIA 崩溃的原生路径保留防护，移植不得丢失

- 状态：已采纳
- 日期：2026-10-08
- 决策者：维护者
- 置信度：中
- 相关：[阶段 8 迁移方案](../../archive/development/phase8-port-plan.md)第 3.1 节 I9 与“维护者决定（2026-10-08，P7-07b follow-up）”；[ADR 0041](0041-unregister-cfc-chart-protection.md)、[0012](0012-old-release-tool-registration.md)；[Openness 限制](../../troubleshooting/openness-limitations.md)；提交 `8159ae3da`

回溯记录（写于 2026-11-25，待补写编号 B13）。迁移方案中写明维护者决定的是 CFC 拒绝；其余防护记录在同一方案的 I9、R8 和任务表中。

## 背景

真机测试发现若干官方 API 路径会使 TIA Portal 退出：V20 读取 OB 的 SiVArc 定义（发现 44）、对没有 CFC 图表的 PLC 做保护预览（发现 45）、V21 的 PLC 交叉引用、Unified 的 `SyntaxCheck` 与脚本改名等。V21 上两次采集在 HMI 诊断控件的一个属性处失败，因果未确认。阶段 8 要把引擎工具移入适配器，移植若丢失防护会使 TIA 退出（R8）。

## 选项

1. 保留防护，接受正常的 CFC 导出和保护也被拒绝，直到有独立安全的图表清单方案和虚拟机证据。
2. 恢复以 `CompleteExport` 证明调用自身安全的预检：被排除，它正是崩溃前的第一处 CFC 操作。

没有记录其他选项。

## 决定

- I9 的隔离规则逐条带入适配器，不得在迁移中丢失。
- V20/V21 的 CFC 保护和三种导出在访问 provider 前拒绝，`skipChartPreflight` 不能绕过；导入不受影响。
- SiVArc 块级工具只接受 FB 和 FC；OB、DB 和未知块类在取得服务前以 `UNSUPPORTED_CAPABILITY` 拒绝。FB/FC 是保守准入，不是官方的安全保证。
- PLC 原生交叉引用在迁移中保持默认禁用与隔离。
- HMI 诊断控件属性 `HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText` 暂缓读取，内容按未知处理。

## 后果

- 正面：已知崩溃路径在原生调用前被拒绝。
- 负面与取舍：部分官方功能在能正常工作的工程上也被拒绝。
- 后续变化：
  - CFC 防护扩大到 V18 及以上（`1c86bd75d`，2026-10-13）；`ManageCfcChartProtection` 八版都不注册（ADR 0041，2026-11-21）。
  - SiVArc 块定义只在 V21 注册，V17–V20 拒绝（`19601271a`、`c34e52986`，2026-11-20）。
  - 原生交叉引用 2026-10-30 起 V14 SP1–V20 默认允许、V21 由人开启（`4b17a0422`），2026-11-20 起在 API 可用的版本默认允许（`4bfd9630b`），因此“默认禁用”一条不再有效。
  - 崩溃探查不再是 4.0 条件，移到 4.1（维护者 2026-11-25，[开发计划](../../plan/plan.md)）。
- 验证方式：`CfcSafetyPolicy`、`OptionalEngineeringCapabilities.SivarcBlockDefinitionProblem`、`HmiReadPolicy.SkipReason` 与 `SivarcGuardTests`；原生行为仍以真机台账为准。
