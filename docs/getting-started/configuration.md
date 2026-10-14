# MCP 图形配置指南

第一次使用请先阅读[新手使用指南](beginners.zh-CN.md)，完成环境准备、连接和单个函数练习。本页用于查询连接选项、客户端文件、升级和故障处理。

当前源码构建的工作台从包根目录 `TiaOpenness.exe` 打开，选择 **MCP 与客户端** 页签（Ctrl+1）进入配置页，选择 **项目操作** 页签（Ctrl+2）返回项目页。标题栏显示当前页面，状态栏显示 MCP 服务与共享会话状态。项目、PLC 操作通过所附着的 MCP 会话调用同一套工具、审批和审计；服务启动、停止、网络权限、测试连接和客户端配置在配置页操作。标题栏的 **引擎 & MCP 设置…** 打开设置抽屉，可切换语言、主题并检查更新。配置页的 **AI 客户端** 卡片提供 **保存两端配置**。配置页编译在工作台内，桌面使用包内附带的 .NET 10 运行时（`runtime/dotnet`），必须保留完整解压包，不能单独复制入口 EXE。

快捷键：Ctrl+1–8 依次打开 MCP 与客户端、工程操作、程序块、版本控制、AI 调用、审计日志、环境体检和日志；Ctrl+O 打开项目选项；Ctrl+S 保存项目；Ctrl+B 编译 PLC 软件；Esc 关闭临时界面。保存和编译仍走原有确认与审批。

使用 Claude Code 插件时，请阅读[插件安装指南](plugin.md)，设置插件要使用的交付包和 TIA 发布版本。

## 同一台电脑

1. 选择 **同一台电脑**。
2. 选择准确 TIA 版本：V14 SP1、V15.1、V16、V17、V18、V19、V20 或 V21。
3. 选择 **自动检测**，或 **浏览** 选择安装根目录，例如 `C:\Program Files\Siemens\Automation\Portal V21`，不带 `Bin`。
4. 选择实际使用的客户端卡片，完全退出该客户端，选择 **写入客户端配置** 并检查结果。
5. 重启客户端、新建会话，使用服务 `tia-portal`。

本机使用 stdio，由客户端启动对应引擎，不需要 IP、密钥或手动启动 HTTP 服务。所选版本应与实际 TIA 和工程匹配，选择器不转换工程版本。

4.0 的八版启动命令由统一产品表生成：`runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`。
V20/V21 自动发现 `worker/TiaMcp.Engine.V20.exe` / `worker/TiaMcp.Engine.V21.exe` 及同目录的 `tool-catalog.json`；无需配置 worker 路径。参数包含 `--bundle-root <绝对包根>`、准确版本和 TIA 安装路径；缺少所选引擎时拒绝写入，不切换版本。工作台本身也接受 `--bundle-root <绝对包根>` 或 `TIA_MCP_BUNDLE_ROOT`：命令行优先，然后是环境变量，最后是正式安装/开发输出位置。根必须包含 `manifest/package-manifest.json`；显式根无效立即报错。

## 虚拟机与宿主机，或两台电脑

### TIA 所在电脑：启动服务

1. 用运行 TIA 的 Windows 用户打开工作台，选择 **虚拟机 ↔ 宿主机**。
2. 选择 TIA 版本和安装根目录，填写该电脑的实际 IPv4 和端口，默认 `8765`。
3. 选择 **生成** 得到密钥，选择**AI 客户端** 卡片右上角的 **保存两端配置** 保存。
4. 选择 **网络权限**，完成管理员授权，再选择 **启动服务**。
5. 查看活动日志的监听信息和连接摘要“运行中”，保持窗口打开。

用户应属于 `Siemens TIA Openness` 组，添加后注销重登，并处理首次 TIA 访问提示。“网络权限”配置所选 HTTP 地址和本地子网入站规则；跨路由或 VPN 的访问按实际网络另行配置。

### AI 所在电脑：写入连接

1. 打开工作台，选择相同模式，填写 TIA 电脑的 IPv4、端口和相同密钥。
2. 选择实际使用的客户端，选择 **测试连接**。
3. 完全退出客户端，选择 **写入客户端配置**，检查写入位置及结果。
4. 重启客户端，新建会话，使用服务 `tia-portal-vm`。

