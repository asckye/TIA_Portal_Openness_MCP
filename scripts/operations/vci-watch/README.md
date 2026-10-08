# TIA VCI 看门狗

此可选 Python 工具定期将已打开工程的 VCI 内容导出到 Git 工作区，写变更日志并创建本地提交。它调用 V20/V21 完整引擎，不能直接套用到 V14 SP1–V19 基础宿主；Studio 的 VCI 适配器也不是这个脚本的通信入口。

## 使用前准备

1. 准备 Python、Git、匹配版本的 TIA 和完整 MCP 引擎。
2. 在 TIA 中打开目标测试工程，建立 VCI 工作区映射。相关工具参数先通过当前引擎的 `GetToolUsage` 核对。
3. 使用专用 Git 工作树作为 `workspaceFolder`。脚本实际执行 `git add -A`，会包含该工作树里的其他变更；不要将工作区与无关文件混放。
4. 从 [config.example.json](config.example.json) 复制 `config.json`，填写 `enginePath`、`tiaMajorVersion`、`projectFolder`、`workspaceFolder`、`workspaceName` 和 `gitAuthor`。
5. 初次保留 `autoCompile=false`，先手动完成一次导出与提交核对，再选择是否注册计划任务。

`projectFolder` 是 TIA 工程所在目录，用于变化信号；`workspaceFolder` 是 VCI 导出与 Git 工作树目录。两者都位于运行脚本的电脑，不会自动映射宿主机/虚拟机路径。

## 执行与结果

在本目录运行：

```powershell
python watch.py
```

脚本检测 GUI 工程、连接并附着，查询变化，然后使用 `ProjectToWorkspace` 导出。没有实际 Git 内容差异时不创建提交；出现非“待编译”的导出失败时，本轮不提交。脚本不会推送远程仓库。

未编译的不一致块可能无法导出，需先在 TIA 编译并检查诊断。`autoCompile=true` 会对明确列出的 `compileSoftwarePaths` 调用编译，改变工程状态；该选项不会保存工程。专有技术保护块和不支持的对象仍可能被 VCI 拒绝，导出内容不是完整工程备份。

当前实现不调用 `WorkspaceToProject` 或 `SaveProject`。它包含 `ConnectPortal` 与附着探测、超时和进程清理逻辑；这些机制不是“绝不会启动实例、影响工程或误判进程”的保证。多实例选择和异常恢复尚需用当前版本在指定测试环境重新核对，不能直接沿用旧版绝对安全表述。

## 计划任务和日志

```powershell
python watch.py --register-task
Disable-ScheduledTask TiaVciWatch
Enable-ScheduledTask TiaVciWatch
python watch.py --register-task --interval-minutes 30
python watch.py --register-task --remove
```

默认计划间隔为 10 分钟。日志位于 `log/watch-YYYYMMDD.log`；`watch.state.json` 记录周期、退避与进程信息。配置中的完整检查间隔、未编译冷却、强制全量检查、锁时限和单轮超时有各自作用，以示例配置为准；超时可能终止脚本启动的引擎，已经导出的文件不会回滚。

历史 V21 工程曾验证变化检测、导出和本地提交，旧耗时数字不能预测当前工程性能。本轮文档整理未运行看门狗、注册任务或连接 TIA；本版该路线原生重验为 NOT RUN。
