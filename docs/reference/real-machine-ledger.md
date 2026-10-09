# 真机验收与历史证据

当前版本的构建、离线功能、协议测试和 Studio mock 结果见 [release-build.json](../../manifest/release-build.json)、[multi-version-build.json](../../manifest/multi-version-build.json)。这些检查没有连接 TIA 工程或在线 PLC，不能标记为原生验收。

## 未发布改动的验收边界

下列改动已合并到 master，只有离线证据；发布前必须在真机上完成并记录，否则不得发布。

| 改动 | 发布前的真机验收 |
|---|---|
| 引擎监控/强制表与工艺对象共享原语（P4-I2，`TiaSharedAdapterPaths` 默认 `false`） | **NOT RUN**；V20/V21 使用测试工程副本比较两种变体，核对借用同一个 ProjectBase、原有 MTA 线程、调用顺序与参数、诊断关联 ID。覆盖根组/嵌套组、重名与精确路径、空表、反射回退、读属性失败；监控表覆盖 XML 导出→修改→Override 导入→新代理读回、None 导入、注释行创建/删除、整表删除及计数读回，强制表保持原有查找/创建和属性拒绝语义。工艺对象覆盖递归枚举、导出、目录导入、受支持的技术版本、创建后 Find、参数读取/写入、删除后验证、部分失败与工程切换；Foundation 八版分别核对根组/递归读取范围与不覆盖导出。比较消息、字段、结果顺序及失败后的工程状态，不自动重放写入。在线监视与 S7 读取须另行授权；离线编译、调用图和响应快照不代替真机验收，未验收不得发布。 |
| 引擎硬件目录与设备创建共享原语（P4-I3，`TiaSharedAdapterPaths` 默认 `false`） | **NOT RUN**；发布前在 V20、V21 测试工程副本分别比较默认与共享路径的 CreateDevice、CreateHardwareDevice、SearchHardwareCatalog、SearchInstalledGsdDevices、CreateHardwareCatalogDevice、CreateGsdDevice。核对同一 ProjectBase、原有 MTA 线程、目录反射及类型化字段读取的顺序与参数、同文件同关联 ID 日志；覆盖无工程、空关键字、无目录候选、重复候选、评分排序、GSDML 回退、精确 TypeIdentifier、Unified 面板版本拒绝、MLFB×版本×名称尝试顺序、失败后继续及部分写入后的工程状态。Foundation V19–V21 保持所属 STA、目录 Find 和字段顺序、计划哈希及确认保护、完整设备清点、恰好一次 CreateWithItem、结果未知后禁止重放；核对不同宿主各自的响应和错误语义，回读创建对象并显式保存。记录引擎/适配器哈希与 TIA 补丁；离线调用图和快照不替代真机验收，验收前不得发布或默认开启。 |
| 引擎 PLC 块与编译共享原语（P4-I4a，`TiaSharedAdapterPaths` 默认 `false`） | **NOT RUN**；八版[静态证据](../development/evidence/p4-i4a-native-evidence.json)通过，宿主限于迁移方法，V20/V21 两种引擎调用图与非域 IL 通过。V20、V21 使用同版测试工程副本比较两种变体：导出覆盖及不一致块跳过、空组删除与删后回读、类型组创建、组重命名/删除、块保护、验证导入及可选编译、移动块的文档/XML 回退与恢复文件保留、OB 拒绝。覆盖软件/设备编译器查找、Safety 登录不登出、编译消息读取失败；在线快照/指纹另需明确授权，核对 TLS/密码回调及分页。逐项记录原生调用顺序、参数、引擎 MTA 与 Foundation/Studio STA、既有工程和对象归属、异常文本及部分失败。八版 Foundation/Studio 核对块/类型遍历和批次导入导出的原有覆盖策略；缺少或失效句柄后不重放写入。离线响应相同与织入校验不代替真机回放。 |
| 引擎 VCI 共享原语（P4-I1，`TiaSharedAdapterPaths` 默认 `false`） | **NOT RUN**；V20、V21 各以测试工程副本对比默认与开启开关的同版引擎，完成五个 VCI 工具的往返验收后，方可考虑默认开启；未验收的试验构建不得发布。核对同一个 ProjectBase、原有 MTA 线程、原生调用顺序与参数、同文件同关联 ID 日志。覆盖无工程/无 VCI 服务、空及嵌套工作区、名称大小写与首个工作区选择、缺目录与重名拒绝、列表可选属性失败；状态覆盖 Equal/Unequal/文件缺失/未知或异常及 changedOnly。同步覆盖 dryRun、跳过 Equal、未知状态的两种 changedOnly、逐项失败后继续；WorkspaceToProject 仍在原生调用前拒绝。映射覆盖根与用户设备组、设备过滤、粗粒度对象拥有子树、既有映射、文件命名、maxObjects 截断、walkTrace、dryRun、导出失败与查询失败后重取工作区、部分完成后继续，确认不重试同一次写入。切换、关闭、重开工程后不得沿用旧 VCI 句柄；强制 GC 后中间代理仍可用。比较完整消息、字段、计数和结果顺序，回读工作区文件与映射并显式保存；记录引擎/适配器哈希、TIA 补丁和失败后的工程状态。离线调用图与响应快照不代替这些验收。 |
| PLC 文档与外部源共享原语（P4-I4b，`TiaSharedAdapterPaths` 默认 `false`） | **NOT RUN**；V20、V21 分别用测试工程副本比较默认与共享引擎的十个工具，静态证据见 [P4-I4b](../development/evidence/p4-i4b-native-evidence.json)（八版静态证明通过，不能替代真机验收）；L5 验收前不得默认开启或发布试验构建。核对借用同一个 ProjectBase、原有 MTA、调用参数与顺序及同关联 ID 诊断。文档覆盖单块/批量导入导出、成对文件检查、覆盖选项、Override 后编号恢复的写入顺序、空/失败结果、返回对象不符、部分成功与停止、目录和文件操作失败、导出不一致块及不支持/许可失败；回读完整消息、对象顺序、编号和实际文件。外部源覆盖名称宽松匹配与精确匹配的既有差异、重复导入、缺失源的幂等删除、无参生成与带选项及目标组生成、主副本创建、dryRun、非空组删除拒绝、V20 改名拒绝与 V21 改名回读、单元与系统组遍历；失败后确认实际工程状态，不自动重试写入。八版 Foundation 保留各自版本拒绝、精确寻址、预览/覆盖、生成/编译/回读顺序、未知结果和原 STA 线程；批量导入继续通过同一导入上下文。切换/关闭/重开工程后不得复用失效句柄；记录构建哈希、TIA 补丁、日志、读回与显式保存结果，离线测试不代替真机往返。 |
| Studio 共享适配器会话（P4-G2，`TiaSharedAdapterPaths` 默认 `false`） | **NOT RUN**；L5 完成前不得把开关默认值改为 `true`，试验构建不得作为发布包。八个精确版本（14sp1、15.1、16、17、18、19、20、21）分别用测试工程副本比较旧、新路径：附加已有进程与启动进程、带界面/无界面、打开/复用/保存/关闭/断开，PLC 与 HMI 设备及块/类型/变量表遍历、SimaticML 与源导出、覆盖拒绝与确认后的 XML/源导入、软件/设备编译、检查报告。核对调用顺序、参数、所属 STA、同步进度及回调重入拒绝；比较错误文本、部分成功结果和出错后继续操作。VCI 覆盖 16/17 初版、18/19 旧版、20/21 新版的工作区、映射、状态、双向同步（含 dryRun），14sp1/15.1 及无服务工程保持能力不可用；切换工程后不沿用旧 VCI 句柄。记录 bridge/adapter 哈希、打包副本织入验证、hello 身份、诊断日志及退出清理结果；离线 DTO 比对、mock、八版编译和桥接冒烟不代替真机验收。 |

