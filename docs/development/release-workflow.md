# 完整 Release 发布流程

每次正式 Release 都必须提供完整的 `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip` 和对应 `.sha256`，无需下载旧版补文件。不能以单版运行包、源码压缩包或补丁替代完整交付包。

完整包包含两版 EXE 及依赖、WPF 图形入口 `TiaMcpConfigurator.exe`、工程生成、预热和取证脚本、中英文手册、PLC/HMI 模板、工程蓝图、源码、测试、校验脚本与文件清单，必须保留原始 `LICENSE`、`NOTICE.md` 来源声明和依赖许可证。运行文件仅在 `runtime/v20`、`runtime/v21`，不再复制到旧 bin 路径。**2.8.1 起二进制不进 Git**（维护者决定）：`runtime/v20`、`runtime/v21`、`TiaMcpConfigurator.exe` 在 `.gitignore` 里，由本机 `Build-Release.ps1` 生成，它们的逐文件哈希记录在提交的 `manifest/release-build.json` 与 `manifest/configurator-build.json`；交付 ZIP 由本机 `Release.ps1` 直接上传到 GitHub Release，工作流只做验证。PublicAPI、个人密钥、工程导出物、构建日志和私人启动脚本不随包分发。

## 配置器或文档更新（沿用已验证引擎）

