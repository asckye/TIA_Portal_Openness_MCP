# 模板与工程蓝图

| 目录 | 内容 |
|---|---|
| [plc](plc/README.md) | 变量表、UDT、DB、LAD 配方与 SCL 示例 |
| [hmi](hmi/README.md) | Unified designJson 画面与绑定示例 |
| [project-blueprints](project-blueprints) | 完整工程蓝图及 CLI scaffold spec |
| [mcp-full-e2e-verify](mcp-full-e2e-verify/README.md) | 可恢复测试工程使用的端到端验收素材 |

先读 [工程生成指南](../docs/guides/project-generation.md)；指令与网络模式见 [PLC 模板指南](../docs/guides/plc/templates.md)，HMI 规则见 [画面指南](../docs/guides/hmi/design.md)。

`full_plc_hmi_project.json` 的 `requiredBundleFiles` 用于交付校验。`scaffold_spec_*.json` 传给 CLI `gen` 前先用 `--dry-run` 检查。`__BUNDLE__` 由引擎解析为交付根目录，无需替换成机器绝对路径。

实际订货号、PLC/HMI 名称、分辨率和工程路径需按工程确认，模板本身不构成现场验收。

## 可选外部参考

西门子 HMI Template Suite、官方示例和图片不包含在仓库或交付包内，也不需要所谓 `reference` 目录才能使用本项目。请自行从官方渠道取得匹配版本及许可的素材，在 TIA 中查看后将适用的颜色、间距和结构转换为 designJson；不要将受限安装媒体、客户工程或私有素材放入发布包。
