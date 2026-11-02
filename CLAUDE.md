# 给 AI 编码助手

- 本仓库遵循通用《工程开发手册》v1.1（[docs/standards/engineering-handbook.html](docs/standards/engineering-handbook.html)），采用领域 A/C/D、档位 M（审批与数据安全按 L）；项目自己的选择和偏差见 [docs/standards/README.md](docs/standards/README.md)。新任务说明按手册模板 T.4，完成按手册第 23 节的完成定义；只写主要功能，不做防御性堆砌和无目标的实验。
- 接续开发先读 `docs/development/handoff.md`；范围、任务状态和放行门槛见 `docs/status/release-plan.md`，决定见 `docs/development/decisions/`，逐版缺口见 `docs/reference/openness-coverage.md`。`bin-build/refactor/tasks/followups.md` 只是收件箱，决定当天转写到发布计划或 ADR。机器地址、PID 和旧会话状态从当前环境确认，不从历史文档推断。
- 新代码由 Codex 按任务说明实现（规则见 `AGENTS.md`）；研究、诊断、任务说明、审查、主机测试和合并由 Claude 负责。任务说明写明根因、涉及的文件和函数、预期设计、修改前失败而修改后通过的测试和验证命令；主机检查失败时先定位原因再交回 Codex，不反复重跑完整门禁。
- 工具示例、语言代码、参数来源、结果解释和官方出处统一在 `reference/tool-examples` 维护，由 `GetToolUsage` 按版本返回。兼容入口复用同一数据，不为个别问题新增规则文档。修改示例后先运行 `dotnet run --project build-tools/release -- generate-catalogs` 和 `dotnet run --project build-tools/release -- generate-tool-usage`，再构建并执行八版检索和功能检查。
- 本地 SDK、用户工程、设计交接和密钥不纳入发布。第三方来源和许可证保留；纯逻辑优先复用，共用代码与版本 API 差异分别维护。
- 产品或测试源码变动后按 `docs/development/validation.md` 完成对应构建和检查。合并门是本机 `check-all -Affected` 加受影响的检查；完整冷全集由每日 nightly 运行，失败或超时阻止下一次发布；GitHub CI 不阻塞合并（ADR 0024），发布证据见 ADR 0025。任何检查、断言和阈值只在维护者批准后才能删除、跳过或放宽。文档变动核对链接、路径、工具适用版本和引用；不手工改测试哈希。
- master 是唯一长期分支：任务分支审查并通过主机检查后变基推送到 master，合并后删除。使用明确的 `git add` 路径，不用 `git add -A`。运行二进制不进 Git；英文提交，不添加 AI 署名。
- 发布使用 `dotnet run --project build-tools/release -- release -Version X.Y.Z[-beta.N|-rc.N] -Summary "..." -NoWait`，流程见 `docs/development/release-workflow.md`；每次发布前由维护者确认，发布期间不改代码。发布后更新当前交接及 `manifest/publication-vX.Y.Z.json`，历史变化记录在 CHANGELOG 和 GitHub Releases。
- 构建和发布自动化维护在 `build-tools/release`；仓库检查拒绝新增 PowerShell、batch 和 cmd 文件。审查链可用 `-MaxParallelism 1` 串行执行。
