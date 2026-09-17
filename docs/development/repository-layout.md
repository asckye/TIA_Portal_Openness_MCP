# 仓库结构与迁移对照

[文档目录](../README.md)

根目录保留项目说明、治理/许可证、插件配置及 `TiaMcpConfigurator.exe`。源码放在 `tools/`，运行文件放在 `runtime/`，模板放在 `templates/`，清单放在 `manifest/`，示例放在 `examples/`。

## 文档分类

| 目录 | 内容 |
|---|---|
| `docs/getting-started` | 图形配置、CLI 和 AI spec 提示词 |
| `docs/guides` | 工程生成、版本控制、在线监视、PLC / HMI 专题 |
| `docs/reference` | 工具矩阵、能力和自然语言配方 |
| `docs/troubleshooting` | 错误、Openness 限制和 HMI 快照诊断 |
| `docs/development` | 发布、验证、结构与路线图 |
| `docs/tools` | 按工具族组织的技术细节 |
| `docs/releases`、`docs/archive` | 当前发布说明和历史记录 |
| `docs/licenses` | 随包分发的第三方程序集许可证清单与原文 |

原 `手册/` 并入上述分类；“开始使用”和“使用说明与介绍”并入 README 与配置指南；AI spec 提示词并入 CLI，PLC 网络模式并入模板指南，能力增补并入能力参考。v2.7.15 及之前发布说明合并存档。失效路线图、临时验收命令和私人原始采集数据不再作为当前文档保留。

## 脚本和运行路径

脚本分别移入 `build/`、`checks/`、`generate/`、`diagnostics/`、`operations/`，完整入口见 [脚本索引](../../scripts/README.md)。Actions、脚本间调用、清单、蓝图和文档同步更新。

旧 Build-DefectFix、Build-MultilingualFix、Package-DefectFix、Package-ReadOnlyV21 和 Python 预热桥接已删除。发布统一见 [发布流程](release-workflow.md)，预热使用原生 CLI；取证、预热和拖放生成仍有用途，保留在相应分类。

交付和 checkout 均使用 `runtime/v20`、`runtime/v21`，不再创建旧 `tools/.../bin[-v20]/Release/net48` 副本。引擎源码/测试与运行二进制未改变，原构建哈希仍可验证。

`bin-build/` 是被忽略的本机验证/打包目录，不进入公开交付。用户工程、外部素材及客户端配置不属于仓库清理范围。