## 3.3.0 发布前最小验收（按虚拟机）

3.3.0 只发布已进入默认构建的改动；`TiaSharedAdapterPaths` 开关后的改动（引擎 VCI 共享原语、Studio 共享适配器会话）不在 3.3.0 范围内，不需要在此验收。每个 TIA 版本在各自的虚拟机上验收，全部使用 3.3.0 候选包（解压到仓库以外的新目录，不覆盖原有安装）和测试工程副本。每项记录：虚拟机、TIA 版本及补丁、候选包 SHA-256、通过/失败，以及返回文本或截图。

| 虚拟机 | 项目 |
|---|---|
| V14 SP1 | A（同时验证 net48 worker）、B |
| V15.1 | A（同时验证 net48 worker） |
| V16 | A（同时验证 net48 worker）、B |
| V17、V18、V19 | A |
| V20 | C、D |
| V21 | B、C、D |

**A Foundation MCP（协议 2、worker DTO；V14 SP1–V16 另含 net48）**：在 TIA 中打开测试工程副本；启动该版本的 3.3.0 MCP 服务；依次调用
`ListPortalProcessProjects` → `Connect {processId}` → `GetState`（版本正确、worker 正常）→ `GetProjectTree` → `GetBlocks`（PLC 名）→
`GetBlockInfo`（一个已有块）；再用不存在的块名调用 `GetBlockInfo`，应返回错误，随后再次 `GetBlocks` 仍正常（会话未失效、调用未重放）；
最后 `Disconnect`，应确认 worker 已断开。

