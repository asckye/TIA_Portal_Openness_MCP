# 完整 Release 发布流程

`release -CiTimeoutMinutes` 默认等待 CI 50 分钟；发布提交和发布后核验均使用此默认值。

状态：现行 · 最后核对：2026-10-11，任务启动基线 master `578a05ee`

交付包 `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip` 包含 V14 SP1、V15.1、V16–V21 MCP 运行文件、工作台、Studio 及八版原生适配器，以及运行资源、用户文档、模板、插件和许可证。自 3.3.0 之后的下一个版本起，交付 ZIP 不再携带开发源码及构建工具。各发布键的工具范围不同，见[版本矩阵](../reference/version-tools.md)。每次发布同时上传 .sha256；GitHub 自动生成的源码压缩包不含运行二进制。

运行文件不提交 Git。交付的构建记录是 manifest/configurator-build.json 和 manifest/multi-version-build.json，manifest/delivery.json 绑定这两份的哈希。P8-04j2 起交付包不含 V20/V21 引擎：引擎交叉检查的记录 manifest/release-build.json 原样保存在 multi-version-build.json 的 `engineCompatibility` 段，本身不再交付。PublicAPI、个人密钥、私人 TIA 工程、设计交接和构建日志不分发，原始版权与许可证保留。

更新器 `runtime/tools/TiaMcp.Updater.exe` 及 `.exe.config` 由 `build-release` 构建并测试到 `bin-build/updater/`，不属于任何运行文件清单。`build-release` 把两者的包内路径、SHA-256 和长度写入 manifest/release-build.json 的 `updaterFiles`；`build-multi-version -CompleteOnly` 核对磁盘字节后把同一行复制到 multi-version-build.json 顶层 `updaterFiles`，由 delivery.json 一并绑定，`compare-release-records` 要求两份相等。打包器只接受与记录一致的更新器字节，`validate-bundle` 带二进制时在仓库检查 `bin-build/updater/`、在包内检查 `runtime/tools/`，资产核验要求 ZIP 的运行文件恰为运行文件清单加记录的两个更新器文件。

## 交付清单与两种校验模式

每日冷全集由 `dotnet run --project build-tools/release -- nightly -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe>`
运行，默认使用本地 `origin/master`，可用 `-Rev` 指定提交；工具打印每日 03:00 的 Task Scheduler 命令，由维护者登记。
运行目录为 `bin-build/nightly/<date>-<sha8>`，`latest.json` 保存提交、报告路径、通过/失败、耗时和三十分钟门槛。
`history.json` 是发布阻断历史，不随旧产物清理。release、`release -AdoptEvidence`、publish 和独立 preflight 在发布提交或上次发布之后的
祖先提交存在失败/超时 nightly，且没有更晚的相关成功 nightly 时拒绝继续；没有 `-AcceptNightlyFailure` 覆盖项。
同一干净提交的成功 nightly `release-evidence.json` 可直接采纳，仍逐项验证 tree、输入、机器与原有全量门禁。
merge/affected/quick 报告不能用于发布；合并验证与 nightly 操作见[验证分层](validation.md)。

[`scripts/operations/delivery-files.json`](../../scripts/operations/delivery-files.json) 是唯一交付规则文件。
`include.files` 为精确路径，`include.prefixes` 为以 `/` 结尾的目录前缀，`exclude` 优先；未匹配的跟踪文件不交付。
Python 打包器、资产核验、仓库/布局检查与 .NET release tool 校验器、更新器均读取它。运行文件仍只来自构建记录，
不会把磁盘上任意新文件塞入 ZIP。`runtime/verification/` 只用于发布前的 IL 验证，不分发。

保留 BundleLayout 的九项资源、全部模板、生态与操作脚本、Claude Code 插件及其 skill、用户说明和许可证。
Python 桥接所用两个第三方项目只交付 `src`、PLC Tools 的八个 `packages/*/src` 和许可证；
`tia install-plc-tools` 准备用户自己的 Python 3.12+ venv 和外部运行依赖，不再依赖未交付的本地项目打包元数据。
字体、Studio、TiaGitAddIn、Siemens OPC UA、Eido 与嵌入官方代码示例的许可原文在 `docs/licenses/` 保留副本。

交付的构建记录供解包校验读取版本、依赖清单及 SHA-256，`delivery.json` 绑定记录哈希并供软件检查版本；
`tools-list.json` 用于包内入口及工具数量校验，`version-tools.json` 是用户版本矩阵入口。
其他构建证据和 `RELEASE_STATUS.txt`、`release-file-hashes.json` 不进入新 ZIP。

- 仓库模式：`dotnet run --project build-tools/release -- validate-bundle -Strict` 保留源码、构建输入哈希、版本和本地 IL 校验；日常检查可用
  `-NoBinaries -SkipSourceHashes`。`Check-Repository.py` 运行源码门禁与 BundleLayout 表校验。
- 包模式：从仓库执行 `dotnet run --project build-tools/release -- validate-bundle -BundleRoot <解包根> -PackageMode -Strict` 与
  `Check-Repository.py --root <解包根> --package-mode`。没有 `Version.props` 时也自动识别包模式。
  检查交付资源、用户文档链接、许可证、构建记录、运行文件版本/哈希；不读取源文件或调用包内 IL 验证器。
  生成的旧 package/blueprint 元数据中的 `scripts/checks/validate-bundle` 条目仅属仓库说明，包模式明确忽略该单项。
  新增资源必须纳入规则，不能以源码缺失为由跳过其他必需项。

带二进制的 `validate-bundle` 还检查交付程序集的依赖闭包：逐个读取宿主、worker、Studio、更新器、
工具和根启动器的 PE 引用及 `deps.json`，按程序目录、Framework 配置的私有探测目录和随包共享框架检查传递依赖。
PLC worker 的 `ProjectGroupRuntime` 嵌入程序集按既有加载器规则解析，并继续检查其传递引用。
交付规则排除的文件不能满足依赖；失败信息包含程序路径和缺失程序集名称。Framework/GAC 程序集无需复制，
已声明身份的 Siemens Openness PublicAPI 程序集继续从所选 TIA 安装加载，不分发 SDK。
打包器在实际暂存目录运行同一检查，package、quick、full 三档均执行。

