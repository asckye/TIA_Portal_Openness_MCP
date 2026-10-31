# 预发布真机验证清单

本页写给在真实 TIA Portal 上测试 4.0 预发布版本（`v4.0.0-beta.N`、`v4.0.0-rc.N`）的人：每个 TIA 版本要试什么、怎样记录、怎样报告。维护者不做真机测试，预发布只经过构建、离线测试和 CI；真机结果全部来自测试者。通过的结果和失败的结果同样有用。

每个检查项有一个编号（T01、T02……），报告时写上编号。预发布说明末尾的“Not verified on a real TIA Portal”清单另有一套编号，报告那些项目时写那里的编号。

## 准备

- 按[从 3.3 迁移到 4.0](migrate-3.3-to-4.0.md) 把预发布包解压到新目录，与 3.3 并排安装。
- 只用工程副本。不要连接生产 PLC；在线、下载类检查只用 PLCSIM Advanced 或测试硬件，并自行判断风险。
- 写操作先预览（`dryRun: true` 或工具的预览模式），核对后再实际执行并在工作台批准。
- 调用不熟悉的工具前，先用 `GetToolUsage(toolName, operation)` 取得当前版本的参数和示例。V20/V21 默认是 lite 档，不在列表中的工具用 `FindTools` 查找、`CallTool` 调用。
- 每次测试记下：包文件名和 SHA-256、TIA 版本及已装 Update、发布键（`14sp1`–`21`）、MCP 客户端、审批开或关。

## 要保留的证据

