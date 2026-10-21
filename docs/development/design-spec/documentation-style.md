# 文档写作规范

状态：现行 · 最后核对：2026-10-11，master `3a227657`

[设计规范索引](README.md) · [文档偏差清单](deviations/docs.md)

本规范是 TIA Portal Openness MCP 项目全部文字材料的写作标准。维护者于 2026-10-11 决定以 Microsoft 设计规范为基线：英文按 [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/)，中文按 Microsoft 发布的 [Chinese (Simplified) Localization Style Guide](https://aka.ms/chinese-simplified-styleguide)（以下简称“zh-CN 指南”，PDF，2024 年版，58 页）。新写和改写的文档必须符合本规范；现有材料与本规范不一致之处逐条列在 [文档偏差清单](deviations/docs.md)，每条的状态（4.0 前修改、4.0 后修改或有意偏差）已按 2026-10-11 的决定登记，4.0 前的修改由 P8-63 与 P7-11b 一起完成。基线决定见 [ADR 0015](../decisions/0015-microsoft-design-spec-baseline.md)。

## 0 规范用语与引用方式

| 用语 | 含义 |
|---|---|
| MUST / 必须 | 新文档和改写的段落必须满足；不满足的现有内容记为偏差项 |
| SHOULD / 应当 | 默认遵守；不遵守时在文档或评审记录中写明理由 |
| MAY / 可以 | 允许的做法，不作要求 |

每条规则后的方括号注明出处：`[MS: 页面]` 指 Microsoft Writing Style Guide 的对应页面，`[zh-CN §x.y]` 指 zh-CN 指南的章节号，`[项目]` 指本项目在基线之外补充的规则（通常因为基线没有覆盖 Markdown、仓库结构或 MCP 产品）。所有引用均为转述，原文以链接页面为准。zh-CN 指南区分“软件本地化”和“文档本地化”两套标点约定（§4.1.13）：本项目 `docs/` 下的文档一律按“文档”约定，工作台界面字符串按“软件”约定（界面字符串的规则见工作台设计规范）。

## 1 适用范围与文档类别

| 类别 | 位置 | 是否随交付包分发 | 主要读者 | 语言 |
|---|---|---|---|---|
| 入口说明 | `README.md`、`README.zh-CN.md` | 是 | 新用户 | 英文、中文各一份 |
| 用户文档 | `docs/README.md`、`docs/getting-started`、`docs/guides`、`docs/troubleshooting`、`docs/reference`（`real-machine-ledger.md` 除外） | 是 | 使用者、AI 客户端操作者 | 中文（见第 2 节） |
| 发布记录 | `CHANGELOG.md`、`docs/releases/*.md` | 是 | 使用者、集成者 | 英文 |
| 生成文档 | `docs/reference/tool-matrix.md`、`docs/reference/version-tool-catalog.md`、`docs/releases/v4.0.0-tool-migration.md`、`docs/reference/real-machine-ledger.md` 的标记块、`docs/reference/tool-usage-coverage.json` | 部分 | 使用者、维护者 | 随生成器 |
| 开发文档 | `docs/development/*.md`、`docs/reference/real-machine-ledger.md`、`scripts/README.md`、`src/**/README.md` | 否 | 维护者、实现代理 | 中文 |
| 设计规范 | `docs/development/design-spec/` | 否 | 维护者、实现代理 | 中文 |
| 设计记录与决策 | `docs/development/design/`、`docs/development/decisions/`（见第 14 节） | 否 | 维护者 | 中文 |
| 代理说明 | `CLAUDE.md`、`AGENTS.md`、`plugin/skill/SKILL.md` | `SKILL.md` 随插件分发 | AI 编码与使用代理 | `CLAUDE.md`、`AGENTS.md` 中文；`SKILL.md` 英文 |

MCP 工具描述、参数说明和错误消息不属于本规范，它们的语言政策见 `docs/development/runtime-layout.md`“用户可见文案语言”（新增 MCP 文本一律英文）及协议设计规范。工作台界面文字的措辞规则在工作台设计规范中定义，但用户文档引用界面文字时遵守本规范第 6 节。

## 2 语言策略

- MUST：一个文件只用一种语言写正文。代码、命令、标识符、引用的 TIA 原文和产品名不受此限。[MS: [Global communications](https://learn.microsoft.com/en-us/style-guide/global-communications/)；zh-CN §2.1]
- MUST：不在同一文件中先写中文段落再附一段内容相同的英文段落。需要两种语言时提供两个文件。[项目]
- MUST：同一主题提供两种语言时，英文文件为 `<name>.md`，中文文件为 `<name>.zh-CN.md`（沿用 `README.md`/`README.zh-CN.md` 的做法）。单语文件不加语言后缀。[项目]
- SHOULD：用户文档以中文为主语言；英文读者从 `README.md`、发布记录和 `GetToolUsage` 获取信息。英文用户指南保留期间，必须在 `docs/README.md` 的链接后标明“（英文）”。[项目]
- SHOULD：开发文档用中文。现有英文开发文档（`handoff.md`、`unified-version-framework.md`、`official-tool-usage.md`）可以保持英文，但不得在其中混入中文段落，反之亦然。[项目]
- MUST：发布记录（`CHANGELOG.md`、`docs/releases/*.md`）用英文，遵守 Microsoft Writing Style Guide。[项目]

## 3 语气与读者

- SHOULD：写给要完成任务的人，先给结论和下一步，再给背景。[MS: [Top 10 tips for Microsoft style and voice](https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice)；[Brand voice](https://learn.microsoft.com/en-us/style-guide/brand-voice-above-all-simple-human)]
- SHOULD：用短句和常用词；一句只表达一件事。中文避免多层定语堆叠，长修饰语拆成两句。[MS: [Use simple words, concise sentences](https://learn.microsoft.com/en-us/style-guide/word-choice/use-simple-words-concise-sentences)；zh-CN §4.1.9、§2.1.4]
- SHOULD：中文能省略主语时省略；需要称呼读者时用“你”，不用“您”。英文用第二人称 you。[zh-CN §4.1.12；MS: [Person](https://learn.microsoft.com/en-us/style-guide/grammar/person)]
- MUST：用户文档不出现内部任务编号（如 P6-44、P8-03x）、阶段名、代理名称和评审过程；这些只出现在开发文档和设计记录中。[MS: [Headings](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings) 要求关注读者要做什么；项目]
- MUST：限制和未验收状态写一次、写清楚，并链接到权威页面（[能力与验收边界](../../reference/capabilities.md)、真机台账），不在每段重复免责声明。[MS: Top 10 tips“Get to the point fast”]
- SHOULD：避免带偏见或军事化的措辞；描述进程时用“结束”“停止”而不是“杀掉”。[MS: [Bias-free communication](https://learn.microsoft.com/en-us/style-guide/bias-free-communication)、[Militaristic language](https://learn.microsoft.com/en-us/style-guide/militaristic-language)]
- MUST：描述用户操作用与输入方式无关的动词：中文用“选择”（按钮、菜单、选项卡、复选框），不用“点击”或“单击”；英文用 select，不用 click。[zh-CN §3.2；MS: [Describing interactions with the UI](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/describing-interactions-with-ui)]
- MUST：文档描述当前代码的行为。已删除的功能、开关、入口或旧版本状态不得写成“当前可用”；历史内容移入发布记录或 Git 历史。[项目，参见 MS: [Content planning](https://learn.microsoft.com/en-us/style-guide/content-planning)]

## 4 结构与可扫读性

### 4.1 标题

- MUST：每个文件只有一个一级标题，作为文档标题。[项目]
- MUST：英文标题用句子式大小写（只大写首词和专有名词），结尾不加句号；问号仅在必要时使用。[MS: [Headings](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings)、[Capitalization](https://learn.microsoft.com/en-us/style-guide/capitalization)]
- MUST：中文标题结尾不加标点。[MS: Headings；项目]
- SHOULD：同级标题结构平行：概念类用名词短语，操作类用动宾短语（如“导入 SCL 源”）。[MS: Headings]
- SHOULD：标题短，重点在前；两个标题之间要有正文。[MS: Headings]
- MUST：现行文档的标题不含任务编号、日期或状态（如“（P8-03a / P8-03b）”“（2026-10-09）”）；这类信息写在正文或状态行。设计记录和 ADR 可以在标题中保留编号。[MS: Headings；项目]
- MAY：超过约 300 行的设计记录可以给标题编号（`## 3 方案比较`、`### 3.1 …`）；用户文档不编号。[项目]
- SHOULD：标题中避免“&”“+”和连字符组合词。[MS: Headings]

### 4.2 列表与表格

- MUST：有先后顺序的内容用编号列表，无顺序的用项目符号列表。[MS: [Lists](https://learn.microsoft.com/en-us/style-guide/scannable-content/lists)]
- SHOULD：列表项结构平行；完整句子结尾加句号，短语不加。[MS: Lists]
- SHOULD：只有两个以上属性需要对比时用表格；同类条目的简单罗列用列表。[MS: [Tables](https://learn.microsoft.com/en-us/style-guide/scannable-content/tables)]
- MUST：表格有表头；表头具体（“发布键”而不是“名称”）；最左列放行的标识。[MS: Tables]
- MUST：表格单元格不留空，也不用破折号表示“无”；中文写“无”或“不适用”，英文写 *None* 或 *Not applicable*。[MS: Tables；zh-CN §4.1.13 Em Dash]
- SHOULD：单元格内容尽量一行；长篇说明放在表格外的段落中。[MS: Tables、[Responsive content](https://learn.microsoft.com/en-us/style-guide/responsive-content)]
- MUST：一张表不得被空行打断；表格行前后与段落之间空一行。[项目]

### 4.3 文档开头

- MUST：开发文档、设计记录在一级标题下第一行写状态行：`状态：现行 | 部分过时（第 x 节）| 历史记录`，以及“最后核对：日期 + 提交”。[项目]
- SHOULD：用户文档在标题下用一两句话说明本页适用的版本范围和读者要完成的任务。[MS: [Writing step-by-step instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions)“Scannability”]
- MAY：导航行（`[文档目录](../../README.md) · […]`）放在状态行之后；最多五个链接。[项目]

## 5 操作步骤

- MUST：多步操作用编号列表，一步一个动作；同一界面位置的短动作可以合并。[MS: [Writing step-by-step instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions)]
- MUST：每步是完整句子，以祈使动词开头；需要先说明位置时，位置在前、动作在后（“在 **MCP 与客户端** 页，选择 **写入客户端配置**。”）。[MS: 同上]
- SHOULD：只有一步时用项目符号而不是编号。[MS: 同上]
- MUST：菜单路径写成 `**视图** > **MCP 与客户端**`：每一级单独加粗，`>` 前后各一个空格，`>` 本身不加粗；不用“→”。[MS: 同上“Tips for writing steps”]
- SHOULD：最后一步或步骤后的段落说明怎样判断已成功，以及成功不代表什么（例如“编译返回不等于编译通过”）。[项目，符合 MS: Top 10 tips“Ready to lend a hand”]
- MUST：步骤中的命令写成可直接复制的代码块，并说明从哪个目录运行。[MS: [Code examples](https://learn.microsoft.com/en-us/style-guide/developer-content/code-examples)]
- SHOULD：给 AI 客户端的提示词放在 `text` 代码块中，方括号占位符说明要替换的值。[项目]

## 6 界面文字与键盘

- MUST：引用工作台界面元素时按界面实际显示的文字书写，并加粗：中文文档用中文界面文字，英文文档用英文界面文字。[MS: [Formatting text in instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/formatting-text-in-instructions)]
- 有意偏差：zh-CN 指南的文档约定用中文双引号标出界面元素（§4.1.13 Quotation Marks 的文档示例）。本项目在中文 Markdown 中同样用加粗，不加引号，与英文规则一致，也便于检查器识别。见 DOC-15 的说明。
- MUST：键名和组合键用英文原名和加号，不加粗，不加代码样式：Ctrl+2、Alt+F4。[MS: [Keys and keyboard shortcuts](https://learn.microsoft.com/en-us/style-guide/a-z-word-list-term-collections/term-collections/keys-keyboard-shortcuts)；zh-CN §5.5.3、§5.5.4]
- MUST：引用错误消息原文时加引号（中文文档用“”，英文文档用 ""）；引用错误码时用代码样式（`RESOURCE_UNAVAILABLE`）。[MS: [Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements)]

## 7 代码与技术元素

- MUST：工具名、参数名、JSON 字段、错误码、警告码、枚举值、命令行选项、环境变量、文件名、路径、发布键和代码片段用行内代码样式，不用加粗：`GetToolUsage`、`dryRun: true`、`--release-key`、`TIA_MCP_DATA_DIRECTORY`、`runtime/v21/worker/`、`14sp1`。[MS: [Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements)]
- MUST：Markdown 中不写 HTML 标签和 HTML 实体来表达代码（不用 `<code>`、`&lt;`）；需要尖括号占位符时放进行内代码：`runtime/v<发布键>/`。[项目]
- MUST：代码块标注语言：`powershell`、`json`、`csharp`、`text`（提示词、命令输出）、`mermaid`。[MS: [Code examples](https://learn.microsoft.com/en-us/style-guide/developer-content/code-examples)]
- MUST：占位符在代码中用尖括号并说明含义，在正文中用斜体或尖括号。[MS: Formatting developer text elements“Placeholders”]
- SHOULD：示例值明显是示例（如 `D:\TIA-MCP`），不写真实机器地址、密钥、PID 或私人工程路径。[项目；CLAUDE.md]
- MUST：工具参数、调用序列和结果解释的唯一可编辑来源是 `reference/tool-examples`，文档链接 `GetToolUsage` 或示例 ID，不复制完整参数表。[CLAUDE.md]

## 8 中文排版与中英文混排

### 8.1 空格

- MUST：中文与英文单词、数字之间加一个半角空格（“Foundation 宿主”“120 秒”“V20 引擎 worker”）。[zh-CN §4.1.12、§4.1.13 Comma：指南示例在产品名、数字两侧都留半角空格]
- MUST：全角标点与相邻文字之间不加空格。[zh-CN §4.1.13 Colon、Question Mark]
- MUST：行内代码、加粗与相邻中文之间加一个半角空格，与相邻全角标点之间不加。[项目，延伸 zh-CN 的空格规则]
- MUST：中文段落在 Markdown 源文件中写成一行，不在句中硬换行。GitHub 等渲染器把段内换行显示为空格，会在两个汉字之间插入多余空格。英文段落可以按句换行。[项目]

### 8.2 标点

- MUST：中文句子使用全角标点：，。、；：？！“”‘’（）《》。并列词语用顿号。[zh-CN §4.1.13“For Documentation”表、Comma]
- MUST：英文句子、代码、路径、URL、版本号、数字内部使用半角标点。[zh-CN §4.1.13 Period 例外]
- SHOULD：括号内容含中文时用全角括号（），内容只有英文、数字或代码时用半角括号 ( )，半角括号与括号外的文字之间各留一个空格。[zh-CN §4.1.13 Parentheses（Document localization）]
- MUST：中文正文中引号用“”，嵌套用‘’。[zh-CN §4.1.13 Quotation Marks]
- MUST：中文正文不使用破折号（—），改写句子或用冒号、括号代替。[zh-CN §4.1.13 Em Dash]
- SHOULD：省略号在中文中用“…”，在英文和代码中按原样。[zh-CN §4.1.13 Ellipses]
- MUST：加粗内容不得以全角标点开头或结尾后紧跟文字（如 `**用 AI 操作：**双击`）。按 CommonMark 规则这种写法不会渲染为加粗，而是显示星号。把标点移到加粗外：`**用 AI 操作**：双击`。[项目]
- SHOULD：书名号《》只用于中文出版物名称；文档链接用链接文字，不加书名号。[zh-CN §4.1.13 Book title marks]

### 8.3 英文术语在中文中

- MUST：产品名、商标、工具名、类型名保持英文原文和大小写，不翻译。[zh-CN §5.2]
- MUST：缩写全部大写，不加复数 s（“两个 PLC”而不是“两个 PLCs”）。[zh-CN §4.1.2]
- SHOULD：数量词用具体量词（“两台 CPU”“三个工具”），避免一律用“个”。[zh-CN §4.1.10]
- SHOULD：常用技术词首次出现时可以括注英文（“审批（approval）”）；同一文档后文只用中文。术语见第 10 节。[项目]

## 9 数字、单位、日期与版本

- MUST：与单位连用、在表格中或表示计数的数字用阿拉伯数字。英文正文中零到九的普通数量可以拼写，10 以上用数字。[zh-CN §4.1.10；MS: [Numbers](https://learn.microsoft.com/en-us/style-guide/numbers)]
- MUST：数字与单位之间空一格：`4 MiB`、`120 秒`。英文时间单位按 MS 缩写（sec、min、h）或拼写；中文用“秒”“分钟”。[MS: [Date and time terms](https://learn.microsoft.com/en-us/style-guide/a-z-word-list-term-collections/term-collections/date-time-terms)、[Units of measure terms](https://learn.microsoft.com/en-us/style-guide/a-z-word-list-term-collections/term-collections/units-of-measure-terms)]
- SHOULD：四位及以上的数量加千位分隔逗号（`17,666 项检查`）；年份、端口、PID、版本号、提交号、编号不加。中英文一致。[MS: Numbers“Commas in numbers”]
- 有意偏差：日期统一用 ISO 8601 `YYYY-MM-DD`（如 2026-10-11），中英文一致；需要时间时用 UTC 并写明。MS 要求英文日期写成“month day, year”，本项目的日期主要出现在决策和证据记录中，需要可排序且不受地区格式影响（MS Numbers 的 Global tip 所担心的歧义 ISO 格式同样避免）。见 DOC-30。
- 有意偏差：范围统一用 en dash（–），前后不加空格：`V14 SP1–V19`、`10–180 秒`、`P6-01–P6-41`。zh-CN 指南对中文数字范围使用波浪线（§4.1.13 Wave dash），但本项目的版本范围在英文文档、MCP 文本和中文文档中是同一个写法，统一更利于检索。不使用全角“～”。见 DOC-29。
- MUST：TIA Portal 版本写作 V14 SP1、V15.1、V16、V17、V18、V19、V20、V21；不写 V14SP1、“V14”（指 V14 SP1 时）或“V15”（指 V15.1 时）。发布键写作代码样式 `14sp1`、`15.1`、`16`–`21`。[项目]
- MUST：工具数量、测试数量等会随代码变化的数字不手写在正文中。需要时引用生成文档（如[生成的工具目录](../../reference/version-tool-catalog.md)）或契约基线，写明“以运行时 `tools/list` 为准”。必须出现数字时（如发布说明的版本表）由生成器写入或在发布提交中从契约基线核对。[项目，MS: Numbers“Be consistent”]

## 10 项目术语表

本表是用户文档和开发文档的统一用词。“避免”列的词在新文档中不得用来指代同一对象；历史记录中保留原文。

| 对象 | 中文首选 | 英文首选 | 代码或文件标识 | 避免 | 说明 |
|---|---|---|---|---|---|
| 西门子工程软件 | TIA Portal（首次），后文可简称 TIA | TIA Portal | 不适用 | 博途 | 产品名不翻译 |
| TIA Portal 版本 | V14 SP1、V15.1、V16 … V21 | 同左 | 不适用 | V14SP1、V14（指 V14 SP1）、V15（指 V15.1） | 原版 V14、V15 不受支持 |
| 发布键 | 发布键 | release key | `14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20`、`21` | 版本键、版本号、`release-key`（作名词时） | 行内一律用代码样式 |
| 交付包 | 交付包 | delivery package | `TIA_MCP_Delivery_v<版本>_<日期>.zip` | 安装包、完整包（混用） | 解压后的根目录称“包根” |
| 包根 | 包根 | bundle root | `--bundle-root`、`TIA_MCP_BUNDLE_ROOT` | 安装根、仓库根（指交付包时） | 含 `manifest/package-manifest.json` 的目录 |
| 数据根 | 数据根 | data root | `<包根>/data`、`TIA_MCP_DATA_DIRECTORY` | 数据目录（泛指时可用） | 见运行时布局 |
| 工作台 | 工作台 | Workbench | `TiaOpenness.exe`（包根启动器）→ `runtime/studio/TiaOpenness.exe` | 配置器、统一工作台、统一桌面、Studio（指整个程序时） | 3.x 的独立配置器只在历史记录中称“配置器” |
| 工作台页面 | **MCP 与客户端** 页、**工程操作** 页、**AI 调用**、**审批**、**审计日志**、**环境**、**版本控制** | **MCP & clients**、**Engineering** 等 | 不适用 | 配置页、Studio 页 | 按界面实际文字，加粗 |
| Studio | Studio（只作源码名） | Studio | `src/Studio` | 用来称呼用户界面 | 用户文档写“工作台”或“工程操作页” |
| MCP 宿主 | 宿主（MCP 宿主进程）；指程序时写 FoundationHost | MCP host；FoundationHost | `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>` | 主机、Foundation 主机、基础宿主、基础引擎、PLC 基础引擎、foundation host（小写）、LegacyHost（只作代码名） | 八个版本都用同一个宿主程序 |
| 宿主机 | 宿主机 | host computer | 不适用 | 宿主、主机（指虚拟机外的电脑时） | 只指运行虚拟机的物理电脑，与“宿主”严格区分 |
| worker | worker（总称） | worker | 不适用 | 工作进程、子进程（作术语时） | 由宿主为每个 MCP 会话启动的 net48 进程 |
| PLC worker | PLC worker | PLC worker | `runtime/v<发布键>/worker/TiaMcp.PlcWorker.<发布键>.exe` | 基础 worker、Foundation worker（混用） | 八个版本；V14 SP1–V19 为 STA 线程，V20/V21 为 MTA 线程 |
| 引擎 worker | 引擎 worker | engine worker | `TiaMcp.Engine.V20.exe --engine-worker`（V21 同理） | 完整引擎（指运行形态时）、full engine、引擎宿主 | 已退役：P8-04j2 起 V20/V21 也使用 PLC worker，交付包不含引擎；只在历史记录或仓库交叉检查中出现 |
| 引擎 | 引擎 | engine | `TiaMcp.Engine.V20.exe`、`TiaMcp.Engine.V21.exe` | 完整引擎 | 只指这两个程序；不再交付，只作为 `bin-build/engine-compat` 下的兼容副本供仓库交叉检查，由 P8-04l 删除 |
| V20/V21 专有工具 | V20/V21 专有工具 | V20/V21-only tools | 不适用 | 完整引擎工具 | 35 个能力族已移入 Foundation 适配器，原生调用在 PLC worker 中执行 |
| Foundation 适配器 | Foundation 适配器 | Foundation adapters | `TiaMcp.Adapter.<发布键>.dll`、`src/Adapters` | 共享适配器、Foundation 代码（混用） | 按发布键编译八次 |
| 能力族 | 能力族（F01–F35） | capability family | `PortedFamilies` | 族、工具族（混用） | 已移入适配器的称“已移植族” |
| 工具目录 | 工具目录 | tool catalog | `tools/list` | 工具清单（指运行时目录时） | lite 档、full 档；V20/V21 的 `tool-catalog.json` 已随引擎退出交付包 |
| lite 档 / full 档 | lite 档、full 档 | lite profile、full profile | `--profile full`、`TIA_MCP_PROFILE` | 精简模式、完整模式 | 只有 V20/V21 有 lite 档 |
| MCP 会话 | MCP 会话 | MCP session | 不适用 | 连接、会话（与 TIA 会话混用时） | 每个 MCP 会话一个 worker |
| 绑定 | 绑定（工程绑定） | binding | `ConnectPortal`、`AttachOpenProject` | 连接工程 | 先连接 TIA 进程，再附着工程 |
| TIA 进程租约 | TIA 进程租约 | TIA process lease | `<数据根>/leases/<PID>-<启动 ticks>.lease` | 实例锁、实例租约（混用） | 状态 `IDLE`、`BUSY`、`UNCERTAIN`、`RELEASED` |
| Openness 通道 | Openness 通道（lane） | Openness lane | `ToolDispatchLanes` | 串行锁、全局锁 | 每个会话一条，原生调用串行 |
| 审批 | 审批 | approval | `approval.settings`、`TiaMcp.Approval.v1.*` | 确认、批准（作功能名时） | 工作台对 MCP 写调用的人工审批 |
| 预检 | 预检 | precheck | `APPROVAL_PRECHECK_REFUSED` | 预审 | 审批前用预览参数运行的检查 |
| 预览 | 预览 | preview | `dryRun: true`、`mode: preview` | 演练、试运行 | 不审批、不写审计 |
| 审计链 | 审计链 | audit chain | `<数据根>/logs/audit` | 审计日志（指哈希链时）、审批日志 | `tia audit verify` 校验 |
| 调用日志 | 调用日志 | call journal | `<数据根>/diagnostics/calls-*.jsonl` | 原生日志（混用） | 与审计链分开 |
| 控制通道 | 工作台控制通道 | Workbench control channel | `TiaMcp.Workbench.v1.*` | 控制管道（混用） | 显示、读取、预填三类工具 |
| 操作端管道 | 操作端管道 | operator pipe | `TiaMcp.Operator.v1.*` | 操作通道 | 工作台附着 MCP 会话 |
| V4 信封 | V4 信封 | V4 envelope | `schemaVersion`/`ok`/`data`/`error`/`meta` | 响应格式、返回结构（作术语时） | 见协议规范 |
| 结果未知 | 结果未知 | unknown outcome | `OUTCOME_UNKNOWN`、`meta.outcome = unknown` | 状态未知、不确定结果 | 会话随后需要重置 |
| 操作前拒绝 | 操作前拒绝 | rejected before operation | `rejected-before-operation` | 预拒绝 | 原生操作未开始 |
| 真机验收 | 真机验收（L5） | real-machine acceptance | `NOT RUN` | 实机验证、VM 验证（混用） | 结果记入真机台账 |
| 真机台账 | 真机台账 | real-machine ledger | `docs/reference/real-machine-ledger.md` | 验收清单、证据索引（混用） | 不随交付包分发 |
| 标准包 | 标准包 | standard package | `templates/standards/`、`<数据根>/standards` | 规范包、模板包 | 生成框架的输入之一 |
| 机器描述 | 机器描述 | machine description | `MachineDescription` | 设备清单（作文件名时） | 生成的唯一结构化输入 |
| 生成计划 / 生成运行 | 生成计划、生成运行 | generation plan、generation run | `tiamcp.plan/1`、`tiamcp.run/1` | 方案、执行记录（混用） | 一次 Apply 审批一次 |
| 暂存 | 导入暂存 | import staging | `<包根>/staging/<会话>/<批次>/` | 上传目录 | 不在数据根下 |
| V4 会话 | V4 会话；上下文清楚时简称会话 | V4 session | 不适用 | 连接（指会话状态时） | 宿主为一个 MCP 会话维护的全部状态（worker、租约、绑定、会话锁、计划、句柄等），4.0 中由 MCP 传输会话承载，定义见 [V4 工具协议规范](v4-protocol.md) 第 0.4 节 |
| worker 代次 | worker 代次 | worker generation | 不适用 | worker 代（混用） | 一次 worker 进程的生命周期；故障后经 `RestartOpennessWorker` 或 `DisconnectPortal` 开始新代次 |
| TIA 工程 | 工程 | project | `.ap<主版本>` 文件 | 项目（在文档中混用） | 文档统一写“工程”；工作台界面字符串的用词按工作台规范 LOC-07 决定 |
| 写栅栏 | 生成写栅栏 | generation write fence | `generation-run-active` | 围栏 | 生成运行期间拒绝同一会话中不属于该运行的写调用 |
| 发散 | 发散 | divergence | `diverged` | 偏离（指生成运行时） | 执行前的工具预览与已批准的计划不符，运行停止且不写入 |
| 偏差 | 偏差（规范偏差） | deviation | `CS-nn`、`MCP-nn`、`API-nn`、`UI-nn`、`DOC-nn` | 偏离（混用） | 现有代码或文档与设计规范的差异，记在偏差清单并标明状态 |

## 11 文件、目录与链接

### 11.1 文件

- MUST：文件名用小写字母、数字和连字符，扩展名 `.md`；语言后缀见第 2 节。[项目]
- MUST：UTF-8 无 BOM，仓库内 LF 行尾。建议在 `.gitattributes` 中为 `*.md` 声明 `text eol=lf`。[项目；AGENTS.md 对 C# 的同类要求]
- MUST：文档按第 1 节的类别放在对应目录；用户文档不得依赖不随包分发的文件才能读懂。[项目]
- SHOULD：架构图用 Mermaid 写在 Markdown 中，便于审阅差异；需要 SVG 时同时保留可编辑源或生成步骤。[项目]
- MUST：图片有说明用途的替代文本（如“TIA Portal Openness MCP 4.0 组件关系图”），不写“架构图”这类泛称。[MS: [Alternative text](https://learn.microsoft.com/en-us/style-guide/accessibility/alternative-text)]

### 11.2 链接

- MUST：链接文字具体，说明目标页面是什么，不用“这里”“此处”“链接”。[MS: [URLs and web addresses](https://learn.microsoft.com/en-us/style-guide/urls-web-addresses)]
- MUST：两端都随交付包分发，或两端都不分发时，用相对链接。[项目]
- 有意偏差（规则化）：随包分发的文档链接到不分发的文件（`docs/development/`、`docs/reference/real-machine-ledger.md`、`src/`、`reference/tool-examples/`、`scripts/`）时，用 `https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/<路径>` 绝对链接，否则解压后的交付包中链接失效。发布记录中的这类链接在发布提交中固定到发布标签（`blob/v4.0.0/…`）。见 DOC-24。
- MUST：随包分发的文档之间不得使用绝对链接。[项目]
- SHOULD：正文不出现裸 URL；来源列表中用页面标题作链接文字，必要时在其后写访问日期。[MS: URLs and web addresses]
- SHOULD：链接到其他文档的章节锚点时，只链接稳定的标题；中文标题锚点随标题改动失效，仓库检查应核对锚点存在。[项目]
- MUST：不在文档中写真实机器地址、PID、会话 ID 和本机绝对路径（示例路径除外）。[CLAUDE.md]

## 12 生成文档

- MUST：生成文档第一段说明“本文件由 `<命令>` 从 `<输入>` 生成，不要手工编辑”，命令写成可复制的 `dotnet run --project build-tools/release -- <命令>` 形式。[项目]
- MUST：生成器对同一输入逐字节输出相同内容，并提供 `-Check`；生成文档在 `.gitattributes` 中声明 `text eol=lf`。[项目；AGENTS.md]
- MUST：生成文档不含生成时间和本机信息；需要版本时写契约基线或提交号。[项目]
- MUST：手写文档中嵌入的生成段落用成对标记 `<!-- generated:<名称>:begin -->`、`<!-- generated:<名称>:end -->` 包围，标记外的内容由人维护。[项目，统一现有 `real-machine-ledger.md` 的标记写法]
- SHOULD：同一类生成文档用同一种语言（建议英文，表头和固定词由生成器提供），表格遵守第 4.2 节，空值写 *None* 或 *Not applicable*。[项目；MS: Tables]
- MUST：生成文档的内容不得被手写文档复述（例如工具数量），手写文档链接生成文档即可。[项目]

## 13 发布记录与 CHANGELOG

- MUST：英文，遵守 Microsoft Writing Style Guide；标题用句子式大小写。需要副标题时用冒号（`v4.0.0: Draft`），破折号两侧不加空格。[MS: Headings、Capitalization、[Dashes and hyphens](https://learn.microsoft.com/en-us/style-guide/punctuation/dashes-hyphens/)]
- MUST：每条变化先说变了什么、对用户有什么影响，再给细节；工具名、参数、错误码用代码样式。[MS: Top 10 tips；Formatting developer text elements]
- MUST：发布版本中不留 `TODO` 注释，不链接已归档或即将归档的内部文档作为规范出处；协议与错误码链接到现行协议规范。[项目]
- MUST：版本表中的数字来自生成器或契约基线，并在发布提交中核对。[项目]
- SHOULD：结构固定为：Summary、Changes、Tools per release、Upgrade、Known limits、Acceptance status。[项目]

## 14 设计记录与架构决策记录（ADR）

维护者于 2026-10-11 决定用架构决策记录（Architecture Decision Record，ADR）记录设计决策：一页只记一个决策。参考 Microsoft Azure Well-Architected Framework 的 [Maintain an architecture decision record (ADR)](https://learn.microsoft.com/en-us/azure/well-architected/architect-role/architecture-decision-record) 与 Michael Nygard 的 [Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)（常见的 Nygard 模板）。ADR 索引与待补写清单见 [架构决策记录](../decisions/README.md)，模板文件为 [0000-template.md](../decisions/0000-template.md)。

### 14.1 何时必须写 ADR

满足任一条件的决定必须有 ADR（Microsoft 指南：只记录影响结构、关键质量属性或难以撤回的决定）：

- 改变进程、程序集或部署结构（例如宿主与 worker 的划分、引擎退役、工作台附着会话）；
- 改变 MCP 对外契约或其兼容策略（工具命名、信封、注册规则、版本差异处理）；
- 改变安全或数据完整性边界（审批、审计、租约、结果未知后的处理、写栅栏）；
- 改变发布、验收或质量门禁的规则（真机验收时机、合并门禁、不放宽检查）；
- 选择或替换外部基线、格式和第三方组件（设计规范基线、标准包格式、JSON 库）；
- 推翻或替代已有 ADR。

不需要 ADR：单个工具的修复、措辞改动、重构中不改变行为的移动、实施细节（写在设计文档中）。

### 14.2 文件与编号

- 位置：`docs/development/decisions/NNNN-<短标题>.md`，`NNNN` 为四位递增序号（`0001` 起），短标题用小写英文和连字符，例如 `0007-one-worker-per-mcp-session.md`。[项目]
- 目录下的 `README.md` 列出全部 ADR：编号、标题、状态、日期，按编号排序。[项目]
- 语言：中文（与开发文档一致）。标题用陈述句写出决定本身。[项目]
- ADR 是只追加的日志：已采纳的 ADR 不改写正文（只允许修正笔误和补链接）；决定改变时写新的 ADR，并在两边互相链接，旧 ADR 状态改为“已被 NNNN 取代”。[Azure WAF ADR“append-only log”]
- 回溯补写既往决定时，日期写原决定日期，正文注明“回溯记录（写于 YYYY-MM-DD）”。[Azure WAF ADR：棕地项目应回溯补写]

### 14.3 状态值

| 状态 | 含义 |
|---|---|
| 提议（Proposed） | 已写出，等待维护者决定 |
| 已采纳（Accepted） | 维护者已决定，代码与文档按此执行 |
| 已拒绝（Rejected） | 讨论后未采用，保留理由 |
| 已被 NNNN 取代（Superseded by NNNN） | 被后续 ADR 替代 |
| 已废弃（Deprecated） | 不再适用，且没有替代决定 |

### 14.4 模板

```markdown
# NNNN 决定的标题（陈述句）

- 状态：提议 | 已采纳 | 已拒绝 | 已被 NNNN 取代 | 已废弃
- 日期：YYYY-MM-DD（决定日期）
- 决策者：维护者（提出者：…）
- 置信度：高 | 中 | 低
- 相关：ADR NNNN；设计文档链接；任务编号

## 背景

需要解决的问题、约束和推动因素。只写做决定时已知的事实。

## 选项

1. 选项 A：要点；优点；缺点。
2. 选项 B：要点；优点；缺点。

## 决定

采用的选项，以及一句话理由。

## 后果

- 正面：…
- 负面与取舍：…
- 后续工作：…
- 验证方式：用什么检查或验收证明决定被执行。
```

模板结合了 Nygard 模板的“背景、决定、状态、后果”与 Azure WAF 建议的“备选方案、取舍、置信度”。详细推导写在设计文档中并从“相关”链接，ADR 本身必须能独立读懂。

### 14.5 设计文档

- MUST：设计文档放在 `docs/development/design/`，开头有第 4.3 节的状态行；维护者的决定同时写成 ADR，设计文档链接 ADR，而不是在文末维护“维护者决定”小节。[项目]
- MUST：`bin-build/` 下的设计草稿不是正式记录；被采纳的设计在合并对应实现时移入 `docs/development/design/`，文档不得链接 `bin-build/` 路径。[项目；CLAUDE.md：设计交接不纳入发布]
- SHOULD：实施完成的设计文档改为“历史记录”状态，或把仍有效的结论并入现行文档后删除，由 Git 历史保留原文。[项目；repository-layout.md 的现行做法]

## 15 代理说明文件

- SHOULD：`CLAUDE.md`、`AGENTS.md` 只写必须遵守的规则和入口链接，用祈使句，一条规则一行；详细流程链接到开发文档，不复制。[项目]
- MUST：代理说明中的阶段性约束在阶段结束后删除或改写（例如“阶段 0–5 不改变 MCP 对外接口”）。[项目]
- MUST：`plugin/skill/SKILL.md` 随插件分发，英文，描述当前版本的使用方式，遵守第 7 节。[项目]

## 16 落地与检查

以下检查建议加入 `dotnet run --project build-tools/release -- check-repository`（尚未实施）：

| 检查 | 对应规则 |
|---|---|
| 中文与英文、数字之间缺空格；全角标点旁多余空格 | 8.1 |
| 中文段落硬换行 | 8.1 |
| 加粗以全角标点结尾后紧跟文字 | 8.2 |
| 中文行内破折号、全角“～” | 8.2、9 |
| 禁用术语清单（第 10 节“避免”列）在用户文档中出现 | 10 |
| 随包文档之间的绝对链接；随包文档指向不分发文件的相对链接；锚点不存在 | 11.2 |
| 生成文档缺少生成说明或含时间戳 | 12 |
| 手写文档中出现工具数量 | 9 |
| 表格被空行打断；表格空单元格 | 4.2 |
| 发布记录中的 `TODO` 注释与 HTML 标签 | 7、13 |

## 附录 A Microsoft 来源索引

| 主题 | 链接 | 本规范使用的章节 |
|---|---|---|
| 总览 | [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/) | 全文 |
| 语气 | [Top 10 tips for Microsoft style and voice](https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice)、[Brand voice](https://learn.microsoft.com/en-us/style-guide/brand-voice-above-all-simple-human) | 3 |
| 可扫读内容 | [Headings](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings)、[Lists](https://learn.microsoft.com/en-us/style-guide/scannable-content/lists)、[Tables](https://learn.microsoft.com/en-us/style-guide/scannable-content/tables) | 4 |
| 步骤 | [Writing step-by-step instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions)、[Describing interactions with the UI](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/describing-interactions-with-ui)、[Formatting text in instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/formatting-text-in-instructions) | 5、6 |
| 大小写 | [Capitalization](https://learn.microsoft.com/en-us/style-guide/capitalization) | 4.1、13 |
| 数字与日期 | [Numbers](https://learn.microsoft.com/en-us/style-guide/numbers)、[Date and time terms](https://learn.microsoft.com/en-us/style-guide/a-z-word-list-term-collections/term-collections/date-time-terms)、[Units of measure terms](https://learn.microsoft.com/en-us/style-guide/a-z-word-list-term-collections/term-collections/units-of-measure-terms) | 9 |
| 标点 | [Punctuation](https://learn.microsoft.com/en-us/style-guide/punctuation/)、[Dashes and hyphens](https://learn.microsoft.com/en-us/style-guide/punctuation/dashes-hyphens/) | 8、9、13 |
| 用词 | [Word choice](https://learn.microsoft.com/en-us/style-guide/word-choice/)、A–Z 词表 | 3、10 |
| 无障碍 | [Accessibility guidelines and requirements](https://learn.microsoft.com/en-us/style-guide/accessibility/accessibility-guidelines-requirements)、[Alternative text](https://learn.microsoft.com/en-us/style-guide/accessibility/alternative-text) | 3、11 |
| 全球化 | [Global communications](https://learn.microsoft.com/en-us/style-guide/global-communications/)、[Writing tips](https://learn.microsoft.com/en-us/style-guide/global-communications/writing-tips) | 2 |
| 开发者内容 | [Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements)、[Code examples](https://learn.microsoft.com/en-us/style-guide/developer-content/code-examples) | 6、7 |
| 链接 | [URLs and web addresses](https://learn.microsoft.com/en-us/style-guide/urls-web-addresses) | 11 |
| 无偏见 | [Bias-free communication](https://learn.microsoft.com/en-us/style-guide/bias-free-communication) | 3 |
| 中文 | [Chinese (Simplified) Localization Style Guide](https://aka.ms/chinese-simplified-styleguide)（[全部语言列表](https://learn.microsoft.com/en-us/globalization/reference/microsoft-style-guides)） | §2.1（语气与灵活改写）、§3.2（无障碍：选择而非点击）、§4.1.1–§4.1.2（缩写）、§4.1.9（修饰语）、§4.1.10（数字与量词）、§4.1.12（代词：你）、§4.1.13（标点）、§5.2（产品名）、§5.5（错误消息、键名） |
| ADR | [Maintain an architecture decision record (ADR)](https://learn.microsoft.com/en-us/azure/well-architected/architect-role/architecture-decision-record)、[Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions) | 14 |

## 附录 B 与基线的有意偏差汇总

| 编号 | 规则 | 基线 | 本项目做法 | 理由 |
|---|---|---|---|---|
| DOC-15（部分） | 中文文档中界面元素的标记 | zh-CN §4.1.13：文档中用中文双引号 | 加粗，不加引号 | 与英文 MS 规则一致；Markdown 中便于检查 |
| DOC-24 | 随包文档链接不分发文件 | 无（MS 只要求链接文字具体） | GitHub `blob/master` 绝对链接，发布记录固定到标签 | 交付包不含开发文档，相对链接会失效 |
| DOC-29 | 范围符号 | zh-CN §4.1.13：数字范围用波浪线；MS：英文用 from…through 或 en dash | 中英文统一 en dash | 版本范围在全部文本中写法一致 |
| DOC-30 | 日期格式 | MS：month day, year | ISO 8601 | 可排序，不受地区格式影响 |
| DOC-19 | 归档文档中的 HTML | 本规范 7 | 已冻结的归档文档保留原样 | 归档内容不再维护 |
| DOC-20（部分） | 标题编号与任务号 | MS Headings 的读者导向 | 设计记录与 ADR 可保留 | 记录类文档需要与任务对照 |
| DOC-25（部分） | 历史发布记录中的裸 URL | MS URLs | `CHANGELOG.md` 的历史条目不改写 | 历史记录保持原文 |
