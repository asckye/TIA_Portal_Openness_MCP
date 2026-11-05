# 路线图与待办（v4.0.0-beta.4 已发布）

状态：现行 · 最后核对：2026-11-05，已发布 v4.0.0-beta.4（`48abffe8`）

本页是开发计划：按版本记录范围、任务状态、放行门槛和待维护者决定，取代已归档的[路线图](../archive/development/roadmap.md)和[重构计划](../archive/development/refactor-plan.md)（阶段 0–8 的 P0–P8 任务表、历史目标和风险留在那里）。

当前状态和进行中的分支见[开发进度](progress.md)，决定见 [ADR](../standards/decisions/README.md)。`bin-build/refactor/tasks/followups.md`（不入库）只是收件箱，条目在决定当天转写到本页或 ADR；任务说明只引用本页的编号。

4.0 的方向：八个版本使用 FoundationHost 加每会话一个 PLC worker，除工具集合和官方 API 差异外共用一套逻辑（[ADR 0022](../standards/decisions/0022-engine-retirement-and-eight-release-unification.md)、[ADR 0014](../standards/decisions/0014-tool-counts-follow-official-api.md)）；真机可靠性优先，每批修复在 VM 上复测；补齐对照 T-IA 和官方 API 的小缺口；流程变简单：合并看受影响的检查，注册套件和传输检查在 `merge-check`，交付构建和包检查在 `prepare`，确认后 `publish-prepared` 只核对、推送、打标签并公开草稿。离线通过不证明原生行为，未在真实 TIA Portal 上验证的项写进发布说明。

状态取值：已集成（本基线含实现与提交号）· 已合并（带提交号）· 待合并（分支已完成，等合并窗口）· 进行中 · 排队（任务说明已写，等前置任务）· 未开始 · 推迟 · 取消 · 提议（待维护者决定）· 并入（由另一任务完成）。新任务用主题编号（VMFIX、DES、SPD、Q 等），已有的 P8 编号不变（D-P12）。

## 放行门槛

| 关口 | 门槛 | 依据 |
|---|---|---|
| 合并 | 本机 `merge-check`，普通 `dotnet build`、受影响的 `dotnet test` 与生成/仓库检查。FAST-GATE 目标约 10 分钟，当前宽范围运行报告约 12 分钟，优化前测得 1268.6 秒（约 21 分钟，2026-11-04，失败但各作业运行到底的诊断轮次）。任务说明先经独立 AI 审查，交付后经审查与主机验证。GitHub CI 不阻塞合并 | [ADR 0031](../standards/decisions/0031-release-path.md)与 FAST-GATE 任务记录 |
| 预发布 `4.0.0-beta.N` | GitHub 预发布，不设为最新版，3.3 更新器不提示；发布说明附未验收清单；发布后测试者按[清单](../guides/beta-tester-checklist.md)报告，维护者做 VM 回归；每次公开前维护者确认，确认后保持候选提交不变 | [ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md) |
| 发布准备与公开 | 候选前手动运行 `merge-check -All`；`prepare` 构建、打包并完成 ZIP 检查后封存候选和上传草稿，实测约 6 分钟；`publish-prepared` 不构建或测试，300 秒内完成公开。没有 nightly，也不采纳旧收据。发布后只校验 ZIP 与 sidecar | [ADR 0031](../standards/decisions/0031-release-path.md) |
| 4.0 正式版 | 全部 4.0 任务合并、完整准备通过、rc 经验证、没有未决 blocker、维护者确认；正式版公开规则须先实现。起初不向 3.3 更新器提供，不设固定公测期或连续通过次数 | [ADR 0023](../standards/decisions/0023-release-4-0-final-criteria.md)、[0031](../standards/decisions/0031-release-path.md) |

