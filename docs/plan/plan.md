# 路线图与待办（v4.0.0-beta.3）

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本页是开发计划：按版本记录范围、任务状态、放行门槛和待维护者决定，取代已归档的[路线图](../archive/development/roadmap.md)和[重构计划](../archive/development/refactor-plan.md)（阶段 0–8 的 P0–P8 任务表、历史目标和风险留在那里）。

当前状态和进行中的分支见[开发进度](progress.md)，决定见 [ADR](../standards/decisions/README.md)。`bin-build/refactor/tasks/followups.md`（不入库）只是收件箱，条目在决定当天转写到本页或 ADR；任务说明只引用本页的编号。

4.0 的方向：八个版本使用 FoundationHost 加每会话一个 PLC worker，除工具集合和官方 API 差异外共用一套逻辑（[ADR 0022](../standards/decisions/0022-engine-retirement-and-eight-release-unification.md)、[ADR 0014](../standards/decisions/0014-tool-counts-follow-official-api.md)）；真机可靠性优先，每批修复在 VM 上复测；补齐对照 T-IA 和官方 API 的小缺口；流程变简单：合并看受影响的检查，完整检查放 nightly，发布只核对、打包和上传。离线通过不证明原生行为，未在真实 TIA Portal 上验证的项写进发布说明。

状态取值：已合并（带提交号）· 待合并（分支已完成，等合并窗口）· 进行中 · 排队（任务说明已写，等前置任务）· 未开始 · 推迟 · 取消 · 并入（由另一任务完成）。新任务用主题编号（VMFIX、DES、SPD、Q 等），已有的 P8 编号不变（D-P12）。

## 放行门槛

| 关口 | 门槛 | 依据 |
|---|---|---|
| 合并 | 本机 `check-all -Affected` 加受影响的检查通过（SLIM-1 目标：少于 20 分钟）；每个 Codex 交付先经独立 AI 审查（Opus，高强度），再由 Claude 审查和主机测试；REVIEW-1 合并后由 `check-loosening` 拦截未经批准的放宽；GitHub CI 不阻塞合并 | [ADR 0024](../standards/decisions/0024-ci-does-not-block-merges.md)；维护者 2026-11-02、11-03 |
| 预发布 `4.0.0-beta.N` | GitHub 预发布，不设为最新版，3.3 更新器不提示；说明附 “Not verified on a real TIA Portal” 清单；测试者按[预发布真机验证清单](../guides/beta-tester-checklist.md)报告，我们在 VM 上按同一清单回归；每次发布前维护者确认，发布期间不改代码 | [ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md) |
| 发布证据 | 最近一次通过的 nightly 加已验证的增量构建单元；nightly 门槛 45 分钟，SLIM-1 目标少于 30 分钟（RULES-1 合并前发布工具仍只采纳同一提交的冷 nightly，代码门槛 30 分钟）；nightly 失败或超时阻止下一次发布；发布不等 CI（`-NoWait`）；发布需要 7 天内通过的 GitHub CI 依赖漏洞扫描，离线“未运行”不使 nightly 失败 | [ADR 0025](../standards/decisions/0025-release-evidence-nightly-and-verified-build-units.md)；维护者 2026-11-02 |
| 4.0 正式版 | 全部 4.0 任务合并；check-all 通过，CI 结果在维护者确认前另行核对；没有未决的 blocker 报告（D-P1）；由发布链生成 rc；维护者确认。不设公测期，不要求连续 20 次通过；起初不向 3.3 更新器提供 | [ADR 0023](../standards/decisions/0023-release-4-0-final-criteria.md)（D-P1 已补记） |

检查、测试、断言、超时和阈值只有维护者批准后才能删除、跳过或放宽；2026-11-03 批准的删除清单见 SLIM-1。预先授权的例行决定（2026-11-01）：测试计时接缝（产品代码不变）、工作台设计值断言跟随新设计、带差异说明的正式重新生成。

## 4.0.0-beta.3（未发布）

