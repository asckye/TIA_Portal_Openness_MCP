# 路线图与待办（v3.3.0）

[当前交接](handoff.md) · [功能缺口](../reference/openness-coverage.md) · [版本工具矩阵](../reference/version-tools.md) · [重构计划](refactor-plan.md)

八个版本的 MCP、配置器与直接调用 Openness 的 Studio 已完成当前构建与离线功能检查。
后续工作按实际工程流程和精确版本推进，不再用历史工具数量或 API 词法引用率表示完成度。

## 优先完成真实工程验收

| 工作 | 完成条件 |
|---|---|
| V14 SP1、V15.1、V16–V21 的 PLC 导入/导出 | 在对应版本测试工程中核对实际对象、声明、内容、覆盖行为和错误后的工程状态 |
| 外部源导入与生成 | 核对源名称、编码、生成对象、返回值差异及同名对象行为；随后编译和读回 |
| 编译与保存 | 确认目标 PLC、全部可观察的工程离线状态、嵌套错误/警告；保存后重新打开核对 |
| Studio 的八版本工作流 | 连接、打开、选择 PLC、导入/导出、编译、显式保存；中文/英文使用同一 ViewModel 与命令 |
| VCI | V16–V19 旧接口与 V20/V21 新接口分别验证映射、状态、导入/导出和差异；V14 SP1/V15.1 不提供 VCI |
| 原生会话与故障恢复 | 按独立生命周期及生产 MCP 会话流程验证；结果未知时检查实际工程，不自动重放写入 |

以上新增原生验收均为 **NOT RUN**。编译、模拟对象、HTTP/STDIO 和示例检索通过，只证明各自所测层。
已有 BICO、Unified 库脚本改名及交叉引用问题见[限制说明](../troubleshooting/openness-limitations.md)，
修复判断需要对应调用的实际复测。

## 补齐功能与格式

当前[功能矩阵](../../reference/version-feature-matrix.json)核对 29 个工作流/工具族和八个版本。
它区分 implemented、partial、pending、unavailable 和 unverified；232 个单元格是审计范围，
不是全部 Siemens API 的覆盖率。

1. 历史 62 工具迁移仍缺 `EnsureOpennessUserGroup`、`PlcBuildAndImport`、
   `ImportTechnologyObject`、`ImportTechnologyObjectsFromDirectory`、`SeedProjectFromReference`。
   先确认是否需要独立工具，再按版本 API、现有工具组合及实际使用范围补齐。
2. 扩大监控表、工艺对象、软件单元等流程前，先补齐各版本的可执行路径与返回契约。
   API 签名存在不等于当前工具已开放，也不等于目标设备支持。
3. UDT/GlobalDB 声明已可选择八种输出版本；其余 XML 构造器仍有 V21 格式限制。
   后续应逐格式加入官方 XSD、完整文件及实际导入验证，不能只改版本头。
4. Foundation 的普通工程流程已实现；LocalSession、多用户锁/提交、受保护工程认证等
   仍有独立范围限制。绑定快照的生命周期接线尚未完成，见[版本框架](unified-version-framework.md)。
5. 所有新增工具、动作和编程语言示例进入同一 `GetToolUsage` 数据源，并检查实际检索结果。

## 复用与去重原则

MCP 与 Studio 共享不依赖 Siemens 版本的算法、路径/环境信息和诊断基础设施；
Studio 保持直接 Openness 调用。各版本的原生入口、会话所有权、线程与数据契约不能仅因名称相同合并。

已有上游代码和授权记录见[生态参考](../reference/ecosystem-tools.md)与第三方许可证。
评估新项目时先对照现有实现，复用真正缺少且许可兼容的组件，避免再维护同功能的第二套工具。

## 待修工具描述

完整引擎 `CreateVersionControlWorkspace` 与 `GetVersionControlWorkspaces` 的编译
Description 仍写 “Requires TIA V21+”，RequireVci 缺服务错误也写 V21 起步；但当前 V20/V21 工具目录和实现均有相应 VCI 路线。
现行使用指南已按实际版本说明。后续应核对两版真实 SDK 的具体工作区操作后修正源代码 Description 和对应错误文本，
重新完成完整构建并生成工具清单/能力矩阵；本次文档清理不手改生成文件或已发布二进制。
此项是工具提示的版本文案问题，不代表已经完成 V20/V21 原生 VCI 往返验收。

## 维护方式

当前待办留在本页；已完成的版本历史写入 CHANGELOG 和发布说明。构建结果以生成的 manifest 为准，
不再另建日期检查点、旧机器交接或重复的逐候选验收页。官方示例、来源证据和机器审计记录继续保留。
