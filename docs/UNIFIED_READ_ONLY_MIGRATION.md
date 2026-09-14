# WinCC Unified 只读迁移采集（V21，v2.7.5 / FileVersion 2.7.5.0）

文档中的 `Example_Project`、`HMI_1`、模块名称和库路径都是示例，调用时替换为目标工程的实际值。
本次新增接口只读取工程；不保存、编译、下载、创建事件、实例化库类型、写变量或关闭博途。原生导出只写入服务进程的独立临时目录，读取后删除；不会覆盖用户文件。

## 接口分组

| 组 | 接口 | 作用 |
|---|---|---|
| 全局脚本 | `ListUnifiedGlobalScripts` | 分页列出模块；每条明确 `bodyReadSuccess=false`，不冒充正文 |
| 全局脚本 | `ReadUnifiedGlobalScript` | 按准确模块名原生导出 JS/YAML，返回原文、SHA-256、函数、参数、正文、全局语句、导入和引用证据 |
| 变量 | `ReadUnifiedTagDefinitions` | 连接定义、所有分组/表、根变量、结构成员、数组实际成员，跨页保留遍历位置 |
| 面板/库脚本 | `ReadUnifiedLibraryType` | 只读指定 `/文件夹/类型` 和准确版本，查询该类型的 `GetSupportedExportFormats()` 后导出；保留官方诊断、文件清单和导出完整性，不进入编辑模式 |
| 面板实例 | `ReadUnifiedFaceplateInstance` | 读取页面实例与接口传值，验证实例库版本 GUID 与指定版本一致后读取内部原生定义；不能验证就返回缺口 |
| 库定位 | `ListUnifiedLibraryFolder` | 只列一个明确文件夹的直属目录、类型及版本；调用方选择下一层，不扫描整个库 |
| 页面分支 | `ReadUnifiedScreenBranch` | 从准确页面/对象进入属性、属性成员、索引或同名键指定的集合成员；递归返回路径记录 |
| 续采清理 | `ReleaseUnifiedReadCursor` | 丢弃采集游标和临时文件，不操作工程生命周期 |

默认精简工具列表通过 `FindTools` + `CallTool` 使用这些接口。所有采集接口都必须传 `expectedProject` 和 `softwarePath`，防止读错工程或 PLC。

## 结果含义与续采

返回 `meta` 包括 `scope`、`collectionId`、`apiCallSuccess`、`dataComplete`、`traversalComplete`、`expectedCount`、`actualCount`、`cumulativeCount`、`truncated`、`failures`、`failureCount`、`nextCursor`、`records`。

- `apiCallSuccess` 表示本次采集请求正常处理；字段失败/不支持仍能正常返回记录。请求错误或遍历异常中止时为 false。
- `dataComplete` 只在遍历结束且累计无失败/不支持项时为 true。它针对 **scope 中选定的读取范围**，不表示整个工程、所有依赖或运行时表达式已经解析。
- `actualCount` 的单位是证据记录，**不是变量数量**。`kind=tag` 才是一条变量/成员，`tagAlias` 是变量表与根集合的同一变量关联。每个集合另带 `expectedCount` 和结束记录。全局预计记录数在遍历前未知，返回 null 并说明原因，不用调用方提供的预期数量 冒充实际测量。
- 将所有页面的 `records`、`failures` 保存后汇总。最后一页 `failureCount` 是累计值，但 `failures` 仅列该页失败项；不要只保存最后一页。
- 首次 `cursor=""`。后续原样保留范围参数，只将 `cursor` 改为 `nextCursor`。最近一页可以使用其 `pageCursor` 重放；更早页游标报明确错误，不能当作新采集继续拼接。
- 游标在服务内存中。最多 16 个未完成采集，闲置 30 分钟后失效；完成后立即释放工程迭代器，不再占用未完成采集名额。独立保留最多 32 个已完成集合的末页供重放，闲置 10 分钟或容量不足时按最近使用顺序回收。工程切换、服务重启后同样失效，不得与新采集混合。
- 将所有页面保存完成后调用 `ReleaseUnifiedReadCursor`，传入末页的 `releaseCursor`；放弃未完成采集时也可使用该字段释放。`nextCursor=null` 表示遍历结束，不等于无限期保存历史页。最近完成页在重放缓存内可用 `pageCursor` 重试；失效会明确报错，不会静默重读。
- `pageSize=1..500`，`budgetMs=50..20000`。时间预算在同步 API 调用之间检查；不能承诺中断一个已经阻塞的 Openness 调用。无需生成并行后台读取任务。
- 当前工程是实时读取，**不是原子快照**。采集期间不要编辑；集合数量变化会记为失败。数量不变的编辑无法全部检测，必须重采受影响范围。
- 大页面仍可能触发现有 `GetExport` 分块机制：先按照 `exportId/offset` 取全 JSON 文本并解析，再取其中的 `nextCursor`。文本分块游标和采集游标是两层机制。

## 调用示例

`CallTool` 的 `argumentsJson` 使用下列 JSON 字符串。示例范围外的对象不会被遍历。

