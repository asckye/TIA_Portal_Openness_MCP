# 运行时布局与清理设计（G7、P2-05）

[重构计划](refactor-plan.md) · [引擎拆分设计](engine-decomposition.md) · [响应与异常设计](response-and-errors.md) · [验证分层](validation.md)

本页是 G7（运行时与仓库布局解耦）和 P2-05（历史注释、`*Leftovers` 与用户可见文案语言）的设计结论。路径缩写：`E/` =
`tools/tiaportal-mcp/src/TiaMcpServer/`，`L/` = `tools/tiaportal-mcp/src/TiaMcp.Logic/`，`S/` = `tools/tia-openness-studio/src/`。
数字为 2026-10-03 的源码统计。

## G7：运行时资源定位

### 现状

交付包由 `Package-Release.py` 取全部 `git ls-files` 加清单中的运行文件组成，因此 `reference/`、`templates/`、`scripts/`、`tools/`
和 `manifest/` 都在包内；现有向上探测在交付包中基本可用。问题在于：

- 标记不统一，共 5 种（`plc_tools_bridge.py`、`manifest/delivery.json`、`manifest/package-manifest.json`、`templates`+`tools`、
  `TMP_EXPORT`+`tools`），查找深度各不相同（不限、4、12）。
- 4 处硬编码开发输出路径（`E/Siemens/EngineRouter.cs`、`S/TiaOpenness.Gui/Configuration/ConfigCore.cs`、
  `S/TiaOpenness.Client/BridgeClient.cs` 的源码回退已成死代码）。
- 不限深度的向上查找可能越出交付包；`Package-Release.py` 的暂存目录在仓库内，暂存缺文件时会落到仓库根，掩盖缺陷。
- CI 没有任何证据证明安装布局下引擎能找到资源。
- 缺陷：git worktree 的 `.git` 是文件，`S/…/UpdateCheck.cs` 只认目录，会在工作树中误启用“运行更新”。

| # | 查找对象 | 使用者 |
|---|---|---|
| R1 | `EcosystemFiles.RepositoryRoot`（`TIA_MCP_REPOSITORY_ROOT` 或向上找 `plc_tools_bridge.py`） | 引擎：Openness 指南、伴随 Python 工具、V21 生态目录、导出审计 |
| R2 | `FindInstallRoot`（向上 4 级找 `manifest/delivery.json`） | 引擎 CheckForUpdate |
| R3 | `SpecLoader.FindBundleRoot`（12 级，`templates`+`tools`） | 引擎 CLI gen/patch |
| R4–R6 | `GetWorkspaceRoot`、HMI 模板默认目录、以 `workspaceRoot` 为参数的套件（私人工作区残留） | CLI 报告动词与两个 MCP 工具 |
| R7 | `EngineRouter.FindSiblingExe`（两套硬编码布局） | 引擎改道、doctor、CLI 配置写入 |
| R10 | LegacyHost：exe 旁 `release-key.txt` 与 `worker/`，已按安装布局定位 | Foundation 宿主 |
| R11 | Studio `FindBundleRoot`、`ConfigCore.Engine`（路径写入客户端配置）、`UpdateCheck` | 桌面端 |
| R12 | Launcher 保留 C# 5 查找实现，`Check-BundleLayout.py` 校验全部相对探测路径与解析器安装锚点及 GUI 输出文件名一致 | 桌面端 |
| R13–R14 | `BridgeClient.LocateBridge`、`SessionFactoryLoader` | 桌面端 |

### 目标设计

- `tools/openness-shared/BundleLayout.cs`（只依赖 BCL，链接进 `TiaMcp.Logic`、`TiaOpenness.Core`、`TiaOpenness.Gui`；不编入织入的引擎
  EXE）：资源 ID → 包根相对路径的代码表，根标记为 `manifest/package-manifest.json`。`scripts/checks/Check-BundleLayout.py` 检查表中
  路径都存在于 git 树，且与 `Validate-Bundle.ps1 -Strict` 使用同一份清单。
