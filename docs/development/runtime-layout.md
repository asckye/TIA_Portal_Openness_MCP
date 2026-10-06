# 运行时布局与清理（G7、P2-05）

[重构计划](refactor-plan.md) · [引擎拆分设计](engine-decomposition.md) · [响应与异常设计](response-and-errors.md) · [验证分层](validation.md)

本页记录 G7（运行时与仓库布局解耦）的现行实现和 P2-05（历史注释、`*Leftovers` 与用户可见文案语言）的清理规则。路径缩写：`E/` =
`src/Engine/`，`L/` = `src/Logic/`，`S/` = `src/Studio/`。
P2-05 的初始统计取自 2026-10-03，完成情况另行标明。

## V20/V21 服务在 Openness 未就绪时启动（P6-54）

V20/V21 MCP host startup no longer exits solely because TIA is absent, the detected major version differs, or Openness initialization/user-group checks fail. It records a shared readiness state for Bootstrap, the environment doctor, and tool admission. `RESOURCE_UNAVAILABLE` with resource `tia-openness-environment` is returned before a TIA-dependent native call; the V4 outcome is `rejected-before-operation` and execution is `not-started`. Diagnostic and offline-classified tools remain available. Explicit CLI verbs, `doctor`, and syntax/runtime exit codes keep their command behavior.

Isolation uses one build property, `TiaMcpWorkerIsolationDefault`, recorded as `workerIsolation.enabledByDefault` in the release build record. Release builds set it to `false` until the V20/V21 real-machine acceptance in `docs/reference/real-machine-ledger.md` passes. `--isolate-openness` and `--no-isolate-openness` override that default for MCP host invocations. Client config generation does not add either switch. The Workbench environment page distinguishes a running engine from a ready Openness environment, and the call panel identifies calls dispatched to `engine-worker`.

## G7：运行时资源定位

### MCP 写操作审批（P6-44）

完整引擎与 Foundation 的 MCP 写调用默认等待工作台批准；CLI 由本机用户直接执行。
工作台标题栏 MCP 状态片的菜单可开关审批，设置页可选 60/120/300 秒，默认 120 秒。
`data/config/approval.settings` 保存两行 `enabled=true|false` 与 `timeoutSeconds=1..3600`；缺失、损坏或不可读时开启审批。
离线快照/测试可在专用 `TIA_MCP_DATA_DIRECTORY` 下显式设置 `enabled=false`，写结果会含
`APPROVAL_DISABLED`，D1 的 confirm/planHash 校验继续执行。该设置不得放入正式交付缺省值。

本机命名管道名为 `TiaMcp.Approval.v1.<sha256>`，摘要绑定当前 Windows 用户 SID 与设置文件绝对路径。
工作台创建首实例，使用显式受保护 DACL `O:<SID>D:P(A;;GA;;;<SID>)`，只有当前用户可打开，拒绝远程客户端；
工作台核对连接方 SID，宿主核对服务进程 SID。管道不注册为 MCP 工具或 HTTP 端点。
威胁模型是 **AI writes through MCP tools**：MCP 客户端不能用工具参数、元数据或批次自行批准。
这不保护同一用户下的恶意本机进程，也不保护配置文件被该用户改写或替代工作台。
MCP 的 `SaveExportContent` 不能写 `approval.settings`、其常规短文件名、默认数据流或指向该文件的现有链接；
宿主在文件操作前返回 `CONFIRMATION_REQUIRED` / `denied` 并审计。无法核对文件身份时也拒绝，不能通过导出工具关闭审批。

协议 v1 是 4 字节小端长度加 UTF-8 JSON，每帧最大 1 MiB。每次连接只有一个请求和一个决策；
请求包含随机请求 ID、tool/releaseKey、已缓存及请求指定的工程身份、对象/动作清单、计划哈希和完整参数/身份摘要。
显示输入先脱敏，摘要仍包含敏感输入的变化。批准匹配 ID、planHash 与参数摘要，只允许一次尝试；
重复、超时、格式异常、断连或无工作台均拒绝，工程动作尚未发出。完成通知只更新该请求的结果状态，不能授予批准。
审批请求、决定、超时和开关变化写入现有审计链。批次逐项批准写项；只读项不入队。
等待发生在专用 Openness 线程之外、动作调用之前；隔离引擎使用内部等待通知扣除有界审批时间，原生执行截止预算不变。
审批不改变 Siemens 调用序列、参数、线程归属或会话。

### 安装根与开发输出

