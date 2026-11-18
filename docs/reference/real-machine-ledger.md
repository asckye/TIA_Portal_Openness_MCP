# 真机验收与历史证据

当前构建、离线功能与协议检查由 `merge-check` 和 `prepare` 记录在 `bin-build/merge-check/`、`bin-build/prepared/`；这些检查不等于连接真实 TIA 工程或在线 PLC 的验收。历史版本与现场结果在下方单列，不能推广为当前版本通过。

## 预发布例外

维护者决定（2026-10-20，2026-11-01 修订）：预发布（`X.Y.Z-beta.N`、`X.Y.Z-rc.N`）交给测试者测试，维护方在虚拟机上按[预发布真机验证清单](../guides/beta-tester-checklist.md)回归。下文“发布前必须在真机上完成”
“失败即停止发布”等真机门槛不约束预发布；预发布说明必须带由本台账与[官方 API 审计 VM 核对清单](api-audit-vm-checklist.md)生成的
“Not verified on a real TIA Portal”清单（`generate-prerelease-notes`）。正式版本 `X.Y.Z` 的放行条件见 [ADR 0023](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/standards/decisions/0023-release-4-0-final-criteria.md)：本台账列出需要真机确认的项目，由 VM 回归按 [ADR 0020](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/standards/decisions/0020-prereleases-tested-by-users.md) 覆盖。

## 未发布改动的验收边界

TIA-BATCH-5a：**NOT RUN**。V21 工程副本上按 unified-alarms-excel 与 unified-tags-excel 各走一遍 60 行（两个批次），含一个故意失败的行，核对失败项回滚、其后为 NOT_EXECUTED、Excel 文件可读。另逐行对照当前 PLC 导出核对变量绑定的数据类型与读写权限，确认不匹配或证据缺失的行在批次预览前报告并排除；核对多语言报警文本与变更后的读回结果。

TIA-BATCH-4：**NOT RUN**。在 V15.1 与 V21 工程副本上：导出含结构、数组、默认与非默认起始值的全局 DB，转 xlsx，修改 5 个起始值（含 1 个原为默认值的 Int 成员），转回 XML，ImportPlcBlockVerified 预览并执行，读回并编译；确认 TIA 接受新增的非默认 StartValue，并核对各基础类型字面量的导出规范形式；表格拒绝常量引用、批量值、清空单元格和新默认值。

TIA-BATCH-2：**NOT RUN**。在 V14 SP1、V18、V21 工程副本上，用 ExportPlcTagTables 分别从 PLC 根组和一个精确用户组执行 recursive=false 与 recursive=true 两种预览和导出，覆盖根组非递归、根组递归、用户组非递归、用户组递归四种组合；工程包含用户组、子组及中文名、带 / 名称的变量表。核对非递归仅包含选定组本级的变量表，递归包含选定组及其全部子组的变量表，逐次核对文件数与 objectPath，并把其中一个文件用 ImportPlcTagTable 导回原组（overwrite=true）。
TIA-BATCH-3：**NOT RUN**。在 V14 SP1 或 V15.1 与 V21 工程副本上：导出含中英文注释、常量与外部访问标志的变量表，ConvertEngineeringTable 转 xlsx 与 csv，用 Excel 打开核对无乱码，修改注释与地址并新增 2 个变量和 1 个常量，转回 XML，ImportPlcTagTable overwrite=true 预览、审批、执行，ListPlcTags 读回；核对未改动的变量保持原样。

| 改动 | 真机验收 |
|---|---|
| TIA-TEST（beta.6）PLC 测试用例库、批量运行、结果历史、JUnit、FAT/SAT 与复现报告（八版；Test Suite 条目 V17+，系统测试 V18+，测试集 V20+） | **NOT RUN**。离线测试只证明文件格式、判定映射、退出码和报告文本；Part 2 合成服务覆盖计划失效拒绝、整批一次审批、失败继续、未知即停及逐条历史持久化。需在装有 PLCSIM Advanced（含许可证）的机器上：保存并运行一条通过和一条失败的场景，核对历史、junit.xml 与退出码；在 V17–V21 工程副本上运行 application 用例、V18+ system 用例、V20+ 测试集，核对多用例时 TestResults 消息树与条目判定是否一致、Warning 判为失败、缺许可证时的错误；长运行与 MCP 客户端超时；FAT 报告中的审计链核对。 |
| TIA-TEST（beta.6）PLC 测试用例库、批量运行、结果历史、JUnit、FAT/SAT 与复现报告（八版；Test Suite 条目 V17+，系统测试 V18+，测试集 V20+） | **NOT RUN**。离线测试只证明文件格式、判定映射、退出码和报告文本。需在装有 PLCSIM Advanced（含许可证）的机器上：保存并运行一条通过和一条失败的场景，核对历史、junit.xml 与退出码；在 V17–V21 工程副本上运行 application 用例、V18+ system 用例、V20+ 测试集，核对多用例时 TestResults 消息树与条目判定是否一致、Warning 判为失败、缺许可证时的错误；长运行与 MCP 客户端超时；FAT/SAT 报告中的审计链核对、签署栏和不完整运行标记；复现报告的原文、调用去敏与路径隐私；`tia test report` 的退出码和 JUnit 文件。 |
| ECO-S S2 导出去易变节点（八版） | **NOT RUN**。各版工程副本上以 `stripVolatile=true` 导出块、类型、变量表，两次导出字节一致；把规范化后的文件导入另一个工程副本（覆盖与新建各一次），确认导入成功、编号与接口不变，Engineering 版本未改。每种对象保留未规范化导出与规范化导出文件，对 `DocumentInfo` 之外的字节做 diff 并记录全部差异（包括编码、换行及 XML 序列化变化）；增加一个 F-block 或依赖选件包的对象用例，记录选件、TIA 补丁、导出范围和往返结果，不把接口导出误报为完整程序备份。 |
| ECO-S S4 经典 PLC 模式示例库（八版；S7-1200/S7-1500；LAD 仅 V21） | **NOT RUN**。S4a 已有启停、TON/TOF/TP、计数、正反转、报警锁存与模拟量缩放六个 SCL 候选；后续 S4b/S4c 增补其余 SCL 与 LAD。各版先 V21，再 V20 至 V14 SP1，在一次性工程副本中各放一台 S7-1200、S7-1500，记录订货号、固件与 TIA 更新。逐模式经 `StageImportFiles` → `ImportPlcExternalSource` 预览与执行 → `GenerateBlocksFromExternalSource` 预览与执行 → `CompilePlcDiagnostics`，记录 UDT/FB/FC/DB、编译错误与警告、导出指令版本及 SHA-256。核实 `pattern.json` 中未核实的指令名称与版本，填写确切官方手册页；某版或家族没有指令时保留原生消息与官方出处并记为 absent。后续 Modbus 按家族分变体，MC_Power 仅 S7-1500；LAD 仅 V21，先导入 `BuildPlcGlobalDb` 实例 DB，再导入 `BuildPlcLadFcBlock` 输出并编译。负责人填写 `reference/plc-patterns/evidence.json` 并重新生成后才提供已满足门禁的模式；PLC 运行行为 NOT RUN。 |
| TIA-TEST（beta.6）PLC 测试用例库、批量运行、结果历史、JUnit、FAT/SAT 与复现报告（八版；Test Suite 条目 V17+，系统测试 V18+，测试集 V20+） | **NOT RUN**。离线测试只证明文件格式、判定映射、退出码和报告文本。需在装有 PLCSIM Advanced（含许可证）的机器上：保存并运行一条通过和一条失败的场景，核对历史、junit.xml 与退出码；在 V17–V21 工程副本上运行 application 用例、V18+ system 用例、V20+ 测试集，核对多用例时 TestResults 消息树与条目判定是否一致、Warning 判为失败、缺许可证时的错误；长运行与 MCP 客户端超时；FAT 报告中的审计链核对。 |
| API-P1c 型号替换与项目子网（beta.6） | **NOT RUN**。分别用 V21、V18 项目副本，更换 CPU 和信号模块：同系列较新固件及不同订货号各一次；核对原路径重新解析后的 TypeIdentifier、地址/模块/软件影响及随后编译结果。无锚点创建 Ethernet（`System:Subnet.Ethernet`）和 PROFIBUS（`System:Subnet.Profibus`）子网，回读 Name/TypeIdentifier/NetType；删除含两个已连接节点及 IO system 的子网，预览核对节点数、接口路径和 IO system 名称，执行后确认子网不存在并重新读取节点状态。**V17 拒绝核对**：在 V17 项目副本上预览和执行 changeType，均须在原生访问前返回 UNSUPPORTED_CAPABILITY，确认项目未改变。七个动作均核对预览、计划哈希、目标项目与 WRITE 审批，拒绝缺失/过期计划，保留调用顺序及 worker 所属线程；型号替换失败不重试、不自动撤销。**显式保存核对**：完成上述 V21、V18 回读和编译后，分别独立调用 SaveProject 显式保存项目副本，记录保存结果；ManageHardwareObject 不自动保存。 |
TIA-BATCH-1：**NOT RUN**。在 V14 SP1 或 V15.1 与 V21 工程副本上，用一次 PreviewToolBatch/ApplyToolBatch 删除 3 个块、新建 5 个变量；V21 再批量建 3 个 Unified 变量；确认需要计划哈希的工具在批处理中直接执行、第一个失败项整体回滚且后续项为 NOT_EXECUTED；记录请求 ID 与原生消息。还需确认：带自身计划哈希的工具在逐项执行时复核并拒绝变更的输入文件；ImportPlcTagTable 及现有不带自身计划哈希的批量导入只在批次复验证阶段检查输入文件，复验证之后的变化不受逐项计划哈希保护。
API-P1b（beta.6）：**NOT RUN**。在 V21、V18 的一次性工程副本中核对现有监控表组的 createTable 预览、计划哈希和项目绑定执行、名称与数量读回、同组 watch/force 重名拒绝及缺失父组拒绝；验证 force 目录导出的整批预览、tableKind/清单/项目绑定哈希、单次审批、审批后复查与首错停止 → 导入另一现有组 → 按返回名称重新查找，watch XML 按 force 导入必须拒绝，保留原文件与失败后的工程状态。确认全程没有在线监视、激活强制、下载或自动保存；最后由测试者显式保存。Part B 已实现 block/group/unit 范围编译，原生验收仍为 **NOT RUN**。负责人 Step 0 仅在 V21 确认 PlcBlock.GetService<ICompilable>() 返回 CompileProvider，具有 Compile()；V18 及组/单元服务尚未探测。各版按 SDK XML 实现，接口存在不等于运行时服务可用。负责人须在 V21/V18 核对 block/group/unit 各范围预览的 compilable、精确对象、MIN-ESCAPE 路径和单元根隔离，验证不支持时拒绝且不回退整 PLC；执行保持 confirm + expectedProjectFile（无计划哈希、无 WRITE 审批）、两轮离线检查、完整嵌套诊断和 Safety 登录/清理顺序。Step 0b 某范围在 V21 返回 compilable=false 时，由负责人按任务决定下一轮移除对应枚举。V14 SP1/V15.1 单元范围须在原生访问前拒绝。

VMFIX-3 Round 2：**NOT RUN**。V14 SP1、V15.1、V16、V17、V18、V19、V20 的 ImportDeviceAml 保留原有 ExclusiveAccess 路径；需分别在工程副本上确认导入、失败后的工程状态及设备计数。只有 V21 按已记录的 reset 13 证据在 ExclusiveAccess 外导入，仍需复测。库文档 Override、带目标环境及非 None 选项的失败保持未知结果，不能只凭类型/版本数量未变判为未写入。

VMFIX-3（2026-11-03）：**NOT RUN**。需在 V21 工程副本上复验 ExclusiveAccess 占用拒绝、通道属性未改变读回、三类密码策略拒绝、无 ExclusiveAccess 的 AML 导入、库类型/版本导入失败详情和数量读回、文件夹中的 Unified 屏幕及无名部件创建、文档导入失败后的声明名称查找、PROFINET 连接、报警文本导出、HMI 输入拒绝、未知写入的会话锁存、worker 执行证据、标签注释、强制表列表及下载预览。库导入仍保留 ExclusiveAccess；其他导入族的 ExclusiveAccess 兼容性留待 VM 回归。主题负载和标签绑定功能修复由 VMFIX-5b 负责。本任务仅使用离线与本机合成夹具，没有连接 TIA Portal、PLC 或 VM。

