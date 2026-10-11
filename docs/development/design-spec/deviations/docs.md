# 文档偏差清单

状态：现行 · 测量与最后核对：2026-10-11，master `3a227657`

本清单把现有文档与 [文档写作规范](../documentation-style.md) 及其 Microsoft 基线逐条比较。每项给出规则出处、位置与计数、示例、建议和状态。建议分三类：**4.0 前修改**、**4.0 后修改**、**有意偏差**（写入规范附录 B，不再修改）。状态按维护者 2026-10-11 的决定填写：默认采用建议，另有决定的条目（DOC-01、DOC-02、DOC-05）在状态中注明；“4.0 前 - P8-63”的条目由任务 P8-63 与 P7-11b 一起完成。状态取值见 [设计规范索引](../README.md)。测量覆盖 `docs/**/*.md`、`README.md`、`README.zh-CN.md`、`CLAUDE.md`、`AGENTS.md`、`scripts/README.md`、`plugin/skill/SKILL.md`，必要时加上 `CHANGELOG.md`、`runtime/README.md`、`src/Studio/README.md`。除特别说明外，计数不含生成文档（`tool-matrix.md`、`version-tool-catalog.md`、`v4.0.0-tool-migration.md`）和 `docs/licenses/` 下的许可证原文。计数由临时脚本按正则统计，排除了代码块；接近边界的数字（如硬换行）是近似值，示例均已人工核对。

## 摘要

| 状态 | 项数 | 编号 |
|---|---:|---|
| 4.0 前 - P8-63 | 21 | DOC-01–09、DOC-11–16、DOC-18、DOC-21–23、DOC-32、DOC-36 |
| 4.0 后 | 12 | DOC-10、DOC-17、DOC-20、DOC-25–28、DOC-31、DOC-33–35、DOC-37 |
| 有意偏差（已接受） | 4 | DOC-19、DOC-24、DOC-29、DOC-30 |
| 合计 | 37 | DOC-01–DOC-37 |

部分项目同时包含两类处理（例如 DOC-15 的菜单箭头 4.0 前修改，界面文字加粗为有意偏差），表中按主要建议计数，细节见各项。

### 最重要的 10 项

| 顺序 | 编号 | 问题 | 为什么重要 |
|---:|---|---|---|
| 1 | DOC-01 | 用户文档仍指导使用已删除的 `--isolate-openness` 开关 | 照做会使宿主拒绝启动（未知选项），属于错误指导 |
| 2 | DOC-02 | 8 份文档给出 7 套互相矛盾的工具数量，且都与当前契约基线不符 | 用户无法判断哪个版本有哪些工具；发布说明也受影响 |
| 3 | DOC-03 | 入口、架构和代码位置仍按阶段 7 之前描述（含 README 架构图） | 新读者和实现代理会按旧结构理解系统 |
| 4 | DOC-12 | `configuration.md` 有两节只用英文写，其他 4 页中英重复 | 导入暂存、覆盖导入等关键说明中文读者看不到 |
| 5 | DOC-05 | 重构计划、路线图、交接中的任务状态过期 | CLAUDE.md 要求接续开发先读交接；过期状态会误导 |
| 6 | DOC-06 | 4.0 发布说明草稿留有 TODO，错误码规范链接到将归档的文档 | 发布说明是 4.0 的交付物 |
| 7 | DOC-04 | 多处仍写“v3.2.0 当前实现”“v3.2.0 ZIP 仍使用独立配置器” | 已发布的 3.3.0 已含统一工作台，说法错误 |
| 8 | DOC-07 | 同一程序称“工作台”“Workbench”“配置器”“统一工作台”“Studio” | 用户找不到文档说的界面 |
| 9 | DOC-08 | 宿主进程有 9 种叫法；“宿主”与“宿主机”混用 | 虚拟机场景中“宿主”可能被理解为物理电脑 |
| 10 | DOC-18 | 4.0 发布说明用加粗标记约 177 个代码元素，并用 HTML 实体 | 与 MS 开发者文本格式规则不符，复制命令和名称易出错 |

### 已符合基线的方面

测量中以下方面基本符合规范，不列为偏差：中英文之间加空格（约 15,700 处有空格，仅 6 处缺失，见 DOC-37）；中文后的半角标点只有 5 处；标题结尾没有句号；除一份许可证原文外每个文件只有一个一级标题；没有使用“您”；全部代码块都标注了语言。

## A 内容时效与一致性

### DOC-01 已删除的隔离开关仍写成可用功能

