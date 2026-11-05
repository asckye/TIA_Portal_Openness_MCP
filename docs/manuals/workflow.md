# 开发流程

状态：现行 · 最后核对：2026-11-04，master `ef1988bc`

本文写改动从任务说明到进入 master 的项目做法，附录取代已归档的[工具规格模板](../archive/development/templates/tool-spec.md)；通用流程见[《工程开发规范》通用规范](https://github.com/asckye/engineering-standards/blob/v2.0/general/README.md)第 18、19、23 节，命令见[验证分层](validation.md)，发布见[发布流程](release.md)。

## 1 角色

职责按通用规范第 19.1 节，本项目的对应和补充：

| 通用规范的角色 | 本项目 | 补充 |
|---|---|---|
| 维护者 | 维护者 | 批准 ADR、检查与断言或阈值的改变、每次发布 |
| 主导代理 | Claude | 不写新功能代码，合并时的一行变基修正和文档笔误除外 |
| 实现代理 | Codex | 规则见 [AGENTS.md](../../AGENTS.md) |

## 2 任务生命周期

1. 新事项先进 `bin-build/refactor/tasks/followups.md`（不入库），当天转写到[开发计划](../plan/plan.md)或 ADR；任务说明只引用开发计划的编号。
2. Claude 定位根因后写任务说明：按通用规范模板 [T.4](https://github.com/asckye/engineering-standards/blob/v2.0/general/appendix/t-templates.md#tpl-task)，另写 Siemens SDK 路径（`-p:SiemensEngineeringDirectory=<绝对路径>`）、点名的套件、生成器与要重新捕获的基线、留给主机的检查。Codex 的执行规则只在 AGENTS.md 中维护，不复制进说明。
3. 从当前 master 建 worktree 和 `refactor/<任务 ID>` 分支；Codex 实现，运行涉及的测试类和 Debug 项目构建，文档变更运行 `check-repository -NoBinaries`。
4. 独立 AI 先审查交付，Claude 再审查 diff 并在主机运行 `merge-check` 和其余涉及的检查；失败先定位，再带具体问题退回同一 Codex 会话，不反复重跑完整门禁。
5. 用明确路径提交，变基到 master，通过合并门后快进推送，删除任务分支；更新[开发进度](../plan/progress.md)和任务状态。

## 3 变更级别与完成条件

级别按通用规范第 18.3 节。L2 的设计产物是工具规格（附录）或设计说明，新工具和 MCP 契约变化（工具名、参数、返回结构、错误语义）必须写工具规格；L3 另加 ADR（[文档规范](../standards/documentation.md)第 14.1 节）。开始前，L2 的规格已审查，L3 的 ADR 已采纳，依赖的分支已合并。

完成条件为通用规范第 23 节，另加：

- 生成文件只由生成器产生：依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`，迁移表变化时运行 `generate-release-migration`，各自的 `-Check` 通过。
- 契约变化通过正式工具重新捕获工具定义快照并逐项解释差异；破坏性变化写入发布说明和迁移表；新增 MCP 文本为英文。全部八版逐工具响应捕获的移除见 [ADR 0031](../standards/decisions/0031-release-path.md)。
- 用户可见的变化写入 `CHANGELOG.md` 的 `[Unreleased]` 段；准备发布前由维护者统一版本条目、日期、`Version.props`、插件版本与发布说明，`prepare` 只检查一致性，不改写这些输入。

## 4 验证

验证按 [ADR 0031](../standards/decisions/0031-release-path.md) 分为合并、完整准备和公开；删除、跳过或放宽检查仍须维护者批准。具体命令和保留的门槛见[验证](validation.md)。

| 阶段 | 何时 | 入口 | 判定 |
|---|---|---|---|
| 开发自检 | 开发中、Codex 交付前 | 涉及的测试类、Debug 项目构建；文档用 `check-repository -NoBinaries` | 列出通过、失败、阻塞和沙箱限制，不作为可公开候选 |
| 合并门 | 每次合并前，在主机上 | `merge-check` | 普通构建、受影响的测试与生成/仓库检查通过，宽范围改动实测约 21–28 分钟 |
| 完整准备 | 发布前提前运行 | `prepare -Version <版本>` | 候选前运行 `merge-check -All`；`prepare` 构建、打包并检查 ZIP，包与测试字节一致，封存候选并上传私有草稿（实测约 6 分钟） |
| 公开 | 维护者确认后 | 已构建工具的 `publish-prepared -Version <版本>` | 300 秒内核对候选、推送、打标签和公开；不构建、不测试、不打包 |

没有 nightly，不等待 GitHub CI 合并。CI 失败仍按缺陷处理。当前命令均已实现，beta.3 已完成准备和公开；状态见[进度](../plan/progress.md)，上传和公开失败的恢复步骤见[发布流程](release.md)。

## 5 提交、发布与资源

- 按通用规范第 18.4 节（GIT-01、GIT-03、GIT-04、GIT-07）；不手改测试哈希、`manifest/*.json` 中的哈希和生成文件。
- 构建与发布自动化只在 `build-tools/release` 中用 C# 维护；仓库拒绝新增 PowerShell、batch 和 cmd 文件。
- 文档改动核对链接、路径、工具适用版本和引用，并运行 `dotnet run --project build-tools/release -- check-repository -NoBinaries`。
- 每次发布前由维护者确认，发布期间不改代码；4.0 预发布见 [ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md)，正式版条件见 [ADR 0023](../standards/decisions/0023-release-4-0-final-criteria.md)。
- Codex 并行数由维护者决定，有依赖的任务串行；只结束自己启动并核对过可执行文件路径的进程，不按名称或模糊匹配结束进程。

## 附录 工具规格模板

新工具或 MCP 契约变化在实现前把下面的模板填进任务说明，随任务说明交审，不另建工具规格文件；现有工具不补写规格，事实以生成目录为准。规则与错误码见[设计规范](../standards/design.md)第 4.6 节和第 6 节。

```markdown
# 工具规格：`<ToolName>`

状态：草稿 | 已审查 | 已实现 · 最后核对：YYYY-MM-DD，master `<提交>` · 任务：`<ID>` · 相关：ADR、设计说明、偏差编号

## 1 目的
模型借此完成什么；为什么现有工具的动作或可选参数不够（一两句）。

## 2 身份
| 项目 | 值 |
|---|---|
| 工具名 | `<ToolName>`（动词在 `build-tools/release/CatalogData.cs` 的闭集中） |
| 描述前缀 | `[<层>][<域>][<操作>]` |
| 分类 | UI、SESSION、READ、WRITE、FILE、OFFLINE、ONLINE、ONLINE-WRITE、EXECUTE 之一，登记在 `ToolMetadata` 或 `[ToolClassification]` |
| 注解 | 四个 hint 的值与理由 |
| 档位与批处理 | 是否进入所选版本的统一 lite 名单；`BatchRead`、`BatchWrite` 的值与理由 |
| 实现位置 | 声明、共享逻辑、按发布键的适配器 |

## 3 发布键与 API 证据
证据来自 PublicAPI 元数据或官方文档，不按名称推断；至少一个动作有证据的发布键才注册。
| 发布键 | 支持的动作或“不注册” | API 证据（类型、成员、文档）或“无” | 不支持时的错误码或“不适用” |
|---|---|---|---|
| `14sp1` | | | |
| `15.1` | | | |
| `16` | | | |
| `17` | | | |
| `18` | | | |
| `19` | | | |
| `20` | | | |
| `21` | | | |

## 4 输入
| 参数 | 类型与形式 | 必填 | 默认值 | 含义与来源（哪个工具返回该值） |
|---|---|---|---|---|
- 互斥参数与 `oneOf`；别名与弃用；与现有工具同一概念是否同名同形式：

## 5 输出
- `data` 字段；`meta` 的警告、分页、审批信息；是否寄存为导出句柄；`dryRun: true` 预览与执行结果的差别：

## 6 错误
| 情况 | `error.code` | `details` 关键字段 | `meta.outcome` |
|---|---|---|---|
| 参数无效 | `INVALID_ARGUMENT` | `parameter`、`parameters`、`allowedValues` | `rejected` |
| 本版不支持 | `UNSUPPORTED_CAPABILITY` | `releaseKey`、`capability`、`action` | `rejected` |
| 原生失败 | `NATIVE_OPERATION_FAILED` | `nativeCode`、`nativeMessage`、`evidence` | `failed` 或 `read-failed` |
| 写入中断 | `OUTCOME_UNKNOWN` | `stage`、`evidence` | `unknown` |

## 7 审批、审计与安全
- 需审批的动作；预览是否完全不写入；计划哈希或预览令牌如何绑定执行：
- 审计内容；带凭据的参数是否替换（安全标准 SEC-06）；读取路径与大小限制；能否回滚、失败后项目状态：

## 8 结果未知时的处理
可能结果未知的阶段、会话是否锁定、恢复步骤，以及为什么不自动重放。

## 9 示例
- `reference/tool-examples` 记录与 `GetToolUsage` 示例编号（每个动作一条，项目相关值放在绑定中）；`validation` 文字；真机核对的发布键、日期和台账条目：

## 10 测试与捕获
- 新增或修改的测试 csproj 与 `tests/test-suites.json` 套件；必须覆盖的失败路径；要运行的生成器；工具定义契约基线的预期差异：

## 11 兼容性与发布
- 增量或破坏性（是否升 `schemaVersion`）；发布说明与迁移表条目；弃用的名称或参数及保留期限：
```
