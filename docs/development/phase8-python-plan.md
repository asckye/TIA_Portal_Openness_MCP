# P8-10 自有 Python 改为 C#：清单与分批方案（研究稿，2026-10-08）

统计口径：`git ls-files "*.py"`，提交 `5623a784`（master）。工作树当时有未提交修改
（`ToolProfiles.resx`、`gap-review-2026-10.md`、`refactor-plan.md`、`roadmap.md`），逐字节双跑必须改用干净提交。
`bin-build/` 未跟踪，不计。P8-10 依赖 P7-11：P7-11 的 B1/B2（测试目录重组）、B5（退役 `phase6_groups.py`，
预检不再执行 `Generate-Phase6Plan --check`）和 B13（`scripts/build/Package-*.py` 移到 `build-tools/`）会改变本文部分路径。
开工前按合并后的树重新运行本清单的统计命令。

## 1. 总览

| 范围 | 文件 | 行 |
|---|---:|---:|
| 全部被跟踪的 `*.py` | 507 | 120,503 |
| 自有 Python | 100 | 23,817 |
| 其中需要转换 | 98 | 23,637 |
| 保留：两个随包桥接脚本 | 2 | 180 |
| 保留：第三方 Python（`third_party/siemens-plc-tools`、`third_party/simaticml-decoder`） | 407 | 96,686 |

自有文件按主类别分组。每个文件只计入一组；调用方有重叠时归入最靠前的类别（保留 > 打包 > 生成器 > vci-watch > 虚拟机/诊断 > 发布链 > CI > 共享模块 > 手动）：

| 组 | 文件 | 行 | 说明 |
|---|---:|---:|---|
| 0 共享模块（被其他脚本导入的库） | 6 | 635 | `mcp_results`、`engine_sources`、`offline_fixtures`、`tool_usage_checks`、`snapshot_text_migration`、`phase6_groups` |
| 1 CI 检查（只由 workflow 调用） | 8 | 671 | 源码契约类 `Test-*Sources`、`Test-EngineSources`、`Check-TiaFeatures`、`Test-ScriptClients` |
| 2 发布链检查（由 build-tools/release 调用，多数也在 CI 中运行） | 30 | 14,966 | 棘轮、仓库与布局检查，快照，TRX 门禁，传输/审批/稳定性/重定位等运行检查，资产校验 |
| 3 生成器（写出受跟踪文件） | 8 | 2,595 | `Generate-*`（3 个）、`Audit-VersionTools`、`Audit-ToolUsage`、`make_ledger`、`vm_ledger`、PlcRender `generate.py` |
| 4 虚拟机验收与诊断（scripts/diagnostics，生成器除外） | 26 | 1,675 | `Probe-McpServer`、`Sweep-WrongPathHonesty`、campaign 的 `camp`/`rawmsg`/`export_full` 和 21 个 `plans/plan_*.py` |
| 5 打包 | 2 | 430 | `Package-Release`、`Package-MultiVersion` |
| 6 vci-watch | 1 | 604 | `scripts/operations/vci-watch/watch.py`（不随包） |
| 7 随包产品脚本（保留） | 2 | 180 | `scripts/ecosystem/plc_tools_bridge.py`、`simaticml_decode_bridge.py` |
| 8 第三方 Python（保留） | 407 | 96,686 | 随包 src：244 个/68,221 行；不随包的上游 tests：163 个/28,465 行 |
| 9a 手动/按需检查 | 14 | 1,655 | validation.md 中的“手动补充检查”和无调用方的证明脚本 |
| 9b 测试目录中的脚本（手动） | 3 | 406 | `measure_concurrency.py`、`EngineSliceOracle.py`、`Test-BridgeSmoke.py` |

调用方有重叠，分别统计：workflow 调用 30 个/11,033 行，build-tools/release 调用 36 个/17,448 行，两者至少其一 45 个/18,187 行。

随包情况：交付规则见 `scripts/operations/delivery-files.json`。包内 Python 只有 `scripts/ecosystem/` 下的两个桥接脚本，以及 `third_party/siemens-plc-tools/{src,packages/*/src}`、
`third_party/simaticml-decoder/src`。`scripts/operations/vci-watch/` 已被排除。其余自有 Python 都是开发、CI 或发布工具，不进入包内。

`.py` 文件之外，还有以下 Python 调用需要一并处理：

| 位置 | 内容 | 处理批次 |
|---|---|---|
| `.github/workflows/release.yml` 第 57、80 行 | 两处内联 `python -c`：读取 `delivery.json`、解压 ZIP | 批 5 |
| `tests/Release/TiaMcp.ReleaseTool.Tests/SourceRootsTests.cs` 第 63、93、117 行 | 三处内联 Python 加载 `Check-BundleLayout.py`，与 C# 的 `ReleaseRecords.GetSources`/`SourceRoots.Load` 做一致性比对 | 批 2 |
| `scripts/checks/Test-DownloadRouteSelection.cs` 第 14、53 行 | `-Python` 选项，用 `python -c` 和 `engine_sources` 提取生产成员 | 批 1 |
| `scripts/checks/Test-SharedNativeMigration.cs` 第 29–160 行 | 调用 `Compare-SharedNativePaths.py`、`Test-SharedNativeIlReader.py` | 批 6 |
| build-tools/release | `Py`（`PYTHON` 环境变量）、各命令的 `-Python` 选项，前置检查 “Python >= 3.12”（`ReleasePrerequisites.RequirePython`、`ReleasePrerequisiteTests`） | 批 8 |
| 产品与伴随环境（保留） | `InstallPlcToolsCommand.cs`、`DataLocations.EcosystemPython`、`EcosystemTools`/`QualityAuditTools`/`V21EcosystemTools`、TiaMcp.Engine.Harness 伴随夹具、`TIA_MCP_PLC_TOOLS_PYTHON`、`RequireEcosystemPython` | 不改 |

## 2. 清单表

调用方缩写如下：

- **CI**
  - `CI-ot`：offline-checks/offline-tests（Ubuntu）
  - `CI-sc`：offline-checks/source-contracts（Ubuntu，目前只装 Python）
  - `CI-ft`：offline-checks/foundation-transport（Windows）
  - `CI-va`：validate.yml（Windows）
  - `CI-rl`：release.yml（Windows，发布后校验；检查脚本取自 master）
- **build-tools/release**
  - `RT-pf`：`ReleaseCommands.Preflight`
  - `RT-eg`：`ReleaseProduction.RunReleaseEarlyGates`
  - `RT-br`：`BuildReleasePipeline`（build-release）
  - `RT-mv`：build-multi-version（`ReleaseCommands` 约 1150–1500 行）
  - `RT-bg`：`BranchGate`
  - `RT-rc`：`ReleaseCandidateChecks`（run-release-build）
  - `RT-st`：`release-checks.json` 的 `selfTests`
  - `RT-pk`：`ReleaseProduction` 的打包与资产校验
  - `RT-hash`：发布记录写入该脚本的 SHA-256，`Package-Release` 再次核对
- **其他**
  - `PY`：被其他 Python 导入，或作为文本读取
  - `CS`：C# 源码或测试引用
  - `DOC`：文档中的命令

除另行说明外，外部依赖都只有标准库。“逐字节”列出受跟踪的输出：它们必须与 Python 版本完全一致，见第 4 节。
未跟踪的结果文件（`bin-build/`）只要求字段结构和 stdout 标记兼容，因为发布工具会解析这些结果。

C# 目标的缩写：

- `RT 子命令`：build-tools/release 的子命令
- `FBA`：.NET 10 file-based app
- `xUnit`：测试项目
- `共用库`：建议新建 `build-tools/common`（见第 3 节）

### 2.0 共享模块（scripts/、scripts/checks/）

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 性质 | 目标·批次 |
|---|---:|---|---|---|---|---|---|
| `scripts/mcp_results.py` | 68 | 严格解码 V4 结果（envelope/successful），第一方 MCP 脚本客户端共用；自带自检 | CI-sc 自检；PY：Check-LiteProfile、Test-NativeMcpSession、Test-ReleaseApprovalGate、Test-ResourceDiscovery、tool_usage_checks、Probe-McpServer、Sweep-WrongPathHonesty、camp、watch；DOC validation.md | 无 | — | 开发 | 共用库 `McpResults` + xUnit；批 1（Python 副本保留到批 7） |
| `checks/engine_sources.py` | 139 | 按名称定位引擎 C# 成员和类型（复用吞异常检查的词法器）；找不到或有歧义时直接失败 | PY：Check-ToolsList、Snapshot-ToolContracts、Test-DomainTools、Test-EngineSources、6 个源码契约、3 个提取编译检查、Generate-ToolUsage、Generate-Phase6Plan；CS：Test-DownloadRouteSelection.cs | 无 | — | 开发 | 共用库 `EngineSources`；批 1（副本保留到批 4） |
| `checks/offline_fixtures.py` | 18 | 建立离线夹具目录，子进程继承工作树权限 | PY：Test-NativeLifecycle、Test-NativeMcpSession、Test-ResourceDiscovery、Snapshot-ToolResponses | 无 | — | 开发 | 共用库；批 1（副本保留到批 6） |
| `checks/tool_usage_checks.py` | 235 | 检索 GetToolUsage，并在内存中执行白名单示例（不调用原生 API） | PY：Test-ResourceDiscovery、Test-FoundationTransport、Snapshot-ToolResponses、Test-ToolUsage、Snapshot-SharedToolUsage | 无 | — | 开发 | 共用库 MCP 测试客户端；批 4 |
| `checks/snapshot_text_migration.py` | 51 | P6-26 规则：只比较文本，结构、类型与非文本值保持冻结 | PY：Snapshot-ToolContracts、Snapshot-ToolResponses | 无 | — | 开发 | 并入快照实现；批 4 |
| `checks/phase6_groups.py` | 124 | 用 runpy 执行 `Generate-Phase6Plan.py`，导出阶段 6 分组供快照证明使用 | PY：Snapshot-ToolContracts、Snapshot-ToolResponses | 无 | runpy | 开发 | P7-11 B5 计划退役；如果仍在，随批 4 处理 |

