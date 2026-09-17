# 历史发布说明

本页合并 v2.7.2–v2.7.15 的原发布记录。工具数量、路径、测试结果和已知问题均为当时快照，不作为当前使用说明。当前入口见 [文档目录](../README.md) 和 [变更记录](../../CHANGELOG.md)。

---

<a id="v2.7.15"></a>

## v2.7.15

完整运行包：**TIA_MCP_Delivery_v2.7.15_20260915.zip**。包含 V20/V21 两版 EXE（2.7.15.0）、全部依赖、CMD/BAT 配置入口、手册、模板、源码、测试及校验清单；无需旧包补文件。公开包不含 PublicAPI 或个人连接密钥。

新增 38 个专用工具入口，总计 298 个：HMI Unified 属性、事件脚本、多语言、交叉引用、归档和工厂视图；PLC 原生实例 DB/源/装载文件、标签定义；工程语言、文本、归档恢复和库操作；ProDiag、CFC、Motion、Test Suite、SiVArc、Startdrive、DCC 的部分原生操作。细分动作与参数见 [实现与缺口清单](../reference/capabilities.md) 和 [工具表](../reference/tool-matrix.md)。

新增写入/文件操作默认 dryRun=true。预览不等于真实执行成功；原生诊断失败单独标注，文件哈希不等于内部语义完整。事件修改要求预览 token，应用/系统 Test Suite 执行要求额外明确确认其仿真/服务器影响。不自动保存、编译、下载或关闭博途；库保存/关闭仅作为明确选择的动作。

本地验证：708 项离线测试；官方 API 形状及默认预览检查 V20 115 项、V21 130 项；两版实际 EXE 的 HTTP、HMI、资源发现、远程对象失效、解析器等回归。完整结果见 manifest/release-build.json。原有 OpcUaLiveReader 空引用编译警告仍存在。

**真实工程验收尚未完成。** 本轮没有连接/修改虚拟机工程或现场设备。用户已安装选件，将另行提供环境与测试项目。Teamcenter、多用户、Safety Validation、完整 DCC/SiVArc 等仍有明确缺口，不能将本版称为官方 API 全覆盖；详见上述清单。


---

<a id="v2.7.14"></a>

## v2.7.14

完整运行包：**TIA_MCP_Delivery_v2.7.14_20260915.zip**。包含 V20/V21 两版 EXE（文件版本 2.7.14.0）、依赖、CMD/BAT 入口、模板、源码和测试；无需旧包补文件。

本版新增 15 个工具，将此前列出的十类工程能力缺口纳入专用接口，并补充 HMI Unified 对象和文件夹管理。多个相关动作合并在按领域命名的工具中，而不是每个动作重复生成工具。

完整操作、参数及限制见 [能力覆盖表](../reference/capabilities.md)。新增写入工具均默认 `dryRun=true`；创建、重命名、删除、导入等只有传 `false` 才执行。不自动保存、编译或下载。部分原生操作可能连带修改依赖或留下已创建对象，失败响应保留 `mayHaveChanged` 或已创建父组，不自动重试或回滚。

例：创建嵌套 PLC 类型组（先预览）：

```json
{"softwarePath":"+S1-K1","groupPath":"Common/Motors","dryRun":true}
```

工具：`CreatePlcTypeGroup`。PLC 组统一管理示例：

```json
{"softwarePath":"+S1-K1","family":"types","groupPath":"Common/Motors","action":"rename","newName":"Drives","dryRun":true}
```

工具：`ManagePlcUserGroup`。Unified 报警读取示例：

```json
{"softwarePath":"HMI_RT_2","category":"discreteAlarms","offset":0,"limit":100}
```

工具：`ReadUnifiedEngineeringObjects`。读取范围为公开标量属性，复杂引用和条目另列，不是完整 HMI 迁移包。

验证包括离线行为测试、两版 EXE 的 API 形状和默认预览检查，以及原有 HTTP/HMI 回归。详细计数见 `manifest/release-build.json`。**没有在真实 TIA 工程中执行新增写入验收。编译、API 存在和离线测试通过不等于原生工程动作已验证。**

明确边界：Unified 脚本组无对应集合；文本/图形列表使用原生导入，不能调用不存在的 `Create(string)`；提供的 V20 API 不包含 `HmiGraphicLists`。面板内部内容完整导出、原生图形组合和既有深层采集缺口没有因此自动解决。Safety 修改要求对应许可及已有的 TIA 登录状态；本工具不处理密码、关闭安全模式或执行现场动作。


---

<a id="v2.7.13"></a>

## v2.7.13

完整包 **TIA_MCP_Delivery_v2.7.13_20260915.zip**，包含 V20/V21 **2.7.13.0** EXE、全部依赖、启动脚本、源码、测试、模板及文档。

### 删除空 PLC 程序块分组

新增 L2 工具 `DeleteEmptyPlcBlockGroup(softwarePath, groupPath, dryRun=true)`。默认只预览。若客户端未直接列出它，可通过 FindTools / CallTool 调用。

预览示例：
```json
{"softwarePath":"+S1-K1","groupPath":"ZZ_MCP_TEST","dryRun":true}
```
确认预览目标后，以相同参数并将 `dryRun` 改为 `false` 执行删除。

`groupPath` 相对于 Program blocks，支持 `Parent/Child`、反斜杠和 Program blocks/程序块前缀。每段按完整名称匹配，不使用通配符或裸名递归猜测。只允许空的 PlcBlockUserGroup，根组、非空组（包括含空子组）、重名和错误路径均拒绝。

实际操作要求确认 Offline，取得 TIA 独占访问，在 Delete 前再次检查内容与状态，然后回读确认组不存在。Unknown 不视为 Offline。删除后验证失败会明确报错，不自动重试。不自动保存、编译、下载、关闭工程或 GoOffline。