VMFIX-2（已合并，2026-11-02 复验清单）：**NOT RUN**。在 V21 工程副本上复验报警、OPC UA、SiVArc、Startdrive/DCC、Safety 的正常、不支持与失败回复，确认后续读取无需重置；复验 8 个 Unified 分页读取工具的目录示例及续页、事件脚本正文、分组 PLC/HMI 的名称与项目树路径探测、缺失目标及 HMI 快捷操作的原生失败原因。本任务仅使用离线夹具，没有连接 TIA Portal、PLC 或 VM。

下列改动已合并到 master，只有离线证据；正式版本发布前必须在真机上完成并记录，否则不得发布（预发布见上方例外）。

| 改动 | 发布前的真机验收 |
|---|---|
| TIA-GAPS A3 LAD 梯形图网络 JSON 生成（V21；A3a） | **NOT RUN**。在 V21 工程副本先导入数据块（沿存储位、操作数）并定义全局常量，再导入 `BuildPlcLadFcBlock` 生成的 FC，覆盖常开、常闭、上升沿、下降沿、六种比较、OR 组、共用前段分支、普通/置位/复位线圈、MOVE 与 ENO 串接、全局常量及数组下标路径。经 `StageImportFiles` → `ImportPlcProgramFromDirectory` 预览与绑定计划执行 → `CompilePlcSoftware` 块编译无错误 → `ExportPlcBlock` 导出 → `RenderPlcBlock` 读回，逐网络核对，记录 TIA 补丁、原生消息、导出 XML 及其哈希；将导出 XML 作为第一份带来源的真实夹具提交。核对 ENO 未连接时 `DisabledENO`、数组索引无 `ConstantType` 的写法。A3b 的定时器/计数器/命名引脚调用与 A3c 的其他输出格式另行验收；V14 SP1–V20 保持未注册，只有对应版本的 VM 导入编译记录获确认后才开放。 |
| TIA-GAPS A1 Unified 脚本模块新建（V19–V21） | **NOT RUN**。在 V19、V20、V21 工程副本上：预览、执行新建，确认 `Import` 接受只有 `ScriptModules/<名称>/ScriptFile` 的 `.hmi.yml`、模块出现在 TIA、导出读回文本一致；同名（含大小写不同）拒绝；导入返回 false 时工程状态。Openness 没有删除脚本模块的接口，测试后在 TIA 中手工删除。 |
| TIA-GAPS A2 Unified 嵌套运行设置（V19–V21） | **NOT RUN**。各版工程副本上读取全部嵌套字段；写入 OPC UA 端口与会话数、登录锁定次数、报表存储、V20+ 遥测/UPSS/变量设置、V21 独占操作变量，确认读回、失败时恢复、整数越界与枚举名拒绝；确认嵌套对象本身没有被替换。核对整个工具的预览令牌、计划哈希和工程文件绑定，以及通用工具对 RuntimeSettings 子树写入的拒绝。 |
| TIA-GAPS B 次工程打开与关闭（V15.1–V21） | **NOT RUN**。在已绑定主项目时以 Secondary 打开另一个项目副本，确认 `IsPrimary=false`、TIA 界面不显示、只读（写入被拒）、`CompareProjects targetProjectName` 可用；`CloseProject secondaryProjectFile` 只关闭次项目且主项目状态与绑定不变，不保存；主项目关闭与 `DisconnectPortal` 在次项目打开时被拒绝并提示先关闭次项目。使 worker 丢失并恢复，核对次项目路径、持有 TIA PID 与启动时间记录保留，换到其他 TIA 时关闭被拒且记录保留；恢复到同一持有进程后只按该路径且 `IsPrimary=false` 关闭，同进程中路径已不存在时清除记录且 `Executed=false`。持有进程确认退出或 PID 已复用后，恢复与断开直接清除记录，不启动 TIA；持有进程存活或身份未知时仍拒绝断开。Studio 连接仅有次项目的 Portal 时不自动绑定且不抛异常。配置项目 preset 后，次项目打开、关闭与比较不改变主项目写绑定；把次项目作为后续写入目标被拒绝。覆盖主、次项目同名副本的精确路径比较、拒绝将次项目绑定为活动项目、带 UMAC 的次项目及八版项目概览序列；离线夹具和 SDK 编译不代替真机验收。 |
| Unified 连接、变量表删除与报警文本（API-P1d，beta.6） | **NOT RUN**。在 V19、V21 工程副本上预览并按计划哈希/工程文件执行连接删除，核对绑定到该连接的变量；删除含变量的 `/Diagnostic/DiagInfo` 表，核对预览变量数、路径与删除后读回。V21 执行图形列表导出 → 删除 → 导入 → 读回，确认列表不能 Create。一次创建含触发变量/位、报警类别及两种语言文本的报警；记录新报警是否每种项目活动语言都有文本项，并核对只读 TriggerBitAddress/TriggerAddress。用 PreviewToolBatch/ApplyToolBatch 注入一项文本写入失败，确认先前调用保留、失败调用回滚、后续未执行及 cleanupRequired；同时覆盖更新回滚。核对整个工具（含原有动作）的计划绑定、TIA UI 读回并显式保存。AlarmParameterTags 仍待主导代理的 VM 类型/可写形状探针，本改动不提供 parameterTags 或 items。 |
| API-P1a 库对象实例化（beta.6） | **NOT RUN**。在 V21 与 V18 的一次性工程副本上，分别从项目库及已打开的全局库对 block、blockGroup、tagTable、tag、device、classicScreen、classicTagTable 各做一次预览与绑定计划执行；确认已发布代码块、Classic 屏幕类型版本的两条 instantiate 路径。V21 另验无 copyMode 的同名拒绝及 Rename/Replace、updatePathsMode、逐项读回、多对象部分创建失败回滚；Replace 原对象无法自动恢复时核对 cleanupRequired。显式保存，记录请求 ID、TIA 补丁和原生消息。已有 copy/delete/edit/release 等变更动作也需预览哈希及工程身份；不把 SDK 编译视为实机通过。 |
| TIA-SAFE 在线显式地址、地址白名单、下载安全、始终确认、机密保管库（八版；V14 SP1 见 D3） | **NOT RUN**。V15.1–V21 各版：ConnectOnlinePlc 只用显式 ipAddress，V15.1–V20 先 ApplyConfiguration 再 GoOnline()，V21 用 GoOnline(ConfigurationAddress)；R/H 在 V15.1–V20 先 ApplyConfiguration 再 GoOnlineToPrimary/Backup()；不在白名单的地址在审批与 worker 之前被拒绝；DownloadPlc 不带 targetIpAddress 被拒绝；PLCSIM 接口与真实网卡的分类、confirmPhysicalTarget、审批关闭时真机下载仍弹审批卡；编译后 IsModified 是否为 true、未保存与不一致工程的拒绝；vault 引用的密码送达在线认证提示且不出现在响应、日志与审计中。八版运行时 S7 的 ip、Web API 的 host（含端口）、OPC UA endpointUrl 主机也复用白名单：未列出或非 IPv4 的地址须在审批、worker 与套接字之前拒绝；配置为空或不可读时拒绝；未配置时保持原行为；PLCSIM Advanced 与 Unified Open Pipe 不受此地址检查影响。工作台编辑白名单、计划有效期和保管库后由八版宿主读回；审批等待时 GetApprovalStatus 的会话隔离及决定回显；密码框不读回值、审计不含机密值。 |
| TIA-SAFE 在线显式地址、地址白名单、下载安全、始终确认、机密保管库（八版；V14 SP1 见 D3） | **NOT RUN**。V15.1–V21 各版：ConnectOnlinePlc 只用显式 ipAddress，V15.1–V20 先 ApplyConfiguration 再 GoOnline()，V21 用 GoOnline(ConfigurationAddress)；R/H 在 V15.1–V20 先 ApplyConfiguration 再 GoOnlineToPrimary/Backup()；不在白名单的地址在审批与 worker 之前被拒绝；DownloadPlc 不带 targetIpAddress 被拒绝；PLCSIM 接口与真实网卡的分类、confirmPhysicalTarget、审批关闭时真机下载仍弹审批卡；编译后 IsModified 是否为 true、未保存与不一致工程的拒绝；vault 引用的密码送达在线认证提示且不出现在响应、日志与审计中。在线写入计划到期（含审批等待期间到期）与按会话隔离；ConnectOnlinePlc / DownloadPlc 的本机网卡、子网匹配与无匹配警告（路由网络、PLCSIM Softbus），均 **NOT RUN**。 |
| TIA-SAFE 在线显式地址、地址白名单、下载安全、始终确认、机密保管库（八版；V14 SP1 见 D3） | **NOT RUN**。V15.1–V21 各版：ConnectOnlinePlc 只用显式 ipAddress，V15.1–V20 先 ApplyConfiguration 再 GoOnline()，V21 用 GoOnline(ConfigurationAddress)；R/H 在 V15.1–V20 先 ApplyConfiguration 再 GoOnlineToPrimary/Backup()；不在白名单的地址在审批与 worker 之前被拒绝；DownloadPlc 不带 targetIpAddress 被拒绝；PLCSIM 接口配真实子网地址仍按真机、运行中 PLCSIM Advanced 实例地址匹配与独占虚拟网卡子网的分类、confirmPhysicalTarget 的宿主与 worker 双重守卫、审批关闭时真机下载仍弹审批卡；编译后 IsModified 是否为 true、未保存与不一致工程的拒绝；vault 引用的密码送达在线认证提示且不出现在响应、日志与审计中。在线写入计划到期（含审批等待期间到期）、超过半有效期后重新预览签发新计划与按会话隔离；ConnectOnlinePlc / DownloadPlc 仅报告 Up 且匹配目标子网的非 VPN/环回网卡与无匹配警告（路由网络、PLCSIM Softbus），均 **NOT RUN**。 |
| TIA-SAFE 在线显式地址、地址白名单、下载安全、始终确认、机密保管库（八版；V14 SP1 见 D3） | **NOT RUN**。V15.1–V21 各版：ConnectOnlinePlc 只用显式 ipAddress，V15.1–V20 先 ApplyConfiguration 再 GoOnline()，V21 用 GoOnline(ConfigurationAddress)；R/H 在 V15.1–V20 先 ApplyConfiguration 再 GoOnlineToPrimary/Backup()；不在白名单的地址在审批与 worker 之前被拒绝；DownloadPlc 不带 targetIpAddress 被拒绝；PLCSIM 接口配真实子网地址仍按真机、运行中 PLCSIM Advanced 实例 controllerIP 数组匹配与独占虚拟网卡子网的分类（须同时满足允许接口、所有状态的非 PLCSIM 子网不覆盖目标；碰撞实例地址和显式真实接口不得获得模拟器豁免；PLCSIM 掩码连续且至少 /8）、confirmPhysicalTarget 的宿主与 worker 双重守卫、审批关闭时真机下载仍弹审批卡；编译后 IsModified 是否为 true、未保存与不一致工程的拒绝；vault 引用的密码送达在线认证提示且不出现在响应、日志与审计中。在线写入计划到期（含审批等待期间到期）、超过半有效期后重新预览签发新计划与按会话隔离；ConnectOnlinePlc / DownloadPlc 仅报告 Up 且匹配目标子网的非 VPN/环回网卡与无匹配警告（路由网络、PLCSIM Softbus），均 **NOT RUN**。 |
| TIA-SAFE 在线显式地址、地址白名单、下载安全、始终确认、机密保管库（八版；V14 SP1 见 D3） | **NOT RUN**。V15.1–V21 各版：ConnectOnlinePlc 只用显式 ipAddress，V15.1–V20 先 ApplyConfiguration 再 GoOnline()，V21 用 GoOnline(ConfigurationAddress)；R/H 在 V15.1–V20 先 ApplyConfiguration 再 GoOnlineToPrimary/Backup()；不在白名单的地址在审批与 worker 之前被拒绝；DownloadPlc 不带 targetIpAddress 被拒绝；PLCSIM 接口配真实子网地址仍按真机、运行中 PLCSIM Advanced 实例 controllerIP 数组匹配与独占虚拟网卡子网的分类（须同时满足显式 PLCSIM 接口、所有状态的非 PLCSIM 子网不覆盖目标；空接口即使与运行中实例同 IP 也按真机，包含实例 192.168.0.1 与真实网卡 192.168.0.130/25 经网关的场景；碰撞实例地址和显式真实接口不得获得模拟器豁免；PLCSIM 掩码连续且至少 /8）、confirmPhysicalTarget 的宿主与 worker 双重守卫、审批关闭时真机下载仍弹审批卡；编译后 IsModified 是否为 true、未保存与不一致工程的拒绝；vault 引用的密码送达在线认证提示且不出现在响应、日志与审计中。在线写入计划到期（含审批等待期间到期）、超过半有效期后重新预览签发新计划与按会话隔离；ConnectOnlinePlc / DownloadPlc 仅报告 Up 且匹配目标子网的非 VPN/环回网卡与无匹配警告（路由网络、PLCSIM Softbus），均 **NOT RUN**。 |
| TIA-SAFE 在线显式地址、地址白名单、下载安全、始终确认、机密保管库（八版；V14 SP1 见 D3） | **NOT RUN**。V15.1–V21 各版：ConnectOnlinePlc 只用显式 ipAddress，V15.1–V20 先 ApplyConfiguration 再 GoOnline()，V21 用 GoOnline(ConfigurationAddress)；R/H 在 V15.1–V20 先 ApplyConfiguration 再 GoOnlineToPrimary/Backup()；不在白名单的地址在审批与 worker 之前被拒绝；DownloadPlc 不带 targetIpAddress 被拒绝；PLCSIM 接口与真实网卡的分类、confirmPhysicalTarget、审批关闭时真机下载仍弹审批卡；编译后 IsModified 是否为 true、未保存与不一致工程的拒绝；vault 引用的密码送达在线认证提示且不出现在响应、日志与审计中；同一 Windows 用户的保管库引用在审批卡和计划中保持可读，未知名称、损坏记录与其他用户无法解密的记录在审批及 worker 之前拒绝，原生认证失败回显去敏，明文参数仍兼容。Part 3 Round 3 另验只读在线工具、CallTool 和批次子项的保管库审批不能关闭；Web 预览与审批预检不调用 Api.Login。V17–V21 核对项目 UmcServer 的动态 ServerAddress/ServerName/Address 是否提供实际服务器；不可识别时在审批前拒绝，审批期间变化时不发送密码。此元数据读取不能触发 UMC Authentication；runtime-channel 地址白名单另由 Part 5 验收。Part 3 Round 4 的 UMC vault 路径仍 **NOT RUN**：kind=server/action=checkConsistency 的默认预览与审批预检不得调用 CheckConsistency 或登录；审批执行只调用一次并核对认证事件、凭据清理及响应去敏。OpenProject/RetrieveProjectArchive 的 Global UMAC 用户须显示目标项目及该项目实际 UMC 服务器；未能在无登录条件下识别目标服务器时须在审批前拒绝，不能借用当前其他项目的服务器。 |
| SU-1 软件单元与导入选项（V16–V21；SW 选项从 V15.1） | **NOT RUN**。用各版工程副本确认精确单元名、相同名称对象的根/单元隔离、嵌套相对组、单块/类型/标签表与目录往返、空/不存在/大小写不同单元、SW 选项组合及两种文化选项的实际语义、覆盖备份与恢复、未知结果、树/变量/常量/交叉引用导出范围、Published/Unpublished 的预览/审批/Access 回读及 OB 拒绝。核对调用顺序、原 STA/MTA 线程、原 XML 字节与编号、TIA 补丁；显式保存。SDK 编译、合成夹具与快照不证明这些原生语义。 |
| DES-2 Rounds 1–4（2026-11-02）批处理与安全移块 | **VM pending / NOT RUN**。在工程副本确认四个批工具的整批预览与单一哈希/审批、逐项规范调用、首错停止和未开始项。移块核对 WithDefaults 备份与 XML 身份→删除源块→目标组 None 导入→父组/名称/编号/语言核验；不一致的导入保留成功并提示编译。目标核验失败时先删除该导入块，再恢复原组；清理失败则不恢复，保留恢复文件并报告唯一已验证副本。覆盖原组恢复失败、审批期间计划变化、源一致性准入与恢复文件保留。未确认前不标记原生验收通过。 |
| XREF（2026-10-30）V21 原生交叉引用确认 | **pending / NOT RUN**。V14 SP1–V20 在 API 可用时默认允许是维护者决策，不是真机稳定性证明。V21 保持人工会话开关或环境变量 opt-in；首次查询前保存，编译无安全保证。待测试者在真实 V21 工程上记录补丁、查询/删除预览结果与 TIA 日志后再决定默认值。 |
| 监控/强制表与工艺对象（P4-I2） | **NOT RUN**；八版按实际目录入口验收当前 FoundationHost → PLC worker → adapter 路径。核对监控表导出、覆盖导入、读回与删除，强制表受支持操作，以及工艺对象的遍历、导入导出、创建、参数修改和删除。覆盖根组、嵌套组、精确路径、失败后工程状态及切换工程；在线监视另需授权。保留线程、原生参数和调用顺序证据，不比较已退役引擎变体。 |
| 硬件目录与设备创建（P4-I3） | **NOT RUN**；八版按当前工具目录和对应 SDK 能力验收。核对目录查询、精确 TypeIdentifier、重复或不存在的候选、设备创建预览与确认、Unified 面板版本保护；回读完整设备清单、创建次数与失败后状态。V14 SP1–V17 的 CreateDevice 保留入口，不以较新版工具可用性替代其验收。记录宿主/worker/adapter 哈希、TIA 补丁、线程与原生调用顺序。 |
| PLC 块与编译（P4-I4a） | **NOT RUN**；八版按实际入口验收块/类型组、保护、导入导出、移动及软件/设备编译。覆盖精确目标、空组、重名、不一致块、OB 移动拒绝、失败后的工程状态与恢复文件。核对编译消息、Safety 登录清理、原生调用顺序及所属线程；离线测试不替代真实读回。 |
| VCI（P4-I1） | **NOT RUN**；仅在对应 SDK 和项目暴露 VCI 服务时验收当前路径。覆盖工作区与映射、状态、同步方向、dryRun、缺失文件、部分失败及工程切换后的句柄失效；回读工作区文件和映射，记录调用顺序、线程、诊断及显式保存结果。不再要求比较已退役引擎的开关变体。 |
| PLC 文档与外部源（P4-I4b） | **NOT RUN**；八版按目录和 SDK 能力验收单块/批量文档往返、配对文件、覆盖与编号恢复、外部源导入/生成/删除及组操作。覆盖不支持选项、返回身份不符、部分成功、文件失败与工程切换；核对文件、对象、调用顺序、线程和实际状态。失败后不自动重试未知写入。 |
| Studio 可选共享适配器路径（P4-G2） | **NOT RUN**；TiaSharedAdapterPaths 仍是 Studio 编译开关，默认 false，prepare 明确设为 false；它不再选择引擎路径。八版在可选路径启用前，以同版工程副本验收桥接会话生命周期、PLC/HMI 浏览、导入导出、编译、VCI（按能力）、STA 与回调重入、错误和部分结果、句柄失效及清理。当前工作台项目页的 MCP 操作端路径另按 P6-PRODUCT 验收。 |

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

