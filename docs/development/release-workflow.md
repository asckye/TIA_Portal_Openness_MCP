# 完整 Release 发布流程

从 v3.2.0 起，完整 TIA_MCP_Delivery_v<版本>_<YYYYMMDD>.zip 包含 V14 SP1、V15.1、V16–V21 MCP 运行文件、WPF 配置器、Studio 及八版原生适配器，以及运行资源、用户文档、模板、插件和许可证。自 3.3.0 之后的下一个版本起，交付 ZIP 不再携带开发源码及构建工具。基础引擎与完整引擎的范围不同，见[版本矩阵](../reference/version-tools.md)。每次发布同时上传 .sha256；GitHub 自动生成的源码压缩包不含运行二进制。

运行文件不提交 Git。三份构建记录是 manifest/release-build.json、manifest/configurator-build.json、manifest/multi-version-build.json；manifest/delivery.json 绑定它们的哈希。PublicAPI、个人密钥、私人 TIA 工程、设计交接和构建日志不分发，原始版权与许可证保留。

## 交付清单与两种校验模式

[`scripts/operations/delivery-files.json`](../../scripts/operations/delivery-files.json) 是唯一交付规则文件。
`include.files` 为精确路径，`include.prefixes` 为以 `/` 结尾的目录前缀，`exclude` 优先；未匹配的跟踪文件不交付。
Python 打包器、资产核验、仓库/布局检查与 .NET release tool 校验器、更新器均读取它。运行文件仍只来自三份构建记录，
不会把磁盘上任意新文件塞入 ZIP。`runtime/verification/` 只用于发布前的 IL 验证，不分发。

保留 BundleLayout 的九项资源、全部模板、生态与操作脚本、Claude Code 插件及其 skill、用户说明和许可证。
Python 桥接所用两个第三方项目只交付 `src`、PLC Tools 的八个 `packages/*/src` 和许可证；
`tia install-plc-tools` 准备用户自己的 Python 3.12+ venv 和外部运行依赖，不再依赖未交付的本地项目打包元数据。
字体、Studio、TiaGitAddIn、Siemens OPC UA、Eido 与嵌入官方代码示例的许可原文在 `docs/licenses/` 保留副本。

三份构建记录供解包校验读取版本、依赖清单及 SHA-256，`delivery.json` 绑定记录哈希并供软件检查版本；
`tools-list.json` 用于包内入口及工具数量校验，`version-tools.json` 是用户版本矩阵入口。
其他构建证据和 `RELEASE_STATUS.txt`、`release-file-hashes.json` 不进入新 ZIP。

- 仓库模式：`dotnet run --project build-tools/release -- validate-bundle -Strict` 保留源码、构建输入哈希、版本和本地 IL 校验；日常检查可用
  `-NoBinaries -SkipSourceHashes`。`Check-Repository.py` 运行源码门禁与 BundleLayout 表校验。
- 包模式：从仓库执行 `dotnet run --project build-tools/release -- validate-bundle -BundleRoot <解包根> -PackageMode -Strict` 与
  `Check-Repository.py --root <解包根> --package-mode`。没有 `Version.props` 时也自动识别包模式。
  检查交付资源、用户文档链接、许可证、构建记录、运行文件版本/哈希；不读取源文件或调用包内 IL 验证器。
  生成的旧 package/blueprint 元数据中的 `scripts/checks/validate-bundle` 条目仅属仓库说明，包模式明确忽略该单项。
  新增资源必须纳入规则，不能以源码缺失为由跳过其他必需项。

无发布构建时可查看当前文件投影，不执行 Release 或生成可发布资产：

```powershell
python scripts/build/Package-Release.py --dry-run --include-untracked --stage-directory bin-build/delivery-preview
python scripts/checks/Check-Repository.py --root bin-build/delivery-preview --no-binaries
dotnet run --project build-tools/release -- validate-bundle -BundleRoot bin-build/delivery-preview -PackageMode -Strict -NoBinaries
```

`--include-untracked` 仅在 dry run 中用于预览待审查的新文件，正式打包仍要求干净提交树。
预览打印完整文件集和排除的顶层分组；没有运行二进制时不能作为完整发布验证。
仓库的 `-NoBinaries` 模式允许 CHANGELOG 最新条目高于已发布版本，但必须有同版本发布说明；
各份旧 manifest 仍须相互一致。包模式和带二进制的发布模式继续要求精确一致，不能手改记录哈希。

## quick 测试包与 full 发布候选包

