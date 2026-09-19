# 工程能力与验收边界

本文说明 2.7.38 引擎的能力与缺口：最新的工具族在前，按版本倒序追加，2.7.14–2.7.15 的基础表格保留在后。静态清单 421 项（7 个大类，见 `ListToolCategories` 与 [工具矩阵](tool-matrix.md)），默认 lite 暴露 56 项。工具数量不表示覆盖全部 API——对照官方 V21 PublicAPI 的逐类型记分板在[官方 API 覆盖清单](openness-coverage.md)（2.7.38：核心程序集 Base / Step7 / 经典 WinCC / WinCC Unified / Safety 与选件包 SiVArc 的功能类型缺口为 0，其余选件包剩 72 类型 / 307 成员）。原生方法按本机官方 V20/V21 PublicAPI 对照实现，每个调用的成员在构建时做程序集形状检查；**真机验收状态按版本分段记录**——每段末尾的"真机"句说明哪些在 V21 参考工程 `AutomaticDipCoatingMachine` 上跑过、哪些只有形状检查（选件包无许可、经典 HMI 无工程），不能混同。

## 2.7.38 新增工具族（阶段 6 ⑥-① SiVArc 选件包）

| 族 | 工具 | 边界 |
|---|---|---|
| 规则层次 | `ReadSivarcRuleTree`、`ManageSivarcRuleContainer`、`ManageSivarcRule` | 要求工程上有 `Sivarc` 服务（无 SiVArc 选件时 NotSupported）；实做写入要 SiVArc 许可（TIA 报 "License not found"）；只删空文件夹 / 空组、默认规则表拒绝删除；条件 / 注释 500 字、名称 128 字的官方上限在参数门就拒；`ConditionOperator` 对不适用的规则回 `None`、写入会被 TIA 拒绝；设备列只对画面 / 报警 / 复制 / 高级变量规则；实例化规则表在发布模式下的编辑被 TIA 拒绝 |
| 块定义 | `ReadSivarcBlockDefinitions`、`ManageSivarcBlockDefinition` | 只对代码块（OB / FB / FC）；`TagMemberSettings` / `CommonParameters` / `BlockParameters` 仅 V21，块参数只 update（镜像块接口） |
| 表达式解析 | `ResolveSivarcExpression` | 只读；PLC 必须已编译且有调用结构；库对象是母本或库类型 |
| 布局数据 | `ManageSivarcScreenLayout` | 仅 V21；经典 `Screen` 与 Unified `HmiScreen` 都可；导出必须是新文件；导入默认预览 |
| 定义升级 / 生成 | `UpgradeSivarcDefinitions`、`GenerateSiVArc`（类型化改造，新增多设备重载） | 默认预览；生成结果按 `IsGenerationSuccessful` 判定 `operationSuccess`；HMI 未连到所选 PLC 时 TIA 抛异常 |
| 类型化改造 | `ReadSiVArcRules` / `ManageSiVArcRule` 锚点、`ReadLibraryType`（`typeKind` 新增 `hmiFaceplate` / `hmiVbScript` / `hmiCScript` / `unifiedScriptModule` / `sivarc*` / `dccBlockType`） | 签名不变 |

**真机不可验证**（虚拟机没有 SiVArc 许可；部署后只验 NotSupported 与 `typeKind`）；形状检查 V20 2301 / V21 2497 对本机 PublicAPI 逐成员核对通过。

## 2.7.37 新增工具族（阶段 5 经典 WinCC 文件夹层次，核心程序集收口）

| 族 | 工具 | 边界 |
|---|---|---|
| 画面树 / 画面对象 | `ReadClassicHmiScreenTree`、`ManageClassicHmiScreenObject` | 只对经典 `HmiTarget`（Unified 目标明确拒绝）；滑入画面按 `SlideinType` 寻址、无 Name / Delete；总览 / 全局元素每台设备一个，只有 Export / Import；导出必须是新文件；删除与 Override 导入要 `confirmDelete`；普通画面仍走原有画面工具 |
| 文件夹 | `ManageClassicHmiFolder` | 用户文件夹 `Name` 只读（无原生改名）；只删空文件夹；系统文件夹拒绝删除 |
| 多语言图形 | `ManageClassicHmiGraphic` | `GraphicsProvider` 仅 V21；导出写 XML + 图片文件（默认语言后缀 `default`，同名自动编号）；不读图片字节 |
| 类型化改造 | `ReadClassicHmiScripts` / `ManageClassicHmiScript`、`ReadLibraryType`（`typeKind`）、`EngineeringScalarProperties.Json`（`ConstValue` / `NullableDateTime` 渲染钩子） | 签名不变 |

真机（V21 `AutomaticDipCoatingMachine`，2026-09-19）：四个工具对 Unified `HMI_RT_1` / PLC 目标按预期 NotSupported；`typeKind` 在 1,038 个工程库类型上核对（`hmiUdt` 4 个真实命中；Unified `ScriptModuleType` 与基类 `LibraryType` 面板暂回 `other`，2.7.38 补 `hmiFaceplate` / `hmiVbScript` / `hmiCScript` / `unifiedScriptModule`）；`ConstValue` / `NullableDateTime` 只出现在经典 HMI 动态属性里，Unified 工程验不到，钩子对 Unified 标量读取无回归。经典 WinCC 实做路径无经典 HMI 工程可验；形状检查 V20 1957 / V21 2140 对本机 PublicAPI 逐成员核对通过。

## 2.7.36 新增工具族（阶段 4 ④-③ 工艺对象映射，阶段 4 收口）

| 族 | 工具 | 边界 |
|---|---|---|
| 工艺对象树 | `ReadTechnologyObjectTree` | 只读；每组最多 200 个对象、每个对象最多 500 个参数；`includeMotionView` 只看根组前 50 个对象；参数值按标量 JSON 渲染，复杂值给类型名 |
| 类型化 Motion | `ReadMotionAxisConfiguration`（`typed`）、`ManageMotionAxis`、`ConfigureMotionHardwareConnection`、`ManageTechnologyObject` | 签名不变；`targetJson` 新增 `channelType` + `channelIoType` + `channelNumber`（`Connect(Channel)`，扭矩接口没有）；`TOMapping` / `DBMemberMapping` / 叠加轴 / Ident 仅 V21，V20 回 NotSupported 或反射回退；V20 的 `Connect(Telegram)` 只经反射；`ConnectOption` 只对两模块 / 位地址目标；从不下发运动命令 |

