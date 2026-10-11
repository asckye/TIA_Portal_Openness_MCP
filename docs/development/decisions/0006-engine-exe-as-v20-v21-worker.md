# 0006 复用引擎 EXE 作为 V20/V21 的 worker，引擎宿主层退役

- 状态：已采纳
- 日期：2026-10-08
- 决策者：主导者受维护者委托
- 置信度：高
- 相关：[阶段 7 设计](../design/p7-host-worker-split.md) 第 0、2、3 节；[重构计划](../refactor-plan.md) P7-01、P7-08；[4.0 发布说明草稿](../../releases/v4.0.0.md)“Product and bundle layout”；[ADR 0005](0005-v20-v21-on-foundation-host.md)、[0009](0009-port-v20-v21-tools-to-foundation-adapters.md)

回溯记录（写于 2026-10-11）。

## 背景

ADR 0005 决定 V20/V21 改用 FoundationHost。V20/V21 有数百个专有工具，实现位于引擎中，调用 Siemens API 的代码与引擎自己的 MCP 宿主层（stdio、HTTP、隔离模式）交织在一起。P6-54 原计划让引擎以 `--isolate-openness` 自行实现“宿主加 worker”。

## 选项

1. 先把 V20/V21 专有工具重写为 Foundation 适配器，再切换宿主：一次到位；工作量大，4.0 前无法完成。
2. 复用引擎 EXE 作为 worker，工具与服务中调用 Siemens API 的代码作为库在 worker 中复用：改动集中在宿主与 worker 的接缝；引擎代码暂时保留，逐族移植另行进行。
3. 沿用 P6-54 的引擎自带隔离：两套宿主依然存在。

## 决定

采用选项 2。引擎 EXE 以 `--engine-worker` 模式作为 V20/V21 的 worker，经 WorkerChannel 的 Engine 配置（`engine.status`、`engine.invoke`）调用；工具目录在构建时由同一二进制以 `--write-tool-catalog` 导出为 `worker/tool-catalog.json`，宿主核对 worker 的哈希。引擎自己的 stdio/HTTP 宿主层与隔离模式退役（P7-08），P6-54 被取代；引擎 EXE 继续作为 V20/V21 的 CLI。

## 后果

- 正面：V20/V21 的工具契约与响应基线可以保持逐字节不变；宿主规则在 FoundationHost 中只有一份。
- 负面与取舍：V20/V21 专有工具在 4.0 中仍由引擎代码执行，宿主侧走链接进 EngineHost 的引擎宿主层包装链，而不是 `FoundationV4Tool`。`--isolate-openness` 等开关已删除，旧文档中的相关说明须改正（DOC-01，P8-63）。
- 后续工作：逐族移植与引擎退役见 ADR 0009（P8-03、P8-04）。
- 验证方式：契约与响应快照；`--write-tool-catalog` 生成的目录与 worker 哈希核对；无参数运行引擎 EXE 时打印用法并以 64 退出。
