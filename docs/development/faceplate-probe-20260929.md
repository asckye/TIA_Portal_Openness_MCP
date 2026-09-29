# Unified 面板类型能力探测（2026-09-29）

## 范围

- 用户指定在另一个已打开的工程 `SICAR_StartUp_V52_1` 中测试，排除 `AutomaticDipCoatingMachine`。
- HTTP MCP 引擎版本 `2.9.2.0`，TIA V21；指定工程进程 PID `19408`。
- `Connect(projectName, allowStart=false)` 后用 `GetState` 核对目标；HMI 读取额外传入 `expectedProject`。
- 首轮只做工程读取和临时文件导出；独立服务重测执行了临时画面、面板容器和库编辑版本的创建及清理（见末节）。全程没有保存工程、编译或下载。

## 已取得的证据

1. 工程树包含 `HMI_RT_1`、`HMI_RT_2`、`HMI_RT_3`、`HMI_RT_4`。探测使用 `HMI_RT_2`。
2. 项目库路径逐级读取：`/LSicar/Types_HMI/HMI_OPMode`。
3. 样本类型：`LSicar_SelectBtn`，版本 `5.2.0`，状态 `Committed`，最低设备版本 `21.0.0.0`。
   - 类型 GUID：`883e8a6e-96a2-4717-a4be-8bf47cc9c869`。
   - 版本 GUID：`92094af6-daed-48bd-9eaf-da222673dc21`。
   - 原始库：`SICAR_Master_V52_TIAP_V21`。
   - 依赖：`LSicar_LockIcon 5.1.0`、`LSicar_ColorPalette 5.2.0`。
4. `ReadLibraryType` 返回 `supportedExportFormats: []`。运行时类为通用 `LibraryType` / `LibraryTypeVersion`，不能仅凭类名判定内部对象模型支持。
5. `ReadUnifiedLibraryType` 按现有实现使用独立的官方 `LibraryTypeVersion.Export(FileInfo, WithReadOnly)`：调用成功，生成 `Type.xml`，3794 字节。
   - 首次导出 SHA-256：`d3134eb7619ffe5a3cca7ed1abf5fce2828cb583122b0e18771769a893785b05`。
   - 完整 XML 只有安装产品、库类型版本属性和多语言备注；没有内部控件、接口定义、布局、事件脚本正文。
   - 文件导出成功不代表内部定义获取成功，不能把此 XML 当成完整面板模板重新导入。

## 限制和未完成项

- 首次游标因工程切换失效。用户确认还有另一个客户端使用该 MCP 服务，并表示会暂停它；随后重新连接指定工程，开启新的独立读取，完整取得 72 条记录（未拼接旧游标）。
- 完整读取结果：`apiCallSuccess=true`、`dataComplete=false`、`nextCursor=null`；缺口 `LibraryXmlContentUnverified`，总结为 `nativeFilesComplete=true`、`internalContentStatus=unverified`、`scriptFileCount=0`。第二次文件 SHA-256：`c4f9abb997d69532a2ccc4d9ce4fae758cb5185d483c5f820e7aa182a3d6d651`（文件含导出时间）。
- 多次观察到共享 MCP 连接在调用之间回到 PID `39192` / `AutomaticDipCoatingMachine`。本地工程名断言阻止了后续调用，没有因此执行工程写入。
- HTTP 服务源码明确使用进程级共享 TIA 句柄，不隔离各 MCP 会话的工程绑定。用户已确认其他客户端存在；本次没有检查另一个客户端的调用日志，不能断言每次切换的具体调用来源。
- 最后一次 `GetState`（2026-09-29 09:44:19 -07:00）确认仍为 `SICAR_StartUp_V52_1` / PID `19408`，进程存活，HMI 读取未被阻断。
- 当前证据表明该样本未提供用于完整面板编辑的文档往返路径；没有据此推断所有版本、所有面板类型或所有官方 API 都不支持。
- 首轮未进行内部定义修改、类型新建或编译；后续仅验证了现有类型的 `Edit()` / `Discard()` 生命周期，不能据此认定内部定义可编辑。

## 双服务连接验证（09:48 -07:00）

用户启动第二个 HTTP MCP 服务后，确认两个端点均为 `2.9.2.0`，鉴权及握手通过：

- `192.168.86.128:8765/mcp` 保持连接 `AutomaticDipCoatingMachine` / PID `39192`。
- `192.168.86.128:8766/mcp` 初始未连接；通过 `Connect(projectName="SICAR_StartUp_V52_1", allowStart=false)` 附加到 PID `19408`。
- 顺序回读 8766、8765、8766，两个端点分别保持自己的目标，两个博途进程存活。没有执行工程修改。
- 本会话后续测试使用 8766。宿主机现有 `tia-portal-vm` 配置仍指向 8765，本次没有覆盖该配置。