返回 Meta 包含 softwarePath、resolvedSoftwareName、groupPath、blockCount、subgroupCount、onlineState、canDelete、dryRun、deleted、verifiedAbsent。canDelete 表示当前检测条件，不保证后续请求状态不变。

基于西门子官方 `PlcBlockUserGroup.Delete()`：
https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/deleting-group-for-blocks

### 验证范围

两版实际 EXE 检查默认预览、根路径拒绝、非空拒绝、Online/Unknown 拒绝、删除前重检查和删除后验证；完整编译与回归清单见 manifest/release-build.json。**未在真实工程执行删除**，本地替身测试不能代替实际工程验收。

公开包不含密钥、客户工程、日志或 Siemens PublicAPI，保留 LICENSE、NOTICE 与原作者版权。


---

<a id="v2.7.12"></a>

## v2.7.12

完整包 **TIA_MCP_Delivery_v2.7.12_20260915.zip**，包含 V20/V21 **2.7.12.0** EXE、依赖、启动脚本、源码、测试、模板及文档，无需旧包补文件。

### 共用容器解析修补

用户确认读取正常，但 PlcBuildAndImport 真导入和 CreatePlcBlockGroup 对 +S1-K1 仍失败。它们使用 GetSoftwareContainer，未使用读侧已有的 PLC 枚举后备路径。本版在共用的裸名称容器解析入口补上类型化工程组/设备/DeviceItem 遍历，返回原始 SoftwareContainer。原有精确路径解析先执行，未命中时才使用该后备路径。

后备遍历采用 GetAllPlcSoftware 的工程组与设备枚举结构，但不复用其模糊选择或按名称去重：软件名必须完整匹配（忽略大小写）；不采用单 PLC、子串或正则猜测；多个同名容器、扫描异常或容量上限均明确失败，不把不完整扫描当作唯一目标。既有使用 GetPlcSoftware 的工具原有匹配规则未在此版本整体改写。

修补由 GetSoftwareContainer 的调用方共用，包括块导入、建组以及使用该入口的类型/变量表导入和组定位。其他写入工具尚未在真实工程逐一验证，不能宣称已全部可用。保留既有 dryRun、写入和权限规则，不自动修改、保存、编译、下载或关闭任何工程。

### 验证与限制

两版实际 EXE 验证入口调用、IEC 精确名称匹配、错误单 PLC 名称拒绝、子串/正则拒绝、重名拒绝及扫描中断不返回匹配；同时运行已有 HTTP/HMI 与离线回归，详见 manifest/release-build.json。

**未在真实工程执行建组或导入，也未操作 PLC 在线、下载、强制或写变量。** 本地解析测试不能代替真实工程写入验收；原精确硬件查找为何漏匹配仍未独立复现，不能归因于 F-CPU 安全块。

公开包不含用户密钥、客户工程、日志或 Siemens PublicAPI；保留 LICENSE、NOTICE 与原作者版权。沿用已有连接配置及密钥。


---

<a id="v2.7.11"></a>

## v2.7.11

完整包 **TIA_MCP_Delivery_v2.7.11_20260915.zip**，包含 V20/V21 **2.7.11.0** EXE、依赖、启动脚本、源码、测试、模板及文档，无需旧包补文件。

### 单块读取定位修补

2.7.10 的列表器已恢复，但 GetBlock 仍调用旧 SoftwareContainer 解析，并在块组定位时再次解析软件。GetBlockInfo 与 DescribeBlockLogic（经 ExportBlock）因此仍可能返回 Block not found。本版复用列表器的 GetBlockRootGroup，在同一根组下定位块，不再重复走旧入口。

支持 03_OPMode/OPMODE01_FC、Program blocks/03_OPMode/OPMODE01_FC、程序块前缀、反斜杠及唯一裸名。明确指定错误分组时不忽略路径；裸名匹配多个块时拒绝歧义。保留现有块名精确匹配优先及锚定正则行为。只遍历用户块组，不能据此认定系统/安全内部块已完整取得。

GetBlock 的现有调用方均使用修补后的定位，包括单块导出。不会自动保存、编译、下载或关闭 TIA；DescribeBlockLogic 仍通过本地临时 XML 导出解析逻辑。

### 验证与限制

两版实际 EXE 检查分组块各种路径、重复裸名、错误限定路径、含点名称和实际入口调用。完整验证与哈希见 manifest/release-build.json。**尚未在用户真实工程读取 FC300 的逻辑正文**，本地测试不能代替工程验收。精确软件查找原先漏匹配的工程条件仍未独立复现，不归因于 F-CPU 安全块。

公开包不含用户密钥、工程、日志或 Siemens PublicAPI；保留 LICENSE、NOTICE 和原作者版权。沿用已有连接配置和密钥。


---

<a id="v2.7.10"></a>

## v2.7.10

完整包 **TIA_MCP_Delivery_v2.7.10_20260915.zip**，包含 V20/V21 的 **2.7.10.0** EXE、全部依赖、启动脚本、源码、测试、模板及文档，无需旧包补文件。

### PLC 解析入口统一

2.7.9 的列表器仅调用精确 SoftwareContainer 解析，跳过了 GetSoftwareInfo、GetPlcTagTables 等正常接口已有的后备枚举。用户在分组 +S1 内的 ET 200SP F-CPU +S1-K1 上确认返回 null；此时尚未访问块或类型根组，不能归因于安全块读取。

本版让 GetBlocks、GetBlocksWithHierarchy、GetSoftwareTree、GetTypes 共用 GetPlcSoftware，ExportBlocksAsDocuments 通过 GetBlocks 使用同一入口。保留精确解析；未命中时使用既有的工程级 PLC 枚举与名称匹配，包括单 PLC 后备匹配。该匹配可接受单 PLC 工程的别名，并非始终严格路径匹配。未修改其他写入工具的解析行为。