**B Studio 桥接（协议 2、桥接 JSON）**：用候选包根目录的 `TiaOpenness.exe` 打开工作台并选择该版本；工程操作页连接已打开的 TIA、
打开测试工程副本、读取块列表、导出一个块到桌面文件夹（文件生成）；再导出到不存在的目录，界面应显示原错误文本，随后再次读取块列表
仍正常；最后断开并关闭工作台，确认桥接进程（任务管理器中的 `TiaOpenness.Bridge`）已退出。

**C PLC 名称严格解析（G9）**：单 PLC 工程：`GetBlocks` 的 `softwarePath` 用不存在的名字，应返回“未找到”并列出可用 PLC 路径
（3.2 会错误地选中唯一 PLC）；空名字、软件名、CPU 名、站点名和分组路径都解析到该 PLC。两个 PLC 共用站点名的工程：用站点名应返回歧义
并列出两个候选，用各自的软件名解析到正确 PLC。读取（用 `ExportPlcTagTable` 导出测试变量表）与写入（用 `ImportPlcTagTable` 把刚导出的文件导回测试工程）各验证一次；
设备较多的工程中，同一路径的后续调用应与 3.2 一样快（不重新遍历）。

**D 引擎冒烟（阶段 3 拆分、响应构造器、引擎引用共享适配器后的默认构建）**：连接并绑定测试工程副本 → `GetProjectTree` →
`ExportBlock`（一个块，导出到桌面文件夹）→ `CompileSoftware`（测试工程）→ V21 再调用 `GetVersionControlWorkspaces`（工程无 VCI 时
记录返回的提示即可）→ `Disconnect`；检查 `%LOCALAPPDATA%\TiaMcp\diagnostics` 下当天的调用日志存在且无写入失败。

A、C、D 可以在虚拟机上用 AI 客户端执行，也可以由虚拟机启动该版本的 HTTP 服务后从开发机远程执行；B 必须在虚拟机界面上手动操作。
任何一项失败时停止发布，记录现象后修复并重新生成候选包。

### 3.3.0 验收结果（2026-10-04/05）

全部项目通过。运行文件来自第七版候选构建（提交 9847e141，候选包 SHA-256 `a84ca290865c64a679f03a0c4ce6c3a181c1ed1bbea9bbd61ecd68c9bc3d8307`）；正式发布包由同一构建记录重新打包，只多出本节文档。A、C、D 由开发机通过各虚拟机的 HTTP 服务远程执行，B 由维护者在虚拟机界面上操作。TIA 补丁号未采集。

