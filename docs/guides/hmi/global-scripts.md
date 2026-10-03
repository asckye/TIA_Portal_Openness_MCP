# Unified 全局脚本

适用于 V20/V21 完整引擎中实际提供相应 Unified 服务的设备。全局模块属于 `HmiSoftware.Scripts`，按钮事件属于 `EventHandlers.Script`；两者使用不同的读取和更新方法。

## 读取与修改现有模块

1. 用 `ListUnifiedGlobalScripts` 找到实际模块名称。
2. 读取 `GetToolUsage(toolName="ReadUnifiedGlobalScript")`，取得完整 `.hmi.js`、`.hmi.yml` 及导出信息；有分页时续读到完成。
3. 读取 `GetToolUsage(exampleId="unified-update-existing-module")`。修改完整 JS 正文并保留仍需使用的定义和依赖。
4. 用 `UpdateUnifiedGlobalScript` 提供实际 `softwarePath`、`expectedProject`、`moduleName` 和完整 `scriptCode`，先预览。
5. 检查前后正文，将预览返回的 `token` 作为执行调用的 `expectedToken`，其余目标及内容保持相同，设置 `dryRun=false`。
6. 检查 `operationSuccess`、`verificationSuccess` 和最终状态。`Verified` 或 `Unchanged` 才能说明目标正文与请求一致。

示例模块见 `GetToolUsage(exampleId="unified-global-module")` 和 [ReadActualValue.hmi.js](../../../reference/tool-examples/languages/ReadActualValue.hmi.js)。按钮事件使用[事件动作指南](unified-actions.md)，不能给全局模块设置事件的 `ScriptCode` 属性。

## 怎样解释返回值

| 字段或状态 | 含义 |
|---|---|
| `apiCallSuccess` | 原生调用正常返回；仍需检查实际导入及读回 |
| `ImportRejected` | 原生 Import 返回 false |
| `ReadbackMismatch` | 导入返回成功，但读回正文不符合请求 |
| `mayHaveChanged` | 已尝试修改，需检查工程现状后再处理 |
| `backupDirectory` / `candidateDirectory` | TIA/MCP 电脑上的原生备份与候选文件位置 |

预览令牌绑定原文和拟修改内容；修改其中任何一项后重新预览。接口使用原生模块导出/导入，保留 YAML 和编码。离线 JavaScript 解析不会执行脚本，也不能证明 Runtime 名称和依赖正确。

更新后检查引用、编译诊断并按需保存；接口本身不自动保存或部署。调用示例中的官方来源可通过 `GetToolUsage` 继续读取。当前新增路径的真实 TIA 验收状态见[能力说明](../../reference/capabilities.md)。