All eight releases use FoundationHost with a PLC worker per MCP session. The V20/V21 migration checks below remain **NOT RUN** until performed on the exact candidate with the corresponding TIA version and a disposable project copy. Offline startup tests do not satisfy them; prereleases follow the exception above.

| releaseKey | State | Required isolated-mode acceptance |
|---|---|---|
| 20 | **NOT RUN** | On the V20 VM, connect to the intended TIA process and explicitly bind a disposable project copy; verify project identity and read state. Import one fixture and export it back to a separate directory; read back both sides. Compile the selected PLC software and record full nested diagnostics. Induce the approved worker timeout/fault fixture, inspect actual project state, restart the idle worker, connect and explicitly bind the project again, then read back state. |
| 21 | **NOT RUN** | Repeat the same sequence on the V21 VM using a V21 project copy and version-matched import/export fixtures. Connect and explicitly bind, verify project identity, import and export with read-back, compile and retain nested diagnostics, induce worker timeout/fault, inspect state, restart, explicitly rebind, and read back again. |

For each row record Windows/TIA build and patch, host/worker/adapter SHA-256, test project copy identity, worker mode and timeout, tool arguments with credentials removed, MCP request IDs, before/after project state, call ordering, thread apartment, restart and rebind evidence, import/export output hashes, compile errors/warnings and nested messages, logs, and whether the project was explicitly saved. A timeout/crash after worker dispatch is not a pre-operation refusal; never repeat an operation until project state is inspected. Do not use a production project or connect to a PLC for this gate.

The shipped host already uses the PLC worker on both keys; there is no current `workerIsolation.enabledByDefault` switch. The pending rows describe native acceptance, not an opt-in execution route. Retain any failures and inspect the project before further writes.

## 4.0 安全策略验收计划（P6-R2）

以下为[4.0 规范](../archive/development/phase6-review.md)的新增验收项目，**全部 NOT RUN**，仅登记计划，不授权连接 TIA、PLC、VM 或网络。
每行按实际广告能力逐版本记录，八个发布键为 14sp1、15.1、16、17、18、19、20、21；无该能力的版本记录 N/A 并附目录证据，不能跳过而标记通过。
行为能力表当前仍标为 current / NOT RUN。该版本/族完成真实回放前不能声称 safe-v4 已验收；正式版门槛与预发布例外见上文。
共用证据要求：精确 SDK/TIA 补丁、工程副本/目标身份、宿主/worker/adapter 哈希、前后调用顺序及参数、MTA/STA 所属线程、关联 ID、预览哈希、故障后实际状态、回读/编译及是否显式保存。

| ID / 行为族 | 状态 / 版本范围 | 验收项目与切换门槛 |
|---|---|---|
| P6-DEVICE 设备创建 | **NOT RUN**；19/20/21 的硬件创建入口，其余创建能力按目录逐版 | 精确目录 TypeIdentifier、型号与版本能力、重名、目录不存在、预览不创建；apply 原生 Create 恰一次；确认/哈希/工程身份变化拒绝；Create 前/中/后故障与残留检查，禁止换候选重试。 |
| P6-IMPORT 导入 | **NOT RUN**；八版普通 PLC；SD/HMI/软件单元按目录 | 默认拒绝覆盖及升级/版本改写；不改 BOM/源文件；同版块/UDT/tag-table 回读；明确支持的覆盖参数才执行；非法目标、锁定文件、批次第一个/中间失败、文件在 preview 后改变；返回成功/失败/未执行逐项证据，不回滚或重放未知写入。 |
| P6-EXPORT 导出 | **NOT RUN**；八版普通 PLC；SD/专用对象按目录 | 预览不写目的地；单项/批量暂存发布、原文件/目录保全；拒绝覆盖、显式支持的覆盖、磁盘/权限/发布冲突；内容/路径回读、partial staging 保留；不扩大 SD、普通块/GlobalDB、软件单元与版本范围。 |
| P6-SESSION connect/open | **NOT RUN**；八版 | PID/start time 重用与工程切换拒绝；附加不等于绑定、连接不启动；已有工程 reuseOpen=false；旧工程 upgrade=reject；明确允许升级时只改测试副本；失败不关闭陌生/借用工程；验证所有权和会话纪元。 |
| P6-CLOSE save/close | **NOT RUN**；八版 | 普通 Project 与 LocalSession 分别保存回读；借用工程拒绝关闭；脏工程先显式保存或独立 discard 预览确认；不隐式保存/关闭；失败后的绑定、对象有效性与工作进程清理。 |
| P6-SOURCE PLC 路径/外部源 | **NOT RUN**；八版；G9 两版基线同时回放 | 唯一 PLC 空路径、多个 PLC 候选、非空错误名、结构名称映射和转义路径；读写选中同一目标；源导入/生成/删除顺序、精确名称/扩展名、缺失删除拒绝、批次故障；14sp1 observation 与其他版原生结果分列；切工程后缓存失效，未知写入不重放。 |
| P6-COMPILE 编译/Safety | **NOT RUN**；八版普通 PLC；HMI/Safety 按能力 | 在线前提拒绝且无自动下线；每个编译入口目标范围和诊断树/根计数一致；本次 Safety 登录仅由本次结束，原有登录不退出；登录/编译/注销分别故障，保留清理证据及真实工程状态；不改变 MTA/STA。 |
| P6-FALLBACK 原生回退 | **NOT RUN**；20/21 在线下载及 VCI；其他版共享 VCI 路径按能力 | ApplyConfiguration 失败后不下载或换路线；自动下线分支改为前置条件拒绝；传输/通道每个中断点记录调用是否发出与真实结果；unknown 要求重建会话、不自动重放；VCI 仅显式允许且证明只读对象失效时重取一次，已写或未知时停止。 |
| P6-PRODUCT 新产品启动/绑定 | **NOT RUN**；八版宿主、PLC worker 与 Studio | 仓库外完整包从根 TiaOpenness.exe 打开 Studio；选择版本启动正确新 EXE/worker/adapter；逐版 hello 身份与绑定、读取、显式断开；新目录日志可关联，安装目录只读；不借用历史机器/PID 授权。 |

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
宿主/worker/adapter 哈希、完整日志、错误后的工程状态、回读和显式保存结果须登记。失败即停止正式版本发布（预发布见上方例外）。

