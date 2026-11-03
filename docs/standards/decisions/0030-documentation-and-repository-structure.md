# 0030 文档和仓库根目录采用本项目自己的结构，取代 ADR 0026 的目录部分

- 状态：已采纳
- 日期：2026-11-03
- 决策者：维护者
- 置信度：高
- 相关：[ADR 0026](0026-documentation-structure-and-language.md)（目录部分被本决定取代，语言规则继续有效）、[ADR 0016](0016-architecture-decision-records.md)、[ADR 0027](0027-archify-diagrams.md)；[文档规范](../documentation.md)第 1、14、15 节；[本项目采用的开发规范](../README.md)；[开发计划](../../plan/plan.md) DOCS-1、P8-10

## 背景

[ADR 0026](0026-documentation-structure-and-language.md) 决定按姊妹项目 EplanMCP 的目录（`docs/architecture/`、`docs/adr/`、`docs/process/`、`docs/status/`、`docs/design/`、`docs/tools/`、`docs/templates/`、`docs/diagrams/` 等）重组开发文档，但只执行了一部分。到 2026-11-03，现行规范、偏差清单、流程手册、计划、交接、路线图和已完成阶段的设计仍分散在 `docs/development/`、`docs/development/design-spec/`、`docs/standards/`、`docs/process/`、`docs/status/`、`docs/templates/` 和 `docs/references.md` 中，同一规则常在几份文件里各写一遍。2026-11-02 起仓库遵循通用《工程开发手册》v1.1，手册已经覆盖大部分写作、ADR 和流程规则，项目文档只需记录补充和偏差。

仓库根目录也有同样的问题：根目录有本机杂物，构建输出不全在 `bin-build/` 下，`runtime/README.md` 单独描述运行时布局，`schemas/` 与 `reference/`、`hooks/` 与 `plugin/` 各自分开，有两个 `.slnx`，`CLAUDE.md` 与 `AGENTS.md` 重复了一部分代理规则。

约束：

- 随包文档和被交付清单、生成器、检查或已发布包引用的路径不能随意移动；必须移动时在同一批次修改引用并加测试。
- 在途分支需要在搬迁合并后变基，搬迁要分批，每批有排他窗口。
- 已采纳的 ADR 只追加，不改写。

## 选项

1. 继续执行 ADR 0026 的 EplanMCP 目录：两个仓库同一张地图；目录多，许多目录只有一两个文件，规则仍会分散。
2. 本项目自己的少量目录，按文档用途划分（规范、计划、手册、历史），同类主题合并成一个文件：文件少，每条规则只写一次；与 EplanMCP 和手册第 2.6 节的目录不同。
3. 维持现状，只修过期内容：没有搬迁成本；现行与历史资料继续混放。

## 决定

采用选项 2。维护者撤回 ADR 0026 的目录部分，批准以下结构；ADR 0026 的语言规则（含语言表）继续有效。

开发文档：

| 目录 | 文件 |
|---|---|
| `docs/standards/` | `README.md`（采用的手册范围、项目偏差、外部参考）、`engineering-handbook.html`、`development.md`、`design.md`、`ui.md`、`security.md`、`documentation.md`、`decisions/`（ADR） |
| `docs/plan/` | `plan.md`（版本、放行门槛、未决决定）、`progress.md`（当前状态、分支、测试者反馈） |
| `docs/manuals/` | `workflow.md`、`validation.md`、`release.md`、`real-machine-testing.md` |
| `docs/archive/` | 冻结的历史资料，不再修改 |

- 用户文档（`docs/getting-started/`、`docs/guides/`、`docs/troubleshooting/`、`docs/reference/`、`docs/releases/`、`docs/licenses/`、`docs/assets/`）的位置不变。
- 图（[ADR 0027](0027-archify-diagrams.md)）放在 `docs/assets/`，不设 `docs/diagrams/`。
- 新主题写进同类的现有文件；新建独立文档需要理由。

仓库根目录：

- 删除根目录杂物，构建与运行输出统一放在 `bin-build/` 下。
- `runtime/README.md` 的内容并入 `docs/standards/design.md`。
- `schemas/` 并入 `reference/`，`hooks/` 并入 `plugin/`。
- 只保留一个 `.slnx`。
- 各代理共用的规则写在 `AGENTS.md`，`CLAUDE.md` 导入它，只补充 Claude 专有的规则。
- `Version.props` 留在根目录。

分批执行，每批先用只含 `git mv` 的提交搬迁，再用只改链接和路径常量的提交修正引用，最后改写内容：

| 批次 | 范围 | 状态 |
|---|---|---|
| 0 | 本机清理：根目录杂物和输出移到 `bin-build/` | 已完成 |
| 1 | 文档：按上表搬迁、合并和改写开发文档（DOCS-1） | 进行中 |
| 2 | 数据和脚本，与 P8-10 一起 | 未开始 |
| 3 | 源码、测试和交付包路径，最后进行 | 未开始 |

## 后果

- 正面：开发文档集中到四个目录中的少量文件，每条规则只写一次；手册已有的规则只链接不重复；历史资料与现行文档分开。
- 负面与取舍：
  - 与 EplanMCP 和手册第 2.6 节的目录不同，在[本项目采用的开发规范](../README.md)中登记为偏差。
  - 已发布包和外部记录中指向旧开发文档路径的 `blob/master` 链接会失效；是否为它们放重定向页（ADR 0026 原定保留到 4.1.0 之后）由维护者决定（待核对）。
  - 每个批次都需要排他窗口，在途分支要变基。
- 后续工作：批次 2、3 各自写任务说明；批次 3 同时修改交付清单、包清单、插件和仓库检查中的路径。
- 验证方式：每批的搬迁提交用 `git diff -M` 确认 100% 相似；`check-repository` 的链接与锚点检查通过；交付规则不包含开发文档目录。
