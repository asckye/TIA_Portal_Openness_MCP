# V20 / V21 缺陷与工具缺口核查（2026-09-30）

## 结论和范围

3.0.0 审计发现代码问题和专用工具缺口；后续 3.1.0 已按下列范围补齐本地实现，不能把“464 个工具”或旧覆盖表的“功能类型缺口归零”解释为全部 API 已实现、所有操作已经验收。

用户暂不确定已安装的 Update / Hotfix，要求以根目录 PublicAPI 核对。本轮读取官方更新说明、扫描两版 SDK XML、检查关键调用代码；没有连接 TIA、触发原生交叉引用、运行界面操作或改变工程。引擎基线为 3.0.0，仓库 HEAD `e10355ff2cef5adc9de58014063ab1fd52c6a341`；最初审计只修了脚本和文档；随后用户要求“全部加入”，已完成 3.1.0 实现。下面的问题表保留 3.0.0 基线，最新状态见本节和补充工具指南，尚未部署。

### 3.1.0 实现状态

B1/B2/B3/B4/B6 已修正：统一反射拒绝、真实一致性和未知就绪状态、失败结果不返回部分列表、精确目标组存在性验证、构建生成 lite 计数。B5 加入补丁/格式预检与限制提示，不能由 MCP 修复 TIA 厂商缺陷或把 SDK 版本当作安装补丁。

专用工具补入变量/系统常量交叉引用参数、根/软件单元/安全单元的块范围工具、SINUMERIK 三类工具、SIMOTION、SCADA，以及兼容性和原生日志读取。新增共 10 个工具，详见 [完整用法和验证边界](../reference/v20-v21-audit-tools.md)。

工程操作尚未在用户指定测试工程上验收；默认禁用原生交叉引用，未触碰 AutomaticDipCoatingMachine。面板类型内部创作和索引重建仍没有已核实的公开实现。

### 本地依据

| 项目 | V20 | V21 |
|---|---|---|
| SDK 目录 | `TIA_V20_PublicAPI/V20` | `TIA_V21_PublicAPI/V21/net48` |
| 代表 DLL 文件版本 | `Siemens.Engineering.dll`：`2000.4.401.2` | `Siemens.Engineering.Base.dll`：`2100.0.121.1` |
| XML 文件数（脚本枚举数） | 2 | 18 |
| 扫描到的领域成员 | 4,016 | 4,480 |
| 词法判定已引用 / 仅所属类型 / 未引用 | 2,085 / 576 / 1,355 | 2,241 / 732 / 1,507 |
| 有领域成员的类型 | 1,113 | 1,215 |
| 有引用 / 仅类型名 / 动态登记 / 未触及 | 612 / 92 / 289 / 120 | 671 / 105 / 325 / 114 |

文件版本只描述 SDK 副本，不能据此证明虚拟机已安装哪个补丁。V20 的单体程序集与 V21 的模块化程序集布局不同，XML 文件数量不可直接比较。AddIn 的成员不计入扫描分母。

这些数字是排查线索，不是完成百分比或缺失工具数量：词法扫描会看到注释、字符串及另一编译版本的条件分支；一个同名方法出现在别处也可能满足匹配条件。动态登记也不证明每个属性都读写成功。原始 CSV 和汇总保存在忽略目录 `TiaMcp_Output/audit-20260930/`。

3.0.0 基线静态清单确有 464 个工具，源码 lite 白名单中 59 项均能在清单中找到；未直接暴露的工具可经 `FindTools` / `CallTool` 使用。`package-manifest.json` 的 lite 数量当时是 56，这是元数据漂移（3.1.0 已改为构建生成并校验），不是工具真的少了三个。

## 3.0.0 基线代码问题（3.1.0 修正情况见上）

以下为静态调用链确认的问题；没有为了验证缺陷而在工程上执行危险操作。共享源码问题涉及两版构建，具体原生后果仍取决于 TIA、设备和工程。