| 门槛 | 必须完成的机器步骤 |
|---|---|
| P6-PRODUCT（14sp1、15.1、16、17、18、19、20、21） | 用仓库外的完整 4.0 候选包启动根 `TiaOpenness.exe`；逐版选择对应 TIA；核对 hello 的 releaseKey 和新 FoundationHost/PlcWorker/adapter 身份；连接该 VM 当前 TIA 进程并显式绑定测试工程副本；读取工程树和一个 PLC 对象；显式断开并确认本次 worker/桥接退出；检查新目录日志可关联、只读安装无包内写入，且不借用历史 PID/机器授权。 |
| P4-E1 / P4-E2 Foundation worker（14sp1、15.1、16） | 每版用该版本真实 TIA 打开工程副本；启动 net48 worker，核对协议 2 hello 身份；读 PLC 列表和块；核对错误后会话仍可用；显式断开并确认 worker 退出。 |
| P4-F Studio bridge（14sp1、16、21） | 用 Studio 连接对应真实 TIA、打开工程副本、读取块列表、导出一个块；触发一次已处理错误并确认原错误文本及会话恢复；断开并确认桥接退出。 |
| G3 / P4-G2 Studio 可选共享会话路径（八版） | 在 TiaSharedAdapterPaths 启用前，对八版保留的 Studio 桥接路径分别比较默认与可选共享适配器：附加/启动、打开/保存/关闭、PLC/HMI 浏览、导入导出、编译和按能力提供的 VCI；核对 STA、回调、错误、句柄失效及清理。此开关不影响已退役引擎。 |
| P4-I1 VCI（按各版 SDK 与项目能力） | 当前 FoundationHost/PLC worker 路径逐工具验收工作区、映射、状态和同步；覆盖 dryRun、部分失败、工程切换及文件读回，不比较已退役引擎变体。 |
| P4-I2 监控/强制表与工艺对象（按各版目录） | 当前路径验收监控表导出/覆盖导入/删除、强制表受支持操作与工艺对象往返；检查目标、消息、计数、失败后状态和工程切换。在线监视另需授权。 |
| P4-I3 硬件目录与设备创建（八版，按各自入口） | 核对精确 TypeIdentifier、目录候选、版本拒绝与预览/确认；回读新设备、原生创建次数和失败状态，显式保存。 |
| P4-I4a 块与编译（八版） | 当前路径回放块/类型组、保护、导入导出与软件/设备编译，核对线程归属、诊断和失败后不重放；OB 移动仍拒绝。 |
| P4-I4b 文档与外部源（八版） | 按版本能力核对往返、拒绝、精确寻址、覆盖、生成/编译、编号恢复和部分完成；回读文件/工程并显式保存，不比较旧引擎变体。 |
| J 可选 Studio 路径后续清理 | Studio 可选路径尚待上述八版验收；TiaSharedAdapterPaths 默认保持 false。旧引擎已删除，不能再以等待此验收描述引擎退役。后续变更仍须运行适用构建、功能测试、工具定义契约和包检查；逐工具响应捕获已退役。 |

<!-- behavior-capabilities:start -->

逐版行为能力由 `generate-catalogs` 从上面的 L5 台账与实际入口目录生成。空入口数组表示该宿主无此族 MCP 入口；不授予原生验收。

