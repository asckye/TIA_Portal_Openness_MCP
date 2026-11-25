# 0041 撤下 ManageCfcChartProtection，八个版本都不注册

- 状态：已采纳
- 日期：2026-11-21
- 决策者：维护者
- 置信度：高
- 相关：按 [ADR 0012](0012-old-release-tool-registration.md)、[ADR 0014](0014-tool-counts-follow-official-api.md) 的注册规则；修改[ADR 0046](0046-native-crash-guards.md)（待补写编号 B13）；任务 LOW-A

## 背景

`ManageCfcChartProtection` 读取、添加、修改和删除单个 CFC 图表的保护密码，只在 V18–V21 注册。SDK 没有独立的图表清单接口，V20 在空 PLC 上做 `CompleteExport` 预检后崩溃，V21 对不存在的图表调用 `GetChartProtection` 也崩溃。因此 V18 起每个动作都已在访问提供者前以 `UNSUPPORTED_CAPABILITY` 拒绝。一个没有任何动作能运行的工具不符合“至少一个动作能运行才注册”的规则。

## 选项

1. 保留注册，所有动作继续拒绝：调用方看到一个永远拒绝的工具。
2. 撤下注册，保留 CFC 导入和其他 CFC 工具：工具列表与实际能力一致。

## 决定

采用选项 2。`ManageCfcChartProtection` 在八个版本都不注册；直接调用也被拒绝。V18、V19、V20、V21 的工具数分别为 402、478、499、518，八版并集为 523。CFC 导出与导入的既有规则不变。

## 后果

- 正面：工具数量按官方 API 中能安全运行的动作计算。
- 负面与取舍：对已发布 rc.1 的客户端是删除工具的不兼容改动，写入 CHANGELOG 和 rc.2 说明。
- 后续工作：Engine harness 中直接调用该工具的 CFC 用例随 harness 修复移到 4.1，删除用例需维护者批准。
- 验证方式：目录数量测试和八版契约基线；批准记录见 `build-tools/release/baselines/approved-check-changes.json`（LOW-A）。
