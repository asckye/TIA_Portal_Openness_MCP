# Studio bridge channel compatibility

The desktop and Framework bridge use `TiaMcp.WorkerChannel` protocol 2 with an
explicit `Studio` profile. The default Foundation profile keeps its existing
`adapter.*` namespace and envelope rules. Studio sends its original method names,
including `ping`, unchanged. Unknown Studio methods still reach `RpcDispatcher`
and return its existing method-not-found error.

## Identity and payloads

客户端启动前计算桥接 EXE 和已部署适配器的哈希，生成新的 32 字节 nonce，验证 hello 后才发送请求。
桥接进程在 hello 前加载适配器，但不创建 Openness 会话。Mock 和缺少适配器的诊断会话校验承载工厂的
Framework `TiaOpenness.Core.dll`，不会替代已存在的原生适配器。版本在启动前确定：未指定时选最新已安装版本，
无安装时诊断/mock 使用 `21`。

参数和结果 DTO 两端仍用 Newtonsoft 与 `BridgeJson.Settings`，JSON 原样嵌入 STJ 信封。
客户端保留先 `JObject.Parse`、再转换 DTO 的日期处理，包括原有 DateTime token 类型。
通道 id 为严格递增的正整数，`CallRawAsync` 仍返回字符串 id；未发送的取消不消耗 id。

`error.data` 保留 `outcome`、`evidence` 和完整的 `rpc = {code, message, data}`。
客户端还原原有 `RpcResponse.Error` 和 `BridgeRpcException`，保留消息格式 `<method> failed (<code>): <message>`、
`Code`、`Method`、`Data2`，以及 `type`、`stack`、可选 `inner` 诊断字段。
进入后端前拒绝记为 `RejectedBeforeNative`，读取失败为 `ReadFailed`，进入可能修改状态的方法后失败保守记为 `Unknown`；
该分类不保证实际发生了原生调用。Studio 只记录分类：桥接已捕获并返回的合法错误保持会话可用，包括 `Unknown`，
原始 RPC 错误仍交给 UI。Foundation 的 `Unknown` 仍使会话失效。

进度在 `params.payload` 中保留 `{operation,current,total,message}`，同时携带协议 2 的请求 id、序号和百分比。
回调绑定本次请求，不能成为后续请求的进度；先验证通道，再在状态锁外通知 UI，避免阻塞超时或取消。

绑定纪元只记录托管命令版本：`session.connect`、`session.disconnect`、`project.open`、`project.close` 成功后各加一，
读取和错误均不改变纪元。观察不调用 `GetState` 或读取 Siemens 对象，也不验证外部项目改绑。

## UI error readers

No GUI code branches on RPC codes or reads `BridgeRpcException.Data2`.
The paths below are relative to `src/TiaOpenness.Gui`:

| Reader | Displayed content |
|---|---|
| `Services/WorkbenchActivity.cs`, `Guarded` | `Exception.Message` in the status and localized error log |
| `ViewModels/VersionControlViewModel.cs`, `LoadVcDiffAsync` | `Exception.Message` as the diff caption |
| `ViewModels/MainViewModel.cs`, `OnBridgeLog` → `WorkbenchActivity.Append` | Bridge stderr, including the original method, RPC code and message |
| `App.xaml.cs`, `OnUnhandledException` | `Exception.Message` in the error dialog; the exception's `ToString()` in the crash log |

Other exception readers do not consume bridge RPC errors:

| Reader | Source |
|---|---|
| `App.OnStartup` | Configuration command exception message |
| `ConfigurationPage.OnPageExecuted` / `OnHelpOpened` | Configuration page initialization exception message |
| `Configuration/ConfigurationView.xaml.cs` | Service-start base exception message and `HttpListenerException.NativeErrorCode`; client-only save, client-settings save and update-check exception messages |
| `Configuration/UpdateCheck.cs` | API/fallback base exception messages, captured or wrapped for the update-check handler |

Result-level messages remain in unchanged DTOs: export/import errors, compile
descriptions and inspection findings in `EngineeringViewModel`; mapping/sync
errors and diff `Detail` in `VersionControlViewModel`; inspection issue `Message`
bindings in `MainWindow.xaml`; progress `Message` in `WorkbenchActivity.OnProgress`.

## Lifetime and native calls

桥接已处理的错误不终止 Studio 会话，不设置 `BridgeClient._faulted`；下次调用沿用同一进程并递增 id。
通道故障、超时和分派后取消仍使会话失效，包括管道失败、非法或超大帧、并发调用、重复或迟到的进度/回复、进程退出；
失效后不再发送、不重放、不自动重启。未发送的取消无害。

默认预算仍为十分钟，超时保留原有 `TimeoutException` 消息，分派后取消保留 `OperationCanceledException` 和调用方 token。
进度不延长预算。Mock 通过 `--mock` 子进程走同一通道。

Dispose 关闭通道，仍等待五秒让子进程退出，必要时结束所拥有的桥接进程；通道本身不结束进程。
Start 等待 hello 验证，失败时关闭所拥有的子进程。

原生调用前后均为 STA `RpcDispatcher.Invoke` → 所选会话方法 → 原有 Siemens 调用 → 序列化回复，释放会话仍在 STA。
新链路包在同步 `ChannelServer.Handle` 内；程序集加载提前到 hello 前，工厂配置和原生会话创建仍在首次相关分派时执行。
Siemens 调用、参数、枚举顺序和线程归属均未改变。

## Offline verification

Run the Core and GUI xUnit projects with `dotnet test -c Release`, and the
configuration console with `dotnet run --project
tests/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj -c Release`.
The existing Core mock workflows now exercise the channel for both implicit and
explicit bridge locations. `BridgeChannelTests` adds identity rejection before
writes, all five Studio errors, diagnostic payload parity, cancellation before
and after dispatch, timeout, concurrent calls, killed children, no replay,
handled write/read errors with continued calls, late progress and disposal checks. The shared
`worker-channel` suite still verifies the default Foundation protocol's 98 cases.

After `scripts/build/Build-Studio.ps1 -PublicApiRoot <local-sdk-root>`, run from the
repository root:

```powershell
python tools/tia-openness-studio/tests/Test-BridgeSmoke.py --bridge tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Release/net10.0-windows/bridge/TiaOpenness.Bridge.exe --public-api-root <local-sdk-root>
```

The smoke verifies exact hello identity, `session.state`, `ping`, `doctor.run`,
and clean EOF shutdown for 14sp1, 16 and 21 (15 checks). `--public-api` selects
local SDK copies for that bridge process only; no registry/install changes,
TIA session, PLC, VM, network service, or live test is involved.