```json
{"softwarePath":"HMI_1","expectedProject":"Example_Project","moduleName":"Module_A","pageSize":50,"budgetMs":5000}
```

对应 `ReadUnifiedGlobalScript`。随后依次读取 `Module_B`、`Module_C`、`Module_D`、`Module_E`。`LibraryModule_V_1_0_0` 若不在该 HMI 的 Scripts 中，返回缺口；使用明确的库文件夹定位实际类型/版本后调用 `ReadUnifiedLibraryType`，不能去掉版本后任取默认类型。

```json
{"softwarePath":"HMI_1","expectedProject":"Example_Project","pageSize":100,"budgetMs":5000,"cursor":""}
```

对应 `ReadUnifiedTagDefinitions`。每个成员 `own` 保存自身 API 返回值；`originBasis=inferredFromRoot` 和 `inheritedFrom` 单独说明来源推导。不会将根 PLC 地址或符号拼接后伪装成成员自身地址。无来源证据的连接是 `unresolvedConnection`。

```json
{"softwarePath":"HMI_1","expectedProject":"Example_Project","screenPath":"/实际分组/实际页面","itemName":"实际对象名","branchJson":"[{\"property\":\"Interface\"},{\"name\":\"实际接口属性名\",\"key\":\"PropertyName\"},{\"property\":\"Dynamizations\"}]","pageSize":100,"budgetMs":5000}
```

对应 `ReadUnifiedScreenBranch`。`screenPath` 每个名称段按 URI 编码，以 `ListHmiScreenPaths` 实际结果为准；示例中的中文占位内容不是工程中的已确认名称。`branchJson=[]` 读取整个选中对象；可以使用 `{"index":0}` 定位无名称集合成员；禁止方法调用和 Parent 向上跳出范围。

## 原始数据与边界

变量来源识别接受 `Connection` 的准确 `<内部变量>` 标记（比较时忽略首尾空白，原始文本保持不变），`origin=internal`、`originBasis=ownConnectionMarker`。成员没有自身来源信息时，根来源推导仍标记为 `inferredFromRoot`；显式成员标记优先于根推导。内部标记与 PLC 名称/符号冲突时 `classificationStatus=conflictingEvidence`，保留字段并要求人工核对，不制造 PLC 地址。

每条 `tagSource` 分别返回 `definitionFieldCount`、`expectedDefinitionFieldCount=17`、`definitionFieldsComplete` 与 `classificationComplete` / `classificationStatus`。分页摘要的 `readComplete`、`readFailureCount` 与 `classificationComplete`、`classificationFailureCount` 分开统计；来源未识别不等于定义字段未返回。`failureCount` 仍是两类缺口总数，`dataComplete` 仍要求读取和分类均无缺口，翻页中不得提前报告完整。

脚本使用 Esprima 3.0.5 解析，**不执行**。解析失败仍保留 `rawText`，并将 `status` 设为 unsupported。函数声明、函数表达式和箭头函数均有原始文本及 UTF-16 起止偏移。`globalDefinitions` 是函数声明外的原始顶层语句，不会声称从中还原了编辑器不可见的额外定义区。注释和空白完整保留在模块 `rawText` 中。

`Tags("确切名称")` 返回 `literalCandidateRequiresExactNameMatch`；`Tags(prefix + suffix)` 保留原始表达式并返回 `runtimeExpressionUnresolved`。字符串是候选引用，须与实际变量名逐项匹配；别名、eval、反射式调用不会冒充已经完整静态展开。`runtimeResolutionComplete=false`。

面板原生 YAML/XML 的属性、接口类型、对象、动态属性、事件和依赖以实际文件路径、节点路径和原文返回。实例与库版本的关系必须由官方 `LibraryTypeInstanceInfo` 的 GUID 证据确认；若 API 对该对象不提供服务，就无法证明关系，不进行默认版本回退。原生定义值仍需按接口准确名称建立内部引用关系；本版本不把未经核实的自动匹配称为完整追溯。

库导出先查询所选类型的官方格式列表，使用 API 返回的第一个格式，原样记录在 `nativeExportPlan.supportedFormats` / `selectedFormat`；不向库脚本强传 WinCCML，也不尝试编辑、实例化或转换类型。无支持格式时明确返回 `Unsupported`。`ExportAsDocuments` 使用 `LibraryExportOptions.None`，只导出领域文件。

先读取 `ExportTransferResult.TransferResultState`，按 V21 官方示例仅在非 Success 结果下访问 `Messages[*].Message`。Success 的 `diagnosticsStatus=notRequiredOnSuccess` 表示成功路径不要求枚举诊断，不能解读为枚举到了零条。全部结果检查在返回首条导出记录前完成或停止；`nativeExportPlan` 出现在分页中不表示尚未执行导出。

立即消费 `ExportedDocuments` 并把可能属于 .NET Remoting 代理的 FileInfo 转成本地路径字符串，后续只读取本次独立临时目录及子目录，不再跨页访问远程文件对象。任何结果检查异常均停止继续访问远程结果，不自动重试导出、不重连、不保存、不关闭项目或 Portal。已落盘文件仍可回收为部分证据。

