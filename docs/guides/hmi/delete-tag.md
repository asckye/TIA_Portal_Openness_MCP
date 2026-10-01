# 删除一个 HMI 变量

本地 3.1.0 开发版本新增 `DeleteHmiTag`，工具总数 478（lite 直接列出 62 个，其余通过 `FindTools` / `CallTool`）。已用本地 V20/V21 PublicAPI 编译和离线检查；真实 TIA 删除验收尚未运行。先前运行中的 3.0 服务需要更新后才会出现该工具。

该工具调用官方 Classic `Tag.Delete()` 或 Unified `HmiTag.Delete()`。先从工程树取得准确的 HMI 软件名，使用完整变量表路径预览：

```json
{
  "softwarePath": "HMI_RT_1",
  "tagTablePath": "/Folder/TagTable_1",
  "tagName": "Speed",
  "dryRun": true
}
```

确认目标后，相同参数增加 `"confirmDelete": true`，并将 `"dryRun"` 改为 `false`。示例中的名称必须替换为实际工程对象；本次开发没有删除任何用户工程变量。

- Classic 必须指定 `/变量表名` 或 `/文件夹/变量表名`；默认变量表也按其准确名称选择。
- Unified 的空 `tagTablePath` 选择设备级 `Tags`；分组变量表用 `/组/子组/变量表`。路径不进行递归搜索、URL 解码或通配匹配。
- 实际删除先取得工程独占访问，调用一次 `Delete`，再以 `Find` 验证不存在。失败不重试，`mayHaveChanged` 表明是否可能已修改。
- 不删除 PLC 变量、变量表或系统变量，不自动保存、编译、下载。
- 不查询交叉引用。`referencesChecked=false` 表示使用情况未知；画面、脚本、报警、记录等引用可能受影响，也不保证 TIA 不处理依赖配置。建议先导出备份，删除后检查并编译 HMI，确认后再保存。

官方依据：[Classic V21 删除单个 HMI 变量](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-an-hmi-device/tag-table/deleting-an-individual-tag-from-an-hmi-tag-table)、[Unified V21 Tags.Delete](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/tags/tags.delete)、[Unified V20 Tags.Delete](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-accessing-the-data-of-an-hmi-unified-device-rt-unified/hmisoftware-rt-unified/tags-rt-unified/tags.delete-rt-unified)。
