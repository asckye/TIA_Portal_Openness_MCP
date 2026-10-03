# WinCC Unified 画面生成

适用于 V20/V21 完整引擎。这里说明工具操作与数据格式；画面的布局、配色和交互按用户需求确定。Classic HMI 使用独立工具和格式，不能复用 Unified 接口。

## 操作顺序

读取 `GetToolUsage(exampleId="sequence/hmi-unified-screen")`，按返回的适用版本、参数及实际工程路径执行：连接与变量准备 → 创建画面和控件 → 绑定动态化或事件 → 读回 → 编译和保存。

画面尺寸应与目标设备匹配。连接和变量的建立见[连接](connections.md)及[变量绑定](tag-binding.md)。

## 选择工具

| 需求 | 工具 |
|---|---|
| 创建画面 | `EnsureUnifiedHmiScreen` |
| 使用简单 JSON 批量创建基础对象 | `ApplyUnifiedHmiScreenDesignJson` |
| 查询其他对象类型和属性 | `DescribeUnifiedScreenItemType` |
| 创建、读取或修改具体控件 | `ManageUnifiedScreenItem` |
| 集合或部件 | `ManageUnifiedObjectParts` |
| 事件 / 动态化 | `ManageUnifiedEvent` / `ManageUnifiedDynamization` |

先读取所选工具的 `GetToolUsage`，再使用其准确字段和枚举。`designJson` 的顶层是 `screen`、`items`，基础控件包括 `Rectangle`、`Text`、`Button`、`IOField`。文字标签使用 `Text`；`Rectangle` 是图形对象，不承担文字标签。

通用对象接口的多语言文字按 culture 组织，嵌套部件使用对象结构；这些格式不等同于简单 `designJson`。属性 schema 返回可写性和可用枚举，避免套用另一对象的属性。

可修改模板见 [templates/hmi](../../../templates/hmi/README.md)，主题和网格工具见[主题与布局](unified-theme-layout.md)。文件名中的分辨率只是模板尺寸，应用前调整到实际目标。

完成后核对控件数量、名称、尺寸、变量引用及事件正文。按钮事件是否经过原生语法检查要看返回的检查状态；脚本写入成功不能代替运行验证，参见[事件动作](unified-actions.md)。
