# ADR 0033：统一工具参数形式并保留一代别名

- 状态：已采纳
- 日期：2026-11-04
- 依据：DES-4-P1；维护者于 2026-11-04 批准 D1–D6
- 适用：V14 SP1、V15.1、V16–V21，lite 与 full 使用同一契约

## 背景

相同的 `exportPath` 有时指目录、有时指文件；设备路径既有字符串又有数组，模型无法仅凭名字选择输入。DES-PORT A/B/C 已统一存活工具和预览契约，OUTROOT-1 已使包外文件写入经过审批。本决定只统一宿主参数边界，不改变 Siemens 调用顺序、参数语义或线程。

## 决定

1. 参数名用 camelCase。文件系统路径是 **TIA 宿主机上的绝对路径**，文件与目录分别命名为 `exportFile`、`exportDirectory`；输入目录为 `sourceDirectory`。相对路径不能作为这些规范参数的值。路径不得有首尾空白。
2. 工程对象路径不是文件系统路径。PLC `blockPath`、`groupPath`、`folderPath`、`objectPath` 保留现有相对所选软件或软件单元根的规则；例如 `Main` 合法。`softwarePath` 保留发现工具返回的标识。HMI 对象仍遵循现行绝对对象路径及转义规则。
3. `devicePath` 的规范形式为名称数组：根设备 `["Station"]`，用户组 `["Group", "Subgroup", "Station"]`，未分组设备 `["$ungrouped", "Station"]`。数组元素是原始精确名称，顺序不可重排。原先的七个字符串入口接受数组，并保留原字符串的解析语义；字符串输入产生弃用警告。其余数组入口不新增字符串形式。对象是否存在、歧义与候选项由设备解析器处理。
4. `expectedProject` 是**项目名称**，`expectedProjectFile` 是**绝对项目文件路径**。两者不互为别名；读取保留名称断言，写入使用文件身份。保留 `projectPath`、`newProjectPath`、`archivePath`、`libraryPath`、`filePath`、`inputPath`、`importPath` 等已有专用路径名；对同时作为输入与输出的操作参数也保留其既有意义。它们不是本次导出别名的隐式替换规则。
5. `expectedPlanHash` 是该工具预览的计划摘要；`expectedInventoryHash`、`expectedPackageHash`、`expectedDocumentHash` 继续表示各自对象的摘要，`previewToken` 继续表示一次性令牌。不同类别不能互换，不引入 `expectedToken`。
6. 本次新增的可选规范参数通过省略表示未提供，schema 不声明 `""` 默认值；显式 `null`、空字符串和相对路径不合法。内部实现可继续接收旧的缺省值；未改名的可选字符串不作全库迁移（API-35）。
7. 以下别名保留至 5.0。规范名与同组别名不能同时提供，即使值相等也拒绝；不猜测优先级。使用旧名时保留旧语义，并在 `meta.warnings` 返回 `DEPRECATED_PARAMETER`，含 `parameter` JSON pointer、`replacement` 和 `removalVersion: "5.0"`。工具名称不变。

## 路径别名表

| 工具 | 旧名（至 5.0） | 规范名 |
|---|---|---|
| `ExportPlcBlock`、`ExportPlcBlocks`、`ExportPlcType`、`ExportPlcTypes`、`ExportPlcBlockDocuments`、`ExportPlcBlocksDocuments` | `exportPath` | `exportDirectory` |
| `ExportPlcTagTable`、`ExportPlcWatchTable`、`ExportTechnologyObject`、`ExportAlarmClasses`、`ExportAlarmTextLists`、`ExportAlarmInstanceTexts`、`ExportHmiScreen`、`ExportHmiTagTable`、`ExportHmiConnection`、`ExportOpcUaInterface`、`ExportDeviceAml` | `exportPath` | `exportFile` |
| `ExportDeviceAml` | `exportPath` | `exportDirectory` |
| `BuildDeviceAmlDocument`、`GenerateOpcUaModelledInterface`、`PatchPlcBlockDocument`、`WritePlcSclSourceFile`、`RenderPlcBlock`、`RenderPlcProgramAtlas`、`RenderPlcVisualDiff`、`SaveExportContent`、`ManageStandardPackage`、`ManageMachineDescription`、`RenderPlcBlockDocument`、`GeneratePlcDocumentation`、`ManageUnifiedCwcPackage` | `outputPath` | `exportFile` |
| `ManageMachineDescription` | `outputPath` | `exportDirectory` |
| `GenerateAcceptanceReport`、`GenerateErrorReport`、`WriteClassicHmiMinimalPackageFiles`、`BuildReleaseHandoffArtifacts`、`InstantiatePlcTemplates` | `outputDirectory` | `exportDirectory` |
| `ExportHmiProgram`、`ExportTechnologyObjectsToDirectory` | `exportDir` | `exportDirectory` |
| `ExportPlcWatchTablesToDirectory` | `dir` | `exportDirectory` |
| `ImportPlcBlocksFromDirectory`、`ImportHmiScreensFromDirectory`、`ImportHmiTagTablesFromDirectory`、`ImportPlcTagTablesFromDirectory`、`ImportTechnologyObjectsFromDirectory` | `dir` | `sourceDirectory` |
| `AnalyzePlcReferences`、`ScanPlcSourceAnnotations`、`GeneratePlcDocumentation`、`ManageUnifiedCwcPackage` | `directory` | `sourceDirectory` |