| 虚拟机 | 项目 | 结果 |
|---|---|---|
| V14 SP1（192.168.86.131，Windows 10 1607，另装有 .NET 8/10） | A、B | A 11/11 通过，基础宿主实际加载随包 `runtime/dotnet`；B 在第四版通过（工作台界面后续改动由 V16、V21 复核） |
| V15.1（192.168.86.132，只装 .NET Framework 4.8） | A | 11/11 通过，同时确认解压即用。第六版发现并修复 `.ap15_1` 工程无法绑定（自 3.2.0 存在，bb1753a2） |
| V16（192.168.86.133） | A、B | A 11/11 通过；B 通过，含新菜单、MCP 状态标签与菜单、卡片样式、Ctrl+O，以及服务运行时关闭工作台不崩溃、桥接进程退出 |
| V17（192.168.86.134）、V18（192.168.86.135）、V19（192.168.86.136） | A | 各 11/11 通过 |
| V20（192.168.86.129，两个站点各一个 PLC） | C、D | 通过：错误名称与两 PLC 时的空名称均拒绝并列出全部路径；软件名、大小写与空白、站点名、站点/CPU 路径解析正确；子串拒绝；同一路径再次解析 0.63 s → 0.30 s；变量表导出后导回成功；导出未编译块被明确拒绝，编译 0 错误后导出成功；调用日志 failedWrites 0 |
| V21（192.168.86.128，单 PLC） | B、C、D | 通过：单 PLC 空名称选中该 PLC，其余同 V20；`GetVersionControlWorkspaces` 返回无工作区提示；B 由维护者完成 |

两 PLC 共用站点名的歧义情形无法在真实工程中构造（TIA 不允许站点重名），由离线解析测试覆盖。每台虚拟机首次连接新构建时 TIA 会弹出 Openness 访问确认，未确认前 worker 在 120 s 后报超时；该提示及调用日志的保留时长列入 3.3.x 改进。

已随 3.3.0 发布并完成上述验收的改动：

| 改动 | 验收 |
|---|---|
| Foundation 协议 2（P4-E2）及 worker DTO 改用 System.Text.Json（P2-04b） | 发布前对 V14 SP1、V15.1、V16、V17、V18、V19 各连接一次对应真实 TIA：核对 hello 身份、显式连接/绑定、读取、断开及错误后不重放；记录版本、worker/adapter 哈希和结果。离线协议故障、PublicAPI 加载冒烟及响应快照不代替此验收；3.3.0 已验收，见上表。 |
| V14 SP1、V15.1、V16 worker 改为 net48（P4-E1） | 发布前分别以 net48 worker 连接一次对应版本的真实 TIA：打开测试工程、读取 PLC 列表、读取块、断开连接，并记录版本及结果。加载本地 PublicAPI 的离线 worker 冒烟不代替此验收；3.3.0 已验收，见上表。 |
| Studio 桥接协议 2（P4-F）及桥接 JSON 改用 System.Text.Json（P2-04a） | 发布前用 Studio 分别连接 V14 SP1、V16、V21 的真实 TIA：核对 hello 身份后连接、打开测试工程副本、读取块列表、导出一个块；再触发一次已处理的错误（如导出到不存在的目录），确认界面显示原错误文本且同一会话可继续操作；最后断开并关闭 Studio，确认桥接进程退出。离线桥接冒烟和 mock 测试不代替此验收；3.3.0 已验收，见上表。 |
| PLC 名称严格解析（G9） | 3.3.0 已在 V20、V21 验收，见上表；歧义情形由离线测试覆盖。 |

## V20/V21 host + worker acceptance (P6-54)

The host + worker implementation keeps the existing V20/V21 engine and tool sets. These VM steps remain **NOT RUN** until performed on the exact release candidate with the corresponding TIA version and a disposable project copy. They are a release-default gate; offline startup tests do not satisfy it.

| releaseKey | State | Required isolated-mode acceptance |
|---|---|---|
| 20 | **NOT RUN** | On the V20 VM, connect to the intended TIA process and explicitly bind a disposable project copy; verify project identity and read state. Import one fixture and export it back to a separate directory; read back both sides. Compile the selected PLC software and record full nested diagnostics. Induce the approved worker timeout/fault fixture, inspect actual project state, restart the idle worker, connect and explicitly bind the project again, then read back state. |
| 21 | **NOT RUN** | Repeat the same sequence on the V21 VM using a V21 project copy and version-matched import/export fixtures. Connect and explicitly bind, verify project identity, import and export with read-back, compile and retain nested diagnostics, induce worker timeout/fault, inspect state, restart, explicitly rebind, and read back again. |

For each row record Windows/TIA build and patch, engine/worker/adapter SHA-256, test project copy identity, worker mode and timeout, tool arguments with credentials removed, MCP request IDs, before/after project state, call ordering, thread apartment, restart and rebind evidence, import/export output hashes, compile errors/warnings and nested messages, logs, and whether the project was explicitly saved. A timeout/crash after worker dispatch is not a pre-operation refusal; never repeat an operation until project state is inspected. Do not use a production project or connect to a PLC for this gate.

