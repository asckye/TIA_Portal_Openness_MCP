# 完整 Release 发布流程

从 v3.2.0 起，完整 TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip 包含 V14 SP1、V15.1、V16–V21 MCP 运行文件、WPF 配置器、Studio 及八版原生适配器，以及源码、示例、文档、模板和许可证。基础引擎与完整引擎的范围不同，见[版本矩阵](../reference/version-tools.md)。每次发布同时上传 .sha256；GitHub 自动生成的源码压缩包不含运行二进制。

运行文件不提交 Git。三份构建记录是 manifest/release-build.json、manifest/configurator-build.json、manifest/multi-version-build.json；manifest/delivery.json 绑定它们的哈希。PublicAPI、个人密钥、私人 TIA 工程、设计交接和构建日志不分发，原始版权与许可证保留。

## 准备和执行

在 Windows 的 master 工作区操作，审查全部改动，停止该安装目录下的 MCP 和 Studio 进程。需要 PowerShell 7、Git、所需 .NET SDK/运行时与 Framework 开发环境、Python 和八版 SDK。设置 TIA_MCP_PLC_TOOLS_PYTHON 指向已有 PLC Tools 伴随环境。SDK 目录结构见[构建说明](../reference/version-tools.md#build-test-and-package)。

先写 CHANGELOG.md 最新版本条目与 docs/releases/vX.Y.Z.md，更新当前说明和交接历史。发布正文写明各版范围、组件、测试结果和真实工程验收状态。英文文档和提交说明使用英文，不添加 AI 署名。

```powershell
pwsh -NoProfile -File scripts/build/Release.ps1 -Version X.Y.Z -Summary "Concise English release summary" -PublicApiRoot <SDK-root> -V20ReferenceRoot <V20-SDK> -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>
```

PowerShell 7 不在 PATH 时用 -PowerShell7 指定完整路径。多版本工具清单必须由 PowerShell 7 输出；Windows PowerShell 5 的重定向会改变 JSON 编码。

脚本依次执行：

1. 检查 master、上游、SDK、进程和发布说明；更新完整引擎、配置器、插件和 Studio 版本。
2. Build-Release.ps1 构建两个完整引擎与配置器，执行功能、协议和稳定性测试。
3. Build-MultiVersion.ps1 -SkipFullEngines -Test 构建八版 Worker/Adapter、六个基础引擎运行包和 Studio，执行八版 SDK 元数据、XSD、基础工具、传输和全部工具示例校验，写入八版本交付记录。
4. 检查仓库链接、失效工具引用和严格包验证，提交明确变更路径，生成一次 Release X.Y.Z: <summary> 提交。
5. Package-Release.py 核对提交树与清单内全部运行文件、源码哈希、版本及必要组件，在实际暂存目录运行严格检查，生成 ZIP、SHA-256 和 package-result.json。
6. Verify-ReleaseAsset.py 独立验证 ZIP 等于提交树加记录哈希的运行文件。
7. 推送 master，等待 validate-bundle 与 offline-checks，通过后创建 annotated tag vX.Y.Z 并推送。
8. Publish-Release.ps1 创建草稿、上传 ZIP 和 SHA-256、回读大小及 digest，然后公开发布并置为 latest。
9. 等待 Verify published release 下载并校验公开资产，再记录发布 URL、提交和验收状态。

任一步失败即停止。日志保留在 release.log、build.log、bin-build/releases/v<版本>/ 和 bin-build/multi-version/。令牌来自 -Token、GITHUB_TOKEN 或 Git Credential Manager，不打印、不放入发布包。

## 分阶段与恢复

-NoPush 完成本地提交、打包和验证后停止；检查具体产物后用相同参数加 -Resume 继续推送、CI、tag 和发布。-NoTag 在推送及 CI 后停止；-DryRun 只到构建与本地检查。

-SkipBuild 只适用于所有源码和运行文件仍与三份构建记录相符的情况，不能用于改版本号却没有重建的产物。Prepare-Delivery.ps1 单独运行只准备完整引擎与配置器；正式发布仍需多版本构建。

已有归档不自动覆盖或删除。先检查、保留，再用 Package-Release.py --output-directory <新目录> 生成本地候选。恢复发布前，默认发布目录的 package-result.json 必须指向此次验证的归档及当前提交。

已公开的 Release、tag 和资产不改写；发现问题应修正并发布新补丁版本。发布不自动更新运行中的服务或虚拟机。

## GitHub 与原生验收

托管 runner 无法取得全部 Siemens SDK 重建引擎，因此由本机构建上传。推送 CI 检查源码、构建记录、Studio/WPF 及无 Siemens 依赖的基础引擎协议；发布工作流下载实际 ZIP，核对 tag、提交、文件集合和运行文件哈希，再在解包目录执行严格验证。

真实 TIA 工程导入、生成、编译、读回与设备操作须在明确指定的版本和工程上单独验收。构建、XSD、离线功能与公开资产验证不能代替原生验收。
