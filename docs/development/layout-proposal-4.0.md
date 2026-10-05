# 4.0 目录整理方案

[当前结构](repository-layout.md) · [路线图](roadmap.md) · [重构计划](refactor-plan.md)

维护者已确认（2026-10-05）。本方案描述目标结构与执行方式，不在 D333 及冻结分支（P4-I0d、P4-I2、P4-I3、P4-I4a、P4-I4b、P6-02）合并前移动文件。
4.0 不兼容 3.x，交付包内路径可以一次改到位；与 P6-36～39（入口改名、资源布局、产品标识、日志与工作区归位）同批完成。

## 目标结构

| 目标位置 | 现在位置 | 说明 |
|---|---|---|
| `src/Engine` | `tools/tiaportal-mcp/src/TiaMcpServer` | V20/V21 完整引擎 |
| `src/FoundationHost` | `tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost` | V14 SP1–V19 基础宿主；程序集与 EXE 改名按 P6-36 |
| `src/Worker` | `tools/tiaportal-mcp/src/TiaMcpServer.PlcWorker` | 按版本编译的 net48 工作进程 |
| `src/Logic`、`src/Runtime`、`src/WorkerChannel` | `tools/tiaportal-mcp/src/TiaMcp.Logic` 等 | 纯逻辑、运行通道、进程通道库 |
| `src/Adapters`、`src/Adapters.Contracts` | `tools/tiaportal-mcp/src/TiaMcp.Adapters*` | 八版适配器及契约 |
| `src/Shared` | `tools/openness-shared` | 链接进多个程序集的共享源码与 props |
| `src/Studio/*` | `tools/tia-openness-studio/src/*` | Gui、Core、Client、Contracts、Bridge、Openness、Launcher |
| `tests/Engine/*`、`tests/Studio/*` | `tools/tiaportal-mcp/tests/*`、`tools/tia-openness-studio/tests/*` | `test-suites.json` 随之移到 `tests/` |
| `third_party/*` | `tools/third-party/*`、`tools/tia-openness-studio/upstream*` | 上游源码、许可证与来源记录集中存放 |
| `build-tools/native-call-weaver` | `tools/native-call-weaver` | 仅构建期工具 |
| `scripts/operations/vci-watch` | `tools/vci-watch` | 独立运维脚本 |
| `plugin/skill` | `tools/tiaportal-mcp/skill` | Claude Code 插件技能；`.claude-plugin/plugin.json` 同步 |
| `docs/development/evidence` | `docs/development/*-evidence.json`、`legacy-*.json` | 机器审计与历史证据归档 |

不变：`docs/`、`reference/`、`templates/`、`manifest/`、`scripts/` 其余子目录、`.github/`、`.claude-plugin/`、`hooks/`。
根目录只保留 README、LICENSE、NOTICE、CHANGELOG、AGENTS、CLAUDE、`Version.props` 与两个 `.slnx`。

本地且不入 Git 的目录：`runtime/`（构建输出）、`data/`（运行数据）、`sdk/`（八版 PublicAPI 副本，原根目录 `TIA_V*_PublicAPI`）、
`bin-build/`、`TiaMcp_Output/`。

## 需要同步的引用

- 所有 `.csproj`/`.props`/`.targets` 的 `ProjectReference`、`Compile Include` 链接与 `Directory.Build.*`；两个 `.slnx`。
- `scripts/build`、`scripts/checks`、`scripts/generate` 中的硬编码路径；CI 工作流。
- `BundleLayout.cs` 的开发输出锚点（引擎、Studio、桥接的 `bin` 路径）与 `Check-BundleLayout.py` 资源表。
- `scripts/operations/delivery-files.json`（技能与第三方代码路径）、`plugin.json` 的 `skills`。
- 文档链接、`AGENTS.md`/`CLAUDE.md`、Codex 任务模板、验证文档中的命令。
- 构建记录中的源文件清单由下一次构建重新生成，不手改哈希。

## 执行方式

1. 合并 D333 与全部冻结分支，master 干净。
2. 单独一个提交：`git mv` 迁移加路径更新，不改任何代码逻辑；用“迁移前后编译产物 IL 一致”的现有比对工具确认无行为变化。
3. 完整验证：八版构建、全部测试套件、仓库与包模式校验、发布预演（不发布）。
4. 之后再进入阶段 6 的代码工作，避免目录移动与逻辑改动混在同一批。
