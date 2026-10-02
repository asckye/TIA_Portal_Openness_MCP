# Openness 生态工具

[文档目录](../README.md) · [接入与验证记录](../development/ecosystem-integration-20260929.md) · [第三方许可](../licenses/THIRD-PARTY-NOTICES.md)

这些功能位于未发布的本地源码中。引擎构建后通过 `FindTools` / `CallTool` 调用；正在运行的旧 MCP 服务不会自动加载新工具。无需操作 TIA 界面。此轮没有连接 VM、修改工程、保存或下载到 PLC。

官方 `tia-portal-ai-extensions` 为优先参考。先调用 `GetAuthoringGuide(topic="openness-workflow")` 了解开发流程，再用 `ReadOpennessGuidance` 阅读对应主题。指南与具体版本手册冲突时，以手册和 PublicAPI 为准；详细差异、修复与缺口见[官方流程审计](../development/official-openness-audit-20260929.md)。`RunToolsInTransaction` 已收紧为其描述中的 8 个同步编辑工具，编译、保存、在线、文件和嵌套编排不能放进该事务。

| 新工具 | 用途 |
|---|---|
| `ReadOpennessGuidance` | 检索/分页阅读 32 份 Siemens 官方指南及随附 Markdown。文档作为资料返回。 |
| `RenderPlcVisualDiff` | 对两个单块 SimaticML 导出做结构比较及 LAD 并排 SVG/HTML 差异；其它语言保留结构比较。 |
| `ReadToolBatch` | 顺序读取最多 50 个明确声明为 READ 的工具，逐项保留失败和未知结果。 |
| `PreviewToolBatch` / `ApplyToolBatch` | 支持原生 dryRun 的 WRITE 工具先预览，签发 10 分钟一次性令牌；应用前重新检查工程/会话/PID和预览结果。 |
| `ManagePlcGitRepository` | 导出/VCI 目录的 status/history/diff/show/stage/commit；提交只包含指定文件。 |
| `RunPlcCompanionTool` | 独立 Python 进程调用 PLC Tools 的全部已发现 CLI：当前 49 个命令/命令组条目。 |
| `GenerateOpcUaModelledInterface` | 从 PLC 或软件单元读取类型、标签、DB，生成用户建模 OPC UA XML；可选文件夹、空 DB 和访问权限过滤。 |
| `AuditEngineeringExports` | 命名、注释、元数据、重复名、网络数以及自定义硬件/库 XML 规则；JSON、HTML、PDF 报告。 |
| `InstantiatePlcXmlTemplates` | 按行展开已验证的 PLC XML 模板，结构化转义替换属性/文本；先验证全部输出，执行时仅新建文件。 |
| `ComposePlcAliasAlarmLad` | V21 布尔别名/简单报警位 LAD FC；普通线圈或置位/确认复位网络。 |

## V21 ecosystem survey and adapters (2026-10-01)

The [dated catalog](../../reference/v21-ecosystem.json) contains 86 assessed entries: 35 official products/examples and 51 third-party entries. It also retains all 144 results from the public GitHub query `"TIA Portal" "V21" in:readme`, with search-only hits explicitly unqualified. Repositories with a renamed product, related forks and separate official manuals can appear as separate entries; these counts are not 86 independent engines. This is a best-effort public survey, not proof that every available tool has been found or that every entry works with V21.

Each entry records its source, version evidence, license metadata, current MCP mappings and remaining gaps. `documented_v21` is stronger than `upstream_targets_v21`; the latter means the upstream README targets or mentions V21, not local acceptance. `not_confirmed`, `adaptation_required` and `unsupported_v21` must not be presented as working V21 tools. `NOASSERTION` is unknown license metadata, not permission to reuse code. A mapping to an existing MCP tool is a functional overlap, not a claim that an entire upstream project was ported. No candidate skills, installers, Add-Ins or native engines were automatically installed or run.

