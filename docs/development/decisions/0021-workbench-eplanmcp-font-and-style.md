# 0021 Workbench font and style aligned with the EplanMCP Workbench

- 状态：已采纳
- 日期：2026-10-22
- 决策者：维护者
- 置信度：高
- 相关：[ADR 0018](0018-workbench-ant-design.md)；[工作台规范](../design-spec/workbench-ui.md)；P8-80d；EplanMCP ADR-0017（`docs/adr/0017-workbench-wpf-tia-theme.md`，第 4、5 条）及其 `docs/standards/workbench-ui.md` 的 TYP-01、TYP-10、TYP-11、NAV-03、APR-02

## 背景

EplanMCP 从本仓库 `c89b7c20` 复制了 Workbench 的 Ant Design v5 主题，并按维护者决定采用 Maple Mono CN 与更严格的审批按钮样式。维护者于 2026-10-22 要求本项目的 GUI 字体与设计风格也与 EplanMCP Workbench 统一。本决定补充 ADR 0018，取代偏差 UI-37 的 Noto Sans SC / JetBrains Mono 嵌入方案。

## 选项

1. 保留 Noto Sans SC 与 JetBrains Mono：布局改动少，但两个 Workbench 的字体与审批选择的视觉权重不同。
2. 沿用共同的 Ant 主题，采用 EplanMCP 的私有字体加载、连字、导航选中和审批按钮规则：满足维护者要求，需要为较宽的等宽字形调整布局和交付资源。

## 决定

采用选项 2。

- `Ui.Font` 与 `Ui.FontMono` 统一使用 Maple Mono CN v7.9，仅带 Regular 与 SemiBold 两个未经修改的官方字重（SIL OFL 1.1）。源文件及许可证在 `src/Studio/Gui/Fonts/`，来源与 SHA-256 在该目录的 `SOURCES.txt`。
- 两种 `TiaSharedAdapterPaths` 构建都将字体及 `LICENSE.txt` 复制到可执行文件旁的 `Fonts/`，发布包保留在 `runtime/studio/Fonts/`；不安装，不嵌入为 WPF Resource。采用 EplanMCP `WorkbenchFonts.cs` 验证过的文件夹 base URI 与 `./#Maple Mono CN` 形式；不用 `pack://siteoforigin` 或仅家族名。
- UI 回退按顺序为 Segoe UI Variable Text、Segoe UI、Microsoft YaHei UI；等宽回退为 Cascadia Mono、Consolas、Microsoft YaHei UI。字体目录缺失或家族未找到时，只用系统回退。保留两个 token，便于技术文本继续按语义选择样式。
- 所有 Workbench 窗口、对话框关闭标准连字、上下文连字与上下文替代（TYP-11）。侧栏选中项使用 SemiBold 并保留选中条（NAV-03）。
- 审批的拒绝和批准使用相同的 secondary 样式、纯文本标签，无访问键，既不是默认按钮也不是取消按钮。两者处于等宽单元格，长标签换行时一起增高（本项目 APR-04，对齐 EplanMCP APR-02）。本项目的审批抽屉、自动打开、倒计时、输入守卫和 AI 控制行为照旧。
- 布局容纳等宽拉丁字形，文字控件以最小宽度代替会截断的固定宽度；允许页头动作、卡片说明、表头和按钮换行。按 TYP-10 检查 1024、1044×780、1200、1366 epx、中英及浅深主题。

## 后果

- 正面：两个 Workbench 共享字体、类型层级和 Ant 观感；审批两个选择具有相同视觉权重；技术文本原样显示。
- 负面与取舍：两个字重共约 37.2 MB；等宽拉丁文字需要更多横向空间，部分卡片可能增高；缺失字体时系统回退的外观不同。
- 后续工作：UI-37 更新为本决定；保留原布局、主题、对比度和固定卡片高度断言。维护者在 P8-80d follow-up 1 批准将退休字体的既有断言同步为 Maple Mono CN；仅替换期望字体、文件与数量，逐项差异列在任务结果中。其余断言与阈值不得自行修改。
- 验证方式：增加私有字体加载 / 回退 / 实际字重、窗口连字、导航与审批按钮测试，以及字体原始字节和包内必需资源测试。按 P8-80d 分工，本次只构建 GUI 与测试的两种共享适配器模式及改动的发布测试工程；负责人运行测试、截图和 fast gate。
