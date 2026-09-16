# 创建 PLC 数据类型分组

`CreatePlcTypeGroup` 操作 PLC 的“PLC 数据类型”用户文件夹，不是项目库类型文件夹，也不创建 UDT 定义。

先连接并打开工程，预览目标：

```json
{"softwarePath":"+S1-K1","groupPath":"Common/Motors","dryRun":true}
```

确认后传 `dryRun:false` 创建。缺失的父组会一并创建，已有分组会复用。返回 `createdPaths`、`createdCount`、`missingPaths` 和 `alreadyExisted`。预览不会写入；实际创建后的 `missingPaths` 表示本次调用开始遍历时缺失的组。

路径相对于“PLC 数据类型”根节点，可带 `PLC data types/` 或 `PLC 数据类型/` 前缀，也支持反斜杠。拒绝根节点、空路径段及 `.`、`..`；组名按字面匹配，不作为正则表达式。

PLC 使用公共精确软件容器解析器，名称不匹配或存在歧义时失败，不根据“只有一台 PLC”猜测写入目标。创建使用官方 `PlcTypeUserGroupComposition.Create(string)`，实际操作持有 TIA 独占访问。不自动保存、编译或下载。

若中途原生调用失败，错误包含已创建的父组路径，不自动回滚。检查工程状态后再决定是否重试。

验证：V20、V21 编译通过；离线测试覆盖预览、多级创建、重复调用、中文根前缀、IEC 名称、非法路径、歧义以及部分失败。未在真实 TIA 工程中执行创建验收。
