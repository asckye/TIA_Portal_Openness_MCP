# 读取和比较图形对象坐标

V20/V21 完整引擎提供 `GetUnifiedGraphicSelection` 和离线工具 `CompareUnifiedGraphicSelections`。它们读取用户明确选择的对象、比较前后坐标，不移动对象。

## 操作步骤

1. 从真实画面获得准确对象名，读取 `GetToolUsage(toolName="GetUnifiedGraphicSelection")`。
2. 提供实际工程、HMI、画面路径和 `itemNamesJson`，采集所选对象。
3. 保持选择范围相同，按 `nextCursor` 续读到 `traversalComplete=true`，保存每页 `Meta`。
4. 需要比较时，在工程修改后对相同选择范围重新完整采集。
5. 按 `CompareUnifiedGraphicSelections` 的示例，将前后两个完整页数组分别作为 JSON 字符串传入。
6. 查看每个字段的 `before`、`after`、`delta` 和 `changed`，以及 `changedObjectCount`。

`geometryComplete` 表示所选对象的 Left/Top/Width/Height 是否齐全。`nativeGroupVerified=false` 表示尚未证明编辑器中的原生组合关系，不能理解为“这些对象未分组”。`rawCoordinateEnvelope` 只是原始坐标的包围范围，不等于验证过旋转和变换的组合边界。

采集是分步读取，期间保持工程不变。页数、范围或对象类型不一致时应重新采集相关范围；不要把缺页当成对象没有变化。保存全部页后按返回的 `releaseCursor` 释放缓存。

同名对象的前后差异不证明产生变化的原因，也不能排除对象被删除重建。完整的原生组合/变换验证仍不在该工具范围内。更多分页说明见[只读采集](read-only-migration.md)。
