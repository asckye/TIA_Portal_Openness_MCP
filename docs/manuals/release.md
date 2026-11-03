# 完整 Release 发布流程

状态：现行 · 最后核对：2026-11-01，master `27675208`（发布证据与 CI 等待的现行做法）；其余部分 2026-10-11，任务启动基线 master `578a05ee`

发布默认不等待 CI。准备阶段完成构建和全部检查；发布阶段验证同一个候选包，然后打 tag 和上传。CI 结果在发布后读取。

现行做法（[ADR 0024](decisions/0024-ci-does-not-block-merges.md)）：发布不等待发布提交和发布后核验的 CI；CI 照常运行，失败按缺陷处理。`release -Version` 按 NATIVE-1a 直接完成冷构建与一次完整测试阶段，不要求 nightly，也不采纳先前的验证证据。`release-prepare` 与 `-FromCandidate` 是另一个封存候选工作流，见下文。新的发布检出需要 `bin-build/cache/dotnet-10.0.12/` 中的随包 .NET 运行时压缩包（下载已关闭），缺少时从已有检出复制。

交付包 `TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip` 包含 V14 SP1、V15.1、V16–V21 MCP 运行文件、工作台、Studio 及八版原生适配器，以及运行资源、用户文档、模板、插件和许可证。自 3.3.0 之后的下一个版本起，交付 ZIP 不再携带开发源码及构建工具。各发布键的工具范围不同，见[版本矩阵](../reference/version-tools.md)。每次发布同时上传 .sha256；GitHub 自动生成的源码压缩包不含运行二进制。

运行文件不提交 Git。交付的构建记录是 manifest/configurator-build.json 和 manifest/multi-version-build.json，manifest/delivery.json 绑定这两份的哈希。P8-04j2 起交付包不含 V20/V21 引擎：引擎交叉检查的记录 manifest/release-build.json 原样保存在 multi-version-build.json 的 `engineCompatibility` 段，本身不再交付。PublicAPI、个人密钥、私人 TIA 工程、设计交接和构建日志不分发，原始版权与许可证保留。

更新器 `runtime/tools/TiaMcp.Updater.exe` 及 `.exe.config` 由 `build-release` 构建并测试到 `bin-build/updater/`，不属于任何运行文件清单。`build-release` 把两者的包内路径、SHA-256 和长度写入 manifest/release-build.json 的 `updaterFiles`；`build-multi-version -CompleteOnly` 核对磁盘字节后把同一行复制到 multi-version-build.json 顶层 `updaterFiles`，由 delivery.json 一并绑定，`compare-release-records` 要求两份相等。打包器只接受与记录一致的更新器字节，`validate-bundle` 带二进制时在仓库检查 `bin-build/updater/`、在包内检查 `runtime/tools/`，资产核验要求 ZIP 的运行文件恰为运行文件清单加记录的两个更新器文件。

## 交付清单与两种校验模式

### 独立准备流程（NEW-REL-A）

`dotnet run --project build-tools/release -- prepare -Version 4.0.0-beta.3 -BuildOnly -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -Python <pinned-python.exe>`
从干净的 master 提前构建交付包，默认 MSBuild 并行度为 6（可用 `-MaxParallelism` 指定）。版本文件、插件、marketplace、最新 CHANGELOG 版本和有效日期、发布说明及生成的未验证清单必须一致；工作区字节必须符合 Git 规范化规则。
伴随 Python 按 `-CompanionPython`、`TIA_MCP_PLC_TOOLS_PYTHON`、本地 `TiaMcp_Output/ecosystem-python/Scripts/python.exe` 解析并检查依赖。

输出保留在 `bin-build/prepared/<version>/`：stage、逐阶段日志、唯一 ZIP 和 `.sha256`。每次运行先核对并清理该版本的旧输出目录，再清理 `runtime/`（保留 README）及 `src`、`tests`、`third_party`、`build-tools/native-call-weaver` 下的全部 `bin`/`obj`，避免旧依赖进入交付包；运行中的 `build-tools/release` 不清理。之后通过普通 MSBuild/dotnet 构建；同版本的新提交可重新准备，不使用 nightly、证据或旧发布编排，不写跟踪文件，不发布。
TEMP、TMP、TMPDIR 和 `DOTNET_CLI_HOME` 使用仓库外的短 `tmr-<8位标识>` 目录，运行结束后删除；占用中的文件导致清理失败时保留目录并打印警告。
四份清单及工具矩阵只在 stage 生成。`multi-version-build.json` 是更新器所需的 `{files:[{path,sha256}]}` 文件库存（不包含它自身）；旧构建记录和 `TEST-PACKAGE.txt` 不交付。ZIP 解压后的逐文件哈希必须与 stage 相同；同一次运行内不覆盖 ZIP。

