# 架构决策记录

状态：现行 · 最后核对：2026-10-11，master `3a227657`

[设计规范索引](../design-spec/README.md) · [文档写作规范](../design-spec/documentation-style.md) · [ADR 模板](0000-template.md) · [当前交接](../handoff.md)

本目录用架构决策记录（ADR）记录影响结构、契约、安全边界、发布规则或外部基线的决定，一页一个决定，只追加不改写（[ADR 0016](0016-architecture-decision-records.md)）。何时必须写 ADR、文件命名、状态值和模板见 [文档写作规范](../design-spec/documentation-style.md) 第 14 节；新 ADR 从 [模板](0000-template.md) 开始，写好后在下表登记。

## 已写入的 ADR

“待办编号”是 2026-10-11 整理决定清单时使用的临时编号（`ADR-B nn`），正式编号按决定日期排序。0001–0014 是回溯记录。

| 编号 | 标题 | 状态 | 日期 | 待办编号 |
|---|---|---|---|---|
| [0001](0001-hard-cut-v4-contract.md) | 4.0 硬切换到 V4 契约，不提供 3.x 兼容层 | 已采纳 | 2026-10-03 | B01 |
| [0002](0002-native-behavior-by-family-acceptance.md) | 原生行为按 D1 行为族真机验收后才切换为 safe-v4 | 已采纳 | 2026-10-03 | B05 |
| [0003](0003-workbench-approval-for-mcp-writes.md) | MCP 写调用默认须在工作台审批，工作台功能纳入 4.0 | 已采纳 | 2026-10-05 | B21 |
| [0004](0004-precheck-before-approval.md) | 写请求先预检再审批，批准后复核再执行 | 已采纳 | 2026-10-06 | B28 |
| [0005](0005-v20-v21-on-foundation-host.md) | V20/V21 与其他六个版本采用同一架构：FoundationHost 加每版本 worker | 已采纳 | 2026-10-08 | B07 |
| [0006](0006-engine-exe-as-v20-v21-worker.md) | 复用引擎 EXE 作为 V20/V21 的 worker，引擎宿主层退役 | 已采纳 | 2026-10-08 | B08 |
| [0007](0007-one-worker-per-mcp-session.md) | 每个 MCP 会话一个 worker | 已采纳 | 2026-10-08 | B09 |
| [0008](0008-worker-timeouts-and-faults.md) | worker 调用按类超时，故障使 worker 代次失效且从不重放 | 已采纳 | 2026-10-08 | B10 |
| [0009](0009-port-v20-v21-tools-to-foundation-adapters.md) | V20/V21 专有工具按能力族移入 Foundation 适配器，之后引擎退役 | 已采纳 | 2026-10-08 | B11 |
| [0010](0010-discovery-and-batch-tools-on-all-releases.md) | 发现、桥接与批处理工具在全部八个版本发布 | 已采纳 | 2026-10-08 | B16 |
| [0011](0011-tia-process-lease-on-all-releases.md) | 八个版本都用 TIA 进程租约把一个 TIA 进程保留给一个 MCP 会话 | 已采纳 | 2026-10-08 | B19 |
| [0012](0012-old-release-tool-registration.md) | 旧版只注册至少有一个动作能在该版 API 上运行的工具 | 已采纳 | 2026-10-09 | B14 |
| [0013](0013-release-4-0-after-phases-7-and-8.md) | 4.0 在阶段 7 与阶段 8 全部完成后发布，真机验收最后集中进行一次 | 已采纳 | 2026-10-09 | B33 |
| [0014](0014-tool-counts-follow-official-api.md) | 各版本的工具数量按官方 API 支持决定，不为一致而拉平 | 已采纳 | 2026-10-10 | B15 |
| [0015](0015-microsoft-design-spec-baseline.md) | 项目设计规范以 Microsoft 规范为基线 | 已采纳 | 2026-10-11 | B39 |
| [0016](0016-architecture-decision-records.md) | 设计决策用 ADR 记录，一页一个决定 | 已采纳 | 2026-10-11 | B40 |
| [0017](0017-own-openness-group-code.md) | Openness 用户组检查改用自有代码，不再分发 Siemens.Collaboration.Net | 已采纳 | 2026-10-18 | — |

## 待补写的决定

以下决定已经做出（B27 除外），但还没有 ADR。“现记录位置”列出今天能找到该决定的地方；只有协调记录的，补写时以维护者确认为准。被后续决定修改的仍建议回溯记录，以保留方向变化的原因。“4.0 前”为“是”的条目在整合 P7-11b 文档时补写（P8-63）。

