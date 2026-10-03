# 测试迁移设计（重构 P1-05、P1-06）

[重构计划](refactor-plan.md) · [引擎拆分设计](engine-decomposition.md) · [验证分层](validation.md)

## 现状

`tools/tiaportal-mcp/tests` 下的回归测试原为控制台程序，用自写的 `Check()` 并由 `dotnet run` 执行。
任务 1–4 已将下表中的回归工程包装为 xunit 并接入 TRX 门禁；其余控制台框架仍须显式执行。Studio 测试已经使用 xunit。

| 类别 | 工程 | 运行方式 |
|---|---|---|
| 已迁移到 xunit | `TiaMcpServer.Tests`（并入 ImportSelection，3096 项）、`TiaMcpServer.VersionPolicyTests`（10 项） | CI 与 `Build-Release.ps1` 使用 TRX 最低数量门禁 |
| 已迁移到 xunit | `TiaMcpServer.LegacyHostTests`（并入 BindingSnapshot 69、ExternalSourcePlan 84、ExternalSourceDelete 46 项；无 PublicAPI 时 6521 项，带 PublicAPI 时 7566 项，均 1 项跳过） | CI 跑 foundation，`Build-MultiVersion.ps1 -Test` 跑 foundation-api 并记录计数 |
| 已恢复并原地迁移到 xunit | PromptRegistration（含两个夹具，134 项）、SoftwareRead（39）、SpecialExportShape（11）、DeviceAdd（124）、HardwareCatalog（56）、DiagnosticMembership（4） | CI 的 Windows foundation-transport 作业与 `Build-MultiVersion.ps1 -Test` 使用各自 TRX 门禁 |
| 保留控制台 | `tests/TiaMcp.Adapters.DiagnosticsTests` | 仅手动运行 |
| 测试框架，保持控制台 | `TiaMcpServer.HttpTests`（加载织入后的 V20/V21 程序，13 种模式由构建脚本解析 `COMPLETE: N`）、`NativeTests`、`DiagnosticsTests`+`FakeApi`、`ApiCompileChecks`、三个传输/端点夹具 | 构建脚本与检查脚本 |
| 第 A 步已删除 | 未接线的预览协议及其测试 | 适用规则由 `worker-channel` 套件覆盖，见[最终规则核对](adapter-merge.md#第-a-步最终规则核对) |

## 决定

- **HttpTests 保持加载织入后的发布程序**，不改为编译期引用；字符串反射在引擎拆分步骤 3 由 `EngineSurface`
  查找辅助统一处理。
- **先包装、后拆分**：共享 `Xunit.props`（与 Studio 相同的 xunit 2.9.2、runner 2.8.2、Test SDK 17.12.0）和
  `CheckSuite` 适配器，把现有套件按 `Program.cs` 的顺序作为一个有序 theory 执行，每项检查是一条结果，合计与
  控制台一致；测试并行关闭（套件会修改静态状态与环境变量）。之后按套件分批拆成测试类，每类计数不得减少。
  与 HttpTests 共享编译的 `HmiScreenTraversalTests.cs` 不转换。
- **门禁读取 trx 并对照最低数量表**：新增 `scripts/checks/Test-DotnetSuites.py` 与 `tools/tiaportal-mcp/tests/test-suites.json`，
  运行 `dotnet test --logger trx`，失败、低于下限、跳过超限或缺少 trx 均失败，输出 `COMPLETE: N … passed`，
  `release-build.json` 字段不变。门禁切换后，`Program.Main` 只保留子进程夹具入口，普通 `dotnet run` 以非零退出。
- **控制台测试框架加保护**：`Harness.props` 设 `IsTestProject=false`，直接对其运行 `dotnet test` 时报错；仓库检查
  要求 `tests` 下每个工程都已分类，每个 xunit 工程都在最低数量表中。
- **保留 `TiaMcpServer.TransportFixture`**：它是 Foundation 协议 2 的行 JSON 替身，由 CI 与
  `Build-MultiVersion` 使用；第 A 步删除了无生产调用方的两个预览夹具。
- **适配器诊断测试位于 `tests/TiaMcp.Adapters.DiagnosticsTests/`**，保留程序集名 `Diagnostics.Tests` 以沿用 `InternalsVisibleTo`。
- **名称必须与西门子类型一致的测试替身保留**（生产代码按完整类型名识别），集中到一个文件并加允许名单检查。

## 任务顺序

M 可机械执行，J 需要设计判断。每步保持 L0/L1 通过且最低数量不降；改动生产代码的步骤另需 L2、L4 零差异与织入
调用点集合不变。

1. **M** `Xunit.props`、`CheckSuite` 与 `TiaMcpServer.Tests` 的有序 theory；门禁仍用控制台（两种编译符号均为 3086）。
2. **J** `Test-DotnetSuites.py`、最低数量表与自检；`Build-Release.ps1`、`offline-checks.yml`、文档切换（offline、offline-v20、version-policy）。
3. **M，已完成** LegacyHostTests 按原顺序包装（foundation、foundation-api），动态跳过由共享适配器写入 TRX；ApiMetadata 独立 theory 在缺少 PublicAPI 环境变量时于发现阶段跳过，foundation 门禁过滤该 theory，保留原有 1 项跳过预算。CI 与 `Build-MultiVersion` 已切换。
4. **M，已完成** 四个纯逻辑工程并入已有门禁工程并删除原工程，其余六个原地迁移并接入 CI/构建门禁。PromptRegistration 原基线为 131 项成功检查后清理失败；仅修复测试夹具的引用生命周期与卸载等待，新增 2 项卸载检查和 1 项清理检查，现为 134 项。未改动生产逻辑。
5. **M** 合并按西门子名称命名的替身，增加允许名单检查。
6. **M** 将适配器诊断测试移入 `tests/TiaMcp.Adapters.DiagnosticsTests/`。
7. **M** VersionPolicy 包装函数改为接收两个策略参数，去掉测试中的 `partial class McpServer`。
8. **J** `Responses.cs` 移入 `TiaMcp.Logic`，测试引用 MCP SDK，删除伪造的 DTO 与属性（与 P2-01 衔接）。
9. **J** 脚手架操作的 `IScaffoldBoundary` 接缝。
10. **J**（引擎步骤 2）ToolBridge 夹具改为测试用 `[McpServerToolType]` 类，`ToolCatalog` 接收显式类型列表。
11. **M**（引擎步骤 3）`EngineSurface`，删除 `HmiAdapter.cs`。
12. **J**（引擎步骤 4 后，步骤 14 开头）用 `FakeEngineeringSession` 替换 partial `Portal` 测试夹具。
13. **M** 持续：按套件拆分测试类、表驱动套件改为数据行。

基础版的伪 SDK 测试组（SoftwareRead、SpecialExportShape、DeviceAdd、HardwareCatalog、DiagnosticMembership）的真正接缝
属于 P4-01/P4-02 的适配器契约，在此之前原地改为 xunit 并记为例外。