G7-1…G7-7 已完成。完整交付包可放在仓库之外；安装根不需要 `.git`。交付包由
[`Package-Release.py`](../../scripts/build/Package-Release.py) 按 [`delivery-files.json`](../../scripts/operations/delivery-files.json) 过滤 Git 文件集，再加入清单中的运行文件，
排除 `runtime/verification/`。交付包保留资源、用户文档、插件及桥接所用 Python 源码；开发源码、检查脚本和开发文档不分发，不能只复制 EXE。

[`BundleLayout.cs`](../../src/Shared/BundleLayout.cs) 以 `manifest/package-manifest.json` 为根标记，
维护资源相对路径和同级产品路由。公共实现由 `TiaMcp.Logic` 导出，两个引擎、Foundation 和 CLI 共用；
Studio 与适配器的链接副本保留内部可见性，跨程序集通过 AppDomain 中的字符串共享启动时的选择。

P6-37 的引擎、Foundation 和 CLI 都接受 `--bundle-root <absolute-path>` 与 `TIA_MCP_BUNDLE_ROOT`。
选择顺序为 **CLI → 环境变量 → 正式安装/开发锚点**；两项显式输入并存时 CLI 优先。
相对路径或缺参数属于语法错误（CLI 退出 64）；目录不存在或缺根标记退出 70。
显式根无效立即报 `RESOURCE_UNAVAILABLE`，不会尝试环境变量或其他根，不启动工具。
启动选择固定后供资源、数据目录及路由共用，并传给子进程。

下表路径均相对包根；`<configuration>` 为 `Release` 或 `Debug`。

| 锚点 | 基目录 |
|---|---|
| 包根 | 自身含 `manifest/package-manifest.json` 的目录 |
| MCP 安装输出 | `runtime/v14sp1`、`runtime/v15.1`、`runtime/v16`–`runtime/v21` |
| 完整引擎开发输出 | `src/Engine/bin-v20/<configuration>/net48`、`src/Engine/bin/<configuration>/net48` |
| Foundation 开发输出 | `src/FoundationHost/bin/<configuration>/net10.0` |
| 引擎测试宿主输出 | `tests/Engine/TiaMcpServer.HttpTests/bin/<configuration>/net48`；`tests/Engine/TiaMcpServer.LegacyHostTests/bin/<configuration>/net10.0`；`tests/Engine/TiaMcpServer.Tests/bin/<configuration>/net10.0` |
| Studio 安装输出（既有） | `runtime/studio`、`runtime/studio/bridge` |
| 随包独立工具 | `runtime/tools`；apphost 先找 `../dotnet`，再找 `DOTNET_ROOT` 和已安装 .NET |
| Studio 开发输出（既有） | `src/Studio/Gui/bin/<configuration>/net10.0-windows` 及其 `bridge` 子目录；`src/Studio/Bridge/bin/<configuration>/net48` |
| 随包 .NET 运行时 | `runtime/dotnet`；apphost 先找 `../dotnet`，再找 `DOTNET_ROOT` 和已安装 .NET |

解析不依赖当前工作目录、`.git` 或写权限，不向任意祖先查找资源。
资源缺失由 `RequirePath` / `RequireResource` 报 `RESOURCE_UNAVAILABLE`，携带选定根下的预期路径。

| 资源 | 包根相对路径 |
|---|---|
| 根标记、交付清单 | `manifest/package-manifest.json`、`manifest/delivery.json` |
| 指南及来源 | `reference/siemens-openness/skills`、`reference/siemens-openness/UPSTREAM.json` |
| V21 生态目录参考副本 | `reference/v21-ecosystem.json` |
| Claude Code 写入防护工具 | `runtime/tools/TiaMcp.WriteGuard.exe`，由 `hooks/hooks.json` 通过 `${CLAUDE_PLUGIN_ROOT}` 启动 |
| PLC Tools、SimaticML 桥接 | `scripts/ecosystem/plc_tools_bridge.py`、`scripts/ecosystem/simaticml_decode_bridge.py` |
| 更新器 | `runtime/tools/TiaMcp.Updater.exe` 及其 `.config`（.NET Framework 4.8） |
| CLI 模板 | `templates`、默认 HMI 模板 `templates/hmi`；`__BUNDLE__` 引用的具体文件/目录也必须存在于选定根 |

`EcosystemFiles.RepositoryRoot` 现在只返回公共解析器选定的根；指南不再要求 Python 桥接文件作为根标记。
不再读取旧 `TIA_MCP_REPOSITORY_ROOT`，也不使用祖先仓库、`templates`/源码目录组合、
`TMP_EXPORT` 或 cwd 推断包根。私人工作区及输出缺省的后续收口属于 P6-39。

