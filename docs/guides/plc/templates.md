# PLC 模板选择与使用

[templates/plc](../../../templates/plc/README.md) 提供可修改的 PLC 数据结构、SCL 程序和调用网络。完整代码与调用示例集中在 `GetToolUsage` 和 [reference/tool-examples](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/tool-examples/languages/catalog.json)，避免在多个指南复制维护。

| 内容 | 入口 | 使用方式 |
|---|---|---|
| 加法、状态、定时、数组 | `GetToolUsage(language="scl")` | 读取完整源、声明及版本要求，再走外部源导入 |
| 通用启停、步序、缩放等模板 | `scl-library-*` 示例 ID | 从语言目录选择当前引擎实际返回的例子 |
| UDT / Global DB JSON | `BuildPlcUdtXml` / `BuildPlcGlobalDbXml` | 显式选择 `outputReleaseKey`，返回 XML 后另行导入 |
| PLC 变量表和 FC-call LAD 网络 | 相应 builder 的 `GetToolUsage` | 当前输出仍是 V21 候选格式 |
| 完整项目蓝图 | [项目生成](../project-generation.md) | PLC + Unified 工程流程使用 V20/V21 完整工具；模板格式需逐项核对 |

`plcbuild-json/*.json` 包装文件包含 `kind`、`tool` 和 `json`。传入 builder 或 `PlcBuildAndImport` 的字符串来自内部 `json` 对象，不是整个包装文件。

导入顺序按依赖安排：先数据类型和接口数据，再被调用的块，最后调用方。`PlanArtifactImportOrder` 可根据已有文件生成顺序计划；计划结果不表示已经导入。包含算术、比较、循环或状态机的 FC/FB 直接使用完整 `.scl` 源。

模板中的名称、DB 号、地址和扫描条件是示例。替换为实际项目的接口，并读回确认生成对象、编译诊断和引用关系。`DB_HMI_Interface` 的固定地址布局仅适用于该模板，不能用于任意 DB；参见 [HMI 变量绑定](../hmi/tag-binding.md)。

需要逐步操作时看 [SCL](scl.md)、[LAD/FBD](lad.md) 和 [Builder](builders.md)。