- 每次调用的完整结果信封，至少包括 `ok`、`error.code`、`meta.outcome`、`meta.execution` 和 `meta.requestId`。
- TIA 是否退出、在哪一步退出，工程是否已保存。
- TIA 或 worker 退出时：在工作台 **环境体检** 页导出“诊断包”，它收集最近 24 小时的匹配 Windows 事件、TIA 进程和转储文件清单；再新建 MCP 会话调用 `GetNativeInvocationLog`，找出退出前最后一个原生调用。
- 日志位置：交付目录的 `data\logs\<发布键>\`（宿主日志）、`data\diagnostics\`（原生调用诊断）、`data\logs\audit\`（审计）。交付目录不可写时，日志在 `%TEMP%\TiaMcp\logs\<发布键>`，诊断在 `%LOCALAPPDATA%\TiaMcp\diagnostics`。

分享前检查日志和诊断包，删除客户工程名、密码、密钥和其他敏感信息。不要附加客户工程。

## 每个版本都做

| 编号 | 检查 | 预期结果和要记录的内容 |
|---|---|---|
| T01 | 启动与绑定：从新目录启动 `TiaOpenness.exe`，选择该 TIA 版本，打开 **环境体检**；在 AI 客户端依次调用 `ListPortalProcessProjects` → `ConnectPortal` → `AttachOpenProject` → `GetSessionState` → `GetProjectTree` → `ListPlcBlocks` → `GetPlcBlockInfo`（一个已有块） | 每步成功；`GetSessionState` 显示正确的发布键和工程；记录第一次附着时 TIA 是否弹出 Openness 访问确认 |
| T02 | 错误后会话可用：用不存在的块名调用 `GetPlcBlockInfo`，再调用 `ListPlcBlocks` | 第一次返回错误且说明原因，第二次仍成功；会话没有被锁定 |
| T03 | 编译行为：`CompilePlcSoftware` 对应 TIA 的“仅更改”还是“全部重建”，步骤见下文 | 记录结论、两种 TIA 编译各自涉及的块数和 MCP 结果中的块数 |
| T04 | 编译诊断：一个只有警告的编译，以及一个故意制造错误的编译，分别用 `CompilePlcSoftware` 和 `CompilePlcDiagnostics` | 只有警告时成功；有错误时返回 `COMPILE_ERRORS`，带错误数、警告数和最多十条带对象路径的叶级错误；与 TIA“信息 > 编译”对照并记录差异 |
| T05 | 导出导入往返：`ExportPlcBlock` 导出到已有目录中的新 `.xml` 文件；`ImportPlcBlock` 以 `overwrite: false` 导回 | 导出文件存在；同名对象存在时导入在 TIA 调用前被拒绝；预览后以 `overwrite: true` 执行并批准，对象被替换，再次导出内容一致 |
| T06 | 审批：任选一个写工具，先预览，再实际执行；第一次在工作台拒绝，第二次批准 | 预览不等待审批；拒绝时返回 `CONFIRMATION_REQUIRED` 且工程没有变化；批准后执行；**审计日志** 页出现同一个 `requestId`，`tia audit verify` 通过 |
| T07 | 保存和断开：修改后调用 `SaveProject` 并批准，再调用 `DisconnectPortal` | 保存需要批准；断开不保存、不关闭工程；任务管理器中本次的 worker 进程退出 |
| T08 | worker 重启：空闲时调用 `RestartOpennessWorker`，再调用 `ConnectPortal` 和 `AttachOpenProject` | 新 worker 启动；重新绑定后 `GetProjectTree` 正常；TIA 不需要重启 |
| T09 | 日志位置：完成 T01–T08 后查看交付目录 | 宿主日志在 `data\logs\<发布键>\`，诊断在 `data\diagnostics\`，审计在 `data\logs\audit\` |

## 按版本补充

| TIA 版本 | 补充检查 |
|---|---|
| V14 SP1、V16、V21 | T10 工作台工程操作页 |
| V15.1 | 绑定 `.ap15_1` 工程（T01 使用该工程即可） |
| V14 SP1、V15.1 | 导出一个块后查看 SimaticML 文件头中 TIA 写入的版本写法（例如 “V14 SP1”、“V15.1”），附在报告中 |
| V15.1–V21 | 受 UMAC 保护的工程：`OpenProject` 分别用正确、错误和缺少的 `umacUserName` / `umacPassword` 打开；缺少凭据时应返回 `PRECONDITION_FAILED` 并指出缺少的参数，密码不出现在日志、审批和审计中 |
| V18–V20 | `GetPlcCrossReferences`（V19–V20 另有 `GetUnifiedCrossReferences`）：先看 `GetToolUsage` 中当前版本是否默认允许查询；能查询时记录查询是否完成、TIA 是否保持运行 |
| V19–V21 | T12 删除预览格式错误路径；T13 HMI 面板容器；Unified 变量表和画面以 `/组/子组/名称` 创建时位于已有分组内（例如 `EnsureUnifiedHmiTagTable`） |
| V20、V21 | T01–T08 使用版本匹配的导入导出样本；T04 记录完整的嵌套编译诊断 |
| V21 | T11 原生交叉引用（需显式开启） |

预发布说明的“Not verified on a real TIA Portal”清单还列出在线下载、硬件与网络、会话生命周期、PLC 软件、安全与可选产品等逐项核对点；有条件时按那里的编号测试和报告。清单中依赖试验开关、需要比较两种内部实现的项目只能由维护者完成，测试者可以跳过。

## 重点项说明

### T03 编译：仅更改还是全部重建

`CompilePlcSoftware` 调用官方的编译接口，V14 SP1–V21 的 API 都没有编译选项，因此它对应 TIA 的“编译 > 软件（仅更改）”还是“编译 > 软件（重新编译所有块）”（英文界面为 “Compile > Software (only changes)” 和 “Software (rebuild all blocks)”）只能在真机上确认。确认后会写进工具说明；不会用其他方式模拟“全部重建”。

1. 在工程副本中，对目标 PLC 执行 TIA 的“全部重建”，使所有块一致。
2. 只修改一个块（例如改一行代码），不要编译。
3. 调用 `CompilePlcSoftware`：先预览，再按 `GetToolUsage` 的说明确认执行并批准。
4. 记录 MCP 结果 `data` 中出现的块和消息数量，以及 TIA“信息 > 编译”中列出的块。
5. 再次修改同一个块，在 TIA 中执行“仅更改”，记录列出的块；然后执行“全部重建”，再记录一次。
6. 对比三次结果，判断 `CompilePlcSoftware` 与哪一种一致。时间允许时对 `CompilePlcDiagnostics` 重复一次。

### T10 工作台工程操作页（V14 SP1、V16、V21）

1. 从新目录启动 `TiaOpenness.exe`，选择该版本，在 **工程操作** 页连接 TIA、打开工程副本、读取块列表。
2. 导出一个块到一个已有文件夹，确认文件生成。
3. 再导出到一个不存在的目录：界面应显示原始错误文本，之后再次读取块列表仍正常。
4. 断开并关闭工作台，在任务管理器中确认工作台为本次连接启动的后台进程都已退出，记录残留的进程名。

### T11 V21 原生交叉引用（需显式开启）

V21 上的原生交叉引用查询曾导致 TIA Portal 退出，有时发生在返回结果之后，因此默认不查询。拒绝表示“未查询”，不表示“没有引用”。这项检查确认它在你的 V21 环境中是否稳定。

1. 保存工程副本，关闭其他工程。
2. 在启动 MCP 服务之前设置 `TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1`：本机连接写在客户端配置中该服务的 `env`，或在启动客户端的窗口中设置；HTTP 服务在启动工作台之前设置。若该预发布的说明列出了工作台中的开启方式，再用那种方式重复一次。
3. 确认目标块已编译且一致，调用 `GetPlcCrossReferences`；再对一个准确的 Unified 对象调用 `GetUnifiedCrossReferences`。
4. 对一个有引用的块执行删除预览（`DeletePlcBlock`，`crossReferences: true`，不要确认执行），删除工具使用同一个开关。
5. 每次查询后在 TIA 中继续操作几分钟（例如打开一个块），观察 TIA 是否退出。
6. 测试结束后删除该环境变量并重启 MCP 服务。

记录：每次查询是否返回、返回的行数、TIA 是否退出以及退出前的最后一个操作；退出时附诊断包和 `GetNativeInvocationLog` 结果。

### T12 删除预览中的格式错误路径（V19–V21）

已有测试者报告：AI 客户端在一个 Unified 对象工具的删除预览中，把对象路径写成字符串数组（例如 `["Screens","Main"]`），而工具要求属性步骤数组（`[{"property":"Screens","name":"Main"}]`），随后 TIA 进程退出。原因尚未确认。从 beta.2 起，格式错误的路径在确认、审批和任何 TIA 调用之前被拒绝。

如果你遇到过这种情况，请用问题报告表单提供：工具名、TIA 版本和 Update、发布键、包名、完整参数 JSON、完整结果（或“没有返回”）、TIA 何时退出，以及诊断包和 `GetNativeInvocationLog` 的结果。

复测步骤：

1. 在工程副本中，对 `ManageUnifiedObjectParts`、`ManageUnifiedDynamization` 或 `ManageUnifiedEvent` 之一调用删除动作的预览，`objectPath` 写成字符串数组。
2. 预期：返回 `INVALID_ARGUMENT`，消息给出期望格式；结果显示操作未开始；TIA 保持运行。
3. 改用正确的属性步骤数组再预览一次，预期正常返回预览结果。不要确认执行删除。

### T13 HMI 面板容器（V19–V21）

1. 在一个已有面板实例（`HmiFaceplateContainer`）的 Unified 画面上，用 `ManageUnifiedScreenItem` 的 `list` 和 `read` 读取该容器，`screenPath` 使用 `/组/画面` 形式的绝对路径。
2. 从读取结果中原样复制 `ContainedType` 的值。
3. 用 `create` 新建一个容器：`itemType` 为 `HmiFaceplateContainer`，`containedType` 为复制的值，先 `dryRun: true` 预览，再实际执行并批准。
4. 再次 `read` 新容器，并在 TIA 中查看它引用的面板类型和版本是否与原容器相同。
5. 对一个普通画面对象类型传入 `containedType`，预期被拒绝。
6. 有自定义 Web 控件或自定义控件容器时，按同样步骤测试。
7. 最后用 `delete`（`confirmDelete: true`）删除新建的容器。

记录：`ContainedType` 的格式（可隐去客户名称）、创建是否成功、TIA 中显示的结果。需要追溯面板实例对应的库类型版本时，可以用 `GetUnifiedFaceplateInstance` 读取。

## 报告

| 情况 | 使用的表单 |
|---|---|
| 完成了一个检查项或说明清单中的编号项（通过或失败都报告） | [真机验证结果](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=real_machine_result.yml) |
| 工具返回错误结果、TIA 或 worker 退出、文档与行为不符 | [问题报告](https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=bug_report.yml) |

每个 Issue 写一个检查项和一个 TIA 版本。维护者把收到的报告记入[测试者反馈台账](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/tester-feedback.md)，复核通过的真机结果再记入[真机验收台账](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/reference/real-machine-ledger.md)。已知限制见[Openness 限制和已知问题](../troubleshooting/openness-limitations.md)。