形状检查 V20 1879 / V21 2045 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-19，`AutomaticDipCoatingMachine`）**：临时 `TO_SpeedAxis` V9.0 上类型化树 / 42 个参数 / 执行器与扭矩接口行 / `Connect(DeviceItem)` 到原生门控（"Target not available for input=-1 and output=-1"，驱动无地址）/ 通道目标门控 / `Disconnect` / 删除通过。已知：工艺对象的 `typeIdentifier` 形式是 `TO_SpeedAxis`（不是 `System:TO.…`），1510SP F V4.1 + TIA V21 用 TO 版本 9.0；速度轴没有传感器接口、不提供 `InterpreterMappings`。

## 2.7.35 新增工具族（阶段 4 ④-② Step7 收尾）

| 族 | 工具 | 边界 |
|---|---|---|
| 外部源 | `ManagePlcExternalSources` | 源文件必须已在 TIA Portal 机器上且是 ASCII（.scl / .awl / .stl / .db / .udt）；`PlcExternalSource` 在 PublicAPI 只有 `Name`；`generateBlocks` 原生覆盖同名对象、出错时工程回滚到调用前；用户组只删空组；V20 的 `PlcExternalSourceUserGroup.Name` 只读（改名 V21+）；从块生成源文件仍是 `GeneratePlcSourceFromBlocks` |
| 系统组 / 常量 | `ReadPlcSystemGroups`、`ReadPlcTagTableConstants` | 系统组只读（TIA 生成）；每组最多列 200 个对象；用户常量的写入走 `ManagePlcTag kind=constant` |
| 报警文本列表 XLSX | `ExchangePlcAlarmTextListsXlsx` | 过滤重载要求文本列表与语言同时给出；TIA 原生拒绝系统文本列表与未激活语言（`UserException`）；`ImportOptions.None` 遇到已有列表报错、`Override` 替换其条目；导出文件必须是新文件 |
| 表条目 | `ManagePlcTableEntries` | 只读配置值不是在线值；强制表刻意只读（`PlcForceTableEntry` 不提供写）；`Create()` 只能追加注释行（带地址的条目只能导入）；删除按 0 基索引并回数，但 TIA 拒绝删 Openness 建的空注释行（2.7.35 真机） |
| ProDiag | `ExportPlcProDiagInfo` | 只对语言为 ProDiag 且一致的 FB；目录须已存在；输出 CSV |
| 类型化改造 | `ManageWatchForceTableWebAccess`、`ReadOpcUaAccessControl` / `ManageOpcUaAccessControl`、`ExportAlarmClasses` / `ImportAlarmClasses`、`ManagePlcSupervision exportSettings/importSettings`、`ReadLibraryType`（`typeKind`）、`ManagePlcUserGroup`（新增 `watchTables` / `externalSources`） | 签名不变，行里多了原生类型信息；`AlarmClassDataProvider` 在 PLC 上拿不到时回退到工程 |

形状检查 V20 1784 / V21 1929 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-19，`AutomaticDipCoatingMachine`）**：外部源从 SCL 文件建源、生成 FC 到临时用户组、删源 / 改名 / 删组全链通过；系统块组 / 类型组、默认变量表 79 个系统常量、类型化 OPC UA 限制行 / 报警类消息 / 库类型 `typeKind` 通过；文本列表 XLSX、ProDiag、Web 访问规则到达原生门控（无用户文本列表 / 无 ProDiag FB / Web 服务器未启用）。已知：`PlcTableCommentEntryComposition.Create()` 后同一代理 `Count` 陈旧（2.7.36 改为重新导航计数）；Openness 建的空注释行 `Delete` 被 TIA 拒绝；变量表路径要带组名（`IO/F_Input`）。

## 2.7.34 新增工具族（阶段 4 ④-① Step7 软件单元与 PlcSoftware 小服务）

| 族 | 工具 | 边界 |
|---|---|---|
| 软件单元 | `ReadPlcSoftwareUnits`；`ManagePlcSoftwareUnit`（新增 `unitKind=safety`、`createFromMasterCopy`、`commentsJson`） | 只有部分 PLC 支持单元（`PlcUnitProvider` 为 null 时 NotSupported）；安全单元系统生成、不可创建 / 删除；`Name` 改名拒绝（要引用影响评审）；`Comment` 是 `MultilingualText`，按激活的工程语言逐条写 `Items[i].Text`；`PlcUnitSystemGroup.Name` 仅 V21 且按界面语言本地化（"软件单元"）；关系 Create / Delete 后旧组合代理失效（2.7.34 真机，2.7.35 修） |
| 文档 | `ManagePlcDocuments` | `PlcDocument` 在 PublicAPI 只有 `Name`；导出目录必须已存在且不含 `name.*`；导入结果 `PartialSuccess` 不算失败但会带原生消息；文档的 `CreateFrom(MasterCopy / PlcDocumentLibraryTypeVersion)` 仅 V21，UDT 的两种 `CreateFrom` 两版都有 |
| 校验和 / 指纹 | `ReadPlcChecksums`、`ReadPlcObjectFingerprints` | 校验和未编译为 null；指纹只看用户输入、对象不一致时原生拒绝；`FingerprintProvider` 对变量表不存在；在线 CPU 指纹仍是 `ReadPlcBlockFingerprints` |
| 块写保护 | `ManagePlcBlockWriteProtection` | 仅 V21；官方状态机（define → protect / unprotect → change / remove）在调用前门控；实做要 `dryRun=false` + `confirmProtectionChange=true` + Offline；密码不回显 |
| 工程编译设置 | `ManageProjectCompilationSettings` | V20 是 `Project` 属性；V21 删掉了属性、只剩 `PlcSimulationSettingsProvider` / `VirtualPlcSettingsProvider` 服务（官方页面仍按属性举例） |
| 通道关联变量 / 过程映像 | `ReadDeviceItemChannels includeLinkedTags`；`UpdateDeviceAddress processImageObName` | `PlcTagProvider` / `ProcessImageProvider` 仅 V21（`Channel` / `Address` 在 V20 不是服务提供者）；V20 仍走 `Address.AssignProcessImageToOrganizationBlock` |