`ExportDeviceAml.exportPath` 原先允许文件或既存目录，新形式显式选择 `exportFile` 或 `exportDirectory`，两者互斥；目录形式仍用设备名生成 AML 文件名。`ExchangeUnifiedTags` 和 `ExchangeUnifiedScriptModules` 分别用 `exportDirectory` 导出、`sourceDirectory` 导入；`directory` 保留为旧别名。

`ManageMachineDescription.outputPath` 按动作与格式表示 JSON 文件或 CSV 目录：CSV 导出及模板导出使用 `exportDirectory`；导入后的规范化 JSON 输出（无论输入为 JSON 或 CSV）和 JSON 导出使用 `exportFile`。`WritePlcSclSourceFile.outputPath` 保留旧的文件/目录推断，规范 `exportFile` 必须指定带扩展名的文件。`ExportPlcBlock` 当前存活实现以目录为目标，规范名为 `exportDirectory`。文档导出参数指文档集目录。保留的旧别名绝不依据新名字重新解释。

## 缺省导出位置

默认位置为 `<OutputRoot>/<session>`，其中 `<session>` 是宿主生成的 GUID，`OutputRoot` 由现有 `DataLocations.OutputRoot` 决定：正常安装在包内，开发布局、显式 `TIA_MCP_DATA_DIRECTORY` 和只读安装沿用各自的输出根与用户目录回退。宿主启动和工具枚举不创建会话目录。首次实际需要缺省目标的调用才创建它；只读 `PreviewToolCall` 只计算稳定路径，不创建会话目录。导出预览本身若需要既存目录，可在首次使用时准备此宿主输出目录，但不写导出内容。

目录导出使用该目录，要求新树的批量文档导出使用其下工具名目录，文件导出使用稳定的 `<ToolName>.<格式扩展名>`。同一会话内的预览和执行得到同一目标，现有碰撞、覆盖和确认规则不变。创建失败只拒绝该次调用，并提示修复输出根或显式指定目标；`OutputRoot` 为空时没有缺省值，工具要求显式目标，不阻止宿主启动，也不影响有显式目标的后续调用。

原先可选的文件输出仍可省略而仅返回内容；不把它变成隐式磁盘写入。报告类原先可选的 `outputDirectory` 使用会话目录。显式旧空字符串保留旧缺省语义并给出弃用警告。默认目标在审批前具体化，包外显式目标仍由 OUTROOT-1 审批，输出目录不可写时不绕过既有保护。

## 2026-11-04 兼容性修订

维护者收到的用户报告称 Claude API 对工具 `inputSchema` 的根层组合返回 400，并拒绝整个工具列表。这是用户报告，不作为官方文档引用。因此全部八版的 full、lite 目录均禁止根层 `oneOf`、`allOf` 和 `anyOf`；字段内的组合继续允许。`PrefillWorkbenchForm` 的不同表单/模式要求由参数说明声明，并在发送工作台请求前完整校验。

## 实现与后果

共享参数表在宿主边界生成 schema 和内部参数，Foundation 工具、直接宿主调用、`CallTool` 与批次使用同一表。schema 采用 JSON Schema 2020-12，但根层不使用 `allOf`、`oneOf` 或 `anyOf`。各参数说明写明选择关系，别名互斥、条件必填和工作台表单字段由运行时校验；现有封闭对象和字段约束保留。设备字符串的兼容转换留在宿主边界，原生解析器的错误、候选项和精确匹配工作属于 DES-4 Part 2。

源示例只维护在 `reference/tool-examples/calls.json`、`sequences.json`；两种档案使用规范名。目录元数据同步 `reference/tool-catalog/engine-source.json` 后运行正式生成器。存活工具名称及官方 API 的版本可用性不改变，跨版本 schema 差异仍由官方功能可用性决定；八版实际目录差异在任务结果中逐项列出。

## 验证

覆盖八版目录、四版兼容调用与警告、别名冲突、空值和相对路径拒绝、文件/目录区分、设备组顺序、缺省目标稳定、可选输出仍省略，以及规范示例的检索与 schema 自检。合并主机重新捕获八版工具契约、工具名单和覆盖率；不手改基线哈希。

## 相关规范

- [设计规范 6.4](../design.md#64-参数)
- [安全标准](../security.md)
- [统一八版架构 ADR 0022](0022-engine-retirement-and-eight-release-unification.md)
- [验证步骤](../../manuals/validation.md)
