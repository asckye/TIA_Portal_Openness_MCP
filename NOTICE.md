# 来源、版权与独立维护声明

自动化工程工作台（Automation Engineering Workbench，原名 TIA Portal Openness MCP）由 **asckye** 独立开发和维护，最初派生自下列 MIT 许可项目；当前与原项目及其作者没有隶属或维护关系。代码、文档和模板包含从该项目取得的成果及其后续修改：

- 原项目：[bulaofen0036-coder/TIA_Portal_Openness_MCP](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP)
- 原始版权：**Copyright (c) 2026 bulaofen0036-coder**。
- 原项目贡献者包括 bulaofen0036-coder / bulaofen、xl 及其他贡献者。原有成果的作者身份和权利不因仓库独立维护而转移。
- 当前维护者版权：**Copyright (c) 2026 asckye**。
- 完整 MIT 许可证、原始版权声明和当前维护者版权声明保存在根目录 [LICENSE](LICENSE)，必须随软件的复制件或实质性部分一同保留。随包分发的第三方程序集及其许可证逐项列于 [第三方组件许可证清单](docs/licenses/THIRD-PARTY-NOTICES.md)，原文在 [docs/licenses](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/licenses)。其中 `Siemens.Collaboration.Net.*` 程序集适用 Siemens 免版税软件条款而非 MIT，其再分发边界见该清单说明。

独立维护、仓库所有者、插件发布者以及 Git 快照导入者均不等于全部原始代码的作者。新的 Git 根提交表示现有许可代码的导入和独立维护起点，不宣称全部代码为重新创作，也不代表原作者对本项目的背书。

本项目使用自己的 `master`、问题反馈、安全报告、构建及 Release 发布流程。以上原项目链接仅用于来源和版权说明，不是同步源、发布入口或维护联系方式。

## 名称与商标

公开名称自 4.0 起改为“自动化工程工作台”（Automation Engineering Workbench）。可执行文件（`TiaOpenness.exe`、`TiaMcp.*`）、程序集、命名空间、交付包 ZIP、Claude Code 插件 ID `tia-portal-openness-mcp`、MCP 服务名 `tia-portal` 和 GitHub 仓库地址暂保留原名，以兼容现有配置。

TIA Portal、SIMATIC、STEP 7、WinCC 和 Openness 是 Siemens AG 的商标；本项目与 Siemens 无关联，也未获得 Siemens 的认可或背书。

TIA Portal, SIMATIC, STEP 7, WinCC and Openness are trademarks of Siemens AG; this project is not affiliated with or endorsed by Siemens.

## 历史快照

独立化前的完整 Git 历史已在维护者本地备份。独立仓库的旧版本标签采用保留原文件树的无父提交快照：导入者记录为当前维护者，原文件中的版权与许可证保持不变。旧 Release 附件保留为原始发布记录，其中的旧提交编号是构建时的来源记录。

后续交付包应同时包含本文件、LICENSE 和依赖许可证。独立化后继续按 `vX.Y.Z` 发布完整的 V20/V21 交付包。

2026-09-29 未发布的生态扩展另包含 Siemens 官方指南、Siemens OPC UA 接口生成源代码、Czarnak 的 TiaGitAddIn.Core 和 core-engineering 的 PLC Tools。固定提交、版权、许可及本地改动逐项记录在[第三方组件清单](docs/licenses/THIRD-PARTY-NOTICES.md)。上述作者不因此成为本项目的维护者或背书方。

2026-10-03: TIA Openness Studio desktop, mock and inspection sources are integrated under `src/Studio`, from asckye/tia-openness-studio commit `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02`, MIT, copyright 2026 asckye. The upstream license, file hashes, full source snapshot and Git history are retained there. The integrated desktop uses its own direct Openness bridge; the MCP dependency has been removed.

The workbench embeds Noto Sans SC and JetBrains Mono under SIL Open Font License 1.1. The fonts, original licenses and source records are retained together in [the Studio font directory](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/src/Studio/Gui/Fonts); see [the third-party notices](docs/licenses/THIRD-PARTY-NOTICES.md).

The shared import dependency planner incorporates MIT-licensed code from EidoAut/EidoTiaWorkbench, copyright (c) 2026 EIDO AUTOMATION, S.L.U.; see [license and pinned provenance](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/eido-import-planner/README.md).

The AI-facing usage catalog embeds reference text from Siemens AI extensions and
all C# example sources from Siemens TIA Portal Openness Code Snippets, copyright
Siemens 2025-2026, pinned at `4a8cc79d0666633e524e52f3335d99ff993f8830`.
Source code is MIT under section 2 of the complete retained
[Siemens license](docs/licenses/SiemensCodeSnippets-LICENSE.md).
The files are reference data, not compiled dependencies; Siemens SDK binaries and
sample engineering archives are excluded. See [scope and provenance](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md).
