# Siemens 官方 Openness 流程核对（2026-09-29）

本轮以 `siemens/tia-portal-ai-extensions` 为重点，对 32 个指南主题建立功能映射，并深入检查版本解析、连接与所有权、线程、事务、结果判定及模型收到的操作指南。发现的问题已在本地源码修复。**这不是对全部 464 个工具完成了真机验收的声明。** 未连接虚拟机、未操作界面、未保存或修改任何 TIA 工程，也未更新发布运行时。

## 依据与证据等级

- [Siemens 官方仓库](https://github.com/siemens/tia-portal-ai-extensions)：当日 `git ls-remote ... HEAD` 为 `2119df978ffe26cf1436384b6d94549b28aa2264`，与 [本地来源记录](../../reference/siemens-openness/UPSTREAM.json) 一致。
- 它是 AI 开发指南集合，不是新增一个 API 的可执行 SDK。32 个主题已经放在 `reference/siemens-openness/skills/`；`ReadOpennessGuidance` 可检索、分页阅读。原始指南保留上游内容，没有为了迎合现有实现而修改。
- 主要人工核对依据为 V21 官方 API 手册：[连接和程序集解析](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/connecting-to-the-tia-portal)、[独占访问](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/exclusive-access)、[事务](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/transaction-handling)、[结束连接](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/terminating-the-connection-to-the-tia-portal)、[进程诊断接口](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/diagnostic-interfaces-on-tia-portal)。网页不能直接读取的章节通过官网 FluidTopics API 获取，map 为 `gpR5ZkKnLSuzVoGX1ovoKg`。
- 手册正文已核对的 contentId：连接 `DP7NDA58oox~t1~oZJkHQQ`；独占 `rhL8NoRL_eJ7V9_QnNsl2w`；事务 `J8~ErHJhdmM5UVbcQMausw`；关闭项目 `BbxsaHKFjnxznS8ytLSwZA`；PLC 交叉引用 `jVaQIEt37wzizA32Z3dJBA`。
- V21 和 V20 分别使用根目录对应的 PublicAPI 编译、做 API 形状检查。官方 AI 仓库的 V21 示例不构成 V20 兼容承诺；编译和反射通过也不等于原生调用成功。

发现参考指南与手册不一致时没有照抄：指南把 `ExclusiveAccess` 写成必须，而手册说是强烈推荐；嵌套独占则明确不允许。指南对 `GetCurrentProcess().Dispose()` 的“安全”说法也不能当成普通句柄释放：V21 XML 文档和手册均说明 **`TiaPortalProcess.Dispose` 会关闭对应进程**。本项目清理使用 `TiaPortal.Dispose` 断开客户端连接，不调用进程对象的 Dispose。

## 已修复的问题

| 编号 | 原问题与影响 | 本地修复 | 验证范围 |
|---|---|---|---|
| F1 高 | `Connect(projectName)` 找不到目标时可退回另一个已打开的工程；空实例回退时还会丢失期望工程名 | 只允许目标工程或空实例；保留期望名称；多用户会话也参与名称匹配；释放未选中的客户端连接 | 纯逻辑选择回归；未做多进程 TIA 验收 |
| F2 高 | `Portal.Dispose` 无条件 `Project.Close`；所有权布尔值可能随重新绑定被错误继承；类还未实现 `IDisposable` | 所有权跟踪实际打开的项目对象，换对象不能继承；只关闭自己打开的工程；实现 `IDisposable` 供宿主清理；断开时清除绑定与缓存；补上 archive retrieve / Open 回退的所有权记录 | 假代理身份、共享工程保持打开、失败后仍断连的回归；真实 TIA 退出行为待验收 |
| F3 中 | `ConnectPortal` 清空旧连接字段却未释放旧客户端；超时 Attach 后成功返回的客户端无人释放 | 自有工程仍打开时拒绝隐式替换；其他重连先断开旧连接；MTA 后台 Attach 用明确的结果所有权交接，超时后晚到的连接由创建线程释放 | 成功、超时后成功、异常、MTA 线程回归；无法取消正在阻塞的原生 Attach |
| F4 高 | 事务中 `CanCommit=false` 仍可能因所有工具返回成功而报告 committed=true | 抽出实际执行路径；检查明确的操作成功、取消、CanCommit、CommitRequested；Dispose 成功后才确认结果；Dispose 异常不声称已回滚 | 取消、工具失败、失效提交、未请求提交、Dispose 异常等回归 |
| F5 高 | 事务接收几乎任意工具，错误声称其副作用均可回滚；参数错误可能在前面的写入之后才发现 | 仅允许审查过的 8 个同步工程编辑工具，执行前预检全部参数；拒绝编译、在线、保存、会话、文件/Git/伴随进程、嵌套编排及未审核工具 | 两个编译产物直接调用工具验证拒绝路径与允许的离线预览 |
| F6 中 | 两个 PLC 文件夹工具在事务内重复申请 ExclusiveAccess，触发官方禁止的嵌套独占 | `CreatePlcTypeGroup` / `DeleteEmptyPlcBlockGroup` 复用外层独占访问，并复用绑定工程检查 | 双版本编译、调用路径检查；原生 undo/rollback 待验收 |
| F7 高 | 显式 V20 路径仍参与“取所有候选最大版本”；解析器未校验 DLL 完整身份；错误版本环境路径被当最后回退 | 显式路径版本优先；解析前比较名称/版本/公钥标记；不再回退到标注为其他版本的目录；程序目录的通用解析器不再兜底加载 Engineering DLL | 优先级、版本/名称/token 不匹配测试；宿主真实多版本安装待验收 |
| F8 中 | 构建默认路径含开发者机器的 `D:\app\TIA20`，运行时缺少 Global/Registry32 发现路径 | 构建默认从官方 Global 注册表取得路径；运行时查两种视图的 TIA_Opns/Global；保留用户显式 PublicAPI 路径；移除默认安装目录枚举 | 两版本显式本地 PublicAPI 构建；不同安装布局待验收 |
| F9 中 | 模型指南含过期工具数量、无条件写后保存、用 ModifiedDate 证明内容落地等不严谨流程 | 增加 `openness-workflow` 主题和官方文档路由；统一 Bootstrap/ServerInstructions 的保存范围；要求内容回读，编译/保存放到事务外；修正 Bootstrap 的中文 SCL 编码说明 | 两版本指南工具入口、文档/工具引用检查 |

事务允许列表：`CreatePlcTypeGroup`、`DeleteEmptyPlcBlockGroup`、`ManagePlcUserGroup`、`ManageDeviceUserGroup`、`ManageUnifiedHmiGroup`、`DeleteEmptyUnifiedHmiScreenGroup`、`UpdateUnifiedObjectProperties`、`UpdateUnifiedMultilingualProperty`。**这是一次有意收紧的调用契约**：其他工具仍可单独使用，但不能因带 `[WRITE]` 标签就假定支持事务。`ApplyToolBatch` 仍是顺序执行，失败前的操作可能保留。

核心实现位于 `tools/tiaportal-mcp/src/TiaMcpServer/`：

- `Siemens/Portal/Portal.cs`、`Siemens/ProjectOwnership.cs`、`Siemens/TimedAttachment.cs`：连接与清理。
- `Siemens/Engineering.cs`、`Siemens/EngineeringAssemblyIdentity.cs`、`Directory.Build.props`：版本与 DLL。
- `ModelContextProtocol/TransactionExecution.cs`、`ModelContextProtocol/Tools/McpServer.BaseLeftovers.cs`：事务协议与执行。
- `ModelContextProtocol/McpGuides.cs`、`ModelContextProtocol/Tools/McpServer.Guides.cs`：官方开发流程入口。
- `tools/tiaportal-mcp/tests/TiaMcpServer.Tests/OfficialWorkflowTests.cs`：本轮故障回归（相对于仓库根目录）。

## 32 个官方主题的对应情况

“已有入口”只说明存在实现/注册工具并完成主题映射；不表示已逐分支核对所有语义或全部通过真机测试。“深入核对”指本轮已结合实际代码、官方规则与本地测试检查。

| 官方主题 | 本项目对应 | 本轮结论 |
|---|---|---|
| openness-overview | Bootstrap / Doctor / 官方指南检索 | 已接入开发流程，不能把指南当 SDK |
| openness-base | V20/V21 csproj、Engineering 解析器 | 深入核对；修复版本优先级和 DLL 身份验证 |
| session-and-project | Connect、Attach、Open/Create/Retrieve、Dispose | 深入核对；F1–F3，真实退出待测 |
| threading-and-concurrency | SerializedCallTool、MTA Attach、可选 worker 队列 | 2026-10-01 增加进程隔离及有界队列；跨 MCP 的同一 TIA 协调和精确工程身份仍未实现 |
| engineering-objects | 属性/服务入口、RunToolsInTransaction | 深入核对；F4–F6，保留明确的事务边界 |
| crash-diagnosis | InvocationJournal、HMI 读故障阻断、PLC 交叉引用禁用、可选 worker 故障锁定 | 新增宿主/子进程关联日志及故障后可读诊断；仍缺全面的原生调用级日志与事件/转储关联 |
| openness-testing | net8 纯逻辑、net48 API/HTTP/产物测试，独立 net48 V20/V21 NativeTests | 2026-09-30 补齐显式启用的自有 scratch 工程生命周期框架及进程监督；仅完成编译/离线安全检查，原生执行与各工具族矩阵仍待验收 |
| performance-and-caching | 软件容器缓存、分页、ReadObjectIdentifier | 部分覆盖；缺少统一缓存失效与所有读取路径的批量属性策略 |
| object-tree-walking | 有界对象树、GetService/DescribeService、精确软件查找 | 已有入口；全服务遍历不等于无崩溃风险 |
| change-detection | ReadPlcChecksums / ReadPlcObjectFingerprints / 安全签名 | 已有入口；校验和、对象指纹、F 签名语义须保持区别 |
| licensing-and-firewall | Doctor、用户组检测、授权拒绝识别 | 已有入口；选件许可和缺包状态需安装环境验证 |
| devices-and-hardware | AddDevice、ManageHardwareObject、硬件目录工具 | 已有入口；TypeIdentifier 必须来自目标目录/用户真实型号 |
| hardware-and-modules | ManageHardwareObject / ManageDriveHardwareModule | 已有入口；槽位、父子层级、编码器 slot 需设备专用验收 |
| networks-and-drivecliq | 网络接口/子网/端口/IO 系统管理 | 已有入口；物理拓扑与逻辑 IO 连接不能混同 |
| drive-objects | ReadDriveObjects / ManageDriveFunctions | 已有入口；按设备项查找 DriveObjectContainer |
| parameters | ReadDriveParameters / ManageStartdriveParameter | 已有入口；值类型/BiCo 路径已存在；写后刷新和语言相关经验待专项核验 |
| telegrams | ManageDriveTelegrams | 已有入口和既有 G120C 限制；不能照示例解除崩溃保护 |
| drive-control-charts | DCC 的 chart/block/pin/interface/partition 工具 | 已有入口；依赖 Startdrive/DCC 安装及实际工程 |
| online-and-download | DownloadToPlc / CheckDownloadReadiness / 在线工具 | 已有入口；未在本轮连接 PLC、下载或操作运行状态 |
| safety-commissioning | 驱动安全验收工具 | 已有入口；不能把调用成功当安全验收完成 |
| plc-safety-administration | ManagePlcSafety / ManageSafetyGlobalSettings / 签名读取 | 已有入口；运行组块依赖和 setter 顺序需 Safety 工程验收 |
| security | 项目 UMAC、CPU 保护、密码/证书工具 | 已有入口；权限/保护与连接授权不是同一机制 |
| software-hierarchy | 软件容器查找、组工具、层级读取 | 已有入口；根 PLC 与软件单元的作用域需按工具分别确认 |
| blocks | 导入/导出/编译/逻辑回读 | XML、SCL、SD 路由已存在；原生交叉引用默认拒绝保持不变 |
| simatic-sd | ExportAsDocuments / ImportFromDocuments / SD 构建器 | 已有入口；需要保留资源与 pragma，不能把 V21 示例当所有版本通用 |
| plc-data-types | 类型读取、XML 构建/导入、CreatePlcTypeGroup | 已有入口；本轮修复组创建的独占嵌套问题 |
| tags-and-tagtables | 标签/常量/表读写与 XML 交换 | 已有入口；软件单元作用域不能默认为 PLC 根 |
| sw-units | ReadPlcSoftwareUnits / ManagePlcSoftwareUnit / 访问控制 | 有单独入口；不能由此推断每个旧块工具均有 PLC 全范围搜索 |
| technology-objects | TO 管理、Motion 轴与硬件连接 | 已有入口；设备系列能力和实际连接需真机/仿真工程验收 |
| global-library | 库生命周期、版本、主副本、比较同步 | 已有入口；导入替换后的代理失效和版本兼容仍需专项回归 |
| libraries-and-alarms | 库工具、PLC 报警类/文本/XML/XLSX 交换 | 已有入口；不能混淆工程报警配置与运行时报警 |
| tia-addin-scaffold | 官方模板保留在参考目录 | 这是独立的 TIA Add-In 项目模板，不应强行变成 MCP 工程写入工具 |

## 仍需补齐或专项验证

1. **原生验收体系**：已新增独立、显式开启的 V20/V21 net48 live-test 项目，实现自有 scratch 工程的创建、事务提交/回滚、保存重开及回读、清理与超时监督，见[原生测试框架](native-lifecycle-tests.md)。当前用户要求不操作 TIA，原生分支未执行；超时 Attach 晚到代理释放、共享连接的工程保留、替换导入后回读及各工具族矩阵仍未加入该框架。
2. **崩溃定位粒度**：现有调用日志记录工具/桥接的 BEFORE/RETURNED/THREW，不包含每一个原生 getter/service 调用的对象路径。2026-10-01 的[可选 worker](../guides/openness-worker-isolation.md)增加故障锁定、宿主/子进程相关 ID 和显式恢复；它能缩小范围，但不足以证明具体崩溃 API。PLC `GetCrossReferences` 保持默认禁用；未为了符合示例而复现崩溃或扩大全设备跳过列表。
3. **跨客户端一致性**：串行门仅在一个 MCP 进程内有效，同端口客户端共享当前绑定。独立 MCP 进程/端口绑定独立 TIA 实例仍是隔离方案；`PreviewToolBatch` 的工程名/会话/PID 与预览比较不是完整修订锁，也不是分布式事务。同名工程还应扩展明确 PID/完整路径选择。
4. **缓存和作用域覆盖**：缓存清理已修复到断开连接路径，但 GUI/另一个 Openness 客户端修改或导入替换仍需统一失效策略。软件单元的已有工具不代表所有旧工具支持完整的 root + unit 查找。批量属性优化、搜索索引暂停/恢复尚无统一策略；它们是优化与覆盖项，不应未经实测就全局开启。
5. **初始化边界**：现有程序有早期 resolver 注册及已通过的离线启动检查，但 Program 是大型 partial 类，还未完全拆为官方推荐的“无 Siemens 类型的独立启动类 + NoInlining 入口”。真实缺包、授权失败、多版本安装矩阵仍需专机测试。
6. **驱动/Safety**：官方指南有设备和语言相关的经验规则，部分编码器索引例子自身也注明未验证。现有 API 形状可通过编译，但电机/编码器、BiCo、DCC、在线和 F 功能必须按设备族分别验收，不能把示例索引直接写入用户设备。
7. **面板类型内部编辑**：这 32 个指南没有证明可以从零创建 Unified FaceplateType、修改其内部控件或类型局部脚本。已有画面/实例、库版本编辑和元数据能力不能等价为这三项能力；此缺口仍保留，不能用反射猜接口后报告已实现。

## 验证记录

本轮在 Windows 本地使用用户提供的 V20/V21 PublicAPI 副本。源码修复后的实际结果以 [验证清单](../../manifest/ecosystem-validation.json) 为准；原始日志位于忽略目录 `TiaMcp_Output/official-audit/`。

- 两版本 Release 构建均为 0 错误；V21 有 12 条、V20 有 14 条警告，尚未全部清理。
- net8 离线测试为 2551 通过、0 失败、0 跳过。V21/V20 API 形状检查分别为 3097/2805 通过；每版本 localhost HTTP 回归 28 项通过。
- net8 离线测试覆盖本轮 37 个新增断言，验证实际使用的事务执行器、所有权逻辑、超时交接及程序集判定，不以文本匹配替代行为测试。
- 每版本 31 项编译产物检查，直接经 CallTool 验证官方指南入口、允许的事务预览、非法工具及参数的拒绝；另检查 Portal 实现 IDisposable。
- API 形状检查和 localhost HTTP 回归与 live TIA 测试严格分开。

本地二进制仍位于源码的 `bin` / `bin-v20`；`runtime/` 交付目录与虚拟机未更新。事务允许列表收紧需要在下一次正式发布说明中保留，不能作为无行为变化的升级交付。