### 2.1 CI 检查（scripts/checks/）

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `Test-EngineSources.py` | 93 | engine_sources 自检：搬文件、重载、词法边界、缺失与歧义 | CI-sc；DOC validation.md | 临时文件 | — | xUnit（源码契约项目）；批 1 |
| `Test-DiagnosticMembershipSources.py` | 55 | 诊断只读组、显式修复与默认不连接的源码契约 | CI-sc | 无 | — | xUnit；批 2 |
| `Test-DocumentImportSafetySources.py` | 100 | 文档导入前置拒绝、不重试、部分结果的源码契约 | CI-sc | 无 | — | xUnit；批 2 |
| `Test-ImportSelectionSources.py` | 58 | 导入选择、冲突、覆盖与确定性排序 | CI-sc | 无 | — | xUnit；批 2 |
| `Test-PromptRegistrationSources.py` | 91 | 两种传输的显式 prompt 清单 | CI-sc | 无 | — | xUnit；批 2 |
| `Test-SupplementaryReadSources.py` | 50 | 补充读取与 worker 派发的源码契约（导入 Check-TiaFeatures） | CI-va | 无 | 经 Check-TiaFeatures 调用 dotnet msbuild | xUnit；批 2 |
| `Check-TiaFeatures.py` | 115 | 用 MSBuild 求值八版编译常量，与评审表比对 | CI-va；PY：Test-SupplementaryReadSources；DOC | `--capture` → `scripts/checks/tia-feature-expectations.json`（**逐字节**）；`--output` 为证据文件 | `dotnet msbuild -getProperty` | RT 子命令 `check-tia-features`；批 2 |
| `Test-ScriptClients.py` | 109 | 对 camp/export_full/rawmsg/Sweep/vci-watch 做合成测试，不启动进程、不访问网络 | CI-sc | 无 | unittest.mock | 改为 xUnit，随 C# 客户端；批 7 |

### 2.2 发布链检查（scripts/checks/，30 个）

| 文件 | 行 | 用途 | 调用方 | 写出（逐字节） | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `Check-AdapterBoundary.py` | 112 | 拒绝每版原生适配器引用主机策略和 JSON 依赖 | CI-sc 自检与运行；RT-st；DOC src/Adapters/README | 仅自检临时文件 | — | RT `check-adapter-boundary` + xUnit；批 2 |
| `Check-BundleLayout.py` | 435 | BCL 资源表与 Git、C# 包校验器、Launcher/GUI 路径保持一致；同时作为库提供 `release_checks`/`delivered`/`compiler_sources`/`source_roots` | CI-sc 自检；RT-eg 自检、RT-st；PY：Check-Repository、Package-Release、Package-MultiVersion、Verify-ReleaseAsset；CS：SourceRootsTests.cs；source-roots.json 中 repository/bundle/package 消费者 | 无 | tomllib（自检解析 pyproject.toml）、git | RT `check-bundle-layout`，复用 `SourceRoots.cs`/`BundleManifestRequirements.cs`/`ReleaseRecords.GetSources`；批 2（Python 库保留到批 5） |
| `Check-CommentHygiene.py` | 365 | 历史注释与 Leftovers 的棘轮；提供 `fingerprint()` | CI-sc 自检；RT-st；PY：Check-McpText，Check-Repository 以子进程调用 | `--update-baseline` → `comment-hygiene-baseline.json` | — | RT `check-ratchet comments`；批 2 |
| `Check-DeadToolReferences.py` | 647 | 工具描述与引导文案中点名的工具必须已注册 | CI-sc；RT-pf、RT-eg、RT-st；DOC CONTRIBUTING、validation | `--fix` 改写引导字面量（同一输入必须得到同一改写） | git | RT `check-dead-tool-references`；批 2 |
| `Check-EnvelopeRewrite.py` | 348 | 相对 Git 基线，只接受闭合、保留表达式的响应信封改写（E4） | CI-sc 自检；RT-st | 无 | git | RT `check-envelope-rewrite`；批 2 |
| `Check-McpText.py` | 398 | MCP 运行时源码中文字面量的棘轮（含间接消息） | CI-sc 自检；RT-st；PY：Check-DeadToolReferences、Check-Repository | `--update-baseline`/`--review-data`/`--rename-product-references` → `mcp-text-baseline.json`（219 KB；指纹为 Python `json.dumps` 结果的 sha256） | — | RT `check-ratchet mcp-text`；批 2 |
| `Check-Repository.py` | 392 | 检查文档链接、入口、交付、CHANGELOG 版本和禁用扩展名；以子进程串行调用 5 个检查 | CI-sc、CI-va、CI-rl（`--package-mode`）；RT-pf 含自检、RT-eg、RT-st、`ReleaseCommands`（`--root`）；PY：Package-Release；DOC 多处 | 仅自检临时文件 | git、`sys.executable` | RT `check-repository`，子检查在进程内调用；批 2 |
| `Check-ScriptToolCalls.py` | 162 | 第一方 Python、PowerShell、计划 JSON 中的工具调用必须已注册（用 `ast` 解析 Python） | CI-sc；RT-st；DOC validation.md | 无 | ast | RT `check-script-tool-calls`，扫描对象改为 `.cs`（词法器）和 `.json`；批 2 |
| `Check-SwallowedExceptions.py` | 683 | 吞异常棘轮；内含 C# 词法器 `Lexer`/`matching_pairs`，被 5 个模块复用 | CI-ot 自检；RT-st；PY：engine_sources、Check-CommentHygiene、Inventory-ResponseEnvelopes、Check-EnvelopeRewrite、Check-Repository | `--update-baseline` → `swallowed-exceptions-baseline.json`（指纹为词元 JSON 的 sha256） | — | 词法器 → 共用库（批 1）；检查 → RT `check-ratchet swallowed`（批 2）；Python 副本保留到批 4 |
| `Check-ToolsList.py` | 234 | 写保护名单与操作类别稳定性，不构建 | CI-sc 自检与运行；RT-st | 无 | runpy（Generate-ToolUsage）、Snapshot-ToolContracts | RT `check-tools-list`；批 4 |
| `Compare-NativeCallOrder.py` | 350 | 比较引擎、助手和 CLI 迁移前后的 NativeCallWeaver 清单 | RT-st 自检；PY：Snapshot-SharedNative；DOC engine-decomposition | 无 | — | FBA；批 6 |
| `Compare-SharedNativePaths.py` | 2,368 | 比较配置域的织入路径，不执行原生代码；临时编译 `SharedNativeIlReader.cs` | RT-st 自检；PY：Test-SharedNativeIlReader；CS：Test-SharedNativeMigration.cs | `--evidence-from/--output` → `docs/development/evidence/p4-i*.json`（含 `expandedGraphSha256`，为紧凑、ensure_ascii 的 `json.dumps` 结果的 sha256） | dotnet build（Mono.Cecil） | FBA，直接编入 `SharedNativeIlReader.cs`；批 6 |
| `Inventory-ResponseEnvelopes.py` | 454 | 清点响应构造，并对手写赋值做棘轮 | CI-sc 自检；RT-st；PY：Check-Repository | `--update-baseline` → `response-envelope-baseline.json`（`ensure_ascii` 默认值） | — | RT `check-ratchet envelopes`；批 2 |
| `Snapshot-ToolContracts.py` | 900 | 捕获离线工具契约，拒绝不兼容的输入结构变化（capture/compare/verify/self-test） | CI-sc verify；RT-pf、RT-bg verify、RT-rc（stdio/http capture+compare、引擎 A/B）、RT-st；PY：Check-ToolsList、Snapshot-ToolResponses | capture → `manifest/contracts/v4/baseline/*.json`（`sort_keys`、indent=2） | TiaMcp.Engine.Harness、引擎/主机 EXE、HTTP | RT `snapshot-contracts`；批 4 |
| `Snapshot-ToolResponses.py` | 1,714 | 捕获并比较离线响应与派发前拒绝；连续两次捕获须一致，掩码规则经评审 | CI-sc verify；RT-pf、RT-bg、RT-rc（含 foundation-responses）、RT-st；PY：Test-DomainTools、Test-ToolUsage、Snapshot-SharedToolUsage、EngineSliceOracle、Check-ScriptToolCalls（负例路径）；CS：ReleaseTierTests.cs | capture → `manifest/contracts/v4/responses/*.json`（`responseDigest.sha256` = Python canonical JSON 的 sha256） | TiaMcp.Engine.Harness、dotnet、HTTP | RT `snapshot-responses`；批 4 |
| `Test-DomainTools.py` | 1,509 | 比较 full/lite、直连/隔离 STDIO 下的断开领域响应；`--source-only` 只核对注册与夹具 | CI-sc `--source-only`；RT-st；DOC engine-decomposition | 无 | TiaMcp.Engine.Harness | 源码部分 → xUnit，运行部分 → RT/FBA；批 4 |
| `Test-DotnetSuites.py` | 322 | 运行 xUnit 套件，按 TRX、最少通过数和跳过上限门禁 | CI-ot、CI-ft、CI-va；RT-pf 自检、RT-br（6 个套件）、RT-mv（Foundation 套件）、RT-rc、RT-st；PY：Test-HostBehaviorParity；CS：`tests/Engine/*/Program.cs` 提示文字、Offline.slnx 注释 | `<结果>/<suite>/<suite>.json` 摘要（不跟踪，发布工具读取） | dotnet test | RT `test-suites`，发布工具内直接调用；批 1 |
| `Test-FoundationTransport.py` | 326 | 真实 STDIO/HTTP 主机对接合成 worker 的传输检查 | CI-ft；RT-br（worker-protocol）、RT-bg、RT-mv；RT-hash（`protocolScriptSha256`）；PY：Test-VersionCatalogWiring 读取其文本 | 结果 JSON | ctypes（kernel32 进程快照）、HTTP | RT 子命令；批 6 |
| `Test-HostBehaviorParity.py` | 59 | 同一夹具分别经引擎、Foundation、EngineHost 三条派发路径运行，并执行 TRX 门禁 | RT-mv | `host-behavior-parity.json`（不跟踪） | dotnet | RT `host-parity`；批 1 |
| `Test-LocalStability.py` | 368 | 不连 TIA 的主机方法浸泡测试：句柄/内存增长、日志配对 | RT-br、RT-bg；RT-hash（`scriptSha256`，Package-Release 核对）；PY：Check-ScriptToolCalls 负例、Test-VersionCatalogWiring 文本 | 结果 JSON | ctypes（kernel32/psapi） | RT 子命令；批 6 |
| `Test-NativeDiagnostics.py` | 55 | 运行构建时织入的替身 API 诊断夹具 | RT-br、RT-bg；RT-hash（`scriptSha256`） | `result.json` | dotnet | RT；批 6 |
| `Test-NativeLifecycle.py` | 196 | net48 原生烟雾测试的监督器，默认只运行自检 | CI-va 自检；RT-eg、RT-br 自检、RT-st；RT-hash（`supervisorSha256`）；CS：NativeTests/Program.cs；DOC native-lifecycle-tests.md | 输出目录 | NativeTests.exe | RT；批 6 |
| `Test-NativeMcpSession.py` | 172 | 生产 MCP 会话生命周期测试，默认只给出计划 | CI-va 自检；RT-eg、RT-br、RT-st；DOC native-mcp-session-tests.md | 输出目录 | 引擎 EXE | RT；批 6 |
| `Test-ReleaseApprovalGate.py` | 411 | 证明默认开启的审批在派发前拒绝写入（engine/foundation） | RT-pf 自检、RT-br、RT-bg、RT-mv、RT-st；RT-hash；PY：Test-ReleaseSmoke | `result.json` | TiaMcp.Engine.Harness、主机 EXE | RT；批 6 |
| `Test-ReleaseSmoke.py` | 91 | 八个随包 MCP 主机的 STDIO 冒烟测试 | RT-rc、RT-st；source-roots.json；`ReleaseCommands.ValidateBuildRecords` 将其列为必需文件 | 无 | 主机 EXE | RT；批 6 |
| `Test-RelocatedBundle.py` | 1,068 | 解压包移到仓库外后，检查只读、不连 TIA 的启动路径 | RT-pf 自检、RT-rc、RT-st；DOC release-workflow、validation | 临时副本 | winreg、dotnet、EXE | RT 子命令（在仓库外执行）；批 6 |
| `Test-ResourceDiscovery.py` | 313 | 真实 EXE 的 STDIO/HTTP 资源发现；同时是 10 个脚本共用的 MCP 测试客户端 | RT-br、RT-st；RT-hash（`resourceHelperSha256`）；PY：10 个脚本 | `--usage-output`（不跟踪） | HTTP、TiaMcp.Engine.Harness | 共用 MCP 客户端 + RT `check-resources`；批 4（副本保留到批 6） |
| `Test-V21Ecosystem.py` | 106 | 经离线主机检查生态适配器（导入 simaticml 桥接） | RT-br（伴随 Python）、RT-st | 结果 JSON | 伴随 Python 环境 | RT；批 6 |
| `Test-VersionCatalogWiring.py` | 143 | 版本门禁、派发、配置与构建接线的源码契约；读取 Package-Release、Test-FoundationTransport、Test-LocalStability 的源码文本 | CI-sc；RT-br、RT-eg | 无 | — | xUnit；批 2（批 5、批 6 时改断言目标） |
| `Verify-ReleaseAsset.py` | 265 | 将交付 ZIP 与对应 Git 提交逐文件比对，并核对清单哈希 | CI-rl；RT-pk、RT-st；DOC | 自检时写临时 ZIP | git、zipfile | RT `verify-release-asset`；批 5 |

