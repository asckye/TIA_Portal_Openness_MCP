# 开发进度

状态：现行 · 最后核对：2026-11-05，已发布 v4.0.0-beta.4（`48abffe8`）

本页记录当前 worktree 基线包含的实现和测试者反馈，范围、顺序与放行门槛见[开发计划](plan.md)。本页的集成记录不证明已发布或已通过真机验收。

## 当前状态

- 当前版本 [`4.0.0-beta.4`](../releases/v4.0.0-beta.4.md) 已于 2026-11-05 发布为预发布（`48abffe8`），GitHub latest 仍为正式版 [v3.3.0](../releases/v3.3.0.md)。预发布不设为 latest，3.3 安装不提示升级。
- beta.4 已集成默认写入预览、批次与安全移块、回滚日志、参数模型、会话恢复、原生原因、输出根审批、确认词、relay 与工作台页面重绘。所有这些变化仍待真实 TIA Portal 复验。
- `prepare` 构建、打包、检查 ZIP 并上传私有草稿，完整候选准备已含新鲜依赖漏洞扫描。扫描接入前的构建打包实测约 6 分钟，不能作为含扫描的完整耗时。`publish-prepared` 公开封存候选，300 秒硬预算，beta.3 实测 6.9 秒。beta.4 的构建、打包和检查实测 347 秒；上传受本机网络影响，经本机代理约 4–54 KB/s，绕过代理约 148 KB/s（约 20 分钟），beta.3 约 350 KB/s（8 分 10 秒）。上传没有进度显示和超时，待修。
- 合并门为 `merge-check`，含 38 个注册套件与两组 full 传输检查。FAST-GATE 目标约 10 分钟；2026-11-05 宽范围改动实测 731 秒（约 12 分钟），优化前约 21 分钟。beta.4 候选的 `merge-check -All` 实测 779 秒（约 13 分钟），数量门槛保留。没有 nightly，GitHub CI 不阻塞合并。
- 架构仍为八版 FoundationHost，每个 MCP 会话一个 PLC worker。V20/V21 使用 MTA，V14 SP1–V19 使用 STA，MTA-1 尚待实施。工具列表呈现已统一为 `plc-foundation`，默认 lite，完整行为统一仍由 UNIFY-1 完成。
- 名称为自动化工程工作台，现有程序、插件、服务和仓库标识继续使用。GitHub 仓库改名尚未执行。

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

## 2026-11-04、2026-11-05 决定与未完成项