同级路由统一调用 `BundleLayout.RequireEngine`：安装布局使用
`runtime/v<key>/TiaMcp.FoundationHost.exe` 或 `TiaMcp.Engine.V20.exe` / `TiaMcp.Engine.V21.exe`；
正式开发锚点使用对应 Release/Debug 输出。目标缺失即拒绝，不改用当前 EXE，也不接受任意 `bin` 或 `v数字` 目录。
Foundation 保留 EXE 旁 `release-key.txt` 的版本选择/一致性校验，显式 `--worker-exe` 保持优先；
默认 worker 位于选定根的 `runtime/v<key>/worker/TiaMcp.PlcWorker.<key>.exe`。
引擎更新检查只把选定根下的正式 `runtime/v<key>` 输出认作安装；开发输出保持原有的非安装响应，
正式安装缺交付清单或 `runtime/tools/TiaMcp.Updater.exe` 时报告选定根下的预期路径。

工作台（Studio）的配置页、客户端桥接与更新探测由 P6-38 改用同一规则：`BundleLayout.ResolveWorkbenchRoot` /
`RequireWorkbenchRoot`（显式根、`TIA_MCP_BUNDLE_ROOT`、已知锚点；无效显式根不回退），引擎、bridge 与 adapter 位置取自产品表
（`GetProduct`、`WorkbenchEnginePath`、`WorkbenchBridgePath`）；缺目标版本引擎即拒绝，源码 worktree 拒绝安装更新。
Studio 的共享数据目录副本通过 `TIA_BUNDLE_LAYOUT_STUDIO` 使用 `FindWorkbenchRoot`，采用同一启动选择与 Workbench 锚点；原生路径选择不变。
正式相邻 bridge/adapters 部署保持，`TiaSharedAdapterPaths` 默认 false，仍遵守 G3/J 原生验收边界。
根启动器仍只启动 `runtime/studio/TiaOpenness.exe`。

V21 生态目录查询仍读取两个完整引擎嵌入的 `TiaMcp.V21Ecosystem.json`，磁盘副本缺失不影响查询。
[`reference/v21-ecosystem.json`](../../reference/v21-ecosystem.json) 是可编辑源，修改后须重建引擎。
Python 解释器由 `TIA_MCP_PLC_TOOLS_PYTHON` 覆盖；缺省环境位于用户的 `TiaMcp/ecosystem-python`。
这些生态工具属于 V20/V21，不加入 Foundation。

### 软件自身的数据目录

[`DataLocations.cs`](../../src/Shared/DataLocations.cs) 在进程首次使用时确定一个数据根，并在链接该源码的
程序集之间共享缓存；之后不会因环境变量或目录权限变化重新选择。顺序如下：

1. 非空 `TIA_MCP_DATA_DIRECTORY`：必须为绝对路径；无效值报错，不静默回退。
2. 引擎/Foundation 启动选定根下的 `data`：创建目录，写入并删除小探测文件；成功才采用。未初始化的链接副本先用 `FindRoot`，再用 `FindWorkbenchRoot`，使 Studio 的首次解析也采用 P6-38 的 Workbench 根规则与启动选择，包括显式根与开发测试锚点。
3. 未找到包根或探测失败（例如安装于不可写的 `C:\Program Files`）时，沿用各用途原有的用户目录。

| 数据根下的目录 | 内容 | 无数据根时的原位置 |
|---|---|---|
| `diagnostics` | 原生调用 JSONL 日志 | `%LOCALAPPDATA%\TiaMcp\diagnostics` |
| `leases` | 实例租约 | `%LOCALAPPDATA%\TiaMcp\instance-leases` |
| `config` | `http-v<版本>.json`、`client.json` 等本机配置 | `%LOCALAPPDATA%\TiaPortalMcp` |
| `ui` | `ui.settings` 语言与主题偏好 | `%LOCALAPPDATA%\TiaOpennessStudio` |
| `logs/<releaseKey>` | 引擎/Foundation 共用每进程主日志，另有 HMI 读取与原生导出日志；文件名含 PID、启动 UTC、GUID | `%TEMP%\TiaMcp\logs\<releaseKey>` |
| `logs/studio` | Workbench 崩溃日志，同样按进程命名 | `%TEMP%\TiaMcp\logs\studio` |
| `logs/audit` | 每数据根一条写调用与审批哈希链；不受普通/诊断日志保留影响 | `%LOCALAPPDATA%\TiaMcp\logs\audit` |
| `reports` | 未指定输出目录时的诊断报告 | `%TEMP%\TiaMcpReports` |
| `temp` | 引擎与 Studio 的临时文件、默认 scaffold 和 mock 工程 | `%TEMP%` |

