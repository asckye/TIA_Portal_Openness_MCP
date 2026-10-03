# HMI 与 PLC 变量绑定

本页用于 V20/V21 完整引擎的 WinCC Unified 工具。V14 SP1–V19 基础目录不提供这些 HMI 路径。

## 先确定连接和变量来源

1. 用工程树获取准确 PLC/HMI 软件路径，并读回 PLC 变量或 DB 成员。
2. 依据 [连接指南](connections.md) 创建或检查 HMI 连接。
3. 从 `GetToolUsage(toolName="EnsureUnifiedHmiTag")` 获取当前参数示例。
4. 外部变量在同一次调用中传入准确 `plcTag` 和 `address`，并指定 HMI 变量表、变量名称、类型与连接名。
5. 读回 `Address` / `LogicalAddress`、连接及符号引用，再编译 PLC/HMI。

`plcTag` 是符号引用，例如 `DB_HMI_Interface.CmdEnable`；`address` 是已核实的地址，例如 `%DB200.DBX0.0`。这两种文本用途不同。当前接口直接提供 `address`，无需用通用反射工具另外设置地址。

## 模板地址不等于所有工程的地址

[db_hmi_interface.json](../../../templates/plc/plcbuild-json/db_hmi_interface.json) 给出了示例 DB200 的成员和 `absoluteLayout`。只有使用该布局、DB 号和标准访问方式时，其中地址才适用。修改 DB 结构后重新核对实际偏移与类型；不要沿用旧表。

绝对地址依赖标准访问 DB 的确定布局。若项目采用优化 DB 或要求符号连接，应依据实际 PLC/HMI 的支持范围选择符号方式，读回解析结果；不能给优化 DB 随意填写固定偏移。内部 HMI 变量不需要 PLC 地址，应按工具示例明确作为内部变量处理。

| 现象 | 核对内容 |
|---|---|
| 一组变量均无效 | 通信驱动、PLC 伙伴、接口和连接名 |
| 个别变量无效 | 实际成员、类型、地址及 DB 访问方式 |
| 组态正常但没有实时值 | 运行设备的连接、部署状态及目标变量；工程读回不是运行时读值 |

## 删除 HMI 变量

V20/V21 的 `DeleteHmiTag` 支持 Classic 和 Unified。先读取 `GetToolUsage(toolName="DeleteHmiTag")`，使用准确 HMI 路径、变量表路径和变量名预览，再按示例传入执行及确认参数。

Classic 指定完整变量表路径；Unified 的空变量表路径表示设备级 Tags，分组表使用实际分组路径。执行后检查变量确实不存在。工具不检查所有交叉引用，`referencesChecked=false` 表示画面、脚本或报警引用仍需核查；删除后编译 HMI，再决定保存。

删除变量不等于删除变量表，也不会删除 PLC 变量。若结果表明可能已修改，先读回状态再决定是否重试。
