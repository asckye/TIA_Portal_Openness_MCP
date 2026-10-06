# Unified 启动画面与 Runtime 工程设置

V20/V21 完整引擎提供 `GetUnifiedRuntimeSettings` 和 `SetUnifiedRuntimeSettings`。它们编辑 HMI 工程的 `RuntimeSettings`，不是运行设备上的画面切换命令；实际属性支持取决于安装的 Unified API 和设备。

## 读取再修改

1. 用实际工程名称和 HMI 软件路径读取 `GetToolUsage(toolName="GetUnifiedRuntimeSettings")`。
2. 按示例读取所需字段，例如 `StartScreen`、`ScreenResolution`。查看当前值、类型、字段失败项及 `capabilities.allowedValues`。
3. 需要修改启动画面时，用 `ListHmiScreenPaths` 取得目标完整路径。工具将其解析为实际画面，并检查名称是否唯一。
4. 读取 `GetToolUsage(toolName="SetUnifiedRuntimeSettings")`，通过 `changesJson` 提交所需字段，先 `dryRun=true`。
5. 核对 `before`、`requested`、`proposed`，以相同修改内容和返回的 `token` 执行 `dryRun=false`、`expectedToken=...`。
6. 检查 `operationSuccess`、`verificationSuccess`、`readback` 和 `failures`，确认实际值后保存工程。

枚举使用能力查询返回的准确名称。`dataComplete` 表示请求字段是否读全，不表示所有 Runtime 设置都已获取。可用字段和参数以当前工具示例为准，未提供的子设置不用猜测属性路径。

若部分字段写入后发生失败，查看 `appliedFields` 和 `mayHaveChanged`，再读回现状；多个设置并不是原子事务。接口不重启 Runtime 或自动部署。工程中的启动画面修改在目标设备的部署/启动流程完成后才可能生效，需另行检查运行结果。

当前发布包含这些工具；真实设备支持与新增原生验收边界见[能力说明](../../reference/capabilities.md)。