发布链将正在执行的 release tool 程序集路径通过 `Package-Release.py --release-tool <assembly>` 传给打包器。
子校验直接运行该程序集，不执行会重新构建项目的 `dotnet run`，也不假设普通 `bin/Release` 布局。
独立打包和 CI 未传路径时先在 `bin-build/package-runners/` 的唯一目录构建一次，再复用该工具完成两次校验；
链中的 `build-tool` 检查同样使用唯一输出目录，保留完整构建检查。

无发布构建时可查看当前文件投影，不执行 Release 或生成可发布资产：

```powershell
python build-tools/Package-Release.py --dry-run --include-untracked --stage-directory bin-build/delivery-preview
python scripts/checks/Check-Repository.py --root bin-build/delivery-preview --no-binaries
dotnet run --project build-tools/release -- validate-bundle -BundleRoot bin-build/delivery-preview -PackageMode -Strict -NoBinaries
```

`--include-untracked` 仅在 dry run 中用于预览待审查的新文件，正式打包仍要求干净提交树。
预览打印完整文件集和排除的顶层分组；没有运行二进制时不能作为完整发布验证。
仓库的 `-NoBinaries` 模式允许 CHANGELOG 最新条目高于已发布版本，但必须有同版本发布说明；
各份旧 manifest 仍须相互一致。包模式和带二进制的发布模式继续要求精确一致，不能手改记录哈希。

## package、quick 测试包与 full 发布候选包

VM 测试包使用 `-Tier package`。它只执行源码预检、全部交付二进制构建、打包、严格验包、重定位和八版产品 smoke；
不执行套件、自测、稳定性压测、快照捕获或生态检查。预检保留仓库、文档链接、死引用、版本和静态包规则。
`quick` 在这些必需项上按变化路径追加检查；`full` 执行全部检查。三档交付相同的产品集合，都不证明真实 TIA 工程验收。

```powershell
# VM test package; use prepared local SDK/runtime archives and offline NuGet config.
dotnet run --project build-tools/release -- run-release-build -Tier package -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -OutputDirectory bin-build/test-package
# Branch checks before spending time on the complete candidate.
dotnet run --project build-tools/release -- branch-gate -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -OutputDirectory bin-build/branch-checks
```

构建缓存默认在主仓库软件目录的 `bin-build/build-cache/`（Git 忽略），在清理 worktree 后仍可复用。
`-BuildCacheDirectory <absolute-directory>` 可指定其他目录；`-NoBuildCache` 禁用缓存。
每个构建单元一个可读名称的子目录，`index.json` 列出 unit、inputHash、createdUtc、lastHitUtc 和 sizeBytes；
每个输入哈希保存独立的完整输出。默认总容量上限为 10 GiB（条目的输出文件和元数据字节数，不含单元索引），
可用 `-BuildCacheMaxBytes <positive-byte-count>` 覆盖；每次存入或命中后按最近成功命中时间跨单元淘汰最旧条目。
超出上限的单个条目不保留。索引损坏可从条目元数据恢复；命中仍验证完整文件清单和 SHA-256。

```powershell
# 显示默认路径、容量上限、实际大小和全部单元索引；也可传 -BuildCacheDirectory。
dotnet run --project build-tools/release -- cache-info
# 清空默认缓存；可传 -Unit <index 中的完整 unit 值> 只清一个单元。
dotnet run --project build-tools/release -- cache-clear
```

缓存目录应在产品输出和清理目录之外。不同路径的工作树按每个 worker 发布键、引擎、Foundation host、Studio、
原生适配器、配置器、harness、weaver 和 release tool 分别缓存。
键覆盖 MSBuild 实际求值的源码、链接资源和项目引用闭包、导入后的 props/targets、项目本地输入集合、NuGet assets/锁/config
及实际包文件、编译器和 Framework 引用、SDK 副本、固定 bundled .NET 版本、构建参数与环境属性。
工作树内输入用仓库相对路径（正斜杠），外部包、SDK 和 Framework 引用保留绝对路径。
预处理项目文本、NuGet assets、构建参数与环境值中的工作树根路径统一为固定 token，兼容大小写、两种分隔符和 JSON 转义。
求值前使用构建相同的离线配置、有效属性和环境执行 restore；publish 的 restore 同时设置 `_IsPublishing=true`。
环境仅计入 `DOTNET_*`、`MSBUILD*`、`NUGET_*`、`Configuration`、`Platform`、`UseSharedCompilation`、`NuGetAudit`、配置器编译器的 `LIB` 和 `TIA_MCP_*`（名称不区分大小写）。
排除 `DOTNET_CLI_HOME`、`TIA_MCP_DATA_DIRECTORY`、`TIA_MCP_DIAGNOSTICS_DIRECTORY`、`TIA_MCP_RELEASE_TEMP_ROOT`、`TIA_MCP_RELEASE_CHECK_PLAN`、
`TIA_MCP_TEST_PUBLIC_API_ROOT`、`TIA_MCP_BUILD_CACHE_*` 和 `TIA_MCP_OFFLINE_NUGET_CONFIG`；离线配置文件内容另行哈希。
其他会影响构建的设置应通过显式 `-p:` 参数或已哈希的 props/targets 提供。
求值失败或输入缺失时重建且不写缓存；构建后再次求值，输入变化则不保存。
先在唯一暂存目录保存完整产物及文件哈希，再原子移动为完整条目；复用前验证全部文件、清单与哈希，损坏或缺失均重建。
schema 5 的条目按仓库相对路径保存本项目及实际构建的所有 ProjectReference 的 TargetDir，publish 还保存发布目录。
引用的框架、配置与属性沿用 MSBuild 的 `PrepareProjectReferences` 求值；不确定的自定义引用构建目标退回冷构建。
每个目录保存独立清单，全部目录验证成功后才按清单覆盖文件；不删除目录或清单外的文件，保留其他单元恢复的共享输出。
路径嵌入检查覆盖每个输出目录；任一引用产物记录 worktree 路径时也不缓存整个单元。旧 schema 的条目不会命中。
发布链不直接读取 `obj/` 编译中间产物；缓存命中前的 restore 重新生成 NuGet assets 与导入文件。
缓存只复用编译结果，各档选择的检查每次重新执行。包内 tier record 和运行结果保存 `buildCache` 命中/缺失记录。

