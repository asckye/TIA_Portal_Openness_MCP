# 真机验收与历史证据

当前版本的构建、离线功能、协议测试和 Studio mock 结果见 [release-build.json](../../manifest/release-build.json)、[multi-version-build.json](../../manifest/multi-version-build.json)。这些检查没有连接 TIA 工程或在线 PLC，不能标记为原生验收。

## v3.2.0 验收边界

八版新增外部源导入、生成、编译路线与目标版本 UDT / DB XML，以及 Studio 新版本适配器的真实工程往返，当前均为 **NOT RUN**。后续应按明确的版本、测试工程副本及选定 PLC 执行，记录实际导入对象、编译诊断、读回与保存结果。选择测试目标不会由历史记录自动授权。

| 已知现场事项 | 当前结论与证据 |
|---|---|
| `ManageStartdriveParameter` 读取 `p2051[0]` | 用户报告 TIA 崩溃。现代码按官方 BICO 路线做单值读取，去掉无关元数据遍历；原生复测未运行，根因未确认。详见[限制与结果解释](../troubleshooting/openness-limitations.md)。 |
| Unified 脚本模块库类型改名 | 2026-10-01，在项目库和独立全局库中的样本均出现 `NonRecoverableException` 并伴随测试 TIA 退出；没有已验证修复。[机器可读证据](../../manifest/unified-library-rename-native-20261001.json)。 |
| PLC 原生交叉引用 | 有 V21 退出历史，默认关闭；编译成功不能保证查询稳定。离线引用分析仅覆盖输入导出。 |
| HMI 深层快照 | 有 getter / 句柄失效记录；部分属性暂停读取。必须查看完整性字段，[诊断说明](../troubleshooting/hmi-snapshots.md)。 |

## 保留的历史记录

[原逐工具台账](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/reference/real-machine-ledger.md)保留早期 V21 测试工程、设备、工具行和现场修复过程。该台账主要对应 2.7.39–2.7.62；旧汇总中“调用过”“前置条件拒绝”“环境拒绝”与真实成功分列，不能将总行数称为全部验收通过。

历史证据中，标准 CPU / PLCSIM Advanced 的下载、上线、比较、变量读写与单步场景曾完成；这些结果仅适用于当时设备、工程与软件组合，不能推广到其他版本、F-CPU 或真实生产设备。早期某个 BICO 参数写入成功，也不能证明 `p2051[0]` 读取已通过。

[Unified 改名现场完整快照](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/299f947d4b54a92873e9321a39f6b3dc39279e11/docs/development/unified-library-rename-incident-20261001.md)保存两个测试位置、时间、调用标识与源工程读回结果。它不是独立于 MCP 的最小 C# 复现，也没有确定所有 V21 补丁的行为。

## 新记录应包含什么

记录引擎版本和文件哈希、TIA 版本/已知补丁、精确工程与 PLC、工具参数（排除凭据）、预览与实际结果、读回内容、编译错误/警告以及是否保存。失败或结果不确定时保留对应日志，先确认工程实际状态，再决定下一步。最新构建不会覆盖上述历史证据。
