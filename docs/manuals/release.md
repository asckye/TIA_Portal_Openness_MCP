# 准备和发布版本

状态：现行 · 最后核对：2026-11-04，master `ef1988bc`

本文说明维护者如何提前构建、检查和上传候选，再在确认后公开预发布，依据 [ADR 0031](../standards/decisions/0031-release-path.md)。

每个发布候选前，先在主机手动运行 `dotnet run --project build-tools/release -- merge-check -All`，完成全部 38 个注册套件和源码检查；数量门槛保持不变。发布分为两个阶段：`prepare` 完成所有耗时工作；`publish-prepared` 使用已上传的候选，在 300 秒内核对、推送提交、打标签并公开。发布阶段不构建、不测试、不打包。

实施状态：master 已含 NEW-REL-A 的构建打包、NEW-REL-B 的完整检查与候选、NEW-REL-C 的上传和公开命令。`prepare -BuildOnly` 只得到 ZIP，不能视为可发布候选，也不能手工补候选记录。

## 准备环境

完整准备和公开从 Windows 上的 master 检出根目录运行；`prepare -BuildOnly` 可在任意分支的干净 worktree 中运行。准备前核对：

- 工作树干净；完整准备时，所有待发布改动已经过审查并在 master；跟踪文件的实际字节符合 `.gitattributes`。仅 `git status` 干净不足以证明行尾正确。
- `Version.props`、`.claude-plugin/plugin.json`、`.claude-plugin/marketplace.json` 和 `CHANGELOG.md` 最新版本条目一致；日期为有效 ISO 日期；`docs/releases/v<版本>.md` 存在，生成的未验收清单是最新的。
- .NET SDK 与依赖缓存可用；八版 Siemens PublicAPI 在 `sdk/`，或由 `-PublicApiRoot` 指向其父目录。可用 `-NuGetConfig` 指定已有的离线配置。
- 解码器使用固定 CPython，必要时由 `-Python` 或 `TIA_MCP_DECODER_PYTHON` 指定；PLC Tools 伴随 Python 的依赖导入检查通过。伴随解释器按 `-CompanionPython`、`TIA_MCP_PLC_TOOLS_PYTHON`、已有的 `TiaMcp_Output/ecosystem-python/Scripts/python.exe` 解析。
- 本机已有校验过的随包 .NET 压缩包缓存 `bin-build/cache/dotnet-10.0.12/`；SDK、缓存、密钥和私人项目均不随包分发。
- GitHub 凭据能上传草稿、推送 master 和创建标签。上传和公开按 `-Token`、`GITHUB_TOKEN`、Git Credential Manager 的顺序取令牌；不要把令牌写进文档或命令历史。

准备会重建当前检出的 `runtime/`（保留其中的 `README.md`）和 `bin-build/prepared/<版本>/`，并清理 `src`、`tests`、`third_party`、`build-tools/native-call-weaver` 下的全部 `bin`/`obj`，避免旧依赖进入交付包；运行中的 `build-tools/release` 不清理。`TEMP`、`TMP`、`TMPDIR` 和 `DOTNET_CLI_HOME` 使用仓库外的短 `tmr-<8位标识>` 目录，运行结束后删除；占用中的文件导致清理失败时保留目录并打印警告。重跑前保留仍需审查的旧日志；不要让运行中的程序占用这些输出。命令会验证跟踪文件未被改写，生成的包清单只写入暂存目录。

## 提前准备候选

以下以 `4.0.0-beta.3` 为例。版本号必须与准备好的源文件一致；命令不会替维护者改版本或提交文件。

```powershell
dotnet run --project build-tools/release -- prepare -Version 4.0.0-beta.3
```

完整准备首先运行依赖漏洞扫描，再按顺序完成构建、暂存校验、ZIP 和摘要生成，再对提取的 ZIP 运行交付检查，然后写候选记录并上传私有草稿，2026-11-04 实测约 6 分钟（未计入漏洞扫描）。注册套件、传输、配置页、原生安全、工具用法与活动输入检查由 `merge-check` 执行，范围及最低通过数、最大跳过数见[验证](validation.md)。只有全部通过、工作树仍干净且测试用运行文件与 ZIP 一致时才写入 `candidate.json`；各检查运行到底后统一报告失败。各阶段和检查输出耗时，供后续优化使用。`BehaviorParityTests` 在 `merge-check` 中随 `foundation-api` 执行；不再单独构建或运行 `host-behavior-parity` 作业。

扫描先于任何清理或构建，报告保存在 `bin-build/prepared/<版本>/logs/vulnerable-packages.json`。未批准的 High/Critical 阻止准备；网络或恢复失败、空源、无效报告显示 `not run`，停止并提示恢复网络后重跑，通常约 20 秒内失败，超时上限为 300 秒。离线 `-NuGetConfig` 只用于后续构建；扫描显式使用 nuget.org。`-NoDraftUpload` 也需要完成扫描；`-BuildOnly` 不生成候选，因而跳过扫描。`merge-check`、`upload-draft`、`publish-prepared` 不扫描，公开阶段的 300 秒预算保持不变；不要求 GitHub CI 扫描收据。

