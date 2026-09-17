# 第三方组件许可证清单

[文档目录](../README.md) · [NOTICE](../../NOTICE.md)

本清单覆盖 `runtime/v20` 与 `runtime/v21` 中随交付包分发的**全部**第三方程序集。版本与许可证信息取自构建时实际还原的 NuGet 包元数据（`.nuspec`）及包内嵌许可证文件，核对日期 2026-09-17。本目录中的许可证原文来自对应 NuGet 包内嵌文件；未内嵌原文的以官方链接标注。

Siemens PublicAPI（`Siemens.Engineering*.dll`）由用户本机已安装并取得许可的 TIA Portal 提供，**不随本包分发**，不在此列。

## 引擎依赖（`runtime/v20`、`runtime/v21`）

| 程序集 | NuGet 包 / 版本 | 许可证 | 版权 | 原文 |
|---|---|---|---|---|
| `Esprima.dll` | Esprima 3.0.5 | BSD-3-Clause | Sebastien Ros | [Esprima-3.0.5.txt](Esprima-3.0.5.txt) |
| `Sharp7.dll` | Sharp7 1.1.84 | MIT | © 2018 Federico Barresi | [Sharp7-1.1.84.txt](Sharp7-1.1.84.txt) |
| `Workstation.UaClient.dll` | Workstation.UaClient 3.2.3 | MIT | © 2023 Converter Systems LLC | [Workstation.UaClient-3.2.3.txt](Workstation.UaClient-3.2.3.txt) |
| `YamlDotNet.dll` | YamlDotNet 13.7.1 | MIT | © 2008–2014 Antoine Aubry and contributors | [YamlDotNet-13.7.1.txt](YamlDotNet-13.7.1.txt) |
| `BouncyCastle.Crypto.dll` | Portable.BouncyCastle 1.9.0（Workstation.UaClient 依赖） | Bouncy Castle Licence（MIT X11 改编） | © 2000–2021 Legion of the Bouncy Castle Inc. | [官方许可证页](https://www.bouncycastle.org/about/license/) |
| `ModelContextProtocol.dll`、`ModelContextProtocol.Core.dll` | ModelContextProtocol 0.3.0-preview.4 | MIT | © Anthropic and Contributors | 标准 MIT，见 [DotNet-Foundation-MIT.txt](DotNet-Foundation-MIT.txt) 同款条款 |
| `Microsoft.Extensions.*.dll`（28 个）、`Microsoft.Bcl.AsyncInterfaces.dll`、`Microsoft.Bcl.Memory.dll`、`System.Text.Json.dll`、`System.IO.Pipelines.dll`、`System.Net.ServerSentEvents.dll`、`System.Text.Encodings.Web.dll`、`System.Diagnostics.DiagnosticSource.dll`、`System.Threading.Channels.dll`、`System.Threading.Tasks.Dataflow.dll`、`System.CodeDom.dll`、`System.Security.AccessControl.dll`、`System.Security.Principal.Windows.dll`、`System.IO.FileSystem.AccessControl.dll`、`System.Buffers.dll`、`System.Memory.dll`、`System.Numerics.Vectors.dll`、`System.Runtime.CompilerServices.Unsafe.dll`、`System.Threading.Tasks.Extensions.dll` | .NET 运行时/扩展包（Microsoft.Extensions.* 10.0.0-preview.4 等，见 `manifest/release-build.json`） | MIT | © .NET Foundation and Contributors / © Microsoft Corporation | [DotNet-Foundation-MIT.txt](DotNet-Foundation-MIT.txt) |
| `System.Reactive.dll` | System.Reactive 5.0.0 | MIT | © .NET Foundation and Contributors | 同上 |
| `Microsoft.IO.RecyclableMemoryStream.dll` | Microsoft.IO.RecyclableMemoryStream 3.0.0 | MIT | © Microsoft Corporation | 同上 |
| `Siemens.Collaboration.Net.dll`、`.CoreExtensions.dll`、`.Logging.dll`、`.OperatingSystem.Windows.dll`、`.Windows.Authentication.dll`（3.0.1725521661）、`.TiaPortal.Openness.Resolver.dll`（1.1.1725480302） | Siemens.Collaboration.Net.* | **Siemens 免版税软件条款**（见下） | © Siemens AG | [Siemens.Collaboration.Net.md](Siemens.Collaboration.Net.md) |

`Siemens.Collaboration.Net.TiaPortal.Packages.Openness` 仅在编译时使用，不随包分发。

## 配置器（`TiaMcpConfigurator.exe`）

仅依赖 .NET Framework 4.8 自带的 WPF 与基础类库，无第三方程序集。

## 关于 Siemens.Collaboration.Net 条款的说明

上述 6 个 Siemens 程序集以**目标码**形式随包分发。其包内许可证（[Siemens.Collaboration.Net.md](Siemens.Collaboration.Net.md)）的结构是：

- 第 2 条：MIT 许可**仅适用于源码及生成的源码**。
- 第 3 条：对目标码适用"Royalty-Free Siemens Software Conditions"——授予**免版税、非独占、不可再许可、不可转让**的使用权，限于"开发和测试可与 Siemens 产品配合使用的软件"这一预期用途。
- 第 1.1 条：除非第 2 条明确授予，不得反编译、翻译、提取、修改或**分发**该软件。

因此，将这些 DLL 打入交付 ZIP 向第三方再分发，与"不可转让 / 不得分发"的措辞之间存在需要维护者自行评估的空间。本清单只如实记录条款，不构成法律意见。可选的处理方式包括：保留现状并在 NOTICE 中明示；或从交付包中剔除这 6 个 DLL、改由安装步骤通过 NuGet 还原；或向 Siemens 确认再分发许可。

## 维护规则

- 引擎依赖版本变更时（`tools/tiaportal-mcp/src/TiaMcpServer/*.csproj`），同步更新本表及对应原文文件。
- 新增会随包分发的程序集，必须先在此登记许可证，再进入 `runtime/`。
- 与 MIT 不兼容的许可证（GPL/AGPL 等）不得以静态链接或打包方式进入交付包；LGPL 组件只能以独立进程或动态链接方式使用并保留替换可能。
