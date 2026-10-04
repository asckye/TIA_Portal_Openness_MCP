# 运行时布局与清理（G7、P2-05）

[重构计划](refactor-plan.md) · [引擎拆分设计](engine-decomposition.md) · [响应与异常设计](response-and-errors.md) · [验证分层](validation.md)

本页记录 G7（运行时与仓库布局解耦）的现行实现和 P2-05（历史注释、`*Leftovers` 与用户可见文案语言）的清理规则。路径缩写：`E/` =
`tools/tiaportal-mcp/src/TiaMcpServer/`，`L/` = `tools/tiaportal-mcp/src/TiaMcp.Logic/`，`S/` = `tools/tia-openness-studio/src/`。
P2-05 的初始统计取自 2026-10-03，完成情况另行标明。

## G7：运行时资源定位

### 安装根与开发输出

G7-1…G7-7 已完成。完整交付包可放在仓库之外；安装根不需要 `.git`。交付包由
[`Package-Release.py`](../../scripts/build/Package-Release.py) 取 Git 文件集和清单中的运行文件组成，
`reference/`、`templates/`、`scripts/`、`tools/` 和 `manifest/` 都是包根下的交付内容，不能只复制 EXE。

[`BundleLayout.cs`](../../tools/openness-shared/BundleLayout.cs) 以 `manifest/package-manifest.json` 为根标记，
维护 `BundleResource` 到包根相对路径的代码表。它不依赖 Siemens API，链接进 `TiaMcp.Logic`、
`TiaOpenness.Core`、`TiaOpenness.Client` 和 `TiaOpenness.Gui`，不编入织入的引擎 EXE。

`FindRoot(baseDirectory, explicitRoot)` 的顺序是：非空显式根 → 已知安装锚点 → 已知开发锚点。
显式根必须是绝对路径且含根标记；无效时直接返回 null，不再尝试自动定位。显式根保留原拼写，自动定位使用
`DirectoryInfo` 的父目录路径。解析器不读取环境变量、不检查 `.git`，也不向任意祖先目录搜索。

下表路径均相对包根；`<configuration>` 是 `Release` 或 `Debug`，运行文件与输出目录由构建生成。
版本目录取自 [`TiaVersionCatalog.cs`](../../tools/tiaportal-mcp/src/TiaMcp.Logic/Siemens/TiaVersionCatalog.cs)。

| 锚点 | 支持的基目录 |
|---|---|
| MCP 安装输出 | `runtime/v14sp1`、`runtime/v15.1`、`runtime/v16`–`runtime/v21` |
| Studio 安装输出 | `runtime/studio`、`runtime/studio/bridge` |
| 完整引擎开发输出 | `E/bin-v20/<configuration>/net48`（V20）、`E/bin/<configuration>/net48`（V21） |
| Studio 开发输出 | `S/TiaOpenness.Gui/bin/<configuration>/net10.0-windows` 及其 `bridge` 子目录；`S/TiaOpenness.Bridge/bin/<configuration>/net48` |

`FindResource` 在选定根下检查文件或目录是否存在；不存在返回 null，不另选一个根。
这只是解析器的行为；调用方保留的兼容回退及错误处理见下表。

### 资源与调用方的解析顺序

代码表当前有九项。路径均相对包根，目录项检查目录存在，其余检查文件存在。

| 资源 ID | 相对路径 | 用途 |
|---|---|---|
| `PackageManifest` | `manifest/package-manifest.json` | 安装根标记 |
| `DeliveryManifest` | `manifest/delivery.json` | 引擎与 Studio 的安装版本信息 |
| `OpennessGuides` | `reference/siemens-openness/skills` | `ReadOpennessGuidance` 的指南正文 |
| `OpennessProvenance` | `reference/siemens-openness/UPSTREAM.json` | 指南来源与固定版本 |
| `V21EcosystemCatalog` | `reference/v21-ecosystem.json` | 随包参考副本；查询工具读取嵌入数据 |
| `PlcToolsBridge` | `scripts/ecosystem/plc_tools_bridge.py` | PLC Tools 伴随命令与质量审计 PDF |
| `SimaticMlDecodeBridge` | `scripts/ecosystem/simaticml_decode_bridge.py` | SimaticML 只读解码伴随进程 |
| `UpdateScript` | `scripts/operations/Update-Engine.ps1` | 安装包更新脚本路径 |
| `Templates` | `templates` | CLI 规格文件中 `__BUNDLE__` 引用的模板 |

