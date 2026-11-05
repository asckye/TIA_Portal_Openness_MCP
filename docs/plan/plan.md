# 路线图与待办（v4.0.0-beta.3）

状态：现行 · 最后核对：2026-11-04，master `ef1988bc`

本页是开发计划：按版本记录范围、任务状态、放行门槛和待维护者决定，取代已归档的[路线图](../archive/development/roadmap.md)和[重构计划](../archive/development/refactor-plan.md)（阶段 0–8 的 P0–P8 任务表、历史目标和风险留在那里）。

当前状态和进行中的分支见[开发进度](progress.md)，决定见 [ADR](../standards/decisions/README.md)。`bin-build/refactor/tasks/followups.md`（不入库）只是收件箱，条目在决定当天转写到本页或 ADR；任务说明只引用本页的编号。

4.0 的方向：八个版本使用 FoundationHost 加每会话一个 PLC worker，除工具集合和官方 API 差异外共用一套逻辑（[ADR 0022](../standards/decisions/0022-engine-retirement-and-eight-release-unification.md)、[ADR 0014](../standards/decisions/0014-tool-counts-follow-official-api.md)）；真机可靠性优先，每批修复在 VM 上复测；补齐对照 T-IA 和官方 API 的小缺口；流程变简单：合并看受影响的检查，完整检查和打包在 `prepare`，确认后 `publish-prepared` 只核对、推送、打标签并公开草稿。离线通过不证明原生行为，未在真实 TIA Portal 上验证的项写进发布说明。

状态取值：已合并（带提交号）· 待合并（分支已完成，等合并窗口）· 进行中 · 排队（任务说明已写，等前置任务）· 未开始 · 推迟 · 取消 · 提议（待维护者决定）· 并入（由另一任务完成）。新任务用主题编号（VMFIX、DES、SPD、Q 等），已有的 P8 编号不变（D-P12）。

## 放行门槛

| 关口 | 门槛 | 依据 |
|---|---|---|
| 合并 | 本机 `merge-check`：普通 `dotnet build`、受影响的 `dotnet test` 与生成/仓库检查，目标约 10 分钟（NEW-REL-2）。任务说明先经独立 AI 审查，交付后经审查与主机验证；GitHub CI 不阻塞合并 | [ADR 0031](../standards/decisions/0031-release-path.md)；维护者 2026-11-03 |
| 预发布 `4.0.0-beta.N` | GitHub 预发布，不设为最新版，3.3 更新器不提示；发布说明附未验收清单；发布后测试者按[清单](../guides/beta-tester-checklist.md)报告，维护者做 VM 回归；每次公开前维护者确认，确认后保持候选提交不变 | [ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md) |
| 发布准备与公开 | `prepare` 完整构建、所有注册套件与保留检查通过后封存候选并提前上传草稿；`publish-prepared` 不构建或测试，300 秒内完成公开。没有 nightly，也不采纳旧收据。发布后只校验 ZIP 与 sidecar | [ADR 0031](../standards/decisions/0031-release-path.md) |
| 4.0 正式版 | 全部 4.0 任务合并、完整准备通过、rc 经验证、没有未决 blocker、维护者确认；正式版公开规则须先实现。起初不向 3.3 更新器提供，不设固定公测期或连续通过次数 | [ADR 0023](../standards/decisions/0023-release-4-0-final-criteria.md)、[0031](../standards/decisions/0031-release-path.md) |

