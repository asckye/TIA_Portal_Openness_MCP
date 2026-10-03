# 给 AI 编码助手

- 接续开发先读 `docs/development/handoff.md`；当前缺口见 `docs/development/roadmap.md` 和 `docs/reference/openness-coverage.md`。机器地址、PID 和旧会话状态从当前环境确认，不从历史文档推断。
- 工具示例、语言代码、参数来源、结果解释和官方出处统一在 `reference/tool-examples` 维护，由 `GetToolUsage` 按版本返回。兼容入口复用同一数据，不为个别问题新增规则文档。修改示例后生成嵌入目录并执行八版检索和功能检查。
- 本地 SDK、用户工程、设计交接和密钥不纳入发布。第三方来源和许可证保留；纯逻辑优先复用，共用代码与版本 API 差异分别维护。
- 引擎或测试源码变动后按 `docs/development/validation.md` 完成对应构建及全部版本检查。文档变动核对链接、路径、工具适用版本和引用；不手工改测试哈希。
- 仅维护 master。使用明确的 `git add` 路径，不用 `git add -A`。运行二进制不进 Git；英文提交，不添加 AI 署名。
- 发布使用 `scripts/build/Release.ps1 -Version X.Y.Z -Summary "..."`，流程见 `docs/development/release-workflow.md`。发布后更新当前交接及 `manifest/publication-vX.Y.Z.json`，历史变化记录在 CHANGELOG 和 GitHub Releases。
