# 2026-09-30 本地收尾记录

用户要求在 America/Los_Angeles 时间 16:30 前结束本机工作；随后明确不会在另一台电脑继续开发。本次只提交和备份到本地，不推送 GitHub、不发布、不连接虚拟机、不操作界面或 AutomaticDipCoatingMachine。

## 已完成

- 独立 V20/V21 net48 原生生命周期测试框架：官方要求的程序集启动顺序、单 MTA 线程、自有无界面实例及 scratch 工程，事务提交/回滚、保存/关闭/重开、7 项回读断言和所有权约束下的清理。
- 独立 Python 监督器：显式启用、总期限、异常退出、缺失/不完整报告、未配对日志判失败；结果未知时不重试。超时只终止测试程序，TIA 可能仍运行，需人工检查。
- `Build-Release.ps1` 纳入两版框架编译及离线检查，保留 `liveAcceptance=NOT RUN`。完整源码哈希及验证结果已更新。
- 新增[运行范围及说明](native-lifecycle-tests.md)、[生产工作进程隔离设计](openness-worker-isolation-plan.md)，纠正官方审计中原生框架尚未存在的旧描述。

## 本次已核验

| 验证 | 结果 |
|---|---|
| 新增 net48 框架 | V20/V21 均编译成功，0 警告、0 错误 |
| 框架安全检查 | 每版 25 项，包括双重启用参数、拒绝已有工程/PID 参数、路径约束、未加载 Siemens DLL、MTA |
| 监督器假进程及证据检查 | 22 项，包含超时终止、异常/启动失败、证据缺失/不完整与正常结果 |
| 原有离线套件 | 2,579 通过 |
| PublicAPI 形状检查 | V20 2,833；V21 3,119 |
| 本地混合压力 | 两版合计 8,968 次工具请求及 1,400 项协议/认证检查；仅本机模拟宿主，未接 TIA |
| 其他运行回归、配置器、严格产物校验 | 完整构建通过，详见 manifest |
| 原生 TIA 执行 | **未执行**，不宣称 TIA 稳定性验收通过 |

所有具体输入、哈希、计数见 [release-build.json](../../manifest/release-build.json)。此前的 52,968 次延长压力测试仍是前一轮独立记录，本次没有重复运行，不累计为本次结果。

## 仍未完成

- 生产 MCP host/Openness worker 分进程、跨 MCP 进程协调、统一连接故障失效/恢复状态机。
- 全部原生 getter/service 的对象路径日志、Windows 崩溃事件/转储证据的完整收集流程。
- 新框架的真实 V20/V21 运行及每个 PLC/HMI 工具族的原生验收；目前框架只有最小工程生命周期案例。
- PLC GetCrossReferences 崩溃的准确根因。默认禁用及反射旁路保护继续保留。
- 从零新建 Unified FaceplateType、修改类型内部控件及局部脚本的完整官方 API 路径；当前不能声明已支持。

## 本地保存

工作分支为 `codex/v3.1-audit-stability`。本次提交的精确 SHA 和备份校验值写入本地备份目录的 `README.txt` / `SHA256SUMS.txt`，不在会变更 SHA 的提交内自引用。

备份目录位于 `bin-build/checkpoints/20260930-local-checkpoint/`，归档为 `bin-build/checkpoints/TIA_MCP_Local_Checkpoint_20260930.zip`。其中包含本分支 Git bundle、最新已提交源码 ZIP、本次构建清单对应的两版运行时/配置器、两版原生测试 EXE/配置、验证日志，以及此前已经核验的 3.1.0 候选交付 ZIP。备份不包含 Siemens PublicAPI、虚拟机或用户 TIA 工程，也不复制凭据。

旧候选 ZIP `TIA_MCP_Delivery_v3.1.0_20260930.zip` 保持原样，其源码提交是 `81835b3f1078b821f40093fbfb68f259c237ed43`，SHA-256 为 `b8f4d9f79d59572d82a7c8ed44b6e786d74bf64dc50d553a621078cb35fb1baf`。它不含本次新增框架；新框架的源码和测试 EXE 在本次本地备份中。该备份是工作快照，**不是新的公开发行包**；最新公开版本仍为 3.0.0。