不带 `-BuildOnly` 时，`prepare` 在解压后的 ZIP 上执行 smoke、搬迁、随包运行时、写保护、八版审批和契约检查，同时执行全部 44 个注册套件及保留的 PLC 功能检查。所有作业完成后汇总失败；只有全部通过、工作树仍干净且测试用运行文件与 ZIP 一致时，才写入 `bin-build/prepared/<版本>/candidate.json`。构建阶段与检查作业均打印秒数，并按耗时降序汇总。

默认随后上传草稿；`-NoDraftUpload` 只保留本地候选，并打印 `upload-draft` 作为下一步。上传失败保留候选，使用 `dotnet run --no-build --project build-tools/release -- upload-draft -Version <版本>` 重试。草稿上传完成后执行 `dotnet run --no-build --project build-tools/release -- publish-prepared -Version <版本>`。`-BuildOnly` 只构建和打包，不执行这些检查、不生成候选、不上传草稿。下面的旧命令及其记录格式仍保持原行为。

每日冷全集由 `dotnet run --project build-tools/release -- nightly -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe>`
运行，默认使用本地 `origin/master`，可用 `-Rev` 指定提交；工具打印每日 03:00 的 Task Scheduler 命令，由维护者登记。
运行目录为 `bin-build/nightly/<date>-<sha8>`，`latest.json` 保存提交、报告路径、通过/失败、耗时和三十分钟门槛。
`history.json` 不随旧产物清理。独立 publish 和 preflight 在发布提交或上次发布之后的
祖先提交存在失败/超时 nightly，且没有更晚的相关成功 nightly 时拒绝继续；没有 `-AcceptNightlyFailure` 覆盖项。
同一干净提交的成功 nightly `release-evidence.json` 可直接采纳，仍逐项验证 tree、输入、机器与原有全量门禁。
merge/affected/quick 报告不能用于发布；合并验证与 nightly 操作见[验证分层](validation.md)。

`release -Version`（含演练）在任何版本改写或构建之前检查生态/伴随 Python，选定的绝对路径传给全部子检查。
路径按 `-EcosystemPython`、`-CompanionPython`、`TIA_MCP_PLC_TOOLS_PYTHON`、本地已准备环境的顺序选择；
缺失时打印准确路径，先用 `tia install-plc-tools` 准备 Python 3.12+ 环境，再明确传入其 `python.exe`。
`prerequisites` 也检查同一路径及依赖。发布、`preflight`、`prerequisites` 和 `check-release-evidence` 还比较跟踪文件的
原始字节与 Git 规范化字节；即使 `git status` 干净，CRLF/LF 差异也会提前失败并列出全部文件。
先保留编辑内容，再按 `.gitattributes` 修正列出的文件（通常为 UTF-8/LF）；仅对可丢弃编辑的文件使用
`git -c core.autocrlf=false restore --worktree -- <paths>`。重新检查并在修正后的干净提交上收集新证据，不改写已有证据哈希。

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
  `-NoBinaries -SkipSourceHashes`。`dotnet run --project build-tools/release -- check-repository` 运行源码门禁与 BundleLayout 表校验。
- 包模式：从仓库执行 `dotnet run --project build-tools/release -- validate-bundle -BundleRoot <解包根> -PackageMode -Strict` 与
  `dotnet run --project build-tools/release -- check-repository -Root <解包根> -PackageMode`。没有 `Version.props` 时也自动识别包模式。
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
独立打包和 CI 未传路径时先在 `bin-build/package-runners/` 的唯一目录构建一次，再复用该工具完成仓库和暂存包校验；
预览模式用 `check-repository -NoBinaries` 执行源码门禁后列出投影。链中的 `build-tool` 检查同样使用唯一输出目录，保留完整构建检查。