The current release property remains `workerIsolation.enabledByDefault=false` for both keys. Change it only after both rows pass and the acceptance evidence is reviewed. If either row fails, keep the default off and retain the findings.

## 4.0 安全策略验收计划（P6-R2）

以下为[4.0 规范](../development/phase6-review.md)的新增验收项目，**全部 NOT RUN**，仅登记计划，不授权连接 TIA、PLC、VM 或网络。
每行按实际广告能力逐版本记录，八个发布键为 14sp1、15.1、16、17、18、19、20、21；无该能力的版本记录 N/A 并附目录证据，不能跳过而标记通过。
原生行为只有该版本/族的基线与候选回放均通过才可切换为 safe-v4；未验收族保持 current，且不解除上文阶段 4 的发布限制。
共用证据要求：精确 SDK/TIA 补丁、工程副本/目标身份、引擎/adapter 哈希、前后调用顺序及参数、MTA/STA 所属线程、关联 ID、预览哈希、故障后实际状态、回读/编译及是否显式保存。

| ID / 行为族 | 状态 / 版本范围 | 验收项目与切换门槛 |
|---|---|---|
| P6-DEVICE 设备创建 | **NOT RUN**；19/20/21 的硬件创建入口，其余创建能力按目录逐版 | 精确目录 TypeIdentifier、型号白名单、重名、目录不存在、预览不创建；apply 原生 Create 恰一次；确认/哈希/工程身份变化拒绝；Create 前/中/后故障与残留检查，禁止换候选重试。 |
| P6-IMPORT 导入 | **NOT RUN**；八版普通 PLC；SD/HMI/软件单元按目录 | 默认拒绝覆盖及升级/版本改写；不改 BOM/源文件；同版块/UDT/tag-table 回读；明确支持的覆盖参数才执行；非法目标、锁定文件、批次第一个/中间失败、文件在 preview 后改变；返回成功/失败/未执行逐项证据，不回滚或重放未知写入。 |
| P6-EXPORT 导出 | **NOT RUN**；八版普通 PLC；SD/专用对象按目录 | 预览不写目的地；单项/批量暂存发布、原文件/目录保全；拒绝覆盖、显式支持的覆盖、磁盘/权限/发布冲突；内容/路径回读、partial staging 保留；不扩大 SD、普通块/GlobalDB、软件单元与版本范围。 |
| P6-SESSION connect/open | **NOT RUN**；八版 | PID/start time 重用与工程切换拒绝；附加不等于绑定、连接不启动；已有工程 reuseOpen=false；旧工程 upgrade=reject；明确允许升级时只改测试副本；失败不关闭陌生/借用工程；验证所有权和会话纪元。 |
| P6-CLOSE save/close | **NOT RUN**；八版 | 普通 Project 与 LocalSession 分别保存回读；借用工程拒绝关闭；脏工程先显式保存或独立 discard 预览确认；不隐式保存/关闭；失败后的绑定、对象有效性与工作进程清理。 |
| P6-SOURCE PLC 路径/外部源 | **NOT RUN**；八版；G9 两版基线同时回放 | 唯一 PLC 空路径、多个 PLC 候选、非空错误名、结构名称映射和转义路径；读写选中同一目标；源导入/生成/删除顺序、精确名称/扩展名、缺失删除拒绝、批次故障；14sp1 observation 与其他版原生结果分列；切工程后缓存失效，未知写入不重放。 |
| P6-COMPILE 编译/Safety | **NOT RUN**；八版普通 PLC；HMI/Safety 按能力 | 在线前提拒绝且无自动下线；每个编译入口目标范围和诊断树/根计数一致；本次 Safety 登录仅由本次结束，原有登录不退出；登录/编译/注销分别故障，保留清理证据及真实工程状态；不改变 MTA/STA。 |
| P6-FALLBACK 原生回退 | **NOT RUN**；20/21 在线下载及 VCI；其他版共享 VCI 路径按能力 | ApplyConfiguration 失败后不下载或换路线；自动下线分支改为前置条件拒绝；传输/通道每个中断点记录调用是否发出与真实结果；unknown 要求重建会话、不自动重放；VCI 仅显式允许且证明只读对象失效时重取一次，已写或未知时停止。 |
| P6-PRODUCT 新产品启动/绑定 | **NOT RUN**；八版引擎/宿主与 Studio | 仓库外完整包从根 TiaOpenness.exe 打开 Studio；选择版本启动正确新 EXE/worker/adapter；逐版 hello 身份与绑定、读取、显式断开；新目录日志可关联，安装目录只读；不借用历史机器/PID 授权。 |