### 2.3 生成器（8 个）

| 文件 | 行 | 用途 | 调用方 | 写出（逐字节） | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `scripts/generate/Generate-ToolUsage.py` | 517 | 生成嵌入的官方来源与示例目录，以及工具配置资源 | CI-sc `--check`；RT-pf、RT-eg、RT-br `--check`；PY：Check-ToolsList、Generate-Phase6Plan（runpy） | `src/Shared/ToolUsageData.json`（3.4 MB）、`src/Logic/ModelContextProtocol/ToolProfiles.resx`（1.1 MB，经 Phase6Plan 的 `resource_text()`），均为**产品嵌入资源** | ast（`literal_eval` 读取 Phase6Plan 源码中的 `NEW_V4_TOOLS`）、runpy | RT `generate-tool-usage [--check]`；批 3 |
| `scripts/generate/Generate-Phase6Plan.py` | 1,135 | 生成 V4 提案与运行目录；模块顶层执行全部计算（含 git ls-files，包括未跟踪文件） | RT-pf（`--shared-host --check`）；PY：Generate-ToolUsage、phase6_groups | `--shared-host`：ToolProfiles.resx、`tests/Engine/TiaMcp.Engine.Tests/FullEngineRejections.json`、`manifest/version-tools.json`、`docs/reference/version-tool-catalog.md`、`docs/development/phase6-review.md` 标记块；不带 `--shared-host` 时另写 `manifest/package-manifest.json` 计数、`reference/version-feature-matrix.json`、`docs/reference/real-machine-ledger.md` 能力块 | git、runpy | 拆分：仍在使用的产品输出 → RT `generate-catalogs`；phase6-review.md 冻结归档（待决定）；批 3 |
| `scripts/generate/Generate-ReleaseToolMigration.py` | 347 | 生成 3.x→4.0 工具与输入迁移表 | RT-pf（文件存在时才执行 `--check`） | `docs/releases/v4.0.0-tool-migration.md` | — | RT `generate-release-migration`，或冻结为历史文档（待决定）；批 3 |
| `scripts/diagnostics/Audit-VersionTools.py` | 114 | 按版本目录对工具分组，对照官方 XML 成员与已编译调用点 | RT-mv（native-coverage）；DOC openness-coverage、gap-review | `manifest/version-tools.json`、`docs/reference/version-tool-catalog.md`（**与 Phase6Plan 写同一文件，表头不同**）、`manifest/version-api-audit.json`（**当前提交为 CRLF**）；`bin-build` 中的候选清单 | 本地 PublicAPI XML、`bin-build` 覆盖清单 | RT `audit-version-tools`；批 3 |
| `scripts/diagnostics/Audit-ToolUsage.py` | 51 | 汇总八版用法检索覆盖 | RT-mv（foundation-transport + Test） | `manifest/tool-usage-coverage.json`（文本模式写出，在 Windows 上为 CRLF；当前提交为 LF，Test-ToolUsage 也写这个文件） | 依赖 `bin-build` 结果 | RT `audit-tool-usage`；批 3 |
| `tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender/generate.py` | 86 | 经临时 .NET runner 调用 `PlcProgramRenderer`，重建渲染 golden | DOC 同目录 README | `Fixtures/PlcRender/*.html`（`-text`）、`bin-build/P6-62` 样例 | dotnet run（net10 runner 引用 TiaMcp.Logic） | FBA，用 `#:project` 引用 `src/Logic/TiaMcp.Logic.csproj`（net48;net10.0）；批 3 |
| `scripts/diagnostics/campaign/make_ledger.py` | 301 | 由战役台账与人工覆盖生成真机台账 | DOC scripts/README（camp 说明） | `docs/reference/real-machine-ledger.md`（与 Phase6Plan 能力块写同一文件） | gzip（`ledger-runs.jsonl.gz`） | 作为 campaign FBA 的子命令；批 7 |
| `scripts/diagnostics/campaign/vm_ledger.py` | 44 | 从原开发机的 Claude 会话记录统计 VM 上调用过的工具 | 无；make_ledger 读取其输出 | `scripts/diagnostics/campaign/vm_ledger.json`（输入只存在于原开发机） | 本机 `~/.claude` 会话记录 | 建议退役，`vm_ledger.json` 作为冻结数据；批 7 |

