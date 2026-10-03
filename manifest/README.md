# 清单与验证记录

> Current multi-version development: [release tools, API audit and build instructions](../docs/reference/version-tools.md). Historical release/VM records below describe their original dates and do not authorize new native operations.


| 文件 | 含义 |
|---|---|
| [package-manifest.json](package-manifest.json) | 交付版本、入口、模板和工具统计 |
| [tools-list.json](tools-list.json) | 静态工具清单；实际暴露以 tools/list 为准 |
| [release-build.json](release-build.json) | 原始引擎日期、测试结果、源码与双版本运行文件哈希 |
| [local-stability-extended-r3-20261001.json](local-stability-extended-r3-20261001.json) | 较早 r3 候选的延长本地负载历史证据；不适用于当前发布 EXE，不代表真实 TIA 稳定性 |
| [configurator-build.json](configurator-build.json) | 最近一次配置器隔离测试、源码和 EXE 哈希 |
| [delivery.json](delivery.json) | 交付版本与两个构建记录的绑定 |
| [publication-v3.1.0.json](publication-v3.1.0.json) | v3.1.0 的 GitHub 发布地址、附件摘要和三项通过的工作流 |

打包另生成 `manifest/release-file-hashes.json`，列出文件哈希及 sourceCommit；它属于发布产物，不提交到仓库。旧临时修复清单已被统一构建记录替代。

只改文档、目录或 GUI 时，引擎版本和原测试时间保持不变。引擎源码、测试或运行文件变化需重新验证，不能手工改哈希让旧记录通过。

`release-build.json` 的 `workerIsolation` 保存工作进程故障注入与协议验证计数，`isolatedLocalStability` 保存隔离模式的独立压力结果。其余 `localStability` 保留普通模式证据；各结果绑定各自的运行时和脚本哈希，不能互相替代。原生生命周期的 `liveAcceptance` 未运行时必须继续标为 `NOT RUN`。
