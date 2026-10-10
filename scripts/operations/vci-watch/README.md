# TIA VCI 看门狗

此可选 .NET 10 file-based app 定期将已打开工程的 VCI 内容导出到专用 Git 工作区，写变更日志并创建本地提交。它仅用于 V20/V21 完整引擎路线，通过所选发布包的 FoundationHost 通信，不进入发布包。

1. 准备 Git、.NET 10、匹配版本的 TIA 和解压的发布包。
2. 在 TIA 打开测试工程，建立 VCI 工作区映射；用当前宿主的 `GetToolUsage` 核对参数。
3. 准备专用 `workspaceFolder`。工具执行 `git add -A`，会包含其中的其他变更。
4. 发布后从 [config.example.json](config.example.json) 复制 `bin-build/vci-watch/config.json`，填写 `bundleRoot`、`releaseKey`、`projectFolder`、`workspaceFolder`、`workspaceName`、`gitAuthor`。旧 `enginePath` 不再使用。
5. 初次保留 `autoCompile=false`，在授权的测试环境手动核对导出与本地提交，再注册任务。

```powershell
dotnet run --project build-tools/release -- publish-vci-watch -NuGetConfig <offline-nuget.config>
bin-build/vci-watch/watch.exe --config bin-build/vci-watch/config.json
bin-build/vci-watch/watch.exe --register-task
bin-build/vci-watch/watch.exe --register-task --interval-minutes 30
bin-build/vci-watch/watch.exe --register-task --remove
Disable-ScheduledTask TiaVciWatch
Enable-ScheduledTask TiaVciWatch
```

发布目录是 `bin-build/vci-watch`，输出为 `OutputType=WinExe` 的 `watch.exe`；计划任务指向发布的 EXE，使用当前用户的 InteractiveToken、LeastPrivilege、IgnoreNew 和 15 分钟执行上限。默认间隔 10 分钟。默认配置在 EXE 同目录；也可传绝对 `--config`。所有子进程设置 `CreateNoWindow`，无需 pythonw 或 PowerShell 进程。

看门狗启动 `runtime/v<releaseKey>/TiaMcp.FoundationHost.exe --bundle-root <bundleRoot> --release-key <releaseKey> --transport stdio --profile full --logging 0`，宿主解析同包的 worker。P/Invoke 使用 Toolhelp 进程快照和 `NtQueryInformationProcess(ProcessCommandLineInformation)` 查询命令行，.NET `Process` 负责优先级、内存与终止，无新增 NuGet 包。查询失败时不把进程视为用户 GUI，也不据未知命令行终止进程。

一个周期检测 GUI 工程，依次探测 `ListPortalProcessProjects`、`ConnectPortal`、`AttachOpenProject`，读取 VCI 状态并执行 `ProjectToWorkspace`。不调用 `OpenProject`、`WorkspaceToProject` 或 `SaveProject`。无实际 Git 差异时不创建提交；非“待编译”导出失败时不提交。`autoCompile=true` 对明确列出的 `compileSoftwarePaths` 调用编译，会改变工程状态；此选项不保存工程。调用可能需要 Workbench 审批，应保持工作台打开或在 MCP 菜单关闭审批；客户端不会绕过审批。

配置目录的 `log/watch-YYYYMMDD.log` 与 `watch.state.json` 保存日志、周期、冷却和进程信息。工程目录变化信号跳过 Vci，完整检查间隔、未编译冷却、锁和单轮超时各按配置生效。超时会终止本轮宿主及 worker，已导出的文件不会回滚。记录的宿主 PID 必须同时匹配启动时间和命令行归属；清理仅针对当前进程树的 Openness 无头 Portal，用户 GUI 不属于清理目标。多实例和异常恢复仍须在指定测试环境验收。

VCI 文本不包含完整硬件组态，也不是工程备份。本次仅运行合成客户端测试、编译与发布，未执行看门狗周期、注册任务、用户 Git 提交或 TIA 连接；本版原生重验为 NOT RUN。