- 根的解析顺序：显式覆盖（引擎沿用 `TIA_MCP_REPOSITORY_ROOT`，名称与文案不变；Studio 沿用 `ShowConfiguration(bundleRoot)`；
  LegacyHost 沿用命令行参数）→ 安装锚点（`runtime/<RuntimeDirectory>`、`runtime/studio`、`runtime/studio/bridge`）→ 开发锚点（枚举出的
  `bin`/`bin-v20` 与 Gui/Bridge 输出目录）→ **兼容回退：保留各调用方原有的探测**（决策 D-G7-3）→ 原有的 null 或异常。
- 受支持布局下输出逐字节不变：用与原来相同的原语计算路径（不额外规范化），异常类型和首行文案不变；注意
  `L/…/EcosystemFiles.cs` 用 `Substring(folder.Length+1)` 生成文档 ID。
- R4–R6 不纳入（不是交付资源，R5 在连 TIA 的 CLI 路径上），阶段 6 删除或改为显式输入。

### 离线证明

- 布局矩阵单元测试：旧实现逐字复制为基准，与新实现在交付包、仓库+runtime、bin/bin-v20、worktree、CI、仅 runtime、仓库内嵌套
  暂存、含空格/中文/尾斜杠的路径等布局下比较返回字符串与异常。
- 重定位交付包实跑：把交付包暂存到仓库之外，V20/V21 各用 master 与新 EXE 调用 ReadOpennessGuidance、ReadV21EcosystemCatalog、
  `CheckForUpdate(repository="x")`（提前返回，不联网）、RunPlcCompanionTool（catalog）、DecodePlcSimaticMl，以及 CLI
  `gen … --dry-run --json` 和改道；只允许把根路径替换为 `<ROOT>` 后比较原始字节。
- 先在未改动的 master 上把上述只读工具加入 P0-06 并重录基线。
- 常规：契约 0/0/0、P0-06 原始 0 差异、HttpTests 各模式不变、织入全类别不变。

### 任务

| 任务 | 内容 | 时机 |
|---|---|---|
| G7-1 | P0-06 加入 ReadOpennessGuidance、ReadV21EcosystemCatalog 与 bin 布局下的 CheckForUpdate，重录基线 | 现在 |
| G7-2 | `BundleLayout.cs`、单元测试、`Check-BundleLayout.py`，不改调用方 | 现在 |
| G7-3 | Logic 中的 R1 改用解析器 | G7-2 之后 |
| G7-4 | 引擎内 R2、R3、R7 与 `McpConfigInstaller` 改用解析器 | 阶段 3 第 16 步之前 |
| G7-5 | Studio R11–R14 改为查表，修复 worktree `.git` 判断，删除死回退 | P4-03 之前或并入 P4-03 |
| G7-6 | `v21-ecosystem.json` 改为嵌入数据（指南需差分证明后再做） | G7-3 之后 |
| G7-7 | 文档与 CHANGELOG；P3-xx 置为 done，解除 P1-07 目录重组的阻塞 | 最后 |

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
| D-G7-2 | 阶段 0–5 只保留 `TIA_MCP_REPOSITORY_ROOT`；4.0 再引入 `TIA_MCP_BUNDLE_ROOT` 或 `--bundle-root`，旧名保留为别名 |
| D-G7-3 | 不受支持的重定位：阶段 0–5 保留原有探测作为兼容回退，4.0 删除（依据“先兼容，后破坏”的原则） |
| D-G7-4 | 开发锚点编入产品，只认枚举出的输出目录；测试显式给根 |
| D-G7-5 | 先嵌入 `v21-ecosystem.json`，指南需差分证明后再做 |
| D-G7-6 | 安装目录写入（启动日志、崩溃日志、伴随 Python 环境）阶段 0–5 不动，4.0 改到 LocalAppData |
| D-G7-7 | Studio 的 worktree `.git` 判断在 G7-5 中修复（仅 UI 行为） |
| D-G7-8 | 私有工作区默认值阶段 0–5 不动，阶段 6 删除或改为显式输入 |
| D-P5-1…6 | 见上文“注释与 `*Leftovers`”与“用户可见文案语言”；D-P5-3/5（新文本英文、4.0 统一英文）由维护者决定（2026-10-03） |