正式 `release` 强制完整档冷构建并禁用构建缓存，避免把缓存的正确性当作发布信任边界。
`-SkipBuild` 因此会被拒绝；`-Resume` 也重新构建。`publish` 在访问凭据或 GitHub 前验证结果记录和 ZIP 内完整档记录，
拒绝 package、quick、pending、缺少任一 full 检查，以及缓存启用或缺少冷构建来源的包。
需要独立 publish 的候选用 `run-release-build -Tier full -NoBuildCache` 生成；记录中的 `buildCacheEnabled=false` 是必需证据。

[`release-checks.json`](../../build-tools/release/release-checks.json) 是路径到检查的映射；quick 使用最具体的匹配规则。
Studio 视图只选择 GUI 检查，不选择引擎压测；文档路径只运行必需项，示例路径追加用法/响应检查。
构建依赖由缓存的实际 MSBuild 输入决定，文档/示例变化不会使无关 worker 或适配器失效。
未映射路径或缺少可信 baseline 继续选择全部检查。
默认 baseline 在 `bin-build/release-review/last-full.json`，`-FullBaseline` 可选择本 worktree 的 bin-build 下其他文件。
仅 full 全部成功、最终包再次严格验证且源码未变化时更新；baseline 绑定 ZIP 哈希与源码集合。

`branch-gate` 构建当前分支的两版引擎及夹具，可复用同一缓存，然后运行普通/隔离各四组合的稳定性检查（默认 50 轮，
与 check-all 相同，不能低于 50）、诊断夹具（行为至少 36、拒绝至少 5）、两版诊断 JIT/织入清单、worker supervisor（至少 25）、
HTTP concurrency mode、默认审批门禁及其自测、两种快照的 `verify`。
它复用完整链的检查代码和成功判定，不启动 live 分支。引擎部分不捕获快照；V20/V21 的 PlcWorker 后继作业（P8-04j1a）与发布链相同，执行 contracts/responses 捕获并与基线逐字节比较。输出 `result.json` 与逐项日志，任何检查失败即失败。
目标为热缓存 package ≤ 10 分钟、branch gate ≤ 8 分钟；必须用成功的实际运行证明。

构建中包标为 `checkStatus=pending`，选择的检查全部通过后重新打包为 `passed`。
退出后逐字节恢复跟踪的 manifest 和生成文档；运行中的记录副本、计时和日志保存在 `bin-build/release-review/`。
`-MaxParallelism` 控制检查并发；快照 compare 等待自己的 capture，失败停止新任务并等待已启动任务完成。
比较时间必须同时记录冷/热缓存、机器空闲状态和 quick 的选择集合，不能将失败运行或 dry run 当成成功包时间。

## 测试包（package-test）

`package-test` 从当前 checkout 在几分钟内打出可安装的测试包，供他人试用尚未发布的构建。它不发布、不打 tag、不写入发布记录，也不改写跟踪文件；
与 `run-release-build -Tier package` 不同，它不运行预检、严格验包、重定位或产品 smoke。

```powershell
# 打包已构建的产物：runtime/、根启动器和 bin-build/updater
dotnet run --project build-tools/release -- package-test
# 先增量构建产物（不运行测试），再打包
dotnet run --project build-tools/release -- package-test -Build -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> [-OutputDirectory <dir>] [-BuildCacheDirectory <dir>]
```

- 不带 `-Build` 时，八个宿主、八个 PlcWorker、工作台、根启动器、更新器、写保护、插件启动器或捆绑 .NET 缺失，或早于最新的产品源码
  （`src/`、`third_party/SiemensOpcUaModelled`、原生织入器、`Version.props`、`Directory.Build.props`、`global.json`，不含 bin/obj 与 Markdown）时拒绝，并列出文件和较新的源码。
- `-Build` 复用发布链的构建步骤：原生织入器、`get-bundled-dotnet -Offline`、`build-plc-workers`、`build-studio -NoRecord`（含根启动器，不改写
  `manifest/configurator-build.json`）、Foundation 宿主发布并按八个发布键安装、工作台 apphost、写保护、插件启动器和更新器，最后确认宿主与工作台加载捆绑 .NET。
  不运行测试，不改版本或记录；默认使用 MSBuild 增量构建，`-BuildCacheDirectory` 启用构建缓存。构建前后比较全部跟踪文件，有改写即失败。
  成功后 `bin-build/test-package-work/last-build.json` 记录开始时间和产物哈希，增量构建未重写的产物据此视为最新。
- 内容为交付清单过滤后的跟踪文件（工作树中的内容）、磁盘上的运行文件和 `bin-build/updater` 的更新器，排除 `runtime/verification/`、`*.pdb` 与 Siemens PublicAPI（`Siemens.Engineering*`）；
  交付规则已排除 `Siemens.Collaboration.*`，可再分发的 `Siemens.Simatic.S7.Webserver.API` 依赖和 Siemens 许可文本与正式包一样交付。
  暂存目录运行不依赖记录的包检查：`validate-bundle -PackageMode -NoBinaries`，交付清单、包入口和蓝图列出的全部路径（含二进制）、包资源、测试策略程序集、
  每个入口程序集的 Openness 组运行时、依赖闭包和 `check-bundle-layout`。绑定记录的运行文件哈希与版本检查不适用于测试包。
- 默认输出到 `bin-build/test-packages/`：`TIA_MCP_TestPackage_v<版本标签>_<yyyyMMdd-HHmm>_<短提交>[-dirty].zip` 和同名 `.sha256`。
  ZIP 根目录的 `TEST-PACKAGE.txt` 写明提交、工作树状态、构建时间和 “Test package - not a release; no release records are bound”。测试包不上传到 GitHub Release。

## 准备和执行

