# 第三方组件许可证清单

[文档目录](../README.md) · [NOTICE](../../NOTICE.md)

本清单登记随 v3.2.0 八版 MCP、Studio 和配置器交付的第三方组件及源码来源。下表按组件范围列示，早期 V20/V21 依赖项保留其核对版本；实际文件版本与哈希以 `manifest/release-build.json` 和 `manifest/multi-version-build.json` 为准。许可原文和版权声明继续保留，不将一个运行时的依赖清单视为所有适配器都相同。

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
| `Siemens.Simatic.S7.Webserver.API.dll`（2.7.18 起，S7 Web 服务器 API 运行时通道） | Siemens.Simatic.S7.Webserver.API 3.3.76 | MIT | © 2023 Siemens Aktiengesellschaft | [Siemens.Simatic.S7.Webserver.API-3.3.76.txt](Siemens.Simatic.S7.Webserver.API-3.3.76.txt) |
| `Newtonsoft.Json.dll`（2.7.18 起，Webserver API 依赖） | Newtonsoft.Json 13.0.4 | MIT | © 2007 James Newton-King | [Newtonsoft.Json-13.0.4.txt](Newtonsoft.Json-13.0.4.txt) |
| `MimeMapping.dll`（2.7.18 起，Webserver API 依赖） | MimeMapping 4.0.0 | MIT | Matthew Little | [MimeMapping-4.0.0.txt](MimeMapping-4.0.0.txt) |

`Siemens.Simatic.Simulation.Runtime.Api.x64.dll`（S7-PLCSIM Advanced API，2.7.19 起的 `Simulation` 域工具）同样不随包分发：引擎在运行时从本机 PLCSIM Advanced 安装目录定位并经反射调用，未安装时工具返回 `ApiNotFound`。

`Siemens.Collaboration.Net.TiaPortal.Packages.Openness` 仅在编译时使用，不随包分发。2.7.18 引入 Webserver API 后，`Microsoft.Extensions.Logging.Abstractions`、`Microsoft.Extensions.DependencyInjection.Abstractions`、`System.Diagnostics.DiagnosticSource` 由传递依赖提升到 10.0.x 正式版（仍为 MIT），其余 Microsoft.Extensions.* 保持 10.0.0-preview.4；以 `manifest/release-build.json` 的逐文件记录为准。

## 随包 .NET 运行时（`runtime/dotnet`）

3.3.0 起随包分发 Microsoft .NET 10.0.12 运行时（Microsoft.NETCore.App、Microsoft.AspNetCore.App、Microsoft.WindowsDesktop.App），取自微软官方压缩包 `aspnetcore-runtime-10.0.12-win-x64.zip` 与 `windowsdesktop-runtime-10.0.12-win-x64.zip`，按 [scripts/build/bundled-dotnet.json](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/scripts/build/bundled-dotnet.json) 记录的 SHA-512 校验后原样展开，不做修改。许可证为 MIT（© .NET Foundation and Contributors），原文与第三方声明随运行时保留在 `runtime/dotnet/LICENSE.txt`、`runtime/dotnet/ThirdPartyNotices.txt`；参见 [DotNet-Foundation-MIT.txt](DotNet-Foundation-MIT.txt)。

## 仅构建期的诊断覆盖工具

