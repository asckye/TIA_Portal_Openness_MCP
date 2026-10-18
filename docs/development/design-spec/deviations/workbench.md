# 工作台偏差清单

状态：现行 · 最后核对：2026-10-11，master `3a227657`；C.0 与 UI-31、UI-32、UI-33、UI-37、UI-39、UI-40 按 2026-10-18 的 P8-80 更新（含已批准的 Ant 布局）

- 对照：[工作台界面设计规范](../workbench-ui.md) v0.2（项目红线 + WCAG 2.2 AA + Microsoft Windows 应用设计指南）；视觉层自 2026-10-18 起以 Ant Design v5 为规范，见 [C.0](#c0-ant-design-v5-视觉规范)。
- 状态：按维护者 2026-10-11 的决定填写，默认采用各条建议。维护者另外确认以下各项在 4.0 前完成：高对比主题（UI-01）、UI Automation 名称（UI-02–UI-04）、焦点管理与访问键（UI-05、UI-18）、文字对比 4.5:1 并相应提高测试门槛（UI-08、UI-49）、较小的最小窗口（UI-13）、风险确认默认取消（UI-28）、可见的预填标记（UI-25）。“4.0 前 - P8-61”的条目由任务 P8-61 实施；状态取值见 [设计规范索引](../README.md)。本文不改代码。
- 编号 UI-01…UI-49；无障碍条目在前（UI-01…UI-24），每条标注 WCAG 2.2 准则编号与级别。

## 摘要

### 按状态统计

| 状态 | 条数 | 条目 |
|---|---|---|
| 4.0 前 - P8-61 | 32 | UI-01…UI-19、UI-25…UI-30、UI-33、UI-34、UI-35、UI-42、UI-47、UI-48、UI-49 |
| 4.0 后 | 11 | UI-20、UI-21、UI-22、UI-23、UI-36、UI-38、UI-41、UI-43、UI-44、UI-45、UI-46 |
| 有意偏差（已接受） | 6 | UI-24、UI-31、UI-32、UI-37、UI-39、UI-40 |
| 合计 | 49 | |

按类别：无障碍 24 条（4.0 前 19、4.0 后 4、有意偏差 1）；审批与 AI 控制红线 6 条（均 4.0 前）；视觉体系 11 条（4.0 前 3、4.0 后 3、有意偏差 5）；状态与对话框 5 条（4.0 前 1、4.0 后 4）；本地化与文案 2 条（4.0 前）；测试与流程 1 条（4.0 前）。

成本估计：S ≤ 0.5 天，M 1–3 天，L > 3 天。4.0 前的 32 条中 S 级 18 条、S–M 或 M 级 13 条、M–L 级 1 条（UI-13）。

WCAG 2.2 A/AA 中按代码阅读存在不满足的准则共 17 条：A 级 9 条（1.1.1、1.3.1、1.4.1、2.1.1、2.4.3、2.5.2、3.1.1、3.3.2、4.1.2），AA 级 8 条（1.4.3、1.4.4、1.4.10、1.4.11、2.4.7、2.4.11、2.5.8、4.1.3）。2.2.1 按“必要”例外处理（UI-24）。

### 最重要的 10 项

1. **UI-01 不支持高对比主题**：开启 Windows 对比主题的用户仍看到固定配色，文字、焦点、选中状态可能不可辨；是 Microsoft 的基本要求，也是其他颜色修正的前提。
2. **UI-13 窗口最小 1200 × 780**：放不进 1366 × 768 和 1920 × 1080（150%）的工作区，状态栏与待批提示落到屏幕外；无法与 TIA Portal 并排贴靠（WCAG 1.4.4、1.4.10）。
3. **UI-03 输入框无关联标签（同批：UI-02 图标按钮无名称、UI-04 列表项读出类型名）**：读屏无法识别字段、按钮和审批卡；全 Gui 中 `AutomationProperties` 使用次数为 0（WCAG 1.1.1、1.3.1、3.3.2、4.1.2）。
4. **UI-05 抽屉、确认层、选项侧栏没有焦点管理**：打开后焦点留在被遮住的页面上，Esc 无效（WCAG 2.4.3、2.4.11）。
5. **UI-07 焦点指示不足**：强调按钮、安静按钮、页签聚焦时无变化，深色主题下默认点线对比约 1.21:1（WCAG 2.4.7）。
6. **UI-28 风险确认默认继续**：保存项目、断开共享会话（会结束 AI 的会话）、工作区覆盖项目、覆盖导入，回车即执行，按钮是“确定 / 是”。
7. **UI-08 文字对比不足**：时间戳与提示 2.85–3.04:1；深色主题下“关闭审批”确认按钮 2.52:1；测试把门槛固定在 2.0/2.5（WCAG 1.4.3）。
8. **UI-11 状态变化不播报**：新的待批请求、AI 操作界面、审批已关等对读屏用户不可知，待批请求会超时自动拒绝（WCAG 4.1.3）。
9. **UI-25 预填标记只用 ToolTip**：红线要求被 AI 预填的字段有可见标记和“清除预填”；现状下看不出哪些值是 AI 填的。
10. **UI-18 访问键被关闭，消息框按钮显示“_OK”“确定(_O)”字面值**：每个消息框都受影响；修复成本很低，修复时须同步 UI-29（输入守卫覆盖访问键）。

### 条目索引

| ID | 标题 | WCAG 2.2 | 建议 | 成本 | 状态 |
|---|---|---|---|---|---|
| UI-01 | 不支持高对比主题 | 无直接条款（Microsoft 要求；关联 1.4.3、1.4.11 AA） | 4.0 前 | M | 4.0 前 - P8-61 |
| UI-02 | 图标按钮与状态圆点缺少 UIA 名称 | 1.1.1 A、4.1.2 A | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-03 | 输入控件无关联标签，部分只有占位符 | 1.3.1 A、3.3.2 A、4.1.2 A | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-04 | 列表项读出类型名；行复选框无名称 | 4.1.2 A、1.3.1 A | 4.0 前 | M | 4.0 前 - P8-61 |
| UI-05 | 浮层没有焦点管理 | 2.4.3 A、2.4.11 AA、2.1.1 A | 4.0 前 | M | 4.0 前 - P8-61 |
| UI-06 | 只能用鼠标或在按下时触发的交互 | 2.1.1 A、2.5.2 A | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-07 | 焦点指示不足 | 2.4.7 AA、1.4.11 AA | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-08 | 文字对比不足 | 1.4.3 AA | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-09 | 选中与状态指示对比不足 | 1.4.11 AA、1.4.1 A | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-10 | 日志级别只靠颜色 | 1.4.1 A | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-11 | 状态变化不播报 | 4.1.3 AA | 4.0 前 | M | 4.0 前 - P8-61 |
| UI-12 | 输入框边界对比不足 | 1.4.11 AA | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-13 | 窗口最小尺寸过大、无法贴靠、DPI 未声明 | 1.4.4 AA、1.4.10 AA | 4.0 前 | M–L | 4.0 前 - P8-61 |
| UI-14 | 指针目标过小 | 2.5.8 AA | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-15 | 主窗口未提供界面语言 | 3.1.1 A | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-16 | 页面标题未标为标题；无 F6 | 1.3.1 A；2.4.1 A（软件中按 SHOULD） | 4.0 前（F6 可后） | S / M | 4.0 前 - P8-61（HeadingLevel）；F6 在 4.0 后 |
| UI-17 | 小于 12 px 的文字 | 无直接条款（Microsoft 最小字号；关联 1.4.4 AA） | 4.0 前 | M | 4.0 前 - P8-61 |
| UI-18 | 访问键被关闭，“_OK”字面值 | 无直接条款（Microsoft 键盘指南） | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-19 | 快捷键不可发现，文档与代码不一致 | 无 | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-20 | 不跟随“文本大小”；截断内容只能悬停 | 1.4.4 AA（部分） | 4.0 后 | L | 4.0 后 |
| UI-21 | 嵌套可交互元素与双重停靠点 | 2.1.1 A（效率） | 4.0 后 | M | 4.0 后 |
| UI-22 | 控件类型与用途不一致 | 4.1.2 A（技术上满足） | 4.0 后 | M | 4.0 后 |
| UI-23 | 自绘窗口按钮 | 4.1.2 A（名称随 UI-02） | 4.0 后 | M | 4.0 后 |
| UI-24 | 审批超时自动拒绝 | 2.2.1 A（必要例外） | 保留 | 不适用 | 有意偏差（已接受） |
| UI-25 | 预填标记只用 ToolTip | 1.3.1 A（部分） | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-26 | AI 操作提示不播报；AI 切页丢焦点 | 4.1.3 AA、2.4.3 A | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-27 | 关闭审批确认层 | 2.4.3 A、2.1.1 A、1.4.3 AA | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-28 | 风险确认默认继续，按钮为“确定 / 是” | 3.3.4 AA（基本满足） | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-29 | 输入守卫未覆盖将来的访问键 | 无 | 4.0 前（随 UI-18） | S | 4.0 前 - P8-61 |
| UI-30 | “允许 AI 控制界面”开关的控件与位置 | 无 | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-31 | Ant Design v5 配色与固定主色（原 Primer 配色） | 无 | 保留 | 无 | 有意偏差（已接受） |
| UI-32 | 未使用 WPF Fluent 主题（采用 Ant Design v5） | 无 | 保留（4.0） | 无 | 有意偏差（已接受） |
| UI-33 | 调色板外颜色与按主题分支的触发器 | 无 | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-34 | 审批抽屉中的 DataGrid / TreeView 未套主题 | 无 | 4.0 前 | S–M | 4.0 前 - P8-61 |
| UI-35 | 选项侧栏背后铺满不透明正文色 | 无 | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-36 | 字号 token 未使用，字号散落 | 无 | 4.0 后 | L | 4.0 后 |
| UI-37 | 随包字体代替系统字体 | 无 | 保留 | 无 | 有意偏差（已接受） |
| UI-38 | 间距不在 4 epx 网格上 | 无 | 4.0 后 | M–L | 4.0 后 |
| UI-39 | 圆角值（Ant 6 / 4 / 8） | 无 | 保留 | 无 | 有意偏差（已接受） |
| UI-40 | 文字按钮政策；字形已统一为 Icon.* | 无 | 保留（政策） | 无 | 有意偏差（已接受） |
| UI-41 | 两种主要按钮颜色、同面多个强调按钮 | 无 | 4.0 后 | S–M | 4.0 后 |
| UI-42 | 忙碌无不确定进度、无取消、关闭被静默阻止 | 4.1.3 AA（随 UI-11） | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-43 | 通知与 toast 机制 | 4.1.3 AA（随 UI-11） | 4.0 后 | M | 4.0 后 |
| UI-44 | 消息框呈现与错误文字 | 3.3.1 A、3.3.3 AA（基本满足） | 4.0 后 | M | 4.0 后 |
| UI-45 | 禁用的导航没有说明 | 无 | 4.0 后 | S | 4.0 后 |
| UI-46 | 窗口标题不反映当前页；产品名多种写法 | 2.4.2 A（软件中按 SHOULD） | 4.0 后 | S | 4.0 后 |
| UI-47 | 硬编码文字与单位 | 3.1.2 AA（轻微） | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-48 | 文案问题 | 1.3.3 A、3.2.4 AA（轻微） | 4.0 前 | S | 4.0 前 - P8-61 |
| UI-49 | 测试固定低标准值，缺少无障碍自动检查 | 无 | 4.0 前 | M | 4.0 前 - P8-61 |

## 核对方法与局限

- 方法：逐个阅读 `src/Studio/Gui` 的 XAML、主题、壳层、控制通道与本地化源码，用 grep 计数（文件:行以基线为准）；文字对比按 WCAG 相对亮度公式计算，含透明色先与底色合成。
- 未构建、未运行测试、未启动程序。按 WPF 框架行为推断、需截图或 Inspect 确认的条目标注“需核实”（UI-04 的项名称回退、UI-07 的默认焦点视觉、UI-18 的下划线显示、UI-34、UI-35）。
- 视觉参照：当前 Primer 布局的设计交接稿（本地资料，不纳入仓库）。2026-10-06 的 P6-57 渲染证据是侧栏旧布局，不代表当前界面。

---

## A 无障碍（WCAG 2.2 AA 与 Microsoft 无障碍指南）

### UI-01 不支持高对比主题

- **规则**：COL-02、COL-03、A11Y-07；[Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)；[.NET Accessibility Best Practices › Support High Contrast](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices#visual-ui-design)。
- **WCAG**：无直接条款（WCAG 不要求高对比主题，这是 Microsoft 的要求）；在用户配色下影响 1.4.3（AA）与 1.4.11（AA）。
- **位置**：`Themes/ThemeManager.cs:139-157、173-186、193-206` 只读取注册表 `AppsUseLightTheme`，只在浅色 / 深色调色板间切换；Gui 源码中 `SystemParameters.HighContrast` 与 `SystemColors` 的引用均为 0；调色板 29 个 token 全为固定十六进制（`Themes/Palette.Light.xaml:2-30`、`Palette.Dark.xaml:2-30`）；调色板外另有 3 处颜色（见 UI-33）。`ThemeSurfaceTests.cs:31-43` 断言调色板恰为这 29 个固定值。
- **用户影响**：使用 Windows 对比主题（Aquatic、Desert、Dusk、Night sky）的用户看到的仍是 Primer 配色；系统指定的前景 / 背景 / 高亮色不生效，文字、焦点与选中状态可能不可辨。
- **成本 / 风险**：M。新增 `Palette.HighContrast.xaml`（29 个 token 映射到 `SystemColors.*BrushKey` 的动态资源），`ThemeManager` 订阅 `SystemParameters.StaticPropertyChanged`；前置 UI-33；更新 `ThemeSurfaceTests` 的计数断言并新增高对比渲染用例；需在四个内置对比主题下人工检查。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-02 图标按钮与状态圆点缺少 UIA 名称

- **规则**：ICO-02、A11Y-01；[Expose basic accessibility information › Accessible name（Buttons and links）](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-name)。
- **WCAG**：1.1.1 Non-text Content（A）、4.1.2 Name, Role, Value（A）。
- **位置**：Gui 中 `AutomationProperties` 使用次数为 0。仅含图标的按钮 7 个：`MainWindow.xaml:48-56`（最小化 / 最大化 / 关闭，内容为 `Path`）、`MainWindow.xaml:154`（抽屉关闭，内容为 Segoe Fluent Icons 私用区字符）、`MainWindow.xaml:187`（toast 关闭）、`Views/EngineeringOptionsView.xaml:10`（选项侧栏关闭）、`Views/ProjectOperationsView.xaml:73`（“⋯”，只有 ToolTip）。MCP 状态圆点（`MainWindow.xaml:127`）与各 `Primer.StatusDot` 只用颜色表达状态。
- **用户影响**：读屏读出空名称或无意义的字符；关闭抽屉、窗口操作对读屏用户不可发现。
- **成本 / 风险**：S；新增约 6 个目录键。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-03 输入控件无关联标签，部分只有占位符

- **规则**：CTL-02、A11Y-02；[Expose basic accessibility information › Labels and LabeledBy](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#labels-and-labeledby)。
- **WCAG**：1.3.1 Info and Relationships（A）、3.3.2 Labels or Instructions（A）、4.1.2（A）。
- **位置**：视图中 16 个 `TextBox`、1 个 `PasswordBox`、6 个 `ComboBox`，`LabeledBy` 与 `Label.Target` 均为 0 次；可见标签是独立的 `TextBlock`。
  - 只有占位符（经 `Tag` 由模板绘制，不进入 UIA）：`ProjectOperationsView.xaml:46`（项目路径）、`BlocksView.xaml:20`（筛选）、`AiCallsView.xaml:24`（搜索）；`EngineeringOptionsView.xaml:19` 有标签但未关联。
  - 没有任何标签：`ProjectOperationsView.xaml:8`（会话选择）、`:77`（软件选择）、`:25`（只读结果框）、`EngineeringOptionsView.xaml:13`（版本选择）。
  - 分段单选没有组名关联：`SettingsView.xaml:19-20、35-37、57-58、73-75`、`AuditLogView.xaml:124-134`，读屏只听到“开启”“60s”“3”。
- **用户影响**：读屏读“编辑，空白”；输入后占位符消失，视觉用户也看不出字段含义。
- **成本 / 风险**：S–M。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-04 列表项、树节点读出类型名；行复选框无名称

- **规则**：CTL-04、A11Y-01；[Expose basic accessibility information › Accessible names from dynamic data](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-names-from-dynamic-data)。
- **WCAG**：4.1.2（A）、1.3.1（A）。
- **位置**：8 个 `ListBox`、2 个 `TreeView` 的项容器都没有设置名称；数据类型 `CallRow`、`ApprovalRow`（`ViewModels/FeaturePagesViewModel.cs:345、403`）、`BlockRow`（`ViewModels/BlockRow.cs:8`）、`DeviceNode`（`ViewModels/DeviceNode.cs:15`）未重写 `ToString()`；`LogRow` 是 record（`Controls/ResultPresentation.cs:107`），`ToString()` 为“LogRow { Time = … }”。按 WPF `ItemAutomationPeer` 的默认行为，项名称回退为数据项的 `ToString()`，即类型全名（如 `TiaOpenness.Gui.ViewModels.ApprovalRow`），**需核实**（Inspect）。程序块行的 `CheckBox`（`BlocksView.xaml:89`）无内容、无名称；AI 调用行是 `ListBoxItem` 中内容为 `Grid` 的 `Button`（`AiCallsView.xaml:77`），按钮名称为空。
- **用户影响**：审批卡、调用记录、程序块全部读成同一个类型名，读屏用户无法分辨要批准的是哪一项。
- **成本 / 风险**：M；每个列表一个本地化格式串 + `ItemContainerStyle` 绑定，新增约 8 个目录键。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-05 浮层没有焦点管理

- **规则**：DLG-02、DLG-10、KEY-04、KEY-08、A11Y-11；[Keyboard interactions › Esc key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#esc-key)。
- **WCAG**：2.4.3 Focus Order（A）、2.4.11 Focus Not Obscured (Minimum)（AA）、2.1.1 Keyboard（A）。
- **位置**：Gui 中调用 `Focus()`、`Keyboard.Focus`、`FocusManager` 的次数为 0，处理 `Key.Escape` 的次数为 0。设置 / 审批 / 调用详情抽屉（`MainWindow.xaml:144-171`；`WorkbenchShell.cs:149-163` 打开时只改可见性）、关闭审批确认层（`MainWindow.xaml:172-184`；`WorkbenchShell.cs:183`）、选项侧栏（`Views/EngineeringOptionsView.xaml:2-3`）、toast（`MainWindow.xaml:185`）都是窗口内的兄弟元素：打开后焦点留在原处，Tab 仍进入被遮住的页面，Esc 无效，关闭后焦点不归还。确认层两个按钮都没有 `IsDefault`/`IsCancel`（`MainWindow.xaml:179-180`）。
- **用户影响**：键盘用户打开抽屉后要 Tab 很多次才能进入，或在遮罩后看不见的控件上操作；关闭审批的确认不能用 Esc 取消。
- **成本 / 风险**：M；实现一个通用的模态层行为（焦点进入、Tab 循环、Esc、焦点归还），套用到 4 处。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-06 只能用鼠标或在按下时触发的交互

- **规则**：KEY-01、CMD-10、STS-01；[Keyboard accessibility › Keyboard alternatives to pointer actions](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-alternatives-to-pointer-actions-and-events-for-specific-control-elements)。
- **WCAG**：2.1.1 Keyboard（A）、2.5.2 Pointer Cancellation（A）。
- **位置**：
  1. 状态栏 MCP 状态区 `DockPanel` 用 `MouseLeftButtonUp` 打开上下文菜单，菜单中是审批开关（`MainWindow.xaml:121-126`，`WorkbenchShell.cs:184`）；该元素不可聚焦。
  2. 抽屉遮罩在 `MouseLeftButtonDown`（按下）时关闭抽屉（`MainWindow.xaml:145`）。
  3. toast 正文在 `MouseLeftButtonDown`（按下）时打开审批抽屉或环境页（`MainWindow.xaml:185`，`WorkbenchShell.cs:204`）；键盘只能点到关闭按钮。
  4. 配置页“已选 / 检测到”文字用 `MouseLeftButtonUp` 打开客户端说明（`Configuration/ConfigurationView.xaml:137`，`ConfigurationView.xaml.cs:98`）。
  5. 程序块行在 `PreviewMouseLeftButtonDown`（按下）时切换勾选（`Views/BlocksView.xaml.cs:22、56-64`）；键盘 Space 只改变列表选择、不勾选。
- **用户影响**：审批开关的快捷入口、待批 toast 的主操作、客户端说明对键盘用户不可用；按下即触发无法通过移开指针取消。
- **成本 / 风险**：S。状态栏审批菜单可以直接删除（设置抽屉已有同一开关）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-07 焦点指示不足

- **规则**：KEY-03、CTL-01、A11Y-05；[Visual feedback › High Visibility Focus Visuals](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback#high-visibility-focus-visuals)；[Styling for Focus in Controls, and FocusVisualStyle](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle)。
- **WCAG**：2.4.7 Focus Visible（AA）、1.4.11 Non-text Contrast（AA）。
- **位置**：`Primer.ButtonTemplate` 聚焦时只把 1 px 边框改为 accent（`Themes/Primer.xaml:163-165`）；`Primer.Run` 的边框本来就是 accent（`:189-195`），聚焦无变化；`Primer.Quiet` 边框为 0（`:201-209`，19 处使用），聚焦无变化；选中的分段按钮底色为 accent，聚焦同样无变化；`Primer.Tab`/`SubTab`（`:756-804`）、`Primer.TableItem`（`:333-350`）、`Primer.TableButtonTemplate`（`:537-561`）、`Primer.Expander`（`:612-639`）没有焦点触发器。项目未定义全局 `FocusVisualStyle`，只剩 Aero2 主题样式叠加的默认点线（1 px 黑色，内缩 2 px），在深色卡片 #161B22 上对比约 1.21:1，**需核实**（截图）。
- **用户影响**：深色主题下键盘焦点几乎不可见；浅色主题下强调按钮、安静按钮和页签的焦点也难以辨认。
- **成本 / 风险**：S–M；一个全局 `FocusVisualStyle`（外 2 px + 内 1 px），并去掉模板中与之重复的边框变色。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-08 文字对比不足

- **规则**：COL-04、COL-08、A11Y-06；[Accessible text requirements › Contrast ratios](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements#contrast-ratios)。
- **WCAG**：1.4.3 Contrast (Minimum)（AA）。
- **位置**（按 WCAG 公式计算）：
  - `textFaint`（#8C959F）在浅色卡片上 3.04:1，在窗口底与按钮底上 2.85:1；深色主题 3.77–4.12:1。它不只用于禁用，也用于信息性文字：时间戳（`Primer.Mono.textFaint`，7 处）、提示与说明（`Primer.Muted.textFaint`，4 处，如 `ProjectOperationsView.xaml:52` 连接提示、`AuditLogView.xaml:35` 审计局限说明、`AiCallsView.xaml:148` 密钥说明、`SettingsView.xaml:84` 包名）、输入框占位符（`Primer.xaml:236`）；`Primer.textFaint` 直接引用 16 处。
  - 深色主题警告按钮：白字在 #D29922 上 2.52:1（`Primer.xaml:647-650`）。用于关闭审批的确认按钮（`MainWindow.xaml:180`）和每个警告消息框的第一个按钮（`Controls/WorkbenchMessageBox.cs:54`）。
  - 测试把低于标准的值固定为门槛：`ThemeSurfaceTests.cs:70`（ok / diffRed / warn 在卡片上 ≥ 2.5:1）、`:71`（textFaint ≥ 2:1）。
- **用户影响**：低视力用户读不清时间戳、提示，以及深色主题下的“关闭审批”。
- **成本 / 风险**：S；信息性文字改用 `textMuted`（4.93–6.31:1），深色警告按钮改深色字或加深底色，测试门槛提到 4.5（见 UI-49）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-09 选中与状态指示对比不足

- **规则**：COL-05、COL-06、CMD-07。
- **WCAG**：1.4.11 Non-text Contrast（AA）、1.4.1 Use of Color（A）。
- **位置**：选中底色 `cardSel` 与卡片 / 按钮底对比：浅色 1.08:1 / 1.01:1，深色 1.13:1 / 1.00:1。用于映射列表选中行（`Primer.xaml:965-967`）、程序块勾选行（`:888-894`）、筛选按钮按下（`:530-535`，按下时文字从 textMuted 换为 accent，两者亮度比浅色仅 1.06:1）。程序块列表的键盘选中（`IsSelected`）没有任何视觉（`Primer.TableItem` 无触发器）。
- **用户影响**：分不清哪些筛选已开启、哪一行被选中，色弱用户尤甚。
- **成本 / 风险**：S–M；加选中边条 / 勾选标记，筛选按钮加勾选图标。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-10 日志级别只靠颜色

- **规则**：COL-06；[Color in Windows › Usability](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)。
- **WCAG**：1.4.1 Use of Color（A）。
- **位置**：`Primer.LogLevelText`（`Primer.xaml:933-951`）按 Info / Warning / Error / Debug 改文字颜色；`LogView.xaml:28` 只显示时间与消息，没有级别文字或图标。
- **用户影响**：色弱与高对比用户无法区分错误与普通日志。
- **成本 / 风险**：S；加级别前缀或图标列。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-11 状态变化不播报

- **规则**：STS-02、AI-03、APR-07、A11Y-08；[What's new in accessibility in .NET Framework › UIAutomation LiveRegion support](https://learn.microsoft.com/en-us/dotnet/framework/whats-new/whats-new-in-accessibility)。
- **WCAG**：4.1.3 Status Messages（AA）。
- **位置**：`LiveSetting` 与 `RaiseAutomationEvent` 使用次数为 0。不播报的变化：新待批 toast（`MainWindow.xaml:185`，`WorkbenchFeaturePages.cs:61-73`）、待批徽章（`MainWindow.xaml:114`）、审批已关（`:115-117`）、AI 操作提示（`:132`，`WorkbenchControl.cs:384-389`）、预填提示（`:109-113`）、忙碌状态与进度（`:138-143`）、诊断包与图集进度、MCP 服务状态（`:121-129`）。
- **用户影响**：读屏用户不知道有请求在等待批准（到时自动拒绝），也不知道 AI 改动了界面。
- **成本 / 风险**：M；一个统一的“播报”辅助方法 + 各状态源接入；单元测试断言事件已触发。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-12 输入框边界对比不足

- **规则**：COL-05（Stroke.Control）。
- **WCAG**：1.4.11 Non-text Contrast（AA）。
- **位置**：输入框、下拉框、密码框边框 `inputBorder` #D0D7DE 在白色上 1.45:1（深色 #30363D 为 1.55:1）；输入框底色与卡片相同（`Palette.Light.xaml:3、8-9`），边框是识别输入框的唯一视觉线索。复选框框线 `checkBorder` 为 3.04:1（满足）。
- **用户影响**：低视力用户找不到输入框的位置和范围。
- **成本 / 风险**：S；把 `inputBorder` 调到 ≥ 3:1，或采用 Fluent 式加深的底边。卡片边框属装饰性，不在此列。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-13 窗口最小尺寸过大；无法贴靠；DPI 未声明

- **规则**：LAY-01、LAY-02、LAY-03、LAY-10、A11Y-09；[Screen sizes and breakpoints](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#breakpoints)；[Support snap layouts](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu)；[High DPI Desktop Application Development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)。
- **WCAG**：1.4.4 Resize Text（AA）、1.4.10 Reflow（AA）。
- **位置**：`MainWindow.xaml:1` `MinWidth="1200" MinHeight="780"`；`Configuration/ConfigurationView.xaml.cs:135-136` 按工作区收缩窗口时仍以最小值为下限；6 个页面固定 300 px 右侧栏（`MainWindow.xaml:97`、`AiCallsView.xaml:6`、`AuditLogView.xaml:6`、`EnvironmentView.xaml:6`、`VersionControlView.xaml:6`、`ConfigurationView.xaml:6`）；视图中 72 个数值型固定行高；项目没有 `app.manifest`，未显式声明 DPI 感知方式（运行时实际模式需核实）。
- **用户影响**：1366 × 768（100%）的可用高度约 728 epx，1920 × 1080（150%）约 672 epx，均小于 780，状态栏、待批徽章、“审批已关”提示落到屏幕外。窗口不能缩到半屏与 TIA Portal 并排（Snap 要求窗口能放进分区）。200% 显示缩放下只有高分辨率屏幕放得下。
- **成本 / 风险**：M–L。先把最小尺寸降到 1024 × 640 并让右侧栏在窄窗口下折叠；固定 1200 × 780 画布的渲染测试不受影响，需新增窄窗口渲染用例。
- **建议**：**4.0 前修改**（至少完成 1024 × 640；侧栏折叠可分步）。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-14 指针目标过小

- **规则**：CTL-10、A11Y-13。
- **WCAG**：2.5.8 Target Size (Minimum)（AA）。
- **位置**：“⋯”项目选项按钮 20 × 16（`ProjectOperationsView.xaml:73`）；审计“跳转”按钮高 18（`AuditLogView.xaml:25`）；状态栏待批徽章按钮高 20（`Primer.xaml:714`）；“选项…”等安静按钮高 22（`ProjectOperationsView.xaml:51`、`VersionControlView.xaml:211`）；程序块行复选框 16 × 16 且无内容（`BlocksView.xaml:89`；整行点击可作等效目标，但见 UI-06）。需按 2.5.8 的间距例外逐个核实。
- **用户影响**：手部不稳或使用触控板的用户难以点中。
- **成本 / 风险**：S。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-15 主窗口未提供界面语言

- **规则**：TYP-09、LOC-05。
- **WCAG**：3.1.1 Language of Page（A）。
- **位置**：只有配置页随语言设置 `Language`（`Configuration/ConfigurationView.xaml.cs:169`）；主窗口与其余视图保持 WPF 默认的 en-US，中文界面下也是如此；`StringFormat`（`ApprovalsDrawer.xaml:98`、`EnvironmentView.xaml:97`、`CallDetailDrawer.xaml:20`）按 en-US 格式化。
- **用户影响**：辅助技术与字形选择拿到错误的语言；格式与界面语言不一致。对读屏语音选择的实际影响需核实。
- **成本 / 风险**：S；在语言切换时设置根元素 `Language`（格式化按 LOC-05 另行处理）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-16 页面标题未标为标题；无 F6 区域切换

- **规则**：A11Y-10、NAV-09；[Landmarks and headings › Usage](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings#usage)；[Keyboard accessibility › F6](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-navigation-between-application-panes-with-f6)。
- **WCAG**：1.3.1（A）；2.4.1 Bypass Blocks（A，EN 301 549 与 Section 508 认为对软件不适用，本规范按 SHOULD）。
- **位置**：`HeadingLevel` 使用 0 次；页面标题是 `FontSize="18"` 的 `TextBlock`（如 `AiCallsView.xaml:19`、`AuditLogView.xaml:19`、`EnvironmentView.xaml:21`、`BlocksView.xaml:17`、`LogView.xaml:15`），卡片标题用 `Primer.ContainerTitle`；没有 F6 处理。
- **用户影响**：读屏用户无法按标题跳转，只能逐项浏览。
- **成本 / 风险**：HeadingLevel 为 S；F6 为 M。
- **建议**：**4.0 前修改**（HeadingLevel）；F6 可在 4.0 后。
- **状态**：4.0 前 - P8-61（HeadingLevel）；F6 在 4.0 后

### UI-17 小于 12 px 的文字

- **规则**：TYP-04、TYP-02；[Typography › Typography best practices in Windows 11（Minimum values）](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#typography-best-practices-in-windows-11)。
- **WCAG**：无直接条款（Microsoft 最小字号）；与 1.4.4（AA）相关。
- **位置**：视图中小于 12 的字号属性 56 处（11.5 × 17、11 × 13、10.5 × 12、10 × 12、9.5 × 1、9 × 1），`Primer.xaml` setter 中另有 14 处，`Controls/WorkbenchLogView.cs:32` 为 11。使用最广的 `Primer.Muted`（11.5 px，111 处）与 `Primer.Mono`（26 处）都小于 12；表头 `Primer.TableHeader` 为 10 px SemiBold（14 处）；审批卡元数据 9.5 px（`ApprovalsDrawer.xaml:80`）、传输方式标签 9 px（`:21`）。小于 14 px 的 SemiBold 用法也很多（表头、输入标签、芯片）。
- **用户影响**：高分辨率屏和 VM 远程桌面下难以阅读；审批卡的关键元数据字号最小。
- **成本 / 风险**：M；集中修改 Primer 样式可覆盖大部分，但会改变版面密度，需重看渲染与 `WorkbenchRenderTests`。
- **建议**：**4.0 前修改**（先把所有小于 12 px 的文字提到 12；SemiBold ≥ 14 可随 UI-36）。
- **状态**：4.0 前 - P8-61

### UI-18 访问键被关闭，消息框按钮显示下划线字面值

- **规则**：KEY-06；[Keyboard interactions › Access keys](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#access-keys)。
- **WCAG**：无直接条款（2.1.1 可经 Tab 满足；属 Microsoft 键盘指南）。
- **位置**：按钮模板 `RecognizesAccessKey="False"`（`Primer.xaml:138`）；消息框按钮文案带助记符：`Dialog.OK` “_OK / 确定(_O)”等（`Localization/Strings.cs:112-115`），由 `Controls/WorkbenchMessageBox.cs:49-53` 绑定到使用该模板的按钮。按 WPF `ContentPresenter` 对字符串内容的处理，不识别访问键时下划线按字面显示，即按钮上是“_OK”“确定(_O)”，**需核实**（截图）。全应用没有其他访问键。
- **用户影响**：每个消息框按钮带多余符号；运动障碍用户无法用 Alt+字母操作。
- **成本 / 风险**：S；模板改为识别访问键，为对话框按钮与主要字段分配访问键；必须同时完成 UI-29。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-19 快捷键不可发现，文档与代码不一致

- **规则**：CMD-05、KEY-07；[Keyboard interactions › Accelerators](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#accelerators)；[Keyboard accessibility › Keyboard shortcuts](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-shortcuts)。
- **WCAG**：无直接条款。
- **位置**：`MainWindow.xaml:5-16` 定义 Ctrl+O/S/B 与 Ctrl+1…8（Ctrl+1 = MCP 与客户端，Ctrl+2 = 工程操作），没有任何 ToolTip 或 `AutomationProperties.AcceleratorKey` 说明。`docs/getting-started/configuration.md:5`、`docs/getting-started/beginners.zh-CN.md:80、138` 写的是 Ctrl+1 = 工程操作、Ctrl+2 = MCP 与客户端，并描述“视图 / 工具 / 帮助”菜单栏；`configuration.md:124` 写“帮助 → 更新 → 更新引擎…”；测试断言菜单栏不存在（`UnifiedDesktopTests.Menu.cs:56`）。
- **用户影响**：照文档操作会切到错误的页面或找不到菜单；读屏用户不知道有快捷键。
- **成本 / 风险**：S（文档更新须按仓库规则核对链接与路径）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-20 不跟随 Windows“文本大小”；截断内容只能悬停查看

- **规则**：A11Y-09、TYP-06、LAY-06；[Accessible text requirements › Text font size and scale](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements#text-font-size-and-scale)。
- **WCAG**：1.4.4 Resize Text（AA，部分）；1.4.13（AA）的 ToolTip 行为本身满足。
- **位置**：全局 `TextBlock` 样式默认不换行并截断（`App.xaml:19-22`）；301 个 `TextBlock` 中只有 37 个换行；28 个 ToolTip 只用于显示被截断的完整值（路径、目标、工具名），宿主 `TextBlock` 不可聚焦；没有读取 `TextScaleFactor`。
- **用户影响**：放大文字的用户看到被截断的内容；键盘用户看不到完整路径（读屏可经 HelpText 读到 ToolTip）。
- **成本 / 风险**：L（字号 token 化后统一缩放，并允许重排）。
- **建议**：**4.0 后修改**。4.0 内由 UI-13（显示缩放路径）与 UI-04（完整名称）部分缓解。
- **状态**：4.0 后

### UI-21 嵌套的可交互元素与双重停靠点

- **规则**：CTL-03、CTL-07；[Nested UI in list items › Nested UI handling](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/nested-ui#nested-ui-handling)。
- **WCAG**：2.1.1（A，可操作但低效）；名称问题见 UI-04（4.1.2）。
- **位置**：AI 调用行是 `ListBoxItem` 中的 `Button`（`AiCallsView.xaml:74-77`）：Tab 先到项再到按钮，方向键选中项后按 Enter 无效；环境检查的“修复”按钮位于 Expander 标题的 ToggleButton 内（`EnvironmentView.xaml:47-63`，`Primer.xaml:620`）；程序块行内复选框（`BlocksView.xaml:89`）。
- **用户影响**：键盘操作步骤多、行为不一致。
- **成本 / 风险**：M。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

### UI-22 控件类型与用途不一致

- **规则**：NAV-04、CTL-06。
- **WCAG**：4.1.2（A）技术上满足（角色与状态可被读出）；依据 Microsoft 控件选择指南。
- **位置**：页签用 `RadioButton` 实现（`MainWindow.xaml:66-85`，9 个）；复选框套按钮样式（`ConfigurationView.xaml:170`“显示”、`VersionControlView.xaml:124`“全部”）；“会话已锁定”用不可点击、不可聚焦的复选框表示只读状态（`ProjectOperationsView.xaml:19`）。
- **用户影响**：读屏说“单选按钮”，用户看到的是页签；“显示”看起来是按钮却读作复选框。
- **成本 / 风险**：M。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

### UI-23 自绘窗口按钮

- **规则**：ICO-05、LAY-11、COL-10；[Windows app title bar › Caption controls](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design#standard-design)。
- **WCAG**：4.1.2（A，名称部分随 UI-02 修复）。
- **位置**：`MainWindow.xaml:1-3`（`WindowStyle="None"`、`WindowChrome`，标题栏 44 epx）；`:48-56` 窗口按钮 28 × 28（`Primer.xaml:751-755`），用 `Path` 绘制，无 ToolTip；最大化后图标不切换为还原；无非激活态；未提供 HTMAXBUTTON 命中测试，因此不显示 Windows 11 Snap 布局。
- **用户影响**：与系统窗口外观和行为不一致；Snap 布局不可用。
- **成本 / 风险**：M。
- **建议**：**4.0 后修改**（名称先在 UI-02 中解决）。
- **状态**：4.0 后

### UI-24 审批超时自动拒绝

- **规则**：APR-06。
- **WCAG**：2.2.1 Timing Adjustable（A）。
- **位置**：`SettingsView.xaml:35-37`（60/120/300 秒），审批卡倒计时（`ApprovalsDrawer.xaml:96-102`）；协议允许 1–3600 秒（phase8 §1.1）。
- **用户影响**：反应较慢或使用读屏的用户可能来不及审阅。
- **偏差理由**：超时是安全设计的一部分；AI 客户端的工具调用本身有时限，把时限延长到默认值的 10 倍（1200 秒）会使请求在客户端一侧失效，适用 2.2.1 的“必要”例外；用户已可把时限调到 300 秒。阈值播报在 UI-11 中实现。
- **建议**：**有意偏差**，在 4.0 文档中写明；**MAY** 增加 600 秒选项。复审：4.1。
- **状态**：有意偏差（已接受）

## B 审批与 AI 控制红线（项目规则）

### UI-25 预填标记只用 ToolTip

- **规则**：AI-04（项目规则，phase8 §2.7：“被预填的字段显示‘AI 预填，待确认’标记和‘清除预填’按钮”）。
- **WCAG**：1.3.1（A，部分：状态只在悬停或读屏 HelpText 中可得）。
- **位置**：`Views/BlocksView.xaml.cs:43-47`、`Views/EngineeringOptionsView.xaml.cs:21-28` 只把“AI 预填，待确认”设为字段的 ToolTip（`WorkbenchControl.cs:350-359` 调用）；状态栏汇总提示与按钮存在（`MainWindow.xaml:109-113`），但按钮用 `Primer.Quiet`（11 px、聚焦无变化，见 UI-07）。
- **用户影响**：字段上看不出哪些值是 AI 填的，人可能把 AI 的值当作自己的输入去执行。
- **成本 / 风险**：S–M；字段旁的标记组件 + 清除按钮，复用现有 `_prefillRestore`。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-26 AI 操作提示不播报；AI 切页可能丢失焦点

- **规则**：AI-03、AI-07、NAV-07。
- **WCAG**：4.1.3（AA）、2.4.3（A）。
- **位置**：提示是状态栏右侧 11.5 px 次要色文字，最大宽 400 并截断（`MainWindow.xaml:132`，`WorkbenchControl.cs:384-389`）；`Navigate`（`WorkbenchShell.cs:99-130`）折叠旧页面时不处理焦点，AI 切页后焦点可能落回窗口根部。
- **用户影响**：读屏用户不知道 AI 改了界面；键盘用户的位置被重置。
- **成本 / 风险**：S–M（与 UI-11、UI-05 共用实现）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-27 关闭审批确认层

- **规则**：APR-05、DLG-02、DLG-03、DLG-04、DLG-05、COL-08；[Confirmations › Default values](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm#guidelines)。
- **WCAG**：2.4.3（A）、2.1.1（A）、1.4.3（AA，深色主题）。
- **位置**：`MainWindow.xaml:172-184`：“取消”在左、“关闭审批”在右（与 DLG-05 相反）；两按钮都不是默认 / 取消按钮；无警告图标；窗口内覆盖层，背景仍可用键盘操作；深色主题“关闭审批”白字 2.52:1。标题与按钮文案（`Strings.cs:96-98`）已符合 DLG-04/06。
- **用户影响**：这是红线界面：键盘用户不能可靠地取消，深色主题下按钮难读。
- **成本 / 风险**：S（改用统一对话框组件，或补齐模态行为与对比）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-28 风险确认默认继续，按钮为“确定 / 是”

- **规则**：DLG-04、DLG-07、APR-09；[Confirmations › Commit buttons / Default values](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm#guidelines)；[Dialog controls › Confirmation dialogs](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#confirmation-dialogs-okcancel)。
- **WCAG**：3.3.4 Error Prevention（AA）基本满足（有确认步骤）；本条依据 Microsoft 确认指南。
- **位置**：`Controls/WorkbenchMessageBox.cs:52` 第一个按钮恒为 `IsDefault`，`:54` 警告图标时第一个按钮用警告样式；`Services/WpfDialogService.cs:9-10` 只提供 OK/Cancel 与 Yes/No/Cancel。受影响的确认：保存项目（`ViewModels/EngineeringViewModel.cs:283-285`，OK/Cancel）；断开共享会话（`ViewModels/SessionViewModel.cs:179-181`，OK/Cancel，正文提到“分离面板会保持 AI 的连接”却不提供该按钮）；工作区覆盖项目（`ViewModels/VersionControlViewModel.cs:272-274`）；导入覆盖（`EngineeringViewModel.cs:230-232`，Yes/No/Cancel，正文说“否 = 更安全”，默认却是“是”，`Strings.cs:503`）。Yes/No 对话框不响应 Esc（`IsCancel` 只给 Cancel 或单按钮的 OK）。
- **用户影响**：按一下回车就会结束 AI 的会话或覆盖项目中的块。
- **成本 / 风险**：S–M；对话框 API 增加具体动词与默认按钮参数，补“仅分离面板”选项。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）

### UI-29 输入守卫未覆盖将来的访问键

- **规则**：AI-02。
- **位置**：`WorkbenchControl.cs:373-381` 只拦截鼠标与 Enter/Space；`Control/ControlInputGuard.cs:27-38`。目前没有访问键，暂无缺口；UI-18 引入访问键后，Alt+字母可能在 AI 切页后的 500 毫秒内触发新页面上的按钮。UI Automation 的 Invoke 绕过 Preview 事件，属于威胁模型之外的同用户进程（phase8 §7 风险 1），不在此处理。
- **用户影响**：防误触保护出现缺口。
- **成本 / 风险**：S。
- **建议**：**4.0 前修改**，与 UI-18 同批。
- **状态**：4.0 前 - P8-61

### UI-30 “允许 AI 控制界面”开关的控件与位置

- **规则**：AI-06、CMD-08；[Toggle switches › Choosing between toggle switch and check box](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggles#choosing-between-toggle-switch-and-check-box)。
- **位置**：`SettingsView.xaml:4` 是位于所有分节标题之前的复选框，没有说明文字；同一抽屉里的审批开关是“开启 / 关闭”分段单选（`:19-20`）。两个即时生效的二元设置用了两种控件。
- **用户影响**：不易理解开关的作用和受保护的行为，与审批开关的关系不清楚。
- **成本 / 风险**：S。
- **建议**：**4.0 前修改**（独立的“AI 控制”分节、开关语义、一句说明）。
- **状态**：4.0 前 - P8-61

## C 视觉体系

### C.0 Ant Design v5 视觉规范

- **决定**：维护者 2026-10-18 决定工作台采用 Ant Design v5 设计规范（P8-80）。Ant 没有官方 WPF 库，因此保留 WPF，只替换视觉层，做法与 P6-61 的 Primer 重建相同：ViewModel、命令、绑定、引擎调用、中英文界面文字与 UIA 名称不变；`Themes/Primer.xaml` 与 `Primer.*` 键名保留为实现名，Ant 独有的 token 用 `Ant.*` 键。
- **令牌**：浅色调色板对应 Ant 默认算法，深色对应暗色算法（布局底 #000000、容器 #141414、浮层 #1F1F1F、主色 #1668DC），高对比继续使用 Windows 系统色；逐项取值见[规范 5.1](../workbench-ui.md#51-语义-token)。浅色的种子 / 映射 / 别名 token、蓝色色板与浅深两套中性色已对照 ant.design 的 Customize Theme 与 Colors 页面核对；暗色的容器、浮层、主色以及金 / 绿 / 红色板各阶不在 ant.design 的静态页面中，按 Ant 暗色算法的输出取值。
- **WCAG 优先的偏差**（维护者 2026-10-18 接受）：
  - 主色文字、链接与浅色主要按钮用 blue-7 #0958D9（#1677FF 作文字为 4.1:1）；深色文字与链接用 #3C89E8，深色主要按钮保持 #1668DC（白字 5.2:1）。#1677FF / #1668DC 只用于图形（选中条、墨条、勾选、开关、进度、焦点边框，≥ 3:1）。
  - 实心按钮的悬停与按下取更深的色阶（浅色 #003EB3 / #002C8C，深色 #1554AD / #15417E；危险按钮 #CF1322 / #A8071A 与 #7E2E2F / #5B2526），因为 Ant 变浅的悬停色使白字降到 3.0:1；默认、描边与链接按钮的悬停文字用 #003EB3 / #65A9F3。
  - 警告文字用 gold-9 #874D00、成功文字用 green-8 #237804、错误文字用 red-7 #CF1322（Ant 的 #FAAD14、#52C41A、#FF4D4F 分别为 1.9、2.3、3.3:1）。
  - 输入框、选择框、复选框、单选框与开关关闭态的边界用 gray-7 #8C8C8C（深色 #737373），达到 3:1；Ant 的 colorBorder #D9D9D9 / #424242 只用于以文字识别的按钮与标签。
  - 次要文字（65%）同时用于占位符与弱化标签；Ant 的 45% 与 25% 不足 4.5:1。
  - 键盘焦点为 2 px colorPrimary 环加 1 px 间隙（Ant 的 4 px #91CAFF 轮廓为 1.8:1）；输入框聚焦时画 2 px 主色内环，代替 10% 透明度的 controlOutline。
  - Segmented 选中项加 colorPrimary 边框，表格与列表选中行保留 3 px 选中条（Ant 的选中底色与背景仅 1.1:1）。
  - 测试禁止 `Effect`，阴影只以三层半透明边框画在弹出层上；页面内的抽屉与对话框保留 colorBorderSecondary 边框。悬停动效 0.12 s（测试固定）。标签与筛选标签高 24 px（WCAG 2.5.8 目标尺寸），滚动条保留 24 px 命中区与行按钮。
  - 风险确认按钮（`Primer.WarningAction`）改为 Ant 实心危险按钮（红色）。
- **其他决定**：深色布局底保持 Ant 的 #000000（卡片 #141414）；待批徽章底改为 colorWarningBg #FFFBE6（warn 文字 6.5:1）；测试中固定 Primer 交接取值的断言改为 Ant 取值（`ThemeSurfaceTests` 的交接 token 与警告计数色、`PrimerInteractionTests` 的徽章底色、`TiaOpenness.Configuration.Tests` 的窗口底色），对比、硬编码颜色、焦点、禁用效果与几何检查保持不变。
- **布局**（维护者 2026-10-18 批准）：左侧 Sider 216 epx（分组菜单，宽度低于 992 epx 时收为 56 epx 图标栏，收起按钮可覆盖，越过断点后恢复自动）；Header 56 epx（面包屑“分组 / 页名”、MCP 状态与会话标签按优先级在右端截断、待批、诊断、语言与窗口按钮）；每页 PageHead（标题、说明与操作）；“项目”页合并原项目操作与程序块（选择以 Alert 显示批量操作，右栏为编译、检查、环境体检与日志卡片）；AI 调用页四个 Statistic 卡片、Segmented 筛选（各筛选仍可组合，“全部”清除）、版本选择、搜索与“跟随最新”开关；环境体检页为警告 Alert 与按组的 Collapse 列表，菜单显示需处理数；MCP 页为 Steps（按检测与服务状态标当前步骤）、两张竖排标签的表单卡片与客户端复选卡片；待批抽屉 480 epx，请求为小卡片，按钮顺序“查看完整参数”（链接）、“拒绝”、“批准执行”（主要）；调用详情抽屉 520 epx；对话框按钮按视觉顺序排列，肯定操作在最右。

### UI-31 Ant Design v5 配色与固定主色（原 Primer 配色）

- **规则**：COL-07；[Color in Windows › Accent color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#accent-color)。
- **位置**：`Themes/Palette.Light.xaml`、`Palette.Dark.xaml`（60 个 token：36 个 `Primer.*` 与 24 个 `Ant.*`，取值来自 Ant Design v5 并按 C.0 调整，`ThemeSurfaceTests` 固定）；主色 #0958D9 / #3C89E8（图形 #1677FF / #1668DC），不使用 Windows 强调色。
- **用户影响**：与系统其他应用的强调色不一致；对比已达标（作文字 5.2–9.0:1）。
- **偏差理由**：维护者 2026-10-18 采纳 Ant Design v5 作为视觉规范（此前为 Primer 设计交接）；固定的主色便于在 VM 与远程桌面中识别工作台。
- **建议**：**有意偏差**；条件：高对比映射保持完整，`onAccent` 与主要按钮文字对比保持 ≥ 4.5:1。
- **状态**：有意偏差（已接受）

### UI-32 未使用 WPF Fluent 主题

- **规则**：PLT-01；[What's new in WPF for .NET 9 › Fluent theme](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90#fluent-theme)。
- **位置**：`App.xaml` 未设置 `ThemeMode`（默认 Aero2）；`Themes/Primer.xaml`（1306 行）重写了按钮、输入框、下拉框、复选框、列表、页签等模板。
- **用户影响**：得不到 Fluent 主题自带的强调色、高对比与焦点视觉，需要自行补齐（UI-01、UI-07）。
- **偏差理由**：4.0 前整体换主题风险过大（全部渲染测试、`PrimerVocabularyTests` 与交接设计都要重做）。2026-10-18 起视觉规范为 Ant Design v5（C.0），控件模板仍由 `Themes/Primer.xaml` 自绘，焦点、禁用与高对比按 C.0 补齐。
- **建议**：**有意偏差（4.0）**，4.0 后评估迁移。
- **状态**：有意偏差（已接受）

### UI-33 调色板外的颜色与按主题分支的触发器

- **规则**：COL-01、COL-11。
- **位置**：`Primer.xaml:125` `Primer.CountWarningLight`（#FFF1E5，`StaticResource`）；`:700-709`、`:710-730` 用 `DataTrigger` 绑定 `EffectivelyDark` 切换底色；`:644`、`:649` `Foreground="White"`；遮罩用正文色加 0.3 不透明度（`:1126-1129`）。`ThemeSurfaceTests.cs:19` 专门放行 #FFF1E5。
- **用户影响**：高对比主题下这些颜色不会被替换。
- **P8-80 进展**：#FFF1E5 已改为调色板 token `Primer.countWarningBg`（colorWarningBg #FFFBE6），遮罩改用 `Ant.colorBgMask`，模板中不再按 `EffectivelyDark` 分支。
- **成本 / 风险**：S。
- **建议**：**4.0 前修改**（UI-01 的前置）。
- **状态**：4.0 前 - P8-61

### UI-34 审批抽屉中的 DataGrid / TreeView 未套主题

- **规则**：CTL-08。
- **位置**：`Views/ApprovalsDrawer.xaml:61-65`（TreeView）、`:66-73`（DataGrid，列头“ID”为字面值）；`Primer.xaml` 没有 DataGrid 样式，TreeViewItem 只设置前景色（`:837-839`）。
- **用户影响**：审批生成计划时，深色主题下可能出现 Aero2 白底表格，**需核实**（渲染）；这是审批界面。
- **成本 / 风险**：S–M。
- **建议**：**4.0 前修改**（先渲染核实）。
- **状态**：4.0 前 - P8-61

### UI-35 选项侧栏背后铺满不透明的正文色

- **规则**：COL-01、DLG-10。
- **位置**：`Views/EngineeringOptionsView.xaml:2` `OptionsOverlay` 的 `Background="{DynamicResource Primer.text}"`，未设不透明度（抽屉遮罩 `Primer.Overlay` 为 0.3，`Primer.xaml:1126-1129`）；没有渲染测试覆盖选项侧栏打开的状态。
- **用户影响**：按代码推断，打开“选项…”时，侧栏左侧的页面区域在浅色主题下为近黑色、深色主题下为近白色，**需核实**（渲染）。
- **成本 / 风险**：S。
- **建议**：**4.0 前修改**（先渲染核实）。
- **状态**：4.0 前 - P8-61

### UI-36 字号 token 未使用，字号散落

- **规则**：TYP-02、TYP-03；[Typography in Windows › Type ramp](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#type-ramp)。
- **位置**：`Primer.xaml:12-58` 定义的 `Ui.FontSize.*` 与 `Ui.Text.*` 在 Primer 之外使用 0 次；视图中 125 个字面 `FontSize`、15 种字号（9–22）；全局正文 12.5（`MainWindow.xaml:24`），Fluent Body 为 14；页面标题 18（Fluent Subtitle 为 20）；对话框标题 15（`WorkbenchMessageBox.cs:28`、`MainWindow.xaml:176`）；抽屉标题 14（`MainWindow.xaml:156`）。`WorkbenchRenderTests.cs:70`、`:80` 固定了 12 px 字号与 830 px 宽度。
- **用户影响**：层级不清，无法统一缩放；新代码容易延续字面值。
- **成本 / 风险**：L（涉及大部分视图与渲染测试）。
- **建议**：**4.0 后修改**；从现在起新代码 MUST 遵守 TYP-03。小于 12 px 的部分在 UI-17 中 4.0 前处理。
- **状态**：4.0 后

### UI-37 随包字体代替系统字体

- **规则**：TYP-01；[Typography in Windows › Languages](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#languages)。
- **位置**：`Primer.xaml:10-11`：`Segoe UI, Noto Sans SC`（随包 `Fonts/NotoSansSC-Regular.otf` 与 `-Bold.otf` 共约 16.9 MB）、`JetBrains Mono, Consolas`。Microsoft 建议 Segoe UI Variable + Microsoft YaHei UI。
- **用户影响**：中文字形与其他 Windows 应用略有不同；包体增大。
- **偏差理由**：交接设计指定；随包字体在 VM 与不同语言版本的 Windows 上字形一致，截图与验收证据稳定。Ant Design 建议使用平台字体，2026-10-18 改用 Ant 规范时维护者决定保留随包 Noto Sans SC 与 JetBrains Mono（`WorkbenchResourceTests` 固定字体链）。
- **建议**：**有意偏差**；4.0 后评估改用系统字体。
- **状态**：有意偏差（已接受）

### UI-38 间距不在 4 epx 网格上

- **规则**：LAY-04、LAY-05；[Screen sizes and breakpoints › Multiples of four](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#multiples-of-four)。
- **位置**：视图中 271 个字面 `Margin`/`Padding`，约 108 个分量为 6、10、14、22、3、5、18、7 等非 4 倍数；25 处 14 px 的间隔行列；卡片内边距 18,16（`Primer.xaml:99`）。
- **用户影响**：125%/150%/175% 缩放下边缘可能发虚，间距节奏不一。
- **成本 / 风险**：M–L。
- **建议**：**4.0 后修改**；新代码 MUST 遵守。
- **状态**：4.0 后

### UI-39 圆角值

- **规则**：LAY-08；[Geometry in Windows 11 › Rounded corners](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry#rounded-corners)。
- **位置**：按 Ant 的 borderRadius：控件 6、小号控件与标签 4、卡片 / 抽屉 / 对话框 / 弹出层 8（`Ui.Radius*`）；计数徽章 10。
- **偏差理由**：Ant Design v5 规范（C.0）；与 Windows 11 的 4 / 8 差异小，无可用性影响。
- **建议**：**有意偏差**。
- **状态**：有意偏差（已接受）

### UI-40 图标字形来源混杂

- **规则**：ICO-01、ICO-03。
- **位置**：Segoe Fluent Icons 私用区字符直接写在模板与视图中（`Primer.xaml:211、267`，`MainWindow.xaml:154、187`，`EngineeringOptionsView.xaml:10`）；Unicode 近似字符“✓”（`Primer.xaml:307、1022`）、“⌄/⌃”（`:926、929`）、“⋯”（`ProjectOperationsView.xaml:73`）、“!”（`MainWindow.xaml:189`）；`Themes/Icons.xaml:14、16` 定义的 `Icon.Check`、`Icon.ChevronDown` 未被使用。
- **偏差理由**：以文字为主的按钮是交接设计的决定（`Icons.xaml` 注释），保留。
- **P8-80 进展**：勾号、展开 / 滚动箭头、溢出省略号、提示“!”与 Alert / Empty 图形已改为 `Themes/Icons.xaml` 的命名几何（`Icon.*`），自动化名称不变；Segoe Fluent Icons 私用区字符只剩关闭类按钮。
- **建议**：**有意偏差（文字按钮政策）**；剩余私用区字形在 4.0 后统一，ICO-01 对新代码 MUST。
- **状态**：有意偏差（已接受）

### UI-41 两种主要按钮颜色、同一面多个强调按钮

- **规则**：CMD-02；[Buttons › Recommendations](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#recommendations)。
- **位置**：`Primer.Primary` 绿色（`Primer.xaml:640-646`，用于 `ConfigurationView.xaml:182`）与 `Primer.Run` 青色（`:189-195`）在配置页同屏出现；项目选项侧栏同时显示两个强调按钮“连接”“打开”（`EngineeringOptionsView.xaml:28-29`）。
- **用户影响**：主操作不明确。
- **成本 / 风险**：S–M。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

## D 状态、进度与对话框

### UI-42 忙碌状态无不确定进度、无取消、关闭被静默阻止

- **规则**：STS-03、STS-04、STS-05；[Guidelines for progress controls › Types of progress](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls#types-of-progress)。
- **WCAG**：4.1.3（AA，进度状态；播报随 UI-11）。
- **位置**：全局忙碌区的 `ProgressBar` 绑定 `Maximum=ProgressMax`（`MainWindow.xaml:141`），没有进度信息时 `ProgressMax=0`（`Services/WorkbenchActivity.cs:97`），进度条为空，未使用 `IsIndeterminate`；忙碌时关闭窗口被静默取消（`MainWindow.xaml.cs:27`）；除 MCP 面板外没有取消入口。
- **用户影响**：连接、编译、保存期间看不出程序在工作；点关闭没有反应，用户可能强行结束进程（TIA 会话仍在）。
- **成本 / 风险**：S。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-43 通知与 toast 机制

- **规则**：STS-06、STS-07、STS-09；[InfoBar › Is this the right control?](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#is-this-the-right-control)。
- **WCAG**：4.1.3（AA，播报随 UI-11）。
- **位置**：toast 只有一个位置，诊断反馈与待批提示共用（`WorkbenchFeaturePages.cs:56-60、65-67、74-78`），后到的覆盖先到的；系统通知用 WinForms `NotifyIcon` 气泡（`Services/ApprovalNotification.cs:12-26`）；持续状态（审批已关、控制通道不可用）只在状态栏用小字显示。
- **用户影响**：待批提示可能被诊断反馈覆盖；持续状态不醒目。
- **成本 / 风险**：M。字面值“TIA Workbench”并入 UI-47。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

### UI-44 消息框呈现与错误文字

- **规则**：DLG-01、DLG-07、ERR-01、ERR-02；[Writing style › Error messages](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#error-messages)；[Error Messages（Win32 UX Guide）](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-error#guidelines)。
- **WCAG**：3.3.1 Error Identification（A）、3.3.3 Error Suggestion（AA）基本满足（输入错误有文字与建议，如 `Strings.cs:297`）。
- **位置**：`Controls/WorkbenchMessageBox.cs` 不显示图标（`:14-61`），错误时整段正文染红（`:36`）；“未预期的错误”标题（`Strings.cs:511`）也用于预期内的错误（`SettingsView.xaml.cs:115` 目录不存在；`WorkbenchShell.cs:138、169、196`）；直接显示 `ex.Message`；启动与网络助手用系统 `MessageBox`，标题为“TIA Portal”（`App.xaml.cs:33、85、130`）；全应用约 20 处模态消息框，其中部分是可以内联显示的上下文错误。
- **用户影响**：错误看起来比实际严重，外观与措辞不一致。
- **成本 / 风险**：M。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

### UI-45 禁用的导航没有说明

- **规则**：NAV-05；[Navigation basics › Clarity](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation)。
- **位置**：未打开项目时“程序块”“版本控制”页签禁用（`WorkbenchShell.cs:77`），没有提示；Ctrl+3 / Ctrl+4 静默无效（`WorkbenchShell.cs:134`）。
- **用户影响**：用户不知道为何进不去。
- **成本 / 风险**：S。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

### UI-46 窗口标题不反映当前页；产品名多种写法

- **规则**：NAV-03、LOC-07。
- **WCAG**：2.4.2 Page Titled（A；EN 301 549 认为对软件不适用，本规范按 SHOULD）。
- **位置**：`Window.Title` 固定为 `App.Title`“TIA Portal 工作台”（`MainWindow.xaml:1`，`Strings.cs:334`），当前页只显示在标题栏的 `TextBlock` 中（`WorkbenchShell.cs:82-87`）；产品名还有“TIA 工作台”（`Shell.Brand`）与“TIA Workbench”（`SettingsView.xaml.cs:25`、`ApprovalNotification.cs:20`）。
- **用户影响**：任务栏与读屏读到的标题不含页名；产品名不统一。
- **成本 / 风险**：S。
- **建议**：**4.0 后修改**。
- **状态**：4.0 后

## E 本地化与文案

### UI-47 硬编码文字与单位

- **规则**：LOC-01、LOC-03；[Globalization and localization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization)。
- **WCAG**：3.1.2 Language of Parts（AA，轻微：中文界面中出现普通英文词）。
- **位置**：“ · Software”（`ProjectOperationsView.xaml:82`、`Controls/ResultPresentation.cs:113`）；“PLC”标签与“ID”列头（`ApprovalsDrawer.xaml:37、68`）；“60s/120s/300s”（`SettingsView.xaml:35-37`）；“{0}s”（`ApprovalsDrawer.xaml:98`）；“TIA Workbench ”（`SettingsView.xaml.cs:25`）；托盘“TIA Workbench”（`Services/ApprovalNotification.cs:20`）；消息框标题“TIA Portal”与“IO_FAILED: ”（`App.xaml.cs:33、85、126`）；“MCP · TIA Portal ”拼接（`Configuration/ConfigurationView.xaml.cs:236`）。产品名（如“TIA VCI”，`VersionControlView.xaml:34`）与 URL 可保留。
- **用户影响**：中文界面混入英文；单位不随语言变化。
- **成本 / 风险**：S（新增目录键须通过 `StringsTests` 与 `SourceConsistencyTests`）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

### UI-48 文案问题

- **规则**：TYP-05、TXT-02、TXT-03、TXT-06、LOC-06、LOC-07、LOC-09、CMD-09；[Writing style](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style)。
- **WCAG**：1.3.3 Sensory Characteristics（A，轻微）、3.2.4 Consistent Identification（AA，软件中按 SHOULD）。
- **位置**：
  - 英文全大写：`Strings.cs:120`（“ENGINEERING LINK CONSOLE”）、`:232-237`、`:258`（表头 NAME / TYPE / NO. / LANG / GROUP PATH / CONSISTENT / FILE）、`:509`（OVERWRITES、INTO）。
  - “点击 / click”：`Strings.FeaturePages.cs:90、160`，`Strings.cs:105、181、200、208`。
  - 中英不对等：`Strings.cs:105`（中文多出“点击生成”）、`:48`（“Keep values”对“确认预填”）。
  - 术语混用：中文目录中“项目”26 处、“工程”19 处、“博途”5 处。
  - 中文半角逗号：`Strings.cs:341`。
  - 同一功能不同叫法：“导出 · SimaticML XML”与“导出”（`Blocks.Export`）、“导入 XML（覆盖）”与“导入”。
  - 用位置指代：`Calls.EmptyHelp`“连接信息见右侧”（`Strings.FeaturePages.cs:31`）。
- **用户影响**：一致性与专业感下降；键盘与读屏用户被“点击”误导。
- **成本 / 风险**：S（只改文案；`StringsTests` 中的交接文案断言需同步）。
- **建议**：**4.0 前修改**。
- **状态**：4.0 前 - P8-61

## F 测试与流程

### UI-49 测试固定了低于标准的值，缺少无障碍自动检查

- **规则**：A11Y-06、A11Y-14；[Accessibility checklist › 6](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)。
- **位置**：`ThemeSurfaceTests.cs:70-71`（对比门槛 2.5 与 2.0）、`:31-32`（固定 29 个颜色值）、`:19`（放行调色板外颜色）；`PrimerVocabularyTests.cs:43` 固定动画时长；`WorkbenchRenderTests.cs:70、80` 固定字号与宽度；没有 UIA 名称、标签、焦点、目标尺寸方面的测试。2026-10-06 的 P6-57 渲染证据（本地资料，不纳入仓库）是旧布局的渲染，不能作为当前界面的视觉证据。
- **用户影响**：修正 UI-08 等问题会被现有测试阻挡；无障碍回归无法被自动发现。
- **成本 / 风险**：M（按仓库规则，测试源码变动后需完成对应构建与全部版本检查）。
- **建议**：**4.0 前修改**（随 UI-02/03/04/08 一起调整测试，并新增 §15.1 的 UIA 树测试）。
- **状态**：4.0 前 - P8-61（维护者 2026-10-11 另行确认）