### 2.4 虚拟机验收与诊断（scripts/diagnostics/，26 个）

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `Probe-McpServer.py` | 167 | 直连 VM 上的 HTTP MCP 服务（绕过代理）：tools/call/bridge | PY：camp；DOC scripts/README、CHANGELOG | 无 | urllib；读取 `~/.claude.json` 中 tia-portal-vm 的令牌 | FBA；批 7 |
| `Sweep-WrongPathHonesty.py` | 163 | 给只读工具传入不存在的路径，找出误报成功的工具；手动运行，需要真实工程和 TIA | PY：Test-ScriptClients；DOC | 无 | 引擎 EXE（stdio） | FBA；批 7 |
| `campaign/camp.py` | 125 | 真机批跑器（call/run/raw），写入 `ledger/*.jsonl` | PY：export_full、rawmsg、Test-ScriptClients；DOC | `ledger/` 下文件（不跟踪） | Probe-McpServer | campaign FBA 主程序；批 7 |
| `campaign/rawmsg.py` | 26 | 显示 V4 工具 data/error 的原文 | PY：Test-ScriptClients | 无 | camp | 并入 campaign FBA；批 7 |
| `campaign/export_full.py` | 31 | 拼接 GetExportContent 的分页结果 | PY：Test-ScriptClients | 指定的输出文件 | camp | 并入 campaign FBA；批 7 |
| `campaign/plans/plan_*.py`（21 个，见下行） | 1,163 | 生成各能力族的真机测试计划 JSON；硬编码 VM 路径 `C:\Users\SIEMENS\Desktop` | camp 文档；Check-ScriptToolCalls 扫描其中的工具名 | `plans/plan_*.json`（未跟踪；Windows 上为 CRLF） | — | 用 Python 生成一次后，作为 JSON 数据提交，不写 C# 代码；批 7 |

21 个计划文件及行数：

- `plan_base` 40、`plan_drive1` 28
- `plan_hmi1` 56、`plan_hmi2` 46
- `plan_hw1` 61、`plan_hw2` 47
- `plan_misc1` 74、`plan_misc2` 51、`plan_misc3` 57
- `plan_off1` 48
- `plan_online1` 42、`plan_online2` 30
- `plan_plc1` 69、`plan_plc2` 43、`plan_plc3` 72、`plan_plc4` 83
- `plan_rerun1` 90、`plan_rerun2` 50
- `plan_sim1` 49
- `plan_uni1` 62、`plan_uni2` 65

### 2.5 打包（build-tools/）

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `Package-Release.py` | 318 | 用干净提交加本地已验证二进制构建交付 ZIP；核对记录哈希、交付规则，运行 validate-bundle 与 Check-Repository | RT-pk（候选包 `--local`、正式包 `--git`）、`ReleaseCommands` 03-package-local；`ReleasePublisher` 读取其 `package-result.json`；PY：Test-VersionCatalogWiring 读取其文本；DOC AGENTS、release-workflow、runtime-layout | `bin-build/releases/vX/<pkg>.zip`、`.sha256`、`package-result.json`（不跟踪；ZIP 是发布资产，按规范化方式比对） | git、`dotnet run validate-bundle`、zipfile | RT `package`（取代 P7-11 B13 的搬移）；批 5 |
| `Package-MultiVersion.py` | 112 | 构建本地全版本开发包，不发布 | DOC release-workflow、validation、version-tools、scripts/README、Studio README | 指定的 ZIP + `.sha256` | git、zipfile | RT `package-multi-version`，或退役（待决定）；批 5 |

### 2.6 vci-watch

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `scripts/operations/vci-watch/watch.py` | 604 | VCI 看门狗：附着已打开的工程，执行 ProjectToWorkspace 导出，写变更日志并做本地 git 提交；`--register-task` 注册 pythonw 计划任务 | PY：Test-ScriptClients；DOC vci-watch/README、scripts/README | 用户工作区、`log/`、`watch.state.json`、用户仓库提交（均为用户数据） | 引擎 EXE（stdio）、git、schtasks、`powershell`（Get-CimInstance 查进程命令行）、pythonw | FBA（`OutputType=WinExe`，计划任务指向发布的 exe）；不随包；批 7 |

### 2.7 保留的随包桥接脚本（scripts/ecosystem/）

| 文件 | 行 | 用途 | 调用方 | 外部依赖 | 性质 |
|---|---:|---|---|---|---|
| `plc_tools_bridge.py` | 120 | PLC Tools 的 JSON/stdio 适配：catalog/help/run/audit-pdf | CS：EcosystemTools.cs、QualityAuditTools.cs、BundleLayout.cs（`BundleResource.PlcToolsBridge`）、BundleLayoutTests.cs；RT：BundleManifestRequirements.cs、`RequireEcosystemPython` 预检 | click、reportlab、上游包 | 随包产品，保留 |
| `simaticml_decode_bridge.py` | 60 | SimaticML 解码的只读适配 | CS：V21EcosystemTools.cs、BundleLayout.cs；RT：BundleManifestRequirements.cs；PY：Test-V21Ecosystem | simaticml_decoder（随仓库附带） | 随包产品，保留 |

### 2.8 保留的第三方 Python（按包汇总，407 个）

| 目录 | 文件 | 行 | 随包 |
|---|---:|---:|---|
| `third_party/siemens-plc-tools/src` | 2 | 128 | 是 |
| `…/packages/plc-code/src` | 140 | 46,688 | 是 |
| `…/packages/plc-core/src` | 24 | 4,777 | 是 |
| `…/packages/plc-iol/src` | 16 | 3,771 | 是 |
| `…/packages/plc-modbus/src` | 3 | 366 | 是 |
| `…/packages/plc-net/src` | 7 | 1,074 | 是 |
| `…/packages/plc-sim/src` | 20 | 3,164 | 是 |
| `…/packages/plc-sup/src` | 8 | 1,010 | 是 |
| `…/packages/plc-trace/src` | 6 | 1,281 | 是 |
| `third_party/siemens-plc-tools/tests` 与 `packages/*/tests` | 163 | 28,465 | 否（供 Test-Ecosystem 使用） |
| `third_party/simaticml-decoder/src` | 18 | 5,962 | 是 |

### 2.9a 手动/按需检查（scripts/checks/，14 个）

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `Check-LiteProfile.py` | 182 | 检查 lite 默认档可用且与 full 不同；需要已构建的引擎与 harness | DOC scripts/README（手动） | 无 | TiaMcp.Engine.Harness | FBA 或退役；批 4 |
| `Snapshot-SharedNative.py` | 100 | 用精确 HEAD 的 SDK 构建证明 P7-04 的 Siemens IL 与调用顺序 | 无调用方 | `--evidence` 指定的证据文件 | Compare-NativeCallOrder、SDK | 退役候选；批 6 |
| `Snapshot-SharedToolUsage.py` | 67 | 在八版 FoundationHost 管线上检查用法 | 无调用方 | coverage 输出 | 两个快照脚本、Test-ToolUsage | 退役候选，或并入 Test-ToolUsage；批 4 |
| `Test-CampaignInputs.py` | 51 | 用已构建引擎的 V4 结构离线校验全部战役输入 | DOC validation、scripts/README | 输出目录 | TiaMcp.Engine.Harness | FBA（批 7 计划改为 JSON 后同步读取方式）；批 4 |
| `Test-Ecosystem.py` | 24 | 用伴随 Python 运行上游 plc-code/iol/trace 单元测试 | DOC validation.md | 无 | pytest（伴随环境） | C# 启动器 FBA，或归入保留的生态部分（待决定）；批 7 |
| `Test-ExternalSourceDispatch.py` | 139 | 提取派发方法体，编译后对托管替身执行 | DOC | `--work-dir` 证据 | dotnet | FBA（使用共用的 EngineSources）；批 2 |
| `Test-FoundationProxyIdentity.py` | 140 | 用新建且值相等的 API 代理运行 Foundation 源规划与删除代码 | DOC | `--work-dir` | dotnet | FBA；批 2 |
| `Test-HmiImportSafety.py` | 156 | 提取 HMI 调用方和助手，做纯替身检查 | DOC | `bin-build` 临时目录 | dotnet | FBA；批 2 |
| `Test-PilotTools.py` | 118 | 迁移试点域经 SDK、桥接与隔离 STDIO 运行 | DOC engine-decomposition（历史文档） | 无 | TiaMcp.Engine.Harness | 退役候选；批 4 |
| `Test-PlcEditingMcp.py` | 118 | PLC 文档工具经真实 MCP 传输运行，不连 TIA | DOC | 输出目录 | TiaMcp.Engine.Harness | FBA；批 4 |
| `Test-SharedNativeIlReader.py` | 267 | 编译合成 IL，证明回调与守卫的处理 | CS：Test-SharedNativeMigration.cs；DOC adapter-merge | 临时文件 | dotnet | 并入 Compare-SharedNativePaths FBA 的自检；批 6 |
| `Test-SnapshotComparison.py` | 88 | 检查两个快照比较命令行的版本过滤 | 无调用方 | 临时文件 | `sys.executable` | 并入快照实现的 xUnit；批 4 |
| `Test-TechnologyImportSafety.py` | 96 | 技术对象导入安全：提取方法后对替身运行 | DOC | 临时文件 | dotnet | FBA；批 2 |
| `Test-ToolUsage.py` | 109 | 八版 STDIO 用法检索，执行白名单示例；CLAUDE.md 要求修改示例后运行 | DOC validation | `--coverage-output` → `manifest/tool-usage-coverage.json`（LF） | TiaMcp.Engine.Harness、主机 | RT 子命令或 FBA；批 4 |

### 2.9b 测试目录中的脚本（3 个）