完整决定见[计划中的实施决定](plan.md#2026-11-042026-11-05-实施决定)。

- S3 更新签名未完成，维护者接受同一发布摘要无法抵御 GitHub 账户被控制的风险。S5 未实现加密，维护者接受 LAN 明文 HTTP 暴露访问密钥与 PLC 密码的风险，仅在可信网络使用。两项移出 4.0 前实施清单，见[安全标准](../standards/security.md)。
- S8 D1 A、D2 已定，AI 调用将拒绝网络路径，含 localhost 和 127.0.0.1 的网络路径，Workbench 人工操作不受此限制。仍未实现，排 4.0 前。T3 D1 保留并解释 `rollbackCompleted`，D2 迁移 bridge 路由测试，顺序为 U4/C2 → T2 → T3，S9/U3/S8 可独立进行。
- VMFIX-6 D1–D5 已批准，纯 ASCII SCL 无 BOM，非 ASCII 保留 BOM 与导入限制说明，词过滤从变量名移到 options，停止迁入旧审批关闭设置，参数细节交 DES-4 Part 2，CreateDevice 确定拒绝的 not-started 分类保留。6a 排队，6b 跟随 Part 2。
- DES-4 D1–D6 已批准，别名保留至 5.0，规范导出参数和数组 devicePath，项目名与文件身份分开，默认会话目录，空串改动限于改名参数。Part 1 已集成。参数 JSON pointer 与原生 `innererror` 分属 Parts 2、3，2026-11-05 移到 beta.5。
- UNIFY-1 仍是 4.0 条件，替代 Q6a/Q6b/P8-04n2 并吸收 C9。U1 解除无 API 依据的工艺导出限制，U2 统一无 TIA 与能力准入，U4 保留需实测的 s7res 配对并移除无依据的 None-only 限制，U5 开放旧版 seed HMI，U8 按能力选择生成指纹并兼容历史记录。U3 线程模型留待实机，U6 实机保护保留，U7 V21 builder 保持到 4.1。尚未完成。
- VMAUTO-1 已取消，先前自动回归方案失效。beta.4 公开后另开 AI 会话，书面指定 VM、工程副本、覆盖与通过规则，只在副本写入，原始结果固定格式保存在 `bin-build/vm-test/<run>/`。归档分支保留，Campaign.Alive 缺陷未修复，正式回归尚未开始。
- FAST-GATE D1/D2/D4 批准，D3 仅在 Part 2 后仍超 10 分钟时考虑，D5–D8 暂缓。HANG-GUARD 批准的 151 处等待、速度断言与预算调整已集成，WriteGuard 15/20 秒保持。传输每组最多八版，WinError 10055 后共享标记限制为四版。TREND-ALERT 超近期中位数两倍只提示，至少五条历史、最近至多二十条，坏数据与存储失败不改退出码，prepare 显示最近提示。
- TIDY-7、HOST-RENAME、LONG-LINE-GATE 移到 beta.5 开头，等 DES-4 Parts 2、3 合并，因为未完成分支无法可靠跟随文件拆分和改名。仅 High/Medium 审查发现触发返工，Low 合并后排期并在影响用户时披露。超过三轮审查先停下复核设计，告知维护者。

## 真机结果

| 测试 | 结果 | 处理状态 |
|---|---|---|
| v4.0.0-beta.2，V21 VM，2026-10-31 至 11-01 | 原记录称 511 个工具：通过 213、失败 93、未测 192、按设计拒绝 12；TIA 重启 4 次 | 分类合计为 510，与原总数相差 1，待台账核对；相关任务见计划 |
| beta.3 测试包（`1dfe2067`），V21 VM，2026-11-02 至 11-03 | 512 个工具：通过 293、缺陷 103、按设计拒绝 60、不确定 1、未测 55；去重缺陷 59 个（HIGH 20）；会话中断 18 次，TIA 重启 2 次 | R1 对应 VMFIX-2 已合并，但这不是修复后复测结果；其余修复与复测待执行 |
| v4.0.0-beta.4（`48abffe8`），V21 VM，2026-11-05 至 11-06 | **不通过**。839 次调用：通过 578、失败 158、按设计拒绝 103；512 个工具调用 511 个（`ConnectOnlinePlc` 只预览）；去重缺陷 87 个（HIGH 10、MEDIUM 25、LOW 52）；beta.3 的 59 个缺陷已修 15、有变化 12、仍在 32，其中 HIGH 20 个已修 8、有变化 9、仍在 3（B02、B11、B20）；会话中断 12 次，TIA 重启 2 次、宿主重启 2 次（均由维护者操作），TIA 未崩溃 | 只代表 V21，不代表另外七个版本；只读工具 `AnalyzePlcCrossReferences` 会锁会话；修复任务见计划 |

原始记录在维护者本机：beta.3 在 `bin-build/refactor/vm-test/`，尚待 FB-VM 登记；beta.4 在 `bin-build/vm-test/beta4-v21/`，含明文密码的原始返回，不入库，也不复制到夹具。beta.4 回归的缺陷原因尚未逐条验证。未测的正常流程：`OpenProject`、`CloseProject`（自有工程）、`CreateProject`、`RetrieveProjectArchive`、`ConnectIsolatedPortal`、`PublishGenerationRecord`；在线、下载、上传、强制和仿真只读取或预览。调查列出的无任务缺陷和测试缺口已在计划中标为提议，不能当作已分配或已修复。离线检查不能证明原生行为；验收边界以[真机台账](../reference/real-machine-ledger.md)为准。

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
