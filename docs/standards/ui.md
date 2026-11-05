# 工作台界面规范

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本文是自动化工程工作台（`src/Studio/Gui`）唯一的界面规范，包括设计令牌、布局、组件、审批交互、无障碍、文案和偏差登记；它取代本文件的旧版（v0.4，见 Git 历史）与[工作台偏差清单](../archive/development/design-spec/deviations/workbench.md)。

## 1 范围与依据

- 适用范围：`src/Studio/Gui`（`TiaOpenness.exe`，WPF，.NET 10，`net10.0-windows`）的全部界面：主窗口、侧边栏与工具栏、8 个一级页面、设置页、确认面板与对话框、提示条、toast 与系统通知、`--mock` 离线演示。测试在 `tests/Studio/TiaOpenness.Gui.Tests`。
- 视觉与交互的默认值来自共用的 [EplanMcp 工作台设计系统](https://claude.ai/artifact/Kbjk7iPWb7cMGxdVLbaQsg)（以 Apple HIG 为基础），通用规则见手册[第 21 节 界面设计](engineering-handbook.html#ui)。本文只写本项目的取值、例外和 WPF 落实方式。设计系统取代了 [ADR 0018](decisions/0018-workbench-ant-design.md) 的 Ant Design v5 和 [ADR 0021](decisions/0021-workbench-eplanmcp-font-and-style.md) 的 Maple Mono 字体，决定与逐条例外（C1–C17）见 [ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md)。
- 实施状态：4.0.0-beta.3 只含 UI-2a（token、主题、字体、焦点环）；UI-2b（外框、导航、设置页、提示条）、UI-2c（审批页）、UI-2d（其余页面与用词）在 4.0.0-beta.4。本文写目标状态，现状差距登记为偏差 UI-50。

冲突时按下列顺序取上位：

| 顺序 | 依据 | 本文位置 |
|---|---|---|
| 1 | 审批与 AI 控制红线：[工作台控制设计](../archive/development/design/workbench-control-and-shared-session.md)、[ADR 0003](decisions/0003-workbench-approval-for-mcp-writes.md) | §5 |
| 2 | WCAG 2.2 A/AA（按 WCAG2ICT 用于桌面软件），以及本文的键盘、焦点、高对比和本地化规则 | §6、§7 |
| 3 | ADR 0028 对设计系统的例外 | 各节标注 C 编号 |
| 4 | EplanMcp 工作台设计系统与手册第 21 节 | §2–§4 |
| 5 | Microsoft Windows 应用设计与 WPF 指南（设计系统未覆盖的平台行为：对话框、访问键、F6、DPI、UI Automation） | §4.1、§6 |

用词与编号：

- MUST / SHOULD / MAY 的含义同手册。新代码不满足 MUST 不合并；存量差异只能经 §8 的偏差条目处理（改正，或记为有意偏差并写明理由与复审时间）。
- 规则前缀：LAY 布局、NAV 导航、PLT 平台、TYP 字体、COL 颜色与主题、ICO 图标、CMD 命令、CTL 控件、DLG 对话框与确认、STS 状态与通知、ERR 错误与空状态、APR 审批、AI AI 控制界面、KEY 键盘与焦点、A11Y 可测的无障碍要求、LOC 本地化、TXT 界面文字；UI-nn 是偏差编号。
- 编号只在本文内有效，与手册的同名编号无关；引用手册规则时写“手册 UI-16”。删除的编号不复用。
- 技术实现名（`Primer.*`、`Ant.*` 资源键，`Themes/Primer.xaml`）是 UI-2a 为资源兼容保留的旧名，不代表采用 Primer 或 Ant 的视觉。

外部依据（2026-10-11 核对；部分 `/windows/apps/design/...` 地址会重定向到 `/windows/apps/develop/...`）：

| 来源 | 用于 |
|---|---|
| [WCAG 2.2](https://www.w3.org/TR/WCAG22/)、[WCAG2ICT（2025-12-11 Group Note）](https://www.w3.org/TR/wcag2ict-22/) | 无障碍底线及其对非 Web 软件的解释 |
| [Keyboard interactions](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions)、[Keyboard accessibility](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility) | 焦点、访问键、快捷键、F6 |
| [Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes) | 高对比主题与 SystemColors 配对 |
| [Expose basic accessibility information](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information)、[.NET Accessibility Best Practices](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices) | UIA 名称、标签、角色与值 |
| [Dialog controls](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs)、[Confirmations（Win32 UX Guide）](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)、[Error Messages（Win32 UX Guide）](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-error) | 对话框、风险确认、错误信息 |
| [Styling for Focus / FocusVisualStyle](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle)、[High DPI desktop development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)、[What's new in accessibility in .NET Framework](https://learn.microsoft.com/en-us/dotnet/framework/whats-new/whats-new-in-accessibility) | WPF 焦点视觉、DPI、LiveSetting 与 SizeOfSet |
| [Writing style](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style)、[Globalization and localization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal) | 文案与本地化 |

## 2 设计令牌

token 名称取自设计系统；WPF 实现在 `Themes/Palette.Light.xaml`、`Palette.Dark.xaml`、`Palette.HighContrast.xaml`（颜色）和 `Themes/Primer.xaml`（字号、行高、尺寸、圆角、间距），字体由 `Themes/WorkbenchFonts.cs` 提供。需要新值时先加入设计系统，两个产品一起用，再在这里登记。

| ID | 级别 | 规则 |
|---|---|---|
| COL-01 | MUST | 颜色只通过语义 token 以 `DynamicResource` 引用；十六进制颜色只出现在调色板文件中（`ThemeSurfaceTests` 与 `PrimerVocabularyTests` 扫描）。 |
| TYP-03 | MUST NOT | 在视图 XAML 或代码中写字面 `FontSize`；只引用字号 token 或基于 token 的样式。 |
| LAY-04 | SHOULD | 尺寸、外边距、内边距取 4 epx 的倍数（文字本身除外）；不由测试强制。 |

### 2.1 颜色

高对比列是运行时映射的 `SystemColors` 键（[ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md)：每个值都映射到 Window、WindowText、Highlight、HighlightText、HotTrack、GrayText、ButtonFace），不是固定色值。

| token | 浅色 | 深色 | 高对比 | 实现键 | 用途 |
|---|---|---|---|---|---|
| bg-window | #FFFFFF | #1E1E1E | Window | `Primer.window` | 窗口主体、列表区与详情区 |
| bg-grouped | #F2F2F7 | #000000 | Window | `Primer.bg` | 分组列表页（如设置页）底色，上放 bg-elevated 卡片 |
| bg-elevated | #FFFFFF | #1C1C1E | Window | `Primer.card`、`Primer.menuBg` | 分组卡片、确认面板、弹出层 |
| bg-sidebar | #EBEBED | #2A2A2C | Window | `Primer.sidebar` | 侧边栏，只放导航和全局状态 |
| fill-control | #E5E5EA | #3A3A3C | ButtonFace | `Primer.pill` | 非默认按钮底（拒绝、取消、写入请求的批准）、关闭态开关轨道 |
| fill-track | #F2F2F7 | #2C2C2E | Window | `Primer.input`、`Primer.cardSoft` | 分段控件与搜索框底槽 |
| fill-selected-inactive | #E3E3E8 | #3A3A3C | Highlight（文字用 HighlightText） | `Primer.cardSel` | 窗口非激活时的选中行、侧边栏当前项 |
| separator | #D1D1D6 | #38383A | WindowText | `Primer.cardBorder`、`Primer.divider` | 装饰性 1 px 分隔线 |
| border-control | #86868B | #8E8E93 | WindowText | `Primer.inputBorder`、`Primer.checkBorder` | 输入框、复选框等控件边界（≥ 3:1，C12） |
| focus-ring | #0071E3 | #409CFF | WindowText | `Primer.focusOuter` | 键盘焦点环（KEY-03，C13） |
| hc-outline | 透明 | 透明 | WindowText | `Primer.hcOutline` | 高对比下卡片、按钮、输入框、提示条与状态标记的 1 px 描边 |
| label | #1D1D1F | #F5F5F7 | WindowText | `Primer.text` | 正文、标题 |
| label-secondary | #636366 | #AEAEB2 | WindowText | `Primer.textMuted`、`Primer.textFaint` | 说明、元数据、表头、占位符（所有底色上 ≥ 4.5:1） |
| label-tertiary | #8E8E93 | #8E8E93 | GrayText | `Ant.colorTextDisabled` | 只用于禁用态和图标，不用于要读的文字 |
| label-on-accent | #FFFFFF | #FFFFFF | HighlightText | `Primer.onPrimary`、`Primer.onAccent` | accent 与 destructive 实底上的文字和图标 |
| accent | #0071E3 | #0071E3 | Highlight | `Primer.primaryBg`、`Ant.colorPrimary` | 默认按钮实底、获得焦点的选中行、开关打开、进度（白字 4.7:1） |
| accent-text | #0066CC | #409CFF | HotTrack | `Primer.accent` | 链接、文字按钮、选中的图标 |
| accent-tint | #E8F1FC | #0A2540 | Window | `Primer.noteBg` | 信息类提示条底，上面文字用 accent-text |
| destructive | #D70015 | #D70015 | Highlight | `Ant.colorErrorSolid` | 破坏性确认后的实底按钮、破坏性标记（白字 5.4:1） |
| destructive-text | #BD0012 | #FF827A | WindowText | `Primer.destructiveText` | 破坏性按钮文字（普通与悬停底色均 ≥ 4.5:1） |
| destructive-tint | #FDECEC | #3B1517 | Window | `Ant.colorErrorBg` | 错误提示条、破坏性请求图标底 |
| warning | #FF9500 | #FF9F0A | WindowText | 无 | 警告图标实底、少于 30 秒的倒计时环；不用于文字 |
| warning-text | #B25000 | #FFB340 | WindowText | `Primer.warn` | 警告文字、即将超时的时间 |
| warning-tint | #FFF4E5 | #3A2A12 | Window | `Primer.warnBg`、`Primer.countWarningBg` | 警告提示条、警告行底 |
| success | #34C759 | #30D158 | WindowText | 无 | “已连接”圆点、成功图标实底；不用于文字 |
| success-text | #1A7431 | #30D158 | WindowText | `Primer.ok` | “已批准”“已完成”文字 |
| success-tint | #E9F7EE | #12301B | Window | `Primer.okBg` | 成功提示条、新增变更的标记底 |
| icon-write / icon-destructive / icon-read | #0A84FF / #FF3B30 / #8E8E93 | #0A84FF / #FF453A / #636366 | Window + hc-outline | 无（随 UI-2c） | 审批请求图标底（白色图形） |
| scrim | 黑 25% | 黑 50% | 黑 80%，浮层另加 hc-outline（维护者 2026-11-03） | `Ant.colorBgMask` | 确认面板与模态层背后的遮罩 |
| toast-bg | rgba(30,30,30,0.94) | rgba(58,58,60,0.96) | Window | `Ant.colorBgSpotlight` | toast 与工具提示底，上面白字 |

破坏性按钮文字使用独立的 `Primer.destructiveText`：原亮色 `#D70015` 在 `Primer.pill`（`#E5E5EA`）上只有 4.29:1，低于普通文字要求的 4.5:1。表中的亮色与暗色值由 [ThemeSurfaceTests](../../tests/Studio/TiaOpenness.Gui.Tests/ThemeSurfaceTests.cs) 固定，并由 [AccessibilityTests](../../tests/Studio/TiaOpenness.Gui.Tests/AccessibilityTests.cs) 检查普通与悬停底色上的对比度。`Primer.diffRed`、`Ant.colorError` 仍表示差异删除与错误说明，不能代替破坏性按钮文字的专用令牌。

### 2.2 字体

| ID | 级别 | 规则 |
|---|---|---|
| TYP-01 | MUST | `Ui.Font` 为 Segoe UI Variable Text、Segoe UI、Microsoft YaHei UI；`Ui.FontMono` 为 Cascadia Mono、Consolas、Microsoft YaHei UI（显式中文回退，避免日文字形）。只用系统字体，不随包、不安装、不加载网络字体；Maple Mono CN 及其随包文件已移除（C9）。等宽只用于工具名、路径、标识、日志、时间和数值。 |
| TYP-02 | MUST | 只用下表的字号阶梯；16 个 `Ui.FontSize.*` 别名都已并到这些值上。数据密集区域（表格、日志、审批元数据）MAY 用 callout 或 footnote 作正文。 |
| TYP-04 | MUST | Regular 文字不小于 12 epx；SemiBold 文字 SHOULD 不小于 14 epx。 |
| TYP-05 | MUST | 英文用句首大写，不用全大写表达强调或表头；不用斜体，强调用 SemiBold。 |
| TYP-06 | MUST | 允许省略号截断，但完整内容必须能用键盘取得（可聚焦的单元格、详情区或包含全文的 UIA 名称）；不可聚焦元素上的 ToolTip 不能是唯一途径。 |
| TYP-08 | MUST | 技术标识（MCP 工具名、V4 错误码、路径、哈希、请求 ID、参数名）用 mono 或 mono-small 原样显示，不翻译；数字用等宽数字。 |
| TYP-09 | SHOULD | 根元素的 `FrameworkElement.Language` 跟随界面语言（zh-CN / en-US），切换语言时更新。 |
| TYP-10 | MUST | 布局不按字宽假设：文字控件用 `MinWidth` 代替会截断内容的固定 `Width`；工具栏动作、卡片、按钮、表头可随宽度换行或增高。在 1024 × 640 与 1366 × 860、中英文、浅色 / 深色 / 高对比下检查；布局断言不因换字体放宽。 |
| TYP-11 | MUST | 主窗口与全部工作台窗口、对话框关闭 `Typography.StandardLigatures`、`ContextualLigatures`、`ContextualAlternates`，技术文本按输入的字符显示（`WorkbenchFontStyleTests`）。 |

| token | 字号 / 行高（epx） | 字重 | 用途 | 实现键 |
|---|---|---|---|---|
| large-title | 28 / 34 | 700 | 工具栏页面标题，每页一个 | `Ui.FontSize.Heading`、`Heading1`–`Heading3` |
| title-1 | 22 / 28 | 600 | 详情区标题，一句话说清要发生什么 | `Ui.FontSize.Title`、`Subtitle`、`Heading4` |
| title-2 | 17 / 24 | 600 | 确认面板、空状态、页内分区标题 | `Ui.FontSize.Large`、`DialogTitle`、`Heading5`；`Ui.LineHeight.Large` |
| headline | 14 / 20 | 600 | 列表行主文字、按钮文字、卡片标题 | `Ui.FontSize.SmallTitle` |
| body | 14 / 21 | 400 | 正文、控件文字、分组列表行 | `Ui.FontSize.Body`、`Control`；`Ui.LineHeight` |
| callout | 13 / 18 | 400 | 列表行副文字、分组标题、表头 | `Ui.FontSize.Section` |
| footnote | 12 / 17 | 400 | 注释、快捷键提示；最小的可读文字 | `Ui.FontSize.Small`、`Caption`；`Ui.LineHeight.Small` |
| mono / mono-small | 13 / 18、12 / 17 | 400 | 工具名、路径、标识；列表里的路径 | `Ui.FontMono` + `Section` / `Small` |
| timer | 28 / 34（等宽） | 500 | 审批详情区倒计时 | 随 UI-2c |

### 2.3 间距与尺寸

| token | 值（epx） | 实现键 | 用途 |
|---|---|---|---|
| space-1 | 4 | `Ui.Space.XXS` | 图标与紧贴的文字 |
| space-2 | 8 | `Ui.Space.XS` | 同一控件组内、按钮之间、控件与上方标签 |
| space-3 | 12 | `Ui.Space.SM` | 行内图标与文字、行的上下内边距、控件与旁边标签 |
| space-4 | 16 | `Ui.Space` | 分组卡片左右内边距、卡片之间 |
| space-5 | 20 | `Ui.Space.MD` | 区块之间 |
| space-6 | 24 | `Ui.Space.LG` | 详情区左右内边距 |
| space-8 | 32 | `Ui.Space.XL` | 页面左右内边距、工具栏下方 |
| control-sm | 28 | `Ui.ControlHeight.Small` | 工具栏小按钮、分段控件 |
| control-md | 32 | `Ui.ControlHeight` | 普通按钮、输入框、下拉框 |
| control-lg | 44 | `Ui.ControlHeight.Large` | 审批决策按钮、确认面板按钮 |
| 行高（项目值） | 36 | 无 | 表格与列表行最小高度；不采用设计系统的 row-min 44（C11） |
| sidebar-width / -collapsed | 232 / 64 | 随 UI-2b | 侧边栏；窗口窄于 1200 epx 时收为图标栏 |
| list-width | 320 | 随 UI-2b | 列表 + 详情页的中间列表 |
| sheet-width | 420 | 随 UI-2c | 确认面板 |

### 2.4 圆角

| token | 值（epx） | 实现键 | 用途 |
|---|---|---|---|
| radius-xs | 4 | `Ui.Radius.XS` | 复选框、键帽、小标记 |
| radius-sm | 6 | `Ui.Radius.Small` | 小按钮、分段控件的选中块 |
| radius-md | 8 | `Ui.Radius` | 按钮、输入框、列表选中行、请求图标 |
| radius-lg | 12 | `Ui.Radius.Large` | 分组卡片、确认面板、提示条 |
| radius-full | 全圆 | 无 | 开关、计数徽标、状态圆点与标记 |
| 0 | 0 | 无 | 相接的直边、最大化的窗口 |

### 2.5 阴影与时长

WPF 不画 CSS 阴影：测试禁止 `Effect`（`PrimerVocabularyTests`），阴影由 `Primer.Shadow` 用多层半透明边框（`Ant.boxShadow*` 键）画出。

| token | 浅色 | 深色 | 高对比 | 用途 |
|---|---|---|---|---|
| shadow-card | 0 0 0 0.5px 黑 8%，0 1px 2px 黑 4% | 0 0 0 0.5px 白 8% | 无，改用 hc-outline | 分组卡片：以细描边为主 |
| shadow-segment | 0 1px 3px 黑 12%，0 0 0 0.5px 黑 4% | 同浅色 | 无 | 分段控件选中块、开关圆钮 |
| shadow-sheet | 0 20px 48px 黑 22%，0 0 0 0.5px 黑 12% | 0 20px 48px 黑 60%，0 0 0 0.5px 白 12% | 无 | 确认面板、提示条：唯一需要明显层级的地方 |
| duration-fast | 150 ms | 同左 | 同左 | 选中、悬停、开关切换 |
| duration-base | 250 ms，缓出 | 同左 | 同左 | 确认面板滑入、提示条出现；系统“减少动态效果”开启时改为淡入 |

## 3 布局

### 3.1 窗口结构

| 区域 | 尺寸 | 底色 | 内容 |
|---|---|---|---|
| 侧边栏 | 232 epx；窗口窄于 1200 epx 时 64 epx 图标栏（C11） | bg-sidebar | 应用名、分组的 8 个一级页面、底部状态行（MCP 服务、TIA Portal 与当前项目、写操作审批开 / 关）、固定在底部的设置 |
| 工具栏 | 自适应高度 | bg-window | 页名（large-title）与可选的 callout 上下文；本页操作在右；窗口按钮贴在右上角 |
| 提示条 | 工具栏下方，一条 | 按严重级别的 tint | 安全与环境提示，按 §3.2 的优先级合并 |
| 内容 | 其余宽度 | bg-window 或 bg-grouped | 按页面类型选用：列表 320 + 详情、分组列表（bg-grouped 上放 bg-elevated 卡片）、整页表格 |

### 3.2 页面与入口

- 一级页面 8 个，按侧边栏顺序：审批、项目、版本控制；AI 调用、审计日志；环境体检、日志与诊断、MCP 与客户端（Ctrl+1…Ctrl+8 同序）。设置（Ctrl+,）固定在侧边栏底部。删除隐藏的导航别名（概览、程序块）；`WorkbenchPage` 协议枚举不变，`Blocks` 是项目页的别名，MCP 契约不变。
- 每类功能只有一个主入口：待批请求 → 审批页（取代审批抽屉）；环境 → 环境体检页，其他页面只显示一条链接到该页的提示条；日志和诊断包 → 日志与诊断页；连接信息 → MCP 与客户端页。
- 设置是页面，分三区：外观（主题、语言）；服务（版本、更新、目录、关于）；AI 控制（允许 AI 控制工作台界面、写操作审批与超时、原生交叉引用），AI 控制区只有人能改。
- 安全提示同时出现时合并为一条提示条，只显示最高的一条并标出其余数量：1 待批计数（侧边栏徽标，永不隐藏）> 2 审批已关 > 3 工作台控制通道不可用 > 4 AI 预填，待确认 > 5 “AI 操作了界面”（短暂显示）。

### 3.3 规则

| ID | 级别 | 规则 |
|---|---|---|
| LAY-01 | MUST | 窗口最小尺寸不超过 1024 × 640 epx，在此尺寸下所有功能可用（允许滚动）。 |
| LAY-02 | SHOULD | 最小宽度不超过 960 epx，使窗口能贴靠到 1920 宽屏幕的一半，与 TIA Portal 并排（维护者 2026-11-03：保持 960，不采用设计系统的 1024）。 |
| LAY-03 | MUST | 窄窗口：侧边栏按 §3.1 收起；列表 + 详情页保留 320 列表；带右栏的页面在宽度不足时把右栏移到内容下方；数据表格属于二维内容，允许横向滚动。 |
| LAY-05 | SHOULD | 间距按 §2.3 的用途取值；一致的间距本身表达分组。 |
| LAY-06 | MUST | 承载文字的行和容器不用固定高度，用 `Auto`、`MinHeight` 或 `*`。 |
| LAY-07 | SHOULD | 文字左对齐；只有空状态与图标下的说明居中。 |
| LAY-08 | SHOULD | 圆角按 §2.4。 |
| LAY-09 | SHOULD | 浮层与背景可区分：常规主题用描边和 shadow-sheet；高对比用 hc-outline，阴影为无。 |
| LAY-10 | MUST | 声明 Per-Monitor V2 DPI 感知（`app.manifest`），在 100–200% 与跨屏混合缩放下不模糊、不截断。 |
| LAY-11 | SHOULD | 自绘标题区保留系统行为：空白处拖动、右键系统菜单、双击最大化 / 还原、最大化按钮悬停显示 Snap 布局（命中测试返回 HTMAXBUTTON）。 |
| LAY-12 | MUST | 控件高 32 epx；行内紧凑按钮不低于 24 epx（指针目标见 CTL-10）；表格和列表行高 36 epx；审批决策按钮和确认面板按钮 44 epx（control-lg）。 |
| NAV-01 | MUST | 一级页面扁平，不超过 8 个（C14），按 §3.2 排列；不再加深二级页签。 |
| NAV-02 | MUST | 所有导航（人点击、快捷键、AI 控制）只经过 `MainWindow.Navigate(page)` 一个入口；守卫、快照与侧边栏同步只在这里实现。 |
| NAV-03 | MUST | 当前位置始终可见：侧边栏当前项同时有选中底色、指示条和 SemiBold（C17），未选中项恢复 Regular；工具栏显示页名；`Window.Title` 为“<页名> – 自动化工程工作台”（SHOULD，对应 WCAG 2.4.2）。 |
| NAV-04 | MUST | 侧边栏在 UIA 中是一组可选项：每项有名称、选中状态、`SizeOfSet` / `PositionInSet`；整组一个 Tab 停靠点，方向键切换。 |
| NAV-05 | MUST | 暂不可用的页面保持可见但禁用，并说明原因（HelpText / ToolTip，如“连接 TIA 项目后可用”）；指向它的快捷键给出可见反馈，不静默无效。 |
| NAV-06 | SHOULD | 每页使用 §3.1 的结构和三种页面类型之一。 |
| NAV-07 | MUST | 切页后焦点可预期：人点击侧边栏时焦点留在该项；快捷键或 AI 切页时焦点放到页面标题或当前侧边栏项，绝不放到执行类或审批类按钮上；原焦点元素被折叠时恢复焦点。 |
| NAV-08 | MUST | AI 可定位的页面白名单是一个 `WorkbenchPage` 枚举列表，不含审批页、设置页和 MCP 与客户端页（C15）。 |
| NAV-09 | SHOULD | F6 / Shift+F6 在区域间循环：侧边栏 → 工具栏 → 提示条 → 内容（列表 → 详情）；每个区域根元素有 `AutomationProperties.Name`。 |

## 4 组件

每个可交互组件都定义七种状态（默认、悬停、按下、焦点、禁用、选中、错误），见手册[第 21.7 节](engineering-handbook.html#ui-components)；组件清单与外观以设计系统为准。

### 4.1 WPF 落实

| 概念 | 工作台做法 |
|---|---|
| 控件外观 | 原生 WPF 控件重写模板（`Themes/Primer.xaml`、`Shell.xaml`、`Pages.xaml`、`Approvals.xaml`），不用 WPF Fluent `ThemeMode` |
| 对话框 | `Controls/WorkbenchMessageBox.cs`（带 Owner 的模态窗口）；窗口内模态层用 `Controls/ModalPane.cs` |
| 提示条（InfoBar） | 自建：严重级别图标 + 标题 + 正文 + 可选一个操作 |
| 开关 | `ToggleButton` + 开关模板（UIA Toggle 模式） |
| 不确定进度 | `ProgressBar IsIndeterminate="True"`；倒计时环随 UI-2c |
| 焦点视觉 | 全局 `FocusVisualStyle`（`SystemParameters.FocusVisualStyleKey`） |
| 高对比 | `ThemeManager` 检测 `SystemParameters.HighContrast`，换入映射到 `SystemColors` 的 `Palette.HighContrast.xaml` |
| 访问键 | `_` 助记符 + `AccessText`（模板 `RecognizesAccessKey="True"`） |
| 快捷键 | `KeyBinding` + `AutomationProperties.AcceleratorKey` |
| 标题与对话框语义 | `AutomationProperties.HeadingLevel`、`AutomationProperties.IsDialog` |
| Live region | `AutomationProperties.LiveSetting` + `RaiseAutomationEvent(LiveRegionChanged)`，封装在 `Controls/AccessibleStatus.cs` |
| 标题栏 | `WindowChrome` + 自绘按钮 |
| 系统通知 | Windows App SDK `AppNotificationManager`（未打包应用可用）或 `NotifyIcon` |

### 4.2 按钮与命令

| ID | 级别 | 规则 |
|---|---|---|
| PLT-02 | MUST | 优先用原生 WPF 控件并重写模板，不为外观派生新控件；原生控件自带键盘行为和 AutomationPeer。 |
| CMD-01 | MUST | 按钮文字是动词（加对象），说清后果；不用“确定”“是”“OK”；不加句号；点了之后还要确认或填写时加省略号（“删除…”“选项…”）。 |
| CMD-02 | MUST | 每个决策区最多一个默认按钮（accent 实底、白字，响应回车）；其余按钮用 fill-control 底；破坏性按钮用 destructive-text 文字，不用 accent。一屏的 accent 实底不超过一处。 |
| CMD-03 | SHOULD | 位置：页面操作在工具栏右侧；对话框与确认面板按钮在底部右对齐，取消 / 拒绝在左，主要操作在最右（C5）。 |
| CMD-04 | MUST | 因状态不可用的命令，原因不明显时说明原因（HelpText / ToolTip）；不隐藏用户预期存在的命令。 |
| CMD-05 | MUST | 常用命令的快捷键写在 ToolTip（“保存项目 (Ctrl+S)”）与 `AutomationProperties.AcceleratorKey` 中，与 KEY-07 一致。 |
| CMD-06 | MUST NOT | 为“批准”“拒绝”“关闭审批”设置快捷键；审批两键也没有访问键（C4）。 |
| CMD-07 | MUST | 开关与筛选按钮在 UIA 中暴露按下状态（Toggle 模式），视觉上除颜色外还有形状或勾选标记，对比 ≥ 3:1。 |
| CMD-08 | SHOULD | 立即生效的二元设置（写操作审批、允许 AI 控制工作台界面）用开关；复选框用于多选或需要再保存的场合；同一区域的二元设置用同一种控件。 |
| CMD-09 | SHOULD | 同一功能在多处出现时，用同一文字、同一命令对象和同一快捷键。 |
| CMD-10 | MUST | 上下文菜单只作加速入口：其中每一项在别处也有入口；可用 Shift+F10 或菜单键打开，宿主元素可获得焦点。 |

### 4.3 控件与图标

| ID | 级别 | 规则 |
|---|---|---|
| CTL-01 | MUST | 每个重写的模板保留焦点视觉（KEY-03）、禁用外观（不只靠降低不透明度）、悬停与按下反馈、高对比外观。 |
| CTL-02 | MUST | 文本框、下拉框、密码框都有可见标签（在上方，间距 8），并以 `AutomationProperties.LabeledBy` 或 `Label.Target` 关联；占位符不代替标签，并作为 HelpText 暴露。错误时边框变色、下方显示文字（ERR-03）。 |
| CTL-03 | MUST | 列表：整个列表一个 Tab 停靠点，↑↓ 移动，到底不循环；列表单选，选中立即更新详情区；Enter 执行项的主操作，Space 切换勾选；表格多选用复选框，选中行用 fill-selected-inactive 加勾选，不只靠底色；窗口激活时获得焦点的选中行用 accent 实底加白字。尽量不在项内嵌按钮。 |
| CTL-04 | MUST | 列表项、树节点有可读的 UIA 名称：在 `ItemContainerStyle` 上绑定来自 `Strings` 的格式串（如“GetProjectInfo，读，成功，210 毫秒”），或提供本地化的 `ToString()`；名称不能是 CLR 类型全名。 |
| CTL-05 | SHOULD | 带列头的数据表用 `ListView` + `GridView` 或已套主题的 `DataGrid`；自制表格的项名称须包含列名与值。 |
| CTL-06 | MUST | 复选框有内容或名称；需要按钮外观时用 `ToggleButton`；只读状态用文字显示，不用禁用或不可聚焦的复选框。 |
| CTL-07 | SHOULD | Expander 标题只做展开 / 折叠，其他操作放在标题之外。 |
| CTL-08 | MUST | `DataGrid`、`TreeView`、`ToolTip`、`ContextMenu`、`ScrollBar` 等默认控件都套用 token，深色与高对比下不出现 Aero2 白底。 |
| CTL-10 | MUST | 指针目标不小于 24 × 24 epx（图标按钮建议 32 × 32），或满足 WCAG 2.5.8 的间距例外。 |
| ICO-01 | MUST | 图标来自 `Themes/Icons.xaml` 的命名资源（`Icon.*`）；暂时沿用产品自己的 16 网格图标，按 18 epx 绘制（C16）；不在模板中内联路径或私用区字符，不用外观近似的 Unicode 字符代替图标。 |
| ICO-02 | MUST | 仅含图标的按钮设置 `AutomationProperties.Name` 与 ToolTip，文字来自 `Strings`，名称描述动作（“关闭”而非“X”）。 |
| ICO-03 | SHOULD | 按钮以文字为主；图标主要用于导航、状态与窗口按钮。 |
| ICO-04 | MUST | 状态标记形状互不相同（i、!、✓、✕、—），高对比下仍可区分，不只靠圆点颜色。 |
| ICO-05 | SHOULD | 自绘窗口按钮用 `Icon.*` 的最小化、最大化、还原、关闭图形，最大化后切换为还原图标。 |

### 4.4 对话框与确认面板

确认面板（设计系统的 Sheet）只给撤不回的操作用：从工具栏下方滑入，背后盖 scrim，宽 420 epx；结构为图标 → title-2 问句标题 → 一句后果 → 按钮。审批与破坏性操作的特别规则见 §5。

| ID | 级别 | 规则 |
|---|---|---|
| DLG-01 | MUST | 模态消息统一用 `WorkbenchMessageBox`，以主窗口为 Owner 并居中；窗口内模态层的根元素设置 `AutomationProperties.IsDialog="True"` 与名称；独立窗口的 `Title` 等于对话框标题。 |
| DLG-02 | MUST | 模态语义：打开时焦点移到第一个可聚焦内容或安全按钮；Tab 只在内部循环；背景不可交互；关闭后焦点回到触发元素。 |
| DLG-03 | MUST | 每个对话框和确认面板都有一个安全、无破坏性的按钮（取消 / 关闭），设为 `IsCancel`，响应 Esc。 |
| DLG-04 | MUST | 有风险的确认：默认按钮（Enter）是安全选项；提交按钮用具体动词（“保存项目”“断开并结束会话”“覆盖块”“关闭审批”）。 |
| DLG-05 | SHOULD | 按钮顺序：取消在左，提交在右，均在底部右对齐（C5）。 |
| DLG-06 | MUST | 标题是简短的问句或主指令（“关闭写操作审批？”）；正文补充后果，不换说法重复标题。 |
| DLG-07 | MUST | 风险确认与错误显示对应图标并配文字；正文用常规文字色，不把整段染红。 |
| DLG-08 | MUST NOT | 用对话框报告与某个字段相关的验证错误（改为内联，ERR-03）。 |
| DLG-09 | SHOULD | 确认只用于不可撤回或后果重大的操作（保存项目、断开共享会话、工作区覆盖项目、覆盖导入、关闭审批）；可撤回的操作不确认。 |
| DLG-10 | MUST | 带遮罩的面板与侧栏（确认面板、调用详情、项目选项）按模态处理：焦点进入、Tab 内循环、Esc 关闭、焦点归还；没有遮罩的侧栏才允许背景继续交互。 |

### 4.5 状态、通知、错误与空状态

| ID | 级别 | 规则 |
|---|---|---|
| STS-01 | MUST | 侧边栏底部状态行和提示条的每一项有文字（不只是圆点）和 UIA 名称；可交互的项是可聚焦的按钮，不用鼠标事件挂在面板上。 |
| STS-02 | MUST | 重要状态变化用 live region 播报（`AccessibleStatus`）：MCP 服务状态、会话连接 / 断开、AI 操作界面、预填待确认、长操作完成或失败用 Polite；新的待批请求与阻断性错误可用 Assertive。 |
| STS-03 | MUST | 长操作显示进度：已知总量用确定进度，否则用不确定进度，旁边有说明当前操作的文字；进度放在发起处附近。 |
| STS-04 | SHOULD | 后端支持取消的长操作（MCP 面板请求、图集生成、诊断包）提供“取消”。 |
| STS-05 | MUST | 被阻止的操作说明原因与办法（如忙碌时关闭窗口：“正在执行 <操作>，完成或取消后再关闭”），不静默无效。 |
| STS-06 | MUST | 持续的全局状态（审批已关、控制通道不可用、离线演示、环境问题）用提示条或侧边栏状态行显示，带严重级别图标与文字，不用会自动消失的 toast。 |
| STS-07 | SHOULD | toast 只作短暂反馈：排队逐条显示，不遮挡审批按钮与焦点元素，可用键盘关闭。 |
| STS-08 | MUST | 提示条、提示与徽标不闪烁；短时间内多次变化时合并显示。 |
| STS-09 | SHOULD | 系统通知文字本地化；激活时还原窗口并打开对应页面。 |
| STS-10 | MUST | 计数徽标的 UIA 名称包含完整含义（“2 项待批”），不只是数字。 |
| ERR-01 | MUST | 错误说明发生了什么、已知原因、用户可以怎么做；写出具体对象（路径、工具、版本）；不责备用户；“未预期的错误”只用于真正的崩溃。 |
| ERR-02 | MUST | 主信息是本地化文字，原始异常与堆栈不作主信息；可展开“详细信息”（V4 `error.code`、请求 ID、诊断 ID，等宽显示）；V4 错误码经 `EngineResultText` 本地化。 |
| ERR-03 | MUST | 字段验证错误内联显示：边框变为错误色、下方显示文字（不只靠颜色），作为字段的 HelpText 暴露并播报；焦点移到第一个出错字段。 |
| ERR-04 | MUST | 页面或全局的失败（HTTP 监听失败、环境检查失败、控制通道不可用）用提示条显示，并给出可执行的操作（“网络权限”“重新检查”）。 |
| ERR-05 | MUST | 空状态：48 epx 图标（label-tertiary）+ title-2 一句话 + footnote 下一步，最多一个操作，在内容区居中。 |
| ERR-06 | SHOULD | 加载、空、错误三种状态分开：加载时列表顶部显示进度，空时显示空状态，错误时显示提示条或内联提示。 |
| ERR-07 | MUST | 错误严重程度用图标与“错误”等字样表达，不只用红色文字。 |

## 5 审批与危险操作交互

本节是项目红线（即旧版第 11 节，[ADR 0003](decisions/0003-workbench-approval-for-mcp-writes.md) 与 [ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md) 按旧编号引用），优先级高于本文其他章节。原则：AI 可以显示、定位和预填，但改变项目、批准写操作、改动审批或 AI 控制开关的最后一步，只能由人在可见、可读、可用键盘操作的界面上完成。

### 5.1 风险等级与决策按钮

风险等级在宿主侧生成审批项时计算，不使用 MCP 的 `destructiveHint` 注解；生成计划取各步骤中的最高等级。

| 等级 | 判定 | 按钮 | 回车 | Esc |
|---|---|---|---|---|
| 只读 | 不需要审批的调用 | 不进入审批 | 无 | 无 |
| 写入 | 其余需要审批的调用，包括带 overwrite 的导入 | “拒绝 · 批准”等权重：同样的 fill-control 底、control-lg 高、等宽单元格，拒绝在左；两者都不是默认或取消按钮（C1、C5） | 永不批准，即使焦点在“批准”上（对 KEY-05 的例外）；只有空格或鼠标点击能批准 | 不拒绝 |
| 破坏性 | ONLINE-WRITE 工具（如下载到设备）、`Delete*` 工具，以及设置了 `confirmDelete` 或 `deleteAtTarget` 的调用 | “拒绝”是默认按钮（accent 底）；“批准并……”用 destructive-text，点击后打开确认面板（C1） | 触发“拒绝” | 拒绝（维护者 2026-11-03） |
| 破坏性的确认面板 | 同上 | “取消”在左，是默认按钮；确认按钮可用 Tab + 空格或它的 Alt 访问键触发（C6） | 触发“取消”；确认按钮永不响应回车 | 取消 |

### 5.2 审批

| ID | 级别 | 规则 |
|---|---|---|
| APR-01 | MUST | 每个写请求单独决定，没有“全部批准”或批量批准。 |
| APR-02 | MUST | 面板（人）发起的写请求自动在审批页打开对应请求，仍须人单独批准；自动打开时焦点放在请求标题上，MUST NOT 放在“批准”或“拒绝”上。AI 切页后的 500 ms 输入保护覆盖两个决策按钮（AI-02）。 |
| APR-03 | MUST | 请求详情显示：客户端名（标“（未验证）”）、传输方式、宿主 / 版本、目标项目、PLC、操作清单（动作 / 对象 / 工具）、风险等级文字、人 / AI 来源、剩余时间；脱敏参数或生成计划放在默认收起的区域，来自项目的数据标明只作数据显示。密码、密钥、凭据和令牌按调用日志的共用规则换成固定占位符后才计算参数与长文本摘要；详情不携带原值或其无盐哈希；只更换敏感值不改变审批绑定，实际操作仍用原值。 |
| APR-04 | MUST | 决策按钮按 §5.1：写入请求两键同样式（fill-control 底、control-lg 高）、纯文本标签、没有访问键（C4），既不是默认按钮也不是取消按钮（`IsDefault=False`、`IsCancel=False`）；并列在等宽单元格，长标签换行，两者一起增高。破坏性请求的“拒绝”是默认按钮。 |
| APR-05 | MUST | 写操作审批开关只在 设置 › AI 控制（C7）；侧边栏底部只显示状态。关闭审批走确认面板（标题“关闭写操作审批？”，提交按钮“关闭审批”，取消为默认并响应 Esc）。提交前须在面板中输入确认词（维护者 2026-11-03），只针对关闭总开关，平时的审批不受影响。审批关闭期间侧边栏状态行显示“写操作审批 · 关”，提示条显示带图标与文字的“审批已关”，变化时以 live region 播报。 |
| APR-06 | MUST | 审批超时即拒绝。倒计时用环（线宽 3，轨道 fill-control，进度 accent，少于 30 秒变为 warning）加剩余时间文字；读屏只在 30 秒和 10 秒时播报。超时在 设置 › AI 控制 中可选 60 / 120 / 300 秒；按 WCAG 2.2.1 的“必要”例外处理（偏差 UI-24）。 |
| APR-07 | MUST | 新的待批请求：侧边栏徽标计数立即更新并以 live region 播报；窗口最小化时发系统通知；激活通知或徽标打开审批页；通知文字本地化。 |
| APR-08 | MUST | AI 不能把人引向审批：`display.call` 拒绝待批请求；AI 页面白名单不含审批页（NAV-08）。 |
| APR-09 | MUST | 断开共享会话：确认写出 AI 客户端名与后果（AI 需新建会话重连），提供“仅分离面板”作替代，默认按钮为取消。 |
| APR-10 | MUST | 决定立即生效，拒绝后不提供撤销（C8）：MCP 调用立即返回，审计链立即记录；界面只给一条短提示“已拒绝……”或“已批准，正在 TIA Portal 中执行”。 |

### 5.3 AI 控制界面

| ID | 级别 | 规则 |
|---|---|---|
| AI-01 | MUST | 以下情况拒绝 AI 的显示与预填（读取不受影响）：“允许 AI 控制工作台界面”关闭；有模态对话框或确认面板打开；当前页是审批页或设置页（C15）；最近 1.5 秒内有人的键鼠输入；目标页正在执行命令。 |
| AI-02 | MUST | 每次由 AI 引起的切页或定位之后 500 ms 内，忽略对执行类与审批类按钮的鼠标点击、Enter / Space 和会触发执行的访问键（`Control/ControlInputGuard.cs`）。 |
| AI-03 | MUST | AI 操作界面后，提示条以文字显示“AI 操作了界面 · <工具> · <客户端名> · n 秒前”（优先级 5，短暂显示），以 polite live region 播报；调用详情显示对应的控制记录。 |
| AI-04 | MUST | 每个被预填的字段旁显示可见标记“AI 预填，待确认”和可用键盘操作的“清除预填”（`PrefillMarker`），字段的 HelpText 同时暴露该状态；提示条（优先级 4）保留汇总及“确认预填 / 清除预填”。只用 ToolTip 不满足本条。 |
| AI-05 | MUST | 定位只做滚动与高亮；不改变复选框（复选框即导出选择），不执行任何命令。 |
| AI-06 | MUST | “允许 AI 控制工作台界面”只能由人在 设置 › AI 控制 中修改，默认开启，保存在 UI 设置文件；控件旁说明受保护的行为（人正在操作时不抢焦点、切页后短时间忽略执行与审批按钮）；任何工具只能回显其值。 |
| AI-07 | MUST | AI 引起的导航不把键盘焦点移到执行或审批按钮上（NAV-07）。 |
| AI-08 | MUST | 控制通道不可用时，提示条以文字显示“工作台控制通道不可用”（优先级 3），不影响审批。 |

## 6 无障碍与高对比度

WCAG 2.2 A/AA 是必须达到的底线，按 WCAG2ICT 对非 Web 软件的解释应用到 WPF；Microsoft 指南在其上增加高对比主题、访问键、F6 区域切换和焦点视觉要求。无障碍是合并条件：审批界面若不能被所有用户可靠操作，安全设计就会失效。

### 6.1 颜色与主题

| ID | 级别 | 规则 |
|---|---|---|
| COL-02 | MUST | 三套主题：浅色、深色、高对比。`SystemParameters.HighContrast` 为真时自动启用高对比字典，优先于用户选择；运行中切换（`SystemParameters.StaticPropertyChanged`）即时重绘。设置 › 外观 只有“自动 / 浅色 / 深色”，4.0 不提供手动选择高对比。 |
| COL-03 | MUST | 高对比配对：Window / WindowText 用于面与文字；Highlight / HighlightText 用于选中、按下、进行中；ButtonFace / ButtonText 用于可交互元素；HotTrack 只用于链接；GrayText 只用于禁用。所有卡片、按钮、输入框和提示条加 1 px WindowText 描边（hc-outline），阴影为无，状态只靠图标形状和文字；列表项文字继承项容器的前景色。 |
| COL-04 | MUST | 浅色与深色主题中，每一对实际使用的文字与底色对比 ≥ 4.5:1（含次要文字、占位符、时间戳、徽标文字）；只有禁用态豁免。 |
| COL-05 | MUST | 非文字对比 ≥ 3:1：焦点环、状态指示（选中、勾选、按下、开关）、识别控件所需的边界（border-control）；separator 是装饰，不在此列。 |
| COL-06 | MUST | 不只靠颜色传达信息：状态配图标和文字；日志级别带文字标签；差异行带“+ / −”前缀；开关与筛选的状态另有形状、字重或勾选标记。 |
| COL-07 | SHOULD | accent 只用于默认按钮、获得焦点的选中行、开关和进度；链接和文字按钮用 accent-text，不把 accent 直接当文字色。强调色固定为 #0071E3，不跟随 Windows 强调色（ADR 0028）。 |
| COL-08 | MUST | 状态色只有带 -text 的 token 能放文字，其余只给图标和圆点；语义色作实底时，上面文字对比 ≥ 4.5:1。 |
| COL-09 | MUST | 主题切换即时生效、不重启（保留 `ThemeManager` 的槽位替换）；“自动”跟随 Windows 应用模式。 |
| COL-10 | SHOULD | 标题区与窗口按钮随主题、高对比及窗口激活状态变化（非激活时降低不透明度）。 |
| COL-11 | MUST NOT | 在样式里用 `DataTrigger`（如绑定 `ThemeManager.EffectivelyDark`）切换颜色；主题差异只放在调色板里。 |

### 6.2 键盘与焦点

| ID | 级别 | 规则 |
|---|---|---|
| KEY-01 | MUST | 所有功能都能用键盘完成；不在非控件元素上挂 `MouseLeftButtonUp/Down` 作唯一触发；指针操作在抬起时触发，可通过移开取消（按下即触发的处理器只用于输入守卫和拖动）。 |
| KEY-02 | MUST | Tab 顺序与视觉顺序一致；非交互元素不是 Tab 停靠点。 |
| KEY-03 | MUST | 每个可聚焦元素有可见焦点：全局 `FocusVisualStyle` 画 focus-ring，线宽 2 px、画在控件外 2 px（C13），在所有主题中对相邻颜色 ≥ 3:1；模板可以增加焦点效果，不能去掉它。 |
| KEY-04 | MUST | Esc 关闭临时界面（对话框与确认面板 = 取消、弹出框、toast），从不触发破坏性操作；写入请求中 Esc 不拒绝（§5.1）。 |
| KEY-05 | MUST | Enter 激活聚焦的按钮；对话框中 Enter 触发默认按钮，风险确认的默认按钮是安全选项（DLG-04）。例外（ADR 0028）：Enter 永不批准写入请求，即使焦点在“批准”上；破坏性确认按钮永不响应 Enter。 |
| KEY-06 | MUST | 对话框按钮、确认面板按钮、页面按钮和主要表单字段有访问键，并在标签中显示：英文 `_Save`，中文 `保存(_S)`；按 Alt 显示下划线；随语言本地化，同一作用域内不重复。审批的“批准”“拒绝”没有访问键（C4）；破坏性确认按钮有访问键（C6）。 |
| KEY-07 | MUST | 快捷键在代码、ToolTip 和文档三处一致，见下表。 |
| KEY-08 | MUST | 浮层关闭后、或焦点元素因切页被折叠后，焦点回到触发元素或当前页的合理位置。 |
| KEY-09 | MUST | 除模态界面（Tab 在内循环）外不出现键盘陷阱。 |
| KEY-10 | SHOULD | 一组相关选项（分段控件、侧边栏）是一个 Tab 停靠点，组内用方向键移动，Home / End 到首尾。 |
| KEY-11 | MUST | 鼠标位置不干扰键盘操作；控制通道的人活动检测（光标位置采样）不改变键盘焦点。 |

| 快捷键 | 动作 | 级别 |
|---|---|---|
| Ctrl+1 … Ctrl+8 | 依次切到审批、项目、版本控制、AI 调用、审计日志、环境体检、日志与诊断、MCP 与客户端（与侧边栏顺序一致） | MUST |
| Ctrl+, | 打开设置 | MUST |
| Ctrl+Tab / Ctrl+Shift+Tab | 下一个 / 上一个一级页 | SHOULD |
| Ctrl+O | 打开项目选项 | MUST |
| Ctrl+S | 保存项目（照常确认与审批） | MUST |
| Ctrl+B | 编译 PLC 软件 | MUST |
| Ctrl+F | 聚焦当前页的搜索或筛选框 | SHOULD |
| F5 | 刷新当前页数据（只读） | MAY |
| F6 / Shift+F6 | 在区域间循环（NAV-09） | SHOULD |
| Esc | 关闭临时界面（KEY-04） | MUST |
| Shift+F10 / 菜单键 | 打开上下文菜单（CMD-10） | MUST |

### 6.3 可测要求

“UIA 树测试”指在 `TiaOpenness.Gui.Tests` 中用 `UIElementAutomationPeer.CreatePeerForElement` 从各页面、面板、对话框（离线夹具状态）递归遍历并断言属性；“AI4W”指 Accessibility Insights for Windows 的 FastPass。

| ID | 级别 | 要求 | 验证 | WCAG |
|---|---|---|---|---|
| A11Y-01 | MUST | 每个可聚焦或可操作元素的 UIA 名称非空、来自 `Strings`、以可见标签开头，且不是 CLR 类型全名 | UIA 树测试（中英文各一遍）、AI4W | 1.1.1、2.5.3、4.1.2 |
| A11Y-02 | MUST | 每个输入控件的 `LabeledBy` 指向可见标签 | UIA 树测试 | 1.3.1、3.3.2 |
| A11Y-03 | MUST | 控件类型与行为一致（侧边栏项、开关、复选框、按钮），状态（选中、按下、展开）可读 | Inspect / AI4W 人工 | 4.1.2 |
| A11Y-04 | MUST | 只用键盘完成：连接 / 打开项目、选择软件、编译、导出、导入、检查、打开调用详情、批准与拒绝、关闭与重新开启审批、确认与清除预填、切换语言与主题、生成诊断包 | 每次发布前人工键盘脚本；自动化测试用 `KeyboardDevice` 事件驱动关键路径 | 2.1.1、2.1.2、2.4.3 |
| A11Y-05 | MUST | 焦点在浅色、深色、高对比下都可见，对相邻色 ≥ 3:1，且不被 toast、提示条或遮罩完全遮住 | 渲染测试取焦点像素计算对比；人工检查遮挡 | 2.4.7、2.4.11、1.4.11 |
| A11Y-06 | MUST | 每组实际使用的文字 / 底色 token 对比 ≥ 4.5:1，非文字指示 ≥ 3:1 | `ThemeSurfaceTests`、`AccessibilityTests`（阈值 4.5 / 3.0） | 1.4.3、1.4.11 |
| A11Y-07 | MUST | 四个内置对比主题（Aquatic、Desert、Dusk、Night sky）下无写死的颜色、文字可读、选中与焦点可辨 | 渲染测试注入高对比调色板；发布前在真实对比主题下人工检查 | 1.4.3、1.4.11 |
| A11Y-08 | MUST | 读屏能听到：新的待批请求、AI 操作界面、预填待确认、长操作完成 / 失败、错误、审批开关变化 | 单元测试断言 `LiveSetting` 与 `LiveRegionChanged`；Narrator 人工脚本 | 4.1.3 |
| A11Y-09 | MUST | 显示缩放 100–200% 与混合 DPI 下不模糊、不截断；窗口在 1366 × 768（100%）和 1920 × 1080（150%）的工作区完整显示；Windows“文本大小”150% 时关键文字不被截断 | 断言窗口 `MinWidth` / `MinHeight`；人工检查两种工作区 | 1.4.4、1.4.10 |
| A11Y-10 | MUST | 页面标题为 `HeadingLevel` Level1，卡片与分区标题为 Level2 | UIA 树测试 | 1.3.1、2.4.6 |
| A11Y-11 | MUST | 对话框与带遮罩的面板：`IsDialog`、初始焦点、Tab 循环、Esc、焦点归还 | 自动化测试检查 `Keyboard.FocusedElement` | 2.4.3、2.4.11 |
| A11Y-12 | MUST | 不只靠颜色传达状态、日志级别、筛选与选中、差异 | 评审清单与灰度截图 | 1.4.1 |
| A11Y-13 | MUST | 指针目标 ≥ 24 × 24 epx 或满足间距例外 | 遍历可视树断言可点击元素的 `ActualWidth` / `ActualHeight`（例外列在测试白名单） | 2.5.8 |
| A11Y-14 | MUST | 以上自动化检查放在 `tests/test-suites.json` 中工作台的现有套件，由合并门（本机 `merge-check`）与完整准备（`prepare`）阻断回归（[ADR 0031](decisions/0031-release-path.md)） | 合并门、`prepare` | 无 |
| A11Y-15 | SHOULD | 每个页面在 `--mock` 与夹具状态下 AI4W FastPass 零失败 | 发布前人工运行 | 无 |

### 6.4 WCAG 2.2 A / AA 准则对照

| 准则 | 工作台中的要求 | 规则 / 验证 |
|---|---|---|
| 1.1.1 非文本内容 | 图标按钮、状态圆点有文字替代；装饰元素不进入 UIA 控件视图 | ICO-02、A11Y-01 |
| 1.3.1 信息与关系 | 标签关联输入框；标题有 HeadingLevel；分段控件有组名；表格列头可被读出 | CTL-02、CTL-05、A11Y-02、A11Y-10 |
| 1.3.2 有意义的顺序 | UIA 树顺序与阅读顺序一致；浮层在树中靠后时须获得焦点 | KEY-02，Inspect |
| 1.3.3 感官特征 | 说明不只用位置或颜色指代（“见右侧”须写出区域名） | TXT-03，文案评审 |
| 1.3.4 方向 | 不锁定方向 | 人工 |
| 1.4.1 颜色的使用 | 不只靠颜色 | COL-06、A11Y-12 |
| 1.4.3 对比度（最低） | 文字 ≥ 4.5:1；工作台不使用大字例外 | COL-04、A11Y-06 |
| 1.4.4 调整文字大小 | 200% 显示缩放或“文本大小”放大下不丢内容和功能 | A11Y-09，偏差 UI-20 |
| 1.4.5 文字图像 | 不用文字图片 | 评审 |
| 1.4.10 重排 | 窄窗口下非表格内容单列重排，表格可横向滚动 | LAY-01、LAY-03 |
| 1.4.11 非文本对比度 | 焦点、状态指示、控件边界 ≥ 3:1 | COL-05、KEY-03 |
| 1.4.13 悬停或焦点内容 | ToolTip 可关闭、可悬停、持续显示；不在 ToolTip 中放唯一信息 | TYP-06 |
| 2.1.1 键盘、2.1.2 无键盘陷阱 | 全键盘操作，模态外无陷阱 | KEY-01、KEY-09、A11Y-04 |
| 2.1.4 字符快捷键 | 不定义单字符快捷键（访问键需 Alt） | 评审 |
| 2.2.1 可调整时限 | 审批超时可选，按“必要”例外处理 | APR-06，偏差 UI-24 |
| 2.2.2 暂停、停止、隐藏 | 自动更新的调用列表与日志尾部可暂停跟随（“跟随最新”开关） | 人工 |
| 2.3.1 闪烁 | 不闪烁 | STS-08 |
| 2.4.3 焦点顺序 | 焦点进入、循环和归还 | KEY-02、KEY-08、DLG-02、DLG-10、A11Y-11 |
| 2.4.4 链接目的、2.4.6 标题和标签 | 按钮结合所在行或卡片可判断目的；标题与标签描述内容 | 文案评审 |
| 2.4.7 焦点可见 | 焦点环 | KEY-03、A11Y-05 |
| 2.4.11 焦点不被遮挡（最低） | 焦点元素不被 toast、提示条或遮罩完全遮住 | STS-07、A11Y-05 |
| 2.5.1 指针手势、2.5.7 拖动 | 无多点或路径手势，无仅能拖动完成的功能 | 评审 |
| 2.5.2 指针取消 | 抬起时触发 | KEY-01，代码扫描 `MouseLeftButtonDown` |
| 2.5.3 名称包含标签 | 名称以可见文字开头 | A11Y-01 |
| 2.5.8 目标尺寸（最低） | ≥ 24 × 24 epx | CTL-10、A11Y-13 |
| 3.1.1 页面语言、3.1.2 部分语言 | 根元素 `Language` 随界面语言；中文界面中的普通英文词须翻译，产品名与技术标识例外 | TYP-09、LOC-01 |
| 3.2.1 获得焦点、3.2.2 输入 | 获得焦点不切换上下文；选择会话不自动附着；关闭审批弹确认属于预期行为 | 人工 |
| 3.2.6 一致的帮助 | 帮助入口（设置 › 服务 › 关于、环境体检页的首次连接说明）位置固定 | 评审 |
| 3.3.1 错误识别、3.3.3 错误建议 | 出错项以文字指出并给出修正建议 | ERR-01、ERR-03 |
| 3.3.2 标签或说明 | 可见标签，不只靠占位符 | CTL-02 |
| 3.3.4 错误预防 | 修改项目经确认或审批 | DLG-04、§5 |
| 3.3.7 冗余输入 | 已填写的路径、地址在同一流程中自动带入 | 人工 |
| 3.3.8 无障碍认证（最低） | 共享密钥可生成、可粘贴、可显示，不需记忆 | 人工 |
| 4.1.2 名称、角色、值 | 名称、角色、状态可读 | A11Y-01、A11Y-03、CTL-04、CTL-06 |
| 4.1.3 状态消息 | UIA live region | STS-02、AI-03、APR-07、A11Y-08 |

不适用或替代：1.2.x、1.4.2、2.5.4 不适用（无音视频、无运动操作）；1.3.5 不适用（无收集个人信息的字段）；1.4.12 通常不适用（Windows 与 WPF 没有覆盖行距、字距的机制），仍按 LAY-06 不用固定高度。EN 301 549 与 Section 508 认为 2.4.1、2.4.5、3.2.3、3.2.4 对软件不适用，本文分别以 NAV-09、无、NAV-06、CMD-09 作 SHOULD 级替代；2.4.2 以 NAV-03 补充。4.1.1 已在 WCAG 2.2 中删除。

### 6.5 验收与测试

- 新增或修改界面的 PR 自查：COL-01 与 TYP-03（无字面颜色和字号）、CTL-02 与 CTL-04（名称与标签）、KEY-03 与 KEY-04（焦点与 Esc）、LOC-01（无硬编码文字）、AI-02 与 APR-02（守卫与焦点），并运行 §6.3 的自动化检查；评审清单见手册[第 21.13 节](engineering-handbook.html#ui-checklist)。
- 每次发布前：A11Y-04 键盘脚本、A11Y-07 高对比人工检查、A11Y-08 Narrator 脚本、A11Y-09 两种工作区检查、A11Y-15 AI4W FastPass，结果记入发布证据。
- 渲染测试覆盖 1024 × 640 与 1366 × 860、中英文、浅色 / 深色 / 高对比；`ThemeSurfaceTests` 与 `AccessibilityTests` 固定三套主题的 token 与对比度；`WorkbenchControlTests` 覆盖 AI 页面白名单。
- 设计值断言（尺寸、宽度、页数、颜色、字体）按维护者 2026-11-01 的批准随设计系统更新；行为、无障碍、键盘、审批和 AI 红线类断言不放宽。ADR 0028 要求新增的断言：破坏性请求的默认按钮是“拒绝”、回车永不批准写入请求、Esc 不拒绝写入请求、按当前页拒绝 AI、风险等级在宿主侧计算。

## 7 文案用语

### 7.1 术语

| 对象 | 用语 | 不用 |
|---|---|---|
| MCP 宿主及其 worker 的总称 | 服务（Service） | 引擎（Engine） |
| TIA Portal 中的项目 | 项目（Project） | 工程 |
| TIA Portal | TIA Portal | 博途 |
| 产品 | 自动化工程工作台 | TIA 工作台、TIA Workbench |
| 写操作的决定 | 审批、批准、拒绝 | 同一动作的其他说法 |
| 操作来源 | AI（MCP 客户端的操作）、人（工作台）（面板的操作） | 混用 |
| 客户端名 | 名称后标“（未验证）”（名称由客户端自报） | 不标注 |

会话、工作区、MCP 客户端等其余术语全目录一致；用词来源见 [ADR 0026](decisions/0026-documentation-structure-and-language.md) 与 ADR 0028。

### 7.2 规则

| ID | 级别 | 规则 |
|---|---|---|
| LOC-01 | MUST | 所有可见与可读出的文字（含 `AutomationProperties.Name` / `HelpText`、ToolTip、通知文字、窗口与对话框标题、单位）来自 `Strings` 目录的（键，英文，中文）三元组，由 `StringsTests` 覆盖。例外：产品名（TIA Portal、MCP、VCI、SimaticML）、用户数据、技术标识（TYP-08）。 |
| LOC-02 | MUST | 句子不用字符串拼接；用带占位符的格式串（`Loc.T`），让译文可以调整语序；分隔符“ · ”统一管理。 |
| LOC-03 | MUST | 单位本地化（“{0} 秒” / “{0}s”、“{0} 毫秒” / “{0} ms”、百分号）。 |
| LOC-04 | MUST | 布局容纳语言差异（英文通常比中文宽 1.5–2 倍）：文字按钮用 `MinWidth`，按钮与段落文字不截断，过长时换行或改布局；每个页面在两种语言、最小窗口下检查。 |
| LOC-05 | MUST | 数字与日期按用户的区域格式（`CultureInfo.CurrentCulture`），不按界面语言推断区域；XAML 绑定中的 `StringFormat` 指定 `ConverterCulture` 或经格式化转换器；日志与审计的技术时间戳可固定为 `yyyy-MM-dd HH:mm:ss zzz`。 |
| LOC-06 | MUST | 中文用全角标点（，。：；（）“”）；中文与英文或数字之间留一个空格（“TIA Portal V21”“60 秒”）；紧凑状态用中点“·”分隔。 |
| LOC-07 | MUST | 按 §7.1 的术语表全目录一致。 |
| LOC-08 | MUST | 切换语言即时生效（`Loc` 索引器绑定），UIA 名称同样绑定到目录、随语言更新。 |
| LOC-09 | MUST | 中英两种文案含义与操作指引对等，不在一种语言里多出说明或少掉信息。 |
| TXT-01 | MUST | 句子短、主动语态、动词开头、先说重点；标题说清要发生什么（“在项目 Demo 中编译 2 个块”，不写“编译请求”）。 |
| TXT-02 | MUST | 英文用句首大写，不用全大写；中文不用感叹号，不用“您”；少用“请”，只在请用户做麻烦的事时使用。 |
| TXT-03 | MUST | 用与设备无关的动词：“选择 / 打开”（select / open），不写“点击”（click），除非确实只针对鼠标。 |
| TXT-04 | MUST | 标签、按钮、单选与复选文字不加句号；完整句子（ToolTip、错误、对话框正文）以句号结尾。 |
| TXT-05 | MUST | 对话框与确认面板的标题和按钮一问一答：按钮是对标题问题的直接回答。 |
| TXT-06 | MUST NOT | 在确认中写“警告 / 注意 / Warning”，用图标表达；不用全大写词强调。 |
| TXT-07 | SHOULD | 首次出现的缩写在说明里解释一次（MCP、VCI、stdio、lane）；面向工程师的技术区域可以直接用通行术语。 |
| TXT-08 | MUST | 人 / AI 的归属按 §7.1 的固定说法，审计与审批中一眼可辨。 |

## 8 偏差登记

现状与本规范的差异逐条登记在这里；位置、计算和成本的原始记录见[归档的偏差清单](../archive/development/design-spec/deviations/workbench.md)（2026-10-11 测量，P8-80 与 2026-11-01 审查更新）。

状态取值：“4.0 前（待核对）”表示 P8-61 已合并（`7cd04605`、`dbe99a69`），但该条尚未逐项核实；“随 UI-2x”表示由设计系统迁移任务改正；“4.0 后”；“有意偏差”须写明理由与复审时间。

| ID | 现状 | 规则 | WCAG 2.2 | 状态 |
|---|---|---|---|---|
| UI-06 | 只能用鼠标或在按下时触发：顶栏状态区用 `MouseLeftButtonUp` 打开审批菜单；遮罩、toast 正文、程序块行在按下时触发；配置页说明文字只响应鼠标 | KEY-01、CMD-10、STS-01 | 2.1.1、2.5.2 | 4.0 前（待核对；状态区随 UI-2b 改为侧边栏） |
| UI-07 | 部分模板聚焦时无变化（安静按钮、页签、表格项、Expander） | KEY-03、CTL-01、A11Y-05 | 2.4.7、1.4.11 | 4.0 前（待核对；UI-2a 已加 focus-ring） |
| UI-09 | 选中底色与背景对比约 1.0–1.1:1；筛选按下态只换文字色；程序块列表的键盘选中没有视觉 | COL-05、COL-06、CMD-07 | 1.4.11、1.4.1 | 4.0 前（待核对） |
| UI-10 | 日志级别只靠文字颜色，没有级别文字或图标 | COL-06 | 1.4.1 | 4.0 前（待核对） |
| UI-12 | 输入框边界对比曾为 1.45:1 | COL-05 | 1.4.11 | 4.0 前（待核对；UI-2a 改用 border-control #86868B / #8E8E93） |
| UI-14 | 指针目标过小：项目“⋯”按钮 20 × 16、审计“跳转”高 18、待批徽标高 20、安静按钮高 22、程序块行复选框 16 × 16 | CTL-10、A11Y-13 | 2.5.8 | 4.0 前（待核对） |
| UI-16 | 没有 F6 区域切换（HeadingLevel 部分已完成） | NAV-09 | 2.4.1（按 SHOULD） | 4.0 后 |
| UI-17 | 视图与样式中仍有小于 12 epx 的字面字号 | TYP-04、TYP-02 | 无（关联 1.4.4） | 4.0 前（待核对） |
| UI-19 | 快捷键没有 ToolTip 与 `AcceleratorKey`；用户文档的 Ctrl+1 / Ctrl+2 与代码相反，并描述已删除的菜单栏 | CMD-05、KEY-07 | 无 | 4.0 前（待核对；Ctrl+1…8 顺序随 UI-2b 改） |
| UI-20 | 不跟随 Windows“文本大小”；全局 `TextBlock` 默认截断，完整值只在不可聚焦元素的 ToolTip 中 | A11Y-09、TYP-06、LAY-06 | 1.4.4（部分） | 4.0 后 |
| UI-21 | 嵌套的可交互元素与双重停靠点（AI 调用行内按钮、Expander 标题内的“修复”、程序块行复选框） | CTL-03、CTL-07 | 2.1.1 | 4.0 后 |
| UI-22 | 页签用 `RadioButton`；复选框套按钮样式（“显示”“全部”）；“会话已锁定”用不可聚焦的复选框 | NAV-04、CTL-06 | 4.1.2（技术上满足） | 4.0 后（页签随 UI-2b 改为侧边栏） |
| UI-23 | 自绘窗口按钮无 ToolTip、最大化后不切换还原图标、无非激活态、不显示 Snap 布局 | ICO-05、LAY-11、COL-10 | 4.1.2 | 4.0 后 |
| UI-24 | 审批超时自动拒绝（60 / 120 / 300 秒） | APR-06 | 2.2.1（“必要”例外） | 有意偏差：超时是安全设计的一部分，AI 客户端的调用本身有时限，延长到默认值 10 倍会使请求失效；MAY 增加 600 秒选项；复审 4.1 |
| UI-26 | AI 操作提示只是次要色小字、不播报；`Navigate` 折叠旧页时不处理焦点 | AI-03、AI-07、NAV-07 | 4.1.3、2.4.3 | 4.0 前（待核对；提示随 UI-2b 进提示条） |
| UI-29 | 输入守卫只拦截鼠标与 Enter / Space；访问键启用后，Alt+字母可能在 AI 切页后 500 ms 内触发新页面的按钮 | AI-02 | 无 | 4.0 前（待核对） |
| UI-30 | “允许 AI 控制界面”是设置抽屉顶部的复选框，无说明，与审批开关控件不同 | AI-06、CMD-08 | 无 | 随 UI-2b（设置 › AI 控制） |
| UI-33 | 调色板外的颜色与按主题分支的触发器（P8-80 已改 `Primer.countWarningBg` 与遮罩，并去掉 `EffectivelyDark` 分支） | COL-01、COL-11 | 无 | 4.0 前（待核对） |
| UI-34 | 审批界面的生成计划用未套主题的 `DataGrid` / `TreeView`，列头“ID”为字面值 | CTL-08 | 无 | 随 UI-2c（先渲染核实） |
| UI-35 | 项目选项侧栏背后用不透明正文色铺满（按代码推断） | COL-01、DLG-10 | 无 | 4.0 前（待核对，先渲染核实） |
| UI-36 | 视图中仍散落字面 `FontSize`，字号 token 未普遍使用 | TYP-02、TYP-03 | 无 | 4.0 后；新代码 MUST 遵守 TYP-03 |
| UI-38 | 视图中约 108 个间距分量不在 4 epx 网格上 | LAY-04、LAY-05 | 无 | 4.0 后；新代码 MUST 遵守 |
| UI-40 | 关闭类按钮仍用 Segoe Fluent Icons 私用区字符；其余已改为 `Icon.*` | ICO-01、ICO-03 | 无 | 有意偏差（文字按钮政策保留）；私用区字形 4.0 后统一，新代码 MUST 遵守 ICO-01 |
| UI-41 | 同一面有多个强调按钮（项目选项侧栏的“连接”“打开”） | CMD-02 | 无 | 4.0 后（待核对） |
| UI-42 | 忙碌时进度条为空（未用不确定进度）；除 MCP 面板外没有取消；忙碌时关闭窗口被静默取消 | STS-03、STS-04、STS-05 | 4.1.3 | 4.0 前（待核对） |
| UI-43 | toast 只有一个位置，诊断反馈与待批提示互相覆盖；持续状态只用小字 | STS-06、STS-07、STS-09 | 4.1.3 | 4.0 后（持续状态随 UI-2b 进提示条） |
| UI-44 | 消息框不显示图标、错误时整段染红；“未预期的错误”用于预期内的错误；直接显示 `ex.Message`；启动阶段用系统 `MessageBox` | DLG-01、DLG-07、ERR-01、ERR-02 | 3.3.1、3.3.3（基本满足） | 4.0 后 |
| UI-45 | 未打开项目时禁用的页面没有说明，对应快捷键静默无效 | NAV-05 | 无 | 4.0 后 |
| UI-46 | `Window.Title` 不含页名；产品名有“TIA 工作台”“TIA Workbench”等写法 | NAV-03、LOC-07 | 2.4.2（按 SHOULD） | 4.0 后 |
| UI-47 | 硬编码文字与单位（“ · Software”、“PLC”“ID”、“60s”、“TIA Workbench”、“TIA Portal”消息框标题、“IO_FAILED: ”等） | LOC-01、LOC-03 | 3.1.2（轻微） | 4.0 前（待核对） |
| UI-48 | 文案问题：英文全大写表头、“点击 / click”、中英不对等、“项目 / 工程 / 博途”混用、半角逗号、同一功能不同叫法、用位置指代 | TYP-05、TXT-02、TXT-03、TXT-06、LOC-06、LOC-07、LOC-09、CMD-09 | 1.3.3、3.2.4（轻微） | 4.0 前（待核对；“引擎”改“服务”随 UI-2d） |
| UI-50 | 设计系统迁移未完成：外框仍是 216 epx 侧栏加顶栏；快捷键仍是 Ctrl+1 = MCP 与客户端、Ctrl+2 = 工程操作；审批与设置仍是抽屉；`WorkbenchPage` 仍含概览入口；悬停动效 0.12 s（`PrimerVocabularyTests` 固定），设计系统为 150 ms | §2.5、§3、§5、NAV-01、KEY-07 | 无 | 随 UI-2b…UI-2d；动效断言按设计值断言规则改 |

已关闭，编号不复用：

- 已完成（2026-11-01 审查核实）：UI-01、UI-02、UI-03、UI-04、UI-05、UI-08、UI-11、UI-13、UI-15、UI-18、UI-25、UI-27、UI-28、UI-49，以及 UI-16 的 HeadingLevel 部分。旧版第 0.4 节列出的“4.0 前必须达到的要求”都在其中。
- 只涉及 Ant Design 或旧字体，已失效：UI-31（Ant 配色与固定主色；强调色现由 ADR 0028 固定为 #0071E3）、UI-32（未用 WPF Fluent 主题；ADR 0028 采用共用设计系统，不再计划迁移）、UI-37（随包字体；ADR 0021 改为 Maple Mono CN，ADR 0028 又改为系统字体）、UI-39（Ant 圆角值）。
