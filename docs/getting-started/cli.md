# 命令行与 AI spec

本页适用于 V20/V21 完整引擎的 CLI。V14 SP1–V19 基础引擎不提供同一套 `gen` / `patch` 命令。首次使用可先完成[新手指南](beginners.zh-CN.md)；MCP 客户端配置使用[配置器](configuration.md)。

## 选择匹配的引擎

从完整交付包根目录打开 PowerShell。下列命令以 V21 为例；V20 使用 `runtime\v20\TiaMcp.Engine.V20.exe` 并核对 spec 中各输入格式是否支持 V20。非默认安装位置使用该引擎支持的 `--tia-portal-location` 和匹配的 `--tia-major-version`。

```powershell
.\runtime\v21\TiaMcp.Engine.V21.exe doctor
.\runtime\v21\TiaMcp.Engine.V21.exe schema
.\runtime\v21\TiaMcp.Engine.V21.exe gen .\spec.json --dry-run
.\runtime\v21\TiaMcp.Engine.V21.exe gen .\spec.json --json
```

`schema` 输出当前 spec 字段；`--dry-run` 离线校验输入，不创建工程。实际生成后查看每个步骤结果、实际工程位置及编译诊断，不能只根据进程有输出判断成功。

## 常用命令

| 命令 | 用途 |
|---|---|
| `gen <spec>` | 创建 spec 描述的工程 |
| `patch <spec>` | 按 spec 的 `projectPath` 增量修改已有工程 |
| `describe <工程路径> --plc <PLC名>` | 查看指定 PLC |
| `compile <工程路径> --plc <PLC名>` | 编译并查看诊断 |
| `prewarm` | 保持一个可复用的无界面 TIA 实例 |
| `doctor` | 检查本机安装、API 和用户环境 |
| `schema` | 查看 spec 字段和格式 |

`gen`、`patch`、`describe`、`compile`、`export`、`import` 在标准输出只打印一份 V4 结果 JSON（与 MCP 工具结果相同的信封），诊断信息只写到标准错误。退出码：`0` 成功；`2` 操作前被拒绝（未执行）；`3` 失败或读取失败；`4` 部分完成；`5` 结果未知（需重置会话后核对工程状态）；`64` 命令或参数写法错误；`70` 无法建立工具运行环境。`4`、`5` 不能当作成功处理。工程、源文件和输出目录均指运行 CLI 的电脑；相对路径以当前工作目录解析。需要 TIA 窗口时按命令支持使用 `--with-ui`。

## 让 AI 生成 spec

先复制 `schema` 输出，再向 AI 说明工程目标：

```text
请根据下面当前引擎的 schema，为我输出合法的 spec.json。
目标 TIA 版本：[V21]；CPU/HMI 型号和程序要求：[填写实际需求]。
只使用 schema 中存在的字段；列出需要单独保存的源文件及其服务端路径。
完整程序从当前 GetToolUsage 的语言示例和调用示例改写，核对适用版本与编码。
不要把示例 DB 号、地址或面板分辨率当成我的工程参数。
[粘贴 schema 输出]
```

把 spec 和它引用的所有文件保存到实际位置，先离线校验，再执行。若某一步失败，将该步诊断、输入和实际工程状态交给 AI 修正。

现有蓝图见 [templates/project-blueprints](../../templates/project-blueprints)。文件中的 `__BUNDLE__` 会解析为交付包根目录。模板不是任意版本的通用工程：UDT/DB builder 有显式八版输出，其余 PLC builder 仍为 V21 候选格式，见 [Builder 说明](../guides/plc/builders.md)。

`scripts\operations\生成工程.bat` 支持拖入一个 spec 文件，默认选择包内 V21 引擎；V20 用户直接调用匹配 EXE，避免用文件是否存在来决定目标版本。

关闭前台 `prewarm` 可按 Ctrl+C。`prewarm --stop` 关闭其 TIA 实例，预热进程本身仍需结束。完整工程流程见[项目生成](../guides/project-generation.md)。