审查入口必须显式选择 `-Tier quick` 或 `-Tier full`。两者编译相同的全部交付二进制；层级仅控制检查数量，
不改变编译参数、运行功能或交付文件集。`quick` 用于 VM 回归测试，目标在本机不超过 15 分钟；实际耗时以日志为准。
`full` 执行全部检查，包含普通/隔离稳定性、八版快照及比较、重定位。`release` 默认 `full`，拒绝 `-Tier quick`，
并在发布前验证包记录为 `tier=full`、`checkStatus=passed`、全部检查已运行且无跳过项。quick 包不能直接升级为 full 证据。

```powershell
dotnet run --project build-tools/release -- run-release-build -Tier full -PublicApiRoot <SDK-root> -CompanionPython <python.exe> -NuGetConfig <offline.config> -OutputDirectory bin-build/full-candidate
dotnet run --project build-tools/release -- run-release-build -Tier quick -PublicApiRoot <SDK-root> -CompanionPython <python.exe> -NuGetConfig <offline.config> -OutputDirectory bin-build/quick-test
```

两层都执行 preflight、全部产品编译、单元/离线套件、原生调用覆盖与 JIT 检查、资源发现、打包、严格包验证、prompt 注册，
以及真实进程 smoke。smoke 经 STDIO 检查六个 Foundation 和 V20/V21 引擎的启动、完整工具目录、InitializeEnvironment 响应，
以及一次在派发前被就绪/审批门禁拒绝的写请求；使用没有 TIA EXE 的 SDK-only 夹具。Studio/配置器无 MCP 目录，
通过无效网络参数的启动拒绝检查；更新器运行帮助入口。smoke 不连接 TIA、PLC 或 VM。

[`release-checks.json`](../../build-tools/release/release-checks.json) 是唯一审查过的路径到检查映射。quick 对比最近成功 full 候选包的
源码内容清单（包括新增、删除和未提交文件），追加对应检查。FoundationHost/Worker 选择 Foundation transport、默认审批与 responses；
Engine 选择审批、worker isolation、V20/V21 responses 等；Studio 选择 GUI；检查器选择自身 self-test（无 self-test 的检查器运行原检查）。
未识别路径、缺少/损坏 baseline、候选 ZIP 丢失或哈希不一致均选择全部检查。首次 quick 因而会运行 full 数量的检查，仍标为 quick。

默认 baseline 在忽略的 `bin-build/release-review/last-full.json`；`-FullBaseline` 可选择本 worktree 的 bin-build 下其他文件。
仅 full 全部成功、最终包再次严格验证且源文件未在运行中改变时更新。baseline 绑定候选 ZIP 的 SHA-256、tier 与检查清单，
不以 Git 提交时间推断成功。构建记录和包内 `package-manifest.json` 保存实际计划；最终包的 `checksRan`/`checksSkipped` 记录执行/跳过的检查组。
构建中包标为 `checkStatus=pending`，全部选择的检查成功后重新打包为 `passed`。本地审查运行退出后恢复跟踪的 manifest 和生成文档。

两层均使用 `-MaxParallelism`（默认 CPU 数，设为 1 则串行）的 continue 6 有界并行器；快照 compare 等待自己的 capture，
失败停止新任务并等待已启动任务完成。各步骤和任务日志记录耗时；比较 wall time 必须注明 quick 的 selected/skipped 清单，
不能把 dry run 或失败构建的时间当作候选包耗时。正式 release 在提交和上传前也执行 full 候选包检查。

## 准备和执行