| 待办编号 | 日期 | 决定 | 现记录位置 | 建议状态 | 4.0 前 |
|---|---|---|---|---|---|
| B02 | 2026-10-03 | 新增的 MCP 可见文本一律英文；MCP 响应不做本地化，本地化只在工作台和 CLI `doctor` 输出；4.0 起错误使用稳定错误码，文本不再承担功能 | `runtime-layout.md`“用户可见文案语言”（D-P5-3/5）；`tool-development.md` | 已采纳 | 是 |
| B03 | 2026-10-03 | 产品入口与命名：三个同名程序改为 FoundationHost 与 `TiaMcp.Engine.V20/V21`；包根启动器 `TiaOpenness.exe` 打开工作台并删除旧配置器入口；3.x 升级到 4.0 是全新安装 | `phase6-review.md` §6（决策 7、8）；`v4.0.0.md`“Upgrade from 3.x” | 已采纳 | 是 |
| B04 | 2026-10-03 | PLC 名称解析（G9）：非空名称在读写中都只接受精确或别名匹配，否则返回 NotFound 与候选路径；空名称只在工程内唯一 PLC 时选中 | `engine-decomposition.md`“G9”；`phase6-review.md` §6 决策 5 | 已采纳 | 否 |
| B06 | 2026-10-03 | 所有 worker 与适配器统一为 .NET Framework 4.8；删除未接线的预览 worker 协议，PLC worker 与 Studio 桥接统一使用 WorkerChannel 协议 2 | `adapter-merge.md`“待维护者决定”D4、D7 | 已采纳 | 否 |
| B12 | 2026-10-07 | 4.0 保留 Foundation 宿主与 V20/V21 引擎（当时称“完整引擎”）两个程序，两类宿主共用一份行为规则，合并推迟到 4.x | `refactor-plan.md` P6-65 | 已被 0005 取代 | 否（回溯） |
| B13 | 2026-10-08 | 已知 TIA 崩溃路径的原生防护在移植中保留：V20/V21 拒绝 CFC 图表保护与导出；SiVArc 块级工具只接受 FB/FC；PLC 原生交叉引用默认禁用；HMI 诊断控件属性暂缓读取 | `phase8-port-plan.md` §3.1 I9 与相关维护者决定；`troubleshooting/openness-limitations.md` | 已采纳 | 是 |
| B17 | 2026-10-08 | 新开与审批管道分开的工作台控制通道，提供显示、读取、预填三类共 8 个宿主工具；通道上不提供审批、审批开关、服务启停；“允许 AI 控制界面”默认开启，人正在操作时优先 | `phase8-workbench-control.md` §0、§4 与“维护者决定（2026-10-08）”；`runtime-layout.md`“工作台控制通道” | 已采纳 | 是 |
| B18 | 2026-10-08 | 工程操作页改为共享 MCP 会话的操作面板：默认附着 AI 正在使用的会话；没有 AI 会话时只在工作台启动的宿主中自建会话；面板发起的写仍须审批；“人/AI”由传输决定 | `phase8-workbench-control.md` §5 与“维护者决定”；`refactor-plan.md` P8-21 | 已采纳 | 是 |
| B20 | 2026-10-08 | 审计链只记录写调用与审批；工作台控制调用只进调用日志与工作台控制日志；迁移期 V16–V19 版本控制页经 Studio 桥接只读 VCI；`--mock` 演示只显示离线页面 | `phase8-workbench-control.md`“维护者决定”与 §5.6 | 已采纳 | 否 |
| B22 | 2026-10-08 | 生成框架用可替换的标准包（JSON 清单与分部文件、zip 分发、JSON Schema 2020-12 校验、声明式规则不执行包内代码）；规范 JSON 机器描述是唯一生成输入；编排器在宿主内；已有工程只补缺 | `phase8-generation-framework.md`“维护者决定（2026-10-08）”；`roadmap.md` | 已采纳 | 是 |
| B23 | 2026-10-08 | 首批标准包为中性基础包 `tiamcp.basic` 与自写的状态模型包；专有标准不入库，只能由用户自备 | `phase8-generation-framework.md`“维护者决定”§5 | 已采纳 | 否 |
| B24 | 2026-10-08 | 生成写入每次执行审批一次：审批绑定计划哈希与全部步骤，之后逐步执行、逐步审计，失败即停 | `phase8-generation-framework.md`“维护者决定”与 §6.6 | 已采纳（由 B26 细化） | 是 |
| B25 | 2026-10-07 | 宿主机上的 AI 经导入暂存把文本源发到 TIA 电脑；目录导入支持 `overwrite: true`，替换前导出备份，失败时按依赖顺序恢复 | `refactor-plan.md` P6-67；`configuration.md`“Import staging on the TIA machine”；`v4.0.0.md` | 已采纳 | 是 |
| B26 | 2026-10-10 | 完整工程生成采用分阶段执行 D3（工具预览比对、写栅栏、运行事实链、效果核对、发散即停、同次运行换绑），Q5–Q14 按推荐 | [分阶段生成执行设计](../design/p8-31-staged-generation.md) 第 4–11 节；实现 `b0f0eda9`、`6127d939` | 已采纳 | 是 |
| B27 | 未决 | 维护者自用的编程规范是否作为第一个企业标准包试点、是否入库 | `phase8-generation-framework.md`“维护者决定”末条 | 提议（待决定后再写） | 否 |
| B29 | 2026-10-03 | 包根与数据根：包根由 `--bundle-root`、`TIA_MCP_BUNDLE_ROOT` 或已知安装锚点决定，显式根无效立即报错；软件自身数据优先写 `<包根>/data`，不可写时按用途回退到用户目录 | `phase6-review.md` §5、§6 决策 9；`runtime-layout.md` 决策记录 D-G7-2、D-G7-6 | 已采纳 | 是 |
| B30 | 2026-10-06 | 构建、发布与检查自动化只用 C#（`build-tools/release`），仓库拒绝新增 PowerShell、batch、cmd 文件；2026-10-08 起自有 Python 也分批改为 C# | `CLAUDE.md`；`refactor-plan.md` P6-50–P6-53、P8-10；`phase8-python-plan.md` | 已采纳 | 否 |
| B31 | 2026-10-03 | 源码哈希只在发布时严格校验，日常 CI 只校验 manifest 结构与版本一致；manifest 与测试哈希只由构建生成 | `refactor-plan.md` 约束 2、P1-01；`CLAUDE.md` | 已采纳 | 否 |
| B32 | 2026-10-03 | 改变原生路径的共享适配器原语用构建开关 `TiaSharedAdapterPaths` 隔离在发布二进制之外，默认关闭，真机验收后才开启 | `adapter-merge.md`“迁移顺序”与 D2；`refactor-plan.md` 阶段 4、6 | 已采纳 | 否 |
| B34 | 2026-10-08 | 原定 4.0 之后的工作立即作为阶段 8 并行开展；4.0 照常在 V20/V21 真机验收后发布 | `refactor-plan.md` 阶段 8 开头；`roadmap.md`“阶段 8” | 已被 0013 取代 | 否（回溯） |
| B35 | 2026-10-10 | 合并门禁：合并前只跑快速闸门，原先合并前的全部检查在合并后对合并提交运行，失败在 master 上向前修复；完整发布候选只在改动发布链时运行，最后再生成一次虚拟机测试包 | `handoff.md`（2026-10-10 的门禁调整）；仓库外的任务说明；时间目标仅协调记录 | 已采纳 | 是 |
| B36 | 2026-10-10 | 不删除、跳过、过滤或放宽任何功能检查、测试、断言、阈值和门禁，除非维护者批准；行为按设计改变时，可用仓库内正式工具重新生成基线和 golden 文件，差异在合并时逐项审查（2026-10-11 补充） | `AGENTS.md`“范围”；提交 `59731d94`、`3a227657` | 已采纳 | 是 |
| B37 | 2026-10-11 | 实现代理在独立 worktree 的沙箱中工作，不提交；审查者复跑完整检查集后提交并合并；代理可以在 127.0.0.1 上启动测试夹具；研究与方案由 Claude 完成，代码任务交给 Codex | `AGENTS.md`；提交 `c1bdd1ec`；`refactor-plan.md`“任务流程” | 已采纳 | 否 |
| B38 | 2026-10-03 | 只维护 master：每个任务在临时分支或 worktree 完成，验收后变基并快进合并；英文提交信息，不加 AI 署名 | `refactor-plan.md` 约束 4 与“任务流程”；`CLAUDE.md` | 已采纳 | 否 |

## 不写 ADR 的事项

以下是操作规则或个人偏好，不影响系统结构或质量属性，保留在代理说明、任务说明或协调记录中：

- 虚拟机验收脚本的导出与临时工程写入 `<包根>/exports/<run>`，不放在虚拟机桌面。
- 实现代理使用的模型与速度档位。
- 单项任务的验收命令与临时脚本。
- 已被 B36 覆盖的单次检查修复批准。