| 文件 | 行 | 用途 | 调用方 | 写出 | 外部依赖 | 目标·批次 |
|---|---:|---|---|---|---|---|
| `tests/Engine/TiaMcp.Engine.Harness/measure_concurrency.py` | 202 | 离线派发及真实 STDIO/HTTP 并发测量 | 无调用方（TiaMcp.Engine.Harness 已有 `concurrency-only` 模式） | 输出目录 | 主机、harness | 退役候选；批 6 |
| `tests/FoundationHost/TiaMcp.FoundationHost.Tests/EngineSliceOracle.py` | 118 | 将主机与织入后的引擎 worker 对比冻结的 V21 切片记录 | 无调用方 | 输出目录 | dotnet、Snapshot-ToolResponses | 退役候选，或改为 FBA；批 4 |
| `tests/Studio/Test-BridgeSmoke.py` | 86 | Studio 适配器 hello 与只读 RPC（使用本地 SDK） | DOC src/Studio/README.md | 无 | Bridge.exe | FBA，或并入 Studio 测试项目；批 7 |

## 3. 分批方案

### 3.0 目标形态与约定（沿用已有移植的做法）

已有先例：提交 `038ed553`、`1d0bc966`、`12398d4e` 曾把 PowerShell 移植到 C#，做法如下。

- **file-based app**
  - 实例：`scripts/checks/Test-MatchPlcName.cs`、`Test-SharedNativeMigration.cs`、`scripts/generate/Generate-ToolCapabilityMatrix.cs`、`src/Adapters/build/Test-*.cs`。
  - 文件头：`#!/usr/bin/env dotnet`，一行用途注释，`// Usage: dotnet run <path> -- …`，`#:property PublishAot=false`，离线时加 `#:property NuGetAudit=false`。
  - 编码约定：每个文件自带 `FindRoot`/`Take`/`Required`；JSON 用 `JsonNode`（不走反射序列化）；写文件用 `new UTF8Encoding(false)`；`--check` 比较 UTF-8 字节。
  - 发布工具用 `dotnet run <file.cs> -- …` 调用（见 `BuildReleasePipeline` 的 `tool-capability-matrix`）。
- **build-tools/release 子命令**
  - 新子命令要登记到 `CommandLine.Commands` 和 `ReleaseCommands.Run`。
  - 选项采用 `-Name value` 风格，布尔选项需列入 `BooleanOptions`。注意 `Options.Parse` 只去掉前导 `-`，所以 `--no-binaries` 不等于 `-NoBinaries`；需要兼容旧拼写时，在解析中去掉名称里的 `-`。
  - 失败抛出 `ReleaseException`（带退出码）；`ReleaseCommandTable` 登记命令说明；`release-checks.json` 维护路径与检查的映射和 `selfTests`。
  - 测试放在 `tests/Release/TiaMcp.ReleaseTool.Tests`（InternalsVisibleTo），对应 `tests/test-suites.json` 中的 `release-tool` 套件。
- **反射 net48 引擎的检查**：放进 `tests/Engine/TiaMcp.Engine.Harness` 的模式（`DeveloperChecks.cs`），用 `TiaMcp.Engine.Harness.exe <engine> <mode>` 调用。

本任务的分工：

1. **发布链与 CI 热路径**：棘轮、仓库检查、生成器 `--check`、快照、TRX 门禁、运行检查、打包和资产校验，都改为 **RT 子命令**。这样在发布工具内只编译一次，能进程内调用，也能与 `SourceRoots.cs`、`ReleaseRecords`、validate-bundle 共用代码。
   CI 用 `dotnet run --project build-tools/release -- <cmd>`。
2. **纯断言类源码契约**：`Test-*Sources`、`Test-EngineSources`、`Test-VersionCatalogWiring`、`Test-DomainTools --source-only` 和各检查的 `--self-test`，改为 **xUnit**。
   建议新建 `tests/Repository/TiaMcp.SourceContracts.Tests`（net10，不依赖 Siemens），在 `tests/test-suites.json` 中登记为 `source-contracts` 套件并设定 minimumPassed，由 TRX 门禁执行。发布工具自身子命令的自检放进现有的 ReleaseTool.Tests。
3. **按需、虚拟机、诊断、运维工具**：改为 **FBA**，放在 `scripts/checks`、`scripts/diagnostics`、`scripts/operations`。
4. **共用库**：建议新建 `build-tools/common`（名称按 P7-11 的“目录 = csproj = 程序集 = 命名空间”规则确定，net10.0，不引用外部包），包含：
   - `PythonJson`：与 Python `json.dumps` 一致的写出器
   - `CSharpLexer`、`MatchingPairs`：从 `Check-SwallowedExceptions` 逐行移植
   - `EngineSources`、`McpResults`、`OfflineFixtures`、Git 文件列表与根目录定位
   - 批 4 加入 MCP 测试客户端

   发布工具和测试项目以 ProjectReference 引用，FBA 用 `#:project` 引用（批 1 先在 CI 的 SDK 10.0.x 上试验 `#:project`；本机 SDK 为 10.0.401）。
5. **词法器不改用 Roslyn**：Roslyn 的词元与现有词法器不同，会改变棘轮指纹，而且需要 NuGet 还原，Codex 沙箱没有网络。
6. **删除时机**：一个 Python 模块要等到最后一个 Python 导入者转换完才能删除。在此之前，Python 和 C# 两份实现由同一组测试向量覆盖（下文“副本保留到”指这一点）。
7. 每批完成后按 `docs/development/validation.md` 执行 V。涉及发布链的批次（2–6）在分支头运行 preflight 和 `run-release-build -Tier full` 候选检查后再快进合并。CI 需要 Ubuntu 与 Windows 都通过。

### 3.1 批次表

| 批 | 内容 | 文件 | 行 | 依赖 | 风险 |
|---|---|---:|---:|---|---|
| 1 | 共享基础与 TRX 门禁 | 6 | 699 | P7-11 | 低 |
| 2 | 静态源码检查与棘轮基线 | 21 | 5,139 | 1 | 中（指纹） |
| 3 | 生成器（产品嵌入资源逐字节一致） | 6 | 2,250 | 1、Phase6Plan 去留决定 | 高 |
| 4 | 契约/响应快照与 MCP 测试客户端 | 16 | 5,931 | 1、3 | 高（摘要哈希） |
| 5 | 打包与发布资产校验 | 3 | 695 | 2 | 高（发布资产） |
| 6 | 发布链运行检查 | 14 | 6,080 | 4、5 | 中高（发布记录字段） |
| 7 | 虚拟机验收、诊断、vci-watch | 32 | 2,843 | 1、2（脚本调用检查须先能扫描 `.cs`） | 低中，可与 3–6 并行 |
| 8 | 收尾：删除剩余 Python 副本，禁止新增自有 Python，清理 `-Python` | 0 | — | 1–7 | 低 |

合计 98 个文件、23,637 行。

#### 批 1：共享基础与 TRX 门禁

- **转换内容**
  - `mcp_results.py`、`offline_fixtures.py`、`engine_sources.py`，以及 `Check-SwallowedExceptions.py` 中的 `Lexer`/`matching_pairs`/`SKIP_DIRS`/`GENERATED_SUFFIXES`，移入共用库。
  - `Test-EngineSources.py` → xUnit。
  - `Test-DotnetSuites.py` → RT `test-suites`，发布工具改为进程内调用。
  - `Test-HostBehaviorParity.py` → RT `host-parity`。
- **测试向量**
  - `PythonJson` 的向量由 Python 生成一次后提交，覆盖下列选项组合：`ensure_ascii` 真/假 × `indent` 无/0/1/2 × 默认/紧凑分隔符 × `sort_keys`。
  - 值覆盖控制字符、U+2028、代理对/emoji、`</`、非 BMP 键、浮点数（1e16、1e-05、-0.0、1.0）、大整数和空容器。
  - 词法器做一次性全仓对拍：Python 与 C# 对全部 `src/**/*.cs` 产生的词元序列（kind、start、end、value）必须完全相同。
- **转换期间 Python 副本保留到**
  - `engine_sources.py` 与词法器：批 4
  - `mcp_results.py`：批 7
  - `offline_fixtures.py`：批 6
- **CI 改动**
  - offline-tests、foundation-transport、validate.yml 中的 `python …Test-DotnetSuites.py` 改为 `dotnet run --project build-tools/release -- test-suites -Suite …`。
  - source-contracts job 加 `setup-dotnet`，改为执行 `test-suites -Suite source-contracts`。
- **build-tools/release 改动**
  - 涉及的调用：`RunDotnetSuiteForRelease`；`ReleaseCandidateChecks` 05-prompt-registration；`ReleaseCommands` 1332 和 1352 行；Preflight 中的“suite runner self-tests”。
  - 配置：`ReleaseCommandTable`（offline、offline-v20、write-guard 等条目）；`release-checks.json` 中 `dotnetsuites-self-test` 及 paths 映射。
- **其他引用**
  - `Test-DownloadRouteSelection.cs` 改用共用库 EngineSources，去掉 `-Python`。
  - 修改 `tests/Engine/*/Program.cs` 的提示文字、`TiaPortalOpenness.Offline.slnx` 的注释、AGENTS.md 和 validation.md。

#### 批 2：静态源码检查与棘轮基线