保留 2.7.9 的可选属性失败诊断、dataComplete 标记、根组访问阶段及 IPC 中断处理。不自动保存、编译、下载或关闭 TIA。用户块组范围不等于所有系统/安全内部块。

### 验证

检查实际 EXE 的列表解析确实调用 GetPlcSoftware，并覆盖 IEC 精确名称、多 PLC 名称匹配、单 PLC 站名后备匹配及此前完整性/IPC 回归。全部验证与文件哈希见 manifest/release-build.json。

**尚未在用户的真实 ET 200SP F-CPU 工程验证**。工程硬件树精确查找为何漏匹配仍需运行时证据；本版复用用户已验证正常的解析入口，不宣称已证明符号或 F-CPU 是根因。

公开包不含密钥、客户工程、日志或 Siemens PublicAPI。保留原作者版权、LICENSE 和 NOTICE。沿用已有连接配置与密钥，替换为对应 TIA 版本的新程序。


---

<a id="v2.7.9"></a>

## v2.7.9

完整运行包 **TIA_MCP_Delivery_v2.7.9_20260915.zip**，含 V20/V21 两版 **2.7.9.0** EXE、依赖、CMD/BAT 入口、源码、测试、模板和文档。完整解压使用，无需旧包补文件。

### PLC 列块与列树修补

- `GetBlocks`、`GetBlocksWithHierarchy`、`GetSoftwareTree`、`GetTypes` 每次重新精确解析 PLC，并在单次调用中持有解析到的软件；不依赖之前缓存的 SoftwareContainer，不使用正则、子串或单 PLC 猜测。保留 `+S1-K1` 的字面含义及同名歧义检查。
- 解析、Software 属性和 BlockGroup/TypeGroup 访问分别报告失败阶段及异常。根组访问失败不会再冒充软件匹配失败；没有读取 BlockGroup 的解析错误会明确说明这一点。
- 对可选块/UDT 属性逐项容错：无法读取的字段为 null，树中用 `?`/`unavailable` 表示；保留其他可读对象，并返回 `meta.dataComplete=false`、失败数量和属性诊断。根组、集合遍历、对象名称仍为必要数据，失败时不返回伪完整清单。
- IPC、COM 或句柄释放错误立即终止，不自动重连、重试、关闭 TIA，也不执行保存、编译或下载。
- 这些接口当前范围为用户块分组；GetSoftwareTree 另含 PLC 数据类型。系统块分组和外部源不在本次范围内，描述和返回 scope 已明确。不能用它们证明 F-CPU 的全部系统/安全内部块已读完。

`ExportBlocksAsDocuments` 的批量选择调用同一个 `GetBlocks`，随本次修补使用新解析入口；未在真实 F-CPU 上执行批量导出。

### 验证与限制

离线测试及两版实际 EXE 检查覆盖精确名称、设备嵌套、歧义拒绝、可选属性失败保留、完整性标记、IPC 中断和根组失败阶段。准确数量、编译输入与运行文件哈希见 `manifest/release-build.json`。

用户已确认问题发生于 2.7.8.0，但尚无完整错误响应或该 F-CPU 的本机复现。因此本版修复已识别的缓存依赖与可选属性导致整次读取失败的问题，**尚未确认这台 F-CPU 的实际故障根因，也未完成真实工程验收**。安全程序块或位号命名引发故障仍是待验证假设，不能凭发布成功认定已解决。

公开包不含用户密钥、客户工程、日志或 Siemens PublicAPI。保留 LICENSE、NOTICE 与原作者版权。需本机安装匹配的 TIA Portal/Openness 和 .NET Framework 4.8。沿用已有连接参数及旧密钥，启动新版对应 runtime 目录中的 EXE。


---

<a id="v2.7.8"></a>

## v2.7.8

完整附件 **TIA_MCP_Delivery_v2.7.8_20260914.zip** 包含 V20/V21 两版程序、全部分发依赖、CMD/BAT 配置入口、手册、模板、源码、测试和校验清单。两版 EXE 文件版本均为 **2.7.8.0**。整包解压使用，无需从旧包补文件。

本版包含三组更新：

- **设备分组内 PLC 查找修复**：裸软件名称（例如 `+S1-K1`）在全部设备组中按字面精确匹配，与软件信息接口使用同一基础解析入口；同名软件明确报歧义，不通过正则、子串或单 PLC 猜测目标。硬件扫描异常、扫描限制和块根组访问异常保留原因，不再笼统当成软件或块不存在。
- **图形选择与坐标核对**：`ReadUnifiedGraphicSelection` 按准确对象名称列表采集坐标、尺寸和可读的一层归属；`CompareUnifiedGraphicSelections` 对比完整的前后证据，拒绝缺页或范围混用。选择范围不等于原生图形组合，不推断坐标联动因果，也不写坐标。见[接口说明](../guides/hmi/graphic-selection.md)。
- **HMI Runtime 启动画面与设置**：`ReadUnifiedRuntimeSettings` 读取受支持设置；`UpdateUnifiedRuntimeSettings` 默认预览，校验准确画面路径、字段类型和并发令牌，实际应用后逐项及最终回读。失败报告部分应用风险，不自动重试、回滚、保存、编译、下载或重启。见[接口说明](../guides/hmi/runtime-settings.md)。

保留 HTTP 单响应读取、超时恢复及鉴权、资源发现接口、HMI 子文件夹递归、内部变量来源分类、分页缓存管理、全局脚本读取/编辑及 HMI 快照诊断。

本地验证包含离线回归、V20/V21 编译、两版实际 EXE 的 HTTP/HMI/资源发现/远程代理检查，以及新增软件解析、图形选择、启动设置测试。最终通过数量和输入/运行文件哈希记录在 `manifest/release-build.json`。

**本版尚未部署到用户虚拟机完成真实工程验收。** PLC 分组查找、启动设置写入和图形坐标关系仍需用实际工程核验；原生图形组合关系、库面板内部绑定、动态名称展开及完整迁移核对仍有明确缺口。V20 编译通过不证明 V21 特有的 Unified API 在 V20 可用。测试通过或发布成功不代表这些缺口已经解决。

