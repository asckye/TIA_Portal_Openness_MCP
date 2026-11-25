# 准备和发布版本

状态：现行 · 最后核对：2026-11-22

本文说明维护者如何提前构建、检查和上传候选，再在维护者确认后公开。4.0 正式版须满足任务合并、检查通过、无未决 blocker、已生成 rc 和维护者确认等放行条件。见 [ADR 0023](../standards/decisions/0023-release-4-0-final-criteria.md) 和 [ADR 0031](../standards/decisions/0031-release-path.md)。

每个发布候选前，先在主机手动运行 `dotnet run --project build-tools/release -- merge-check -All`，完成全部 38 个注册套件和源码检查；数量门槛保持不变。发布分为两个阶段：`prepare` 完成所有耗时工作；`publish-prepared` 使用已上传的候选，在 300 秒内核对、推送提交、打标签并公开。发布阶段不构建、不测试、不打包。

在待发布的干净提交上运行完整门禁。主检出的 `bin-build/scorecard/merge-check-runs.jsonl` 记录提交、Git 树和完成时间。生成候选时，`prepare` 在清理或构建前核对该提交和树最近一次完整运行的结果，写候选时再次核对 HEAD。缺少记录、旧记录没有树哈希、完整运行失败，或匹配记录之后有无法解析的记录时，在该提交上重新运行 `merge-check -All`。若设置了 `TIA_MCP_UPDATE_GOLDEN`，先清除该环境变量，再运行 `merge-check`、`test-suites` 或 `prepare`。

[开发规范](../standards/development.md) 第 5.9 节原文：“注册套件与源码检查的发布证据是与候选提交和 Git 树完全匹配的 `merge-check -All` 通过记录。”交付 ZIP 的证据来自本次 `prepare` 的包检查与摘要（见 [ADR 0031](../standards/decisions/0031-release-path.md)）。

实施状态：master 已含 NEW-REL-A 的构建打包、NEW-REL-B 的完整检查与候选、NEW-REL-C 的上传和公开命令。`prepare -BuildOnly` 只得到 ZIP，不能视为可发布候选，也不能手工补候选记录。

## 双语发布说明

在 `docs/releases/v<版本>.md` 中先写 `## 中文`，再写 `## English`。两节使用同一份改动、兼容性和验收事实。预发布的每节在清单位置各保留一对 `<!-- generated:not-verified:begin -->` 与 `<!-- generated:not-verified:end -->` 标记。运行 `generate-prerelease-notes -Version <版本>` 会按相同编号更新两节的验收事项。合并门在台账、API 清单或译文变更时运行 `generate-prerelease-notes -Check -TranslationsOnly`，直接从当前来源核对两种语言的覆盖。台账或 API 清单的条目改变时，同步维护[清单译文](../../build-tools/release/prerelease-translations.json)。上传草稿时，发布工具在每节末尾添加同一交付包、提交和摘要。若已有草稿仍为单语，先用 `upload-draft` 刷新；`publish-prepared` 在推送及创建标签前检查草稿的双语章节。已有发布记录或标签的说明保持冻结。

## 准备环境

完整准备和公开从 Windows 上的 master 检出根目录运行；`prepare -BuildOnly` 可在任意分支的干净 worktree 中运行。准备前核对：

- 工作树干净；完整准备时，所有待发布改动已经过审查并在 master；跟踪文件的实际字节符合 `.gitattributes`。仅 `git status` 干净不足以证明行尾正确。
- `Version.props`、`.claude-plugin/plugin.json`、`.claude-plugin/marketplace.json` 和 `CHANGELOG.md` 最新版本条目一致；日期为有效 ISO 日期；`docs/releases/v<版本>.md` 含中英文两节。预发布还须核对生成的未验收清单。
- .NET SDK 与依赖缓存可用；八版 Siemens PublicAPI 在 `sdk/`，或由 `-PublicApiRoot` 指向其父目录。可用 `-NuGetConfig` 指定已有的离线配置。
- 解码器使用固定 CPython，必要时由 `-Python` 或 `TIA_MCP_DECODER_PYTHON` 指定；PLC Tools 伴随 Python 的依赖导入检查通过。伴随解释器按 `-CompanionPython`、`TIA_MCP_PLC_TOOLS_PYTHON`、已有的 `TiaMcp_Output/ecosystem-python/Scripts/python.exe` 解析。
- 本机已有校验过的随包 .NET 压缩包缓存 `bin-build/cache/dotnet-10.0.12/`；SDK、缓存、密钥和私人项目均不随包分发。
- GitHub 凭据能上传草稿、推送 master 和创建标签。上传和公开按 `-Token`、`GITHUB_TOKEN`、Git Credential Manager 的顺序取令牌；不要把令牌写进文档或命令历史。
- 仓库为 `asckye/automation-engineering-workbench`。旧名（`asckye/` 下以产品旧名 TIA Portal Openness MCP 命名、单词以下划线连接的仓库名）永不再用于新仓库、复刻或改名，3.3 和 beta 更新器经 GitHub 的重定向读取发布（[ADR 0039](../standards/decisions/0039-repository-rename-never-reuse-old-name.md)）。