`TIA_MCP_DIAGNOSTICS_DIRECTORY` 仍优先于数据根决定诊断目录，读写使用同一解析逻辑；
`TIA_STUDIO_MOCK_STATE_ROOT` 仍优先覆盖 mock 版本控制状态位置。调用方明确给出的报告或工程目录不变。
临时文件保留原有命名与清理方式，没有新增自动清理策略。

首次使用数据根（含显式覆盖）中的 `config` 或 `ui` 时，仅将缺失的文件从旧用户目录复制过来；
已有目标文件不覆盖，旧文件不移动、不删除。诊断、租约、日志和临时文件不迁移。
客户端自身的配置（`ClientProfiles`、`McpConfigInstaller`、`ConfigCore.ClaudePath`）、Siemens ProgramData 读取、
TIA 默认 `MyDocuments\Automation` 工程目录、URL ACL 与防火墙设置保持原行为。
日志目录不可写时使用表中的用途回退；显式 `TIA_MCP_DATA_DIRECTORY` 不可写立即报错，不静默改写目的地。
无可写日志位置报 `IO_FAILED`，诊断/审计写失败报 `DIAGNOSTIC_WRITE_FAILED`，均包含目的路径，不写入可执行文件旁。
AI 调用面板与保留时间窗口继续读取 `diagnostics/calls-*.jsonl*`；审计页与 `tia audit verify` 分别校验主目录单链及已存在的用户回退单链，报告各自的目录、文件和链内断点；不合并链或重编号。
环境页尾部读取、诊断 ZIP 与 `tia doctor` 使用发布键/studio 日志目录，读者也检查用户回退目录。

宿主启动和运行消息合并写入 `TiaMcpServer-<PID>-<yyyyMMdd-HHmmss-fffffff UTC>-<GUID N>.log`，不再产生 startup 副本。
HMI 读取、原生导出和 Studio 崩溃日志分别以 `TiaMcpServer.hmi-read`、`TiaMcpServer.native-export`、`TiaOpenness.crash` 为前缀。
宿主与 Workbench 启动时检查主目录和 `%TEMP%\TiaMcp\logs` 下全部正式发布键及 `studio` 目录；
固定常量 `PlainLogCopies = 32` 按用途前缀保留最新 32 份（mtime UTC 降序，路径 ordinal 排序打破并列）。
此规则只匹配上述产品前缀与完整 PID/启动时间/GUID 命名，可含一个会话 GUID；不递归、不穿过 reparse point、不碰其他文件。
较旧但仍被打开/锁定的文件及 PID 和启动 UTC 都匹配活动进程的文件保留，因此文件数可能暂时超过 32；进程时间无法检查时也保留。
清理失败每进程只向 stderr/Trace 报一次 `IO_FAILED`，继续启动；普通日志写失败每用途每进程报一次，原操作行为不变。
该文件数量常量独立于下文调用 JSONL 的大小/份数配置，审计链不自动删除。


伴随 Python 默认解释器为 `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`；
显式 `TIA_MCP_PLC_TOOLS_PYTHON` 优先，缺省目录缺失或不可写报 `IO_FAILED` 与具体路径。
不创建安装目录环境，不复制或执行旧 `TiaMcp_Output` 环境；开发者须显式指定解释器或安装脚本的 `-EnvironmentPath`。

私人报告/fixture CLI 要求 `--workspace-root <existing-absolute-directory>`，独立于 `--bundle-root`；
HMI 模板还要求 `--hmi-template-directory <absolute-directory>`，组件目录分析要求具体 `--global-library-probe-json-path`。
`TMP_EXPORT` 等已知夹具布局只在显式工作区内解释；PLC Builder 套件同时接收 fixtureDirectory 与 workspaceRoot，不从夹具反推根。
缺输入返回 `INVALID_ARGUMENT`，CLI 语法退出 64；不猜 cwd、私人模板目录或最新报告。