无发布构建时可查看当前文件投影，不执行 Release 或生成可发布资产：

```powershell
python build-tools/Package-Release.py --dry-run --include-untracked --stage-directory bin-build/delivery-preview
dotnet run --project build-tools/release -- check-repository -Root bin-build/delivery-preview -NoBinaries
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

已退役的 `release-checks.json` 是路径到检查的映射；quick 使用最具体的匹配规则。
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
  （`src/`、`third_party/SiemensOpcUaModelled`、原生织入器、`Version.props`、`Directory.Build.props`、`Directory.Packages.props`、`global.json`，不含 bin/obj 与 Markdown）时拒绝，并列出文件和较新的源码。
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

维护者决定（2026-10-30，SPD-5）：候选包在 prep merge 准备，发布日只验证、打 tag、上传。
先用 `release -Version X.Y.Z -BumpOnly` 更新版本，补齐 CHANGELOG 与发布说明并提交。预发布说明保留未验证清单。
准备环境需要八版本地 PublicAPI、离线 NuGet 配置、已缓存且哈希匹配的运行时和 PLC Tools 伴随 Python。

```powershell
# 在干净的 prep 提交运行；完成后提交生成的记录，绑定这个记录提交，不创建 tag。
dotnet run --project build-tools/release -- release-prepare -Version X.Y.Z -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -Python <python.exe> -EcosystemPython <companion-python.exe> -NoPush -NoTag
# 在同一 worktree、同一分支验证；维护者确认前先运行这个无网络写入的演练。
dotnet run --project build-tools/release -- release -FromCandidate <release-candidate.json> -DryRun
# 维护者确认后，从 master 发布已推送的记录提交。
dotnet run --project build-tools/release -- release -FromCandidate <release-candidate.json> -ConfirmPublication
```

准备阶段保留所有现行检查：早期门禁、多版本 preparation/completion、build-release、完整 preflight（含完整 release-tool 套件）、
严格交付校验、候选打包、plc-contracts、plc-responses、09-packaged-responses-verify-plc、12-foundation-responses、
prompt-registration、静态自测、重定位、八版产品 smoke 和独立资产核验。所有原有计数、断言、超时和门槛保持原值。
未传 `-AdoptEvidence` 时先冻结 prep 源码的独立 check-all 快照，冷的完整证据运行与候选构建/检查并行；
生成的记录不会被证据快照读到。传入已有证据时按下节规则验证和采纳。
并行运行的证据与候选子进程使用互不重叠的 CPU 集合；少于四个可用 CPU 时先完成证据，再运行候选链。

ZIP、SHA-256 sidecar、package-result.json、release-candidate.json 位于本次 `bin-build/release-runs/<run>/candidate/`。
ZIP 只打包一次；其内部 package-manifest 保持完整计划的 `pending` 状态，所有检查结束后才生成外部 `passed` 候选凭据。
发布器仅在完整外部凭据、包哈希、全部检查回执均通过时接受这种包；旧的独立 `publish` 仍要求包内 `passed`，不能直接发布 pending 包。
这样检查的字节和上传的字节完全相同，检查后不再改写或重新压缩 ZIP。

准备命令在所有候选检查之后仅提交精确允许的生成记录，再对该提交执行资产核验，最终候选绑定其 commit、tree、分支、
全部工作树输入摘要、每个包内运行文件哈希、记录哈希、证据运行号及回执哈希。候选还保留经检查的 release tool 私有副本；
发布入口若因记录提交的版本戳而重建了启动器，会在验证私有副本全部哈希后转交给原工具字节执行。
`-NoPush -NoTag` 适用于 detached worktree 的完整演练；`release-prepare -DryRun` 运行全部本地步骤，
不暂存、不提交、不推送、不打 tag，资产核验读取当前跟踪字节，产出的 dry-run 候选不可发布。

发布日的记录检查只有 `compare-release-records`、`candidate-record-bindings`、`candidate-record-only-diff`：
比较两份记录，重算 ZIP/sidecar/package-result/记录/证据及回执哈希，核对 prep 到发布提交的差异只来自精确记录清单，
核对分支、tree、commit 和全部输入字节。完整 preflight 和 release-tool 套件已经在准备阶段运行，并绑定工具字节。
发布日不构建、不测试、不重新打包、不等待 CI。目标是验证 2–4 分钟加上传；`-DryRun` 目标低于 60 秒，需主机实测。
每阶段耗时写入本次运行的 `stage-times.json`，未通过主机演练前不宣称达到时限。

Nightly 门禁读取本 worktree 的 `bin-build/nightly/latest.json`（SPD-4）：`rev`、`status`/`passed`、
`underThirtyMinutes`。发布提交祖先上的最近 nightly 失败或超时会阻止发布，直到后续 nightly 完整通过；没有覆盖选项。
缺少 nightly 记录也拒绝发布。SPD-4 合并时应核对这一字段约定。

发布后的 CI 是异步检查，稍后执行：

```powershell
dotnet run --project build-tools/release -- release-publication-check -Version X.Y.Z
```

该命令只读一次当前提交的 validate-bundle、offline-checks 和 `Verify vX.Y.Z` 最新运行/attempt，不轮询等待。
结果写入 `bin-build/publication-verification/` 和 `manifest/publication-vX.Y.Z.json` 的 `postPublication`；
失败会阻止下一次发布，重新运行仍在 pending 时不会清除失败，只有全部成功才解除。将发布记录交给审查者提交，
使后续 worktree 继承失败状态。发布记录还列出证据运行号、按哈希采纳的回执与本次实际运行的检查。

<a id="采纳同一提交的-check-all-与-ci-证据-adoptevidence"></a>

## release 的一次完整构建与测试

```powershell
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.3 -DryRun -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe>
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.3 -Summary "..." -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe>
```

`release -Version` 不读取 nightly 门槛或先前 check-all 证据，拒绝 `-AdoptEvidence`。发布本身保持冷构建。
最早检查包括版本格式、最新 CHANGELOG 正式条目（忽略 `[Unreleased]`）及真实 `yyyy-MM-dd` 日期、
marketplace 版本、发布文档、伴随 Python 的依赖导入、Git 规范化字节和占用进程。
正式发布保留 master/origin、令牌和干净工作树检查；`-DryRun` 允许任意分支及工作树改动，不提交、打 tag 或上传。

本地阶段顺序：

1. 重新构建分析器报告，再运行分析器 ratchet 与源码/文档早期检查；旧报告不能阻断这次发布。
2. `build-multi-version -PrepareOnly`、`build-release -DeferTests`、`build-multi-version -CompleteOnly -DeferTests` 保留所有产物及二进制/审批安全检查，生产器不执行套件。
3. 保留 `tests/test-suites.json` 的全部 38 个注册套件门槛，逐套件检查原有 `minimumPassed` 和 `maximumSkipped`。
   先完成普通套件、配置器、PLC file app 及行为对等夹具的构建，再启动测试池；不同编译参数的变体保留各自输出，同项目普通测试串行执行。
   池排空后，`foundation-api` 独占调用已有的 `FastSuites.Run` 分片执行器，沿用 `FoundationApiSuiteArguments`、成本分片、合并和门槛检查，不使用 worker copies。
   同次合并 TRX 经批准的 SPD 派生规则生成 `foundation`、`worker-robustness`、`workbench-control-host`、`shared-pipeline`、`shared-lifecycle-plc`，保留各自门槛，并在全部六份结果中记录 `exitCode`。
   `updater`、`studio-gui` 独占运行，native-diagnostics 与 ecosystem-assembly harness 也在池排空后逐项独占执行。
   其余独立项目受核心数及 `-MaxParallelism` 限制。各进程使用独立的 `FastSuites.EnvironmentFor` 环境。
4. Python 自测、V20/V21 主机行为对等、配置器控制台测试，以及 PLC 功能、合同与响应捕获继续执行。
   V20/V21 响应保留两种布局、两种传输及逐字节比较。稳定性 campaign 和计时/延迟门槛不在此发布路径中。
5. 当次 TRX、门槛和哈希写入 `adoptedValidation`（`source=release-run`）及 `validation.dotnetSuites`；这不是外部证据采纳。
   生产器先写出 pending 的 delivery；所有功能检查成功后重新绑定记录并执行严格校验。
   记录比较、完整 preflight、严格校验和候选打包继续执行；
   preflight 与候选验证引用当次已通过的套件/自测结果，避免再次执行相同测试。
6. 保留重定位、产品 smoke、包内 PLC 响应校验、提示词注册、完整 ZIP 校验与正式发布前验证。

若工作树已有生产器暂存的产物，可单独复核测试阶段：

```powershell
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.3 -TestStageOnly -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe>
dotnet run --project build-tools/release -- release -Version 4.0.0-beta.3 -TestStageOnly -Only updater,studio-gui,host-behavior-parity,foundation-transport,foundation-transport-plc -PublicApiRoot <SDK-root> -NuGetConfig <offline-nuget.config> -CompanionPython <python.exe>
```

`-TestStageOnly` 保留本地版本、文档、依赖及 SDK 前置检查，只准备所选测试需要的夹具和测试程序集，然后复用相同调度。
省略 `-Only` 执行完整测试阶段；提供时接受注册套件名、Python 检查名、`configuration-page`、`plc-v20`、`plc-v21`、独占 `harness-*` 作业名及上述三个后置作业名。
未知或空作业名在启动测试前报错并列出可选名称。每个执行作业打印 `PASS`/`FAIL`，日志写入新的 `bin-build/release-runs/<run>/tests`。
选择任一 Foundation 派生套件会运行 `foundation-api` 及全部五个派生门槛。诊断模式和正式发布均使用仓库外的临时根目录。
测试阶段会运行完所有可执行作业，再列出全部失败并返回失败；前一个套件或作业失败不会掩盖后续作业。
此诊断模式不重跑生产器，不更新或绑定发布记录，不打包、提交、打 tag 或上传；其结果不能作为完成发布的记录。
测试子进程清除发布器的禁用构建缓存标记，独立编译输出通过 `TIA_MCP_TEST_REGISTERED_OUTPUT` 映射到项目的正常测试输出目录。

移除的 release check id 为 `plc-stability`、`plc-isolated-stability`；`http-concurrency` 是功能检查，继续保留。
`productionRelease` 标记限定此删除范围；通用 `full`、分支检查及旧证据验证保留原来的稳定性检查。
每段耗时写入 `bin-build/release-runs/<run>/stage-times.json`。完整演练的目标约一小时，实际时间由主机记录。
需要清理时，只清理当前 worktree 的 bin/obj 与 runtime；不能用 `git clean -X`，它会删除伴随 Python 与 bin-build 状态。
`release-prepare` 的独立证据封存和 `release -FromCandidate` 的快速验证流程不属于这条重构路径。

## CI 分工候选与在线采纳

本节适用于显式选择 CI 分工的旧回退路径；新的离线准备默认运行完整本地证据。

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
含分工的证据不能离线采纳；`check-release-evidence` 必须带 `-Ci`，`release-prepare -DryRun -AdoptEvidence` 会拒绝它。
需要保持不访问 GitHub 的完整发布演练时，使用不带 `-CiCovered` 的 check-all 证据。

## 预发布（beta / rc）

维护者决定（2026-10-20，2026-11-01 修订，[ADR 0020](decisions/0020-prereleases-tested-by-users.md)）：4.0 先以 GitHub 预发布交给测试者，我们在 VM 上按预发布真机验证清单回归。版本写作 `X.Y.Z-beta.N` 或 `X.Y.Z-rc.N`
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

-NoPush 完成本地提交、打包和验证后停止；-NoTag 在推送后、tag 前停止；-DryRun 在本地完成构建、检查和候选包后于暂存前停止，不执行暂存或远程操作（见[预发布](#预发布beta--rc)）。
`-Resume` 重跑预检、早期门禁和冷构建。`-NoReuse` 仍可显式表达冷构建；`-SkipBuild` 不能用于正式发布。

原来的 `sourceFiles` 保持打包器所要求的集合；新记录另存 `validationInputs`，覆盖构建/验证脚本、生态桥接、参考数据、模板等输入。缺少该字段的历史记录需要重建一次。构建前后再次比较输入，期间发生变化则拒绝记录测试结果。缓存命中只恢复编译产物；当前档位的检查、记录时间和交付绑定每次重新生成，正式发布始终冷构建。`prepare-delivery` 要求多版本运行产物和匹配的完整引擎记录已存在，缺项在重建配置器前明确失败；它刷新引擎/配置器交付绑定并执行严格验证。正式发布仍需多版本完成记录及最终严格验证。准备阶段的 `bin-build/multi-version/prepared-build.json` 不是发布记录，不能通过最终八版本发布门禁。

长路径环境可用 `Package-Release.py --output-directory <较短的新目录>`，保留生成的 package-result.json 所记录的实际 ZIP 路径；恢复 Release 流程时将该结果记录及同名 ZIP/SHA-256 放到默认版本目录。

每次新运行或 `-Resume` 都将已有 `bin-build/releases/v<版本>` 完整移动到同级 `v<版本>.previous-<时间戳>-<唯一后缀>`，保留原归档和日志，再从当前 HEAD 生成并验证候选；完整引擎记录新增 `validationArtifacts`，绑定 API 织入清单和实际 GetToolUsage 证据的哈希。归档后保留哈希匹配的四个历史审计输入，冷构建仍重新生成全部当前证据；移动前检查绝对父目录和重解析点，不覆盖历史目录。无需手动归档。

已公开的 Release、tag 和资产不改写；发现问题应修正并发布新补丁版本。发布不自动更新运行中的服务或虚拟机。

## 预上传候选的快速发布

准备阶段将 ZIP、同名 `.sha256` 和 `candidate.json` 放在 `bin-build/prepared/<版本>/`。
候选记录包含 `version`、`commit`、`zip`、`zipSha256`、`zipSize`、`fileCount`、`suites`、`checks` 和 `finishedUtc`；
`zip` 可为该目录内的文件名或仓库相对路径。准备程序调用 `ReleasePublisher.UploadDraftAsync`，
以 `target_commitish=master` 建立或复用私有草稿；发布说明来自 `docs/releases/v<版本>.md`，
必须含生成的“Not verified on a real TIA Portal”节，链接指向候选提交，并附文件数、大小和 SHA-256。
草稿只保留 ZIP 与 sidecar；已验证的资产复用，失败的上传可重试，成功后回写 `draftReleaseId`、`draftAssetId`。
令牌依次来自 `-Token`、`GITHUB_TOKEN`、Git Credential Manager。

使用准备阶段已经构建的 Debug 工具。上传失败时保留候选，用 `upload-draft` 重试，无需重新准备；维护者确认后发布：

```powershell
dotnet run --no-build --project build-tools/release -- upload-draft -Version 4.0.0-beta.3
dotnet run --no-build --project build-tools/release -- publish-prepared -Version 4.0.0-beta.3
```

两个命令只接受预发布版本。发布前核对候选、HEAD、干净工作树、ZIP/sidecar、远端资产大小和 digest，
并解析已有标签（包括 annotated tag）；标签指向其他提交时在推送前拒绝。
随后推送候选提交到 `origin/master`，按需创建标签，将草稿公开为预发布（`make_latest=false`）。
创建标签的响应丢失时重新读取标签；翻转最多尝试 3 次，间隔 5 秒；下载确认最多尝试 5 次，间隔 3 秒。
下载 URL 取自发布响应的资产列表，不能复用草稿的 `untagged-*` URL。

发布命令从进程启动起硬限时 300 秒，HTTP 请求超时 30 秒；超过 150 秒不再开始标签步骤。
标签步骤后第一次翻转必定尝试，其余请求和等待均受剩余时间限制。所有结果都会输出总秒数；
失败时说明“标签存在或状态未知、草稿可能尚未公开”或“已公开、下载未确认”，重跑同一命令即可恢复。
若已公开且标签及两个资产仍与候选匹配，则只确认当前下载地址，不再推送、创建标签或 PATCH。
发布阶段不启动构建、测试或打包；发布工作流只校验公开 ZIP 与 sidecar 的 SHA-256。

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

托管 runner 无法取得全部 Siemens SDK 重建引擎，因此由本机构建上传。推送 CI 检查源码、构建记录、Studio/WPF 及无 Siemens 依赖的基础引擎协议；发布工作流下载实际 ZIP，核对其公开 `.sha256`。包内容的严格验证在准备阶段完成。

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
