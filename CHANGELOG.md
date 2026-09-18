# Change Log

## [2.7.29] - 2026-09-18

引擎 2.7.29.0（V20/V21 均重建），工具 367、默认 lite 56 项不变。详见 [v2.7.29](docs/releases/v2.7.29.md)。WinCC Unified 阶段（路线图 §2.0 阶段 2）收口。

- **修复**：`ExchangeUnifiedTags export` 在真实工程上恒失败——`HmiTagComposition.Export(dir, name)` 回报的 `FileInfo` 是 `<dir>\<name>`（无扩展名），而 TIA 实际写出 `<name>.hmi.yml`。原生回报路径不存在时按已知扩展名（`.hmi.yml` / `.hmi.js` / `.yml` / `.js` / `.xlsx` / `.xml`）再按同名前缀解析到实际文件，记录里保留 `reportedPath`；两条导出工具都附 `directoryListing`（TIA 留下的全部文件，含 `NameData.yml` 等副文件）。
- **登记**：剩余 Unified 类型全部进入动态覆盖登记表（系统变量、阈值、替代值、审计类、连接/驱动属性、OPC UA 报警类型、记录变量、文本/图形列表、`Cpm`、`Common` / `UIBase` / `IValidator` / `ImportResult`、画面组），`verified` 字段如实区分"真机验证"与"仅形状检查"。审计：完全未触及 599 → 556，动态覆盖 285 → 322，未封装功能类型 288 / 1,185 → 267 / 1,067（Unified 21 → 0）。
- **验证**：离线 1446 项（新增 5 项：无扩展名回报路径解析、同名前缀解析、不可解析拒绝、目录清单）；形状检查 V20 990 / V21 1087 不变；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `HMI_RT_1`，2026-09-18，引擎 2.7.29.0）：变量导出核对通过（`ShiftModel.hmi.yml` 4,400 B，`reportedPath` 无扩展名，该表无副文件）；文本列表 16 / 系统文本列表 18 读出并各原生导出一个（`<名>.hmi.yml` + `<名>.TextLibrary.hmi.yml`）；`HmiTag.Validate()` 3 行诊断；画面组建删；`opcUaAlarmTypes` / `LoggingTags` 组合到达但工程为空，`Cpm` / `HmiConnections` 仍无对象可验。登记表 `verified` 已回写，详见发布说明。

## [2.7.28] - 2026-09-18

引擎 2.7.28.0（V20/V21 均重建），工具 364 → 367，默认 lite 56 项不变。详见 [v2.7.28](docs/releases/v2.7.28.md)。WinCC Unified 阶段子批次 ⑤。

- **新工具 3 个**：`ExchangeUnifiedTags`（变量 WinCC ML `.hmi.yml` 导入导出，`HmiTagComposition.Export/Import`，根或任意组内变量表；此前 Unified 变量表导出走 SimaticML 路径报 unsupported）、`ExchangeUnifiedScriptModules`（全局脚本模块整体/单个导出与导入，`IChromDataExchangeExport`）、`ImportUnifiedOpcUaAlarms`（连接上的 `OpcUaAlarm` 服务：显示名、`GetNodeId`、xml 导入）。
- **登记**：阈值（`ManageUnifiedObjectParts`）、替代值/范围（嵌套写入）、系统变量、驱动属性、审计类、文本/图形列表、记录变量、Cpm、`UIBase`/`HmiBase` 经既有工具到达。
- **修复**：`Object` 类型属性（变量范围/替代值/阈值 `Value`）写入后读回按跨类型文本/数值比较，不再把已写入的值误报为 "readback differs"。真机确认：`HmiThresholdComposition.Create()` 被 TIA 原生拒绝（"New thresholds are not supported"），替代值只对外部变量可设。
- **验证**：离线 1441 项（新增 33 项）；形状检查 V20 990 / V21 1087（新增 54 / 55 项）；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `HMI_RT_1`，2026-09-18）：脚本模块导出/导入预览、变量导入预览、范围嵌套写入读回、系统变量/审计类/工厂视图读取通过；**变量导出核对失败**（TIA 回报的 `FileInfo` 无 `.hmi.yml` 扩展名，文件本身已写出），2.7.29 修复；`OpcUaAlarm` 因工程无 OPC UA 连接未能验证。详见发布说明。

## [2.7.27] - 2026-09-18

引擎 2.7.27.0（V20/V21 均重建），工具 364、默认 lite 56 项不变。详见 [v2.7.27](docs/releases/v2.7.27.md)。

