# V20 / V21 补充工具与验证流程（3.1.0）

本批补上 [2026-09-30 审计](../development/v20-v21-bugs-and-gaps-20260930.md) 确认的代码问题和可实现的专用入口。新增 10 个工具，完整清单 474 项，lite 保持 59；在 lite 中通过 `FindTools` / `CallTool` 调用其余入口。

这些功能通过本地编译、离线回归和 API 成员检查，不等于在实际工程或相应选件上验收。它们不自动保存、编译或下载，也不会切换连接到其他工程。所有路径都是 MCP 服务所在机器的路径。

## 工具清单

| 工具 | 能力和边界 |
|---|---|
| `ReadOpennessCompatibility` | 读取已加载 Siemens 程序集的文件版本、引擎版本、交叉引用策略和官方已知问题链接；实际安装 Update/Hotfix 保留未知，不从 SDK 副本推断。 |
| `InspectSimaticSdCompatibility` | 离线检查 `.s7dcl`，提示 V20 Update 4 语言要求、SCL 文本接口数据丢失风险及格式限制；不是完整解析器，未检出关键词不能证明兼容。`installedUpdate=-1` 表示未知，其他值只代表调用者提供的信息。 |
| `ReadNativeInvocationLog` | 从当前诊断目录读取最近日志；`BEFORE/RETURNED/THREW` 关联同一调用 ID。包含交叉引用、块/类型一致性、导入回读和新增 V20 选件调用阶段；没有覆盖全部原生调用。日志不写入参数、密码、脚本正文或异常正文。 |
| `ReadPlcBlockScopes` | 递归列出根 PLC、普通软件单元、安全单元的块，保留 `unitName/unitKind/blockPath`；包含系统块组。先完整枚举再分页；读取异常不冒充空集合。 |
| `ManagePlcBlockDocuments` | 在明确的根/软件单元/安全单元用户块组中 `list/read/export/import`；SIMATIC SD 导入实际执行须 Offline。默认 `dryRun=true`，拒绝覆盖导出文件。系统块可列出，但此工具不向系统块组导入。 |
| `ManageSinumerikArchive` | V20：从 `Project.HwUtilities` 获取 `SinumerikArchiveProvider`，执行 `archive/retrieve/fAddressArchive`，使用 `.dsf`、精确设备/PLC 项路径；支持注释、作者和 SecureString 密码。归档输出检查非空并计算哈希，恢复报告返回设备名。 |
| `ImportSinumerikAlarmTexts` | V20：NCU `Device` 的 `SinumerikAlarmTextProvider.AlarmTextImporter.ImportAlarmTexts`，输入 TS/CSV 列表。原生导入可替换已有 DB2 文本，项目语言须已激活；返回不等于逐条内容已验证。 |
| `ManageSinumerikSafetyMode` | V20：NCU `SafetyModeProvider` 读取/设置 `None/NcSI/DbSI`。写入需 `dryRun=false` 和 `confirmSafetyChange=true`；原生 API 检查实际 NCU 的离线/配置条件，修改后回读。不能替代安全工程验收。 |
| `InitializeSimotionScripting` | V20：工程 `SimotionProvider.Initialize`，返回原生初始化字符串，不执行该字符串或运行外部 SCOUT 脚本；需要 SIMOTION SCOUT TIA。 |
| `ExportScadaData` | V20：工程或单个 PLC 的 `ScadaExportProvider.Export` 输出 ZIP；需安装 SIMATIC SCADA Export for TIA Portal。导出 PLC 配置，不部署 WinCC 运行系统。 |

五个选件封装只编入 V20 的实际调用路径。现有 V21 SDK 没有对应类型，V21 明确拒绝，不能把该事实推广成“西门子 V21 产品绝无该能力”。官方 V21 模块化说明列出了独立选件程序集（包括 `Siemens.Engineering.ScadaExporter.dll`），补齐对应官方 SDK/产品环境后才可增加相应构建和验收。

## 推荐使用顺序

先调用 `ReadOpennessCompatibility`，需要排查断连时再读 `ReadNativeInvocationLog`。没有 `RETURNED` 的 `BEFORE` 只能定位中断附近，不能证明崩溃原因。针对跨调用退出，仍需 TIA/Windows 日志、补丁信息和时间关联。