master `1dfe2067` 已含定稿的 CHANGELOG `[4.0.0-beta.3]` 条目和[发布说明](../releases/v4.0.0-beta.3.md)（`dbe12b88`）。范围按 2026-11-01 23:50 的裁剪（批次 1、批次 2、UI-2a、FIXTURE-CRASH、DEP-1），2026-11-02 加入 VMFIX-2，2026-11-03 加入 SLIM-1。发布顺序：PIPE-INT（含 SLIM-1）主机门禁 → 合并 → VMFIX-2 变基并把其 CHANGELOG 行移入 beta.3 → 主机门禁 → 合并 → nightly → 维护者确认 → 发布，目标 2026-11-03。

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| beta3-rename | 对外名称改为自动化工程工作台，维护者版权行，西门子商标声明 | 已合并（`77bdf060`、`1f37ab3d`、`1cad1df2`） | 2026-10-30 |
| beta3-docs | 3.3 到 4.0 迁移指南、预发布真机验证清单、测试者反馈台账、Issue 表单 | 已合并（`f6bf1dea`） | 2026-10-30 |
| SPD-1、SPD-2、SPD-4 | 重复工作只做一次、派生子集、`-Affected`、nightly 复用与阻断发布、`-FailFast` | 已合并（`a6ced5da`、`b7db27a0`、`bf04b534`、`c68c31d3`） | 2026-10-30 |
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
| PIPE-INT | SPD-3/3b/3c/3d、SPD-5、SPD-7 并到 master：可复现构建、已验证构建单元、按哈希发布已封存的候选、可用的 `-Affected` 与 nightly 复用；保留第 1–4 轮（缓存键、解码器 Python、快速门禁、声明的输入、内存峰值） | 进行中（`refactor/PIPE-INT` `92d58d94`，与 SLIM-1 一起过主机门禁） | 2026-11-02 |
| SLIM-1 | 整套精简（取代 PIPE-INT 第 9 轮）：删除延迟门禁与基准作业、HTTP 稳定性作业和独占静默阶段；删除 `suite-output-parity`；每个套件只按交付配置跑一次（删除 `studio-*-true/false`、`offline-v20`、`kept-code-v20` 等重复变体）；V14 SP1–V19 删除逐调用响应捕获和 transport-usage（契约快照保留）；冷构建提速。目标：合并门少于 20 分钟，nightly 少于 30 分钟 | 进行中（已交回，待主机验收） | 2026-11-03 |
| VMFIX-2 | beta.3 测试包 V21 VM 测试的 R1 会话中断：扩展族（报警、OPC UA、SiVArc、Startdrive/DCC、Safety）回复校验，Unified 结构化读取范围，事件脚本代码，分组设备的 HMI 探测，HMI 运行状态原因；第 2 轮修复 `ManagePlcProtection` 回复被新校验拒绝的回归 | 进行中（`refactor/VMFIX-2` `3a588b36`，第 2 轮） | 2026-11-02 |

## 4.0.0-beta.4