| releaseKey | family | state | L5 | entries |
|---|---|---|---|---|
| 14sp1 | P6-DEVICE | current | NOT RUN | CreateDevice |
| 14sp1 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 14sp1 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 14sp1 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 14sp1 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject |
| 14sp1 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 14sp1 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 14sp1 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware |
| 14sp1 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 14sp1 | F22 | current | NOT RUN | CompareSoftwareToOnline, DisconnectOnlinePlc, DisconnectOnlinePlcs, GetOnlineState, ListTransferRoutes |
| 14sp1 | F31 | current | NOT RUN | GetDriveParameters, ListDriveObjects, ManageDriveTelegrams, ManageStartdriveParameter |
| 14sp1 | F00 | current | NOT RUN | — |
| 14sp1 | F34 | current | NOT RUN | InitializeSimotionScripting |
| 14sp1 | F05 | current | NOT RUN | BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, ValidateAutomationContext |
| 14sp1 | F33 | current | NOT RUN | ManagePlcGitRepository |
| 14sp1 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 14sp1 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 14sp1 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, PlanGlobalLibraryTemplateReuse |
| 14sp1 | F06 | current | NOT RUN | CompareProjects, ExportProjectTexts, GetProjectSettings, ImportProjectTexts, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 14sp1 | F23 | current | NOT RUN | CheckLibraryUpdates, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 14sp1 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 14sp1 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 14sp1 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 14sp1 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcUserGroup, MovePlcBlockToGroup |
| 14sp1 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference |
| 14sp1 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, ManagePlcExternalSources |
| 14sp1 | F12 | current | NOT RUN | PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, TraceTagCause, TraceTagCauseLive |
| 14sp1 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ManageMotionAxis, ManageTechnologyObject |
| 14sp1 | F10 | current | NOT RUN | GetPlcBlockScopes |
| 14sp1 | F14 | current | NOT RUN | — |
| 14sp1 | F15 | current | NOT RUN | — |
| 14sp1 | F16 | current | NOT RUN | — |
| 14sp1 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 14sp1 | F18 | current | NOT RUN | CreateDevice, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 14sp1 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ManageIoSystem, ManagePortInterconnection, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 14sp1 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 14sp1 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 14sp1 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject |
| 15.1 | P6-DEVICE | current | NOT RUN | CreateDevice |
| 15.1 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 15.1 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 15.1 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 15.1 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 15.1 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 15.1 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 15.1 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, DownloadPlc |
| 15.1 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 15.1 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, GetOnlineState, ListTransferRoutes, ManagePlcDataBlockSnapshot, UploadStationFromPlc |
| 15.1 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcRules, ManageSivarcRule |
| 15.1 | F31 | current | NOT RUN | GetDriveParameters, GetOnlineDriveParameters, ListDriveObjects, ManageDriveFunctions, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter |
| 15.1 | F00 | current | NOT RUN | ExportPlcWatchTable |
| 15.1 | F34 | current | NOT RUN | InitializeSimotionScripting |
| 15.1 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 15.1 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, ManagePlcProtection, SetPlcPutGetAccess |
| 15.1 | F33 | current | NOT RUN | ManagePlcGitRepository |
| 15.1 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 15.1 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 15.1 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, PlanGlobalLibraryTemplateReuse |
| 15.1 | F06 | current | NOT RUN | CompareProjects, ExportProjectTexts, GetProjectSettings, ImportProjectTexts, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 15.1 | F23 | current | NOT RUN | CheckLibraryUpdates, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 15.1 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 15.1 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 15.1 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 15.1 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 15.1 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference |
| 15.1 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, ManagePlcExternalSources |
| 15.1 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, TraceTagCause, TraceTagCauseLive |
| 15.1 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ManageMotionAxis, ManageTechnologyObject |
| 15.1 | F10 | current | NOT RUN | GetPlcBlockScopes |
| 15.1 | F14 | current | NOT RUN | — |
| 15.1 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 15.1 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints |
| 15.1 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 15.1 | F18 | current | NOT RUN | CreateDevice, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 15.1 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ListTransferAreas, ManageIoSystem, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 15.1 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 15.1 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 15.1 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject |
| 16 | P6-DEVICE | current | NOT RUN | CreateDevice |
| 16 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 16 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 16 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 16 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 16 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 16 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 16 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, GetVersionControlStatus, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 16 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 16 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, GetOnlineState, GetPlcBlockFingerprints, ListTransferRoutes, ManagePlcDataBlockSnapshot, UploadStationFromPlc |
| 16 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcRules, ManageSivarcRule |
| 16 | F31 | current | NOT RUN | GetDccObject, GetDriveParameters, GetOnlineDriveParameters, ListDccCharts, ListDriveObjects, ManageDcbLibraries, ManageDccChart, ManageDriveFunctions, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter |
| 16 | F30 | current | NOT RUN | ManageSafetyGlobalSettings |
| 16 | F00 | current | NOT RUN | ExportPlcWatchTable |
| 16 | F34 | current | NOT RUN | ManageSinumerikArchive, ManageSinumerikSafetyMode |
| 16 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 16 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, ManageDeviceUsers, ManagePlcCertificate, ManagePlcProtection, ManageWatchForceTableWebAccess, SetPlcPutGetAccess |
| 16 | F33 | current | NOT RUN | ConnectProjectToWorkspace, CreateVersionControlWorkspace, GetVersionControlStatus, ListVersionControlWorkspaces, ManagePlcGitRepository, SynchronizeVersionControlWorkspace |
| 16 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 16 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 16 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, PlanGlobalLibraryTemplateReuse |
| 16 | F06 | current | NOT RUN | CompareProjects, ExportProjectTexts, GetProjectSettings, ImportProjectTexts, ManageProjectCompilationSettings, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 16 | F23 | current | NOT RUN | CheckLibraryUpdates, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 16 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 16 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 16 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 16 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 16 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference |
| 16 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, ManagePlcExternalSources |
| 16 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, TraceTagCause, TraceTagCauseLive |
| 16 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ImportTechnologyObject, ImportTechnologyObjectsFromDirectory, ManageMotionAxis, ManageTechnologyObject |
| 16 | F10 | current | NOT RUN | GetPlcBlockScopes |
| 16 | F14 | current | NOT RUN | ExchangePlcAlarmTextLists, ExportAlarmInstanceTexts, ExportAlarmTextLists, ImportAlarmTextLists, ImportPlcAlarmInstanceTexts |
| 16 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 16 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints, ListPlcSoftwareUnits, ManagePlcSoftwareUnit, SetPlcUnitObjectAccess |
| 16 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 16 | F18 | current | NOT RUN | CreateDevice, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 16 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ListNetworkDomains, ListTransferAreas, ManageIoSystem, ManageNetworkDomain, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 16 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 16 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 16 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject |
| 17 | P6-DEVICE | current | NOT RUN | CreateDevice |
| 17 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 17 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 17 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 17 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 17 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 17 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 17 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, GetVersionControlStatus, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 17 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 17 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, GetOnlineState, GetPlcBlockFingerprints, ListTransferRoutes, ManagePlcDataBlockSnapshot, UploadStationFromPlc |
| 17 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcBlockDefinitions, ListSivarcRules, ManageSivarcBlockDefinition, ManageSivarcRule |
| 17 | F31 | current | NOT RUN | GetDccObject, GetDriveParameters, GetOnlineDriveParameters, ListDccCharts, ListDriveObjects, ManageDcbLibraries, ManageDccChart, ManageDriveFunctions, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter |
| 17 | F30 | current | NOT RUN | ExportSafetyPrintout, GetSafetyBlockSignatures, ManagePlcSafety, ManageSafetyGlobalSettings |
| 17 | F00 | current | NOT RUN | ExportPlcWatchTable |
| 17 | F34 | current | NOT RUN | ExchangeTestSuiteCase, ExportScadaData, InitializeSimotionScripting, ListTestSuiteCases, ManageSinumerikArchive, ManageSinumerikSafetyMode, ManageTestSuiteCase, RunTestSuiteCase |
| 17 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, ManageMultiuserSession, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 17 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, GetProjectUserManagement, ManageDeviceUsers, ManagePlcCertificate, ManagePlcProtection, ManageProjectUserManagement, ManageUmcUsers, ManageWatchForceTableWebAccess, SetPlcPutGetAccess |
| 17 | F33 | current | NOT RUN | ConnectProjectToWorkspace, CreateVersionControlWorkspace, GetVersionControlStatus, ListVersionControlWorkspaces, ManagePlcGitRepository, SynchronizeVersionControlWorkspace |
| 17 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 17 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 17 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, ManageUnifiedCwcPackage, PlanGlobalLibraryTemplateReuse |
| 17 | F06 | current | NOT RUN | CompareProjects, ExportProjectTexts, GetProjectSettings, ImportProjectTexts, ManageProjectCompilationSettings, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 17 | F23 | current | NOT RUN | CheckLibraryUpdates, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 17 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 17 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 17 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 17 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 17 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference |
| 17 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, ManagePlcExternalSources |
| 17 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, SetPlcWatchTableModifyValue, TraceTagCause, TraceTagCauseLive |
| 17 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ImportTechnologyObject, ImportTechnologyObjectsFromDirectory, ManageMotionAxis, ManageTechnologyObject |
| 17 | F10 | current | NOT RUN | GetPlcBlockScopes |
| 17 | F14 | current | NOT RUN | ExchangePlcAlarmTextLists, ExchangePlcSupervisions, ExportAlarmClasses, ExportAlarmInstanceTexts, ExportAlarmTextLists, ExportPlcProDiagInfo, ImportAlarmClasses, ImportAlarmTextLists, ImportPlcAlarmInstanceTexts, ManagePlcSupervision |
| 17 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 17 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints, ListPlcSoftwareUnits, ManagePlcSoftwareUnit, SetPlcUnitObjectAccess |
| 17 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 17 | F18 | current | NOT RUN | CreateDevice, ExchangeSystemDiagnosticsSettings, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 17 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ListNetworkDomains, ListTransferAreas, ManageIoSystem, ManageNetworkDomain, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 17 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 17 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 17 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject |
| 18 | P6-DEVICE | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, CreateHardwareDevice |
| 18 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 18 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 18 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 18 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 18 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 18 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 18 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, GetVersionControlStatus, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 18 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 18 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, GeneratePlcLoadableFile, GetOnlineState, GetPlcBlockFingerprints, ListTransferRoutes, ManagePlcDataBlockSnapshot, UploadStationFromPlc |
| 18 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcBlockDefinitions, ListSivarcRules, ManageSivarcBlockDefinition, ManageSivarcRule, ManageSivarcRuleContainer, ManageSivarcTableRule |
| 18 | F31 | current | NOT RUN | GetDccObject, GetDriveParameters, GetOnlineDriveParameters, ListDccCharts, ListDriveObjects, ManageDcbLibraries, ManageDccBlock, ManageDccChart, ManageDccChartInterface, ManageDccChartPartition, ManageDccPin, ManageDriveFunctions, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter, ManageTechnologyExtensions |
| 18 | F30 | current | NOT RUN | ExportSafetyPrintout, GetSafetyBlockSignatures, ManagePlcSafety, ManageSafetyGlobalSettings |
| 18 | F00 | current | NOT RUN | CreateHardwareDevice, ExportPlcWatchTable, SearchHardwareCatalog |
| 18 | F32 | current | NOT RUN | ExchangeCfcCharts, ManageCfcChartProtection |
| 18 | F34 | current | NOT RUN | ExchangeTestSuiteCase, ExportScadaData, ImportSinumerikAlarmTexts, InitializeSimotionScripting, ListTestSuiteCases, ManageSinumerikArchive, ManageSinumerikSafetyMode, ManageTestSuiteCase, RunTestSuiteCase |
| 18 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, ManageMultiuserSession, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 18 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, GetProjectUserManagement, ManageDeviceUsers, ManagePasswordPolicy, ManagePlcCertificate, ManagePlcProtection, ManageProjectUserManagement, ManageUmcUsers, ManageWatchForceTableWebAccess, SetPlcPutGetAccess |
| 18 | F33 | current | NOT RUN | ConnectProjectToWorkspace, CreateVersionControlWorkspace, GetVersionControlStatus, ListVersionControlWorkspaces, ManagePlcGitRepository, ManageTeamcenterConnection, ManageTeamcenterDataset, ManageTeamcenterWorkflow, SynchronizeVersionControlWorkspace |
| 18 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 18 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 18 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, BuildClassicHmiScreen, ManageUnifiedCwcPackage, PlanGlobalLibraryTemplateReuse |
| 18 | F06 | current | NOT RUN | CompareLibraries, CompareProjects, ExportProjectTexts, GetProjectSettings, ImportProjectTexts, ManageProjectCompilationSettings, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 18 | F23 | current | NOT RUN | CheckLibraryUpdates, CompareLibraryObjects, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 18 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 18 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 18 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 18 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 18 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference, SetPlcProgram |
| 18 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, GetPlcCrossReferences, ManagePlcExternalSources |
| 18 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, SetPlcWatchTableModifyValue, TraceTagCause, TraceTagCauseLive |
| 18 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ImportTechnologyObject, ImportTechnologyObjectsFromDirectory, ManageMotionAxis, ManageTechnologyObject |
| 18 | F10 | current | NOT RUN | GetPlcBlockScopes |
| 18 | F14 | current | NOT RUN | ExchangePlcAlarmTextLists, ExchangePlcSupervisions, ExportAlarmClasses, ExportAlarmInstanceTexts, ExportAlarmTextLists, ExportPlcProDiagInfo, ImportAlarmClasses, ImportAlarmTextLists, ImportPlcAlarmInstanceTexts, ManagePlcAlarmTextList, ManagePlcSupervision |
| 18 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 18 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints, ListPlcSoftwareUnits, ManagePlcSoftwareUnit, SetPlcUnitObjectAccess |
| 18 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 18 | F18 | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, ExchangeSystemDiagnosticsSettings, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceServiceObjects, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 18 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ListNetworkDomains, ListTransferAreas, ManageIoSystem, ManageNetworkDomain, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 18 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 18 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 18 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreateHardwareDevice, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject, SearchHardwareCatalog |
| 19 | P6-DEVICE | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, CreateHardwareDevice |
| 19 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 19 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 19 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 19 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 19 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 19 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 19 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, DownloadPlcToFolder, GetVersionControlStatus, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 19 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 19 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, DownloadPlcToFolder, GeneratePlcLoadableFile, GetOnlineState, GetPlcBlockFingerprints, ListTransferRoutes, ManagePlcDataBlockSnapshot, ScanAccessibleDevices, UploadStationFromPlc |
| 19 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcBlockDefinitions, ListSivarcRules, ManageSivarcBlockDefinition, ManageSivarcRule, ManageSivarcRuleContainer, ManageSivarcTableRule |
| 19 | F31 | current | NOT RUN | GetDccObject, GetDriveParameters, GetOnlineDriveParameters, ListDccCharts, ListDriveObjects, ManageDcbLibraries, ManageDccBlock, ManageDccChart, ManageDccChartInterface, ManageDccChartPartition, ManageDccPin, ManageDriveFunctions, ManageDriveHardwareModule, ManageDriveSecurity, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter, ManageTechnologyExtensions |
| 19 | F30 | current | NOT RUN | ExportSafetyPrintout, GetSafetyBlockSignatures, ManagePlcSafety, ManageSafetyGlobalSettings |
| 19 | F00 | current | NOT RUN | CreateHardwareDevice, ExportPlcWatchTable, SearchHardwareCatalog |
| 19 | F32 | current | NOT RUN | ExchangeCfcCharts, ManageCfcChartProtection |
| 19 | F34 | current | NOT RUN | ExchangeTestSuiteCase, ExportScadaData, ImportSinumerikAlarmTexts, InitializeSimotionScripting, ListTestSuiteCases, ManageSinumerikArchive, ManageSinumerikSafetyMode, ManageTestSuiteCase, RunTestSuiteCase |
| 19 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, ManageMultiuserSession, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 19 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, GetProjectUserManagement, ManageDeviceUsers, ManagePasswordPolicy, ManagePlcCertificate, ManagePlcProtection, ManageProjectUserManagement, ManageSyslogServers, ManageUmcUsers, ManageWatchForceTableWebAccess, SetPlcPutGetAccess |
| 19 | F33 | current | NOT RUN | ConnectProjectToWorkspace, CreateVersionControlWorkspace, GetVersionControlStatus, ListVersionControlWorkspaces, ManagePlcGitRepository, ManageTeamcenterConnection, ManageTeamcenterDataset, ManageTeamcenterWorkflow, SynchronizeVersionControlWorkspace |
| 19 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 19 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 19 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, AnalyzeUnifiedHmiTemplateLayout, BuildClassicHmiScreen, BuildUnifiedHmiButtonActionScript, BuildUnifiedHmiLayoutDesign, BuildUnifiedHmiTemplateApplyDesign, BuildUnifiedHmiTemplateApplyDesignManifest, BuildUnifiedHmiThemeDesign, ManageUnifiedCwcPackage, PlanGlobalLibraryTemplateReuse, RunHmiActionScriptRecipeSafetySelfTest, RunHmiTemplatePlcSyncPrecheckSuite |
| 19 | F06 | current | NOT RUN | CompareLibraries, CompareProjects, ExportProjectTexts, GetProjectSettings, ImportProjectTexts, ManageProjectCompilationSettings, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 19 | F23 | current | NOT RUN | CheckLibraryUpdates, CompareLibraryObjects, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ImportMasterCopyFromGlobalLibrary, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 19 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiScreenItem, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 19 | F26 | current | NOT RUN | ApplyUnifiedHmiLayout, ApplyUnifiedHmiScreenDesign, ApplyUnifiedHmiTheme, BindUnifiedHmiTagDynamization, DeleteEmptyUnifiedHmiScreenGroup, DeleteUnifiedHmiButtonEvent, DeleteUnifiedHmiDynamization, DescribeUnifiedHmiButtonEventScript, DescribeUnifiedScreenItemType, EnsureUnifiedHmiButtonAction, EnsureUnifiedHmiButtonEventHandler, EnsureUnifiedHmiConnection, EnsureUnifiedHmiDynamization, EnsureUnifiedHmiScreen, EnsureUnifiedHmiScreenItem, EnsureUnifiedHmiTag, EnsureUnifiedHmiTagTable, GetUnifiedHmiButtonEvent, GetUnifiedHmiDynamization, GetUnifiedHmiTexts, ListUnifiedHmiApiTypes, ManageUnifiedHmiGroup, ManageUnifiedScreenItem, SetUnifiedHmiButtonEventScriptCode, SetUnifiedHmiRuntimeState |
| 19 | F27 | current | NOT RUN | GetUnifiedAlarmCommon, GetUnifiedAuditSettings, GetUnifiedCrossReferences, GetUnifiedObjectEvents, GetUnifiedObjectProperties, GetUnifiedPlantObject, ListUnifiedEngineeringObjects, ManageUnifiedDynamization, ManageUnifiedEngineeringObject, ManageUnifiedEvent, ManageUnifiedLoggingTag, ManageUnifiedObjectParts, ManageUnifiedOpcUaAlarmType, ManageUnifiedPlantNode, ManageUnifiedScreenLayout, SetUnifiedLogDuration, SetUnifiedMultilingualProperty, SetUnifiedObjectProperties, SetUnifiedPlantObject, ValidateUnifiedObject |
| 19 | F28 | current | NOT RUN | CompareUnifiedGraphicSelections, ExchangeUnifiedScriptModules, ExchangeUnifiedTags, GetUnifiedFaceplateInstance, GetUnifiedGlobalScript, GetUnifiedGraphicSelection, GetUnifiedLibraryType, GetUnifiedRuntimeSettings, GetUnifiedScreenBranch, ImportUnifiedOpcUaAlarms, ListUnifiedGlobalScripts, ListUnifiedLibraryFolderEntries, ListUnifiedTagDefinitions, ReleaseUnifiedReadCursor, SetUnifiedGlobalScript, SetUnifiedRuntimeSettings |
| 19 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 19 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 19 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 19 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference, SetPlcProgram |
| 19 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, GetPlcCrossReferences, ManagePlcExternalSources |
| 19 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, SetPlcWatchTableModifyValue, TraceTagCause, TraceTagCauseLive |
| 19 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ImportTechnologyObject, ImportTechnologyObjectsFromDirectory, ManageMotionAxis, ManageTechnologyObject |
| 19 | F10 | current | NOT RUN | GetPlcBlockScopes |
| 19 | F14 | current | NOT RUN | ExchangePlcAlarmTextLists, ExchangePlcSupervisions, ExportAlarmClasses, ExportAlarmInstanceTexts, ExportAlarmTextLists, ExportPlcProDiagInfo, ImportAlarmClasses, ImportAlarmTextLists, ImportPlcAlarmInstanceTexts, ManagePlcAlarmTextList, ManagePlcSupervision |
| 19 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 19 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints, ListPlcSoftwareUnits, ManagePlcSoftwareUnit, SetPlcUnitObjectAccess |
| 19 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 19 | F18 | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, ExchangeSystemDiagnosticsSettings, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceServiceObjects, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 19 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ListNetworkDomains, ListTransferAreas, ManageIoSystem, ManageNetworkDomain, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 19 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 19 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 19 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreateHardwareDevice, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlocks, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject, SearchHardwareCatalog |
| 20 | P6-DEVICE | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, CreateHardwareDevice |
| 20 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 20 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlockDocuments, ExportPlcBlocks, ExportPlcBlocksDocuments, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 20 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 20 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 20 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 20 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 20 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, DownloadPlcToFolder, ExportPlcBlockDocuments, GetVersionControlStatus, ImportPlcBlockDocuments, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 20 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 20 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, DownloadPlcToFolder, GeneratePlcLoadableFile, GetOnlineState, GetPlcBlockFingerprints, ListTransferRoutes, ManagePlcDataBlockSnapshot, ScanAccessibleDevices, UploadStationFromPlc |
| 20 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcBlockDefinitions, ListSivarcRules, ManageSivarcBlockDefinition, ManageSivarcRule, ManageSivarcRuleContainer, ManageSivarcTableRule, ResolveSivarcExpression, UpgradeSivarcDefinitions |
| 20 | F31 | current | NOT RUN | GetDccObject, GetDriveParameters, GetOnlineDriveParameters, ListDccCharts, ListDriveObjects, ManageDcbLibraries, ManageDccBlock, ManageDccChart, ManageDccChartInterface, ManageDccChartPartition, ManageDccPin, ManageDriveFunctions, ManageDriveHardwareModule, ManageDriveSecurity, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter, ManageTechnologyExtensions |
| 20 | F30 | current | NOT RUN | ExportSafetyPrintout, GetSafetyBlockSignatures, ManagePlcSafety, ManageSafetyGlobalSettings |
| 20 | F00 | current | NOT RUN | CreateHardwareDevice, ExportPlcWatchTable, SearchHardwareCatalog |
| 20 | F32 | current | NOT RUN | ExchangeCfcCharts, ManageCfcChartProtection |
| 20 | F34 | current | NOT RUN | ExchangeTestSuiteCase, ExportScadaData, ImportSinumerikAlarmTexts, InitializeSimotionScripting, ListTestSuiteCases, ManageSinumerikArchive, ManageSinumerikSafetyMode, ManageTestSuiteCase, RunTestSuiteCase |
| 20 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, ManageMultiuserSession, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 20 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, GetProjectUserManagement, ManageDeviceUsers, ManagePasswordPolicy, ManagePlcCertificate, ManagePlcProtection, ManageProjectUserManagement, ManageSyslogServers, ManageUmcUsers, ManageWatchForceTableWebAccess, SetPlcPutGetAccess |
| 20 | F33 | current | NOT RUN | ConnectProjectToWorkspace, CreateVersionControlWorkspace, GetVersionControlStatus, ListVersionControlWorkspaces, ManagePlcGitRepository, ManageTeamcenterConnection, ManageTeamcenterDataset, ManageTeamcenterWorkflow, SynchronizeVersionControlWorkspace |
| 20 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 20 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, ComparePlcBlockDocuments, ConvertEngineeringTable, DecodePlcSimaticMl, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InspectSimaticSdCompatibility, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 20 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, AnalyzeUnifiedHmiTemplateLayout, BuildClassicHmiScreen, BuildUnifiedHmiButtonActionScript, BuildUnifiedHmiLayoutDesign, BuildUnifiedHmiTemplateApplyDesign, BuildUnifiedHmiTemplateApplyDesignManifest, BuildUnifiedHmiThemeDesign, ManageUnifiedCwcPackage, PlanGlobalLibraryTemplateReuse, RunClassicHmiOfflineValidationSuite, RunClassicHmiTemporaryImportPreflight, RunHmiActionScriptRecipeSafetySelfTest, RunHmiTemplatePlcSyncPrecheckSuite, ValidateClassicHmiMinimalPackageFiles, ValidateClassicHmiMinimalPackagePlcSync, WriteClassicHmiMinimalPackageFiles |
| 20 | F06 | current | NOT RUN | CompareLibraries, CompareProjects, ExportProjectTexts, GetObjectIdentifier, GetProjectSettings, ImportProjectTexts, ManageProjectCompilationSettings, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 20 | F23 | current | NOT RUN | CheckLibraryUpdates, CompareLibraryObjects, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ImportLibraryTypeDocuments, ImportMasterCopyFromGlobalLibrary, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 20 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiScreenItem, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 20 | F26 | current | NOT RUN | ApplyUnifiedHmiLayout, ApplyUnifiedHmiScreenDesign, ApplyUnifiedHmiTheme, BindUnifiedHmiTagDynamization, DeleteEmptyUnifiedHmiScreenGroup, DeleteUnifiedHmiButtonEvent, DeleteUnifiedHmiDynamization, DescribeUnifiedHmiButtonEventScript, DescribeUnifiedScreenItemType, EnsureUnifiedHmiButtonAction, EnsureUnifiedHmiButtonEventHandler, EnsureUnifiedHmiConnection, EnsureUnifiedHmiDynamization, EnsureUnifiedHmiScreen, EnsureUnifiedHmiScreenItem, EnsureUnifiedHmiTag, EnsureUnifiedHmiTagTable, GetUnifiedHmiButtonEvent, GetUnifiedHmiDynamization, GetUnifiedHmiTexts, ListUnifiedHmiApiTypes, ManageUnifiedHmiGroup, ManageUnifiedScreenItem, SetUnifiedHmiButtonEventScriptCode, SetUnifiedHmiRuntimeState |
| 20 | F27 | current | NOT RUN | ExportUnifiedEngineeringList, GetUnifiedAlarmCommon, GetUnifiedAuditSettings, GetUnifiedCrossReferences, GetUnifiedObjectEvents, GetUnifiedObjectProperties, GetUnifiedPlantObject, ImportUnifiedEngineeringList, ListUnifiedEngineeringObjects, ManageUnifiedDynamization, ManageUnifiedEngineeringObject, ManageUnifiedEvent, ManageUnifiedListEntries, ManageUnifiedLoggingTag, ManageUnifiedObjectParts, ManageUnifiedOpcUaAlarmType, ManageUnifiedPlantNode, ManageUnifiedScreenLayout, SetUnifiedLogDuration, SetUnifiedMultilingualProperty, SetUnifiedObjectProperties, SetUnifiedPlantObject, ValidateUnifiedObject |
| 20 | F28 | current | NOT RUN | CompareUnifiedGraphicSelections, ExchangeUnifiedScriptModules, ExchangeUnifiedTags, GetUnifiedFaceplateInstance, GetUnifiedGlobalScript, GetUnifiedGraphicSelection, GetUnifiedLibraryType, GetUnifiedRuntimeSettings, GetUnifiedScreenBranch, ImportUnifiedOpcUaAlarms, ListUnifiedGlobalScripts, ListUnifiedLibraryFolderEntries, ListUnifiedTagDefinitions, ReleaseUnifiedReadCursor, SetUnifiedGlobalScript, SetUnifiedRuntimeSettings |
| 20 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 20 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 20 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 20 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference, SetPlcProgram |
| 20 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, GetPlcCrossReferences, ManagePlcExternalSources |
| 20 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, SetPlcWatchTableModifyValue, TraceTagCause, TraceTagCauseLive |
| 20 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ImportTechnologyObject, ImportTechnologyObjectsFromDirectory, ManageMotionAxis, ManageTechnologyObject |
| 20 | F10 | current | NOT RUN | ExportPlcBlockDocuments, ExportPlcBlocksDocuments, GetPlcBlockScopes, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ManagePlcBlockDocuments, ManagePlcDocuments |
| 20 | F14 | current | NOT RUN | ExchangePlcAlarmTextLists, ExchangePlcSupervisions, ExportAlarmClasses, ExportAlarmInstanceTexts, ExportAlarmTextLists, ExportPlcProDiagInfo, ImportAlarmClasses, ImportAlarmTextLists, ImportPlcAlarmInstanceTexts, ManagePlcAlarmTextList, ManagePlcSupervision |
| 20 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 20 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints, ListPlcSoftwareUnits, ManagePlcSoftwareUnit, SetPlcUnitObjectAccess |
| 20 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 20 | F18 | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, ExchangeSystemDiagnosticsSettings, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceServiceObjects, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 20 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListDeviceItemChannels, ListIoSystems, ListNetworkDomains, ListTransferAreas, ManageIoSystem, ManageNetworkDomain, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 20 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 20 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 20 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreateHardwareDevice, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlockDocuments, ExportPlcBlocks, ExportPlcBlocksDocuments, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject, SearchHardwareCatalog |
| 21 | P6-DEVICE | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, CreateHardwareDevice |
| 21 | P6-IMPORT | current | NOT RUN | ImportPlcBlock, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ImportPlcBlocksFromDirectory, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcTagTablesFromDirectory, ImportPlcType |
| 21 | P6-EXPORT | current | NOT RUN | ExportPlcBlock, ExportPlcBlockDocuments, ExportPlcBlocks, ExportPlcBlocksDocuments, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes |
| 21 | P6-SESSION | current | NOT RUN | AttachOpenProject, ConnectIsolatedPortal, ConnectPortal, OpenProject |
| 21 | P6-CLOSE | current | NOT RUN | CloseProject, DisconnectPortal, SaveProject, SaveProjectCopy |
| 21 | P6-SOURCE | current | NOT RUN | DeletePlcExternalSource, GenerateBlocksFromExternalSource, ImportPlcExternalSource, ListPlcExternalSources, ManagePlcExternalSources, PlanPlcExternalSourceImport |
| 21 | P6-COMPILE | current | NOT RUN | CompileDevice, CompileHmiDiagnostics, CompilePlcDiagnostics, CompilePlcSoftware |
| 21 | P6-FALLBACK | current | NOT RUN | CompilePlcSoftware, ConnectProjectToWorkspace, CreateVersionControlWorkspace, DownloadPlc, DownloadPlcToFolder, ExportPlcBlockDocuments, GetVersionControlStatus, ImportPlcBlockDocuments, ListVersionControlWorkspaces, SynchronizeVersionControlWorkspace |
| 21 | F04 | current | NOT RUN | GetPlcLiveValuesOpcUa, GetPlcLiveValuesS7, GetPlcRunStateS7, GetPlcSimAdvancedTags, GetPlcWebDiagnostics, GetPlcWebVars, GetUnifiedRuntimeAlarms, GetUnifiedRuntimeTags, InvokeUnifiedOpenPipe, ListPlcSimAdvancedInstances, ManagePlcSimAdvancedInstance, ProbeS7CpuIdentity, RunPlcCompanionTool, RunPlcSimAdvancedTestScenario, SamplePlcLiveValuesS7, SetPlcWebOperatingMode, WritePlcSimAdvancedTags, WritePlcWebVars, WriteUnifiedRuntimeTags |
| 21 | F22 | current | NOT RUN | CheckDownloadReadiness, CompareSoftwareToOnline, ConnectOnlinePlc, DisconnectOnlinePlc, DisconnectOnlinePlcs, DownloadPlc, DownloadPlcToFolder, GeneratePlcLoadableFile, GetOnlineState, GetPlcBlockFingerprints, ListTransferRoutes, ManagePlcDataBlockSnapshot, ScanAccessibleDevices, UploadDeviceParameters, UploadStationFromPlc |
| 21 | F29 | current | NOT RUN | GenerateSivarc, GetSivarcRuleTree, ListSivarcBlockDefinitions, ListSivarcRules, ManageSivarcBlockDefinition, ManageSivarcRule, ManageSivarcRuleContainer, ManageSivarcScreenLayout, ManageSivarcTableRule, ResolveSivarcExpression, UpgradeSivarcDefinitions |
| 21 | F31 | current | NOT RUN | GetDccObject, GetDriveParameters, GetOnlineDriveParameters, ListDccCharts, ListDriveObjects, ManageDcbLibraries, ManageDccBlock, ManageDccChart, ManageDccChartInterface, ManageDccChartPartition, ManageDccPin, ManageDriveFunctions, ManageDriveHardwareModule, ManageDriveSafetyAcceptanceTest, ManageDriveSecurity, ManageDriveTelegrams, ManageOnlineDriveFunctions, ManageStartdriveParameter, ManageTechnologyExtensions |
| 21 | F30 | current | NOT RUN | ExportSafetyPrintout, GetSafetyBlockSignatures, ListSafetyActivationTests, ManagePlcSafety, ManageSafetyActivationTest, ManageSafetyActivationTestGroup, ManageSafetyFunction, ManageSafetyFunctionCondition, ManageSafetyGlobalSettings |
| 21 | F00 | current | NOT RUN | CreateHardwareDevice, ExportPlcWatchTable, SearchHardwareCatalog |
| 21 | F32 | current | NOT RUN | ExchangeCfcCharts, ManageCfcChartProtection |
| 21 | F34 | current | NOT RUN | ExchangeTestSuiteCase, ListTestSuiteCases, ManageTestSuiteCase, RunTestSuiteCase |
| 21 | F05 | current | NOT RUN | ArchiveSavedProject, BuildProjectScaffold, ConnectIsolatedPortal, EnsureOpennessUserGroup, GetPortalInfo, ManageMultiuserSession, RetrieveProjectArchive, SaveProjectCopy, ValidateAutomationContext |
| 21 | F07 | current | NOT RUN | GetPlcPutGetAccess, GetProjectProtection, GetProjectUserManagement, ManageDeviceUsers, ManagePasswordPolicy, ManagePlcCertificate, ManagePlcProtection, ManageProjectUserManagement, ManageSyslogServers, ManageUmcUsers, ManageWatchForceTableWebAccess, SetPlcPutGetAccess |
| 21 | F33 | current | NOT RUN | ConnectProjectToWorkspace, CreateVersionControlWorkspace, GetVersionControlStatus, ListVersionControlWorkspaces, ManagePlcGitRepository, ManageTeamcenterConnection, ManageTeamcenterDataset, ManageTeamcenterWorkflow, SynchronizeVersionControlWorkspace |
| 21 | F01 | current | NOT RUN | ApplyToolBatch, BuildReleaseDiagnosticReport, BuildReleaseHandoffArtifacts, BuildReleaseManifest, BuildReleaseRunbook, CallTool, CheckProductUpdate, ClearExportHandles, DeleteExportHandle, ExportPlcTestReport, FindTools, GenerateAcceptanceReport, GenerateErrorReport, GetApprovalStatus, GetEnvironmentDiagnostics, GetExportContent, GetNativeInvocationLog, GetOpennessCompatibility, GetOpennessGuidance, GetOpennessWorkerStatus, GetV21EcosystemCatalog, ListExportHandles, ListToolCategories, ManagePlcTestLibrary, PreviewToolBatch, PreviewToolCall, RestartOpennessWorker, RunOfflineReleaseValidationSuite, RunOnlineMonitoringSafetySelfTest, RunPlcTestLibrary, RunReadOnlyToolBatch, SaveExportContent |
| 21 | F02 | current | NOT RUN | AnalyzePlcReferences, AnalyzePlcSclSource, AuditEngineeringExports, BuildPlcAliasAlarmLad, ComparePlcBlockDocuments, ConvertEngineeringTable, DecodePlcSimaticMl, ExtractPlcBlockMetrics, GeneratePlcDocumentation, InspectSimaticSdCompatibility, InstantiatePlcTemplates, PatchPlcBlockDocument, RenderPlcBlockDocument, RenderPlcVisualDiff, ScanPlcSourceAnnotations, ValidatePlcDocumentSchemas, WritePlcSclSourceFile |
| 21 | F03 | current | NOT RUN | AnalyzeGlobalLibraryPackage, AnalyzeHmiTemplateReference, AnalyzeUnifiedHmiTemplateLayout, BuildClassicHmiMinimalPackage, BuildClassicHmiScreen, BuildClassicHmiTagTable, BuildUnifiedHmiButtonActionScript, BuildUnifiedHmiLayoutDesign, BuildUnifiedHmiTemplateApplyDesign, BuildUnifiedHmiTemplateApplyDesignManifest, BuildUnifiedHmiThemeDesign, ManageUnifiedCwcPackage, PlanGlobalLibraryTemplateReuse, RunClassicHmiOfflineValidationSuite, RunClassicHmiTemporaryImportPreflight, RunHmiActionScriptRecipeSafetySelfTest, RunHmiTemplatePlcSyncPrecheckSuite, ValidateClassicHmiMinimalPackageFiles, ValidateClassicHmiMinimalPackagePlcSync, WriteClassicHmiMinimalPackageFiles |
| 21 | F06 | current | NOT RUN | CompareLibraries, CompareProjects, ExportProjectTexts, GetObjectIdentifier, GetProjectSettings, ImportProjectTexts, ManageProjectCompilationSettings, ManageProjectLanguage, RunToolTransaction, ShowObjectInEditor |
| 21 | F23 | current | NOT RUN | CheckLibraryUpdates, CompareLibraryObjects, CreateLibraryMasterCopy, GetLibraryOverview, GetLibraryType, ImportLibraryTypeDocuments, ImportMasterCopyFromGlobalLibrary, ManageGlobalLibrary, ManageLibraryFolder, ManageLibraryMasterCopy, ManageLibraryType, ManageLibraryTypeVersion, ProbeGlobalLibrary, SynchronizeLibrary |
| 21 | F24 | current | NOT RUN | DeleteHmiTag, DescribeHmiScreen, DescribeHmiScreenItem, DescribeHmiSoftware, DescribeHmiTag, DescribeHmiTagTable, ExportHmiConnection, ExportHmiProgram, ExportHmiScreen, ExportHmiTagTable, GetHmiProgramInfo, GetHmiScreenSnapshot, ImportHmiConnection, ImportHmiScreen, ImportHmiScreensFromDirectory, ImportHmiTagTable, ImportHmiTagTablesFromDirectory, ListHmiConnections, ListHmiScreenPaths, ListHmiScreens, ListHmiTagTables, ListHmiTags |
| 21 | F26 | current | NOT RUN | ApplyUnifiedHmiLayout, ApplyUnifiedHmiScreenDesign, ApplyUnifiedHmiTheme, BindUnifiedHmiButtonPressedTag, BindUnifiedHmiTagDynamization, DeleteEmptyUnifiedHmiScreenGroup, DeleteUnifiedHmiButtonEvent, DeleteUnifiedHmiDynamization, DescribeUnifiedHmiButtonEventScript, DescribeUnifiedScreenItemType, EnsureUnifiedHmiButtonAction, EnsureUnifiedHmiButtonEventHandler, EnsureUnifiedHmiConnection, EnsureUnifiedHmiDynamization, EnsureUnifiedHmiScreen, EnsureUnifiedHmiScreenItem, EnsureUnifiedHmiTag, EnsureUnifiedHmiTagTable, GetUnifiedHmiButtonEvent, GetUnifiedHmiDynamization, GetUnifiedHmiTexts, ListUnifiedHmiApiTypes, ManageUnifiedHmiGroup, ManageUnifiedScreenItem, SetUnifiedHmiButtonEventScriptCode, SetUnifiedHmiRuntimeState |
| 21 | F27 | current | NOT RUN | ExportUnifiedEngineeringList, GetUnifiedAlarmCommon, GetUnifiedAuditSettings, GetUnifiedCrossReferences, GetUnifiedObjectEvents, GetUnifiedObjectProperties, GetUnifiedPlantObject, ImportUnifiedEngineeringList, ListUnifiedEngineeringObjects, ManageUnifiedDynamization, ManageUnifiedEngineeringObject, ManageUnifiedEvent, ManageUnifiedListEntries, ManageUnifiedLoggingTag, ManageUnifiedObjectParts, ManageUnifiedOpcUaAlarmType, ManageUnifiedPlantNode, ManageUnifiedScreenLayout, SetUnifiedLogDuration, SetUnifiedMultilingualProperty, SetUnifiedObjectProperties, SetUnifiedPlantObject, ValidateUnifiedObject |
| 21 | F28 | current | NOT RUN | CompareUnifiedGraphicSelections, ExchangeUnifiedScriptModules, ExchangeUnifiedTags, GetUnifiedFaceplateInstance, GetUnifiedGlobalScript, GetUnifiedGraphicSelection, GetUnifiedLibraryType, GetUnifiedRuntimeSettings, GetUnifiedScreenBranch, ImportUnifiedOpcUaAlarms, ListUnifiedGlobalScripts, ListUnifiedLibraryFolderEntries, ListUnifiedTagDefinitions, ReleaseUnifiedReadCursor, SetUnifiedGlobalScript, SetUnifiedRuntimeSettings |
| 21 | F25 | current | NOT RUN | GetClassicHmiGlobalization, GetClassicHmiScreenTree, ListClassicHmiFaceplates, ListClassicHmiScripts, ManageClassicHmiCycle, ManageClassicHmiFolder, ManageClassicHmiGraphic, ManageClassicHmiScreenObject, ManageClassicHmiScript, ManageClassicHmiTextGraphicList |
| 21 | F35 | current | NOT RUN | DescribeObject, DescribeObjectProperty, DescribeService, GetObjectProperty, InvokeObject, InvokeService, ListObjectChildren |
| 21 | F08 | current | NOT RUN | CreatePlcBlockGroup, CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, DeletePlcBlock, DeletePlcTagTable, DeletePlcType, ListPlcSystemGroups, ManagePlcBlockProtection, ManagePlcUserGroup, MovePlcBlockToGroup |
| 21 | F09 | current | NOT RUN | BuildAndImportPlcArtifact, CreatePlcInstanceDb, DescribePlcBlockLogic, GetPlcBlockEditCapabilities, GetPlcTagTableConstants, ImportPlcBlockVerified, ImportPlcTagTablesFromDirectory, ManagePlcTagDefinition, RepairAndReimportPlcBlock, SeedProjectFromReference, SetPlcProgram |
| 21 | F11 | current | NOT RUN | GeneratePlcSourceFromBlocks, GetPlcCrossReferences, ManagePlcExternalSources |
| 21 | F12 | current | NOT RUN | ExportPlcWatchTablesToDirectory, GetPlcWatchTableCurrentValuesReadOnly, ImportPlcWatchTableOffline, ListPlcForceTables, ManagePlcTableEntries, MonitorPlcWatchTableS7, PlanOnlineReadOnlyDataProvider, PlanOnlineReadOnlyMonitoring, ProbePlcMonitorOnlineCapabilities, SetPlcWatchTableModifyValue, TraceTagCause, TraceTagCauseLive |
| 21 | F13 | current | NOT RUN | ConfigureMotionHardwareConnection, ExchangeMotionCamData, ExportTechnologyObjectsToDirectory, GetMotionAxisConfiguration, GetTechnologyObjectTree, ImportTechnologyObject, ImportTechnologyObjectsFromDirectory, ManageMotionAxis, ManageTechnologyObject |
| 21 | F10 | current | NOT RUN | ExportPlcBlockDocuments, ExportPlcBlocksDocuments, GetPlcBlockScopes, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ManagePlcBlockDocuments, ManagePlcDocuments |
| 21 | F14 | current | NOT RUN | ExchangePlcAlarmTextLists, ExchangePlcSupervisions, ExportAlarmClasses, ExportAlarmInstanceTexts, ExportAlarmTextLists, ExportPlcProDiagInfo, ImportAlarmClasses, ImportAlarmTextLists, ImportPlcAlarmInstanceTexts, ManagePlcAlarmTextList, ManagePlcSupervision |
| 21 | F15 | current | NOT RUN | ExportOpcUaInterface, GenerateOpcUaModelledInterface, GetOpcUaAccessControl, GetPlcOpcUaConfiguration, ImportOpcUaInterface, ManageOpcUaAccessControl, ManageOpcUaInterface, SetOpcUaInterfaceEnabled |
| 21 | F16 | current | NOT RUN | GetPlcChecksums, GetPlcObjectFingerprints, ListPlcSoftwareUnits, ManagePlcBlockWriteProtection, ManagePlcSoftwareUnit, SetPlcUnitObjectAccess |
| 21 | F17 | current | NOT RUN | CompileDevice, CompileHmiDiagnostics |
| 21 | F18 | current | NOT RUN | CreateDevice, CreateGsdDevice, CreateHardwareCatalogDevice, ExchangeSystemDiagnosticsSettings, GetDeviceAttributes, GetDeviceInfo, GetDeviceItemInfo, GetDeviceItemTree, GetDevicePlugLocations, GetHardwareFeatures, ListDevices, ManageDeviceServiceObjects, ManageDeviceUserGroup, ManageHardwareObject, ManageHardwareUtilities, PlugDeviceItem, SearchInstalledGsdDevices, SetDeviceItemAttribute, SetPlcCpuSettings |
| 21 | F20 | current | NOT RUN | AttachDeviceNodeToSubnet, ConnectDeviceNodesToProfinetSubnet, EnsureSubnet, GetDeviceItemNetworkInfo, GetProjectTopology, ListCommunicationConnections, ListDeviceItemChannels, ListIoSystems, ListNetworkDomains, ListTransferAreas, ManageCommunicationConnection, ManageIoSystem, ManageNetworkDomain, ManagePortInterconnection, ManageTransferArea, PlanHardwareNetworkConfiguration, ProbeHardwareHmiConnectionOwnerCandidates, ProbeHardwareHmiConnectionWhitelistedServices, SetDeviceItemChannel |
| 21 | F21 | current | NOT RUN | BuildDeviceAmlDocument, ExportDeviceAml, ImportDeviceAml |
| 21 | F19 | current | NOT RUN | GetDeviceAddressing, GetDeviceIpAddress, GetDeviceItemIoAddresses, SetDeviceAddress, SetDeviceItemIoAddress |
| 21 | FOUNDATION | current | NOT RUN | AnalyzePlcCrossReferences, AttachOpenProject, CloseProject, CompilePlcDiagnostics, CompilePlcSoftware, ConnectPortal, CreateHardwareDevice, CreatePlcTag, CreatePlcTagTable, CreatePlcUserConstant, CreateProject, DeletePlcExternalSource, DisconnectPortal, ExportPlcBlock, ExportPlcBlockDocuments, ExportPlcBlocks, ExportPlcBlocksDocuments, ExportPlcTagTable, ExportPlcTagTables, ExportPlcType, ExportPlcTypes, ExportPlcWatchTable, ExportTechnologyObject, GenerateBlocksFromExternalSource, GetPlcBlockHierarchy, GetPlcBlockInfo, GetPlcTypeInfo, GetPortalConnectionReadiness, GetProjectInfo, GetProjectTree, GetSessionState, GetSoftwareInfo, GetSoftwareTree, ImportPlcBlock, ImportPlcBlockDocuments, ImportPlcBlocksDocuments, ImportPlcBlocksFromDirectory, ImportPlcExternalSource, ImportPlcProgramFromDirectory, ImportPlcTagTable, ImportPlcType, ListPlcBlocks, ListPlcExternalSources, ListPlcSystemConstants, ListPlcTagTables, ListPlcTags, ListPlcTypes, ListPlcUserConstants, ListPlcWatchTables, ListPortalProcessProjects, ListTechnologyObjects, OpenProject, PlanPlcExternalSourceImport, SaveProject, SearchHardwareCatalog |