- **转换内容**（21 个文件）
  - 四个棘轮：吞异常、注释、MCP 文本、响应信封，合并为 RT `check-ratchet -Kind …`，支持 `-UpdateBaseline`、`-AllowGrowth`、`-ReviewData`。
  - RT 子命令：`check-envelope-rewrite`、`check-adapter-boundary`、`check-bundle-layout`、`check-repository`、`check-dead-tool-references [-Fix]`、`check-tia-features [-Capture]`、`check-script-tool-calls`。
  - 6 个 `Test-*Sources` 与 `Test-VersionCatalogWiring` → xUnit。
  - 4 个“提取方法后编译替身”的检查（Hmi、Technology、ExternalSource、ProxyIdentity）→ FBA。
- **`check-script-tool-calls` 的扫描范围**：从本批起扫描 `scripts/**/*.cs`，用词法器找出调用助手中的字面工具名，例如 `CallTool("…")`、`{"name": …, "arguments": …}`。
  计划 JSON 照旧扫描。`.py` 扫描继续用 Python 版本，直到批 7。否则批 7 移植的 VM 客户端会悄悄脱离保护。
- **逐字节输出**
  - 四个基线：在未改动的树上执行 update，必须得到与提交相同的字节；另外对全仓重新计算全部指纹，Python 与 C# 的多重集合必须相等。
  - `tia-feature-expectations.json`（`-Capture`）。
  - DeadToolReferences `--fix`：在副本上对同一组变异输入执行，比较改写结果。
- **Python 副本保留到**：Check-BundleLayout 库保留到批 5；Check-SwallowedExceptions 词法器保留到批 4。
- **CI 改动**
  - source-contracts：除 Generate-ToolUsage、快照、Check-ToolsList、Test-DomainTools、mcp_results、Test-ScriptClients 外，全部改为 C#。
  - offline-tests 去掉 setup-python（吞异常自检已移到 xUnit）。
  - validate.yml：Check-Repository、Check-TiaFeatures、Test-SupplementaryReadSources 改为 C#。
  - release.yml：Check-Repository `--package-mode` 改为 C#。master 的检查也用于复验旧 tag，必须兼容旧包的布局。
- **build-tools/release 改动**
  - Preflight 前两项及“dead tool references”，`RunReleaseEarlyGates` 中的 repository、dead references、bundle layout 自检。
  - `release-checks.json` 的 selfTests：现有结构是 `{check, script, arguments}`，由 `runPython` 执行。需要新增 C# 命令形式（例如 `"command": ["check-repository", "-SelfTest"]`，由发布工具在进程内或用 `dotnet run` 执行），同时更新 paths 映射的键（改为新的 C# 源文件路径）。
  - `ReleaseTierTests`、`source-roots.json`（Check-BundleLayout 的消费者条目）。
  - `SourceRootsTests` 删除内联 Python，因为两份实现已合为一份。
  - Check-Repository 的禁用扩展名逻辑保持不变，批 8 再收紧。

#### 批 3：生成器

- **前提**：先决定 Generate-Phase6Plan 的去留，与 P7-11 B5 协调。建议如下：
  - 把仍在使用的产品输出抽成 RT `generate-catalogs [-Check]`：ToolProfiles.resx（`resource_text`）、FullEngineRejections.json、version-tools.json、version-tool-catalog.md；如有需要，再加 package-manifest 计数、version-feature-matrix 的 behaviorCapabilities、台账中的能力块。
  - `phase6-review.md` 冻结归档，不再生成。
  - `NEW_V4_TOOLS` 改为数据文件或 C# 常量，因为 Generate-ToolUsage 现在用 `ast.literal_eval` 读取 Phase6Plan 的源码。
- **转换内容**
  - Generate-ToolUsage → RT `generate-tool-usage [-Check]`。
  - Generate-ReleaseToolMigration → RT，或冻结为历史文档（待决定）。
  - Audit-VersionTools、Audit-ToolUsage → RT `audit-version-tools`、`audit-tool-usage`。
  - PlcRender 的 generate.py → FBA（`#:project` 引用 TiaMcp.Logic）。
- **逐字节输出**：`ToolUsageData.json`、`ToolProfiles.resx`、`FullEngineRejections.json`、`version-tools.json`、`version-tool-catalog.md`、`v4.0.0-tool-migration.md`、`version-api-audit.json`、`tool-usage-coverage.json`、PlcRender 的 16 个 html，以及 Phase6Plan 不带 `--shared-host` 时的 3 个输出。方法见第 4 节。
- **Python 副本保留到**：如果 phase6_groups 仍存在，`Generate-Phase6Plan.py` 保留到批 4。
- **CI 改动**：source-contracts 中的 `Generate-ToolUsage --check` 改为 C#。
- **build-tools/release 改动**
  - Preflight 中的“phase-6 tables generator”“tool usage catalog generator”“release migration tables generator”。
  - `RunReleaseEarlyGates` 的 tool usage catalog；`BuildReleasePipeline` 的 `tool-usage` 步骤及 `ReleaseCommandTable`。
  - `ReleaseCommands` 第 1488、1494 行的 Audit 步骤。
- **文档**：CLAUDE.md 第 2 条所说的“生成嵌入目录”改为指向新命令。

#### 批 4：契约/响应快照与 MCP 测试客户端

- **共用库加入 MCP 测试客户端**，内容来自：
  - Test-ResourceDiscovery 的 STDIO/HTTP 会话与 harness 启动
  - tool_usage_checks
  - snapshot_text_migration
- **快照**：Snapshot-ToolContracts、Snapshot-ToolResponses → RT `snapshot-contracts`、`snapshot-responses`，提供 capture/compare/verify/self-test。canonical JSON 与 `snapshot_text` 的布局交给 `PythonJson`。
- **其他转换**
  - Check-ToolsList → RT。
  - Test-DomainTools：源码部分 → xUnit，运行部分 → RT/FBA。
  - Test-SnapshotComparison → xUnit。
  - 依赖本批库的按需脚本：Test-CampaignInputs、Test-PlcEditingMcp、Test-ToolUsage → FBA 或 RT；Check-LiteProfile、Test-PilotTools、Snapshot-SharedToolUsage、EngineSliceOracle 退役或改为 FBA（待决定）。
- **逐字节输出**
  - 用同一批已构建二进制分别执行两种 capture，与已提交的 `manifest/contracts/v4/{baseline,responses}/*.json` 逐字节比较。
  - verify 模式对每个已存的 `responseDigest` 重新计算并比较。
- **Python 副本保留到**：Test-ResourceDiscovery 保留到批 6；engine_sources 和词法器在本批末删除；Snapshot-* 在本批末删除。
- **CI 改动**：source-contracts 中的 Check-ToolsList、`Test-DomainTools --source-only`、两个快照的 verify 改为 C#。
- **build-tools/release 改动**
  - `BranchGate` 的 verify；`ReleaseCandidateChecks` 的 `CaptureSnapshots` 与各 compare 步骤；Preflight 的 V4 快照两项。
  - `BuildReleasePipeline` 的 `resource-discovery`；`release-checks.json` 中 contracts/responses/resource-discovery 的自检与 paths。
  - `ReleaseTierTests` 的 InlineData（`Snapshot-ToolResponses.py` → 新路径）；manifest/contracts/v4/README.md。

#### 批 5：打包与发布资产校验

- **转换内容**
  - Package-Release → RT `package`，复用 validate-bundle、SourceRoots 和 ReleaseRecords，直接取代 P7-11 B13 的搬移。
  - Verify-ReleaseAsset → RT `verify-release-asset`。
  - Package-MultiVersion → RT `package-multi-version`，或退役。
- **release.yml**：两处内联 `python -c` 改为发布工具子命令（读取 delivery.json，带 `-Extract` 解压），删除 setup-python。
- **记录核对**：C# 打包器沿用现有记录字段，按路径核对 `scripts/checks/Test-*.py` 的 SHA-256。这些路径在批 6 改为 C# 源文件。
- **验证**：
  - ZIP 采用规范化一致，不要求逐字节，见第 4 节。
  - 两套实现互验对方的 ZIP。
  - 更新器从 C# 生成的 ZIP 做升级与回滚测试。
  - 完整走一次候选发布。
- **build-tools/release 改动**：`ReleaseProduction` 的 package/verify-package、`ReleaseCommands` 03-package-local 与最终打包、`ReleasePublisher` 与 `BuildReleasePipeline` 中提示 “run Package-Release.py” 的文字。
- **文档**：AGENTS.md、release-workflow.md、runtime-layout.md。
- **Python 删除**：Check-BundleLayout 库随本批删除。

#### 批 6：发布链运行检查

- **转换内容**
  - Test-FoundationTransport、Test-ReleaseApprovalGate、Test-ReleaseSmoke、Test-LocalStability、Test-NativeDiagnostics、Test-NativeLifecycle、Test-NativeMcpSession、Test-RelocatedBundle、Test-V21Ecosystem → RT 子命令或进程内阶段。ctypes 和 winreg 部分改用 .NET 的 Process、Registry 和 P/Invoke。
  - Compare-NativeCallOrder、Compare-SharedNativePaths（并入 Test-SharedNativeIlReader 的自检）→ FBA，同时改 `Test-SharedNativeMigration.cs`。
  - Snapshot-SharedNative、measure_concurrency 退役或改为 FBA。
- **发布记录**：`scriptSha256`、`supervisorSha256`、`protocolScriptSha256`、`resourceHelperSha256` 及审批脚本哈希改指 C# 实现文件。字段名不变，但含义改为“实现源文件”。
  记录 schema 改动需要按“阶段中途需要发版”的规则，通过完整发布重建 manifest。C# 打包器的核对路径同步修改。