运行仍需对应 TIA Portal、Openness 和 .NET Framework 4.8。在虚拟机运行时，沿用现有 HTTP 参数、端口和旧密钥，将程序路径指向新版 `runtime/v21/TiaMcpServer.exe` 或 `runtime/v20/TiaMcpServer.exe`。完整配置说明见[开始使用](../../README.zh-CN.md)。

公开包不包含个人密钥、私人启动脚本、客户工程、日志或 Siemens PublicAPI。原作者版权、MIT 许可证和 NOTICE.md 来源说明保留。`RELEASE_STATUS.txt` 记录源码提交；`manifest/release-file-hashes.json` 记录包内校验值；ZIP 的 SHA256 另附 `.sha256`。


---

<a id="v2.7.7"></a>

## v2.7.7

完整附件 **TIA_MCP_Delivery_v2.7.7_20260914.zip** 包含 V20/V21 程序、全部分发依赖、CMD/BAT 配置入口、手册、模板、源码、测试和校验清单。两版 EXE 文件版本均为 **2.7.7.0**，整包解压使用，无需从旧包补文件。

本版修复 HMI 读取稳定性与 Unified 全局脚本修改路径：

- HMI 页面按准确绝对路径查找，只进入指定文件夹；软件对象只读取一次。句柄释放、IPC 或不可恢复异常后停止后续读取，保留部分证据，明确标记失败、数据不完整及需要显式重新绑定。
- 增加 `GetState.Meta.hmiReadHealth` 和 MCP 所在机器的 `%TEMP%\TiaMcpServer.hmi-read.log`，记录读取阶段、路径及错误；显式重新绑定成功后清除软件缓存。日志不包含脚本正文或连接密钥。
- 暂时隔离 `HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText` 属性读取，明确保留缺口。现有证据不足以确定该属性就是博图退出的根因，本版不宣称已根治博图异常退出。
- 修复通用修改接口对 `HmiScripts`、`HmiScriptModule` 的精确定位，以及官方方法的 `DirectoryInfo` / `FileInfo` 参数转换，保留原有写入限制。
- 新增 `UpdateUnifiedGlobalScript`，精确定位已有 HMI 的 Navigation 等全局模块。默认只预览并备份原生 JS/YAML；真正应用须传入预览令牌，重新核对原文后仅导入一个模块，再回读完整 JS 正文验证。导入被拒绝、无效或回读不一致均报告失败；导入开始后的异常明确提示可能已有变化，不自动重试或回滚。
- 脚本更新接口不创建或删除模块，不保存、编译、下载、关闭博图，也不删除页面。脚本只做语法解析，不执行脚本；原生备份保留在 MCP 所在机器。

本地 551 项离线检查通过；V20/V21 两版实际 EXE 各通过 28 项 HTTP、18 项 HMI 递归、42 项资源发现、8 项原生导出/远程代理、4 项快照远程代理及采集接口程序集检查。全局脚本桥接 V20 通过 7 项，V21 通过 8 项。完整结果记录在 `manifest/release-build.json`。V21 使用实际官方 DLL 核实全局脚本方法签名；提供的 V20 PublicAPI 不含 WinCCUnified DLL，因此 V20 原生 Unified 导入能力尚未确认。

**本次尚未在虚拟机真实工程中验证 Navigation 导入及稳定性修复。** 导航和页面未因发布动作而修改，运行时跳转也未因此完成核对。库版本 XML 可能仍只有元数据，面板内部对象、脚本、变量绑定的完整读取仍是明确缺口。

保留 HTTP 鉴权、单响应读取与超时恢复、资源发现、HMI 子文件夹递归、内部变量分类、缓存管理及只读迁移功能。沿用现有启动参数、端口和密钥，将程序路径指向新版 `runtime/v21` 或 `runtime/v20`。公开包不含个人密钥、客户工程数据、私人日志或 Siemens PublicAPI；仍需对应 TIA Portal、Openness 和 .NET Framework 4.8。

详见[全局脚本更新说明](../guides/hmi/global-scripts.md)、[HMI 快照稳定性诊断](../troubleshooting/hmi-snapshots.md)和[开始使用](../../README.zh-CN.md)。原作者版权、MIT 许可证与 NOTICE.md 来源说明保留；`RELEASE_STATUS.txt` 记录源码提交，`manifest/release-file-hashes.json` 记录包内文件校验值，ZIP 校验值另附 `.sha256`。


---

<a id="v2.7.6"></a>

## v2.7.6

完整附件 **TIA_MCP_Delivery_v2.7.6_20260914.zip** 包含 V20/V21 程序、分发依赖、CMD/BAT 配置入口、手册、模板、源码、测试和校验清单。两版 EXE 文件版本均为 **2.7.6.0**，整包解压使用，无需从旧包补文件。

本版补充空格式列表之后的只读读取路径，并修正结果解释：

- `GetSupportedExportFormats()` 为空，仅表示未提供文档导出格式。对非脚本库类型，改用另一条官方 `LibraryTypeVersion.Export(FileInfo, ExportOptions.WithReadOnly)` XML 导出路径；不会猜测 WinCCML 格式。
- 仅对选定路径和准确版本调用一次。已开始的文档导出失败、IPC 异常或结果检查异常均不触发备用导出；不编辑、实例化、保存、编译或关闭工程。
- `exportAttempted` 明确区分未执行和执行失败。未执行不再额外报 `NativeExportEmpty`；零文件数不代表零内部对象或零变量。
- 返回实际 XML 原文、SHA-256、结构化路径和分页证据。`nativeFilesComplete` 表示文件及解析是否完整；版本 XML 可能只有元数据，因此保留 `LibraryXmlContentUnverified`，`dataComplete=false`，内部数量为未知。取得 XML 不等于面板内部迁移核对通过。