### 4.0 纳入与延期（P6-42）

本次八版候选的范围逐行由下方生成表中的 releaseKey、family、entries 表示。P6-42 没有任何 L5 通过：
八版都只纳入已有 `current` 行为；没有 D1 行为族切换为 `safe-v4`。每个 family 对应的 L5 计划在主 checkout 的
`bin-build` 中，提交与交付包不包含这些机器计划。

| releaseKey | family | 4.0 纳入 | 延期 | L5 计划（主 checkout） |
|---|---|---|---|---|
| 14sp1、15.1、16、17、18、19、20、21 | P6-DEVICE | 所有有目录入口的版本保持 current | safe-v4 设备创建，逐目录能力验收 | `bin-build/P6-27/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-IMPORT | 现有导入入口保持 current | safe-v4 导入预览、覆盖与故障语义 | `bin-build/P6-28/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-EXPORT | 现有导出入口保持 current | safe-v4 导出暂存、覆盖与发布语义 | `bin-build/P6-29/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-SESSION | 现有 connect/open 入口保持 current | safe-v4 连接、绑定及工程打开边界 | `bin-build/P6-30/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-CLOSE | 现有 save/close 入口保持 current | safe-v4 保存、关闭与对象所有权边界 | `bin-build/P6-31/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-SOURCE | 现有 PLC 路径和外部源入口保持 current | safe-v4 精确路径与外部源写入顺序 | `bin-build/P6-32/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-COMPILE | 现有编译入口保持 current | safe-v4 编译范围、Safety 生命周期与拒绝 | `bin-build/P6-33/l5-plan.md` |
| 14sp1、15.1、16、17、18、19、20、21 | P6-FALLBACK | 现有原生回退保持 current | safe-v4 在线下载与 VCI 回退；按版本能力执行 | `bin-build/P6-34/l5-plan.md` |

family 计划按版本能力限定适用项；无入口的版本记录 N/A，不将 N/A 记作通过。下方生成表确认每个发布键的所有
family 均为 `state=current`、`L5=NOT RUN`，并列出当前入口。默认构建的行为能力表因此没有切换项。

### 4.0 发布前仍需的真实机器步骤

以下均为 **NOT RUN**，只能由维护者在各精确 TIA 版本 VM 上用测试工程副本完成；版本/补丁、候选 SHA-256、
引擎/worker/adapter 哈希、完整日志、错误后的工程状态、回读和显式保存结果须登记。失败即停止发布。

