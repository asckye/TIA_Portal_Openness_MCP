# LAD、FBD 与混合语言程序

优先通过 `GetToolUsage(language="lad")`、`language="fbd"` 或 `language="mixed"` 获取完整例子。返回的 `releaseKeys` 决定例子适用范围；通用语言名称不表示八个版本都支持相同导入格式。

## 选择输入格式

| 场景 | 路径 |
|---|---|
| 导入同版本 TIA 导出的程序块 | 使用该版本服务的 `ImportPlcBlock` 示例导入 SimaticML XML |
| 编写通用触点、线圈、沿和混合网络 | V21 的 SIMATIC SD 文档例子；按 `ImportPlcBlockDocuments` 示例导入 |
| 用 JSON 组装触点、沿、比较、OR、分支、线圈、MOVE、定时器、计数器或用户 FC/FB 调用 | `BuildPlcLadFcBlock` 的 `rung` 网络；V21 候选 XML |
| 用 XML 组装 FC 调用网络 | `BuildFlgNetCall` 生成片段；`BuildPlcLadFcBlock` 的 `call` 网络生成完整 V21 候选块 |

`BuildPlcLadFcBlock` 的每个网络须选 `call` 或 `rung` 之一。`rung` 支持常开/常闭、使用全局存储位的上升/下降沿、六种比较、OR 组、共用前段分支、普通/置位/复位线圈、MOVE 及其 ENO 串接。操作数支持全局常量、数组下标组件路径和本块声明的输入/输出局部变量；不解析符号或推断类型，编译结果才是依据。每个 rung 网络最多 256 个 Access/Part/Call，现有 64 网络和深度 16 限制不变。

`rung` 还支持 TON/TOF/TP、CTU/CTD/CTUD，以及带命名引脚的用户 FC/FB 调用。定时器和计数器的 `instance` 使用全局符号路径；FB 调用的 `instance` 是全局实例 DB 名称，FC 调用不接受实例。CTU 必须给出 `reset`，CTD 必须给出 `load`，CTUD 必须给出 `down`、`reset`、`load`；辅助条件串从电源轨开始。`q`/`qd` 接输出列表，`et`/`cv` 接符号，未使用的输出接 `OpenCon`。只有 MOVE 和 CALL 接受由 ENO 驱动的 `then`。调用引脚名不区分大小写去重，不得使用 `en`/`eno`；Output 引脚必须接符号。先准备并编译实例与被调用块，生成器不解析这些对象或推断类型。

通过 `GetToolUsage(toolName="BuildPlcLadFcBlock", operation="rung")` 获取基础输入，`operation="rung-boxes"` 获取定时器、计数器和 FB 调用输入；导入与读回步骤见 `GetToolUsage(exampleId="sequence/plc-lad-rung-build-import")`。输出仅限 V21，原生导入与编译验收为 **NOT RUN**。任意算术、库或系统指令使用完整文档/同版本导出块。V20 的 SIMATIC SD 使用还取决于对应安装/更新；本包这些完整文档示例限定 V21。V14 SP1–V19 基础目录没有 V20/V21 的文档导入入口。

## V21 文档练习

1. 读取 `GetToolUsage(exampleId="lad-contacts-edge")` 或 `exampleId="fbd-and"`。中文标题配套资源见 `exampleId="lad-chinese-resources"`。
2. 将返回的完整文件保存在 TIA/MCP 电脑的同一目录；[FB_LadStart.s7dcl](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/tool-examples/languages/FB_LadStart.s7dcl) 与 [FB_LadStart.s7res](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/tool-examples/languages/FB_LadStart.s7res) 使用相同基本文件名，保留示例编码。
3. 读取 `GetToolUsage(exampleId="sequence/plc-s7dcl-import")`。文档导入参数使用**目录和不带扩展名的文件名**，不是把 `.s7dcl` 完整路径放进任意名称参数。
4. 检查导入状态、消息和实际导入的块，再编译、读回接口及网络。

沿检测的历史状态应持久保存；定时器需要对应实例。每个网络使用自己的语言声明。例子中的修正说明解释这些细节，本页不再维护另一份指令/引脚注册表。

混合网络见 `GetToolUsage(exampleId="mixed-lad-scl")`。更多工具参数和官方来源都从同一入口获取。
