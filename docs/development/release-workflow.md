# 完整 Release 发布流程

每次正式 Release 都必须提供完整的 `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip` 和对应 `.sha256`，无需下载旧版补文件。不能以单版运行包、源码压缩包或补丁替代完整交付包。

完整包包含两版 EXE 及依赖、WPF 图形入口 `TiaMcpConfigurator.exe`、工程生成、预热和取证脚本、中英文手册、PLC/HMI 模板、工程蓝图、源码、测试、校验脚本与文件清单，必须保留原始 `LICENSE`、`NOTICE.md` 来源声明和依赖许可证。运行文件仅在 `runtime/v20`、`runtime/v21`，不再复制到旧 bin 路径。PublicAPI、个人密钥、工程导出物、构建日志和私人启动脚本不随包分发。

## 配置器或文档更新（沿用已验证引擎）

当引擎源码、测试及 runtime 全部未变时，可单独提升交付版本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Prepare-Delivery.ps1 -Release 2.7.17 -ReleaseDate 20260917
```

先更新 CHANGELOG、发布说明和插件版本；配置器代码变更时同步其程序集版本。脚本编译并测试 GUI，写入 `manifest/configurator-build.json`；原 `manifest/release-build.json` 保持不变，保留实际引擎版本、测试日期、源码和二进制哈希。`manifest/delivery.json` 记录交付版本、组成及两个构建记录的哈希。打包时仍强制校验全部输入与运行文件，任何引擎变动均须走完整重建流程；不能仅修改版本或复用已失效的测试记录。Release 正文明确列出交付版本和引擎版本。

随后按照下面的提交、打包和发布步骤操作。

## 引擎构建与本地验证

需要 Windows、.NET SDK（包含测试项目所需运行时）、.NET Framework 4.8 开发环境、Python 3.10+、Git，以及本地 V20/V21 PublicAPI。两个参考路径均应直接包含对应的 Siemens DLL；V21 使用 `net48` 目录。HTTP 测试仅绑定本机测试端口，可能需要管理员终端。

1. Release 标签和标题仅使用 `vX.Y.Z`（例如 `v2.7.3`、`v2.7.4`），不添加个人或功能后缀；日期只进入附件名。同步两个 `.csproj` 的 `FileVersion` 和 `InformationalVersion`，同步 `AssemblyVersion`，更新 CHANGELOG、本版说明与 `README.zh-CN.md`。
2. 运行统一构建入口（路径按本机安装修改）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Release.ps1 -V20ReferenceRoot "D:\TIA20\PublicAPI\V20" -V21ReferenceRoot "D:\TIA21\PublicAPI\V21\net48"
```

用 `-ReleaseDate 20260913` 指定发布日期（默认使用本机日期）；例如 `TIA_MCP_Delivery_v2.7.4_20260913.zip`，包内始终包含 V20/V21。可用 `-Dotnet` 指定 SDK、`-Python` 指定 Python 可执行文件、`-NuGetConfig` 指定 NuGet 配置；依赖已恢复时可用 `-NoRestore`。脚本从工程文件读取版本，编译 V20/V21、调用 Prepare-Delivery 编译测试 GUI，执行离线回归、两版实际 EXE 的 HTTP/HMI 检查、HTTP/STDIO 两种模式及完整/精简配置的资源发现协议测试、实际 net48 EXE 的远程文件代理失效检查和采集接口的程序集检查，更新 `runtime`、工具清单、构建校验清单和包元数据。资源协议测试使用独立测试程序加载 EXE 的实际服务入口，不改变系统 Openness 用户组。结果写入 `bin-build/releases/v<版本>/`。这些检查不会启动或修改 TIA 工程。

3. 审查并提交两版运行文件、源码、测试、说明和清单，推送 `master`。工作区必须干净。

统一构建入口也会运行 `scripts/build/Build-Configurator.ps1 -Test`，生成独立 WPF 配置 EXE 并验证 11 张客户端卡片的配置格式、备份、加密、HTTP 就绪检查和窗口渲染。单独修改 UI 时可直接运行此脚本，提交更新的 `TiaMcpConfigurator.exe`；图形工具不依赖本机 TIA 来配置远程客户端。真实客户端/虚拟机联调与自动化测试结果分开记录。
4. 生成完整包：

```text
python scripts/build/Package-Release.py
```

Git 不在 PATH 时加 `--git "Git可执行文件完整路径"`。打包器核对编译输入、依赖和版本，运行完整交付目录的严格检查，回读 ZIP 中的每个文件，生成 SHA256。任何校验失败均停止。已存在输出时不覆盖，须先检查已有产物。

## 上传与发布检查

推荐在 GitHub 的 **Actions → Publish complete release → Run workflow** 手动发布已审查的 master。工作流根据已提交的双版本运行文件和验证清单重新生成完整包，校验 GitHub 资产大小与 SHA256 后才发布，并自动使用标准版本标签和标题；不需要个人访问令牌或 MCP 密钥。编译和真实工程验收仍在前面单独完成。

首次整理独立项目的发布列表时，可勾选 `archive_legacy`：带 `asckye/defects/readonly` 后缀的历史 Release 转为草稿，原标签和附件保留；已有标准版本只规范标题。后续正常发布无需勾选。

新建标准版本标签，指向完整包的 sourceCommit；Release 标题与标签同名。上传上述输出目录内的完整 ZIP 和 `.sha256`。Release 正文标明包内文件版本、完整包对应的 `sourceCommit`、本地测试结果和真实工程验收状态。核对 GitHub 资产大小及 SHA256 与发布工作流产生的校验文件一致，再交付下载链接。ZIP 时间戳可能导致不同打包运行的字节哈希不同，应比较同一次打包的记录。GitHub 自动生成的 Source code ZIP/TGZ 仍可保留，但运行用户应下载完整交付 ZIP。

纠正同一 Release 的附件时，移除容易误认成完整包的旧精简附件，并写明修订后的完整包对应提交。不要为了附件修订强制移动已公开的标签。

已删除过期的按版本 Build/Package 脚本。完整发布统一使用 `scripts/build/` 的入口，结构检查在仓库和实际暂存交付目录各执行一次。

真实博途验收必须单独执行并记录。脚本检查通过、EXE 能加载或 Release 上传成功，都不等于真实工程迁移数据完整。
