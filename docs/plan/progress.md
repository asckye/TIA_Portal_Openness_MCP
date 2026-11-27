# 开发进度

状态：现行 · 最后核对：2026-11-27，master `b1d48f670`；v4.0.1 已发布（2026-11-27，修复版，GitHub latest）

本页记录当前 worktree 基线包含的实现和测试者反馈，范围、顺序与放行门槛见[开发计划](plan.md)。本页的集成记录不证明已发布或已通过真机验收。

## 当前状态

- **v4.0.1 已于 2026-11-27 发布（`b1d48f670`，修复版，GitHub latest，不向 3.3 更新器提供）。** 发布前 `merge-check -All` 通过（1477 秒），`prepare` 383 秒，`publish-prepared` 8.1 秒。 FIX-401 与 CI-FIX 已合并到 master `1e0a99c56`：V16–V19 同步后的建议按比较结果给出，中断时报告错误、比较未完成时建议重试；V16–V21 工作区根路径或映射路径无效时状态照常报告映射；八版验证导入在证据路径会达到 260 个字符时提前拒绝。GitHub CI 的 offline-checks、validate-bundle 和 vulnerability-scan 在该提交上全部通过。修复只经过离线测试和完整门禁，不在虚拟机上重跑。[发布说明](../releases/v4.0.1.md)，任务见[开发计划](plan.md#401)。
- **v4.0.0 已于 2026-11-26 发布（`906a06bac`，正式版，GitHub latest，不向 3.3 更新器提供）。** rc.8 真机复验（2026-11-26，八个版本）HIGH 0、没有新的 MEDIUM，rc.7 的 HIGH 和 MEDIUM 确认修复。复验的 LOW 由 GA-LOWS 修复：设备创建预览只回显传入的 `family`，同步汇总按未验证原因给建议（不一致的块提示编译），`minItems` 错误写出参数名和数量，连接报告复用的已有文件并单独计数 `reused`，证书提示包括 V21，状态点名断开的映射和没有映射的工作区文件。工具名称和输入没有变化，连接结果多一个 `reused` 计数，部分消息文字改变。GA-LOWS 只经过离线测试和 V17 复验数据的回放，不在真机上重跑。[4.0 发布说明](../releases/v4.0.0.md)已定稿，待维护者确认。任务见[开发计划](plan.md#400)。
- **v4.0.0-rc.8 已于 2026-11-26 发布（`039436e36`，GitHub 预发布，不设为 latest）。** rc.7 真机复验的修复和审查积累的 LOW 问题已合并到 master `8f58c6c0e`：连接工作区在所有版本上都不覆盖内容不同的已有文件，修复 V18/V19 的 HIGH；V20/V21 SD 覆盖导入不再因 `LibraryConformanceStatus` 误报属性丢失，修复 MEDIUM，并拒绝覆盖库类型实例（VMFIX-35）；RC8-A 至 RC8-E 处理宿主、PLC 软件、硬件、HMI、库、安全、工作台和版本控制的 LOW 问题及维护者决定 D1–D16，RC8-F、RC8-G 修复合并门失败；COV-V21 已完成。工具名称和输入没有变化，部分工具和参数的说明改变，`MonitorPlcWatchTableS7`、`ManagePlcTestLibrary` 等几处错误码和结果改变。2026-11-26 已在八个版本的真机上复验（见“真机结果”）。任务见[开发计划](plan.md#400-rc8)。
- **v4.0.0-rc.7 已于 2026-11-25 发布（`7431033b7`，GitHub 预发布，不设为 latest）。** rc.6 真机复验的修复已合并到 master `e7343a592`：V20/V21 SIMATIC SD 覆盖导入的核对（SD-OVERRIDE，修复 rc.6 的 HIGH）与工作台 SD 还原检查（SD-RESTORE）、V16–V19 版本控制状态刷新与拒绝原因（VCI-STATUS）、存储 Equal 以内容证明后才跳过（VCI-PROOF）、V16/V17 先写文件再建映射（VCI-EXPORT）、空元素两种写法（VMFIX-34）、工具返回与写保护钩子的凭据遮蔽（S9）、生成失败的编译器文字（LOW-GENERR）、设备创建与只读属性的拒绝原因（LOW-DEVREASON），另在 4.1 计划中加入十个候选项。工具名称和输入没有变化，V16–V21 的版本控制工具和 V20/V21 的 SD 文档工具说明改变，`CreateDevice` 面板版本不兼容的错误码改变。2026-11-25 至 11-26 已在八个版本的真机上定向复验（见“真机结果”）。任务见[开发计划](plan.md#400-rc7)。
- **v4.0.0-rc.6 已于 2026-11-25 发布（`e85fa9910`，GitHub 预发布，不设为 latest）。** rc.5 真机复验的修复（VMFIX-31 至 VMFIX-33）和工作台应用图标（UI-APPICON）已合并到 master `41efb7fe3`。工具名称和输入没有变化，`SetDeviceAddress.startAddress` 的说明改变，部分结果和错误码改变。2026-11-25 已在八个版本的真机上定向复验（见“真机结果”）。任务见[开发计划](plan.md#400-rc6)。
- **v4.0.0-rc.5 已于 2026-11-25 发布（`9862ec67c`，GitHub 预发布，不设为 latest）。** rc.4 真机复验的修复（VMFIX-26 至 VMFIX-30）、工具示例（EXAMPLES-RC4）和结果判定更正（UNK-B、UNK-A）已合并到 master `991b3c334`。工具名称和输入没有变化，只有 `ManageMultiuserSession` 的 `port` 说明和两个工具的示例注解改变。2026-11-25 已在八个版本的真机上定向复验（见“真机结果”）。任务见[开发计划](plan.md#400-rc5)。
- **v4.0.0-rc.4 已于 2026-11-24 发布（`960883603`，GitHub 预发布，不设为 latest）。** 按最终设计稿改版的工作台已合并到 master `372453e06`：UI-FINAL（外壳与七个页面）、UI-AIHINT（AI 通知卡片）、
  UI-CFGFIX、UI-RAILFIX（模态框关闭后恢复原有可用状态）和 UI-LOWS（审查发现的修复）。MCP 对外接口与 rc.3 相同。2026-11-24 已在八个版本的真机上定向复验，工作台尚未逐页手动走查。
  任务见[开发计划](plan.md#400-rc4)。
- **2026-11-22，4.0.0-rc.1 已发布**（提交 `149ab2e4c`，GitHub 预发布，不设 latest）。发布前修了 RDY-RESTART（无 TIA 时 V20/V21 重启 worker 先于审批拒绝，与另外六版一致）。
  rc.1 的公开已获维护者确认，之后的 rc 各自确认。GitHub latest 仍为 v3.3.0。
- **2026-11-22，GitHub 仓库改名为 `asckye/automation-engineering-workbench`。** 旧网址经 GitHub 重定向，旧名永不再用，
  因为 3.3 和 beta 更新器经重定向读取发布（[ADR 0039](../standards/decisions/0039-repository-rename-never-reuse-old-name.md)）。
- **v4.0.0-rc.3 已于 2026-11-23 发布（`e26a3249f`，GitHub 预发布，不设为 latest）。** rc.2 在 V14 SP1、V15.1 真机定向复验中各有 2 个 HIGH（带 UDT 成员的 DB 验证导入报读回不一致；带口令的编译在无该 API 的版本上报结果未知），修复（VMFIX-23、VMFIX-24）与工作台界面修复（UI-CENTER、UI-POLISH）已合并，rc.4 真机复验：不支持密码编译的版本已确认修复，验证导入部分修复（见“真机结果”）。任务见[开发计划](plan.md#400-rc3)。
- **v4.0.0-rc.2 已于 2026-11-23 发布（`bb154034d`，GitHub 预发布，不设为 latest）。** 完整门禁 `merge-check -All` 与 `prepare` 在该提交上通过；rc.1 反馈的修复和 LOW 审查发现都在这个 rc 中。任务与状态见[开发计划](plan.md#400-rc2)。
  - rc.1 之后已合并：LOW-A（`ManageCfcChartProtection` 八版不再注册，[ADR 0041](../standards/decisions/0041-unregister-cfc-chart-protection.md)）、CHECK-FP、GATE-HANG、RC2-LOW（会启动 worker 的工具在无 TIA 时审批前拒绝，[ADR 0040](../standards/decisions/0040-readiness-before-approval.md)）、LOW-TOOLS、VMFIX-20B（含更新检查限流改报 `RESOURCE_UNAVAILABLE`）、RENAME-GH、LOW-RC2C、VMFIX-20A（`ExportDeviceAml` 输出根外与 OPC UA 导出给出确定结果、V14 SP1 读回对照 UDT 声明、审批宿主读取计划）、LOW-RC2D、文档与 ADR 更新（`16f764d6c`）、VMFIX-21（V15.1 目录导入批次汇总）、REV-REL、REV-DESK、REV-SEC、VMFIX-22（V15.1 批次 C、D 的 LOW）。
  - 2026-11-22 全面审查的修复：REV-SEC 宿主拒绝通配与公网绑定地址、AI 调用拒绝网络与设备输入路径（S8）、可备份目标备份失败或读不到状态时拒绝覆盖导入、审计开始记录写不进时拒绝写入、审批卡转义控制与双向字符、派发后异常报未知、用户 XML 禁用 DTD；REV-DESK 审批防误点、第二实例检测、设置原子写入且损坏时 AI 控制关闭、崩溃日志与诊断编号、诊断包隐私、更新器替换前核对暂存包；REV-REL `prepare` 须有同一提交的 `merge-check -All` 通过记录、golden 重新生成不能通过门禁、工作流动作固定、公开时报告 CI 状态。
  - 最后合并：REV-AUTH（带安全密码的编译须审批、HTTP 审计记录客户端远程地址、审查小修）、REV-APPR（写入审批开关旁说明关闭后哪些操作不再确认、哪些始终确认）。
  - 移到 4.1：更新器整目录切换、配置页后台检测、审批摘要绑定真实参数值并在卡片上显示未遮蔽的值、只读监视计划接受变量表全局变量、批处理短 PLC 名别名（B099）、通用大用法分页规则、Engine harness 完整形状模式。
- **2026-11-23 维护者按建议批准全面审查的决定**：MCP 连接继续不设密码或访问密钥（[ADR 0038](../standards/decisions/0038-keyless-http-transport.md)）；
  工具参数中的密码与更新包不签名是 4.0 接受的风险（[ADR 0042](../standards/decisions/0042-accepted-risks-for-4-0.md)）；审批开关范围不变；
  带安全密码的编译须审批。详见[开发计划](plan.md#待维护者决定)。
- **4.0 正式版之前还剩**：见[开发计划](plan.md#400)；GA-LOWS 已合并（`ff72c1cf5`），完整 `prepare` 后直接发布（维护者 2026-11-26 同意）；维护者走查工作台、核对 S4，不阻塞发布。
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
| rc.4（`960883603`），V14 SP1 VM，2026-11-24，定向复验 | 94 次调用：通过 70、按设计拒绝 12、HIGH 2（1 个缺陷）、MEDIUM 0、LOW 10；没有重启 TIA。rc.3 修复：密码编译确认修复；验证导入部分修复，实例起始值不同于 UDT 声明时仍误报不一致 | VMFIX-29 已合并，待 rc.5 复验 |
| rc.4，V15.1 VM，2026-11-24，定向复验 | 111 次调用：通过 83、按设计拒绝 23、HIGH 1、MEDIUM 1、LOW 2、未运行 1；没有重启 TIA。rc.3 的两个 HIGH 修复均确认 | 离线修改的参数错误报未知、空标题误报由 VMFIX-26 修复，待 rc.5 复验 |
| rc.4，V16 VM，2026-11-24，定向复验 | 152 次调用：通过 92、按设计拒绝 45、HIGH 4（3 个缺陷）、MEDIUM 1、LOW 7、未运行 3；无崩溃。密码编译确认修复 | 验证导入由 VMFIX-29、`ExportDeviceAml` 由 VMFIX-28 修复，待 rc.5 复验；VCI 原生导出未解决 |
| rc.4，V17 VM，2026-11-24，定向复验 | 148 次调用：通过 99、按设计拒绝 29、HIGH 4（3 个缺陷）、MEDIUM 6、LOW 10；无崩溃 | 假安全密码编译报未知由 VMFIX-27，验证导入由 VMFIX-29，离线修改由 VMFIX-26 修复，待 rc.5 复验；VCI 原生导出和 HMI AML 导入未解决 |
| rc.4，V18 VM，2026-11-24，定向复验 | 199 次调用：通过 131、按设计拒绝 41、HIGH 8（3 个缺陷）、MEDIUM 5、LOW 14；无崩溃 | VMFIX-27 至 VMFIX-29 已合并，待 rc.5 复验 |
| rc.4，V19 VM，2026-11-24，定向复验 | 162 次调用：通过 109、按设计拒绝 27、HIGH 6（4 个缺陷）、MEDIUM 6、LOW 14；无崩溃 | VMFIX-26、VMFIX-27、VMFIX-29 已合并，待 rc.5 复验；VCI 状态与同步、TP700 拒绝和 HMI AML 导出未解决 |
| rc.4，V20 VM，2026-11-24，定向复验 | 177 次调用：通过 117、按设计拒绝 38、HIGH 8（3 个缺陷）、MEDIUM 10、LOW 4；无崩溃 | VMFIX-27、VMFIX-29、VMFIX-30 已合并，待 rc.5 复验；两个相同 UDT 实例的比较、TP700 拒绝和 HMI AML 导出未解决 |
| rc.4，V21 VM，2026-11-24，定向复验 | 169 次调用：通过 116、按设计拒绝 27、HIGH 7（2 个缺陷）、MEDIUM 6、LOW 13；无崩溃。syslog 崩溃确认修复 | VMFIX-27、VMFIX-29 已合并，待 rc.5 复验；两个相同 UDT 实例的比较和 HMI AML 导出未解决 |
| rc.5（`9862ec67c`），V14 SP1 VM，2026-11-25，定向复验 | 205 行：通过 173、按设计拒绝 21（其中 9 行是会话传参错误）、HIGH 1、MEDIUM 2、LOW 8；无崩溃。未改动的块和 DB 全部匹配，没有误判一致 | 编译报错报未知由 VMFIX-32 修复；最小 LAD FC 和部分展开 DB 的误报由 VMFIX-31 修复；待 rc.6 复验 |
| rc.5，V15.1 VM，2026-11-25，定向复验 | 241 行：通过 191、按设计拒绝 44（其中 22 行是会话传参错误）、失败 6；HIGH 0、MEDIUM 5 行（4 个问题，另 1 个已知）、LOW 5；无崩溃。6 个未改动的块匹配 | 点错成员和最小 LAD FC 由 VMFIX-31，拒绝的导入报未知和同 UDT 实例比较由 VMFIX-32 修复，待 rc.6 复验 |
| rc.5，V16 VM，2026-11-25，定向复验 | 242 行：通过 200、按设计拒绝 33、失败 8、未运行 1；HIGH 0、MEDIUM 3、LOW 6；无崩溃。5 个未改动的块匹配 | 最小 LAD FC 由 VMFIX-31，拒绝的导入报未知由 VMFIX-32，IO 地址原因和重名单元由 VMFIX-33 修复，待 rc.6 复验；VCI 原生导出未解决 |
| rc.5，V17 VM，2026-11-25，定向复验 | 170 行：通过 136、按设计拒绝 22、失败 12；HIGH 1、MEDIUM 8、LOW 3，另有 5 行按设计拒绝带 LOW；无崩溃。假密码编译的两个工具确认修复 | 编译报错报未知由 VMFIX-32，UDT 声明默认值误报由 VMFIX-31 修复，待 rc.6 复验；同步报未知受会话干扰，待复查 |
| rc.5，V18 VM，2026-11-25，定向复验 | 173 行：通过 139、按设计拒绝 26、失败 8；HIGH 1、MEDIUM 6、LOW 5；无崩溃。5 个未改动的块匹配；`CompilePlcDiagnostics` 的假密码编译没有跑 | VMFIX-31 至 VMFIX-33 已合并，待 rc.6 复验；VCI 状态全部未知未解决 |
| rc.5，V19 VM，2026-11-25，定向复验 | 174 行：通过 137、按设计拒绝 24、失败 11、未跑 2；HIGH 1、MEDIUM 9、LOW 5；无崩溃。5 个未改动的块匹配 | VMFIX-31 至 VMFIX-33 已合并，待 rc.6 复验；VCI 状态与同步、TP700 拒绝和 HMI AML 导出未解决 |
| rc.5，V20 VM，2026-11-25，定向复验 | 175 行：通过 140、按设计拒绝 28、失败 6、未跑 1；HIGH 1、MEDIUM 5、LOW 6；无崩溃。工作区全部拒绝报确定失败确认修复 | VMFIX-31 至 VMFIX-33 已合并，待 rc.6 复验；TP700 拒绝和 HMI AML 导出未解决；TIA 拒绝的验证导入没有跑 |
| rc.5，V21 VM，2026-11-25，定向复验 | 174 行：通过 143、按设计拒绝 23、失败 8；HIGH 1、MEDIUM 6、LOW 9；无崩溃。5 个未改动的块匹配 | VMFIX-31 至 VMFIX-33 已合并，待 rc.6 复验；HMI AML 导出未解决 |
| rc.6（`e85fa9910`），V14 SP1 VM，2026-11-25，定向复验 | 149 行：通过 137、失败 7、派发前拒绝 5（会话传参错误）；会话定级 HIGH 1、LOW 5，复核后该行记 MEDIUM；无崩溃。rc.5 的 HIGH 确认修复，VMFIX-31 九项中八项通过 | 空元素写法误报由 VMFIX-34 修复，待 rc.7 复验 |
| rc.6，V15.1 VM，2026-11-25，定向复验 | 147 行：通过 132、按设计拒绝 15、失败 0；HIGH 0、MEDIUM 0、LOW 1；无崩溃。TIA 拒绝的验证导入有证明 | 只读属性原因只在 `data.evidence` 中由 LOW-DEVREASON 修复，待 rc.7 复验 |
| rc.6，V16 VM，2026-11-25，定向复验 | 143 行：通过 125、按设计拒绝 17、失败 1；HIGH 0、MEDIUM 1（已知）、LOW 1；无崩溃。拒绝后导出与导入前相同 | VCI 首次导出由 VCI-EXPORT 修复，待 rc.7 复验 |
| rc.6，V17 VM，2026-11-25，定向复验 | 主批 142 行：通过 122、按设计拒绝 18、失败 2；HIGH 0、MEDIUM 2（已知）、LOW 2；另有 VCI 45 行，MEDIUM 2，结束时未决 1；无崩溃 | VCI 状态总是未知由 VCI-STATUS，首次导出由 VCI-EXPORT 修复，待 rc.7 复验 |
| rc.6，V18 VM，2026-11-25，定向复验 | 144 行：通过 121、按设计拒绝 18、失败 4、未完成 1；HIGH 0、MEDIUM 4（rc.5 已有）、LOW 2；无崩溃 | VCI 状态全部未知由 VCI-STATUS，TP700 拒绝无原因由 LOW-DEVREASON 修复，待 rc.7 复验 |
| rc.6，V19 VM，2026-11-25，定向复验 | 140 行：通过 117、按设计拒绝 17、失败 5、未跑 1；会话定级 HIGH 1（复核后记 MEDIUM）、MEDIUM 3、LOW 3；无崩溃 | 空元素写法误报由 VMFIX-34，VCI 状态由 VCI-STATUS，TP700 原因由 LOW-DEVREASON 修复，待 rc.7 复验 |
| rc.6，V20 VM，2026-11-25，定向复验 | 148 行：通过 120、按设计拒绝 22、失败 5、未跑 1；HIGH 2（SD 覆盖导入）、MEDIUM 2（已知）、LOW 4；无崩溃 | SD 覆盖导入由 SD-OVERRIDE 修复，待 rc.7 复验；SD 新建导入没有跑 |
| rc.6，V21 VM，2026-11-25，定向复验 | 148 行：通过 120、按设计拒绝 21、失败 7；HIGH 3（同一缺陷：SD 覆盖导入改动 OB 和 DB 编号与属性却报成功）、MEDIUM 2（已知）、LOW 4；无崩溃 | SD-OVERRIDE 已合并，待 rc.7 复验；SD 新建导入和 `ImportPlcBlocksDocuments` 没有跑 |
| rc.7（`7431033b7`），V14 SP1 VM，2026-11-26，定向复验 | 78 行：通过 72、按设计拒绝 1、失败 3、未跑 2（参数错误，派发前拒绝）；HIGH 0、MEDIUM 0、LOW 3；无崩溃，未决 0。VMFIX-34、LOW-GENERR、只读属性原因确认修复 | 空名称原因、`generatedObjects` 为空、嵌套 UDT 补丁段由 VMFIX-35 修复，待 rc.8 复验 |
| rc.7，V15.1 VM，2026-11-26，定向复验 | 66 行：通过 60、按设计拒绝 6、失败 0；HIGH 0、MEDIUM 0、LOW 2；无崩溃，未决 0。VMFIX-34、只读属性原因、LOW-GENERR 确认修复；TP700 可直接创建 | `objectsAfter` 为空、只读属性预览无警告由 VMFIX-35 修复，待 rc.8 复验 |
| rc.7，V16 VM，2026-11-26，定向复验 | 104 行：通过 99、按设计拒绝 4、失败 1；HIGH 0、MEDIUM 0、LOW 3；无崩溃，未决 0。VCI 检查通过：连接自行写出文件，内容不同的已有文件被拒且未被覆盖，一致的文件复用 | 工作区文件夹缺失的消息、预览比较计数等 LOW 由 VMFIX-35 修复，待 rc.8 复验 |
| rc.7，V17 VM，2026-11-26，定向复验 | 106 行：通过 99、按设计拒绝 5、失败 2；HIGH 0、MEDIUM 1（已知：证书）、LOW 4；无崩溃，未决 0。VCI 检查通过，已有文件规则通过 | `importOrder` 示例、预览 `wouldRefuse`、旧映射残留等 LOW 由 VMFIX-35 修复，待 rc.8 复验 |
| rc.7，V18 VM，2026-11-26，定向复验 | 99 行：通过 89、按设计拒绝 7、失败 3；HIGH 1（连接时覆盖内容不同的已有工作区文件）、MEDIUM 1（已知：证书）、LOW 3；无崩溃，未决 0。VCI 检查通过，没有过期 Equal | HIGH 由 VMFIX-35 修复，待 rc.8 复验；全新附加、TIA 外改删文件没有测 |
| rc.7，V19 VM，2026-11-26，定向复验 | 103 行：通过 93、按设计拒绝 7、失败 3；会话待定级 1 行，复核后记 HIGH（同 V18）、MEDIUM 1（已知：证书）、LOW 4；无崩溃，未决 0。VCI 检查通过，没有过期 Equal | HIGH 由 VMFIX-35 修复，待 rc.8 复验；全新附加、TIA 外改删文件没有测 |
| rc.7，V20 VM，2026-11-25，定向复验 | 100 行：通过 84、按设计拒绝 12、失败 4；HIGH 0、MEDIUM 3（2 个新：普通 FB/FC 的 SD 覆盖导入误报；1 个已知：证书）、LOW 2；无崩溃，未决 0。rc.6 的 SD 覆盖导入 HIGH 确认修复 | MEDIUM 由 VMFIX-35 修复，待 rc.8 复验；启动 OB 无法 SD 导出，没有测 |
| rc.7，V21 VM，2026-11-25，定向复验 | 108 行：通过 87、按设计拒绝 15、失败 6；HIGH 0、MEDIUM 5（3 行同一新缺陷：普通 FB/FC 的 SD 覆盖导入误报；2 个已知：HMI AML）、LOW 3；无崩溃，未决 0。rc.6 的 SD 覆盖导入 HIGH 确认修复 | MEDIUM 由 VMFIX-35 修复，待 rc.8 复验 |
| rc.8（`039436e36`），V14 SP1 VM，2026-11-26，冒烟检查 | 63 行：通过 61、按设计拒绝 2、失败 0；HIGH 0、MEDIUM 0、LOW 0；无崩溃，未决 0。rc.7 的 V14 SP1 LOW（`generatedObjects`、嵌套 UDT 示例、空名称原因）确认修复 | 无 |
| rc.8，V15.1 VM，2026-11-26，冒烟检查 | 48 行：通过 46、按设计拒绝 2、失败 0；HIGH 0、MEDIUM 0、LOW 0；无崩溃，未决 0。`objectsAfter`、只读属性预览确认修复 | 无 |
| rc.8，V16 VM，2026-11-26，冒烟检查 | 59 行：通过 55、按设计拒绝 4、失败 0；HIGH 0、MEDIUM 0、LOW 0；无崩溃，未决 0。连接已有文件规则通过：内容不同的文件写入前被拒、未改变、无映射 | 无 |
| rc.8，V17 VM，2026-11-26，冒烟检查 | 69 行：通过 62、按设计拒绝 6、失败 1；HIGH 0、MEDIUM 1（已知：证书）、LOW 1（新：重复映射的状态行都写成断开的状态）；无崩溃，未决 0。连接已有文件规则通过 | LOW 由 GA-LOWS 修复，不在真机上重跑 |
| rc.8，V18 VM，2026-11-26，定向复验 | 88 行：通过 78、按设计拒绝 8、失败 1、未跑 1（Openness 访问对话框超时，确认后重连成功）；HIGH 0、MEDIUM 1（已知：证书）、LOW 3（`family` 回显、`changes=[]` 消息、复用只写 mapped）；无崩溃，未决 0。rc.7 的 HIGH 确认修复：内容不同的文件被拒且未覆盖，一致的文件复用 | LOW 由 GA-LOWS 修复；规范化后一致但字节不同的文件仍被拒，按决定保持严格比较 |
| rc.8，V19 VM，2026-11-26，定向复验 | 86 行：通过 77、按设计拒绝 7、失败 2；HIGH 0、MEDIUM 1（已知：证书）、LOW 2（不一致块的同步建议、`family` 回显）；无崩溃，未决 0。rc.7 的 HIGH 确认修复 | LOW 由 GA-LOWS 修复 |
| rc.8，V20 VM，2026-11-26，定向复验 | 119 行：通过 108、按设计拒绝 10、失败 1；HIGH 0、MEDIUM 1（已知：证书）、LOW 2（已知：`family` 回显）；无崩溃，未决 0。rc.7 的 SD 覆盖导入 MEDIUM 确认修复；连接已有文件规则通过 | LOW 和“导入断开映射后状态不提示”由 GA-LOWS 修复；启动 OB 的 SD 导出需要 V20 Update 4 和 SimaticSdEnabler |
| rc.8，V21 VM，2026-11-26，定向复验 | 105 行：通过 92、按设计拒绝 10、失败 3；HIGH 0、MEDIUM 1（已知：证书）、LOW 4（新：证书提示漏写 V21；已知 3）；无崩溃，未决 0。rc.7 的 SD 覆盖导入 MEDIUM 确认修复；连接已有文件规则通过 | LOW 由 GA-LOWS 修复 |

原始记录在维护者本机：beta.3 在 `bin-build/refactor/vm-test/`，尚待 FB-VM 登记；beta.4 在 `bin-build/vm-test/beta4-v21/`，含明文密码的原始返回，不入库，也不复制到夹具；RC 回归在 `bin-build/vm-test/rc-full-<版本>/`、`rc2-full-v17/`、`rc3-full-v21/`，rc.1 复验在 `rc1-full-v14sp1/`、`rc1-full-v15.1/`，rc.4 复验在 `rc4-verify-<版本>/`，rc.5 复验在 `rc5-verify-<版本>/`，rc.6 复验在 `rc6-verify-<版本>/`，rc.7 复验在 `rc7-verify-<版本>/`，rc.8 复验在 `rc8-verify-<版本>/`，每批一份汇总。rc.5 至 rc.8 各行的数字按各份汇总原样抄录，各复验会话的计数方式不同，不宜跨版本比较。
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
