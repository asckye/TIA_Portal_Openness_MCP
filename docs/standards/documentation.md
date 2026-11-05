# 文档规范

状态：现行 · 最后核对：2026-11-04，master `ef1988bc`

本文规定仓库全部文字材料的位置、语言、写法、术语、生成文档和 ADR，并登记现有文档与本文的偏差；它合并并取代了[文档偏差清单](../archive/development/design-spec/deviations/docs.md)。

## 0 用语与基线

- 规范用语 MUST、SHOULD、MAY 按[《工程开发规范》通用规范](https://github.com/asckye/engineering-standards/blob/v2.0/general/00-purpose.md#purpose-read)第 0.3 节。通用规范[第 20 节“文档”](https://github.com/asckye/engineering-standards/blob/v2.0/general/20-documentation.md#docs)的规则全部适用，本文不重复，只写本项目的补充和偏差。
- 基线（[ADR 0015](decisions/0015-microsoft-design-spec-baseline.md)）：英文按 [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/)（下称 MS），中文按 [Chinese (Simplified) Localization Style Guide](https://aka.ms/chinese-simplified-styleguide)（下称 zh-CN 指南）。冲突时依次服从 ADR、本文、基线。
- `docs/` 下的文档按 zh-CN 指南 §4.1.13 的“文档”标点约定；工作台界面字符串按“软件”约定，见[工作台界面规范](ui.md)。
- 本文的 DOC-nn 是偏差编号（第 17 节），与通用规范的规则编号 DOC-nn 无关。

## 1 文档位置

目录结构由 [ADR 0030](decisions/0030-documentation-and-repository-structure.md) 决定：

| 类别 | 位置 | 随包 |
|---|---|---|
| 入口说明 | `README.md`、`README.zh-CN.md` | 是 |
| 用户文档 | `docs/README.md`、`docs/getting-started/`、`docs/guides/`、`docs/troubleshooting/`、`docs/reference/`（逐个文件以 `scripts/operations/delivery-files.json` 为准） | 是 |
| 发布记录 | `CHANGELOG.md`、`docs/releases/` | 是 |
| 许可证与图片 | `docs/licenses/`、`docs/assets/` | 是 |
| 规范 | `docs/standards/`：`README.md`（采用范围、项目偏差、外部参考）、`development.md`、`design.md`、`ui.md`、`security.md`、`documentation.md` | 否 |
| ADR | `docs/standards/decisions/` | 否 |
| 计划 | `docs/plan/plan.md`（版本、放行门槛、未决决定）、`docs/plan/progress.md`（当前状态、分支、测试者反馈） | 否 |
| 手册 | `docs/manuals/`：`workflow.md`、`validation.md`、`release.md`、`real-machine-testing.md` | 否 |
| 历史资料 | `docs/archive/` | 否 |
| 代理说明 | `AGENTS.md`、`CLAUDE.md`、`plugin/skill/SKILL.md` | 只有 `SKILL.md`（随插件） |
| 代码旁说明 | `src/**`、`tests/**`、`build-tools/**` 下的 `README.md` | 否 |

| 级别 | 规则 |
|---|---|
| MUST | 新主题写进同类的现有文件。新建独立文档要有理由，写在任务说明中，并在本节的表中登记位置。 |
| MUST | `docs/archive/` 只读：放在其中的文件按位置视为历史记录，原样保留，不再修改；现行文档不把归档文件当作规范出处。 |
| MUST | 用户文档不得依赖不随包的文件才能读懂。 |
| MUST | 移动被代码、交付清单或检查读取的路径时，在同一批次修改引用并加测试。搬迁的第一个提交只含 `git mv`，第二个只改链接和路径常量，内容改写放在之后。 |

## 2 语言

一个文件只用一种语言（通用规范 DOC-03）。各类文档的语言按 [ADR 0026](decisions/0026-documentation-structure-and-language.md)：

| 文档类型 | 位置 | 语言 |
|---|---|---|
| 入口说明 | `README.md`、`README.zh-CN.md` | 英文、中文各一份 |
| 用户文档（随包） | `docs/README.md`、`docs/getting-started/`、`docs/guides/`、`docs/troubleshooting/`、`docs/reference/` 中手写的文件、`templates/**/README.md` | 中文，只维护一份；现有英文页译为中文，不保留英文版（DOC-13） |
| 生成文档 | `tool-matrix.md`、`version-tool-catalog.md`、迁移表 | 由生成器决定（英文） |
| 发布记录 | `CHANGELOG.md`、`docs/releases/*.md`、`docs/reference/api-audit-vm-checklist.md` | 英文 |
| 开发文档 | `docs/standards/`、`docs/plan/`、`docs/manuals/`、ADR | 中文（MUST） |
| 代理说明 | `CLAUDE.md`、`AGENTS.md` | 中文 |
| MCP 可见文本与插件技能 | 工具描述、`GetToolUsage`、`plugin/skill/SKILL.md` | 英文 |
| GitHub 社区文件 | `.github/*.md`、PR 与 Issue 模板 | 英文，写明欢迎中文 |
| 代码旁说明 | `src/**`、`tests/**`、`build-tools/**` 下的 README | 英文 |
| 冻结文件 | `docs/archive/`、`manifest/history/contracts-v3/README.md` 等 | 保持原样 |

- MUST：只有入口说明提供两种语言：英文为 `<name>.md`，中文为 `<name>.zh-CN.md`。
- MUST：英文用户页译完之前，`docs/README.md` 在其链接后标“（英文）”。
- MCP 可见文本的语言政策见[设计规范](design.md)。

## 3 语气与读者

通用规范 DOC-08、DOC-09 已规定先结论后背景、短句常用词、用“选择”和“你”、只写当前行为。补充：

| 级别 | 规则 |
|---|---|
| MUST | 用户文档不出现内部任务编号（如 P8-03x）、阶段名、代理名称和评审过程。 |
| MUST | 限制和未验收状态只写一次，链接[能力与验收边界](../reference/capabilities.md)或真机台账，不在每段重复免责声明。 |
| MUST | 英文描述操作用 select，不用 click；称呼读者用 you。 |
| SHOULD | 中文能省略主语时省略；多层定语拆成两句。 |
| SHOULD | 描述进程用“结束”“停止”，不用“杀掉”；避免带偏见或军事化的措辞。 |

## 4 结构与格式

依据：MS [Headings](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings)、[Lists](https://learn.microsoft.com/en-us/style-guide/scannable-content/lists)、[Tables](https://learn.microsoft.com/en-us/style-guide/scannable-content/tables)、[Capitalization](https://learn.microsoft.com/en-us/style-guide/capitalization)。

| 级别 | 规则 |
|---|---|
| MUST | 每个文件只有一个一级标题。英文标题用句子式大小写，结尾不加句号；中文标题结尾不加标点。 |
| MUST | 现行文档的标题不含任务编号、日期或状态；ADR 和历史记录可以保留。 |
| SHOULD | 同级标题结构平行：概念用名词短语，操作用动宾短语；标题短，避免“&”“+”。 |
| MAY | 超过约 300 行的开发文档可以给标题编号；用户文档不编号。 |
| MUST | 有先后顺序的内容用编号列表，否则用项目符号；完整句子结尾加句号，短语不加。 |
| MUST | 表格有具体的表头，最左列放行的标识；单元格不留空也不用破折号，中文写“无”或“不适用”，英文写 *None* 或 *Not applicable*；一张表不被空行打断。 |
| SHOULD | 两个以上属性需要对比时才用表格；单元格尽量一行，长说明放在表外。 |
| MUST | 开发文档在一级标题下写状态行 `状态：<状态> · 最后核对：<日期>，master <提交>`，状态取“现行”“部分过时（第 x 节）”或“历史记录”；下一段用一句话说明本文覆盖什么。 |
| MAY | 状态行之后可以有导航行，最多五个链接。 |
| SHOULD | 用户文档在标题下用一两句话说明适用的版本范围和读者要完成的任务。 |

## 5 操作步骤

依据：MS [Writing step-by-step instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions)。

| 级别 | 规则 |
|---|---|
| MUST | 多步操作用编号列表，一步一个动作，以祈使动词开头；需要先说明位置时位置在前（“在 **MCP 与客户端** 页，选择 **写入客户端配置**。”）。只有一步时用项目符号。 |
| MUST | 菜单路径写成 `**视图** > **MCP 与客户端**`：每级单独加粗，`>` 前后各一个空格且不加粗，不用“→”。 |
| MUST | 命令写成可复制的代码块，并说明从哪个目录运行。 |
| SHOULD | 步骤后说明怎样判断成功，以及成功不代表什么（例如编译返回不等于编译通过）。 |
| SHOULD | 给 AI 客户端的提示词放在 `text` 代码块中，方括号占位符说明要替换的值。 |

## 6 界面文字与键盘

依据：MS [Formatting text in instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/formatting-text-in-instructions)；zh-CN 指南 §5.5。

| 级别 | 规则 |
|---|---|
| MUST | 界面元素按实际显示的文字书写并加粗：中文文档用中文界面文字，英文文档用英文界面文字。中文也用加粗而不加引号（有意偏差 DOC-15）。 |
| MUST | 键名和组合键用英文原名和加号，不加粗也不用代码样式：Ctrl+2、Alt+F4。 |
| MUST | 错误消息原文加引号（中文“”，英文 ""）；错误码用代码样式，如 `RESOURCE_UNAVAILABLE`。 |

## 7 代码与技术元素

依据：MS [Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements)、[Code examples](https://learn.microsoft.com/en-us/style-guide/developer-content/code-examples)。

| 级别 | 规则 |
|---|---|
| MUST | 工具名、参数、JSON 字段、错误码、枚举值、命令行选项、环境变量、文件名、路径和发布键用行内代码，不用加粗：`GetToolUsage`、`dryRun: true`、`--release-key`、`TIA_MCP_DATA_DIRECTORY`、`14sp1`。 |
| MUST | Markdown 中不用 HTML 标签或实体表达代码；尖括号占位符放进行内代码并说明含义：`runtime/v<发布键>/`。 |
| MUST | 代码块标注语言：`powershell`、`json`、`csharp`、`text`（提示词、命令输出）。 |
| MUST | 示例值明显是示例（如 `D:\TIA-MCP`）；不写真实机器地址、PID、会话 ID、密钥和私人工程路径。 |
| MUST | 工具参数、调用序列和结果解释只在 `reference/tool-examples` 维护；文档链接 `GetToolUsage` 或示例 ID，不复制参数表。 |

## 8 中文排版

依据：zh-CN 指南 §4.1.2、§4.1.10、§4.1.12、§4.1.13、§5.2。

### 8.1 空格

| 级别 | 规则 |
|---|---|
| MUST | 中文与英文、数字之间的空格按通用规范 DOC-08；全角标点两侧不加空格。 |
| MUST | 行内代码、加粗与相邻中文之间加一个半角空格，与相邻全角标点之间不加。 |
| MUST | 中文段落在源文件中写成一行，不在句中换行（渲染器会在两个汉字之间插入空格）；英文段落可以按句换行。 |

### 8.2 标点

| 级别 | 规则 |
|---|---|
| MUST | 中文句子用全角标点，并列词语用顿号；英文句子、代码、路径、URL、版本号和数字内部用半角标点。 |
| SHOULD | 括号内含中文时用全角括号；只有英文、数字或代码时用半角括号，括号外各留一个空格。 |
| MUST | 中文引号用“”，嵌套用‘’；中文正文不用破折号，改用冒号、括号或改写句子；省略号用“…”。 |
| MUST | 加粗内容不以全角标点结尾后紧跟文字（`**用 AI 操作：**双击` 不会渲染为加粗）；把标点移到加粗外。 |
| SHOULD | 书名号只用于中文出版物名称；文档链接不加书名号。 |

### 8.3 英文术语

| 级别 | 规则 |
|---|---|
| MUST | 产品名、商标、工具名和类型名保持英文原文和大小写，不翻译。 |
| MUST | 缩写全部大写，不加复数 s（“两个 PLC”）。 |
| SHOULD | 用具体量词（“两台 CPU”“三个工具”）；常用技术词首次出现可以括注英文，后文只用中文。 |

## 9 数字、日期与版本

依据：MS [Numbers](https://learn.microsoft.com/en-us/style-guide/numbers)；zh-CN 指南 §4.1.10。

| 级别 | 规则 |
|---|---|
| MUST | 与单位连用、在表格中或表示计数的数字用阿拉伯数字；数字与单位之间空一格：`4 MiB`、`120 秒`。英文时间单位用 sec、min、h 或拼写。 |
| SHOULD | 四位及以上的数量加千位分隔逗号（`17,666 项检查`）；年份、端口、PID、版本号、提交号和编号不加。 |
| MUST | 日期用 ISO 8601 `YYYY-MM-DD`，中英文一致；需要时间时用 UTC 并写明（有意偏差 DOC-30）。 |
| MUST | 范围用 en dash，前后不加空格：`V14 SP1–V19`、`10–180 秒`；不用全角“～”（有意偏差 DOC-29）。 |
| MUST | TIA Portal 版本写作 V14 SP1、V15.1、V16 … V21；不写 V14SP1，不用 V14、V15 指 V14 SP1、V15.1。发布键用代码样式：`14sp1`、`15.1`、`16`–`21`。 |
| MUST | 会随代码变化的数字（工具数量、测试数量）不手写：链接生成文档，或写“以运行时 `tools/list` 为准”；必须出现时由生成器写入（[ADR 0014](decisions/0014-tool-counts-follow-official-api.md)，DOC-02）。 |

## 10 项目术语表

本表是用户文档和开发文档的统一用词。“避免”列的词在新文档中不得用来指代同一对象；历史记录保留原文。

| 对象 | 中文首选 | 英文首选 | 代码或文件标识 | 避免 | 说明 |
|---|---|---|---|---|---|
| 西门子工程软件 | TIA Portal（首次），后文可简称 TIA | TIA Portal | 不适用 | 博途 | 产品名不翻译 |
| TIA Portal 版本 | V14 SP1、V15.1、V16 … V21 | 同左 | 不适用 | V14SP1；用 V14、V15 指 V14 SP1、V15.1 | 原版 V14、V15 不受支持 |
| 发布键 | 发布键 | release key | `14sp1`、`15.1`、`16`–`21` | 版本键、版本号 | 行内一律用代码样式 |
| 交付包 | 交付包 | delivery package | `TIA_MCP_Delivery_v<版本>_<日期>.zip` | 安装包、完整包（混用） | 解压后的根目录称“包根” |
| 包根 | 包根 | bundle root | `--bundle-root`、`TIA_MCP_BUNDLE_ROOT` | 安装根、仓库根（指交付包时） | 含 `manifest/package-manifest.json` 的目录 |
| 数据根 | 数据根 | data root | `<包根>/data`、`TIA_MCP_DATA_DIRECTORY` | 数据目录（作术语时） | 布局见[设计规范](design.md) |
| 工作台 | 工作台 | Workbench | 包根 `TiaOpenness.exe` 启动 `runtime/studio/TiaOpenness.exe` | 配置器、统一工作台、统一桌面、Studio（指整个程序时） | “配置器”只用于 3.x 历史 |
| 工作台页面 | **MCP 与客户端**、**项目**、**AI 调用**、**审批**、**审计日志**、**环境**、**版本控制** | **MCP & clients** 等 | 不适用 | 配置页、Studio 页 | 按界面实际文字，加粗 |
| Studio | Studio（只作源码名） | Studio | `src/Studio` | 用来称呼用户界面 | 用户文档写“工作台”或“项目页” |
| MCP 宿主 | 宿主；指程序时写 FoundationHost | MCP host、FoundationHost | `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>` | 主机、Foundation 主机、基础宿主、基础引擎、LegacyHost（只作代码名） | 八个版本同一个宿主程序 |
| 宿主机 | 宿主机 | host computer | 不适用 | 宿主、主机（指虚拟机外的电脑时） | 只指运行虚拟机的物理电脑 |
| 服务（界面用语） | 服务、MCP 服务 | service、MCP service | 不适用 | 引擎、MCP 引擎、引擎设置 | 界面和用户文档对宿主及其 worker 的总称；开发文档按对象写宿主、FoundationHost 或 worker |
| worker | worker（总称） | worker | 不适用 | 工作进程、子进程（作术语时） | 宿主为每个 MCP 会话启动的 net48 进程 |
| PLC worker | PLC worker | PLC worker | `runtime/v<发布键>/worker/TiaMcp.PlcWorker.<发布键>.exe` | 基础 worker、Foundation worker | V14 SP1–V19 为 STA 线程，V20/V21 为 MTA 线程 |
| 引擎 | 引擎（只作历史名词） | engine | `TiaMcp.Engine.V20.exe`、`TiaMcp.Engine.V21.exe`、`src/Engine` | 完整引擎、引擎 worker、full engine；用“引擎”指当前产品 | 已退役（[ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md)），只在历史记录和源码名中出现 |
| V20/V21 专有工具 | V20/V21 专有工具 | V20/V21-only tools | 不适用 | 完整引擎工具 | 原生调用在 PLC worker 中执行 |
| Foundation 适配器 | Foundation 适配器 | Foundation adapters | `TiaMcp.Adapter.<发布键>.dll`、`src/Adapters` | 共享适配器、Foundation 代码（混用） | 按发布键编译八次 |
| 能力族 | 能力族（F01–F35） | capability family | `PortedFamilies` | 族、工具族（混用） | 已移入适配器的称“已移植族” |
| 工具目录 | 工具目录 | tool catalog | `tools/list` | 工具清单（指运行时目录时） | 分 lite 档和 full 档 |
| lite 档、full 档 | lite 档、full 档 | lite profile、full profile | `--profile full`、`TIA_MCP_PROFILE` | 精简模式、完整模式 | 八版默认 lite，`--profile full` 列出完整目录；工作台操作员会话始终使用完整目录 |
| MCP 会话 | MCP 会话 | MCP session | 不适用 | 连接、会话（与 TIA 会话混用时） | 每个 MCP 会话一个 worker |
| V4 会话 | V4 会话；上下文清楚时简称会话 | V4 session | 不适用 | 连接（指会话状态时） | 宿主为一个 MCP 会话维护的全部状态：worker、租约、绑定、会话锁、计划、句柄 |
| worker 代次 | worker 代次 | worker generation | 不适用 | worker 代 | 一次 worker 进程的生命周期；故障后经 `RestartOpennessWorker` 或 `DisconnectPortal` 开始新代次 |
| 绑定 | 绑定 | binding | `ConnectPortal`、`AttachOpenProject` | 连接工程 | 先连接 TIA 进程，再附着项目 |
| TIA 进程租约 | TIA 进程租约 | TIA process lease | `<数据根>/leases/<PID>-<启动 ticks>.lease` | 实例锁、实例租约（混用） | 状态 `IDLE`、`BUSY`、`UNCERTAIN`、`RELEASED` |
| Openness 通道 | Openness 通道（lane） | Openness lane | `ToolDispatchLanes` | 串行锁、全局锁 | 每个会话一条，原生调用串行 |
| 审批 | 审批 | approval | `approval.settings`、`TiaMcp.Approval.v1.*` | 确认、批准（作功能名时） | 工作台对 MCP 写调用的人工审批 |
| 预检 | 预检 | precheck | `APPROVAL_PRECHECK_REFUSED` | 预审 | 审批前用预览参数运行的检查 |
| 预览 | 预览 | preview | `dryRun: true`、`mode: preview` | 演练、试运行 | 不审批、不写审计 |
| 审计链 | 审计链 | audit chain | `<数据根>/logs/audit` | 审计日志（指哈希链时）、审批日志 | `tia audit verify` 校验 |
| 调用日志 | 调用日志 | call journal | `<数据根>/diagnostics/calls-*.jsonl` | 原生日志（混用） | 与审计链分开 |
| 控制通道 | 工作台控制通道 | Workbench control channel | `TiaMcp.Workbench.v1.*` | 控制管道（混用） | 显示、读取、预填三类工具 |
| 操作端管道 | 操作端管道 | operator pipe | `TiaMcp.Operator.v1.*` | 操作通道 | 工作台附着 MCP 会话 |
| V4 信封 | V4 信封 | V4 envelope | `schemaVersion`、`ok`、`data`、`error`、`meta` | 响应格式、返回结构（作术语时） | 见[设计规范](design.md) |
| 结果未知 | 结果未知 | unknown outcome | `OUTCOME_UNKNOWN`、`meta.outcome = unknown` | 状态未知、不确定结果 | 会话随后需要重置 |
| 操作前拒绝 | 操作前拒绝 | rejected before operation | `rejected-before-operation` | 预拒绝 | 原生操作未开始 |
| 真机验收 | 真机验收（L5） | real-machine acceptance | `NOT RUN` | 实机验证、VM 验证（混用） | 结果记入真机台账 |
| 真机台账 | 真机台账 | real-machine ledger | `docs/reference/real-machine-ledger.md` | 验收清单、证据索引（混用） | 不随包 |
| 标准包 | 标准包 | standard package | `templates/standards/`、`<数据根>/standards` | 规范包、模板包 | 生成框架的输入之一 |
| 机器描述 | 机器描述 | machine description | `MachineDescription` | 设备清单（作文件名时） | 生成的唯一结构化输入 |
| 生成计划、生成运行 | 生成计划、生成运行 | generation plan、generation run | `tiamcp.plan/1`、`tiamcp.run/1` | 方案、执行记录（混用） | 一次 Apply 审批一次 |
| 暂存 | 导入暂存 | import staging | `<包根>/staging/<会话>/<批次>/` | 上传目录 | 不在数据根下 |
| TIA 项目 | 项目 | project | `.ap<主版本>` 文件 | 工程（指 TIA Portal 项目时） | 跟随 TIA Portal 中文界面；现有文档的“工程”在改写时替换 |
| 写栅栏 | 生成写栅栏 | generation write fence | `generation-run-active` | 围栏 | 生成运行期间拒绝同一会话中不属于该运行的写调用 |
| 发散 | 发散 | divergence | `diverged` | 偏离（指生成运行时） | 执行前的工具预览与已批准的计划不符，运行停止且不写入 |
| 偏差 | 偏差 | deviation | `CS-nn`、`MCP-nn`、`API-nn`、`UI-nn`、`DOC-nn` | 偏离（混用） | 现有代码或文档与规范的差异，登记在对应的规范文件中 |

## 11 文件、图与链接

### 11.1 文件与图

| 级别 | 规则 |
|---|---|
| MUST | 文件名用小写字母、数字和连字符；语言后缀见第 2 节。 |
| MUST | UTF-8 无 BOM，仓库内 LF 行尾（`.gitattributes` 尚未覆盖 `*.md`，DOC-34）。 |
| MUST | 架构、流程和时序图按 [ADR 0027](decisions/0027-archify-diagrams.md) 用 archify 从 JSON 源生成，HTML 与来源记录一起提交，文字为权威，图与对应章节同批修改；生成命令 `diagrams` 尚未实现。图放在 `docs/assets/`，不设 ADR 0027 写的 `docs/diagrams/`（ADR 0030）。 |
| MUST | README 的架构图是手写 SVG（英文、中文各一张），放在 `docs/assets/`。 |
| MUST | 图片的替代文本说明图的内容（如“MCP 客户端与工作台共享 FoundationHost 会话”），不写“架构图”这类泛称（MS [Alternative text](https://learn.microsoft.com/en-us/style-guide/accessibility/alternative-text)）。 |

### 11.2 链接

依据：MS [URLs and web addresses](https://learn.microsoft.com/en-us/style-guide/urls-web-addresses)。

| 级别 | 规则 |
|---|---|
| MUST | 链接文字说明目标是什么，不用“这里”“此处”“链接”。 |
| MUST | 两端都随包或都不随包时用相对链接；随包文档之间不用绝对链接。 |
| MUST | 随包文档链接不随包的文件时，用 `https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/<路径>` 绝对链接（有意偏差 DOC-24）；发布记录中的这类链接在发布提交中固定到发布标签。 |
| SHOULD | 正文不出现裸 URL，用页面标题作链接文字（`CHANGELOG.md` 的历史条目除外，DOC-25）。 |
| SHOULD | 章节锚点只链接稳定的标题；改标题时同时修改指向它的链接。 |

## 12 生成文档

| 级别 | 规则 |
|---|---|
| MUST | 第一段说明“本文件由 `<命令>` 从 `<输入>` 生成，不要手工编辑”，命令写成 `dotnet run --project build-tools/release -- <命令>`。 |
| MUST | 同一输入逐字节输出相同内容，生成器提供 `-Check`；生成文档在 `.gitattributes` 中声明 `text eol=lf`。 |
| MUST | 不含生成时间和本机信息；需要版本时写契约基线或提交号。 |
| MUST | 手写文档中嵌入的生成段落用 `<!-- generated:<名称>:begin -->` 与 `<!-- generated:<名称>:end -->` 包围，标记外由人维护。 |
| SHOULD | 同一类生成文档用同一种语言（英文），表格遵守第 4 节，空值写 *None* 或 *Not applicable*。 |
| MUST | 手写文档不复述生成文档的内容（例如工具数量），只链接。 |

## 13 发布记录与 CHANGELOG

变化当次写入 `CHANGELOG.md` 的 `Unreleased`（通用规范 DOC-06）。

| 级别 | 规则 |
|---|---|
| MUST | 英文，标题用句子式大小写；副标题用冒号（`v4.0.0: Draft`），破折号两侧不加空格。 |
| MUST | 每条变化先说变了什么、对用户有什么影响，再给细节；工具名、参数和错误码用代码样式。 |
| MUST | 发布版本中不留 `TODO` 注释；不把归档文档当作规范出处链接，协议与错误码链接现行[设计规范](design.md)。 |
| MUST | 版本表中的数字来自生成器或契约基线，并在发布提交中核对。 |
| SHOULD | 结构固定为 Summary、Changes、Tools per release、Upgrade、Known limits、Acceptance status。 |

## 14 ADR 与设计记录

通用规范第 20.2 节（DOC-04、DOC-05）规定 ADR 的字段、只追加规则、状态值和索引，第 0.4 节规定 AI 代理只能写“提议”状态的 ADR；外部来源见[本项目采用的开发规范](README.md)。

### 14.1 何时必须写 ADR

通用规范 G-04 的范围在本项目中包括：

- 进程、程序集或部署结构（例如宿主与 worker 的划分、引擎退役、工作台附着会话）；
- MCP 对外契约或其兼容策略（工具命名、信封、注册规则、版本差异处理）；
- 安全或数据完整性边界（审批、审计、租约、结果未知后的处理、写栅栏）；
- 发布、验收或质量门禁的规则（合并门、真机验收时机、不放宽检查）；
- 外部基线、格式和第三方组件（规范基线、标准包格式、JSON 库）；
- 文档或仓库的目录结构；
- 推翻或替代已有 ADR。

不需要 ADR：单个工具的修复、措辞改动、不改变行为的移动、实施细节。

### 14.2 文件与编号

| 级别 | 规则 |
|---|---|
| MUST | 位置 `docs/standards/decisions/NNNN-<短标题>.md`（通用规范写 `docs/adr/`，见 ADR 0030）；`NNNN` 四位递增，短标题用小写英文和连字符。 |
| MUST | 用中文；标题用陈述句写出决定本身；写好后在 [ADR 索引](decisions/README.md) 登记编号、标题、状态和日期。 |
| MUST | 已采纳的 ADR 只修正笔误和补链接。决定改变时写新的 ADR，在旧 ADR 末尾追加一行指向新 ADR，并更新索引中的状态。 |
| MUST | 回溯补写既往决定时，日期写原决定日期，并注明“回溯记录（写于 YYYY-MM-DD）”。 |

### 14.3 状态值

状态取提议、已采纳、已拒绝、已被 NNNN 取代、已废弃（通用规范 DOC-05）；只有部分被取代时写“已采纳；<部分>被 NNNN 取代”。

### 14.4 模板

新 ADR 从 [ADR 模板](decisions/0000-template.md) 开始，模板只在该文件中维护。ADR 必须能独立读懂；详细推导放在相关文档中，从“相关”字段链接。

### 14.5 设计说明与设计记录

| 级别 | 规则 |
|---|---|
| MUST | L2 及以上改动的设计说明按通用规范第 18.3 节和[开发流程](../manuals/workflow.md)编写；合并实现时，长期有效的规则写进对应的规范文件，决定写成 ADR。设计文档链接 ADR，不在文末维护“维护者决定”小节。 |
| MUST | 不再新建设计记录目录；已完成的设计记录在 `docs/archive/development/design/`，只读。 |
| MUST | `bin-build/` 下的草稿不是正式记录，文档不链接 `bin-build/` 路径。 |

## 15 代理说明文件

| 级别 | 规则 |
|---|---|
| MUST | 各代理共用的规则只写在 `AGENTS.md`；`CLAUDE.md` 导入 `AGENTS.md`，只补充 Claude 专有的规则（ADR 0030）。 |
| SHOULD | 只写必须遵守的规则和入口链接，用祈使句，一条规则一行；详细流程链接到手册，不复制。 |
| MUST | 阶段性约束在阶段结束后删除或改写。 |
| MUST | `plugin/skill/SKILL.md` 随插件分发，用英文，描述当前版本的用法，遵守第 7 节。 |

## 16 检查

| 检查 | 对应规则 | 状态 |
|---|---|---|
| 相对链接和锚点存在；随包文档不链接交付范围外的文件 | 11.2 | 已实施（`check-repository`） |
| 嵌入的生成段落和生成文档与生成器输出一致 | 12 | 已实施（各生成命令的 `-Check`） |
| 排版：缺空格、硬换行、加粗后紧跟全角标点、破折号与“～”、表格空行与空单元格 | 4、8 | 未实施 |
| 用词与数字：第 10 节“避免”列的词、手写的工具数量 | 9、10 | 未实施 |
| 生成文档缺少说明行或含时间戳；发布记录中的 `TODO` 与 HTML 标签 | 7、12、13 | 未实施 |

## 17 文档偏差

“4.0 后”表示在 4.0 正式版之后统一处理，“有意偏差”不再修改。已完成且无人引用的条目已删除，原文见[文档偏差清单](../archive/development/design-spec/deviations/docs.md)。

| 编号 | 偏差 | 位置 | 处理 | 状态 |
|---|---|---|---|---|
| DOC-01 | 用户文档指导使用已删除的 `--isolate-openness` 开关 | 用户文档 | 改为现行的每会话 worker 与恢复流程 | 已完成 |
| DOC-02 | 手写文档中的工具数量互相矛盾 | 入口说明、配置指南、版本范围页 | 由 `generate-tool-counts` 写入生成段落 | 已完成 |
| DOC-13 | 用户文档中的英文页 | `docs/guides/` 中 5 篇、`docs/reference/` 中 3 篇手写页 | 按第 2 节译为中文；译完前索引标“（英文）” | 4.0 后 |
| DOC-17 | 中文段落硬换行 | 主要在开发文档 | 用脚本合并，加入检查 | 4.0 后 |
| DOC-20 | 现行文档的标题含任务编号、日期 | 开发文档 | 整理时去掉 | 4.0 后 |
| DOC-27 | 全角括号包住纯英文、数字或代码 | 开发文档、`CHANGELOG.md` | 用脚本处理，加入检查 | 4.0 后 |
| DOC-28 | 中文破折号和表格中的破折号空值 | 手写表格；生成的 `docs/releases/v4.0.0-tool-migration.md` 用“ — ”表示空值 | 手写表格改为“无”“不适用”，迁移表由生成器改为 *None* | 4.0 后 |
| DOC-31 | 千位分隔与时间单位不统一（“N s”与“N 秒”混用） | 开发文档、发布记录 | 按第 9 节统一 | 4.0 后 |
| DOC-33 | 生成文档的说明行、语言和标记不统一 | `docs/reference/tool-matrix.md`（中文表头、“生成时间”）等 | 生成器统一说明行，去掉时间戳 | 4.0 后 |
| DOC-34 | `*.md` 未在 `.gitattributes` 中声明 `text eol=lf` | `.gitattributes` | 增加规则 | 4.0 后 |
| DOC-37 | 少量中英文之间、中文与行内代码之间缺空格，中文后用半角标点 | 开发文档 | 加入检查后批量处理 | 4.0 后 |
| DOC-15 | 中文文档的界面文字用加粗，不用 zh-CN 指南文档约定的引号 | 全部中文文档 | 与英文规则一致，便于检查 | 有意偏差 |
| DOC-19 | Markdown 中的 `<br>` 和 `<a id>` 锚点 | `docs/archive/development/phase6-review.md` | 冻结文件，不改；新文档不得这样写 | 有意偏差 |
| DOC-24 | 随包文档用 GitHub 绝对链接指向不随包的文件 | 随包文档 | 解压后的交付包中相对链接会失效；按第 11.2 节规则化 | 有意偏差 |
| DOC-25 | 裸 URL | `CHANGELOG.md` 的历史条目 | 历史记录不改写 | 有意偏差 |
| DOC-29 | 范围用 en dash，不用 zh-CN 指南的波浪线 | 全部文档 | 中英文与 MCP 文本同一写法，便于检索 | 有意偏差 |
| DOC-30 | 日期用 ISO 8601，不用 MS 的“month day, year” | 全部文档 | 可排序，不受地区格式影响 | 有意偏差 |
| DOC-38 | ADR 正文为英文 | [ADR 0021](decisions/0021-workbench-eplanmcp-font-and-style.md)、[ADR 0029](decisions/0029-authoritative-session-state.md) | ADR 只追加，不改写（ADR 0026） | 有意偏差 |
