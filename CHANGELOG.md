# Change Log

## [2.7.6] - 2026-09-14

- 库类型没有文档导出格式时，对非脚本类型增加官方 `LibraryTypeVersion.Export(FileInfo, ExportOptions.WithReadOnly)` XML 路径，只调用一次；已有文档导出异常时不追加尝试。
- 区分 `exportAttempted=false`（未执行）、原生调用返回、文件读取完整与内部内容未验证；未执行不再报 `NativeExportEmpty`，内部对象/变量数量保持未知。
- XML 原文、SHA-256 及结构化路径可分页读取。库版本 XML 可能只有元数据，明确返回 `LibraryXmlContentUnverified`，不能冒充面板内部绑定已读全。
- 完整 V20/V21 包文件版本为 2.7.6.0。新增离线与实际 net48 EXE 的 XML 路径回归；真实工程中的面板导出仍待部署新版验证。

## 历史版本

- [v2.7.5](docs/releases/v2.7.5.md)
- [v2.7.4](docs/releases/v2.7.4.md)
- [v2.7.3](docs/releases/v2.7.3.md)
- [此前完整更新日志（仓库既有提交，保持原文）](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6a7298cbc08dd59fd08864d56a728a4da3435ed8/CHANGELOG.md)
