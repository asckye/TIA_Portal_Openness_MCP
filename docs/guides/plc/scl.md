# 编写、导入和编译 SCL

V14 SP1–V21 均提供外部源导入、生成块和编译工具，但基础引擎与完整引擎的参数和执行流程不同。先让 AI 读取当前服务的 `GetToolUsage`，再调用对应工具。

## 从完整示例开始

| 目标 | `GetToolUsage` 查询 | 文件 |
|---|---|---|
| 第一个加法 FC | `exampleId="scl-add"` | [FC_Add.scl](../../../reference/tool-examples/languages/FC_Add.scl) |
| FB 状态和定时器 | `exampleId="scl-state-timer"` | [FB_DelayPulse.scl](../../../reference/tool-examples/languages/FB_DelayPulse.scl) |
| 数组与控制流 | `exampleId="scl-control-flow"` | [FC_ArrayTotal.scl](../../../reference/tool-examples/languages/FC_ArrayTotal.scl) |
| 查找其他程序模板 | `language="scl"` | [语言目录](../../../reference/tool-examples/languages/catalog.json) |

查询结果包括完整声明、实现、文件编码、适用版本及常见错误。按返回的 `releaseKeys` 选择例子；不要只复制函数体而漏掉接口声明、实例或返回值。

## 按所选引擎完成导入

| 引擎 | 调用序列 |
|---|---|
| V14 SP1–V19 基础引擎 | `GetToolUsage(exampleId="sequence/plc-scl-block-foundation")` |
| V20/V21 完整引擎 | `GetToolUsage(exampleId="sequence/plc-scl-block")` |

1. 读取实际工程和 PLC 路径。
2. 把完整源文件放在运行 TIA/MCP 的电脑上。虚拟机服务读取的是虚拟机文件系统，不是 AI 宿主机的同名路径。
3. 按当前示例导入外部源，检查实际返回的源名称。
4. 将该名称交给生成工具，核对实际生成的块及接口。导入源文件本身不等于生成块。
5. 在工程设备满足离线编译前提后编译，检查错误数、警告数和嵌套诊断，再读回目标块。
6. 确认结果后保存项目。以上步骤不自动下载到 PLC。

基础引擎当前执行路径接受 ASCII 源文件；中文等非 ASCII 源编码仍待原生验证。完整引擎的编码以具体文件示例为准，不能将 `.scl` 与 `.s7dcl` 的编码要求混为一谈。

V14 SP1 的生成 API 没有返回对象列表，因此需结合前后块清单和读回结果判断。其他版本返回生成对象也不能代替编译诊断。

## 修改示例时关注什么

- 类型和返回值：局部变量按声明使用，数值转换与目标类型一致。
- 跨周期状态：定时器、沿检测和累计状态需要持久实例，完整 FB 示例展示了声明位置。
- 依赖：先导入被引用的 UDT、DB、FC/FB，再导入调用方。
- 工程语义：编译通过只证明编译器接受程序；周期时间、范围和设备行为还需按实际工程验证。

`BuildStructuredTextXml` 的小型 JSON 操作集合不是完整 SCL 编译器。复杂表达式和控制流直接使用外部源；Builder 的版本范围见 [PLC XML builders](builders.md)。