<!-- behavior-capabilities:end -->

## v3.2.0 验收边界

八版新增外部源导入、生成、编译路线与目标版本 UDT / DB XML，以及 Studio 新版本适配器的真实工程往返，当前均为 **NOT RUN**。后续应按明确的版本、测试工程副本及选定 PLC 执行，记录实际导入对象、编译诊断、读回与保存结果。选择测试目标不会由历史记录自动授权。

| 已知现场事项 | 当前结论与证据 |
|---|---|
| DEP-1 稳定版 JSON 依赖加载 | **NOT RUN**：需在至少一个 V14 SP1–V19 版本确认 net48 worker 能在 Siemens 程序集旁加载 System.Text.Json、System.Text.Encodings.Web 和 Microsoft.Bcl.AsyncInterfaces 10.0.12；离线构建与响应比对不代替原生加载验收。 |
| `ManageStartdriveParameter` 读取 `p2051[0]` | 用户报告 TIA 崩溃。现代码按官方 BICO 路线做单值读取，去掉无关元数据遍历；原生复测未运行，根因未确认。详见[限制与结果解释](../troubleshooting/openness-limitations.md)。 |
| Unified 脚本模块库类型改名 | 2026-10-01，在项目库和独立全局库中的样本均出现 `NonRecoverableException` 并伴随测试 TIA 退出；没有已验证修复。[机器可读证据](../../manifest/history/unified-library-rename-native-20261001.json)。 |
| PLC 原生交叉引用 | 有 V21 退出历史。2026-10-30 决定：V14 SP1–V20 在 API 可用时默认允许；V21 保留人工会话开关或环境变量 opt-in，真机确认 pending / NOT RUN。编译成功不能保证查询稳定。离线引用分析仅覆盖成功导出且可索引的输入。 |
| HMI 深层快照 | 有 getter / 句柄失效记录；部分属性暂停读取。必须查看完整性字段，[诊断说明](../troubleshooting/hmi-snapshots.md)。 |