| 编号 / 优先级 | 问题与触发条件 | 影响与修正方向 |
|---|---|---|
| B1 / P1 | `InvokeService` 获取任意非 Force 服务后进入 `InvokeOnInstance`。其硬拒绝规则仅检查 Force 和部分 Online/Watch/Monitor 方法，没有接入 `CrossReferenceGuardLogic`；`allowWrite=true` 可允许原生交叉引用方法通过。 | 3.0 的保护只覆盖专用 `GetCrossReferences` 和删除支路，不能视为服务器级封锁。应在目标解析/获取服务之前统一阻断或路由至同一受保护实现。未在本轮实际调用这一旁路。 |
| B2 / P1 | `CheckDownloadReadiness` 只因 PLC 提供 `ICompilable` 就设 `IsConsistent=true`；`Ready` 仅取决于下载服务、配置是否存在及读取异常。 | 未编译/有错误的 PLC 也可能被报告一致且已就绪；这也不证明目标可连接或下载授权已满足。应区分配置检查、编译一致性、连接/许可等状态，未检查应为未知。 |
| B3 / P2 | 交叉引用结果展开及 Children 遍历中吞掉异常并返回已收集的部分/空列表。 | 会把读取失败混同于零引用或完整结果；应明确 `complete/partial/failed`，不能据此判断对象可安全删除。默认禁用降低触发机会，但未修复开启后的行为。 |
| B4 / P2 | `ImportFromDocuments` 的 `verified` 只检查 PLC 根块树中是否存在匹配名称，精确匹配失败后还会使用未加锚点的正则查找；没有核对目标组与导入后的内容。 | 例如查不到 `X` 却找到 `X_backup` 仍可能标记已验证。同名存在也不能证明接口、网络、多语言文本完整。应使用精确目标路径，并明确存在性验证与内容验证的区别。 |
| B5 / P2 | SIMATIC SD 工具和 `Capability.DocumentExport` 主要以主版本 ≥20 判断支持，未体现 V20 Update 4 的语言扩展及官方已知限制。 | 参数通过不代表该安装补丁支持相应语言/文本接口；应提供补丁级能力诊断与格式预检，并按官方限制解释不能无损往返的属性。 |
| B6 / P3 | 包清单 lite 数量为 56，源码实际为 59；旧覆盖表、部分说明仍带历史计数和宽泛“反射可到达全部”表述。 | 不能用这些元数据直接判断工具缺失；应由构建生成并校验数量，收窄覆盖声明。 |

代码位置（行号以本轮未改动的引擎源码为准）：

- B1：[Portal.Helpers.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/Portal.Helpers.cs)，`InvokeOnInstance` 2021、`GetHardDeniedReflectionReason` 2166、`InvokeService` 2334；策略仅在 [Portal.Software.CrossReferences.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/Portal.Software.CrossReferences.cs) 的专用入口使用。
- B2：[Portal.Download.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/Portal.Download.cs)，`CheckDownloadReadiness` 618 起，一致性赋值 663、就绪判定 682。
- B3：[Portal.Software.CrossReferences.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/Portal.Software.CrossReferences.cs)，`TryFlattenCrossReferenceResult` 158 起。
- B4：[McpServer.Documents.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.Documents.cs)，导入后验证 319 起。
- B5：[Capability.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Capability.cs) 和 [McpServer.Documents.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.Documents.cs)。
- B6：[包清单](../../manifest/package-manifest.json)、[lite 白名单](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.Profile.cs)。

## 官方已公布的问题和限制

已修复公告不等于当前安装仍有该故障；需知道实际 Update / Hotfix 才能判断。下面列出与本项目常用流程相关的内容，不声称是 TIA 全部已知问题。