| Added MCP tool | Actual implementation and limits |
|---|---|
| `ReadV21EcosystemCatalog` | Search and paginate the bundled catalog by query and official/third-party source. Reports version evidence, license links, tool mappings and integration status. Does not search the network itself. |
| `ValidatePlcXmlSchemas` | Validate recognized interface and LAD/FBD/SCL/STL/GRAPH fragments against exact matching local PublicAPI XSDs. No DTDs, network schema resolution, missing-schema pass or schema redistribution. `fragmentSchemasPassed` is the validation result; `success` only says the checks ran. Whole-document validation and native import remain unverified. |
| `DecodePlcSimaticMl` | Pinned MIT SimaticML decoder for one V21 FC/FB export, returning readable logic and metadata. Analysis only: the output is not recompilable or reimportable, and unknown instructions/warnings are explicit. Python 3.11+ is required; the existing companion environment is sufficient. V20 documents and S7DCL input are refused; GRAPH/STL semantic translation is unsupported. |
| `ManageUnifiedCwcPackage` | Inspect a custom web control folder, validate basic identity/start/local-reference rules, and build a new `{GUID}.zip` from the inspected content snapshot. Apply requires the matching fingerprint. Does not overwrite output files, execute JavaScript, deploy to TIA, validate the complete manifest contract or certify runtime behavior. CWC is distinct from a Unified faceplate/library type. |

Examples through `CallTool` in the default lite profile:

```json
{"name":"ReadV21EcosystemCatalog","argumentsJson":"{\"source\":\"official\",\"offset\":0,\"limit\":50}"}
{"name":"ValidatePlcXmlSchemas","argumentsJson":"{\"filePath\":\"C:\\\\Exports\\\\FB_Pump.xml\",\"schemaDirectory\":\"C:\\\\PublicAPI\\\\V21\\\\Schemas\"}"}
{"name":"DecodePlcSimaticMl","argumentsJson":"{\"filePath\":\"C:\\\\Exports\\\\FB_Pump.xml\"}"}
{"name":"ManageUnifiedCwcPackage","argumentsJson":"{\"directory\":\"C:\\\\Controls\\\\MyControl\",\"action\":\"inspect\"}"}
```

For CWC creation, pass `action="build"`, `dryRun=false`, the returned `packageFingerprint` as `expectedFingerprint`, and a new absolute `outputPath` using `suggestedFileName` outside the source folder. Inspect the returned validation limits before a separate deployment. A filesystem failure can leave a partial new ZIP; verify `written` and the returned hash, and do not treat a failed call as a valid package.

### Official sources first

