# 脚本索引

日常连接配置使用根目录 `TiaMcpConfigurator.exe`。下列脚本面向维护、诊断或 CLI 用户；示例从仓库根目录执行。

| 分类 | 入口 | 用途 |
|---|---|---|
| 构建 | [Build-Configurator.ps1](build/Build-Configurator.ps1) | 编译 WPF；`-Test` 执行隔离测试并更新记录 |
| 构建 | [Build-Release.ps1](build/Build-Release.ps1) | 使用 Siemens PublicAPI 构建/验证两版引擎 |
| 构建 | [Prepare-Delivery.ps1](build/Prepare-Delivery.ps1) | 沿用已验证引擎，更新交付与 GUI 记录 |
| 发布 | [Package-Release.py](build/Package-Release.py)、[Publish-Release.cjs](build/Publish-Release.cjs) | 干净提交打包；Actions 上传并核对附件后发布 |
| 检查 | [Check-Repository.py](checks/Check-Repository.py)、[Validate-Bundle.ps1](checks/Validate-Bundle.ps1) | 文档/路径与交付内容校验 |
| 检查 | [Check-DeadToolReferences.py](checks/Check-DeadToolReferences.py) | 工具描述死引用检查 |
| 检查 | [Check-LiteProfile.py](checks/Check-LiteProfile.py)、[Test-ResourceDiscovery.py](checks/Test-ResourceDiscovery.py) | 实际 EXE 发现协议，需相应环境或测试 harness |
| 检查 | [Test-DownloadRouteSelection.ps1](checks/Test-DownloadRouteSelection.ps1)、[Test-MatchPlcName.ps1](checks/Test-MatchPlcName.ps1)、[Test-MigrationReadAssembly.ps1](checks/Test-MigrationReadAssembly.ps1) | 路由、名称匹配和程序集专项回归 |
| 生成 | [Generate-ToolCapabilityMatrix.ps1](generate/Generate-ToolCapabilityMatrix.ps1) | 从源码生成工具矩阵 |
| 生成 | [Generate-ToolsList.py](generate/Generate-ToolsList.py)、[Generate-ToolsListFromAssembly.ps1](generate/Generate-ToolsListFromAssembly.ps1) | 从协议或程序集生成工具清单 |
| 诊断 | [Audit-OpennessSurface.py](diagnostics/Audit-OpennessSurface.py)、[Sweep-WrongPathHonesty.py](diagnostics/Sweep-WrongPathHonesty.py) | API 对照、错误路径行为；先确认环境和参数 |
| 诊断 | [Collect-TiaExitEvidence.cmd](diagnostics/Collect-TiaExitEvidence.cmd) | 采集 TIA 退出证据，输出不可直接公开提交 |
| 操作 | [预热.bat](operations/预热.bat)、[生成工程.bat](operations/生成工程.bat) | 可选 CLI 快捷操作，默认选择包内 V21；V20 请直接调用对应 EXE |

详见 [验证说明](../docs/development/validation.md) 和 [发布流程](../docs/development/release-workflow.md)。按旧版本构建/打包脚本和 Python 预热桥接已由统一流程替代并删除。