资源表不包含生成的引擎、桥接或适配器二进制；这些候选路径仍由调用方和版本目录表组合。
以下 R 编号沿用 G7 清点编号；“回退”均指 D-G7-3 保留到 4.0 的兼容路径，不是新增资源的定位方式。

| 使用者 / 资源 | 正常解析顺序 | 兼容回退及未找到时的行为 |
|---|---|---|
| R1：`EcosystemFiles.RepositoryRoot`；指南、Python 桥接、审计 PDF | 非空 `TIA_MCP_REPOSITORY_ROOT` 优先：须为绝对路径且含 `PlcToolsBridge`，返回 `Path.GetFullPath`；否则解析安装/开发根，并检查该桥接文件 | 无显式覆盖且解析未成功时，从引擎基目录逐级向上找 `PlcToolsBridge`；失败抛原 `DirectoryNotFoundException`。无效显式覆盖直接抛错，不回退 |
| V21 生态目录查询 | `ReadV21EcosystemCatalog` 在 V20/V21 引擎均直接读取程序集资源 `TiaMcp.V21Ecosystem.json` | 不读取磁盘副本或根覆盖；包内 JSON 缺失仍可查询 |
| R2：引擎 `FindInstallRoot` / `CheckForUpdate` | 解析器识别的 `runtime/<RuntimeDirectory>` 安装锚点，且根下存在 `DeliveryManifest`；更新脚本相对此根定位 | 从基目录起最多检查 4 层的 `DeliveryManifest`；失败返回 null。开发输出不因解析器识别根而成为安装包；不读取仓库根环境变量 |
| R3：CLI `SpecLoader.FindBundleRoot` | 解析安装/开发根，仍要求根下有 `templates` 与 `tools`；替换 `__BUNDLE__`，根路径斜杠转为 `/` | 从基目录起最多检查 12 层的这两个目录；失败返回 null，token 保留。忽略仓库根环境变量 |
| R7：`EngineRouter.FindSiblingExe`；改道、doctor、CLI 配置 | 在 V20/V21 安装输出中按目标版本的 `RuntimeDirectory` 找同名 EXE；完整引擎 Release 开发输出按目标 `EngineOutputDirectory` 找同名 EXE | 原 `bin`/`bin-v20` 的 `Release/net48` 匹配，再检查当前 `v` 加数字目录的同级目标；候选必须存在且不是自身。失败返回 null；`McpConfigInstaller` 在当前版本或找不到同级引擎时使用自身 EXE。忽略仓库根环境变量 |
| R10：Foundation LegacyHost | 版本参数与 EXE 旁存在的 `release-key.txt` 校验一致；未指定版本时读该文件。`--worker-exe` 优先，否则使用 EXE 旁 `worker` 中的对应版本 worker | 沿用宿主自身的安装布局和参数校验，不经过 `BundleLayout`，不搜索仓库 |
| R11：Studio 配置页根 | `ShowConfiguration(bundleRoot)` 的显式值优先，否则 `FindBundleRoot` 调用解析器 | 未识别时逐级向上找 `PackageManifest`；失败抛原 `DirectoryNotFoundException` |
| R11：`ConfigCore.Engine` | 校验配置页传入的根，先查 `runtime/<RuntimeDirectory>/TiaMcpServer.exe`；V20/V21 再查对应的 `E/<EngineOutputDirectory>/Release/net48/TiaMcpServer.exe` | 根标记缺失仍保留传入根及同一组候选；失败抛原 `FileNotFoundException`。Foundation 版本没有源码输出候选 |
| R11：Studio `UpdateCheck` | 安装版本由 `FindResource(DeliveryManifest, …, root)` 读取；更新脚本由显式根定位 | 解析失败仍使用传入根下的原路径。`.git` 文件和目录都判为源码工作区，禁用安装更新 |
| R12：兼容 Launcher | 根目录的 `TiaMcpConfigurator.exe` 只查自身目录下 `runtime/studio/TiaOpenness.exe` | C# 5 启动器不链接解析器，无其他相对候选；缺文件沿用原提示 |
| R13：`BridgeClient` | 显式 `bridgeExePath` 优先；否则识别安装/开发锚点，依次查调用方基目录旁和 `bridge` 子目录内的 `TiaOpenness.Bridge.exe` | 保留同样的两个本地候选，再查原相对开发路径下的 Bridge Debug、Release 输出；失败返回 null |
| R14：Studio `SessionFactoryLoader` | 默认构建识别 `runtime/studio/bridge`，从桥接基目录下 `adapters/v<key>/TiaOpenness.Openness.dll` 加载 | 开发桥接和未识别布局仍用同一相邻适配器路径，不搜索其他根；缺文件返回原不可用会话工厂 |

