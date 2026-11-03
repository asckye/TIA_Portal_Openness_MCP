# 0017 Openness 用户组检查改用自有代码，不再分发 Siemens.Collaboration.Net

- 状态：已采纳
- 日期：2026-10-18
- 决策者：维护者
- 置信度：高
- 相关：[ADR 0009](0009-port-v20-v21-tools-to-foundation-adapters.md)；[P8-04 引擎退役设计](../design/p8-04-engine-retirement.md)；[第三方声明](../../licenses/THIRD-PARTY-NOTICES.md)；[重构计划](../refactor-plan.md) P8-76、P8-04l

## 背景

PlcWorker 用 `Siemens.Collaboration.Net` 的六个程序集检查和修复当前用户的 `Siemens TIA Openness` 组成员资格，并以目标码随包分发。这些程序集的包内许可证不允许本项目这样再分发。V20/V21 引擎也携带同样六个程序集。

## 选项

1. 继续分发：取得书面许可前不能发布 4.0。
2. 安装时从 NuGet 下载：用户机器须联网，安装流程变复杂，许可问题仍须核实。
3. 改用自有代码：依据公开的 Windows API 和 Openness 用户组要求实现，不反编译、反汇编或反射这些程序集。

## 决定

采用选项 3。八版 PlcWorker 改用自有 Windows 代码检查和修复组成员资格。

- 就绪以当前登录令牌为准，与旧行为一致。本机组的直接成员记录（按 SID）只作诊断：两者不一致或查询失败时写一条 stderr 提示，结果不变。
- 只有既有的 `EnsureOpennessUserGroup` / `group.fix` 修复路径可以提权，并且须经原有确认。修复未生效时按原因返回五种结果：`OPENNESS_GROUP_ELEVATION_DECLINED`、`OPENNESS_GROUP_MISSING`、`OPENNESS_GROUP_RELOGON_REQUIRED`、`OPENNESS_GROUP_ADDED_RELOGON_REQUIRED`、`OPENNESS_GROUP_ADD_FAILED`。成功时仍返回 `Openness user group OK`。

## 后果

- 正面：PlcWorker 和 FoundationHost 不再携带受限程序集。失败原因可以区分，用户能看出该重新登录、安装 Openness，还是找管理员。
- 负面与取舍：修复失败时的输出与旧版不同。直接成员记录不展开嵌套组和域组，因此只作诊断，不决定就绪。
- 后续工作：P8-04l 删除引擎时一并删除其携带的六个程序集；删除前不能发布 4.0。UAC 提示和重新登录后的生效情况在最终真机验收中确认。
- 验证方式：`build-plc-workers` 的输出清单和 `validate-bundle` 拒绝 PlcWorker 与 FoundationHost 中的这些程序集、引用和资源；`diagnostic-membership` 与 `release-tool` 套件。