当引擎源码、测试及 runtime 全部未变时，可单独提升交付版本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Prepare-Delivery.ps1 -Release 2.7.17 -ReleaseDate 20260917
```

先更新 CHANGELOG、发布说明和插件版本；配置器代码变更时同步其程序集版本。脚本编译并测试 GUI，写入 `manifest/configurator-build.json`；原 `manifest/release-build.json` 保持不变，保留实际引擎版本、测试日期、源码和二进制哈希。`manifest/delivery.json` 记录交付版本、组成及两个构建记录的哈希。打包时仍强制校验全部输入与运行文件，任何引擎变动均须走完整重建流程；不能仅修改版本或复用已失效的测试记录。Release 正文明确列出交付版本和引擎版本。

随后按照下面的提交、打包和发布步骤操作。

## 一键发布（2.7.56 之后的常规路径）

先手写两样：`CHANGELOG.md` 顶部 `## [X.Y.Z] - 日期` 条目、`docs/releases/vX.Y.Z.md`（连同 `docs/reference/capabilities.md` 新段、`docs/development/roadmap.md` §5 条目等文案）。然后在 PowerShell 里：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Release.ps1 -Version X.Y.Z -Summary "(1/3) 提交的一句话说明"
```

脚本按顺序做：前置检查（在 master 且不落后 `origin/master`、PublicAPI 目录、Python、没有残留的 `TiaMcpServer.exe` 进程、CHANGELOG 最新条目 = 该版本、发布说明存在）→ 版本号 8 处（两个 `.csproj`、`Configurator.cs`、`plugin.json`、`docs/README.md` 当前发布链接、`capabilities.md` 首段、`roadmap.md` 标题）→ `Build-Release.ps1` → `Check-Repository.py` + `Check-DeadToolReferences.py` + `Validate-Bundle.ps1 -Strict` → **一次提交** `Release X.Y.Z: <summary>`（源码 + 文档 + `manifest/*` + `tool-matrix.md`；二进制被 `.gitignore` 挡在外面）→ `Package-Release.py`（提交的树 + 本机二进制 → ZIP + `.sha256`）→ `Verify-ReleaseAsset.py`（ZIP = 树 + 清单里记录的二进制哈希）→ 推送 → 等 `validate-bundle` / `offline-checks`（GitHub API，带令牌）→ annotated tag `vX.Y.Z` → `Publish-Release.ps1`（草稿 Release → 上传 ZIP 与 `.sha256` → 回读大小与 sha256 → 发布）→ 等 `Verify published release` 工作流绿。任一步失败即停，日志在仓库根 `release.log`（Build-Release 自己的在 `build.log`）。令牌：`-Token`，否则 `GITHUB_TOKEN`，否则 Git Credential Manager 存的 github.com 凭据（`git credential fill`，repo 权限即可）。选项：`-DryRun`（只到本地闸门，不提交）、`-SkipBuild`（复用上次构建）、`-NoPush` / `-NoTag` / `-NoWait`、`-KillStrayEngine`、`-Resume`（提交已在，从打包 / 推送接着走）、`-ReleaseDate yyyyMMdd`、`-V20ReferenceRoot` / `-V21ReferenceRoot` / `-Python`。提交与 tag 说明一律英文、不带任何 AI 署名行。发布成功后把结果记进 `docs/development/handoff.md` §1 与 `handoff-history.md`，单独提交。

引擎在托管 runner 上编不了（Openness NuGet 只有 targets，PublicAPI 不可分发），二进制又不进 Git，所以构建、打包、上传都在本机；Actions 只做两件事：推送时验证源码树（`validate-bundle` 用 `--no-binaries` / `-NoBinaries`，`offline-checks`），发布后验证上传的 ZIP（`Verify published release`：tag = `v` + `manifest/delivery.json` 且指向 master HEAD，下载 ZIP + `.sha256` 校验，`Verify-ReleaseAsset.py` 逐文件比对树与清单哈希，解包后 `Validate-Bundle.ps1 -Strict`）。下面各节是脚本逐步做的事，也是它失败时的手工路径。

## 引擎构建与本地验证

需要 Windows、.NET SDK（包含测试项目所需运行时）、.NET Framework 4.8 开发环境、Python 3.10+、Git，以及本地 V20/V21 PublicAPI。两个参考路径均应直接包含对应的 Siemens DLL；V21 使用 `net48` 目录。HTTP 测试仅绑定本机测试端口，可能需要管理员终端。

1. Release 标签和标题仅使用 `vX.Y.Z`（例如 `v2.7.3`、`v2.7.4`），不添加个人或功能后缀；日期只进入附件名。同步两个 `.csproj` 的 `FileVersion` 和 `InformationalVersion`，同步 `AssemblyVersion`，更新 CHANGELOG、本版说明与 `README.zh-CN.md`。
2. 运行统一构建入口（路径按本机安装修改）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Release.ps1 -V20ReferenceRoot "D:\TIA20\PublicAPI\V20" -V21ReferenceRoot "D:\TIA21\PublicAPI\V21\net48"
```

用 `-ReleaseDate 20260913` 指定发布日期（默认使用本机日期）；例如 `TIA_MCP_Delivery_v2.7.4_20260913.zip`，包内始终包含 V20/V21。可用 `-Dotnet` 指定 SDK、`-Python` 指定 Python 可执行文件、`-NuGetConfig` 指定 NuGet 配置；依赖已恢复时可用 `-NoRestore`。脚本从工程文件读取版本，编译 V20/V21、调用 Prepare-Delivery 编译测试 GUI，执行离线回归、两版实际 EXE 的 HTTP/HMI 检查、HTTP/STDIO 两种模式及完整/精简配置的资源发现协议测试、实际 net48 EXE 的远程文件代理失效检查和采集接口的程序集检查，更新 `runtime`、工具清单、构建校验清单和包元数据。资源协议测试使用独立测试程序加载 EXE 的实际服务入口，不改变系统 Openness 用户组。结果写入 `bin-build/releases/v<版本>/`。这些检查不会启动或修改 TIA 工程。

3. 审查并提交源码、测试、说明和清单（`manifest/*.json`、`docs/reference/tool-matrix.md`），推送 `master`。两版运行文件与 `TiaMcpConfigurator.exe` 留在本机（`.gitignore`），打包时从工作区取；工作区必须干净（被忽略的二进制不算）。

统一构建入口也会运行 `scripts/build/Build-Configurator.ps1 -Test`，生成独立 WPF 配置 EXE 并验证 12 张客户端卡片的配置格式、备份、加密、HTTP 就绪检查和窗口渲染。单独修改 UI 时可直接运行此脚本；EXE 本身不提交，`manifest/configurator-build.json` 记录它的哈希。图形工具不依赖本机 TIA 来配置远程客户端。真实客户端/虚拟机联调与自动化测试结果分开记录。
4. 生成完整包：

```text
python scripts/build/Package-Release.py
```

Git 不在 PATH 时加 `--git "Git可执行文件完整路径"`。打包器取 Git 里的全部文件加本机 `runtime/v20`、`runtime/v21`、`TiaMcpConfigurator.exe`（缺一个或被跟踪都拒绝），核对编译输入、依赖和版本，运行完整交付目录的严格检查，回读 ZIP 中的每个文件，生成 SHA256。任何校验失败均停止。已存在输出时不覆盖，须先检查已有产物。随后 `python scripts/checks/Verify-ReleaseAsset.py bin-build/releases/vX.Y.Z/<包名>.zip` 证明 ZIP 就是当前提交的树加清单里记录哈希的二进制。

## 上传与发布检查

2.8.1 起的做法（2.7.30–2.8.0 是三段提交 + tag 触发工作流打包，已废）：一次提交 `Release X.Y.Z: <summary>`，推送，等 `validate-bundle` / `offline-checks` 绿，推 annotated tag `vX.Y.Z`，然后 `scripts/build/Publish-Release.ps1 -Version X.Y.Z` 在本机：核对 `bin-build/releases/vX.Y.Z/package-result.json` 的 ZIP（大小、sha256、sourceCommit）= master HEAD = tag 指向的提交 → 建**草稿** Release（正文 = `docs/releases/vX.Y.Z.md` + 包名 / 文件数 / 字节数 / 源码提交 / SHA-256）→ 上传 ZIP 与 `.sha256`（草稿上的残留资产先删；每个资产上传后从 API 回读 `state=uploaded`、大小与 `digest`，最多 6 次）→ 发布并置为 latest → 写 `published-release.json`。已发布的 Release 及其资产**永不改动**，修正一律出新补丁版本。`-DraftOnly` 只到上传（草稿留着看），`-DeleteDraft` 删掉该 tag 的草稿。发布事件触发 `Verify published release` 工作流做独立复核（见上）；`Release.ps1` 等它绿才报 DONE。任何引擎或测试源码改动都要重跑 `Build-Release.ps1`，否则 `manifest/release-build.json` 的源码哈希对不上，`validate-bundle` 与打包器都会以 "Source changed after validation" 拒绝；细节与闸门清单见[接续工作交接 §4](handoff.md)。编译和真实工程验收仍在前面单独完成。

Release 标题与标签同名。Release 正文标明包内文件版本、完整包对应的 `sourceCommit`、本地测试结果和真实工程验收状态。ZIP 时间戳可能导致不同打包运行的字节哈希不同，应比较同一次打包的记录。GitHub 自动生成的 Source code ZIP/TGZ 里没有二进制，运行用户必须下载完整交付 ZIP。

纠正同一 Release 的附件时，移除容易误认成完整包的旧精简附件，并写明修订后的完整包对应提交。不要为了附件修订强制移动已公开的标签。

已删除过期的按版本 Build/Package 脚本。完整发布统一使用 `scripts/build/` 的入口，结构检查在仓库和实际暂存交付目录各执行一次。

真实博途验收必须单独执行并记录。脚本检查通过、EXE 能加载或 Release 上传成功，都不等于真实工程迁移数据完整。