| 选项 | 用途与边界 |
|---|---|
| `-BuildOnly` | 跳过漏洞扫描，只构建、暂存校验和打包，不运行交付包检查，不生成可发布候选，不上传草稿；可在任意分支检查构建和交付布局 |
| `-NoDraftUpload` | 仍须通过漏洞扫描；完整检查通过后保留本地候选，但不上传草稿；之后使用 `upload-draft` |
| `-PublicApiRoot`、`-NuGetConfig` | 指定本机 SDK 父目录及离线 NuGet 配置文件 |
| `-Python`、`-CompanionPython` | 分别指定解码器和 PLC Tools 的解释器绝对路径 |
| `-MaxParallelism` | 构建并行度，必须为正整数，默认 6 |

成功产物位于 `bin-build/prepared/<版本>/`：

- `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip` 和同名 `.sha256`，只生成一次，检查和上传使用相同字节。
- `candidate.json`：版本、提交、ZIP 路径、SHA-256、大小、文件数、检查名称和完成时间；上传成功后还含草稿与资产 ID。
- 暂存包及 `logs/`。候选记录和日志留在本机，不提交 Git。

交付文件按 `scripts/operations/delivery-files.json` 过滤，包括八版宿主、worker 和适配器、工作台、工具、用户文档、模板、插件及许可证。暂存阶段生成 `manifest/package-manifest.json`、`delivery.json`、`tools-list.json` 和文件清单 `multi-version-build.json`。旧构建收据不进入新包；`runtime/verification/`、源码、SDK 和私人数据不交付。

## 重试草稿上传

上传失败不会删除通过检查的本地候选。使用准备阶段已经构建的 Debug 发布工具重试：

```powershell
dotnet run --no-build --project build-tools/release -- upload-draft -Version 4.0.0-beta.3
```

命令创建或复用同版本草稿，核对并保留匹配的资产、替换不匹配的资产，草稿最终只有 ZIP 和 `.sha256` 两项资产。草稿说明来自对应版本的发布说明，包含候选提交和未验收边界。成功后回写候选中的 ID；无需重新构建或测试。

## 确认后公开

维护者先审查准备结果和发布说明，再明确确认发布。确认后保持候选提交及工作树不变，从同一检出运行：

```powershell
dotnet run --no-build --project build-tools/release -- publish-prepared -Version 4.0.0-beta.3
```

命令先核对候选、HEAD、干净工作树、ZIP 与 sidecar、草稿资产的大小与摘要以及已有标签的目标。核对通过后推送候选提交到 `origin/master`，按需创建 `v<版本>` 标签，将草稿公开为 GitHub 预发布，设置 `make_latest=false`，最后确认公开资产可下载。它不等待 GitHub CI。

300 秒是从发布工具进程启动起的硬性要求。超过 150 秒不再开始标签步骤；标签步骤后仍先尝试公开草稿，其余请求和重试受剩余预算约束。所有结果均打印总秒数。超时或网络故障不会变成成功；已创建标签或已公开的状态必须由重跑核实。

`upload-draft` 与 `publish-prepared` 当前只接受 beta/rc 预发布版本。正式版本的公开规则须在 4.0 前决定，不能直接把示例版本替换为 `4.0.0`。

## 失败后恢复

| 失败位置或结果 | 处理 |
|---|---|
| 准备的前置条件、构建或检查失败 | 修复报告的问题，由维护者将源文件改动提交到 master 后重跑同一条 `prepare`；不要改候选或清单中的哈希 |
| 草稿上传失败，候选仍存在 | 排除凭据或网络问题，重跑同一条 `upload-draft` |
| 公开前核对失败 | 按报错修复候选与环境不一致的问题；源代码变化必须重新准备。条件一致后重跑同一条 `publish-prepared` |
| “标签存在或状态未知、草稿可能尚未公开” | 重跑同一条 `publish-prepared`，由命令读取已有标签和草稿状态；不要手工删除标签 |
| “已公开、下载未确认” | 重跑同一条 `publish-prepared`；已有公开版本及资产匹配时只确认下载，不再次推送、建标签或公开 |

恢复沿用同一版本和同一候选；不要以新建同名版本、改摘要或重传未经检查的包绕过失败。

## 发布后验证

GitHub 发布后工作流只验证公开 ZIP 与 `.sha256` 相符；它不再把标签中的旧构建记录当作发布依据，也不证明原生功能正确。

发布后由获授权的测试者和维护者进行 VM 回归。先复测对应修复，再按[预发布真机验证清单](../guides/beta-tester-checklist.md)检查真实行为；VMAUTO-1 的自动化计划见[开发计划](../plan/plan.md)。结果写入[测试者反馈](../plan/progress.md)，复核后进入真机台账。离线检查或预发布公开都不替代真实 TIA 验收。