在 Windows 的 master 工作区操作，审查全部改动，停止该安装目录下的 MCP 和 Studio 进程。需要 .NET 10 SDK、Git、Framework 开发环境、Python 和八版 SDK。设置 TIA_MCP_PLC_TOOLS_PYTHON 指向已有 PLC Tools 伴随环境。SDK 目录结构见[构建说明](../reference/version-tools.md#build-and-package)。

先写并提交 CHANGELOG.md 最新版本条目与 docs/releases/vX.Y.Z.md，更新当前说明，确保工作树（含未跟踪文件）干净；发布后在 handoff.md 和 publication-vX.Y.Z.json 记录结果。发布正文写明各版范围、组件、测试结果和真实工程验收状态。英文文档和提交说明使用英文，不添加 AI 署名。

```powershell
dotnet run --project build-tools/release -- release -Version X.Y.Z -Summary "Concise English release summary" -PublicApiRoot <SDK-root> -V20ReferenceRoot <V20-SDK> -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>
```

命令通过 .NET 10 release tool 调度；Python 只运行尚未迁移的检查器与打包器。

脚本依次执行：

1. 首先运行 `prerequisites`：汇总检查 .NET 10 SDK、Python 3.12、实际伴随环境的模块/命令目录、八版 PublicAPI、固定 SHA-512 的 .NET 缓存包、令牌、干净工作树和至少 10 GiB 空间。没有缓存时先下载并验哈希；任何缺项均在修改版本或构建前失败。随后检查 master、上游和进程。
2. 更新 `Version.props`、插件版本、文档当前发布链接及路线图标题，立即执行早期门禁：版本/CHANGELOG/发布说明/README/路线图断言、仓库及链接、失效工具引用、仓库模式包验证、布局与交付集合自测、示例目录、版本目录接线、原生监督器与 MCP 安全自测、崩溃证据和写保护测试。路由选择与 PLC 名称匹配从当前生产源码提取方法并编译小型夹具运行，不需要完整引擎产物。
3. 冷构建多版本准备产物，运行 `build-multi-version -PrepareOnly -Test`；再运行两版 `build-release` 的完整检查，最后用 `build-multi-version -CompleteOnly -Test` 核对输入、运行文件和证据并绑定交付。发布期间禁用构建缓存和旧完整构建记录的复用。

4. 执行依赖实际二进制的仓库检查和严格包验证；构建后的路由/PLC 名称测试仍反射实际 V21 程序。检查通过后，正式运行才暂存明确路径并创建 `Release X.Y.Z: <summary>` 提交；`-DryRun` 在暂存前退出。

5. Package-Release.py 在仓库核对完整提交树、全部构建记录和源码哈希，运行发布期 IL 校验，再按交付清单过滤，在实际暂存目录运行包模式严格检查，生成 ZIP、SHA-256 和 package-result.json。
6. Verify-ReleaseAsset.py 独立验证 ZIP 等于交付清单过滤后的 tag 文件集加记录哈希的运行文件（排除 `runtime/verification/`）和 `updaterFiles` 记录的更新器。
7. 推送 master，等待 validate-bundle 与 offline-checks，通过后创建 annotated tag vX.Y.Z 并推送。

Build-Release 的程序集反射检查和工具清单生成运行在 net48 TiaMcp.Engine.Harness harness modes 中；独立生成器和输入检查使用 .NET 10 C# file-based apps。工具矩阵在重新生成后可用 `Generate-ToolCapabilityMatrix.cs --check` 验证稳定字节。它们沿用 `manifest/release-build.json` 的既有字段和最小/精确检查计数。
8. Publish-release 创建草稿、上传 ZIP 和 SHA-256、回读大小及 digest，然后公开发布并置为 latest；预发布不置为 latest（见[预发布](#预发布beta--rc)）。
9. 等待 Verify published release 下载并校验公开资产，再记录发布 URL、提交和验收状态。

任一步失败即停止。日志保留在 release.log、build.log、bin-build/releases/v<版本>/ 和 bin-build/multi-version/；完整引擎分版日志在版本目录下的 `v20/`、`v21/`，汇总为 `release-v20.log`、`release-v21.log`。一版失败仍收集并报告两版结果。令牌来自 -Token、GITHUB_TOKEN 或 Git Credential Manager，不打印、不放入发布包。

正式发布前先在同一 checkout 运行一次 `release -Version X.Y.Z -DryRun ...`（见[预发布](#预发布beta--rc)）：它执行到候选检查后在暂存前停止，不需要令牌，
可提前暴露只有发布链才检查的问题；演练后按其输出用 `git checkout -- <路径>` 恢复改写的版本文件和记录。
互不依赖的只读检查并行执行（P8-93）：早期门禁中的仓库、失效引用、构建前包验证、两个生成器 `-Check` 和三个 Python 自检同时运行，
需要编译的布局自检与套件随后逐个执行；完成记录后，`compare-release-records`、`preflight`、`validate-bundle -Strict` 与候选打包同时运行，
四项全部通过后才开始候选检查；候选检查中 plc-contracts、plc-responses 各自的八个捕获最多四个同时运行（与准备阶段的 PLC 作业相同，
各有独立输出、临时目录和数据目录，比较仍按表序在全部捕获之后进行）。并行阶段中任一项失败即不再启动新项，等待已启动项结束，并报告全部失败项。
每次运行把各阶段耗时写入 `bin-build/release-runs/<run>/stage-times.json`，演练结束时另在控制台列出。

## 采纳同一提交的 check-all 与 CI 证据（-AdoptEvidence）

维护者决定（2026-10-21，P8-93）：自 4.0.0-beta.2 起，发布可以采纳同一提交已通过的 check-all 与 CI 证据，不再重跑套件、捕获、
harness 模式与压测；发布仍冷构建产品、打包，并在实际包上运行全部包检查。任何检查都不删除、不放宽，只改变运行位置；
不带 `-AdoptEvidence` 的完整路径和 `-DryRun` 保持不变。

```powershell
# 1. 准备提交：机械更新版本（只改版本文件并列出路径），写 CHANGELOG 条目和发布说明，提交并推送，CI 在该提交上运行。
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.2 -BumpOnly
# 2. 在该提交的干净工作树上运行冷的、无缓存的 check-all；输出目录中写出 release-evidence.json。
dotnet run --project build-tools/release -- check-all -Profile nightly -Clean -NoBuildCache -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe> -OutputDirectory bin-build/check-all/beta2
# 3. 只读核对（-Ci 另读 GitHub Actions），然后演练与正式发布，参数与完整路径相同。
dotnet run --project build-tools/release -- check-release-evidence -Evidence bin-build/check-all/beta2/release-evidence.json -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -Ci
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.2 -DryRun -AdoptEvidence bin-build/check-all/beta2/release-evidence.json -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -Python <python.exe>
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.2 -Summary "..." -AdoptEvidence bin-build/check-all/beta2/release-evidence.json -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -Python <python.exe>
```

- Profile：只有 `-Profile nightly` 的完整运行可采纳；默认 `merge` 的派生门禁和选择的 GUI 类不能作为发布证据。`keptCodeV20Passed` 保留，来自所采纳完整运行的 V20 套件。旧格式未记录 profile 时仍表示当时的全集。
- 证据：每次 check-all 结束时都写 `release-evidence.json`，是否可采纳由发布判断。它在任何生成之前记录提交、Git tree id、开始时的
  `git status`（含未跟踪文件）、全部跟踪及未忽略文件的 SHA-256 清单，PublicAPI 目录全部文件的长度与 SHA-256 摘要、离线 NuGet 配置的
  SHA-256、`dotnet --version`、`python --version`、伴随 Python 可执行文件的 SHA-256 和机器标识（无法取得的值记为 unavailable，
  check-all 照常运行，发布拒绝）；结束时记录运行模式（`-Clean`/`-Cold`、结果缓存、`-NoBuildCache`、
  `-BuildOnly`）、计划的全部作业、每个作业的状态与日志 SHA-256、每个注册套件合并 TRX 的计数与摘要文件 SHA-256、构建缓存模式、
  report.json/cpu-plan.json 的 SHA-256、退出状态、失败/复用数、源码漂移和三十分钟门槛。`-NoBuildCache` 让 check-all 不读写文件程序的构建缓存，
  并随之关闭结果缓存。
- 拒绝：另一提交或 tree；开始或现在工作树不干净；任一输入文件、PublicAPI、NuGet 配置、SDK、Python 或机器不同；部分运行（`-BuildOnly`、
  计划作业缺结果、缺注册套件、缺发布所需的作业族、日志或摘要文件缺失或被改动）；失败运行（退出非零、任一作业失败或被阻塞、漂移、
  超过三十分钟门槛、套件低于注册的最低通过数或高于跳过上限）；复用或缓存（结果复用、结果缓存开启、非 `-Clean`/`-Cold`、未带 `-NoBuildCache`、
  恢复了缓存的构建输出）；开始时间早于提交时间或完成时间在将来；Version.props 尚未是待发布版本。全部原因一次列出。
  `-AdoptEvidence` 不能与 `-EarlyGatesOnly`、`-Resume` 同用，`-BumpOnly` 不能与 `-DryRun`、`-EarlyGatesOnly`、`-AdoptEvidence` 同用。
- CI：正式发布用现有令牌读取 `repos/<repo>/actions/runs?head_sha=<提交>`，要求 `validate-bundle` 与 `offline-checks` 在 master 上由 push
  触发的最新一次运行（最高 run number 与 attempt）已完成且结论为 success，并逐页读取该次 attempt 的全部 job，每个都必须 completed/success。
    未分工的 `-DryRun` 不访问 GitHub，记录写明 CI 未核对；`check-release-evidence -Ci` 可在发布前只读核对。
- 采纳的阶段：早期门禁的 source-contracts、write-guard、crash-evidence 套件；build-release 公共阶段的注册套件（计数取自证据，同样要求
  通过数大于零且无失败、无跳过）；多版本准备与完成改为不带 `-Test`（记录因此为 `test: false`，PLC 证明只要求审批收据），即 Studio 配置/客户端/GUI
  测试、七个多版本套件、Foundation 与 PLC 传输、PLC harness 模式（含 P8-04l 移入的 V21 路由选择与 PLC 名称夹具）、资源、生态、用法、campaign、
  压测与准备阶段的 PLC 捕获，以及全版本用法审计（check-all 的 usage-audit-plc 与已提交的 `manifest/tool-usage-coverage.json` 逐字节比较）。
  发布要求证据含全部注册套件的 gate、五个 Studio 作业、八个发布键的 contracts/responses/transport-usage/usage、V20/V21 的 harness/approval/
  resources/ecosystem/campaign-inputs/stability c8 与 c16，以及 build-once、analyzer-warnings、suite-output-parity、tracked-tree-drift、usage-audit。
- 仍在本地运行：早期门禁的其余检查；产品冷构建、八版 worker、Studio、Foundation 发布与目录；Foundation 默认审批和 V20/V21 PLC 审批；
  check-all 不覆盖的 host behavior parity（V20、V21）、已安装与夹具适配器的 native-call 覆盖和 native harness 安全自检（准备之后与
  build-release 之前，二者并行）；build-release 的产品发布、三个计数自检、用法目录检查与诊断夹具；API 审计；配置器测试；preflight、
  严格验证、候选包、全部候选检查（含 HTTP 与已安装宿主的 PLC 捕获、Foundation 响应、打包的 no-TIA 响应、重定位、产品 smoke 与自检）、
  最终包与资产校验，以及发布提交后的 CI 与发布后校验。
- 记录：字段不变。`manifest/multi-version-build.json` 增加 `adoptedValidation`，写明证据文件（仓库相对路径）、SHA-256、运行号、提交、tree、
  输入摘要、起止时间、模式、CI 运行（workflow、run id、attempt、URL、job 数）、各注册套件计数、采纳的阶段和本地补充检查的日志 SHA-256；
  `validation.dotnetSuites` 由证据填写，`manifest/release-build.json` 的套件计数同样来自证据。运行目录另存证据副本
  `adopted-release-evidence.json` 与 `adopted-validation.json`。
- 发布提交只能包含 build 改写的记录（`manifest/` 与 `docs/reference/tool-matrix.md`、`docs/reference/version-tool-catalog.md`），
  且 HEAD 仍是证据的提交；否则在暂存前拒绝（演练同样检查）。

## CI 分工候选与在线采纳

`check-all -Profile merge -CiCovered <文件>` 可把[验证映射表](validation.md)中的 foundation/release/studio-true profile 交给同提交的 Windows CI。
merge 分工证据不能用于发布；发布另跑完整本机 nightly。nightly 需要独立子集与超集的同轮 TRX，拒绝只有聚合收据的 `-CiCovered`。
默认不分工，仍运行全部本机检查；分工模式要求本机 checkout 干净且与收据提交相同，防止借用 CI 覆盖未提交的测试输入。
对应的 Windows CI job 在覆盖范围内的全部命令成功后调用 `ci-coverage`，读取完整注册套件的 TRX、核对摘要与门禁，
foundation 另核对四个 runner 的完整分片并集报告；收据包含提交、仓库、run/attempt、job 名、workflow 哈希、注册定义和通过/失败/跳过数。
收据同时保存为 artifact 中的 `ci-receipt.json` 并以 `TIA_MCP_CI_COVERAGE=` 开头的编码行写入 job 日志。
发布收据只来自干净、未改写的 CI checkout；Linux、SDK 套件和未列入 profile 的检查不参与替换。
release 收据在 `build-configurator -Test` 之前生成，因为后者按原流程写入跟踪的配置器构建记录；该命令和全部断言仍执行，
采纳仍要求包含它的整个 job 最终成功，不能凭提前生成的收据接受后续失败的 job。

从当前 prep 提交的最新成功 push run 下载两份收据，保存在本 worktree 的 `bin-build` 下；从对应 attempt 的 jobs 列表取数字 job ID。
可只提供一个 profile；没有提供的部分仍本机执行。示例 ID 必须换成实际值：

```powershell
dotnet run --project build-tools/release -- ci-coverage -Receipt bin-build/ci-foundation/ci-receipt.json -JobId 123456 -Receipt bin-build/ci-release/ci-receipt.json -JobId 123457 -Output bin-build/ci-covered.json
dotnet run --project build-tools/release -- check-all -Profile merge -Clean -NoBuildCache -CiCovered bin-build/ci-covered.json -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe> -OutputDirectory bin-build/check-all/merge-ci
```

候选文件不是可信发布证明。check-all 保留全部原计划作业名与依赖，CI 承担的每个 shard/gate/静态检查写为 `covered by CI`，
绑定具体 run、attempt、job；其他检查照常执行，deadline、最低通过数、最大跳过数、压测参数和三十分钟门槛均不变。
`-CiCovered` 不与 `-BuildOnly`、`-ProveEquivalence`、本机 sequential baseline 同用。`-Clean` 转发候选的绝对路径。
生成、完整构建、analyzer、输出清单对等性、SDK/Windows 本机状态检查继续由本机证明；不减少全集覆盖。

历史全集证据的 `check-release-evidence -Ci` 与正式发布采纳会重新查询 GitHub（当前 merge profile 仍直接拒绝）：仍要求两个 workflow 的最新 push/master run 及其全部 job 成功，
另要求每条分工收据的 run/attempt 与该最新运行相同，数字 job ID、job 名、head SHA、run ID、attempt 精确一致且 completed/success，
并在该 job 的日志中找到完全相同的门禁收据。缺少/取消/失败/运行中、其他提交、旧 attempt、日志缺失或计数不同均拒绝，不能自动退回旧成功运行。
日志已过保留期时须重新运行 CI 并重新取得同 attempt 的收据。`release-evidence.json` 与最终 `adoptedValidation` 都保留每个分工作业和具体 CI 证明者。
含分工的证据不能离线采纳；`check-release-evidence` 必须带 `-Ci`，`release -DryRun -AdoptEvidence` 会拒绝它。
需要保持不访问 GitHub 的完整发布演练时，使用不带 `-CiCovered` 的 check-all 证据。

## 预发布（beta / rc）

维护者决定（2026-10-20）：我们不做真机测试，4.0 先以 GitHub 预发布交给其他用户测试。版本写作 `X.Y.Z-beta.N` 或 `X.Y.Z-rc.N`
（语义化版本，不接受其他后缀），例如：

```powershell
dotnet run --project build-tools/release -- generate-prerelease-notes -Version 4.0.0-beta.1
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.1 -DryRun -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -Python <python.exe>
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.1 -Summary "..." -PublicApiRoot <SDK-root> -V20ReferenceRoot <V20-SDK> -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>
```

- 版本只有一个来源：`Version.props` 的 `TiaMcpRelease`（数字 X.Y.Z）加 `TiaMcpPrerelease`（空、`beta.N` 或 `rc.N`），组合为 `TiaMcpReleaseLabel`。
  AssemblyVersion 与 FileVersion 仍为数字（`4.0.0`、`4.0.0.0`；PlcWorker 与更新器的 AssemblyVersion 保持 `1.0.0.0`，FileVersion 和
  InformationalVersion 同样来自 `Version.props`）；InformationalVersion、`TiaMcpRelease` 程序集元数据、插件版本、
  构建记录与 `manifest/delivery.json` 的 `release`、包名 `TIA_MCP_Delivery_v4.0.0-beta.1_<日期>.zip`、tag `v4.0.0-beta.1`、发布说明和
  `manifest/publication-v4.0.0-beta.1.json` 使用完整版本。正式版本 X.Y.Z 的行为不变。
- CHANGELOG 条目写作 `## [4.0.0-beta.1] - YYYY-MM-DD`，与正式版本一样在发布命令前写入；`docs/releases/v4.0.0-beta.1.md` 必须带有
  `generate-prerelease-notes` 生成的 “Not verified on a real TIA Portal” 段落。它来自[真机验收记录](../reference/real-machine-ledger.md)
  的四个 4.0 段落与[官方 API 审计 VM 核对清单](../reference/api-audit-vm-checklist.md)；两者变化后重新生成。段落中的每一项带全段连续的
  编号，测试者用[预发布问题模板](../../.github/ISSUE_TEMPLATE/prerelease_report.md)报告时填写该编号。发布命令和 `publish`
  在构建或访问凭据前拒绝缺少或过期的段落。正式版本不需要该段落。
- 发布时草稿和公开两次写入都带 `prerelease=true`、`make_latest="false"`，GitHub 的 latest 仍指向最新正式版本。正式版本保持草稿
  `prerelease=false`、`make_latest="false"`，公开时 `make_latest="true"`。
- 更新：3.3 的 `Update-Engine.ps1`、工作台更新检查和引擎的更新检查工具只读取 `releases/latest`，GitHub 的 latest 不含预发布和草稿，
  因此 3.3 用户不会收到预发布。4.0 的更新器、工作台和 `CheckProductUpdate` 对正式安装同样只读 `releases/latest`，并拒绝被标为预发布
  的答复；自身版本为预发布的安装读取 releases 列表，按语义化版本优先级选择最新的已发布版本（忽略草稿），包括随后的正式版本。
  更新器的 `-Version vX.Y.Z-beta.N` 可显式选择某个预发布，正式安装也可以；目标为预发布时，更新器在下载和替换前先说明。
  API 被限流时，预发布安装改读发布页，发布页只给出最新正式版本，更新器会说明更新的预发布要等 API 恢复后才能看到。
- 门禁：构建、离线套件、快照、check-all 与 CI 保持不变，预发布不放宽任何检查。发布命令本身没有因真机验收 NOT RUN 而拒绝的步骤；
  [真机验收记录](../reference/real-machine-ledger.md#预发布例外)中“发布前必须在真机上完成”的规则对预发布不适用（按上面的维护者决定），
  未验证项改为写入预发布说明。正式版本仍须遵守该规则。
- `-DryRun` 是本地演练：在当前 checkout（任意分支、允许未提交改动）依次执行版本机械更新、早期门禁、冷构建、preflight、严格包验证、
  候选包和候选检查，然后在暂存前停止；不需要令牌，不访问 origin 或 GitHub，不暂存、不提交、不打 tag。它把两次 GitHub 请求的正文写入
  `bin-build/release-runs/<run>/github-requests.json`，并列出改写的版本文件和构建记录；演练后用 `git checkout -- <这些路径>` 恢复。
  `-DryRun` 不能与 `-EarlyGatesOnly` 或 `-KillStrayEngine` 同用。演练只拒绝从本 checkout 启动的引擎/宿主进程，正式发布仍按名称拒绝所有进程。
- 机械更新只改 `.claude-plugin/marketplace.json` 中本插件条目的版本值，其余字节保持原样；早期门禁的 `validate-bundle -PendingRelease <版本>`
  把插件与 marketplace 版本与该待发布版本精确比较（构建记录要到 build-release 才重写），不带 `-PendingRelease` 时仍与 `manifest/delivery.json` 比较。
  包名必须正好是 `TIA_MCP_Delivery_v<完整版本>_<YYYYMMDD>`（ZIP 为同名 `.zip`）。路线图标题写作 `# 路线图与待办（v<版本>）`，机械更新按此替换。

## 分阶段与恢复

-NoPush 完成本地提交、打包和验证后停止；-NoTag 在推送及 CI 后停止；-DryRun 在本地完成构建、检查和候选包后于暂存前停止，不执行暂存或远程操作（见[预发布](#预发布beta--rc)）。
`-Resume` 重跑预检、早期门禁和冷构建。`-NoReuse` 仍可显式表达冷构建；`-SkipBuild` 不能用于正式发布。

原来的 `sourceFiles` 保持打包器所要求的集合；新记录另存 `validationInputs`，覆盖构建/验证脚本、生态桥接、参考数据、模板等输入。缺少该字段的历史记录需要重建一次。构建前后再次比较输入，期间发生变化则拒绝记录测试结果。缓存命中只恢复编译产物；当前档位的检查、记录时间和交付绑定每次重新生成，正式发布始终冷构建。`prepare-delivery` 要求多版本运行产物和匹配的完整引擎记录已存在，缺项在重建配置器前明确失败；它刷新引擎/配置器交付绑定并执行严格验证。正式发布仍需多版本完成记录及最终严格验证。准备阶段的 `bin-build/multi-version/prepared-build.json` 不是发布记录，不能通过最终八版本发布门禁。

长路径环境可用 `Package-Release.py --output-directory <较短的新目录>`，保留生成的 package-result.json 所记录的实际 ZIP 路径；恢复 Release 流程时将该结果记录及同名 ZIP/SHA-256 放到默认版本目录。

每次新运行或 `-Resume` 都将已有 `bin-build/releases/v<版本>` 完整移动到同级 `v<版本>.previous-<时间戳>-<唯一后缀>`，保留原归档和日志，再从当前 HEAD 生成并验证候选；完整引擎记录新增 `validationArtifacts`，绑定 API 织入清单和实际 GetToolUsage 证据的哈希。归档后保留哈希匹配的四个历史审计输入，冷构建仍重新生成全部当前证据；移动前检查绝对父目录和重解析点，不覆盖历史目录。无需手动归档。

已公开的 Release、tag 和资产不改写；发现问题应修正并发布新补丁版本。发布不自动更新运行中的服务或虚拟机。

## 独立检查与并发资源

```powershell
# 离线预检不下载，也不调用凭据管理器；缺少缓存或环境令牌会明确失败。
dotnet run --project build-tools/release -- prerequisites -PublicApiRoot <SDK-root> -Offline
# 已完成版本机械更新后，只跑早期门禁，不访问 GitHub、不构建完整引擎、不提交。
dotnet run --project build-tools/release -- release -Version X.Y.Z -EarlyGatesOnly -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>
```

| 共享资源 | 并发处理 |
|---|---|
| HTTP 端口 | TiaMcp.Engine.Harness 与 Python 协议夹具均从系统申请端口 0；没有两版共用的固定监听端口 |
| Logic、Runtime、contracts、第三方项目的 obj/bin | 工作树路径派生的 mutex 保护还原、编译和运行文件复制；该短段串行，随后两版长耗时门禁并行 |
| MSBuild/C# 服务 | 关闭节点复用和共享编译器；公共测试夹具、weaver 先构建一次，子流水线只读 |
| 开发宿主 `src/FoundationHost/bin/Release` | 一版的套件构建会重建它；两版流水线（build-release、branch-gate）因此各自在首个步骤前复制一份私有宿主，harness 步骤经 `TIA_MCP_TEST_HOST_PATH` 启动该副本 |
| TEMP/TMP、DOTNET_CLI_HOME、诊断目录及测试日志 | 各版独立目录；稳定性、生态证据沿用 GUID 子目录 |
| worker/宿主进程 | 原测试仅清理自己创建的进程；没有按全局进程名清理另一流水线 |
| tools-list、工具矩阵和 manifest | 两版成功汇合后才串行生成；任一版失败不写完整构建记录 |

`Build-release -SelfTest` 用两个必须同时启动的假流水线验证并发，以及一版/两版失败时的日志与汇总；不启动引擎。实际两版构建耗时仍须在具备 SDK、伴随环境和本地 HTTP 能力的维护者机器测量。

## GitHub 与原生验收

托管 runner 无法取得全部 Siemens SDK 重建引擎，因此由本机构建上传。推送 CI 检查源码、构建记录、Studio/WPF 及无 Siemens 依赖的基础引擎协议；发布工作流下载实际 ZIP，核对 tag、提交、文件集合和运行文件哈希，再在解包目录执行严格验证。

真实 TIA 工程导入、生成、编译、读回与设备操作须在明确指定的版本和工程上单独验收。构建、XSD、离线功能与公开资产验证不能代替原生验收。

## 离线审查完整构建链

维护命令 `dotnet run --project build-tools/release -- run-release-build` 可从没有运行产物的 checkout 开始。
它不修改版本、不提交、不发布，每步独立记录日志，在 `finally` 中逐字节恢复全部已跟踪的 manifest 和生成的版本工具文档；
后续步骤在执行期间使用上一步保存的构建记录。日志及执行中的记录副本保存在 `bin-build/release-review/<时间戳>-<GUID>/`。

```powershell
# 只展示顺序，不要求本地 SDK/cache，也不构建。
dotnet run --project build-tools/release -- run-release-build -Tier full -DryRun
dotnet run --project build-tools/release -- run-release-build -SelfTest
# 审查者预先准备好离线依赖后，实际执行；输出必须是新目录，仓库内仅允许 bin-build 下。
dotnet run --project build-tools/release -- run-release-build -Tier full -PublicApiRoot <SDK-root> -OutputDirectory <new-output-directory> -CompanionPython <prepared-python.exe> -NuGetConfig <offline-nuget.config>
```

`-MaxParallelism` 默认使用处理器数，可设为较小正整数；`-MaxParallelism 1` 使用串行执行。步骤 0–4 顺序完成并记录每步耗时。
之后 prompt-registration、契约捕获及比较、响应捕获及比较、重定位检查作为四个有界任务并行；两个 compare 各自等待其 capture 完成。
V20/V21 契约和响应捕获各运行独立进程，使用独立输出与临时目录，再合并为比较输入。多版本检查先串行构建套件，
然后并行运行互不共享构建目录、结果文件和宿主数据目录的测试套件。失败会停止启动排队任务，等待已运行任务结束，并保留每个步骤日志。

步骤顺序为（第 0 步为预检）：

0. `preflight`：不需构建的全部发布检查（仓库与链接、随包文档只链接包内文件、CHANGELOG 最新条目与
   `Version.props` 一致、死引用、生成器、V4 快照格式、发布脚本与审批/重定位检查器自检、严格包规则）。几分钟内跑完全部项目后
   一次列出所有失败，避免每次一小时的构建只暴露一个问题；也可单独运行 `dotnet run --project build-tools/release -- preflight`。
1. `Build-MultiVersion -PrepareOnly -Offline -Test`：八版 worker/Studio、六版 Foundation、bundled .NET 和功能/传输检查。
2. `Build-Release`：V20/V21 完整门禁、配置器及严格交付验证；随后 `Build-MultiVersion -CompleteOnly -Offline -Test`，验证准备证据并完成八版本记录与交付绑定。
3. `Package-Release.py --local`，生成 ZIP、sidecar 和 `package-result.json`。
4. 从 package-result 的 ZIP 父目录与无扩展名文件名计算实际 bundle 路径，执行 `validate-bundle -Strict -PackageMode`，保留二进制检查；路径计算按 ZIP 扩展名移除最后一段，不能留下尾点。
5. prompt-registration TRX 门禁。
6. 普通 V4 tool contracts capture（V20/V21）；参数与 master 现行调用一致，传 repo root、PublicAPI root 和真实 EXE，不传 `--harness`。
7. 与 `manifest/contracts/v4/baseline` 普通 compare；只在两版捕获完成后运行。
8. 普通 V4 response capture（V20/V21），传 `--harness <TiaMcp.Engine.Harness.exe>`；SDK-only 捕获通过测试宿主设置仅测试用的 readiness 标记。
9. 与 `manifest/contracts/v4/responses` 普通 compare；只在两版捕获完成后运行，差异直接失败，不刷新基线。
10. `Test-RelocatedBundle.py --bundle-root <extracted-package> --public-api-root <SDK-root>` 检查候选包在仓库外复制和读取时可用。
    当前用户必须位于 Siemens TIA Openness 组之外，检查会拒绝组内用户。该步骤只依赖已验证包，可与步骤 5–9 并行。

第 08 步通过 `TiaMcp.Engine.Harness.exe --harness` 加载真实 V20/V21 引擎宿主方法。`sdk-only-fixture` 仍复制 SDK 到临时安装布局；
TiaMcp.Engine.Harness 进程用仅测试用的 readiness 标记模拟 Openness 组已就绪，使 capture 能读取断开状态，并让产品默认审批在派发前拒绝写操作。
该标记不由引擎读取，也不改变真实安装要求 Openness 组。步骤仍用 `Run-Command -ProductDefaults`，不创建审批设置文件。
`Snapshot-ToolResponses.py --packaged-no-tia` 继续启动真实 EXE，并检查真实 no-TIA readiness 拒绝。

`-Dotnet`、`-Python` 和 `-CompanionPython` 指定本地解释器。默认生成清空 package feeds 的 NuGet 配置；
显式配置也必须清除继承的 feeds，拒绝 URL/UNC 网络源，并关闭 NuGetAudit。运行前检查全部 pinned runtime archives 已缓存且 SHA-512 匹配，
缺失时停止；多版本构建还显式传 `-Offline`，不下载。每步成功或失败后都恢复原记录并验证哈希。
`Package-MultiVersion.py` 的开发包也接受 bundled .NET 清单中的 `.version` 文件，框架目录以外仍保留扩展名限制；字体 `.ttf`/`.otf` 继续按原始字节哈希。
完整链仍需要非受限的本地 HTTP 与原子文件操作能力；此命令不进入 TIA/PLC/VM 或 live 分支。

何时运行完整链：合并任何改动宿主行为（引擎、Foundation、worker、审批与数据目录）、打包与交付清单、随包文档或发布脚本的变更后，
从干净 worktree 运行一次，不要攒到发布前。日常审查验证不包含发布构建内的宿主检查，4.0 发布候选曾因此连续暴露多个只在发布时才检查的问题。

产品 profile、拒绝夹具、版本目录、包计数和行为表统一由 `generate-catalogs` 生成；嵌入用法目录由 `generate-tool-usage` 生成。先按此顺序生成，再构建与捕获。`generate-release-migration -Check` 校验 3.x→4.0 迁移表，4.0 发布后冻结该表。`audit-version-tools` 校验版本目录并只写 API 证据；用法覆盖清单仅由 `audit-tool-usage` 写入。
