# 完整 Release 发布流程

从 v3.2.0 起，完整 TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip 包含 V14 SP1、V15.1、V16–V21 MCP 运行文件、WPF 配置器、Studio 及八版原生适配器，以及运行资源、用户文档、模板、插件和许可证。自 3.3.0 之后的下一个版本起，交付 ZIP 不再携带开发源码及构建工具。基础引擎与完整引擎的范围不同，见[版本矩阵](../reference/version-tools.md)。每次发布同时上传 .sha256；GitHub 自动生成的源码压缩包不含运行二进制。

运行文件不提交 Git。三份构建记录是 manifest/release-build.json、manifest/configurator-build.json、manifest/multi-version-build.json；manifest/delivery.json 绑定它们的哈希。PublicAPI、个人密钥、私人 TIA 工程、设计交接和构建日志不分发，原始版权与许可证保留。

## 交付清单与两种校验模式

[`scripts/operations/delivery-files.json`](../../scripts/operations/delivery-files.json) 是唯一交付规则文件。
`include.files` 为精确路径，`include.prefixes` 为以 `/` 结尾的目录前缀，`exclude` 优先；未匹配的跟踪文件不交付。
Python 打包器、资产核验、仓库/布局检查与 PowerShell 校验器、更新器均读取它。运行文件仍只来自三份构建记录，
不会把磁盘上任意新文件塞入 ZIP。`runtime/verification/` 只用于发布前的 IL 验证，不分发。

保留 BundleLayout 的九项资源、全部模板、生态与操作脚本、Claude Code 插件及其 skill、用户说明和许可证。
Python 桥接所用两个第三方项目只交付 `src`、PLC Tools 的八个 `packages/*/src` 和许可证；
`Install-PlcTools.ps1` 安装外部运行依赖，不再依赖未交付的本地项目打包元数据。
字体、Studio、TiaGitAddIn、Siemens OPC UA、Eido 与嵌入官方代码示例的许可原文在 `docs/licenses/` 保留副本。

三份构建记录供解包校验读取版本、依赖清单及 SHA-256，`delivery.json` 绑定记录哈希并供软件检查版本；
`tools-list.json` 用于包内入口及工具数量校验，`version-tools.json` 是用户版本矩阵入口。
其他构建证据和 `RELEASE_STATUS.txt`、`release-file-hashes.json` 不进入新 ZIP。

- 仓库模式：`Validate-Bundle.ps1 -Strict` 保留源码、构建输入哈希、版本和本地 IL 校验；日常检查可用
  `-NoBinaries -SkipSourceHashes`。`Check-Repository.py` 运行源码门禁与 BundleLayout 表校验。
- 包模式：从仓库执行 `Validate-Bundle.ps1 -BundleRoot <解包根> -PackageMode -Strict` 与
  `Check-Repository.py --root <解包根> --package-mode`。没有 `Version.props` 时也自动识别包模式。
  检查交付资源、用户文档链接、许可证、构建记录、运行文件版本/哈希；不读取源文件或调用包内 IL 验证器。
  生成的旧 package/blueprint 元数据中的 `scripts/checks/Validate-Bundle.ps1` 条目仅属仓库说明，包模式明确忽略该单项。
  新增资源必须纳入规则，不能以源码缺失为由跳过其他必需项。

无发布构建时可查看当前文件投影，不执行 Release 或生成可发布资产：

```powershell
python scripts/build/Package-Release.py --dry-run --include-untracked --stage-directory bin-build/delivery-preview
python scripts/checks/Check-Repository.py --root bin-build/delivery-preview --no-binaries
pwsh -NoProfile -File scripts/checks/Validate-Bundle.ps1 -BundleRoot bin-build/delivery-preview -PackageMode -Strict -NoBinaries
```

`--include-untracked` 仅在 dry run 中用于预览待审查的新文件，正式打包仍要求干净提交树。
预览打印完整文件集和排除的顶层分组；没有运行二进制时不能作为完整发布验证。
旧构建记录与 CHANGELOG 版本不一致仍报错，不能修改记录哈希来让预览变绿。

## 准备和执行

