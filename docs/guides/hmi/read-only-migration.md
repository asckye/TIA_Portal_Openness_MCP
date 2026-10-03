# WinCC Unified 只读迁移采集

适用于 V20/V21 完整引擎中实际提供相应 Unified API 的设备。采集用于读取脚本、变量、画面及库定义，保留原文和缺口，便于后续迁移分析；采集完成本身不等于迁移完成。

## 选择工具

| 工具 | 获取内容 |
|---|---|
| `ListUnifiedGlobalScripts` | 模块清单，清单不含完整正文 |
| `ReadUnifiedGlobalScript` | 原生 JS/YAML、正文和引用信息 |
| `ReadUnifiedTagDefinitions` | 变量定义、表/组、成员及来源信息 |
| `ListUnifiedLibraryFolder` | 指定库文件夹的直接子项与版本 |
| `ReadUnifiedLibraryType` | 明确类型和版本的原生导出 |
| `ReadUnifiedFaceplateInstance` | 面板实例接口与有证据支持的库版本关系 |
| `ReadUnifiedScreenBranch` | 指定页面/对象分支的属性和成员 |
| `ReleaseUnifiedReadCursor` | 释放本次采集缓存 |

从 `GetToolUsage(toolName="...")` 读取当前参数与官方例子。通过实际工程名称、HMI 路径和对象范围定位；库类型选择准确版本，不把默认版本当成实例引用版本。

## 一次完整采集

1. 列出范围，选择要采集的对象或分支。
2. 第一次调用使用空游标，保存该页的 `records`、`failures` 和 `Meta`。
3. 若有 `nextCursor`，保持其他范围参数相同，传入该游标继续读取。
4. 直到 `traversalComplete=true`，检查累计失败及最终完整性。
5. 保存完整结果后，用返回的 `releaseCursor` 释放缓存。

大型响应可能先通过 `GetExport` 分块返回：先取全该页 JSON，再从其中读取采集游标。分块偏移与采集游标不是同一个机制。工程切换或服务重启后重新采集，不能拼接不同会话的页。

## 结果如何解释

| 字段 | 含义 |
|---|---|
| `apiCallSuccess` | 请求或原生调用返回，不代表所有内容已读全 |
| `traversalComplete` | 当前范围遍历结束 |
| `dataComplete` | 当前范围遍历结束且没有报告的缺口，不代表整个工程或运行时依赖完整 |
| `actualCount` | 证据记录数量，不一定是变量或控件数量 |
| `definitionFieldsComplete` | 定义字段读取是否完整 |
| `classificationComplete` | 变量来源是否能确定，与定义读取分开 |
| `nativeFilesComplete` | 原生文件取得和解析情况，不证明文件包含全部内部对象 |

变量的原始地址、连接和符号保持原样；从根对象推导的来源会另行标记。动态 JavaScript 表达式不能仅靠静态解析还原实际运行引用。库导出的 XML 可能只含版本等元数据，遇到 `LibraryXmlContentUnverified` 应保留缺口。

检查官方导出状态、消息和实际文件清单；空文件或未调用导出不是“对象为空”的证据。发现故障后保留已取得的页面与诊断，先检查工程和连接状态，再开始新的采集。

原生导出在服务进程的临时目录进行，不保存或修改工程。采集内容可能包含工程源码，按项目要求保存。相关操作见[全局脚本](global-scripts.md)、[图形坐标](graphic-selection.md)和[工具示例说明](../../development/official-tool-usage.md)。