宿主机未安装 TIA 时，“保存两端配置”可能记录客户端信息并报告服务端环境不可用；实际服务端是前一台电脑。测试连接成功说明 MCP 可达，仍需让 AI 确认实际 TIA 工程和 PLC。

### 文件、PLC 和保存分别是什么

- **文件路径**：导入工具读取 TIA/MCP 电脑上的文件。在虚拟机模式中，宿主机 `D:\...` 不会自动成为虚拟机的同一路径；先把源文件复制到服务端可读取的位置。
- **选择 PLC**：一个工程可能包含多台 PLC，程序导入、浏览和编译需要明确目标。使用工程树实际返回的软件路径；工作台在设备下拉框中选择。
- **保存工程**：导入和编译可能只改变当前打开的工程。核对结果后调用 `SaveProject`，或在工作台选择 **保存项目**，才完成相应保存操作。

这些是完成一次实际操作的步骤，不是另外增加三个功能。

## 客户端卡片和配置文件

卡片支持多选，副标题显示本机检测结果；未检测到的客户端也可先写配置。模型名称卡片实际对应其支持 MCP 的 CLI 或宿主，按卡片说明选择软件。

| 卡片 | 配置目标及默认位置 |
|---|---|
| Claude Code | `%USERPROFILE%\.claude.json` |
| Codex | `%CODEX_HOME%\config.toml`，未设置时为 `%USERPROFILE%\.codex\config.toml` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` |
| Qwen | Qwen Code：`%USERPROFILE%\.qwen\settings.json` |
| Kimi | Kimi Code CLI：`%KIMI_CODE_HOME%\mcp.json`，未设置时为 `%USERPROFILE%\.kimi-code\mcp.json` |
| Yuanbao | CodeBuddy Code：`%USERPROFILE%\.codebuddy\.mcp.json` |
| DeepSeek / GLM / Grok | OpenCode：`%USERPROFILE%\.config\opencode\opencode.json` |
| Qwen Agent | `%USERPROFILE%\.qwen-agent\mcp.json` |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` |
| VS Code · Copilot | `%APPDATA%\Code\User\mcp.json`；仅检测到 Insiders 时选择对应目录 |

三张 OpenCode 卡片共用一个文件；选择后还需在 OpenCode 中使用相应模型。写入配置不代表已完成真实客户端联调。自定义 profile、portable 安装等位置以写入确认中展示的路径为准。

工作台保留已有其他服务和设置，并为旧文件生成 `.bak_...` 备份。无效输入会报告具体失败；多选客户端时分别查看各项结果。JSONC 重写后为标准 JSON，原文保留在备份中。已有同名 `tia-portal` / `tia-portal-vm` 定义时检查是否存在重复来源。

已有 3.x EXE 或旧目录的本地配置，在选择“写入客户端配置”后会显示迁移目标；确认后先在原文件旁保存 `.bak_yyyyMMddTHHmmssfffffffZ` 时间戳副本，再更新启动命令和参数。取消时不写文件；预览后文件发生变化会拒绝写入，写入失败会恢复原文件，备份保留。迁移保留 `tia-portal` / `tia-portal-vm`、远程 `/mcp` URL、鉴权字段和其他服务器。复杂的 Codex TOML 启动字段会拒绝自动迁移，请先整理为单行 `command` 和 `args` 再确认。

本机连接记录默认位于可写交付包的 `data\config`；可用绝对路径环境变量 `TIA_MCP_DATA_DIRECTORY` 指定数据根。无法定位交付包或其 `data` 不可写时，沿用 `%LOCALAPPDATA%\TiaPortalMcp`。首次使用新目录只复制缺失的旧记录，保留旧文件且不覆盖已有文件；HTTP 服务配置按所选版本保存为 `http-v<版本>.json`，客户端记录为 `client.json`。这些本机记录使用当前 Windows 用户的加密保护；客户端自身配置文件按其格式保存密钥，不要公开配置或备份。

### MCP 会话与 worker

八个版本都由 FoundationHost 提供 MCP，每个 MCP 会话使用一个 worker。V14 SP1–V19 使用 PLC worker，V20/V21 使用引擎 worker；隔离开关 `--isolate-openness` 和 `--no-isolate-openness` 已删除。使用工作台生成的宿主命令，无需选择隔离模式。

