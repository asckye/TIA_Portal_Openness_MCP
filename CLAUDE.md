# 给 AI 编码助手

- 新增或修改原生工具时，把匹配版本的官方示例转成调用 AI 可读取的指引：`GetToolUsage` 覆盖所有版本实际注册的工具，工具描述与 `ToolExamples` 给准确参数，`GetAuthoringGuide` / `GetRecipe` 给调用顺序、官方来源和真实验收范围；不要只写在项目文档里。对已报告的崩溃，预检说明原生读取与 `dryRun` 的实际语义，断连后先保留诊断，不自动重放。通过真实 MCP STDIO/HTTP 检查这些信息在 full/lite 下可见；指引不能冒充原生验收通过。官方示例目录变更后运行 `scripts/generate/Generate-ToolUsage.py`；新工具必须通过 `Audit-ToolUsage.py` 的八版本实际调用覆盖检查。

- 继续"官方 Openness API 全量对齐"计划前先读 `docs/development/handoff.md`（现状、下一步、每阶段固定动作、发布闸门、真机验证约定、只在真机上学到的 API 事实）；逐版本历史在 `docs/development/handoff-history.md`——新发布后 handoff §1 只改现状表，条目加到 history 顶部。
- 换了机器接手：先读 `docs/development/handoff-checklist.md`（新机器准备、虚拟机现状、部署后按序要做的事、真机批跑工具 `scripts/diagnostics/campaign/`）。
- 提交信息、PR、Release 正文不加任何 AI 署名行（`Co-Authored-By`、"Generated with …"）。
- 不要 `git add -A`；引擎或测试源码改动后必须重跑 `scripts/build/Build-Release.ps1` 再提交清单（见 handoff §4）。二进制（`runtime/v20`、`runtime/v21`、`TiaMcpConfigurator.exe`）不入库，clone 后先跑 Build-Release 才有。
- 发版走 `scripts/build/Release.ps1 -Version X.Y.Z -Summary "…"`（先手写 CHANGELOG 顶部条目与 `docs/releases/vX.md`；脚本自己做版本号、Build-Release、闸门、一次提交、打包、推送、CI、tag、从本机上传 ZIP、等验证工作流）。按维护者 2026-10-02 最新要求只维护 master 分支；原 v17–v21 分支历史保存在删除前的完整 Git bundle，范围、提交和恢复说明见 `docs/reference/version-branches.md`，不得将历史分支的版本号当作已验证的支持能力；英文文档不含中文；提交说明英文。
