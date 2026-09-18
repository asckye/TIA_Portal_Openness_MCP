# 验证分层

[文档目录](../README.md) · [发布流程](release-workflow.md)

命令从仓库根目录执行。结果分别存于 [引擎记录](../../manifest/release-build.json) 和 [配置器记录](../../manifest/configurator-build.json)。离线检查不代表真实 TIA 验收。

## 无需安装 TIA

```powershell
python scripts/checks/Check-Repository.py
python scripts/checks/Check-DeadToolReferences.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/checks/Validate-Bundle.ps1 -Strict
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Configurator.ps1 -Test
```

- 仓库检查：本地文档链接、入口和蓝图路径、旧启动器残留、工具统计。
- 交付检查：JSON、模板、双版本文件、构建哈希及必需入口。
- 离线回归：需 .NET 8 SDK / 运行时。这是控制台程序，必须使用 `dotnet run`；`dotnet test` 不会执行这些用例。
- WPF：需 Windows / .NET Framework 4.8；使用隔离配置和模拟 HTTP，验证 11 张客户端卡片、合并/备份、密钥保护及 XAML 渲染，不写真实配置或网络规则。`-Test` 会重编配置器并更新记录；发布前执行 Prepare-Delivery 绑定新记录。

GitHub `offline-checks` 和 `validate-bundle` 运行对应检查。完整引擎编译需要 Siemens PublicAPI，托管 runner 不具备该环境。

## PublicAPI / 实际 EXE 检查

[Build-Release.ps1](../../scripts/build/Build-Release.ps1) 使用本地匹配 PublicAPI 构建 V20/V21，执行实际 EXE 的 HTTP、资源发现、HMI 遍历、文件代理和 API 签名检查。测试数量和日期写入 release-build，不在本页重复维护版本快照。

专项脚本在 [scripts/checks](../../scripts/checks)。`Check-LiteProfile.py` 等协议探测可能启动服务，只在匹配环境执行。诊断脚本在 `scripts/diagnostics`，不是无 TIA 的 CI 项目。

## 真实工程验收

在可恢复工程上确认设备、许可证、语言和安装版本，再进行读取、预览、授权修改、回读、编译及保存，记录实际错误/警告和未支持项。在线设备操作、网络/UAC、各 AI 客户端需分别验收。HTTP 可达、工具枚举、程序集加载或发布成功均不等于工程语义正确。

真实写入验收状态见 [能力边界](../reference/capabilities.md)。不向公开仓库提交工程、密钥、现场日志或未经脱敏的截图。