worker 超时预算默认为 120 秒，每个代次的首次附着为 300 秒，编译、传输、导入导出、归档和库更新为 30 分钟。可用 `--worker-timeout-config` 或 `TIA_MCP_WORKER_TIMEOUTS` 设置分类预算，`--worker-timeout-seconds` 设置默认预算。派发前可以取消；派发后等待真实结果，超时或通道故障不会重放原生操作。

V20/V21 用 `GetOpennessWorkerStatus` 查看 worker 代次、活动调用和故障。发生结果未知时先检查 TIA 工程及调用日志，再用 `DisconnectPortal` 恢复：空闲 worker 干净分离，忙碌或丢失的 worker 结束后须重启 TIA。也可显式使用 `RestartOpennessWorker` 重启当前会话的 worker；重新连接后必须再次绑定工程。详见[worker 与恢复](../guides/openness-worker-isolation.md)。

## 工具显示与示例

<!-- generated:tool-counts:begin -->
由 `generate-tool-counts` 从 `manifest/version-tools.json` 和 `ToolProfiles.resx` 生成。以运行时 `tools/list` 为准。

| TIA 版本 | 注册工具数 | lite 档 |
|---|---:|---:|
| V14 SP1 | 278 | 不适用 |
| V15.1 | 318 | 不适用 |
| V16 | 346 | 不适用 |
| V17 | 369 | 不适用 |
| V18 | 395 | 不适用 |
| V19 | 470 | 不适用 |
| V20 | 500 | 84 |
| V21 | 511 | 84 |
<!-- generated:tool-counts:end -->

V20/V21 默认使用 lite 档，可用 `FindTools` / `CallTool` 访问其余工具，或向宿主传入 `--profile full`。V14 SP1–V19 发布其全部注册工具，也提供发现、桥接和批处理入口。所有版本都用 `GetToolUsage` 读取自己的参数、语言示例和调用序列。修改配置后重启服务与客户端，刷新目录缓存。

Foundation 的单项软件工具接受工程树中的精确 `softwarePath`，也接受唯一的 PLC/device 别名；`plc` 参数使用同一规则。`table` 可传 `ListPlcTagTables` 返回的精确转义路径，或唯一的单段原始 `Name`（包含中文名称）。批量块/类型导出和目录导入保留精确软件路径及组路径要求。`ExportPlcBlocks` 与 `ExportPlcTypes` 的目标目录必须已存在；单项 XML 导出则使用现有目录下的新 `.xml` 文件。

## V4 结果、审批与审计

每个工具结果都是 V4 信封：检查 `ok`、`data`、`error` 和 `meta`，重点读取 `meta.outcome`、`meta.execution`、`meta.completeness` 与分页字段。遇到 `OUTCOME_UNKNOWN` 时先检查工程实际状态，按要求重置会话，不要自动重放写操作。D1 原生行为目前仍为 `current`、L5 为 `NOT RUN`；离线测试不会改变真机验收状态。

Foundation worker 的前置参数错误会把可修正的 worker 消息放在 `error.message`，并在 `INVALID_ARGUMENT` 中标出参数；路径和密钥按既有脱敏规则处理，消息长度有上限。读失败的诊断位于 `error.details.nativeMessage`。未知写入仍返回 `OUTCOME_UNKNOWN`，worker 消息位于错误详情中；先核对 TIA 状态，再按结果要求重置会话。

MCP 的 `WRITE` / `ONLINE-WRITE` 实际执行默认等待工作台审批后才派发；`dryRun=true` 和候选工具的 `mode=preview` 不等待审批，也不作为写请求写入审计。`SaveProject`、`SaveProjectCopy`（save-as）和 `CloseProject` 的实际执行也须审批，尽管工具目录将它们标为 `SESSION`；连接、附着、打开和断开等其他会话操作仍不审批。拒绝、默认 120 秒超时或工作台不可用时，返回 `CONFIRMATION_REQUIRED`，操作在开始前被拒绝；MCP 客户端不能自行审批。审批、决定和操作结果记录在共享 `data/logs/audit` 哈希链，可通过工作台审计页或 `tia audit verify` 校验。哈希链只验证保留记录间的完整性，不能证明整段日志未被删除。诊断调用包位于 `data/diagnostics`，普通引擎和工作台日志位于 `data/logs`；详细路径和只读安装回退见[运行时布局](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/runtime-layout.md)。