在 Windows 的 master 工作区操作，审查全部改动，停止该安装目录下的 MCP 和 Studio 进程。需要 .NET 10 SDK、Git、Framework 开发环境、Python 和八版 SDK。设置 TIA_MCP_PLC_TOOLS_PYTHON 指向已有 PLC Tools 伴随环境。SDK 目录结构见[构建说明](../reference/version-tools.md#build-test-and-package)。

先写并提交 CHANGELOG.md 最新版本条目与 docs/releases/vX.Y.Z.md，更新当前说明，确保工作树（含未跟踪文件）干净；发布后在 handoff.md 和 publication-vX.Y.Z.json 记录结果。发布正文写明各版范围、组件、测试结果和真实工程验收状态。英文文档和提交说明使用英文，不添加 AI 署名。

```powershell
dotnet run --project build-tools/release -- release -Version X.Y.Z -Summary "Concise English release summary" -PublicApiRoot <SDK-root> -V20ReferenceRoot <V20-SDK> -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>
```

命令通过 .NET 10 release tool 调度；Python 只运行尚未迁移的检查器与打包器。

脚本依次执行：

1. 首先运行 `prerequisites`：汇总检查 .NET 10 SDK、Python 3.12、实际伴随环境的模块/命令目录、八版 PublicAPI、固定 SHA-512 的 .NET 缓存包、令牌、干净工作树和至少 10 GiB 空间。没有缓存时先下载并验哈希；任何缺项均在修改版本或构建前失败。随后检查 master、上游和进程。
2. 更新 `Version.props`、插件版本、文档当前发布链接及路线图标题，立即执行早期门禁：版本/CHANGELOG/发布说明/README/路线图断言、仓库及链接、失效工具引用、仓库模式包验证、布局与交付集合自测、示例目录、版本目录接线、原生监督器与 MCP 安全自测、崩溃证据和写保护测试。路由选择与 PLC 名称匹配从当前生产源码提取方法并编译小型夹具运行，不需要完整引擎产物。
3. 分别判断两份记录能否复用。先复用多版本准备产物，或运行 `dotnet run --project build-tools/release -- build-multi-version -PrepareOnly -Test`，生成 Foundation、worker、Studio 和 bundled .NET；版本从 `Version.props` 读取。完整引擎不能复用时，`Build-release` 运行公共离线套件及夹具构建，再启动 V20/V21 两条并行流水线，保留全部功能、协议、普通/隔离稳定性门禁；每版普通和隔离稳定性仍各四组。汇合后生成清单、构建记录与配置器。随后用 `dotnet run --project build-tools/release -- build-multi-version -CompleteOnly -Test` 核对准备阶段的输入、运行文件和审计证据，执行依赖完整引擎的八版本 API/示例审计，生成最终记录并绑定交付。完整引擎重建时，多版本记录也重新验证；两份记录都可复用时保持原记录不变。
4. 执行依赖实际二进制的仓库检查和严格包验证；构建后的路由/PLC 名称测试仍反射实际 V21 程序。检查通过后，正式运行才暂存明确路径并创建 `Release X.Y.Z: <summary>` 提交；`-DryRun` 在暂存前退出。

5. Package-Release.py 在仓库核对完整提交树、全部构建记录和源码哈希，运行发布期 IL 校验，再按交付清单过滤，在实际暂存目录运行包模式严格检查，生成 ZIP、SHA-256 和 package-result.json。
6. Verify-ReleaseAsset.py 独立验证 ZIP 等于交付清单过滤后的 tag 文件集加记录哈希的运行文件（排除 `runtime/verification/`）。
7. 推送 master，等待 validate-bundle 与 offline-checks，通过后创建 annotated tag vX.Y.Z 并推送。

Build-Release 的程序集反射检查和工具清单生成运行在 net48 HttpTests harness modes 中；独立生成器和输入检查使用 .NET 10 C# file-based apps。工具矩阵在重新生成后可用 `Generate-ToolCapabilityMatrix.cs --check` 验证稳定字节。它们沿用 `manifest/release-build.json` 的既有字段和最小/精确检查计数。
8. Publish-release 创建草稿、上传 ZIP 和 SHA-256、回读大小及 digest，然后公开发布并置为 latest。
9. 等待 Verify published release 下载并校验公开资产，再记录发布 URL、提交和验收状态。

任一步失败即停止。日志保留在 release.log、build.log、bin-build/releases/v<版本>/ 和 bin-build/multi-version/；完整引擎分版日志在版本目录下的 `v20/`、`v21/`，汇总为 `release-v20.log`、`release-v21.log`。一版失败仍收集并报告两版结果。令牌来自 -Token、GITHUB_TOKEN 或 Git Credential Manager，不打印、不放入发布包。

## 分阶段与恢复

-NoPush 完成本地提交、打包和验证后停止；检查具体产物后用相同参数加 -Resume 继续推送、CI、tag 和发布。-NoTag 在推送及 CI 后停止；-DryRun 只到构建与本地检查，且不写 Git 暂存区。`-Resume` 仍重跑预检和早期门禁；只有输入及产物与记录相同时才跳过构建和提交，否则回到构建/提交路径。

默认自动复用：release/fileVersion、完整源码文件集合（含新增/删除）、源码哈希、补充验证输入和运行文件集合/哈希均一致，且原有验证完整时，才跳过对应引擎或多版本构建及测试。日志写明复用或重建原因。`-NoReuse` 强制两部分都重建；`-SkipBuild` 保留为严格断言，任何不匹配立即失败，不能与 `-NoReuse` 同用。

原来的 `sourceFiles` 保持打包器所要求的集合；新记录另存 `validationInputs`，覆盖构建/验证脚本、生态桥接、参考数据、模板等输入。缺少该字段的历史记录需要重建一次。构建前后再次比较输入，期间发生变化则拒绝记录测试结果。复用不改写两份构建记录的日期、版本、哈希或测试结果；配置器/交付元数据单独刷新，随后重新绑定原多版本记录。`prepare-delivery` 要求多版本运行产物和匹配的完整引擎记录已存在，缺项在重建配置器前明确失败；它刷新引擎/配置器交付绑定并执行严格验证。正式发布仍需多版本完成记录及最终严格验证。准备阶段的 `bin-build/multi-version/prepared-build.json` 不是发布记录，不能通过最终八版本发布门禁。

长路径环境可用 `Package-Release.py --output-directory <较短的新目录>`，保留生成的 package-result.json 所记录的实际 ZIP 路径；恢复 Release 流程时将该结果记录及同名 ZIP/SHA-256 放到默认版本目录。

每次新运行或 `-Resume` 都将已有 `bin-build/releases/v<版本>` 完整移动到同级 `v<版本>.previous-<时间戳>-<唯一后缀>`，保留原归档和日志，再从当前 HEAD 生成并验证候选；完整引擎记录新增 `validationArtifacts`，绑定 API 织入清单和实际 GetToolUsage 证据的哈希。归档后只恢复哈希匹配的这四个审计输入，缺失或改写则重建完整引擎；移动前检查绝对父目录和重解析点，不覆盖历史目录。无需手动归档。

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
| HTTP 端口 | HttpTests 与 Python 协议夹具均从系统申请端口 0；没有两版共用的固定监听端口 |
| Logic、Runtime、contracts、第三方项目的 obj/bin | 工作树路径派生的 mutex 保护还原、编译和运行文件复制；该短段串行，随后两版长耗时门禁并行 |
| MSBuild/C# 服务 | 关闭节点复用和共享编译器；公共测试夹具、weaver 先构建一次，子流水线只读 |
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
dotnet run --project build-tools/release -- run-release-build -DryRun
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
8. 普通 V4 response capture（V20/V21），传 `--harness <HttpTests.exe>`；SDK-only 捕获通过测试宿主设置仅测试用的 readiness 标记。
9. 与 `manifest/contracts/v4/responses` 普通 compare；只在两版捕获完成后运行，差异直接失败，不刷新基线。
10. `Test-RelocatedBundle.py --bundle-root <extracted-package> --public-api-root <SDK-root>` 检查候选包在仓库外复制和读取时可用。
    当前用户必须位于 Siemens TIA Openness 组之外，检查会拒绝组内用户。该步骤只依赖已验证包，可与步骤 5–9 并行。

第 08 步通过 `HttpTests.exe --harness` 加载真实 V20/V21 引擎宿主方法。`sdk-only-fixture` 仍复制 SDK 到临时安装布局；
HttpTests 进程用仅测试用的 readiness 标记模拟 Openness 组已就绪，使 capture 能读取断开状态，并让产品默认审批在派发前拒绝写操作。
该标记不由引擎读取，也不改变真实安装要求 Openness 组。步骤仍用 `Run-Command -ProductDefaults`，不创建审批设置文件。
`Snapshot-ToolResponses.py --packaged-no-tia` 继续启动真实 EXE，并检查真实 no-TIA readiness 拒绝。

`-Dotnet`、`-Python` 和 `-CompanionPython` 指定本地解释器。默认生成清空 package feeds 的 NuGet 配置；
显式配置也必须清除继承的 feeds，拒绝 URL/UNC 网络源，并关闭 NuGetAudit。运行前检查全部 pinned runtime archives 已缓存且 SHA-512 匹配，
缺失时停止；多版本构建还显式传 `-Offline`，不下载。每步成功或失败后都恢复原记录并验证哈希。
`Package-MultiVersion.py` 的开发包也接受 bundled .NET 清单中的 `.version` 文件，框架目录以外仍保留扩展名限制；字体 `.ttf`/`.otf` 继续按原始字节哈希。
完整链仍需要非受限的本地 HTTP 与原子文件操作能力；此命令不进入 TIA/PLC/VM 或 live 分支。

何时运行完整链：合并任何改动宿主行为（引擎、Foundation、worker、审批与数据目录）、打包与交付清单、随包文档或发布脚本的变更后，
从干净 worktree 运行一次，不要攒到发布前。日常审查验证不包含发布构建内的宿主检查，4.0 发布候选曾因此连续暴露多个只在发布时才检查的问题。
