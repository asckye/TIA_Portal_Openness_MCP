# PLC 模板库

本目录提供 TIA Portal 项目生成所需的通用 PLC 模板，覆盖变量表、UDT、全局 DB、SCL FC/FB、LAD 调用配方和外部 SCL 源示例。模板不包含现场项目程序段。

先使用当前服务 `GetToolUsage` 读取工具与语言示例。`BuildAndImportPlcArtifact` 属于完整引擎入口；基础宿主使用自身公开工具和合同。两个声明构建器的 `outputReleaseKey` 支持八版 UDT / Global DB，其他模板和生成器不能因此视为八版通用。

## 目录

| 路径 | 用途 |
|---|---|
| `instruction-recipes/basic_plc_instruction_recipes.json` | 指令和语法配方 |
| `plcbuild-json/*.json` | `BuildAndImportPlcArtifact` 入参模板（仅 tagtable/udt/globaldb） |
| `lad-recipes/lad_call_recipes.json` | `BuildFlgNetCall` LAD 调用网络配方 |
| `scl-examples/*.scl` | 外部 SCL 源：FC/FB 功能块 + 指令示例 |

## PLC Build 模板（DSL：tagtable / udt / globaldb）

| 文件 | 类型 | 内容 |
|---|---|---|
| `tagtable_basic_signals.json` | tagtable | 命令、状态、过程值基础变量 |
| `udt_basic_status.json` | udt | 状态、数值、计数基础结构 |
| `db_basic_status.json` | globaldb | 基础状态 DB |
| `db_hmi_interface.json` | globaldb | HMI 命令、状态、参数、显示值接口 DB |

## FC/FB 功能块（外部 SCL，不走 DSL）

含算术/比较/函数/CASE 的 FC/FB 超出 `BuildAndImportPlcArtifact` 单变量 DSL 能力，统一用原生
`.scl` 经 `ImportPlcExternalSource` + `GenerateBlocksFromExternalSource` 导入。

| 文件 | 类型 | 内容 |
|---|---|---|
| `scl-examples/FC_BasicScaleLimit.scl` | fc | 线性缩放和 LIMIT 限幅 |
| `scl-examples/FC_MathCompareDemo.scl` | fc | ABS、比较、误差、限幅输出 |
| `scl-examples/FB_BasicLatch.scl` | fb | 布尔保持和复位 |
| `scl-examples/FB_TimerCounterDemo.scl` | fb | TON 定时、上升沿计数、复位 |
| `scl-examples/FB_StepSequenceDemo.scl` | fb | CASE 步序状态机 |
| `scl-examples/FC_InstructionGallery.scl` | fc | SCL 指令参考示例 |
| `scl-examples/FB_SelfTest_Template.scl` | fb | DB 驱动的最小自测骨架：Start 触发、用例表预期/实际、通过/失败计数 |

> 旧 `plcbuild-json/fc_*.json`、`fb_*.json` 已移除；FC/FB 一律使用 `scl-examples/` 下的 `.scl` 外部源导入，不要用 DSL 表达式写法。

## 调用顺序

按统一示例库选取当前版本的流程，读取工程及 PLC 后，再核对目标对象和依赖。需要预览的操作先查看预览结果；外部源导入后另行生成块。之后调用当前版本的编译工具，检查错误、警告及嵌套诊断，读回实际对象，最后根据需要单独保存。

依赖顺序由实际引用决定，可用 `PlanArtifactImportOrder` 辅助；不要把 TagTable → UDT → DB → FC/FB 的简表当作任意程序都成立的顺序。

## 注意

- 所有 `softwarePath`、分组路径和设备路径必须来自 TIA 读回。
- `BuildAndImportPlcArtifact` 的 `json` 参数是字符串，调用前需要把模板中的 `json` 字段序列化。
- 外部源编码按 `GetToolUsage(language="scl")` 的对应示例处理；基础宿主当前仅验过根目录 ASCII 源。不要为修复编码问题改用不支持该程序逻辑的 DSL。
- 复杂 LAD/FBD 网络应使用 TIA 导出的已验证 XML 或 `BuildFlgNetCall` 生成调用网络。