范围按 2026-11-01 23:50 的裁剪，加上 2026-11-02 批准的代码精简（Q 系列）、VM 修复和审查链，以及 2026-11-03 的评分卡和仓库整理。Q 系列合并顺序：PIPE-INT → Q1‖Q5 → Q2 → Q3‖Q4 → Q7 → Q8‖CPM → Q9 → Q6a → Q10a → Q10b。VMFIX-3/4/5 在 VMFIX-2 合并后启动；DES-4 在 DES-INT 之后。设计细节见已归档的 [P8-04 引擎退役设计](../archive/development/design/p8-04-engine-retirement.md)和[工作台控制设计](../archive/development/design/workbench-control-and-shared-session.md)。

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| Q1–Q5、Q7 | Q1 生成器不再读 `src/Engine`，不再在运行时改写 3.x 工具名；Q2 删除 `src/Engine` 和只测引擎的测试、夹具、脚本（取代 P8-04l2）；Q3 删除宿主引擎交叉检查路径和死编译符号；Q4 Windows 主机只跑 `foundation-api`，删除 `kept-code-v20`；Q5 删除已有 C# 实现的 Python 检查及其对照作业；Q7 打标签 `archive/safev4-candidates` 后删除 SafeV4 候选代码，修订 ADR 0002，取消 P8-04l3 | 待合并（`refactor/Q1`…`refactor/Q7`，叠在 PIPE-INT 上） | 2026-11-02 |
| CPM、Q8、Q9 | 集中包版本（`Directory.Packages.props`）；契约基线格式 2（公共输出 schema 每个文件存一次，按 SHA-256 引用，比较仍逐字节）；发布键数组从版本范围派生 | 待合并（`295acbc5`、`e53590d9`、`99977ba3`） | 2026-11-02 |
| Q6a | 已移植工具一条调用管线：V20/V21 也经 `FoundationV4Tool`，15 个调用流程旗标合并为一个档案；吸收 Q6b，取代 P8-04n2；合并后在 VM 上复测 V20/V21 | 排队（Q9 之后） | 2026-11-02 |
| Q10a、Q10b | 声明式工具清单：读（手写工具表导出到 `reference/tool-catalog/tools.json`）、写（由清单生成各表，删除正则解析副本）；Q6c（宿主工具改走 `FoundationV4Tool`）在 Q10b 后评估 | 排队（Q6a 之后；Q10b 任务说明待写） | 2026-11-02 |
| DES-INT（DES-2、DES-3） | DES-2：一项功能一个实现（L2 工具转到 Foundation 实现），安全移块：导出并核实 → 删除源块 → 导入目标组 → 核实，失败时把备份恢复到原组；DES-3：写入工具默认预览（`dryRun` 默认 true）并绑定 `expectedProjectFile`，多步写入失败时回滚；工具描述去掉“引擎”说法，批处理准入按 BatchRead/BatchWrite 描述；`ReleaseUnifiedReadCursor` 移出只读批处理（D-P7） | 待合并（`refactor/DES-INT` `5b74969a`，第 2 轮完成） | 2026-11-01 |
| DES-4 | 统一参数模型（先写 ADR），参数错误指出具体字段，返回去敏的原生消息与诊断编号（`innererror`），按请求号查调用日志 | 排队（DES-INT 之后） | 2026-11-01 |
| VMFIX-3 | 写入结果如实（R2/R4）：确定的结果不报 `OUTCOME_UNKNOWN`、不要求会话重置；独占访问；`ImportDeviceAml` 只在 V21 移出独占访问；已批准 `HmiDryRunTests`、`PortedPortalAdmissionTests` 的断言修改；与 Q2 相遇时 `HonestOutcomeMappingTests` 移到 KeptCode/FoundationHost 测试，`EngineWorkerHost` 的修改随 `src/Engine` 删除 | 进行中（`refactor/VMFIX-3` `867efd36`，第 4 轮，基于 VMFIX-2） | 2026-11-02 |
| VMFIX-4 | 会话生命周期与恢复（R3/R5）：重新绑定纪元、是否重启 TIA 的判断、读超时取消、重置关口、日志过滤 | 排队（VMFIX-2 合并后） | 2026-11-02 |
| VMFIX-5a、VMFIX-5b | 其余 HIGH 缺陷：5a 结果映射、去敏和宿主诊断；5b V21 的 PLC 服务、工艺对象和 Unified HMI；两者可并行 | 排队（VMFIX-2 合并后） | 2026-11-02 |
| RELAY-1 | 代理安全的远程连接：随包本地 stdio 中继，私网地址绕过 HTTP(S)_PROXY，其他地址遵守 NO_PROXY，令牌只在工作台数据目录；环境检查“代理与远程连接”及一键修复 | 待合并（`refactor/RELAY-1` `0e4f5d5c`，第 2 轮完成） | 2026-11-01 |
| UI-v2：UI-2b、UI-2d、UI-2c | UI-2b 外壳（232/64 侧栏、工具栏、合并的安全横幅、设置与审批成为页面）与 UI-2d 各页面重绘合在 UI-INT；UI-2c 审批改为列表加详情页，宿主计算风险等级，写入决定按钮等权重，破坏性操作默认拒绝 | 待合并（`refactor/UI-INT` `390fb6e4`、`refactor/UI-2c` `6942199f`） | 2026-11-01 |
| P8-04n1（N1-INT） | 八版统一对外呈现：精简清单默认启用、服务器说明、档案名、目录呈现（P8-04n 的后半 P8-04n2 由 Q6a 承担） | 待合并（`refactor/N1-INT` `5c800dc0`） | 2026-11-01 |
| P8-84 | 工作台 TIA 进程选择器：多个 TIA 进程时由用户选择，按 PID 精确附着 | 待合并（`refactor/P8-84` `e9cca2d8`） | 2026-10-21 |
| P8-21f | 删除工作台重复的 TIA 适配代码：Studio 改用共享适配器（P4-03 第 G3 步，删除 `StudioOpenness.V*`），VCI 平铺工作区与 MCP `UngroupedDevices` 对齐 | 未开始 | 2026-10-08 |
| FB-VM | beta.2 VM 发现和 beta.3 测试包 V21 VM 测试（59 个去重缺陷）登记进[测试者反馈台账](progress.md)；VMFIX-2..5 合并后按复测计划复测（45 项 R1 复测、VMFIX-3/4/5 的待确认项、约 40 次回归抽样）；含 P8-20 端到端验收（清单 T10） | 未开始（VM 会话等主机门禁空闲后的信号） | 2026-11-01 |
| REVIEW-1 | 自动审查：`check-loosening`（合并门，阻断）、.NET 安全分析器加棘轮基线与依赖漏洞扫描、复杂度报告（不阻断）；已知检测盲区写进文档后不再追加 | 排队（第 3 轮；基于 Q8，SLIM-1 合并后变基） | 2026-11-02 |
| SCORE-1 | 质量评分卡：发布工具 `scorecard` 命令，14 个维度各 0–5 分加性能趋势，只读已有数据，每个发布一张，附在发布证据中 | 排队（REVIEW-1、SLIM-1 合并后） | 2026-11-03 |
| RULES-1 | 三档验证、发布证据按 ADR 0025、机器级重型作业锁、`regen-all`、Codex 沙箱可跑子集、发布默认不等 CI；第 2 轮：实时相关测试只在 nightly 和受影响时运行，纯文档改动只跑文档与仓库检查；理顺 nightly 门槛（代码 30 分钟，ADR 45 分钟） | 排队（流水线项，不阻塞发布；`refactor/RULES-1` `4d3704fe`，SLIM-1 合并后变基） | 2026-11-01 |
| SPD-6、SPD-6b | 按用例成本分片，压缩 nightly | 排队（流水线项；SLIM-1 合并后重新评估是否仍需要） | 2026-11-01 |
| DOCS-1 | 仓库整理批次 1：文档分为 `standards/`、`plan/`、`manuals/`、`archive/`，用户文档路径不变；共用规则写在 `AGENTS.md`，`CLAUDE.md` 引用它；[ADR 0030](../standards/decisions/0030-documentation-and-repository-structure.md) 取代 ADR 0026 的目录部分（语言规则保留）；收尾 P8-63、P8-04m 和 P7-11b 的文档剩余项 | 进行中（`refactor/DOCS-1`，beta.3 发布后合并） | 2026-11-03 |
| REORG-2 | 仓库整理批次 2（数据与脚本）：`schemas/` 并入 `reference/`，`hooks/` 并入 `plugin/`，`runtime/` 说明并入设计规范，一个 `.slnx`，输出写到 `bin-build`；决定 `manifest/release-build.json` 去留；删除 LibraryRenameProbe 及其历史记录；归档 `docs/development/evidence/*.json`（待核对）；交付清单移除 `JetBrainsMono-OFL.txt`、`NotoSansSC-OFL.txt`；与 P8-10d/e/f 同批 | 排队（Q 系列之后） | 2026-11-03 |
| REORG-3 | 仓库整理批次 3：源码、测试和交付包路径 | 排队（最后） | 2026-11-03 |
| COV-V21 | 更正 [Openness 覆盖](../reference/openness-coverage.md)中的 V21 数字（直接引用 1,748，待复核 2,575）和复现命令（`build-multi-version` 后运行 `audit-version-tools`） | 未开始 | 2026-11-02 |

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
| P8-10d/e/f | 发布链 Python 改 C#（快照、打包、验包），重新移植 10-21 以来的发布链修复（含更新器哈希绑定），rc 证明字节一致；方案见已归档的 [Python 改 C# 方案](../archive/development/phase8-python-plan.md) | 进行中（`refactor/P8-10d`、`P8-10e`、`P8-10f` 只有未验证的 WIP；随 REORG-2） | 2026-10-22 |
| P8-93b、P8-94b | 未被 check-all 覆盖的发布链检查并入 check-all（SLIM-1 后按新作业清单重新确定范围，（待核对））；TiaMcp.Logic 统一构建属性；package-test 报告退役文件、排除日志 | 未开始 | 2026-10-22 |
| ADR-0023 | ADR 0023 补写 D-P1 一行（没有未决的 blocker 报告） | 并入 DOCS-1 | 2026-11-01 |
| RENAME-GH | GitHub 仓库改名为 automation-engineering-workbench，同步 README、NOTICE、插件和发布工具中的仓库地址（执行前再次确认） | 未开始 | 2026-10-30 |
| RC | 用完整发布链生成 rc；定稿 [4.0 发布说明](../releases/v4.0.0.md)；4.0 起初不向 3.3 更新器提供 | 未开始 | 2026-10-30 |

