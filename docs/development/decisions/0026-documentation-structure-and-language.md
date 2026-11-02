# 0026 开发文档按 EplanMCP 的目录结构重组，路径稳定优先，docs 下统一用中文

- 状态：已采纳
- 日期：2026-10-22（维护者批准文档重组方案的全部 15 项决定）；回溯记录（写于 2026-11-01）
- 决策者：维护者
- 置信度：中
- 相关：[ADR 0015](0015-microsoft-design-spec-baseline.md)、[0016](0016-architecture-decision-records.md)、[0027](0027-archify-diagrams.md)；[文档写作规范](../design-spec/documentation-style.md) 第 1、2、11、14 节；任务 DOCS（批次 D0–D5）、P8-04m、P8-63

## 背景

`docs/development/` 中混放着现行规范、流程手册、状态文档、设计记录和已完成阶段的历史资料，读者很难判断哪份文档是现行的。姊妹项目 EplanMCP 使用 `adr/`、`architecture/`、`standards/`、`process/`、`design/`、`templates/`、`tools/`、`diagrams/` 和 `references.md` 的目录结构，维护者和 AI 代理在两个仓库之间切换时需要同一张“地图”。

约束：

- 随包文档的路径被包清单、项目蓝图、插件、生成器、仓库检查和已发布包中的绝对链接引用，不能随意移动。
- 文档规范第 2 节只把开发文档用中文写成 SHOULD，`handoff.md`、`unified-version-framework.md`、`official-tool-usage.md` 等仍是英文；用户指南中还有几份英文指南。
- 发布记录必须英文（文档规范第 13 节），`generate-prerelease-notes` 原样复制审计清单。
- ADR 只追加，已采纳 ADR 的正文不能改写。

## 选项

1. 维持现状，只修过期内容：没有搬迁成本；现行与历史资料继续混放，两个仓库的结构不一致。
2. 开发文档放在 `docs/development/` 下的新子目录：不改变顶层；两个仓库的路径仍不同。
3. 开发文档放在 `docs/` 顶层，与 EplanMCP 同构；随包路径不动，必须移动的路径在同一批次改代码并加测试：两个仓库同一张地图；搬迁期间与在途分支冲突，需要排他窗口和重定向页。

## 决定

采用选项 3。

- 目录：开发文档放在 `docs/architecture/`、`docs/adr/`、`docs/standards/`、`docs/process/`、`docs/status/`、`docs/design/`、`docs/tools/`、`docs/templates/`、`docs/diagrams/` 和 `docs/references.md`。这些目录不在 `scripts/operations/delivery-files.json` 的允许列表中，不随包。交接、路线图和发布计划放在独立的 `docs/status/`。
- 路径稳定：`docs/README.md`、`docs/getting-started/`、`docs/guides/`、`docs/troubleshooting/`、`docs/reference/` 中被交付或被机器读取的文件、`docs/releases/`、`docs/licenses/`、`docs/assets/` 和 `docs/development/evidence/` 不移动。必须移动且被代码读取的路径，在同一批次修改代码、策略文件和 Python 检查并加测试。
- 先搬后写：每个搬迁批次的第一个提交只含 `git mv`，第二个提交只改链接和路径常量，内容重写放在后续批次。
- 旧链接：只为已发布包中实际出现的绝对链接目标放重定向页，保留到 4.1.0 发布之后。
- 历史资料：移入 `docs/archive/`，状态行改为“历史记录”，不删除；被检查读取的文件（如 `phase6-review.md`）字节不变。
- 设计记录命名：`YYYY-MM-DD-<任务>-<主题>.md`。
- 工具规格：`docs/tools/` 只为新工具和契约变更写规格，现有工具的事实由生成目录提供。
- 语言：

| 文档类型 | 位置 | 语言 |
|---|---|---|
| 入口说明 | `README.md`、`README.zh-CN.md` | 英文、中文各一份 |
| 用户文档（随包） | `docs/README.md`、`docs/getting-started/`、`docs/guides/`、`docs/troubleshooting/`、`docs/reference/` 中手写的文件、`templates/**/README.md` | 中文，只维护一份；现有英文指南译为中文，不保留英文版 |
| 生成文档 | `tool-matrix.md`、`version-tool-catalog.md`、迁移表 | 由生成器决定（英文） |
| 发布记录 | `CHANGELOG.md`、`docs/releases/*.md`、`docs/reference/api-audit-vm-checklist.md` | 英文 |
| 开发文档 | `docs/` 下的开发目录、ADR、设计记录 | 中文（文档规范第 2 节由 SHOULD 改为 MUST） |
| 代理说明 | `CLAUDE.md`、`AGENTS.md` | 中文 |
| MCP 可见文本与插件技能 | 工具描述、`GetToolUsage`、`plugin/skill/SKILL.md` | 英文 |
| GitHub 社区文件 | `.github/*.md`、PR 与 Issue 模板 | 英文，写明欢迎中文 |
| 代码旁说明 | `src/**`、`tests/**`、`build-tools/**` 下的 README | 英文 |
| 冻结文件 | `manifest/history/contracts-v3/README.md` 等 | 保持原样 |

- 改写现有文档时同时采用维护者 2026-11-01 的用词决定：TIA Portal 的项目写“项目”；工作台界面和用户文档对 MCP 宿主及其 worker 的总称写“服务”；开发文档按对象写 FoundationHost、宿主或 worker。
- ADR 0021 的英文正文是既有偏差，按只追加规则不改写，在文档偏差清单中登记。

## 后果

- 正面：两个仓库的开发文档使用同一结构；现行规范、流程、状态和历史资料分开；新写的标准页（安全、测试、MCP 工具设计、开发流程、工具规格模板、参考资料）可以直接放在最终位置。
- 负面与取舍：
  - 搬迁批次需要排他窗口，在途分支要在搬迁合并后变基。
  - 重定向页在 4.1.0 之前一直存在。
  - 现有英文开发文档和英文用户指南都要译为中文，翻译技术内容有出错风险；标识符、命令和界面文字不翻译。
- 后续工作：
  - 批次 D1a、D1b 执行搬迁与重定向页；D2a–D2c2 重写内容；D3a、D3b 补齐标准页、模板和文档规范第 1、2、11、14 节的规则更新。
  - 文档规范更新前，新写的开发文档已按本决定使用中文。
  - 开发目录不随包的样例测试加入仓库检查（只收紧）。
- 验证方式：
  - 每个批次运行 `dotnet run --project build-tools/release -- check-repository -NoBinaries` 与文档方案列出的文档关卡，链接和锚点全部通过。
  - 纯搬迁提交用 `git diff -M` 确认 100% 相似。
  - 交付规则不包含新开发目录，由样例测试证明。