`nativeExportStatus` 描述原生调用和结果检查，`apiCallSuccess` 表示原生方法是否返回，`nativeState` 保留原始状态，`resultInspectionComplete` 单独报告检查完整性；`failurePhase` 给出失败阶段。例如原生返回 Success 后读取文件清单失败，原始 Success 仍保留，但明确标注结果检查失败，不能据此宣布完整。该阶段 `dataComplete=null`，文件与正文完整性以末尾 `nativeExportSummary.dataComplete` 为准。Warning、空导出、读取/解析失败、脚本没有 JS 正文均不完整。

原生异常保留 `operationId`、`phase`、异常类型、HResult、原文与调用栈；响应中长异常文本有 `textTruncated` 标志，超过八层异常有 `exceptionChainTruncated` 标志。完整异常及每个远程读取阶段写入 `%TEMP%\TiaMcpServer.native-export.log`，不记录连接密钥或脚本正文。`connectionUnavailable` 表示观察到对象释放或远程通信异常，不能仅凭它断言博途已退出或确定根因。

同一绑定项目的续页和末页重放按游标范围及项目对象身份校验，不重复读取远程项目名称或重新定位 HMI，因此已采集的原生文件可在句柄失效后继续回收。需要远程对象的未完成遍历仍会明确失败；新建采集仍验证实际项目名称。重新绑定，即使名称相同，也会令旧游标失效，不能把新旧集合混合为同一快照。

`nativeFileInventory` 保留清单数量与实际相对路径。缺失文件、目录外文件、重解析点均报缺口，不读取任意目录。正文和 SHA-256 来自同一次有大小上限的文件读取，不把哈希错误或文件消失变成静默丢失。库内绑定和迁移关系仍须独立验证。

数组遍历官方 `Members` 实际集合，不根据长度制造元素。`DataType` 若包含官方返回的 `Array[a..b,c..d] of ...` 文本，则标记为从该原始文本解析的上下界；若未暴露范围，返回明确缺口。不能将成员数推断成从零开始的上下界。

边界：对象深度 128，准确分支段数 64；无 Find 的命名查找最多 10000 个元素，库版本查找最多 1000；直接库依赖最多 1000；单个原生文件目前最多 1 MiB，超过则明确失败，不返回被裁掉的正文。原生库格式不支持、访问保护、字段读取失败、无法索引的集合均作为缺口返回。

## 验收与当前状态

旧版用户实测发现：有些全局脚本正文和变量定义字段已经成功读取，但内部连接标记未被识别；不能把分类失败解释为定义未读到。库类型曾出现 Warning、空导出，以及脚本不支持固定 WinCCML 格式的问题。具体工程信息、采集结果和原始证据不随公开包分发。

v2.7.4 增加分类修复、按类型查询格式、导出诊断与文件清单、完成缓存回收和资源发现接口。离线回归与真实编译后服务入口测试只证明本地代码和协议行为；**未在真实博途工程上完成本版复测，不证明库内部文件已成功取得。**

v2.7.5 修正上述结果读取和分页生命周期并补充阶段日志。已在真实编译的 .NET Framework 4.8 程序中模拟 FileInfo 透明代理及 IPC 失效；这不是实际博途导出验收，也不足以证明异常退出已解决。

如博途无弹窗退出，在虚拟机完整包目录的 CMD 执行 `scripts\Collect-TiaExitEvidence.cmd`，只读提取最近 24 小时内最多 100 条应用崩溃/.NET/WER 事件、当前博途进程列表和本版原生导出日志。输出位置会显示在控制台；空事件不等于没有异常。先在本地检查后再分享，日志和事件可能包含私有路径。该命令不连接、启动或关闭博途。

部署后仍需逐项验收：

1. 检查 `<内部变量>` 分类、成员自身字段与根来源继承，分别核对根数量与含成员总数。连续完成超过 16 个集合后继续采集，验证保存后释放和末页重放。
2. 按实际库路径和准确版本读取库脚本，验证返回的支持格式及原生 JS/YAML 正文。无正文仍为缺口。
3. 只对页面实际引用的准确面板版本逐一导出，保存非成功结果的 `ExportResult/Messages` 及文件清单。若官方 API 仍不支持，保留明确缺口；接口传值、内部绑定及嵌套依赖需逐项核对。
4. 补读旧页面快照的属性事件、阈值、报警列、工具栏及触发变量。
5. 将全局脚本静态引用对应到调用页面，建立页面到全局函数调用链；运行时拼接名称保留原表达式和待解析状态。不能由接口返回成功推断迁移全部核对通过或可原生导入。

官方 API 依据：

- [V21 全局脚本导出](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/export-of-global-script)
- [V21 全局脚本 JS/YAML 格式](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/description-of-global-script)
- [V21 变量及 Members 定义](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/tags/accessing-tag-properties)
- [V21 库类型版本](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-type-versions)
- [V21 查询所选库类型的支持格式](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/retrieving-supported-export-formats-for-library)
- [V21 库版本导出、诊断及返回文件清单](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/exporting-library-type-version-as-document)

第三方解析器许可证：[Esprima BSD-3-Clause](licenses/Esprima-3.0.5.txt)。
