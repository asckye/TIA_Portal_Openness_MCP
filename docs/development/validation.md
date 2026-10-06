# 验证分层

[文档目录](../README.md) · [发布流程](release-workflow.md) · [当前交接](handoff.md)

命令从仓库根目录运行。v3.2.0 已完成八个精确 SDK 目标及当前离线功能构建；
新增真实 TIA 工程验收仍为 **NOT RUN**。发布上传、发布后验包和原生验收分别记录。

| 证据 | 位置和用途 |
|---|---|
| V20/V21 完整构建 | [release-build.json](../../manifest/release-build.json)：编译、离线、实际进程、API 形状及诊断记录 |
| 配置器 | [configurator-build.json](../../manifest/configurator-build.json)：源码/文件哈希及界面功能测试 |
| 全版本交付 | [multi-version-build.json](../../manifest/multi-version-build.json)：八版 worker/Studio 适配器、文件与源码哈希 |
| 统一示例 | [tool-usage-coverage.json](../../manifest/tool-usage-coverage.json)：所有注册工具/操作的检索与可执行离线示例 |
| 工具与功能范围 | [版本矩阵](../reference/version-tools.md)及 [功能矩阵](../../reference/version-feature-matrix.json) |

旧日期报告里的“SDK 未提供”“仅 Linux 源码验证”属于历史修订，不覆盖当前构建记录。
机器证据中的历史字段仍保留原意；不要将历史通过数复制成当前测试结果。

## 运行资源包校验

