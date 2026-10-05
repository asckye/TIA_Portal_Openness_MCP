# 独立原生生命周期测试

当前构建包含 V20/V21 独立 net48 测试程序及进程监督脚本；默认仅编译并执行离线安全测试，**没有启动或连接 TIA，没有执行以下原生场景**。完整发布构建记录各版测试程序哈希、离线断言数与 `liveAcceptance=NOT RUN`，见 [release-build.json](../../manifest/release-build.json)。

## 实现与范围

- [NativeTests.V20.csproj](../../tests/Engine/TiaMcpServer.NativeTests/V20/NativeTests.V20.csproj) 使用 V20 单体 API；[NativeTests.V21.csproj](../../tests/Engine/TiaMcpServer.NativeTests/V21/NativeTests.V21.csproj) 使用 V21 Base 模块，两者均为 x64/net48、`Private=False`。
- [启动与安全检查](../../tests/Engine/TiaMcpServer.NativeTests/Program.cs) 无 Siemens 类型引用；先从对应版本安装注册表找到 PublicAPI、检查主版本，再注册精确程序集身份解析器。独立 `NoInlining` 方法进入原生调用，显式单 MTA 线程执行。
- [原生场景](../../tests/Engine/TiaMcpServer.NativeTests/LiveSuite.cs) 只创建自己的 `WithoutUserInterface` 实例和唯一 scratch 目录；没有 Attach、现有工程路径、PLC 地址或进程 ID 参数。每轮创建工程，提交设备组事务，回滚另一个设备组事务，保存、关闭、重新打开自有工程并核对持久化，共 7 项语义断言。
- 只对本轮创建/打开的 `Project` 调用 Close，只对本轮创建的 `TiaPortal` 调用 Dispose；不调用 `TiaPortalProcess.Dispose`，不操作用户已有实例。无法从单纯 Dispose 返回推断 TIA 服务进程一定已经退出。
- 正常完成后在 `finally` 中清理 scratch：核对路径边界、随机所有权标记、祖先及子树中的 reparse point。原生失败或清理失败保留目录和证据；不复用或覆盖已有输出目录。
- 每个测试阶段追加 `BEFORE/RETURNED/THREW` 并 Flush 到磁盘；记录周期及自己创建实例的 PID。日志中的事务等阶段仍含多个 API 调用，只能定位阶段，不能据此宣布某个具体 API 是崩溃根因。
- [监督器](../../scripts/checks/Test-NativeLifecycle.py) 给整个测试进程设置期限，捕捉异常退出、超时及结果不完整。只终止它启动的测试程序，不结束 TIA 进程、不遍历用户进程、不自动重试操作。超时后原生结果未知，TIA 可能仍运行，需结合 OWNED_PORTAL 记录人工检查；构造 TIA 时卡住可能来不及记录 PID。
- 只有进程正常退出、报告中的版本/轮次/7 项断言均正确、各阶段日志完全配对、所有轮次和清理均完成，监督器才报告 PASSED。

这套程序测试原始 Openness 的最小生命周期，不经 MCP 工具分发，不能替代实际注册工具的原生验收。尚未覆盖 PLC/HMI 内容、编译、导入、原生并发争用、超时 Attach 晚到代理释放或生产 MCP 工作进程隔离。测试进程监督器也不等于生产 MCP 已具备原生进程隔离。

## 本地构建和默认行为

现有 `Build-Release.ps1` 自动编译两版，仅运行各自的 `--self-test` 和 Python 监督器的假进程故障测试；不运行 live 分支。单独构建示例（将 SDK 路径替换为实际路径）：

```powershell
dotnet build tests/Engine/TiaMcpServer.NativeTests/V20/NativeTests.V20.csproj -c Release -p:SiemensEngineeringDirectory=C:\SDK\V20
dotnet build tests/Engine/TiaMcpServer.NativeTests/V21/NativeTests.V21.csproj -c Release -p:SiemensEngineeringDirectory=C:\SDK\V21\net48
python scripts/checks/Test-NativeLifecycle.py --self-test
& tests/Engine/TiaMcpServer.NativeTests/V21/bin/Release/net48/NativeTests.V21.exe --self-test
```

裸运行 EXE 或监督器均不执行原生测试。EXE 的 `--preflight` 只读取匹配版本的安装注册表和 DLL 身份，不创建 TIA；不能证明许可、组权限或防火墙已配置。编译时可用离线 PublicAPI 副本，运行时必须从匹配的实际 TIA 安装解析。

## 将来获准执行时

以下命令会启动新的 TIA 实例并创建测试工程，仅在明确获准的测试范围内执行。需要匹配 V20/V21 的专用 Windows TIA 安装、产品许可、Siemens TIA Openness 组权限，以及可信测试 EXE 的防火墙许可。程序不修改组或注册表、不点击防火墙对话框。不能在通用 GitHub hosted runner 上宣布完成原生验收。

```powershell
python scripts/checks/Test-NativeLifecycle.py `
  --runner tests/Engine/TiaMcpServer.NativeTests/V21/bin/Release/net48/NativeTests.V21.exe `
  --major 21 --run-live --confirm-new-portal `
  --output C:\TIA-Test-Runs\new-unique-run --iterations 1 --timeout-seconds 600
```

先从 1 轮开始；10 轮及以上属于后续原生耐久测试，不能用离线结果替代。`--timeout-seconds` 是总期限（含启动、所有轮次及清理），不是每个 API 的独立取消能力。输出保留 `supervisor-result.json`、`runner-output.log`、`native/native-result.json`（正常返回时）和 `native/native-events.jsonl`；报告缺失本身就是失败证据。

## 官方依据及后续边界

遵循项目固定版本的 Siemens [openness-testing](../../reference/siemens-openness/skills/openness-testing/SKILL.md)、[session-and-project](../../reference/siemens-openness/skills/session-and-project/SKILL.md)、[threading-and-concurrency](../../reference/siemens-openness/skills/threading-and-concurrency/SKILL.md) 和 [crash-diagnosis](../../reference/siemens-openness/skills/crash-diagnosis/SKILL.md) 的适用流程。指南面向 V21+；V20 适配另外依据本地 V20 PublicAPI 编译核对，不宣称官方指南保证 V20 行为。

V20/V21 提供可选生产 MCP worker，见[使用说明](../guides/openness-worker-isolation.md)与[诊断和隔离边界](native-call-diagnostics.md)。本框架仍独立于 MCP 分发；两者的本地测试都不能证明 TIA 不会崩溃。