## DES-5 分页与解码器验收

以下项目在 V14 SP1、V15.1、V16、V17、V18、V19、V20、V21 均为 **NOT RUN**。离线夹具、构建和导入跟踪不代替现场验收。

| 待验收项目 | 状态 | 需要保留的证据 |
|---|---|---|
| 130 块工程的分页大小与耗时 | NOT RUN | 逐版本记录默认四项及显式页大小的序列化字节数、首尾页和总耗时，直接调用及 CallTool 的 nextCursor 连续读取。 |
| 转义的组限定块路径 | NOT RUN | 含分隔符、百分号和 Unicode 的实际块路径被 GetPlcBlockInfo、ExportPlcBlock 原样接受；无遗漏或重复。 |
| 重绑、关闭和重开后的游标 | NOT RUN | 原游标按会话、绑定及查询失效，新游标从首项重启；实际工程对象与读回结果。 |
| 无 Python / VC++ 安装的干净 Windows | NOT RUN | 交付包中固定 CPython 运行时的启动、FC/FB 解码、未知指令及不支持版本拒绝；安装环境与依赖诊断。 |

## 保留的历史记录

[原逐工具台账](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/reference/real-machine-ledger.md)保留早期 V21 测试工程、设备、工具行和现场修复过程。该台账主要对应 2.7.39–2.7.62；旧汇总中“调用过”“前置条件拒绝”“环境拒绝”与真实成功分列，不能将总行数称为全部验收通过。

