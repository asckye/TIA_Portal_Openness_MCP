# HMI 快照稳定性诊断

本次变更针对只读 `ReadHmiScreenSnapshot`。离线与宿主机测试不能证明真实 TIA 工程稳定，也不能把成功返回的有界快照视为完整项目。

## 已确认的问题

- 旧实现即使传入绝对页面路径，也会先遍历整个 HMI 页面树；现在仅遍历目标路径的各级集合。裸页面名仍需要遍历，以拒绝重名歧义。
- 旧实现把属性内的句柄释放异常记录为普通 `ReadFailed` 后继续访问其他代理，顶层仍可能返回 `success=true`。现在遇到句柄释放、IPC 或不可恢复错误立即停止，保留已读取节点，并返回 `apiCallSuccess=false`、`dataComplete=false`。
- 显式重新绑定成功后，旧实现可能因项目对象引用未改变而沿用旧软件缓存。现在每次成功绑定都清空软件缓存。

## 诊断控件属性暂缓读取

两份真实 V21 响应都在 `HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText` 属性处出现没有详细消息的异常，紧随其后的是该控件的已释放句柄错误。这个顺序是排查线索，不是 Portal 退出根因的证明。

现在对该属性返回 `Quarantined`、`readAttempted=false`，不调用 getter，明确保留数据缺口。`ReadUnifiedScreenBranch` 的属性/属性值读取也不能绕过这一限制。其他诊断控件属性和已读脚本仍可作为部分证据。

V21 官方系统诊断控件属性说明：
https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/accessing-system-diagnosis-control-properties

该官方页面未列出上述属性；不能仅凭未列出就认定 Siemens 明确禁止调用，或认定所有 V21 更新均有相同问题。

## 状态及后续请求

- `apiCallSuccess`：本次快照调用是否在连接仍可用的情况下返回。普通单字段失败仍需查看 `readFailureCount`。
- `dataComplete`：声明的公共属性范围是否读全。深度、节点、时间、字符串限制，字段读取失败或暂缓读取均会使其为 false。不承诺库类型内部内容。
- `connectionUnavailable`：观察到连接或句柄故障，不等同于已经确认 TIA 进程退出。
- 故障后 HMI step 工具会返回 `HmiReadSessionBlocked`，不会自行重试、绑定、关闭或释放 Portal。必须先检查日志，再由调用方显式成功执行 `AttachToOpenProject` 才解除阻断。此阻断不是所有 MCP 工具的全局锁。
- `GetState.Meta.hmiReadHealth` 区分附着状态与 HMI 读取故障。`isConnected=true` 仍只代表持有 Portal 对象，不能作为所有软件句柄有效的证据。
- 快照本身没有续采游标；`nextCursor=null` 不代表内容完整。正常连接下可根据已报告路径使用只读分支接口补读。故障时停止采集，不能自动换接口继续探测。

## 本地日志

实际运行 MCP 的 Windows 用户临时目录：`%TEMP%\TiaMcpServer.hmi-read.log`。

日志只写读取阶段、路径、操作 ID、带时区的时间和异常栈，不写成功读取的属性值、脚本正文或连接密钥。每次请求返回 `operationId`、`diagnosticLog`、`lastAttemptedPath` 和 `lastCompletedPath`；日志无法写入时返回 `diagnosticLogError`。日志约 8 MiB 后循环覆盖，应在故障后及时复制。

结合故障时刻附近的 TIA/MCP 日志、Windows 应用程序事件和虚拟机进程状态核对。最后一次 getter 是定位线索；另一个进程、用户操作或 Siemens 内部异常仍可能影响句柄生命周期。时间预算只在同步读取之间检查，不能中断已经发出的 Openness 调用。

## 验证边界

测试覆盖绝对路径隔离、经典 HMI 子目录兼容、疑似故障 getter 不被调用、嵌套句柄错误立即停止、下一次请求不触碰软件对象、内容令牌不受诊断耗时影响，以及实际 net48 EXE 的透明远程代理故障。没有连接虚拟机，没有保存、编译、下载或修改 TIA 工程。
