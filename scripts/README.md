# 脚本索引

日常连接配置使用根目录 `TiaMcpConfigurator.exe`。下列脚本面向维护、诊断或 CLI 用户；示例从仓库根目录执行。

| 分类 | 入口 | 用途 |
|---|---|---|
| 构建 | [Build-Configurator.ps1](build/Build-Configurator.ps1) | 编译 WPF；`-Test` 执行隔离测试并更新记录 |
| 构建 | [Build-Release.ps1](build/Build-Release.ps1) | 使用 Siemens PublicAPI 构建/验证两版引擎 |
| 构建 | [Prepare-Delivery.ps1](build/Prepare-Delivery.ps1) | 沿用已验证引擎，更新交付与 GUI 记录 |
| 发布 | [Release.ps1](build/Release.ps1) | 一键发布：前置检查 → 版本号 8 处 → Build-Release → 本地闸门 → 一次提交 → Package-Release → Verify-ReleaseAsset → 推送 → CI（API）→ tag → Publish-Release 上传 → 等验证工作流检查；先手写 CHANGELOG 条目与 `docs/releases/vX.md`（见[发布流程](../docs/development/release-workflow.md)） |
| 发布 | [Package-Release.py](build/Package-Release.py)、[Publish-Release.ps1](build/Publish-Release.ps1) | 干净提交 + 本机二进制打包；本机建草稿 Release、上传 ZIP 与 `.sha256`、回读校验后发布（2.8.1 起二进制不入库、不再由 Actions 打包） |
| 检查 | [Check-Repository.py](checks/Check-Repository.py)、[Validate-Bundle.ps1](checks/Validate-Bundle.ps1)、[Verify-ReleaseAsset.py](checks/Verify-ReleaseAsset.py) | 文档/路径与交付内容校验（`--no-binaries` / `-NoBinaries` 用于没有二进制的源码 checkout）；ZIP 与提交树 + 清单哈希逐文件比对（本机上传前、`Verify published release` 工作流发布后各跑一次） |
| 检查 | [Check-DeadToolReferences.py](checks/Check-DeadToolReferences.py) | 工具描述死引用检查 |
| 检查 | [Check-LiteProfile.py](checks/Check-LiteProfile.py)、[Test-ResourceDiscovery.py](checks/Test-ResourceDiscovery.py) | 实际 EXE 发现协议，需相应环境或测试 harness |
| 检查 | [Test-DownloadRouteSelection.ps1](checks/Test-DownloadRouteSelection.ps1)、[Test-MatchPlcName.ps1](checks/Test-MatchPlcName.ps1)、[Test-MigrationReadAssembly.ps1](checks/Test-MigrationReadAssembly.ps1) | 路由、名称匹配和程序集专项回归，反射已发布的 V21 EXE；均由 Build-Release 调用（路由测试需 -PublicApiDirectory） |
| 检查 | [Test-WriteGuard.ps1](checks/Test-WriteGuard.ps1) | Claude Code 写保护钩子 `hooks/tia-write-guard.ps1` 的拒绝/放行/审计自检；由 Build-Release 调用 |
| 生成 | [Generate-ToolCapabilityMatrix.ps1](generate/Generate-ToolCapabilityMatrix.ps1) | 从 manifest/tools-list.json 按大类→域生成工具矩阵（由 Build-Release 调用） |
| 生成 | [Generate-ToolsListFromAssembly.ps1](generate/Generate-ToolsListFromAssembly.ps1) | 从已编译程序集反射生成 `manifest/tools-list.json`（由 Build-Release 调用） |
| 诊断 | [Audit-OpennessCoverage.ps1](diagnostics/Audit-OpennessCoverage.ps1) | 逐成员对照官方 PublicAPI XML 与引擎源码，生成 [覆盖清单](../docs/reference/openness-coverage.md) 的统计表；需本机 PublicAPI，不加载 DLL |
| 诊断 | [Sweep-WrongPathHonesty.py](diagnostics/Sweep-WrongPathHonesty.py) | 手动诊断：给只读工具喂不存在的路径，找出误报成功的工具；需真实工程与 TIA |
| 诊断 | [Collect-TiaExitEvidence.cmd](diagnostics/Collect-TiaExitEvidence.cmd) | 采集 TIA 退出证据，输出不可直接公开提交 |
| 诊断 | [Probe-McpServer.py](diagnostics/Probe-McpServer.py) | 直连 MCP 服务的探针（不经 MCP 客户端、绕过代理）：`tools [关键字]` / `call <lite 工具> '{…}'` / `bridge <任意工具> @args.json`；连接信息取 `~/.claude.json` 的 `tia-portal-vm` 或 `--url` / `--token` |
| 诊断 | [campaign/](diagnostics/campaign/) | 真机批跑：`camp.py`（单调 / 按 plan 批跑并记 ledger）、`rawmsg.py`（UTF-8 看原文）、`plans/plan_*.py`（各族测试计划）、`make_ledger.py`（生成[真机台账](../docs/reference/real-machine-ledger.md)）、`export_full.py`；用法见[换机器交接单 §4](../docs/development/handoff-checklist.md) |
| 诊断 | [openness-dynamic-coverage.json](diagnostics/openness-dynamic-coverage.json) | 经反射 / 泛型到达、类型名不出现在源码里的 Openness 类型登记表（`pattern` / `tool` / `mechanism` / `verified`），覆盖审计据此计入"动态覆盖" |
| 操作 | [预热.bat](operations/预热.bat)、[生成工程.bat](operations/生成工程.bat) | 可选 CLI 快捷操作，默认选择包内 V21；V20 请直接调用对应 EXE |
| 操作 | [Update-Engine.ps1](operations/Update-Engine.ps1) | 在已解压的交付包里就地更新到最新 GitHub Release（只在线，机器要能访问 github.com；2.7.62 去掉了离线包路径）：引擎在运行就拒绝并列出 pid，下载 ZIP + `.sha256` 校验、解压到 `%TEMP%`、备份到 `.previous\`、用 robocopy 替换 `runtime\` / `manifest\` 并覆盖其余文件（2.8.0 起不受 260 字符路径限制）；`-Check` 只比版本，`-Rollback` 换回上一版；`-WaitForPid` / `-RelaunchConfigurator` 供配置器菜单“更新引擎…”调用；不碰 TIA 与客户端配置 |

详见 [验证说明](../docs/development/validation.md) 和 [发布流程](../docs/development/release-workflow.md)。按旧版本构建/打包脚本和 Python 预热桥接已由统一流程替代并删除。