形状检查 V20 1670 / V21 1810 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-19，`AutomaticDipCoatingMachine`）**：临时单元建 / 改（标量 + 多语言注释）/ 关系 / 删、UDT `.s7dcl` 导出与 Override 导入（原生消息回传，UDT 名在整个 PLC 内唯一）、校验和、指纹（含不一致对象的原生拒绝）、块写保护 define → protect → unprotect → change → remove 全链、编译设置经 V21 服务提供者读写、DI / F-DI / DQ / AI 的通道关联变量、事务内层不带 `dryRun` 的提交与回滚通过。已知缺陷（2.7.35 修）：单元关系 Create 后同一组合代理 `Find` 回 null、Delete 后抛 `EngineeringObjectDisposedException`——要从单元组重新导航回读。未验证：安全单元本体（工程没有 `SafetyUnit`）、单元 / 文档的母本与库类型实例化、`.nvt` 文档导出导入。

## 2.7.33 新增工具族（阶段 3 ③-④ Base 收尾）

| 族 | 工具 | 边界 |
|---|---|---|
| 门户诊断 | `ReadPortalInfo` | `TiaPortalProcess` 是快照（`AcquisitionTime`），不阻塞；`TextCategories` 只在 V21；无工程也可调 |
| 传输路由 / R/H | `ReadTransferRoutes`；`DownloadToPlc` / `GoOnline` 的 `rhTarget` | 只读路由树、不套用；`rhTarget` 需要 R/H 系统的 `RHDownloadProvider` / `RHOnlineProvider`（参考工程没有，仅形状检查）；V20 的 R/H 在线没有地址重载 |
| 硬件工具 | `ManageHardwareUtilities` | `Project.HwUtilities.Find`；OPC UA 导出只对 PLC 项、PSC 导出只对设备且拒绝覆盖，F 激活设备在 V18 及以下拒绝，加密需 CPU V40.0+；密码不回显 |
| 设备服务对象 | `ManageDeviceServiceObjects` | Web 应用与遥控数据点对象是 V21 专有（V20 只剩遥控数据点的导入导出）；`RemainingCertificateLifetime` 10..90，`Runtime` 用途在 Web 服务器 / OPC UA 服务器停用时被 TIA 拒绝，`ServiceGroupName` ≤ 64；V20 的服务 Id 是只读 `UInt16` |
| 对象标识 / 编辑器 | `ReadObjectIdentifier`、`ShowObjectInEditor` | 官方只保证设备、设备项、块、变量、软件单元、工艺实例 DB、`PlcStruct`；`ShowInEditor` 只动 UI、需要带界面的 Portal；V20 的设备不实现 `IShowable` |
| 事务 | `RunToolsInTransaction` | 一个 `ExclusiveAccess` + `Transaction`，内层工具复用独占访问；全部成功才提交；`CallTool` / 嵌套事务拒绝；`TargetInvocationException` 之后再 `CommitOnDispose` 会被 TIA 拒绝。**2.7.33 缺陷**：内层调用要显式写 `"dryRun": false`，否则按预览跑却回报已提交（2.7.34 修） |
| 受保护工程 / UMAC 在线 | `OpenProject`（`umacUserName` / `umacPassword` / `umacUserType`）、`GoOnline`（`userName` / `userType`） | 凭据只进 SecureString；`GetSupportedAuthenticationTypes` 与 `IsSecureCommunication` 回报到 meta，密码永不回显 |
| 改绑守卫 | 所有写工具 | 显式绑定的工程名被记住：自愈只回绑它，写前核对 `_project.Name`，不一致直接拒绝并要求 `AttachToOpenProject` |

