# Safety（F 程序）工具

本页针对 V20/V21 完整引擎。工具可用性还取决于 F-CPU、Safety 产品和具体版本；V14 SP1–V19 的基础目录不提供这一工具族。

## 先读取目标程序

1. 从工程树取得实际 PLC 软件路径。
2. 通过 `GetToolUsage(toolName="ManagePlcSafety", operation="read")` 获取本版本示例。
3. 检查实际 F 能力、登录状态、设置、运行组及支持的签名类型。
4. 需要逐块签名时读取 `ReadSafetyBlockSignatures` 的示例；需要安全打印件时读取 `ExportSafetyPrintout` 的示例。

| 返回内容 | 用途 |
|---|---|
| `administration` | 密码设置与当前登录状态 |
| `settings` | F 程序设置、号段、当前和可选安全系统版本 |
| `runtimeGroups` | 主安全块、实例 DB、前后处理及周期设置 |
| `programSignatures` | 当前 API 提供的程序签名；V20/V21 字段能力不同 |
| `cpu` | 实际 CPU F 能力和可用服务 |

签名是离线工程数据。无有效签名或未编译状态不能当成验收通过；普通 `CompileSoftware` 的成功也不等于完成 F 编译和功能安全验收。

## 修改设置或运行组

用 `GetToolUsage(toolName="ManagePlcSafety")` 查看当前版本动作，再对选定动作读取 `operation` 示例。按示例完成实际项目、离线状态和登录准备，先检查预览，再执行所需修改。检查实际设置、运行组或签名的读回结果，随后完成 TIA 要求的编译和项目验证。

`ManageSafetyGlobalSettings` 是 TIA Portal 级设置，影响范围不同于单个 PLC。CPU 的 `Failsafe_FCapabilityActivated` 也不同于普通运行组设置，关闭 F 能力可能删除安全程序，不能作为一般故障处理步骤。

## 版本差异

V21 增加若干安全签名、BaseID 和安全激活测试路径；V20 不能套用这些动作。工具级和动作级差异以当前 `GetToolUsage` 及[版本清单](../../reference/version-tools.md)为准，不把某个程序集的类型数当成全部功能已验收。

打印件工具检查原生结果及输出文件；PDF/XPS 依赖 TIA 电脑上的相应打印支持。输出路径属于服务端电脑。真实 Safety 操作和验收状态见[能力说明](../../reference/capabilities.md)。