## 4.1

| 编号 | 内容 | 状态 | 决定日期 |
|---|---|---|---|
| P8-31f–i | 库与母版实例化、`CheckProjectStandard`（2026-11-02 更正后仍在 4.1）、工作台生成模块、状态模型包；P8-31h1 审批抽屉扩展；P8-31e6 随 SafeV4 候选删除（Q7）失去前提（待核对）。设计见已归档的[项目生成框架](../archive/development/design/project-generation-framework.md)和[分阶段生成](../archive/development/design/p8-31-staged-generation.md) | 推迟 | 2026-10-22 |
| P8-40 | 表格与 ECAD 导入、Classic VB 到 Unified JS、V20/V21 补深度、按版本输出的构建工具 | 推迟 | 2026-10-22 |
| API-P2、API-P3 | V21 API 差距评审的 P2（嵌套 Unified 运行设置、SIMATIC SD 更多语言、Multiuser、UMAC 与证书设置、PLC 与显示屏密码、参考工程、VCI 管理、GSD 参数、运动解释程序、项目文本筛选、带依赖生成源）和 P3（Classic 删除、空 FB、块元数据、少量硬件服务等） | 推迟 | 2026-11-02 |
| P8-95a–g | EplanMCP 借鉴项：VM HTTP 加固、“工程内容是数据”的服务器说明与审批卡清理、防误点、退出确认与单实例、Claude Desktop（MSIX）配置路径、CI 加固、doctor/audit 的 `schemaVersion` | 推迟 | 2026-11-01 |
| T-IA 大项 | 块接口编辑与 SCL 体替换（带回滚）；其余硬件缺口（插槽兼容、命名 CPU/网络/PN 属性、AML/GSD 预检；改型号和子网已提前到 API-P1c）；异步作业与取消；离线快照与撤销；工程报告与交付物；PLCSIM Advanced 扩展；Classic HMI 动画与事件；GRAPH/FBD/STL 生成；流水线与工作计划；帮助与文档搜索；多工程会话；模式库内容；VCI 工作区分组；一次调用导出整个工程到 Git | 推迟 | 2026-10-30 |
| 生态大项 | 整程序导出、磁盘编辑、只导入变化（先预览）；覆盖前自动快照与回滚；PLC 代码规则检查（规则目录、SARIF、评分）；SCL 单元测试库与运行器；CI 入口与 GitHub/GitLab 模板；Siemens 文档搜索；Web API 补全；生成后自检循环；AML 离线核对与 IO 变量生成；DB 注释生成 HMI 报警、导出全部 Unified 画面脚本；GRAPH 文本 DSL 与 STL 控制流 | 推迟 | 2026-10-30 |
| MCP-SDK | MCP C# SDK 从 0.3.0-preview.4 迁到 2.x；MCP 协议在 .NET SDK 支持后迁到无状态修订 | 推迟 | 2026-11-01 |
| MTA-1 收尾 | 删除隐藏的 STA 回退开关 | 推迟 | 2026-11-01 |
| 其他 | 托盘与开机启动；非 Studio 命名空间统一 | 推迟 | 2026-10-22 |

