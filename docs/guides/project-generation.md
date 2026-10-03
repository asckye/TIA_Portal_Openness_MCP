# 从蓝图生成 PLC 与 Unified 工程

本流程使用 V20/V21 完整引擎的项目、硬件、PLC 和 HMI 工具。V14 SP1–V19 基础目录不提供整套 HMI 生成能力；旧版 PLC 开发使用[SCL 流程](plc/scl.md)及该版本工具示例。

## 准备输入

- [完整项目蓝图](../../templates/project-blueprints/full_plc_hmi_project.json)：按实际 CPU、HMI、程序和地址修改。
- [PLC 模板](../../templates/plc/README.md)与[HMI 模板](../../templates/hmi/README.md)：可修改的输入文件。
- `GetToolUsage`：当前版本的参数、调用序列、完整程序例子及结果解释。

模板能被解析不等于已适配所有 TIA 版本。UDT/Global DB builder 可显式输出八版格式；其余六种 PLC builder 仍输出 V21 候选 XML。使用 V20 时核对每项输入格式与实际原生支持，参见 [Builder](plc/builders.md)。

## 分阶段操作

| 阶段 | 做什么 | 检查什么 |
|---|---|---|
| 连接 | 按 `sequence/connect-project` 获取或打开实际目标 | 工程路径、TIA 版本和绑定状态 |
| 硬件 | 按 `sequence/hardware-device` 创建明确型号的设备 | 实际设备和软件路径、网络读回 |
| PLC 数据 | 按依赖导入类型、接口 DB 和变量表 | 实际类型、块、成员及地址 |
| PLC 程序 | 按 `sequence/plc-scl-block` 导入完整源并生成 | 实际生成块、接口及 PLC 编译诊断 |
| HMI | 按 `sequence/hmi-unified-screen` 创建连接、变量、画面及控件 | 连接驱动、变量来源、控件和事件正文 |
| 保存 | 确认目标工程后调用保存工具 | 实际保存结果 |

以上 `sequence/...` 均通过 `GetToolUsage(exampleId="...")` 获取。每步使用上一步返回的实际路径，不直接复制示例设备名。

使用 `PlcBuildAndImport` 时，先检查预览生成的文件、分类和目标，再按其执行示例导入；导入结果与编译结果分别检查。外部 SCL 导入、生成块和编译同样是三个阶段。

HMI 外部变量的符号引用和实际地址在 `EnsureUnifiedHmiTag` 的同一次调用中传入。固定 DB200 地址仅适用于对应模板布局。详见[变量绑定](hmi/tag-binding.md)、[连接](hmi/connections.md)和[画面生成](hmi/design.md)。

## 完成标准

工程树中能找到预期设备、PLC/HMI 软件和程序；PLC/HMI 编译结果没有未解决的错误；变量和事件能读回；保存结果明确。编译、导入和保存均不证明已下载或运行，目标设备验收另行进行。

若使用命令行生成 JSON/YAML spec，参见 [CLI 指南](../getting-started/cli.md)。首次练习建议先完成[新手指南](../getting-started/beginners.zh-CN.md)的单个函数导入，再扩展到整工程。
