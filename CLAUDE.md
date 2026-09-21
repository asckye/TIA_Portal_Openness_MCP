# 给 AI 编码助手

- 继续"官方 Openness API 全量对齐"计划前先读 `docs/development/handoff.md`（现状、下一步、每阶段固定动作、发布闸门、真机验证约定、只在真机上学到的 API 事实）；逐版本历史在 `docs/development/handoff-history.md`——新发布后 handoff §1 只改现状表，条目加到 history 顶部。
- 换了机器接手：先读 `docs/development/handoff-checklist.md`（新机器准备、虚拟机现状、部署后按序要做的事、真机批跑工具 `scripts/diagnostics/campaign/`）。
- 提交信息、PR、Release 正文不加任何 AI 署名行（`Co-Authored-By`、"Generated with …"）。
- 不要 `git add -A`；引擎或测试源码改动后必须重跑 `scripts/build/Build-Release.ps1` 再提交清单（见 handoff §4）。二进制（`runtime/v20`、`runtime/v21`、`TiaMcpConfigurator.exe`）不入库，clone 后先跑 Build-Release 才有。
- 发版走 `scripts/build/Release.ps1 -Version X.Y.Z -Summary "…"`（先手写 CHANGELOG 顶部条目与 `docs/releases/vX.md`；脚本自己做版本号、Build-Release、闸门、一次提交、打包、推送、CI、tag、从本机上传 ZIP、等验证工作流）。仓库只有 master 分支；英文文档不含中文；提交说明英文。
