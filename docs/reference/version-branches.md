# TIA 版本历史与适用范围

2026-10-02，维护者将分支策略更新为只保留 `master`。此前一次性同步的 `v17`–`v21` 不再作为维护分支；其源码与提交历史已保存在清理前的完整 Git bundle。开发分支 `offline/publicapi-validation` 的累计工作在完整构建校验通过后进入主线，`codex/v3.1-audit-stability` 的提交已在原主线中。清理分支不更改既有发布标签或发布附件。

v3.2.0 正式交付包与当前 `master` 提供八版本配置选择、V14 SP1–V19 PLC 基础宿主、V20/V21 完整引擎及八版本 Studio 直接适配器。共用/独有工具见[逐版本矩阵](version-tools.md)。62 工具历史迁移台账保持原合同口径；新增路由和离线测试不替代真实 TIA 验收。

## 原上游分支快照

这些快照于 2026-09-29 从 [原仓库](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP) 一次性同步。以下状态描述对应当时快照，不代表当前主线支持范围。

| 原分支 | 历史提交 | 快照范围 |
|---|---|---|
| v17 | `70758f2b7359021fefb674c099c1c8eddbba44e1` | 上游 PLC Software Phase 1，62 工具 profile；该快照未在本次执行真实 TIA 验收 |
| v18 | `5c621e4b3921e8e17a5b1cbe0cb2e268d0d70398` | 历史 v2.2.1，无独立 V18 工程 |
| v19 | `5c621e4b3921e8e17a5b1cbe0cb2e268d0d70398` | 与 v18 相同，无独立 V19 工程 |
| v20 | `b5b0a68b3d8d747fc81473fd46ac7ddd2ed260f3` | 上游历史 v2.3.2；当前 V20 开发使用 master |
| v21 | `af7f7a7f8d6a08cd5eafabfda6b379837c82014e` | 上游历史 v2.5.2；当前 V21 开发使用 master |

来源和同步记录见 [upstream-version-branches.json](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/manifest/upstream-version-branches.json)。旧分支有未进入主线的独立历史，清理时没有将这些旧源码覆盖到当前实现。

## 备份与恢复

维护者持有仓库外的 `TIA_Portal_MCP_before_master_cleanup_20261002.bundle`：93,269,802 字节，SHA-256 `efe1b6b9a9ab8ea920483d7a80ae1a72955aeef59f87371712aff64bad312b50`。`git bundle verify` 已确认包含完整历史，无外部前置提交依赖；其中包括清理前全部已获取分支及标签。备份不包含本地忽略文件、Siemens PublicAPI、未跟踪文件或 GitHub 设置。

其他清理前引用：`master` 为 `fac2b6aab343257bb10af421ed30b1c65f82375f`，`offline/publicapi-validation` 为 `41dcc91ed4e88968cc1bf01e37fa3ff384baf366`，`codex/v3.1-audit-stability` 为 `4350af478836077ec81be8868c81ae905c250a5d`。

恢复历史应在单独目录操作。先将备份文件置于当前目录，然后：

```powershell
git clone --mirror ./TIA_Portal_MCP_before_master_cleanup_20261002.bundle ./TIA_MCP_history.git
git --git-dir=./TIA_MCP_history.git show refs/remotes/origin/v17:README.md
```

镜像仓库保留 bundle 内原始引用，包括 `refs/remotes/origin/v17` 等。需要编辑历史源码时，可从该镜像再创建单独 checkout。不要将历史版本目录中的运行时当作其他 TIA 版本引擎使用。
