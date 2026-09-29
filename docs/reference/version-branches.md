# TIA 版本分支与适用范围

2026-09-29 按维护者要求：先发布 [v3.0.0](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v3.0.0)，再将[原仓库](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP)的五个版本分支同步为本仓库的独立分支。全部提交历史保留，五个分支头均与同步时的上游一致。此次为一次性同步，没有设置定期任务。

| 本仓库分支 | 同步提交 | 状态与使用边界 |
|---|---|---|
| [master](https://github.com/asckye/TIA_Portal_Openness_MCP/tree/master) | v3.0.0 发布基线 `32856cf2a35b` | 当前独立维护主线，完整交付包含 V20/V21；464 个工具，lite 59 |
| [v17](https://github.com/asckye/TIA_Portal_Openness_MCP/tree/v17) | `70758f2b7359` | 上游 V17 PLC Software Phase 1，独立 `TiaMcpServer.V17.csproj`；上游说明的 profile 为 `plc-software-v17-phase1`、62 个工具；本次未本地编译或真机验证 |
| [v18](https://github.com/asckye/TIA_Portal_Openness_MCP/tree/v18) | `5c621e4b3921` | 历史 v2.2.1 快照，未包含 V18 工程；不能据分支名认定支持 V18 |
| [v19](https://github.com/asckye/TIA_Portal_Openness_MCP/tree/v19) | `5c621e4b3921` | 与 v18 指向同一提交，未包含 V19 工程；不能据分支名认定支持 V19 |
| [v20](https://github.com/asckye/TIA_Portal_Openness_MCP/tree/v20) | `b5b0a68b3d8d` | 上游历史 v2.3.2 快照；需要本项目最新 V20 功能时使用 master 的发布包 |
| [v21](https://github.com/asckye/TIA_Portal_Openness_MCP/tree/v21) | `af7f7a7f8d6a` | 上游历史 v2.5.2 快照；需要本项目最新 V21 功能时使用 master 的发布包 |

完整 SHA 与来源记录在 [upstream-version-branches.json](../../manifest/upstream-version-branches.json)。这里的 62 工具及验证说明来自上游 V17 README，不是本次本地验收结果。V17 当前不包含 HMI、PLC 自动下载、在线写值、强制及 V20+ Documents/S7DCL 实做。当前宿主机只有 V20/V21 PublicAPI，因此此次仅验证分支提交一致性。

## 获取和维护

克隆本仓库后执行 `git fetch origin`，然后在干净工作区中用 `git switch --track origin/v17` 选择对应分支（其他版本替换分支名）。回到最新主线使用 `git switch master`。旧分支保留各自的源码布局和构建说明，不能把当前 `runtime/v20` 或 `runtime/v21` 当作 V17/V18/V19 引擎使用。

当前 checkout 已配置名为 `upstream` 的原仓库远端，`origin` 仍是 asckye 的仓库。以后同步前应先比较新提交和 API 差异；已有本地修改的分支不得直接强制覆盖。主线 v3.0.0 的交叉引用保护、事务范围及官方流程修复没有自动回移到这些历史分支。

v3.0.0 发布包本身仍为 V20/V21 完整包；分支同步在其发布及独立验证之后完成，没有修改已发布的标签或附件。
