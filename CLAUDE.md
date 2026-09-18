# 给 AI 编码助手

- 继续"官方 Openness API 全量对齐"计划前先读 `docs/development/handoff.md`（现状、下一步、每阶段固定动作、发布闸门、真机验证约定、只在真机上学到的 API 事实）。
- 提交信息、PR、Release 正文不加任何 AI 署名行（`Co-Authored-By`、"Generated with …"）。
- 不要 `git add -A`；引擎或测试源码改动后必须重跑 `scripts/build/Build-Release.ps1` 再提交清单（见 handoff §4）。