| 门槛 | 必须完成的机器步骤 |
|---|---|
| P6-PRODUCT（14sp1、15.1、16、17、18、19、20、21） | 用仓库外的完整 4.0 候选包启动根 `TiaOpenness.exe`；逐版选择对应 TIA；核对 hello 的 releaseKey 和新 Engine/worker/adapter 身份；连接该 VM 当前 TIA 进程并显式绑定测试工程副本；读取工程树和一个 PLC 对象；显式断开并确认本次 worker/桥接退出；检查新目录日志可关联、只读安装无包内写入，且不借用历史 PID/机器授权。 |
| P4-E1 / P4-E2 Foundation worker（14sp1、15.1、16） | 每版用该版本真实 TIA 打开工程副本；启动 net48 worker，核对协议 2 hello 身份；读 PLC 列表和块；核对错误后会话仍可用；显式断开并确认 worker 退出。 |
| P4-F Studio bridge（14sp1、16、21） | 用 Studio 连接对应真实 TIA、打开工程副本、读取块列表、导出一个块；触发一次已处理错误并确认原错误文本及会话恢复；断开并确认桥接退出。 |
| G3 / P4-G2 Studio 共享会话路径（八版） | 对 14sp1、15.1、16、17、18、19、20、21 分别用同版工程副本比较默认路径与 `TiaSharedAdapterPaths` 开启路径：附加/启动、打开/复用、保存/关闭/断开、PLC/HMI 导航、块/类型/变量表、SimaticML/源导入导出、软件/设备编译与 VCI（按版本/服务能力）；核对原生调用顺序、参数、STA、进度回调、错误/部分成功、句柄失效和清理。 |
| P4-I1 VCI 共享原语（V20、V21） | 默认与共享变体逐工具比对五个 VCI 工具；覆盖 workspace 列表/状态、双向同步、映射、dryRun、失败后继续、项目切换后句柄失效；回读文件和映射并显式保存。 |
| P4-I2 监控/强制表与工艺对象（V20、V21） | 默认与共享变体回读监控表导出/覆盖导入/删除、强制表原有行为和工艺对象遍历/导入/导出/创建/修改/删除；检查目标对象、消息、计数、失败后状态及工程切换。在线监视或 S7 读取需单独授权。 |
| P4-I3 硬件目录与设备创建（V20、V21；Foundation V19–V21） | 核对硬件候选排序、GSDML 回退、精确 TypeIdentifier、版本拒绝及 Foundation 的确认/计划哈希；回读新设备，确认创建调用恰一次，显式保存。 |
| P4-I4a 块与编译（八版） | V20/V21 默认与共享变体回放块/类型组、保护、导入/导出与软件/设备编译；八版 Foundation/Studio 按各自入口回放遍历和批次导入导出；核对线程归属、诊断和失败后不重放。 |
| P4-I4b 文档与外部源（八版） | V20/V21 对十个工具默认/共享变体做文档与外部源往返；八版 Foundation 按各自版本边界核对拒绝、精确寻址、覆盖、生成/编译、编号恢复和部分完成；回读文件/工程并显式保存。 |
| J 清理阶段（上述 L5 全部通过后） | 才能移除 `TiaSharedAdapterPaths` 开关和旧 Studio/引擎路径；随后重跑八版构建、离线套件、响应/契约对比及严格候选包校验。L5 未全部通过时保持开关默认 false，试验变体不得发布。 |

<!-- behavior-capabilities:start -->

逐版行为能力由 `Generate-Phase6Plan.py` 从上面的 L5 台账与实际入口目录生成。空入口数组表示该宿主无此族 MCP 入口；不授予原生验收。

| releaseKey | family | state | L5 | entries |
|---|---|---|---|---|
| 14sp1 | P6-DEVICE | current | NOT RUN | — |
| 14sp1 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType |
| 14sp1 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 14sp1 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectPortal, OpenProject |
| 14sp1 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 14sp1 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, PlanPlcExternalSourceImport |
| 14sp1 | P6-COMPILE | current | NOT RUN | CompilePlcDiagnostics, CompilePlcSoftware |
| 14sp1 | P6-FALLBACK | current | NOT RUN | — |
| 14sp1 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 15.1 | P6-DEVICE | current | NOT RUN | — |
| 15.1 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType |
| 15.1 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 15.1 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectPortal, OpenProject |
| 15.1 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 15.1 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, PlanPlcExternalSourceImport |
| 15.1 | P6-COMPILE | current | NOT RUN | CompilePlcDiagnostics, CompilePlcSoftware |
| 15.1 | P6-FALLBACK | current | NOT RUN | — |
| 15.1 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 16 | P6-DEVICE | current | NOT RUN | — |
| 16 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType |
| 16 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 16 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectPortal, OpenProject |
| 16 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 16 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, PlanPlcExternalSourceImport |
| 16 | P6-COMPILE | current | NOT RUN | CompilePlcDiagnostics, CompilePlcSoftware |
| 16 | P6-FALLBACK | current | NOT RUN | — |
| 16 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 17 | P6-DEVICE | current | NOT RUN | — |
| 17 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType |
| 17 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 17 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectPortal, OpenProject |
| 17 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 17 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, PlanPlcExternalSourceImport |
| 17 | P6-COMPILE | current | NOT RUN | CompilePlcDiagnostics, CompilePlcSoftware |
| 17 | P6-FALLBACK | current | NOT RUN | — |
| 17 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 18 | P6-DEVICE | current | NOT RUN | — |
| 18 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType |
| 18 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 18 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectPortal, OpenProject |
| 18 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 18 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, PlanPlcExternalSourceImport |
| 18 | P6-COMPILE | current | NOT RUN | CompilePlcDiagnostics, CompilePlcSoftware |
| 18 | P6-FALLBACK | current | NOT RUN | — |
| 18 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 19 | P6-DEVICE | current | NOT RUN | CreateHardwareDevice |
| 19 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType |
| 19 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 19 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectPortal, OpenProject |
| 19 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 19 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, PlanPlcExternalSourceImport |
| 19 | P6-COMPILE | current | NOT RUN | CompilePlcDiagnostics, CompilePlcSoftware |
| 19 | P6-FALLBACK | current | NOT RUN | — |
| 19 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 20 | P6-DEVICE | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, CreateHardwareDevice |
| 20 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 20 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlockDocuments, ExportPlcBlocks, ExportPlcBlocksDocuments, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 20 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 20 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 20 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 20 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 20 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, DownloadPlcToFolder, ExportPlcBlockDocuments, GetVersionControlStatus, ImportPlcBlockDocuments, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 21 | P6-DEVICE | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, CreateHardwareDevice |
| 21 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 21 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlockDocuments, ExportPlcBlocks, ExportPlcBlocksDocuments, ExportPlcTagTable, ExportPlcType, ExportPlcTypes |
| 21 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 21 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 21 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 21 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 21 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, DownloadPlcToFolder, ExportPlcBlockDocuments, GetVersionControlStatus, ImportPlcBlockDocuments, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |

