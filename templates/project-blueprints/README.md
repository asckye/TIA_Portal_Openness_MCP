# 工程蓝图

这些 JSON 是完整引擎的工作流输入示例，不是八版本通用工程文件。

| 文件 | 用途 |
|---|---|
| `full_plc_hmi_project.json` | PLC + WinCC Unified 创建流程参考，包含硬件、PLC、HMI 和读回项。 |
| `scaffold_spec_start_stop.json` | 完整引擎 CLI 的启停工程 spec，先按 CLI 文档预览。 |
| `scaffold_spec_motor.json` | 电机工程 spec，引用随包 PLC 源和 HMI 模板。 |

使用前，通过当前引擎 `GetToolUsage` 核对流程、工具和输出版本；基础宿主没有完整引擎的全部 CLI、HMI 与蓝图入口。原生硬件标识、软件路径及设备参数应与目标版本和实际工程相符，不能直接采用示例名称。

先核对输入依赖及支持的动作，再按实际工具合同预览/执行。HMI 的连接和变量先于画面绑定；编译结果和对象读回分别检查，工程保存需要明确操作。`Validate-Bundle.ps1` 验证包内文件和结构，不证明蓝图已在实际 TIA 工程执行成功。