`runtime/tools/TiaMcp.Updater.exe` 面向 Windows 和 .NET Framework 4.8，不依赖随包 `runtime/dotnet`；启动更新/回滚前将自身和配置复制到系统临时目录，使其可以替换根启动器、运行时文件及 `runtime/tools` 下的自身。更新过程使用 Windows 扩展路径处理长路径，并在操作前检查当前包的引擎、Foundation、worker 和 Workbench 进程；列出 PID 后退出，不会终止这些进程。`-Check` 只检查版本和资产，默认调用执行更新，`-Rollback` 恢复最新备份；从包根手动运行时要传入 `-InstallRoot .`，命令行和运行方式见[配置指南](../getting-started/configuration.md#更新)。

更新器按新包的 `delivery-files.json` 接受运行资源包。备份到 `.previous` 后，使用固定 `legacyCleanup` 规则与旧包
`manifest/release-file-hashes.json` 的交集清理已交付的开发文件；只删除哈希仍匹配的旧文件，保留用户新增或改写的内容。
`plugin/skill/` 和 `third_party/` 中继续交付的 Python 源码保留，不按整目录递归清除。旧 `tools/` 文件、运行文件及 manifest 也仅删除记录中确认且新包不再包含的项。
没有旧完整文件记录时，只从构建记录识别旧运行二进制，不猜测其他文件的所有权。
备份附带本次新包文件收据，回滚先删除备份中不存在且未被用户改写的新文件，再恢复原文件，避免叠加出混合布局。
`data`、`.previous`、`.update`、`TiaMcp_Output` 不参与开发路径清理；数据目录在覆盖和回滚时保留。
下载 ZIP、校验和及展开目录保存在系统临时目录，完成或失败后清理暂存文件；不再受 PowerShell 260 字符路径限制。
`data`、日志与用户新增或修改过的文件在覆盖和回滚时保留。更新器只按旧交付哈希清理退役开发文件，
每次更新记录新文件清单以便安全回滚，并只保留最近两份备份。`data` 已加入根 `.gitignore`。

### 兼容边界与风险

- 阶段 0–5 的路径字符串、异常类型与首行错误语义冻结已结束；P6-37 按上节硬切根变量和资源失败行为。
  `EcosystemFiles.Guidance` 的文档 ID 仍按指南根路径截取生成，不额外规范化。
- 引擎、Foundation 和 CLI 不再触发祖先兼容探测；嵌套暂存缺资源也拒绝借用外层仓库。
  Studio 调用方剩余根探测由 P6-38 处理，完整交付包的重定位仍需独立验收。
- 软件自身的配置、诊断、日志和临时数据按上节优先写入包内；Python 环境独立位于 LocalAppData。
- P6-37 删除 R4–R6 的 `TMP_EXPORT`/cwd 根猜测，并将默认 HMI 模板改为选定根下的 `templates/hmi`。
  P6-39 进一步要求私人工作区与 HMI 模板显式输入，保留明确给出的输出路径。
- P1-07 的 G7 前置阻塞已解除，整体目录重组仍待阶段 4 完成后进行。

### 检查器与离线证明

[`Check-BundleLayout.py`](../../scripts/checks/Check-BundleLayout.py) 不运行 dotnet 或引擎，检查：

- 枚举与字面量代码表一一对应，资源 ID 和路径不重复；路径是普通根相对路径，不含绝对路径、反斜杠、空段、`.` 或 `..`。
- 九项资源在本地存在，文件本身或目录中的文件属于 `git ls-files` 文件集。
- 每项资源及其跟踪子文件都匹配 `delivery-files.json` 的交付规则。
- 每项路径都在 C# `validate-bundle` 实际遍历检查的 `BundleManifestRequirements` 中；两份列表由检查器核对，并非共用一个数据文件。
- C# 5 Launcher 的唯一相对候选与解析器的 Studio 安装锚点、GUI 工程的输出 EXE 名称一致。

仓库模式核对代码表与跟踪文件；包模式从仓库运行校验器，按交付规则检查资源而不要求包内源码。
检查器不证明运行时返回值、二进制完整性或西门子行为；`Check-Repository.py --no-binaries` 同时运行此检查。
布局矩阵及调用方差分测试位于完整引擎的 [BundleLayoutTests.cs](../../tests/Engine/TiaMcpServer.Tests/BundleLayoutTests.cs)、
[EcosystemTests.cs](../../tests/Engine/TiaMcpServer.Tests/EcosystemTests.cs)、
[EngineBundleLayoutTests.cs](../../tests/Engine/TiaMcpServer.Tests/EngineBundleLayoutTests.cs) 和 Studio 的
[Core 布局测试](../../tests/Studio/TiaOpenness.Core.Tests/StudioBundleLayoutTests.cs)、
[GUI 布局测试](../../tests/Studio/TiaOpenness.Gui.Tests/StudioBundleLayoutTests.cs)，覆盖安装、开发输出、worktree、CI、仅 runtime、嵌套暂存、
空格/中文/尾分隔符、缺文件及显式覆盖。
Foundation 的 [BundleRootTests.cs](../../tests/Engine/TiaMcpServer.LegacyHostTests/BundleRootTests.cs)
覆盖六个 release-key、相邻版本文件、默认 worker 及显式 `--worker-exe`。

G7-1 已将 `GetOpennessGuidance`、`GetV21EcosystemCatalog` 和 bin 布局的 `CheckProductUpdate` 加入 P0-06。
G7-3 的仓库外交付包比较保持原始响应一致（质量 PDF 因缺少 ReportLab 未验证）；G7-4 的九项重定位比较只存在时间字段差异。
G7-5 比较了 1,921 份 Studio 重定位结果；G7-6 在 V20/V21 各完成 75 项生态检查（含 HTTP），
证明目录查询不再依赖磁盘 JSON。以上为各实现任务的离线证据，不代表原生验收；任务证据汇总见[重构计划](refactor-plan.md)。

### 新增运行时资源

1. 将交付资源放入受版本管理的包内路径，在 `BundleResource` 与 `ResourcePaths` 增加一项。
   目录资源同步调整 `FindResource` 的目录判断；编译生成的 EXE/DLL 继续由构建与交付清单管理。
2. 同步 `delivery-files.json` 和 C# `BundleManifestRequirements`；核对 `Check-Repository.py` 和 `Package-Release.py` 的必需文件清单，
   按资源用途补齐。清单与源码哈希由对应构建生成器更新，不手工改 manifest 哈希。
3. 调用方复用解析器和版本目录表；需要新输出布局时显式扩充锚点。既有调用方保持覆盖优先级、路径拼写和错误语义，
   不增加新的仓库探测。仅限单个程序集使用的固定数据可按生态目录模式直接嵌入，并保留唯一可编辑源。
4. 补充受影响资源/调用方的布局测试，验证缺文件、显式覆盖与仓库外重定位；运行
   `python scripts/checks/Check-BundleLayout.py --self-test`、`python scripts/checks/Check-BundleLayout.py`、
   `python scripts/checks/Check-Repository.py --no-binaries` 和
   `dotnet run --project build-tools/release -- validate-bundle -Strict -NoBinaries -SkipSourceHashes`。
   C# 变更继续按[验证分层](validation.md)执行受影响套件、构建与兼容快照。

### 任务

| 任务 | 完成内容 | 状态 |
|---|---|---|
| G7-1 | P0-06 加入参考资料工具及 bin 布局下的更新检查基线 | done |
| G7-2 | 资源解析器、布局单元测试与 Python 交付资源检查器 | done |
| G7-3 | Logic 中的 R1 接入解析器，保留既有覆盖与兼容回退 | done |
| G7-4 | 引擎 R2、R3、R7 与 CLI 配置写入接入解析器 | done |
| G7-5 | Studio R11–R14 接入；修复 worktree `.git` 判断，清除死回退并校验 Launcher 路径 | done |
| G7-6 | V21 生态目录直接嵌入 V20/V21 引擎；指南仍从磁盘读取 | done |
| G7-7 | 更新现行布局文档与 CHANGELOG；关闭 G7，解除 P1-07 的 G7 阻塞 | done |

## P2-05：清理与文案语言

### 注释与 `*Leftovers`

- 引擎含产品版本号的注释 221 行（93 个文件），Logic 33 行；旧“Phase N”说法 32 处（与当前重构阶段同名，易混）；维护者原话 13 处；
  中文修复叙事 48 处；迁移墓碑注释 20 处；注释掉的代码约 9 行；与代码矛盾的工具数 4 处。
- 改写规则：删除版本号、维护者归属和旧 Phase 说法；原生行为证据（约 91 行）保留并写明 TIA/PLCSIM 版本、日期和
  [真机验收清单](../reference/real-machine-ledger.md)链接（D-P5-1）；修复叙事改写为不变量；墓碑、注释掉的代码和过时计数直接删除；
  改写时保留原注释的语言（D-P5-6）。生成脚本内容里的“TODO”属于响应字节，不得改动。
- `*Leftovers`：产品文件 6 个、1,289 行，成员分属内核、基础设施、在线、硬件、PLC 数据、Motion/ProDiag、PLC 程序等领域，随阶段 3
  各步骤的纯迁移逐步消解，不单独先改名（D-P5-2）；步骤 15 结束时 `*Leftovers*` 文件数为 0。`engine-decomposition.md` 把
  Step7Leftovers 列为步骤 7 的可选包，与实际内容不符，由 P2-05d 修正。

P3-18 将最后三个产品余项文件按职责拆开，产品与测试均不再使用 `*Leftovers*` 文件名；注释与吞异常基线均为 0。
会话成员分别在 `Portal.HmiOperation.cs`、`Portal.Diagnostics.cs`、`Portal.SessionResolvers.cs`、
`Portal.ObjectIdentity.cs` 和 `Portal.Transactions.cs`。原 Base 校验按硬件实用程序、设备服务对象、对象标识、事务、凭据
归入引擎内的具名 `*Rules` 类；原 Step7 校验按外部源、PLC 表、报警文本列表与 ProDiag 导出归入 Logic 内的具名 `*Rules` 类。
程序集边界不变，原生调用与反射仍由引擎织入覆盖。工具共享实现直接由具名辅助类提供，目录与注册基础设施仍在 `McpServer`；
不再经过迁移期嵌套 `*ToolSupport` 或 HMI 编译转发。

### 用户可见文案语言

- 现状：V21 的 488 条工具描述中 482 条为纯英文，其余 6 条只在引用 TIA 原文或示例名称时含中文；参数描述 2,349 条中 11 条含中文示例值；
  30 个 prompts 全英文；引擎与 Logic 约 100 条消息含中文（另有中文报告、双语表和 CLI 输出）。
- 维护者已决定（2026-10-03）：
  - 阶段 0–5：所有对 MCP 可见的文本冻结（含中文消息与错别字）；**新增文本一律英文**，中文只作为数据出现（TIA 原文、示例名称）；
    中文字面量用检查器约束只减不增。
  - 阶段 6（4.0）：MCP 文本统一为英文，错误加稳定的机器码，使文本不再承担功能；提供旧→新对照表。
  - 不为 MCP 响应引入本地化层（调用方是 AI，本地化会破坏快照与确定性）；本地化只在 Studio（`Loc`）和 CLI doctor 输出。
- 功能性文本（`TryCanonicalizeEnumArgument` 解析的拒绝文本、快照脚本的标记、`Test-LocalStability` 检查的 stderr、FindTools 的关键词
  检索）在阶段 6 引入错误码之前不得改动。

### 任务

| 任务 | 内容 | 证明 |
|---|---|---|
| P2-05a | 注释清点与 MCP 可见中文字面量两个只减不增检查器（复用 `Check-SwallowedExceptions.py` 的词法器），接入仓库检查；语言政策写入 `tool-development.md` | 自检；不改 C# |
| P2-05b | 阶段 3 领域之外只改注释（Logic、Runtime、CLI、Studio、基础设施） | 引擎 EXE SHA-256 不变；其他程序集确定性构建前后一致；去注释比较 0 差异 |
| P2-05c… | 阶段 3 每一步的第一阶段提交（与吞异常原因一起） | 同上 |
| P2-05d | `*Leftovers` 随领域迁移消解，拆分并改名两个 Logic 类 | 阶段 3 标准证据 |
| P2-05e | Studio 剩余硬编码中文迁入 `Loc` | Configuration/Strings/Gui 测试 |
| P2-05z | 统一 MCP 文本并引入错误码 | 阶段 6 |

## 决策记录

| 编号 | 决定 |
|---|---|
| D-G7-1 | 布局用 `openness-shared` 中的代码表，`manifest/package-manifest.json` 为根标记，`Check-BundleLayout.py` 校验 |
| D-G7-2 | 历史阶段 0–5 使用旧根变量；P6-37 硬切为 `--bundle-root` / `TIA_MCP_BUNDLE_ROOT`，旧名不作别名 |
| D-G7-3 | P6-37 已删除引擎/Foundation/CLI 的兼容探测；Studio 收口由 P6-38 执行 |
| D-G7-4 | 开发锚点编入产品，只认枚举出的引擎、Foundation 和测试宿主输出；其他测试布局显式给根 |
| D-G7-5 | `v21-ecosystem.json` 已直接嵌入 V20/V21 引擎；指南仍读取随包文件，改为嵌入前需另做差分证明 |
| D-G7-6 | 软件自身数据优先留在包内，旧用户路径作为回退；P6-39 崩溃日志接 data/logs/studio，Python 默认位于 LocalAppData |
| D-G7-7 | G7-5 已修复 Studio 的 worktree `.git` 判断，文件和目录都禁止安装更新（仅 UI 行为） |
| D-G7-8 | 私有工作区默认值阶段 0–5 不动，阶段 6 删除或改为显式输入 |
| D-P5-1…6 | 见上文“注释与 `*Leftovers`”与“用户可见文案语言”；D-P5-3/5（新文本英文、4.0 统一英文）由维护者决定（2026-10-03） |

## 审计链与调用日志保留（P6-46）

V20/V21 引擎与 Foundation MCP 宿主记录目录中分类为写操作的请求、开始和结束；结束保留 V4 的
`meta.outcome`。预览仍按工具的写分类记录，只读工具不记录。CallTool 目标及批次写项分别记录；未执行项
保留 `rejected-before-operation`，无法读取 V4 结果或调用异常离开边界时记 `unknown`。不记录参数、返回业务内容、
凭据或原生对象。审批 API 提供 granted/denied/timeout 与开关变化，审批流程由 P6-44 接入。

审计文件为 `audit-<首条序号，20 位补零>.jsonl`，每条是 UTF-8、无 BOM、LF 结尾的 JSON 对象。
固定字段为 `approvalEnabled,event,host,index,outcome,planHash,previousHash,processId,release,requestId,tool,utc`，
顺序按字段名的 ordinal 升序；数字十进制，布尔和 null 为 JSON 字面量，字符串使用 System.Text.Json 默认转义，
无缩进或字段间空白，UTC 使用 invariant `O` 格式。SHA-256 对这份完整规范 JSON 的 UTF-8 字节计算，
不包含换行；第一条的 `previousHash` 为 64 个零，其余为上一条的哈希。字段重排或等价 JSON 转义不改变哈希。
10 MiB 轮转后的第一条仍链接上一文件末条；单条大于上限时保持完整行。审计文件不自动删除。

同一数据根的所有宿主/链接副本共同写入 `logs/audit` 的一条链，先以 `FileShare.None` 打开该链的 `.audit.lock`，再读取末条、分配序号、轮转、
追加并 `Flush(true)`，最后关闭锁。校验和读取以只读访问独占同一个锁文件，便于读取只读安装的历史链；缺锁文件时仍需目录允许创建。操作系统在进程退出时释放句柄。锁文件不删除，
避免删除后创建新锁导致并行写入；竞争每 10 ms 重试，30 秒超时报 I/O 错误。成功写入不会交错或覆盖其他进程的行。
若崩溃留下不完整末行，追加拒绝自动修复；校验报告该行，需操作员保留证据后处理。
审计 I/O 失败以 `DIAGNOSTIC_WRITE_FAILED` 写入 stderr 与 Trace，不修改工具结果或重试工程操作；写入成功才具有落盘保证。

`tia audit verify --path <绝对目录>` 输出一份 JSON 报告：`chain,passed,count,breakIndex,file,reason`。
未指定路径时输出主目录及已存在用户回退目录各自的报告数组；每个目录单独从 genesis 校验，序号不合并。
所有链完整退出 0，任一链断链或读取失败退出 3；`breakIndex` 从 1 起，是预期记录位置。
工作台“审计日志”的校验按钮使用同一校验器，显示失败链的目录、文件与第一处断链，切换到该链后允许跳转；通过时列出校验过的目录。
用户可以删除整份日志、截去完整记录组成的尾部，或重写整条链；没有外部可信锚点或签名，
哈希链只检测保留记录的中间修改、缺失和插入，不证明日志完整或事件真实性。空目录和完整行尾部截断可通过校验。

工作台通过 `data/config/journal-retention.settings` 保存 `fileSizeMb` 与 `copies`；允许 1–1024 MiB、1–256 份。
宿主每秒在下一次日志写入时重新读取，配置用独占锁保护并刷新到磁盘。默认每个进程日志流为 **50 MiB × 16 份**（约 800 MiB），
包含活动文件；归档使用 `.1`（最新）到 `.15`，旧 `.previous` 在下一次写入时迁入。
每个进程/链接副本使用含 GUID 的独立文件名，防止 PID 重用或同进程多个程序集同时写一个文件。
调低份数在下一次写入时清理多余归档；超出新大小时轮转，不切断 JSON 行。旧进程的独立日志流保留，容量上限按流计算。

工作日容量假设为 8 小时、平均每秒 10 个原生调用（每次 BEFORE/终态两行）及 5 条其他调用日志记录，
平均每行预算 1 KiB：约 703 MiB/工作日，800 MiB 默认容量约覆盖 9.1 小时；更密集的批量操作可在工作台调高大小或份数。
离线织入诊断夹具的 128 条原生记录平均 711.09 字节、最大 929 字节（含行尾）；1 KiB 是测量后的规划预算。
调用速率和额外记录速率是明确假设，不是原生现场性能测量。
记录更大或速率更高时覆盖时间会缩短，因此服务的 `Coverage` 返回保留文件中实际的首末完整记录时间，
跨进程与归档取最早/最晚边界，忽略中断末行；日志时间按宿主时钟记录。
工作台显示这段时间窗口，P6-45 可复用该值为 AI 调用面板添加标签。