准备会重建当前检出的 `runtime/`（保留其中的 `README.md`）和 `bin-build/prepared/<版本>/`，并清理 `src`、`tests`、`third_party`、`build-tools/native-call-weaver` 下的全部 `bin`/`obj`，避免旧依赖进入交付包；运行中的 `build-tools/release` 不清理。`TEMP`、`TMP`、`TMPDIR` 和 `DOTNET_CLI_HOME` 使用仓库外的短 `tmr-<8位标识>` 目录，运行结束后删除；占用中的文件导致清理失败时保留目录并打印警告。重跑前保留仍需审查的旧日志；不要让运行中的程序占用这些输出。命令会验证跟踪文件未被改写，生成的包清单只写入暂存目录。

## 提前准备候选

以下以 `4.0.0-rc.6` 为例。版本号必须与准备好的源文件一致；命令不会替维护者改版本或提交文件。

```powershell
dotnet run --project build-tools/release -- prepare -Version 4.0.0-rc.6
```

完整准备核对完整门禁记录后运行依赖漏洞扫描，再按顺序完成构建、暂存校验、ZIP 和摘要生成，再对提取的 ZIP 运行交付检查，然后写候选记录并上传私有草稿，2026-11-04 实测约 6 分钟（未计入漏洞扫描）。注册套件、传输、配置页、原生安全、工具用法与活动输入检查由 `merge-check` 执行，范围及最低通过数、最大跳过数见[验证](validation.md)。只有全部通过、工作树仍干净且测试用运行文件与 ZIP 一致时才写入 `candidate.json`；各检查运行到底后统一报告失败。各阶段和检查输出耗时，供后续优化使用。`BehaviorParityTests` 在 `merge-check` 中随 `foundation-api` 执行；不再单独构建或运行 `host-behavior-parity` 作业。

扫描先于任何清理或构建，报告保存在 `bin-build/prepared/<版本>/logs/vulnerable-packages.json`。未批准的 High/Critical 阻止准备；网络或恢复失败、空源、无效报告显示 `not run`，停止并提示恢复网络后重跑，通常约 20 秒内失败，超时上限为 300 秒。离线 `-NuGetConfig` 只用于后续构建；扫描显式使用 nuget.org。`-NoDraftUpload` 也需要完成扫描；`-BuildOnly` 不生成候选，因而跳过扫描。`merge-check`、`upload-draft`、`publish-prepared` 不扫描，公开阶段的 300 秒预算保持不变；不要求 GitHub CI 扫描收据。

| 选项 | 用途与边界 |
|---|---|
| `-TimeoutMinutes <正整数>` | 从提取 ZIP 到交付检查结束的总时限。每静默 10 分钟显示一次作业心跳。到达总时限时列出运行中的作业及耗时，并行终止本次检查的子进程树，最多再等 5 秒，报告仍在清理的 PID 并返回失败。构建阶段的 MSBuild 属性查询另有 5 分钟的单次限时。 |
| `-BuildOnly` | 跳过漏洞扫描，只构建、暂存校验和打包，不运行交付包检查，不生成可发布候选，不上传草稿；可在任意干净分支检查构建和交付布局，无需完整门禁记录 |
| `-NoDraftUpload` | 仍须通过漏洞扫描；完整检查通过后保留本地候选，但不上传草稿；之后使用 `upload-draft` |
| `-PublicApiRoot`、`-NuGetConfig` | 指定本机 SDK 父目录及离线 NuGet 配置文件 |
| `-Python`、`-CompanionPython` | 分别指定解码器和 PLC Tools 的解释器绝对路径 |
| `-MaxParallelism` | 同时指定构建和交付检查的并行度，必须为正整数。省略时构建默认使用 6，交付检查使用可用逻辑处理器数减 4，最低为 1。交付检查实际串行时默认限时 360 分钟，并行时为 120 分钟。显式指定的 `-TimeoutMinutes` 按原值生效。 |

成功产物位于 `bin-build/prepared/<版本>/`：

- 4.0 起的正式版生成 `TIA_MCP_v<版本>_<YYYYMMDD>.zip`，预发布沿用 `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip`。两者均附同名 `.sha256` 文件，检查和上传使用同一份 ZIP。
- `candidate.json`：版本、提交、完整门禁的完成时间（`mergeCheck.utc`）与树哈希（`mergeCheck.tree`）、ZIP 路径、SHA-256、大小、文件数、检查名称和完成时间；上传成功后还含草稿与资产 ID。
- 暂存包及 `logs/`。候选记录和日志留在本机，不提交 Git。

交付文件按 `scripts/operations/delivery-files.json` 过滤，包括八版宿主、worker 和适配器、工作台、工具、用户文档、模板、插件及许可证。暂存阶段生成 `manifest/package-manifest.json`、`delivery.json`、`tools-list.json` 和文件清单 `multi-version-build.json`。旧构建收据不进入新包；`runtime/verification/`、源码、SDK 和私人数据不交付。