| 版本 / 来源 | 官方说明 | 对本项目的意义 |
|---|---|---|
| [V21 Update 2 Hotfix 1：工程代理稳定性](https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/program-stability-when-using-eigen-engineering-agent?contentId=weBGl8qnaL8w6cK066BWiA) | 特定场景下通过 Eigen Engineering Agent 导入、编译块导致 TIA 崩溃，公告称已修复。 | 与自动化导入/编译流程相关，值得核对安装补丁；不能因此认定修复了本次 PLC 交叉引用退出。 |
| [V21 Update 2 Hotfix 1：编译时网络误删](https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/deletion-of-invalid-networks-during-compilation?contentId=8tEOhRXS6hFVJ5IOOpT~CQ) | 特定的错误块调用只部分修正后，编译可能误删网络；公告称已修复。 | 编译调用本身不是绝对无副作用的验证；需要补丁及内容回读证据。 |
| [V21 Update 2：Unified Engineering](https://docs.tia.siemens.cloud/r/en-us/v21-updates/tia-portal-updates-readme/improvements-in-wincc-unified/unified-engineering/improvements-in-update-2?contentId=PqQW8IprsyNkxyDG_TV2Kw) | 改善编辑语言切换后的画面显示、修改动态属性时多选对象变量保持、SiVArc 安装后项目显示等。 | 属于官方产品修复，不应归咎于 MCP 参数或把界面故障硬绕过。 |
| [V20 Update 4：STEP 7 / SIMATIC SD](https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-step-7/improvements-in-update-4) | 扩展 SCL、FBD 和混合网络 SD 往返；同时提示 SCL 文本接口导入可能丢数据。旧工程/早期版本编辑的多语言注释 ID 不一致可能使导出失败，给出 SimaticSdEnabler 修复办法。 | 本项目已有 SD 导入导出，缺的是补丁/语言/内容完整性诊断。原始格式还存在 OB 类型等往返限制，不能宣称 SD 等价于完整工程备份。 |
| [V20 Update 4：Unified Engineering](https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-wincc-unified/unified-engineering/improvements-in-update-4) | 改善面板实例删除、自定义样式升级、嵌套面板授权继承等。 | 应先核对补丁和设备版本，再判断为 MCP 缺陷。 |

V21 有一次重大程序集重构，必须以 V21 程序集重新构建，V21 构建不能直接运行于 V20；本项目分别构建两版的方式正确。[官方 V21 兼容说明](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21)

## 3.0.0 基线缺少的专用能力（本轮已补入可实现项）

“没有专用工具”与“官方没有 API”不同；通用反射也不能代替参数校验、对象选择和结果验证。

| 项目 | 已核实状态 | 范围与优先级 |
|---|---|---|
| PLC 变量、系统常量的专用交叉引用查询 | 现有 `GetCrossReferences` 只接受 Block / Type；变量表删除仍向表本身请求服务，不能替代逐变量查询。 | 两版候选；先修 B1/B3 和诊断流程，再考虑扩展，不能为补工具而重新开放原生风险。 |
| 软件单元内块的统一查找、导入导出与覆盖诊断 | `ReadPlcSoftwareUnits` 已能读单元及部分内容名称，部分深层工具有 unit 参数；但 `GetBlocks` 与 `GetPlcBlockGroupByPath` 从根 `PlcSoftware.BlockGroup` 出发，未接入单元树。 | 两版；这是既有工具的范围缺口，不是完全没有软件单元功能。应统一带单元身份的目标路径和查找规则。 |
| SINUMERIK 归档/恢复、F 地址分配归档 | V20 SDK 的 `SinumerikArchiveProvider` 存在，未找到专用封装。 | V20；需对应 NCU / 产品环境。 |
| SINUMERIK 报警文本、Safety Integrated 模式 | V20 的 `AlarmTextImporter` / `SinumerikAlarmTextProvider` / `SafetyModeProvider` 存在，未找到专用封装。 | V20；与普通 PLC Safety 工具不是同一个对象模型。官方模式切换还有离线及报文配置条件。[官方说明](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-sinumerik-840d-sl/code-example/activating-safety-integrated?contentId=LmS5hjX7TVS0iGHGPZBsOQ) |
| SIMOTION 初始化 | V20 `SimotionProvider.Initialize` 存在，未找到专用封装。 | V20；需要相应设备/选件验收。 |
| Runtime Professional 数据导出 | V20 `ScadaExportProvider.Export` 存在，未找到专用封装。 | V20；与已有经典 HMI 画面导出不是同一能力。 |
| 原生调用阶段日志、补丁级能力与已知问题诊断 | 有 `Doctor`、`ReadPortalInfo`、工具级调用日志；缺少统一关联原生调用阶段和补丁限制的诊断。 | 两版，应优先于低频选件扩展。 |

以上五个 V20 核心候选类型（SinumerikArchiveProvider、AlarmTextImporter、SafetyModeProvider、SimotionProvider、ScadaExportProvider）额外使用 .NET Framework 反射只读加载确认 `IsPublic=true`，没有实例化或调用原生服务。现有 V21 SDK 副本中未检出这些同名类型；这不证明其他产品包没有对应能力，不能直接把 V20 封装标为 V21 通用。

Unified 面板类型内部控件/脚本编辑、从零新建类型，以及重建交叉引用信息，仍未在现有 PublicAPI 中核实可行的公开纯 Openness 路径。不能为满足工具数而添加无法兑现的入口。已有面板容器和普通画面脚本工具不等于类型内部编辑，详见 [面板与交叉引用范围核查](cross-reference-and-faceplate-github-20260929.md)。

## 验证缺口与建议顺序

两版编译和 API 形状检查记录不等于两版功能实测；历史实机记录主要来自 V21，且部分只验证了 NotSupported / 拒绝路径。经典 HMI、软件单元深层、R/H、SafetyValidation、Teamcenter、不同设备版本仍需适合的工程和选件，不能只看工具调用次数判断覆盖。

建议顺序：B1 统一交叉引用门控 → B2 下载检查语义 → B3/B4 结果与内容验证 → B5 补丁级诊断 → 软件单元统一范围 → 按实际设备需求增加 V20 专用选件工具。原生实测只在用户指定的测试工程副本进行。

最初审计脚本原先在 Windows PowerShell 5.1 绑定默认参数时因 `$PSScriptRoot` 为空而失败；已将默认动态登记表路径移到脚本体内解析，显式空值关闭登记的语义保留。V20/V21 扫描均完成；该次扫描没有修改引擎。后续 3.1.0 实现仍未发布。

验证：两版分别以默认登记表参数重新运行审计成功；仓库文档/入口检查通过（185 个 Markdown 文件，0 问题），`git diff --check` 通过。这是最初只读审计的记录；3.1.0 的完整构建及回归记录见 `manifest/release-build.json`。始终未运行原生工程操作。