`build-tools/native-call-weaver` 使用 [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6)，作者 Jb Evain，MIT 许可证（[官方源码许可证](https://github.com/jbevain/cecil/blob/0.11.6/LICENSE.txt)）。通过 NuGet 还原，仅处理本项目的 Release 中间程序集；`Mono.Cecil.dll` 和构建工具二进制不随引擎运行目录分发，不修改 Siemens PublicAPI。

## 配置器（`TiaOpenness.exe`）

仅依赖 .NET Framework 4.8 自带的 WPF 与基础类库，无第三方程序集。

## 随包生态集成

| 组件 | 固定来源 | 许可与本地文件 |
|---|---|---|
| TiaGitAddIn.Core：SimaticML、结构比较、LAD 布局 | Czarnak/tia-git-addin `01d0bca9f92da9052aeb8f3130d25edb1847a22b` | MIT，© 2026 Łukasz Czarnacki；[许可证](TiaGitAddIn.Core-LICENSE.txt)、[来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/TiaGitAddIn.Core/UPSTREAM.json)。构建新增 `TiaGitAddIn.Core.dll`。 |
| Siemens 官方 Openness 指南 | siemens/tia-portal-ai-extensions `b5c7041648dc10f9225ef082306ade6c335b8771` | MIT，© 2026 Siemens AG；[许可证](../../reference/siemens-openness/LICENSE)、[来源](../../reference/siemens-openness/UPSTREAM.json)。2026-10-01 核对更新，32 个指南正文未变化，本地保留稳定的 skills 路径；作为资料，不自动执行。 |
| Siemens OPC UA 接口生成源码 | tia-portal-applications/tia-addin-opc-ua-modelled-interface `317dfd06dae840ff229e721b140de4b89829120e` | 保留[完整 Siemens 条款](SiemensOpcUaModelled-LICENSE.md)；第 2 节对源代码及生成源代码授予 MIT，© 2022 Siemens AG；[改动记录](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/SiemensOpcUaModelled/UPSTREAM.json)。未复制上游 Add-In 二进制。 |
| PLC Tools 的八个 Python 包及根 CLI | core-engineering/siemens-plc-tools `887eea1ea3495832648f47e42759320141952717` | MIT；[许可证](../../third_party/siemens-plc-tools/LICENSE)、[改动记录](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/siemens-plc-tools/UPSTREAM.json)。以独立 Python 进程运行，安装环境和依赖不随源码入库。 |
| SimaticML Decoder 0.2.3 | Czarnak/simaticml-decoder `2f100023c2d6f4e7a4df02cbd04975de48f5d207` | MIT，© 2026 Łukasz Czarnacki；[许可证](../../third_party/simaticml-decoder/LICENSE)、[来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/simaticml-decoder/UPSTREAM.json)。原样源码、独立 Python 进程、只读分析；输出不可回编译或导入。上游测试语料不随本项目分发。 |

`TiaUtilities` 和 `tia-linter` 的 GPL 源码未复制。布尔别名/报警生成、XML 模板展开及通用工程质量规则为独立实现。Repsay 客户端和 Czarnak MCP 的功能需求通过本项目现有 API 与新批量协议实现，未复制其源代码。Python 依赖由安装脚本单独还原，包括可选 PDF 审计报告使用的 ReportLab；安装环境不打包进交付仓库。

## 关于 Siemens.Collaboration.Net 条款的说明

上述 6 个 Siemens 程序集以**目标码**形式随包分发。其包内许可证（[Siemens.Collaboration.Net.md](Siemens.Collaboration.Net.md)）的结构是：

- 第 2 条：MIT 许可**仅适用于源码及生成的源码**。
- 第 3 条：对目标码适用"Royalty-Free Siemens Software Conditions"——授予**免版税、非独占、不可再许可、不可转让**的使用权，限于"开发和测试可与 Siemens 产品配合使用的软件"这一预期用途。
- 第 1.1 条：除非第 2 条明确授予，不得反编译、翻译、提取、修改或**分发**该软件。

因此，将这些 DLL 打入交付 ZIP 向第三方再分发，与"不可转让 / 不得分发"的措辞之间存在需要维护者自行评估的空间。本清单只如实记录条款，不构成法律意见。可选的处理方式包括：保留现状并在 NOTICE 中明示；或从交付包中剔除这 6 个 DLL、改由安装步骤通过 NuGet 还原；或向 Siemens 确认再分发许可。

## 维护规则

- 引擎依赖版本变更时（`src/Engine/*.csproj`），同步更新本表及对应原文文件。
- 新增会随包分发的程序集，必须先在此登记许可证，再进入 `runtime/`。
- 与 MIT 不兼容的许可证（GPL/AGPL 等）不得以静态链接或打包方式进入交付包；LGPL 组件只能以独立进程或动态链接方式使用并保留替换可能。

## TIA Openness Studio desktop integration

- Source: [asckye/tia-openness-studio](https://github.com/asckye/tia-openness-studio), commit `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02`, v2.4.0.
- Copyright (c) 2026 asckye; [MIT license](TiaOpennessStudio-LICENSE.txt).
- Integrated WPF UI, contracts, typed client, mock, inspection/diff helpers and tests. The integrated desktop now uses a direct Openness bridge with eight version-specific adapters and no MCP transport. No upstream native DLL or Siemens SDK is included in the desktop build.
- [Provenance and archive hashes](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/tia-openness-studio/upstream.json); [integration and behavior](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/src/Studio/README.md). The full original source and complete Git history are retained as reference archives.
- Runtime NuGet dependency: Newtonsoft.Json 13.0.3, MIT (already listed in this repository). Test-only: Microsoft.NET.Test.Sdk 17.12.0, xUnit 2.9.2 and Visual Studio runner 2.8.2.

## Primer desktop fonts

JetBrains Mono (JetBrains) and Noto Sans SC (Noto CJK) are embedded in the WPF
workbench under SIL Open Font License 1.1. Their original [JetBrains Mono license](JetBrainsMono-OFL.txt)
and [Noto Sans SC license](NotoSansSC-OFL.txt) are included here. Source URLs and hashes
remain in the source repository. Segoe UI is supplied by Windows. No fonts are installed system-wide.

## Eido import dependency planner

MIT, copyright (c) 2026 EIDO AUTOMATION, S.L.U. The [original license](Eido-LICENSE.txt) is included here; implementation provenance remains in the source repository. Both MCP profiles share this implementation.

The foundation HTTP transport uses ModelContextProtocol.AspNetCore 0.3.0-preview.4 (MIT), matching the existing MCP SDK, and the Microsoft.AspNetCore.App shared framework. No independent HTTP MCP protocol implementation is copied from another repository.

## Embedded official example reference

The complete C# source corpus of [Siemens TIA Portal Openness Code Snippets](https://github.com/siemens/tia-portal-openness-code-snippets)
is retained as reference text at commit `4a8cc79d0666633e524e52f3335d99ff993f8830`,
copyright Siemens 2025-2026. Section 2 of the [original license](SiemensCodeSnippets-LICENSE.md)
grants MIT terms for source code. All original notices remain. The complete license is bundled here; source/dependency files and provenance
remain in the source repository; no upstream executable, SDK or sample engineering archive is included.
The generated MCP catalog also embeds the previously vendored MIT Siemens AI extension
guides. It does not compile or execute these references. Project summaries and MCP
argument templates are identified separately from official source text.
