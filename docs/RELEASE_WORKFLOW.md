# 完整 Release 发布流程

每次正式 Release 都必须提供完整的 `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip` 和对应 `.sha256`，无需下载旧版补文件。不能以单版运行包、源码压缩包或补丁替代完整交付包。

完整包包含两版 EXE 及依赖、原版 CMD/BAT 配置入口、中英文手册、PLC/HMI 模板、工程蓝图、源码、测试、校验脚本与文件清单，必须保留原始 `LICENSE`、`NOTICE.md` 来源声明和依赖许可证。旧手册使用的 `bin/Release/net48` 和 `bin-v20/Release/net48` 也包含相应运行文件。PublicAPI、个人密钥、工程导出物、构建日志和私人启动脚本不随包分发。

## 构建与本地验证

需要 Windows、.NET SDK（包含测试项目所需运行时）、.NET Framework 4.8 开发环境、Python 3.9+、Git，以及本地 V20/V21 PublicAPI。两个参考路径均应直接包含对应的 Siemens DLL；V21 使用 `net48` 目录。HTTP 测试仅绑定本机测试端口，可能需要管理员终端。

1. Release 标签和标题仅使用 `vX.Y.Z`（例如 `v2.7.3`、`v2.7.4`），不添加个人或功能后缀；日期只进入附件名。同步两个 `.csproj` 的 `FileVersion` 和 `InformationalVersion`，同步 `AssemblyVersion`，更新 CHANGELOG、本版说明与 `开始使用.md`。
2. 运行统一构建入口（路径按本机安装修改）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -V20ReferenceRoot "D:\TIA20\PublicAPI\V20" -V21ReferenceRoot "D:\TIA21\PublicAPI\V21\net48"
```

用 `-ReleaseDate 20260913` 指定发布日期（默认使用本机日期）；例如 `TIA_MCP_Delivery_v2.7.4_20260913.zip`，包内始终包含 V20/V21。可用 `-Dotnet` 指定 SDK、`-Python` 指定 Python 可执行文件、`-NuGetConfig` 指定 NuGet 配置；依赖已恢复时可用 `-NoRestore`。脚本从工程文件读取版本，编译 V20/V21，执行离线回归、两版实际 EXE 的 HTTP/HMI 检查、HTTP/STDIO 两种模式及完整/精简配置的资源发现协议测试和采集接口的程序集检查，更新 `runtime`、工具清单、构建校验清单和包元数据。资源协议测试使用独立测试程序加载 EXE 的实际服务入口，不改变系统 Openness 用户组。结果写入 `bin-build/releases/v<版本>/`。这些检查不会启动或修改 TIA 工程。

3. 审查并提交两版运行文件、源码、测试、说明和清单，推送 `master`。工作区必须干净。
4. 生成完整包：

```text
python scripts/Package-Release.py
```

Git 不在 PATH 时加 `--git "Git可执行文件完整路径"`。打包器核对编译输入、依赖和版本，运行完整交付目录的严格检查，回读 ZIP 中的每个文件，生成 SHA256。任何校验失败均停止。已存在输出时不覆盖，须先检查已有产物。

## 上传与发布检查

推荐在 GitHub 的 **Actions → Publish complete release → Run workflow** 手动发布已审查的 master。工作流根据已提交的双版本运行文件和验证清单重新生成完整包，校验 GitHub 资产大小与 SHA256 后才发布，并自动使用标准版本标签和标题；不需要个人访问令牌或 MCP 密钥。编译和真实工程验收仍在前面单独完成。

首次整理独立项目的发布列表时，可勾选 `archive_legacy`：带 `asckye/defects/readonly` 后缀的历史 Release 转为草稿，原标签和附件保留；已有标准版本只规范标题。后续正常发布无需勾选。

新建标准版本标签，指向完整包的 sourceCommit；Release 标题与标签同名。上传上述输出目录内的完整 ZIP 和 `.sha256`。Release 正文标明包内文件版本、完整包对应的 `sourceCommit`、本地测试结果和真实工程验收状态。核对 GitHub 资产大小及 SHA256 与本地一致，再交付下载链接。GitHub 自动生成的 Source code ZIP/TGZ 仍可保留，但运行用户应下载完整交付 ZIP。

纠正同一 Release 的附件时，移除容易误认成完整包的旧精简附件，并写明修订后的完整包对应提交。不要为了附件修订强制移动已公开的标签。

`Package-ReadOnlyV21.ps1 -DevelopmentOnly` 仅生成本地开发验证包，禁止作为正式 Release 附件。旧的按版本命名的 Build/Package 脚本保留作历史记录；后续发布统一使用本文的两个入口。

真实博途验收必须单独执行并记录。脚本检查通过、EXE 能加载或 Release 上传成功，都不等于真实工程迁移数据完整。