- **输出协议**：保持 stdout 标记逐字一致，因为 `BuildReleasePipeline` 按 Regex 解析，例如 `COMPLETE: (\d+) native supervisor checks passed; live TIA tests NOT RUN`。`result.json` 的字段也保持不变。
- **CI 改动**：foundation-transport 与 validate.yml 的 Python 步骤改为 C#，删除 setup-python。
- **build-tools/release 改动**：`BuildReleasePipeline`（各 `RunBuildSpec`、`VerifyDiagnosticFixture`、`VerifyEngineApproval`、`VerifyStability`）、`BranchGate`、build-multi-version 的 transport/approval、`ReleaseCandidateChecks` 的 relocation/smoke，以及 `ReleaseCommandTable`、`release-checks.json` 的 paths/selfTests、`source-roots.json` 中的 Test-ReleaseSmoke、`ValidateBuildRecords` 的必需文件。
- **文档**：Test-VersionCatalogWiring（批 2 的 xUnit）的断言目标；native-lifecycle-tests.md、native-mcp-session-tests.md。
- **Python 删除**：Test-ResourceDiscovery、offline_fixtures 在本批删除。

#### 批 7：虚拟机验收、诊断与 vci-watch（批 2 后可与 3–6 并行，路径不重叠）

- **转换内容**
  - Probe-McpServer、Sweep-WrongPathHonesty → FBA。
  - camp、rawmsg、export_full、make_ledger 合并为 campaign FBA，子命令为 call/run/raw/export/ledger。
  - 21 个 `plan_*.py` 用 Python 生成一次，作为 JSON 数据提交（审查硬编码的 VM 路径）。
  - vm_ledger 退役，`vm_ledger.json` 冻结。
  - Test-ScriptClients → xUnit，覆盖 C# 客户端。
  - vci-watch → FBA：`OutputType=WinExe`，`dotnet publish` 发布到 `bin-build/vci-watch` 后注册计划任务。进程命令行的查询，从调用 `powershell Get-CimInstance` 改为 System.Management（需要 NuGet）或 P/Invoke（待定）。
  - Test-BridgeSmoke → FBA 或 Studio 测试模式。
  - Test-Ecosystem → C# 启动器，或归入保留的生态部分（待定）。
- **逐字节输出**：make_ledger 生成的 `real-machine-ledger.md`（同时检查 Phase6Plan 的能力块是否仍要保留）；`plans/*.json` 首次提交时与 Python 生成结果一致。
- **CI 改动**：source-contracts 中 `python scripts/mcp_results.py` 和 `Test-ScriptClients.py` 改为 C#，删除 setup-python。
- **文档**：scripts/README.md、vci-watch/README.md、validation.md。
- **Python 删除**：mcp_results 在本批删除。

#### 批 8：收尾

- 确认所有 Python 副本已删除，`git ls-files "*.py"` 只剩保留清单（第 5 节）。
- Check-Repository 增加规则：拒绝被跟踪的自有 `*.py`。白名单为 `third_party/siemens-plc-tools/**`、`third_party/simaticml-decoder/**`、`scripts/ecosystem/plc_tools_bridge.py`、`scripts/ecosystem/simaticml_decode_bridge.py`。
- Check-ScriptToolCalls 不再扫描 `.py`。两个桥接脚本不调用 MCP 工具。
- 删除发布工具的 `Py`、`-Python` 选项、“Python >= 3.12”前置检查及其测试。保留 `EcosystemPython`/`CompanionPython`/`TIA_MCP_PLC_TOOLS_PYTHON` 和 v21-ecosystem 夹具。
- workflow 中不再出现 setup-python。
- 更新文档：
  - validation.md：“纯 Python 源码契约”一节改名改写
  - scripts/README.md、release-workflow.md、CONTRIBUTING.md、AGENTS.md
  - runtime-layout.md、tool-development.md、official-tool-usage.md
  - src/Adapters、src/Studio、src/Shared 的 README
  - manifest/README.md、WorkerChannel.Tests/README.md、PlcRender/README.md

  历史文档（phase6-review、engine-decomposition、adapter-merge、CHANGELOG）保留原样。
- `.gitattributes` 中的 `*.py text eol=lf` 和 `.gitignore` 中的 `__pycache__/` 为保留文件继续保留。

## 4. 生成器的逐字节一致验证方法

**范围**：

- 逐字节适用于受跟踪的生成结果（第 2 节标注“逐字节”的文件）。
- 输出中嵌入的哈希也必须逐字节一致：`fingerprint`、`responseDigest.sha256`、`expandedGraphSha256`、`catalogSha256`。
- 未跟踪的结果文件只要求字段与 stdout 标记兼容。
- 发布 ZIP 用规范化一致（第 9 步）。

1. **基准树**
   - 选一个干净提交 B，用 `git -c core.autocrlf=false worktree add <dir> B` 建工作树。本机全局为 `core.autocrlf=true`，docs 和 json 在工作树中会变成 CRLF，从而掩盖行尾差异。
   - 先用 Python 写模式运行全部生成器，确认 `git status --porcelain` 为空，即提交的输出是最新的。如有漂移，先记录并单独处理，不并入移植。
2. **候选树**：在同时含 Python 与 C# 实现的分支头 C（尚未删除 Python）上，用 C# 写模式运行。
   对每个输出比较 `git hash-object --no-filters <file>` 与 `git rev-parse B:<path>`，或直接比较原始字节的 sha256。不要依赖 `git diff`，它受 autocrlf 和 eol 属性影响。
3. **交叉 `--check`**：C# 的 `--check` 必须接受 Python 的输出，反之亦然；再修改 1 个字节（含只改 LF/CRLF、只加 BOM），两边都必须拒绝。
4. **两个平台**：
   - Windows：维护者机器上运行，中文系统与本机区域设置。
   - Ubuntu en-US：在过渡期增加临时 CI job `py-cs-parity`，Ubuntu 与 Windows 各跑一次，覆盖不需要二进制的生成器：Generate-ToolUsage、generate-catalogs、release-migration、四个棘轮的 update（在副本上运行）、Check-TiaFeatures capture。批 8 删除该 job。
   - 需要二进制或 SDK 的生成器只能在维护者机器上用同一批构建结果先后运行 Python 与 C#，结果写入 `bin-build/refactor/tasks/P8-10-批N.md`：快照 capture、Audit-VersionTools、PlcRender golden、Test-ToolUsage 覆盖、make_ledger。
5. **哈希对拍**
   - 棘轮：对全仓重算全部指纹，比较两份实现的完整多重集合。
   - 响应快照：用 C# 的 `canonical()` 对基线中每个保存的响应重算 digest。
   - `catalogSha256`：读取时去 BOM、CRLF 改为 LF，再算 sha256。
   - `PythonJson` 的向量见批 1。
6. **词法与提取对拍**：对 `src/**/*.cs` 做词元序列全量比较；对检查中用到的全部成员名，`member()`/`type_text()` 的提取文本必须相等（含 Test-DownloadRouteSelection 中的提取器）。
7. **固定输入**
   - 不放未跟踪文件。Generate-Phase6Plan 使用 `git ls-files --others --exclude-standard`，会读入未跟踪文件。
   - 固定 `TZ`、区域设置和 `DOTNET_CLI_UI_LANGUAGE`。
   - 比较前后不切换分支，不重新构建二进制。
8. **删除 Python 后的预期漂移**
   - Generate-Phase6Plan 会扫描 `git ls-files`，并校验 TASK_PATHS 中的脚本路径是否存在；Check-ScriptToolCalls 会扫描 `scripts/**`。因此删除 `.py` 后，生成结果中的路径列表和计数会正常变化。
   - 生成文件里还写着生成器文件名，例如 `Generated by \`Generate-Phase6Plan.py\``、台账中的 `` `Generate-Phase6Plan.py` ``。
   - 做法：先在含两套实现的树上完成逐字节对拍并合并；再删除 Python，用 C# 重新生成；最后作为单独的“预期差异”提交，逐行列出变化来源。
9. **ZIP 规范化一致**
   - 先说明为什么不能逐字节：Python `zipfile.writestr` 写入当前本地时间、`external_attr`（0o600<<16）和 `create_system`，.NET `ZipArchive` 的写法与 deflate 实现都不同。
   - 比较内容：条目名与顺序、每个条目的未压缩字节和大小、条目数，以及 `.sha256` 的文件格式（`<hex>  <name>\n`）。
   - `package-result.json` 的字段一致。
   - 两套 Verify-ReleaseAsset 互相接受对方的 ZIP；更新器能完成解包、升级与回滚；release.yml 中的提取步骤在 Windows 上能解出相同的文件树。

## 5. 保留不改的文件

| 文件 | 数量/行 | 原因 |
|---|---|---|
| `scripts/ecosystem/plc_tools_bridge.py` | 1/120 | **随包产品脚本**，是 MCP 引擎调用上游 PLC Tools（click 命令组、reportlab 审计 PDF）的进程边界。BundleLayout 将其列为必需资源（`BundleResource.PlcToolsBridge`），BundleManifestRequirements 也将其列为必需文件。由伴随 Python 环境运行（`install-plc-tools` 建 venv；`TIA_MCP_PLC_TOOLS_PYTHON`） |
| `scripts/ecosystem/simaticml_decode_bridge.py` | 1/60 | **随包产品脚本**，用于 V21 生态工具的只读 SimaticML 解码，直接导入随仓库附带的 `simaticml_decoder` |
| `third_party/siemens-plc-tools/**` | 389/90,724 | MIT 上游代码，固定在 UPSTREAM.json 的提交（`887eea1…`）。src 部分随包，tests 不随包，供 Test-Ecosystem 使用。CLAUDE.md 要求保留第三方来源和许可证；用 C# 重写等于分叉上游，并失去上游测试 |
| `third_party/simaticml-decoder/**` | 18/5,962 | MIT 上游，随包；由桥接脚本导入 |
| 相关配置与数据 | — | `.gitattributes` 的 `*.py text eol=lf`；`.gitignore` 的 `__pycache__/`；`delivery-files.json` 的 ecosystem 和 third_party 前缀；`docs/licenses`；`InstallPlcToolsCommand` 的 ExternalPackages（与上游 `pyproject.toml` 一致，批 2 的 C# 自检需要解析 TOML，见第 6 节） |