| Source | Value for this project | Current status |
|---|---|---|
| [Siemens AI extensions](https://github.com/siemens/tia-portal-ai-extensions) | Official Openness workflow, diagnostics and coding guidance | Refreshed to `b5c7041648dc10f9225ef082306ade6c335b8771`. Upstream moved its skills folder; all 32 guide bodies are unchanged. Local lookup retains its stable layout and now reads pinned provenance from the manifest. |
| [Openness Explorer 2.0](https://cache.industry.siemens.com/dl/files/816/109760816/att_1351237/v1/109760816_TiaOpennessExplorer_DOC_V2_0_en.pdf) | V21 object-model inspection for diagnosing supported attributes/services | External diagnostic aid; not installed or exposed as unrestricted reflection writes. |
| [Project Check 3.2.0](https://cache.industry.siemens.com/dl/files/418/109741418/att_1358048/v1/109741418_ProjectCheck_Intro_DOC_V3.2.0_en.pdf) and [Test Suite V21 Update 1](https://cache.industry.siemens.com/dl/files/057/109990057/att_1356678/v1/Readme_TestSuite_V21_Update_1.pdf) | Quality checks and repeatable application tests | Existing typed Test Suite tools and export audit cover part. Installation, licenses, matching PLCSIM and native acceptance are still required; Project Check has not been embedded. |
| [Modular Application Creator use cases](https://github.com/siemens/modular-application-creator-use-cases) | Official MIT module examples and MAC 21.0.5+ command-line project generation | High-value next integration candidate. Requires installed MAC/Module Builder and module/template inputs. No MAC runner or generated project is claimed in this change. |
| [OPC UA modelled-interface Add-In](https://github.com/tia-portal-applications/tia-addin-opc-ua-modelled-interface) and [SiOME plugin](https://github.com/tia-portal-applications/SiOME-Base-Plugin) | Model generation and NodeSet workflows | The generator is already adapted; SiOME remains external. Official generator main targets V21+, with a separate older-version branch. |
| [V21 CWC manifest](https://docs.tia.siemens.cloud/r/en-us/v21/programming-custom-web-controls-rt-unified/contract-based-interaction-and-the-manifest-file-rt-unified/manifest-structure-rt-unified) and [ZIP rules](https://docs.tia.siemens.cloud/r/en-us/v21/programming-custom-web-controls-rt-unified/creating-the-zip-file-rt-unified) | Documented custom-control packaging contract | New independent package helper; no official example control code was copied. Older example repositories do not themselves establish V21 compatibility. |
| [V21 XML export/import](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-scl-blocks) | Early structural checking before any native import | New fragment validator uses the supplied schema filenames and namespaces; Siemens XSDs remain outside the distributable source. |
| [Official TIA application repositories](https://github.com/tia-portal-applications) | ShowScripts, Unified library generation, synoptics, VS Code documentation, CWC tables/gauges, alarm/runtime/UMC utilities | All 17 non-profile repositories in the observed 19-repository organization inventory are catalogued. Older Add-Ins require V21 API adaptation; remaining utilities are candidates, not newly integrated tools. |

V21 changed the Openness assembly structure, so the existence of an older Add-In is insufficient compatibility evidence. Use [the V21 API changes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness) and installed PublicAPI to decide whether a port is required. Additional catalog entries cover VCI, CAx/AutomationML, Automation Tool, Automation Compare Tool, DCC and Unified Industrial Edge examples with their own prerequisites.

### Third-party candidates and outstanding gaps

| Source/group | Outcome |
|---|---|
| [simaticml-decoder](https://github.com/Czarnak/simaticml-decoder) | Added pinned MIT source and a bounded subprocess adapter. Upstream native-format/corpus qualification is incomplete; test fixture corpus is not distributed. |
| [BlockParam](https://github.com/Sawascwoolf/BlockParam) | V20/V21 DB start-value workflow. Existing scalar patch/verified-import tools cover part; UDT pattern rules, bulk rule tiers and its UI have not been added. MIT Add-In and licensed Pro tier are distinguished. |
| [tia-linter](https://github.com/Thomas-Schlangen/tia-linter), [plc-block-scanner](https://github.com/Czarnak/plc-block-scanner), [tia-todo](https://github.com/Czarnak/tia-todo) | Existing audits, metrics and annotation scanning overlap. The current linter README claims a live V21 run; this is upstream evidence, not our acceptance, and GPL source was not copied. |
| [totally-integrated-claude](https://github.com/Czarnak/totally-integrated-claude), [TiaHelpFast](https://github.com/shobert-rhs/TiaHelpFast) | Domain knowledge and installed-help search are useful reference candidates. No third-party skills or Siemens help corpus were installed/copied. |
| [TIA Unified Exporter](https://github.com/coobi7/TIA-Unified-Exporter), [code-agent](https://github.com/industrix-com-br/tia-portal-code-agent), VS Code bridges and CWC projects | Export, browsing and custom-control candidates are catalogued separately. No evidence found here that fixes the observed Unified script-library `Name` crash. |
| [TiaCommander](https://github.com/a4webdev/tiacommander-mcp) | Documented V17–V20 support, with V19 tested; do not list its V21 selector as working V21 engineering support. |
| [rung](https://github.com/sa1ntsinner/rung), [tia-openness](https://github.com/renanlido/tia-openness), [AnyAutomationStudio](https://github.com/StaniB88/AnyAutomationStudio) | Mixed-license/prerelease or commercial engines remain external. A public client repository does not provide the licensed engine. |
| [XRef Exporter](https://github.com/WZAutomation/TIA_Portal_XRef_Exporter) | Catalogued only. Its existence does not resolve our observed native cross-reference crash; default protection remains in force and offline reference analysis remains available. |

The remaining MCP forks, CLIs, archive tools, hardware utilities, Safety/CEM workflows and CWC projects are listed individually in the catalog. They are not silently installed or declared fully compatible. The next native work should prioritize official Test Suite/Project Check acceptance and a version-specific MAC adapter with real installed prerequisites, followed by missing start-value rule coverage. Library/faceplate creation, internal control editing and the Unified script-library rename incident remain separate unresolved native tasks.

Validation: both runtimes built successfully with 486 tools (lite 62), 2,703 offline checks and 17,936 local pressure calls across direct/isolated hosts. The new adapters passed 75 actual MCP checks per version, across HTTP/STDIO and full/lite profiles, using synthetic XML, actual local Siemens XSDs and ZIP inspection. Selected upstream decoder parsing/folding/emission/input-policy tests passed 107 checks; two symlink tests were skipped because the Windows account could not create symbolic links. Exact hashes and boundaries are in [the V21 ecosystem validation record](../../manifest/v21-ecosystem-validation-20261001.json). These results do not certify native TIA stability. No VM connection, native import, CWC deployment, push or release was performed.

## Python 伴随工具

需要 Python 3.12+，执行 [Install-PlcTools.ps1](../../scripts/ecosystem/Install-PlcTools.ps1)。默认环境在被 Git 忽略的 `TiaMcp_Output/ecosystem-python`，不会更改系统 Python。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ecosystem/Install-PlcTools.ps1
```

引擎在仓库内可自动发现默认环境。部署到别处时，在 MCP 服务进程环境配置 `TIA_MCP_REPOSITORY_ROOT`（包含 scripts、reference、tools/third-party 的根目录）和 `TIA_MCP_PLC_TOOLS_PYTHON`（环境中 python.exe 的绝对路径）。保留整个伴随源码目录及许可证。

先调用 `RunPlcCompanionTool(workingDirectory, mode="catalog")` 获取精确命令/选项；`mode="help", argumentsJson=["code","lint"]` 查看某个命令帮助。`mode="run"` 默认仍为预览，只有 `dryRun=false` 执行。

| 模块 | 接入的功能 |
|---|---|
| code | 项目配置/状态、SCL 检查、格式检查、内存布局、转译 Python、测试/覆盖率、文档/网页、语义差异、离线引用、安全边界规则、draw.io、参数表、PDF/Markdown、变量追踪 |
| iol | 项目配置、I/O 列表、标签 XML/Excel 导入导出、比较、验证 |
| net | 工业网络监视、OPC UA 包分析；抓包需要本机可用的 pcap/Npcap 提供程序 |
| sim | OPC UA 浏览/读写与测试场景；使用 core 和 modbus 库；可选本地 Web UI |
| sup | OPC UA 到 Redis/PostgreSQL/HTTP 的监督与集成测试 |
| trace | 从 UDT 生成跟踪文件、设备跟踪状态/启停/抓取 |

这些是 CLI 调用入口。code 的测试可以执行项目 Python；sim/sup/trace 可操作设备，net 可抓包。执行前需明确配置目标。服务不会自动选择设备、开始抓包或向 PLC 写值。每次命令有 1–300 秒超时，输出各限 1 MiB并报告截断；超时会结束子进程树，已发生的文件/设备操作不回滚。长期 web/monitor 可直接使用安装环境中的 CLI 运行。

上游 `code export pdf` 使用 pandoc/XeLaTeX/Eisvogel，需要另外配置；本项目的 `AuditEngineeringExports(..., reportPath="...pdf")` 使用 ReportLab，无需 TeX。来源工具的“测试通过”不等于 TIA 编译或真实 PLC 行为通过。

## PLC editing and offline references (local 3.1.0 candidate)

The following additions implement gaps identified during the TiaCommander comparison using this project's code and Siemens' documented interfaces. No TiaCommander source or binaries are incorporated. They are available through `FindTools` and `CallTool` after installing the newly built engine; an already running service does not reload them automatically.

| Tool | Implemented scope |
|---|---|
| `ReadPlcBlockEditCapabilities` | Inspect one exported SimaticML block, or export an exact live block. Returns each network's own language/source kind, existing multilingual entries, member start values, library binding and a document fingerprint. |
| `AnalyzePlcReferences` | Offline queries over explicit SimaticML `CallInfo` and global `Symbol` records: summary, callers, callees, OB-root call paths, unreachable candidates and symbol references. Cycles, ambiguous names, omitted callees, unindexed files and pagination are explicit. |
| `PatchPlcBlockDocument` | Create a new XML file with selected existing block/network titles or comments, or existing scalar member start values changed. Exact culture, old value and input fingerprint are required. Unrelated XML is retained. |
| `ImportPlcBlockVerified` | Preview an overwrite of one existing standard PLC block, retain its export, preserve omitted scalar block attributes and attributes of matching interface members, and return the exact planned document and a token. Applying rechecks project/session/target/current content, imports into the original group and re-exports for comparison. |

This is a bounded initial editing workflow, not a complete LAD/FBD rung or SCL statement editor. The patcher does not create missing multilingual entries, guess array/default-value representations or rewrite wiring. Capability inspection reports these boundaries instead of advertising edits that have no implementation.

For a document-only workflow:

1. Call `ReadPlcBlockEditCapabilities(filePath="C:\\Temp\\FB_Pump.xml")` and read its actual culture/member targets and `data.documentFingerprint`.
2. Pass that fingerprint to `PatchPlcBlockDocument`. For example, `changesJson` can contain `[{"action":"setNetworkText","networkIndex":0,"field":"Title","culture":"zh-CN","expectedValue":"Old title","value":"New title"}]`. All values must match the inspected document. The default is a preview; `dryRun=false` writes only the new `outputPath`.
3. Preview `ImportPlcBlockVerified` with an exact `softwarePath`, `blockPath`, edited `importPath` and absolute `evidenceDirectory`. Review `planned.xml` and `preservedOmittedBlockAttributes` before applying the returned token with the same arguments.

`compileAfterImport=false` is the default. Set it to true in both preview and apply only when compilation of the imported block is wanted. An inconsistent block cannot be re-exported; without requested compilation this remains an unverified failure, never a successful content check. Compilation errors stop the workflow. No project save or download is performed.

The import tool refuses library-connected blocks, instance DBs, Safety/unknown code languages, changed name/kind/number/layout/language and replacement of nonempty logic with an empty block. Other missing interface members or networks in a full candidate document still mean deletion: this is an overwrite, not a general merge. Omitted attributes on matched members are preserved; explicitly supplied values remain the caller's proposed changes. The original export is retained for diagnosis, but its restorability is not asserted and no automatic rollback follows a native failure.

Readback comparison retains namespaces, wiring connections, member attributes, literal GUIDs and literal timestamps. It normalizes object/network identifier numbering and structural formatting, and excludes only the documented comparison layer's top-level export metadata and block modification/compile/creation dates. A mismatch or unavailable export reports unsuccessful verification and `mayHaveChanged=true`. A match proves only the compared exported document, not PLC runtime behavior, external references or saved-project persistence.

The offline reference tool indexes single-block XML exports from one PLC/scope. Text-only `.scl`/`.s7dcl` inputs are reported unindexed. It does not invoke the crash-prone native PLC cross-reference service. It cannot establish whole-project reference coverage, infer read/write direction from arbitrary pin names, or prove that a block is safe to delete; `coverageComplete`, `dataComplete` and `safeToDelete` remain false.

Native V20/V21 execution of the new overwrite workflow remains pending. Local tests exercise XML preservation, changed wiring/literals, stale preview rejection, project identity changes, mismatching readback and stopping immediately after write/compile failure. They do not prove TIA crash safety.

Validation on 2026-10-01: the full V20/V21 build passed 2,682 offline checks. Both versions passed 4,484 local tool calls in direct mode and another 4,484 in isolated mode (17,936 total). `scripts/checks/Test-PlcEditingMcp.py` additionally passed 88 checks per version across STDIO/HTTP, full/lite and direct/isolated hosts, including actual output files, stale-input refusal, existing-output protection, partial reference coverage, and disconnected-import refusal. These protocol checks use synthetic XML and never attach to TIA. Exact evidence and runtime hashes are in [the supplement record](../../manifest/plc-editing-supplement-20261001.json). Native overwrite and compilation remain untested.

Official references: [block export options and loss of library type connections during import](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-blocks), [interface/member XML contract](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section).

## 批量调用

预览输入示例格式：

```json
[{"name":"某个具有 dryRun 的 WRITE 工具","arguments":{"softwarePath":"PLC_1","dryRun":true}}]
```

令牌绑定服务器保存的精确有序操作。申请令牌必须指定 `expectedProject`；应用时不能更换参数。失败、未知结果、工程切换或预览变化会停止执行，后续条目标记 skipped。先前成功的操作可能保留；这不是原子事务。原生预览没有覆盖的内部状态无法由令牌检测，其他客户端仍可能在调用之间修改工程。需要原生事务时使用已有 `RunToolsInTransaction` 的限定能力。

单个服务器进程的工具调用串行执行，降低并发进入 Openness 的风险。不同端口的服务器不共享这个锁，应分别绑定不同 TIA 进程。日志默认在 `%LOCALAPPDATA%/TiaMcp/diagnostics`，也可配置 `TIA_MCP_DIAGNOSTICS_DIRECTORY`；按调用 ID记录 BEFORE/RETURNED/THREW，不记录参数、代码、认证头。RETURNED 只代表调用返回，并不等于业务成功。

## 生成与质量规则

OPC UA 生成器沿用官方目前支持的优化节点和字符串 ID。Safety 区域只允许排除或只读；嵌套 FB 的内部成员访问仍是上游限制。输出 XML 必须另行用 `ImportOpcUaInterface` 导入并编译，此轮未作原生导入验收。参数、节点跳过警告、输出哈希会返回。

布尔生成器按 `source` / `destination` 的明确全局符号分量数组处理，不能猜测标签类型。指定 `acknowledge` 时采用“先置位、再复位”，同时为真时**复位优先**，确认是电平触发；它不替代报警系统或安全功能设计。复杂网络用原生导出模板展开。所有新生成器均不自动导入、保存或下载。

质量审计的 `success` 仅表示执行完成；`qualityPassed` 才是规则结果，`dataComplete` 表示所选输入/规则是否完整处理。硬件、库等使用明确 XML 规则，例如：

```json
[{"id":"required-version","files":"Library.*[.]xml$","xpath":"//*[local-name()='Version']","minCount":1,"valuePattern":"^1[.]","severity":"error"}]
```

匹配不到文件的规则返回 notEvaluated，不算通过。默认检查不是完整 Siemens 编程风格认证，也不判断设备安全性。离线引用只覆盖输入导出，不能代替完整原生交叉引用。`GetCrossReferences` 的默认禁用保护继续有效。

## 八个推荐项目的对应关系

- [Siemens 官方指南](https://github.com/siemens/tia-portal-ai-extensions)：本地检索、并发和诊断措施。
- [Czarnak MCP](https://github.com/Czarnak/tia-portal-mcp)：独立实现批量读、预览/应用协议；复用现有参数预检、分页和结果护栏。
- [Czarnak Git Add-In](https://github.com/Czarnak/tia-git-addin)：MIT Core 的解析/结构差异/LAD 布局，加本项目 HTML 与 Git 适配器；未复制桌面 UI。上游尚未提供的 FBD/HMI 图形差异不列为已实现。
- [PLC Tools](https://github.com/core-engineering/siemens-plc-tools)：八个包及全部 CLI入口，固定提交源码随仓库保留。
- [Siemens OPC UA Add-In](https://github.com/tia-portal-applications/tia-addin-opc-ua-modelled-interface)：源代码生成阶段适配为无界面工具；导入复用已有接口。
- [TiaUtilities](https://github.com/Parozzz/TiaUtilities)：按功能独立实现别名/报警/模板批量生成，复用 XML 导入导出和版本管理；不移植 WinForms 表格编辑器或执行其 JavaScript 自定义代码。图形操作的逐步撤销/重做未做成新的 MCP 工具。
- [Repsay 客户端](https://github.com/Repsay/tia-openness-api-client)：工程创建/打开/保存/编译，设备/PLC/HMI，块/实例 DB，库/主副本等已有原生 MCP 工具覆盖；无需再叠加第二个 Python Openness 连接层。
- [tia-linter](https://github.com/Thomas-Schlangen/tia-linter)：按功能独立实现导出审计、规则与报告，SCL/调用结构检查由现有分析器和 PLC Tools 提供；不复制 GPL 源码；上游当前 README 已声称完成一次 V21 真机运行，但不能替代本项目的原生验收。

“所有功能”不等于所有第三方桌面 UI 或路线图项目已经重写。上述未支持项保留为明确缺口；特别是从零创建 Unified 面板类型、修改类型内部控件/局部脚本，仍没有在本项目实证可用的公开 Openness 实现，见[面板验证记录](../development/faceplate-probe-20260929.md)。