本地验证：494 项离线检查通过；V20/V21 两版实际 EXE 各通过 28 项 HTTP、18 项 HMI 递归、42 项资源发现、8 项原生导出/远程代理检查及采集接口程序集检查。测试覆盖 XML 备用路径、仅元数据文件、空文件输出、异常不重试以及跨页结果生命周期。

**本版新增 XML 路径尚未完成真实博途工程验收，不能宣称已经取得面板内部对象、接口或绑定。** 部署后按准确类型/版本建立新采集，保存全部分页中的 `nativeFile`、`nativeValue` 和缺口记录。先验证一个类型，再决定是否继续；出现 IPC 异常时停止导出重试并保留日志。详见[只读迁移说明](../guides/hmi/read-only-migration.md)。

保留既有 HTTP 鉴权、资源发现、HMI 子文件夹递归、内部变量分类、缓存管理及脚本读取修复。沿用现有启动参数、端口和密钥，将程序路径指向新版 `runtime/v21` 或 `runtime/v20`。公开包不含个人密钥、客户工程数据、私人日志或 Siemens PublicAPI；仍需对应 TIA Portal、Openness 和 .NET Framework 4.8。

原作者版权、MIT 许可证与 NOTICE.md 来源说明保留。`RELEASE_STATUS.txt` 记录源码提交，`manifest/release-file-hashes.json` 记录完整文件清单；ZIP 校验值另附 `.sha256`。


---

<a id="v2.7.5"></a>

## v2.7.5

完整附件为 **TIA_MCP_Delivery_v2.7.5_20260914.zip**，包含 V20/V21 程序、全部分发依赖、原版 CMD/BAT 配置入口、手册、模板、源码、测试和校验清单。两版 EXE 的文件版本均为 **2.7.5.0**，完整解压使用，无需从旧包补文件。

本版处理库类型原生导出结果检查和分页期间的对象失效：

- 按 V21 官方示例先读取原生状态；Success 不再访问 Messages，非成功结果才读取有数量上限的官方诊断。
- 返回的 FileInfo 可能是 .NET Framework 远程代理，现于结果检查阶段读取路径并转换为本地数据。跨页文件读取不再访问这些远程代理；发生异常后也不在错误处理分支重复访问失效对象。
- 分开报告原生方法是否返回、原始状态、结果检查是否完整、文件和脚本正文是否完整。保留 Success 原值时会同时明确具体失败阶段，不把结果检查错误笼统标成原生导出调用失败。
- 结果检查异常即停止后续远程访问；回收已生成的本地文件作为部分证据，不自动导出重试、重连、保存或关闭博途。同一绑定项目的游标分页可继续读取已采集的文件；重新绑定仍使旧游标失效。
- 增加带操作编号、UTC 时间和异常栈的持久日志，以及只读退出诊断命令 `scripts\diagnostics\Collect-TiaExitEvidence.cmd`。

本地验证：487 项离线检查通过；V20/V21 两版实际 EXE 各通过 28 项 HTTP、18 项 HMI 递归、42 项资源发现协议、6 项 .NET Framework 远程代理检查和采集接口程序集检查。透明代理测试覆盖 IPC 失效后不再触碰原对象、继续读取本地文件，以及原生调用成功但结果检查失败的状态区分。详细构建记录见 `manifest/release-build.json`。

**本版未完成真实博途工程复测，不能宣称库脚本/面板内部文件已取得，也不能确认博途异常退出已解决或确定退出根因。** 如再出现无弹窗退出，请保留 `%TEMP%\TiaMcpServer.native-export.log` 和 Windows 事件记录；具体步骤、状态解释及验收范围见[只读迁移说明](../guides/hmi/read-only-migration.md)。

保留已有 HTTP 鉴权、HMI 子文件夹递归、内部变量分类、缓存回收和资源发现修复。沿用现有启动参数、端口及密钥，将路径指向新版 `runtime/v21` 或 `runtime/v20`。公开包不含个人密钥、工程数据、私人日志或 Siemens PublicAPI；仍需安装对应 TIA Portal、Openness 和 .NET Framework 4.8。

原作者版权、MIT 许可证与 NOTICE.md 来源说明均保留。`RELEASE_STATUS.txt` 记录源码提交，`manifest/release-file-hashes.json` 记录完整文件清单，ZIP 校验值另附同名 `.sha256`。


---

<a id="v2.7.4"></a>

## v2.7.4

完整附件为 **TIA_MCP_Delivery_v2.7.4_20260913.zip**，包含 V20/V21 两版程序、分发依赖、原版 CMD/BAT 配置入口、手册、模板、源码、测试、LICENSE、NOTICE.md 与校验清单。两个 EXE 的文件版本均为 **2.7.4.0**，完整解压即可使用，无需旧包补文件。

本版修复：

- HTTP 和 STDIO 均响应 `resources/list`、`resources/templates/list`，分别返回 `resources: []` 和 `resourceTemplates: []`，不返回续页游标；正确声明资源能力，不宣称支持订阅或列表变化通知。博途数据仍通过现有工具读取，空资源目录不代表工程为空。
- 识别变量连接字段中的准确 `<内部变量>` 标记。保留原文，标记来源为 `ownConnectionMarker`；空成员字段从根变量推导时仍明确标注 `inferredFromRoot`。内部标记与 PLC 名称或符号冲突时返回分类冲突，不掩盖证据。
- 分开报告字段读取和来源分类。`readComplete` 表示遍历结束且没有读取缺口，`classificationComplete` 表示遍历结束且没有来源分类缺口；`dataComplete` 继续要求全部完成。`readFailureCount`、`classificationFailureCount` 为累计计数，原 `failureCount` 保留为合计。变量来源记录增加 `definitionFieldCount`、`expectedDefinitionFieldCount`、`definitionFieldsComplete`。
- 库类型按 `GetSupportedExportFormats()` 返回的格式导出，不再向所有类型强传 WinCCML。补齐官方诊断、返回文件清单、子目录检查及导出摘要；Warning、空文件、脚本无 JS 正文均明确报缺口。只读取所选类型和版本，不编辑或实例化类型。
- 已完成集合立即释放迭代器，并从 16 个活动采集名额中移除。末页重放单独保留最多 32 个集合、闲置 10 分钟，按最近使用顺序回收；未完成采集仍保留 30 分钟闲置期限。新增 `releaseCursor`，客户端持久保存全部页面后应调用 `ReleaseUnifiedReadCursor`。

