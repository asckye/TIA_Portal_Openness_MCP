# 给 Claude

@AGENTS.md

以上共用规则之外，Claude 负责：

- 研究、诊断、任务说明、审查、主机测试和合并；新代码交给 Codex。任务说明写明根因、涉及的文件和函数、预期设计、修改前失败而修改后通过的测试和验证命令。Codex 每次交回后先由独立的 AI 审查一遍再合并。主机检查失败时先定位原因再交回 Codex；修复后先重跑失败的作业，通过后再跑完整门禁。
- 接续开发先读 `docs/plan/progress.md`。`bin-build/refactor/tasks/followups.md` 只是收件箱，决定当天转写到开发计划或 ADR。
- 主机合并门为 `merge-check`，接手沙箱未运行的套件和构建；发布前 `prepare` 做完整检查，没有 nightly，GitHub CI 不阻塞合并（ADR 0031）。
- master 是唯一长期分支：任务分支审查并通过主机检查后变基推送到 master，合并后删除。使用明确的 `git add` 路径，不用 `git add -A`。
- 发布前提前运行 `dotnet run --project build-tools/release -- prepare -Version X.Y.Z-beta.N`；维护者确认后 `publish-prepared` 公开，`upload-draft` 重试上传；两者用 `dotnet run --no-build`，见 `docs/manuals/release.md`。发布期间不改代码；之后更新 `docs/plan/progress.md`、CHANGELOG 和 GitHub Releases。主机检查可用 `-MaxParallelism 1` 串行执行。
