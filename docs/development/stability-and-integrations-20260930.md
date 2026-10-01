# 稳定性验收与 GitHub 接入优先级（2026-09-30）

**2026-10-01 更新：**本页下方数字保留为 9 月 30 日基线。新候选已加入默认关闭的[生产 worker 隔离](../guides/openness-worker-isolation.md)，现有 476 个工具（lite 61）；构建门分别测试普通与隔离模式。旧文中“当前未实现”描述的是当日状态，完整目标的剩余项见[实施状态表](openness-worker-isolation-plan.md)。当前版本的延长测试记录由新构建重跑更新，不能把旧 EXE 的测试结果沿用到新 EXE。

本轮按用户要求只进行本地测试，不连接虚拟机，不打开或修改 TIA 工程。3.1.0 是本地候选版本。上一轮的八个生态项目接入范围见[既有记录](ecosystem-integration-20260929.md)；本页的候选方向不表示已移植或已通过原生验收。

## 收益排序

| 顺序 | 项目与已核实能力 | 建议接入范围与边界 |
|---|---|---|
| 1 | [Czarnak/tia-portal-mcp](https://github.com/Czarnak/tia-portal-mcp/blob/main/docs/ARCHITECTURE.md)：MCP host 与 net48 Openness worker 分进程；超时、退出或管道异常使工作进程失效，后续请求启动新进程；工程绑定随故障失效 | 我们已借鉴批处理，下一步价值最大的是进程隔离和故障注入测试。保留现有 V20/V21 工具协议，先做传输兼容、工程身份绑定和故障状态机，再迁移原生调用。不得自动重放结果未知的写入；重新连接必须核对原工程。隔离可以保护 MCP host，但不能阻止 Siemens 服务端自身崩溃。当前未实现。 |
| 2 | [siemens/tia-portal-ai-extensions](https://github.com/siemens/tia-portal-ai-extensions)：官方的 threading-and-concurrency、crash-diagnosis、openness-testing 指南 | 已接入指南、调用串行化和日志；下一步将这些规则落实为匹配安装版本的原生工程验收。指南是开发参考，不是能修复 TIA 内部缺陷的运行库。 |
| 3 | [siemens/tia-portal-openness-code-snippets](https://github.com/siemens/tia-portal-openness-code-snippets)：官方 V20 STEP 7 / Startdrive / DCC 示例 | 按工具族核对服务归属和操作顺序，将最小示例改写为有断言、明确工程所有权的验收案例。官方 README 明确示例的测试方法没有断言，不是完整测试套件；其许可也不是通用 MIT，复制代码前需按原许可保留声明。当前只调研。 |
| 4 | [Lorenz-Software/PLCSIM.UnitTest](https://github.com/Lorenz-Software/PLCSIM.UnitTest)：可扩展版本插件、CLI 和 PLCSIM Advanced 测试 | 用于验证导入、编译之后的 PLC 程序行为。README 列出的插件是 TIA V16–V18 与 PLCSIM Advanced 5/6，V20/V21 要另做适配与许可环境验收；它不替代 MCP 故障测试。当前只调研。 |

另一个值得跟踪的经验是 [tia-linter 的连接稳定性说明](https://github.com/Thomas-Schlangen/tia-linter/blob/main/README.md)：作者报告长扫描遇到 Openness 对象数量相关异常，并引入分段重连。我们已实现离线质量审计，后续应优先测量原生长扫描的对象/会话资源，再决定是否引入绑定到同一工程的分段扫描与显式重连。不能据此推断本项目交叉引用崩溃的根因，也不直接照搬自动重连。

## 本地发布门

[Test-LocalStability.py](../../scripts/checks/Test-LocalStability.py) 已加入 [Build-Release.ps1](../../scripts/build/Build-Release.ps1)，每次完整构建对 V20/V21 分别运行：

- STDIO / HTTP × full / lite 四种组合；full 检查 474 个注册项、lite 检查 59 个注册项。
- 每组合 21 类场景，默认 50 轮；另有预热和每轮恢复读取。HTTP 使用 8 个并发客户端，STDIO 串行发送。
- 本地状态、版本、SD 格式检查、日志读取、工具桥接，以及错误类型、缺文件、错误 JSON、未知工具/方法、自递归和 HTTP 未授权请求。
- 交叉引用普通块、变量、软件单元请求均应拒绝，`queried=false`；反射获取服务、短服务后缀和调用对象方法的旁路也必须拒绝。
- 错误后继续 ping / GetState / 资源发现，检查原请求 ID、返回语义、日志调用配对、串行执行次序、子进程存活及资源边界。

每版默认 **4,484 次工具请求**（包含预期拒绝、错误和恢复请求），另有 **700 项协议/认证检查**；两版合计 **8,968 次工具请求、1,400 项协议/认证检查**。测试不把 `isError=false` 当作业务成功；检查实际 `meta` 和桥接内层返回。

结果、测试脚本/运行时/测试宿主的 SHA-256、场景计数、时间、延迟、私有内存及句柄采样写入 [release-build.json](../../manifest/release-build.json) 的 `validation.runtimes.V20/V21.localStability`。主离线套件和真实 PublicAPI 成员检查仍独立保留。

首次校准发现测试宿主跳过生产启动流程后没有设置引擎主版本，导致诊断返回 `engineMajor=0`；已在测试宿主设置显式版本。STDIO 本地检查可在沙箱内运行，本机沙箱中的 HttpListener 启动受限，最终 HTTP 压测在正常本地权限下、仅监听 127.0.0.1 运行。

这些是短时混合负载与错误恢复测试。512 MiB 私有内存上限、128 个句柄增长上限只是本地发布门，并非产品的通用性能承诺。测试宿主加载实际 EXE 的服务方法与 SDK 分发，但跳过生产入口中的组配置和 Openness 初始化；从未 Attach / Connect，因此不涵盖原生工程代理、真实 API 卡死、TIA 退出、完整启动流程或长时间内存泄漏。

## 延长测试结果

9 月 30 日基础发布门全部通过后，两版同时启动各自测试宿主，将每组合延长到 300 轮。两版各约 234 秒完成四个组合，每个独立进程约 43–75 秒；本轮不是数小时持续运行验收。当日证据保留在 `bd7b34d` 提交及[本地封存包](local-checkpoint-20260930.md)；[local-stability-extended.json](../../manifest/local-stability-extended.json) 将随新候选重跑更新，不再代表下表的历史 EXE。

| 引擎 | 工具请求（含预期拒绝/错误） | 协议/认证检查 | 异常退出 / 断言失败 | 最高采样私有内存 | 最大句柄增长 |
|---|---:|---:|---:|---:|---:|
| V20 | 26,484 | 4,200 | 0 / 0 | 62.1 MiB | 65 |
| V21 | 26,484 | 4,200 | 0 / 0 | 62.6 MiB | 84 |

两版合计 52,968 次工具请求、8,400 项协议/认证检查；所有日志调用均成对闭合，未进入原生阶段。HTTP 请求在 8 并发下的 P95 最高为 109 ms，仅代表这些本地场景。句柄有增长但仍在本次门限内，不能据此宣称长期无泄漏。打包同时核对基础与延长测试对应的引擎和脚本哈希。

## 原生验收尚未进行

2026-09-30 补充：独立 V20/V21 net48 自有工程生命周期框架及进程监督器现已实现，完整构建只做编译和离线检查。[具体范围](native-lifecycle-tests.md)包括新建工程、设备组事务提交/回滚、保存重开及回读；尚未运行原生分支。生产 MCP 工作进程隔离仍是[待实施设计](openness-worker-isolation-plan.md)，不要将测试监督器当作生产隔离完成。

后续需用户指定的工程副本、安装版本和端口，以精确工程路径及 TIA PID 绑定，逐工具族执行读取、预览、写入、回读、编译和恢复测试。不能用工具数量或 SDK 形状检查代替这一步，也不能遍历调用所有工具：下载、PLC 启停、安全模式和工程生命周期操作需要各自的适用测试环境。

先测日常 PLC/HMI 读取及导出长循环，再测受控导入与异常中断；每次原生故障保留 MCP 调用日志、TIA 进程退出事件及转储线索。出现不可恢复的通道错误后，丢弃旧对象句柄并要求重新核对绑定；不自动重放写入。

PLC 原生交叉引用仍默认关闭，本轮没有为了压测而启用它。其确切崩溃原因仍需实际环境证据，见[交叉引用调查](cross-reference-investigation-20260930.md)。本地通过不能宣称 TIA 不会崩溃。