验证范围：离线回归包含 4,341 条合成变量/成员、17 个字段和 113 个内部标记的分页回归；两版已编译程序集均验证资源发现、HTTP 鉴权、原始请求 ID、工具调用、HMI 递归和只读采集注册。资源协议测试通过独立测试程序加载 EXE 的真实 HTTP/STDIO 服务入口，不改变宿主机 Openness 用户组。具体通过数量及文件哈希见 `manifest/release-build.json`。

**2.7.4 尚未在虚拟机真实工程上重新验收。库面板内部导出、库脚本正文、页面嵌套分支、动态名称和完整函数调用链仍待验证，不能宣布迁移全部核对通过。** 本版补齐被旧实现遗漏的导出诊断，用于识别剩余官方 API 限制；不声称修改代码后已取得这些内部文件。具体项目证据仅供本地核对，不含在公开包内。验收步骤见[只读迁移说明](../guides/hmi/read-only-migration.md)。

沿用现有连接参数、端口和密钥，将运行路径指向新版对应的 `runtime/v21` 或 `runtime/v20`。公开包不含个人密钥、私人启动脚本、项目数据或 Siemens PublicAPI。仍需在安装对应 TIA Portal、Openness 与 .NET Framework 4.8 的系统中运行。

完整包源码提交见 `RELEASE_STATUS.txt`；文件清单见 `manifest/release-file-hashes.json`；ZIP 校验值见同名 `.sha256` 附件。原作者版权、MIT 许可证与来源声明均保留。


---

<a id="v2.7.3"></a>

## v2.7.3

本项目按独立版本线发布，后续使用 `v2.7.4`、`v2.7.5` 等标准版本号。Release 标题与标签均为 `v2.7.3`。

完整附件为 **TIA_MCP_Delivery_v2.7.3_20260913.zip**，V20/V21 两版程序文件版本均为 **2.7.3.0**。包内包含两版运行程序及所有分发依赖、原版 CMD/BAT 配置入口、手册、PLC/HMI 模板、工程蓝图、源码、测试和校验清单。完整解压即可使用，不需要从旧包复制文件。

新增八个只读采集接口，覆盖全局脚本、变量定义分页、精确库类型及面板实例、页面对象分支和游标管理。保留 HTTP 响应路由、鉴权、HMI 子目录递归及已有工程操作修复。完整契约、证据字段和已知限制见[只读迁移说明](../guides/hmi/read-only-migration.md)。

本地离线回归 445 项通过；V20/V21 实际 EXE 各通过 HTTP 28 项、HMI 遍历 18 项，以及新增接口注册和原始脚本解析检查。`manifest/release-build.json` 记录编译输入、运行文件哈希和检查结果。

**新接口尚未完成真实博途工程部署验收。** 脚本实际来源、深层变量完整性、面板内部绑定和页面剩余分支须逐项核验。V20 编译通过不代表 V21 特有 API 在 V20 可用，接口必须据实际支持情况返回缺口。

运行仍需本机安装对应 TIA Portal、Openness 与 .NET Framework 4.8。沿用现有连接密钥与原版参数；公开包不含个人密钥、私人启动脚本、项目导出数据或 Siemens PublicAPI。先阅读根目录 `README.zh-CN.md`。

完整包源码提交位于 `RELEASE_STATUS.txt` 和 `manifest/release-file-hashes.json`；ZIP 校验值位于同名 `.sha256` 附件。后续每次发布遵循[完整发布流程](../development/release-workflow.md)。


---

<a id="v2.7.2-asckye.7-readonly.1"></a>

## v2.7.2-asckye.7-readonly.1 完整交付包

V20/V21 两版程序文件版本均为 **2.7.2.7**。保留 HTTP 响应路由、连接鉴权、HMI 子目录递归查找及上一版修复，新增八个只读采集接口（见[接口与缺口说明](../guides/hmi/read-only-migration.md)）。

本次完整包补齐 V20/V21 运行程序及依赖、原版配置入口、手册、PLC/HMI 模板、工程蓝图、源码和测试；包含新增的 Esprima 脚本解析依赖。完整解压即可使用，不需要先安装旧包，也不要只复制 EXE。

本地离线回归 445 项通过；V20/V21 实际 EXE 各通过 HTTP 28 项、HMI 遍历 18 项，以及只读采集接口与脚本解析程序集检查。以 `manifest/release-build.json` 中的编译输入和运行文件哈希定位验证对象。

**新接口尚未完成真实工程部署验收。** 全局脚本正文、变量成员完整性、面板内部绑定对应关系和原生导出限制须按接口说明逐项核对，不能将本机检查通过视为迁移数据完整。V20 的编译和兼容测试也不代表全部 V21 特有 API 在 V20 可用；接口必须按实际 API 支持情况返回缺口。

公开包不含个人连接密钥或 Siemens PublicAPI。沿用现有密钥和原版启动参数，详见包根目录 `README.zh-CN.md`。完整包对应提交记录于 `RELEASE_STATUS.txt` 和 `manifest/release-file-hashes.json`；本次附件修订保留已有发布标签，完整包包含随后提交的打包流程和 V20 程序补齐。

后续每次发布统一使用[完整发布流程](../development/release-workflow.md)。


