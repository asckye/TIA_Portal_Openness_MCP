# PLC 指令与模板说明

本说明用于配合 `templates/plc/` 生成通用 PLC 程序结构。模板覆盖 SCL、LAD 调用网络、变量表、UDT、全局 DB、FC、FB 和 HMI 接口 DB。

## 覆盖内容

| 类别 | 内容 |
|---|---|
| 布尔逻辑 | `AND`、`OR`、`NOT`、赋值、保持、复位 |
| 分支 | `IF / ELSIF / ELSE`、`CASE` |
| 定时 | `TON` 调用结构、延时完成位 |
| 计数 | 上升沿检测、累计、复位、到达预置 |
| 算术 | 加减乘除、误差、绝对值 |
| 比较 | `=`、`<>`、`>`、`>=`、`<`、`<=` |
| 限幅 | `LIMIT`、范围判断 |
| 类型转换 | `INT_TO_DINT`、`DINT_TO_REAL`、`REAL_TO_DINT`、`BOOL_TO_DINT` |
| 循环 | `FOR` |
| 数据结构 | Tag 表、UDT、Global DB、HMI 接口 DB |
| LAD | `BuildFlgNetCallXml` 调用网络配方和已验证 XML 样例 |

## 推荐导入顺序

```text
tagtable_basic_signals.json          (PlcBuildAndImport, tagtable)
udt_basic_status.json                (PlcBuildAndImport, udt)
db_basic_status.json                 (PlcBuildAndImport, globaldb)
db_hmi_interface.json                (PlcBuildAndImport, globaldb)
lad-recipes/lad_call_recipes.json    (BuildFlgNetCallXml)
scl-examples/FC_InstructionGallery.scl   (外部 SCL 导入)
scl-examples/FC_BasicScaleLimit.scl      (外部 SCL 导入)
scl-examples/FC_MathCompareDemo.scl      (外部 SCL 导入)
scl-examples/FB_BasicLatch.scl           (外部 SCL 导入)
scl-examples/FB_TimerCounterDemo.scl     (外部 SCL 导入)
scl-examples/FB_StepSequenceDemo.scl     (外部 SCL 导入)
```

> **FC/FB 一律走外部 SCL，不走 DSL。** `PlcBuildAndImport(kind=fc|fb)` 的 DSL 只接受
> 单变量名的 `condition`/`source`，不能解析 `Setpoint - Actual`、`Disable OR FaultLatch`、
> `ABS(...)`、`CASE` 等表达式（会编译报 `Tag not defined`）。因此含算术/比较/函数/CASE 的
> FC/FB 改为 `scl-examples/*.scl` 原生源，经 `ImportPlcExternalSource` +
> `GenerateBlocksFromExternalSource` 导入。旧 `plcbuild-json/fc_*.json`、`fb_*.json` 已弃用
> （文件保留并标 `_deprecated`，勿再使用其表达式写法）。

`plcbuild-json/*.json`（仅 tagtable/udt/globaldb）每个文件都包含：

```json
{
  "kind": "globaldb",
  "tool": "PlcBuildAndImport",
  "json": {}
}
```

调用时把 `json` 字段序列化为字符串，传给 `PlcBuildAndImport`。FC/FB 的 `.scl` 文件直接作为
外部源导入，不需要序列化。

## 验收

- 所有模板先 `dryRun=true`。
- 真实导入后执行 `CompileAndDiagnosePlc`。
- 编译错误为 0 后保存。
- HMI 变量应绑定到 PLC tag 或 `DB_HMI_Interface` 成员。
- LAD 调用网络用于组织调用关系，不手写复杂网络树。

## 扩展阅读

- **`docs/guides/plc/templates.md`**：多网络分段、定时器/沿放置规则、SCL 与 LAD 分工，用于把程序段从「演示级」拉长到「工程级」。


---



## PLC 程序扩展：网络与指令模式（提炼自工程实践 + SKILL 已验证子集）

本页在 `docs/guides/plc/templates.md` 与 `tools/tiaportal-mcp/skill/SKILL.md`（LAD §9–11、SCL §10）基础上，补充 **常用网络结构** 与 **扩展指令方向**，便于在 `PlcBuildAndImport` / `ImportBlock` / 外部 SCL 中拉长「程序段」而不堆无意义重复。

## 1. LAD：建议的网络分段模式

| 网络段 | 典型内容 | 指令提示 |
|--------|-----------|-----------|
| 使能与联锁 | 急停、模式、许可 | 串接触点 → `Coil` / `SCoil` / `RCoil` |
| 模拟量/限幅 | 设定、上下限 | `Gt`/`Lt`/`Move`/`Add`/`Sub`/`Mul`/`Div` |
| 定时与沿 | 消抖、脉冲 | **TON/TOF/TP** 仅放在 **FB.Static** 或 **全局 DB**（见 SKILL：F-CPU 下勿放 FC.Temp） |
| 比较链 | 多段阈值 | `Eq`/`Ne`/`Ge`/`Le` 组合 + OR-box `O` |
| 数据类型转换 | 整型↔实型 | `Convert`（SrcType/DestType） |

**扩展建议**：在已有 `MCPVerify_FC_LAD*.xml` 基础上，按网络 **复制-改名-改操作数**，增加「报警锁存」「运行小时计数」「互锁矩阵」等独立网络，每网络单一职责。

## 2. SCL：`PlcBuildAndImport` DSL 与「超出 DSL」

- DSL 支持：`assignment`、`if`/`else`/`endif`、`line`（自由 token）、`elsif`（条件为 **单 Bool**）。
- **不支持**：`FOR`/`WHILE`/`CASE` 等 → 用 **外部 `.scl` + ImportPlcExternalSource + Generate** 或 **TIA 内编写后 ExportBlock**。

**扩展建议**：将工艺拆为多个 **FC**：`FC_AlarmLatch`、`FC_ScaleReal`、`FC_Ramp`… 每个 30～80 行 SCL，再在 OB/Main 中顺序调用，比在单 FC 内堆巨型逻辑更易编译与下载。

## 3. 与参考工程（`reference`）的配合

打开 `reference\Siemens Standard Template V5_V21\*.ap21` 等，在 TIA 中搜索：

- `TON`、`CTU`、`MOVE`、`SEL`、`LIMIT`、`NORM_X`、`SCALE_X` 等块用法；
- 多语言报警、工艺对象接口 DB 结构。

将 **接口 DB 成员命名** 与 `templates/plc/plcbuild-json/db_hmi_interface.json` 对齐，可减少 HMI 绑定时的符号歧义。

## 4. 蓝图内已有块与建议增量

| 已有（`templates/plc/plcbuild-json`） | 可增量方向 |
|----------------------------------------|------------|
| `fb_timer_counter_demo` | 增加 **CTU 级联**、预置值来自 HMI DB |
| `fb_step_sequence_demo` | 增加 **互锁步**、超时步、报警步 |
| `fc_math_compare_demo` | 增加 **Real 比较链** + 死区 `ABS` 模式（SCL 实现） |

具体 JSON 增量由项目工艺决定；本页只规定 **结构与指令选型** 原则，避免为自动化而生成不可维护的「千行单块」。
