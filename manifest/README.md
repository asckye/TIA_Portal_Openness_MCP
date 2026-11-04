# 清单与验证记录

| 文件 | 用途 |
|---|---|
| [package-manifest.json](package-manifest.json) | 版本、交付入口、模板和工具统计 |
| [delivery.json](delivery.json) | 源码 checkout 的交付版本与包名标记；暂存时重新生成 |
| [version-tools.json](version-tools.json) | 八版工具分组及完整列表 |
| [tools-list.json](tools-list.json) | V21 工具目录；实际连接仍以 tools/list 为准 |
| `contracts/v4/baseline/*.json` | 当前 V4 八版离线工具契约基线（格式 2），记录输入 schema、注解、描述哈希、行为能力及 V20/V21 lite 名单；输出 schema 在每个文件的 `schemas` 表中存一份，由 `outputSchemaSha256` 引用。用 `scripts/checks/Snapshot-ToolContracts.py capture` 生成，`compare --baseline manifest/contracts/v4/baseline --current <目录>` 检查兼容性，破坏性变化返回 1，捕获文件仍逐字节比较 |
| [history/contracts-v3/](history/contracts-v3/) | 3.2.0 的 16 份原始基线只读归档；来源提交、发布版本和 SHA-256 见 provenance.json；dotnet run --project build-tools/release -- check-repository 拒绝清单/字节变化和旧目录重新出现 |
| [version-api-audit.json](version-api-audit.json) | 官方 SDK 与编译调用成员的对照，不代表原生功能覆盖率 |
| [tool-usage-coverage.json](tool-usage-coverage.json) | 各版本工具、操作、编程示例和官方来源的检索检查 |

`publication-v*.json` 记录对应公开版本的资产、哈希、源码提交和工作流。发布打包生成的 `release-file-hashes.json` 属于安装包，不提交到源码仓库。一次性日期取证记录位于 [history/](history/)，保留其原始时间、适用版本和文件内容，不作为当前版本测试结果。


文档改动不会改变引擎的构建日期或测试记录。编译输入和运行文件变化应重新运行对应构建流程；使用[验证指南](../docs/development/validation.md)检查实际文件，不手工改哈希来掩盖差异。原生未执行项保持 NOT RUN。

`prepare` 在暂存目录生成 `manifest/multi-version-build.json` 的 `{files}` 安装清单，包含路径和 SHA-256。该清单不再作为源码构建记录跟踪。当前契约检查保留八版 baseline；Studio 读取器使用 `tests/Studio/V4RealEnvelopes.json` 的冻结真实响应夹具。