---

<a id="v2.7.2-asckye.6-defects.1"></a>

## 2.7.2-asckye.6-defects.1：工程操作与结果可信度修复

文件版本 **2.7.2.6**；基线提交 `61a0509`。本版提供已编译的完整 V20/V21 运行包，源码随 Release 标签发布。未替换虚拟机服务，未完成 V20/V21 真实工程验收。本说明不代表现场缺陷已全部关闭。

### 已实现与边界

| 报告项 | 本次改动 | 验证边界 |
| --- | --- | --- |
| D01 / D05 | Describe 仅 Find 既有事件；非法枚举、缺失事件和读取异常返回结构化失败，外层不再覆盖为成功。Set 保留显式写入时创建的契约。 | 只读解析与业务/CallTool 错误传播有离线回归；工程 IsModified、既有脚本现场核对待完成。 |
| D02 | 仅对明确 `text:""` 接受现场已观察到的精确 `<body><p/></body>`；其它显式 HTML/空格不做宽泛等价处理。 | Text / AlternateText / ToolTipText 故障注入通过；真实清空、恢复、保存和关闭重开仍待分别验收。 |
| D02 回执 | 保留 requestedText、expectedRawText、actualRawText、verificationMode、targetMatched、nonTargetUnchanged、setterInvoked、setterCompleted、before/after、mayHaveChanged。回读失败为 Unknown。itemResults 记录创建残留和失败；changed 不是无修改保证。 | strict 仍不是事务；不会自动回滚或自动保存。 |
| D03 | 编译入口及组合导入中的编译结果使用根状态、子状态/计数和有来源的声明错误判断；出现子错误不能返回整体 Success。保留 rootState/rootCounts；冲突时顶层计数可为 null，不把不同范围相加。 | Siemens 计数范围不推定等同。根值、结构路径、可见节点数、声明文本及来源分别保留。 |
| D03 / R02 | 每个消息公开属性和子集合只读一次，去掉反复探测不支持的 GetAttribute。最多 10000 节点、64 层、节点间 30 秒预算；返回 compileElapsedMs、collectionElapsedMs、incomplete、truncated。 | 单次阻塞 Openness 调用无法中断；历史 240 秒超时根因尚未现场定位。全量 MCP 分页不等于取得 TIA 已截断的消息。无截断证据时 diagnosticsComplete=null，表示上游完整性未验证。 |
| D04 | 区分 ObjectNotFound、PropertyNotFound、UnsupportedPath、NullIntermediate、ReadFailed 和真实 null，记录失败段与已解析类型；索引路径明确拒绝。 | GetObjectProperty 普通集合仍是摘要；完整性要求使用有范围说明的快照。 |
| D06 | 先确认 Export API，再写同目录唯一临时文件；存在且非空后计算长度/哈希，使用原子替换；失败保留旧文件和可检查的临时产物。不猜测未知参数类型。 | 校验范围仅存在、长度和 SHA-256，不宣称原生文件已做导入恢复测试。unsupported 不是 Export 成功。 |
| C01 / C02 / C03 | 增加精确事件/动态对象读删、空组删除；默认预检，事件和动态对象实删要求当前内容 token；有内容、同名歧义、过期 token 拒绝。 | 已有其它事件、动态对象保持的离线断言通过；真实删除只能在工程副本验收。遗留误新增事件没有被本轮自动清理。 |
| C04 | 新增有范围/限额/逐项状态的画面快照、带完整路径的画面分页；补已保存工程的原生压缩归档。 | 快照不是备份，不承诺隐藏属性/库内部对象覆盖。Archive 不自动保存、不切换工程路径、不覆盖旧文件；归档检索恢复未测试。 |
| C05 | `/mcp/health` 保持 HTTP 存活；鉴权后的 `/mcp/ready` 区分 Initializing/Ready/Failed/Stopped。TIA 状态为 NotProbed，由 GetState 单独查询。 | 就绪状态不会触发连接或启动博途。内部宿主退出仍取消 listener；失败后应从进程日志读取原因，HTTP 不会为诊断而继续假存活。 |
| R01 | HTTP JSON 明确用严格 UTF-8 解码；失败响应给 requestId/JSON 位置，日志记录字节数、SHA-256 和 charset，不记录正文或密钥。 | 本机实际 EXE 已验证无 charset/显式 UTF-8 的中文原文与 Unicode escape 等价；现场那次 400 原始字节缺失，根因未确认。 |

已有 HMI 递归查找、精确语言匹配、非目标语言逐字保护、HTTP 单读取任务/内部唯一 ID、迟到响应丢弃、鉴权和默认跳过 SyntaxCheck 均保留。本次没有恢复先前已移除的一键启动/关闭功能。

### 新工具

通过 `FindTools` / `CallTool` 调用以下完整工具名：

| 工具 | 关键参数 / 契约 |
| --- | --- |
| ListHmiScreenPaths | softwarePath, offset=0, limit=100；返回 /组/画面 路径，各名称段使用 URI 转义。 |
| ReadUnifiedHmiButtonEvent | softwarePath, screenPath, buttonName, eventType；返回 ScriptCode、GlobalDefinitionAreaScriptCode、Async 和 token。 |
| DeleteUnifiedHmiButtonEvent | 同上，dryRun=true, expectedToken="", onlyIfEmpty=true；非空脚本默认拒绝。 |
| ReadUnifiedHmiDynamization | softwarePath, screenPath, itemName, propertyName；按精确 PropertyName 定位。 |
| DeleteUnifiedHmiDynamization | 同上，dryRun=true, expectedToken=""；读取不完整时拒绝实际删除。 |
| DeleteEmptyUnifiedHmiScreenGroup | softwarePath, groupPath, dryRun=true；只允许 /组/子组 完整路径，根组/非空组拒绝。 |
| ReadHmiScreenSnapshot | softwarePath, screenPath, maxDepth=6, maxNodes=2000；检查 snapshot.coverage/incomplete/nodes。大响应沿用 GetExport。 |
| ArchiveSavedProject | archivePath 为 .zap20/.zap21 文件名，dryRun=true；先显式保存工程，实际执行指定 dryRun=false。不支持 Archive 的本地/多用户会话明确拒绝。 |