## 重试草稿上传

上传失败不会删除通过检查的本地候选。使用准备阶段已经构建的 Debug 发布工具重试：

```powershell
dotnet run --no-build --project build-tools/release -- upload-draft -Version 4.0.0-rc.6
```

命令创建或复用同版本草稿，核对并保留匹配的资产、替换不匹配的资产，草稿最终只有 ZIP 和 `.sha256` 两项资产。草稿说明来自对应版本的发布说明，包含候选提交和未验收边界。成功后回写候选中的 ID；无需重新构建或测试。

## 确认后公开

维护者先审查准备结果和发布说明，再明确确认发布。确认后保持候选提交及工作树不变，从同一检出运行：

```powershell
dotnet run --no-build --project build-tools/release -- publish-prepared -Version 4.0.0-rc.6
```

命令先核对候选、HEAD、干净工作树、ZIP 与 sidecar、草稿资产的大小与摘要以及已有标签的目标。核对通过后推送候选提交到 `origin/master`，按需创建 `v<版本>` 标签，公开草稿，再确认公开资产可下载。正式版设置 `prerelease=false`、`make_latest=true`，成为 GitHub **Latest**。beta 和 rc 设置 `prerelease=true`、`make_latest=false`。核对草稿与标签期间，工具向 GitHub Actions 查询候选提交，逐个显示工作流最近一次运行的最新尝试及其结论或当前状态。推送 master 应触发 offline-checks、validate-bundle 和 vulnerability-scan，任一失败或缺失都会报警。若没有匹配的运行，工具另查远端是否已有该提交；提交尚未推送时会明确提示。CI 状态不可用或查询超时也只报警，继续公开。

300 秒是从发布工具进程启动起的硬性要求。超过 150 秒不再开始标签步骤；标签步骤后仍先尝试公开草稿，其余请求和重试受剩余预算约束。所有结果均打印总秒数。超时或网络故障不会变成成功；已创建标签或已公开的状态必须由重跑核实。

### 发布 4.0.0 正式版

将版本文件、CHANGELOG 和双语发布说明准备为正式版本后，沿用同一流程：

```powershell
dotnet run --project build-tools/release -- prepare -Version 4.0.0
# 需要补传或刷新草稿时
dotnet run --no-build --project build-tools/release -- upload-draft -Version 4.0.0
# 维护者确认后
dotnet run --no-build --project build-tools/release -- publish-prepared -Version 4.0.0
```

正式版上传和公开前均核对候选 ZIP 的版本和包名，以及 `docs/releases/v4.0.0.md` 的中英文两节。3.3 更新器无法识别正式版的新包名，现有安装会停留在 3.3。要升级，请手动下载并按[从 3.3 迁移到 4.0](../guides/migrate-3.3-to-4.0.md)并排安装。

## 失败后恢复

| 失败位置或结果 | 处理 |
|---|---|
| 准备的前置条件、构建或检查失败 | 修复报告的问题，由维护者将源文件改动提交到 master 后重跑同一条 `prepare`；不要改候选或清单中的哈希 |
| 草稿上传失败，候选仍存在 | 排除凭据或网络问题，重跑同一条 `upload-draft` |

上传较大附件时，工具每隔至少 10 秒输出已发送字节数和平均速率。连续 60 秒没有发送进展或上传结束后仍未收到响应，本次尝试终止并按原次数重试。如果代理持续阻塞，可在绕开代理的网络上将同一候选目录中的 ZIP 和 `.sha256` 文件按原名上传到同一版本的 GitHub 草稿。保持草稿状态，随后运行上面的 `upload-draft` 命令；它按文件大小和 SHA-256 核对已有附件，补传缺失或不匹配的文件，并将核对后的附件 ID 写入本地候选记录。
| 公开前核对失败 | 按报错修复候选与环境不一致的问题；源代码变化必须重新准备。条件一致后重跑同一条 `publish-prepared` |
| “标签存在或状态未知、草稿可能尚未公开” | 重跑同一条 `publish-prepared`，由命令读取已有标签和草稿状态；不要手工删除标签 |
| “已公开、下载未确认” | 重跑同一条 `publish-prepared`；已有公开版本及资产匹配时只确认下载，不再次推送、建标签或公开 |

恢复沿用同一版本和同一候选；不要以新建同名版本、改摘要或重传未经检查的包绕过失败。

## 发布后验证

GitHub 发布后工作流只验证公开 ZIP 与 `.sha256` 相符；它不再把标签中的旧构建记录当作发布依据，也不证明原生功能正确。

发布后由获授权的测试者和维护者进行 VM 回归。先复测对应修复，再按[预发布真机验证清单](../guides/beta-tester-checklist.md)检查真实行为；VMAUTO-1 已取消，改为发布后独立 AI 会话按书面测试说明回归，结果按固定格式保存在 `bin-build/vm-test/<run>/`，见[开发计划](../plan/plan.md)。结果写入[测试者反馈](../plan/progress.md)，复核后进入真机台账。离线检查或预发布公开都不替代真实 TIA 验收。