## 独立服务写入重测（09:50–09:54 -07:00）

用户要求重新测试面板功能。所有远程调用均发往 `192.168.86.128:8766/mcp`；每个工程操作前用 `GetState` 断言工程为 `SICAR_StartUp_V52_1`。目标为 `HMI_RT_2`，加载的 Unified API 为 `Siemens.Engineering.WinCCUnified 21.0.0.0`。

### 已通过的实际写入和回读

1. 从原画面 `/00_Screenlayout/OpmodesFp/01_Opmodes12` 的 `instOpmode_12` 读取类型引用，实际格式为 `V5.2.0\LSicar_OpmodesFp12`。
2. `ManageUnifiedScreenLayout(action=create)` 新建临时根画面 `MCP_FaceplateProbe_20260929_0952`，回读尺寸 1920 × 1080。
3. `ManageUnifiedScreenItem(action=create, itemType=HmiFaceplateContainer, containedType=...)` 创建 `ProbeFaceplate`，画面控件数由 0 增至 1。
   - 初始位置 `(24,32)`，尺寸 `306 × 104`，`Adaption=ScreenToWindow`。
   - `update` 改为位置 `(80,96)`，尺寸 `459 × 156`；所有字段写入后回读一致。
4. `ManageUnifiedObjectParts(collectionProperty=Interface)` 枚举并更新实例接口：
   - `partIndex=0`：`PropertyName=PanelHMI`，`Value=PanelHMI_statHMI`。
   - `partIndex=1`：`PropertyName=HmiNo`，`Value=HmiNo`。
   - 两项均由空值写入，随后用独立 `ManageUnifiedScreenItem(read)` 再次确认。只验证工程属性赋值；未编译、运行或验证在线通信。
5. `ManageLibraryTypeVersion(action=edit)` 对 `LSicar/Types_HMI/HMI_OPMode/LSicar_SelectBtn` 的 `5.2.0` 调用官方 `LibraryTypeVersion.Edit()`：
   - 原版仍为 `5.2.0 / Committed / IsDefault=true`。
   - 新增 `5.2.1 / InWork / IsDefault=false`，GUID `bb4454e3-e380-4999-b4f1-69ecc7a63754`。
   - 证明当前 MCP 可以创建已有面板类型的编辑版本；不等同于从零创建全新类型或修改其内部控件。

### 内部定义读取的限制

- 对新建 `5.2.1 / InWork` 版本调用 `ReadUnifiedLibraryType`。原生 `LibraryTypeVersion.Export(FileInfo, ExportOptions)` 明确报错：`The version data cannot be exported as it is in in-work state.` 没有产生原生文件。
- 此次响应外层 `apiCallSuccess=true`，但 `dataComplete=false`，内部导出记录 `apiCallSuccess=false`；必须看具体失败记录，不能把外层成功当成导出成功。
- 临时实例的 `ReadUnifiedFaceplateInstance` 首页面板版本关联返回 `Unsupported`：该实例未通过 `LibraryTypeInstanceInfo` 暴露关联版本，只有 `ContainedType` 字符串，因此无法验证关联版本 GUID。
- 此读取还出现普通 CLR 数组的 `CountFailed`（遍历多语言 `Culture` 元数据），随后继续分页报 `CursorExpiredOrUnknown`。这是额外的读取链路问题，未取得完整遍历，不把它作为“类型没有内部内容”的证据。已通过专用控件/接口工具独立完成上述回读验证。
- 当前没有完成从零创建新面板类型、读取/改写内部按钮和脚本、发布新类型版本的实测。通用文档导入工具存在，但此样本未提供完整、可往返导入的面板定义，未尝试把元数据 XML 当作模板。

### 清理结果

- 对此次产生的 `5.2.1` 执行 `Discard()`，随后 `ReadLibraryType` 确认 `versionCount=1`，唯一版本为原来的 `5.2.0 / Committed`，默认版本、GUID 和两个依赖与测试前一致。
- 删除 `ProbeFaceplate`，回读 `verifiedAbsent=true`、控件数 0；再删除临时画面，回读 `verifiedAbsent=true`。
- `GetHmiScreens` 确认临时画面不存在，画面数恢复为 181。
- 09:53:52 最终状态仍为 `SICAR_StartUp_V52_1` / PID `19408`，进程存活，HMI 读取未被阻断。
- 未保存、编译、下载或更新任何既有实例。临时对象已清理；创建/删除操作可能仍使 TIA 显示工程有未保存修改，未尝试通过重载工程消除此标记。
