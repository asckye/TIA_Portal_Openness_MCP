# LAD、FBD 与混合语言程序

优先通过 `GetToolUsage(language="lad")`、`language="fbd"` 或 `language="mixed"` 获取完整例子。返回的 `releaseKeys` 决定例子适用范围；通用语言名称不表示八个版本都支持相同导入格式。

## 选择输入格式

| 场景 | 路径 |
|---|---|
| 导入同版本 TIA 导出的程序块 | 使用该引擎的 `ImportBlock` 示例导入 SimaticML XML |
| 编写通用触点、线圈、沿和混合网络 | V21 的 SIMATIC SD 文档例子；按 `ImportFromDocuments` 示例导入 |
| 用 XML 组装一个或多个 FC 调用网络 | `BuildFlgNetCallXml` / `ComposePlcLadFcBlockXml`；输出为 V21 候选 XML |

LAD XML builder 只生成 FC 调用网络，不生成任意触点、线圈、比较或算术网络。V20 的 SIMATIC SD 使用还取决于对应安装/更新；本包这些完整文档示例限定 V21。V14 SP1–V19 基础目录没有完整引擎的文档导入入口。

## V21 文档练习

1. 读取 `GetToolUsage(exampleId="lad-contacts-edge")` 或 `exampleId="fbd-and"`。中文标题配套资源见 `exampleId="lad-chinese-resources"`。
2. 将返回的完整文件保存在 TIA/MCP 电脑的同一目录；[FB_LadStart.s7dcl](../../../reference/tool-examples/languages/FB_LadStart.s7dcl) 与 [FB_LadStart.s7res](../../../reference/tool-examples/languages/FB_LadStart.s7res) 使用相同基本文件名，保留示例编码。
3. 读取 `GetToolUsage(exampleId="sequence/plc-s7dcl-import")`。文档导入参数使用**目录和不带扩展名的文件名**，不是把 `.s7dcl` 完整路径放进任意名称参数。
4. 检查导入状态、消息和实际导入的块，再编译、读回接口及网络。

沿检测的历史状态应持久保存；定时器需要对应实例。每个网络使用自己的语言声明。例子中的修正说明解释这些细节，本页不再维护另一份指令/引脚注册表。

混合网络见 `GetToolUsage(exampleId="mixed-lad-scl")`。更多工具参数和官方来源都从同一入口获取。
