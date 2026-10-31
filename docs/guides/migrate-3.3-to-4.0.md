# 从 3.3 迁移到 4.0

本页写给已经在用 3.3 的人：4.0 与 3.3 并排安装、确认可用后再切换客户端，以及怎样回退到 3.3。适用于 4.0 预发布版本和正式版本。完整变化见 [4.0 发布说明](../releases/v4.0.0.md)（英文）。

4.0 起初不会通过 3.3 的更新器或工作台的“检查更新”提供，需要手动下载安装。即使 3.3 的 PowerShell 更新器下载到 4.0 包，它也会因为找不到旧布局要求的文件而报告 `package is incomplete` 并退出，不替换或删除已安装的文件；它下载的 ZIP 留在 3.3 安装目录的 `.update\`，解压副本留在 `%TEMP%\tia-mcp-update-<进程号>`，都可以手动删除。

## 主要变化

| 方面 | 3.3 | 4.0 |
|---|---|---|
| 工作台入口 | 包根的配置器启动程序，再启动 `runtime/studio/TiaOpenness.exe` | 包根 `TiaOpenness.exe`，再启动 `runtime/studio/TiaOpenness.exe` |
| MCP 服务 | `runtime/v<发布键>/` 下的旧服务程序；V20/V21 为完整引擎 | 八个版本统一为 `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`，每个 MCP 会话启动一个 `worker/TiaMcp.PlcWorker.<发布键>.exe` |
| 工具名和参数 | 3.x 名称，部分参数是 JSON 字符串 | 按操作和领域统一命名，参数使用类型化对象和数组；不提供 3.x 别名 |
| 返回结果 | 各工具形状不同 | 统一的 V4 信封：`ok`、`data`、`error`、`meta` |
| MCP 写入审批 | 无 | 默认开启，在工作台中批准 |
| 软件数据 | `%LOCALAPPDATA%` 和 `%TEMP%` 下的各目录 | 交付目录可写时在 `<包根>\data` |
| Claude Code 插件 | 固定启动本机 V21 服务 | 启动器自动选择版本，可用 `TIA_MCP_RELEASE_KEY` 指定 |
| 更新 | PowerShell 更新器 | 随包 `runtime/tools/TiaMcp.Updater.exe`，从 4.0 起用于后续版本 |
| 命令行 | 含 `gen`、`patch`、`describe`、`compile`、`export`、`import`、`prewarm` 等引擎命令 | 只保留 `doctor`、`audit verify`、`schema`、`version`、`config`、`install-plc-tools`、`help`；其余改用 MCP 工具，见 [CLI 指南](../getting-started/cli.md) |

TIA Portal、Openness 组件和许可证仍需单独安装。工作台和宿主使用随包的 .NET 10 运行时；PLC worker 需要 .NET Framework 4.8。

## 并排安装 4.0

1. 在 TIA 中保存工程，结束 3.3 中正在进行的写操作。
2. 从 GitHub Releases 下载 `TIA_MCP_Delivery_v<版本>_<日期>.zip` 和同名 `.sha256`，核对 SHA-256。不要使用 GitHub 自动生成的 `Source code` 压缩包。
3. 把 ZIP 完整解压到一个新的、可写的短路径目录，例如 `D:\TIA-MCP-4.0`。不要解压到 3.3 目录上，也不要对它运行 3.3 的更新器。目录可写时，4.0 的配置、日志和审计都保存在该目录的 `data\` 中，与 3.3 分开。
4. 从新目录启动 `TiaOpenness.exe`，打开 **环境体检**（Ctrl+7）。它检查 TIA 安装、`Siemens TIA Openness` 用户组，以及随包 worker 是否在 Openness 防火墙允许列表中。
5. 按下一节配置 AI 客户端。
6. 第一次让 4.0 连接 TIA 时，TIA 会对新的 worker 程序弹出 Openness 访问确认，选择允许。确认之前，第一次附着最长等待 300 秒后超时。
7. 先用工程副本试用，按“切换前核对”检查，再决定是否切换日常工作。

两个版本都安装后，同一个 TIA Portal 进程同一时间只让一个版本连接。4.0 的进程租约保存在自己的数据目录中，3.3 看不到它，反之亦然。

## 配置 AI 客户端

### 用工作台写入配置

在新目录的工作台中选择 **MCP 与客户端**，选择 TIA 版本、连接方式和实际使用的客户端，再选择 **写入客户端配置**。详细步骤见[配置指南](../getting-started/configuration.md)。

客户端已有 3.x 配置时，工作台会先显示迁移目标。确认后，它在原文件旁保存 `.bak_yyyyMMddTHHmmssfffffffZ` 时间戳备份，再改写启动命令和参数；`tia-portal` / `tia-portal-vm` 服务名、远程 `/mcp` 地址、鉴权字段和其他服务器保持不变。写入后该客户端改用 4.0。复杂的 Codex TOML 启动字段会拒绝自动迁移，先整理为单行 `command` 和 `args`。

写入后完全退出客户端再启动，新建会话，并刷新工具缓存和 schema。不要沿用 3.x 的启动命令和参数。

### 手动增加并排条目

要在同一个客户端里保留 3.3，可以不让工作台改写 `tia-portal`，而是手动增加一个服务名，例如 `tia-portal-4`：

- `command`：新目录中 `runtime\v<发布键>\TiaMcp.FoundationHost.exe` 的绝对路径。
- `args`：`--release-key <发布键>`；包根不在默认位置时再加 `--bundle-root <新目录绝对路径>`。

格式参考 [cursor.example.json](../getting-started/cursor.example.json)。使用时在会话中明确选择要用的服务。

### 虚拟机与宿主机（HTTP）

在 TIA 所在电脑上用 4.0 工作台启动 HTTP 服务。4.0 第一次使用时会复制 3.3 保存的连接设置（包括端口），同一端口同一时间只能有一个服务监听：并排试用时先停止 3.3 的服务，或在 4.0 中改用另一个端口并重新写入 AI 所在电脑的客户端配置。

### Claude Code 插件

| 项目 | 3.3 | 4.0 |
|---|---|---|
| 启动命令 | 插件中的 V21 服务程序，固定 V21 | `${CLAUDE_PLUGIN_ROOT}/runtime/tools/TiaMcp.PluginLauncher.exe` |
| 版本选择 | 只服务 V21 | 先选本机已安装的最新 TIA；未发现安装时选包内最新可运行版本。用 `TIA_MCP_RELEASE_KEY` 指定 `14sp1`–`21` |
| 写保护钩子 | PowerShell 脚本 | `runtime/tools/TiaMcp.WriteGuard.exe`；`TIA_MCP_ALLOW_ONLINE_WRITE=1` 和 `TIA_MCP_WRITE_GUARD=0` 的含义不变 |

插件名 `tia-portal-openness-mcp` 和服务名 `tia-portal` 不变，同一时间只安装一个版本：在 Claude Code 中用 `/plugin` 卸载 3.3 的插件并移除指向 3.3 目录的插件市场，再按[插件安装指南](../getting-started/plugin.md)从 4.0 目录添加市场并安装，然后重启 Claude Code。插件和工作台要共用同一个交付包时，启动 Claude Code 前设置 `TIA_MCP_BUNDLE_ROOT`。

## 配置和数据位置

4.0 第一次使用时只复制缺失的旧配置文件，不移动、不删除 3.3 的文件，因此 3.3 仍可照常使用。

| 3.3 中的数据 | 4.0 中的位置 | 搬迁方式 |
|---|---|---|
| 连接设置：`%LOCALAPPDATA%\TiaPortalMcp\` 下的 `http-v<版本>.json`、`client.json` | `data\config\` | 目标文件缺失时自动复制；保留原文件。它们不是 AI 客户端的 MCP 启动配置 |
| 工作台界面偏好：`%LOCALAPPDATA%\TiaOpennessStudio\ui.settings` | `data\ui\ui.settings` | 目标文件缺失时自动复制；保留原文件 |
| 普通日志：`%LOCALAPPDATA%\TiaMcp\logs\<发布键>\` 和 `studio\` | `data\logs\<发布键>\` 和 `data\logs\studio\` | 不自动搬迁；需要时在两边程序都关闭后复制，避免覆盖已有文件 |
| 原生调用诊断：`%LOCALAPPDATA%\TiaMcp\diagnostics` | `data\diagnostics` | 不搬迁 |
| 审批设置 | `data\config\approval.settings` | 3.3 没有此文件，无需搬迁 |
| 审计记录 | `data\logs\audit\` | 3.3 没有审批审计哈希链，无需搬迁 |

其他位置：

- 导入暂存在 `<包根>\staging`，不在 `data` 或 `%LOCALAPPDATA%` 下。
- 伴随 Python 环境默认仍为 `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`，两个版本共用；`TIA_MCP_PLC_TOOLS_PYTHON` 可指定其他解释器。
- 交付目录不可写时（例如放在 `C:\Program Files` 下），4.0 回退到各用途原有的用户目录：设置在 `%LOCALAPPDATA%\TiaPortalMcp`，日志在 `%TEMP%\TiaMcp\logs\<发布键>`，审计在 `%LOCALAPPDATA%\TiaMcp\logs\audit`。
- 用绝对路径环境变量 `TIA_MCP_DATA_DIRECTORY` 可指定另一个数据根；该目录不可写时启动报错，不会回退。

完整规则见[运行时布局](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/runtime-layout.md#软件自身的数据目录)。

## 写入审批

3.3 没有 MCP 写入审批；4.0 默认开启：

- `WRITE` 和 `ONLINE-WRITE` 工具的实际执行要先在工作台中批准。`SaveProject`、`SaveProjectCopy`、`CloseProject`、`StageImportFiles` 和 `CleanupStagedImportFiles` 的实际执行也要批准。连接、附着、打开和断开不需要批准。
- 预览（`dryRun: true` 或 `mode: preview`）和只读调用不等待批准。
- 工作台关闭时，需要批准的写入在开始前被拒绝，返回 `CONFIRMATION_REQUIRED`。拒绝或超时（默认 120 秒）同样返回该错误，工程没有变化。MCP 客户端不能自行批准。
- 在标题栏 MCP 状态的菜单中开关审批，在设置中选择 60、120 或 300 秒超时。设置保存在 `data\config\approval.settings`。
- 关闭审批后，结果的 `meta.warnings` 带 `APPROVAL_DISABLED`；工具自身的确认和计划哈希检查仍然执行。
- 审批、决定和结果写入 `data\logs\audit` 的哈希链，可在工作台 **审计日志** 页或用 `tia audit verify` 校验。

在 3.3 中无人值守运行的自动化，在 4.0 中要保持工作台打开以便批准，或者明确关闭审批。

## 工具名、参数和结果

4.0 不注册 3.x 名称，旧名称调用会失败。升级后刷新客户端的工具缓存，并修改保存的提示词、脚本和自动化。

### 查找旧名称的去向

[工具迁移表](../releases/v4.0.0-tool-migration.md)（英文，自动生成）按发布键分节，列出每个 3.x 名称的去向，包括未改名的工具。打开你使用的 TIA 版本那一节，搜索旧名称：

| 表中的标记 | 含义 |
|---|---|
| `Rename` | 改名，右列是 4.0 名称 |
| `Unchanged` | 名称不变，参数可能变化 |
| `Merge` | 并入另一个工具 |
| `Removed duplicate lifecycle` | 与其他工具重复，已撤销 |
| 以 `Removed:` 开头的其他标记（如 `no supported action on this release`、`no API on this release`） | 该版本的官方 API 不支持，不再列出 |
| `Added in 4.0` | 新工具 |

每节后面的 **Parameter changes** 和 **New required inputs** 列出参数变化。

### 常见变化

- **会话绑定**：每个版本都先调用 `ConnectPortal`，再调用 `AttachOpenProject`；用 `GetSessionState` 查看状态，用 `DisconnectPortal` 断开。3.3 中 V20/V21 一步连接工程的工具已撤销，改用这两步。
- **PLC 工具带 `Plc` 领域词**：例如 `ExportPlcBlock`、`ImportPlcBlock`、`GetPlcBlockInfo`、`CompilePlcSoftware`、`CompilePlcDiagnostics`。
- **列表工具以 `List` 开头**：例如 `ListPlcBlocks`、`ListPlcTagTables`、`ListPlcTypes`、`ListPlcTags`。
- **生成工具统一为 `Build` 开头、去掉 `Xml` 后缀**：例如 `BuildPlcUdt`、`BuildPlcGlobalDb`、`BuildPlcFbBlock`。
- **环境与连接诊断**：`InitializeEnvironment`、`GetPortalConnectionReadiness`。
- **用法查询**：V20/V21 原来的写作指南和配方查询并入 `GetToolUsage`，按 `toolName`、`operation`、`language` 或 `exampleId` 查询。
- **按版本移除**：V14 SP1–V19 不再列出 FB、FC、LAD FC、FlgNet 调用、变量表和结构化文本的 XML 生成工具；4.0 中 `BuildPlcFbBlock`、`BuildPlcFcBlock`、`BuildPlcLadFcBlock` 只在 V21 提供，`BuildFlgNetCall`、`BuildPlcTagTable`、`BuildStructuredText` 在 V20/V21 提供。V21 的 SDK 没有 `ExportScadaData`、`ImportSinumerikAlarmTexts`、`InitializeSimotionScripting`、`ManageSinumerikArchive`、`ManageSinumerikSafetyMode` 对应的 API，这五个工具在 V21 上不再列出。V20 上不再列出只有 V21 才有 API 的工具，逐项见迁移表。
- **V14 SP1–V19 新增工具**：硬件、网络、HMI、库、在线、批处理和发现等工具族加入这些版本，只要该版本的 API 支持其中至少一个动作。各版数量见[逐版本工具说明](../reference/version-tools.md)（英文）和服务的 `tools/list`。
- **V20/V21 档位**：默认仍为 lite 档，其余工具用 `FindTools` 查找、`CallTool` 调用，或向宿主传入 `--profile full`。

### 参数

原来以 JSON 字符串传入的参数改为对象或数组，例如 Unified 对象路径从字符串参数 `objectPathJson` 改为数组参数 `objectPath`，每一步是一个属性步骤：

```json
{ "objectPath": [ { "property": "Screens", "name": "Overview" } ] }
```

格式不对的路径在确认、审批和任何 TIA 调用之前被拒绝，错误消息给出期望的格式。调用不熟悉的工具前，用 `GetToolUsage(toolName, operation)` 取得当前版本的参数、示例和结果解释。

### 结果

每个结果都是 V4 信封。先看 `ok`，再看 `meta.outcome`、`meta.execution`、`meta.completeness` 和 `meta.requestId`；失败时看 `error.code` 和 `error.details`。结果取值和错误码列表见 [4.0 发布说明](../releases/v4.0.0.md#result-envelope-and-errors)（英文），处理方法见[调用失败时如何处理](../troubleshooting/errors.md)。

遇到 `OUTCOME_UNKNOWN` 或 `SESSION_RESET_REQUIRED` 时，先在 TIA 中检查工程实际状态，再调用 `DisconnectPortal`，然后新建 MCP 会话并重新调用 `ConnectPortal` 和 `AttachOpenProject`。不要重放失败的写请求。

## 切换前核对

用工程副本依次确认：

1. 新会话的 `tools/list` 数量与 [README](../../README.zh-CN.md) 中所选版本一致。
2. `GetToolUsage()` 返回当前版本的示例。
3. `ListPortalProcessProjects` → `ConnectPortal` → `AttachOpenProject` → `GetSessionState` → `GetProjectTree` → `ListPlcBlocks` 都成功，且绑定的是你期望的工程和 PLC。
4. 一个写工具先预览，再实际执行并在工作台批准；**审计日志** 页出现同一个 `requestId`。
5. 需要保存时调用 `SaveProject` 并批准；最后调用 `DisconnectPortal`。

全部通过后再把日常工作切换到 4.0。保留 3.3 目录，直到确认不再需要回退。

## 回退到 3.3

1. 在 TIA 中保存需要保留的修改。关闭 4.0 工作台，退出使用 4.0 的 AI 客户端，使 4.0 宿主和 worker 退出。
2. 如果 4.0 的写操作被中断或结果未知，先在 TIA 中检查工程，并重启该 TIA 实例，再让 3.3 连接。正常空闲退出不需要重启 TIA。
3. 恢复客户端配置：
   - 用工作台写入过的客户端：用原文件旁的 `.bak_...` 备份还原，或用 3.3 工作台重新写入。
   - 手动增加的并排条目：删除该条目即可，3.3 的 `tia-portal` 未改动。
   - Claude Code 插件：卸载 4.0 插件和市场，从 3.3 目录重新添加市场并安装，然后重启 Claude Code。
4. 从 3.3 目录启动工作台。3.3 的设置仍在 `%LOCALAPPDATA%` 原位置，4.0 没有修改它们。
5. 4.0 的日志、诊断和审计留在 4.0 目录的 `data\` 中，3.3 不读取。确定不再使用 4.0 时，关闭它的所有进程后删除整个 4.0 目录。

## 相关文档

- [4.0 发布说明](../releases/v4.0.0.md)（英文）和[工具迁移表](../releases/v4.0.0-tool-migration.md)（英文）
- [配置指南](../getting-started/configuration.md) · [插件安装指南](../getting-started/plugin.md) · [CLI 指南](../getting-started/cli.md)
- [worker 与恢复](openness-worker-isolation.md)（英文） · [已知限制](../troubleshooting/openness-limitations.md)
- [预发布真机验证清单](beta-tester-checklist.md)