按需保留（待维护者决定）：`Test-Ecosystem.py`（24 行）只是用伴随 Python 运行上游 pytest 的启动器。可以改成 C# 启动器（行为不变：要求 `-X utf8`，用 `PYTHONPATH` 让源码优先），也可以视为生态部分保留。

## 6. 风险与未知

**序列化与文本语义**（逐字节一致的主要风险）

1. **Python `json.dumps` 与 System.Text.Json 不同**
   - 即使使用 `UnsafeRelaxedJsonEscaping`，Utf8JsonWriter 仍会转义非 BMP 字符（代理对）和部分字符。控制字符转义为大写 `\u001B`，Python 为小写 `\u001b`。
   - 浮点格式不同：.NET 为 `1E+16`，Python 为 `1e+16`、`1e-05`、`100000.0`。JsonNode 保留数字原文，而 Python 重新解析后会按 `repr` 输出。
   - `sort_keys` 按码点排序；C# 的 Ordinal 按 UTF-16 码元排序，键中出现非 BMP 字符与 U+E000–U+FFFF 时结果不同。
   - 默认分隔符不同：无 indent 时 Python 用 `', '`/`': '`，有 indent 时用 `','`。
   - 结论：必须自写 `PythonJson`。指纹、`responseDigest`、`expandedGraphSha256`、`catalogSha256` 都依赖这一点。
2. **正则的 Unicode 语义**
   - Python 的 `\s` 包含 U+001C–U+001F，.NET 不包含。
   - Python 的 `\w` 是 isalnum 加 `_`：不含 Mn/Mc，含 Nl/No；.NET 的 `\w` 是 L、Mn、Nd、Pc。
   - Check-McpText 和 Check-CommentHygiene 的 `re.sub(r'\s+', ' ', …)` 以及词法器的 IDENTIFIER 都受影响。
   - 处理：用显式字符类写出 Python 的语义，并用测试向量覆盖。
3. **XML**：`ToolProfiles.resx` 由 ElementTree 的 `indent` 和 `tostring` 生成。文本中转义 `& < >`，不转义 `"`；自闭合写成 `<x />`；属性顺序与插入顺序一致。不要用 XmlWriter，按 ET 的规则手工拼接。
4. **生成文本中漏出的 Python 表示**：phase6-review.md 表格中有 `None`（Python `str(None)`），迁移表有 `['en-US','zh-CN']` 样式的文本，生成结果里还写着生成器文件名。C# 必须逐字重现，删除 Python 之后再单独修改。
5. **排序与大小写**：Python `sorted()` 按码点排序。`str.lower()` 与 `ToLowerInvariant` 对个别字符结果不同（Generate-ToolUsage 的文件排序显式用 `lower()`）。Windows 上 pathlib 对路径大小写不敏感。`Directory.EnumerateFiles` 的顺序随系统不同，一律在 Ordinal 下显式排序。
6. **行尾与 BOM**
   - 本机为 `core.autocrlf=true`。`manifest/**`、PlcRender、contracts-v3 为 `-text`。
   - 多数生成器显式写 LF，但以下几处在 Windows 上以文本模式写出 CRLF：`Audit-VersionTools`（**已提交的 `manifest/version-api-audit.json` 就是 CRLF**）、`Audit-ToolUsage`、`Check-TiaFeatures --capture`、`vm_ledger`、`plan_*.py`。
   - Python 的 `read_text` 会把 CRLF 当作 LF 读入；`'utf-8-sig'` 去掉 BOM，`'utf-8'` 保留 `\ufeff`。C# 的 `File.ReadAllText` 不转换行尾，却总会去掉 BOM。`Encoding.UTF8` 写出时会带 BOM。
   - 处理：每一处读写都写明换行与 BOM 规则，并用测试固定。需要决定 C# 是否在 Windows 上继续写 CRLF 以保持逐字节一致，还是先对拍、再做一次统一为 LF 的评审提交。
7. **同一文件有两个生成器**
   - `manifest/version-tools.json` 与 `docs/reference/version-tool-catalog.md`：Audit-VersionTools 与 Generate-Phase6Plan 都会写，表头和换行都不同。
   - `manifest/tool-usage-coverage.json`：Audit-ToolUsage、Test-ToolUsage、Snapshot-SharedToolUsage 都会写。
   - `docs/reference/real-machine-ledger.md`：make_ledger 写整份文件，Phase6Plan 写其中的能力块。
   - 逐字节的基准取发布流程最后写入的状态。需要维护者指定每个文件唯一的生成者。

**平台、进程与工具链**

8. **CI 差异**：en-US Ubuntu 与 Windows（控制台代码页 cp1252，路径较深）。
   - 数字、日期与大小写一律用 InvariantCulture 和 Ordinal。Python `isoformat()` 为 6 位小数，.NET `"o"` 为 7 位。
   - 设置 `Console.OutputEncoding = UTF8`。中文报告（例如死引用报告）此前已因 cp1252 出过问题。
   - 发布工具按 Regex 解析的 stdout 标记保持 ASCII 且逐字不变。
9. **子进程语义**
   - Python 文本模式管道会把 `\r\n` 转成 `\n`，C# 不会；MCP stdio 按行分帧，要显式设定编码和换行。
   - `CREATE_NO_WINDOW`、进程树终止、超时都要对应处理。
   - ctypes 调用要换成 .NET API：`Process32First` 查父进程，`GetProcessMemoryInfo` 中 PrivateUsage 与 `PrivateMemorySize64` 的口径要核对，`GetProcessHandleCount` 对应 `HandleCount`。winreg 改用 `RegistryKey.OpenBaseKey` 加 RegistryView。
10. **file-based app**
    - 默认开启 PublishAot：反射 JSON 会抛异常，必须加 `#:property PublishAot=false`，或只用 JsonNode。
    - 每个 FBA 单独编译。CI 中约 30 次调用会明显变慢，所以热路径放进 RT 子命令。
    - `#:project` 引用共用库能否用于 CI 的 setup-dotnet `10.0.x` 和 Codex 沙箱（无网络，需写入 runfile 缓存），在批 1 先验证。
11. **Python 特有依赖**
    - `ast`：Check-ScriptToolCalls 解析 Python；Generate-ToolUsage 用 `literal_eval` 读 Phase6Plan 的源码。C# 没有对应能力，靠调整顺序和数据外置解决。
    - `tomllib`：BundleLayout 自检解析上游 `pyproject.toml`。BCL 没有 TOML 解析器，要么只解析 `[project]` 和 `optional-dependencies` 的数组，要么引入 NuGet 包（需要还原）。
    - `unittest.mock`：改为接口注入。
    - `runpy`：Phase6Plan 与 Generate-ToolUsage 互相执行（模块顶层有副作用）。
    - vci-watch 依赖 pythonw 无窗口运行，并调用 PowerShell 查询进程命令行；System.Management 需要 NuGet。
12. **发布记录 schema 变化**：脚本哈希字段的含义改变；`release-checks.json` 的 selfTests 改为命令形式；Package 会核对脚本哈希。批 5、批 6 都会改发布链，必须各自完整走一次候选发布，不能在发布周期中间停在半迁移状态。
13. **复验旧 tag**：release.yml 用 master 上的检查复验旧 tag 的树。C# 版的 Check-Repository `--package-mode`、verify-release-asset 必须兼容旧版 delivery-files.json 和 manifest 字段。

**未知与待维护者决定**

14. Generate-Phase6Plan：拆出产品输出，phase6-review.md 冻结；`--check` 移出预检（与 P7-11 B5 协调）。Generate-ReleaseToolMigration 是否同样冻结。
15. 无调用方或历史用途脚本是否直接退役：Snapshot-SharedNative、Snapshot-SharedToolUsage、Test-SnapshotComparison（可并入 xUnit）、Test-PilotTools、Check-LiteProfile、measure_concurrency、EngineSliceOracle、Package-MultiVersion、vm_ledger。
16. Test-Ecosystem 转为 C# 启动器，还是归入保留的生态部分。
17. vci-watch 的发布方式（FBA publish 成 exe 还是独立 csproj），以及进程查询改用 System.Management 还是 P/Invoke。
18. 第 6 条与第 7 条中的行尾策略和单一生成者。
19. 共用库的位置与名称（`build-tools/common`，或放进 build-tools/release 并由 FBA 用 `#:project` 引用 Exe 项目）。
20. CI 时长：source-contracts job 原先只装 Python，以后需要 setup-dotnet 和构建发布工具，要与 P8-01–04 的 CI 改动错开。

**协调**

21. P7-11 合并后（B1/B2 测试目录、B5、B13）再次核对本清单的路径和行数。
22. 阶段 8 其他代码任务也会修改 build-tools/release、release-checks.json 和 validation.md，同一时刻不并行修改这些共享文件。