在 Windows 的 master 工作区操作，审查全部改动，停止该安装目录下的 MCP 和 Studio 进程。需要 PowerShell 7、Git、所需 .NET SDK/运行时与 Framework 开发环境、Python 和八版 SDK。设置 TIA_MCP_PLC_TOOLS_PYTHON 指向已有 PLC Tools 伴随环境。SDK 目录结构见[构建说明](../reference/version-tools.md#build-test-and-package)。

先写 CHANGELOG.md 最新版本条目与 docs/releases/vX.Y.Z.md，更新当前说明；发布后在 handoff.md 和 publication-vX.Y.Z.json 记录结果。发布正文写明各版范围、组件、测试结果和真实工程验收状态。英文文档和提交说明使用英文，不添加 AI 署名。

```powershell
pwsh -NoProfile -File scripts/build/Release.ps1 -Version X.Y.Z -Summary "Concise English release summary" -PublicApiRoot <SDK-root> -V20ReferenceRoot <V20-SDK> -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>
```

PowerShell 7 不在 PATH 时用 -PowerShell7 指定完整路径。多版本工具清单必须由 PowerShell 7 输出；Windows PowerShell 5 的重定向会改变 JSON 编码。

脚本依次执行：

1. 检查 master、上游、SDK、进程和发布说明；更新 `Version.props` 中的 `TiaMcpRelease` 和 `.claude-plugin/plugin.json` 版本，以及文档当前发布链接与路线图标题。完整引擎、基础宿主、配置器和 Studio 从 `Version.props` 派生版本。
2. Build-Release.ps1 构建两个完整引擎与配置器，执行功能、协议和稳定性测试。
3. Build-MultiVersion.ps1 -SkipFullEngines -Test 构建八版 Worker/Adapter、六个基础引擎运行包和 Studio，执行八版 SDK 元数据、XSD、基础工具、传输和全部工具示例校验，写入八版本交付记录。
4. 检查仓库链接、失效工具引用和严格包验证，提交明确变更路径，生成一次 Release X.Y.Z: <summary> 提交。
5. Package-Release.py 在仓库核对完整提交树、全部构建记录和源码哈希，运行发布期 IL 校验，再按交付清单过滤，在实际暂存目录运行包模式严格检查，生成 ZIP、SHA-256 和 package-result.json。
6. Verify-ReleaseAsset.py 独立验证 ZIP 等于交付清单过滤后的 tag 文件集加记录哈希的运行文件（排除 `runtime/verification/`）。
7. 推送 master，等待 validate-bundle 与 offline-checks，通过后创建 annotated tag vX.Y.Z 并推送。
8. Publish-Release.ps1 创建草稿、上传 ZIP 和 SHA-256、回读大小及 digest，然后公开发布并置为 latest。
9. 等待 Verify published release 下载并校验公开资产，再记录发布 URL、提交和验收状态。

任一步失败即停止。日志保留在 release.log、build.log、bin-build/releases/v<版本>/ 和 bin-build/multi-version/。令牌来自 -Token、GITHUB_TOKEN 或 Git Credential Manager，不打印、不放入发布包。

## 分阶段与恢复

-NoPush 完成本地提交、打包和验证后停止；检查具体产物后用相同参数加 -Resume 继续推送、CI、tag 和发布。-NoTag 在推送及 CI 后停止；-DryRun 只到构建与本地检查。

-SkipBuild 只适用于所有源码和运行文件仍与三份构建记录相符的情况，不能用于改版本号却没有重建的产物。Prepare-Delivery.ps1 单独运行只准备完整引擎与配置器；正式发布仍需多版本构建。

长路径环境可用 `Package-Release.py --output-directory <较短的新目录>`，保留生成的 package-result.json 所记录的实际 ZIP 路径；恢复 Release 流程时将该结果记录及同名 ZIP/SHA-256 放到默认版本目录。

已有归档不自动覆盖或删除。先检查、保留，再用 Package-Release.py --output-directory <新目录> 生成本地候选。恢复发布前，默认发布目录的 package-result.json 必须指向此次验证的归档及当前提交。

已公开的 Release、tag 和资产不改写；发现问题应修正并发布新补丁版本。发布不自动更新运行中的服务或虚拟机。

## GitHub 与原生验收

托管 runner 无法取得全部 Siemens SDK 重建引擎，因此由本机构建上传。推送 CI 检查源码、构建记录、Studio/WPF 及无 Siemens 依赖的基础引擎协议；发布工作流下载实际 ZIP，核对 tag、提交、文件集合和运行文件哈希，再在解包目录执行严格验证。

真实 TIA 工程导入、生成、编译、读回与设备操作须在明确指定的版本和工程上单独验收。构建、XSD、离线功能与公开资产验证不能代替原生验收。