历史证据中，标准 CPU / PLCSIM Advanced 的下载、上线、比较、变量读写与单步场景曾完成；这些结果仅适用于当时设备、工程与软件组合，不能推广到其他版本、F-CPU 或真实生产设备。早期某个 BICO 参数写入成功，也不能证明 `p2051[0]` 读取已通过。

[Unified 改名现场完整快照](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/development/unified-library-rename-incident-20261001.md)保存两个测试位置、时间、调用标识与源工程读回结果。它不是独立于 MCP 的最小 C# 复现，也没有确定所有 V21 补丁的行为。

## 新记录应包含什么

记录服务（FoundationHost 与 worker）版本和文件哈希、TIA 版本/已知补丁、精确工程与 PLC、工具参数（排除凭据）、预览与实际结果、读回内容、编译错误/警告以及是否保存。失败或结果不确定时保留对应日志，先确认工程实际状态，再决定下一步。最新构建不会覆盖上述历史证据。

预发布测试者的报告先登记在[测试者反馈台账](../plan/progress.md)；复核后满足上述要求的通过结果再写入本台账，并注明反馈编号。

### VMFIX-1 待复验（2026-11-01）

以下只登记需要测试者确认的改动，状态均为 **NOT RUN**；未连接 TIA、PLC 或 VM，离线测试不构成原生验收。复验时按本节要求补充构建哈希、TIA 补丁、工程副本身份、参数、关联 ID 和实际读回。

| 缺陷 / 路径 | 需要的原生复验 |
|---|---|
| worker 超时与附件释放 | 保持默认读取 120 秒及现有长操作预算；验证读超时立即报告 requiresSessionReset=true，关闭输入后已返回的原生调用在原所属线程调用一次分离 / TiaPortal.Dispose；既有优雅退出期限内仍不响应才终止。核对租约是否仅在确认分离后释放，硬终止仍需重启 TIA；不得自动重放请求。 |
| DCC / Startdrive、库和工程比较、SiVArc、Safety 读取 | 各诱发一次缺选件、无对象和连接失效；首个失败与后续会话状态的重置标志一致，信封保留脱敏后的原生异常类型和消息。 |
| L2 PLC / 硬件原语 | CreatePlcBlockGroup 嵌套组、MovePlcBlockToGroup、RepairAndReimportPlcBlock、ImportPlcTagTablesFromDirectory、CreateDevice、CreateHardwareCatalogDevice；核对原调用顺序、STA/MTA 归属、回复转换、前置拒绝与实际读回。 |
| 精确目录设备创建 | 取消两颗示例 CPU 和 family 白名单，保留既有 Unified 面板版本必须匹配 Portal major 的真实故障保护；从同一目录读取得到 ET 200SP、其他 S7-1500 CPU、HMI 面板的精确 OrderNumber 行，预览不创建，确认工程文件和哈希后只调用一次 CreateWithItem。核对根设备/未分组系统组父对象与完整库存读回；HMI 子类型标识需真实目录和 API 确认，禁止猜测、候选探测或回退。 |
| HMI 与共享状态 | expectedProject 传工程名称可读、传文件路径在调用前 INVALID_ARGUMENT；PlanProjectGeneration 和 GetWorkbenchState 核对同一 MCP 绑定；不支持的标签绑定须在创建前拒绝，已确认部分创建须报告 partial 与重置要求。 |
| 覆盖库类型块 | 预览须提示 XML overwrite 可能断开库类型链接，备份无法恢复链接；预览恢复状态为 not-attempted-preview，执行报告实际备份结果，核对库实例、调用者和编译，不将备份文件哈希不稳定误判为导入内容已改变。 |

### FIX-R1 待复验（2026-11-01）

以下项目均为 **NOT RUN**。本次只运行离线和本机回环合成夹具，未连接 TIA、PLC 或 VM。

| 路径 | 需要的原生复验 |
|---|---|
| 工作台 Stop / Restart | 在真实 TIA 附着会话上验证：调用进行中禁用操作，空闲时经宿主停止路径在原 owner 线程分离，等待退出后再启动新宿主；超出宽限期后才显示单独的强制终止确认，并提示 TIA 可能需要重启。 |
| 结果未知时关闭 worker | 分别覆盖非自有附着、headless 自有 Portal 和所有权未知的 Portal。只对明确非自有附着尝试分离；自有或所有权未知时保留 TIA，不调用会关闭它的 TiaPortal.Dispose，保留不确定租约和关闭证据。 |
| Unified 面板版本保护 | V18 / V19 Portal 下的旧版面板，核对 RequireUnifiedPanelVersion 的真实目录版本和拒绝边界。 |
| 设备创建父对象和设备项 | 核对 CreateWithItem 的 UngroupedDevicesGroup 父对象，以及 ET 200SP / HMI 的真实 device-item 路径；不得用猜测路径替代原生读回。 |
| Unified 标签创建 | 在 V17–V19 核对 Tags.Create(name, table) 的实际返回类型和后续属性访问。 |