检查、测试、断言、超时和阈值只有维护者批准后才能删除、跳过或放宽，已经批准的移除范围见[验证](../manuals/validation.md#已批准移除的检查机制)。REVIEW-PORT 已接入安全分析器、检查放宽检测和完整 `prepare` 的新鲜漏洞扫描。只有 High、Medium 审查发现触发下一轮返工，Low 留待合并后处理，用户可见项写入发布说明。任务超过三轮审查即暂停，先复核设计并告知维护者（2026-11-04、2026-11-05 决定）。

## 4.0.0-beta.3（已发布，2026-11-04）

beta.3 已于 2026-11-04 在 `1d57c277` 发布，含 PIPE-INT/SLIM-1、VMFIX-2、新发布路径、SEC-1、Q1–Q9、CPM、NEW-REL-2/3、THIRD-1 和整理工作，见[发布说明](../releases/v4.0.0-beta.3.md)。依赖仍在开发的产品文件的拆分、`src/EngineHost` 改名与长行合并门已移至 beta.5，等 DES-4 Parts 2、3 合并。

### 发布前顺序

| 顺序 | 任务 | 内容与状态 |
|---|---|---|
| 1 | NEW-REL-B、SEC-1 | 已合并（`0ae4b1e8`、`1af64ce7`；`6ae345fd`、`b711e0bb`、`979b1263`）：NEW-REL-B 接入所有套件、保留检查、候选和自动草稿上传；SEC-1 修补审批设置保护、文件写入与 Git 调用的小范围安全问题（调查 S1/S2）。输出根以外写文件的完整审批另排 beta.4 |
| 2 | Q1–Q9、CPM | 已合并（Q1 `5f956ea8`、Q5 `a26c60bd`、Q2 `84c4e966`、Q3 `bd30bc9d`、Q4 `c001c1c4`、Q7 `6d771add`、CPM `975a0ff5`、Q8 `02548d27`、Q9 `74073ed0`，Q-INT-1 `3cac74e0`）：清理引擎残留、重复检查、SafeV4 候选，集中包版本、压缩契约公共 schema、派生发布键 |
| 3 | NEW-REL-2、NEW-REL-3、THIRD-1、主机测试精简 | 已合并（NEW-REL-2 `90be875b`、`9485a33b`；NEW-REL-3a–e `bce6994c`、`6a489b47`、`3e343bd6`、`3b66a7be`、`090ebab1`；THIRD-1 `63281105`）：新合并门；删除旧发布工具和全部八版响应捕获/基线；第三方裁剪；版本重复的准入用例精简 1,810 例；引擎删除后注册表保留 38 个套件。保留功能、协议、审批、写安全、故障恢复测试及各数量门槛 |
| 4 | DOCS-1 → DOCS-2 → DOCS-3 | 在 NEW-REL-3 之后依次合并：DOCS-1 分目录并归并规范，DOCS-2 刷新发布路径与调查，DOCS-3 核对重基后的事实和指令；已合并（`eec44ca0`、`d0d40b6e`、`230c6d1d`、`1c1deb63`，后续路径修订至 `edbf4ef8`）；[ADR 0030](../standards/decisions/0030-documentation-and-repository-structure.md) 与 [0031](../standards/decisions/0031-release-path.md) |
| 5 | REORG-2 | 整理数据与脚本：`schemas/` 并入 `reference/`、运行布局并入设计规范、一个 `.slnx`；`hooks/` 保留根目录，根目录 `runtime/` 与项目 `bin`/`obj` 保留；清理旧记录和 LibraryRenameProbe，核对交付清单与许可文件引用；已合并（`9406ee46`） |
| 6 | REORG-3 | 整理源码、测试和交付包路径，更新所有引用、注册与交付规则；已合并（`f10e45b3`、`edbf4ef8`） |
| 7 | TIDY-1–TIDY-6 | 已合并（TIDY-1 `0f2ffd29`，TIDY-2–6 至 `b0b4d9a7`），完成长行整理和无行为变更拆分。受产品分支影响的文件留给 beta.5 的 TIDY-7，文档中文段落不在句中换行 |
| 8 | 实际准备 | 已完成 beta.3 准备（公开提交 `1d57c277`）；后续 PREP-SLIM 将注册套件移至合并门 |
| 9 | 确认与公开 | 已公开 beta.3（`1d57c277`）；修复后的 VM 回归与测试者验证仍待执行 |

THIRD-1 保留交付源码、每个项目的 README、LICENSE、UPSTREAM.json、NOTICE 中的来源，以及 `third_party/simaticml-decoder/docs/SIMATICML_READING_GUIDE.md`。裁剪上游测试、示例、其他文档与 Studio 上游归档，并同步修复保留文件的链接和生态检查；不删除本项目自己的桥接测试。

### 已集成的 beta.3 工作

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| beta3-rename | 对外名称改为自动化工程工作台，维护者版权行，西门子商标声明 | 已合并（`77bdf060`、`1f37ab3d`、`1cad1df2`） | 2026-10-30 |
| beta3-docs | 3.3 到 4.0 迁移指南、预发布真机验证清单、测试者反馈台账、Issue 表单 | 已合并（`f6bf1dea`） | 2026-10-30 |
| SPD-1、SPD-2、SPD-4 | 旧检查链的增量与派生子集，已由新发布路径取代；此行仅记录历史集成 | 已合并（`a6ced5da`、`b7db27a0`、`bf04b534`、`c68c31d3`） | 2026-10-30 |
| PREFLIGHT | 发布前检查伴随 Python 与跟踪文件的规范化字节；主机注册文件容忍并发读取 | 已合并（`7830383b`、`e57b64c5`） | 2026-10-30 |
| XREF | 原生交叉引用 V14 SP1–V20 默认允许、V21 工作台会话开关、`AnalyzePlcCrossReferences` | 已合并（`4b17a042`、`f51c9f20`） | 2026-10-30 |
| HMI-B3 | HMI 复核 B6–B8、D6：`PropertyName` 选择接口项、旧 Ensure/Apply/Bind 默认预览、路径预校验 | 已合并（`e5c3d365`） | 2026-10-27 |
| EX-V20V21 | V20/V21 改用维护的调用示例，重新生成工具档案与用法数据 | 已合并（`0494dcac`、`219132f1`、`902a86fe`） | 2026-11-01 |
| S0 | 文档状态刷新、ADR 0020 修订与 0022–0025、标准文档与 ADR 0026–0028 | 已合并（`75abe21f`、`1b9404e6`、`a1740189`） | 2026-11-01 |
| DES-1、VMFIX-1、FIX-R1 | 一份会话状态，MCP 宿主在工作台可见、可重启（[ADR 0029](../standards/decisions/0029-authoritative-session-state.md)）；beta.2 真机发现修复（超时与会话污染、`OUTCOME_UNKNOWN`、L2 工具、`expectedProject`、去掉设备白名单）；两者的审查修复，生命周期测试去掉真实等待 | 已合并（`5df51c71`、`998ebe53`、`438939b8`、`b80d410b`、`68abfab1`、`5c6d5549`、`ce81e748`、`ab5a89af`） | 2026-11-01 |
| SU-1 | 软件单元（V16–V21）：导入导出范围、导入选项、单元内容读取、`SetPlcUnitObjectAccess` | 已合并（`ccebed95`、`cd347719`） | 2026-10-31 |
| DES-5 | 大结果分页；`DecodePlcSimaticMl` 随包带隔离的 CPython 运行时 | 已合并（`cfc5b649`、`4a54952b`、`c99af171`） | 2026-11-01 |
| UI-2a | UI-v2 第 1 批：工作台资源改为 EplanMcp 设计系统的颜色、字体、高对比描边与焦点环（[ADR 0028](../standards/decisions/0028-workbench-visual-design-eplanmcp.md)） | 已合并（`b7e17a5a`、`00a8ce91`、`be1d3b4a`、`d9a13dab`） | 2026-11-01 |
| FIXTURE-CRASH | 测试夹具入口捕获未处理异常并以独立退出码结束，不再弹出 WER；check-all 长路径修复 | 已合并（`997f2e29`） | 2026-11-01 |
| DEP-1 | Microsoft.Extensions、System.Text.Json 等从 10.0.0-preview.4 改为 10.0.12；MCP C# SDK 在 4.0 保持 0.3.0-preview.4（D-P10） | 已合并（`d5d94583`） | 2026-11-01 |
| HANDBOOK | 采用通用《工程开发手册》v1.1，记录项目档位与偏差（[标准说明](../standards/README.md)） | 已合并（`f176e938`） | 2026-11-02 |
| BATCH2 | 批次 2 的正式重新生成与门禁修复，CHANGELOG 与发布说明定稿 | 已合并（`030f1660`、`dbe12b88`、`757ce94d`、`c6c1d99c`、`34e85560`、`1dfe2067`） | 2026-11-02 |
| PIPE-INT、SLIM-1 | 旧检查链精简；已批准的延迟门槛、HTTP 稳定性、静默阶段、套件输出对照及重复变体清理；后续由新工具接替 | 已合并（`eee7b372`） | 2026-11-03 |
| VMFIX-2 | 扩展族回复校验、Unified 读取范围和脚本、HMI 探测与状态原因；第二轮修复安全管理回复回归 | 已合并（`93371695`、`b769d196`） | 2026-11-03 |
| NEW-REL-A | 全新 `prepare` 的构建、暂存校验和打包 | 已合并（`545e3604`、`55f9c737`） | 2026-11-04 |
| NEW-REL-C | 预上传草稿、300 秒公开预算、重跑恢复；发布后 ZIP 与 sidecar 校验 | 已合并（`2db5bf1e`） | 2026-11-04 |

## 4.0.0-beta.4

beta.3 之后已合并：CI-FIX（`67ca6816`）、PREP-SLIM（`ccc8f7aa`、`d1a7b042`）、CI-FIX2（`3a01f49c`）、TRANSPORT-FAST（`e702434f`、`043dd8bf`、`163accea`）。

本节按当前 worktree 基线记录已集成代码。beta.4 已于 2026-11-05 发布为预发布（`48abffe8`，[发布说明](../releases/v4.0.0-beta.4.md)），尚无本版真机复验。DES-4 仅 Part 1 随本版交付，Parts 2、3 已移至 beta.5。

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| HANG-GUARD | 已批准的 151 处等待改为 120 秒防挂起保护，速度转为趋势报告，保留功能断言和数量门槛 | 已集成（`bc860ac7`、`242200dd`） | 2026-11-04 |
| FAST-GATE | 构建复用、共享分片历史、GUI 与传输并行、GUI 四分片和输入独占通道 | 已集成（`1c4031b6`、`47437168`、`6e4cc3a0`），仍高于约 10 分钟目标 | 2026-11-04 |
| TREND-ALERT | 对最近中位数两倍以上的测试和成功作业耗时提示，发布准备摘要带出最近记录 | 已集成（`3c7a1a4b`），不改变退出码 | 2026-11-04 |
| Q6a、Q6b、P8-04n2 | 已移植工具的调用管线和发布策略统一 | 并入 UNIFY-1，尚未完成 | 2026-11-04 |
| Q10a、Q10b | 声明式工具清单的读取与生成，Q6c 后续另行评估 | 排队（UNIFY-1 之后，Q10b 任务说明待写） | 2026-11-02 |
| DES-PORT A/B/C（取代 DES-INT，承接 DES-2、DES-3） | 统一兼容转发、批次、安全移块、27 个工具默认预览与失败回滚，生成步骤使用自己的预览哈希 | 已集成（A `e32b0d76`，B `61bb78a0`，C `1012d0b7`） | 2026-11-04 |
| DES-PORT-FIX | 补诊断计划真实 apply、提示词与读示例，计划哈希绑定输入内容与宿主生命周期，修正独占访问拒绝 | 已集成（`fd757a65`），仅离线验证 | 2026-11-04 |
| DES-4 Part 1 | 统一参数与一代兼容别名，缺省会话导出目录，根 schema 无组合器，[ADR 0033](../standards/decisions/0033-canonical-tool-parameters.md) | 已集成（`4aef120b`、`250a68c1`、`185d80ce`、`13eb7725`） | 2026-11-04 |
| VMFIX-3 | 写入结果如实（R2/R4）：确定的结果不报 `OUTCOME_UNKNOWN`、不要求会话重置；独占访问；`ImportDeviceAml` 只在 V21 移出独占访问；已批准 `HmiDryRunTests`、`PortedPortalAdmissionTests` 及第 5 轮 `HonestWriteSources` 两项替换断言；保留的结果映射测试移入 KeptCode/FoundationHost，已删除引擎路径不再移植 | 已合并（VMFIX-3-PORT，`8fe022bd`、`871d54da`） | 2026-11-02 |
| VMFIX-4 | 会话生命周期与恢复（R3/R5），绑定纪元、TIA 重启判断、读超时排空、重置关口、日志过滤 | 已集成（`76c51b5a`、`8757f27a`），待真机复验 | 2026-11-04 |
| VMFIX-5a、VMFIX-5b | 结果映射、去敏和原生原因，PLC 服务解析、V21 工艺对象崩溃保护、Unified 标签和画面修复 | 已集成（`f902c069`、`a31afbe0`、`93eeb016`），待真机复验 | 2026-11-04 |
| VMFIX-6a | 返回数据、示例、更新查询、SCL 编码、只读计划与审批设置迁移等余项 | 排队，D1–D5 已批准，6b 跟随 DES-4 Part 2 | 2026-11-04 |
| RELAY-1 | 代理安全的远程连接：随包本地 stdio 中继，私网地址绕过 HTTP(S)_PROXY，其他地址遵守 NO_PROXY，令牌只在工作台数据目录；环境检查“代理与远程连接”及一键修复 | 已合并（`d635fbb6`、`0f58eabc`、`ccf9b263`） | 2026-11-01 |
| UI-v2：UI-2b、UI-2d、UI-2c | UI-2b 外壳（232/64 侧栏、工具栏、合并的安全横幅、设置与审批成为页面）与 UI-2d 各页面重绘合在 UI-INT；UI-2c 审批改为列表加详情页，宿主计算风险等级，写入决定按钮等权重，破坏性操作默认拒绝 | 已合并（UI-INT：`400ad350`、`21a8a59a`；UI-2c：`32a8aa1c`；集成修订 `ccf9b263`） | 2026-11-01 |
| APR-05 | 关闭写操作审批时输入并核对本地化确认词，普通审批沿用原流程 | 已集成（`0b08a09b`） | 2026-11-04 |
| P8-04n1（N1-PORT） | 八版统一对外呈现：精简清单默认启用、服务器说明、档案名、目录呈现（P8-04n 的后半 P8-04n2 已并入 UNIFY-1） | 已合并（`fb3bb356`、`89ef43c4`、`1a4add6d`） | 2026-11-01 |
| P8-84 | 工作台 TIA 进程选择器：多个 TIA 进程时由用户选择，按 PID 精确附着 | 已合并（`699e989b`、`ccf9b263`） | 2026-10-21 |
| P8-21f | 删除工作台重复的 TIA 适配代码：Studio 改用共享适配器（P4-03 第 G3 步，删除 `StudioOpenness.V*`），VCI 平铺工作区与 MCP `UngroupedDevices` 对齐 | 未开始 | 2026-10-08 |
| FB-VM | beta.2、beta.3 测试包发现登记与 VMFIX 修复复测，含 P8-20 端到端验收 | 尚未复测，beta.4 已发布，等维护者开 VM 后由独立 AI 会话按书面说明执行 | 2026-11-04 |
| REVIEW-PORT（取代 REVIEW-1） | 未批准检查放宽检测、安全分析器与棘轮、完整 prepare 漏洞扫描，复杂度仅报告 | 已集成（`16567017`、`5c433428`、`f7465f45`），批准记录独立保存 | 2026-11-04 |
| SCORE-PORT（取代 SCORE-1） | 从源码、契约与实际结果生成各维度质量评分 | 已集成（`90b243e1`），首份评分的来源、分档和口径修订待 beta.4 后处理，分档尚未获维护者批准 | 2026-11-05 |
| VMAUTO-1 | 虚拟机自动回归程序 | 已取消（维护者 2026-11-04）：实机回归改由发布后的单独 AI 会话按测试说明执行，只在工程副本上写，原始结果存到 `bin-build/vm-test/<run>/` | 2026-11-04 |
| FILE-APPROVAL（OUTROOT-1） | 文件写入目标在安装级输出根以外时须审批，卡片显示真实完整目标 | 已集成（`9707cd07`、`76ee906f`） | 2026-11-04 |
| CI-MSG | 改善 GitHub CI 失败信息与诊断 | 已合并（`a368066a`） | 2026-11-04 |
| TRANSPORT-FAST、TRANSPORT-KEEP | 传输检查 `full` 档，独立调用默认最多并行 4 个版本，合并门最多 8 个并支持 10055 回退。测试客户端复用连接，不再耗尽主机端口 | 已合并（`efc58105`） | 2026-11-04 |
| GUI-SPACE | 工作台测试窗口放到屏幕外，真实鼠标点不到；按空格的测试等鼠标松开 | 已合并（`7a8ba945`） | 2026-11-04 |
| CI-FIX4 | 会话导出句柄隔离、worker 重启清理，版本策略使用请求的发布键，GUI 输入测试识别与调度 | 已集成（`1d8eb834`、`327c3d82`、`6e4cc3a0`） | 2026-11-05 |
| COV-V21 | 更正 [Openness 覆盖](../reference/openness-coverage.md)中的 V21 数字（直接引用 1,748，待复核 2,575）和复现命令（`prepare -BuildOnly` 后运行 `audit-version-tools`） | 未开始 | 2026-11-02 |
| STD-REF | 改为引用已发布的《工程开发规范》2.0（tag `v2.0`），删除本仓库的 HTML 副本；`mcp/` 草案待采纳（[ADR 0032](../standards/decisions/0032-published-engineering-standards.md)） | 已合并 | 2026-11-05 |

### 2026-11-04、2026-11-05 实施决定

以下为维护者批准的决定，任务表区分已集成与待做。尚未进行 beta.4 真机验收。

- OUTROOT-1 D1–D4。每个安装一个输出根，正常安装为 `<bundle>/exports`，checkout 为 `bin-build/exports`。UNC 写入需显示真实目标的审批卡，审批前查找另由 S8 处理。`BuildDeviceAmlDocument` 本批按目标审批，分类另行重捕获。预览、产品自行选择的目标、Workbench 操作者和已批准计划步骤沿用原规则，输入派生的实际输出仍检查。DES-4 在根内另建会话目录。
- REVIEW-PORT D1–D3。新安全规则的已有警告只允许一次正式登记基线。harness 固定 `Microsoft.Bcl.Memory 9.0.14`。完整 `prepare` 使用新鲜漏洞扫描，取代七天内 CI 证据。`check-loosening` 保持严格，任务说明提前列出拟改断言，由维护者批准、主导代理独立登记。
- DES-PORT D1–D4。OB 移动继续拒绝，八版 SDK 无直接移动 API，往返尚未证明能保留 OB 编号与事件分配。V14 SP1–V17 保留 `CreateDevice` 的原生入口与通用预览。生成步骤统一先执行自身 `write-preview/1` 预览再填入哈希。仅批准说明中逐项列出的断言变化。工艺对象目录导出八版统一走原生路线，只有官方 API 确实不同才按发布键分流。
- VMFIX-3-PORT A1、A2。批准 HmiBetaFixTests、UnifiedPortAdmissionTests 改为检查类型化拒绝与内部参数异常，保留零派发断言。WorkerFamilyCallbackTests 用真实 AML 策略验证绝对路径与相对路径。失败导入后对象不存在，只有原生明确 Failure、无导入身份且无 Success 才证明未改变，否则仍未知并需重置。这一已集成细化比原批准文字更保守，待真机复验。
- VMFIX-4 N1–N3。Workbench 块列表通过 `ListPlcBlocks` 分页。重置期间四个需要 worker 的宿主工具继续拒绝。批准列出的断言与 `shared=false, hang` 行调整。VMFIX-5 D3 保留 V21 工艺对象创建崩溃保护。PortalProcessPickerTests 的三位小数舍入与 HostLifecycleTests 的 Windows 计时粒度 20 ms 容差均已批准。
- DES-4 D1–D6。D1 别名保留至 5.0 并发出弃用警告。D2 导出参数采用 `exportFile`、`exportDirectory`，旧名保留原义。D3 七个原字符串 `devicePath` 入口增加数组形式，字符串作为别名。D4 保留项目名 `expectedProject` 与项目文件 `expectedProjectFile` 两种身份。D5 缺省导出落到输出根内的会话目录。D6 仅改名参数以省略表示未提供，不全库迁移空串。Part 1 已集成，根 schema 组合器改为描述和运行时校验，含 PrefillWorkbenchForm。Part 2 的 `InvalidArgumentDetails.parameter` 使用 JSON pointer 并列允许值，批准原参数名断言迁移且保留隐私断言。Parts 2、3 于 2026-11-05 移到 beta.5。
- VMFIX-6 D1–D5。D1 纯 ASCII 的 SCL 输出不写 BOM，非 ASCII 保留 BOM 并明确导入器限制，现有导入检查保留。D2 词过滤从变量名移到 provider `options`，接受 TIA 引号形式的符号路径。D3 停止从旧配置迁入关闭状态的 `approval.settings`，新安装从开启审批开始，并在 VM 复查审计。D4 参数名与允许值由 DES-4 Part 2 处理，6b 随后补余项。D5 `CreateDevice` 的确定拒绝保留当前 not-started 分类。6a 基于 DES-PORT B/C，6b 等 Part 2。
- S3、S5。更新签名未完成，维护者接受同一发布摘要无法防御 GitHub 账户被控制的风险。LAN 继续明文 HTTP，维护者接受同网者可读访问密钥与 PLC 密码的风险，仅在可信网络使用。两项均移出 4.0 前实施清单，见[安全标准](../standards/security.md)。
- S8 D1、D2。采用方案 A，AI 调用拒绝网络路径，Workbench 人工操作不受此规则影响。指向 localhost 和 127.0.0.1 的网络路径同样拒绝。尚未实现，排 4.0 前。
- T3 D1、D2。保留 `rollbackCompleted` 字段并写清语义，迁移 bridge 路由的审批与回滚测试。U4、C2 优先，其后 T2、T3，S9、U3、S8 独立进行，均在 beta.4 之后。
- UNIFY-1 替代 Q6a、Q6b、P8-04n2 并吸收 C9，是 4.0 条件。U1 解除没有官方 API 依据的工艺对象导出版本限制。U2 八版保留一致的无 TIA、能力优先及安装判断。U4 保留 `.s7res` 配对并在 VM 复测，移除无官方依据的 V20 库导入 None-only 限制，保留有官方依据的 Update 4 提示。U5 向旧版开放已有 API 的 seed HMI 导入。U8 从 V15.1 用指纹作生成见证，V14 SP1 保留 modified-date，历史记录按保存的种类验证。U3 线程模型留待旧版实机证据与 MTA-1。U6 保留实机事件触发的保护。U7 V21 builder 输出保持至 4.1 的 P8-40。统一 foundation 审计标签、披露和调用流程，共用会话、状态、观测，关闭 engine emulation，实际 API 差异进入能力表。
- VM 回归。2026-11-04 取消 VMAUTO-1，取代早前自动回归决定。beta.4 公开后另开 AI 会话，书面说明 VM、工程副本、覆盖和通过规则，写入只作用于副本。原始结果按固定格式保存在 `bin-build/vm-test/<run>/` 供 scorecard 读取。已归档代码留在 `refs/archive/branches/refactor/VMAUTO-1`，Campaign.Alive 缺陷未修复。首轮评分未识别无 run.json 的旧结果，回归须产出可读取的记录。
- FAST-GATE D1、D2、D4 批准，D3 仅在 Part 2 后仍超过 10 分钟时考虑，D5–D8 暂不实施。HANG-GUARD 的等待、已列速度断言和预算调整获批，WriteGuard 的 15、20 秒保持，先于 FAST-GATE Part 2 合并。GUI 输入通道独占，其余三分片随后并行。传输保持 full，每组最多八版，遇 WinError 10055 写主 checkout 持久标记后降至四版。测试管道隔离来自每个分片独立设置路径派生的名称。
- TREND-ALERT 只提示测试与成功作业超过近期中位数两倍的耗时，至少五条历史，比较最近至多二十条。历史缺失、坏行和读写失败不改变退出码。每次发布前在 prepare 摘要中报告最近提示及其提交、时间与当前 HEAD 的关系。
- 整理与审查。TIDY-7、HOST-RENAME、LONG-LINE-GATE 移到 beta.5 开头，在 DES-4 Parts 2、3 合并之后执行，避免仍开的分支无法跟随文件拆分或改名。只有 High、Medium 触发返工，Low 合并后排期。超过三轮审查先停下复核设计并告知维护者。

## 4.0.0-beta.5

DES-4 Parts 2、3 不阻塞 beta.4，继续在各自分支实现。TIDY-7、HOST-RENAME 和 LONG-LINE-GATE 必须等两部分合并，拆分与改名后的文件无法靠重命名检测跟随仍开的分支。V21 API 的 6 个 P1 缺口按 API 先行注册，带预览与读回，API-P1a 的第 0 部分先合并。T-IA 批次和生态小项先核对官方 API 再写说明。许可沿用 2026-10-30 决定。

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| DES-4 Part 2 | 字段级参数错误、JSON pointer、允许值与 B21/B27/B28 等余项 | 推迟至 beta.5，分支 `refactor/DES-4-P2` | 2026-11-05 |
| DES-4 Part 3 | 原生失败的结构化 `innererror`、去敏和关联诊断信息 | 推迟至 beta.5，分支 `refactor/DES-4-P3` | 2026-11-05 |
| VMFIX-6b | 参数与结果文字的剩余修正，D4 的参数细节跟随 DES-4 Part 2 | 排队（Part 2 之后） | 2026-11-05 |
| TIDY-7 | 剩余大文件拆分与新增长行整理，保持行为和 IL | 排队（DES-4 Parts 2、3 合并后） | 2026-11-05 |
| HOST-RENAME | `src/EngineHost` 改名，同步链接源、工程与文档路径 | 排队（DES-4 Parts 2、3 合并后） | 2026-11-05 |
| LONG-LINE-GATE | 将 `wrap-long-lines -Check` 接入合并门 | 排队（上述拆分与改名后） | 2026-11-05 |
| API-P1a | 库对象实例化到项目（`CreateFrom`）：块、变量与变量表、设备、Classic 画面，来源为库类型版本和母版，八个版本按 API 注册；第 0 部分为共用的计划绑定辅助方法 | 排队（任务说明已批准） | 2026-11-02 |
| API-P1b | 新建监控表，导入（并核实导出）强制表，强制表进入 AI 工具面并受 WRITE/ONLINE-WRITE 审批；按块、块组、软件单元编译，先在 VM 上确认 `ICompilable` 暴露，未暴露则只做前半（吸收 P8-97b） | 排队（API-P1a 第 0 部分之后） | 2026-11-02 |
| API-P1c | `DeviceItem.ChangeType` 更换 CPU 或模块型号，项目级子网创建与删除 | 排队（API-P1a 第 0 部分之后） | 2026-11-02 |
| API-P1d | Unified：删除连接与变量表、V21 图形列表、报警一次调用带全部文本与参数并支持批量 | 排队（API-P1a 第 0 部分之后） | 2026-11-02 |
| TIA-TEST | 持久 PLC 测试用例库（PLCSIM Advanced、批量运行、结果历史、官方 TIA Test Suite 用例）、FAT/SAT 报告、支持复现报告；JUnit 报告与稳定退出码 | 未开始 | 2026-10-30 |
| TIA-SAFE | 机密保管库；下载安全（实物与 PLCSIM 确认、先保存并编译、兼容指南）；高风险确认；审批状态读取工具 | 未开始 | 2026-10-30 |
| TIA-BATCH | 批量与递归的块、变量、UDT、HMI 变量操作；变量 CSV/Excel 交换，扩到 HMI 变量与报警、文本列表、DB 起始值 | 未开始 | 2026-10-30 |
| TIA-GAPS | API-P1 未覆盖的其余缺口：建脚本模块、嵌套运行设置（维护者 2026-11-03：排 beta.5）、工程概览与新建/打开选项、专题使用指南 | 未开始 | 2026-10-30 |
| ECO-S | Siemens 官方 tia-portal-ai-extensions 技能进入 `GetToolUsage`（MIT，署名）；参数预设与强制预设；导出时剥离易变节点（可选编译成功后自动提交） | 未开始 | 2026-10-30 |
| SELF | `ManagePlcDataBlockSnapshot` 的注册范围与官方 API 核对（ValueService 只在 V21 文档化）；`ManageProjectCompilationSettings` 从 V16 起注册并修正描述 | 未开始 | 2026-11-01 |
| P8-97b | 单块、块组编译 | 并入 API-P1b | 2026-10-27 |
| MTA-1 | 八个版本的 worker 都在一个 MTA 负责线程上调用 Openness（D-P10 a，方案 A1），隐藏的 STA 回退开关保留到 4.1；合并前 VM 冒烟 | 排队（任务说明已写） | 2026-11-01 |

## 4.0 正式版（rc → 4.0.0）

门槛见上文。除 beta.3–beta.5 的全部任务外，还需完成：

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| ADR-0023 | ADR 0023 补写 D-P1 一行（没有未决的 blocker 报告） | 并入 DOCS-1 | 2026-11-01 |
| UNIFY-1 | 统一调用流程、状态和披露，真实 API 差异进能力表，替代 Q6a/Q6b/P8-04n2 并吸收 C9，按上方 U1–U8 决定实施 | 排队（说明已写），完成为 4.0 条件，尚无真机确认 | 2026-11-04 |
| RENAME-GH | GitHub 仓库改名为 automation-engineering-workbench，同步 README、NOTICE、插件和发布工具中的仓库地址（执行前再次确认） | 未开始 | 2026-10-30 |
| RC | 用完整 `prepare` 生成 rc；定稿 [4.0 发布说明](../releases/v4.0.0.md)；4.0 起初不向 3.3 更新器提供 | 未开始 | 2026-10-30 |

## 4.1

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| P8-31f–i | 库与母版实例化、`CheckProjectStandard`（2026-11-02 更正后仍在 4.1）、工作台生成模块、状态模型包；P8-31h1 审批抽屉扩展；P8-31e6 延后（族通过 L5 后基于现行代码重建规则，候选实现已归档删除）。设计见已归档的[项目生成框架](../archive/development/design/project-generation-framework.md)和[分阶段生成](../archive/development/design/p8-31-staged-generation.md) | 推迟 | 2026-10-22 |
| P8-40 | 表格与 ECAD 导入、Classic VB 到 Unified JS、V20/V21 补深度、按版本输出的构建工具 | 推迟 | 2026-10-22 |
| API-P2、API-P3 | V21 API 差距评审的 P2（SIMATIC SD 更多语言、Multiuser、UMAC 与证书设置、PLC 与显示屏密码、参考工程、VCI 管理、GSD 参数、运动解释程序、项目文本筛选、带依赖生成源）和 P3（Classic 删除、空 FB、块元数据、少量硬件服务等） | 推迟 | 2026-11-02 |
| P8-95a–g | EplanMCP 借鉴项：VM HTTP 加固、“工程内容是数据”的服务器说明与审批卡清理、防误点、退出确认与单实例、Claude Desktop（MSIX）配置路径、CI 加固、doctor/audit 的 `schemaVersion` | 推迟 | 2026-11-01 |
| T-IA 大项 | 块接口编辑与 SCL 体替换（带回滚）；其余硬件缺口（插槽兼容、命名 CPU/网络/PN 属性、AML/GSD 预检；改型号和子网已提前到 API-P1c）；异步作业与取消；离线快照与撤销；工程报告与交付物；PLCSIM Advanced 扩展；Classic HMI 动画与事件；GRAPH/FBD/STL 生成；流水线与工作计划；帮助与文档搜索；多工程会话；模式库内容；VCI 工作区分组；一次调用导出整个工程到 Git | 推迟 | 2026-10-30 |
| 生态大项 | 整程序导出、磁盘编辑、只导入变化（先预览）；覆盖前自动快照与回滚；PLC 代码规则检查（规则目录、SARIF、评分）；SCL 单元测试库与运行器；CI 入口与 GitHub/GitLab 模板；Siemens 文档搜索；Web API 补全；生成后自检循环；AML 离线核对与 IO 变量生成；DB 注释生成 HMI 报警、导出全部 Unified 画面脚本；GRAPH 文本 DSL 与 STL 控制流 | 推迟 | 2026-10-30 |
| MCP-SDK | MCP C# SDK 从 0.3.0-preview.4 迁到 2.x；MCP 协议在 .NET SDK 支持后迁到无状态修订 | 推迟 | 2026-11-01 |
| MTA-1 收尾 | 删除隐藏的 STA 回退开关 | 推迟 | 2026-11-01 |
| 其他 | 托盘与开机启动；非 Studio 命名空间统一 | 推迟 | 2026-10-22 |

## 调查提议（2026-11-04 已排期）

下列来自 2026-11-03 只读调查，维护者已于 2026-11-04 排定版本；调查中的推测仍需复现，每条写任务说明前先确认问题还在。编号是调查编号，不等于已批准任务。涉及接口、安全或测试范围时先决定方案，再写任务说明；不因登记而授权修改检查。

| 编号 | 发现与提议 | 状态 |
|---|---|---|
| P2 | 原调查列出 31 个无任务的 VM 缺陷：B22、B23、B25、B27、B29–B33、B37、B38、B40、B41、B42–B59；需逐项认领。B27/B30 已要求补入 DES-4，余项仍待分配；B21/B28 同时补入 DES-4 | B23、B31、B32、B33、B38、B42、B43、B44、B57 排 4.0 前（2026-11-04 批准）；其余beta.4 VM 回归后决定 |
| T3 | 全目录审批拒绝零写入、预览无原生写入、`dryRun.effective`、B33 与回滚读回，保留并说明 `rollbackCompleted`，迁移 bridge 路由测试 | 4.0 前，D1/D2 已批准，顺序为 U4/C2 → T2 → T3 |
| T2 | 夹具手写回复、跳过派发环节、只测注入故障导致真实缺陷漏检；从契约序列化回复，用真实包装器和记录 worker 检查维护的示例 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| U2 | 工具清单与 `GetToolUsage` 输出过大；评估共享错误 schema 和短索引 | 4.1（2026-11-04 批准） |
| U3 | 参数描述为空或只有通用占位文字；维护具体参数含义 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| U4 | `FindTools` 示例与自身 schema 不符；复用维护的调用示例 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| U5 | 搜索排序与中文检索缺口；评估相关性与中文词汇 | 4.1（2026-11-04 批准） |
| U6 | 非插件客户端缺少连接顺序、审批和不重放规则；统一首次使用引导 | beta.4 VM 回归后决定 |
| U10 | 反馈入口分散、支持页 issue 类型不符、清单未标状态、发布说明计数空位及 `GenerateErrorReport` 文档缺口 | beta.4 VM 回归后决定 |
| C1 | 统一结果映射，处理各族写入 NotFound/Unknown 的不同语义 | 4.1（2026-11-04 批准） |
| C2 | wire DTO 手工复制易漂移，读取失败可能污染会话；从契约派生并核对故障边界 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| C3 | 重复遍历设备、属性和块列表；评估按绑定纪元缓存 | 4.1（2026-11-04 批准） |
| C4 | 原生层大量 catch-all 与吞异常；统一可观察的探测失败语义 | 4.1（2026-11-04 批准） |
| C5 | 操作事实分散维护；统一读取/写入及故障后的会话处理规则 | 4.1（2026-11-04 批准） |
| C6 | 适配器与逻辑层重复规则、重复动态辅助代码；合并纯逻辑 | 4.1（2026-11-04 批准） |
| C7 | 简化 `PortedToolContract` 工厂选择 | 4.1（2026-11-04 批准） |
| C8 | 静态发布键与导出池跨会话状态 | CI-FIX4 已隔离导出句柄并使用请求的发布键，余项待 beta.4 VM 回归后决定 |
| C9 | 版本旗标及重复分支；统一能力档案，先评估状态形状影响 | 并入 UNIFY-1（2026-11-04 批准） |
| C10 | 不可达与无引用代码；核实后删除 | beta.4 VM 回归后决定 |
| S3 | 更新器仅凭同一发布的摘要验证，账户被控制即可发布恶意更新 | 未完成，维护者 2026-11-04 接受风险，移出 4.0 前实施清单 |
| S4 | 发布令牌权限及生命周期；评估细粒度短期令牌、分支和标签规则 | 4.0 前（2026-11-04 批准）；多数是 GitHub 设置，由维护者操作 |
| S5 | 局域网明文 HTTP 暴露访问密钥与 PLC 密码 | 未实现加密，维护者 2026-11-04 接受风险，仅可信网络使用，移出 4.0 前实施清单 |
| S8 | AI 调用拒绝网络路径及 loopback 网络路径，防止审批前 SMB 查找泄露凭据，Workbench 人工操作不受此限制 | 4.0 前，D1 A、D2 已批准，尚未实现 |
| S9 | 调用日志、写入守卫和错误映射的脱敏规则不一致；统一脱敏策略 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |

## 仍需任务说明

T-IA（TIA-TEST/SAFE/BATCH/GAPS）、FB-VM 的正式回归说明、P8-95、S4 与后续零散任务仍需编写或补齐说明。S8、S9、T2、T3、C2、U3、U4、VMFIX-6 和 UNIFY-1 已有说明或并入对应任务，HANG-GUARD 已集成。B23、B31、B32、B33、B38、B42、B43、B44、B57 由 VMFIX-6 与 DES-4 承接。开工前核对问题仍可复现，已批准不等于已完成。

## 待维护者决定

| 编号 | 事项 |
|---|---|
| S7（4.0 前） | 决定如何处理旧公开标签和 3.x 资产中的 Siemens DLL；当前树与 4.0 包未携带它们，不代表历史分发问题已解决 |
| 正式版发布（4.0 前） | 定义并实现 `publish-prepared` 的正式版规则；当前只接受预发布，需决定 latest 与 3.3 更新器的关系 |
| D-P8 | `docs/reference/capabilities.md` 是否为预发布增加条目（旧发布流程曾打印 WARNING，新流程需另行决定） |
| D-P9 | HMI 审查遗留的 D2、D5、D7；B2、B9 是否已随 HMI-B3 完成 |
| D-P13 | 长操作（编译、下载、导入、导出）的 MCP 进度上报经 PLC worker 通道实现，beta.4 未含此项，后续排期待定 |
| D-Q11 | 代码精简计划其余批次的批准与排期：Q6c、Q11（worker 回复携带 V4 结果字段）、Q12a–e（一个 ResultMapper）、Q13（重复源码合并）、Q14（SafeXml 与路径策略）、Q15（命名）、Q16（开发文档从源生成） |
| D-J1 | P8-31j1（生成框架方案的修订与示例）在 4.0 完成，还是随 P8-31f–i 进入 4.1 |
| D-D4 | `docs/releases/v3.2.0.md`、`v3.3.0.md` 保留与否 |
| D-D6 | 4.1 是否把生成的工具矩阵并入版本工具目录 |
| D-MCP | 是否及何时采纳《工程开发规范》的 `mcp/` 草案；采纳时要登记的偏差见[标准说明](../standards/README.md#mcp-草案待采纳) |

已决定（2026-11-04）：

- 4.0 继续支持全部八个版本，含 V14 SP1 和 V15.1。

已决定（2026-11-01）：

- D-P1：4.0 正式版门槛加“没有未决的 blocker 报告”，已补记到 ADR 0023（DOCS-1）。
- D-P6：旧 `transport-usage-v18` 预算决定已随相应作业移除失去适用对象，不能据此放宽其他功能检查。
- D-P7：`ReleaseUnifiedReadCursor` 移出只读批处理，随 DES-3。
- D-P10：八版 worker 改为 MTA（MTA-1，beta.5）；Microsoft.Extensions 等改用稳定版（DEP-1）；MCP C# SDK 在 4.0 保持 0.3.0-preview.4，4.1 迁到 2.x。
- D-P12：新任务用主题编号，已有的 P8 编号不变。