## 待维护者决定

| 编号 | 事项 |
|---|---|
| D-P8 | `docs/reference/capabilities.md` 是否为预发布增加条目（发布命令会打印 WARNING） |
| D-P9 | HMI 审查遗留的 D2、D5、D7；B2、B9 是否已随 HMI-B3 完成 |
| D-P13 | 长操作（编译、下载、导入、导出）的 MCP 进度上报经 PLC worker 通道实现（Q3 发现 `WorkerClient` 未实现进度接口），排 beta.4 还是 beta.5 |
| D-Q11 | 代码精简计划其余批次的批准与排期：Q6c、Q11（worker 回复携带 V4 结果字段）、Q12a–e（一个 ResultMapper）、Q13（重复源码合并）、Q14（SafeXml 与路径策略）、Q15（命名）、Q16（开发文档从源生成） |
| D-J1 | P8-31j1（生成框架方案的修订与示例）在 4.0 完成，还是随 P8-31f–i 进入 4.1 |
| D-D4 | `docs/releases/v3.2.0.md`、`v3.3.0.md` 保留与否 |
| D-D6 | 4.1 是否把生成的工具矩阵并入版本工具目录 |

已决定（2026-11-01）：

- D-P1：4.0 正式版门槛加“没有未决的 blocker 报告”，已补记到 ADR 0023（DOCS-1）。
- D-P6：`transport-usage-v18` 每次应答 15 秒的预算不变；SLIM-1 删除 V14 SP1–V19 的 transport-usage 作业后不再适用。
- D-P7：`ReleaseUnifiedReadCursor` 移出只读批处理，随 DES-3。
- D-P10：八版 worker 改为 MTA（MTA-1，beta.5）；Microsoft.Extensions 等改用稳定版（DEP-1）；MCP C# SDK 在 4.0 保持 0.3.0-preview.4，4.1 迁到 2.x。
- D-P12：新任务用主题编号，已有的 P8 编号不变。