- **2.7.26 真机重跑**（V21 `HMI_RT_1`）：目录 43 类型、schema、update（标量/颜色/部件/顶层多语言）、read、list、delete、隐藏 `EventHandlers` 修复均通过；发现三处缺陷。
- **修复**：① `ManageUnifiedScreenItem create` 与 `ManageUnifiedScreenLayout create`（2.7.18 起）在真实 API 上恒报"not found exactly once"——`Create<T>` 与 `Find()` 返回不同代理，`ReferenceEquals` 恒假；改按名称查回 + 计数核对。② 部件内多语言文本（`Title.Text` 等）此前不被识别，现任意深度拆分并逐条读回。③ `UpdateUnifiedObjectProperties` 不支持颜色与嵌套部件（报警类状态颜色写入失败），现与 `ReadUnifiedObjectProperties` 一起改用 UI 模型嵌套读写。
- **验证**：离线 1408 项（新增 6 项）；形状检查 V20 936 / V21 1032；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `HMI_RT_1`，2026-09-18）全部通过：画面创建、`HmiGauge` 带 `Title.Text` 双语初始属性创建、`HmiAlarmControl` 创建、报警类状态颜色写入并复原、记录 `Settings.LogMaxSize` 写入并复原；`HmiAlarm` / `HmiLogging` / `RuntimeSettings` / `UI.Controls` 登记为动态覆盖，详见发布说明。

## [2.7.26] - 2026-09-18

引擎 2.7.26.0（V20/V21 均重建），工具 362 → 364，默认 lite 56 项不变。详见 [v2.7.26](docs/releases/v2.7.26.md)。官方 API 全量对齐第二阶段（WinCC Unified）子批次 ①：画面对象族。

- **新工具 2 个**：`DescribeUnifiedScreenItemType`（从加载的官方 API 反射出全部 43 个具体画面对象类型的目录与属性 schema：scalar/color/multilingual/part/collection 分类、枚举值、可写性、部件子属性、事件类型；不需要工程）、`ManageUnifiedScreenItem`（任意类型 `Create<T>(name[, containedType])` 创建、list/read/update/delete，嵌套部件与逐语言文本写入并读回）。覆盖 `UI.Shapes` 17 / `UI.Widgets` 16 / `UI.Controls` 13 / `UI.Base` 9 / `UI.Features` 13 / `HmiScreenWindow`。
- **修复**：`HmiSlider` / `HmiToggleSwitch` / `HmiCircleSegment` / `HmiEllipseSegment` 隐藏基类 `EventHandlers` 导致 `Type.GetProperty` 歧义，`ReadUnifiedObjectEvents` / `ManageUnifiedObjectParts` / `ManageUnifiedDynamization` 在这四种对象上此前会失败；改为取最派生声明。
- **覆盖口径**：新增动态覆盖登记表 `scripts/diagnostics/openness-dynamic-coverage.json`，审计脚本单列"动态覆盖"栏；完全未触及类型 885 → 626，Unified 未封装功能类型 113 → 41。
- **验证**：离线 1402 项（新增 49 项）；引擎 API 形状检查 V20 936 / V21 1032（新增 50 / 50 项）；两版 EXE 回归见 `manifest/release-build.json`。

## [2.7.25] - 2026-09-18