<!-- behavior-capabilities:end -->

## v3.2.0 验收边界

八版新增外部源导入、生成、编译路线与目标版本 UDT / DB XML，以及 Studio 新版本适配器的真实工程往返，当前均为 **NOT RUN**。后续应按明确的版本、测试工程副本及选定 PLC 执行，记录实际导入对象、编译诊断、读回与保存结果。选择测试目标不会由历史记录自动授权。

| 已知现场事项 | 当前结论与证据 |
|---|---|
| `ManageStartdriveParameter` 读取 `p2051[0]` | 用户报告 TIA 崩溃。现代码按官方 BICO 路线做单值读取，去掉无关元数据遍历；原生复测未运行，根因未确认。详见[限制与结果解释](../troubleshooting/openness-limitations.md)。 |
| Unified 脚本模块库类型改名 | 2026-10-01，在项目库和独立全局库中的样本均出现 `NonRecoverableException` 并伴随测试 TIA 退出；没有已验证修复。[机器可读证据](../../manifest/history/unified-library-rename-native-20261001.json)。 |
| PLC 原生交叉引用 | 有 V21 退出历史，默认关闭；编译成功不能保证查询稳定。离线引用分析仅覆盖输入导出。 |
| HMI 深层快照 | 有 getter / 句柄失效记录；部分属性暂停读取。必须查看完整性字段，[诊断说明](../troubleshooting/hmi-snapshots.md)。 |

## 保留的历史记录

[原逐工具台账](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/reference/real-machine-ledger.md)保留早期 V21 测试工程、设备、工具行和现场修复过程。该台账主要对应 2.7.39–2.7.62；旧汇总中“调用过”“前置条件拒绝”“环境拒绝”与真实成功分列，不能将总行数称为全部验收通过。

历史证据中，标准 CPU / PLCSIM Advanced 的下载、上线、比较、变量读写与单步场景曾完成；这些结果仅适用于当时设备、工程与软件组合，不能推广到其他版本、F-CPU 或真实生产设备。早期某个 BICO 参数写入成功，也不能证明 `p2051[0]` 读取已通过。

[Unified 改名现场完整快照](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/development/unified-library-rename-incident-20261001.md)保存两个测试位置、时间、调用标识与源工程读回结果。它不是独立于 MCP 的最小 C# 复现，也没有确定所有 V21 补丁的行为。

## 新记录应包含什么

记录引擎版本和文件哈希、TIA 版本/已知补丁、精确工程与 PLC、工具参数（排除凭据）、预览与实际结果、读回内容、编译错误/警告以及是否保存。失败或结果不确定时保留对应日志，先确认工程实际状态，再决定下一步。最新构建不会覆盖上述历史证据。
