# HMI Runtime 启动画面和运行设置

`ReadUnifiedRuntimeSettings` 与 `UpdateUnifiedRuntimeSettings` 对接 Unified HMI 的 `RuntimeSettings`。V21 官方文档和本地 PublicAPI 均确认 `StartScreen` 是可读写字符串属性；`ScreenResolution` 是可读写枚举属性。这些是工程配置属性，不是运行时画面切换命令。

依据：[西门子 V21 RuntimeSettings 属性文档](https://docs.tia.siemens.cloud/r/es-es/v21/funciones-para-el-acceso-a-los-datos-de-un-dispositivo-hmi-unified/hmisoftware/runtimesettings/acceso-a-las-propiedades-de-la-configuracion-de-runtime?contentId=AdQtq4BUs4uwFeOLDd0qjA)；V21 `Siemens.Engineering.WinCCUnified.dll` 的 `Siemens.Engineering.HmiUnified.RuntimeSettings.HmiRuntimeSetting`。适配器也编入 V20 EXE，但本机提供的 V20 PublicAPI 不含同名 Unified 程序集，V20 实际 Unified 设备支持尚未验证；运行时不支持时明确拒绝，不转用其他设备接口。

## 读取与字段范围

通过 `FindTools("Runtime settings")` 发现接口，再使用 `CallTool`：

```json
{
  "softwarePath": "HMI_Device/HMI_RT_1",
  "expectedProject": "MyProject",
  "fieldsJson": "[\"StartScreen\",\"ScreenResolution\"]"
}
```

`fieldsJson` 默认读取启动画面和画面分辨率。返回当前值、类型、证据路径、字段失败项，以及 `capabilities` 中的公开可写属性和准确枚举名称。公开 setter 存在不保证所有设备型号都接受该值；实际写入仍须检查结果。`dataComplete` 仅说明请求字段是否读全，不能当作整个 Runtime 配置已读取完整。

本版允许的根属性：`StartScreen`、`ScreenResolution`、`CentralInputHint`、`CentralPanning`、`CentralZooming`、`BitSelection`、`BitSelectionStrategyForResourceLists`、`BitSelectionStrategyForTagDynamization`、`EnableLanguageCompatibleFontFamilies`、`AutoLogOffURL`、`GMPEnabled`、`GeneralESIGCommentsStrategy`。每次只处理显式传入的属性。

嵌套语言/字体、OPC UA、UPSS 等子设置尚不在本接口写入范围；Windows 自启动、Runtime Manager 启动配置也不在范围内。未知字段、对象替换和任意方法调用均拒绝。

## 预览与应用

`UpdateUnifiedRuntimeSettings` 默认只预览：

```json
{
  "softwarePath": "HMI_Device/HMI_RT_1",
  "expectedProject": "MyProject",
  "changesJson": "{\"StartScreen\":\"/MainScreens/Home\",\"ScreenResolution\":\"SR_1920X1080\"}",
  "dryRun": true
}
```

1. `StartScreen` 输入必须是通过 `ListHmiScreenPaths` 取得的完整画面路径，包含子文件夹、按 URI 转义每个名称。API 实际保存画面名称，接口先验证画面存在，并只扫描所选 HMI 的画面名称确认唯一，再将路径转为准确名称。不会选择第一个同名页面。名称检查有 10000 项集合、64 层、30000 步及 15 秒边界；未完成则拒绝写入，单次同步 API 调用无法被超时中断。
2. 分辨率等枚举必须使用 `capabilities.allowedValues` 返回的准确名称，拒绝数字猜测、大小写猜测和字符串转布尔。所有请求字段先完成验证，再允许任何 setter。
3. 预览返回 `before`、`requested`、`proposed` 和 `token`。核对后传入同一工程、HMI、修改内容，设置 `dryRun=false` 并将令牌填入 `expectedToken`。令牌用于检查预览后的并发变化，绑定原值和目标值。
4. 实际应用在 TIA 独占访问期间重新核对。只写有变化的字段，写后立即回读，并在最后再次回读所有请求字段，以发现设置之间的联动。
5. 判断应用是否成功要检查 `operationSuccess` 和 `verificationSuccess`；`apiCallSuccess=true` 只说明 API 调用返回。检查 `status`、`failures`、`appliedFields`、`readback` 与 `mayHaveChanged`。

多个字段写入不是原子事务。某个字段失败时，之前字段可能已经改变；接口保留原值及已尝试的结果，不自动重试或回滚。句柄或 IPC 失效后停止远程操作，并启用共享的 HMI 读取阻止状态。

接口不会自动保存、编译、下载、重启 Runtime 或关闭博图。成功回读只确认工程内配置；运行设备是否采用新启动画面，需要后续明确授权的保存、部署及启动验证。

## 验证状态

离线回归覆盖路径定位、预览不写入、令牌过期、无效枚举、字段拒绝、同名歧义、部分写入、无效 setter、最终联动回读与 IPC 失败。实际 net48 EXE 的测试使用隔离替身，并核对 V21 DLL 的公开 setter 签名，不修改真实工程。

真实工程目前只读取已有启动设置作为基线；新接口尚未部署到虚拟机，没有执行真实启动设置写入或运行设备启动验收。正式 Release 尚未包含此功能。
