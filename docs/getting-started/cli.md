# 命令行与 AI spec

本页适用于 V20/V21 完整引擎的 CLI。V14 SP1–V19 基础引擎不提供同一套 `gen` / `patch` 命令。首次使用可先完成[新手指南](beginners.zh-CN.md)；MCP 客户端配置使用[配置器](configuration.md)。

包资源使用 `--bundle-root` / `TIA_MCP_BUNDLE_ROOT`。私人报告和夹具命令另需
`--workspace-root <existing-absolute-directory>`；工作区不从 cwd 或包根推断。
HMI 模板命令必须同时给出 `--hmi-template-directory <absolute-directory>`，组件目录分析还需
`--global-library-probe-json-path <absolute-file>`。缺失输入返回 `INVALID_ARGUMENT`、退出 64。
已知 `TMP_EXPORT`/报告布局只在明确选择的工作区内解释。

`tia doctor` 显示实际日志与诊断目录。宿主启动及运行消息共用 `data/logs/<releaseKey>/TiaMcpServer-<processKey>.log`，
Workbench 崩溃日志位于 `data/logs/studio`；进程键含 PID、启动 UTC、GUID。只读安装的普通日志回退到 `%TEMP%\TiaMcp\logs`。
每次宿主/Workbench 启动按用途保留最新 32 份，跳过锁定/活动进程文件及非产品文件；写入失败每用途每进程报告一次 `IO_FAILED`。
审计写入 `data/logs/audit` 的共享单链，只读安装回退到 `%LOCALAPPDATA%\TiaMcp\logs\audit`，失败报 `DIAGNOSTIC_WRITE_FAILED`。
`tia audit verify` 分别校验主目录和已存在的回退链，报告链目录、断点文件及序号；显式数据根不回退。
Python 默认解释器为 `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`；自选解释器用 `TIA_MCP_PLC_TOOLS_PYTHON`，
安装步骤见[生态工具](../reference/ecosystem-tools.md)。

MCP `WRITE` / `ONLINE-WRITE` 实际执行默认须由 Workbench 审批后才派发；`dryRun=true` 和候选工具的 `mode=preview` 不等待审批，也不作为写请求写入审计。`SaveProject`、`SaveProjectCopy`（save-as）和 `CloseProject` 实际执行即使在目录中分类为 `SESSION` 也须审批；connect、attach、open 和 disconnect 仍不审批。拒绝、120 秒默认超时或 Workbench 不可用会在操作开始前返回 `CONFIRMATION_REQUIRED`；MCP 客户端不能自批。每次决定和结果追加到 `data/logs/audit`，用 `tia audit verify` 或 Workbench 审计页检查保留链记录；同一次写调用的 request、审批决定、start/end 与响应使用相同 requestId。校验不能证明整份日志没有被删除。`data/diagnostics` 保存原生调用诊断包。工具输出使用 V4 `ok` / `data` / `error` / `meta` 信封；`OUTCOME_UNKNOWN` 要先核对工程，不能自动重放写调用。

## 选择匹配的引擎

从完整交付包根目录打开 PowerShell。下列命令以 V21 为例；V20 使用 `runtime\v20\TiaMcp.Engine.V20.exe` 并核对 spec 中各输入格式是否支持 V20。非默认安装位置使用该引擎支持的 `--tia-portal-location` 和匹配的 `--tia-major-version`。

```powershell
.\runtime\v21\TiaMcp.Engine.V21.exe doctor
.\runtime\v21\TiaMcp.Engine.V21.exe schema
.\runtime\v21\TiaMcp.Engine.V21.exe gen .\spec.json --dry-run
.\runtime\v21\TiaMcp.Engine.V21.exe gen .\spec.json --json
runtime\v21\TiaMcp.Engine.V21.exe install-plc-tools
```

直接使用 V21 CLI 的生成和预热命令：

```text
runtime\v21\TiaMcp.Engine.V21.exe gen <spec>
runtime\v21\TiaMcp.Engine.V21.exe prewarm
runtime\v20\TiaMcp.Engine.V20.exe gen <spec>
runtime\v20\TiaMcp.Engine.V20.exe prewarm
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

### V20/V21 MCP 服务的隔离开关

隔离开关只用于 MCP 服务启动，不改变 `gen`、`patch`、`describe`、`compile` 等显式 CLI 子命令。4.0 发布版默认关闭；启动 MCP 服务时用 `--isolate-openness` 强制开启，用 `--no-isolate-openness` 强制关闭。Workbench 生成的客户端配置不会预先写入任一开关，只有用户主动修改启动命令时才会选择。

V20/V21 服务进程在 TIA 缺失、版本不匹配或 Openness 用户组尚未就绪时仍提供诊断。调用需要 TIA 的工具会返回 `RESOURCE_UNAVAILABLE`，资源为 `tia-openness-environment`，并以 `rejected-before-operation` / `not-started` 表示没有派发原生操作。先检查 `InitializeEnvironment` 和 `GetEnvironmentDiagnostics` 的原因及修复步骤。隔离状态与环境就绪状态可通过 `GetOpennessWorkerStatus` 查看。

The V20/V21 MCP host remains available when TIA is missing, the installed release does not match, or the current user is not in the Siemens Openness group. TIA-dependent calls return `RESOURCE_UNAVAILABLE` (`tia-openness-environment`) with `rejected-before-operation` / `not-started`; read-only diagnostics remain available. Release builds default to isolation off. Use `--isolate-openness` to force it on or `--no-isolate-openness` to force it off. Generated Workbench client commands include neither switch unless the user changes them. These switches apply to MCP host startup, not explicit CLI subcommands.

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

生成工程和预热不再需要批处理快捷方式；从交付根目录直接运行上面的 V20 或 V21 引擎命令。`install-plc-tools` 为用户 Python 3.12+ 创建伴随环境；pip 按当前索引设置联网安装，也可通过 `PIP_NO_INDEX=1` 配合本地 wheel 缓存或索引离线安装。

关闭前台 `prewarm` 可按 Ctrl+C。`prewarm --stop` 关闭其 TIA 实例，预热进程本身仍需结束。完整工程流程见[项目生成](../guides/project-generation.md)。
