# Unified 全局脚本更新

## 定位问题与修复

全局模块 `HmiSoftware.Scripts` 与按钮事件中的 `EventHandlers.Script` 是不同对象。
原来的通用对象定位支持页面、页面对象和变量，但没有 `HmiScriptModule`、`HmiScripts`；反射调用也没有把 JSON 路径字符串转换为官方方法要求的 `DirectoryInfo` / `FileInfo`。这会造成读取工具已经拿到 Navigation，通用修改工具却无法定位或调用。

现已补齐两个对象类型，读取和修改复用精确模块定位。示例定位参数：

```json
{
  "objectKind": "HmiScriptModule",
  "objectPath": "/Scripts/Navigation",
  "softwarePath": "HMI_RT_2"
}
```

也支持 `objectPath="Navigation"` 和显式 HMI 路径，或者旧式 `objectPath="HMI_RT_2:Navigation"`。模块名称区分大小写，不模糊匹配。`HmiScripts` 使用 `objectPath="/Scripts"` 和显式 HMI 路径。

V21 的官方模块正文更新方式是原生导出、导入。不能把按钮事件的 `ScriptCode` 属性直接套用到全局模块。官方依据：[V21 Import of Global Script](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/import-of-global-script)，以及本机 V21 PublicAPI 的 `HmiScriptModule.Export(DirectoryInfo, string)`、`HmiScriptModuleComposition.Import(DirectoryInfo)` 定义。

## 推荐使用 UpdateUnifiedGlobalScript

1. 先用 `ReadUnifiedGlobalScript` 读取目标模块，保留原生 `.hmi.js`、`.hmi.yml` 和校验信息。完整跟随游标，不能只拿到名称就开始修改。
2. 在完整 `.hmi.js` 原文基础上修改导航逻辑。保留其他函数、全局定义和依赖，明确核对仍被引用的页面。
3. 调用 `UpdateUnifiedGlobalScript`，提供 `softwarePath`、`expectedProject`、`moduleName`、完整 `scriptCode`。默认 `dryRun=true`，只导出备份并预览，不导入工程。
4. 检查返回的 `before.scriptCode`、`after.scriptCode` 及 `token`。实际应用时传入同一组参数，设置 `dryRun=false`、`expectedToken=预览的token`。
5. 只有 `operationSuccess=true`、`verificationSuccess=true`，且状态是 `Verified` 或 `Unchanged`，才可确认目标模块当前正文符合请求。导航目标的有效性仍须单独检查。

在精简工具列表中可先通过 `FindTools` 搜索 `UpdateUnifiedGlobalScript`，再经 `CallTool` 调用。

### 更新边界

- 明确指定现有项目、HMI 和模块；目标不存在则失败，不创建、不删除重建。
- 预览令牌绑定项目、HMI、模块、当前 JS/YAML 和拟写入内容。原文或拟写入内容变化都须重新预览。
- 使用独立目录，仅允许一个模块的 `.hmi.yml` / `.hmi.js` 配对。额外模块、额外文件、目录跳转、未知元数据格式均明确拒绝。
- 保持原生 YAML 字节及脚本编码。当前支持严格 UTF-8、带 BOM 的 UTF-16；每个文件上限 1 MiB。
- JavaScript 仅使用 Esprima 做语法解析，不执行脚本，不调用 TIA `SyntaxCheck`，也不代表运行时名称或依赖已经核对完整。
- 应用前重新导出并检查令牌；应用后重新定位模块并原生导出，逐字符比较完整 JS 正文。
- 不保存、编译、下载、关闭 TIA，不修改或删除页面。导入也不会由本接口自动回滚或重试。

### 结果与失败

`apiCallSuccess` 表示原生调用是否正常返回，必须同时查看 `importReturned`、`verificationSuccess`、`operationSuccess`。导出细节分别保存在 `exportBefore` / `exportAfter`。

`ImportRejected`：官方 Import 返回 false；即使没有抛异常也判为失败。

`ReadbackMismatch`：Import 返回 true，但回读正文不一致；不能当作更新成功。

`importAttempted=true` 后任何失败均保留 `mayHaveChanged=true`，不假定导入是原子操作。若 IPC/对象句柄已失效，停止后续读取和远程清理，记录故障并要求明确重新绑定；不能自动重试。

原生备份和候选文件位于 **MCP 所在机器** 的 `%TEMP%\TiaMcpServer\GlobalScriptEdits\<操作目录>`，实际位置见 `backupDirectory`、`candidateDirectory`。它们含工程源码，应保留到核验完成，之后手动清理；不要混入公开 Release。

## 验证状态

本地离线回归覆盖精确定位、只读预览、令牌失效、未知格式拒绝、单模块导入、正文回读、导入 false、导入无效、部分写入异常、连接失效后停止，以及 UTF-8/UTF-16 编码保留。

V21/V20 均编译通过；实际 EXE 的反射路径转换、写入限制及新工具注册另有验证。V21 使用实际官方 DLL 核实方法签名。提供的 V20 PublicAPI 不含 WinCCUnified DLL，因此 V20 只验证构建及桥接行为，不能声称已验证原生全局脚本导入能力。

v2.7.7 完整运行包已包含该接口。尚未在虚拟机真实工程上导入 Navigation，本修复不代表导航已经修改或运行时跳转已核对通过。
