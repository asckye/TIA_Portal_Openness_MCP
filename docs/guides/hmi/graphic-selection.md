# 图形对象选择范围与坐标核对

新增 `ReadUnifiedGraphicSelection` 和 `CompareUnifiedGraphicSelections`，用于将若干已知对象作为一个明确的选择范围定位、分页采集并对比坐标。它们不写入坐标，不导出、保存、编译或关闭博图。

## 能力边界

目前接入的 V20/V21 Openness 工程 API 适配器不能证明普通图形组合的成员关系和整体变换。`ScreenGroups` 是画面文件夹；运行时 JavaScript 的 `Group.ContainedItems` 也不能直接作为 C# 工程对象接口使用。

返回的 `nativeGroupVerified=false`、`nativeGroup=null`、`nativeGroupBounds=null` 表示尚未验证原生组合，不表示对象未分组。即使所有坐标读取成功，最终页仍会包含 `NativeGraphicGroupUnverified` 缺口，`dataComplete=false`。检查汇总记录中的 `geometryComplete` 可单独判断这批对象的四个坐标字段是否齐全。

接口记录公开的 `Parent` 工程归属（只读取一层及其名称）、属性/集合描述信息，以及明确标为可读的关系属性。字段名称、相同父对象、接近的位置或者移动联动，都不能单独证明组合关系。不会递归读取父对象、诊断控件的无关属性或者猜测的组合成员 getter。

官方资料：[V21 ScreenItem objects](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/screenitem-objects)、[V21 ScreenItems 读取方式](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/description-screenitems)。

## 读取

通过 `FindTools("graphical selection")` 获取接口签名，再调用：

```json
{
  "softwarePath": "HMI_Device/HMI_RT_1",
  "expectedProject": "MyProject",
  "screenPath": "/PopupScreens/Info",
  "itemNamesJson": "[\"fgxTop\",\"txtTop\",\"gfxBotom\",\"txtBotom\"]",
  "pageSize": 20,
  "budgetMs": 5000
}
```

- 名称严格区分大小写，不会将 `fgxTop` 更正为 `gfxTop`，也不会将 `Botom` 更正为 `Bottom`。
- 一次选择 1–128 个对象，至少需要它们的准确名称。不会自动扩展为尚未确认的原生组合全部成员。
- 保持项目、HMI、画面及名称数组不变，用 `nextCursor` 续读到 `traversalComplete=true`。`pageSize` 统计证据记录；正常结束共有 N 条对象记录和 1 条汇总记录。保存每一页的 `Meta`，按 `pageIndex` 排序。
- `actualCount` 是本页证据条数，`expectedCount` 是预计总证据条数，`expectedObjectCount` 是请求的对象数；最终汇总 `actualObjectCount` 是实际定位到的对象数。
- 每个对象包含 `Left/Top/Width/Height` 原始类型、值、证据路径与采样时间。`rawCoordinateEnvelope` 仅对原始值做包围范围计算，未验证坐标系一致性、旋转和变换，不能当作编辑器中的组合边界。
- 读取不是原子快照，期间不要编辑工程。时间预算在对象之间检查，无法中断正在执行的同步 Openness 调用。
- 遇到句柄释放或 IPC 异常立即终止，后续读取被阻止。检查日志后才显式重新绑定，不会自动重试、重开或关闭博图。已保存的页仍可离线查看。
- 保存完成后调用 `ReleaseUnifiedReadCursor(releaseCursor)` 释放缓存；在此之前仅最近一页支持重放。

## 前后对比

分别保存修改前、修改后完整采集的所有 `Meta` 页。将两个页数组编码为 JSON 字符串，传给 `CompareUnifiedGraphicSelections(beforePagesJson, afterPagesJson)`。该接口完全离线，不需要连接 TIA，也不会自行修改对象来制造对比数据。

返回每个对象四个字段的 `before`、`after`、`delta` 和 `changed`，并统计 `changedObjectCount`。它会拒绝缺页、分页混用、范围不一致、缺少坐标或对象类型变化的输入，避免将未采集的对象误判为没有变化。这里的 `dataComplete=true` 仅代表原始几何字段比对完整，不代表组合内部已读取完整；`causalityEstablished=false` 表示没有认定联动原因。同路径、同类型、同名称也无法排除两次采集之间发生删除重建。

在原生组合归属、坐标系和整体变换尚未验证前，本补充不提供“整体移动组合”写入接口，也不承诺修复 TIA 的坐标联动行为。