检查、测试、断言、超时和阈值只有维护者批准后才能删除、跳过或放宽；已经批准的移除范围见[验证](../manuals/validation.md#已批准移除的检查机制)。REVIEW-1 的漏洞扫描须迁入新工具，排 beta.4；新 `prepare` 当前没有该扫描，不把旧扫描规则写成已实现的门禁。

## 4.0.0-beta.3（已发布，2026-11-04）

master `ef1988bc` 已含 beta.3 的 CHANGELOG 和[发布说明](../releases/v4.0.0-beta.3.md)，以及 PIPE-INT/SLIM-1、VMFIX-2、新发布路径的 A、B、C 三部分、SEC-1、Q1–Q9、CPM、NEW-REL-2、NEW-REL-3 和 THIRD-1。beta.3 已于 2026-11-04 在 `1d57c277` 发布，整理先于发布完成；依赖产品分支的拆分、`src/EngineHost` 改名与长行合并门排 beta.4。

### 发布前顺序

| 顺序 | 任务 | 内容与状态 |
|---|---|---|
| 1 | NEW-REL-B、SEC-1 | 已合并（`0ae4b1e8`、`1af64ce7`；`6ae345fd`、`b711e0bb`、`979b1263`）：NEW-REL-B 接入所有套件、保留检查、候选和自动草稿上传；SEC-1 修补审批设置保护、文件写入与 Git 调用的小范围安全问题（调查 S1/S2）。输出根以外写文件的完整审批另排 beta.4 |
| 2 | Q1–Q9、CPM | 已合并（Q1 `5f956ea8`、Q5 `a26c60bd`、Q2 `84c4e966`、Q3 `bd30bc9d`、Q4 `c001c1c4`、Q7 `6d771add`、CPM `975a0ff5`、Q8 `02548d27`、Q9 `74073ed0`，Q-INT-1 `3cac74e0`）：清理引擎残留、重复检查、SafeV4 候选，集中包版本、压缩契约公共 schema、派生发布键 |
| 3 | NEW-REL-2、NEW-REL-3、THIRD-1、主机测试精简 | 已合并（NEW-REL-2 `90be875b`、`9485a33b`；NEW-REL-3a–e `bce6994c`、`6a489b47`、`3e343bd6`、`3b66a7be`、`090ebab1`；THIRD-1 `63281105`）：新合并门；删除旧发布工具和全部八版响应捕获/基线；第三方裁剪；版本重复的准入用例精简 1,810 例；引擎删除后注册表保留 38 个套件。保留功能、协议、审批、写安全、故障恢复测试及各数量门槛 |
| 4 | DOCS-1 → DOCS-2 → DOCS-3 | 在 NEW-REL-3 之后依次合并：DOCS-1 分目录并归并规范，DOCS-2 刷新发布路径与调查，DOCS-3 核对重基后的事实和指令；当前文档链待审查集成；[ADR 0030](../standards/decisions/0030-documentation-and-repository-structure.md) 与 [0031](../standards/decisions/0031-release-path.md) |
| 5 | REORG-2 | 整理数据与脚本：`schemas/` 并入 `reference/`、运行布局并入设计规范、一个 `.slnx`；`hooks/` 保留根目录，根目录 `runtime/` 与项目 `bin`/`obj` 保留；清理旧记录和 LibraryRenameProbe，核对交付清单与许可文件引用；待实施 |
| 6 | REORG-3 | 整理源码、测试和交付包路径，更新所有引用、注册与交付规则；待实施 |
| 7 | TIDY-1–TIDY-6 | TIDY-1 工具已合并（`0f2ffd29`）；在重基后的路径上运行 TIDY-2 长行整理和 TIDY-3–6 无行为变更拆分。受产品分支影响的文件留给 beta.4 的 TIDY-7；文档中文段落不在句中换行 |
| 8 | 实际准备 | 在最终干净 master 上运行完整 `prepare`，审查全部结果、ZIP、候选与草稿；尚未执行 |
| 9 | 确认与公开 | 维护者明确确认后运行 `publish-prepared`，随后 VM 回归与测试者验证；尚未执行 |

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

以下在 beta.3 后集成。VMFIX-3/4/5 和 DES-INT 必须适配整理后的代码及新验证入口；DES-4 在 DES-INT 之后。旧引擎测试迁移到仍在使用的测试工程，保留工具定义契约与功能覆盖。

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| TIDY-7 | DES-INT、VMFIX-3、RELAY-1、UI-INT、N1-INT 全部进入 master 后，独占执行剩余大文件拆分与新增长行整理，保持行为和 IL 不变 | 排队（产品分支全部合并后） | 2026-11-04 |
| HOST-RENAME | `src/EngineHost` 改名，同步链接源、工程和文档路径 | 排队（beta.4 产品分支合并后） | 2026-11-04 |
| LONG-LINE-GATE | 将 `wrap-long-lines -Check` 加入 `merge-check`，检查代码长行 | 排队（beta.4 整理后） | 2026-11-04 |
| HANG-GUARD | 将功能测试的短时限改为防挂起保护；性能只报告趋势，按任务说明保留功能断言和数量门槛 | 已决定，待任务说明 | 2026-11-03 |
| Q6a | 已移植工具一条调用管线：V20/V21 也经 `FoundationV4Tool`，15 个调用流程旗标合并为一个档案；吸收 Q6b，取代 P8-04n2；合并后在 VM 上复测 V20/V21 | 排队（Q9 之后） | 2026-11-02 |
| Q10a、Q10b | 声明式工具清单：读（手写工具表导出到 `reference/tool-catalog/tools.json`）、写（由清单生成各表，删除正则解析副本）；Q6c（宿主工具改走 `FoundationV4Tool`）在 Q10b 后评估 | 排队（Q6a 之后；Q10b 任务说明待写） | 2026-11-02 |
| DES-INT（DES-2、DES-3） | DES-2：一项功能一个实现（L2 工具转到 Foundation 实现），安全移块：导出并核实 → 删除源块 → 导入目标组 → 核实，失败时把备份恢复到原组；DES-3：写入工具默认预览（`dryRun` 默认 true）并绑定 `expectedProjectFile`，多步写入失败时回滚；工具描述去掉“引擎”说法，批处理准入按 BatchRead/BatchWrite 描述；`ReleaseUnifiedReadCursor` 移出只读批处理（D-P7） | 待合并（`refactor/DES-INT` `5b74969a`，第 2 轮完成） | 2026-11-01 |
| DES-4 | 统一参数模型（先写 ADR），字段级参数错误、去敏原生消息和按请求号查日志；补入 B21、B27、B28、B30 的明确处理与验收 | 排队（DES-INT 之后） | 2026-11-01 |
| VMFIX-3 | 写入结果如实（R2/R4）：确定的结果不报 `OUTCOME_UNKNOWN`、不要求会话重置；独占访问；`ImportDeviceAml` 只在 V21 移出独占访问；已批准 `HmiDryRunTests`、`PortedPortalAdmissionTests` 及第 5 轮 `HonestWriteSources` 两项替换断言；与 Q2 相遇时 `HonestOutcomeMappingTests` 移到 KeptCode/FoundationHost 测试，`EngineWorkerHost` 的修改随 `src/Engine` 删除 | 进行中（`refactor/VMFIX-3` `589d6559`，原分支待适配整理后的主线） | 2026-11-02 |
| VMFIX-4 | 会话生命周期与恢复（R3/R5）：重新绑定纪元、是否重启 TIA 的判断、读超时取消、重置关口、日志过滤 | 排队（整理后的主线可用后） | 2026-11-02 |
| VMFIX-5a、VMFIX-5b | 其余 HIGH 缺陷：5a 结果映射、去敏和宿主诊断；5b V21 的 PLC 服务、工艺对象和 Unified HMI；两者可并行 | 排队（整理后的主线可用后） | 2026-11-02 |
| RELAY-1 | 代理安全的远程连接：随包本地 stdio 中继，私网地址绕过 HTTP(S)_PROXY，其他地址遵守 NO_PROXY，令牌只在工作台数据目录；环境检查“代理与远程连接”及一键修复 | 待合并（`refactor/RELAY-1` `0e4f5d5c`，第 2 轮完成） | 2026-11-01 |
| UI-v2：UI-2b、UI-2d、UI-2c | UI-2b 外壳（232/64 侧栏、工具栏、合并的安全横幅、设置与审批成为页面）与 UI-2d 各页面重绘合在 UI-INT；UI-2c 审批改为列表加详情页，宿主计算风险等级，写入决定按钮等权重，破坏性操作默认拒绝；APR-05 关闭写操作审批须输入确认词，普通审批不加输入框 | 待合并（`refactor/UI-INT` `390fb6e4`、`refactor/UI-2c` `6942199f`） | 2026-11-01 |
| P8-04n1（N1-INT） | 八版统一对外呈现：精简清单默认启用、服务器说明、档案名、目录呈现（P8-04n 的后半 P8-04n2 由 Q6a 承担） | 待合并（`refactor/N1-INT` `5c800dc0`） | 2026-11-01 |
| P8-84 | 工作台 TIA 进程选择器：多个 TIA 进程时由用户选择，按 PID 精确附着 | 待合并（`refactor/P8-84` `e9cca2d8`） | 2026-10-21 |
| P8-21f | 删除工作台重复的 TIA 适配代码：Studio 改用共享适配器（P4-03 第 G3 步，删除 `StudioOpenness.V*`），VCI 平铺工作区与 MCP `UngroupedDevices` 对齐 | 未开始 | 2026-10-08 |
| FB-VM | beta.2 VM 发现和 beta.3 测试包 V21 VM 测试（59 个去重缺陷）登记进[测试者反馈台账](progress.md)；VMFIX-2..5 合并后按复测计划复测（45 项 R1 复测、VMFIX-3/4/5 的待确认项、约 40 次回归抽样）；含 P8-20 端到端验收（清单 T10） | 未开始（VM 会话等主机门禁空闲后的信号） | 2026-11-01 |
| REVIEW-1 | 将未经批准的检查放宽检测、安全分析器与棘轮、依赖漏洞扫描移植到新工具；复杂度仅报告 | 待重做（面向 `merge-check` / `prepare`，不复用旧发布链） | 2026-11-02 |
| SCORE-1 | 质量评分卡与性能趋势，只读新工具的实际结果，每个版本一张；删除对旧发布证据链的依赖 | 待适配新工具（REVIEW-1 后） | 2026-11-03 |
| VMAUTO-1 | 扩展 `scripts/diagnostics/campaign`，读取维护的调用与序列；串行读取/预览后执行序列，区分通过、缺陷、应拒绝和待人工判断；发布后在获授权的 VM 与项目副本运行 | 已决定，待任务说明 | 2026-11-03 |
| FILE-APPROVAL | 文件写入目标在安装输出根以外时须审批；输出根、例外和安全验收按 OUTROOT-1 实现 | 实现待审查；主机合并检查待跑 | 2026-11-04 |
| COV-V21 | 更正 [Openness 覆盖](../reference/openness-coverage.md)中的 V21 数字（直接引用 1,748，待复核 2,575）和复现命令（`prepare -BuildOnly` 后运行 `audit-version-tools`） | 未开始 | 2026-11-02 |

## 4.0.0-beta.5

beta.4 合并后开始。V21 API 的 6 个 P1 缺口按 API 先行注册，带预览与读回；API-P1a 的第 0 部分（共用的计划绑定辅助方法）先合并，其余三项随后并行。T-IA 批次和生态小项每项先由 Opus 核对官方 API 再写任务说明。许可规则（2026-10-30）：GPL、AGPL 和专有来源只借鉴思路；复制 Siemens 共享代码前逐份阅读许可；OPC Foundation 许可另行复核。

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
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
| UNIFY-1 | 审查约 36 处点名 V20/V21 的代码和 `ReleaseCatalogData` 旗标（其中 `WorkerClient.cs` 12 处）；真实 API 差异进能力表，其余统一；C9 并入 | 未开始 | 2026-11-04 |
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
| T3 | 全目录审批拒绝零写入、预览无原生写入、`dryRun.effective`、B33 与回滚读回的安全测试缺口；迁移仍有效的旧审批/回滚测试 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
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
| C8 | 静态发布键与导出池跨会话状态；核对并隔离 HTTP 会话 | beta.4 VM 回归后决定 |
| C9 | 版本旗标及重复分支；统一能力档案，先评估状态形状影响 | 并入 UNIFY-1（2026-11-04 批准） |
| C10 | 不可达与无引用代码；核实后删除 | beta.4 VM 回归后决定 |
| S3 | 更新器仅凭同源摘要验证；决定是否采用固定信任密钥与包签名 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| S4 | 发布令牌权限及生命周期；评估细粒度短期令牌、分支和标签规则 | 4.0 前（2026-11-04 批准）；多数是 GitHub 设置，由维护者操作 |
| S5 | 局域网明文 HTTP 的令牌及密码；决定 TLS 或回环加隧道方案 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| S8 | UNC 输入、预览及审批前路径查询可能暴露 NTLM 凭据；决定准入限制与兼容范围。根外写入由 FILE-APPROVAL 审批 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |
| S9 | 调用日志、写入守卫和错误映射的脱敏规则不一致；统一脱敏策略 | 4.0 前（2026-11-04 批准），开工前先确认仍可复现 |

## 待维护者决定

| 编号 | 事项 |
|---|---|
| S7（4.0 前） | 决定如何处理旧公开标签和 3.x 资产中的 Siemens DLL；当前树与 4.0 包未携带它们，不代表历史分发问题已解决 |
| 正式版发布（4.0 前） | 定义并实现 `publish-prepared` 的正式版规则；当前只接受预发布，需决定 latest 与 3.3 更新器的关系 |
| D-P8 | `docs/reference/capabilities.md` 是否为预发布增加条目（旧发布流程曾打印 WARNING，新流程需另行决定） |
| D-P9 | HMI 审查遗留的 D2、D5、D7；B2、B9 是否已随 HMI-B3 完成 |
| D-P13 | 长操作（编译、下载、导入、导出）的 MCP 进度上报经 PLC worker 通道实现（Q3 发现 `WorkerClient` 未实现进度接口），排 beta.4 还是 beta.5 |
| D-Q11 | 代码精简计划其余批次的批准与排期：Q6c、Q11（worker 回复携带 V4 结果字段）、Q12a–e（一个 ResultMapper）、Q13（重复源码合并）、Q14（SafeXml 与路径策略）、Q15（命名）、Q16（开发文档从源生成） |
| D-J1 | P8-31j1（生成框架方案的修订与示例）在 4.0 完成，还是随 P8-31f–i 进入 4.1 |
| D-D4 | `docs/releases/v3.2.0.md`、`v3.3.0.md` 保留与否 |
| D-D6 | 4.1 是否把生成的工具矩阵并入版本工具目录 |

已决定（2026-11-04）：

- 4.0 继续支持全部八个版本，含 V14 SP1 和 V15.1。

已决定（2026-11-01）：

- D-P1：4.0 正式版门槛加“没有未决的 blocker 报告”，已补记到 ADR 0023（DOCS-1）。
- D-P6：旧 `transport-usage-v18` 预算决定已随相应作业移除失去适用对象，不能据此放宽其他功能检查。
- D-P7：`ReleaseUnifiedReadCursor` 移出只读批处理，随 DES-3。
- D-P10：八版 worker 改为 MTA（MTA-1，beta.5）；Microsoft.Extensions 等改用稳定版（DEP-1）；MCP C# SDK 在 4.0 保持 0.3.0-preview.4，4.1 迁到 2.x。
- D-P12：新任务用主题编号，已有的 P8 编号不变。