screenPath 可用唯一裸名称；同名画面必须用 ListHmiScreenPaths 返回的完整路径。token 校验用于发现读取后内容变化；实际删除/归档在 TIA ExclusiveAccess 内重新核对状态，获取独占访问失败时不执行。它仍不提供整个批量工作流的事务回滚。实际删除前应停止并行编辑，重新读取；不根据旧报告直接删除事件。

### 本机验证

- 离线回归：416 passed，0 failed，0 skipped；包含生产工具方法、业务状态包装、CallTool 边界和纯逻辑故障注入。
- 最终 V20 / V21 EXE：各 28 项 HTTP/响应路由检查通过；各 18 项实际 EXE HMI 递归断言通过，并核对引用的 TIA API 主版本和 FileVersion。
- V20 / V21 均编译成功。各有 1 条已有 OpcUaLiveReader.cs 空引用警告，0 编译错误。
- 宿主机无 Openness 用户组资格，完整 CLI 启动被已有检查拒绝；工具清单由最终 EXE 反射元数据生成，未伪称现场 tools/list。
- 本轮只尝试读取旧虚拟机的 health；连接被对端关闭，未取得版本证据。没有通过此通道连接/写入/删除工程。
- **V20 真实工程：未验收。V21 真实工程：未验收。MTP Runtime：未验收。归档恢复：未验收。**

逐文件 SHA-256、构建版本和验证阶段见 `manifest/defects-build.json`。本机日志在 `bin-build/defects-*`，不包含在公开运行包中。

### 安装与现场验收前提

1. 在安装对应 TIA V20/V21、Openness、.NET Framework 4.8 且用户已加入 Siemens TIA Openness 组的 Windows/虚拟机中运行。保留现有 HTTP 地址和密钥，用新版完整 runtime/v20 或 runtime/v21 替换运行目录；密钥不随本公开包分发。
2. 先使用专用测试工程或副本，不在业务工程制造缺陷。记录 health.fileVersion=2.7.2.6 和就绪状态，再附加测试工程。
3. 测 Describe 对存在、缺失、空事件集合、非法枚举和失败代理读取均不改变数量、原脚本、Async、IsModified；Ensure/Set 创建仍正常。
4. 三类文本属性分别测试 zh-CN 清空、恢复、保存和关闭重开，逐字核对其它语言。不要用重新附加仍打开的工程代替关闭重开。
5. 用相同编译范围，记录是否增量/重建；检查根成功子错误、警告、超显示上限及重复诊断，保留阶段耗时和 incomplete。
6. 对新建导出文件先试验；对已有文件进行受控失败试验并核对旧 SHA-256。原生归档须在另一路径检索/打开验证后才能作为已验证备份。
7. 事件/动态对象删除先读取和预检，再用当前 token 测试；验证其它事件/绑定/外观不变。非空组必须拒绝删除。报告中的残留事件由实际内容决定是否清理。

当时使用的专用修复构建脚本已退役；当前构建入口见 [发布流程](../development/release-workflow.md)。历史测试结果保持原样。

官方 API 依据：[递归评估编译结果](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/compiling-a-project)、[已保存工程的 Archive/Compressed 契约](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/archiving-and-retrieving-a-project)。同时核对了本机 V20/V21 PublicAPI 元数据。


---

<a id="v2.7.2-asckye.5-multilingual.1"></a>

## WinCC Unified 多语言文本修复（V20 / V21）

修复指定 zh-CN 时可能覆盖其他语言、空字符串无法清空、多语言回读循环序列化，以及 CallTool 外层成功掩盖业务失败的问题。

- 按 `MultilingualTextItem.Language.Culture.Name` 唯一匹配，取消首语言兜底；写后核对目标及非目标语言。
- 省略 `text` 保持；`text: ""` 清空；`null`/非字符串明确失败。
- 新增 `ReadUnifiedHmiTexts` 返回 culture/text 数组；修复 GetObjectProperty 读取 MultilingualText 的序列化。
- CallTool 增加 bridgeSuccess/operationSuccess/operationStatus；业务状态未知时 success=null。
- 仓库同步提供 V20/V21 修复版 runtime，保留现有客户端启动方式。

验证：离线回归 **328 通过、0 失败、0 跳过**；V20/V21 Release 构建均通过。两版各有一条既有 OPC UA 空引用警告；严格包校验和工具引用校验通过。

构建标识：FileVersion `2.7.2.5`，ProductVersion `2.7.2-asckye.5-multilingual.1`。AssemblyVersion 保持 `2.7.2.0`，部署时应核对真实 EXE 路径、文件版本和 SHA-256。

下载 `multilingual-fix-2.7.2-asckye.5-multilingual.1.zip`，选择与 TIA 对应的 v20/v21 目录。包内提供完整构建输出、逐文件 SHA-256 manifest、测试/构建日志和中文部署验收报告。Siemens TIA Portal/Openness 本体不随包分发。

**尚未替换虚拟机 MCP 服务，修复版实际 TIA 验收未执行。发布成功不代表现场服务已修复。** 本轮只读核查确认旧服务仍可复现 Culture.Parent 循环错误。必须完成三语言 before/after 回读、保存重开、HMI 编译及布局/Logo/脚本/PLC 绑定保持检查后，才能确认现场修复。

操作不是事务，失败可能保留部分工程变更；HTML 回读采用严格原文比较，TIA 正规化导致不一致时会报告失败，需按报告核实。部署与回退步骤详见仓库 `docs/archive/multilingual-fix.md`。
