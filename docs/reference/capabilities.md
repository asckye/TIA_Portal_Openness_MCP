# 工程能力与验收边界

v3.2.0 提供八个 TIA 版本的 MCP 运行时，以及直接调用 Openness 的 Studio。第一次使用请从[新手指南](../getting-started/beginners.zh-CN.md)开始；完整工具与版本对应关系以[版本矩阵](version-tools.md)、[生成的工具目录](version-tool-catalog.md)为准。

## 当前支持范围

| 入口 | 范围 |
|---|---|
| V14 SP1、V15.1、V16、V17、V18、V19 MCP | PLC 基础宿主与各版原生工作进程；覆盖工程连接、PLC 浏览、选定导入导出、外部源生成和编译等已实现子集。并非 V20/V21 完整工具集的直接移植。 |
| V20 / V21 MCP | 完整引擎覆盖 PLC、经典 HMI、Unified、硬件网络、库、在线通道和选件。默认 lite 通过 FindTools / CallTool 访问其余工具。 |
| Studio | 八个独立版本适配器，共用 PLC 浏览、导入导出、编译工作流；界面支持中文和英文。Studio 不经过 MCP。V14 SP1 / V15.1 无 VCI。 |
| 配置器 | 选择精确 TIA 版本、同机或虚拟机连接、客户端配置、服务诊断及更新入口。选择版本不会转换工程。 |

只支持精确版本键 `14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20`、`21`，不包含原始 V14 / V15。同名工具在基础宿主与完整引擎上可能有不同参数和结果结构，应读取实际连接版本的 schema。

## AI 如何取得正确示例

所有版本统一使用 `GetToolUsage` 获取实际参数、输入来源、调用示例、结果解释和官方出处。语言代码、文件模板及多步顺序也从同一库读取；不再单独维护一套自然语言配方。完整引擎的兼容入口 `GetRecipe`、`GetAuthoringGuide` 读取同源记录。

- 按工具查：提供 `toolName`，具有多个动作时再提供 `operation`。
- 按语言或完整流程查：使用 `language` 或 `exampleId`。
- 文件应放在 MCP 服务所在电脑；虚拟机场景中，宿主机路径不会自动映射为虚拟机路径。
- 先明确目标工程、PLC 和对象，再使用示例中的参数。导入、生成、编译、保存、下载分别对应不同操作。

[统一示例说明](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md)区分官方源代码、项目封装示例、离线验证及原生验收。示例帮助正确调用，不保证所有设备、补丁和选件都已通过真实工程测试。

## 主要工作流

| 工作流 | 当前实现与边界 |
|---|---|
| PLC 源文件 | 八版均有外部源导入和块生成路线。基础宿主目前限定根目录 ASCII SCL/AWL/DB/UDT；中文源编码仍未验收。V14 SP1 的生成 API 无返回对象，需要比较前后清单。 |
| PLC XML 与文档 | 两个声明构建器支持八版 UDT / Global DB 格式；其他构建器有各自版本限制。XML 片段 XSD 通过不等于完整文档可导入或程序正确。SIMATIC SD 导入使用目录和不带扩展名的文档名。 |
| 编译 | 返回错误数量、警告和嵌套诊断；API 调用返回不等于编译成功。基础宿主实际编译路线已接入，新增原生验收仍未运行。 |
| PLC 编辑与分析 | 完整引擎支持导出检查、有限的文本/标量初始值补丁、验证导入、离线引用、差异及质量审计；不是任意 LAD/FBD/SCL 编辑器。 |
| HMI / Unified | 完整引擎提供普通画面、控件、标签、报警、事件、全局脚本、列表及库等接口。事件代码与全局脚本模块使用不同 API；面板类型内部创作仍有未实现边界。 |
| 硬件、库与选件 | 以实际版本 SDK、安装选件、许可证和对象提供的服务为准。工具名称存在不表示所有动作都能在所有版本执行。 |
| 运行时数据 | 完整引擎另有 S7 Web API、Unified Open Pipe、PLCSIM Advanced 等通道；这些不属于 Openness 工程对象读取。 |
| 第三方复用 | 已接入的源码、伴随进程及其用途见[生态集成](ecosystem-tools.md)。外部候选不计作已实现工具。 |

## 如何判断验证到了哪一步

| 证据 | 能证明什么 |
|---|---|
| SDK 形状检查 / 编译 | 所检查的目标版本签名存在、适配器可编译。 |
| 离线功能与 MCP 协议测试 | 被测输入、解析、结果和进程协议满足断言。 |
| Studio mock / WPF 检查 | 模拟工作流及界面行为通过对应检查。 |
| 实际 TIA 工程记录 | 仅证明记录中的版本、工程、动作和输入曾得到该结果。 |
| 工具/API 覆盖统计 | 描述清单或审计范围；不是所有 Siemens API 已封装、全部动作可执行或全部工具真机通过的承诺。 |

本版记录以 [release-build.json](../../manifest/release-build.json) 和 [multi-version-build.json](../../manifest/multi-version-build.json) 为准。新增八版工作流的原生工程验收为 **NOT RUN**；旧版本实测不能自动继承为本版验收。实际历史与未解决问题见[真机证据索引](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/reference/real-machine-ledger.md)和[已知限制](../troubleshooting/openness-limitations.md)。

## 仍未完成

基础宿主尚未迁移完整引擎的大部分 HMI、库、硬件网络、在线和选件工具；逐族候选与确定的版本差异见[版本矩阵](version-tools.md#remaining-tools-and-acceptance)。同一 API 在 SDK 中出现，不代表该功能已有 MCP 执行入口。

Unified 脚本库类型改名仍有实测崩溃；`p2051[0]` BICO 读取修正尚未原生复测；PLC 原生交叉引用默认关闭。这些限制统一记录在[已知限制](../troubleshooting/openness-limitations.md)，不另建调用规范系统。

历版能力叙述已移出本页。[原能力文档快照](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/reference/capabilities.md)仅供追溯，不能代替当前矩阵。
