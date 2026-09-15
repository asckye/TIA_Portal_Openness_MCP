# Change Log

## Unreleased

- 新增 `ReadUnifiedGraphicSelection`：通过准确对象名称列表整体定位选择范围，分页读取原始坐标、尺寸、一层工程归属和可用的关系元数据；句柄失效后停止并阻止后续读取。
- 新增离线 `CompareUnifiedGraphicSelections`：核对前后完整分页证据并报告各对象坐标变化，拒绝缺页、缺字段和范围混用。
- 普通图形组合的真实成员、整体变换和坐标联动原因仍未验证，明确返回缺口，不将选择范围当作原生组合。见 [接口说明](docs/HMI_GRAPHIC_SELECTION.md)。本项尚未编入正式 Release。

## [2.7.7] - 2026-09-14

- HMI 快照按准确路径定位页面；句柄释放、IPC 或不可恢复异常后停止后续远程读取，保留已有证据并明确报告缺口，避免自动重试或重新绑定。
- 增加 HMI 读取阶段日志、连接健康状态和显式重新绑定后的缓存清理；暂时隔离诊断控件的特定属性读取。尚未确认博图异常退出的具体根因。
- 新增 `UpdateUnifiedGlobalScript`，支持精确定位 Navigation 等已有 Unified 全局模块，默认预览、原生备份、确认令牌、单模块导入及完整正文回读验证。
- 修复通用修改接口的 `HmiScripts` / `HmiScriptModule` 路径定位，以及原生导入导出参数的文件/目录类型转换，保留写入限制。
- 完整 V20/V21 包文件版本为 2.7.7.0；包含最新程序、依赖、配置入口、源码、测试和文档。真实工程 Navigation 导入及本次稳定性修复尚未完成验收，V20 原生 Unified 导入能力尚未确认。

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
