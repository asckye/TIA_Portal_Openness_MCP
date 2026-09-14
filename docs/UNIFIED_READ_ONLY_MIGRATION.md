# WinCC Unified 只读迁移采集（V21，v2.7.3 / FileVersion 2.7.3.0）

目标工程：`SiCar_StartUp_V50_V21`，HMI：`HMI_RT_2`。
本次新增接口只读取工程；不保存、编译、下载、创建事件、实例化库类型、写变量或关闭博途。原生导出只写入服务进程的独立临时目录，读取后删除；不会覆盖用户文件。

## 接口分组

| 组 | 接口 | 作用 |
|---|---|---|
| 全局脚本 | `ListUnifiedGlobalScripts` | 分页列出模块；每条明确 `bodyReadSuccess=false`，不冒充正文 |
| 全局脚本 | `ReadUnifiedGlobalScript` | 按准确模块名原生导出 JS/YAML，返回原文、SHA-256、函数、参数、正文、全局语句、导入和引用证据 |
| 变量 | `ReadUnifiedTagDefinitions` | 连接定义、所有分组/表、根变量、结构成员、数组实际成员，跨页保留遍历位置 |
| 面板/库脚本 | `ReadUnifiedLibraryType` | 只读指定 `/文件夹/类型` 和准确版本，调用官方 `ExportAsDocuments(..., "WinCCML", None)`；不进入编辑模式 |
| 面板实例 | `ReadUnifiedFaceplateInstance` | 读取页面实例与接口传值，验证实例库版本 GUID 与指定版本一致后读取内部原生定义；不能验证就返回缺口 |
| 库定位 | `ListUnifiedLibraryFolder` | 只列一个明确文件夹的直属目录、类型及版本；调用方选择下一层，不扫描整个库 |
| 页面分支 | `ReadUnifiedScreenBranch` | 从准确页面/对象进入属性、属性成员、索引或同名键指定的集合成员；递归返回路径记录 |
| 续采清理 | `ReleaseUnifiedReadCursor` | 丢弃采集游标和临时文件，不操作工程生命周期 |

默认精简工具列表通过 `FindTools` + `CallTool` 使用这些接口。所有采集接口都必须传 `expectedProject` 和 `softwarePath`，防止读错工程或 PLC。

## 结果含义与续采

返回 `meta` 包括 `scope`、`collectionId`、`apiCallSuccess`、`dataComplete`、`traversalComplete`、`expectedCount`、`actualCount`、`cumulativeCount`、`truncated`、`failures`、`failureCount`、`nextCursor`、`records`。

- `apiCallSuccess` 表示本次采集请求正常处理；字段失败/不支持仍能正常返回记录。请求错误或遍历异常中止时为 false。
- `dataComplete` 只在遍历结束且累计无失败/不支持项时为 true。它针对 **scope 中选定的读取范围**，不表示整个工程、所有依赖或运行时表达式已经解析。
- `actualCount` 的单位是证据记录，**不是变量数量**。`kind=tag` 才是一条变量/成员，`tagAlias` 是变量表与根集合的同一变量关联。每个集合另带 `expectedCount` 和结束记录。全局预计记录数在遍历前未知，返回 null 并说明原因，不用用户提供的 112 冒充实际测量。
- 将所有页面的 `records`、`failures` 保存后汇总。最后一页 `failureCount` 是累计值，但 `failures` 仅列该页失败项；不要只保存最后一页。
- 首次 `cursor=""`。后续原样保留范围参数，只将 `cursor` 改为 `nextCursor`。最近一页可以使用其 `pageCursor` 重放；更早页游标报明确错误，不能当作新采集继续拼接。
- 游标在服务内存中，闲置 30 分钟、工程切换、服务重启后失效；重启后应新建采集，不能与旧快照混合。最多 16 个采集，完成或放弃后可释放。
- `pageSize=1..500`，`budgetMs=50..20000`。时间预算在同步 API 调用之间检查；不能承诺中断一个已经阻塞的 Openness 调用。无需生成并行后台读取任务。
- 当前工程是实时读取，**不是原子快照**。采集期间不要编辑；集合数量变化会记为失败。数量不变的编辑无法全部检测，必须重采受影响范围。
- 大页面仍可能触发现有 `GetExport` 分块机制：先按照 `exportId/offset` 取全 JSON 文本并解析，再取其中的 `nextCursor`。文本分块游标和采集游标是两层机制。

## 调用示例

`CallTool` 的 `argumentsJson` 使用下列 JSON 字符串。示例范围外的对象不会被遍历。

```json
{"softwarePath":"HMI_RT_2","expectedProject":"SiCar_StartUp_V50_V21","moduleName":"Navigation","pageSize":50,"budgetMs":5000}
```

对应 `ReadUnifiedGlobalScript`。随后依次读取 `Colors`、`General`、`Changelog`、`Alarmline`。`LSicar_GeneralScripts_V_5_0_0` 若不在该 HMI 的 Scripts 中，返回缺口；使用明确的库文件夹定位实际类型/版本后调用 `ReadUnifiedLibraryType`，不能去掉版本后任取默认类型。