- 规则：规范 3“文档描述当前代码的行为”[项目；MS: [Content planning](https://learn.microsoft.com/en-us/style-guide/content-planning)]。
- 事实：P7-08 退役了引擎的 MCP 宿主层与隔离模式。`src/Logic/CliOptions.cs:179` 对 `--isolate-openness`、`--no-isolate-openness`、`--worker-timeout-seconds` 等抛出“Engine MCP host modes have retired”；`src/FoundationHost/HostOptions.cs:35` 的选项表没有这两个开关，传入时报“Unknown or incomplete option”并退出。`docs/releases/v4.0.0.md` 也写明已删除。
- 位置与计数：`isolate-openness` 共 16 处，9 个文件。
  - 用户文档：`docs/guides/openness-worker-isolation.md`（整页以此为主题）；`docs/getting-started/beginners.zh-CN.md`“V20/V21 服务启动与隔离”（2 处）；`docs/getting-started/cli.md`“V20/V21 MCP 服务的隔离开关”（4 处，另有一段英文重复）；`docs/getting-started/configuration.md`“V20/V21 Openness worker”（4 处，另有英文重复）；`docs/troubleshooting/openness-limitations.md` 第一节；`docs/README.md` 的“可选工作进程隔离”链接。
  - 开发文档：`native-call-diagnostics.md`“与工作进程隔离的关系”；`native-mcp-session-tests.md`（整页以引擎 EXE 加 `--isolate-openness` 作为 MCP 入口）；`refactor-plan.md` P6-54 行。
- 示例：cli.md“启动 MCP 服务时用 `--isolate-openness` 强制开启，用 `--no-isolate-openness` 强制关闭”。
- 建议：**4.0 前修改**。改写为现行行为：每个 MCP 会话一个 worker，超时策略、`RestartOpennessWorker`、`DisconnectPortal` 恢复流程（v4.0.0.md 已有准确描述）；删除或重写 `openness-worker-isolation.md`，`native-mcp-session-tests.md` 标为历史记录。
- 状态：4.0 前 - P8-63（维护者 2026-10-11 另行确认：改正 `--isolate-openness` 的指导）

### DOC-02 工具数量互相矛盾

- 规则：规范 9“会随代码变化的数字不手写”[项目；MS: [Numbers](https://learn.microsoft.com/en-us/style-guide/numbers)“Be consistent”]。
- 当前契约基线（`manifest/contracts/v4/baseline/*.json`）：14sp1 215、15.1 237、16 253、17 260、18 267、19 277、20 503、21 514；V20/V21 lite 84。
- 文档中的数字：

| 文件 | V14 SP1 / V15.1 / V16 / V17 / V18 / V19 | V20 / V21 | lite |
|---|---|---|---|
| `README.md` | 59 / 60 / 62 / 62 / 62 / 64 | 487 / 498 | 73 |
| `README.zh-CN.md` | 57 / 58 / 60 / 60 / 60 / 62 | 487 / 498 | 73 |
| `docs/getting-started/configuration.md` | 59 / 60 / 62 / 62 / 62 / 64 | 477 / 488 | 60 |
| `docs/getting-started/beginners.zh-CN.md` | 未给出 | 未给出 | 60 |
| `docs/reference/version-tools.md` | 189 / 205 / 214 / 216 / 220 / 230 | 503 / 514 | 84 |
| `docs/releases/v4.0.0.md` | 214 / 230 / 241 / 243 / 249 / 259 | 503 / 514 | 84 |
| `docs/development/runtime-layout.md` | 未给出 | 500 / 511 | 82 |
| `docs/releases/v4.0.0-tool-migration.md`（生成） | 215（14sp1） | 未核对 | 不适用 |

- 建议：**4.0 前修改**。手写文档删除具体数字，改为链接生成目录和 `tools/list`；README 与发布说明的版本表在发布提交中从契约基线生成或核对。v4.0.0.md 中的 `TODO(P8)` 注释已承认此问题。
- 状态：4.0 前 - P8-63（维护者 2026-10-11 另行确认：工具数量由生成器写入，手写文档只链接生成目录）

### DOC-03 入口、架构与代码位置的描述过期

- 规则：规范 3 [项目]。
- 位置与示例：
  - `docs/reference/capabilities.md`：入口写成“V14 SP1–V19 的 `TiaMcp.FoundationHost.exe`，以及 `TiaMcp.Engine.V20.exe` / `TiaMcp.Engine.V21.exe`”；“仍未完成”一节称基础宿主尚未迁移硬件网络和库（B2、B6 已移植）。
  - `docs/development/unified-version-framework.md`：运行形态表把 V20/V21 写成“Full .NET Framework engines”；称完整引擎有“optional worker-isolation contract”。
  - `docs/development/repository-layout.md`：“`src/Engine`、`src/FoundationHost`、`src/PlcWorker` | V20/V21 完整引擎、六版 Foundation 宿主及八版 worker”（实际是八版宿主、六版 PLC worker、两版引擎 worker）；表中没有 `src/EngineHost`。
  - `docs/development/tool-development.md`：“完整引擎的工具声明位于 `src/Engine/ModelContextProtocol/Tools`”（已移植族的声明在 `src/Shared`）。
  - `docs/assets/architecture.svg`（README 引用）：以 `TiaMcp.Engine.V21.exe` 作为 MCP 服务器方框。
  - `docs/guides/hardware-network.md`：“The V19 foundation engine has a narrower hardware search/device-creation route”（B2 已在旧版按 API 启用网络工具）。
  - `docs/reference/ecosystem-tools.md`：`runtime\v21\TiaMcp.Engine.V21.exe install-plc-tools`（实际路径在 `runtime\v21\worker\`）。
  - `docs/development/validation.md` 第 501、526 行：以引擎 EXE 作为响应捕获和稳定性检查的 MCP 入口，路径为 `runtime/v21/TiaMcp.Engine.V21.exe`。
  - `docs/development/refactor-plan.md` 验收层级表与约束 2：引用已删除的 `Build-MultiVersion.ps1`、`Release.ps1`。
- 计数：把 `TiaMcp.Engine.V2x.exe` 当作入口或旧路径的描述 19 处、11 个文件（其中 CLI 用法合法的除外）。
- 建议：**4.0 前修改**用户文档和 README 架构图（用 [架构概览](../architecture.md) 中的图替换）；开发文档随 P7-11b 一并修改。
- 状态：4.0 前 - P8-63

### DOC-04 过时的版本表述

- 规则：规范 3 [项目]。
- 计数：`v3.2.0` 17 处、12 个文件（不含历史发布记录本身）。
- 示例：`README.md`“The published v3.2.0 ZIP retains its original separate interfaces; build the current source to use the unified desktop until the next release”（3.3.0 已发布统一工作台）；`beginners.zh-CN.md`“v3.2.0 ZIP 仍使用独立配置器与 Studio”及以 v3.2.0 文件名举例的哈希命令；`configuration.md`“已发布 v3.2.0 的独立配置器不采用此界面”；`openness-limitations.md`“本页描述 v3.2.0 当前实现”；`THIRD-PARTY-NOTICES.md`“本清单登记随 v3.2.0 八版 MCP”；`release-workflow.md`、`validation.md`、`native-call-diagnostics.md` 各 1 处。
- 建议：**4.0 前修改**。用户文档改为不依赖具体旧版本号的写法，或改为 4.0。
- 状态：4.0 前 - P8-63

### DOC-05 计划、路线图与交接中的状态过期

- 规则：规范 4.3 状态行、规范 3 [项目]；CLAUDE.md 要求接续开发先读 `handoff.md`。
- 示例：
  - `refactor-plan.md`：P8-03“todo”（B1–B6 已合并）、P8-10“todo”（批 1、2、3、7 已合并）、P8-20“review”（a–d 已合并）、P8-21“todo”（a–e 已合并）、P8-31“todo”（a–e4 已合并）；P6-54“todo”但正文写明被阶段 7 取代；P6-69、P6-70“todo”而路线图写“已合并”；阶段 8“2026-10-10 更新”只列到 B3。
  - `roadmap.md`：标题仍为“路线图与待办（v3.3.0）”；阶段 7 行写“P7-01～P7-04、P7-10 已完成…待恢复 5 个生命周期工具、worker 健壮性、打包切换”（P7-04b–P7-08 已完成）。
  - `handoff.md`：只记到 B3、Python 批 1–2，没有 B4–B6、P8-21c–e、P8-31e1–e4、Python 批 3 与 7。
  - `phase8-port-plan.md`：只有 B1 的迁移状态。
  - `phase8-workbench-control.md` 第 6 节：P8-20c/d 标“review”，P8-21c–e 没有状态。
  - `phase8-generation-framework.md` §6.3“计划内参数全部预先确定”已被 2026-10-10 的 D3 决定修改（只有 `expectedPlanHash` 迟填）。
  - `response-and-errors.md` 仍以“完整引擎 ConnectProject 的 processStartUtc”作为现行参数（`ConnectProject` 已撤回）。
- 建议：**4.0 前修改**。状态只在一处维护（重构计划表），交接只写当前事实并链接计划；过期的设计结论随 ADR 更新（见 [架构决策记录](../../decisions/README.md)）。
- 状态：4.0 前 - P8-63（维护者 2026-10-11 另行确认：刷新状态类文档；首批 ADR 已写入 [架构决策记录](../../decisions/README.md)，其余 4.0 前的 ADR 在该页的待补写清单中）

### DOC-06 4.0 发布说明草稿中的 TODO 与规范链接

- 规则：规范 13“发布版本中不留 `TODO`，不链接将归档的内部文档作为规范出处”[项目]。
- 位置：`docs/releases/v4.0.0.md` 3 个 `<!-- TODO … -->` 注释（工具数量、phase6-review 链接、验收段落）；“Result envelope and errors”把错误码定义链接到 `phase6-review.md` 第 3 节，而该节缺少 `COMPILE_ERRORS` 和后续警告码，且文件头已标为归档；“Acceptance status”为占位文字。
- 建议：**4.0 前修改**。错误码与警告码链接到 [V4 工具协议规范](../v4-protocol.md)；验收段落在最终发布候选后从真机台账填写。
- 状态：4.0 前 - P8-63

## B 术语

### DOC-07 工作台的名称不统一

- 规则：规范 10 术语表 [项目；MS: [Use technical terms carefully](https://learn.microsoft.com/en-us/style-guide/word-choice/use-technical-terms-carefully)]。
- 计数：“工作台”241、“Workbench”63（另有小写 workbench 24）、“配置器”31（用户文档 16：beginners 4、configuration 7、cli 1、capabilities 1、errors 1、THIRD-PARTY-NOTICES 2）、“统一工作台”6、“Studio”343（多数指源码或工程操作页，部分指整个程序）。
- 示例：beginners.zh-CN.md“完整解压交付包，打开配置器，选择 **虚拟机 ↔ 宿主机**”；configuration.md 标题“MCP 图形配置指南”与正文“配置器生成的客户端命令”；capabilities.md 表中单列“配置器”一行。
- 建议：**4.0 前修改**用户文档：程序统称“工作台”，页面用界面实际文字；“配置器”只在描述 3.x 历史时使用。开发文档随 P7-11b 修改。
- 状态：4.0 前 - P8-63

### DOC-08 MCP 宿主的叫法过多，与“宿主机”冲突

- 规则：规范 10 [项目]。
- 计数：FoundationHost 198、Foundation host/foundation host 44、Foundation 主机 20、Foundation 宿主 10、基础宿主 10、基础引擎 11、PLC 基础（引擎）7、LegacyHost 22、单独的“主机”71、单独的“宿主”248；“宿主机”（虚拟机外的电脑）18。
- 示例：README.zh-CN.md 版本表“PLC 基础引擎”“Foundation 主机 + 引擎 worker”；scl.md“V14 SP1–V19 基础引擎”；refactor-plan.md“引擎与 Foundation 主机并发”；beginners.zh-CN.md 同时使用“宿主机”（物理电脑）。
- 建议：**4.0 前修改**用户文档（统一为“宿主”或程序名 FoundationHost，物理电脑统一为“宿主机”）；开发文档 **4.0 后**随整理修改。
- 状态：4.0 前 - P8-63（用户文档）；开发文档 4.0 后

### DOC-09 “完整引擎”“基础引擎”仍被当作运行形态名称

- 规则：规范 10 [项目]。
- 计数：“完整引擎”82、“full engine(s)/full-engine”42、“基础引擎”11。
- 说明：阶段 7 后八个版本都由 FoundationHost 提供 MCP；V20/V21 的差别是工具目录和引擎 worker。用户文档中“适用于 V20/V21 完整引擎”出现在 13 篇指南开头。
- 建议：**4.0 前修改**用户文档，改为“V20/V21 专有工具”“适用于 V20/V21”；“引擎”只指 `TiaMcp.Engine.V20/V21.exe`。
- 状态：4.0 前 - P8-63

### DOC-10 发布键的叫法与格式

- 规则：规范 9、10 [项目；MS: [Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements)]。
- 计数：“发布键”35、“版本键”3（capabilities.md 1、adapter-merge.md 2）、“release key”15；正文中未加代码样式的发布键很常见（例如 adapter-merge.md“14sp1–16 用 net461”）。
- 建议：**4.0 后修改**；capabilities.md 的“版本键”随 DOC-03 一起改。
- 状态：4.0 后

### DOC-11 操作动词用“点击”

- 规则：规范 3 [zh-CN §3.2（用“选择”而不用“点击”）；MS: [Describing interactions with the UI](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/describing-interactions-with-ui)]。
- 计数：“点击”35；用户文档 30（beginners.zh-CN.md 18、configuration.md 12）；开发文档 5（其中 4 处描述测试中的模拟点击，可保留）。
- 示例：“选择 TIA 版本，点击 **自动检测**”。
- 建议：**4.0 前修改**用户文档，改为“选择”。
- 状态：4.0 前 - P8-63

## C 语言

### DOC-12 中文文档内嵌英文段落与中英重复

- 规则：规范 2“一个文件只用一种语言”“不附内容相同的英文段落”[项目；MS: [Global communications](https://learn.microsoft.com/en-us/style-guide/global-communications/)]。
- 位置：`configuration.md` 第 155–188 行两整节只有英文（“Workbench rendering and history”“Import staging on the TIA machine”，9 段，含暂存目录、配额、覆盖导入与恢复的关键规则，中文正文中没有对应内容），第 84 行一段英文重复上一段中文；`cli.md`、`troubleshooting/errors.md`、`troubleshooting/openness-limitations.md`、`development/runtime-layout.md` 各有一段英文重复前文中文；`engine-decomposition.md` 第 797 行一段英文。
- 建议：**4.0 前修改**。英文两节译成中文并入对应章节；重复的英文段删除。
- 状态：4.0 前 - P8-63

### DOC-13 用户文档语言混用且索引未标注

- 规则：规范 2 [项目]。
- 现状：`docs/guides` 的 20 篇中 5 篇是英文（`hardware-network.md`、`openness-worker-isolation.md`、`plc/builders.md`、`hmi/unified-actions.md`、`hmi/unified-theme-layout.md`）；`docs/reference` 中 `version-tools.md`、`openness-coverage.md`、`recovery-guidance.md` 是英文，`capabilities.md`、`ecosystem-tools.md` 是中文。中文索引 `docs/README.md` 直接链接它们，没有标注语言。
- 建议：**4.0 前修改**索引，在英文页链接后标“（英文）”；按规范 2 的语言策略翻译放在 **4.0 后**。
- 状态：4.0 前 - P8-63（索引标注语言）；翻译 4.0 后

### DOC-14 英文指南开头的遗留“Document id”行

- 规则：规范 4.3 [项目]。
- 位置：4 处，`hardware-network.md`、`plc/builders.md`、`hmi/unified-actions.md`、`hmi/unified-theme-layout.md`。代码中已没有读取这些 ID 的地方（`src`、`build-tools`、`scripts` 中无引用），是旧指南工具的残留。
- 建议：**4.0 前修改**（删除，或随 DOC-13 翻译时去掉）。
- 状态：4.0 前 - P8-63

## D 结构与格式

### DOC-15 菜单路径与界面文字的写法

- 规则：规范 5、6 [MS: [Writing step-by-step instructions](https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions)：用 `>` 分隔，前后加空格，`>` 不加粗]。
- 现状：用户文档中整条菜单路径加粗并用“→”分隔 4 处（beginners.zh-CN.md 2、configuration.md 2，例如 `**视图 → MCP 与客户端**`），另有“**帮助 → 更新 → 更新引擎…**”类写法；用户文档中“→”共 18 处（部分是流程箭头，可保留）。
- 界面文字加粗：zh-CN 指南的文档约定用中文双引号标界面元素（§4.1.13 Quotation Marks），本项目一律加粗。
- 建议：菜单路径 **4.0 前修改**为 `**视图** > **MCP 与客户端**`；界面文字加粗记为 **有意偏差**（与英文 MS 规则一致，便于检查器识别）。
- 状态：4.0 前 - P8-63（菜单路径）；界面文字加粗为有意偏差（已接受）

### DOC-16 加粗以全角标点结尾，渲染失败

- 规则：规范 8.2 [项目，依据 CommonMark 定界规则]。
- 位置：按 CommonMark 左右定界规则逐个检查加粗片段，渲染失败的共 3 处，全部在 `README.zh-CN.md` 第 17–19 行（`**用 AI 操作：**双击`、`**自己操作：**打开`、`**先看演示：**在`），GitHub 上显示为带星号的原文。
- 建议：**4.0 前修改**，把冒号移到加粗外。
- 状态：4.0 前 - P8-63

### DOC-17 中文段落硬换行

- 规则：规范 8.1 [项目，依据 zh-CN 指南不在汉字之间加空格的约定]。
- 计数：约 549 处汉字与汉字（或中文与代码）跨行相接。主要在开发文档：engine-decomposition.md 167、adapter-merge.md 69、validation.md 59、runtime-layout.md 42、response-and-errors.md 36；随包文档约 20 处（ecosystem-tools.md 8、capabilities.md 5、cli.md 4、configuration.md 3）。渲染时每处在两个汉字之间出现一个空格。
- 建议：**4.0 后修改**开发文档（可用脚本合并）；随包文档的约 20 处随 DOC-12 等 4.0 前的改动一起修正。
- 状态：4.0 后；随包文档的约 20 处随 P8-63 一起修正

### DOC-18 发布说明用加粗标记代码元素并使用 HTML

- 规则：规范 7、13 [MS: [Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements)：工具、参数、开关、错误码、文件名用代码样式]。
- 位置：`docs/releases/v4.0.0.md` 共 255 个加粗片段，其中约 177 个是工具名、参数、命令行开关、错误码或文件名（如 `**GetToolUsage**`、`**--profile full**`、`**INVALID_ARGUMENT**`、`**TiaMcp.FoundationHost.exe**`）；另有 10 个 `<code>` 标签和 24 个 HTML 实体（`&lt;key&gt;`）。
- 建议：**4.0 前修改**（发布说明是 4.0 交付物）。
- 状态：4.0 前 - P8-63

### DOC-19 归档文档中的 HTML

- 规则：规范 7“Markdown 中不写 HTML”[项目]。
- 位置：`docs/development/phase6-review.md` 741 个 `<br>`、70 个 `<a id=…>` 锚点，文件头已注明“Archive: marker blocks are frozen”。
- 建议：**有意偏差**。该文件冻结并按 P7-11b 归档，不再修改；新文档不得这样写。
- 状态：有意偏差（已接受）

### DOC-20 标题中的任务编号、日期与编号

- 规则：规范 4.1 [MS: [Headings](https://learn.microsoft.com/en-us/style-guide/scannable-content/headings)：标题面向读者要做的事]。
- 计数：标题含任务编号、日期或决策标记 32 个（如“B1 迁移状态（P8-03a / P8-03b）”“维护者决定：旧版注册规则（2026-10-09，P8-03x）”）；编号标题 120 个，全部在 phase6-review、phase8-* 设计文档中。
- 建议：设计记录和 ADR 中保留（规范已允许，属于有意做法）；现行文档在整理时去掉，**4.0 后修改**。
- 状态：4.0 后

### DOC-21 文档索引中的重复与误导链接

- 规则：规范 4.2、11.2 [MS: [Lists](https://learn.microsoft.com/en-us/style-guide/scannable-content/lists)]。
- 位置：`docs/README.md`：“类型组”和“构造器”都指向 `guides/plc/builders.md`；“变量绑定”和“变量删除”都指向 `guides/hmi/tag-binding.md`；“能力边界”与“当前能力与验收边界”是同一链接的两次出现；“可选工作进程隔离”指向已失效的功能（DOC-01）。
- 建议：**4.0 前修改**。
- 状态：4.0 前 - P8-63

### DOC-22 `scripts/README.md` 表格断开与笔误

- 规则：规范 4.2“一张表不得被空行打断”[项目]。
- 位置：主表之后有两行表格行被空行隔开且没有表头（“| 手动检查 | …”“| 检查 | `… check-ratchet` …”），渲染为带竖线的普通段落；“（`-NoBinaries` / `-NoBinaries` 用于…）”重复；“本地 Bridge 和 八版本”中两个汉字之间多一个空格。
- 建议：**4.0 前修改**（随 P7-11b，工作量很小）。
- 状态：4.0 前 - P8-63

### DOC-23 架构图过期且替代文本泛化

- 规则：规范 11.1 [MS: [Alternative text](https://learn.microsoft.com/en-us/style-guide/accessibility/alternative-text)]。
- 位置：`README.md`、`README.zh-CN.md` 各引用一次 `docs/assets/architecture.svg`，替代文本为“Architecture”“架构图”；图中内容过期（DOC-03）。
- 建议：**4.0 前修改**：换成 4.0 架构图，替代文本说明图的内容。
- 状态：4.0 前 - P8-63

### DOC-32 英文标题中带空格的破折号

- 规则：规范 13 [MS: [Dashes and hyphens](https://learn.microsoft.com/en-us/style-guide/punctuation/dashes-hyphens/)：破折号两侧不加空格]。
- 位置：`docs/releases/v4.0.0.md` 标题“v4.0.0 — Draft”；历史文件 `v3.2.0.md`、`v3.3.0.md` 同样写法（3 处）。
- 建议：**4.0 前修改** v4.0.0.md 的标题（发布时本来就要改）；历史发布记录不改。
- 状态：4.0 前 - P8-63

### DOC-36 历史设计文档没有状态标识

- 规则：规范 4.3、14.5 [项目]。
- 位置：`engine-decomposition.md`、`adapter-merge.md`（阶段 3、4 设计，仍描述隔离代理和“引擎隔离 已发布（可选）”）没有状态行；`phase6-review.md` 文件头写“Archive”，状态行却写“工具接线与产品迁移待实施”；`native-mcp-session-tests.md` 描述已删除的入口；`phase8-*.md` 仍标“研究稿”，但多数内容已实施。`response-and-errors.md` 已有说明框，是可参照的写法。
- 建议：**4.0 前修改**，随 P7-11b 加状态行或归档。逐份建议如下（整理自 2026-10-11 的设计与规范文档清单）。
- 状态：4.0 前 - P8-63（与 P7-11b 一起）

| 建议 | 文档 |
|---|---|
| 保留 | `native-lifecycle-tests.md`、`official-tool-usage.md`（随术语更新）、`openness-coverage.md`、`real-machine-ledger.md`、生成目录、`v4.0.0-tool-migration.md`、`evidence/*.json`、`gap-review-2026-10.md`（作带日期的记录）、`phase8-port-plan.md` 与 `phase8-python-plan.md`（到对应任务完成） |
| 更新 | `handoff.md`、`roadmap.md`、`refactor-plan.md`、`runtime-layout.md`、`tool-development.md`、`validation.md`、`release-workflow.md`、`repository-layout.md`、`native-call-diagnostics.md`、`native-mcp-session-tests.md`、`phase8-workbench-control.md`（并移入 `docs/development/design/`）、`phase8-generation-framework.md`（P8-31j1，并移入 `docs/development/design/`）、`capabilities.md`、`version-tools.md`、`ecosystem-tools.md`、`v4.0.0.md`、`architecture.svg`、`AGENTS.md`、`src/*/README.md` |
| 并入设计规范 | `unified-version-framework.md` 的版本处理（并入 [架构概览](../architecture.md)）；`adapter-merge.md` 的协议 2 一节；`phase6-review.md` 第 3、8 节、`response-and-errors.md` 的 D1 一节与 `recovery-guidance.md`（并入 [V4 工具协议规范](../v4-protocol.md)）；`runtime-layout.md` 的文案语言政策 |
| 归档 | `engine-decomposition.md`、`adapter-merge.md`（协议 2 一节除外）、`response-and-errors.md`（D1 一节除外）、`phase6-review.md`（并入后）、`runtime-layout.md` 的 G7 与 P2-05 任务表 |
| 已完成 | [阶段 7 宿主与 worker 拆分设计](../../design/p7-host-worker-split.md) 与 [分阶段生成执行设计](../../design/p8-31-staged-generation.md) 已移入仓库（2026-10-11）；P8-20 的早期草稿已被 `phase8-workbench-control.md` 取代，不移入 |

## E 链接

### DOC-24 指向 GitHub master 的绝对链接

- 规则：规范 11.2 [项目]。
- 计数：`blob/master/` 绝对链接 89 处（docs/README.md 17、ecosystem-tools.md 11、v4.0.0.md 10、v3.3.0.md 9、THIRD-PARTY-NOTICES.md 7 等）；`CHANGELOG.md` 另有 67 处指向历史提交，属于历史记录。
- 说明：`scripts/operations/delivery-files.json` 不分发 `docs/development/`、`docs/reference/real-machine-ledger.md`、`src/`、`reference/tool-examples/`，随包文档必须用绝对链接才能在解压后的交付包中打开。抽查未发现随包文档之间使用绝对链接。
- 建议：**有意偏差**，按规范 11.2 规则化；另在发布提交中把 `docs/releases/v4.0.0.md` 的链接固定到 `v4.0.0` 标签，可由发布工具完成。
- 状态：有意偏差（已接受）

### DOC-25 裸 URL

- 规则：规范 11.2 [MS: [URLs and web addresses](https://learn.microsoft.com/en-us/style-guide/urls-web-addresses)]。
- 计数：105 处（CHANGELOG.md 63、phase8-generation-framework.md 来源列表 35、gap-review-2026-10.md 7）。
- 建议：开发文档的来源列表 **4.0 后修改**为“页面标题”链接；`CHANGELOG.md` 历史条目作为有意偏差不改写。
- 状态：4.0 后；`CHANGELOG.md` 的历史条目为有意偏差（已接受）

### DOC-26 链接到中文标题锚点

- 规则：规范 11.2 [项目]。
- 计数：19 个链接、8 个文件（例如 `runtime-layout.md#软件自身的数据目录`、`release-workflow.md#packagequick-测试包与-full-发布候选包`）。标题改动后锚点失效且没有检查。
- 建议：**4.0 后修改**：在仓库检查中核对锚点存在；整理文档时减少深层锚点。
- 状态：4.0 后

## F 标点、数字与日期

### DOC-27 全角括号包住纯英文或数字

- 规则：规范 8.2 [zh-CN §4.1.13 Parentheses：内容只有单字节字符时用半角括号，括号外留空格]。
- 计数：655 处（全角括号共约 2,440 处）。示例：“（127.0.0.1）”“（G9）”“（Ctrl+2）”“（`runtime/dotnet`）”。用户文档中少（每篇 1–4 处），主要在开发文档和 CHANGELOG.md（119）。
- 建议：**4.0 后修改**，用脚本批量处理并加入检查；新文档按规范写。
- 状态：4.0 后

### DOC-28 中文行内破折号与表格中的破折号占位

- 规则：规范 4.2、8.2 [zh-CN §4.1.13 Em Dash；MS: [Tables](https://learn.microsoft.com/en-us/style-guide/scannable-content/tables)：不用破折号表示空值]。
- 计数：中文行内“—”75 处，大多是表格空值占位（phase8-port-plan.md 34、phase8-python-plan.md 21、adapter-merge.md 7、phase8-workbench-control.md 7）；生成的 `v4.0.0-tool-migration.md` 用“ — ”作空值 1,509 处。
- 建议：**4.0 后修改**：手写表格改为“无”“不适用”；迁移表由生成器改为 *None*。
- 状态：4.0 后

### DOC-29 范围符号

- 规则：zh-CN §4.1.13 对中文数字范围用波浪线；MS 英文正文用 from…through，表格中用 en dash。
- 现状：中英文统一用 en dash（264 处，如“V14 SP1–V19”“10–180 秒”）；另有 10 处全角“～”（roadmap.md 4、phase6-review.md 4、refactor-plan.md 1、repository-layout.md 1）；“V14 SP1-V19”连字符写法 1 处（handoff.md）。
- 建议：en dash 记为 **有意偏差**（规范 9）；10 处“～”和 1 处连字符在 4.0 后统一。
- 状态：有意偏差（已接受）；10 处“～”和 1 处连字符 4.0 后统一

### DOC-30 日期格式

- 规则：MS [Date and time terms](https://learn.microsoft.com/en-us/style-guide/a-z-word-list-term-collections/term-collections/date-time-terms)：英文写“month day, year”。
- 现状：148 处 ISO 8601 日期，中英文都用（如 handoff.md“Maintainer decision 2026-10-09”）；没有“October 9, 2026”写法。
- 建议：**有意偏差**（规范 9）。
- 状态：有意偏差（已接受）

### DOC-31 千位分隔与时间单位

- 规则：规范 9 [MS: Numbers“Commas in numbers”；Date and time terms 的单位缩写]。
- 计数：带千位逗号的数 117 处，四位以上不带逗号的数 230 处（含端口、年份等本应不加的情况；同一文件中也混用，如 refactor-plan.md“1,056 个源文件”与“4768”“3748 项”）；时间单位“N s”19 处（phase8-workbench-control.md 9、v4.0.0.md 2 等）与“N 秒”18 处混用，MS 英文缩写为 sec。
- 建议：**4.0 后修改**；v4.0.0.md 的“120 s”在 DOC-18 修改时一并改为“120 seconds”。
- 状态：4.0 后

### DOC-37 少量空格与标点例外

- 规则：规范 8.1、8.2 [zh-CN §4.1.13]。
- 计数：中英文之间缺空格 6 处（如 refactor-plan.md“通用化P3-07b”、phase8-port-plan.md“worker边界”）；中文与行内代码之间缺空格 31 处；中文后用半角逗号、冒号 5 处（phase6-review.md、response-and-errors.md、tool-development.md）。
- 建议：**4.0 后修改**，加入检查后批量处理。
- 状态：4.0 后

## G 生成文档与仓库约定

### DOC-33 生成文档的说明行与语言不统一

- 规则：规范 12 [项目]。
- 现状：`tool-matrix.md` 是中文表头，含“生成时间：2026-10-10 20:53:05”和“产品文件版本：3.3.0.0”；`version-tool-catalog.md`、`v4.0.0-tool-migration.md` 是英文，说明行措辞各不相同；`real-machine-ledger.md` 的嵌入块用 `<!-- …capabilities:begin/end -->` 标记；`tool-usage-coverage.json` 没有说明。
- 建议：**4.0 后修改**生成器输出格式（统一说明行、去掉时间戳、统一语言与标记）；4.0 前只需保证发布候选重新生成这些文件。
- 状态：4.0 后

### DOC-34 Markdown 行尾未在 `.gitattributes` 中声明

- 规则：规范 11.1 [项目]。
- 现状：只有 3 份生成文档声明 `text eol=lf`；抽查的手写文档在索引中是 LF，但工作区为 CRLF（`core.autocrlf`），依赖本机设置。
- 建议：**4.0 后修改**，增加 `*.md text eol=lf`。
- 状态：4.0 后

### DOC-35 代理说明中的过期约束

- 规则：规范 15 [项目]。
- 位置：`AGENTS.md`“阶段 0–5 不改变 MCP 对外接口：工具名称、参数名与类型、返回结构和错误语义保持不变”（当前处于阶段 8，该约束已不适用，但可能让实现代理误以为不能改契约）。
- 建议：**4.0 后修改**（或下次修改 AGENTS.md 时一并删除）。
- 状态：4.0 后

## 附录：测量方法

- 中英文间距：正则统计“汉字后紧跟字母或数字”与“汉字、空格、字母或数字”的出现次数，先把行内代码替换为占位符，排除代码块。
- 加粗渲染：按 CommonMark 的左定界与右定界规则（Unicode 空白与标点类别）逐个检查 `**…**` 片段的开闭定界符。
- 硬换行：上一行以汉字、中文标点或行内代码结尾，下一行以汉字、代码或英文开头，且两行都不是列表、表格或标题行。
- 术语：对正文（排除代码块和行内代码）做正则计数，“宿主”与“宿主机”分开统计。
- 工具数量：读取 `manifest/contracts/v4/baseline/*.json` 中 `tools` 与 `liteTools` 的长度。
- 分发范围：`scripts/operations/delivery-files.json` 的 `include` 与 `legacyCleanup`。