引擎 2.7.25.0（V20/V21 均重建），工具 359 → 362，默认 lite 56 项不变。详见 [v2.7.25](docs/releases/v2.7.25.md)。"官方 Openness API 全量对齐"计划的第一步（[路线图 §2.0](docs/development/roadmap.md#20-官方-api-全量对齐计划)）。

- **`Siemens.Engineering.Safety` 全量封装**（12 类型 / 54 成员，按官方 "F-related Openness" 章节逐页对照）：`ManagePlcSafety` 由反射改为强类型，`read` 补齐 `AssignmentOfBlockNumbers`、`SafetySystemVersion` + 可用版本、`EnableConsistentUploadFromFCpu` / `EnableFCommunicationIdTag`、运行组 `FOBNumber` / `FOBCycleTime` / `FOBPhaseShift` / `FOBPriority`、**集体 / 软件 / 硬件 / 通信地址 F 签名**（旧实现从未真正读出）、工程级 `GlobalSettings` 与 CPU `Failsafe_FCapabilityActivated`；新增动作 `generateGlobalFIOStatusBlock` / `cleanSystemGeneratedObjects` / `generateBaseId` / `login` / `logoff` / `setPassword` / `revokePassword`，`createRuntimeGroup` 三种官方重载，`updateSettings` 支持嵌套块号段与精确版本。
- **新工具 3 个**：`ManageSafetyGlobalSettings`（TIA Portal 级四项全局安全设置）、`ReadSafetyBlockSignatures`（逐块 F 签名，单块或整 PLC）、`ExportSafetyPrintout`（官方安全打印件 PDF/XPS）。下载提示 `SafetyProgram` 应答 `ConsistentDownload`。
- **验证**：离线 1353 项（新增 59 项）；引擎 API 形状检查 V20 886 / V21 982（新增 73 / 80 项 Safety 成员核对）；两版 EXE 回归见 `manifest/release-build.json`。真机（V21 `+S1-K1`，1510SP F V4.1，2026-09-18）全部通过：集体/软件/硬件签名读出（2.7.24 为空）、11 个 F 块逐块签名、官方安全打印件 PDF 205 KB、GlobalSettings 写入并复位，详见发布说明。

## [2.7.24] - 2026-09-18

引擎 2.7.24.0（V20/V21 均重建），工具 359、默认 lite 56 项不变。详见 [v2.7.24](docs/releases/v2.7.24.md)。

- **真机重跑 2.7.20 的 `DescribeBlockLogic` 修复**（V21 `AutomaticDipCoatingMachine`）：LAD `UNIT_MANAGER_FC` NW5 调用框完整读出（7 个 Bool 输入触点链 + 9 个输出绑定）；SCL `CLOCK_GENERATOR_FB` 的调用与命名常量全部恢复，仅剩位切片 `.%X15` 与 `ENO` 左值两处丢失。
- **按 `SW.PlcBlocks.Access_v5.xsd` 重写 SCL 的 Access / Symbol 渲染**：SCL 导出的 `Symbol` / `Instance` 内部是 Token 序列（`.`、`%X15`、`[`、`]`），现按序渲染，FlgNet 无 Token 时才合成分隔符与 `SliceAccessModifier`；`ENO` 为 `<PredefinedVariable>` 新增渲染；补齐 `Label` / `Statusword` / `DataType` / `Expression` / `Reference` 分支；`*Attribute` / `TemplateValue` 元数据不再泄漏进正文。
- **离线文档工具同族修复**：`RenderPlcBlockDocument` / `ComparePlcBlockDocuments` / `GeneratePlcDocumentation` 的渲染器同样漏了命名常量（输出 `:= ;`）、`REGION` 名（`<Text>`）和 `ENO`，一并修复。
- **验证**：离线 1294 项（新增 7 项）；两版 EXE 回归见 `manifest/release-build.json`。引擎 2.7.24.0 真机重跑通过：`CLOCK_GENERATOR_FB` 整块与源码逐字一致，`UNIT_MANAGER_FC` 无回归。

## [2.7.23] - 2026-09-18

交付 2.7.23，引擎沿用已验证的 2.7.20.0，应用界面与 2.7.22 相同。详见 [v2.7.23](docs/releases/v2.7.23.md)。

- **配置器截图裁切修复**：`CapturePage` 按面板尺寸建位图却在其外边距偏移处绘制，每张界面截图都少了右侧和底部各 18px，右上角状态胶囊被切掉一截。位图尺寸现在含两侧外边距。只影响界面检查用的 PNG，应用布局未变。
- **验证**：配置器 88 项（新增 2 项）；引擎沿用 2.7.20 的离线 1287 项。

## [2.7.22] - 2026-09-18

交付 2.7.22，引擎沿用已验证的 2.7.20.0。详见 [v2.7.22](docs/releases/v2.7.22.md)。

- **配置器 AI 客户端栏改为卡内滚动**：11 张卡片此前把 B 栏撑高、A 栏被迫留出大片空白。现在卡片区限高三行并在卡内滚动（细滚动条与整体配色一致），A/B 两栏改为底部对齐，按钮行固定在卡片底部，两栏等高。两种模式同样适用。
- **验证**：配置器 86 项（新增 2 项）；引擎沿用 2.7.20 的离线 1287 项。

## [2.7.21] - 2026-09-18

交付 2.7.21，引擎沿用已验证的 2.7.20.0（运行文件与工具清单未变）。详见 [v2.7.21](docs/releases/v2.7.21.md)。

- **配置器改为单页双栏连接控制台**：深色编号侧栏 + 三页（虚拟机服务端 / AI 客户端 / 本机连接）改为一页两栏（A 服务端 / B AI 客户端），右上角分段开关切换 **虚拟机 ↔ 宿主机**（HTTP）与 **同一台电脑**（stdio）；stdio 模式自动隐藏地址、密钥和服务按钮。新增两栏之间的连线条（客户端 → `IP:端口` 或 `stdio · 无需网络` → 服务侧，状态 idle / running / local）和带条目计数的 `ACTIVITY LOG` 卡片；配色由蓝色改为米白底 + 墨绿强调。
- **地址与密钥合并**：原两页各一份的地址/端口/密钥合并为一组字段加一条共用密钥条；新增“保存两端配置”，客户端侧连接信息总能保存，服务端配置仅在本机装有 TIA 与引擎时写入，宿主机上校验失败只记日志不再弹错。
- 客户端卡片、写入位置、条目格式、服务器名（`tia-portal-vm` / `tia-portal`）与网络权限、启动/停止、测试连接、备份和密钥加密逻辑均未改变。
- **验证**：配置器 84 项（新增 4 项）；引擎沿用 2.7.20 的离线 1287 项。真实客户端与真实虚拟机 UAC 未执行。

## [2.7.20] - 2026-09-17

引擎 2.7.20.0（V20/V21 均重建），工具 359、默认 lite 56 项不变。详见 [v2.7.20](docs/releases/v2.7.20.md)。

- **`DescribeBlockLogic` 渲染丢失修复**（真实工程连机测试发现）：SCL 网络里的调用（`TIME_TCK()`、`TIME_TO_DINT(#a - #b)`、`LIMIT(MN := …)`、多重实例 `#inst⟨FB⟩(…)`）、`LocalConstant`/`GlobalConstant` 命名常量、`%I1.3` 绝对地址、数组下标此前整段丢失或被压成 `#a.b`，工具却照样报 `success`；LAD 网络里 `<Call>` 块调用不被识别，整段退化成孤立触点清单。现 SCL 递归渲染 `CallInfo`/`Parameter`/`Instance`/`Constant Name`/`Address`，LAD 收集 `<Call>` 为调用框并按 `Parameter Section` 判断引脚方向，输出 `CALL "FB"[inst](pin=操作数, pin=⟨触点链⟩)`；块 Bool 输出驱动线圈时内联为 `CALL "FB"[inst].pin`；新增 `Not` 元件；MOVE/定时器框的上游按 `in → IN → en` 回溯（原只查 `in`，EN 触点链丢失）。
- **配置器：客户端卡片重排，加入国产模型与 Grok**：AI 客户端页按 CLI 在前、Claude Code 与 Codex 居首、IDE 在后排列；去掉 Windsurf、Cline、Claude Desktop · Chat（含 `mcp-remote` 桥接与 `tia-portal-vm-chat`）。新增 **通义千问**（写阿里 Qwen Code `%USERPROFILE%\.qwen\settings.json`，`httpUrl`）、**Kimi**（写 Kimi Code CLI `%KIMI_CODE_HOME%\mcp.json`，默认 `~\.kimi-code\mcp.json`）、**腾讯元宝**（写 CodeBuddy Code CLI `~\.codebuddy\.mcp.json`，`type: http`）、**DeepSeek / 智谱清言 / Grok**（写 OpenCode `~\.config\opencode\opencode.json` 的 `mcp` 节，`type: remote`；本机 `type: local` + 单数组 `command`；三张卡片共用一个文件只写一次）。这些模型的聊天 App 不支持 MCP，卡片按模型命名、在所写客户端里选模型即可。豆包（Trae）因全局配置位置未公开且远程仅 SSE 未加入。卡片 8 → 11。未在真实客户端上联调。
- **验证**：离线 1287 项（新增 13 项，含反向哨兵）；配置器 80 项（新增 22 项）。

## [2.7.19] - 2026-09-17

引擎 2.7.19.0（V20/V21 均重建），工具 350 → 359，默认 lite 56 项不变。详见 [v2.7.19](docs/releases/v2.7.19.md)。

- **PLCSIM Advanced 通道**（路线图 #1 / S2）：`ReadPlcSimAdvancedInstances`、`ManagePlcSimAdvancedInstance`（register/powerOn/run/stop/powerOff/memoryReset/unregister）、`ReadPlcSimAdvancedTags`、`WritePlcSimAdvancedTags`、`RunPlcSimAdvancedTestScenario`（写输入 → 单步推进周期 → 断言输出的闭环场景）。官方 `Siemens.Simatic.Simulation.Runtime` API 在运行时定位（`apiPath` → `PLCSIMADV_API_PATH` → 安装目录最新版本）并经反射调用，与 Openness 同样不随包分发；未安装时明确返回 `ApiNotFound`。改变实例/写值/跑场景默认预览且需确认参数。新增分类域 `Simulation`（runtime 大类）。**本机无 PLCSIM Advanced，未做真实运行验证**。
- **离线文档与预检**（S3 / #8 / #9）：`RenderPlcBlockDocument`（SimaticML/SCL → Markdown：接口表、SCL 网络由 StructuredText 令牌还原、LAD/FBD 网络给出部件清单 + Mermaid 流程图）、`GeneratePlcDocumentation`（导出目录 → 单文件手册：索引表、调用交叉引用、逐块渲染）、`LintPlcSclSource`（13 条启发式规则：块关键字配对、括号、`=`/`:=`、缺分号、GOTO、WHILE TRUE、嵌套深度、行长、空白、`;;`、未闭合注释、TODO 标记）。均不需要 TIA 会话；lint 结论不是编译判决。
- **AML 生成**（#6 / S5 的生成半边）：`BuildDeviceAmlDocument` 由 JSON 规格生成 CAEX 2.15 硬件描述供 `ImportDeviceAml` 使用；推荐传入 `ExportDeviceAml` 导出的参考文件以逐字复用版本匹配的头部与角色类库，未传参考时使用内置骨架并标记 `importVerified=false`。
- **写保护钩子与审计日志**（S7）：插件新增 `hooks/tia-write-guard.ps1`（PreToolUse）。按 `tools-list.json` 的操作类型审计所有非只读调用到 `%LOCALAPPDATA%\TiaMcpServer\audit\tool-calls.jsonl`（密码类参数脱敏），并拒绝真实 ONLINE-WRITE 调用（下载、上载、运行时写值、PLCSIM 变更；预览调用放行），除非设置 `TIA_MCP_ALLOW_ONLINE_WRITE=1`；`TIA_MCP_WRITE_GUARD=0` 关闭。自检脚本 `scripts/checks/Test-WriteGuard.ps1`（16 项）接入 `Build-Release`。
- **分类修正**：`WritePlcWebVars`、`WriteUnifiedRuntimeTags`、`SetPlcWebOperatingMode`、`UnifiedOpenPipeRequest`、`UploadStationFromPlc`、`UploadDeviceParameters` 的操作标签由 WRITE 改为 ONLINE-WRITE（它们改变真实运行时/设备，2.7.18 标错）；ONLINE-WRITE 工具现为 10 个。
- **E8**：`TiaMcpServer.csproj` → `TiaMcpServer.V21.csproj`，`HttpTests.csproj` → `TiaMcpServer.HttpTests.csproj`（程序集名不变），脚本与文档同步。
- **发布流程**：`Publish complete release` 工作流除手动触发外，推送 `vX.Y.Z` 标签也触发，标签必须等于 `manifest/delivery.json` 的版本。
- **验证**：离线 1273 项；API 形状检查 V21 902 / V20 813；实际 EXE 回归两版全过；配置器 58 项。**真实工程与真实 PLCSIM Advanced 验收未执行**。

## [2.7.18] - 2026-09-17

引擎 2.7.18.0（V20/V21 均重建），工具 298 → 350，默认 lite 56 项。详见 [v2.7.18](docs/releases/v2.7.18.md)。

- **下载缺陷修复**：`DownloadToPlc` 此前把 `UserManagementDownload`、`AlarmTextLibrariesDownload`、`DownloadCertificate` 当复选框处理，而它们在 V20/V21 都是 `CurrentSelection` 选择型，等于从未应答；另有 27 种下载提示类型完全未处理。现在委托按提示的真实形态（枚举选择 / Checked / SetPassword）应答，调用方 `promptAnswersJson` 优先，内置默认其次（破坏性提示默认 NoAction/NoChange），密码类由专用参数提供，其余提示连同 TIA 提示文本记入 `Meta.promptsUnanswered`。新增 `userManagementMode`、`promptAnswersJson`、`moduleAccessPassword`、`blockBindingPassword`、`masterSecretPassword` 参数。
- **新增 52 个官方 Openness 工具**（全部默认预览、精确名称、写入需确认参数、V20 缺失成员时明确 NotSupported）：设备传输（可达设备扫描、站上载、参数上载、下载到文件夹/存储卡镜像）；PLC 块服务（块保护、DB 快照/实际值、程序升级、指纹、报警实例文本导入、报警文本列表）；工程安全与协作（UMAC 用户/角色/权限读写、工程保护读取、多用户会话、库比较、离线工程比较、Portal 设置）；硬件服务（通信连接 CRUD、监视/强制表 Web 访问、系统诊断设置交换、OPC UA 访问控制、AML 导入、硬件特性探测）；Unified UI 对象模型（全部事件/属性事件枚举、部件 CRUD、完整动态化类型、画面布局/尺寸/删除、列表定位、报警外观与审计设置读取）；Motion/ProDiag/经典 HMI（轴配置与原生操作、ProDiag 对象、VB 脚本/周期/文本图形列表/全球化/面板）。
- **新增运行时通道**（不经 Openness）：S7 Web 服务器 API（官方 `Siemens.Simatic.S7.Webserver.API` 3.3.76，MIT）读写变量、诊断、受确认的运行模式切换；WinCC Unified Open Pipe 命名管道读写变量、读报警、原始请求，消息格式按官方手册核实。
- **新增离线分析**：`ComparePlcBlockDocuments`（剥离时间戳/UId 的语义 diff + 结构差异）、`ScanPlcSourceAnnotations`（TODO/FIXME 扫描）、`ExtractPlcBlockMetrics`；`templates/plc/scl-examples/FB_SelfTest_Template.scl` 自测试骨架。
- **工具分类**：引擎内 `ToolTaxonomy` 作为唯一事实来源——7 个大类（session / project / plc / plc-online / hardware / hmi / runtime）、26 个域、规范操作类型（SESSION/READ/WRITE/FILE/OFFLINE/ONLINE/ONLINE-WRITE/EXECUTE）；新增 `ListToolCategories`，`FindTools` 支持 `category`/`domain` 筛选；`tools-list.json` 带分类字段，`tool-matrix.md` 改由清单按大类→域生成并纳入发布流程。原裸 `PLC` 域并入 `PLC-Software` / `PLC-TechnologyObjects`，8 种历史操作写法统一。
- **引擎待办清理**：14 个大写域名标签统一；移除输入已不存在的 `RunV2PlanCompletionAudit` 工具与 CLI 标志；Doctor 提示、授权指南死工具名修正；硬编码开发机路径改为运行时安装根解析；V20 csproj 的 `TiaPortalLocation` 可被覆盖；移除无对象的 csproj 项。
- **验证**：离线 1158 项通过；官方程序集 API 形状检查 V21 847 / V20 758；实际 EXE HTTP 28、HMI 遍历 18、资源发现 42 等两版全过；配置器 58 项。**真实工程验收未执行**；新工具按 API 形状与离线逻辑验证，状态见能力文档。
- 许可证：新增 Webserver API、Newtonsoft.Json、MimeMapping 条目与原文。
- 配置器：安装目录改为自动探测（TiaPortalLocation 环境变量 → 注册表 TIAP{版本}\TIA_Opns → 默认安装目录，与引擎同一顺序），首次打开与切换版本时自动填入，新增“自动检测”按钮；只接受含对应 Openness DLL 的目录。配置器隔离测试 58 项。

### 随 2.7.18 发布的仓库整理与审计

以下改动与上面的引擎变更同属 2.7.18。

- 插件配置：`tia-portal` MCP 服务器定义内联进 `.claude-plugin/plugin.json`，删除根目录 `.mcp.json`。`${CLAUDE_PLUGIN_ROOT}` 只在插件上下文展开，根目录文件被 Claude Code 当项目级配置加载时路径不展开、服务器启动失败；直接打开仓库或解压包不再触发该错误。
- 清理：删除已并入分类却仍随包分发的 `手册/`、5 个标 `_deprecated` 的 `templates/plc/plcbuild-json/fb_*|fc_*.json`（对应 `.scl` 保留）、被 `Generate-ToolsListFromAssembly.ps1` 取代的 `Generate-ToolsList.py`；`design-qa.md` 移入 `docs/archive`；`package-manifest.json` 去掉与 `plcTemplateLibrary` 重复的 `plcNetworkPatterns` 入口。引擎源码与工程文件未改动（受 `Validate-Bundle` 源码哈希约束，相关待办见路线图）。
- 过期引用：修正 bug 模板的 `bin/Release` 路径与版本示例、蓝图 README 的 `scaffold_spec_start_stop.json` 文件名、HttpTests README 版本参数、CHANGELOG 历史版本锚点；删除 SKILL.md 中不存在的 `ImportBlocksFromScl` 别名说明；`natural-language-recipes.md` 与 `openness-limitations.md` 不再把刻意未注册的 `SetForceTableEntry` 当作可用工具。
- 官方 API 对照：新增 `scripts/diagnostics/Audit-OpennessCoverage.ps1`，用本机 V21 PublicAPI 的 18 个官方 XML 对引擎源码做逐成员词法盘点，结果与确认缺口写入 `docs/reference/openness-coverage.md`。`openness-limitations.md` 更正"在线设备发现仅限工程配置"的错误（`GetAccessibleDevices()` 存在），补充 RUN/STOP 只能作为下载配置附带发生、下载提示覆盖情况与下载到 Windows 文件夹；`capabilities.md` 追加此前两侧均未记录的缺口（设备上载、可达设备扫描、`WatchAndForceTableAccessManager`、`UpdateProgram`、`ProgrammingLanguage.ST` 兼容义务等），并把与 `release-build.json` 重复的验证计数改为指向。
- 审计中确认的下载提示缺陷（`UserManagementDownload` 被当复选框、43 种提示只应答 16 种）已在本版修复，见上。
- 许可证：新增 `docs/licenses/THIRD-PARTY-NOTICES.md` 逐项列出 `runtime/` 全部第三方程序集的许可证，补齐 Sharp7（MIT）、Workstation.UaClient、YamlDotNet、.NET Foundation、Siemens.Collaboration.Net 原文。Siemens 程序集适用免版税软件条款而非 MIT，再分发边界需维护者评估。
- 新增 `docs/development/roadmap.md`：11 项需在 TIA 机器重建的引擎待办、官方 API 缺口优先级、第三方集成候选（含许可证红线）、合规事项。
- 其他：`docs/README.md` 加入语言约定；`tools/README.md` 补 `vci-watch`；`templates/hmi/README.md` 补漏的 `unified_basic_event_log` 行；英文 README 为中文界面按钮加注释；`.gitignore` 增加 TIA 工程/归档扩展名与 `.claude/`。
- 根目录瘦身：`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md`、`SECURITY.md` 移入 `.github/`（GitHub 同样识别）；`examples/` 目录并入 `docs/getting-started/cursor.example.json` 与配置指南；本机 PublicAPI 副本移出仓库目录（`.gitignore` 保留防护）。根目录只剩说明、许可证、Git/插件配置与配置器 EXE。
- 目录整理：`docs/tools/` 四篇并入 `docs/guides/`；`capabilities.md` 按 2.7.14 / 2.7.15 / 2.7.18 工具族分节；删除被 `Audit-OpennessCoverage.ps1` 取代的 `Audit-OpennessSurface.py`，`Test-DownloadRouteSelection.ps1`（新增 `-PublicApiDirectory`）与 `Test-MatchPlcName.ps1` 接入 `Build-Release`；引擎源码按职责分入 `ModelContextProtocol/Tools|Builders/`、`Siemens/Portal|Hmi/`，命名空间不变，全部 `.cs` 统一为无 BOM UTF-8 并在 `.gitattributes` 固定行尾；CI 死引用检查登记新工具描述里点名的原生 API 成员。

## [2.7.17] - 2026-09-17

- 重整文档、示例及脚本目录，合并重复指南和历史发布说明，删除过期脚本、临时清单及现场原始数据。
- 中英文 README 统一为入口，更新所有迁移引用、skill、插件及蓝图，新增文档链接与入口校验。
- 交付包仅保留 runtime/v20 与 runtime/v21，移除旧 bin 路径下的重复运行文件；旧配置可用 GUI 重新保存。
- 保持引擎 2.7.15.0 与其原验证记录不变，独立验证配置器及完整包。详情见 [v2.7.17](docs/releases/v2.7.17.md)。

## [2.7.16] - 2026-09-17

- 新增独立 WPF 图形配置程序 `TiaMcpConfigurator.exe`：按参考设计实现深色编号侧栏、浅色内容区、分类客户端卡片和右侧主操作；配置虚拟机 HTTP 服务和本机/远程 AI 客户端，无需手写 CMD/BAT。
- 三页导航、已选客户端计数、密钥显示与占位提示、服务状态联动；窄窗口自动调整客户端卡片列数，内容可滚动。本机连接显示实际 stdio 传输。
- 支持多选 Claude Code、Claude Desktop、Codex、Cursor、VS Code、Gemini CLI、Windsurf、Cline；按各客户端 JSON/JSONC/TOML 格式合并并备份。Claude Desktop Chat 远程模式使用需 Node.js 的 mcp-remote 桥接。
- 支持 V20/V21、密钥生成与隐藏、按 Windows 用户加密保存服务端设置、合并和备份 Claude 配置、授权指定用户监听及局域网防火墙、服务启动/停止与日志、HTTP 鉴权和就绪检查。
- 图形入口在启动前独立检查 HTTP 监听，直接显示权限/端口错误；原引擎二进制未更改。虚拟机真实 TIA 工程联调待验证。

- 删除被 GUI 替代的根目录配置 BAT 和 tia CMD；CLI 直接调用 runtime 下的 EXE，保留工程生成、预热和取证脚本。
- 交付包版本 2.7.16，原 V20/V21 引擎保持 2.7.15.0；分别记录 GUI 与引擎的测试日期、输入及二进制哈希，完整包仍包含两版运行依赖。

## [2.7.15] - 2026-09-15

- 新增 38 个 HMI/PLC/工程/选件专用入口，总计 298 个工具；具体边界见 [实现与缺口清单](docs/reference/capabilities.md)。
- 新增公开属性定点访问、原生结果失败识别、文件非空/SHA-256 校验及事件预览 token；写入默认预览。
- 提供完整 V20/V21 交付包。离线与程序集回归通过；真实工程和选件验收待环境，不宣称官方 API 全覆盖。

## [2.7.14] - 2026-09-15

- 新增 PLC 类型组创建及四类 PLC 用户组管理、工艺对象管理、离线监视表导入、硬件删除/复制/移槽、软件单元与对象发布、库主副本及版本管理、Safety 离线工程管理、证书管理。新增写入工具默认 dryRun 预览，不自动保存或下载。
- 新增 Unified 画面/变量表组管理，报警类别、离散/模拟报警、报警/数据归档的原生对象管理，以及文本/图形列表原生导入和分页标量属性读取。复杂引用与列表条目不冒充完整读取；V20 图形列表 API 缺失明确失败。
- 新增离线操作边界测试与 V20/V21 官方 API 形状检查。新工具尚未在真实工程执行写入验收，不宣称覆盖全部 Openness API；详见 docs/reference/capabilities.md。

## [2.7.13] - 2026-09-15

- 新增 DeleteEmptyPlcBlockGroup：默认预览，精确定位空用户块分组；拒绝根组、非空组、重名、在线及未知状态，实际删除取得独占访问并回读确认。不递归删除、不自动保存。
- 两版实际 EXE 覆盖删除保护、预览、重检查与删除后验证；尚未在真实工程执行删除。

## [2.7.12] - 2026-09-15

- 共用 SoftwareContainer 解析增加类型化工程组/设备枚举后备路径，覆盖仍走旧入口的导入、建组等工具；严格匹配软件名，拒绝单 PLC 猜测、子串/正则猜测、重名及不完整扫描。
- 实际 EXE 验证入口接线和目标选择边界；未在真实工程执行写入、在线或下载验证。

## [2.7.11] - 2026-09-15

- 单块读取/导出复用列表器的 PLC 根组解析，修复旧软件解析入口导致的 Block not found。支持用户组路径、根组前缀、反斜杠及唯一裸名；同名歧义明确失败。
- 实际 EXE 回归覆盖 OPMODE01_FC 的分组路径及调用入口；真实工程仍待用户验证。

## [2.7.10] - 2026-09-15

- PLC 列块、列树、列类型及批量导出改为复用 GetSoftwareInfo/GetPlcTagTables 的 GetPlcSoftware 入口，恢复精确解析未命中时的工程级 PLC 枚举与名称匹配后备路径。
- 保留 2.7.9 的属性完整性和根组错误诊断；本地测试不能代替用户分组 ET 200SP F-CPU 的实际回归。

## [2.7.9] - 2026-09-15

- PLC 列块/列树及 GetTypes 使用每次调用的新鲜精确解析，避免依赖旧 SoftwareContainer 缓存；解析、Software、BlockGroup/TypeGroup 阶段分别诊断。
- 可选块/UDT 属性失败返回 null/明确占位及 failureCount、failures、dataComplete，不再使整个用户块清单失败；IPC/句柄失效继续立即终止。
- 明确用户块组/PLC 类型的采集范围，不再将未读取的系统块和外部源宣称为完整树。
- 此次 F-CPU 实际故障的根因尚未凭完整错误堆栈确认；真实工程回归独立于本地测试。

- 批量文档导出沿用修补后的 GetBlocks；底层列表读取失败或未打开工程时不再返回成功空结果。

## [2.7.8] - 2026-09-14

- 修复设备分组内 PLC 的裸软件名称解析：`+S1-K1` 等名称按字面递归匹配，与软件信息接口使用同一基础解析入口；重名明确报歧义，不采用模糊匹配或单 PLC 猜测。保留硬件遍历和块根组访问的异常，避免误报软件/程序块不存在。尚待新版在用户工程上验证。
- 新增 `ReadUnifiedRuntimeSettings`、`UpdateUnifiedRuntimeSettings`：读取和修改 Unified HMI 启动画面及受支持的根运行设置。默认预览，验证画面完整路径及名称唯一性，使用并发校验令牌并逐项及最终回读；不自动保存、编译、下载或重启。见 [接口说明](docs/guides/hmi/runtime-settings.md)。
- 新增 `ReadUnifiedGraphicSelection`：通过准确对象名称列表整体定位选择范围，分页读取原始坐标、尺寸、一层工程归属和可用的关系元数据；句柄失效后停止并阻止后续读取。
- 新增离线 `CompareUnifiedGraphicSelections`：核对前后完整分页证据并报告各对象坐标变化，拒绝缺页、缺字段和范围混用。
- 普通图形组合的真实成员、整体变换和坐标联动原因仍未验证，明确返回缺口，不将选择范围当作原生组合。见 [接口说明](docs/guides/hmi/graphic-selection.md)。
- 完整 V20/V21 运行包包含上述功能，文件版本统一为 2.7.8.0；真实工程验收状态见 [本版说明](docs/archive/release-notes.md)。

## [2.7.7] - 2026-09-14

- HMI 快照按准确路径定位页面；句柄释放、IPC 或不可恢复异常后停止后续远程读取，保留已有证据并明确报告缺口，避免自动重试或重新绑定。
- 增加 HMI 读取阶段日志、连接健康状态和显式重新绑定后的缓存清理；暂时隔离诊断控件的特定属性读取。尚未确认博图异常退出的具体根因。
- 新增 `UpdateUnifiedGlobalScript`，支持精确定位 Navigation 等已有 Unified 全局模块，默认预览、原生备份、确认令牌、单模块导入及完整正文回读验证。
- 修复通用修改接口的 `HmiScripts` / `HmiScriptModule` 路径定位，以及原生导入导出参数的文件/目录类型转换，保留写入限制。
- 完整 V20/V21 包文件版本为 2.7.7.0；包含最新程序、依赖、配置入口、源码、测试和文档。真实工程 Navigation 导入及本次稳定性修复尚未完成验收，V20 原生 Unified 导入能力尚未确认。

## [2.7.6] - 2026-09-14

- 库类型没有文档导出格式时，对非脚本类型增加官方 `LibraryTypeVersion.Export(FileInfo, ExportOptions.WithReadOnly)` XML 路径，只调用一次；已有文档导出异常时不追加尝试。
- 区分 `exportAttempted=false`（未执行）、原生调用返回、文件读取完整与内部内容未验证；未执行不再报 `NativeExportEmpty`，内部对象/变量数量保持未知。
- XML 原文、SHA-256 及结构化路径可分页读取。库版本 XML 可能只有元数据，明确返回 `LibraryXmlContentUnverified`，不能冒充面板内部绑定已读全。
- 完整 V20/V21 包文件版本为 2.7.6.0。新增离线与实际 net48 EXE 的 XML 路径回归；真实工程中的面板导出仍待部署新版验证。

## 历史版本

- [v2.7.5](docs/archive/release-notes.md#v275)
- [v2.7.4](docs/archive/release-notes.md#v274)
- [v2.7.3](docs/archive/release-notes.md#v273)
- [此前完整更新日志（仓库既有提交，保持原文）](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6a7298cbc08dd59fd08864d56a728a4da3435ed8/CHANGELOG.md)
