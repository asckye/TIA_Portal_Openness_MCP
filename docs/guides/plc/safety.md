# Safety（F 程序）工具

[文档目录](../../README.md) · [能力与验收边界](../../reference/capabilities.md) · [官方 API 覆盖清单](../../reference/openness-coverage.md)

2.7.25 起，`Siemens.Engineering.Safety` 程序集的全部 12 个类型 / 54 个成员都有专用工具，按官方 "F-related Openness" 章节逐页对照实现。适用于 S7-1200/1500 F-CPU；不是 F-CPU 时 `ManagePlcSafety` 返回 `NotSupportedOnVersion`，`ReadSafetyBlockSignatures` 返回 `fCpu=false` 与 0 条记录。所有工具都是 L2，经 `FindTools("safety")` + `CallTool` 调用。

## 先读一遍：验收快照

```text
ManagePlcSafety(softwarePath="+S1-K1", action="read")
```

返回一次性拿全：

| 字段 | 内容 | 官方成员 |
|---|---|---|
| `administration` | 是否设了 F 程序密码、当前是否已登录 | `IsSafetyOfflineProgramPasswordSet` / `IsLoggedOnToSafetyOfflineProgram` |
| `settings.values` | `ActivationOfFChangeHistory`、`CreateDriverInstanceDataBlocksWithoutPrefix`、`SafetyModeCanBeDisabled` | `SafetySettings` |
| `settings.assignmentOfBlockNumbers` | `managementMode`（`FSystemManaged` / `FixedRange`）与 FB/FC/DB 号段 | `AssignmentOfBlockNumbers` |
| `settings.safetySystemVersion` / `applicableSafetySystemVersions` | 当前安全系统版本与可选版本（升序） | `SafetySystemVersion`、`GetApplicableSafetySystemVersions()` |
| `settings.attributes` | `EnableConsistentUploadFromFCpu`、`EnableFCommunicationIdTag`（特定 F-CPU 才有，缺省为 `null` + 原生原因） | 文档化 `GetAttribute` |
| `runtimeGroups[]` | 主安全块 / IDB / 前后处理 / 信息 DB / 周期时间 + `FOBNumber` / `FOBCycleTime` / `FOBPhaseShift` / `FOBPriority` | `RuntimeGroup` |
| `programSignatures[]` | 集体 / 软件 / 硬件 / 通信地址 F 签名（`type`、`value`、`hex`、`valid`）；**V21**，V20 为 `null` | `SafetyAdministration.ProgramSignatures` |
| `globalSettings` | TIA Portal 级四项设置 | `GlobalSettings` |
| `cpu` | `Failsafe_FCapabilityActivated` 与 `SafetyPrintout` / `SafetyBaseIdProvider` / `SafetySignatureProvider` 可用性 | 硬件属性 + 服务探测 |

逐块签名单独一个工具，整 PLC 递归或指定一块：

```text
ReadSafetyBlockSignatures(softwarePath="+S1-K1")                      # 全部 F 块，分页
ReadSafetyBlockSignatures(softwarePath="+S1-K1", blockPath="Safety/Main_Safety_RTG1")
```

`value` 为 0 表示"自上次 F 编译后已改动、尚无有效签名"——官方语义，工具不会把它当成一个签名值。签名都是离线工程里的值，不是从 CPU 读的。

官方安全打印件（TIA 里"安全摘要"的打印）：

```text
ExportSafetyPrintout(softwarePath="+S1-K1", filePath="D:\\acceptance\\S1-K1_safety.pdf", option="All", dryRun=false)
```

前提：TIA 所在机器启用了 "Microsoft Print to PDF"（或 XPS Document Writer，配 `.xps`/`.oxps`）；文件不能已存在（原生 `Print` 会覆盖，工具拒绝）。返回原生 `bool`、字节数与 SHA-256，内容不解析。

## 写入（默认预览）

| 动作 | 说明 | 额外要求 |
|---|---|---|
| `createRuntimeGroup` | 不给块 → TIA 自动生成主安全 FB + IDB；`mainSafetyBlockPath` 指向 FC → `Create(name, FC)`；FC 换成 FB 并给 `mainSafetyInstanceDbPath` → `Create(name, FB, IDB)` | Offline、已登录 |
| `updateRuntimeGroup` | `propertiesJson`：`WarnCycleTime` / `MaximumCycleTime` / `PreProcessingName` / `PostProcessingName` / `InfoDbName` 与 `FOBNumber` / `FOBCycleTime` / `FOBPhaseShift` / `FOBPriority`；不改名 | 同上 |
| `updateSettings` | `propertiesJson` 可混写：标量（`ActivationOfFChangeHistory` 等）、`AssignmentOfBlockNumbers`（对象或 `AssignmentOfBlockNumbers.FromFB` 或裸 `FromFB`）、`SafetySystemVersion`（精确字符串，须在 `applicableSafetySystemVersions` 里）、两个文档化属性。`ManagementMode` 先于号段写入 | 同上；`SafetyModeCanBeDisabled` 需 `confirmSafetyChange` |
| `deleteRuntimeGroup` | 删除后核对不存在 | `confirmSafetyChange` |
| `generateGlobalFIOStatusBlock` | 创建或覆盖全局 F-I/O 状态块，返回块名/号/路径 | `confirmSafetyChange` |
| `cleanSystemGeneratedObjects` | 清理上次 F 编译生成的对象 | `confirmSafetyChange` |
| `generateBaseId` | 生成并分配 F-BaseID（V21，S7-1200 G2/1500 FW ≥ V4.1）；手动分配或延迟传输激活时原生抛错 | `confirmSafetyChange` |
| `login` / `logoff` | `password` 直传 `LoginToSafetyOfflineProgram(SecureString)`，不记录；已登录/未设密码时拒绝 | 不要求 Offline |
| `setPassword` / `revokePassword` | 设置或撤销 F 程序密码，读回 `IsSafetyOfflineProgramPasswordSet` 核对 | `confirmSafetyChange` |

`ManageSafetyGlobalSettings(action="update", propertiesJson="{\"SafetyModificationsPossible\":false}")` 在 TIA Portal 级锁住所有 F 程序修改（与登录状态无关）；`UsernameForFChangeHistory` 超过 256 字符直接拒绝，空串恢复默认。

CPU 的 `Failsafe_FCapabilityActivated` 是普通硬件属性，用 `SetDeviceItemAttribute` 修改；**关闭它会删除安全程序**，本工具族只读它。

## 边界

- **F 编译不在 Openness PublicAPI 内**：签名只在 TIA 内 F 编译后更新，`CompileSoftware` 不触发 F 编译。工具能拿到签名快照，不能替代功能安全验收。
- 读取不需要 Safety 许可证，写入需要；无许可证时原生抛异常并原样回传。
- V20 差异：`ProgramSignatures`、`SafetyBaseIdProvider` 与集体/软件/硬件/通信地址四种 `SafetySignatureType` 是 V21 API；逐块签名、设置、运行组、打印件、GlobalSettings 两版一致。
- `SafetyValidation`（安全验证助手，独立选件）未封装。
