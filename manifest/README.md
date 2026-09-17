# 清单与验证记录

| 文件 | 含义 |
|---|---|
| [package-manifest.json](package-manifest.json) | 交付版本、入口、模板和工具统计 |
| [tools-list.json](tools-list.json) | 静态工具清单；实际暴露以 tools/list 为准 |
| [release-build.json](release-build.json) | 原始引擎日期、测试结果、源码与双版本运行文件哈希 |
| [configurator-build.json](configurator-build.json) | 最近一次配置器隔离测试、源码和 EXE 哈希 |
| [delivery.json](delivery.json) | 交付版本与两个构建记录的绑定 |

打包另生成 `manifest/release-file-hashes.json`，列出文件哈希及 sourceCommit；它属于发布产物，不提交到仓库。旧临时修复清单已被统一构建记录替代。

只改文档、目录或 GUI 时，引擎版本和原测试时间保持不变。引擎源码、测试或运行文件变化需重新验证，不能手工改哈希让旧记录通过。
