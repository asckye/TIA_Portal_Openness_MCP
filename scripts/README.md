# 脚本索引

日常连接配置使用根目录 `TiaOpenness.exe`。下列脚本面向维护、诊断或 CLI 用户；示例从仓库根目录执行。

独立开发者工具使用相邻的 .NET 10 C# file-based app：`dotnet run <path.cs> -- <args>`。需要反射 net48 引擎程序集的检查由 `tests/Engine/TiaMcp.Engine.Harness` 承载，调用形如 `TiaMcp.Engine.Harness.exe <engine.exe> <mode> <args>`。

| 分类 | 入口 | 用途 |
|---|---|---|
| 构建 | `dotnet run --project build-tools/release -- build-configurator` | 以 Framework csc 编译兼容启动器；`-Test` 需要 .NET 10 SDK，执行工作台配置测试并更新记录 |
| 构建 | `dotnet run --project build-tools/release -- build-release` | 使用 Siemens PublicAPI 构建/验证两版引擎 |
| 构建 | `dotnet run --project build-tools/release -- prepare-delivery` | 沿用已验证引擎，更新交付与 GUI 记录 |
| 发布 | `dotnet run --project build-tools/release -- release` | 一键发布：前置检查 → 组件版本 → build-release / build-multi-version → 本地闸门 → 一次提交 → Package-Release → Verify-ReleaseAsset → 推送 → CI（API）→ tag → Publish-Release 上传 → 等验证工作流检查；先手写 CHANGELOG 条目与 `docs/releases/vX.md`（见[发布流程](../docs/development/release-workflow.md)） |
| 审查 | `dotnet run --project build-tools/release -- run-release-build -PublicApiRoot <SDK-root> -OutputDirectory <new-dir> -MaxParallelism <n>` | 本地完整构建链，构建后并行运行注册、V4 契约/响应比较和重定位检查；`-MaxParallelism 1` 可串行复现 |
| 发布 | [Package-Release.py](../build-tools/Package-Release.py)、`dotnet run --project build-tools/release -- publish` | 干净提交 + 本机二进制打包；本机建草稿 Release、上传 ZIP 与 `.sha256`、回读校验后发布（2.8.1 起二进制不入库、不再由 Actions 打包） |
| 构建 | `dotnet run --project build-tools/release -- build-multi-version` / [Package-MultiVersion.py](../build-tools/Package-MultiVersion.py) | 八版本构建、功能验证和 API 对照；正式发布调用该构建，Package-MultiVersion 另用于本地开发包 |
| 构建 | `dotnet run --project build-tools/release -- build-studio` | 构建 Studio、本地 Bridge 和 八版本原生适配器；`-Test` 运行客户端及 WPF 功能测试，不启动 TIA |
| 检查 | [Check-Repository.py](checks/Check-Repository.py)、`dotnet run --project build-tools/release -- validate-bundle`、[Verify-ReleaseAsset.py](checks/Verify-ReleaseAsset.py) | 文档/路径与交付内容校验（`--no-binaries` / `-NoBinaries` 用于没有二进制的源码 checkout）；ZIP 与提交树 + 清单哈希逐文件比对（本机上传前、`Verify published release` 工作流发布后各跑一次） |
| 检查 | [Check-DeadToolReferences.py](checks/Check-DeadToolReferences.py) | 工具描述死引用检查 |
| 手动检查 | [Check-LiteProfile.py](checks/Check-LiteProfile.py) | lite 清单专项检查，需已构建引擎与测试 harness；默认 CI/发布链不执行 |
| 检查 | [Test-ResourceDiscovery.py](checks/Test-ResourceDiscovery.py) | 实际 EXE 发现协议，发布链执行；需相应环境或测试 harness |
| 检查 | [Test-FoundationTransport.py](checks/Test-FoundationTransport.py)、[Test-LocalStability.py](checks/Test-LocalStability.py) | 本地 worker 故障恢复、分页与宿主退出；普通/隔离模式压力及日志配对。使用测试宿主，不连接 TIA；双版本构建门自动执行 |
| 检查 | [Test-DownloadRouteSelection.cs](checks/Test-DownloadRouteSelection.cs)、[Test-MatchPlcName.cs](checks/Test-MatchPlcName.cs) | 路由与名称匹配回归；可用 `-SourceOnly` 编译生产成员，否则转到 net48 TiaMcp.Engine.Harness 反射引擎 |
| 检查 | `TiaMcp.Engine.Harness.exe <engine.exe> test-migration-read-assembly <PublicAPI>`、`test-ecosystem-assembly <PublicAPI>` | 反射迁移程序集和生态工具行为；由 Build-Release 调用，离线缺依赖时可加 `--skip-pdf --skip-companion` |
| 检查 | [Test-AdapterInputs.cs](../src/Adapters/build/Test-AdapterInputs.cs)、[Test-WorkerIsolation.cs](../src/Adapters/build/Test-WorkerIsolation.cs) | C# file-based app：适配器输入选择与八版 worker 隔离证据 |
| 检查 | [TiaMcp.ShippedTools.Tests](../tests/Tools/TiaMcp.ShippedTools.Tests/TiaMcp.ShippedTools.Tests.csproj) | Claude Code 写保护钩子的拒绝/放行/审计及真实 stdin/stdout 自检；由 Build-Release 调用 |
| 生成 | [Generate-ToolUsage.py](generate/Generate-ToolUsage.py)、[Audit-ToolUsage.py](diagnostics/Audit-ToolUsage.py)、[Audit-VersionTools.py](diagnostics/Audit-VersionTools.py) | 统一示例、八版真实 MCP 检索与按版本 API 对照 |
| 生成 | [Generate-ToolCapabilityMatrix.cs](generate/Generate-ToolCapabilityMatrix.cs) | 从 manifest/tools-list.json 按大类→域生成工具矩阵，支持 `--check`（由 Build-Release 调用） |
| 生成 | `TiaMcp.Engine.Harness.exe <engine.exe> generate-tools-list <PublicAPI> <output> <package>` | 从已编译程序集反射生成 `manifest/tools-list.json`，验证 ToolExamples / ToolRecipes（由 Build-Release 调用） |
| 诊断 | [Audit-OpennessCoverage.cs](diagnostics/Audit-OpennessCoverage.cs) | 逐成员对照官方 PublicAPI XML 与引擎源码，保留的 V21 词法诊断；当前八版报告由 Audit-VersionTools.py 生成，不用旧词法计数覆盖当前报告 |
| 诊断 | [Sweep-WrongPathHonesty.py](diagnostics/Sweep-WrongPathHonesty.py) | 手动诊断：给只读工具喂不存在的路径，找出误报成功的工具；需真实工程与 TIA |
| 诊断 | [Workbench 诊断包](../src/Studio/Gui/Services/TiaExitEvidenceCollector.cs) | 一键包含最近 24 小时的 TIA/Windows 退出事件、TIA 进程清单、dump 文件清单和原生导出日志；不会复制 dump 内容，诊断包需按隐私说明审阅后再分享 |
| 诊断 | [LibraryRenameProbe](diagnostics/LibraryRenameProbe/README.md) | VM 双击 `LibraryRenameProbe.exe` 运行隔离测试；离线自检包含监督器场景；用 `dotnet run scripts/diagnostics/LibraryRenameProbe/BuildPackage.cs -- --sdk-directory <V21 net48 PublicAPI>` 构建单独 ZIP |
| 诊断 | [Probe-McpServer.py](diagnostics/Probe-McpServer.py) | 直连 MCP 服务的探针（不经 MCP 客户端、绕过代理）：`tools [关键字]` / `call <lite 工具> '{…}'` / `bridge <任意工具> @args.json`；连接信息取 `~/.claude.json` 的 `tia-portal-vm` 或 `--url` / `--token` |
| 诊断 | [campaign/](diagnostics/campaign/) | 真机批跑：`camp.py`（单调 / 按 plan 批跑并记 ledger）、`rawmsg.py`（UTF-8 看原文）、`plans/plan_*.py`（各族测试计划）、`make_ledger.py`（生成[真机台账](../docs/reference/real-machine-ledger.md)）、`export_full.py`；运行前按[原生验收说明](../docs/development/validation.md)选择当前测试环境；脚本内旧环境值需核实 |
| 诊断 | [openness-dynamic-coverage.json](diagnostics/openness-dynamic-coverage.json) | 经反射 / 泛型到达、类型名不出现在源码里的 Openness 类型登记表（`pattern` / `tool` / `mechanism` / `verified`），覆盖审计据此计入"动态覆盖" |
| 操作 | [CLI 入门](../docs/getting-started/cli.md) | 生成工程和预热直接运行 `runtime\v21\worker\TiaMcp.Engine.V21.exe gen <spec>` / `prewarm`；V20 使用对应 `runtime\v20` EXE |
| 操作 | [vci-watch](operations/vci-watch/watch.py) | `python watch.py --register-task [--interval-minutes 10]` 注册当前用户的 `pythonw.exe` 计划任务；`python watch.py --register-task --remove` 删除 |
| 操作 | `runtime/tools/TiaMcp.Updater.exe` | 在已解压的交付包里更新或回滚；手动运行时从包根传入 `-InstallRoot .`；支持 `-Check`、`-Version`、`-Force`、`-Repository`、`-TimeoutSeconds`、`-WaitForPid`、`-Rollback` 和 `-RelaunchConfigurator`；不连接 TIA、不结束进程，保留用户数据及未归更新器所有的文件 |

| 手动检查 | [Test-CampaignInputs.py](checks/Test-CampaignInputs.py)、[Test-ExternalSourceDispatch.py](checks/Test-ExternalSourceDispatch.py)、[Test-HmiImportSafety.py](checks/Test-HmiImportSafety.py)、[Test-PilotTools.py](checks/Test-PilotTools.py)、[Test-PlcEditingMcp.py](checks/Test-PlcEditingMcp.py)、[Test-TechnologyImportSafety.py](checks/Test-TechnologyImportSafety.py)、[Test-SharedNativeIlReader.py](checks/Test-SharedNativeIlReader.py)、[Test-SharedNativeMigration.cs](checks/Test-SharedNativeMigration.cs) | 改动对应输入、派发、导入安全、工具或共享原生路径后按验证说明选择运行；默认 CI/发布链不执行，不使用 live 分支 |

详见 [验证说明](../docs/development/validation.md) 和 [发布流程](../docs/development/release-workflow.md)。PowerShell、batch、cmd 构建发布入口已由 .NET release tool 替代并删除。