```json
{"softwarePath":"HMI_RT_2","expectedProject":"SiCar_StartUp_V50_V21","pageSize":100,"budgetMs":5000,"cursor":""}
```

对应 `ReadUnifiedTagDefinitions`。每个成员 `own` 保存自身 API 返回值；`originBasis=inferredFromRoot` 和 `inheritedFrom` 单独说明来源推导。不会将根 PLC 地址或符号拼接后伪装成成员自身地址。无来源证据的连接是 `unresolvedConnection`。

```json
{"softwarePath":"HMI_RT_2","expectedProject":"SiCar_StartUp_V50_V21","screenPath":"/实际分组/实际页面","itemName":"实际对象名","branchJson":"[{\"property\":\"Interface\"},{\"name\":\"实际接口属性名\",\"key\":\"PropertyName\"},{\"property\":\"Dynamizations\"}]","pageSize":100,"budgetMs":5000}
```

对应 `ReadUnifiedScreenBranch`。`screenPath` 每个名称段按 URI 编码，以 `ListHmiScreenPaths` 实际结果为准；示例中的中文占位内容不是工程中的已确认名称。`branchJson=[]` 读取整个选中对象；可以使用 `{"index":0}` 定位无名称集合成员；禁止方法调用和 Parent 向上跳出范围。

## 原始数据与边界

脚本使用 Esprima 3.0.5 解析，**不执行**。解析失败仍保留 `rawText`，并将 `status` 设为 unsupported。函数声明、函数表达式和箭头函数均有原始文本及 UTF-16 起止偏移。`globalDefinitions` 是函数声明外的原始顶层语句，不会声称从中还原了编辑器不可见的额外定义区。注释和空白完整保留在模块 `rawText` 中。

`Tags("确切名称")` 返回 `literalCandidateRequiresExactNameMatch`；`Tags(prefix + suffix)` 保留原始表达式并返回 `runtimeExpressionUnresolved`。字符串是候选引用，须与实际变量名逐项匹配；别名、eval、反射式调用不会冒充已经完整静态展开。`runtimeResolutionComplete=false`。

面板原生 YAML/XML 的属性、接口类型、对象、动态属性、事件和依赖以实际文件路径、节点路径和原文返回。实例与库版本的关系必须由官方 `LibraryTypeInstanceInfo` 的 GUID 证据确认；若 API 对该对象不提供服务，就无法证明关系，不进行默认版本回退。原生定义值仍需按接口准确名称建立内部引用关系；本版本不把未经核实的自动匹配称为完整追溯。

数组遍历官方 `Members` 实际集合，不根据长度制造元素。`DataType` 若包含官方返回的 `Array[a..b,c..d] of ...` 文本，则标记为从该原始文本解析的上下界；若未暴露范围，返回明确缺口。不能将成员数推断成从零开始的上下界。

边界：对象深度 128，准确分支段数 64；无 Find 的命名查找最多 10000 个元素，库版本查找最多 1000；直接库依赖最多 1000；单个原生文件目前最多 1 MiB，超过则明确失败，不返回被裁掉的正文。原生库格式不支持、访问保护、字段读取失败、无法索引的集合均作为缺口返回。

## 验收与当前状态

截至本次开发：连接到实际工程，已独立读取确认 `Scripts.Count=5`、`Tags.Count=112`，并核对根变量名称列表。171 页面、2648 对象、62 PLC/50 内部变量以及 27 种面板来自需求提供的既有结果，本次未重新核验这些计数。

虚拟机现有服务对 `ReadUnifiedGlobalScript` 返回 `No tool named`，因此新接口的真实工程验收尚未执行。离线与 EXE 测试不能替代此项。

待在虚拟机运行新版本后逐项关闭的缺口：

1. 五个模块的原生 JS 正文、函数、参数、全局区与导入读取及原文对照。
2. `LSicar_GeneralScripts_V_5_0_0` 的实际库位置、准确版本与正文来源。
3. 所有变量/成员/数组的末页、集合数量核对、字段失败清单、112 根变量及 62/50 来源重新对账。
4. 页面引用的 27 种准确面板版本：接口传值、内部对象绑定及嵌套依赖的证据闭合。
5. 页面剩余属性事件、阈值、报警列、工具栏及触发变量逐一补读。
6. 静态变量名候选逐条匹配；动态表达式保持未解析清单。没有这些检查，不应宣布迁移数据完整。

官方 API 依据：

- [V21 全局脚本导出](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/export-of-global-script)
- [V21 全局脚本 JS/YAML 格式](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/description-of-global-script)
- [V21 变量及 Members 定义](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/tags/accessing-tag-properties)
- [V21 库类型版本](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-type-versions)

第三方解析器许可证：[Esprima BSD-3-Clause](licenses/Esprima-3.0.5.txt)。
