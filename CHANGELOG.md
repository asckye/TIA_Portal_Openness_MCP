# Change Log

格式遵循 [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)：最新版本在前，日期为 ISO 8601；每个版本的完整说明在 `docs/releases/vX.Y.Z.md`，交付包在 [Releases](https://github.com/asckye/TIA_Portal_Openness_MCP/releases)。版本号从 2.8.0 起按[语义化版本](https://semver.org/lang/zh-CN/)：**MAJOR** = 工具名 / 参数 / 返回形状的不兼容改动或删除工具；**MINOR** = 新工具、新参数、新功能、内部重构；**PATCH** = 修缺陷、改文案 / 文档、只动交付脚本。2.7.x 及之前每版都可能新增工具，未按此规则。

## [Unreleased]

- （未发布的改动写在这里，发版时移到版本标题下。）

## [2.8.1] - 2026-09-21

发布模型改动（PATCH：引擎与工具不变，工具 453、lite 59、离线 2456）。维护者推翻 2.8.0 记下的决定：**不提交 exe，发布 ZIP 由本机 `Release.ps1` 直接上传**；同时“需要清理分支，只保留 master”“英文文档就全部用英文”。详见 [v2.8.1](docs/releases/v2.8.1.md)。

- **二进制不入库**：`.gitignore` 忽略 `runtime/v20/`、`runtime/v21/`、`TiaMcpConfigurator.exe`，索引里的 133 个二进制文件移除（历史保留）；它们由本机 `Build-Release.ps1` 生成，逐文件哈希仍在提交的 `manifest/release-build.json` / `manifest/configurator-build.json`。`Package-Release.py` 取 Git 树 + 本机二进制打包（缺一个或二进制被跟踪都拒绝）；新脚本 `scripts/checks/Verify-ReleaseAsset.py` 证明 ZIP = 提交树的每个文件（字节相同）+ 清单里记录哈希的二进制 + `RELEASE_STATUS.txt` / `release-file-hashes.json` 的源码提交。
- **本机上传**：新脚本 `scripts/build/Publish-Release.ps1`（令牌：`-Token` / `GITHUB_TOKEN` / Git Credential Manager 存的 github.com 凭据）——核对包 = master HEAD = tag → 草稿 Release（正文 = 发布说明 + 包名 / 文件数 / 字节 / 源码提交 / SHA-256）→ 上传 ZIP 与 `.sha256`、逐个回读 `state` / 大小 / `digest`（最多 6 次，草稿上的残留资产先删）→ 发布并置 latest；已发布的 Release 永不改动。`Release.ps1` 改为一次提交 `Release X.Y.Z: <summary>`、打包 + 本地验证、推送、带令牌查 Actions API 等两条工作流、tag、调用 Publish-Release、等 `Verify published release`；`-Resume` 从提交后接着走。`Publish-Release.cjs` 删除。
- **工作流**：`validate-bundle` 在没有二进制的 checkout 上跑（`Check-Repository.py --no-binaries`、`Validate-Bundle.ps1 -Strict -NoBinaries`：清单、版本、启动器语法、源码哈希照查，二进制存在性 / 版本 / 哈希跳过）；`release.yml` 改为 **Verify published release**（`release: published` 触发）：tag = `v` + `delivery.json` 且指向 master HEAD，下载 ZIP + `.sha256` 校验，`Verify-ReleaseAsset.py`，解包后 `Validate-Bundle.ps1 -Strict` + `Check-Repository.py`；不改 Release，红了就出补丁版本。
- **仓库整理**：只保留 `master`——4 个 Dependabot 分支的 action 升级（checkout v7、github-script v9、setup-dotnet v6、setup-python v7）手动合进 master 后删除分支与 `.github/dependabot.yml`，CONTRIBUTING 写明；README、SKILL.md、英文指南里不再有中文（界面按钮用英文写、示例数据英文、本地化错误信息用英文原文并注明、真机记录里的中文工程 / 设备名换成中性占位；SKILL.md 的工具数改为 453 / 59），正文是中文的 `natural-language-recipes.md` 改中文标题。
- 文档：`release-workflow.md`、handoff §4、交接单、CONTRIBUTING、`runtime/README.md`、`repository-layout.md`、`scripts/README.md`、CLAUDE.md 按新模型改写。

## [2.8.0] - 2026-09-21

引擎按族拆分、配置器菜单栏更新、更新器改用 robocopy（工具 453、lite 59 不变；从本版起按语义化版本）。详见 [v2.8.0](docs/releases/v2.8.0.md)。维护者：“开始做 2.8.0”“Update-Engine.ps1 不能做成 UI 吗”“更新做成到菜单栏里”“Zhipu GLM 直接改为 GLM”。

- **引擎拆分**（行为不变，离线 2456 / 形状 V20 2805 / V21 3097 不变）：`Siemens/Portal/Portal.Software.cs`（7,423 行）拆成 `Portal.Software.cs`（查找 + 编译）与 10 个族文件 `Portal.Software.<Family>.cs`（PlcTables / LibrarySeed / TechnologyObjects / HmiDescribe / UnifiedHmi / UnifiedHmiHelpers / HmiExchange / Reflection / CrossReferences / ExternalSources）；`ModelContextProtocol/Tools/McpServer.PlcSoftware.cs`（4,021 行）拆成 `McpServer.PlcSoftware.cs`（GetSoftwareInfo / DescribeObjectProperty / CompileSoftware / GetSoftwareTree）与 11 个族文件 `McpServer.PlcSoftware.<Family>.cs`；`Program.ReportBuilders.cs` / `Program.CliProbes.cs` 移到 `Cli/`。逐行搬迁、每行只落一个文件（拆分脚本校验覆盖），`UnifiedScriptSyntaxCheckTests` 改读新文件。
- **配置器菜单栏**：新增“更新”菜单（引擎版本与包名、检查结果、检查更新、更新引擎…、打开 GitHub Releases）和“帮助”菜单（所选客户端的使用说明、项目主页、关于）。启动时后台联网比对 GitHub 最新版（API 限流时读发布页跳转），有新版本时菜单标题变为“更新 · 有新版本 X”。“更新引擎…”先确认本窗口没启动 MCP、机器上没有 TiaMcpServer.exe 在跑（列出 pid，绝不代杀）、更新器存在、不是源码仓库（有 `.git` 就禁用），然后关闭本程序、在新 PowerShell 窗口里运行 `Update-Engine.ps1 -InstallRoot <目录> -WaitForPid <本程序 pid> -RelaunchConfigurator`，完成或失败后自动重新打开配置器；页面本身不变。新文件 `UpdateCheck.cs`（版本比较、release JSON 解析、发布页 tag、启动参数），配置器测试 111 项。
- **更新器**：`-WaitForPid` / `-RelaunchConfigurator` 两个新参数；所有目录复制 / 删除改走 robocopy，**修了长路径缺陷**——Windows PowerShell 的 `Expand-Archive` / `Copy-Item` / `Remove-Item` 在 260 字符处失败，交付包最深文件在根下 109 字符，加上 `.previous\<包名>\` 后虚拟机现在的安装目录已到 250、宿主机临时目录直接失败（真机复现）；现在解压到 `%TEMP%\tia-mcp-update-<pid>`（只对这一步检查长度），备份 / 覆盖 / 回滚 / 清理都用 robocopy；脚本级 `trap` 保证任何意外错误也打印 FAIL 并重开配置器。宿主机上对交付包副本完整跑过更新（2.7.61 → 2.7.62，备份里 267 字符的文件存在）与回滚。
- **客户端卡片**：Zhipu GLM → **GLM**。
- **决定**：继续把两个引擎 exe 与配置器 exe 提交进仓库、发布走本机 `Release.ps1` + tag 触发的发布工作流；**不用自托管 runner**（仓库公开，托管 runner 编不了引擎——Openness NuGet 只有 targets、PublicAPI 不可分发；自托管 runner 要把维护者机器暴露给公开仓库的工作流）。

## [2.7.62] - 2026-09-21

更新器只保留在线路径，配置器卡片一律英文名（引擎同版本重建，工具 453、lite 59 不变）。详见 [v2.7.62](docs/releases/v2.7.62.md)。维护者：离线更新可以取消、不需要了；AI 客户端的名称都用英文。

- **去掉离线更新**：`scripts/operations/Update-Engine.ps1` 不再有 `-ZipPath` / `-SkipHashCheck`；只从 GitHub Release 在线下载 ZIP + `.sha256`（没有 `.sha256` 资产就拒绝），github.com 不通时直接失败并说明原因。`CheckForUpdate` 的 `steps` 由四步改为三步（停引擎 → 在线更新 → 重启验证），描述与网络失败提示不再提离线包；`UpdateLogic.HowToUpdate` 同步，离线测试改为断言无 `-ZipPath`。README、`scripts/README.md`、交接单同步。
- **客户端卡片英文名**：通义千问 → **Qwen**（写 Qwen Code）、腾讯元宝 → **Yuanbao**（写 CodeBuddy Code）、智谱清言 → **Zhipu GLM**（写 OpenCode）、千问工作助理 → **Qwen Agent**（种类 `桌面` → `Desktop`，副标题不再重复品牌）；`ClientProfiles` 新增测试断言每张卡片的名称与种类都是 ASCII。配置器隔离测试 96 项通过。

## [2.7.61] - 2026-09-21

配置器 2.7.61（引擎同版本重建，工具 453 不变）。详见 [v2.7.61](docs/releases/v2.7.61.md)。维护者：配置文件的写入要能在不同电脑上用，要能检测 AI 客户端装在哪、再写进去——**所有客户端都一样**。

- **本机客户端检测**：`TiaMcpConfigurator.exe` 启动时逐张卡片检测客户端痕迹（要写的配置文件 / 目录、PATH 上的可执行文件含 npm 的 `.cmd`、`%LOCALAPPDATA%\Programs` 等已知安装目录、控制面板卸载项 HKCU / HKLM / WOW6432Node），卡片副标题显示“已检测 / 未检测到”，悬停显示证据与写入路径，日志列全部结果，默认选中第一张检测到的卡片；未检测到的仍可写入。
- **写入位置按当前用户解析**（原本已如此，本版补齐并写进文档）：`%USERPROFILE%` / `%APPDATA%` / `CODEX_HOME` / `KIMI_CODE_HOME`；VS Code 本机只有 Insiders 时写 `Code - Insiders\User\mcp.json`。同一个 EXE 拷到任何电脑都写到那台电脑的正确位置。
- **新卡片“千问工作助理”**（qwen-agent 桌面应用）：写 `%USERPROFILE%\.qwen-agent\mcp.json`，条目 `url` + `headers.Authorization: Bearer <密钥>`（静态令牌，不走 OAuth，无 `type` 字段）；提示必须完全退出进程再打开、项目级文件位置。12 张卡片、10 种格式；配置器隔离测试 95 项通过。

## [2.7.60] - 2026-09-21

引擎 2.7.60.0（V20/V21 均重建），工具 453 不变。详见 [v2.7.60](docs/releases/v2.7.60.md)。2.7.59 真机发现的派生示例缺陷。

- **派生示例的占位符规则重排**（`ToolExamples.PlaceholderValue`）：`*Json` 参数先按名字给 `[]` / `{}`（数组类名字不分大小写：`culturesJson`、`namesJson`……），不再从描述里的 `e.g. [\"PLC_1\"]` 截出 `"["`；`*Path` 参数区分主机路径（`filePath` / `importPath` / `exportPath` / `archivePath` / `*FilePath` / 目录……→ `C:\Temp\...`）与工程内对象路径（`chartPath` / `tablePath` / `typePath` / `rulePath`……→ `<Folder/Name>`）；`e.g.` 值只在是纯值时采用；占位符里的 `<>` 不再被转义成 `\u003C`。
- 2.7.59 真机：`ManageDccChart action:"bogus"` 的拒绝带 `preflight.allowedValues.action`（10 个取值）通过；`GetAuthoringGuide.topic` 的 schema `enum` 到位，引擎对带空格的值 Trim 后照常回答。
- 离线 2456（+3）。

## [2.7.59] - 2026-09-21

引擎 2.7.59.0（V20/V21 均重建），工具 453 不变，默认 lite 59。详见 [v2.7.59](docs/releases/v2.7.59.md)。参数描述批次 2：**每个参数都有描述了**。

- **498 个参数补上 `[Description]`**（36 个工具文件）：155 个枚举型参数写明精确取值（`action` / `kind` / `category` / `unitKind` / `copyMode` / `importOption` / `telegramType` / `resetMode`……，取值从各 Logic 类的校验数组 / 字面量 / 拒绝文案里追溯，按 Portal 方法 → Logic 类逐层解析并按到达的类消歧，追不到就不写、不猜），343 个按名称人工写就（`typeIdentifier`、`chartName`、`pinName`、`sequenceIndex`、`archivePath`、`leftPath` / `rightPath`、`numbersJson`、`hostUrl`……）。schema `enum` 提示 67 → **185**（51 → 119 个工具）。
- 无自身描述的参数 1392 → 894，且 894 个**全部**由词汇表覆盖——`tools/list` 与 `FindTools` 里没有一个参数再是空描述；`callDiscipline` 门禁继续只降不升。
- 修 `ExtractPlcBlockMetrics.path` 的文案（文件 / 目录路径，不是对象路径）；`Check-DeadToolReferences` 允许名单加三个 Openness 枚举值（`ReadOnly`、`DeleteUnusedTypes`、`SetOnlyHigherUpdatedVersionAsDefault`）。
- 离线 2453 不变（描述不改逻辑）；形状检查不变。

## [2.7.58] - 2026-09-21

引擎 2.7.58.0（V20/V21 均重建），工具 **453**（+`GetRecipe`），默认 lite 59。详见 [v2.7.58](docs/releases/v2.7.58.md)。维护者 2026-09-21 明确：目标是**规范 AI 对所有工具的调用、不再试错**（不是教它写程序）——本版全部机制由引擎自动执行、对 453 个工具都生效。

- **参数合法值进 `inputSchema`（全部工具）**：注册时把每个参数描述里的文档备选值（`read | create | delete`、`a/b/c`、`value (None, Override)`）写成 `enum`，C# 默认值写成 `default`，人工示例里的值写成 `examples`；会校验 schema 的客户端在发出前就拒绝无效值，任何客户端都把精确选项给模型看。解析规则只认参数自己那句话里独立成段的列表（`kind=globaldb|fc`、`group/folder path`、引号里的正则、后一句的括号列表都不算——真机上曾误判 `password`、`promptAnswersJson`、`folderPath`），本版 67 个 enum 提示落在 51 个工具上，逐条核对过。
- **失败自动预检（全部工具）**：任何调用失败（`isError` 或 `meta.success=false`，经 `CallTool` 时看内层结果）时，引擎把 `meta.preflight` 附进响应——缺参 / 未知与大小写错的参数名（给最接近的名字）/ 类型 / 不在文档备选值里（`allowedValues`）/ 未满足的前提 / 一条示例 / 下一步。AI 拿到失败的同时拿到改法，不必再调 `PreflightToolCall`，成功响应一字不改。
- **参数词汇表 `ParameterVocabulary`**：2204 个参数里 1488 个没有自己的 `[Description]`（213 个工具）；同名参数在全表意思相同（`dryRun`、`softwarePath`、`devicePathJson`、`offset`、`limit`、`confirm*`……），词汇表给出 79 个通用描述，注入没有描述的 schema 属性、预检与派生示例；本版另手写 14 个常用工具的 96 个参数描述（`ManagePlcProtection` / `ManagePlcTableEntries` / `ManageHardwareObject` / `ManagePlcTagDefinition` / `ManagePlcExternalSources` / `UploadStationFromPlc` / `ScanAccessibleDevices` / `ReadTransferRoutes` / `CompileDevice` / `ReadPortalInfo` / `ManageCommunicationConnection` / `ReadCommunicationConnections` / `ManagePlcAlarmTextList` / `ManageProjectLanguage`）。无自身描述的参数 1488 → 1392，其中 1002 个由词汇表覆盖；`manifest/tools-list.json` 新增 `callDiscipline` 统计，构建门禁止该数字回升。
- **示例覆盖 453 个工具**：80 条人工示例之外，其余按签名派生骨架（必填参数 + 描述里的 `e.g.` 值 / 首个备选值 / 按名推断的占位符，标注 derived），用于 `FindTools`、`CallTool` 缺参拒绝、`PreflightToolCall` 与自动预检；列出工具的描述仍只附人工示例（不增 `tools/list` 体积）。
- **`GetRecipe(topic)`**：12 条真机跑通的多步序列（connect-project、plc-scl-block、plc-s7dcl-import、plc-builder、watch-table、cpu-protection、download-plcsim、plcsim-test、hardware-device、hmi-unified-screen、export-import-block、large-response），每步是精确的工具名 + 参数 + 预期结果；构建时按签名校验每一步。服务器指令 / `Bootstrap` 规则 / `SKILL.md` 指向它。
- 离线 2453（+97：`CallDisciplineTests`）。

## [2.7.57] - 2026-09-21

引擎 2.7.57.0（V20/V21 均重建），工具 **452**（+`PreflightToolCall`、`CheckForUpdate`），默认 lite 58 项（两个新工具都在 L0）。详见 [v2.7.57](docs/releases/v2.7.57.md)。维护者 2026-09-21 定下的第二件事：更新器 + 规范 AI 调用；2.7.56 真机通过（`Connect` 多进程按名附加、不自启）。

- **更新器 `scripts/operations/Update-Engine.ps1`**（随交付包）：在解压好的交付包里就地更新——读 `manifest/delivery.json` 的当前版本；**引擎（`TiaMcpServer.exe` / `TiaMcpConfigurator.exe`）在运行就拒绝并列出 pid，从不杀进程**（维护者要求"先关再更"，不做热更新）；从 GitHub Releases 取最新版（API 被限流时改读发布页的重定向与 expanded_assets 片段），或 `-ZipPath` 离线更新；下载 ZIP + `.sha256` 并校验（无 sidecar 需显式 `-SkipHashCheck`）；备份当前安装到 `.previous\<包名>\`（留两份），整体替换 `runtime\` / `manifest\`、覆盖其余文件；`-Check` 只比版本，`-Rollback` 换回上一版，`-Version vX.Y.Z` 指定版本，`-Force` 重装。本机在 2.7.55 的解压包上验证：离线更新到 2.7.56、回滚到 2.7.55、`-Check` 走发布页回退、伪 `TiaMcpServer.exe` 在跑时拒绝。
- **`CheckForUpdate`**（只读，`[L0][Diagnostics][SESSION]`）：比较引擎版本与最新 Release，回报 ZIP 名 / 大小 / 下载地址 / `.sha256`、`installRoot`、`updaterScript` 与四步更新说明；API 限流时自动改读发布页；不改任何文件。
- **`PreflightToolCall(name, argumentsJson)`**（`[L0][Meta][SESSION]`）：**不执行**地检查一次计划中的调用——工具名解析（打错字给候选）、按真实签名校验参数（缺必填 / 未知或大小写错的参数名并给最接近的名字 / 类型不对 / 不在文档备选值里 / `CallTool` 会做的自动转换）、操作类别与注意事项、`dryRun` / `confirm*` 标志的实际效果、会话前提（是否已连接、是否已绑工程）、一条示例；`Meta.ok` = 参数能绑定，`Meta.success` = 还满足前提。纯逻辑 `PreflightLogic`（备选值解析支持 `a | b`、`a/b`、`(a, b)` 三种写法，排除 `e.g.` 与带连字符的散文）。
- **工具示例表 `ToolExamples`**（80 条，常用与真机验证过的工具）：附在每个列出工具的协议描述末尾（`Example: {...} (说明).`，lite 档 58 个里 43 个有）、`FindTools` 结果、`CallTool` 缺参拒绝与 `PreflightToolCall`；`Generate-ToolsListFromAssembly.ps1` 在构建时按真实签名校验每条示例（参数名精确、必填齐全、工具存在），`manifest/tools-list.json` 每行新增 `example`。
- 服务器指令与 `Bootstrap` 规则新增 "PLAN, DO NOT PROBE"：调用不熟悉的工具之前、用户纠正之后先 `PreflightToolCall`；`SKILL.md` 同步。
- `Connect`：名字不存在时消息末尾不再双句号。离线 2356（+138）。

## [2.7.56] - 2026-09-21

引擎 2.7.56.0（V20/V21 均重建），工具 450 不变，默认 lite 56 项不变。详见 [v2.7.56](docs/releases/v2.7.56.md)。2.7.55 部署后的收口结果与 `Connect` 的多进程修复。

- **2.7.55 真机**：`RunPlcSimAdvancedTestScenario singleStep` PASSED 8/8、4/4——`operatingModeApplied SingleStep_C`、`cyclesStepped 5`（71 ms）、`Cycles` 304→309 恰好 +5、`Start=false` 后 2 周期不增，**在线族收口**；`SaveProject` 通过（`项目1` 含 `MCP_STD` + 程序、两台 CPU 的保护设置、监控表两行）。
- **`Connect` 多进程缺陷（真机）**：引擎重启后调用 `Connect` 时虚拟机开着两个 TIA 进程（维护者的 `AutomaticDipCoatingMachine` pid 15100 与 `项目1` pid 4840），两次 30 s 附加都没答复，旧代码于是**启动了第三个空 TIA 实例**（pid 15748）并绑在上面；MCP 客户端早已超时，`GetState` 显示 `project '-'`，`AttachToOpenProject {projectName:"项目1"}` 才换回 4840。修：`ConnectPortal(projectName, allowStart, info)`——有进程时只附加：`projectName` 命中的进程优先（命中即停，不再逐个探测），否则第一个有工程 / 会话的，再否则第一个可附加的；全部附不上 → `PortalException` 逐进程说明（超时毫秒 / 异常文案）并提示"TIA 可能正弹 Openness 访问对话框 / 实例忙 / 重试 / `ListPortalProcessProjects` / `allowStart=true` 或 `ConnectIsolated`"，**不自启**；只有 `GetProcesses()` 为空（或 `allowStart=true` 且无可附加）才 `new TiaPortal`。每进程等待 20 s、总预算 45 s。`Connect` 工具新增 `projectName`（命中时同时记为期望工程）与 `allowStart`（默认 false），`Meta` 回报 `processCount` / `candidates` / `boundProcessId` / `startedNew` / `attachElapsedMs` / `warning`。纯逻辑 `ConnectLogic`（离线 +7 = 2218）。空实例仍没有工具能关（引擎不杀别人的进程），要在 TIA 机器上手动关。

## [2.7.55] - 2026-09-21

引擎 2.7.55.0（V20/V21 均重建），工具 450 不变，默认 lite 56 项不变。详见 [v2.7.55](docs/releases/v2.7.55.md)。2.7.54 部署后的在线族收尾。

- **2.7.54 真机**：`RunPlcSimAdvancedTestScenario {mode:"singleStep"}` 的模式切换生效（`operatingModeApplied SingleStep_CP`，写 / 断言步之间实例 `Freeze`），Running 断言通过，但 `{cycles:5}` 后 `Cycles` 只从 303 到 304、整个场景 44 ms——手册 "SingleStep operating modes"：`RunToNextSyncPoint()` 只是解除冻结并立即返回，实例自己跑到下一个同步点才再次 `Freeze`，连发 5 次里 4 次落在还在跑的实例上。`UploadStationFromPlc {targetIpAddress:"192.168.0.1", pgPcInterface:"PLCSIM"}`：接口去重生效（PN/IE `PLCSIM #1`；工程级提供者的三个模式是 MPI / PROFIBUS / PN/IE），项目级扫描把 Softbus 实例列为 `192.168.0.1`（虽已配置 192.168.0.3），预览通过（地址在 PLCSIM 接口上创建），真跑被 TIA 拒 "The selected object cannot be uploaded from the device."——从 PLCSIM Advanced 实例上载站是 TIA 侧不支持，设备数仍 5。`GoOnline MCP_PLC` 现在回报 "Connection to device cannot be established. Online: The connection to the target module cannot be established." 与 `onlineStateAfter Offline`（实例已改为 MCP_STD / 192.168.0.3，预期）。
- **修单步场景**：`PlcSimAdvancedChannel.StepCycles` 每个周期先等 `OperatingState == Freeze`、`RunToNextSyncPoint()`、再等 `Freeze`（每周期 5 s 预算、2 ms 轮询；超时把步标失败并写明第几个同步点没到），`cyclesStepped` / `waitedMs` / `stateAfterSteps` 回报；`singleStep` 改为优先 `SingleStep_C`（只在周期控制点冻结——每周期一个同步点，`cycles:N` 恰好 N 个周期），其次 `SingleStep_CP`（读过程映像前多一个同步点）、`SingleStep`。离线 +1（2211）。

## [2.7.54] - 2026-09-21

引擎 2.7.54.0（V20/V21 均重建），工具 450 不变，默认 lite 56 项不变。详见 [v2.7.54](docs/releases/v2.7.54.md)。2.7.53 部署后**在线族在 PLCSIM Advanced 上全链走通**，三处小修。

- **2.7.53 真机通过**：`ManagePlcProtection read` 报 `MCP_PLC` `NoAccess` / `WithoutPassword`；`setAccessLevel FullAccessIncludingFailsafe` 与 `protectMasterSecret` 读回一致；`CompileDevice MCP_PLC` 0 错 3 警。F-CPU 的 `DownloadToPlc` 被 TIA 拒 "Loading or overloading fail-safe data in Openness is not permitted."——Openness 不能下载故障安全数据，是官方规则，`MCP_PLC` 上的在线族到此为止。按维护者规则新建标准 CPU **`MCP_STD`**（CPU 1515-2 PN V2.9，`AddDeviceWithFallback` + `ConnectDeviceNodesToProfinetSubnet MCP_PN`，X1 192.168.0.3；TIA V21 新建默认同样 `NoAccess` + `WithoutPassword`）→ `setAccessLevel FullAccess` + `protectMasterSecret` → `CompileDevice` 0 错 → `DownloadToPlc {pgPcInterface:"PLCSIM", targetIpAddress:"192.168.0.1", masterSecretPassword}` **Success**（首次走 created 地址，`PlcMasterSecretPassword` 提示由 `masterSecretPassword` 应答；实例变成 CPU1515 "MCP_STD" 192.168.0.3 RUN）→ `GoOnline` **Online** → `GetOnlineState` Online → `CompareSoftwareToOnline` 0 差异 → SCL 外部源生成 `MCP_SimDB` / `MCP_SimLogic` / `Main`（`WritePlcSclSourceFile` + `ManagePlcExternalSources createFromFile / generateBlocks`；SCL 里 `Counter` 是保留字）→ 再次下载 Success（子网路由）→ `ReadPlcSimAdvancedTags` 列出 5 个 DB 标签并读值 → `WritePlcSimAdvancedTags` `Speed 12.5` 写回读 → `RunPlcSimAdvancedTestScenario`（default 模式）**PASSED** 6/6、2/2 断言 → `GoOffline`。`ReadPlcBlockFingerprints` 在 1515-2 PN V2.9 上同样 `FingerprintDataProvider` 为 null（TIA 侧）。
- **修 `RunPlcSimAdvancedTestScenario singleStep`**：8.0 API 的 `EOperatingMode` 没有 `SingleStep`，只有 `SingleStep_C / _CT / _P / _CP / _CPT / _Bus` 与 `TimespanSynchronized_*`（手册 "EOperatingMode"）；`PlcSimAdvancedLogic.OperatingModeCandidates` 先试 `SingleStep_CP`（循环程序 + 过程映像，即旧的 SingleStep）再 `SingleStep_C` 再 `SingleStep`，`data.operatingModeApplied` 回报实际名。离线 +2（2210）。
- **修 `UploadStationFromPlc` 的接口歧义**：Softbus 下工程级 `StationUploadProvider.Configuration` 把 "PLCSIM (#1)" 按连接模式列了三遍（PN/IE / PROFIBUS / MPI），精确名匹配到 3 个被当成歧义拒绝；同名同号视为同一块网卡，PN/IE 模式优先，歧义信息带模式名。
- **`GoOnline` 失败文案**：`EngineeringTargetInvocationException` 外层只有 "Error when calling method 'GoOnline'"，现在把内层异常链（` <- ` 连接）与 TIA 当前 `OnlineProvider.State` 一起回报（`State` 用 TIA 的值，`Incompatible` 附"先下载"提示）。

## [2.7.53] - 2026-09-21

引擎 2.7.53.0（V20/V21 均重建），**工具 450**（新增 `ManagePlcProtection`、`CompileDevice`），默认 lite 56 项不变。详见 [v2.7.53](docs/releases/v2.7.53.md)。2.7.52 部署后的真机结果：在线族过了 TLS，卡在 F-CPU V2.9 的安全设置——本版补工具。

- **2.7.52 真机**：`GoOnline {ipAddress:"192.168.0.1", pgPcInterface:"PLCSIM"}` 的 `meta.tlsVerification` 记录 `plcName MCP_PLC`、`verificationInfo "certificate not matching"`、`NonVerified → Trusted`——TLS 提示确实经 `OnlineLegitimation` 来、答完 TIA 记住（第二次不再问）；`GoOnline` 仍抛无正文的异常，但 `GetOnlineState` 报 `Incompatible`（"online but firmware/config mismatch"）：连接已建立，只是 PLCSIM 实例还没下载过。`DownloadToPlc` 同路由报 "硬件配置编译完成，但出现错误"——用反射 `Device.GetService<ICompilable>().Compile()` 看到 3 个错误：访问级别高于"完全访问"却没设完全访问密码（`PlcProtectionAccessLevel = NoAccess`）、"Password for confidential PLC configuration data is not configured"、"The PLC communication certificate cannot be configured without the password"。这两项设置此前没有任何工具能改，反射桥也传不了枚举 / `SecureString`。
- **新增 `ManagePlcProtection`**（官方页 "Access level setting" / "Managing PLC Master Secret in PLCs"）：CPU 设备项上的 `PlcAccessLevelProvider`（`read` 回报 `PlcProtectionAccessLevel`；`setAccessLevel` FullAccess / ReadAccess / HMIAccess / NoAccess / FullAccessIncludingFailsafe；`setAccessPassword` / `resetAccessPassword`，只对比所选级别宽松的级别）与 `PlcMasterSecretConfigurator`（`read` 回报 `MasterSecretConfiguration` None / WithoutPassword / WithPassword / WithPasswordAllDataProtection；`protectMasterSecret` / `changeMasterSecret` / `unprotectMasterSecret` / `resetMasterSecret`；V21 另有 `protectAllConfiguration` / `unprotectAllConfiguration`），`itemPathJson []` 自动找站的 CPU 项，密码走 `SecureString` 不回显，改后读回状态；默认预览，真跑要 `dryRun=false` + `confirmChange=true`。纯逻辑 `PlcProtectionLogic`（离线 +9 = 2208）。
- **新增 `CompileDevice`**：设备（或设备项）的 `ICompilable.Compile()` 硬件编译，诊断按 `CollectCompilerMessages` 摊平（errors / warnings / nodes）——`DownloadToPlc` 之前 TIA 跑的就是它，此前只有软件编译工具。
- **反射桥**：`InvokeObject` / `InvokeService` 的参数绑定支持枚举名（`Enum.Parse`，不分大小写）与 `SecureString`（从字符串），以后类似缺口可以先用桥接顶上。
- 形状检查 V20 2805 / V21 3097（`MasterSecretConfiguration.WithPasswordAllDataProtection` 与三个 `*AllPlcConfiguration` 方法是 V21 独有）。

## [2.7.52] - 2026-09-21

引擎 2.7.52.0（V20/V21 均重建），工具 448 不变，默认 lite 56 项不变。详见 [v2.7.52](docs/releases/v2.7.52.md)。2.7.51 部署后的真机结果与在线族的 TLS 信任修复。

- **2.7.51 真机通过**：`SetWatchTableModifyValue` 绝对地址行 appended（TIA 定 `DisplayFormat` Bool）、符号行 updated（保留 `%I0.0`），`readbackVerified` 都 true，`ManagePlcTableEntries read` 核对 2 行——`ModifyIntention` 读回仍 false：TIA 不按 `ModifyValue` 推导，它是行的"修改"勾选框且 Openness 只读，经 API 只能预置值和触发器。`ReadPlcSimAdvancedInstances` 报 `api.networkMode=TCPIPSingleAdapter`、`managerMembers='SimulationRuntimeManager.NetworkMode {get;set}'`；`powerOff → unregister → register … Softbus` 经全局网络模式通过（`TCPIPSingleAdapter → Softbus`，实例读回 Softbus，`powerOn` 后 `controllerIP 192.168.0.1`）；`ReadTransferRoutes` 只剩 PC 接口 "PLCSIM"（两块物理网卡不再列出），`CheckDownloadReadiness` Ready，`ScanAccessibleDevices` 在 PLCSIM 接口上看到 `S7-1500 CPU:192.168.0.1`——PG 侧不再需要网卡 IP。
- **在线族的真正阻塞（真机 + 官方页）**：`GoOnline {ipAddress:"192.168.0.1", pgPcInterface:"PLCSIM"}` 报 "Connection to device cannot be established. The device is not trusted. Please check the certificate."，`DownloadToPlc` 同路由报 "连接到模块 MCP_PLC 失败"——S7-1500 FW ≥ 2.9（含 PLCSIM Advanced）首次连接会在 `ConnectionConfiguration.OnlineLegitimation` 上抛 `TlsVerificationConfiguration`，官方答法是 `CurrentSelection = Trusted`；引擎的处理器只在给了密码时才订阅，所以提示从没被应答。现在 `GoOnline` / `DownloadToPlc` / `UploadStationFromPlc` / `UploadDeviceParameters` / `ReadPlcBlockFingerprints` 总是订阅 `OnlineLegitimation`，新参数 `trustDeviceCertificate`（默认 true）把提示答成 `Trusted`（与 TIA 界面里的同一个提示；false 时 TIA 拒绝连接），`Meta.tlsVerification` 记录 `plcName` / `verificationInfo` / 前后选择。纯逻辑 `BaseLeftoversLogic.TlsSelectionToApply`（离线 +2 = 2199）。
- **顺手补齐**：`GoOnline` 的响应现在带 Meta（`success` / `route` / `tlsVerification` / `error`）；`SetWatchTableModifyValue`、`CheckDownloadReadiness`、`DownloadToPlc` 的 Meta 带 `success`（此前桥接报 `operationStatus unknown`）；`ManagePlcSimAdvancedInstance` 的 `data.api.networkMode` 改为动作之后的值。

## [2.7.51] - 2026-09-21

引擎 2.7.51.0（V20/V21 均重建），工具 448 不变，默认 lite 56 项不变。详见 [v2.7.51](docs/releases/v2.7.51.md)。2.7.50 部署后的真机结果与三处再修。

- **2.7.50 真机通过**：`ManagePlcTableEntries deleteTable` 把根级的 `MCP_WT_1` … `MCP_WT_5` 逐张删掉并读回缺席（`GetPlcWatchTables` 只剩 `MCP_W/MCP_WT`），工程已保存；`ManagePlcSimAdvancedInstance register / powerOn` 与 `ScanAccessibleDevices`（虚拟网卡上按 MAC 看到 `MCP_SIM`）通过；`ReadPlcSimAdvancedInstances memberFilter` 列出了实例对象的真实成员。
- **监控表行（真机）**：SimaticML 往返的 `Import(Override)` 被 TIA 拒 "Cannot update the 'PlcWatchTableEntry' object … 'set_ModifyIntention' is not supported … The property 'ModifyIntention' is read-only"——`ModifyIntention` 由 TIA 自己按 `ModifyValue` 推导，不能写。`WatchTableEntryXml.Upsert` 不再写它，并在导入前把导出带出来的 `ModifyIntention` 从每一行剥掉（`StripReadOnlyAttributes`）。离线新增 1 项。
- **PLCSIM Advanced 6+（真机 + 手册）**：8.0 API 的 `CommunicationInterface` 在实例类与 `IInstance` 上都只有 getter，也没有任何 `Set…` 方法（`memberFilter` 证实）；手册 "Interfaces for IInstances" 明说 "This property is now read-only. To set the network mode, refer to the NetworkMode section"——接口选择是全局的 `SimulationRuntimeManager.NetworkMode`（`ENetworkMode` `TCPIPMultipleAdapter` / `TCPIPSingleAdapter` / `Softbus`，有实例在跑时 API 以 `InstanceAlreadyRunning` 拒绝）。`communicationInterface` 现在先试实例 setter（PLCSIM Advanced ≤ 5），没有就映射到管理器网络模式（`Softbus` → `Softbus`，`TCPIP` 保留当前 TCPIP 变体、否则 `TCPIPMultipleAdapter`，`ENetworkMode` 名照收），`data.communicationInterfaceRoute` 回报路线与前后模式；`ReadPlcSimAdvancedInstances` 回报 `api.networkMode`，`memberFilter` 同时列出 `managerMembers`。离线新增 5 项（2197）。
- **站上载（真机 + 官方页）**：`UploadStationFromPlc {targetIpAddress:"02-C0-A8-00-C8-00"}` 在虚拟网卡上 `ConfigurationAddressComposition.Create` 报 "does not specify a valid address"——官方页只用 IP 建地址，MAC 不是合法目标；未下载过的 PLCSIM 实例 IP 为 0.0.0.0，先下载才有 IP。拒绝信息与描述现在明说只收 IP。
- **仍待环境**：`DownloadToPlc` / `GoOnline` / `CompareSoftwareToOnline` 等在线族需要 PG 侧——虚拟网卡配 192.168.0.x/24，或部署本版后 `register … communicationInterface:"Softbus"` 让 TIA 走 "PLCSIM" 接口。

## [2.7.50] - 2026-09-21

引擎 2.7.50.0（V20/V21 均重建），工具 448 不变，默认 lite 56 项不变。详见 [v2.7.50](docs/releases/v2.7.50.md)。2.7.49 部署后的真机结果与两处再修。

- **2.7.49 真机**：`DownloadToPlc` / `GoOnline` 的路由选择通过（`-> address 192.168.0.1 (subnet MCP_PN)`），TIA 真正发起下载 / 连接，失败在 PG 侧（虚拟网卡 "Siemens PLCSIM Virtual Ethernet Adapter" 没有 IP："连接到模块 MCP_PLC 失败" / "The connection partner is not responding"）——环境项，给网卡配 192.168.0.x 后重跑；`GetTechnologyObjects` 递归生效（MCP_TO 里的 PID 列出）；`WritePlcSimAdvancedTags` / `RunPlcSimAdvancedTestScenario` 的失败如实回报；powerOff 文案正确。
- **监控表条目（真机 + PublicAPI）**：2.7.49 的 `Entries.Create()` + `SetAttribute` 也不行——`Create()` 只建注释行（`PlcTableCommentEntry`），`Address` / `Name` / `ModifyValue` 对它都 "not supported"；类型化 API 根本建不了带变量的行。而且旧代码只在根级找表，`MCP_WT` 在 `MCP_W` 文件夹里，于是每次调用都新建一张（真机留下 `MCP_WT_1` … `MCP_WT_5`）。`SetWatchTableModifyValue` 现在走官方 SimaticML 往返：`PlcWatchTable.Export` → XML 里加 / 改 `PlcWatchTableEntry` 行（绝对地址进 `Address`，符号按 TIA 的写法逐段加引号进 `Name`）→ `PlcWatchTableComposition.Import(ImportOptions.Override)` 回它自己的组 → 类型化读回（`meta.after` / `readbackVerified`）；表按组路径或全树查找，多处同名拒绝。`ManagePlcTableEntries` 新增 `deleteTable`（此前没有任何工具能删监控表）。离线新增 12 项测试（2191）。
- **PLCSIM Advanced**：`register … communicationInterface` 在 8.0 API 上仍失败——实例类与其全部接口都没有可写的 `CommunicationInterface` 属性；现在再试 `SetCommunicationInterface()` / `set_CommunicationInterface()` 方法，仍不行时拒绝信息列出所有同名成员；`ReadPlcSimAdvancedInstances` 新增 `memberFilter` 诊断参数。
- **站上载**：工程级 `StationUploadProvider` 的 PC 接口既无目标接口也无子网，扫描到的 MAC 建不了地址；再加 `ConfigurationPcInterface.Addresses.Create` 一级。

## [2.7.49] - 2026-09-21

引擎 2.7.49.0（V20/V21 均重建），工具 448 不变，默认 lite 56 项不变。详见 [v2.7.49](docs/releases/v2.7.49.md)。2.7.48 部署后在 `项目1` 跑了 OPC UA 清理、PID 工艺对象往返和对 PLCSIM Advanced 实例 `MCP_SIM` 的在线族——在线族被两处缺陷挡住，本版修。

- **2.7.48 真机通过**：`ManageOpcUaInterface delete` 删掉空接口后 `MCP_PLC` 编译 Success；`PID_Compact 2.3` 的 `ExportTechnologyObject` / `ExportTechnologyObjectsToDirectory` / 删除 / `ImportTechnologyObject`（进 MCP_TO 文件夹）/ `ImportTechnologyObjectsFromDirectory` 全部通过（`TO_SpeedAxis 5.0` 没有驱动报文不能编译一致，TIA 按规则拒绝导出）；`ScanAccessibleDevices` 在 PLCSIM 虚拟网卡上按 MAC 找到实例；`ManagePlcDataBlockSnapshot createSnapshot / exportSnapshot`、`GetOnlineState` / `GoOffline` / `GoOfflineAll` / `CheckDownloadReadiness` 通过。
- **下载 / 上线路由（真机）**：`DownloadToPlc {targetIpAddress:"192.168.0.1"}` 报 "No download route reaches"——路由树里 CPU 的配置 IP 只在 `ConfigurationPcInterface.Subnets["MCP_PN"].Addresses` 下（目标接口 `1 X1` 没有地址），而引擎只查 `TargetInterfaces[].Addresses`；`GoOnline` 从不套用路由（`GoOnline()` 用 TIA 上次的路由），首次上线永远 "The connection cannot be established"。现在：地址按目标接口 → 子网 / 网关 查找，都没有时按官方 `ConfigurationAddressComposition.Create(ip)` 在目标接口上建（首次下载到 PLCSIM Advanced 实例 / 出厂 CPU）并走 5 参 `Download(IConfiguration, ConfigurationAddress, …)`；`GoOnline` 新增 `pgPcInterface`，选路由后走 `GoOnline(ConfigurationAddress)`（V20 只有 `GoOnline()`，先 `ApplyConfiguration(address)`）；`ReadPlcBlockFingerprints` / `UploadDeviceParameters` / `UploadStationFromPlc` 同样接受子网 / 网关地址，站上载对扫描到的 IP / MAC 也能建地址。
- **PLCSIM Advanced（真机）**：`ManagePlcSimAdvancedInstance register/powerOn … communicationInterface` 抛 "未找到属性设置方法"——运行时实例类的公开 `CommunicationInterface` / `OperatingMode` 是只读的，setter 在 `IInstance` 接口上；`RegisterInstance` 已经建出实例而工具报失败。现在经接口 setter 写；`WritePlcSimAdvancedTags` "Wrote 0/1" 与 `RunPlcSimAdvancedTestScenario` "Scenario FAILED" 不再报 `operationSuccess=true`；powerOff 后的空状态有文案。
- **监控表（真机 + PublicAPI）**：`SetWatchTableModifyValue` 永远 "Could not create entry"——`PlcWatchTable.Entries` 是 `PlcTableCommentEntryComposition`，工厂只有无参 `Create()`，条目的类型化属性全部只读；改为 `Create()` + `SetAttribute("Address"|"Name" / "ModifyValue" / "ModifyTrigger")` 并 `GetAttribute` 读回，读回不符时如实报失败。
- **工艺对象**：`GetTechnologyObjects` 只列根组（导入进 `MCP_TO` 文件夹的 TO 报 0 个）；现在递归用户文件夹并回报 `Folder`；`ExportTechnologyObject` 接受 `文件夹/名`；`ExportTechnologyObjectsToDirectory` 把文件夹里的 TO 导出到同名子目录。离线新增 16 项测试（2179）。

## [2.7.48] - 2026-09-21

引擎 2.7.48.0（V20/V21 均重建），**工具 448**（新增 `ManageOpcUaInterface`），默认 lite 56 项不变。详见 [v2.7.48](docs/releases/v2.7.48.md)。2.7.46 / 2.7.47 部署后在 `项目1` 重跑台账 🔁 行与刻意绕开的项目的结果。

- **重跑结果**：45 行 🔁 里 43 行转为通过（标签注释、桥接导出、真实文件路径、DB 编号、交叉引用、报警类 .DAT、ProDiag、系统诊断 .dat、PROFINET 子网连接 PLC↔TP700、导轨插 DI16 与地址改写、端口互连、属性多备选、经典变量表递归、`ImportHmiScreen` 剥属性后导入成功、全局库、`ImportOpcUaInterface` 诚实报错、安全自检、DCC 默认值、Unified 面板旧版本守卫……）；宽松绑定四类写法全部生效。
- **刻意绕开的项也跑了**：`SaveAsProject` / `RetrieveProjectArchive` / `ScaffoldProject`（真建工程，7 步全过）/ `CreateProject` 通过；工艺对象 `PID_Compact 2.3`、`TO_SpeedAxis 5.0`、`TO_PositioningAxis 5.0` 都能建——**crash ⑨ 只在 `TO_PositioningAxis 6.0`**（CPU 不提供的版本不被干净拒绝而是退出），描述改为"用官方表的最低版本"；`ReadMotionAxisConfiguration` / `ManageMotionAxis` / `ConfigureMotionHardwareConnection` 在自建轴上通过。
- **新的 TIA 退出点 ⑩ 与守卫**：经典画面 XML 的 Width/Height 与面板不一致（构建器默认 640×480，TP700 是 800×480）→ `NonRecoverableException "The screen size does not match the device"`，TIA 退出。`ImportHmiScreen` 现在先比对面板已有画面或硬件目录描述里的分辨率（"800 x 480 像素"），不一致或查不到就拒绝；三个经典画面构建器的描述标明尺寸要求。
- **修复**：`ImportTechnologyObject` 反射找错属性名（`TechnologyObjectGroup`），改为类型化 `TechnologicalObjectGroup`；`ExportTechnologyObjectsToDirectory` 空消息；`WriteClassicHmiMinimalPackageFiles` 忽略 `packageName`；8 处 "Invalid … action." 拒绝信息改为列出合法值（宽松绑定的大小写重试才有依据）。
- **新增**：`ManageOpcUaInterface`（read / delete 一个 OPC UA 服务器接口 / SIMATIC 接口 / 引用命名空间）——2.7.45 假成功留下的空接口让整个 PLC 编译失败，此前没有任何工具能删它；`ManagePlcSimAdvancedInstance` 新增 `communicationInterface=TCPIP`（PLCSIM Advanced 实例走虚拟网卡，供 `DownloadToPlc` / `GoOnline` 安全地对虚拟 PLC 跑在线族）。
- **事实更正**：虚拟机网段 192.168.0.1 上那台 "CPU 1510SP F（RUN）"是维护者当时开着的 PLCSIM Advanced 实例，不是真实 CPU（现已消失，实例表为空）。

## [2.7.47] - 2026-09-20

引擎 2.7.47.0（V20/V21 均重建），工具 447 不变，默认 lite 56 项不变。详见 [v2.7.47](docs/releases/v2.7.47.md)。针对维护者反映的"AI 调用工具反复出现格式错误"：`CallTool` 桥接做宽松绑定。

- **宽松绑定（CallTool 桥接）**：`argumentsJson` 既可是 JSON 字符串也可直接是对象；`*Json` / 字符串参数给成对象或数组时自动转成其 JSON 文本；数字 / 布尔给成字符串（或 0/1）时自动解析；字符串参数给成数字时取其文本；`action` / `kind` 这类枚举值大小写不对时按拒绝信息里列出的合法值改成规范拼写重试一次；所有规范化都在 `Meta.bridgeNormalizedArguments` 里回报。离线新增 5 项测试（2163）。

## [2.7.46] - 2026-09-20

引擎 2.7.46.0（V20/V21 均重建），工具 447 不变，默认 lite 56 项不变。详见 [v2.7.46](docs/releases/v2.7.46.md)。**全部 447 个工具在自建设备的空工程 `项目1` 上各跑了一遍**（逐工具台账：[真机台账](docs/reference/real-machine-ledger.md)），一天暴露 23 处缺陷与两个新的 TIA 退出点，全部在本版修掉或守住。

- **修复（真机发现，23 处）**：`ManagePlcTagDefinition` 注释（MultilingualText 按语言写）；`CallTool` 桥接调不到 `ExportBlocks` / `ExportTypes`（async 工具的 server/context 参数）、`""` 视为关键字默认值、数组参数接受 JSON 字符串；`ExportBlock` / `ExportType` 回报真实文件；`CreatePlcInstanceDb` 自动编号不再生成 DB0；`GetCrossReferences` 类型化并回报原因；报警类 `.DAT`、文本列表 / 实例文本类型化导出导入；ProDiag 导出不再解析 importOptions；HW 通信连接改从 `CommunicationManagement` 取；PROFINET 子网连接递归扫描面板接口；`PlugDeviceItem` 读回递归；精确硬件路径回退到 `Items`；`DumpDeviceAttributes` 多备选过滤；经典 HMI 变量表递归用户文件夹、未知表 NotFound；`ImportHmiScreen` 剥掉面板版本不支持的属性重试；`ManageGlobalLibrary` 空 openMode；探针不再关掉已打开的库；`ImportOpcUaInterface` 不再假成功；安全自检误报；DCC 两个工具的 `driveObjectNumber` 默认值；`GeneratePlcLoadableFile` / `ManageTechnologyObject` / `ExchangeSystemDiagnosticsSettings` 的错误信息。
- **守卫（TIA 退出点 ⑧ ⑨）**：`AddDevice` / 目录探针拒绝版本主号不等于 Portal 主版本的 WinCC Unified 面板（`/20.0.0.0` 在 V21 上让 TIA 退出）；`ManageTechnologyObject create` 描述标明 `TO_PositioningAxis 6.0` 在 1515F-2 PN V2.9 上让 TIA 退出，建议导入 XML。
- **验证**：离线 2158 项（新增 12 项：标签注释请求逻辑、桥接基础设施参数识别）；形状检查 V20 2789 / V21 3077。真实工程：见台账（265 通过、6 手工单跑、45 本版修复待重跑、22 TIA/环境拒绝、64 只到参数拒绝、7 刻意不跑（虚拟机网段上有维护者的真实 CPU）、12 未跑（需要工艺对象或会新建工程））。

## [2.7.45] - 2026-09-20

引擎 2.7.45.0（V20/V21 均重建），工具 447 不变，默认 lite 56 项不变。详见 [v2.7.45](docs/releases/v2.7.45.md)。在维护者新建的空工程上用自建的临时 S120 首次真机跑通 DCC 全族，顺带修五处。

- **修复（会话绑定）**：`GetState` 的"取第一个可访问工程"重绑现在尊重显式绑定——`AttachToOpenProject` 之后只接受同名工程，绝不静默切到另一 TIA 实例的工程（真机：第二个实例里打开维护者工程后，引擎从 `项目1` 切到 `AutomaticDipCoatingMachine`，临时面板建进了维护者的工程）。
- **新增参数（硬件）**：`PlugDeviceItem` / `GetDevicePlugLocations` 的 `plugOnDevice=true` 以 Device（站）本身为宿主——Startdrive 驱动组件（电机模块，其下电机 / 编码器）只能这样插（官方 "Creating a drive component"，真机 `Device.CanPlugNew("OrderNumber:6SLxx2x-1xxxx-xxxx", name, 65535)` 为 true，设备项上全为 false）；槽位 65535（任意）插入后按新对象名读回并回报真实位置（此前报"未验证"）。
- **修复（DCC）**：`ManageDccPin update` 的 `Value` 按当前值的 CLR 类型转换（真机：ADD 输入是 `System.Single`，Int32 / Double 被拒）；`ManageDccChart` / `ManageDccBlock` 只带 `sequenceIndex` 的 update 不再被拒；描述记录 V5.2 驱动轴才有 `DriveControlChartContainer`、`DccChartInterfaceComposition.Create` 在该固件上不支持。
- **修复（其它）**：`AddDevice` 失败时列出每个 TypeIdentifier 变体的尝试与原因（真机：TP700 Comfort 只看到最后一个变体 `.../V14.0.1.0.0.0` 的错误，目录原样的 `.../14.0.1.0` 为何被拒不可见），并提示用 `SearchHardwareCatalog` 的原样标识；`ExportAlarmInstanceTexts` 描述改指 `ImportPlcAlarmInstanceTexts`（此前写着 "not yet exposed"）。
- **验证**：离线 2146 项（新增 2 项）；形状检查 V20 2789 / V21 3077（新增 4 项）。真实工程（2.7.44 引擎，`项目1`，见 `docs/releases/v2.7.45.md#真机结果`）：DCC 图表 / 子图 / 块 / 引脚连接 / 发布 / 分区 / 顺序 / 导出导入 / 删除 / DCB 库全部通过；S120 电机 / 编码器插入通过，投影读取与 `changeType` 被 TIA 按其规则拒绝。

## [2.7.44] - 2026-09-20

引擎 2.7.44.0（V20/V21 均重建），工具 447 不变，默认 lite 56 项不变。详见 [v2.7.44](docs/releases/v2.7.44.md)。2.7.43 真机重跑首次看到 CFC 空导出的真实结构，修正预检解析器。

- **修复（CFC 预检）**：空导出是 `Document > DocumentInfo / FunctionChartsFolder(Name="Charts") > ObjectList / UsedAlarmClasses`，2.7.43 的解析器把文件夹元素当成一张图表（清单 `["Charts"]`），于是没有图表的 PLC 上 `exportInstructionData` 不会被"无图表"分支拒绝（未跑，否则会再让 TIA 退出）；2.7.44 只认名字含 Chart、且不含 Folder / List、不以 s 结尾的元素（`CfcLogic.IsChartElement`），离线测试改用真实的导出形状。
- **验证**：离线 2144 项（新增 1 项）；形状检查 V20 2785 / V21 3073 不变。真实工程（2.7.43 引擎）：Test Suite 空组拒绝、条件 NotRelevant 引擎侧拒绝（对象未动）、合法条件 update、`changeEvaluationDevice` 驱动拒绝、CFC 未知图表 NotFound 全部通过，TIA 未退出。真实工程（2.7.44 引擎，同日）：CFC 预检对没有图表的 PLC 回 InvalidState（`read MCP_NONE` / `exportInstructionData` / `selectiveExport`），`export` 附 `inventory`（0 张图表），TIA 未退出。

## [2.7.43] - 2026-09-20

引擎 2.7.43.0（V20/V21 均重建），工具 447 不变，默认 lite 56 项不变。详见 [v2.7.43](docs/releases/v2.7.43.md)。2.7.42 真机重跑让 TIA Portal V21 退出三次（CFC 两次、Safety Validation 一次），本版加守卫；引擎本身三次都活着。

- **守卫（CFC）**：`ExchangeCfcCharts selectiveExport / exportInstructionData` 与 `ManageCfcChartProtection` 全部动作先做 `CompleteExport` 预检（临时 ZIP，解析 `Data.xml` 里带 `Name` 的 `*Chart` 元素得到图表清单，`meta.preflight` 报图表数 / 名字 / ZIP 条目 / XML 元素名），PLC 没有图表回 InvalidState、图表名不存在回 NotFound——真机上 `GetChartProtection("MCP_NONE")` 与 `ExportInstructionData` 在没有 CFC 图表文件夹的 1510SP F 上各让 TIA 退出一次；`skipChartPreflight=true` 可跳过（自担风险）；`export` 结果附带解析出的 `inventory`。两个 csproj 引用 `System.IO.Compression`。
- **守卫（Test Suite）**：`RunTestSuiteCase runAll` 对空的系统组回 InvalidState（真机：application 空组 `TestCaseExecutor.Run` 在 TIA 内抛 NullReferenceException，system 空组回 "No test case(s) in the selected project"，只有样式指南空组回 Success）。
- **守卫（Safety Validation）**：`ManageSafetyFunctionCondition create / update` 先按信号用途校验 NotRelevant（OperatingMode 拥有 executedInput，InputCondition 拥有 initialInput + executedInput，Response 拥有 response；TIA 原生拒绝但此前 `Comment` / `SignalName` 已写入，对象半更新，紧接的条件级 `CheckValidity` 让 TIA 退出），写入顺序改为用途 → 输入 → 描述字段；`changeEvaluationDevice` 改从 `DeviceQuery.EvaluationDevices()` 解析（真机：驱动被拒 "not a valid evaluation device"）；条件级 `checkValidity` 的描述标注真机崩溃过一次。
- **验证**：离线 2143 项（新增 7 项：ZIP 清单解析、按用途的 NotRelevant 门）；形状检查 V20 2785 / V21 3073（新增 4 / 5 项）。真实工程（2026-09-20，见 `docs/releases/v2.7.43.md#验证`）：三处守卫都拦住了，TIA 未退出；但 CFC 清单解析把 `FunctionChartsFolder Name="Charts"` 当成图表（2.7.44 修）。

## [2.7.42] - 2026-09-20

引擎 2.7.42.0（V20/V21 均重建），工具 437 → 447，默认 lite 56 项不变。详见 [v2.7.42](docs/releases/v2.7.42.md)。"官方 Openness API 全量对齐"阶段 6 ⑥-③：SafetyValidation + Test Suite + Teamcenter + CFC 选件包——**最后 25 / 122 个功能类型缺口归零，阶段 6 收口**（SafetyValidation 与 Teamcenter 无许可 / 无环境，只有形状检查）。

- **新增（Safety Validation Assistant，V21）**：`ReadSafetyActivationTests`（激活测试 / 用户组 / 评估设备、单个测试的可用设备与 `TestValidity`）、`ManageSafetyActivationTest`（create / createFromTest / createFromMasterCopy / rename / setAuthor / changeEvaluationDevice / checkValidity / generateReport（.xlsx）/ export / import / delete）、`ManageSafetyActivationTestGroup`、`ManageSafetyFunction`（create / createFrom / update / resetTestResult / checkValidity / setTrace / checkTraceValidity / export / import / delete）、`ManageSafetyFunctionCondition`（按 `Conditions` 位置：create / update / checkValidity / delete）；V20 全部回 NotSupportedOnVersion。
- **类型化改造（Test Suite）**：`ReadTestSuiteCases` / `ExchangeTestSuiteCase` / `RunTestSuiteCase` 改为强类型（`RuleSet` / `TestCase` / `SystemTestCase` / `ApplicationTestSet`，三类 `LoadFromFile` 的 `RSLoadOptions` / `TCLoadOptions` / `TSLoadOptions`，执行器全部 `Run` 重载，`TestResultsMessage` 递归读取），新增 `kind` testSet、`importTestSets`、`namesJson` / `runAll`；新增 `ManageTestSuiteCase`（rename / setScope（样式指南 `scopeJson` 对象、应用测试 `PlcSoftware` + 实例 + `ExecutionMode`、系统测试 OPC UA 地址 + 接口类型 + 目录）/ copyScope / createFromMasterCopy / showInEditor）。
- **新增（Teamcenter Gateway）**：`ManageTeamcenterConnection`（connect（密码转 `SecureString`）/ connectSso / disconnect，会话内保存一份 `TcGatewayConnectionInfo`）、`ManageTeamcenterDataset`（checkout / checkin / cancelCheckout / search / download）、`ManageTeamcenterWorkflow`（readCustomAttributes 与九个保存动作，`confirmSave`；自定义属性经 `SetValue(value, ErrorCallback)`）。
- **类型化改造（CFC）**：`ExchangeCfcCharts` 改为强类型并新增 `selectiveExport`（`chartNamesJson`）与 `exportInstructionData`；新增 `ManageCfcChartProtection`（read / add / change / remove）。
- **审计**：有专用引用 629 → 668，完全未触及 158 → 116，未封装功能类型 25 / 122 → **0 / 0**（SafetyValidation 8 / 34、Test Suite 9 / 44、Teamcenter 7 / 37、CFC 1 / 7 全部归零）。
- **验证**：离线 2136 项（新增 80 项）；形状检查 V20 2781 / V21 3068（新增 190 / 257 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-20，见 `docs/releases/v2.7.42.md#验证`）：Test Suite 三组空表与样式指南空组执行通过，application 空组 `Run` 在 TIA 内抛 NRE；CFC `CompleteExport` 通过，`GetChartProtection`（未知图表）与 `ExportInstructionData` 各让 TIA 退出一次；**Safety Validation Assistant 虚拟机有选件**，激活测试 / 组 / 安全功能 / 条件 / 跟踪 / 有效性 / 报告 / 导出导入 / 删除全链路通过，条件级 `CheckValidity` 在一次半拒绝的 update 之后让 TIA 退出；Teamcenter 提供者存在、无服务器。三次退出引擎都活着（2.7.41 存活路径首次真机验证）。守卫见 2.7.43。

## [2.7.41] - 2026-09-20

引擎 2.7.41.0（V20/V21 均重建），工具 437 不变，默认 lite 56 项不变。详见 [v2.7.41](docs/releases/v2.7.41.md)。2.7.40 真机：TIA Portal 退出后 MCP 引擎进程也随之崩溃（退出码 0xE0434352，"Exception.ToString() 失败"），本版让引擎活下来并再加一处 Startdrive 守卫。

- **引擎存活**：`App.config` 启用 `legacyUnhandledExceptionPolicy`（Openness 后台线程在 TIA 退出后抛出的异常不再终止进程），`Program.cs` 以不调用 `ToString()` / `Message` 优先的方式把未处理异常与未观察的任务异常记进诊断日志；`GetState` 在绑定的 TIA 进程已不在时直接回 `isConnected=false` + `portalProcess.processAlive=false`，不再抛 "disposed"。
- **守卫（Startdrive）**：`ManageDriveTelegrams` 对没有任何报文（未联网）的驱动对象拒绝 check / insert / erase（新建 G120C 上 `CanInsertAdditionalTelegram` 抛 "Invalid operation"、`CanInsertTelegram(700, SupplementaryTelegram)` 让 TIA 退出）。
- **PLCSIM Advanced 桥**：维护者报告 2.7.38 在连续读写 15–30 次后引擎崩溃 5 次（与并发无关）——桥不再每次调用都 `CreateInterface` / `UpdateTagList` / `Dispose`，改为每个实例名缓存一个 `IInstance` 接口、变量表只装载一次（"not found" 时刷新重试）、所有 API 调用串行；`unregister` / `powerOff` / `memoryReset` 与异常后释放接口；结果里 `interface` 报缓存状态。未在真实 PLCSIM 上验证。
- **验证**：离线 2056 项；形状检查 V20 2591 / V21 2811 不变。真实工程（2026-09-20，临时 G120C + 临时 S120 CU320-2 PN，全程 TIA 未退出）：报文守卫生效；G120C 的 `setMotorType` → `readMotorConfiguration` → `projectMotorConfiguration` 实做通过，编码器投影被 TIA 拒绝（设备不支持）；S120 的驱动对象号、类型处理器、激活、`Security`、工艺扩展 `activate` / `deactivate` 实做（TRCDATA 0 → 250 参数）、`DriveItemHardwareModule`、CU 参数枚举表通过；门户级 `readPackages` 列出已装 .tec 包；DCC / 在线 / 验收测试在这两台设备上按设计回 NotSupported，DCC 全族仍待带 DCC 的驱动轴对象。

## [2.7.40] - 2026-09-20

引擎 2.7.40.0（V20/V21 均重建），工具 437 不变，默认 lite 56 项不变。详见 [v2.7.40](docs/releases/v2.7.40.md)。2.7.39 真机重跑暴露了三处让 TIA Portal V21 整个退出的 Startdrive 调用（G120C），本版加守卫与诊断。

- **守卫（Startdrive）**：`ManageDriveTelegrams` 在驱动对象已有主报文时不再调用 `CanInsertMainTelegram` / `InsertMainTelegram`（真机上 TIA 在此崩溃；主报文只在 G220 上可增删），`check` 回 `mainTelegramPresent`，`insert` 明确拒绝；`ReadDriveParameters` / `ReadOnlineDriveParameters` 新增 `includeValue`（默认 true），`Value` 改为最后读且可关闭——先读元数据再决定是否读值（真机上读 `r2139` 与新建驱动上未接线的 `p840[0]` 的值让 TIA 退出）；位参数名（`r722.0`）经父参数 `Bits` 解析（`Find` 对点名回 null），`ManageStartdriveParameter` 的读写与 BICO 源同样支持。
- **诊断**：引擎记住所绑定的 TIA Portal 进程 id；`GetState` 的 `hmiReadHealth.portalProcess`、连接失败保护的记录与 `GetState` 失败文案都报告该进程是否还在（`processAlive=false` 时直接说"TIA Portal 进程已不在，重启并重新打开工程后 AttachToOpenProject"），不再让人猜"disposed"到底是句柄陈旧还是进程没了。
- **SiVArc**：`GenerateSiVArc` 先按 `PlcSoftware.Name` 再按所属设备名调 `Sivarc.Generate`，两次都报 "PLC device not found" 时以 InvalidState 说明 SiVArc 只认 HMI 已连接的 PLC（`meta.attempts` 记录两次）；`ManageSivarcTableRule` 描述改正：`ProgramBlock` 赋 null 会被 TIA 拒绝。
- **验证**：离线 2056 项；形状检查 V20 2591 / V21 2811（新增 2 项）。真实工程（2026-09-20，临时设备）：`processAlive` 诊断、`includeValue=false` 元数据读取、`Bits` 位名解析通过；新建未联网 G120C 上的报文 `Can*` 让 TIA 退出，随后引擎进程也崩溃——2.7.41 修。

## [2.7.39] - 2026-09-19

引擎 2.7.39.0（V20/V21 均重建），工具 421 → 437，默认 lite 56 项不变。详见 [v2.7.39](docs/releases/v2.7.39.md)。"官方 Openness API 全量对齐"阶段 6 ⑥-②：Startdrive + DCC 选件包——**Startdrive 34 / 117 与 DCC 13 / 68 功能类型缺口归零**（形状检查；虚拟机装有 Startdrive Advanced + DCC，部署后真机验证）。

- **修复（2.7.38 真机）**：类型化规则工具 `ManageSivarcRule` 改名 `ManageSivarcTableRule`（`CallTool` 的工具映射不分大小写，被旧工具 `ManageSiVArcRule` 遮蔽，lite 模式下不可达），`Check-DeadToolReferences.py` 新增"工具名不分大小写唯一"闸门；`GenerateSiVArc` 改传 PLC 设备名（`Sivarc.Generate` 的 `plcs` 是设备名，真机报 "PLC device '+S1-K1' not found"）；通用 `ManageSiVArcRule delete` 后从锚点重导航再核对（旧组合代理抛 `EngineeringObjectDisposedException`）；`Probe-McpServer.py` 打印 JSON-RPC 错误。
- **新增（Startdrive）**：`ReadDriveObjects`（`DriveObjectContainer` 驱动对象 + 报文 + 功能接口视图 + 工艺扩展 + DCC 摘要 + `ModuleAccessPoint` / V21 `DriveItemHardwareModule`）、`ReadDriveParameters`（`ReadParameters` / `Parameters` 按名 / 按号 / 分页，BICO 源、位、枚举）、`ManageDriveTelegrams`（Can* / Insert* / Erase / ChangeSize / TelegramNumber、V21 SDR 接口与 V20 基接口的 `Connect(Telegram)`）、`ManageDriveFunctions`（驱动对象类型、激活、Function in Use、调试、安全校验和、电机 / 编码器硬件投影与配置条目）、`ManageDriveSecurity`（UMAC / DDE）、`ManageTechnologyExtensions`（驱动对象的扩展与门户的安装包）、`ManageDriveHardwareModule`（V21）、`ManageDriveSafetyAcceptanceTest`（V21）、`ReadOnlineDriveParameters`（ONLINE）、`ManageOnlineDriveFunctions`（ONLINE-WRITE：恢复出厂 / RAM→ROM / 激活）；`ManageStartdriveParameter` 类型化改造（新增 `driveObjectIndex`——G120C 上 `DriveObjectNumber` 不可读——与 BICO 写入）；Startdrive 下载 / 上载提示类型化。
- **新增（DCC）**：`ReadDccCharts`（图表树、分区、接口、块、引脚、DCB 库、执行顺序）、`ManageDccBlock`、`ManageDccPin`（连接 / 发布 / 参数）、`ManageDccChartInterface`、`ManageDccChartPartition`、`ManageDcbLibraries`（V21 `DcbLibraryImporter`）；`ManageDccChart` 类型化改造（子图路径、自动命名、`exportAll` / `readSequence` / `showEditor`、`MoveInRuntimeSequence`、`confirmDelete`）、`ReadDccObject` 类型化驱动解析；39 个 `DccException` 子类分类回报。
- **审计**：有专用引用 573 → 629，完全未触及 255 → 158，未封装功能类型 72 / 307 → 25 / 122（Startdrive 34 / 117 → 0 / 0，DCC 13 / 68 → 0 / 0；剩 SafetyValidation 8 / 34、TestSuite 9 / 44、Teamcenter 7 / 37、CFC 1 / 7）。
- **验证**：离线 2056 项（新增 103 项）；形状检查 V20 2589 / V21 2809（新增 288 / 312 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-20，临时设备 `MCP_TMP_G120C`）：SiVArc 三处修复中两处通过（`ManageSivarcTableRule` 全链路、通用 delete 重导航），`GenerateSiVArc` 仍报 "PLC device not found"（HMI 无到该 PLC 的连接）；Startdrive 驱动对象 / 报文行 / 参数按名按号分页 / `p1120[0]` 写回 / BICO `p1070[0] ← r2050[1]` 通过；三处调用让 TIA Portal V21 退出（`r2139` 读值、已有主报文时 `CanInsertMainTelegram`、新建驱动上未接线 `p840[0]` 读值），2.7.40 加守卫。

## [2.7.38] - 2026-09-19

引擎 2.7.38.0（V20/V21 均重建），工具 413 → 421，默认 lite 56 项不变。详见 [v2.7.38](docs/releases/v2.7.38.md)。"官方 Openness API 全量对齐"阶段 6 ⑥-①：SiVArc 选件包——**SiVArc 功能类型缺口 33 / 198 归零**（发布时按形状检查；部署后发现虚拟机装有 SiVArc 并完成真机验证）。

- **新增**：`ReadSivarcRuleTree`（六个规则族的类型化文件夹 / 表 / 组 / 规则层次）、`ManageSivarcRuleContainer`（规则文件夹与规则表，含 `CreateFrom(*RuleTableTypeVersion)`）、`ManageSivarcRule`（规则与规则组的 create / createFromMasterCopy / update / delete，类型化属性、库对象与 PLC 块引用、PLC / HMI 设备列）、`ReadSivarcBlockDefinitions` / `ManageSivarcBlockDefinition`（`SivarcDataProvider` 的变量 / 文本定义与 V21 变量成员设置）、`ResolveSivarcExpression`（`ExpressionResolver`）、`ManageSivarcScreenLayout`（V21 `LayoutData`）、`UpgradeSivarcDefinitions`（`SivarcDefinitionsUpgrader`）。类型化改造：`GenerateSiVArc`（类型化 `Sivarc.Generate`、多设备重载、`SivarcGenerationResult` 与递归反馈消息）、`ReadSiVArcRules` / `ManageSiVArcRule` 锚点、`ReadLibraryType typeKind` 新增 `hmiFaceplate` / `hmiVbScript` / `hmiCScript` / `unifiedScriptModule` / `sivarc*` / `dccBlockType`。
- **审计**：有专用引用 505 → 573，完全未触及 322 → 255，未封装功能类型 107 / 515 → 72 / 307（SiVArc 33 / 198 → 0 / 0；SafetyValidation 9 / 43 → 8 / 34 只是 `Condition` 记号的词法误判）。
- **验证**：离线 1953 项（新增 72 项）；形状检查 V20 2301 / V21 2497（新增 344 / 357 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（V21 `AutomaticDipCoatingMachine`，2026-09-19，虚拟机装有 SIMATIC Visualization Architect V21）：六族规则树、规则文件夹 / 表建删、类型化规则行、块的变量 / 文本定义与采集设置建改删、`ExpressionResolver`（3 个块实例）、Unified 画面 `LayoutData` 导出、升级 / 生成预览、`typeKind` `unifiedScriptModule` / `hmiUdt` 全部通过；三处缺陷留给 2.7.39（`ManageSivarcRule` 被同名异大小写的旧工具 `ManageSiVArcRule` 在 `CallTool` 映射里遮蔽、`GenerateSiVArc` 要传 PLC 设备名而非 `PlcSoftware.Name`、通用 `ManageSiVArcRule delete` 后在陈旧代理上核对抛 `EngineeringObjectDisposedException`）。

## [2.7.37] - 2026-09-19

引擎 2.7.37.0（V20/V21 均重建），工具 409 → 413，默认 lite 56 项不变。详见 [v2.7.37](docs/releases/v2.7.37.md)。"官方 Openness API 全量对齐"阶段 5：经典 WinCC 文件夹层次——**经典 WinCC 与 WinCC.Extension 的功能类型缺口归零，核心程序集全部收口**。

- **新增**：`ReadClassicHmiScreenTree`（`ScreenSystemFolder` / `ScreenPopupSystemFolder` / `ScreenTemplateSystemFolder` / `ScreenSlideinSystemFolder` 及用户文件夹树、`ScreenOverview` / `ScreenGlobalElements`）、`ManageClassicHmiScreenObject`（`ScreenPopup` / `ScreenTemplate` / `ScreenSlidein` / `ScreenOverview` / `ScreenGlobalElements` 的读 / 导出 / 导入 / 删除）、`ManageClassicHmiFolder`（画面 / 弹出 / 模板 / 变量 / 脚本用户文件夹的读建删）、`ManageClassicHmiGraphic`（V21 `GraphicsProvider` 的 `MultiLingualGraphic`）。类型化改造：`ReadClassicHmiScripts` / `ManageClassicHmiScript`（`VBScriptSystemFolder` / `VBScriptUserFolder` / `VBScript`）、`ReadLibraryType typeKind`（四个经典 HMI 库类型子类）、`EngineeringScalarProperties.Json` 的引擎侧值渲染钩子（`ConstValue` / `NullableDateTime`）。
- **审计**：有专用引用 469 → 505，完全未触及 357 → 322，未封装功能类型 133 / 585 → 107 / 515（经典 WinCC 24 / 59 → 0 / 0，WinCC.Extension 2 / 11 → 0 / 0；只剩选件包）。
- **验证**：离线 1881 项（新增 45 项）；形状检查 V20 1957 / V21 2140（新增 78 / 95 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（V21 `AutomaticDipCoatingMachine`，2026-09-19）：四个新工具对 Unified `HMI_RT_1` 和 PLC 目标明确拒绝；`ReadLibraryType typeKind` 在 1,038 个工程库类型上核对（`codeBlock` 425 / `plcType` 454 / `hmiUdt` 4；Unified `ScriptModuleType` 与基类 `LibraryType` 面板回 `other`，2.7.38 补四个子类）；`ConstValue` / `NullableDateTime` 在 Unified 读取里不出现（经典 HMI 专属），钩子无回归。经典 WinCC 实做路径无经典 HMI 工程可验。

## [2.7.36] - 2026-09-19

引擎 2.7.36.0（V20/V21 均重建），工具 408 → 409，默认 lite 56 项不变。详见 [v2.7.36](docs/releases/v2.7.36.md)。2.7.35 真机缺陷修复 + "官方 Openness API 全量对齐"阶段 4 子批次 ④-③：工艺对象映射——**`Siemens.Engineering.Step7.dll` 的功能类型缺口归零，阶段 4 收口**。

- **修复（2.7.35 真机）**：`ManagePlcTableEntries createComment` 后从监控表重新导航计数（旧组合代理 `Count` 陈旧）。
- **新增**：`ReadTechnologyObjectTree`（`TechnologicalInstanceDBGroup` 树 + `TechnologicalParameter` 行 + 类型化 Motion / Ident 视图）。类型化改造：`ReadMotionAxisConfiguration`（`typed` 视图）、`ManageMotionAxis`（`TechnologicalInstanceDBAssociation`、`TOMapping` / `DBMemberMapping`、`IdentTechnologicalObjectProvider`、`AxisEncoderHardwareConnectionInterface` / `TorqueHardwareConnectionInterface` / 测量输入 / 输出凸轮的 `Connect` / `Disconnect`，新增 `Connect(Channel)` 目标）、`ManageTechnologyObject`（`TechnologicalInstanceDBComposition.Create`、`TechnologicalParameterComposition.Find`）、`ConfigureMotionHardwareConnection`（类型化提供者）。
- **审计**：有专用引用 451 → 469，完全未触及 370 → 357，未封装功能类型 139 / 637 → 133 / 585（**Step7 5 / 42 → 0 / 0**）。
- **验证**：离线 1836 项（新增 11 项）；形状检查 V20 1879 / V21 2045（新增 95 / 116 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-19 重跑，[v2.7.36 真机结果](docs/releases/v2.7.36.md#真机结果v21-automaticdipcoatingmachine2026-09-19两个-tia-进程同开)）：临时建 `TO_SpeedAxis` V9.0 后，类型化树、42 个参数、执行器 / 扭矩接口行、`Connect(DeviceItem)` 到达原生门控（驱动无地址）、通道目标门控、`Disconnect`、删除，以及注释行计数修复全部通过。

## [2.7.35] - 2026-09-19

引擎 2.7.35.0（V20/V21 均重建），工具 402 → 408，默认 lite 56 项不变。详见 [v2.7.35](docs/releases/v2.7.35.md)。2.7.34 真机缺陷修复 + "官方 Openness API 全量对齐"阶段 4 子批次 ④-②：Step7 收尾。

- **修复（2.7.34 真机）**：`ManagePlcSoftwareUnit` 的关系 Create / Delete 回读改为从单元组重新导航（旧组合代理 `Find` 回 null / 抛 `EngineeringObjectDisposedException`）。
- **新增**：`ManagePlcExternalSources`（`PlcExternalSource(Group/UserGroup/SystemGroup)`：文件 / 母本建源、删除、生成块到指定用户组、用户组建改删）、`ReadPlcSystemGroups`（`PlcSystemBlockGroup` 树 + `PlcSystemTypeGroup`）、`ReadPlcTagTableConstants`（`PlcConstant` 行）、`ExchangePlcAlarmTextListsXlsx`（`PlcAlarmTextListProvider` 导出 / 导入）、`ManagePlcTableEntries`（`PlcWatchTableEntry` / `PlcForceTableEntry` / `PlcTableCommentEntry`）、`ExportPlcProDiagInfo`（`CodeBlock.ExportProDIAGInfo`）。类型化改造：监控 / 强制表访问规则、OPC UA `OpcUaCommunicationGroup` / `NamespaceAccessRestriction`、报警类与监控设置结果消息、库类型子类 `typeKind`、`ManagePlcUserGroup` 新增 `watchTables` / `externalSources` 族与类型化组行。
- **审计**：有专用引用 417 → 451，完全未触及 406 → 370，未封装功能类型 162 / 695 → 139 / 637（Step7 28 / 100 → 5 / 42，只剩工艺对象映射）。
- **验证**：离线 1825 项（新增 71 项）；形状检查 V20 1784 / V21 1929（新增 114 / 119 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-19 重跑，[v2.7.35 真机结果](docs/releases/v2.7.35.md#真机结果v21-automaticdipcoatingmachine2026-09-19两个-tia-进程同开)）：关系回读修复、外部源全生命周期（SCL 文件 → 建源 → 生成 FC 到临时组 → 删）、系统组、79 个系统常量、文本列表 XLSX 的原生拒绝路径、监控表注释行、ProDiag 门控、类型化访问规则 / OPC UA 限制 / 报警类消息 / 库类型种类全部通过；一处缺陷记入 2.7.36（`createComment` 后旧组合代理 `Count` 陈旧）。

## [2.7.34] - 2026-09-19

引擎 2.7.34.0（V20/V21 均重建），工具 396 → 402，默认 lite 56 项不变。详见 [v2.7.34](docs/releases/v2.7.34.md)。2.7.33 真机事务缺陷修复 + "官方 Openness API 全量对齐"阶段 4 子批次 ④-①：Step7 软件单元与 `PlcSoftware` 小服务。

- **修复（2.7.33 真机）**：`RunToolsInTransaction` 内层调用不带 `dryRun` 时跑成预览却回报已提交——`ForceRealExecution` 无论键是否存在都写入 `dryRun=false`。
- **新增**：`ReadPlcSoftwareUnits`（`PlcUnitSystemGroup` / `PlcUnitBase` / `PlcSafetyUnit` / `PlcUnitRelation` 类型化树）、`ManagePlcDocuments`（命名值类型文档与 UDT 的 list / read / export / import / createFromMasterCopy / createFromLibraryType：`PlcDocument` / `PlcDocumentComposition` / `DocumentImportResultForSplDocument` / `DocumentImportResultForTypes` / `DocumentResultMessage`）、`ReadPlcChecksums`（`PlcChecksumProvider`）、`ReadPlcObjectFingerprints`（`FingerprintProvider` / `Fingerprint` / `FingerprintId`）、`ManagePlcBlockWriteProtection`（V21 `PlcBlockWriteProtectionProvider` 状态机）、`ManageProjectCompilationSettings`（V20 `Project` 属性 / V21 `PlcSimulationSettingsProvider` + `VirtualPlcSettingsProvider`）。扩展：`ManagePlcSoftwareUnit` 的 `unitKind=safety` / `createFromMasterCopy` / `commentsJson`，`ReadDeviceItemChannels includeLinkedTags`（V21 `PlcTagProvider.GetLinkedTags`），`UpdateDeviceAddress` 在 V21 经 `ProcessImageProvider` 指派过程映像，`ImportFromDocuments` 的类型化 `DocumentImportResultForBlocks` 与原生消息。
- **审计**：有专用引用 396 → 417，完全未触及 428 → 406，未封装功能类型 178 / 735 → 162 / 695（Step7 44 / 140 → 28 / 100）。
- **验证**：离线 1754 项（新增 87 项）；形状检查 V20 1670 / V21 1810（新增 85 / 102 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-19 重跑，[v2.7.34 真机结果](docs/releases/v2.7.34.md#真机结果v21-automaticdipcoatingmachine2026-09-19两个-tia-进程同开)）：临时软件单元建 / 改 / 关系 / 删、UDT 文档导出与 Override 导入、校验和、指纹、块写保护整条状态机、编译设置读写、四种模块的通道关联变量、不带 `dryRun` 的事务提交与回滚全部通过；一处缺陷记入 2.7.35（单元关系 Create / Delete 后在同一组合代理上回读失效）。

## [2.7.33] - 2026-09-19

引擎 2.7.33.0（V20/V21 均重建），工具 389 → 396，默认 lite 56 项不变。详见 [v2.7.33](docs/releases/v2.7.33.md)。2.7.32 真机问题修复 + "官方 Openness API 全量对齐"阶段 3 子批次 ③-④：Base 收尾——**`Siemens.Engineering.Base.dll` 的功能类型缺口归零，阶段 3 收口**。

- **修复（2.7.32 真机）**：工程级 `SyslogServerComposition.Create` 让 TIA Portal V21 崩溃——`ManageSyslogServers scope=project create` 只留预览并说明替代路径；`ManagePlcCertificate template` 不再要求 `certificateId`；`RequireUmacDevice` 文案指向设备；`CheckLibraryUpdates` 的 `KeyValuePair` 部件扁平渲染为 `parts`；多 TIA 实例下的改绑守卫（记住显式绑定的工程名，自愈只回绑它，写前核对）。
- **新增**：`ReadPortalInfo`（`TiaPortalProcess` / `TiaPortalSession` / `TiaPortalProduct`、`TextCategories`、`HwUtilities`）、`ReadTransferRoutes`（`Connection.*` 路由树、R/H 提供者）、`ManageHardwareUtilities`（`ModuleInformationProvider`、OPC UA XML 导出、PSC 卡片导出）、`ManageDeviceServiceObjects`（Web 应用、遥控数据点、动态证书管理与 `CertificateSupportedService`）、`ReadObjectIdentifier`（`ObjectIdentifierProvider.GetIdentifier / Find`）、`ShowObjectInEditor`（`IShowable`）、`RunToolsInTransaction`（`ExclusiveAccess.Transaction`，全部成功才 `CommitOnDispose`）。扩展：`OpenProject` 的 UMAC 凭据（`UmacDelegate` / `UmacCredentials`）、`GoOnline` 的 `userName` / `userType`（`OnlineAuthenticationConfiguration`）与 `rhTarget`、`DownloadToPlc` 的 `rhTarget`（`RHDownloadProvider`）、下载 / 上载提示与结果的官方基类、`SetAttributes(pairs, AttributeDelegate)` 批量属性写、`GetCrossReferences` 的类型化 `SourceObject` / `ReferenceObject` 字段、`CompareProjects` 的 `CompareResultElement`、`SearchHardwareCatalog` 的 `CatalogEntry`、多用户 / 版本控制 / 设置的类型化行、错误 meta 附 `ExceptionMessageData`。
- **审计**：分母去掉 internal 的 `CompileProvider` / `Private.ProcessHelper`（1,215 类型）；有专用引用 314 → 396，完全未触及 490 → 428，未封装功能类型 230 / 896 → 178 / 735（Base 52 / 161 → 0 / 0）；`HW.CustomDataTypes.*` 登记为动态覆盖。
- **验证**：离线 1667 项（新增 47 项）；形状检查 V20 1585 / V21 1708（新增 182 / 208 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-19 重跑）：五处修复、门户诊断、传输路由、硬件工具（含 20 MB OPC UA XML 真实导出）、设备服务对象读取、对象标识往返、事务提交与回滚、类型化交叉引用 / 目录行全部通过。**缺陷**：`RunToolsInTransaction` 内层调用不带 `dryRun` 时按预览执行却回报 `committed=true`（显式 `dryRun:false` 可绕过）——2.7.34 修，见发布说明"真机结果"。

## [2.7.32] - 2026-09-18

引擎 2.7.32.0（V20/V21 均重建），工具 386 → 389，默认 lite 56 项不变。详见 [v2.7.32](docs/releases/v2.7.32.md)。2.7.31 真机缺陷修复 + "官方 Openness API 全量对齐"阶段 3 子批次 ③-③：用户管理与安全。

- **修复（2.7.31 真机）**：库名统一走 `LibraryRef`（`ProjectLibrary` 没有 `Name`，工程库固定标签 + 所属工程），`ReadLibraryOverview` / `CheckLibraryUpdates` / `SynchronizeLibrary` / `ManageLibraryType` / `CompareLibraries` 在工程库上不再抛 "Name is unavailable"；系统库为 null 的 `TypeFolder` / `MasterCopyFolder` 输出 null + 说明，选择集解析对 null 根给明确 `NotFound`；`CheckLibraryUpdates` 在调用 TIA 前拒绝没有类型文件夹的库（TIA 对系统库抛 `NonRecoverableException` 并会触发连接保护）；`ManageGlobalLibrary` 的 `close` / `save` / `saveAs` / `archive` 对系统库说明 "只在 UserGlobalLibrary 上存在"，`openInfo` 回报 `closableThroughApi`。
- **新增（域 `Security`，3 个强类型工具）**：`ManageSyslogServers`（工程级 `SyslogServerProvider.Servers` 的 read / create / update / delete / assignModule / unassignModule，CPU `SysLogConfigurationManager` 的 read / update / createServer / deleteServer）、`ManagePasswordPolicy`（`PasswordPolicyConfigurator` 8 项、`PlcPasswordPolicyService`、`LegacyPlcPasswordPolicyService` 5 项，legacy 官方范围预核）、`ManageUmcUsers`（UMC 用户 / 组的 read / createOffline / importFromServer / rename / activate / deactivate / delete / assignRole / unassignRole；服务器 read / checkConsistency / synchronize；凭据经 `Authentication` 事件、SecureString 不回显）。扩展 `ManagePlcCertificate`（`template`、类型化模板字段 + `subjectAlternativeNamesJson`、`password` 导入、`EnableGlobalCertificatesStore`）与 `ManageProjectUserManagement`（`activateAnonymousUser` / `deactivateAnonymousUser`；设备功能权行带 identifier / group / comment）。
- **审计**：有专用引用 288 → 314，完全未触及 513 → 490，未封装功能类型 239 / 929 → 230 / 896（Base 61 / 194 → 52 / 161，`Siemens.Engineering.Security` / `Umac` 归零）。
- **验证**：离线 1620 项（新增 66 项）；形状检查 V20 1403 / V21 1500（新增 112 / 112 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-18 重跑）：4 处库缺陷修复、`CompareLibraries` 首次真机到达（396 元素）、`CheckLibraryUpdates` 两种 mode、密码策略三套读写、CPU 级 syslog 读取、offline UMC 用户 / 组全流程、证书模板 + SAN 创建 / 删除、匿名用户预览、类型化设备功能权行全部通过。**工程级 `SyslogServerComposition.Create` 让 TIA Portal 崩溃（两次复现）**；`ManagePlcCertificate template` 误要求 `certificateId`——2.7.33 处理，见发布说明"真机结果"。

## [2.7.31] - 2026-09-18

引擎 2.7.31.0（V20/V21 均重建），工具 380 → 386，默认 lite 56 项不变。详见 [v2.7.31](docs/releases/v2.7.31.md)。2.7.30 真机问题修复 + "官方 Openness API 全量对齐"阶段 3 子批次 ③-②：库深层。

- **修复（2.7.30 真机）**：`ManageNetworkDomain` / `ManageTransferArea` / `ManageDeviceUsers` 的 Create/Delete 核对改为从 owner 重新导航取新组合（旧代理在变更后陈旧：创建后按名查不到、删除后枚举抛 `EngineeringObjectDisposedException`），`Create` 以返回的代理读 `Name`；该异常在预期位置就地捕获（`HmiReadSafety.DisposedObjectOnly`），HMI 快照的全局连接失败保护保持原样；`ReadDeviceItemChannels` 的 `attributeNames` 带 `accessMode`，`UpdateDeviceItemChannel` 写前拒绝只读属性。
- **新增（域 `Library`，6 个强类型工具）**：`ReadLibraryOverview`（`GlobalLibrary` 头 / 历史 / 使用产品、类型文件夹树含一致性 `Status`、母版树含 `ContentDescriptions`）、`ReadLibraryType`（`FindType` / `FindVersion`，版本的 Dependencies / Dependents / MasterCopiesContainingInstances / OriginalLibrary）、`ManageLibraryType`（DoNotUse / SetForUpdate / Name、删除、`LibraryType.UpdateLibrary` / `UpdateProject` 四参重载）、`CheckLibraryUpdates`（`ILibrary.UpdateCheck` 消息树）、`SynchronizeLibrary`（`UpdateLibrary` / `UpdateProject` / `HarmonizeProject` / `CleanUpLibrary`，选择集 + 范围，需 `confirmChange`）、`CompareLibraryObjects`（类型 / 版本 / 母版的 `CompareTo` 详细属性行）。扩展 `ManageGlobalLibrary`（`infos` / `openInfo` / `archive`）与 `ImportLibraryTypeDocuments`（强类型 `TypeCreateTransferResults`、`typePath` + `createOptions` 版本导入、STEP 7 目标环境）。
- **审计**：有专用引用 269 → 288，完全未触及 535 → 513，未封装功能类型 253 / 1,006 → 239 / 929（Base 75 / 271 → 61 / 194，`Siemens.Engineering.Library*` 归零）。
- **验证**：离线 1550 项（新增 44 项）；形状检查 V20 1291 / V21 1388（新增 133 / 133 项）；两版 EXE 回归见 `manifest/release-build.json`。真实工程（2026-09-18 重跑）：三处 2.7.30 修复全部通过；`ManageGlobalLibrary infos` / `openInfo`、全局库 `ReadLibraryOverview` 头与母版树、`ReadLibraryType` 三种入口、`ManageLibraryType` 预览、`CompareLibraryObjects` 三种对象通过。**工程库路径全部失败**——`ProjectLibrary` 没有 `Name` 属性（`ReadLibraryOverview` / `CheckLibraryUpdates` / `SynchronizeLibrary` 与原有 `CompareLibraries`）；系统库 `TypeFolder` 为 null 未守卫；`UpdateCheck` 对系统库触发 TIA `NonRecoverableException` 与连接保护。2.7.32 修，见发布说明"真机结果"。

## [2.7.30] - 2026-09-18

引擎 2.7.30.0（V20/V21 均重建），工具 367 → 380，默认 lite 56 项不变。详见 [v2.7.30](docs/releases/v2.7.30.md)。"官方 Openness API 全量对齐"阶段 3 子批次 ③-①：Base 硬件网络深层。

- **新增（域 `Hardware`，13 个强类型工具）**：`ReadIoSystems` / `ManageIoSystem`（`IoController.CreateIoSystem`、`IoSystem.Delete`、`IoConnector.ConnectToIoSystem` / `DisconnectFromIoSystem`、官方动态属性）；`ReadNetworkDomains` / `ManageNetworkDomain`（`SyncDomainOwner` / `MrpDomainOwner` 的域创建、删除、属性、`DomainParticipants.Add`，`MrpInstances` 读出）；`ReadTransferAreas` / `ManageTransferArea`（`TransferAreaComposition.Create/Find`、映射规则、`MulticastableTransferAreaComposition.Create` 四个重载、CCDX 删除语义）；`ReadDeviceItemChannels` / `UpdateDeviceItemChannel`（`ChannelComposition.Find`、`ChannelAddress` / `ChannelWidth`）；`ReadDeviceAddressing` / `UpdateDeviceAddress`（`Address`、`HwIdentifier`、`AddressController` / `HwIdentifierController`，V20 独有的 `AssignProcessImageToOrganizationBlock`）；`ManageDeviceUserGroup`（`DeviceUserGroupComposition.Create`、`UngroupedDevicesGroup`）；`ManageDeviceUsers`（`WebserverUserManagement` / `SimpleWebserverUserManagement` / `OpcUaUserManagement`，密码转 `SecureString` 不回显，`WebserverUserPermissions` 无 `[Flags]` 故按位解码）；`ManagePortInterconnection`（`NetworkPort.ConnectToPort` / `DisconnectFromPort`）。写入默认预览、删除需确认、执行后按原生对象读回。
- **审计**：有专用引用 238 → 269，完全未触及 556 → 535，未封装功能类型 267 / 1,067 → 253 / 1,006（Base 89 / 332 → 75 / 271）。
- **验证**：离线 1506 项（新增 60 项）；形状检查 V20 1158 / V21 1255（新增 168 / 168 项）；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `AutomaticDipCoatingMachine`，引擎 2.7.30.0，2026-09-18）：13 个工具全部到达真实对象。读取：`ReadIoSystems` 三种入口（子网 `PN/IE_1`、PLC 控制器接口、SINAMICS 设备接口）读出 'PROFINET IO-System' #100、4 台已连 IO 设备、IoController `SyncRole`/`PnDeviceNumber`、IoConnector `PnUpdateTime` 2 ms / 看门狗 ×3 / RT；`ReadNetworkDomains` 读出默认同步域与 MRP 域各 5 个参与者；`ReadDeviceAddressing` 读出 CPU 的 14 个登记地址与 21 个登记硬件标识符；`ReadDeviceItemChannels` 读出 DI16 / DQ16 / AI2 / AI4-RTD 的通道与属性名，精确 `Find` 通过；`ManageDeviceUsers opcUa read` 到达（0 用户）；端口服务与传输区组合到达（工程无拓扑、无传输区）。写入并复原：IO 系统 `Number` 100→101→100、端口互连 connect→disconnect、设备用户组 create→rename→deleteEmpty、通道 `Smoothing` 与地址 `StartAddress` 同值写入读回。发现两处 2.7.31 要修的引擎缺陷：变更前取到的域组合代理是陈旧的（`Create` 后按名查不到，`Delete` 后枚举撞 `EngineeringObjectDisposedException`），以及该异常误触发了连接失败保护、阻断后续步骤工具直到重新附加。临时对象已全部删除，工程未保存。详见发布说明。

## [2.7.29] - 2026-09-18

引擎 2.7.29.0（V20/V21 均重建），工具 367、默认 lite 56 项不变。详见 [v2.7.29](docs/releases/v2.7.29.md)。WinCC Unified 阶段（路线图 §2.0 阶段 2）收口。

- **修复**：`ExchangeUnifiedTags export` 在真实工程上恒失败——`HmiTagComposition.Export(dir, name)` 回报的 `FileInfo` 是 `<dir>\<name>`（无扩展名），而 TIA 实际写出 `<name>.hmi.yml`。原生回报路径不存在时按已知扩展名（`.hmi.yml` / `.hmi.js` / `.yml` / `.js` / `.xlsx` / `.xml`）再按同名前缀解析到实际文件，记录里保留 `reportedPath`；两条导出工具都附 `directoryListing`（TIA 留下的全部文件，含 `NameData.yml` 等副文件）。
- **登记**：剩余 Unified 类型全部进入动态覆盖登记表（系统变量、阈值、替代值、审计类、连接/驱动属性、OPC UA 报警类型、记录变量、文本/图形列表、`Cpm`、`Common` / `UIBase` / `IValidator` / `ImportResult`、画面组），`verified` 字段如实区分"真机验证"与"仅形状检查"。审计：完全未触及 599 → 556，动态覆盖 285 → 322，未封装功能类型 288 / 1,185 → 267 / 1,067（Unified 21 → 0）。
- **验证**：离线 1446 项（新增 5 项：无扩展名回报路径解析、同名前缀解析、不可解析拒绝、目录清单）；形状检查 V20 990 / V21 1087 不变；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `HMI_RT_1`，2026-09-18，引擎 2.7.29.0）：变量导出核对通过（`ShiftModel.hmi.yml` 4,400 B，`reportedPath` 无扩展名，该表无副文件）；文本列表 16 / 系统文本列表 18 读出并各原生导出一个（`<名>.hmi.yml` + `<名>.TextLibrary.hmi.yml`）；`HmiTag.Validate()` 3 行诊断；画面组建删；`opcUaAlarmTypes` / `LoggingTags` 组合到达但工程为空，`Cpm` / `HmiConnections` 仍无对象可验。登记表 `verified` 已回写，详见发布说明。

## [2.7.28] - 2026-09-18

引擎 2.7.28.0（V20/V21 均重建），工具 364 → 367，默认 lite 56 项不变。详见 [v2.7.28](docs/releases/v2.7.28.md)。WinCC Unified 阶段子批次 ⑤。

- **新工具 3 个**：`ExchangeUnifiedTags`（变量 WinCC ML `.hmi.yml` 导入导出，`HmiTagComposition.Export/Import`，根或任意组内变量表；此前 Unified 变量表导出走 SimaticML 路径报 unsupported）、`ExchangeUnifiedScriptModules`（全局脚本模块整体/单个导出与导入，`IChromDataExchangeExport`）、`ImportUnifiedOpcUaAlarms`（连接上的 `OpcUaAlarm` 服务：显示名、`GetNodeId`、xml 导入）。
- **登记**：阈值（`ManageUnifiedObjectParts`）、替代值/范围（嵌套写入）、系统变量、驱动属性、审计类、文本/图形列表、记录变量、Cpm、`UIBase`/`HmiBase` 经既有工具到达。
- **修复**：`Object` 类型属性（变量范围/替代值/阈值 `Value`）写入后读回按跨类型文本/数值比较，不再把已写入的值误报为 "readback differs"。真机确认：`HmiThresholdComposition.Create()` 被 TIA 原生拒绝（"New thresholds are not supported"），替代值只对外部变量可设。
- **验证**：离线 1441 项（新增 33 项）；形状检查 V20 990 / V21 1087（新增 54 / 55 项）；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `HMI_RT_1`，2026-09-18）：脚本模块导出/导入预览、变量导入预览、范围嵌套写入读回、系统变量/审计类/工厂视图读取通过；**变量导出核对失败**（TIA 回报的 `FileInfo` 无 `.hmi.yml` 扩展名，文件本身已写出），2.7.29 修复；`OpcUaAlarm` 因工程无 OPC UA 连接未能验证。详见发布说明。

## [2.7.27] - 2026-09-18

引擎 2.7.27.0（V20/V21 均重建），工具 364、默认 lite 56 项不变。详见 [v2.7.27](docs/releases/v2.7.27.md)。

- **2.7.26 真机重跑**（V21 `HMI_RT_1`）：目录 43 类型、schema、update（标量/颜色/部件/顶层多语言）、read、list、delete、隐藏 `EventHandlers` 修复均通过；发现三处缺陷。
- **修复**：① `ManageUnifiedScreenItem create` 与 `ManageUnifiedScreenLayout create`（2.7.18 起）在真实 API 上恒报"not found exactly once"——`Create<T>` 与 `Find()` 返回不同代理，`ReferenceEquals` 恒假；改按名称查回 + 计数核对。② 部件内多语言文本（`Title.Text` 等）此前不被识别，现任意深度拆分并逐条读回。③ `UpdateUnifiedObjectProperties` 不支持颜色与嵌套部件（报警类状态颜色写入失败），现与 `ReadUnifiedObjectProperties` 一起改用 UI 模型嵌套读写。
- **验证**：离线 1408 项（新增 6 项）；形状检查 V20 936 / V21 1032；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `HMI_RT_1`，2026-09-18）全部通过：画面创建、`HmiGauge` 带 `Title.Text` 双语初始属性创建、`HmiAlarmControl` 创建、报警类状态颜色写入并复原、记录 `Settings.LogMaxSize` 写入并复原；`HmiAlarm` / `HmiLogging` / `RuntimeSettings` / `UI.Controls` 登记为动态覆盖，详见发布说明。

## [2.7.26] - 2026-09-18

引擎 2.7.26.0（V20/V21 均重建），工具 362 → 364，默认 lite 56 项不变。详见 [v2.7.26](docs/releases/v2.7.26.md)。官方 API 全量对齐第二阶段（WinCC Unified）子批次 ①：画面对象族。

- **新工具 2 个**：`DescribeUnifiedScreenItemType`（从加载的官方 API 反射出全部 43 个具体画面对象类型的目录与属性 schema：scalar/color/multilingual/part/collection 分类、枚举值、可写性、部件子属性、事件类型；不需要工程）、`ManageUnifiedScreenItem`（任意类型 `Create<T>(name[, containedType])` 创建、list/read/update/delete，嵌套部件与逐语言文本写入并读回）。覆盖 `UI.Shapes` 17 / `UI.Widgets` 16 / `UI.Controls` 13 / `UI.Base` 9 / `UI.Features` 13 / `HmiScreenWindow`。
- **修复**：`HmiSlider` / `HmiToggleSwitch` / `HmiCircleSegment` / `HmiEllipseSegment` 隐藏基类 `EventHandlers` 导致 `Type.GetProperty` 歧义，`ReadUnifiedObjectEvents` / `ManageUnifiedObjectParts` / `ManageUnifiedDynamization` 在这四种对象上此前会失败；改为取最派生声明。
- **覆盖口径**：新增动态覆盖登记表 `scripts/diagnostics/openness-dynamic-coverage.json`，审计脚本单列"动态覆盖"栏；完全未触及类型 885 → 626，Unified 未封装功能类型 113 → 41。
- **验证**：离线 1402 项（新增 49 项）；引擎 API 形状检查 V20 936 / V21 1032（新增 50 / 50 项）；两版 EXE 回归见 `manifest/release-build.json`。

## [2.7.25] - 2026-09-18

引擎 2.7.25.0（V20/V21 均重建），工具 359 → 362，默认 lite 56 项不变。详见 [v2.7.25](docs/releases/v2.7.25.md)。"官方 Openness API 全量对齐"计划的第一步（[路线图 §2.0](docs/development/roadmap.md#20-官方-api-全量对齐计划)）。

- **`Siemens.Engineering.Safety` 全量封装**（12 类型 / 54 成员，按官方 "F-related Openness" 章节逐页对照）：`ManagePlcSafety` 由反射改为强类型，`read` 补齐 `AssignmentOfBlockNumbers`、`SafetySystemVersion` + 可用版本、`EnableConsistentUploadFromFCpu` / `EnableFCommunicationIdTag`、运行组 `FOBNumber` / `FOBCycleTime` / `FOBPhaseShift` / `FOBPriority`、**集体 / 软件 / 硬件 / 通信地址 F 签名**（旧实现从未真正读出）、工程级 `GlobalSettings` 与 CPU `Failsafe_FCapabilityActivated`；新增动作 `generateGlobalFIOStatusBlock` / `cleanSystemGeneratedObjects` / `generateBaseId` / `login` / `logoff` / `setPassword` / `revokePassword`，`createRuntimeGroup` 三种官方重载，`updateSettings` 支持嵌套块号段与精确版本。
- **新工具 3 个**：`ManageSafetyGlobalSettings`（TIA Portal 级四项全局安全设置）、`ReadSafetyBlockSignatures`（逐块 F 签名，单块或整 PLC）、`ExportSafetyPrintout`（官方安全打印件 PDF/XPS）。下载提示 `SafetyProgram` 应答 `ConsistentDownload`。
- **验证**：离线 1353 项（新增 59 项）；引擎 API 形状检查 V20 886 / V21 982（新增 73 / 80 项 Safety 成员核对）；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `+S1-K1`，1510SP F V4.1，2026-09-18）全部通过：集体/软件/硬件签名读出（2.7.24 为空）、11 个 F 块逐块签名、官方安全打印件 PDF 205 KB、GlobalSettings 写入并复位，详见发布说明。

## [2.7.24] - 2026-09-18

引擎 2.7.24.0（V20/V21 均重建），工具 359、默认 lite 56 项不变。详见 [v2.7.24](docs/releases/v2.7.24.md)。

- **真机重跑 2.7.20 的 `DescribeBlockLogic` 修复**（V21 `AutomaticDipCoatingMachine`）：LAD `UNIT_MANAGER_FC` NW5 调用框完整读出（7 个 Bool 输入触点链 + 9 个输出绑定）；SCL `CLOCK_GENERATOR_FB` 的调用与命名常量全部恢复，仅剩位切片 `.%X15` 与 `ENO` 左值两处丢失。
- **按 `SW.PlcBlocks.Access_v5.xsd` 重写 SCL 的 Access / Symbol 渲染**：SCL 导出的 `Symbol` / `Instance` 内部是 Token 序列（`.`、`%X15`、`[`、`]`），现按序渲染，FlgNet 无 Token 时才合成分隔符与 `SliceAccessModifier`；`ENO` 为 `<PredefinedVariable>` 新增渲染；补齐 `Label` / `Statusword` / `DataType` / `Expression` / `Reference` 分支；`*Attribute` / `TemplateValue` 元数据不再泄漏进正文。
- **离线文档工具同族修复**：`RenderPlcBlockDocument` / `ComparePlcBlockDocuments` / `GeneratePlcDocumentation` 的渲染器同样漏了命名常量（输出 `:= ;`）、`REGION` 名（`<Text>`）和 `ENO`，一并修复。
- **验证**：离线 1294 项（新增 7 项）；两版 EXE 回归见 `manifest/release-build.json`。引擎 2.7.24.0 真机重跑通过：`CLOCK_GENERATOR_FB` 整块与源码逐字一致，`UNIT_MANAGER_FC` 无回归。

## [2.7.23] - 2026-09-18

交付 2.7.23，引擎沿用已验证的 2.7.20.0，应用界面与 2.7.22 相同。详见 [v2.7.23](docs/releases/v2.7.23.md)。

- **配置器截图裁切修复**：`CapturePage` 按面板尺寸建位图却在其外边距偏移处绘制，每张界面截图都少了右侧和底部各 18px，右上角状态胶囊被切掉一截。位图尺寸现在含两侧外边距。只影响界面检查用的 PNG，应用布局未变。
- **验证**：配置器 88 项（新增 2 项）；引擎沿用 2.7.20 的离线 1287 项。

## [2.7.22] - 2026-09-18

交付 2.7.22，引擎沿用已验证的 2.7.20.0。详见 [v2.7.22](docs/releases/v2.7.22.md)。

- **配置器 AI 客户端栏改为卡内滚动**：11 张卡片此前把 B 栏撑高、A 栏被迫留出大片空白。现在卡片区限高三行并在卡内滚动（细滚动条与整体配色一致），A/B 两栏改为底部对齐，按钮行固定在卡片底部，两栏等高。两种模式同样适用。
- **验证**：配置器 86 项（新增 2 项）；引擎沿用 2.7.20 的离线 1287 项。

## [2.7.21] - 2026-09-18

交付 2.7.21，引擎沿用已验证的 2.7.20.0（运行文件与工具清单未变）。详见 [v2.7.21](docs/releases/v2.7.21.md)。

- **配置器改为单页双栏连接控制台**：深色编号侧栏 + 三页（虚拟机服务端 / AI 客户端 / 本机连接）改为一页两栏（A 服务端 / B AI 客户端），右上角分段开关切换 **虚拟机 ↔ 宿主机**（HTTP）与 **同一台电脑**（stdio）；stdio 模式自动隐藏地址、密钥和服务按钮。新增两栏之间的连线条（客户端 → `IP:端口` 或 `stdio · 无需网络` → 服务侧，状态 idle / running / local）和带条目计数的 `ACTIVITY LOG` 卡片；配色由蓝色改为米白底 + 墨绿强调。
- **地址与密钥合并**：原两页各一份的地址/端口/密钥合并为一组字段加一条共用密钥条；新增“保存两端配置”，客户端侧连接信息总能保存，服务端配置仅在本机装有 TIA 与引擎时写入，宿主机上校验失败只记日志不再弹错。
- 客户端卡片、写入位置、条目格式、服务器名（`tia-portal-vm` / `tia-portal`）与网络权限、启动/停止、测试连接、备份和密钥加密逻辑均未改变。
- **验证**：配置器 84 项（新增 4 项）；引擎沿用 2.7.20 的离线 1287 项。真实客户端与真实虚拟机 UAC 未执行。

## [2.7.20] - 2026-09-17

引擎 2.7.20.0（V20/V21 均重建），工具 359、默认 lite 56 项不变。详见 [v2.7.20](docs/releases/v2.7.20.md)。

- **`DescribeBlockLogic` 渲染丢失修复**（真实工程连机测试发现）：SCL 网络里的调用（`TIME_TCK()`、`TIME_TO_DINT(#a - #b)`、`LIMIT(MN := …)`、多重实例 `#inst⟨FB⟩(…)`）、`LocalConstant`/`GlobalConstant` 命名常量、`%I1.3` 绝对地址、数组下标此前整段丢失或被压成 `#a.b`，工具却照样报 `success`；LAD 网络里 `<Call>` 块调用不被识别，整段退化成孤立触点清单。现 SCL 递归渲染 `CallInfo`/`Parameter`/`Instance`/`Constant Name`/`Address`，LAD 收集 `<Call>` 为调用框并按 `Parameter Section` 判断引脚方向，输出 `CALL "FB"[inst](pin=操作数, pin=⟨触点链⟩)`；块 Bool 输出驱动线圈时内联为 `CALL "FB"[inst].pin`；新增 `Not` 元件；MOVE/定时器框的上游按 `in → IN → en` 回溯（原只查 `in`，EN 触点链丢失）。
- **配置器：客户端卡片重排，加入国产模型与 Grok**：AI 客户端页按 CLI 在前、Claude Code 与 Codex 居首、IDE 在后排列；去掉 Windsurf、Cline、Claude Desktop · Chat（含 `mcp-remote` 桥接与 `tia-portal-vm-chat`）。新增 **通义千问**（写阿里 Qwen Code `%USERPROFILE%\.qwen\settings.json`，`httpUrl`）、**Kimi**（写 Kimi Code CLI `%KIMI_CODE_HOME%\mcp.json`，默认 `~\.kimi-code\mcp.json`）、**腾讯元宝**（写 CodeBuddy Code CLI `~\.codebuddy\.mcp.json`，`type: http`）、**DeepSeek / 智谱清言 / Grok**（写 OpenCode `~\.config\opencode\opencode.json` 的 `mcp` 节，`type: remote`；本机 `type: local` + 单数组 `command`；三张卡片共用一个文件只写一次）。这些模型的聊天 App 不支持 MCP，卡片按模型命名、在所写客户端里选模型即可。豆包（Trae）因全局配置位置未公开且远程仅 SSE 未加入。卡片 8 → 11。未在真实客户端上联调。
- **验证**：离线 1287 项（新增 13 项，含反向哨兵）；配置器 80 项（新增 22 项）。

## [2.7.19] - 2026-09-17

引擎 2.7.19.0（V20/V21 均重建），工具 350 → 359，默认 lite 56 项不变。详见 [v2.7.19](docs/releases/v2.7.19.md)。

- **PLCSIM Advanced 通道**（路线图 #1 / S2）：`ReadPlcSimAdvancedInstances`、`ManagePlcSimAdvancedInstance`（register/powerOn/run/stop/powerOff/memoryReset/unregister）、`ReadPlcSimAdvancedTags`、`WritePlcSimAdvancedTags`、`RunPlcSimAdvancedTestScenario`（写输入 → 单步推进周期 → 断言输出的闭环场景）。官方 `Siemens.Simatic.Simulation.Runtime` API 在运行时定位（`apiPath` → `PLCSIMADV_API_PATH` → 安装目录最新版本）并经反射调用，与 Openness 同样不随包分发；未安装时明确返回 `ApiNotFound`。改变实例/写值/跑场景默认预览且需确认参数。新增分类域 `Simulation`（runtime 大类）。**本机无 PLCSIM Advanced，未做真实运行验证**。
- **离线文档与预检**（S3 / #8 / #9）：`RenderPlcBlockDocument`（SimaticML/SCL → Markdown：接口表、SCL 网络由 StructuredText 令牌还原、LAD/FBD 网络给出部件清单 + Mermaid 流程图）、`GeneratePlcDocumentation`（导出目录 → 单文件手册：索引表、调用交叉引用、逐块渲染）、`LintPlcSclSource`（13 条启发式规则：块关键字配对、括号、`=`/`:=`、缺分号、GOTO、WHILE TRUE、嵌套深度、行长、空白、`;;`、未闭合注释、TODO 标记）。均不需要 TIA 会话；lint 结论不是编译判决。
- **AML 生成**（#6 / S5 的生成半边）：`BuildDeviceAmlDocument` 由 JSON 规格生成 CAEX 2.15 硬件描述供 `ImportDeviceAml` 使用；推荐传入 `ExportDeviceAml` 导出的参考文件以逐字复用版本匹配的头部与角色类库，未传参考时使用内置骨架并标记 `importVerified=false`。
- **写保护钩子与审计日志**（S7）：插件新增 `hooks/tia-write-guard.ps1`（PreToolUse）。按 `tools-list.json` 的操作类型审计所有非只读调用到 `%LOCALAPPDATA%\TiaMcpServer\audit\tool-calls.jsonl`（密码类参数脱敏），并拒绝真实 ONLINE-WRITE 调用（下载、上载、运行时写值、PLCSIM 变更；预览调用放行），除非设置 `TIA_MCP_ALLOW_ONLINE_WRITE=1`；`TIA_MCP_WRITE_GUARD=0` 关闭。自检脚本 `scripts/checks/Test-WriteGuard.ps1`（16 项）接入 `Build-Release`。
- **分类修正**：`WritePlcWebVars`、`WriteUnifiedRuntimeTags`、`SetPlcWebOperatingMode`、`UnifiedOpenPipeRequest`、`UploadStationFromPlc`、`UploadDeviceParameters` 的操作标签由 WRITE 改为 ONLINE-WRITE（它们改变真实运行时/设备，2.7.18 标错）；ONLINE-WRITE 工具现为 10 个。
- **E8**：`TiaMcpServer.csproj` → `TiaMcpServer.V21.csproj`，`HttpTests.csproj` → `TiaMcpServer.HttpTests.csproj`（程序集名不变），脚本与文档同步。
- **发布流程**：`Publish complete release` 工作流除手动触发外，推送 `vX.Y.Z` 标签也触发，标签必须等于 `manifest/delivery.json` 的版本。
- **验证**：离线 1273 项；API 形状检查 V21 902 / V20 813；实际 EXE 回归两版全过；配置器 58 项。**真实工程与真实 PLCSIM Advanced 验收未执行**。

## [2.7.18] - 2026-09-17

引擎 2.7.18.0（V20/V21 均重建），工具 298 → 350，默认 lite 56 项。详见 [v2.7.18](docs/releases/v2.7.18.md)。

- **下载缺陷修复**：`DownloadToPlc` 此前把 `UserManagementDownload`、`AlarmTextLibrariesDownload`、`DownloadCertificate` 当复选框处理，而它们在 V20/V21 都是 `CurrentSelection` 选择型，等于从未应答；另有 27 种下载提示类型完全未处理。现在委托按提示的真实形态（枚举选择 / Checked / SetPassword）应答，调用方 `promptAnswersJson` 优先，内置默认其次（破坏性提示默认 NoAction/NoChange），密码类由专用参数提供，其余提示连同 TIA 提示文本记入 `Meta.promptsUnanswered`。新增 `userManagementMode`、`promptAnswersJson`、`moduleAccessPassword`、`blockBindingPassword`、`masterSecretPassword` 参数。
- **新增 52 个官方 Openness 工具**（全部默认预览、精确名称、写入需确认参数、V20 缺失成员时明确 NotSupported）：设备传输（可达设备扫描、站上载、参数上载、下载到文件夹/存储卡镜像）；PLC 块服务（块保护、DB 快照/实际值、程序升级、指纹、报警实例文本导入、报警文本列表）；工程安全与协作（UMAC 用户/角色/权限读写、工程保护读取、多用户会话、库比较、离线工程比较、Portal 设置）；硬件服务（通信连接 CRUD、监视/强制表 Web 访问、系统诊断设置交换、OPC UA 访问控制、AML 导入、硬件特性探测）；Unified UI 对象模型（全部事件/属性事件枚举、部件 CRUD、完整动态化类型、画面布局/尺寸/删除、列表定位、报警外观与审计设置读取）；Motion/ProDiag/经典 HMI（轴配置与原生操作、ProDiag 对象、VB 脚本/周期/文本图形列表/全球化/面板）。
- **新增运行时通道**（不经 Openness）：S7 Web 服务器 API（官方 `Siemens.Simatic.S7.Webserver.API` 3.3.76，MIT）读写变量、诊断、受确认的运行模式切换；WinCC Unified Open Pipe 命名管道读写变量、读报警、原始请求，消息格式按官方手册核实。
- **新增离线分析**：`ComparePlcBlockDocuments`（剥离时间戳/UId 的语义 diff + 结构差异）、`ScanPlcSourceAnnotations`（TODO/FIXME 扫描）、`ExtractPlcBlockMetrics`；`templates/plc/scl-examples/FB_SelfTest_Template.scl` 自测试骨架。
- **工具分类**：引擎内 `ToolTaxonomy` 作为唯一事实来源——7 个大类（session / project / plc / plc-online / hardware / hmi / runtime）、26 个域、规范操作类型（SESSION/READ/WRITE/FILE/OFFLINE/ONLINE/ONLINE-WRITE/EXECUTE）；新增 `ListToolCategories`，`FindTools` 支持 `category`/`domain` 筛选；`tools-list.json` 带分类字段，`tool-matrix.md` 改由清单按大类→域生成并纳入发布流程。原裸 `PLC` 域并入 `PLC-Software` / `PLC-TechnologyObjects`，8 种历史操作写法统一。
- **引擎待办清理**：14 个大写域名标签统一；移除输入已不存在的 `RunV2PlanCompletionAudit` 工具与 CLI 标志；Doctor 提示、授权指南死工具名修正；硬编码开发机路径改为运行时安装根解析；V20 csproj 的 `TiaPortalLocation` 可被覆盖；移除无对象的 csproj 项。
- **验证**：离线 1158 项通过；官方程序集 API 形状检查 V21 847 / V20 758；实际 EXE HTTP 28、HMI 遍历 18、资源发现 42 等两版全过；配置器 58 项。**真实工程验收未执行**；新工具按 API 形状与离线逻辑验证，状态见能力文档。
- 许可证：新增 Webserver API、Newtonsoft.Json、MimeMapping 条目与原文。
- 配置器：安装目录改为自动探测（TiaPortalLocation 环境变量 → 注册表 TIAP{版本}\TIA_Opns → 默认安装目录，与引擎同一顺序），首次打开与切换版本时自动填入，新增“自动检测”按钮；只接受含对应 Openness DLL 的目录。配置器隔离测试 58 项。

### 随 2.7.18 发布的仓库整理与审计

以下改动与上面的引擎变更同属 2.7.18。

- 插件配置：`tia-portal` MCP 服务器定义内联进 `.claude-plugin/plugin.json`，删除根目录 `.mcp.json`。`${CLAUDE_PLUGIN_ROOT}` 只在插件上下文展开，根目录文件被 Claude Code 当项目级配置加载时路径不展开、服务器启动失败；直接打开仓库或解压包不再触发该错误。
- 清理：删除已并入分类却仍随包分发的 `手册/`、5 个标 `_deprecated` 的 `templates/plc/plcbuild-json/fb_*|fc_*.json`（对应 `.scl` 保留）、被 `Generate-ToolsListFromAssembly.ps1` 取代的 `Generate-ToolsList.py`；`design-qa.md` 移入 `docs/archive`；`package-manifest.json` 去掉与 `plcTemplateLibrary` 重复的 `plcNetworkPatterns` 入口。引擎源码与工程文件未改动（受 `Validate-Bundle` 源码哈希约束，相关待办见路线图）。
- 过期引用：修正 bug 模板的 `bin/Release` 路径与版本示例、蓝图 README 的 `scaffold_spec_start_stop.json` 文件名、HttpTests README 版本参数、CHANGELOG 历史版本锚点；删除 SKILL.md 中不存在的 `ImportBlocksFromScl` 别名说明；`natural-language-recipes.md` 与 `openness-limitations.md` 不再把刻意未注册的 `SetForceTableEntry` 当作可用工具。
- 官方 API 对照：新增 `scripts/diagnostics/Audit-OpennessCoverage.ps1`，用本机 V21 PublicAPI 的 18 个官方 XML 对引擎源码做逐成员词法盘点，结果与确认缺口写入 `docs/reference/openness-coverage.md`。`openness-limitations.md` 更正"在线设备发现仅限工程配置"的错误（`GetAccessibleDevices()` 存在），补充 RUN/STOP 只能作为下载配置附带发生、下载提示覆盖情况与下载到 Windows 文件夹；`capabilities.md` 追加此前两侧均未记录的缺口（设备上载、可达设备扫描、`WatchAndForceTableAccessManager`、`UpdateProgram`、`ProgrammingLanguage.ST` 兼容义务等），并把与 `release-build.json` 重复的验证计数改为指向。
- 审计中确认的下载提示缺陷（`UserManagementDownload` 被当复选框、43 种提示只应答 16 种）已在本版修复，见上。
- 许可证：新增 `docs/licenses/THIRD-PARTY-NOTICES.md` 逐项列出 `runtime/` 全部第三方程序集的许可证，补齐 Sharp7（MIT）、Workstation.UaClient、YamlDotNet、.NET Foundation、Siemens.Collaboration.Net 原文。Siemens 程序集适用免版税软件条款而非 MIT，再分发边界需维护者评估。
- 新增 `docs/development/roadmap.md`：11 项需在 TIA 机器重建的引擎待办、官方 API 缺口优先级、第三方集成候选（含许可证红线）、合规事项。
- 其他：`docs/README.md` 加入语言约定；`tools/README.md` 补 `vci-watch`；`templates/hmi/README.md` 补漏的 `unified_basic_event_log` 行；英文 README 为中文界面按钮加注释；`.gitignore` 增加 TIA 工程/归档扩展名与 `.claude/`。
- 根目录瘦身：`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md`、`SECURITY.md` 移入 `.github/`（GitHub 同样识别）；`examples/` 目录并入 `docs/getting-started/cursor.example.json` 与配置指南；本机 PublicAPI 副本移出仓库目录（`.gitignore` 保留防护）。根目录只剩说明、许可证、Git/插件配置与配置器 EXE。
- 目录整理：`docs/tools/` 四篇并入 `docs/guides/`；`capabilities.md` 按 2.7.14 / 2.7.15 / 2.7.18 工具族分节；删除被 `Audit-OpennessCoverage.ps1` 取代的 `Audit-OpennessSurface.py`，`Test-DownloadRouteSelection.ps1`（新增 `-PublicApiDirectory`）与 `Test-MatchPlcName.ps1` 接入 `Build-Release`；引擎源码按职责分入 `ModelContextProtocol/Tools|Builders/`、`Siemens/Portal|Hmi/`，命名空间不变，全部 `.cs` 统一为无 BOM UTF-8 并在 `.gitattributes` 固定行尾；CI 死引用检查登记新工具描述里点名的原生 API 成员。

## [2.7.17] - 2026-09-17

- 重整文档、示例及脚本目录，合并重复指南和历史发布说明，删除过期脚本、临时清单及现场原始数据。
- 中英文 README 统一为入口，更新所有迁移引用、skill、插件及蓝图，新增文档链接与入口校验。
- 交付包仅保留 runtime/v20 与 runtime/v21，移除旧 bin 路径下的重复运行文件；旧配置可用 GUI 重新保存。
- 保持引擎 2.7.15.0 与其原验证记录不变，独立验证配置器及完整包。详情见 [v2.7.17](docs/releases/v2.7.17.md)。

## [2.7.16] - 2026-09-17

- 新增独立 WPF 图形配置程序 `TiaMcpConfigurator.exe`：按参考设计实现深色编号侧栏、浅色内容区、分类客户端卡片和右侧主操作；配置虚拟机 HTTP 服务和本机/远程 AI 客户端，无需手写 CMD/BAT。
- 三页导航、已选客户端计数、密钥显示与占位提示、服务状态联动；窄窗口自动调整客户端卡片列数，内容可滚动。本机连接显示实际 stdio 传输。
- 支持多选 Claude Code、Claude Desktop、Codex、Cursor、VS Code、Gemini CLI、Windsurf、Cline；按各客户端 JSON/JSONC/TOML 格式合并并备份。Claude Desktop Chat 远程模式使用需 Node.js 的 mcp-remote 桥接。
- 支持 V20/V21、密钥生成与隐藏、按 Windows 用户加密保存服务端设置、合并和备份 Claude 配置、授权指定用户监听及局域网防火墙、服务启动/停止与日志、HTTP 鉴权和就绪检查。
- 图形入口在启动前独立检查 HTTP 监听，直接显示权限/端口错误；原引擎二进制未更改。虚拟机真实 TIA 工程联调待验证。

- 删除被 GUI 替代的根目录配置 BAT 和 tia CMD；CLI 直接调用 runtime 下的 EXE，保留工程生成、预热和取证脚本。
- 交付包版本 2.7.16，原 V20/V21 引擎保持 2.7.15.0；分别记录 GUI 与引擎的测试日期、输入及二进制哈希，完整包仍包含两版运行依赖。

## [2.7.15] - 2026-09-15

- 新增 38 个 HMI/PLC/工程/选件专用入口，总计 298 个工具；具体边界见 [实现与缺口清单](docs/reference/capabilities.md)。
- 新增公开属性定点访问、原生结果失败识别、文件非空/SHA-256 校验及事件预览 token；写入默认预览。
- 提供完整 V20/V21 交付包。离线与程序集回归通过；真实工程和选件验收待环境，不宣称官方 API 全覆盖。

## [2.7.14] - 2026-09-15

- 新增 PLC 类型组创建及四类 PLC 用户组管理、工艺对象管理、离线监视表导入、硬件删除/复制/移槽、软件单元与对象发布、库主副本及版本管理、Safety 离线工程管理、证书管理。新增写入工具默认 dryRun 预览，不自动保存或下载。
- 新增 Unified 画面/变量表组管理，报警类别、离散/模拟报警、报警/数据归档的原生对象管理，以及文本/图形列表原生导入和分页标量属性读取。复杂引用与列表条目不冒充完整读取；V20 图形列表 API 缺失明确失败。
- 新增离线操作边界测试与 V20/V21 官方 API 形状检查。新工具尚未在真实工程执行写入验收，不宣称覆盖全部 Openness API；详见 docs/reference/capabilities.md。

## [2.7.13] - 2026-09-15

- 新增 DeleteEmptyPlcBlockGroup：默认预览，精确定位空用户块分组；拒绝根组、非空组、重名、在线及未知状态，实际删除取得独占访问并回读确认。不递归删除、不自动保存。
- 两版实际 EXE 覆盖删除保护、预览、重检查与删除后验证；尚未在真实工程执行删除。

## [2.7.12] - 2026-09-15

- 共用 SoftwareContainer 解析增加类型化工程组/设备枚举后备路径，覆盖仍走旧入口的导入、建组等工具；严格匹配软件名，拒绝单 PLC 猜测、子串/正则猜测、重名及不完整扫描。
- 实际 EXE 验证入口接线和目标选择边界；未在真实工程执行写入、在线或下载验证。

## [2.7.11] - 2026-09-15

- 单块读取/导出复用列表器的 PLC 根组解析，修复旧软件解析入口导致的 Block not found。支持用户组路径、根组前缀、反斜杠及唯一裸名；同名歧义明确失败。
- 实际 EXE 回归覆盖 OPMODE01_FC 的分组路径及调用入口；真实工程仍待用户验证。

## [2.7.10] - 2026-09-15

- PLC 列块、列树、列类型及批量导出改为复用 GetSoftwareInfo/GetPlcTagTables 的 GetPlcSoftware 入口，恢复精确解析未命中时的工程级 PLC 枚举与名称匹配后备路径。
- 保留 2.7.9 的属性完整性和根组错误诊断；本地测试不能代替用户分组 ET 200SP F-CPU 的实际回归。

## [2.7.9] - 2026-09-15

- PLC 列块/列树及 GetTypes 使用每次调用的新鲜精确解析，避免依赖旧 SoftwareContainer 缓存；解析、Software、BlockGroup/TypeGroup 阶段分别诊断。
- 可选块/UDT 属性失败返回 null/明确占位及 failureCount、failures、dataComplete，不再使整个用户块清单失败；IPC/句柄失效继续立即终止。
- 明确用户块组/PLC 类型的采集范围，不再将未读取的系统块和外部源宣称为完整树。
- 此次 F-CPU 实际故障的根因尚未凭完整错误堆栈确认；真实工程回归独立于本地测试。

- 批量文档导出沿用修补后的 GetBlocks；底层列表读取失败或未打开工程时不再返回成功空结果。

## [2.7.8] - 2026-09-14

- 修复设备分组内 PLC 的裸软件名称解析：`+S1-K1` 等名称按字面递归匹配，与软件信息接口使用同一基础解析入口；重名明确报歧义，不采用模糊匹配或单 PLC 猜测。保留硬件遍历和块根组访问的异常，避免误报软件/程序块不存在。尚待新版在用户工程上验证。
- 新增 `ReadUnifiedRuntimeSettings`、`UpdateUnifiedRuntimeSettings`：读取和修改 Unified HMI 启动画面及受支持的根运行设置。默认预览，验证画面完整路径及名称唯一性，使用并发校验令牌并逐项及最终回读；不自动保存、编译、下载或重启。见 [接口说明](docs/guides/hmi/runtime-settings.md)。
- 新增 `ReadUnifiedGraphicSelection`：通过准确对象名称列表整体定位选择范围，分页读取原始坐标、尺寸、一层工程归属和可用的关系元数据；句柄失效后停止并阻止后续读取。
- 新增离线 `CompareUnifiedGraphicSelections`：核对前后完整分页证据并报告各对象坐标变化，拒绝缺页、缺字段和范围混用。
- 普通图形组合的真实成员、整体变换和坐标联动原因仍未验证，明确返回缺口，不将选择范围当作原生组合。见 [接口说明](docs/guides/hmi/graphic-selection.md)。
- 完整 V20/V21 运行包包含上述功能，文件版本统一为 2.7.8.0；真实工程验收状态见 [本版说明](docs/archive/release-notes.md)。

## [2.7.7] - 2026-09-14

- HMI 快照按准确路径定位页面；句柄释放、IPC 或不可恢复异常后停止后续远程读取，保留已有证据并明确报告缺口，避免自动重试或重新绑定。
- 增加 HMI 读取阶段日志、连接健康状态和显式重新绑定后的缓存清理；暂时隔离诊断控件的特定属性读取。尚未确认博图异常退出的具体根因。
- 新增 `UpdateUnifiedGlobalScript`，支持精确定位 Navigation 等已有 Unified 全局模块，默认预览、原生备份、确认令牌、单模块导入及完整正文回读验证。
- 修复通用修改接口的 `HmiScripts` / `HmiScriptModule` 路径定位，以及原生导入导出参数的文件/目录类型转换，保留写入限制。
- 完整 V20/V21 包文件版本为 2.7.7.0；包含最新程序、依赖、配置入口、源码、测试和文档。真实工程 Navigation 导入及本次稳定性修复尚未完成验收，V20 原生 Unified 导入能力尚未确认。

## [2.7.6] - 2026-09-14

- 库类型没有文档导出格式时，对非脚本类型增加官方 `LibraryTypeVersion.Export(FileInfo, ExportOptions.WithReadOnly)` XML 路径，只调用一次；已有文档导出异常时不追加尝试。
- 区分 `exportAttempted=false`（未执行）、原生调用返回、文件读取完整与内部内容未验证；未执行不再报 `NativeExportEmpty`，内部对象/变量数量保持未知。
- XML 原文、SHA-256 及结构化路径可分页读取。库版本 XML 可能只有元数据，明确返回 `LibraryXmlContentUnverified`，不能冒充面板内部绑定已读全。
- 完整 V20/V21 包文件版本为 2.7.6.0。新增离线与实际 net48 EXE 的 XML 路径回归；真实工程中的面板导出仍待部署新版验证。

## 历史版本

- [v2.7.5](docs/archive/release-notes.md#v275)
- [v2.7.4](docs/archive/release-notes.md#v274)
- [v2.7.3](docs/archive/release-notes.md#v273)
- [此前完整更新日志（仓库既有提交，保持原文）](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6a7298cbc08dd59fd08864d56a728a4da3435ed8/CHANGELOG.md)

[Unreleased]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.8.1...HEAD
[2.8.1]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.8.0...v2.8.1
[2.8.0]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.62...v2.8.0
[2.7.62]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.61...v2.7.62
[2.7.61]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.60...v2.7.61
[2.7.60]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.59...v2.7.60
[2.7.59]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.58...v2.7.59
[2.7.58]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.57...v2.7.58
[2.7.57]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.56...v2.7.57
[2.7.56]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.55...v2.7.56
[2.7.55]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.54...v2.7.55
[2.7.54]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.53...v2.7.54
[2.7.53]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.52...v2.7.53
[2.7.52]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.51...v2.7.52
[2.7.51]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.50...v2.7.51
[2.7.50]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.49...v2.7.50
[2.7.49]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.48...v2.7.49
[2.7.48]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.47...v2.7.48
[2.7.47]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.46...v2.7.47
[2.7.46]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.45...v2.7.46
[2.7.45]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.44...v2.7.45
[2.7.44]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.43...v2.7.44
[2.7.43]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.42...v2.7.43
[2.7.42]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.41...v2.7.42
[2.7.41]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.40...v2.7.41
[2.7.40]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.39...v2.7.40
[2.7.39]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.38...v2.7.39
[2.7.38]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.37...v2.7.38
[2.7.37]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.36...v2.7.37
[2.7.36]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.35...v2.7.36
[2.7.35]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.34...v2.7.35
[2.7.34]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.33...v2.7.34
[2.7.33]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.32...v2.7.33
[2.7.32]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.31...v2.7.32
[2.7.31]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.30...v2.7.31
[2.7.30]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.29...v2.7.30
[2.7.29]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.28...v2.7.29
[2.7.28]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.27...v2.7.28
[2.7.27]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.26...v2.7.27
[2.7.26]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.25...v2.7.26
[2.7.25]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.24...v2.7.25
[2.7.24]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.23...v2.7.24
[2.7.23]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.22...v2.7.23
[2.7.22]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.21...v2.7.22
[2.7.21]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.20...v2.7.21
[2.7.20]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.19...v2.7.20
[2.7.19]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.17...v2.7.19
[2.7.18]: https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/releases/v2.7.18.md
[2.7.17]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.16...v2.7.17
[2.7.16]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.15...v2.7.16
[2.7.15]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.14...v2.7.15
[2.7.14]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.13...v2.7.14
[2.7.13]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.12...v2.7.13
[2.7.12]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.11...v2.7.12
[2.7.11]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.10...v2.7.11
[2.7.10]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.9...v2.7.10
[2.7.9]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.8...v2.7.9
[2.7.8]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.7...v2.7.8
[2.7.7]: https://github.com/asckye/TIA_Portal_Openness_MCP/compare/v2.7.6...v2.7.7
[2.7.6]: https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v2.7.6
