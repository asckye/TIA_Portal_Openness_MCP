# 清单与验证记录

| 文件 | 用途 |
|---|---|
| [package-manifest.json](package-manifest.json) | 版本、交付入口、模板和完整引擎统计 |
| [delivery.json](delivery.json) | 交付版本与三份构建记录的绑定 |
| [release-build.json](release-build.json) | V20/V21 功能、协议、稳定性、源码和运行文件哈希 |
| [multi-version-build.json](multi-version-build.json) | 八版 Worker/Adapter、六版基础运行包、Studio 和跨版本功能检查 |
| [configurator-build.json](configurator-build.json) | WPF 配置器测试、源码和 EXE 哈希 |
| [version-tools.json](version-tools.json) | 八版工具分组及完整列表 |
| [tools-list.json](tools-list.json) | V21 完整引擎目录；实际连接仍以 tools/list 为准 |
| `contracts/v4/baseline/*.json` | 当前 V4 八版离线工具契约基线，记录输入 schema、描述哈希及 V20/V21 lite 名单；用 `scripts/checks/Snapshot-ToolContracts.py capture` 生成，`compare --baseline manifest/contracts/v4/baseline --current <目录>` 检查兼容性，破坏性变化返回 1 |
| `contracts/v4/responses/*.json` | 当前 V4 八版离线响应与参数拒绝基线；V20/V21 全部工具覆盖 full 直接调用和 lite 桥接路径。大响应、GetToolUsage 和新增桥接拒绝保存规范化 SHA-256、字节数及 JSON 类型摘要；用 `scripts/checks/Snapshot-ToolResponses.py capture` 连续抓取两轮，`compare --baseline manifest/contracts/v4/responses --current <目录>` 严格比较，任何变化返回 1；命令和范围见[验证指南](../docs/development/validation.md#工具契约兼容检查) |
| [history/contracts-v3/](history/contracts-v3/) | 3.2.0 的 16 份原始基线只读归档；来源提交、发布版本和 SHA-256 见 provenance.json；Check-Repository.py 拒绝清单/字节变化和旧目录重新出现 |
| [version-api-audit.json](version-api-audit.json) | 官方 SDK 与编译调用成员的对照，不代表原生功能覆盖率 |
| [tool-usage-coverage.json](tool-usage-coverage.json) | 各版本工具、操作、编程示例和官方来源的检索检查 |

`publication-v*.json` 记录对应公开版本的资产、哈希、源码提交和工作流。发布打包生成的 `release-file-hashes.json` 属于安装包，不提交到源码仓库。一次性日期取证记录位于 [history/](history/)，保留其原始时间、适用版本和文件内容，不作为当前版本测试结果。

两份 Snapshot 脚本的 `verify` 子命令只检查当前 V4 八版文件、生成目录的 full/lite 名单和现有响应覆盖，不加载产物；快照格式与逐族行为能力的最终冻结仍待 P6-35/P6-41。

文档改动不会改变引擎的构建日期或测试记录。编译输入和运行文件变化应重新运行对应构建流程；使用[验证指南](../docs/development/validation.md)检查实际文件，不手工改哈希来掩盖差异。原生未执行项保持 NOT RUN。