`TiaSharedAdapterPaths=true` 的构建变体使用桥接基目录下 `adapters/v<key>/TiaMcp.Adapter.<key>.dll`，
不改变相邻部署方式；开关默认 false，验收边界见[适配器设计](adapter-merge.md)。这里的 `<key>` 为八个精确版本键。

R1 的指南和桥接文件均相对选定根读取。Python 解释器另由 `TIA_MCP_PLC_TOOLS_PYTHON` 指定，未设置时使用
根下本机准备的 `TiaMcp_Output/ecosystem-python/Scripts/python.exe`；此环境不随包提供。
`ReadOpennessGuidance`、`RunPlcCompanionTool`、`AuditEngineeringExports`、`ReadV21EcosystemCatalog` 和
`DecodePlcSimaticMl` 属于 V20/V21 完整引擎工具，不因布局支持八个版本而加入 Foundation。
`DecodePlcSimaticMl` 在 V20 引擎中也只接受其声明的 V21 FC/FB 输入。

[`reference/v21-ecosystem.json`](../../reference/v21-ecosystem.json) 是生态目录的唯一可编辑源，两个完整引擎工程
直接将其嵌入，不生成另一份 JSON。更新目录须重建引擎；运行时修改随包副本不改变查询结果。指南仍从磁盘读取。

### 兼容边界与风险

- 阶段 0–5 保持路径字符串、异常类型与首行错误语义；环境变量名称也不变。
  `EcosystemFiles.Guidance` 的文档 ID 仍按指南根路径截取生成，不额外规范化。
- 解析器不会跨出已识别的根借文件，但调用方遇到不完整包仍可能触发旧探测并命中祖先仓库。
  因此仓库内暂存不能单独证明可重定位，验收使用仓库外的完整交付包；4.0 删除这些兼容回退。
- 安装目录写入（启动日志、崩溃日志、伴随 Python 环境）保持原位置，迁移到 LocalAppData 属于 4.0。
- R4–R6 的 `GetWorkspaceRoot`、HMI 模板默认目录和 `workspaceRoot` 套件参数是私人工作区输入，未纳入交付资源解析；
  阶段 6 删除默认值或改为显式输入。G7 不改变这部分原生 CLI 路径。
- P1-07 的 G7 前置阻塞已解除，整体目录重组仍待阶段 4 完成后进行。

### 检查器与离线证明

[`Check-BundleLayout.py`](../../scripts/checks/Check-BundleLayout.py) 不运行 dotnet 或引擎，检查：

- 枚举与字面量代码表一一对应，资源 ID 和路径不重复；路径是普通根相对路径，不含绝对路径、反斜杠、空段、`.` 或 `..`。
- 九项资源在本地存在，文件本身或目录中的文件属于 `git ls-files` 文件集。
- 每项路径都在 `Validate-Bundle.ps1` 实际遍历检查的 `bundleResourcePaths` 中；两份列表由检查器核对，并非共用一个数据文件。
- C# 5 Launcher 的唯一相对候选与解析器的 Studio 安装锚点、GUI 工程的输出 EXE 名称一致。

