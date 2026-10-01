# 原生 MCP 会话验收流程

`scripts/checks/Test-NativeMcpSession.py` 使用实际生产 MCP STDIO 入口及 `--isolate-openness`，补充原来的独立 net48 原生生命周期框架。本次只运行该脚本的离线安全检查，**没有执行下述原生流程**。

在具有匹配 TIA、许可证、Openness 用户组和已获准测试程序的 Windows 环境中，先生成计划：

```powershell
python scripts/checks/Test-NativeMcpSession.py --exe 'D:\TIA_MCP\runtime\v21\TiaMcpServer.exe' --major 21 --output 'D:\TIA_TestResults\new-run'
```

默认只显示计划，不启动进程或创建目录。确认处于获准的测试环境后，同一命令显式增加 `--run-live --confirm-new-portal` 才会执行。V20 使用对应 EXE 和 `--major 20`；输出目录必须不存在。

固定流程如下，不能通过参数改为附加用户工程：

1. 启动自有 MCP host/worker，完成 MCP 握手。
2. `ConnectIsolated` 新建自有无界面 TIA；`CreateProject` 在本次新目录创建随机名称工程。
3. `GetState` 核对 PID、启动时间、完整工程路径、版本与绑定代次；工程路径必须属于自有目录。
4. 保存、关闭、重新打开该工程，核对工程路径和 PID 不变、绑定代次已更新。
5. 关闭工程、断开连接，确认不再连接。保留工程及日志供审查。

每个调用先记录并刷盘；单次客户端等待上限 195 秒、worker 总期限 180 秒。异常后不发送保存、重试或恢复命令，不终止 TIA；仅停止测试自己启动的 MCP。输出中的 `nativeOutcomeUnknown` 需人工核对，不能把超时理解为已回滚。

结果为 `native-mcp-result.json`、`scenario.jsonl`、`host-stderr.log` 及 `diagnostics/`。未执行或失败均不能写成原生验收通过。`--self-test` 的成功只证明防误启动参数和错误响应检查通过，不证明该原生流程可成功运行。

本流程覆盖会话、工程生命周期与身份检查；尚不覆盖 PLC/HMI 各工具族的真实编辑、HMI 变量依赖影响、编译结果、同文件在 UI 中关闭后重开，以及原生崩溃根因。HMI 删除需在另行获准的 Classic/Unified 测试工程中验收“预览→删除→不存在→编译”，不可拿生产工程补测。PLC 原生交叉引用继续保持默认关闭。
