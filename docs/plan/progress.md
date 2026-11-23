# 开发进度

状态：现行 · 最后核对：2026-11-23，master `b9f2f597e`；v4.0.0-rc.1 已发布，rc.2 的任务除 REV-AUTH（主机门禁中）外均已合并

本页记录当前 worktree 基线包含的实现和测试者反馈，范围、顺序与放行门槛见[开发计划](plan.md)。本页的集成记录不证明已发布或已通过真机验收。

## 当前状态

- **2026-11-22，4.0.0-rc.1 已发布**（提交 `149ab2e4c`，GitHub 预发布，不设 latest）。发布前修了 RDY-RESTART（无 TIA 时 V20/V21 重启 worker 先于审批拒绝，与另外六版一致）。
  rc.1 的公开已获维护者确认，之后的 rc 各自确认。GitHub latest 仍为 v3.3.0。
- **2026-11-22，GitHub 仓库改名为 `asckye/automation-engineering-workbench`。** 旧网址经 GitHub 重定向，旧名永不再用，
  因为 3.3 和 beta 更新器经重定向读取发布（[ADR 0039](../standards/decisions/0039-repository-rename-never-reuse-old-name.md)）。
- **rc.2 准备中，目标 2026-11-24 至 11-26**，只在 rc.1 反馈的修复合并后发布；LOW 审查发现在同一个 rc 中修复。任务与状态见[开发计划](plan.md#400-rc2)。
  - rc.1 之后已合并：LOW-A（`ManageCfcChartProtection` 八版不再注册，[ADR 0041](../standards/decisions/0041-unregister-cfc-chart-protection.md)）、CHECK-FP、GATE-HANG、RC2-LOW（会启动 worker 的工具在无 TIA 时审批前拒绝，[ADR 0040](../standards/decisions/0040-readiness-before-approval.md)）、LOW-TOOLS、VMFIX-20B（含更新检查限流改报 `RESOURCE_UNAVAILABLE`）、RENAME-GH、LOW-RC2C、VMFIX-20A（`ExportDeviceAml` 输出根外与 OPC UA 导出给出确定结果、V14 SP1 读回对照 UDT 声明、审批宿主读取计划）、LOW-RC2D、文档与 ADR 更新（`16f764d6c`）、VMFIX-21（V15.1 目录导入批次汇总）、REV-REL、REV-DESK、REV-SEC、VMFIX-22（V15.1 批次 C、D 的 LOW）。
  - 2026-11-22 全面审查的修复：REV-SEC 宿主拒绝通配与公网绑定地址、AI 调用拒绝网络与设备输入路径（S8）、可备份目标备份失败或读不到状态时拒绝覆盖导入、审计开始记录写不进时拒绝写入、审批卡转义控制与双向字符、派发后异常报未知、用户 XML 禁用 DTD；REV-DESK 审批防误点、第二实例检测、设置原子写入且损坏时 AI 控制关闭、崩溃日志与诊断编号、诊断包隐私、更新器替换前核对暂存包；REV-REL `prepare` 须有同一提交的 `merge-check -All` 通过记录、golden 重新生成不能通过门禁、工作流动作固定、公开时报告 CI 状态。
  - 主机门禁中：REV-AUTH（带安全密码的编译须审批、HTTP 审计记录客户端远程地址、审查小修），计入 rc.2。
  - 移到 4.1：更新器整目录切换、配置页后台检测、审批摘要绑定真实参数值并在卡片上显示未遮蔽的值、只读监视计划接受变量表全局变量、批处理短 PLC 名别名（B099）、通用大用法分页规则、Engine harness 完整形状模式。
- **2026-11-23 维护者按建议批准全面审查的决定**：MCP 连接继续不设密码或访问密钥（[ADR 0038](../standards/decisions/0038-keyless-http-transport.md)）；
  工具参数中的密码与更新包不签名是 4.0 接受的风险（[ADR 0042](../standards/decisions/0042-accepted-risks-for-4-0.md)）；审批开关范围不变；
  带安全密码的编译须审批。详见[开发计划](plan.md#待维护者决定)。
- **4.0 正式版之前还剩**：REV-AUTH 合并与 rc.2 发布；V16、V18、V19、V20 在 rc.2 上复验，rc.2 对 V14 SP1、V15.1 的修复也待真机复验；崩溃探查（V21 OPC UA 报警类型 nodeId、V16–V20 探测、V21 交叉引用带界面检查，需维护者带界面操作）；11 条 ADR 补写；计划中标为待核对的 4.0 前项（T2、T3、U3、U4、C2、S4、S9、COV-V21）；最后一个 rc 经验证后定稿 [4.0 发布说明](../releases/v4.0.0.md)并由维护者确认。
- **RC 代码已全部合并（2026-11-19 至 11-21）。**
  - UNIFY-1 全部完成：A–D1（beta.6）以及 B2、C2、D2、E。八版共用一个工具包装、一条就绪规则、一个版本目录和一张官方 API 能力表，
    新出现的按版本号分支由 `ReleaseKeyedSitesTests` 拦下。U3 线程模型（MTA-1）、U6 实机保护、U7 V21 builder 输出按 2026-11-04 决定保留。
  - 真机回归修复：VMFIX-12a–f、VMFIX-13a/b、VMFIX-14A–C、VMFIX-15、VMFIX-16、VMFIX-17A–D、VMFIX-18A–D、VMFIX-19。
    主线是结果确定的写入不再报 `OUTCOME_UNKNOWN`；TIA 崩溃后在同一会话恢复；V14 SP1 SCL 移动与导入受保护；V16 告警 XLSX 导出下架；
    VMFIX-18D 从 VM 记录生成[真机工具状态表](../reference/real-machine-status.md)。
  - 收件箱 LOW 分批处理：LOW-B（拒绝措辞、结果映射、脱敏）、LOW-C（HMI、SiVArc、库）、LOW-D（硬件、在线、版本控制、工程生命周期）、
    LOW-E（发布工具、测试卫生、中英文发布说明）、LOW-F（PLC 软件、工艺对象、告警、LAD）。LOW-A 于 rc.1 之后合并。
  - ADR 0035：工作台改为原生 IDE 布局，去掉 HTTP 访问密钥和凭据保管库。ADR 0036 与 UI-0036A–GC：工作台按最终设计稿重做，审批规则不变。
  - XREF-1：V21 默认允许原生 PLC 与 Unified 交叉引用，去掉人工开关与同意记录。CRASH-PROBE：隐藏的崩溃探测（SiVArc、V16 母版副本、告警导出），
    V17–V20 拒绝 SiVArc provider 反射。AUDIT-DOC：按 2026-11-20 API 审计修正版本范围并记录有意不开放的 API。
  - CI-FIX：GitHub validate-bundle 恢复（PLC 模式输入 LF、SDK 用例只在主机跑）。REL-GA（正式版包名与 latest 规则，3.3 更新器不匹配 4.0 正式包；
    README/NOTICE 写明不携带 Siemens 程序集）已合并（`a5045cecb`），随 rc.1 发布。
- **八版 RC 候选全量真机回归已完成（2026-11-19 至 11-21）**，逐版结果见下方“真机结果”，逐工具状态见[真机工具状态表](../reference/real-machine-status.md)。
  VMFIX-14 至 19 修的是这些回归的发现；修复后的复验在 V17（rc2）和 V21（rc3）做过，rc.1 已全量复验 V14 SP1 和 V15.1，V16、V18–V20 尚未复验。

- 2026-11-17 的 beta.5 V21 补测覆盖 142 个工具，确认 79 个 beta.4 问题已修复。余下 5 个高级和 7 个中级问题归入 VMFIX-12。

- **2026-11-13 维护者决定：新功能移出 beta.5。** beta.5 只做 beta.4 真机回归修复、NO-LOCK、DES-4R2/R3 和合并后的整理；API-P1a–d、TIA-TEST、TIA-SAFE、TIA-BATCH、TIA-GAPS、ECO-S、SELF 共十项移到 beta.6，4.0 正式版仍以它们完成为条件，见[开发计划](plan.md)。

- **2026-11-11 恢复开发，2026-11-13 核对基线。** beta.4 V21 回归的修复主体已合并（VMFIX-6a、7A、7B、8、8B、9A、9B、10A、10B、10C、EXAMPLES-B4）；归属表在维护者本机 `bin-build/refactor/tasks/VM-B4-MAP.md`。beta.5 剩余工作逐条核实后列在[开发计划](plan.md#400-beta5)，判据为任务说明点名的源文件与测试是否存在并具备所述行为；提交信息不带任务编号，不能据此判断。
- 最近已发布版本是 [`4.0.0-rc.1`](../releases/v4.0.0-rc.1.md)（2026-11-22，`149ab2e4c`）。此前的 [`4.0.0-beta.5`](../releases/v4.0.0-beta.5.md) 于 2026-11-17 发布为预发布（`52d2d417`，安装包 SHA-256 `697ba5188daeee762511012ca4da2ad65ecf6eb9ec7a17a48fbc3a2d1e88821f`），GitHub latest 仍为正式版 [v3.3.0](../releases/v3.3.0.md)。预发布不设为 latest，3.3 安装不提示升级。发布前 `merge-check -All` 通过（116 个作业），`prepare` 402 秒，`publish-prepared` 6.5 秒。
- beta.4 已集成默认写入预览、批次与安全移块、回滚日志、参数模型、会话恢复、原生原因、输出根审批、确认词、relay 与工作台页面重绘。所有这些变化仍待真实 TIA Portal 复验。
- `prepare` 构建、打包、检查 ZIP 并上传私有草稿，完整候选准备已含新鲜依赖漏洞扫描。扫描接入前的构建打包实测约 6 分钟，不能作为含扫描的完整耗时。`publish-prepared` 公开封存候选，300 秒硬预算，beta.3 实测 6.9 秒。beta.4 的构建、打包和检查实测 347 秒；上传受本机网络影响，经本机代理约 4–54 KB/s，绕过代理约 148 KB/s（约 20 分钟），beta.3 约 350 KB/s（8 分 10 秒）。上传没有进度显示和超时，待修。
- 合并门为 `merge-check`，含 38 个注册套件与两组 full 传输检查。FAST-GATE 目标约 10 分钟；2026-11-05 宽范围改动实测 731 秒（约 12 分钟），优化前约 21 分钟。beta.4 候选的 `merge-check -All` 实测 779 秒（约 13 分钟），数量门槛保留。没有 nightly，GitHub CI 不阻塞合并。
- 架构仍为八版 FoundationHost，每个 MCP 会话一个 PLC worker。V20/V21 使用 MTA，V14 SP1–V19 使用 STA，MTA-1 在单独分支等待八版 VM 冒烟。工具列表默认 lite；UNIFY-1 A–D1 的准入、会话、状态和披露已集成。
- 名称为自动化工程工作台，现有程序、插件和服务标识继续使用。GitHub 仓库已于 2026-11-22 改名为 `asckye/automation-engineering-workbench`。

机器地址、PID 和运行状态须从当前环境确认，本页不据历史记录推断。

## 当前基线中的工作

| 任务 | 代码状态 | 后续 |
|---|---|---|
| PREP-SLIM、CI-FIX/2、TRANSPORT-FAST/KEEP | 已集成 `ccc8f7aa`、`d1a7b042`、`67ca6816`、`3a01f49c`、`e702434f`、`efc58105` | 发布前由主机检查 |
| RELAY-1、UI-INT、UI-2c、P8-84 | 已集成 relay、重绘页面、审批列表和精确 PID 选择，含 `ccf9b263` 修订 | 待真机复验 |
| OUTROOT-1、APR-05 | 已集成 `9707cd07`、`76ee906f`、`0b08a09b`，根外写文件审批及关闭审批确认词 | 待真实客户端与工作台复验 |
| N1-PORT | 已集成 `fb3bb356`、`89ef43c4`、`1a4add6d`，八版默认 lite、同一目录呈现 | 不代表原生行为已统一 |
| VMFIX-3-PORT、VMFIX-4、VMFIX-5a/5b | 已集成 `8fe022bd`、`76c51b5a`、`8757f27a`、`f902c069`、`a31afbe0`、`93eeb016` | 验证确定失败、未知结果、读排空、绑定与 V21 崩溃保护 |
| DES-PORT A/B/C、DES-PORT-FIX | 已集成 `e32b0d76`、`61bb78a0`、`1012d0b7`、`fd757a65`，批次、27 个默认预览、回滚和输入内容哈希 | 待真实预览/apply、失败恢复与重启复验 |
| DES-4 Part 1 | 已集成至 `13eb7725`，参数别名、会话默认目录、无根组合器的 schema | Parts 2、3 推迟到 beta.5 |
| REVIEW-PORT、SCORE-PORT | 已集成 `16567017`、`5c433428`、`f7465f45`、`90b243e1` | 评分分档未批准，首份评分的来源、统计口径待修订 |
| HANG-GUARD、FAST-GATE、TREND-ALERT | 已集成 `bc860ac7`、`1c4031b6`、`47437168`、`3c7a1a4b`，GUI 输入先独占，其余分片随后并行 | 保留所有功能和数量门槛，后续看主机趋势 |
| CI-FIX4 | 已集成 `1d8eb834`、`327c3d82`、`6e4cc3a0`，会话导出句柄隔离、worker 重启清理、请求版本策略 | 多会话和 worker 重启仍待真机确认 |
| APPROVAL-GATE | 已集成 `48abffe8`，打包审批检查换用能走到审批的写工具，新增无 Portal 时 `CreateDevice` 先被拒绝的检查 | 待真机复验 |
| NO-LOCK-1 | 已集成至 `20a8fcc7`，去掉会话锁：结果不明的调用记入未确认清单，后续独立调用不再被拒绝，ADR 0034 | 第 4、6、7、8 部分已于 2026-11-14 至 11-15 合并（至 `26f8b9cd`） |
| NO-LOCK-2 | 已集成 `3295dad1`–`1dd4df49`，超时不再判故障或结束 worker：回答执行中，`GetCallResult` 取迟到结果，执行中拒绝重启和断开，同一写入未读前拒绝重发 | 待真机复验 |
| MEMBER-COMMENT | 已集成 `ace1ce4c`–`7d9886b3`，`PatchPlcBlockDocument` 的 `setMemberComment` 为 DB、FB/FC 接口和 UDT 成员加注释，八版 | UDT 无校验导入；待真机复验 |
| NO-LOCK-3 | 已集成至 `7cfe4acb`，租约改为记录并带上一任未确认请求，断开后可在同一会话以新代次重连，去掉 `RequiresTiaRestart` 等需重启字段，新增 `PREVIOUS_OWNER_UNCLEAN` 警告 | 待真机复验 |
| NO-LOCK-5 | 已集成至 `cc6e6d2e`，TIA 对话框随执行中答复报告，只应答仅「确定」的提示框，只读读取窗口标题，编译、下载、导入导出、归档与库更新预算 300 秒 | 待真机复验 |
| DES-4R2 | 已集成 `46d02fd4`–`e356c135`，参数错误用 RFC 6901 指针定位字段，新增 `details.expected`，`CompareProjects.kind` 与 `ExportUnifiedEngineeringList.category` 声明枚举；未声明的调用方键名在八版所有路径上都不回显 | 八版契约基线已重新捕获；待真机复验 |
| VMFIX-6a、7A、7B、8、8B、9A、9B、10A、10B、10C | 已集成，beta.4 V21 回归的确定结果误报、Unified 绑定与模板、可选组件加载、实时监视与删除确认、Classic HMI 绝对路径等 | 2026-11-13 对照 master 核实；待真机复验 |
| EXAMPLES-B4、GUI-FLAKE | 已集成，工具示例改用 V21 实测参数并统一预览形式；GUI 测试左键状态读取稳定化 | 示例只对 V21 标注实测 |

## 2026-11-04、2026-11-05 决定与未完成项

完整决定见[计划中的实施决定](plan.md#2026-11-042026-11-05-实施决定)。

- S3 更新签名未完成，维护者接受同一发布摘要无法抵御 GitHub 账户被控制的风险（2026-11-23 记入 [ADR 0042](../standards/decisions/0042-accepted-risks-for-4-0.md)）。S5 未实现加密，维护者接受 LAN 明文 HTTP 暴露工程数据与 PLC 密码的风险，仅在可信网络使用；访问密钥已于 2026-11-19 移除，MCP 连接不设密钥（[ADR 0038](../standards/decisions/0038-keyless-http-transport.md)）。两项移出 4.0 前实施清单，见[安全标准](../standards/security.md)。
- S8 D1 A、D2 已定，AI 调用将拒绝网络路径，含 localhost 和 127.0.0.1 的网络路径，Workbench 人工操作不受此限制。由 REV-SEC 实现，已合并（`31c3b9f69`），随 rc.2。T3 D1 保留并解释 `rollbackCompleted`，D2 迁移 bridge 路由测试，顺序为 U4/C2 → T2 → T3，S9/U3/S8 可独立进行。
- VMFIX-6 D1–D5 已批准，纯 ASCII SCL 无 BOM，非 ASCII 保留 BOM 与导入限制说明，词过滤从变量名移到 options，停止迁入旧审批关闭设置，参数细节交 DES-4 Part 2，CreateDevice 确定拒绝的 not-started 分类保留。6a 排队，6b 跟随 Part 2。
- DES-4 D1–D6 已批准，别名保留至 5.0，规范导出参数和数组 devicePath，项目名与文件身份分开，默认会话目录，空串改动限于改名参数。Part 1 已集成。参数 JSON pointer 与原生 `innererror` 分属 Parts 2、3，2026-11-05 移到 beta.5。
- UNIFY-1 仍是 4.0 条件，替代 Q6a/Q6b/P8-04n2 并吸收 C9。U1 解除无 API 依据的工艺导出限制，U2 统一无 TIA 与能力准入，U4 保留需实测的 s7res 配对并移除无依据的 None-only 限制，U5 开放旧版 seed HMI，U8 按能力选择生成指纹并兼容历史记录。U3 线程模型留待实机，U6 实机保护保留，U7 V21 builder 保持到 4.1。A–D1 已集成，其余排 RC。
- VMAUTO-1 已取消，先前自动回归方案失效。beta.4 公开后另开 AI 会话，书面指定 VM、工程副本、覆盖与通过规则，只在副本写入，原始结果固定格式保存在 `bin-build/vm-test/<run>/`。归档分支保留，Campaign.Alive 缺陷未修复，正式回归尚未开始。
- FAST-GATE D1/D2/D4 批准，D3 仅在 Part 2 后仍超 10 分钟时考虑，D5–D8 暂缓。HANG-GUARD 批准的 151 处等待、速度断言与预算调整已集成，WriteGuard 15/20 秒保持。传输每组最多八版，WinError 10055 后共享标记限制为四版。TREND-ALERT 超近期中位数两倍只提示，至少五条历史、最近至多二十条，坏数据与存储失败不改退出码，prepare 显示最近提示。
- TIDY-7、HOST-RENAME、LONG-LINE-GATE 移到 beta.5 开头，等 DES-4 Parts 2、3 合并，因为未完成分支无法可靠跟随文件拆分和改名。仅 High/Medium 审查发现触发返工，Low 合并后排期并在影响用户时披露（2026-11-22 起改为 Low 在同一个 rc 中修复）。超过三轮审查先停下复核设计，告知维护者。

## 真机结果

| 测试 | 结果 | 处理状态 |
|---|---|---|
| v4.0.0-beta.2，V21 VM，2026-10-31 至 11-01 | 原记录称 511 个工具：通过 213、失败 93、未测 192、按设计拒绝 12；TIA 重启 4 次 | 分类合计为 510，与原总数相差 1，待台账核对；相关任务见计划 |
| beta.3 测试包（`1dfe2067`），V21 VM，2026-11-02 至 11-03 | 512 个工具：通过 293、缺陷 103、按设计拒绝 60、不确定 1、未测 55；去重缺陷 59 个（HIGH 20）；会话中断 18 次，TIA 重启 2 次 | R1 对应 VMFIX-2 已合并，但这不是修复后复测结果；其余修复与复测待执行 |
| v4.0.0-beta.4（`48abffe8`），V21 VM，2026-11-05 至 11-06 | **不通过**。839 次调用：通过 578、失败 158、按设计拒绝 103；512 个工具调用 511 个（`ConnectOnlinePlc` 只预览）；去重缺陷 87 个（HIGH 10、MEDIUM 25、LOW 52）；beta.3 的 59 个缺陷已修 15、有变化 12、仍在 32，其中 HIGH 20 个已修 8、有变化 9、仍在 3（B02、B11、B20）；会话中断 12 次，TIA 重启 2 次、宿主重启 2 次（均由维护者操作），TIA 未崩溃 | 只代表 V21，不代表另外七个版本；只读工具 `AnalyzePlcCrossReferences` 会锁会话；修复任务见计划 |
| RC 候选 `d89d0152`，V14 SP1 VM，2026-11-19 | 287 个工具：通过 157、只有按设计拒绝 58、HIGH 14、MEDIUM 5、LOW 52、未运行 1；会话中断与 TIA 重启 0 | HIGH 由 VMFIX-14 处理（SCL 移动与导入保护、结果确定误报未知等），待 rc.1 复验 |
| RC 候选 `d89d0152`，V15.1 VM，2026-11-19 | 328 个工具：通过 192、只有按设计拒绝 51、HIGH 14（均为结果确定却报未知）、MEDIUM 4、LOW 48、未运行 19；TIA 重启 0 | VMFIX-14A/B 处理，待 rc.1 复验 |
| RC 候选 `d89d0152`，V16 VM，2026-11-19 | 354 个注册工具中 349 个有记录；批次 A 通过 38、HIGH 8；批次 B 中 `ExportAlarmTextLists` 使 TIA 崩溃，崩溃后会话无法恢复，B2、C2、D 补跑 | VMFIX-15 下架 V16 告警 XLSX 导出、同会话恢复；VMFIX-16 拒绝 V16 PLC 块母版副本；待 rc.1 复验 |
| RC2 候选 `9a4274f2`，V17 VM，2026-11-20 | 377 个工具：通过 235、只有按设计拒绝 62、HIGH 3、MEDIUM 6、LOW 63；TIA 崩溃与会话中断 0；VMFIX-14–17 首次真机复验通过 | 余项由 VMFIX-18 处理 |
| RC 候选 `d89d0152`，V18 VM，2026-11-20 | 405 个工具：通过约 261，HIGH 约 12 类；TIA 崩溃 2 次（`ListSivarcBlockDefinitions`） | VMFIX-17B 仅在 V21 注册 SiVArc 块定义，CRASH-PROBE 拒绝 V17–V20 反射；待 rc.1 复验 |
| RC 候选 `d89d0152`，V19 VM，2026-11-20 | 481 个工具：通过 319、只有按设计拒绝 86、HIGH 8、MEDIUM 5、LOW 57、未运行 6；TIA 崩溃 1 次（按计划探测） | VMFIX-17/18 处理，待 rc.1 复验 |
| RC 候选 `d89d0152`，V20 VM，2026-11-20 | 504 个工具：通过 328、只有按设计拒绝 84、HIGH 10、MEDIUM 10、LOW 69、未运行 4；TIA 崩溃与会话中断 0 | `ImportDeviceAml` 独占访问、SiVArc 容器、重名导入误报未知由 VMFIX-17A/B 处理，待 rc.1 复验 |
| RC3 候选 `aa9c749b`，V21 VM，2026-11-21 | 519 个工具：通过 348、按设计拒绝 69、失败 91（HIGH 3，其余 LOW）、未运行 11；TIA 崩溃 2 次；beta.6 的 V21 HIGH/MEDIUM 除 `ConnectProjectToWorkspace`（未复验）外均已修复 | 标签导入误报未知、PLC syslog 崩溃由 VMFIX-19 处理；OPC UA 报警类型 nodeId 更新崩溃待探查 |
| rc.1（`149ab2e4c`），V14 SP1 VM，2026-11-22 | 287 个工具：通过 177、只有按设计拒绝 64、HIGH 2、MEDIUM 0、LOW 42、未运行 2；TIA 重启与会话中断 0。RC 候选的 14 个 HIGH 修好 13 个（`ImportPlcBlockVerified` 行为改变但仍是 HIGH），5 个 MEDIUM 全部修好。批次 B、C 的变慢经调查为测试时主机负载，不是代码退化 | 2 个 HIGH（`ExportDeviceAml` 输出根外报未知、`ImportPlcBlockVerified` 读回误报不一致）由 VMFIX-20A 修复，待 rc.2 复验；LOW 由 VMFIX-20B 等处理 |
| rc.1（`149ab2e4c`），V15.1 VM，四批，2026-11-22 至 11-23 | 328 个工具：通过 224、只有按设计拒绝 55、HIGH 3、MEDIUM 0、LOW 42、未运行 4；TIA 重启与会话中断 0。RC 候选的 14 个 HIGH、4 个 MEDIUM 全部修好或不再出现。新 HIGH 3 个：`ManageHardwareUtilities` `exportOpcUa` 确定失败报未知；`ImportHmiScreensFromDirectory` 批次汇总报未知；`ImportPlcBlockVerified` 读回误报不一致（与 V14 SP1 相同）。批次 A 前半段开着审批，写入被拦下，关审批后补跑，表中以补跑为准；测试期间主机负载高，原生调用变慢不算缺陷 | `exportOpcUa`、`ImportPlcBlockVerified` 由 VMFIX-20A 修复，`ImportHmiScreensFromDirectory` 由 VMFIX-21 修复，LOW 由 VMFIX-20A/20B 与 VMFIX-22 处理；均待 rc.2 复验 |

原始记录在维护者本机：beta.3 在 `bin-build/refactor/vm-test/`，尚待 FB-VM 登记；beta.4 在 `bin-build/vm-test/beta4-v21/`，含明文密码的原始返回，不入库，也不复制到夹具；RC 回归在 `bin-build/vm-test/rc-full-<版本>/`、`rc2-full-v17/`、`rc3-full-v21/`，rc.1 复验在 `rc1-full-v14sp1/`、`rc1-full-v15.1/`，每批一份汇总。V16、V18、V19、V20 尚未在 rc.1 上复验。
RC 回归中在线写入、下载和仿真执行只预览（VM 无 PLC，PLCSIM 无实例）。beta.4 回归的缺陷原因尚未逐条验证。未测的正常流程：`OpenProject`、`CloseProject`（自有工程）、`CreateProject`、`RetrieveProjectArchive`、`ConnectIsolatedPortal`、`PublishGenerationRecord`；在线、下载、上传、强制和仿真只读取或预览。调查列出的无任务缺陷和测试缺口已在计划中标为提议，不能当作已分配或已修复。离线检查不能证明原生行为；验收边界以[真机台账](../reference/real-machine-ledger.md)为准。

## 测试者反馈

预发布测试者的报告、维护者在真机上看到的现象和我们按[预发布真机验证清单](../guides/beta-tester-checklist.md)做的 VM 回归都先记在本节（[ADR 0020](../standards/decisions/0020-prereleases-tested-by-users.md)）。复核前不写进[真机验收台账](../reference/real-machine-ledger.md)：`generate-prerelease-notes` 把台账四节（`未发布改动的验收边界`、`V20/V21 host + worker acceptance (P6-54)`、`4.0 安全策略验收计划（P6-R2）`、`4.0 发布前仍需的真实机器步骤`）的表格行写进预发布说明并逐字节核对，`generate-catalogs` 统计台账的 `current | NOT RUN` 行。

处理流程：

1. 收到 Issue（[表单](../../.github/ISSUE_TEMPLATE)）后在下表加一行，编号 `FB-NNN` 顺延，状态“待复核”。
2. 核对包名和 SHA-256、TIA 版本和 Update、发布键、工具参数、完整结果信封和证据；缺项时改为“待补充”，在 Issue 中列出。
3. 定性：缺陷建任务并写明编号（“已确认”）；符合预期标“通过”；与已有行相同标“重复”并指向原编号；该版本无此能力或不是本项目问题标“不适用”。
4. 修复合并后写明提交和首个含修复的版本，状态“已修复，待复测”；复测通过后“已关闭”。
5. 满足台账[“新记录应包含什么”](../reference/real-machine-ledger.md#新记录应包含什么)的通过结果由维护者写入台账（不动生成块和上述四节，除非同时重新生成下一个预发布的说明），本行写台账位置；在 Issue 中回复结论后关闭。

| 编号 | 收到日期 | 来源 | 包和 TIA 版本 | 清单项 | 工具 | 现象 | 状态 | 处理与去向 |
|---|---|---|---|---|---|---|---|---|
| FB-001 | 2026-10-26 | 维护者真机测试（截图） | v4.0.0-beta.1；TIA 版本未记录 | 不适用 | `EnsureUnifiedHmiTagTable`、`EnsureUnifiedHmiTag` | 以 `/组/子组/名称` 给出的 Unified 变量表在根目录建成以完整路径为名的表 | 已修复，待复测 | P8-81e、P8-81-int2，v4.0.0-beta.2 发布说明 “Tester reports”；复测见清单 V19–V21 行 |
| FB-002 | 2026-10-26 前后 | 测试者的 AI 客户端 | v4.0.0-beta.1；TIA 版本未记录 | T12 | 未知（某个接受对象路径的 Unified 工具） | 删除预览中对象路径写成字符串数组而不是属性步骤数组，随后 TIA 进程退出 | 待补充 | v4.0.0-beta.2 起格式错误路径在任何 TIA 调用前拒绝（P8-81-int2）；根因未确认，等测试者提供工具名、版本、参数和崩溃证据 |
| FB-003 | 2026-10-26 前后 | 测试者报告 | v4.0.0-beta.1；V20/V21 | 不适用 | `SetUnifiedHmiButtonEventScriptCode` | V20/V21 上返回结果未知（绑定器参数异常） | 已修复，待复测 | P8-81-int2 合并的宿主修复，v4.0.0-beta.2 发布说明 “Tester reports” |

清单项结果（每项按版本记最新结论和 `FB` 编号）：

| 清单项 | 已收到结果 | 仍缺的版本 |
|---|---|---|
| T01、T02、T04–T09（每个版本） | 无 | 14sp1、15.1、16、17、18、19、20、21 |
| T03 编译仅更改或全部重建 | 无 | 14sp1、15.1、16、17、18、19、20、21 |
| T10 工作台项目页 | 无 | 14sp1、16、21 |
| T11 V21 原生交叉引用 | 无 | 21 |
| T12 删除预览格式错误路径 | 无 | 19、20、21 |
| T13 HMI 面板容器 | 无 | 19、20、21 |

## 接续开发先读

1. [CLAUDE.md](../../CLAUDE.md) 和 [AGENTS.md](../../AGENTS.md)：分工、共用规则和合并规则。
2. [标准说明](../standards/README.md)：采用的规范范围和项目偏差。
3. [开发计划](plan.md)：各版范围、任务状态、放行门槛和待决定事项。
4. [验证](../manuals/validation.md)和[发布流程](../manuals/release.md)：检查分层、合并门和发布步骤。

修改工具示例后依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`，捕获工具定义契约前先构建；生成器都有逐字节检查模式，生成的哈希只由正式工具写入。发布后在本页记下版本、标签、候选摘要与验证结果；新发布路径不再生成受跟踪的 publication 构建记录。构建或离线结果都不授权连接 TIA、PLC、VM 或外部服务。
