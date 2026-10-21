# 路线图与待办（v3.3.0）

状态：现行 · 最后核对：2026-10-11，任务启动基线 master `578a05ee`

[当前交接](handoff.md) · [重构计划](refactor-plan.md) · [设计规范](design-spec/README.md) · [架构决策](decisions/README.md)

本页说明后续方向。任务及状态只在重构计划维护；已合并实现见当前交接和 Git 历史。4.0 在阶段 7、8 完成后发布，依据 [ADR 0013](decisions/0013-release-4-0-after-phases-7-and-8.md)；我们不做真机验收，4.0 先以 GitHub 预发布交给其他用户测试，见 [ADR 0020](decisions/0020-prereleases-tested-by-users.md)，正式版的放行条件尚待维护者决定。

## 八版统一架构

八版使用 FoundationHost，每个 MCP 会话一个 PLC worker，工具按各版官方 API 注册。35 个能力族（B1–B12）已按[移植方案](phase8-port-plan.md)全部迁入 Foundation 适配器；P8-04j2 起 V20/V21 也使用 PLC worker，交付包不再包含引擎，引擎兼容副本由 P8-04l 删除，见 [ADR 0009](decisions/0009-port-v20-v21-tools-to-foundation-adapters.md)。数量不拉平，见 [ADR 0014](decisions/0014-tool-counts-follow-official-api.md)。

2026-10-18 许可决定：六个 `Siemens.Collaboration.Net` 目标码程序集不得再分发。P8-76 从八版 PlcWorker 移除它们，改用公开 Windows API 查询当前令牌与本机直接组成员、经既有确认路径提权添加当前用户；加入后注销重登才生效。令牌继续决定就绪状态。交付包不再包含这六个程序集：`delivery-files.json` 的 `exclude` 规则对八个发布键都排除它们，`Check-BundleLayout.py` 检查这一点；P8-04j2 起交付包也不再含引擎。因此它们不再阻塞 4.0 交付。剩余的包引用只在引擎兼容副本的工程（`src/Engine/TiaMcp.Engine.V20.csproj`、`TiaMcp.Engine.V21.csproj`）中，随 P8-04l 删除。结果文字与成员资格语义的审查见 [P8-04 设计](design/p8-04-engine-retirement.md)许可行及 `bin-build/refactor/P8-76.result.md`。

## 工作台与工程生成

控制通道提供显示、读取和预填，不能审批、切换审批或启停服务。工程操作页和版本控制页已使用所选 MCP 会话，保留人和 AI 的归属；端到端验收及旧桥接代码清理按[设计](design/workbench-control-and-shared-session.md)继续。

标准包驱动命名、工程结构、硬件、程序、报警和 HMI 规则。机器描述是结构化输入，计划先预览，执行审批一次，逐步使用工具自己的预览核对，并记录运行事实和对象归属。分阶段执行、写栅栏、换绑和记录已实现；界面、标准扩展及最终验收见[生成设计](design/project-generation-framework.md)和[分阶段执行](design/p8-31-staged-generation.md)。

## 检查、文档与补充工作流

自有 Python 按[迁移方案](phase8-python-plan.md)改为 C#，输出逐字节一致；第三方生态包及桥接脚本保留。`check-all` 统一运行全部检查，五分钟目标与功能门禁分别判定，不减少检查。

C#、协议、工作台和文档按设计规范处理偏差。ECAD、设备表格导入、Classic VB 到 Unified JS 等补充工作流仍按工具及目标版本验收。最终真机验收必须包含新开放的旧版动作、V20/V21 移植复验和生成恢复场景；离线通过不替代该证据。