形状检查 V20 1585 / V21 1708 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-19，`AutomaticDipCoatingMachine`）**：门户诊断、传输路由、硬件工具（OPC UA 导出实做）、设备服务对象读取、对象标识往返、事务提交 / 回滚、类型化交叉引用与目录行通过，见 [v2.7.33](../releases/v2.7.33.md#真机结果v21-automaticdipcoatingmachine2026-09-19)；R/H、UMAC 凭据打开 / 在线、PSC 导出未验证。

## 2.7.32 新增工具族（阶段 3 ③-③ 用户管理与安全）

| 族 | 工具 | 边界 |
|---|---|---|
| syslog | `ManageSyslogServers` | `scope=project` 是 `SyslogServerProvider` 的工程级服务器（V19+），模块分配用 `AssignedModules` 关联的 `Add` / `Remove`——**真机上 `Create` 让 TIA Portal V21 崩溃（两次复现），实做前先看 [v2.7.32](../releases/v2.7.32.md#真机结果v21-automaticdipcoatingmachine2026-09-18)**；`scope=plc` 是 CPU 的 `SysLogConfigurationManager`（S7-1500 FW 3.1+），TLS 协议要配合三个证书动态属性；删除需 `confirmDelete` |
| 密码策略 | `ManagePasswordPolicy` | `umac` 8 项、`plc` 只有 `PasswordPolicyEnabled`（S7-1200/1500 沿用 UMAC 复杂度）、`legacyPlc` 5 项且官方范围 5..8 / 0..8 / 0..8 在调用前核；超范围 TIA 抛 `PasswordPolicySettingsException`；已有密码不会被重新校验；实做需 `confirmChange` |
| UMC 用户 / 组 / 服务器 | `ManageUmcUsers` | 需要受保护工程（`UmacConfigurator`）；`importFromServer` / `checkConsistency` / `synchronize` 经 `Authentication` 事件送 UMC 凭据（SecureString、不回显），需要 UMC View 权限的账号；offline 用户 / 组不需要服务器；同名 offline 创建 TIA 抛 `EngineeringTargetInvocationException`；`UmcServer` 没有公开标量；实做需 `confirmChange` |
| 证书模板 | `ManagePlcCertificate`（`template` / `create` + SAN / `import` + `password`） | `CertificateUsage.None` 与 `SignatureAlgorithm.None` 官方不支持；SAN 类型 Dns / Email / IP / Uri；私钥永不导出；2.7.32 的 `template` 误要求 `certificateId`（给任一现有 Id 可绕过，2.7.33 修） |
| 匿名用户 | `ManageProjectUserManagement`（`activateAnonymousUser` / `deactivateAnonymousUser`） | 每个受保护工程一个匿名用户；停用后 `AnonymousUser` 为 null，依赖无密码访问的客户端会被锁在外面，默认预览 |

形状检查 V20 1403 / V21 1500 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-18，`AutomaticDipCoatingMachine`）**：密码策略三套读写、CPU 级 syslog 读取、offline UMC 用户 / 组全流程、证书模板 + SAN 创建 / 删除、匿名用户预览、设备功能权典型行通过；工程级 syslog `create` 让 TIA 崩溃，见 [v2.7.32](../releases/v2.7.32.md#真机结果v21-automaticdipcoatingmachine2026-09-18)。

## 2.7.31 新增工具族（阶段 3 ③-② 库深层）

| 族 | 工具 | 边界 |
|---|---|---|
| 库总览 / 类型 | `ReadLibraryOverview`、`ReadLibraryType` | 工程库或**已打开**的全局库（不隐式打开）；类型 `Status` / 版本 `Dependencies` / `Dependents` 在 InWork 版本上官方注明可能抛，逐字段捕获；`maxDepth` / `maxItems` 有界 |
| 类型操作 | `ManageLibraryType` | `SetForUpdate` 在工程库类型与写保护类型上被 TIA 拒绝；`updateLibrary` 目标须是另一个库；`updateProject` 只接受精确软件路径作范围，不做全工程 |
| 更新检查 / 同步 | `CheckLibraryUpdates`、`SynchronizeLibrary` | 检查只读；同步/协调/清理实做需 `confirmChange`，范围必须显式给出；`HarmonizeProjectOptions.None` 被 TIA 拒绝；全局库源会先同步工程库；清理只删未用且非 in-test 的版本 |
| 详细比较 | `CompareLibraryObjects` | 只比同类对象；`OriginalLibrary` 官方默认不比较 |
| 全局库 | `ManageGlobalLibrary`（`infos` / `openInfo` / `archive`） | 系统/企业库只读；归档前必须已保存，`None` / `DiscardRestorableData` 产物不能经 API 检索 |
| 文档导入 | `ImportLibraryTypeDocuments`（`typePath` 版本导入） | 仅工程库；同名多扩展文件时原生拒绝；`CreateOptions.None` 遇 in-work 版本原生失败 |

形状检查 V20 1291 / V21 1388 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-18）**：全局库头 / 母版树、`ReadLibraryType` 三种入口、`ManageLibraryType` 预览、`CompareLibraryObjects` 三种对象、`ManageGlobalLibrary infos/openInfo` 通过；工程库路径因 `ProjectLibrary` 没有 `Name` 全部失败，系统库 `TypeFolder` 为 null 未守卫，`UpdateCheck` 对系统库触发 `NonRecoverableException`，`close` 对系统库报 "not found"——四处在 2.7.32 修，见 [v2.7.31](../releases/v2.7.31.md#真机结果v21-automaticdipcoatingmachine2026-09-18)。

## 2.7.30 新增工具族（阶段 3 ③-① 硬件网络深层）

| 族 | 工具 | 边界 |
|---|---|---|
| IO 系统 | `ReadIoSystems`、`ManageIoSystem` | 按子网或接口设备项；`create` 要求接口已连子网且无 IO 系统（空名 = TIA 默认名）；`connect` 要求 IoConnector 未连接且与目标控制器同子网；`Number` 超出界面范围只在编译时失败；动态属性逐名读、失败逐条列出 |
| 同步域 / MRP | `ReadNetworkDomains`、`ManageNetworkDomain` | 只在暴露 `SyncDomainOwner` / `MrpDomainOwner` 的（PROFINET）子网上可用；参与者只能 `Add`（官方关联没有移除）；只支持 `NetworkInterface` 作为参与者（`IoSystem` 亦实现 `ISyncDomainParticipant`，本版未暴露） |
| 传输区 | `ReadTransferAreas`、`ManageTransferArea` | 类型创建后不可改；位置号缺失时 PN/PN 耦合器拒绝创建子模块；CCDX 删发送方连带删全部接收方；映射规则 `Target` 须是 IO 设备上的模块；"编译要求所有设备离线"是 TIA 侧约束 |
| 通道 | `ReadDeviceItemChannels`、`UpdateDeviceItemChannel` | 无通道的模块返回空集；可写的动态属性由 TIA 决定，写入后按当前值类型读回 |
| 地址 / 硬件标识符 | `ReadDeviceAddressing`、`UpdateDeviceAddress` | 改 `StartAddress` 可能连带移动同模块另一 IoType、不重连变量、不支持打包地址；`InterruptObNumber` 仅 S7-300/400；`AssignProcessImageToOrganizationBlock` 仅 V20 |
| 设备用户组 | `ManageDeviceUserGroup` | `CreateFrom(MasterCopy)` 未暴露；只删空组 |
| 设备用户 | `ManageDeviceUsers` | Web 服务器 / OPC UA 未启用（或未启用用户名密码认证）时 TIA 拒绝 create/delete/setPassword，读取仍可用；SIWAREX 用户是固定槽位（无 Create/Delete）；密码转 `SecureString`、不回显、不可读回 |
| 端口互连 | `ManagePortInterconnection` | 同一接口的两个端口、已互连的伙伴、不支持备选伙伴的第二连接均被 TIA 拒绝 |

形状检查 V20 1158 / V21 1255 对本机 PublicAPI 逐成员核对通过；**真机（2026-09-18，`AutomaticDipCoatingMachine`）13 个工具全部到达真实对象**，读取与可复原写入通过，见 [v2.7.30](../releases/v2.7.30.md#真机结果v21-automaticdipcoatingmachine)。真机暴露的两处引擎缺陷（域组合代理在 Create/Delete 后陈旧；`EngineeringObjectDisposedException` 误触发连接失败保护）在 2.7.31 修。

## 2.7.19 新增工具族

| 族 | 工具 | 边界 |
|---|---|---|
| PLCSIM Advanced（`Simulation` 域） | `ReadPlcSimAdvancedInstances`、`ManagePlcSimAdvancedInstance`、`ReadPlcSimAdvancedTags`、`WritePlcSimAdvancedTags`、`RunPlcSimAdvancedTestScenario` | 官方 `Siemens.Simatic.Simulation.Runtime` API 运行时定位并反射调用，DLL 不随包分发；未安装返回 `ApiNotFound`。实例变更 / 写值 / 场景默认预览，需 `confirmInstanceChange` / `confirmWrite` / `confirmRun`。**本机无 PLCSIM Advanced，未做真实运行验证**：API 成员名按官方 V4–V7 文档，版本差异（如 `UpdateTagList` 重载）已做回退，仍可能在真实环境暴露差异 |
| 离线文档 | `RenderPlcBlockDocument`、`GeneratePlcDocumentation` | Mermaid 图按导出连线生成，不是梯形图版式；SCL 由令牌还原，不可回导；手册最多 2000 个文档 |
| SCL 预检 | `LintPlcSclSource` | 13 条启发式规则，"无发现"不等于可编译；`CompileSoftware` 是判决 |
| AML 生成 | `BuildDeviceAmlDocument` | 推荐传 `referenceAmlPath`（`ExportDeviceAml` 导出）复用版本匹配的头部与角色类库；内置骨架**未经真实导入验证**，`Meta.importVerified=false` |
| 写保护钩子 | `hooks/tia-write-guard.ps1`（Claude Code PreToolUse） | 审计非只读调用、拒绝真实 ONLINE-WRITE；只对 Claude Code 插件生效，其他 MCP 客户端仍靠工具自身的 `dryRun` + `confirm*` |

分类修正：6 个运行时/上载写入工具由 WRITE 改为 ONLINE-WRITE。

## 2.7.18 新增工具族

| 族 | 工具 | 边界 |
|---|---|---|
| 下载提示（缺陷修复） | `DownloadToPlc` 新参数 `userManagementMode`、`promptAnswersJson`、`moduleAccessPassword`、`blockBindingPassword`、`masterSecretPassword` | 43 种提示按真实形态应答；破坏性提示默认 NoAction/NoChange，需 `promptAnswersJson` 显式指定；未应答提示回传 `Meta.promptsUnanswered` |
| 设备传输 | `ScanAccessibleDevices`、`UploadStationFromPlc`、`UploadDeviceParameters`、`DownloadPlcToFolder` | 扫描为在线网络探测；上载要求 `confirmUpload`，目标地址须与扫描结果精确一致；参数上载 V20 无 API；文件夹下载要求新目录或空目录 |
| PLC 块服务 | `ManagePlcBlockProtection`、`ManagePlcDataBlockSnapshot`、`UpdatePlcProgram`、`ReadPlcBlockFingerprints`、`ImportPlcAlarmInstanceTexts`、`ManagePlcAlarmTextList` | 保护/取消保护需 `confirmProtectionChange`；快照装载改变 CPU 实际值需 `confirmValueChange`，V20 无 `ValueService`；指纹读取是在线调用；报警文本列表 API 无条目与 `Create(string)`，只有主副本创建/删除 |
| 工程安全与协作 | `ReadProjectUserManagement`、`ManageProjectUserManagement`、`ReadProjectProtection`、`ManageMultiuserSession`、`CompareLibraries`、`CompareProjects`、`ReadProjectSettings`、`ManageUmcUsers`、`ManagePasswordPolicy`、`ManageSyslogServers`、`ReadPortalInfo`、`ReadObjectIdentifier`、`RunToolsInTransaction` | UMAC 17 种动作（含匿名用户激活）需 `confirmChange`；不启用/停用工程保护；UMC 导入 / 同步 / 一致性检查由 `ManageUmcUsers` 经 `Authentication` 事件送凭据（2.7.32）；官方无工程级"已保护"标量；比较接口 V20/V21 命名空间不同，按反射绑定 |
| 硬件服务 | `ReadCommunicationConnections`、`ManageCommunicationConnection`、`ManageWatchForceTableWebAccess`、`ExchangeSystemDiagnosticsSettings`、`ReadOpcUaAccessControl`、`ManageOpcUaAccessControl`、`ImportDeviceAml`、`ReadHardwareFeatures` | 通信连接与 OPC UA 访问控制 V20 无 API；连接创建要求调用方精确指定拥有 `ConnectionComposition` 的对象；AML 导入需 `confirmImport` 并回传原生日志哈希 |
| Unified 原生交换（2.7.28，2.7.29 真机修复） | `ExchangeUnifiedTags`、`ExchangeUnifiedScriptModules`、`ImportUnifiedOpcUaAlarms` | 导出要求新目录、每个原生文件哈希（变量导出回报的无扩展名路径解析到实际 `.hmi.yml`，附 `reportedPath` 与 `directoryListing`）；导入要求现有目录并核对期望名/模块名；`OpcUaAlarm` 只在 OPC UA 连接上存在；默认预览，无保存/编译/下载 |
| Unified 画面对象（2.7.26，2.7.27 真机修复） | `DescribeUnifiedScreenItemType`、`ManageUnifiedScreenItem` | 目录与 schema 反射自加载的官方 API（V21 43 个具体类型 / V20 37 个）；`create` 经原生 `Create<T>(name[, containedType])`，按名称查回核对；部件嵌套对象、多语言按 culture（任意深度，如 `Title.Text`）、颜色 `#AARRGGBB`，每个叶子读回；`delete` 需 `confirmDelete`；事件/动态化仍用各自工具 |
| Unified UI 对象模型 | `ReadUnifiedObjectEvents`、`ManageUnifiedObjectParts`、`ManageUnifiedDynamization`、`ManageUnifiedScreenLayout`、`ManageUnifiedListEntries`、`ReadUnifiedAlarmCommon`、`ReadUnifiedAuditSettings` | 阈值/数据网格/报警行列无原生 `Create`；画面复制官方无 API，布局字段导入导出是 SiVArc 选件的 `LayoutData`（`ManageSivarcScreenLayout`，V21）；Unified 列表条目官方无类型，`ManageUnifiedListEntries` 的变更动作明确 NotSupported |
| Motion / ProDiag / 经典 HMI | `ReadMotionAxisConfiguration`、`ManageMotionAxis`、`ManagePlcSupervision`、`ReadClassicHmiScripts`、`ManageClassicHmiScript`、`ManageClassicHmiCycle`、`ManageClassicHmiTextGraphicList`、`ReadClassicHmiGlobalization`、`ReadClassicHmiFaceplates` | ProDiag 无类型化监督组合，只能经官方动态组合接口；经典 HMI 脚本/周期/列表无 `Create(string)`，新对象只能经原生 XML 导入 |
| 运行时通道（不经 Openness） | `ReadPlcWebVars`、`WritePlcWebVars`、`ReadPlcWebDiagnostics`、`SetPlcWebOperatingMode`；`ReadUnifiedRuntimeTags`、`WriteUnifiedRuntimeTags`、`ReadUnifiedRuntimeAlarms`、`UnifiedOpenPipeRequest` | 写入与模式切换需 `confirmWrite` / `confirmModeChange`；证书默认校验；Open Pipe 只在本机、需 "SIMATIC HMI" 组；订阅类消息拒绝 |
| 离线分析 | `ComparePlcBlockDocuments`、`ScanPlcSourceAnnotations`、`ExtractPlcBlockMetrics` | 指标来自导出文档，不是西门子质量判定 |
| 分类 | `ListToolCategories`；`FindTools(category=…, domain=…)` | 分类来自引擎内 `ToolTaxonomy` |

## 2.7.14 工具族（工具与范围）

| 工具 | 已实现动作 | 参数重点 |
|---|---|---|
| CreatePlcTypeGroup | 类型组多级创建、幂等复用 | softwarePath、groupPath |
| ManagePlcUserGroup | blocks/types/tags/technology/watchTables/externalSources 六类组的 create、rename、deleteEmpty，结果组读回为类型化行 | family、相对 groupPath、newName |
| ManageTechnologyObject | read、create、delete、setParameter | objectPath；创建需官方 typeIdentifier/version；参数需 parameter/valueJson |
| ImportPlcWatchTableOffline | 原生监视表 XML 导入 | filePath、现有 groupPath；仅离线、无覆盖、拒绝强制表/混合对象/DTD |
| ManageHardwareObject | deleteDevice/deleteItem/moveItem/copyItem | devicePathJson/itemPathJson 精确名称数组；移动/复制需目的路径及 position，通过 CanPlug 检查 |
| ManagePlcSoftwareUnit | list/read/create/createFromMasterCopy/delete/update/createRelation/deleteRelation（`unitKind` unit / safety） | name、relatedUnit、官方 relationType；update 只收 Author / NamespacePreset 与 commentsJson；安全单元不可创建 / 删除 |
| ReadPlcSoftwareUnits | 类型化读单元树（含安全单元、关系与各组内容名） | unitName、unitKind、includeContents、分页 |
| SetPlcUnitObjectAccess | 对单元内块/UDT 设置 Published/Unpublished | unitName、objectKind、objectPath、access；原生 API 不支持 OB 发布 |
| CreateLibraryMasterCopy | 从准确块/UDT/设备/画面创建主副本 | sourceKind/sourcePath、已存在 folderPath；device 路径使用 JSON 数组 |
| ManageLibraryTypeVersion | read/edit/release/setDefault/deleteVersion/updateInstances | typePath、version；release 需 newVersion/dependenciesMode；更新实例限定 targetSoftwarePath |
| ManagePlcSafety | read/createRuntimeGroup/deleteRuntimeGroup/updateRuntimeGroup/updateSettings/generateGlobalFIOStatusBlock/cleanSystemGeneratedObjects/generateBaseId/login/logoff/setPassword/revokePassword（2.7.25 强类型重写） | read 含块号段、安全系统版本、文档化属性、运行组 FOB 属性、集体签名（V21）、GlobalSettings、CPU F 能力；写入需 Offline，密码保护需已登录（可 `action=login`，密码直传不记录）；删除/生成/清理/密码类动作需 confirmSafetyChange |
| ManageSafetyGlobalSettings | read/update（2.7.25） | TIA Portal 级 SafetyModificationsPossible、GenerationOfDefaultFailsafeProgram、ManagementOfFailsafeInSoftwareUnitsEnvironment、UsernameForFChangeHistory |
| ReadSafetyBlockSignatures | 单块或整 PLC 逐块 F 签名（2.7.25） | blockPath 可空；值 0 = 无有效签名；离线工程值，不读 CPU |
| ExportSafetyPrintout | 官方安全打印件写文件（2.7.25） | printer PDF/XPS、option All/Compact、documentLayout；拒绝覆盖；返回 SHA-256；TIA 机器需启用对应 Windows 打印驱动 |
| ManagePlcCertificate | list/read/template/create/import/export/delete/assign/unassign | 精确 DeviceItem 与 certificateId；`template` 读某用途的默认模板；create 的模板字段类型化校验 + `subjectAlternativeNamesJson`；import 可带 `password`（2.7.32）；可用 assignmentItemPathJson 指定 OPC UA 分配属性所属子模块 |
| ManageUnifiedHmiGroup | screens/tags 的 create/rename/deleteEmpty | 精确相对 groupPath，不支持原生不存在的 ScriptGroups |
| ReadUnifiedEngineeringObjects | 七类集合分页标量读取 | category、精确可选 name、offset/limit |
| ManageUnifiedEngineeringObject | 原生 create/update/delete | category、name、公开可写标量 propertiesJson；不以反射绕过复杂引用类型 |
| ImportUnifiedEngineeringList | 文本/图形列表原生导入 | textLists/graphicLists、filePath、expectedNamesJson；核对导入返回值与名称存在性 |

HMI 七类集合：`alarmClasses`、`discreteAlarms`、`analogAlarms`、`alarmLogs`、`dataLogs`、`textLists`、`graphicLists`。前五类具有原生 Create(string)。后两类没有该方法，创建须走原生文件导入；不能把创建失败解释为集合为空。

## 共同约束

- 新增写入接口默认 dryRun=true。预览不做原生持久修改，不能保证执行时通过许可证、设备能力和语义检查。
- 所有真实修改取得 TIA 独占访问。PLC 用户组、工艺对象、监视表导入、软件单元及 Safety 编辑要求确认 Offline，不自动 GoOffline。
- 精确匹配名称，拒绝歧义；没有“只有一个 PLC 就随便选它”的写入回退。硬件路径使用 JSON 字符串数组，以保留站名中的 `/`。
- 组删除只允许空用户组。设备、软件单元、报警等对象删除可能影响内部对象及外部引用，预览明确说明不包含依赖影响分析。
- 不自动保存、编译、下载或关闭 TIA。原生库版本 Edit/Release/Update 可能按自身语义改变依赖；不是事务回滚。
- 多步骤失败保留可能已修改标志和可用的已完成列表；不要自动重复提交。
- HMI/IPC 句柄失效沿用会话阻断机制，不自动重新绑定继续读取。
- 原有强制操作和通用反射在线写入拦截保持不变；离线监视表文件导入不执行监视表中的修改值。

## 读取完整性与原生限制

- HMI 新读取接口只读取公开标量属性；每对象返回 values、failures、excludedComplexProperties、dataComplete 和 fullObjectComplete。dataComplete 只针对声明的标量范围，不能等同整对象完整。
- offset 分页是实时集合视图，期间应保持集合不变；不是冻结游标。单次最多 500 个对象，超过总枚举边界明确失败。
- 文本/图形列表导入返回 nativeSuccess 和预期名称核对结果，不宣称条目或变量绑定已逐项验证。整个文件可能影响预期名单外的列表。
- V20 supplied API 没有 HmiGraphicLists；返回明确不支持。V20 部分属性通过动态属性写入而不是 CLR setter，目前通用标量修改只支持公开 CLR setter；组重命名单独兼容官方 SetAttribute(Name)。
- Unified ScriptGroups 不存在；Audit Class 的只读字段不可假装支持写入。复杂多语言文本、对象引用、数组和列表条目仍需要专门结构适配。
- 库 updateInstances 调用类型级 UpdateProject，原生 API 选择适用版本；version 参数用于准确证据定位，不承诺强制降级到该版本。
- 库对象仅使用项目库或已在 TIA 打开的全局库，不隐式打开、关闭或保存全局库。
- 证书不处理密码保护的导入或私钥导出，导出拒绝覆盖文件。Safety 不处理登录密码，不设置 SafetyModeCanBeDisabled。
- 已有面板内部正文/绑定、原生图形组合、深层完整快照的未验收问题继续保留，不能因工具数增加就认定解决。

## 官方参考

- [V21 Openness API 目录](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api)
- [工艺对象](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects)
- [Unified 离散报警](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/discretealarms/description-discretealarms)
- [软件单元对象发布](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-software-units/publishing-software-unit-object)
- [SafetyAdministration](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/f-related-openness/safetyadministration/safetyadministration)

发布清单中的 engineeringApiShapePassed 只证明被检查的 API 签名、工具暴露及默认预览参数；engineeringLiveEdits 明确标为未测试。真实验证应在可恢复的测试工程中逐类执行，并保存预览、执行结果及读回证据。


## 2.7.15 工具族

v2.7.15 新增的 38 个入口，按范围与边界列出；同样未经真实工程验收。

### 已接入源码

| 范围 | 接口 | 精确边界 |
|---|---|---|
| Unified 定点对象 | ReadUnifiedObjectProperties、UpdateUnifiedObjectProperties | 公开标量与属性模式；复杂字段另行定位。只读分页为实时 offset，不是稳定快照 |
| 多语言 | UpdateUnifiedMultilingualProperty | 现有语言条目，目标回读与其他语言不变检查；不是任意语言集合 CRUD |
| 原生校验 | ValidateUnifiedObject | 参数为空的对象 Validate，独立返回 validationPassed；不调用 SyntaxCheck/Compile |
| HMI 交叉引用 | GetUnifiedCrossReferences | 原生 Sources/References/Locations；不展开运行时拼接名称，不保证复杂字段完整 |
| 原生列表导出 | ExportUnifiedEngineeringList | 单个文本/图形/系统文本列表，必须存在对应 Export 签名；新目录、非空文件、SHA-256；不把哈希校验视为条目核对 |
| 归档变量 | ManageUnifiedLoggingTag | 普通 HmiTag 的 LoggingTags 读/建/改/删，验证 DataLog 名称及标量回读 |
| 归档时长 | SetUnifiedLogDuration | 原生 LogDuration/SegmentDuration 五分量设置，返回原生字符串/数值；不宣称独立单位换算验证 |
| OPC UA 报警类型 | ManageUnifiedOpcUaAlarmType | 原生三参数创建或 NodeId/Connection 更新；连接必须存在，完整绑定有效性需真实验证 |
| 工厂视图/CPM | ReadUnifiedPlantObject、ManageUnifiedPlantNode、UpdateUnifiedPlantObject | 视图/节点精确定位，CPM 接口/成员公开标量，创建和叶节点删除；无递归删除或完整内部绑定保证 |
| 原生实例 DB | CreatePlcInstanceDb | 精确 FB 和目标组，原生 CreateInstanceDB；不是离线 XML 模拟 |
| 原生源/装载文件 | GeneratePlcSourceFromBlocks、GeneratePlcLoadableFile | 显式对象集合与选项，新文件、非空及哈希校验；不下载，不保证语义完整 |
| 工程恢复 | RetrieveProjectArchive | 已连接但没有打开工程/会话的 Portal，新目录，显式 upgrade；绝不自动关闭现有工程 |
| 工程文本 | ExportProjectTexts、ImportProjectTexts | 原生 XLSX 交换；导入返回 ProjectTextResult。updateSourceLanguage 表示更新源语言 |
| 工程语言 | ManageProjectLanguage | 激活/停用/设置编辑与参考语言并回读；禁止停用正在使用的编辑或参考语言 |
| 全局库 | ManageGlobalLibrary | 列举、创建、打开、恢复、保存、另存、显式关闭。升级打开要求 ReadWrite；不隐式保存或关闭工程 |
| 库文件夹 | ManageLibraryFolder | types/masterCopies 精确文件夹读/建/重命名/删除；只删空文件夹 |
| PLC 标签/常量 | ManagePlcTagDefinition | 单条原生读/建/改/删，精确标签表路径；写入要求 Offline，不写在线值 |

已有 ReadUnifiedEngineeringObjects 额外接入 systemTags、systemTextLists、auditTrails、opcUaAlarmTypes。只读系统集合不能经 ManageUnifiedEngineeringObject 写入；审计对象没有对应创建签名时明确返回不支持。

V20 的 PlantViews 是工程属性，V21 是 PlantViewsProvider 服务，分别编译适配；不以一个版本的签名推断另一版本。官方项目访问说明：[Plant views](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/plantobjecttags/plantviews/description-plant-view)。实际名称和签名以本地相应版本官方程序集为准。

### 专用选件与原生交换增补

| 接口 | 实现范围与限制 |
|---|---|
| ExchangePlcSupervisions | ProDiag XLSX 导出/导入/设置导入，返回原生状态及日志路径；不是全部 ProDiag FB 操作 |
| ExchangeCfcCharts | ChartProviderS7 完整 ZIP 导出/导入，显式 modelVersion/filter/deleteAtTarget；不含选择性导出 |
| ReadTestSuiteCases、ExchangeTestSuiteCase、RunTestSuiteCase | styleGuide/application/system 精确案例读取/文件交换/单案例执行；应用和系统测试真执行另需 confirmExternalExecution。未配置测试服务器，也未执行任何测试工程 |
| ExchangeMotionCamData、ConfigureMotionHardwareConnection | Cam 文本/二进制/点列表交换；离线轴驱动/传感器/转矩映射，地址单位为 bit；不向驱动发送运动命令 |
| ManageUnifiedEvent | 普通事件/属性事件精确读取、创建、脚本字段更新和删除；执行修改需预览 token；不执行脚本或 SyntaxCheck，不涵盖所有自定义 Web 事件 |
| ManageStartdriveParameter | 离线 DriveObject 精确编号和参数名读取/修改；不获取 OnlineDriveObject，不写在线参数 |
| ReadSiVArcRules、ManageSiVArcRule、GenerateSiVArc | 规则公开标量、支持 Create(string) 的集合操作（按路径的通用反射入口，保留）；2.7.38 起类型化的规则层次 / 引用 / 设备列 / 块定义 / 表达式 / 布局 / 升级见上文"2.7.38 新增工具族"，`GenerateSiVArc` 已类型化并支持多设备 |
| ManageLibraryMasterCopy、ImportLibraryTypeDocuments | 主副本精确复制/比较/删除；原生类型文档导入。已有 ManageLibraryTypeVersion 增加 discard/findInstances |
| ManageDccChart、ReadDccObject | 离线 DCC 图表读/建/改/删/文件交换/优化顺序；块、引脚等定点标量读取。不含全部块/引脚连接编辑 |

依赖安装版本、许可及选中对象是否提供服务。缺少组件或签名时返回明确不支持，不降级为成功空列表。V21 选件签名已通过本地官方程序集检查；V20 选件行为未获真实工程验证。

### 调用约定

全部新增写入/文件操作默认 `dryRun=true`。预览只确认对象、公开签名及输入类型，不表示 TIA 已接受所有业务参数。执行异常可能留下部分改动或文件，通过 `mayHaveChanged` / `mayHaveWrittenFiles` 说明；不自动回滚或保存。保存/另存/关闭全局库是该工具的显式动作，不会附带到其他操作。

对象路径示例：

```json
[{"property":"TagTables","name":"Table_1"},{"property":"Tags","name":"Tag_1"}]
```

每一步使用官方公开属性和精确名称。最多 24 步；拒绝 Parent 等反向属性、索引和方法表达式。名称中的 `/`、`+`、`-` 保留在 JSON name 中。普通文件夹路径参数则使用相对层级路径。

`dataComplete` 必须结合 `scope` 理解。标量字段齐全不代表对象内部结构完整；复杂字段在 `excludedComplexProperties` 中列出。分页超过 10,000 个候选对象明确失败，不静默截断；不承诺断点游标或读取期间工程变化下的一致性。CPM、交叉引用和原生文件接口保留完整性限制。

## 尚未完成，不能宣称已加入

2.7.18 之后仍未实现或官方无 API 的项（全量对照见 [官方 API 覆盖清单](openness-coverage.md)）：

- **官方无 API，保持明确拒绝**：独立 RUN/STOP（只能经运行时通道或下载附带）、清除强制、诊断缓冲区、按块选择性下载、Unified 画面复制、Unified 列表条目类型、经典 HMI 脚本/周期/列表的 `Create(string)`、ProDiag 类型化监督组合、阈值/数据网格/报警行列的 `Create`、工程级"已保护"标量。
- **选件与协作**：Startdrive / DCC 的类型化封装（阶段 6 ⑥-②，2.7.39）、SafetyValidation / Teamcenter（无任何入口）与 TestSuite / CFC 的剩余动作（⑥-③，2.7.40）；UMC 服务器在线同步与启用/停用工程保护（工程无 UMC 服务器；工程级保护官方无 API）；V20 `Connect(Telegram, …)` 重载。SiVArc（2.7.38）、UMC 离线用户/组（2.7.32）、`CompareLibraryObjects` 的详细比较（2.7.31）、`Connect(Channel)`（2.7.36）已做。
- **硬件杂项**：App ID、批量硬件参数、Software Controller PSC/资源配置、自定义 Logo、CiR、I-Device PN-GSD 导出、共享设备、GSDX 签名状态、向 PLC 下载附加用户文件；`SelectiveDeleteDownload`、`Upgrade/DowngradeTargetDevice`、`TurnOffSequence`、`OverwriteHmiData`、Startdrive 下载提示无内置默认，需经 `promptAnswersJson` 显式指定。
- **库**：实例清理/更新全部流程、HMI-Library 之外的模板分析。
- **未纳入组件表的 V21 程序集**：`Siemens.Engineering.ScadaExporter.dll`、`SafeKinematics.dll`、`Sinumerik.dll`。
- 没有官方入口证据的功能仍为待核实，不能以反射占位工具宣称支持。`ProgrammingLanguage.ST`（V21，SIMATIC AX 导入块）已核实：引擎对该枚举值只做 `ToString`/`Enum.GetName`，不会失败。

这些能力需要各自的官方参数/生命周期实现和回归验证；选件与真实设备相关功能还需要匹配环境验收。这些构建记录不包含真实虚拟机工程或在线 PLC 的验收。

## 本地验证

离线测试、官方程序集 API 形状检查和实际 EXE HTTP 回归的项数与日期以 [manifest/release-build.json](../../manifest/release-build.json) 为准，本页不重复维护快照（口径见[验证说明](../development/validation.md)）。三类检查都是元数据或本地回环级别，不连接博图，不代表 TIA 工程实际操作成功。

各版本工具族的真机验收结果在本页对应段落与 `docs/releases/vX.Y.Z.md` 的"真机结果"表里（V21 参考工程 `AutomaticDipCoatingMachine`：PLC `+S1-K1`、Unified HMI `HMI_RT_1`）；没有对象或许可的项（经典 HMI 实做、选件包）只有形状检查，不计作验收通过。真实安装环境和测试工程需另行验收。