先通过 `ReadPlcBlockScopes(softwarePath="PLC_1")` 找到准确的单元和用户组，再预览导入：

```json
{
  "softwarePath": "PLC_1",
  "action": "import",
  "name": "MotorControl",
  "groupPath": "Control",
  "unitName": "MotionUnit",
  "unitKind": "unit",
  "directoryPath": "D:\\Exchange\\MotorControl",
  "dryRun": true,
  "verifyDocumentReadback": true
}
```

确认对象路径和预检结果后，将 `dryRun` 改为 `false` 才会导入。存在性使用官方 `ImportedPlcBlocks` 返回的名称在**同一目标组**核对。只有一个导入块时，可导出到新建的 `tia-readback-*` 文件夹并逐一比较 `.s7dcl/.s7res`（仅统一换行符）；文件保留供复核。`sdDocumentsMatch` 只表示这两种文档的文本匹配，`contentVerified` 仍为未知，因为格式本来不包含全部工程属性。若导入后回读失败，`mayHaveChanged=true`，先检查工程，不要盲目重试。

旧 `ImportFromDocuments` 的 `verified` 字段保留兼容，但仅代表精确存在性，与新增 `existsVerified` 相同；它不再使用模糊名称匹配。新调用者应读取 `existsVerified`、`contentVerified` 和 `verificationScope`。

## 交叉引用和下载检查语义

`GetCrossReferences` 增加 `Tag/SystemConstant` 与 `unitName/unitKind`。变量路径为 `[组/]变量表/变量或常量名`；块和类型是相对各自组根目录的准确路径。变量表删除检查逐个读取变量及系统常量的服务，不向表本身请求服务。

默认仍禁用原生 PLC 交叉引用。只有用户在保存后的测试工程上明确诊断并设置服务进程环境变量 `TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1` 后才可能调用；还要求根、软件单元和安全单元的块/类型一致性完整可读且为真。**编译不保证避免崩溃。** 通用 `InvokeObject/InvokeService/DescribeService` 不允许绕开该策略。

结果 `status=notQueried/complete/failed`、`queried`、`complete` 分别说明是否开始原生查询和是否完整读取。展开失败时丢弃部分行，不能据此断言没有引用或安全删除；真正完整的空结果才表示零引用。

`CheckDownloadReadiness.IsConsistent` 现在来自实际块/类型值，未检查或读取失败为未知。`Ready=false` 表示已发现阻碍；离线检查通过时 `Ready=null`，因为设备可达性、访问权限、硬件一致性和下载提示仍未验证。客户端不得再把未知值当作允许下载。

## 官方依据与仍有的边界

- [Siemens V20 Update 4 STEP 7 / SIMATIC SD 说明](https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-step-7/improvements-in-update-4)：SCL 文本接口风险、语言扩展及文档格式限制；预检不能修复 TIA 本身的问题。
- [SINUMERIK 报警文本官方示例](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-sinumerik-one/code-example/importing-sinumerik-plc-alarm-texts)说明服务所属 NCU Device；本项目 V20 调用签名另外以用户提供的 V20 DLL 核对。
- [SINUMERIK Safety Integrated 示例](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-sinumerik-840d-sl/code-example/activating-safety-integrated?contentId=LmS5hjX7TVS0iGHGPZBsOQ)、[Openness 系统手册的归档示例](https://cache.industry.siemens.com/dl/files/802/109773802/att_1007204/v1/TIAPortalOpenness_en-US.pdf)、[SCOUT TIA 手册](https://support.industry.siemens.com/cs/attachments/109986921/SCOUT_TIA_en-US.pdf)。具体选件路径和返回值仍需对应工程验收。
- [WinCC 官方说明：SCADA Export 选件](https://support.industry.siemens.com/cs/attachments/109818202/WinCC_GeneralInfo_Installation_Readme_en-US_en-US.pdf)、[V21 模块化及兼容性](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21)。

目前没有新增“从零创建 Unified 面板类型 / 编辑类型内部控件或脚本”的虚假入口，也没有通过 Openness 重建交叉引用索引的已核实路径。普通画面、面板实例及库导入导出能力仍不能证明这些功能可用。