检查器不证明运行时返回值、二进制完整性或西门子行为；`Check-Repository.py --no-binaries` 同时运行此检查。
布局矩阵及调用方差分测试位于完整引擎的 [BundleLayoutTests.cs](../../tools/tiaportal-mcp/tests/TiaMcpServer.Tests/BundleLayoutTests.cs)、
[EcosystemTests.cs](../../tools/tiaportal-mcp/tests/TiaMcpServer.Tests/EcosystemTests.cs)、
[EngineBundleLayoutTests.cs](../../tools/tiaportal-mcp/tests/TiaMcpServer.Tests/EngineBundleLayoutTests.cs) 和 Studio 的
[Core 布局测试](../../tools/tia-openness-studio/tests/TiaOpenness.Core.Tests/StudioBundleLayoutTests.cs)、
[GUI 布局测试](../../tools/tia-openness-studio/tests/TiaOpenness.Gui.Tests/StudioBundleLayoutTests.cs)，覆盖安装、开发输出、worktree、CI、仅 runtime、嵌套暂存、
空格/中文/尾分隔符、缺文件及显式覆盖。

G7-1 已将 `ReadOpennessGuidance`、`ReadV21EcosystemCatalog` 和 bin 布局的 `CheckForUpdate` 加入 P0-06。
G7-3 的仓库外交付包比较保持原始响应一致（质量 PDF 因缺少 ReportLab 未验证）；G7-4 的九项重定位比较只存在时间字段差异。
G7-5 比较了 1,921 份 Studio 重定位结果；G7-6 在 V20/V21 各完成 75 项生态检查（含 HTTP），
证明目录查询不再依赖磁盘 JSON。以上为各实现任务的离线证据，不代表原生验收；任务证据汇总见[重构计划](refactor-plan.md)。

### 新增运行时资源

1. 将交付资源放入受版本管理的包内路径，在 `BundleResource` 与 `ResourcePaths` 增加一项。
   目录资源同步调整 `FindResource` 的目录判断；编译生成的 EXE/DLL 继续由构建与交付清单管理。
2. 同步 `Validate-Bundle.ps1` 的 `bundleResourcePaths`；核对 `Check-Repository.py` 和 `Package-Release.py` 的必需文件清单，
   按资源用途补齐。清单与源码哈希由对应构建生成器更新，不手工改 manifest 哈希。
3. 调用方复用解析器和版本目录表；需要新输出布局时显式扩充锚点。既有调用方保持覆盖优先级、路径拼写和错误语义，
   不增加新的仓库探测。仅限单个程序集使用的固定数据可按生态目录模式直接嵌入，并保留唯一可编辑源。
4. 补充受影响资源/调用方的布局测试，验证缺文件、显式覆盖与仓库外重定位；运行
   `python scripts/checks/Check-BundleLayout.py --self-test`、`python scripts/checks/Check-BundleLayout.py`、
   `python scripts/checks/Check-Repository.py --no-binaries` 和
   `pwsh -NoProfile -File scripts/checks/Validate-Bundle.ps1 -Strict -NoBinaries -SkipSourceHashes`。
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
| D-G7-2 | 阶段 0–5 只保留 `TIA_MCP_REPOSITORY_ROOT`；4.0 再引入 `TIA_MCP_BUNDLE_ROOT` 或 `--bundle-root`，旧名保留为别名 |
| D-G7-3 | 不受支持的重定位：阶段 0–5 保留原有探测作为兼容回退，4.0 删除（依据“先兼容，后破坏”的原则） |
| D-G7-4 | 开发锚点编入产品，只认枚举出的输出目录；测试显式给根 |
| D-G7-5 | `v21-ecosystem.json` 已直接嵌入 V20/V21 引擎；指南仍读取随包文件，改为嵌入前需另做差分证明 |
| D-G7-6 | 安装目录写入（启动日志、崩溃日志、伴随 Python 环境）阶段 0–5 不动，4.0 改到 LocalAppData |
| D-G7-7 | G7-5 已修复 Studio 的 worktree `.git` 判断，文件和目录都禁止安装更新（仅 UI 行为） |
| D-G7-8 | 私有工作区默认值阶段 0–5 不动，阶段 6 删除或改为显式输入 |
| D-P5-1…6 | 见上文“注释与 `*Leftovers`”与“用户可见文案语言”；D-P5-3/5（新文本英文、4.0 统一英文）由维护者决定（2026-10-03） |
