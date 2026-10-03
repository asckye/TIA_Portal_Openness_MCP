# 验证分层

[文档目录](../README.md) · [发布流程](release-workflow.md) · [当前交接](handoff.md)

命令从仓库根目录运行。v3.2.0 已完成八个精确 SDK 目标及当前离线功能构建；
新增真实 TIA 工程验收仍为 **NOT RUN**。发布上传、发布后验包和原生验收分别记录。

| 证据 | 位置和用途 |
|---|---|
| V20/V21 完整构建 | [release-build.json](../../manifest/release-build.json)：编译、离线、实际进程、API 形状及诊断记录 |
| 配置器 | [configurator-build.json](../../manifest/configurator-build.json)：源码/文件哈希及界面功能测试 |
| 全版本交付 | [multi-version-build.json](../../manifest/multi-version-build.json)：八版 worker/Studio 适配器、文件与源码哈希 |
| 统一示例 | [tool-usage-coverage.json](../../manifest/tool-usage-coverage.json)：所有注册工具/操作的检索与可执行离线示例 |
| 工具与功能范围 | [版本矩阵](../reference/version-tools.md)及 [功能矩阵](../../reference/version-feature-matrix.json) |

旧日期报告里的“SDK 未提供”“仅 Linux 源码验证”属于历史修订，不覆盖当前构建记录。
机器证据中的历史字段仍保留原意；不要将历史通过数复制成当前测试结果。

## 无需运行 TIA 的检查

```powershell
python scripts/checks/Check-Repository.py
python scripts/checks/Check-DeadToolReferences.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/checks/Validate-Bundle.ps1 -Strict
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Configurator.ps1 -Test
```

仓库检查验证链接、入口和统计；交付检查核对清单、版本、八版运行文件与构建哈希。
控制台用例必须用 `dotnet run` 执行。WPF 检查包括统一主窗口的导航、共同设置、退出清理与渲染；配置模块同时在原 Framework 测试宿主和实际 .NET 10 桌面宿主执行。测试使用隔离配置和模拟 HTTP，覆盖客户端配置、
合并/备份、密钥处理与界面渲染，不修改真实客户端配置或系统网络规则。

GitHub 的 offline-checks 与 validate-bundle 执行相应离线检查。托管 runner 没有 Siemens
PublicAPI，不能替代本机完整构建。修改编译输入后必须重新构建，不能手填 manifest 哈希。

## 完整八版本构建

```powershell
pwsh -NoProfile -File scripts/build/Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Python <python.exe> -Test
```

此命令先运行 V20/V21 `Build-Release.ps1`，再构建 Foundation worker、Studio 及全部适配器，
执行功能和传输检查并生成证据。只有刚完成完整引擎构建才使用 `-SkipFullEngines`。
PLC Tools 功能检查需要现有伴随 Python 环境；可用 `TIA_MCP_PLC_TOOLS_PYTHON` 指向其解释器。
设置 `TIA_MCP_TEST_PUBLIC_API_ROOT` 可运行八版 UDT/GlobalDB 官方 interface XSD 检查；
片段 XSD 通过不代表完整文档或目标 CPU 语义通过。

完整引擎构建包含以下不同层次：

- 两种版本编译符号下的纯逻辑回归、实际 MCP SDK 版本准入检查。
- HTTP/STDIO、full/lite、资源分页、HMI 遍历、文件代理及普通/隔离宿主功能检查。
- 针对实际匹配 SDK 的类型、属性、方法签名、枚举与服务接口核对。
- 原生调用织入、覆盖清单核对、假 API 实际执行与崩溃/管道故障注入。
- 独立原生测试程序的编译和离线安全检查；默认不会启动其 live 分支。
- 全部工具示例与实际签名一致性、按操作的输入/结果解释、语言文件检索及源文件哈希核对。

官方 API 审计的成员名词法引用只提供排查线索，不证明重载正确、路径可达或原生行为成功。
按版本的实现与缺口以功能矩阵为准。

## 压力与故障检查

`Test-LocalStability.py` 对 V20/V21 的普通/隔离进程、两种传输和 full/lite
组合执行正常调用与错误后的恢复检查。默认每组合 50 轮、HTTP 8 并发；
实际范围和结果由完整构建记录保存。

```powershell
python scripts/checks/Test-LocalStability.py --exe runtime/v21/TiaMcpServer.exe --major 21 --public-api TIA_V21_PublicAPI/V21/net48 --host-harness tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe --rounds 50 --output TiaMcp_Output/stability-v21
```

输出目录必须不存在。脚本仅清理它创建的服务，不连接 TIA，不解禁原生交叉引用。
假 worker 的超时、异常退出、错误响应和晚到响应检查，验证宿主故障处理而非 Siemens 内部可靠性。

## 真实工程验收

按[独立生命周期](native-lifecycle-tests.md)和[生产 MCP 会话](native-mcp-session-tests.md)
分别启用原生场景。真实工程需要匹配版本/许可、当前明确的测试目标与操作范围。
从可恢复工程开始，核对设备和版本，执行读取、预览、修改、回读、编译及显式保存，
记录实际错误/警告、未支持对象和失败后的状态。

导入验收应核对对象内容及返回身份，覆盖不允许覆盖、显式覆盖、批次部分完成和依赖顺序；
外部源需区分创建源、生成块与编译。Studio 再验证相应界面命令。在线设备操作单独验收。
HTTP 可达、工具枚举、程序构建或公开发布都不等于工程语义正确。

真实写入边界见[能力说明](../reference/capabilities.md)；尚未解决的原生事件见
[Openness 限制](../troubleshooting/openness-limitations.md)。工程、密钥、现场原始日志及未脱敏截图不进入公开仓库。
