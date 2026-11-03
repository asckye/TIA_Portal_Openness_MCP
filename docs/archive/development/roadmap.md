# 路线图与待办（v4.0.0-beta.3）

状态：现行 · 最后核对：2026-11-01，master `27675208`

[发布计划](../../plan/plan.md) · [当前交接](../../plan/progress.md) · [重构计划](refactor-plan.md) · [设计规范](design-spec/README.md) · [架构决策记录](../../standards/decisions/README.md)

本页只写方向。按版本的范围、任务状态和放行门槛见[发布计划](../../plan/plan.md)，当前源码事实见[当前交接](../../plan/progress.md)。

## 4.0：可靠的八版统一产品

八个版本使用 FoundationHost 加每会话一个 PLC worker，除工具集合和官方 API 差异外共用一套逻辑与流程（[ADR 0022](../../standards/decisions/0022-engine-retirement-and-eight-release-unification.md)），工具数量按各版官方 API 决定（[ADR 0014](../../standards/decisions/0014-tool-counts-follow-official-api.md)）。4.0 剩下的方向：

- 工作台按已采用的 EplanMcp 工作台设计系统重新设计（Apple HIG 基础，强调色 #0071E3，Segoe UI 加 Microsoft YaHei UI），随 beta.3 落地。
- 真机可靠性优先：修完 beta.2 真机测试的发现（会话稳健性、一项功能一个实现、每个写入可预览与回滚、统一参数模型与可诊断的错误、大结果分页），每批修复在 VM 上复测（[ADR 0020](../../standards/decisions/0020-prereleases-tested-by-users.md)）。
- 补齐对照 T-IA 与生态调研中值得做的小缺口，每项先核对官方 API。
- 收尾引擎残留、八版统一呈现、发布链改 C#，让流程本身更简单：合并看受影响的检查，完整检查放到 nightly，发布只核对、打包和上传（[ADR 0024](../../standards/decisions/0024-ci-does-not-block-merges.md)、[ADR 0025](../../standards/decisions/0025-release-evidence-nightly-and-verified-build-units.md)）。
- 文档按已批准的方案重组，计划、决定和现状各有一个位置。

4.0 正式版的条件见 [ADR 0023](../../standards/decisions/0023-release-4-0-final-criteria.md)。离线通过不证明原生行为；未在真实 TIA Portal 上验证的项在发布说明中列出。

## 4.1 及以后

- 工程生成的其余部分：库与母版实例化、标准检查、工作台生成模块、状态模型包（P8-31f–i）。
- 补充工作流：表格与 ECAD 导入、Classic VB 到 Unified JS、V20/V21 补深度、按版本输出的构建工具（P8-40）。
- 对照 T-IA 与生态调研的大项：块接口编辑、硬件缺口、异步作业、离线快照、代码规则检查、单元测试、CI 入口等。
- EplanMCP 借鉴的加固项（P8-95a–g），托盘与开机启动，非 Studio 命名空间统一，MCP 协议迁移到无状态修订（在 .NET SDK 支持之后）。