同一次 MCP 实际写调用只记录一条 `request`，使用响应 `meta.requestId`，已知时带上 `planHash`。获批或关闭审批后的实际派发才记录 `start`；拒绝和超时记录 request、审批决定及 end，不记录 start。各类审计事件用同一 requestId 关联响应和审批决定。

## 更新

### 从 3.x 升级到 4.0

3.x 到 4.0 必须全新安装。下载完整的 4.0 ZIP 并解压到一个新文件夹；不要覆盖 3.x 文件夹。3.3 的 PowerShell 更新器会在备份或替换前检查包是否包含它要求的旧布局文件。4.0 不再包含这些文件，因此 3.3 更新器会报告 `package is incomplete` 并退出，不会替换或删除现有安装的文件；此前下载的 ZIP 留在安装目录的 `.update\`，解压副本留在系统临时目录的 `tia-mcp-update-<进程号>`，可以手动删除。保留旧文件夹，直到 4.0 工作台连接及客户端配置确认完成。

4.0 的 engine 可执行文件名和 MCP 工具名都已变化。用新目录里的工作台为每个 AI 客户端重新生成配置，并重启客户端、刷新工具缓存；不要沿用 3.x 客户端启动命令。

| 3.3 中的数据 | 4.0 中的位置 | 搬迁方式 |
|---|---|---|
| 连接设置：`%LOCALAPPDATA%\TiaPortalMcp\` 下的 `http-v<版本>.json`、`client.json` | 可写交付目录的 `data\config\` | 首次启动时，4.0 在目标文件缺失时自动复制这些旧设置文件；保留 3.3 原文件作为备份。它们不是 AI 客户端的 MCP 启动配置。 |
| 工作台界面偏好：`%LOCALAPPDATA%\TiaOpennessStudio\ui.settings` | `data\ui\ui.settings` | 首次启动时目标文件缺失才会自动复制；保留原文件。 |
| 审批状态 | 3.3 没有工作台 MCP 审批状态文件；4.0 为 `data\config\approval.settings` | 无 3.3 状态可搬。新装 4.0 默认开启审批；以后重装 4.0 时，关闭程序后可复制该文件以保留审批开关和超时设置。 |
| 普通日志：`%LOCALAPPDATA%\TiaMcp\logs\<releaseKey>\` 和 `studio\` | `data\logs\<releaseKey>\` 和 `data\logs\studio\` | 4.0 不自动搬日志。旧日志留在原处即可；如需在新工作台中查看，可在两边程序关闭后复制对应子目录到新目录，避免覆盖已有文件。 |
| 审计数据 | 3.3 没有 4.0 的审批审计哈希链；4.0 保存在 `data\logs\audit\` | 没有 3.3 审计链可搬。迁移已有 4.0 安装时，如需保留校验链，关闭所有程序后完整复制整个 `audit` 目录，不要只复制其中一部分。 |

当交付目录不可写时，4.0 的设置回退到 `%LOCALAPPDATA%\TiaPortalMcp` 和 `%LOCALAPPDATA%\TiaOpennessStudio\ui.settings`，engine/工作台日志回退到 `%TEMP%\TiaMcp\logs\<releaseKey>` / `studio`，审批审计回退到 `%LOCALAPPDATA%\TiaMcp\logs\audit`。从 4.0 开始，后续版本更新使用随包的 C# 更新器。

### 从 4.0 更新到后续版本

先结束工程操作并保存需要保留的修改，关闭工作台、AI 中的 TIA 会话及 MCP 服务。交付目录可使用 设置抽屉中的 **检查更新**。随包 `runtime/tools/TiaMcp.Updater.exe` 检查下载校验和、备份到 `.previous`，替换交付文件后重新打开工作台；日志显示在更新器控制台。更新前会等待指定的工作台退出，并拒绝正在运行的引擎、FoundationHost或 worker。用户数据目录和未归更新器所有的文件会保留，`.previous` 最多保留两份。源码 Git 工作区使用仓库的开发流程，不由交付更新器覆盖。虚拟机服务端更新完成后重新启动服务。

无界面时从交付根目录运行 `runtime/tools/TiaMcp.Updater.exe -InstallRoot . -Check` 检查版本，去掉 `-Check` 执行更新，或运行 `runtime/tools/TiaMcp.Updater.exe -InstallRoot . -Rollback` 恢复最近一次更新前的备份。可用 `-Version vX.Y.Z` 选择版本、`-Force` 重装、`-Repository owner/name` 指定发布仓库、`-TimeoutSeconds n` 设置网络超时、`-WaitForPid n` 等待一个进程退出，成功后用 `-RelaunchConfigurator` 重新打开工作台。更新器使用随 Windows 提供的 .NET Framework 4.8，因此替换 `runtime` 中的 .NET 运行时不会影响正在运行的更新器；它会先复制自身和配置到临时目录，再对安装目录进行更新。

工作台的更新检查和更新启动入口会拒绝 Git checkout、worktree（包括 `.git` 文件）以及保留源码标记的源码包；放在这些目录中的暂存包同样拒绝更新。只读安装仍能读取产品和客户端连接信息，软件自身数据沿用上述用户目录回退；可把完整新包解压到可写的新目录，再显式迁移客户端命令。

## 常见问题

| 现象 | 检查或操作 |
|---|---|
| HTTP 地址拒绝访问 | 在服务端选择“网络权限”，检查实际地址 |
| 端口占用 | 停止旧服务或使用另一个明确端口，并重写客户端配置 |
| 找不到 API | 版本、安装根目录和对应 Openness 安装 |
| 连接超时 | 服务端实际 IP、服务运行状态、网络和防火墙规则 |
| HTTP 401 | 两端密钥及客户端重新加载情况 |
| HTTP 503 / 未就绪 | 查看服务端日志 |
| 连接后没有工程 | 用当前版本的连接示例明确绑定目标工程 |
| 工具不存在 | 检查当前版本目录，V20/V21 再使用 FindTools |

需要命令行诊断时使用 [CLI 指南](cli.md)。V20/V21 可选worker隔离的参数与恢复方式见[隔离指南](../guides/openness-worker-isolation.md)，工作台没有对应勾选项。

作为 Claude Code 插件使用时，还会运行随包的 `runtime/tools/TiaMcp.WriteGuard.exe`；它记录审计并按环境设置控制在线写入。普通 GUI/MCP 连接不等于加载了该插件。具体工具执行参数与结果解释仍以 `GetToolUsage` 为准。

手动本机 V21 配置见 [cursor.example.json](cursor.example.json)，替换引擎绝对路径后使用。其他版本和远程模式优先由工作台生成，避免复制不匹配参数。

从源码构建时，兼容启动器位于 `src/Studio/Launcher/Launcher.cs`；构建入口与工作台资源位置见[仓库结构](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/repository-layout.md)。

原生调用诊断默认写入数据根的 `diagnostics`，`TIA_MCP_DIAGNOSTICS_DIRECTORY` 仍有最高优先级；无可用数据根时沿用 `%LOCALAPPDATA%\TiaMcp\diagnostics`。语言与主题位于 `data\ui\ui.settings`，同样只复制缺失的旧偏好。完整位置与回退规则见[运行时布局](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/runtime-layout.md#软件自身的数据目录)。

## 工作台外观与历史记录

工作台的浅色和深色主题使用不透明容器、分隔行和下划线选项卡。在 **设置** > **外观** 中选择语言和自动、浅色或深色主题，立即生效。旧版低效果偏好仍可读取；当前外观不使用阴影或模糊。

AI 调用、审批、审计和活动列表使用回收虚拟化。位于顶部时，跟随调用保持最新记录可见；向下滚动时保留当前位置。暂停会冻结调用历史，新审批仍可处理。审计页每次显示最多 1,000 行，选择 **前 1,000 条** 向前翻页，选择 **最近 1,000 条** 返回最新记录。完整审计文件保留在磁盘。校验在后台检查完整哈希链并显示进度；定位断点会加载对应页面。

活动和引擎输出分批更新，内存文本上限为 40 KB，记录和可见日志行上限为 1,000。复制只复制当前保留文本，磁盘日志不受影响。隐藏的调用和审计页暂停读取，打开后补齐记录；每个页面都可接收和处理审批。引擎日志级别仍为 1。

## TIA 所在电脑上的导入暂存

`StageImportFiles` 接收客户端文本，写入包根旁的 `staging/<会话>/<批次>/`。宿主通过 `--bundle-root`、`TIA_MCP_BUNDLE_ROOT` 或程序布局定位包根。已有暂存目录必须可写；首次创建暂存目录时需要包根的创建权限。拒绝信息包含目标路径。审批前拒绝使用 `APPROVAL_PRECHECK_REFUSED`，`details.stage` 为 `approval-precheck`；`NATIVE_WARNING` 只用于原生诊断。暂存不使用 `%LOCALAPPDATA%`。审批请求显示位置、文件名、类型、字节数和 SHA-256，不显示文件正文。

先用 `dryRun: true` 预览，再用 `dryRun: false` 执行。文件名不能包含目录，扩展名必须符合类型；每个文件最多 4 MiB，每个会话最多 128 个文件、32 MiB。SCL、TYPE 源和 S7DCL 使用无 BOM 的 ASCII；SimaticML 使用 UTF-8，并以 Openness `Document` 为根。V20 文档导入需要配对的 `.s7dcl` 和 `.s7res`；V21 资源文件可选。每次调用新建批次，不覆盖已有文件。

批次清单记录归属，不保存文件正文。`ListStagedImportFiles` 返回跨会话最新的 200 个批次，包括绝对目录、路径、SHA-256、`currentSession`、`totalBatches`、`returnedBatches` 和 `truncated`；配额只统计当前会话。无法读取或已消失的会话、批次列为未知并附原因。批次记录 MCP 会话及宿主进程身份；其他仍存活或无法核对的会话所属批次不能清理。会话结束、超时或宿主退出后可以清理。缺少归属证据的旧清单可列出，但拒绝跨会话清理。

清单使用刷盘后的临时文件和原子替换，保留上一份有效副本。主清单损坏时标为 `manifest-invalid`，有效副本可提供文件证据。批次或会话身份与目录不符时，即使有副本也拒绝清理。清理独占打开每个文件，核对长度和 SHA-256 后才删除；修改、锁定、链接或未知文件保留在 `unknownEntries`，返回 `folderRetained: true` 和警告。删除前出错为 `IO_FAILED` / `not-started`；删除后出错为 `PARTIAL_FAILURE`，并返回本批次计数。

目录覆盖导入先用 `overwrite: true` 预览暂存目录，审阅每个新建或替换动作，再提交返回的 `expectedPlanHash`、完整 `importOrder` 和精确工程确认。首次导入前导出所有替换目标。失败后停止批次，按依赖顺序恢复已知替换；检查 `restored`、`remainingChanged`、每项恢复状态及 `recoveryDirectory`。结果未知时保留恢复证据，检查工程并建立新会话。恢复导出与暂存清理分开。所选版本的 SCL、TYPE/UDT 和程序目录流程见 `GetToolUsage`。

单个 XML 导入尝试导出恢复备份。不一致、受保护、恢复目录不可用或导出被拒绝、无效时，返回 `BACKUP_SKIPPED`、对象及原因和 `recoveryStatus: backup-skipped`，仍继续导入。批量覆盖在审批前要求恢复目录可写且所有替换目标可导出。只有 `overwrite: true` 的重名目标检查一致性和保护；无法读取属性标为 `replace-blocked: unknown-consistency`。先用 `CompilePlcSoftware` 编译新导入对象；被阻止的预览以 `replace-blocked` 点名对象并返回 `PRECONDITION_FAILED`。修复后重新预览取得计划哈希。已恢复的替换不计入 `imported`。更新器原位保留暂存，不放入 `.previous` 备份。

编译返回错误是已完成的失败：`ok: false`、`COMPILE_ERRORS`、outcome 为 `failed`、execution 为 `completed`。错误数取原生计数与观察到的错误消息数中的较大值；保留原生警告数、最多十条带对象路径的叶级错误，`data` 保留完整诊断。修复后再编译。工艺对象导出也在审批前拒绝不一致对象。