交付内容以 [`delivery-files.json`](../../scripts/operations/delivery-files.json) 为准；规则、打包预览和许可保留见
[发布流程](release-workflow.md#交付清单与两种校验模式)。仓库模式继续验证源码与开发文件；解包模式无需源码，
从仓库运行 `Validate-Bundle.ps1 -BundleRoot <包根> -PackageMode -Strict` 和
`Check-Repository.py --root <包根> --package-mode`。缺少 `Version.props` 也会自动选择包模式。
包模式检查交付资源、运行文件/版本/哈希、三份构建记录、工具数、用户文档和许可证；相对链接悬空即失败。
`runtime/verification/` 的 IL 检查留在正式打包前的仓库阶段，发布资产不携带验证器。

无构建产物的暂存树加 `-NoBinaries` / `--no-binaries` 只证明静态交付布局，版本差距仍会失败；
不能把这种结果记作完整二进制验证。运行规则和资产集合自检：

```powershell
python scripts/checks/Check-BundleLayout.py --self-test
python scripts/checks/Verify-ReleaseAsset.py --self-test
pwsh -NoProfile -File scripts/operations/Update-Engine.ps1 -SelfTest
```

更新器自检只在当前 worktree 的 `bin-build` 中创建离线夹具，覆盖已交付开发文件删除、未知/改写文件保留、
保护目录、路径越界和回滚新增文件清理，不连接网络或真实安装。资产自检用内存 tag 树和本地 ZIP，
检查缺失/多余文件、误带验证器、二进制哈希、sidecar 和 tag 内容优先于 checkout。

## 重定位与只读安装检查

在 Windows 上对已提取的候选包运行下面的脚本。它将整包复制到仓库外、带空格和中文的临时路径，
用临时只读 ACL 检查根目录，再通过 STDIO `tools/list` 检查六个 Foundation 发布键和 V20/V21 引擎的
工具数量。Foundation 宿主使用 `--offline`；该检查不调用 TIA 工具。它还验证根启动器指向随包 Studio、
无效显式 `--bundle-root` 不回退、缺少所选引擎会被拒绝，以及更新入口拒绝 source checkout。

`SaveProject` 审批探针以 apply 确认请求验证默认开启的审批在没有工作台时于派发前拒绝，并核对只读安装下
主日志、审批配置锁和审批审计分别进入 P6-39 的用户回退路径。探针会在当前用户的 config/audit 回退目录
留下正常锁文件与审批审计行；若已有配置明确关闭审批，脚本保留该用户设置并将此项报告为未验证。
需安装 .NET 10 SDK 与 .NET Framework 4.8 或更高版本。临时只读 ACL 会在退出时移除，复制目录默认清理；
传入 `--keep-relocation` 可保留复制件供审查。

```powershell
python scripts/checks/Test-RelocatedBundle.py --self-test
python scripts/checks/Test-RelocatedBundle.py --bundle-root <extracted-bundle-root>
```

## 一次构建全部开发工程

使用 Windows 和 .NET 10 SDK（支持 .slnx、Studio WPF 及 .NET Framework 目标）。
带条件引用的 .NET Framework 工程默认设置 `UseReferenceAssemblyPackage=true`，通过 NuGet
取得编译所需的引用程序集，与发布脚本和已提交的 `packages.lock.json` 保持一致。八版适配器、PlcWorker 和
API 编译检查均以 net48 为目标，无需额外安装 4.8 targeting pack。
已安装对应 targeting pack 时仍可显式指定 `-p:UseReferenceAssemblyPackage=false`；这会改变还原依赖图，
因此日常方案构建使用默认值，不应提交该覆盖产生的 lock 文件变化。

```powershell
dotnet build TiaPortalOpenness.Offline.slnx -c Release
dotnet test TiaPortalOpenness.Offline.slnx -c Release --no-build
dotnet build TiaPortalOpenness.slnx -c Release
# 验证离线工程不依赖约定的 PublicAPI 文件夹，无需移动本机 SDK：
dotnet build TiaPortalOpenness.Offline.slnx -c Release -p:TiaPublicApiRoot=Z:/nonexistent-public-api
```

Offline 指不需要 Siemens 程序集；NuGet 依赖仍须已缓存或可还原，桌面工程仍须 Windows。
它包含 offline-checks CI 的全部工程及其依赖，也收录其他不依赖 Siemens 的工具、协议和测试。
`dotnet test` 执行 Studio xunit 及已迁移的 MCP 回归工程；正式验收使用下面的 TRX 门禁核对最低数量。
未迁移的控制台测试（包括 `TiaOpenness.Configuration.Tests`）仍须用
`dotnet run --project <csproj> -c Release`。HttpTests、DiagnosticsTests 和 transport 夹具
需由对应验证脚本提供参数；PromptRegistration 的两个依赖夹具不执行测试，直接 `dotnet test` 会明确拒绝。

完整方案默认使用仓库内不入 Git 的 `sdk/` 目录（不存在时为仓库根目录）下的 `TIA_V14SP1_PublicAPI/V14 SP1`、
`TIA_V15.1_PublicAPI/V15.1`、`TIA_V16_PublicAPI/V16` 至 `TIA_V20_PublicAPI/V20`、
`TIA_V21_PublicAPI/V21/net48`。可用 `-p:TiaPublicApiRoot=<SDK-root>` 改变父目录；
单工程的 `-p:SiemensEngineeringDirectory=<绝对路径>` 始终优先，不要将同一版本的该参数传给完整方案。
约定文件夹不存在时，引擎/NativeTests 保留已安装 SDK 的原有回退；缺少匹配程序集会报错并列出所需目录。

完整方案排除 `reference/` 示例、`LibraryRenameProbe` 和没有合理默认版本的 `TiaMcpServer.PlcWorker`。
worker 继续由 `Build-PlcAdapterWorkers.ps1` 逐个传入 `TiaReleaseKey` 构建，产物统一位于 `bin/<key>/Release/net48/`；
`ApiCompileChecks` 的独立/方案默认是 V21，现有脚本仍显式选择八版。
V20/V21 引擎分别写入原有 `obj-v20`/`bin-v20` 和 `obj`/`bin`，适配器通过方案依赖先构建织入工具。
解决方案构建只用于开发；下面的脚本验证、打包和发布门禁仍是发布路径，构建不执行原生 TIA 测试。

## 无需运行 TIA 的检查

### 纯 Python 源码契约（Ubuntu CI）

[offline-checks.yml](../../.github/workflows/offline-checks.yml) 的 `source-contracts` job
执行下列静态检查，无需 .NET、Siemens SDK、引擎进程、TIA 或网络服务。
其中 `Check-Repository.py --no-binaries` 同时执行 BundleLayout、SwallowedExceptions、
CommentHygiene、McpText 和 Inventory-ResponseEnvelopes；下表也列出它们的独立复跑命令。

| 命令（仓库根目录） | 守护范围 |
|---|---|
| `python scripts/checks/Check-Repository.py --no-binaries` | 文档、入口与下列五个静态门禁 |
| `python scripts/checks/Check-BundleLayout.py` | 资源代码表、Git 文件集、交付校验清单及 Launcher/GUI 路径一致性 |
| `python scripts/checks/Check-SwallowedExceptions.py` | 吞异常标记及只减不增基线 |
| `python scripts/checks/Check-CommentHygiene.py` | 注释与 Leftovers 基线 |
| `python scripts/checks/Check-McpText.py` | MCP 中文字面量基线 |
| `python scripts/checks/Inventory-ResponseEnvelopes.py` | 手写响应信封基线 |
| `python scripts/checks/Check-DeadToolReferences.py` | 工具描述死引用与重名注册 |
| `python scripts/checks/Test-EngineSources.py` | 成员定位：搬文件、重载、类型、词法边界与缺失/歧义拒绝 |
| `python scripts/checks/Test-DiagnosticMembershipSources.py` | 诊断只读组检查、显式修复与默认不连接 |
| `python scripts/checks/Test-DocumentImportSafetySources.py` | 文档导入前置拒绝、不重试、部分结果与报告证据 |
| `python scripts/checks/Test-ImportSelectionSources.py` | 导入选择、冲突、覆盖与确定性排序 |
| `python scripts/checks/Test-PromptRegistrationSources.py` | 两种传输的显式 prompt 清单与注册失败处理 |
| `python scripts/checks/Test-VersionCatalogWiring.py` | 版本门禁、派发、配置与构建接线 |
| `python scripts/checks/Test-DomainTools.py --source-only` | 所有已迁移领域的工具注册与回归夹具清单一致 |

该 job 还执行 `python scripts/generate/Generate-ToolUsage.py --check`，以及 BundleLayout、
CommentHygiene、McpText、Inventory-ResponseEnvelopes 的 `--self-test`；吞异常自检仍在
`offline-tests` job。`Test-CfcTools.py` 是领域检查的兼容入口，CFC 已包含在上述全领域检查中。

按成员验证的引擎检查使用 [engine_sources.py](../../scripts/checks/engine_sources.py)，
跨 `src/Engine/**/*.cs` 按成员名查找，显式以 UTF-8 读取，
排除生成和构建文件；重载通过签名、所属类型或 MCP 属性区分，缺失或歧义直接失败。
词法器保留完整方法体，不依赖相邻成员或迁移前文件路径。它是源码契约检查，不替代 C# 编译或原生验收。

### 需要本地 .NET 或工作目录的源码检查

下列命令不进入 Python-only job。HMI/技术对象与外部源替身需要 .NET 10；
依赖须已缓存，替身项目只使用本地还原源。它们编译提取的实际方法体并运行托管替身，不连接 TIA。
外部源的工作目录保留生成源码与构建证据；HMI/技术对象在 `bin-build` 创建并清理临时目录。
另外两项仅用本地 MSBuild 求值八版编译常量，无需构建、还原或加载 Siemens，并在 validate-bundle CI 中运行。

```powershell
python scripts/checks/Test-HmiImportSafety.py
python scripts/checks/Test-TechnologyImportSafety.py
python scripts/checks/Test-ExternalSourceDispatch.py --work-dir bin-build/external-source-dispatch
python scripts/checks/Test-SupplementaryReadSources.py
python scripts/checks/Check-TiaFeatures.py
```

### 构建、离线套件与交付检查

```powershell
python scripts/checks/Check-Repository.py
python scripts/checks/Check-DeadToolReferences.py
python scripts/checks/Check-SwallowedExceptions.py --self-test
python scripts/checks/Check-SwallowedExceptions.py
python scripts/checks/Check-CommentHygiene.py --self-test
python scripts/checks/Check-CommentHygiene.py
python scripts/checks/Check-McpText.py --self-test
python scripts/checks/Check-McpText.py
python scripts/checks/Inventory-ResponseEnvelopes.py --self-test
python scripts/checks/Inventory-ResponseEnvelopes.py
python scripts/checks/Check-TiaFeatures.py
python scripts/checks/Check-BundleLayout.py --self-test
python scripts/checks/Check-BundleLayout.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/checks/Validate-Bundle.ps1 -Strict
python scripts/checks/Test-DotnetSuites.py --self-test
python scripts/checks/Test-DotnetSuites.py --suite offline --suite offline-v20 --suite version-policy
python scripts/checks/Test-DotnetSuites.py --suite foundation --suite prompt-registration --suite software-read --suite special-export-shape --suite device-add --suite hardware-catalog --suite diagnostic-membership
dotnet run --project tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Build-Configurator.ps1 -Test
```

仓库检查验证链接、入口和统计；交付检查核对清单、版本、八版运行文件与构建哈希。
`Check-Repository.py` 同时运行纯 Python 的 [BundleLayout 检查](../../scripts/checks/Check-BundleLayout.py)：
读取并校验资源代码表，用 `git ls-files` 验证文件或目录内文件受版本管理，并确认路径在 `Validate-Bundle.ps1`
实际执行的资源清单中；还核对 C# 5 Launcher 的唯一相对候选与 Studio 安装锚点、GUI 输出文件名一致。
不调用 dotnet，也不依赖 `runtime/` 构建产物。布局矩阵通过临时目录分别验证 Logic/引擎调用方
（offline/offline-v20）与 Studio Core/GUI，覆盖安装、开发输出、worktree、CI、runtime-only、嵌套暂存
和中文/空格/尾分隔符路径。解析器不借用祖先仓库的缺失资源，调用方仍保留到 4.0 的兼容探测；
仓库外完整交付包的重定位检查用于排除这种回退掩盖缺文件。实际解析顺序、嵌入生态目录和证据边界见
[运行时布局](runtime-layout.md)。Markdown 本地链接及入口路径统一由
`python scripts/checks/Check-Repository.py --no-binaries` 检查。
`Check-Repository.py` 同时运行纯 Python 的吞异常门禁；offline-checks CI 还运行其自检，不依赖 dotnet。
门禁扫描 `src`、`src/Shared`、`src/Studio`，
排除 `bin`/`obj`（含 `-v20`）、`Generated` 目录、`.g.cs`/`.g.i.cs`/`.generated.cs`/`.designer.cs`
及带 `<auto-generated>` 文件头的文件。
词法检查覆盖全部条件编译分支，忽略注释、字符串文本和字符常量中的伪代码；插值表达式仍参与异常引用检查。
没有代码 token 的 catch 是 empty；非空且没有 `throw`、异常变量引用（含 `when` 过滤器）或已知日志调用的是 discarding。
日志调用识别 `Log*` 的明确方法清单、Console/Debug/Trace 输出、常用 logger 级别方法及仓库的日志辅助，
不把名称中偶然含有 `log` 的方法视作日志。它不证明控制流覆盖或变量绑定，新日志封装需同步检查器与自检。
类别只取显式标记；既有普通注释不豁免，未知类别、空原因和错误位置均失败。
标记和日志策略见[贡献约定](../../.github/CONTRIBUTING.md#repository-conventions)与[P2-03 设计](response-and-errors.md#p2-03-吞异常治理)。

首次基线统计为 535 个文件、1,389 个 catch：205 empty、194 discarding、990 有处理行为；399 个待处理 catch 均未标记。
设计的 218 个空 catch 包含现已删除的预览协议中的 13 个，去除后与 205 完全一致。
194 个 discarding 包含 170 个宽泛 catch 和 24 个指定异常类型的 catch；设计的“约 171”仅估算宽泛 catch，
不能作为本检查器的精确门槛。输出按工程和标记类别分组，未标记项不猜测业务类别。

基线为[swallowed-exceptions-baseline.json](../../scripts/checks/swallowed-exceptions-baseline.json)，
按规范化 try 体、catch 子句（含过滤器）、catch 体的 SHA-256 多重集合比较；路径和行号仅供定位。
只忽略代码空白/注释，字符串内容变化仍改变指纹；搬文件不失败，重复新增或修改未标记 catch 会失败。
消失的条目会报告，运行 `python scripts/checks/Check-SwallowedExceptions.py --update-baseline` 收缩基线；
该命令拒绝任何新增指纹或重复次数增长，即使总数减少。`--allow-growth` 仅用于经审查的首次建基线。
可用 `--root <scratch-copy>` 和 `--baseline <json>` 对临时副本验证；自检在 worktree 的 `bin-build` 创建并清理夹具。

`Check-Repository.py` 也运行 [响应信封清点](../../scripts/checks/Inventory-ResponseEnvelopes.py)。
它扫描 `src` 的全部 C# 条件分支，使用与吞异常检查相同的词法器及生成文件/构建目录排除规则。输出按路径排序的逐文件计数和总数；`--json` 输出同样确定的 JSON。
计数分别列出 `timestamp`/`success`/`ok` 索引器出现次数（含读取）和赋值次数、对象构造、两类异常抛出，以及六种时间编码。
`timestamp_now_assignments` 包含 `DateTime.Now.ToString("O")`；`timestamp_local_datetime` 只计直接赋入的 Local DateTime。
`datetime_utcnow` 计所有 `DateTime.UtcNow` 取值；两个 DateTimeOffset round-trip 计数接受 `o` 和 `O`；
`created_utc_custom` 识别 `createdUtc` 的 `ToString("yyyy-MM-dd HH:mm:ss") + "Z"`。

B1–B9 是可重叠的词法提示，不是工具分类的精确总数：B1 为仅 timestamp/success，B2 为仅 timestamp，
B3 为 success 位于第三个或更后面的键；这三种只识别直接的 Local DateTime 初始化器。
B4、B5a/c/d 按 `RunHmiStepTool`、`RunOfflineAnalysisTool`、`BatchResult`、`BuildOfflineXmlBuilderReport`
中的 timestamp/success 初始化器识别；B5b 识别 `RunPlcSimTool` 内的 `RuntimeMeta` 调用；
B6 识别 `*Meta`/`Create` 方法中的 timestamp/success 工厂；B7 识别含 ok、没有 timestamp/success 的初始化器；
B8 识别没有 Meta 且字面消息含失败/拒绝提示的 `ResponseMessage`；B9 识别 timestamp 的 Local round-trip 字符串赋值。
间接赋值、变量中的消息、包装层 B10 和运行时控制流不由这些提示推断。

[响应信封基线](../../scripts/checks/response-envelope-baseline.json)按两项全局总数守护手写赋值：
`timestamp_now_assignments` 245、`success_assignments` 306；路径变化不会改变门禁结果。
中央 `TiaMcpServer.ModelContextProtocol.ResponseMeta` 类型自身仍出现在清点中，但不计作手写调用点；此识别与路径无关。
任一总数增长即失败，减少允许且报告；`--update-baseline` 仅收缩上限，首次经审查建立基线才使用 `--allow-growth`。

`ResponseMetaTests` 在 offline/offline-v20 各增加 249 项 E2 检查。每种构造器与注明文件/行号的原初始化器
在同一固定时钟下比较四条序列化路径的 UTF-8 字节；测试的选项与 POCO 替身另对照 HttpTests/Golden 中
V20/V21 的全部 36 项 E1 序列化记录。覆盖键追加、success 原位覆盖、null Meta、Local/UTC、数值 CLR 类型及异步时钟隔离。
`ResponseClock.Pin` 仅在当前执行上下文生效，可嵌套并在释放时恢复；测试进程临时固定 +08:00 时区并恢复 BCL 缓存。
`Complete(meta)` 保留 HMI 的 operationSuccess，显式 bool 重载用于无条件完成；`Failed(meta)` 只追加
operationSuccess/apiCallSuccess/dataComplete，异常文本、状态和 success 的家族差异由原调用点保留。
本阶段构造器尚未接入现有调用点，POCO 和引擎返回保持原样。

已迁移的套件使用 xunit，每个原有 Check 对应一条结果；
[最低数量表](../../tests/test-suites.json)要求 offline、offline-v20、version-policy 至少 4420、4420、10 项通过，均不允许跳过。
两种 offline 套件的 Linux 下限均为 4419（少一项仅 Windows 可运行的 apartment 检查）。
ImportSelection 的 10 项已并入 offline；BindingSnapshot、ExternalSourcePlan、ExternalSourceDelete 的 199 项已并入 foundation。
foundation 至少 6521 项通过、最多 1 项跳过；foundation-api 至少 7566 项通过、最多 1 项跳过。PromptRegistration、SoftwareRead、SpecialExportShape、DeviceAdd、HardwareCatalog、DiagnosticMembership 分别要求 134、39、11、124、56、4 项通过，均不允许跳过。
门禁按请求顺序执行（两种编译符号共享输出目录），拒绝失败、数量不足、超限跳过、零执行及缺失或不一致的 trx；
每次清除同名旧结果，在 `test-results/` 写入 `<suite>.trx` 和按测试类/方法统计的 `<suite>.json`，失败时也保存诊断。
可用 `--results-directory <dir>`、`--dotnet <path>`、`--no-restore` 或重复的 `--dotnet-arg=<arg>` 定制执行。
开发时可直接运行 `dotnet test <csproj> -c Release`（V20 加 `-p:DefineConstants=TIA_V20`），正式门禁仍用脚本核对数量。
普通 `dotnet run` 对已迁移工程返回 2；离线工程保留 `--local-process-fixture` 子进程入口。
共享 [Harness.props](../../tests/Engine/Shared/Harness.props)已用于 PromptRegistration 的两个依赖夹具；其他未迁移框架保持原运行方式。
PromptRegistration 的既有 Windows 清理失败已仅在测试中修复：释放夹具引用、卸载上下文并通过有界 GC 等待确认，再删除临时目录；卸载或删除失败均记录为失败检查。

ApiMetadata 是独立 theory；未设置 `TIA_MCP_TEST_PUBLIC_API_ROOT` 时在发现阶段跳过，foundation 门禁通过过滤器排除它，从而保持原有 1 项跳过预算。
运行 foundation-api 前设置该变量为八版 PublicAPI 的父目录，并先构建当前仓库的八版 adapter/worker；适配器根目录由测试源码所在的仓库布局推导，无需命令行路径。
`Build-MultiVersion.ps1 -Test` 运行 foundation-api 和上述六个独立套件，将通过、失败、跳过、总数及门禁阈值写入 `multi-version-build.json` 的 `validation.dotnetSuites`。

未迁移的控制台用例仍必须用 `dotnet run` 执行。WPF 检查包括统一主窗口的导航、共同设置、退出清理与渲染；配置模块在实际 .NET 10 桌面宿主执行，保留至少 157 项检查。`Build-Configurator.ps1 -Test` 需要 .NET 10 SDK；Framework csc 仅编译兼容启动器，配置测试通过后生成同格式的 `configurator-build.json`。测试使用隔离配置和模拟 HTTP，覆盖客户端配置、
合并/备份、密钥处理与界面渲染，不修改真实客户端配置或系统网络规则。

GitHub 的 offline-checks 与 validate-bundle 执行相应离线检查。push/PR CI 不比对构建记录中的源码哈希；
交付检查使用 `-Strict -NoBinaries -SkipSourceHashes`，仍核对必需文件、JSON、版本及 delivery.json 绑定的构建记录哈希。
发布流程重新生成构建记录，再由 `Validate-Bundle.ps1 -Strict`、`Package-Release.py` 和发布后验包完整验证源码哈希。
托管 runner 没有 Siemens PublicAPI，不能替代本机完整构建。修改编译输入后必须在发布前重新构建，不能手填 manifest 哈希。

### 注释与 MCP 中文门禁

`Check-Repository.py` 同时运行纯 Python 的 [Check-CommentHygiene.py](../../scripts/checks/Check-CommentHygiene.py)
和 [Check-McpText.py](../../scripts/checks/Check-McpText.py)，无需构建、TIA 或网络。两者导入吞异常检查器的 C# 词法器，
扫描全部条件分支，沿用生成文件、构建目录的排除规则；各自的 `--self-test` 覆盖词法边界、
计数、搬文件、重复新增、替换与只减不增更新。注释检查也收集插值表达式和预处理指令尾部的实际注释。

注释类别可重叠，以“物理注释行 × 类别”为单位；同一行多个注释合并指纹。产品版本覆盖 `2.x`/`3.x` 数字和通配写法，
排除可识别的原生固件/模型版本、手册章节和编号步骤；未识别的版本语义仍需人工判断。
`maintainer`/维护者提及保守计为归属候选；`moved to` 等明确迁移措辞计为墓碑。
注释代码只计能识别为完整语句的行（调用、赋值、声明、return/throw 等），不把孤立括号、循环头和含代码示例的叙述计入。
工具数识别静态 roster 候选，排除可识别的第三方客户端上限；它不能自动证明数字已过时。
`*Leftovers*` 是文件数预算，其余类别按规范化注释内容比较 SHA-256 多重集合。

P2-05a 初始清点为 **564 个文件、348 个类别条目**。与[设计统计](runtime-layout.md#注释与-leftovers)的对照：

| 类别 | 检查器 | 设计 | 口径说明 |
|---|---:|---:|---|
| 产品版本注释 | 266 | 引擎 221、Logic 33 | 引擎 224、Logic 33、Studio 9；引擎比数字三段版本的 221 多 4 行 `2.7.x`，排除 1 行原生 `V3.0.0` 示例 |
| 旧 Phase/sub-batch | 33 | 32 | 包含大小写不敏感的行内引用；设计未提供逐行基线，当前词法口径为准 |
| 维护者归属 | 13 | 13 | 所有归属/提及候选，未推断语义 |
| 迁移墓碑 | 19 | 20 | 只计明确迁移措辞；不计原生 API 的迁移说明或单纯 removed 注释，设计无逐条名单可作一一对应 |
| 注释代码 | 5 | 约 9 | 完整语句启发式；例如已注释 foreach 的头与花括号不计，块中的调用计 1 行 |
| 工具数 | 6 | 4 | 引擎 4、Logic 2；包括 CliOptions 的 lite 数和 ParameterVocabulary 的描述数 |
| Leftovers 文件 | 6 | 6 | 引擎 5、Logic 1；按文件而非行数比较 |

设计另列的 48 处“中文修复叙事”是人工改写范围，不是本任务要求的独立机器类别。

中文门禁扫描上述源码根中的引擎、Logic、LegacyHost、worker、共享层及适配器，排除 Studio 的 Gui/Client/Launcher。
优先标记 `Description` 属性、异常构造参数、`Message`/`error` 赋值及 `meta`/`ResponseMeta` 值；
其余中文字符串作为 `other-literal` 保守守护，包含局部变量、常量、辅助调用参数和间接返回文本。
这不是跨方法数据流分析，不能把 `other-literal` 数解释为已证明可达的消息数。
字符串里的伪注释仍是字符串；真正注释、字符常量不计；普通、逐字、raw、插值字符串均支持，插值内的字符串单独计一次。
计数解码 `\u`/`\U`/`\x`，包括补充平面的汉字，不把中文标点算作汉字。

P2-05a 为 **526 个文件、316 个受约束字面量、3,909 个 CJK 字符**：

| 类别 | 字面量 | CJK 字符 |
|---|---:|---:|
| description | 17 | 75 |
| exception | 79 | 775 |
| message | 85 | 1,109 |
| meta | 15 | 201 |
| other-literal | 120 | 1,749 |

描述的 17 个字面量对应设计的 6 条工具描述和 11 条参数描述。设计“约 100 条消息”按整条消息估算；
本检查按拼接片段、插值内部字面量分别计数，并包含异常、meta 和间接文本，因此其余 299 个字面量不是 299 条消息。
另有 **416 个数据字面量、6,201 个 CJK 字符**列入
[mcp-text-baseline.json](../../scripts/checks/mcp-text-baseline.json) 的 `allowlist`：每项保留文本、类别、定位和审查理由，
包括 ToolTaxonomy 双语表、EnvironmentDoctor Zh 字段、既有报告/脚本/XML 数据、TIA 输入匹配值与 CLI 输出。
该名单只匹配既有字面量及重复次数，不整文件豁免，也不豁免 description/exception/message/meta。
新增同文副本、新中文或把数据直接放进异常/消息仍会失败。现有中文描述中的示例仍保留在冻结基线中。

两份基线都不把路径/行号纳入指纹；删除允许，内容替换或重复次数增加失败，即使总数减少。
`--update-baseline` 只收缩当前集合，中文 allowlist 同时去掉消失项；`--allow-growth` 必须配合更新，且只允许创建不存在的初始文件。
新增中文数据的豁免需审查具体字面量和理由，不能通过更新命令自动获得。

```powershell
python scripts/checks/Check-CommentHygiene.py --update-baseline
python scripts/checks/Check-McpText.py --update-baseline
# 临时源码副本验证；默认基线来自 <scratch-copy>/scripts/checks：
python scripts/checks/Check-CommentHygiene.py --root <scratch-copy> --baseline scripts/checks/comment-hygiene-baseline.json
python scripts/checks/Check-McpText.py --root <scratch-copy> --baseline scripts/checks/mcp-text-baseline.json
```

在临时副本新增 `// 3.9.99: temporary regression` 或 `throw new InvalidOperationException("新增中文错误");`
应分别报告新 product-version / Chinese exception 并返回 1；只在 worktree 的 `bin-build` 建立及清理此类夹具。
语言和改写规则见[工具开发](tool-development.md#mcp-文案语言)。

### 适配器输入检查

```powershell
pwsh -NoProfile -File src/Adapters/build/Test-AdapterInputs.ps1 -SourceRoot src -PublicApiRoot <SDK-root> -EvidenceDirectory bin-build/adapter-inputs
```

需要八版 PublicAPI；预期 20 项通过、0 项失败（8 个有效 SDK 选择、12 个无效配置）。
脚本只执行 `ValidateAdapterInputs`，不还原、不编译、不启动 worker 或 TIA，并检查每个用例的独立输出路径没有生成文件。
缺少 SDK 目录或核心程序集时，先由 `TiaPublicApi.props` 拒绝；其余配置继续由适配器目标校验。
逐项日志和 `input-results.json` 写入证据目录。本检查手动运行，未接入 CI。
## Foundation 协议 2

`TiaMcp.WorkerChannel` 同时以 net48/net10.0 构建，无 Siemens 引用。其信封使用与 LegacyHost 相同的 STJ 包版本，DTO 编解码保持原样。[规则与预览测试对应表](../../tests/Engine/TiaMcp.WorkerChannel.Tests/README.md)列出保留和不适用的规则。

```powershell
python scripts/checks/Test-DotnetSuites.py --suite worker-channel
dotnet publish src/FoundationHost/TiaMcpServer.LegacyHost.csproj -c Release -o bin-build/foundation-host
dotnet build tests/Engine/TiaMcpServer.TransportFixture/TransportFixture.csproj -c Release
# 如 CI foundation-transport：将 publish 文件复制到 runtime/v14sp1、v15.1、v16、v17、v18、v19，并写入各自 release-key.txt。
python scripts/checks/Test-FoundationTransport.py --fixture tests/Engine/TiaMcpServer.TransportFixture/bin/Release/net10.0/TransportFixture.exe --output bin-build/foundation-transport
```

`worker-channel` 最低 98 项通过、0 跳过，验证 hello 的全部身份字段、双向帧限制、绑定纪元、单次分派、取消、超时、管道故障、迟到进度、未知/重复回复和 ReadFailed。夹具用 `TIA_FIXTURE_FAULT` 注入故障；正常传输另验证六个 STDIO 版本、两个独立 HTTP 会话、nonce 隔离及中文往返。系统 TEMP 受限时可加 `--temp-root <新的 worktree 目录>`，保留可审查的夹具日志。HTTP 只连接该脚本启动的本地模拟服务，不连接 TIA。

构建八版 adapter/worker 后比较每版 weave inventory；WorkerChannel 在织入程序集之外，PlcWorker 本身不织入。worker 离线冒烟以 `--native-session <key> <PublicAPI绝对目录> <64位十六进制nonce>` 启动，先核对 hello（包括实际文件 SHA-256），再以协议 2 调用 `adapter.ReadState` 和空闲 `adapter.Disconnect`。这些调用不连接 TIA；V21 使用拆分后的 `Siemens.Engineering.Base.dll` / Step7。发布文件清单要求 Foundation 宿主及 worker 各自携带 WorkerChannel、STJ 和对应依赖，SDK DLL 不进入 runtime。

P4-E2 仍要求六版 Foundation 响应快照 `changed=0, rawChanged=0`、八版工具契约零差异和 foundation/foundation-api 等既有套件门禁。[真机台账](../reference/real-machine-ledger.md)中的逐版本协议 2 验收在发布前完成；上述离线检查不替代它。

## 发布脚本的日常验证

发布前的低成本门禁不再留到完整引擎构建之后。`validate.yml` 日常运行两种 PowerShell 的仓库包验证、
CHANGELOG 规则、预检和复用自测、并行流水线自测、发布文档断言、原生监督器/MCP 安全自测、崩溃证据、写保护和生产源码 PLC 名称匹配。
`offline-checks.yml` 已覆盖仓库/链接、失效工具引用、BundleLayout/交付集合自测、示例目录及版本目录接线。
CHANGELOG 可以先提交下一版本条目，但必须同时提交对应发布说明；此时 `Version.props`、插件与旧 manifest 保持原发布版本，
`-NoBinaries` 验证旧记录之间的一致性。发布模式和包模式仍要求精确一致。

```powershell
powershell -NoProfile -File scripts/build/Test-ReleasePrerequisites.ps1 -SelfTest
powershell -NoProfile -File scripts/build/Release.ps1 -SelfTest
powershell -NoProfile -File scripts/build/Build-Release.ps1 -SelfTest
powershell -NoProfile -File scripts/build/Run-ReleaseBuild.ps1 -SelfTest
pwsh -NoProfile -File scripts/build/Run-ReleaseBuild.ps1 -DryRun
python scripts/build/Package-MultiVersion.py --self-test
powershell -NoProfile -File scripts/checks/Validate-Bundle.ps1 -SelfTest
pwsh -NoProfile -File scripts/build/Release.ps1 -DocumentationOnly
powershell -NoProfile -File scripts/checks/Test-MatchPlcName.ps1 -SourceOnly
# 需要本机 V21 PublicAPI 和 .NET Framework 4.8 targeting pack；列入日常本机验证，不等待发布。
powershell -NoProfile -File scripts/checks/Test-DownloadRouteSelection.ps1 -SourceOnly -PublicApiDirectory <V21-net48-SDK> -Python <python.exe>
```

两项 `-SourceOnly` 检查通过仓库词法提取器定位当前生产方法并用 .NET 10 的 C# 编译器生成 net48 小夹具，
不复制算法，不修改生产源码，不构建完整引擎，不连接 TIA；路由夹具仍引用真实 PublicAPI，并使用原有假路由对象。
完整构建后还会对实际 V21 程序重跑这两项检查。其余本机二进制/API/传输门禁继续由下文的完整八版本构建运行。

独立早期门禁为 `Release.ps1 -Version X.Y.Z -EarlyGatesOnly -V21ReferenceRoot <V21-net48-SDK> -Python <python.exe>`，
须在版本、文档已机械更新后运行；它不执行预检、版本修改、归档、完整引擎构建或任何 Git 写入/远程操作。
`Validate-Bundle.ps1 -PendingRelease X.Y.Z -Strict -NoBinaries -SkipSourceHashes` 仅供这个阶段使用：
分别验证新源码版本/文档和旧 manifest 内部一致性，不能用于发布产物验证。
预检独立运行用 `Test-ReleasePrerequisites.ps1 -PublicApiRoot <SDK-root> -Offline`，离线模式只接受已有 SHA-512 正确的缓存及显式/环境令牌。
伴随 Python 用 `TIA_MCP_PLC_TOOLS_PYTHON` 指定；构建中的 V21 生态夹具也使用该解释器，避免预检与执行环境不同。

预检、复用、CHANGELOG 和并发自测均报告通过/失败数量；复用覆盖相同输入、改源码、增删源码、改/缺二进制、改 release/fileVersion，
以及旧输出目录的保留移动、准备→完整引擎→完成记录的顺序、准备阶段输入/产物变化拒绝和哈希绑定审计证据恢复。审查链自测覆盖错误顺序、尾点 bundle 路径，以及成功、PowerShell 异常和 native 非零退出时的记录恢复。上述自测均用 Windows PowerShell 5.1 和 PowerShell 7 运行。完整发布的 dry run 和性能比较在干净 master 上执行，步骤与并发资源清单见[发布流程](release-workflow.md)。
沙箱内 Python 3.12 的 `TemporaryDirectory` 可能因私有 ACL 返回 `WinError 5`；原生监督器/MCP 的离线自测遇到该错误应记录为未通过，
由维护者在普通本机环境复跑，不跳过门禁、不进入 live 分支。

## 完整八版本构建

```powershell
pwsh -NoProfile -File scripts/build/Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Python <python.exe> -Test
```

此命令先构建 Foundation worker、Studio、全部适配器及 bundled .NET，执行功能和传输检查，再运行 V20/V21 `Build-Release.ps1`，最后完成八版本审计和记录。release/fileVersion 从 `Version.props` 读取，不依赖旧 `release-build.json`。
`-PrepareOnly -Test` 只保存准备证据；完整引擎完成后用同参数的 `-CompleteOnly -Test` 验证输入与产物未变并完成记录。
只有完整引擎源码、版本、二进制和哈希绑定审计输入均匹配时，才使用 `-SkipFullEngines`；缺失/过期证据明确失败。
维护审查脚本及完整九步顺序见[发布流程](release-workflow.md#离线审查完整构建链)。
PLC Tools 功能检查需要现有伴随 Python 环境；可用 `TIA_MCP_PLC_TOOLS_PYTHON` 指向其解释器。
设置 `TIA_MCP_TEST_PUBLIC_API_ROOT` 可运行八版 UDT/GlobalDB 官方 interface XSD 检查；
片段 XSD 通过不代表完整文档或目标 CPU 语义通过。

完整引擎构建包含以下不同层次：

- 两种版本编译符号下的纯逻辑回归、实际 MCP SDK 版本准入检查。
- HTTP/STDIO、full/lite、资源分页、HMI 遍历、文件代理及普通/隔离宿主功能检查。
- 针对实际匹配 SDK 的类型、属性、方法签名、枚举与服务接口核对。
- 原生调用织入、覆盖清单核对、假 API 实际执行与崩溃/管道故障注入。
- 独立原生测试程序的编译和离线安全检查；默认不会启动其 live 分支。
- 全部工具示例与实际签名一致性、按操作的输入/结果解释、语言文件检索及源文件哈希核对。

官方 API 审计的成员名词法引用只提供排查线索，不证明重载正确、路径可达或原生行为成功。
按版本的实现与缺口以功能矩阵为准。

## 工具契约兼容检查

[重构计划](refactor-plan.md)的当前 V4 基线位于 `manifest/contracts/v4/baseline`，守护八版工具名称、输入 schema
及 V20/V21 lite 名单。3.2.0 的 16 份原始快照已逐字节迁至 [只读归档](../../manifest/history/contracts-v3/README.md)，由 provenance.json 与 Check-Repository.py 校验清单/字节并拒绝旧目录重建；改动 C# 并重新构建运行目录后执行：

```powershell
python scripts/checks/Snapshot-ToolContracts.py capture --repo-root . --harness tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe --output TiaMcp_Output/contracts
python scripts/checks/Snapshot-ToolContracts.py compare --baseline manifest/contracts/v4/baseline --current TiaMcp_Output/contracts
```

删除工具或 lite 条目、删除参数、新增必填、类型/枚举/默认值变化等破坏性变化返回 1；新增工具或可选参数
只报告。基线只检查输入契约，不检查返回结构或原生语义。基础宿主优先加载 `runtime/dotnet` 中的随包运行时；
只用开发输出且本机缺少 ASP.NET Core 10 时，抓取前把 `DOTNET_ROOT`/`DOTNET_ROOT_X64` 指向私有运行时。

返回结构另用当前 V4 的 `manifest/contracts/v4/responses` 守护。先在当前 worktree 依次构建 Release 的 V20、V21
（指定对应 `SiemensEngineeringDirectory`），再构建 HttpTests 和 LegacyHost；`--exe` 指向这些新产物：

```powershell
$engine = 'src/Engine'
$harness = 'tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe'
$legacy = 'src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe'
python scripts/checks/Snapshot-ToolResponses.py capture --repo-root . --public-api-root <SDK-root> --harness $harness --dotnet-root <private-ASP.NET-Core-10-root> --exe "14sp1=$legacy" --exe "15.1=$legacy" --exe "16=$legacy" --exe "17=$legacy" --exe "18=$legacy" --exe "19=$legacy" --exe "20=$engine/bin-v20/Release/net48/TiaMcp.Engine.V20.exe" --exe "21=$engine/bin/Release/net48/TiaMcp.Engine.V21.exe" --temp-root TiaMcp_Output/responses-temp --output TiaMcp_Output/responses
python scripts/checks/Snapshot-ToolResponses.py compare --baseline manifest/contracts/v4/responses --current TiaMcp_Output/responses
```

脚本复用 STDIO 启动器和统一示例检查，每次 capture 在全新进程中连续抓取两轮，一致才写入。
V20 记录 2,012 次不同调用、V21 记录 2,065 次；两版分别对全部 477/488 个广告工具执行 full 直接参数拒绝，以及 lite `CallTool` 桥接拒绝。
输入使用两个仅大小写不同的参数名：`VersionPolicyTool` 在转交内部调用前拒绝，桥接层在参数绑定和 `InvokeToolMethod` 前拒绝；不能以单个未知参数代替，因为桥接会忽略它。
每个原始响应必须带预期拒绝标记。桥接的 476/487 个目标走重复参数诊断，`CallTool` 自身走更早的递归拒绝并单独统计；脚本列明源码依据，不新增可执行目标工具体调用。
原有 18 个纯离线构造/分析示例、9 个 L1 领域断开状态、参数诊断及 V20 的 11 个 V21 专用工具拒绝全部保留；GetToolUsage 检索全部工具及 493/545 个操作示例，不代表执行这些操作。
Foundation 六版分别记录 59/60/62/62/62/64 次调用，59/60/62/62/62/64 个广告工具中 57/58/60/60/60/62 个验证宿主参数拒绝，RenderPlcBlock/RenderPlcProgramAtlas 按现有捕获规则记录跳过原因，并执行纯托管 `InitializeEnvironment`、`RunCapabilitySelfTest`。参数校验位于 worker/构造器调用之前；正常 `GetSessionState` 需要 worker，故只记录拒绝调用，正常调用跳过原因入基线。Foundation 不广告 `CallTool`，逐工具记录桥接跳过原因。`--dotnet-root` 仅为这些宿主设置 `DOTNET_ROOT` 和 `DOTNET_ROOT_X64`；不使用 `--catalog` 或启动 worker。
JSON 对象键排序、数组保序，仅屏蔽脚本列明的生成时间路径。GetToolUsage、超过 16 KiB 的响应及新增 lite 桥接拒绝保存完整规范化响应的 SHA-256、UTF-8 字节数、顶层键与 JSON 类型摘要；消息和错误文本也参与哈希。其他响应保留全文，每个调用一行；V20/V21 每版约 1.5 MB，Foundation 每版约 100 KB。响应上限固定为 2,000,000 字符，不覆盖自动导出分页。
compare 报告每版 changed/added/removed 数及变化调用的首个差异路径；摘要变化报告哈希路径和字节数增减，任何差异（含新增和元数据）返回 1。两次独立 capture 的八版文件逐字节相同。
source-contracts 同时运行 `Snapshot-ToolContracts.py verify` 和 `Snapshot-ToolResponses.py verify`：每类恰好八个发布键文件、release 与文件名一致、名称唯一、full/lite 与 ToolProfiles.resx 一致、Foundation 无 lite，并按现有捕获规则核对响应覆盖和调用记录。此步骤不构建/运行引擎，不扩展快照格式；P6-35 补充行为能力、最终 P6-41 冻结格式和刷新基线。默认 HTTP harness 两版仍在各 16 项检查通过后因 `HttpListener` 的 `PlatformNotSupportedException` 停止。以上均为离线证据，不代表原生 TIA 验收。

## 压力与故障检查

Release 构建中的每个宿主检查都使用该次运行 temp 目录下独立的 `TIA_MCP_DATA_DIRECTORY`，明确写入 `config/approval.settings`，并隔离 diagnostics、`LOCALAPPDATA` 和临时文件。功能检查关闭审批；成功后删除数据根，失败时保留供检查。读取 V4 结果的检查允许关闭审批的写入结果带唯一的 `APPROVAL_DISABLED` warning。每个 Engine 版本及六个 Foundation 发布键还单独运行默认开启审批的检查：无 Workbench 时写入必须以 `CONFIRMATION_REQUIRED` / `workbench-unavailable` 在派发前拒绝，读取仍成功。Foundation V4 冻结目录不包含 `CallTool`，因此该检查记录工具目录证明和直接调用结果，不扩展公共接口。

真实机器 campaign、VM MCP probe、生产 MCP 生命周期和 VCI watcher 在执行前会提示：写入 campaign 期间保持 Workbench 打开以批准调用，或在 MCP 菜单关闭审批；这些客户端不绕过审批。

`Test-LocalStability.py` 对 V20/V21 的普通/隔离进程、两种传输和 full/lite
组合执行正常调用与错误后的恢复检查。默认每组合 50 轮、HTTP 8 并发；
实际范围和结果由完整构建记录保存。

```powershell
python scripts/checks/Test-LocalStability.py --exe runtime/v21/TiaMcp.Engine.V21.exe --major 21 --public-api sdk/TIA_V21_PublicAPI/V21/net48 --host-harness tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe --rounds 50 --output TiaMcp_Output/stability-v21
```

输出目录必须不存在。脚本仅清理它创建的服务，不连接 TIA，不解禁原生交叉引用。
假 worker 的超时、异常退出、错误响应和晚到响应检查，验证宿主故障处理而非 Siemens 内部可靠性。

## 真实工程验收

按[独立生命周期](native-lifecycle-tests.md)和[生产 MCP 会话](native-mcp-session-tests.md)
分别启用原生场景。真实工程需要匹配版本/许可、当前明确的测试目标与操作范围。
从可恢复工程开始，核对设备和版本，执行读取、预览、修改、回读、编译及显式保存，
记录实际错误/警告、未支持对象和失败后的状态。

导入验收应核对对象内容及返回身份，覆盖不允许覆盖、显式覆盖、批次部分完成和依赖顺序；
外部源需区分创建源、生成块与编译。Studio 再验证相应界面命令。在线设备操作单独验收。
HTTP 可达、工具枚举、程序构建或公开发布都不等于工程语义正确。

真实写入边界见[能力说明](../reference/capabilities.md)；尚未解决的原生事件见
[Openness 限制](../troubleshooting/openness-limitations.md)。工程、密钥、现场原始日志及未脱敏截图不进入公开仓库。

## V4 脚本调用与 campaign 输入

CI 使用 `Check-ScriptToolCalls.py` 检查 Python、PowerShell、CMD/BAT、shell 和 JSON 计划中的工具调用；注册工具名来自引擎注册和版本目录。历史映射、冻结的 v3.3.0 写入守卫清单和精确的未知工具负例具有显式例外。`mcp_results.py` 与 `McpResults.ps1` 读取 V4 `ok/data/error/meta`，批次读取 `data.items`；3.x 的 `message/meta.success` 不能作为成功结果。

```powershell
python scripts/checks/Check-ScriptToolCalls.py --self-test
python scripts/checks/Check-ScriptToolCalls.py
python scripts/mcp_results.py
python scripts/checks/Test-ScriptClients.py
python scripts/checks/Test-CampaignInputs.py --exe src/Engine/bin/Release/net48/TiaMcp.Engine.V21.exe --public-api <V21-net48-SDK> --host-harness tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe --major 21 --output bin-build/campaign-inputs
```

最后一个命令用离线 STDIO 目录和引擎的实际 `InputSchema` 校验全部 campaign 输入，不派发计划中的调用；输出目录必须不存在。显式参数拒绝与原生前置条件拒绝分别统计。campaign 的历史 ledger 只作为 V3 证据，工具名索引迁移不代表 V4 VM 验收。

沙箱中可为 `Test-WorkerIsolation.py` 与 `Test-PlcEditingMcp.py` 指定 `--transport stdio`。默认仍检查全部传输；STDIO 结果不能替代 HTTP 故障和父进程退出检查。`Test-EcosystemAssembly.ps1 -SkipPdf -SkipCompanion` 仅用于缺少本地伴随依赖时的部分证明，默认发布检查保留 PDF 与伴随命令覆盖。Git fixture 只验证状态、提交预览和无提交的历史，不执行 Git staging/commit。

原生安全自测的合成夹具使用 `offline_fixtures.py` 在 `bin-build` 下创建唯一目录并继承 worktree 权限，使子进程可读取输入。清理前核对目录父路径；live 分支的入口和执行范围不变。
