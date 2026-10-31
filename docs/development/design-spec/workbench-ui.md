# 自动化工程工作台（Workbench）界面设计规范

状态：现行（v0.3）· 最后核对：2026-10-22，P8-80d

[设计规范索引](README.md) · [工作台偏差清单](deviations/workbench.md)

- 适用范围：`src/Studio/Gui`（`TiaOpenness.exe`，WPF，.NET 10，`net10.0-windows`）的全部界面：主窗口与标题栏、页签、工程操作 / 程序块 / 版本控制 / AI 调用 / 审计 / 环境 / 日志页、MCP 与客户端（Configuration）页、设置 / 审批 / 调用详情抽屉、选项侧栏、确认层、消息框、toast 与系统通知、`--mock` 离线演示。测试位于 `tests/Studio/TiaOpenness.Gui.Tests`。
- 配套文件：[工作台偏差清单](deviations/workbench.md) 逐项列出现状与本规范的差异（UI-nn）及处理状态；4.0 前的修改由 P8-61 实施。

## 0 如何使用本规范

### 0.1 基线与优先级

冲突时按下列顺序取上位：

1. **项目红线**：审批与“AI 控制工作台界面”的规则，来源 [工作台控制设计](../design/workbench-control-and-shared-session.md)（§2.7、§3.3、§3.4、§4、§5.2–5.3 及 2026-10-08 维护者决定）。本文 §11 原样收录，标注“项目规则”。
2. **WCAG 2.2 AA（可量化的无障碍底线，2026-10-11 维护者决定）**：[WCAG 2.2](https://www.w3.org/TR/WCAG22/) 的 A 级与 AA 级成功准则，按 W3C [WCAG2ICT（2025-12-11 Group Note）](https://www.w3.org/TR/wcag2ict-22/) 对非 Web 软件的解释应用到 WPF 桌面程序。逐条适用性、工作台解释、验证方法与 Microsoft 对应条目见 §15。
3. **Microsoft Windows 应用设计指南**：learn.microsoft.com/windows/apps/design（Fluent、布局、字体、颜色、命令、控件、对话框、无障碍、键盘、全球化、写作）。
4. **WPF 与 Windows 桌面专项指南**：WPF 焦点与 UI Automation 文档、.NET《Accessibility Best Practices》、Win32 高 DPI 指南；新版指南缺少对应条目时才引用 Windows 7 时期的 Win32 UX Guide（`win32/uxguide`，在 Microsoft Learn 上仍在维护，但示例外观已过时）。
5. 本规范自定义的数值与模式（标注“项目规则”）。

无障碍方面：WCAG 2.2 AA 是必须达到的底线；Microsoft 指南在其上增加要求（高对比主题、访问键、F6 区域切换、焦点视觉规格等）。EN 301 549 或 Section 508 认为对非 Web 软件“不适用”的准则（2.4.1、2.4.5、3.2.3、3.2.4，以及 EN 301 549 对 2.4.2 的处理），本规范用等价的 SHOULD 规则替代，并在 §15 写明。

### 0.2 用词

- **MUST / MUST NOT**：必须遵守；新代码不满足不得合并。存量差异只能经偏差清单的 UI-nn 条目处理（改正，或记为有意偏差并写明理由）。
- **SHOULD**：默认遵守；不遵守时须在评审中说明理由。
- **MAY**：可选。
- 每条规则后列“理由”（一句话）与“依据”（Microsoft 或 W3C 原文链接与小节）。Microsoft 原文只做转述，不整段引用。所列 URL 均于 2026-10-11 核对过；部分旧的 `/windows/apps/design/...` 地址会重定向到 `/windows/apps/develop/...`，本文使用重定向后的规范地址。
- “WPF：”说明在 WPF 中如何落实，或指出某项为 WinUI 专有（见 §2 对照表）。

### 0.3 规则编号

`PRI` 原则，`PLT` 平台，`LAY` 布局，`TYP` 字体，`COL` 颜色与主题，`ICO` 图标，`NAV` 导航与页面结构，`CMD` 命令，`CTL` 控件，`DLG` 对话框与确认，`APR`/`AI` 审批与 AI 控制红线（项目规则），`STS` 状态、进度与通知，`ERR` 错误与空状态，`KEY` 键盘与焦点，`A11Y` 可测的无障碍要求（WCAG 映射见 §15），`LOC` 本地化，`TXT` 界面文字。

### 0.4 4.0 前必须达到的要求（维护者决定 2026-10-11）

下列要求在 4.0 发布前完成（任务 P8-61），对应条目的现状与证据见 [工作台偏差清单](deviations/workbench.md)：

| 要求 | 规则 | 偏差 |
|---|---|---|
| 支持 Windows 高对比主题 | COL-02、COL-03、A11Y-07 | UI-01（前置 UI-33） |
| 可操作元素、输入控件和列表项有 UI Automation 名称与标签 | ICO-02、CTL-02、CTL-04、A11Y-01、A11Y-02 | UI-02、UI-03、UI-04 |
| 对话框、抽屉与选项侧栏的焦点管理；对话框按钮与主要字段的访问键 | DLG-02、DLG-10、KEY-04、KEY-06、KEY-08、A11Y-11 | UI-05、UI-18（同批 UI-29） |
| 承载信息的文字对比不低于 4.5:1，测试门槛相应提高到 4.5 / 3.0 | COL-04、COL-08、A11Y-06 | UI-08、UI-49 |
| 窗口最小尺寸降到不超过 1024 × 640 epx | LAY-01、A11Y-09 | UI-13 |
| 有风险的确认默认选择取消，提交按钮用具体动词 | DLG-04、APR-09 | UI-28 |
| 被 AI 预填的字段有可见标记和“清除预填” | AI-04 | UI-25 |

清单中其余“4.0 前”条目按各自建议同样由 P8-61 完成。提高测试门槛属于收紧检查，符合仓库“只收紧、不放宽”的规则。

## 1 原则

- **PRI-01 · MUST** 人优先、安全优先：AI 可以显示、定位和预填，但改变工程、批准写操作、改动审批或 AI 控制开关的最后一步，只能由人在可见、可读、可用键盘操作的界面上完成。理由：工作台是审批与人工确认的唯一界面。依据：项目规则（phase8-workbench-control.md §2.7、§4）。
- **PRI-02 · MUST** 与 Windows 一致：窗口、键盘、对话框、焦点、主题的行为遵循 Windows 约定，不自创交互。理由：用户在 TIA Portal 与工作台之间来回切换，熟悉的行为减少误操作。依据：[Navigation basics › Principles of good navigation（Consistency）](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation)。
- **PRI-03 · MUST** 无障碍是验收条件，不是附加项：键盘、读屏、高对比、缩放和 WCAG 2.2 AA（§15）都纳入合并门禁。理由：审批界面若无法被所有用户可靠操作，安全设计就会失效。依据：[Accessibility checklist](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)（第 1–6 条）。
- **PRI-04 · MUST** 设计值 token 化：颜色、字号、间距、圆角、尺寸只来自资源字典；视图里不写字面值。理由：主题（含高对比）和缩放只能作用于 token。依据：[Contrast themes › Hard-coded colors](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes#hard-coded-colors)；[.NET Accessibility Best Practices › Don't Hard-Code Colors](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices#visual-ui-design)。
- **PRI-05 · MUST** 信息不只靠颜色表达。理由：色觉差异用户和高对比主题下颜色语义会丢失。依据：[Accessibility checklist › 4. Verify color and contrast](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)；[Color in Windows › Usability](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)。
- **PRI-06 · MUST** 本地化优先：所有可见和可被读屏读出的文字（含 UIA 名称、提示、通知）都来自 `Strings` 目录。理由：中英双语切换必须完整。依据：[Expose basic accessibility information › Accessible names and localization](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-names-and-localization)；[Globalization and localization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal)。
- **PRI-07 · SHOULD** 少打扰：确认对话框只用于不可撤销且后果重大的操作；状态变化优先用内联或状态栏提示。理由：频繁确认会让用户不看就点。依据：[Commanding basics › When to confirm or undo actions](https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics#when-to-confirm-or-undo-actions)；[Confirmations（Win32 UX Guide）](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)。

## 2 平台映射：WPF 与 WinUI

Microsoft 新版设计指南以 WinUI 控件为例。下表说明工作台（WPF .NET 10）如何落实每个概念；“WinUI 专有”表示 WPF 没有对应 API，需要用所列方式实现。

| Microsoft 概念 | WinUI 做法 | 工作台（WPF）做法 | 相关规则 |
|---|---|---|---|
| Fluent 控件外观 | 默认 | .NET 9+ 可用 `ThemeMode`（System/Light/Dark）或 `PresentationFramework.Fluent` 资源字典；工作台目前用自绘的 Primer 模板（Aero2 为基底） | PLT-01 |
| 系统强调色 | 主题资源 | `SystemColors.AccentColor*`（.NET 9+，有无 Fluent 主题都可用） | COL-07 |
| ContentDialog | WinUI 专有 | 统一的 `WorkbenchDialog`：带 Owner 的模态 `Window`（`ShowDialog`），或满足 DLG-02 的窗口内模态层 | DLG-01 |
| InfoBar | WinUI 专有 | 自建 Banner 组件（严重级别图标 + 标题 + 正文 + 操作 + 关闭） | STS-06 |
| NavigationView（顶部）/ SelectorBar | WinUI 专有 | 页签条；UIA 上应呈现为可选择的列表或 TabControl | NAV-04 |
| ToggleSwitch | WinUI 专有 | `ToggleButton` + 开关模板（UIA Toggle 模式） | CMD-08 |
| ProgressRing | WinUI 专有 | `ProgressBar IsIndeterminate="True"` | STS-03 |
| 文字缩放 `IsTextScaleFactorEnabled` | WinUI 专有 | WPF 不自动跟随“文本大小”；需读取 `UISettings.TextScaleFactor`（需 Windows 10 TFM）并缩放字号 token | A11Y-09 |
| 高可见焦点视觉（FocusVisualPrimary/Secondary） | WinUI 专有 | 全局 `FocusVisualStyle`（`SystemParameters.FocusVisualStyleKey`），外 2 px + 内 1 px | KEY-03 |
| ThemeDictionaries 的 HighContrast | WinUI 专有 | 检测 `SystemParameters.HighContrast`，把调色板槽位换成映射到 `SystemColors.*BrushKey` 的高对比字典 | COL-02 |
| 访问键 KeyTips | WinUI 专有 | WPF 原生 `_` 助记符 + `AccessText`（模板中 `RecognizesAccessKey="True"`），按 Alt 显示下划线 | KEY-06 |
| KeyboardAccelerator | WinUI 专有 | `KeyBinding` / `InputGesture` + `AutomationProperties.AcceleratorKey` | CMD-05 |
| HeadingLevel、IsDialog | 两者皆有 | WPF windowsdesktop 6.0+ 提供 `AutomationProperties.HeadingLevel`、`AutomationProperties.IsDialog` | A11Y-10/11 |
| LandmarkType | WinUI | WPF 以 HeadingLevel 与 F6 区域为主；若要提供 landmark，须用自定义 AutomationPeer 并先在 Inspect 中验证 | NAV-09 |
| Live region | 两者皆有 | WPF 4.7.1+：`AutomationProperties.LiveSetting` + `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` | STS-02 |
| 自定义标题栏 | AppWindow 绘制标题按钮 | `WindowChrome` + 自绘按钮；Windows 11 Snap 布局需要标题按钮命中测试返回 HTMAXBUTTON（WPF 默认不提供） | LAY-11 |
| 应用通知 | Windows App SDK | 未打包的 WPF 可用 Windows App SDK `AppNotificationManager`，或保留 `NotifyIcon` | STS-09 |
| 每显示器 DPI | 自动 | 需声明 Per-Monitor V2 并在混合 DPI 下测试 | LAY-10 |

- **PLT-01 · SHOULD** 新控件优先用现有 Primer 词汇（`Primer.xaml`）；4.0 之后评估迁到 WPF Fluent 主题（`ThemeMode`），迁移前任何新模板都必须满足 CTL-02（焦点、禁用、按下、高对比）。理由：两套视觉并存会造成不一致；Fluent 主题自带高对比与强调色支持。依据：[What's new in WPF for .NET 9 › Fluent theme / ThemeMode](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90#fluent-theme)。
- **PLT-02 · MUST** 优先使用原生 WPF 控件并重写模板，而不是为外观派生新控件（保持 `Controls/Ux.cs` 注释中的原则）。理由：原生控件自带键盘行为和 AutomationPeer。依据：[.NET Accessibility Best Practices › Programmatic Access](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices#programmatic-access)；[Visual feedback › Recommendations](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback#recommendations)。

## 3 布局与间距

### 3.1 窗口与断点

- **LAY-01 · MUST** 窗口最小尺寸不超过 1024 × 640 epx，在此尺寸下所有功能可用（允许滚动）。理由：要能放进 1366 × 768（100%）和 1920 × 1080（150%，约 1280 × 672 可用区）的工作区；WCAG 1.4.4、1.4.10 也依赖这一点。依据：[Screen sizes and breakpoints › Breakpoints](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#breakpoints)；[.NET Accessibility Best Practices › Ensure All UI Correctly Scales by Any DPI Setting](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices#visual-ui-design)（UI 应能放进 1024 × 768、120 dpi 的屏幕）。
- **LAY-02 · SHOULD** 最小宽度不超过 960 epx，使窗口能贴靠到 1920 宽屏幕的一半，与 TIA Portal 并排；**MAY** 长期目标为 500 epx。理由：Windows 11 Snap 布局要求窗口能缩到分区宽度；工程师常把 TIA Portal 和工作台并排使用。依据：[Support snap layouts for desktop apps on Windows 11 › What if my app's window shows snap layouts but isn't snapping properly?](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu)。
- **LAY-03 · MUST** 采用响应式断点：宽度 ≥ 1280 epx 时为“内容 + 右侧栏”两列；1008–1279 epx 时右侧栏改为可折叠或移到内容下方；< 1008 epx 时为单列。数据表格属于二维内容，允许横向滚动。理由：固定 300 px 侧栏会在窄窗口中挤压主内容。依据：[Screen sizes and breakpoints](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#breakpoints)；[WCAG 2.2 › 1.4.10 Reflow](https://www.w3.org/TR/WCAG22/#reflow)（二维布局例外）。
- **LAY-10 · MUST** 声明每显示器 DPI 感知（Per-Monitor V2），并在 100/125/150/175/200% 与混合 DPI（窗口跨屏拖动）下验证不模糊、不截断。理由：VM 与笔记本常在不同缩放之间切换。依据：[High DPI Desktop Application Development on Windows › DPI Awareness Mode / Testing Your Changes](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows#dpi-awareness-mode)。
- **LAY-11 · SHOULD** 自定义标题栏保留系统行为：空白处可拖动、右键弹出系统菜单、双击最大化 / 还原、最大化按钮悬停时显示 Snap 布局。理由：用户依赖这些系统级操作。依据：[Windows app title bar › Standard design](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design#standard-design)；[Support snap layouts › How do I fix it?](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu)。

### 3.2 网格、间距与尺寸

- **LAY-04 · MUST** 尺寸、外边距、内边距与位置取 4 epx 的倍数（文字本身不受此限）。理由：在 125%/150%/175% 缩放下能落在整像素上。依据：[Screen sizes and breakpoints › Multiples of four](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#multiples-of-four)。
- **LAY-05 · SHOULD** 使用间距 token（附录 A.1）：按钮之间 8；控件与其上方标题 8；控件与旁边标签 12；内容区（卡片）之间 12；卡片内边距 16；页面左右边距 24。理由：一致的间距本身就表达分组关系。依据：[Content layout and spacing › Spacing and gutters](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics#spacing-and-gutters)。
- **LAY-06 · MUST** 承载文字的行和容器不用固定高度；使用 `Auto`、`MinHeight` 或 `*`。理由：翻译变长、字号放大时，固定高度会截断文字。依据：[Globalization › Guidelines for globalization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization)（界面应能容纳不同长度的文字）；[Accessible text requirements › Text font size and scale](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements#text-font-size-and-scale)。
- **LAY-07 · SHOULD** 文字默认左对齐；只有空状态与图标下的说明居中。理由：左对齐给出稳定的阅读起点。依据：[Typography › Typography best practices in Windows 11（Alignment）](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#typography-best-practices-in-windows-11)。
- **LAY-08 · SHOULD** 圆角：页面内控件 4；浮层（抽屉、对话框、toast、弹出框）与顶层容器 8；相接的直边 0；卡片在 4 与 8 中统一选一个值。理由：圆角层级表达元素的层次。依据：[Geometry in Windows 11 › Rounded corners](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry#rounded-corners)。
- **LAY-09 · SHOULD** 浮层与背景要能区分：常规主题下用边框（以及可选阴影）；高对比主题下浮层使用 2 px 边框。理由：浮层边界必须在各主题中可辨。依据：[Contrast themes › Borders](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes#borders)。
- **LAY-12 · MUST** 标准控件高度 32 epx；紧凑的行内文字按钮不低于 24 epx（指针目标见 CTL-10）；表格行不低于 36 epx。理由：与 Fluent 标准尺寸一致，并满足 WCAG 2.5.8。依据：[Buttons › Recommendations](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#recommendations)；[WCAG 2.2 › 2.5.8 Target Size (Minimum)](https://www.w3.org/TR/WCAG22/#target-size-minimum)。

### 3.3 页面骨架（项目规则）

每个页面使用同一骨架，自上而下：

1. **页头卡片**：页面标题（Subtitle 样式）+ 计数或副标题（Caption，次要色）+ 右对齐的页面级命令（最多一个强调按钮）。
2. **主体**：卡片或表格；卡片间距 12。
3. **右侧栏（可选）**：宽 280–320 epx，放与当前页相关的上下文信息（连接信息、保留策略、诊断、日志尾部）；窄窗口时按 LAY-03 折叠。
4. **空状态 / 加载 / 错误**：占据主体区域，按 ERR-05/06。

外框按 Ant 布局（P8-80）：左侧 Sider 216 epx（品牌、分组菜单与收起按钮；宽度低于 992 epx 时收为 56 epx 图标栏）与 Header 56 epx（面包屑“分组 / 页名”、MCP 状态与会话、待批、诊断、语言与窗口按钮）是固定的外框；其高度允许按 LAY-06 随字号增长。

## 4 字体

- **TYP-01 · MUST** 界面文字与等宽文字统一使用 Maple Mono CN v7.9，只随包带 Regular 与 SemiBold（SIL OFL 1.1）。字体原样放在 Workbench 可执行文件旁的 `Fonts/`，以文件夹 base URI 私有加载，不安装、不作为 WPF Resource 嵌入。`Ui.Font` 回退为 Segoe UI Variable Text、Segoe UI、Microsoft YaHei UI；`Ui.FontMono` 回退为 Cascadia Mono、Consolas、Microsoft YaHei UI。目录缺失或未找到该字体时使用上述系统回退。理由：与 EplanMCP Workbench 的字体和观感一致，显式中文回退避免日文字形。依据：[ADR 0021](../decisions/0021-workbench-eplanmcp-font-and-style.md)；EplanMCP ADR-0017 第 4 条及 TYP-01。
- **TYP-02 · MUST** 只使用下表的字号层级（附录 A.3 给出 token 名）。理由：清楚的层级帮助扫读；token 是文字缩放的接入点。依据：[Typography in Windows › Type ramp](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#type-ramp)。

  | 层级 | 字号 / 行高（epx） | 字重 | 工作台用途 |
  |---|---|---|---|
  | Caption | 12 / 16 | Regular | 窗口标题、状态栏、表头、时间戳、元数据、计数徽章 |
  | Body | 14 / 20 | Regular | 正文、控件文字、对话框正文、说明文字 |
  | Body Strong | 14 / 20 | SemiBold | 卡片标题、分节标题、表格中需强调的名称 |
  | Body Large | 18 / 24 | Regular | 抽屉标题（可选） |
  | Subtitle | 20 / 28 | SemiBold | 页面标题、对话框标题 |
  | Title | 28 / 36 | SemiBold | 仅欢迎页或空工程大标题（可选） |
  | Code | 12 / 16 | Regular（等宽） | 路径、工具名、错误码、哈希、请求 ID、JSON |

  数据密集区域（表格、日志、审批卡元数据）**MAY** 使用 Caption 作为正文；其余场景使用 Body。
- **TYP-03 · MUST NOT** 在视图 XAML 或代码中写字面 `FontSize`；只能引用字号 token 或基于 token 的样式。理由：散落的字号无法统一缩放，也无法评审。依据：[Typography in Windows › Hierarchy / Type ramp](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#type-ramp)。
- **TYP-04 · MUST** Regular 文字不小于 12 epx；**SHOULD** SemiBold 文字不小于 14 epx。理由：更小的字号和字重在部分语言（含中文）中难以辨认。依据：[Typography › Typography best practices in Windows 11（Minimum values）](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#typography-best-practices-in-windows-11)。
- **TYP-05 · MUST** 英文界面使用句首大写（sentence case），不用全大写表达强调或表头；不用 Bold 与斜体（强调用 SemiBold）。理由：全大写更难读，斜体对读写障碍用户不友好。依据：[Typography › Typography best practices in Windows 11（Casing）/ Type ramp 注释](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#type-ramp)。
- **TYP-06 · MUST** 允许用省略号截断，但被截断的完整内容必须能用键盘取得（可聚焦的单元格、详情面板，或 UIA 名称包含全文）；不可聚焦的 `TextBlock` 上的 ToolTip 不能作为唯一途径。理由：键盘与读屏用户无法悬停。依据：[Typography › Clipping and ellipses](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#clipping-and-ellipses)；[Keyboard accessibility › Keyboard alternatives to pointer actions](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-alternatives-to-pointer-actions-and-events-for-specific-control-elements)。
- **TYP-07 · SHOULD** 段落文字（说明、对话框正文）每行约 50–60 个拉丁字符（约 25–35 个汉字）；对话框宽度据此设定。理由：行过长难以阅读。依据：[Typography › Character count](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#character-count)。
- **TYP-08 · MUST** 技术标识（MCP 工具名、V4 错误码、路径、哈希、请求 ID、参数名）用 Code 层级显示，原样不翻译。理由：用户要据此对照日志与文档。依据：[Writing style › Style conventions（Abbreviations）](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#style-conventions)；项目规则。
- **TYP-09 · SHOULD** 根元素的 `FrameworkElement.Language` 跟随界面语言（zh-CN / en-US）并在切换语言时更新，使中日韩字形选择与断行正确。理由：WPF 的字形选择与默认格式化依赖该属性。依据：[Globalization › Display text and fonts correctly](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization)；另见 LOC-05。
- **TYP-10 · MUST** 布局按 Maple Mono 的等宽拉丁字形留余量。文字控件用 `MinWidth` 替代会截断内容的固定 `Width`；页头动作、卡片、侧栏项、按钮、表头和 MCP 与客户端卡片可随可用宽度换行或增高。在 1024、1044×780、1200 与 1366 epx 下检查中英两种语言及浅深主题。现有布局断言不因换字体而放宽；冲突记录实测值交维护者决定。理由：原比例字体的宽度假设不再成立。依据：[ADR 0021](../decisions/0021-workbench-eplanmcp-font-and-style.md)；EplanMCP TYP-10。
- **TYP-11 · MUST** 主窗口与全部 Workbench 窗口、对话框关闭 `Typography.StandardLigatures`、`Typography.ContextualLigatures` 和 `Typography.ContextualAlternates`。理由：`->`、`!=` 等技术文本必须按输入的字符显示。依据：[ADR 0021](../decisions/0021-workbench-eplanmcp-font-and-style.md)；EplanMCP TYP-11。

## 5 颜色与主题

### 5.1 语义 token

视觉规范自 2026-10-18 起为 **Ant Design v5**（维护者决定，P8-80）：WPF 保留，只替换视觉层；浅色调色板对应 Ant 默认算法，深色对应 Ant 暗色算法，高对比继续使用 Windows 系统色。WCAG 2.2 AA 与本规范的对比条款（COL-04、COL-05）优先于 Ant 默认值，下表“取值”列已按此调整，调整理由见[偏差清单 C.0](deviations/workbench.md#c0-ant-design-v5-视觉规范)。

视图只引用语义 token；`Primer.*` 键名保留为实现名，Ant 独有的 token 用 `Ant.*` 键（与 Ant token 同名）。每个键必须对应下表的一个语义角色，并在三个主题中都有值（`ThemeTests`、`ThemeSurfaceTests` 与 `AccessibilityTests` 检查键集一致、取值与对比）。

| 语义 token | 实现键 | Ant token | Light | Dark | 高对比（WPF `SystemColors`） | 用途 | 最低对比 |
|---|---|---|---|---|---|---|---|
| Surface.Window | `Primer.bg` | colorBgLayout | #F5F5F5 | #000000 | `WindowColorKey` | 窗口底色、Segmented 轨道 | 无 |
| Surface.Layer | `Primer.card`、`Primer.input`、`Primer.pill` | colorBgContainer | #FFFFFF | #141414 | `WindowColorKey` / `ControlColorKey` | 卡片、标题栏、页签条、输入框、默认按钮 | 无 |
| Surface.Elevated | `Primer.menuBg` | colorBgElevated | #FFFFFF | #1F1F1F | `WindowColorKey` | 下拉、抽屉、对话框、选中的 Segmented 项 | 无 |
| Surface.Subtle | `Primer.cardSoft`、`Primer.codeBg` | colorFillAlter / 代码底 | #FAFAFA / #F5F5F5 | #1D1D1D / #1F1F1F | `WindowColorKey` | 表头、行悬停、代码块 | 无 |
| Surface.Selected | `Primer.cardSel` | controlItemBgActive | #E6F4FF | #111A2C | `HighlightColorKey`（选中行） | 选中行、选中选项 | 另有 3 px colorPrimary 选中条或勾选（COL-05） |
| Surface.Hover | `Primer.pillHover` | controlItemBgHover（合成后） | #F5F5F5 | #262626 | `ControlColorKey` | 文字按钮、选项、菜单悬停 | 无 |
| Scrim | `Ant.colorBgMask` | colorBgMask | 黑 45% | 黑 45% | 不铺遮罩（不透明度 0）；浮层保留边框 | 抽屉与对话框背后的遮罩 | 无 |
| Shadow | `Ant.boxShadowKey`、`Ant.boxShadowUmbra`、`Ant.boxShadowAmbient` | boxShadowSecondary 三层 | 黑 12% / 8% / 5% | 同左 | 隐藏 | 仅弹出层；不用 `Effect` | 无 |
| Stroke.Layer | `Primer.cardBorder`、`Primer.divider` | colorBorderSecondary / colorSplit | #F0F0F0 / rgba(5,5,5,0.06) | #303030 / rgba(253,253,253,0.12) | `WindowTextColorKey` | 卡片边框、行分隔 | 装饰性可低于 3:1 |
| Stroke.Button | `Ant.colorBorder` | colorBorder | #D9D9D9 | #424242 | `WindowTextColorKey` | 默认 / 虚线按钮、标签边框（按钮由文字识别） | 无 |
| Stroke.Control | `Primer.inputBorder`、`Primer.checkBorder` | （gray-7，替代 colorBorder） | #8C8C8C（3.36:1） | #737373（3.89:1） | `WindowTextColorKey` | 输入框、选择框、复选框、单选框、开关关闭态 | ≥ 3:1 |
| Text.Primary | `Primer.text`、`Primer.codeText` | colorText | 黑 88%（16.5:1） | 白 85%（13.4:1） | `WindowTextColorKey` | 正文 | ≥ 4.5:1 |
| Text.Secondary | `Primer.textMuted`、`Primer.textFaint` | colorTextSecondary | 黑 65%（7.0:1） | 白 65%（8.2:1） | `WindowTextColorKey` | 次要文字、时间戳、占位符（Ant 的 45% / 25% 不达标） | ≥ 4.5:1 |
| Text.Disabled | `Ant.colorTextDisabled` | colorTextDisabled | 黑 25% | 白 25% | `GrayTextColorKey` | 仅限禁用状态 | 豁免 |
| Accent.Text | `Primer.accent`、`Primer.noteAccent` | （blue-7 / 暗色 blue-7） | #0958D9（6.2:1） | #3C89E8（5.2:1） | `WindowTextColorKey` | 链接、选中页签文字、强调文字 | ≥ 4.5:1 |
| Accent.Graphic | `Ant.colorPrimary` | colorPrimary | #1677FF（4.1:1） | #1668DC（3.6:1） | `HighlightColorKey` | 选中条、页签墨条、勾选底、开关、进度、输入焦点边框 | ≥ 3:1 |
| Primary button | `Primer.primaryBg` / `Primer.primaryText`；`Ant.colorPrimaryHover`、`Ant.colorPrimaryActive` | colorPrimary 及悬停 / 按下 | #0958D9 + 白（6.2:1）；悬停 #003EB3、按下 #002C8C | #1668DC + 白（5.2:1）；悬停 #1554AD、按下 #15417E | `ControlColorKey` / `ControlTextColorKey` | 主要按钮、选中筛选标签 | ≥ 4.5:1 |
| Hover text | `Ant.colorLinkHover` | colorLinkHover（加深） | #003EB3（9.0:1） | #65A9F3（7.5:1） | `HotTrackColorKey` | 默认 / 描边 / 链接按钮悬停文字 | ≥ 4.5:1 |
| OnAccent | `Primer.onAccent`、`Ant.colorWhite` | colorTextLightSolid / colorWhite | #FFFFFF | #141414 / #FFFFFF | `WindowColorKey` / `HighlightTextColorKey` | 强调底上的文字、勾号与开关手柄 | 文字 ≥ 4.5:1，图形 ≥ 3:1 |
| Status.Success | `Primer.ok`、`Primer.diffGreen` / `Primer.okBg` / `Ant.colorSuccessBorder` | colorSuccess（green-8） | #237804（5.6:1） / #F6FFED / #B7EB8F | #49AA19（6.2:1） / #162312 / #274916 | `WindowTextColorKey` + 图标 | 成功、新增行 | 文字 ≥ 4.5:1 |
| Status.Warning | `Primer.warn` / `Primer.warnBg`、`Primer.countWarningBg` / `Ant.colorWarningBorder` | colorWarning（gold-9） | #874D00（6.8:1） / #FFFBE6 / #FFE58F | #D89614（7.3:1） / #2B2111 / #594214 | `WindowTextColorKey` + 图标 | 警告、待批徽章 | 文字 ≥ 4.5:1 |
| Status.Error | `Primer.diffRed`、`Ant.colorError` / `Ant.colorErrorBg` / `Ant.colorErrorBorder` | colorError（red-7 / colorErrorActive） | #CF1322（5.6:1）、#D9363E / #FFF2F0 / #FFCCC7 | #E86E6B（6.0:1） / #2C1618 / #5B2526 | `WindowTextColorKey` + 图标 | 错误、删除行、危险按钮文字 | 文字 ≥ 4.5:1 |
| Danger.Solid | `Ant.colorErrorSolid`（悬停 / 按下 `…Hover` / `…Active`） | colorError 实心 | #D9363E + 白（4.6:1）；#CF1322 / #A8071A | #AD393A + 白（6.1:1）；#7E2E2F / #5B2526 | `ControlColorKey` / `ControlTextColorKey` | 风险确认按钮（`Primer.WarningAction`） | ≥ 4.5:1 |
| Status.Info | `Primer.noteBg` / `Primer.noteBorder` | colorInfoBg / colorInfoBorder | #E6F4FF / #91CAFF | #111A2C / #15325B | `WindowColorKey` / `WindowTextColorKey` | 提示框（Alert info） | 文字 ≥ 4.5:1 |
| Tooltip | `Ant.colorBgSpotlight` / `Ant.colorTextLightSolid` | colorBgSpotlight | 黑 85% + 白（15.3:1） | #424242 + 白（10.1:1） | `InfoColorKey` / `InfoTextColorKey` | 工具提示 | ≥ 4.5:1 |
| Focus.Outer / Focus.Inner | `Primer.focusOuter` / `Primer.focusInner` | （2 px colorPrimary 环，代替 4 px #91CAFF 轮廓） | #1677FF / #FFFFFF（4.1:1） | #3C89E8 / #141414（5.2:1） | `WindowTextColorKey` / `WindowColorKey` | 焦点视觉（KEY-03） | 与相邻色 ≥ 3:1 |

非颜色 token 在 `Themes/Primer.xaml`：字号 `Ui.FontSize.*`（12 / 14 / 16，标题 38 / 30 / 24 / 20 / 16）、行高 `Ui.LineHeight*`（22 / 20 / 24）、控件高度 `Ui.ControlHeight*`（32 / 24 / 40）、圆角 `Ui.Radius*`（6 / 2 / 4 / 8）、间距 `Ui.Space*`（4 / 8 / 12 / 16 / 20 / 24 / 32）。字体 token `Ui.Font` 与 `Ui.FontMono` 都私有加载 Maple Mono CN，回退与交付要求见 TYP-01；原 UI-37 由 [ADR 0021](../decisions/0021-workbench-eplanmcp-font-and-style.md) 取代。

### 5.2 规则

- **COL-01 · MUST** 颜色只通过语义 token 以 `DynamicResource` 引用；十六进制颜色只出现在调色板文件中（现有 `ThemeSurfaceTests` 的扫描保留）。理由：主题切换与高对比只替换调色板。依据：[Contrast themes › Hard-coded colors](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes#hard-coded-colors)。
- **COL-02 · MUST** 支持三套主题：浅色、深色、高对比。`SystemParameters.HighContrast` 为真时自动启用高对比字典，优先于用户在设置中选的浅色 / 深色；运行中切换高对比（`SystemParameters.StaticPropertyChanged`）要即时重绘。理由：高对比是 Windows 用户的系统级无障碍设置。依据：[Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)；[.NET Accessibility Best Practices › Support High Contrast and all System Display Attributes](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices#visual-ui-design)。WPF：高对比字典中每个 token 用 `{DynamicResource {x:Static SystemColors.XxxBrushKey}}`。
- **COL-03 · MUST** 高对比配对：Window/WindowText 用于面与文字；Highlight/HighlightText 用于选中、悬停、按下、进行中与焦点；ButtonFace/ButtonText（WPF：`ControlBrushKey` / `ControlTextBrushKey`）用于可交互元素；HotTrack 只用于超链接；GrayText 只用于禁用，不用于次要文字。列表项内的文字在高对比下不得写死前景色，须继承项容器的前景色。理由：不匹配的配对会让文字不可读。依据：[Contrast themes › Contrast colors / Best practices / List items with colored text](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes#contrast-colors)。
- **COL-04 · MUST** 浅色与深色主题中，所有承载信息的文字（含次要文字、提示、时间戳、占位符、徽章文字）对比度 ≥ 4.5:1；只有禁用状态豁免。理由：WCAG 1.4.3。依据：[Accessible text requirements › Contrast ratios](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements#contrast-ratios)；[WCAG 2.2 › 1.4.3](https://www.w3.org/TR/WCAG22/#contrast-minimum)。
- **COL-05 · MUST** 非文字对比 ≥ 3:1：焦点指示、状态指示（选中、勾选、按下、开关）、识别控件所需的边界（输入框边框、复选框框线）。理由：WCAG 1.4.11；Microsoft 的无障碍指南以 WCAG 为准。依据：[WCAG 2.2 › 1.4.11 Non-text Contrast](https://www.w3.org/TR/WCAG22/#non-text-contrast)；[Color in Windows › Usability（Contrast）](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)。
- **COL-06 · MUST** 不只靠颜色传达信息：状态用图标或形状加文字；日志级别带文字标签；差异行带“+/−”前缀；开关与筛选的状态除颜色外还要有形状、字重或勾选标记。理由：WCAG 1.4.1；红绿色弱约占男性 8%。依据：[Color in Windows › Usability（Colorblindness）](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)；[Accessibility checklist › 4](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)。
- **COL-07 · SHOULD** 强调色只用于可交互元素与少量重点；**MAY** 使用品牌青色代替 Windows 强调色（须登记为有意偏差），但两套主题中 OnAccent 与 Accent 的对比都必须 ≥ 4.5:1。理由：强调色宜少而一致；用户可在系统中设置个人强调色。依据：[Color in Windows › Accent color / Color principles](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#accent-color)；[What's new in WPF for .NET 9 › Support for Windows accent color](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90#support-for-windows-accent-color)。
- **COL-08 · MUST** 语义状态（成功 / 警告 / 错误 / 信息）每种都有图标与文字；语义色作按钮底色时，其上文字对比 ≥ 4.5:1（深色主题的警告按钮须换深色文字或加深底色）。理由：状态必须在所有主题中可读。依据：[InfoBar › Color and Icon / Severity](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#severity)。
- **COL-09 · MUST** 主题“自动 / 浅色 / 深色”切换即时生效、不重启（保留 `ThemeManager` 的槽位替换方式）；“自动”跟随 Windows 应用模式。理由：保留会话、日志与选择。依据：[Color in Windows › Color modes and themes](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#color-modes-and-themes)。
- **COL-10 · SHOULD** 标题栏与窗口按钮随主题、高对比以及窗口激活 / 非激活状态变化（非激活时降低不透明度）。理由：用户靠标题栏区分活动窗口。依据：[Windows app title bar › Standard design › Bar](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design#standard-design)。
- **COL-11 · MUST NOT** 在样式里用 `DataTrigger`（例如绑定 `ThemeManager.EffectivelyDark`）切换颜色；主题差异只放在调色板里。理由：加入第三套高对比主题后，按“是否深色”分支会出错。依据：[Contrast themes › Creating theme dictionaries](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)。

## 6 图标

- **ICO-01 · MUST** 图标字形来自 Segoe Fluent Icons（回退 Segoe MDL2 Assets），以命名资源（`Icon.*`）引用；不在模板里内联私用区字符，不用外观近似的 Unicode 字符（✓ ⌄ ⌃ ⋯ !）代替图标。理由：统一的字形在各缩放与高对比下表现一致，也便于命名。依据：[Typography in Windows › Symbols and icons](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography#fonts)；[Windows app title bar › Caption controls](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design#standard-design)。
- **ICO-02 · MUST** 仅含图标的按钮设置 `AutomationProperties.Name` 与 ToolTip，文字来自 `Strings`，名称描述动作（“关闭抽屉”而非“X”）。理由：WCAG 1.1.1、4.1.2。依据：[Expose basic accessibility information › Accessible name（Buttons and links）](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-name)。
- **ICO-03 · SHOULD** 按钮以文字为主（保留 `Themes/Icons.xaml` 注释中的决定）；图标主要用于状态（严重级别、检测结果）与窗口按钮。理由：十几个简短的中文标签旁配图标会增加噪音。依据：项目规则；[Buttons › Button text](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#button-text)。
- **ICO-04 · MUST** 状态图标在形状上可区分（例如对勾 / 感叹号 / 叉），不能只靠圆点的颜色。理由：高对比与色弱下颜色无效。依据：[InfoBar › Color and Icon](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#severity)。
- **ICO-05 · SHOULD** 自绘窗口按钮使用 Segoe Fluent Icons 的 ChromeMinimize（E921）、ChromeMaximize（E922）、ChromeRestore（E923）、ChromeClose（E8BB），最大化后切换为还原图标，背景全幅（full bleed）。理由：与系统标题按钮一致。依据：[Windows app title bar › Caption controls](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design#standard-design)。

## 7 导航与页面结构

- **NAV-01 · MUST** 一级页面为扁平结构，不超过 7 个（现为 6 个）；二级页签只出现在“工程操作”下，不再加深。理由：少于 8 个的同级页适合扁平结构，层级越深越容易迷失。依据：[Navigation basics › Flat/lateral / General recommendations](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#use-the-right-structure)。
- **NAV-02 · MUST** 所有导航（人点击、快捷键、AI 控制）只经过 `MainWindow.Navigate(page)` 一个入口。理由：守卫、快照与页签同步只在这一处实现。依据：项目规则（phase8 §1.2、§2.6）。
- **NAV-03 · MUST** 当前位置始终可见：选中侧栏项有非颜色的指示（3 epx 选中条 + SemiBold）；未选中项恢复 Regular；标题栏显示页名；`Window.Title` 为“<页名> – 自动化工程工作台”。理由：用户与读屏都要知道当前所在页。依据：[Navigation basics › Clarity](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation)；[WCAG 2.2 › 2.4.2 Page Titled](https://www.w3.org/TR/WCAG22/#page-titled)（软件中可不适用，本项作为 SHOULD 级补充）。
- **NAV-04 · MUST** 页签在 UIA 中呈现为“一组可选项”：每项有名称、选中状态、序号与总数（`SizeOfSet` / `PositionInSet`）；页签组是一个 Tab 停靠点，组内用方向键切换。理由：读屏需要知道“第 2 项，共 6 项，已选中”。依据：[Keyboard interactions › Control group](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#control-group)；[NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)。
- **NAV-05 · MUST** 暂不可用的目的地保持可见但禁用，并说明原因（HelpText / ToolTip，例如“连接 TIA 项目后可用”）；指向它的快捷键给出可见反馈，而不是静默无效。理由：用户需要知道怎样才能到达。依据：[Navigation basics › Clarity](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation)。
- **NAV-06 · SHOULD** 每页使用 §3.3 的页面骨架。理由：同样的结构降低学习成本。依据：[Navigation basics › Consistency](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation)。
- **NAV-07 · MUST** 切页后焦点可预期：人点击页签时焦点留在页签上；快捷键或 AI 控制切页时，焦点放到选中的页签或页面标题，绝不放到执行类或审批类按钮上；原焦点元素被折叠时要恢复焦点，不能丢失。理由：不要把初始焦点放在可能造成严重后果的元素上。依据：[Keyboard interactions › Initial focus](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#initial-focus)；项目规则（phase8 §2.7）。
- **NAV-08 · MUST** AI 可定位的页面是白名单：`overview`、`blocks`、`versionControl`、`calls`、`audit`、`environment`、`log`；不含 MCP 与客户端页、设置抽屉、审批抽屉、关闭审批的确认层。理由：控制通道不得把人引向审批或服务启停。依据：项目规则（phase8 §3.3、§4）。
- **NAV-09 · SHOULD** F6 / Shift+F6 在主要区域之间循环：标题栏按钮 → 页签 → 页面主体 → 右侧栏 → 状态栏；每个区域根元素有 `AutomationProperties.Name`。理由：减少逐个 Tab 的成本；替代软件中不适用的 WCAG 2.4.1。依据：[Keyboard accessibility › Keyboard navigation between application panes with F6](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-navigation-between-application-panes-with-f6)；[Landmarks and headings › Usage](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings#usage)。

## 8 命令

- **CMD-01 · MUST** 按钮文字是动词（加对象），英文不超过两三个短词，中文宜在 6 个汉字以内；不加句号；只有在执行前还需要用户补充信息时才加省略号（“选项…”）。理由：按钮文字要一眼看懂动作。依据：[Buttons › Button text](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#button-text)；[Writing style › Buttons](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#buttons)。
- **CMD-02 · MUST** 每个面（卡片、对话框、抽屉中的一节）最多一个强调按钮，全应用只用一种强调色；破坏性操作不用强调样式，警告样式只用于确认对话框里的提交按钮。理由：强调色要少用，主操作才能突出。依据：[Buttons › Recommendations](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#recommendations)；[Color in Windows › Accent color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#accent-color)。
- **CMD-03 · SHOULD** 位置：页面级命令在页头右侧；卡片命令在卡片底部，与内容左对齐；对话框提交按钮在底部右对齐，“执行”在左、“安全 / 取消”在右。理由：位置一致便于寻找。依据：[Buttons › Recommended single-button layout](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#recommended-single-button-layout)；[Dialog controls › General guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines)。
- **CMD-04 · MUST** 因状态不可用的命令，在原因不明显时说明原因（HelpText / ToolTip）；不隐藏用户预期存在的命令。理由：禁用而不说明会让用户卡住。依据：[Commanding basics › Provide command feedback](https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics#provide-command-feedback)。
- **CMD-05 · MUST** 常用命令的快捷键写在 ToolTip（如“保存项目 (Ctrl+S)”）与 `AutomationProperties.AcceleratorKey` 中，并与 Windows 常用快捷键一致；文档中的快捷键表与代码一致。理由：快捷键要可发现、可被读屏告知。依据：[Keyboard interactions › Accelerators / Common keyboard shortcuts](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#accelerators)；[Keyboard accessibility › Keyboard shortcuts](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-shortcuts)。
- **CMD-06 · MUST NOT** 为“批准执行”“拒绝”“关闭审批”设置全局快捷键或访问键以外的快捷方式；批准只能通过点击或聚焦后按 Enter / Space。理由：防止盲批与误触；与项目红线一致。依据：项目规则；[Keyboard interactions › Initial focus](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#initial-focus)（不要让可能造成严重后果的元素轻易被触发）。
- **CMD-07 · MUST** 开关式按钮与筛选按钮在 UIA 中暴露按下状态（ToggleButton → Toggle 模式），视觉上用形状或勾选标记加 ≥ 3:1 的对比表示。理由：WCAG 4.1.2、1.4.11。依据：[Expose basic accessibility information › Role and value](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#role-and-value)。
- **CMD-08 · SHOULD** 即时生效的二元设置（审批开关、“允许 AI 控制界面”）使用开关语义（ToggleSwitch 式）；复选框用于多选或需要再点“保存”才生效的场合。同一抽屉里的二元设置使用同一种控件。理由：开关明确表达“现在是开还是关”。依据：[Toggle switches › Choosing between toggle switch and check box](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggles#choosing-between-toggle-switch-and-check-box)。
- **CMD-09 · SHOULD** 同一功能出现在多处（例如编译在概览卡片和程序块页脚）时，使用同一文字、同一命令对象和同一快捷键。理由：WCAG 3.2.4 的软件内等价要求。依据：[WCAG 2.2 › 3.2.4 Consistent Identification](https://www.w3.org/TR/WCAG22/#consistent-identification)；[Navigation basics › Consistency](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation)。
- **CMD-10 · MUST** 上下文菜单只作加速入口：其中每一项在别处也有入口；上下文菜单可用 Shift+F10 或菜单键打开，宿主元素可获得焦点。理由：上下文菜单对键盘用户不可发现。依据：[Nested UI in list items › Options for secondary actions](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/nested-ui#options-for-secondary-actions)。

## 9 控件

- **CTL-01 · MUST** 每个重写的模板保留：键盘焦点视觉（KEY-03）、禁用外观（不能只靠降低不透明度）、悬停与按下反馈、高对比外观。理由：模板改动过大会损害控件的无障碍。依据：[Visual feedback › Recommendations](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback#recommendations)；[Keyboard accessibility › An example of a visual state for a focus indicator](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#an-example-of-a-visual-state-for-a-focus-indicator)。
- **CTL-02 · MUST** 文本框、下拉框、密码框都有可见标签（位于上方，间距 8），并通过 `AutomationProperties.LabeledBy`（或 `Label.Target`）关联；占位符不能代替标签，占位符文字来自 `Strings` 并作为 HelpText 暴露。理由：WCAG 1.3.1、3.3.2、4.1.2。依据：[Expose basic accessibility information › Labels and LabeledBy](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#labels-and-labeledby)。
- **CTL-03 · MUST** 列表：整个列表一个 Tab 停靠点，方向键在项间移动；Enter 执行项的主操作（例如打开调用详情），Space 切换选择 / 勾选；尽量不在项内嵌按钮，必须嵌套时按 Nested UI 指南处理键盘。理由：项内嵌按钮会造成双重停靠点和名称缺失。依据：[Nested UI in list items › Nested UI handling](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/nested-ui#nested-ui-handling)；[Keyboard interactions › Enter key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#enter-key)。
- **CTL-04 · MUST** 列表项、树节点在 UIA 中有可读的名称：在 `ItemContainerStyle` 上绑定 `AutomationProperties.Name`（来自 `Strings` 的格式串，例如“GetProjectInfo，读，成功，210 毫秒”），或为数据类型提供本地化的 `ToString()`；名称不能是 CLR 类型全名。理由：WPF 默认用数据项的 `ToString()` 作为项名称。依据：[Expose basic accessibility information › Accessible names from dynamic data](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-names-from-dynamic-data)。
- **CTL-05 · SHOULD** 带列头的数据表使用 `ListView` + `GridView` 或已套主题的 `DataGrid`，以便 UIA 暴露表格结构；继续用“ListBox 里放 Grid”的自制表格时，项名称须包含列名与值。理由：WCAG 1.3.1（列头关系）。依据：[Collections and lists](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/lists)；[WCAG 2.2 › 1.3.1](https://www.w3.org/TR/WCAG22/#info-and-relationships)。
- **CTL-06 · MUST** 复选框有内容或名称；需要按钮外观时用 `ToggleButton`，不要把复选框套成按钮样式；只读状态用文字显示，不用禁用或不可聚焦的复选框。理由：外观与角色不一致会误导读屏和视觉用户。依据：[Expose basic accessibility information › Role and value](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#role-and-value)；[Buttons › Is this the right control?](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons)。
- **CTL-07 · SHOULD** Expander 的标题只做展开 / 折叠；其他操作按钮放在标题之外（例如行尾单独一列）。理由：嵌套的可交互元素难以用键盘操作。依据：[Nested UI in list items](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/nested-ui#nested-ui-handling)。
- **CTL-08 · MUST** `DataGrid`、`TreeView`、`ToolTip`、`ContextMenu`、`ScrollBar` 等默认控件都套用调色板 token，深色与高对比主题中不得出现 Aero2 默认的白底。理由：同一界面的视觉一致；高对比必须完整。依据：[Contrast themes › Best practices（Be consistent）](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes#best-practices)。
- **CTL-09 · SHOULD** 文字按钮最小宽度约 120 epx（短标签）、文字不超过 26 个拉丁字符；更长时换行或改布局。理由：过窄的按钮难以命中，过长的按钮难以阅读。依据：[Buttons › Button text](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#button-text)。
- **CTL-10 · MUST** 指针目标不小于 24 × 24 epx（图标按钮建议 32 × 32），或满足 WCAG 2.5.8 的间距例外；行内“安静”按钮高度不低于 24。理由：WCAG 2.5.8 AA。依据：[WCAG 2.2 › 2.5.8 Target Size (Minimum)](https://www.w3.org/TR/WCAG22/#target-size-minimum)（WCAG2ICT：作词替换后适用于软件）。

## 10 对话框与确认

- **DLG-01 · MUST** 所有模态消息使用同一个 `WorkbenchDialog` 组件（含启动阶段的错误，若此时应用资源已可用）；对话框以主窗口为 Owner 并居中；窗口内模态层的根元素设置 `AutomationProperties.IsDialog="True"` 与名称；独立窗口的 `Title` 等于对话框标题。理由：一致的外观与行为；读屏能识别对话框。依据：[Dialog controls › General guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines)；[AutomationProperties.IsDialogProperty](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationproperties.isdialogproperty)。
- **DLG-02 · MUST** 模态语义：打开时焦点移到第一个可聚焦内容或安全按钮；Tab 只在对话框内循环；背景不可交互（`IsEnabled=False` 或独立焦点范围）；Esc 等同安全按钮；关闭后焦点回到触发元素。理由：WCAG 2.4.3、2.4.11；Esc 应取消临时界面。依据：[Keyboard interactions › Esc key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#esc-key)；[Dialog controls › CloseButton](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#closebutton)。
- **DLG-03 · MUST** 每个对话框都有一个安全、无破坏性的按钮（取消 / 关闭），设为 `IsCancel`。理由：用户必须能放心退出。依据：[Dialog controls › Provide a safe action](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#provide-a-safe-action)。
- **DLG-04 · MUST** 有风险的确认：默认按钮（Enter）是安全选项；提交按钮用具体动词（“保存项目”“断开并结束会话”“覆盖块”“关闭审批”），不用“确定 / 取消”；原因不明显时可用“是 / 否”迫使用户读问题。理由：风险操作的默认选择应是“不继续”。依据：[Confirmations › Commit buttons / Default values](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm#guidelines)；[Dialog controls › Confirmation dialogs](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#confirmation-dialogs-okcancel)。
- **DLG-05 · SHOULD** 按钮顺序：“执行”在左，安全按钮在最右，均右对齐在对话框底部。理由：与 Windows 对话框一致。依据：[Dialog controls › General guidelines / The three dialog buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines)；[Buttons › Recommendations](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#recommendations)。
- **DLG-06 · MUST** 标题是简短的主指令或问题（“关闭写操作审批？”）；正文补充后果或细节，不换说法重复标题；首行点明问题。理由：用户读完标题就应能作答。依据：[Dialog controls › General guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines)；[Writing style › Dialogs](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#dialogs)。
- **DLG-07 · MUST** 风险确认与错误对话框显示对应图标（警告 / 错误）并配文字；正文使用常规文字色，不把整段正文染成红色。理由：严重程度不能只靠颜色。依据：[Confirmations › Icons](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm#guidelines)；[Color in Windows › Usability](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)。
- **DLG-08 · MUST NOT** 用对话框报告与某个字段相关的验证错误（改为内联，见 ERR-03）。理由：上下文错误应在原位显示。依据：[Dialog controls › General guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines)。
- **DLG-09 · SHOULD** 确认只用于不可撤销或后果重大的操作（保存项目、断开共享会话、工作区覆盖项目、覆盖导入、关闭审批）；可撤销的操作不确认。理由：不必要的确认会被习惯性点掉。依据：[Commanding basics › When to confirm or undo actions](https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics#when-to-confirm-or-undo-actions)；[Confirmations › Design concepts](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)。
- **DLG-10 · MUST** 带遮罩的抽屉（设置、审批、调用详情）与选项侧栏按模态窗格处理：打开时焦点移到标题或第一个控件，Tab 在内循环，Esc 关闭，关闭后焦点回到触发按钮；没有遮罩的侧栏才允许背景继续交互。理由：WCAG 2.4.3、2.4.11；键盘用户不能“在遮罩后面”操作看不见的控件。依据：[Keyboard interactions › Esc key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#esc-key)；[WCAG 2.2 › 2.4.11 Focus Not Obscured (Minimum)](https://www.w3.org/TR/WCAG22/#focus-not-obscured-minimum)。

## 11 审批与 AI 控制红线（项目规则）

本节规则来源于 `docs/development/design/workbench-control-and-shared-session.md`，优先级高于本文其他章节。其中“依据”标注的 Microsoft / W3C 条目只用于说明实现方式。

### 11.1 审批

- **APR-01 · MUST** 每个写请求单独批准；没有“全部批准”或批量批准。理由：每次写入都要人看过目标与操作。依据：项目规则（`Shell.NoPendingDescription` 文案；phase8 §5.3、维护者决定 3）。
- **APR-02 · MUST** 面板（人）发起的写请求自动打开对应审批卡，但仍须人单独点击“批准执行”；自动打开时焦点放在卡片标题，**MUST NOT** 放在“批准执行”或“拒绝”上。理由：同一条批准路径，没有第二条；避免回车即批。依据：项目规则（维护者决定 2026-10-08）；[Keyboard interactions › Initial focus](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#initial-focus)。
- **APR-03 · MUST** 审批卡显示：客户端名（标注“未验证”）、传输方式、宿主 / 版本、目标项目、PLC、操作列表（动作 / 对象 / 工具）、风险标签（文字）、人 / AI 来源、剩余时间（文字 + 进度条），并可展开脱敏参数或生成计划。密码、密钥、凭据和令牌按调用日志的共用规则替换为固定占位符后，才计算参数和长文本摘要；卡片不携带这些原值或其无盐哈希。仅更换敏感值不改变审批绑定；实际操作仍使用原值。理由：人需要足够的信息作决定。依据：项目规则（phase8 §1.1、§5.3）；[Confirmations › Provide all the information](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)。
- **APR-04 · MUST** “批准执行”与“拒绝”使用相同的默认（secondary）样式，不用主要或危险样式；纯文本标签，不识别访问键；都不是默认或取消按钮（`IsDefault=False`、`IsCancel=False`）。两者并列放在等宽单元格，长标签换行，行高由较高者决定、两者一起增高；都受控制通道输入守卫保护（AI-02）。理由：两个选择视觉平等，避免误批。依据：[ADR 0021](../decisions/0021-workbench-eplanmcp-font-and-style.md)；EplanMCP ADR-0017 第 5 条与 APR-02。本文 APR-02 的自动打开与焦点规则照旧。
- **APR-05 · MUST** 关闭审批：入口只在设置抽屉（状态栏的副本入口若保留，必须走同一确认）；确认对话框符合 DLG-02/03/04（标题“关闭写操作审批？”，提交按钮“关闭审批”，取消为默认并响应 Esc）；审批关闭期间状态栏持续显示带图标与文字的“审批已关”，变化时以 live region 播报。理由：关闭审批会让 AI 写入直接执行。依据：项目规则（phase8 §1.1 “关闭需确认覆盖层”、§4）；[Confirmations › Default values](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm#guidelines)。
- **APR-06 · MUST** 审批超时自动拒绝：剩余时间以文字显示，只在固定阈值（例如 30 秒、10 秒）播报，不逐秒播报；超时可在设置中调整（现为 60/120/300 秒）。超时属于安全设计中的时间限制，按 WCAG 2.2.1 的“必要”例外处理（见偏差清单）。理由：AI 客户端的调用本身有时限，延长会使请求失效。依据：项目规则；[WCAG 2.2 › 2.2.1 Timing Adjustable](https://www.w3.org/TR/WCAG22/#timing-adjustable)。
- **APR-07 · MUST** 新的待批请求：窗口最小化时发系统通知；窗口可见时显示应用内提示并以 live region 播报；点击或按 Enter 打开审批抽屉；通知文字本地化。理由：请求会超时，人必须及时知道。依据：项目规则（phase8 §1.2）；[WCAG 2.2 › 4.1.3 Status Messages](https://www.w3.org/TR/WCAG22/#status-messages)。
- **APR-08 · MUST** AI 不能把人引向审批：`display.call` 拒绝待批请求；AI 页面白名单不含审批抽屉（NAV-08）。理由：审批必须由人主动发起查看。依据：项目规则（phase8 §4）。
- **APR-09 · MUST** 断开共享会话：确认对话框写出 AI 客户端名与后果（AI 需新建会话重连），提供“仅分离面板”作为替代选项，默认按钮为取消。理由：断开会结束另一方的会话。依据：项目规则（phase8 §5.2 第 2 点、维护者决定）；[Confirmations › Provide all the information](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)。

### 11.2 AI 控制界面

- **AI-01 · MUST** 以下情况拒绝 AI 的显示与预填（读取不受影响）：“允许 AI 控制界面”关闭；有模态对话框、确认层、设置或审批抽屉打开；最近 1.5 秒内有人的键鼠输入；目标页正在执行命令。理由：人优先。依据：项目规则（phase8 §2.7）。
- **AI-02 · MUST** 每次由 AI 引起的切页或定位之后 500 毫秒内，忽略对执行类与审批类按钮的鼠标点击、Enter / Space，以及将来引入的访问键（KEY-06）中会触发执行或审批的部分。理由：防止人原本要点 A，却落在 AI 刚切出来的 B 上。依据：项目规则（phase8 §2.7），访问键部分为本规范补充。
- **AI-03 · MUST** AI 操作界面时状态栏显示文字提示“AI 操作了界面 · <工具> · <客户端名> · n 秒前”（不只靠颜色），以 polite live region 播报；调用详情抽屉显示对应的控制记录。理由：人要知道界面为何变化。依据：项目规则（phase8 §2.7、§2.8）；[WCAG 2.2 › 4.1.3 Status Messages](https://www.w3.org/TR/WCAG22/#status-messages)。
- **AI-04 · MUST** 每个被预填的字段旁显示可见标记“AI 预填，待确认”与可用键盘操作的“清除预填”；字段的 HelpText（或 ItemStatus）同时暴露该状态；状态栏保留汇总提示及“确认预填 / 清除预填”。只用 ToolTip 不满足本条。理由：预填值在人确认前不可被当作人的输入。依据：项目规则（phase8 §2.7）；[Expose basic accessibility information › Accessible description](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-description-optional)。
- **AI-05 · MUST** 定位只做滚动与高亮（选中）；不改变复选框（复选框即导出选择），不执行任何命令。理由：控制通道不得产生副作用。依据：项目规则（phase8 §2.7、§3.4）。
- **AI-06 · MUST** “允许 AI 控制工作台界面”只能由人在设置抽屉中修改，默认开启，保存在 UI 设置文件；控件旁说明受保护的行为（人正在操作时不抢焦点、切页后短时间忽略执行与审批按钮）；任何工具只能回显其值。理由：开关只归人所有。依据：项目规则（phase8 §2.7、§4、维护者决定）；CMD-08。
- **AI-07 · MUST** AI 引起的导航不得把键盘焦点移到执行或审批按钮上，按 NAV-07 处理焦点。理由：避免一次回车触发 AI 选好的动作。依据：项目规则；[Keyboard interactions › Initial focus](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#initial-focus)。
- **AI-08 · MUST** 控制通道不可用时，状态栏以文字显示“工作台控制通道不可用”，不影响审批。理由：AI 侧得到类型化拒绝，人这边也要知道原因。依据：项目规则（phase8 §2.1）。

## 12 状态、进度与通知

- **STS-01 · MUST** 状态栏每一项有文字（不只是圆点）和 UIA 名称；可交互的项是可聚焦的按钮，不用鼠标事件挂在面板上。理由：WCAG 2.1.1、4.1.2。依据：[Keyboard accessibility › Keyboard alternatives to pointer actions](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-alternatives-to-pointer-actions-and-events-for-specific-control-elements)。
- **STS-02 · MUST** 重要的状态变化用 live region 播报：MCP 服务状态、会话连接 / 断开、AI 操作界面、预填待确认、长操作完成或失败用 Polite；新的待批请求与阻断性错误可用 Assertive。WPF：`AutomationProperties.LiveSetting` + `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)`。理由：WCAG 4.1.3。依据：[What's new in accessibility in .NET Framework › 4.7.1 WPF › UIAutomation LiveRegion support](https://learn.microsoft.com/en-us/dotnet/framework/whats-new/whats-new-in-accessibility)；[InfoBar › Updating the InfoBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#updating-the-infobar)。
- **STS-03 · MUST** 长操作显示进度：已知总量时用确定进度，否则用不确定进度（`IsIndeterminate`），旁边有说明当前操作的文字；进度放在操作发起处附近或全局忙碌区域。理由：用户要知道程序还在工作。依据：[Guidelines for progress controls › Types of progress / Is this the right control?](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls#types-of-progress)。
- **STS-04 · SHOULD** 后端支持取消的长操作（MCP 面板请求、图集生成、诊断包）提供“取消”。理由：用户能中止误发的长操作。依据：项目规则；[Commanding basics › Provide command feedback](https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics#provide-command-feedback)。
- **STS-05 · MUST** 被阻止的操作要说明原因与办法；例如忙碌时关闭窗口，要提示“正在执行 <操作>，完成或取消后再关闭”，不能静默无效。理由：静默失败让用户以为程序卡死。依据：[Commanding basics › Provide command feedback](https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics#provide-command-feedback)。
- **STS-06 · MUST** 持续的全局状态（审批已关、控制通道不可用、离线演示、环境问题）用 Banner（InfoBar 模式）或状态栏标记显示，带严重级别图标与文字，不用会自动消失的 toast。理由：持续状态需要持续可见。依据：[InfoBar › Is this the right control? / Severity](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#is-this-the-right-control)。
- **STS-07 · SHOULD** 应用内 toast 按队列逐条显示，不遮挡审批操作与焦点所在元素；可用键盘关闭与打开；与待批相关的 toast 在请求处理前不自动消失。理由：WCAG 2.4.11；审批提示不能被下一条反馈覆盖。依据：[WCAG 2.2 › 2.4.11](https://www.w3.org/TR/WCAG22/#focus-not-obscured-minimum)；[InfoBar › Enter and Exit Usability](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#recommendations)。
- **STS-08 · MUST** Banner、提示与徽章不得快速反复出现 / 消失（闪烁）；状态在短时间内多次变化时合并显示。理由：闪烁影响光敏用户，也让人无法阅读。依据：[InfoBar › Flashing content](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#recommendations)；[WCAG 2.2 › 2.3.1](https://www.w3.org/TR/WCAG22/#three-flashes-or-below-threshold)。
- **STS-09 · SHOULD** 系统通知使用 Windows App SDK 应用通知（未打包应用可用）或保留 `NotifyIcon`；文字本地化；激活时还原窗口并打开对应抽屉。理由：最小化时仍要提醒待批请求。依据：项目规则（phase8 §1.2）；[Windows app title bar](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design)（窗口激活状态）。
- **STS-10 · MUST** 计数徽章的 UIA 名称包含完整含义（“2 项待批”），不只是数字。理由：读屏单独读“2”没有意义。依据：[Expose basic accessibility information › Accessible name](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-name)。

## 13 错误与空状态

- **ERR-01 · MUST** 错误文字说明发生了什么、已知的原因、用户可以怎么做；不责备用户；写出具体对象（路径、工具、版本）；“未预期的错误”只用于真正的崩溃。理由：错误信息要帮人回到工作。依据：[Writing style › Error messages](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#error-messages)；[Error Messages（Win32 UX Guide）› Text](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-error#text)。
- **ERR-02 · MUST** 原始异常文字与堆栈不作为主信息；主信息为本地化文字，可展开“详细信息”（V4 `error.code`、请求 ID、诊断 ID，以 Code 层级显示）；V4 错误码经 `EngineResultText` 本地化。理由：技术细节对支持有用，但不应挡在用户前面。依据：[Error Messages › Guidelines（Error codes / Progressive disclosure）](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-error#guidelines)。
- **ERR-03 · MUST** 字段验证错误内联显示：字段边框变为错误色并在下方显示文字（不只靠颜色），文字作为字段的 HelpText 暴露并播报；焦点移到第一个出错字段。理由：WCAG 3.3.1、3.3.3。依据：[Dialog controls › General guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines)；[WCAG 2.2 › 3.3.1](https://www.w3.org/TR/WCAG22/#error-identification)。
- **ERR-04 · MUST** 页面或全局的失败（HTTP 监听失败、环境检查失败、控制通道不可用）用 Banner 显示，并给出可执行的操作（“网络权限”“重新检查”）。理由：失败要伴随出路。依据：[InfoBar › Add an action button](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar)。
- **ERR-05 · MUST** 空状态使用统一组件：标题（缺了什么）+ 一句话（为什么 / 下一步）+ 可选的一个操作，在主体区域居中。理由：空白区域要告诉人接下来做什么。依据：项目规则；[Writing style › Lead with what's important](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style)。
- **ERR-06 · SHOULD** 区分加载、空、错误三种状态：加载时在列表顶部显示进度；空时显示空状态；错误时显示 Banner。理由：避免把“还没加载”误显示成“没有数据”。依据：[Guidelines for progress controls › ProgressBar - Indeterminate](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls#progress-controls-best-practices)。
- **ERR-07 · MUST** 错误严重程度用图标与“错误”等字样表达，不只用红色文字。理由：WCAG 1.4.1。依据：[Color in Windows › Usability](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)。

## 14 键盘与焦点

- **KEY-01 · MUST** 所有功能都能用键盘完成；不在非控件元素上挂 `MouseLeftButtonUp/Down` 作为唯一触发方式；指针操作在抬起时触发，可通过移开取消。理由：WCAG 2.1.1、2.5.2。依据：[Keyboard accessibility › Keyboard alternatives to pointer actions](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-alternatives-to-pointer-actions-and-events-for-specific-control-elements)；[WCAG 2.2 › 2.5.2 Pointer Cancellation](https://www.w3.org/TR/WCAG22/#pointer-cancellation)。
- **KEY-02 · MUST** Tab 顺序与视觉顺序一致；非交互元素不是 Tab 停靠点；装饰性标签不可聚焦。理由：WCAG 2.4.3。依据：[Keyboard interactions › Tab stops](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#tab-stops)。
- **KEY-03 · MUST** 每个可聚焦元素都有可见的焦点指示：全局 `FocusVisualStyle`，外圈 2 px（Focus.Outer）、内圈 1 px（Focus.Inner）、距控件边界 1 px，在所有主题中对相邻颜色 ≥ 3:1；自定义模板可以增加焦点效果，不能去掉它。理由：WCAG 2.4.7、1.4.11；默认的 1 px 黑色点线在深色面上几乎不可见。依据：[Visual feedback › High Visibility Focus Visuals / Border Properties](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback#high-visibility-focus-visuals)；[Styling for Focus in Controls, and FocusVisualStyle](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle#when-to-use-focus-visual-styles)。
- **KEY-04 · MUST** Esc 关闭临时界面（对话框 = 取消、抽屉、选项侧栏、弹出框、toast），从不触发破坏性操作。理由：Esc 只取消临时界面。依据：[Keyboard interactions › Esc key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#esc-key)。
- **KEY-05 · MUST** Enter 激活聚焦的按钮；对话框中 Enter 触发默认按钮，风险确认中默认按钮为安全选项（DLG-04）。理由：可预期的提交行为。依据：[Keyboard interactions › Enter key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#enter-key)；[Dialog controls › DefaultButton](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#defaultbutton)。
- **KEY-06 · MUST** 对话框按钮与主要表单字段有访问键：按钮模板 `RecognizesAccessKey="True"`，文案中英文用行内下划线（`_Save`），中文用后缀（`保存(_S)`），按 Alt 显示下划线；访问键随语言本地化并在同一作用域内不重复。理由：访问键主要服务于运动障碍用户。依据：[Keyboard interactions › Access keys](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#access-keys)；[Keyboard accessibility › Keyboard shortcuts](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-shortcuts)。
- **KEY-07 · MUST** 快捷键表（代码、ToolTip、文档三处一致）：

  | 快捷键 | 动作 | 级别 |
  |---|---|---|
  | Ctrl+1 … Ctrl+8 | 依次切到 MCP 与客户端、工程操作、程序块、版本控制、AI 调用、审计日志、环境体检、日志（现状顺序） | MUST |
  | Ctrl+Tab / Ctrl+Shift+Tab | 下一个 / 上一个一级页 | SHOULD |
  | Ctrl+O | 打开项目选项侧栏 | MUST |
  | Ctrl+S | 保存项目（照常确认与审批） | MUST |
  | Ctrl+B | 编译 PLC 软件 | MUST |
  | Ctrl+F | 聚焦当前页的搜索或筛选框 | SHOULD |
  | F5 | 刷新当前页数据（只读操作） | MAY |
  | F6 / Shift+F6 | 在主要区域间循环（NAV-09） | SHOULD |
  | Esc | 关闭临时界面（KEY-04） | MUST |
  | Shift+F10 / 菜单键 | 打开上下文菜单（CMD-10） | MUST |

  理由：快捷键要一致、可记忆，并与 Windows 常用约定一致。依据：[Keyboard interactions › Common keyboard shortcuts](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#common-keyboard-shortcuts)。
- **KEY-08 · MUST** 浮层关闭后、或焦点元素因切页被折叠后，焦点回到合理位置（触发元素或当前页签）。理由：WCAG 2.4.3；丢失焦点会让键盘用户回到窗口开头。依据：[Keyboard interactions › Initial focus](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#initial-focus)。
- **KEY-09 · MUST** 除模态对话框（Tab 在内循环）外不得出现键盘陷阱。理由：WCAG 2.1.2。依据：[Keyboard interactions › Popup UI（cycling 只用于弹出界面）](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#popup-ui)。
- **KEY-10 · SHOULD** 一组相关选项（分段单选、页签）作为一个 Tab 停靠点，组内用方向键移动，Home / End 跳到首尾。理由：减少停靠点数量。依据：[Keyboard interactions › Control group](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#control-group)。
- **KEY-11 · MUST** 鼠标位置不干扰键盘操作；控制通道的人活动检测（光标位置采样）不得改变键盘焦点。理由：键盘用户的操作不应被指针状态改变。依据：[.NET Accessibility Best Practices › Do Not Let Mouse Location Interfere with Keyboard Navigation](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices#navigation)。

## 15 无障碍：可测要求与 WCAG 2.2 AA 映射

### 15.1 可测要求

每条要求都有自动或手工的验证方式。“UIA 树测试”指在 `TiaOpenness.Gui.Tests` 中，用 `UIElementAutomationPeer.CreatePeerForElement` 从各页面、抽屉、对话框（离线夹具状态）递归遍历 AutomationPeer 并断言属性；“AI4W”指 Accessibility Insights for Windows 的 FastPass。

- **A11Y-01 · MUST** 每个可聚焦或可操作的元素 UIA 名称非空、来自 `Strings`、以可见标签开头，且不等于任何 CLR 类型全名。验证：UIA 树测试（中英文各一遍）+ AI4W。WCAG：1.1.1、2.5.3、4.1.2。
- **A11Y-02 · MUST** 每个输入控件的 `LabeledBy` 指向可见标签。验证：UIA 树测试。WCAG：1.3.1、3.3.2。
- **A11Y-03 · MUST** 控件类型与行为一致（页签、开关、复选框、按钮），状态（选中、按下、展开）可读。验证：Inspect / AI4W 人工核对。WCAG：4.1.2。
- **A11Y-04 · MUST** 只用键盘完成以下任务：连接 / 打开项目、选择软件、编译、导出、导入、检查、打开调用详情、批准与拒绝、关闭与重新开启审批、确认与清除预填、切换语言与主题、生成诊断包。验证：键盘脚本（人工，每次发布前）+ 自动化测试用 `KeyboardDevice` 事件驱动关键路径。WCAG：2.1.1、2.1.2、2.4.3。
- **A11Y-05 · MUST** 焦点指示在浅色、深色、高对比主题下都可见，对相邻色 ≥ 3:1，且不被 toast、忙碌区或抽屉完全遮住。验证：渲染测试对焦点元素截图取像素计算对比；人工检查遮挡。WCAG：2.4.7、2.4.11、1.4.11。
- **A11Y-06 · MUST** 实际使用的每组文字 / 背景 token 对比 ≥ 4.5:1，非文字指示 ≥ 3:1。验证：扩展 `ThemeSurfaceTests`，阈值改为 4.5 / 3.0，并覆盖次要文字、徽章、按钮文字、选中态、边框。WCAG：1.4.3、1.4.11。
- **A11Y-07 · MUST** 四个内置对比主题（Aquatic、Desert、Dusk、Night sky）下无写死的颜色、文字可读、选中与焦点可辨。验证：渲染测试注入高对比调色板；每次发布前在真实对比主题下人工检查。WCAG：1.4.3、1.4.11（用户配色下）；Microsoft 要求。
- **A11Y-08 · MUST** 读屏能听到：新的待批请求、AI 操作界面、预填待确认、长操作完成 / 失败、错误、审批开关变化。验证：单元测试断言 `LiveSetting` 已设置且 `LiveRegionChanged` 已触发；Narrator 脚本人工验证。WCAG：4.1.3。
- **A11Y-09 · MUST** 显示缩放 100–200% 与混合 DPI 下无模糊、无截断；窗口在 1366 × 768（100%）和 1920 × 1080（150%）完整显示；Windows“文本大小”150% 时关键文字不被截断（全面支持见偏差清单）。验证：断言窗口 `MinWidth/MinHeight`；人工在两种工作区下检查。WCAG：1.4.4、1.4.10。
- **A11Y-10 · MUST** 页面标题标为 `HeadingLevel` Level1，卡片与分节标题为 Level2。验证：UIA 树测试。WCAG：1.3.1、2.4.6。
- **A11Y-11 · MUST** 对话框与带遮罩的抽屉：`IsDialog`、初始焦点、Tab 循环、Esc、焦点归还。验证：自动化测试打开 / 关闭并检查 `Keyboard.FocusedElement`。WCAG：2.4.3、2.4.11。
- **A11Y-12 · MUST** 不只靠颜色传达信息（状态、日志级别、筛选与选中状态、差异）。验证：评审清单 + 灰度截图人工检查。WCAG：1.4.1。
- **A11Y-13 · MUST** 指针目标 ≥ 24 × 24 epx 或满足间距例外。验证：遍历可视树，断言 `ButtonBase` 等可点击元素的 `ActualWidth/ActualHeight`（例外须列在测试白名单中）。WCAG：2.5.8。
- **A11Y-14 · MUST** 以上自动化检查纳入 `tests/test-suites.json` 中工作台的现有套件，在 CI 上阻断回归。验证：CI。依据：[Accessibility checklist › 6](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)。
- **A11Y-15 · SHOULD** 每个页面在离线演示（`--mock`）与夹具状态下 AI4W FastPass 零失败。验证：发布前人工运行。依据：[Accessibility checklist › 5](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)。

### 15.2 WCAG 2.2 A / AA 成功准则在工作台中的应用

适用性按 [WCAG2ICT](https://www.w3.org/TR/wcag2ict-22/) 对非 Web 软件的解释（多数准则“如原文适用”或“作词替换后适用”：网页 → 软件、页面 → 软件中的一组界面）。“现状”列对应偏差清单的条目；“通过”为按代码阅读的初判，仍以 A11Y 验证为准。

| 准则 | 级别 | 对 WPF 桌面的适用性 | 工作台中的解释 / 要求 | 验证方式 | 对应 Microsoft 指南 | 现状 |
|---|---|---|---|---|---|---|
| [1.1.1 Non-text Content](https://www.w3.org/TR/WCAG22/#non-text-content) | A | 适用 | 图标按钮、状态圆点、字形有文字替代；装饰元素不进入 UIA 控件视图 | UIA 树测试、AI4W | [Accessible name](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-name) | UI-02 |
| 1.2.1–1.2.5（时基媒体） | A/AA | 不适用（无音视频） | 不适用 | 不适用 | 不适用 | 不适用 |
| [1.3.1 Info and Relationships](https://www.w3.org/TR/WCAG22/#info-and-relationships) | A | 适用 | 标签关联输入框；标题有 HeadingLevel；单选组有组名；表格列头可被程序读出 | UIA 树测试 | [Labels and LabeledBy](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#labels-and-labeledby)；[Landmarks and headings](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings#usage) | UI-03、UI-04、UI-16 |
| [1.3.2 Meaningful Sequence](https://www.w3.org/TR/WCAG22/#meaningful-sequence) | A | 适用 | UIA 树顺序与阅读顺序一致（浮层在树中位于内容之后可接受，但须获得焦点） | Inspect | [Keyboard interactions › Tab stops](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#tab-stops) | 通过（待核实） |
| [1.3.3 Sensory Characteristics](https://www.w3.org/TR/WCAG22/#sensory-characteristics) | A | 适用 | 说明文字不只用位置或颜色指代（“见右侧”须同时写出区域名） | 文案评审 | [Writing style](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style) | UI-48（轻微） |
| [1.3.4 Orientation](https://www.w3.org/TR/WCAG22/#orientation) | AA | 适用 | 不锁定方向（桌面窗口） | 人工 | 无 | 通过 |
| [1.3.5 Identify Input Purpose](https://www.w3.org/TR/WCAG22/#identify-input-purpose) | AA | 适用，但工作台没有收集个人信息的字段 | 不适用 | 不适用 | 不适用 | 不适用 |
| [1.4.1 Use of Color](https://www.w3.org/TR/WCAG22/#use-of-color) | A | 适用 | COL-06 | 灰度截图、评审 | [Color › Usability](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability) | UI-09、UI-10 |
| 1.4.2 Audio Control | A | 不适用（无音频） | 不适用 | 不适用 | 不适用 | 不适用 |
| [1.4.3 Contrast (Minimum)](https://www.w3.org/TR/WCAG22/#contrast-minimum) | AA | 适用 | COL-04：文字 ≥ 4.5:1（大字 ≥ 3:1，但工作台没有 ≥ 18.66 px 粗体的正文，按 4.5:1 执行） | token 对比测试 | [Accessible text requirements › Contrast ratios](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements#contrast-ratios) | UI-08 |
| [1.4.4 Resize Text](https://www.w3.org/TR/WCAG22/#resize-text) | AA | 作词替换后适用；可通过平台文字缩放或显示缩放满足 | 在 200% 显示缩放或“文本大小”放大下内容与功能不丢失 | A11Y-09 | [Text font size and scale](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements#text-font-size-and-scale) | UI-13、UI-20 |
| [1.4.5 Images of Text](https://www.w3.org/TR/WCAG22/#images-of-text) | AA | 适用 | 不用文字图片 | 评审 | [Text in graphics](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements) | 通过 |
| [1.4.10 Reflow](https://www.w3.org/TR/WCAG22/#reflow) | AA | 带注释适用（以平台等价的窄宽度为基准，允许二维内容例外） | LAY-01/03：窄窗口下非表格内容单列重排，表格可横向滚动 | 窗口尺寸断言、人工 | [Screen sizes and breakpoints](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#breakpoints) | UI-13 |
| [1.4.11 Non-text Contrast](https://www.w3.org/TR/WCAG22/#non-text-contrast) | AA | 适用 | COL-05：焦点、状态指示、控件边界 ≥ 3:1 | token 对比测试、像素测试 | [Color › Usability](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color#usability)；[Visual feedback › High Visibility Focus Visuals](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback#high-visibility-focus-visuals) | UI-07、UI-09、UI-12 |
| [1.4.12 Text Spacing](https://www.w3.org/TR/WCAG22/#text-spacing) | AA | 通常不适用：Windows / WPF 不提供用户覆盖行距、字距的机制 | 仍按 LAY-06 不用固定高度 | 不适用 | [Globalization › text lengths](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization) | 不适用 |
| [1.4.13 Content on Hover or Focus](https://www.w3.org/TR/WCAG22/#content-on-hover-or-focus) | AA | 适用 | ToolTip 可关闭（Esc / Ctrl）、可悬停、持续显示（WPF 默认行为）；不在 ToolTip 中放唯一信息（TYP-06） | 人工 | [What's new in accessibility › Accessible Tooltip handling](https://learn.microsoft.com/en-us/dotnet/framework/whats-new/whats-new-in-accessibility) | 通过；信息唯一性见 UI-20 |
| [2.1.1 Keyboard](https://www.w3.org/TR/WCAG22/#keyboard) | A | 适用 | KEY-01 | A11Y-04 | [Keyboard accessibility](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-alternatives-to-pointer-actions-and-events-for-specific-control-elements) | UI-05、UI-06 |
| [2.1.2 No Keyboard Trap](https://www.w3.org/TR/WCAG22/#no-keyboard-trap) | A | 适用 | KEY-09 | A11Y-04 | [Keyboard interactions › Popup UI](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#popup-ui) | 通过 |
| [2.1.4 Character Key Shortcuts](https://www.w3.org/TR/WCAG22/#character-key-shortcuts) | A | 适用 | 不定义单字符快捷键（访问键需 Alt） | 评审 | [Keyboard interactions › Accelerators](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#accelerators) | 通过 |
| [2.2.1 Timing Adjustable](https://www.w3.org/TR/WCAG22/#timing-adjustable) | A | 适用 | 审批超时可调（60/120/300 秒），并按“必要”例外处理（APR-06） | 评审 | 无 | UI-24（有意偏差） |
| [2.2.2 Pause, Stop, Hide](https://www.w3.org/TR/WCAG22/#pause-stop-hide) | A | 适用 | 自动更新的调用列表与日志尾部可暂停跟随（“跟随最新”开关） | 人工 | 无 | 通过（日志尾部待核实） |
| [2.3.1 Three Flashes or Below Threshold](https://www.w3.org/TR/WCAG22/#three-flashes-or-below-threshold) | A | 适用 | STS-08 | 人工 | [InfoBar › Flashing content](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#recommendations) | 通过 |
| [2.4.1 Bypass Blocks](https://www.w3.org/TR/WCAG22/#bypass-blocks) | A | EN 301 549 与 Section 508 认为对软件不适用 | 以 NAV-09（F6 区域）作为 SHOULD 级替代 | 人工 | [F6](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-navigation-between-application-panes-with-f6) | UI-16（F6 部分） |
| [2.4.2 Page Titled](https://www.w3.org/TR/WCAG22/#page-titled) | A | WCAG2ICT 解释为窗口标题；EN 301 549 认为对软件不适用 | NAV-03 作为 SHOULD 级补充 | 人工 | [Title bar](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design#standard-design) | UI-46 |
| [2.4.3 Focus Order](https://www.w3.org/TR/WCAG22/#focus-order) | A | 适用 | KEY-02、DLG-02、DLG-10、KEY-08 | A11Y-04、A11Y-11 | [Tab stops](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#tab-stops) | UI-05 |
| [2.4.4 Link Purpose (In Context)](https://www.w3.org/TR/WCAG22/#link-purpose-in-context) | A | 适用 | “打开”“选项…”等按钮结合所在行或卡片可判断目的 | 评审 | [Button text](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#button-text) | 通过 |
| [2.4.5 Multiple Ways](https://www.w3.org/TR/WCAG22/#multiple-ways) | AA | EN 301 549 与 Section 508 认为对软件不适用 | 不适用 | 不适用 | 不适用 | 不适用 |
| [2.4.6 Headings and Labels](https://www.w3.org/TR/WCAG22/#headings-and-labels) | AA | 适用 | 标题与标签描述内容和用途 | 文案评审 | [Writing style](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style) | 通过 |
| [2.4.7 Focus Visible](https://www.w3.org/TR/WCAG22/#focus-visible) | AA | 适用 | KEY-03 | A11Y-05 | [Focus visuals](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#focus-visuals) | UI-07 |
| [2.4.11 Focus Not Obscured (Minimum)](https://www.w3.org/TR/WCAG22/#focus-not-obscured-minimum) | AA | 如原文适用 | 焦点元素不被 toast、忙碌区、抽屉遮罩完全遮住 | A11Y-05、A11Y-11 | [Keyboard interactions › Esc key](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#esc-key) | UI-05 |
| [2.5.1 Pointer Gestures](https://www.w3.org/TR/WCAG22/#pointer-gestures) | A | 适用 | 无多点或路径手势 | 评审 | 无 | 通过 |
| [2.5.2 Pointer Cancellation](https://www.w3.org/TR/WCAG22/#pointer-cancellation) | A | 适用 | KEY-01：在抬起时触发；按下即触发的处理器只允许用于拦截（输入守卫）和拖动 | 代码扫描 `MouseLeftButtonDown` | [Keyboard accessibility](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility#keyboard-alternatives-to-pointer-actions-and-events-for-specific-control-elements) | UI-06 |
| [2.5.3 Label in Name](https://www.w3.org/TR/WCAG22/#label-in-name) | A | 适用 | 显式设置的名称以可见文字开头（A11Y-01） | UIA 树测试 | [Name from inner text](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#name-from-inner-text) | 通过（新增名称时须遵守） |
| 2.5.4 Motion Actuation | A | 不适用 | 不适用 | 不适用 | 不适用 | 不适用 |
| [2.5.7 Dragging Movements](https://www.w3.org/TR/WCAG22/#dragging-movements) | AA | 适用 | 无仅能拖动完成的功能（移动窗口有系统菜单替代） | 评审 | 无 | 通过 |
| [2.5.8 Target Size (Minimum)](https://www.w3.org/TR/WCAG22/#target-size-minimum) | AA | 作词替换后适用 | CTL-10 | A11Y-13 | [Buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons#button-text) | UI-14 |
| [3.1.1 Language of Page](https://www.w3.org/TR/WCAG22/#language-of-page) | A | 作词替换后适用（软件的默认语言可被程序确定） | TYP-09：根元素 `Language` 随界面语言 | UIA / 代码断言 | [Globalization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization) | UI-15 |
| [3.1.2 Language of Parts](https://www.w3.org/TR/WCAG22/#language-of-parts) | AA | 适用 | 中文界面中的普通英文词须翻译；产品名与技术标识例外 | 文案评审 | [Writing style › Abbreviations](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#style-conventions) | UI-47（轻微） |
| [3.2.1 On Focus](https://www.w3.org/TR/WCAG22/#on-focus) | A | 适用 | 获得焦点不切换上下文 | 人工 | 无 | 通过 |
| [3.2.2 On Input](https://www.w3.org/TR/WCAG22/#on-input) | A | 适用 | 选择会话不自动附着；选“关闭”审批时弹确认属于预期行为 | 人工 | 无 | 通过 |
| [3.2.3 Consistent Navigation](https://www.w3.org/TR/WCAG22/#consistent-navigation) | AA | EN 301 549 与 Section 508 认为对软件不适用 | 以 NAV-06 作软件内一致性要求 | 不适用 | [Navigation basics › Consistency](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics#principles-of-good-navigation) | 通过 |
| [3.2.4 Consistent Identification](https://www.w3.org/TR/WCAG22/#consistent-identification) | AA | 同上 | 以 CMD-09 作软件内一致性要求 | 文案评审 | 同上 | UI-48（轻微） |
| [3.2.6 Consistent Help](https://www.w3.org/TR/WCAG22/#consistent-help) | A | 作词替换后适用（“一组网页” → “一组软件界面”） | 帮助入口（设置抽屉的“关于 / 项目主页 / Releases”、环境页的首次连接说明）位置固定；新增“使用说明”类入口须放在标题栏同一位置 | 评审 | 无 | 通过 |
| [3.3.1 Error Identification](https://www.w3.org/TR/WCAG22/#error-identification) | A | 适用 | 出错项以文字指出（ERR-03） | 人工 | [Dialog controls › General guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs#general-guidelines) | 基本满足；改进见 UI-44 |
| [3.3.2 Labels or Instructions](https://www.w3.org/TR/WCAG22/#labels-or-instructions) | A | 适用 | CTL-02：有可见标签，不只靠占位符 | UIA 树测试 | [Labels and LabeledBy](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#labels-and-labeledby) | UI-03 |
| [3.3.3 Error Suggestion](https://www.w3.org/TR/WCAG22/#error-suggestion) | AA | 适用 | 错误信息给出修正建议（ERR-01） | 文案评审 | [Writing style › Error messages](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#error-messages) | 基本满足 |
| [3.3.4 Error Prevention (Legal, Financial, Data)](https://www.w3.org/TR/WCAG22/#error-prevention-legal-financial-data) | AA | 适用于修改用户数据（TIA 项目） | 写项目经确认或审批（DLG-04、APR-*） | 评审 | [When to confirm or undo actions](https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics#when-to-confirm-or-undo-actions) | 通过；默认按钮见 UI-28 |
| [3.3.7 Redundant Entry](https://www.w3.org/TR/WCAG22/#redundant-entry) | A | 如原文适用 | 已填写的路径、地址等在同一流程中自动带入（UI 设置已保存） | 人工 | 无 | 通过 |
| [3.3.8 Accessible Authentication (Minimum)](https://www.w3.org/TR/WCAG22/#accessible-authentication-minimum) | AA | 适用 | 共享密钥可生成、可粘贴、可显示，不需要记忆或认知测试 | 人工 | 无 | 通过 |
| [4.1.2 Name, Role, Value](https://www.w3.org/TR/WCAG22/#name-role-value) | A | 适用 | A11Y-01/03；CTL-04/06 | UIA 树测试、AI4W | [Expose basic accessibility information](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information) | UI-02、UI-03、UI-04 |
| [4.1.3 Status Messages](https://www.w3.org/TR/WCAG22/#status-messages) | AA | 作词替换后适用；在 WPF 中以 UIA live region 实现 | STS-02、AI-03、APR-07 | A11Y-08 | [LiveRegion（.NET 4.7.1）](https://learn.microsoft.com/en-us/dotnet/framework/whats-new/whats-new-in-accessibility) | UI-11 |

（4.1.1 Parsing 已在 WCAG 2.2 中删除。）

## 16 本地化

- **LOC-01 · MUST** 所有可见与可读出的文字（含 `AutomationProperties.Name/HelpText`、ToolTip、`NotifyIcon` 文字、窗口与对话框标题、单位）来自 `Strings` 目录的（键，英文，中文）三元组，并由 `StringsTests` 覆盖。例外：产品名（TIA Portal、MCP、VCI、SimaticML）、用户数据、技术标识（TYP-08）。理由：中英界面必须完整切换。依据：[Globalization and localization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal)；[Accessible names and localization](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-names-and-localization)。
- **LOC-02 · MUST** 句子不用字符串拼接组成；用带占位符的格式串（`Loc.T`），让译文可以调整语序；分隔符“ · ”作为目录项或常量统一管理。理由：拼接会锁死英文语序。依据：[Globalization › Guidelines for globalization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization)。
- **LOC-03 · MUST** 单位本地化（“{0} 秒”/“{0}s”、“{0} 毫秒”/“{0} ms”、百分号）。理由：单位是文字的一部分。依据：同上（Support international units of measurement）。
- **LOC-04 · MUST** 布局容纳语言差异：英文通常比中文宽 1.5–2 倍；文字按钮用 `MinWidth` 而不是固定 `Width`；每个页面在两种语言、最小窗口尺寸下检查。理由：防止英文被截断。依据：[Globalization › Design your UI to accommodate different text lengths](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization)；[Collections and lists › Globalization and localization checklist](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/lists)。
- **LOC-05 · MUST** 数字与日期按用户的区域格式（`CultureInfo.CurrentCulture`）格式化，不按界面语言推断区域；XAML 绑定中的 `StringFormat` 须指定 `ConverterCulture` 或经格式化转换器；日志与审计中的技术时间戳可固定使用 ISO 格式（`yyyy-MM-dd HH:mm:ss zzz`）。理由：语言与区域是两个概念。依据：[Globalization › Don't use language to assume a user's region](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal#guidelines-for-globalization)。
- **LOC-06 · MUST** 中文文案使用全角标点（，。：；（）“”），中文与英文或数字之间按现有目录的习惯加空格（如“{0} 待批”），全目录保持一致。理由：标点混用影响阅读与专业感。依据：项目规则。
- **LOC-07 · MUST** 维护术语表并全目录一致：TIA 项目统一用“项目”（与 TIA Portal 中文界面一致）或“工程”二选一；“TIA Portal”不译为“博途”；审批 / 批准 / 拒绝；会话；工作区；MCP 客户端；人（工作台）/ AI。理由：同一事物多个叫法会让人以为是不同东西。依据：[Writing style › Style conventions](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#style-conventions)；[WCAG 2.2 › 3.2.4](https://www.w3.org/TR/WCAG22/#consistent-identification)。
- **LOC-08 · MUST** 切换语言即时生效（保留 `Loc` 索引器绑定），UIA 名称同样绑定到目录、随语言更新。理由：读屏与界面语言要一致。依据：[Accessible names and localization](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information#accessible-names-and-localization)。
- **LOC-09 · MUST** 中英两种译文含义与操作指引对等，不在某一种语言里多出说明或少掉信息。理由：两种语言的用户应得到同样的帮助。依据：[Globalization and localization › Localization](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal)。

## 17 界面文字

- **TXT-01 · MUST** 简洁、主动语态、先说重点；用户熟悉的词优先。理由：用户扫读，不会逐字读。依据：[Writing style › Voice and tone principles / Lead with what's important / Emphasize action](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#voice-and-tone-principles)。
- **TXT-02 · MUST** 英文用句首大写，不用全大写；中文不用感叹号；少用“请”，只在请用户做麻烦的事时使用。理由：语气平和、不制造紧张。依据：[Writing style › Capitalization](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#style-conventions)；[Error Messages › Text（avoid "please"）](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-error#text)。
- **TXT-03 · MUST** 使用与设备无关的动词：“选择 / 打开”（select / open），不写“点击”（click），除非确实只针对鼠标。理由：键盘与读屏用户也要能照做。依据：[Writing style › Lead with what's important（“Select filters…”）](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style)；[Writing style › Accessibility and localization](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#accessibility-and-localization)。
- **TXT-04 · MUST** 标签、按钮、单选与复选文字不加句号；完整句子（ToolTip、错误、对话框正文）以句号结尾。理由：句号表示完整句子。依据：[Writing style › Periods](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#style-conventions)。
- **TXT-05 · MUST** 对话框标题与按钮“一问一答”：按钮是对标题问题的直接回答。理由：用户读标题就能作答。依据：[Writing style › Dialogs](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#dialogs)。
- **TXT-06 · MUST NOT** 在确认中写“警告 / 注意 / Warning”，用警告图标表达；不用全大写词（OVERWRITES、INTO）强调。理由：图标比字样更快被识别，全大写读起来像喊叫。依据：[Confirmations › Text](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm#text)。
- **TXT-07 · SHOULD** 首次出现的缩写在说明里解释一次（MCP、VCI、stdio、lane）；面向工程师的技术区域可以直接使用通行的技术术语。理由：减少猜测，同时不牺牲专业表达。依据：[Writing style › Abbreviations](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style#style-conventions)。
- **TXT-08 · MUST** 人 / AI 的归属用固定说法：MCP 客户端的操作称“AI”，工作台面板的操作称“人（工作台）”，客户端名附“未验证”。理由：审计与审批中归属必须一眼可辨。依据：项目规则（phase8 §5.3）。

## 18 验收与维护

- 新增或修改界面的 PR 自查：TYP-03（无字面字号）、COL-01（无字面颜色）、CTL-02/04（名称与标签）、KEY-03/04（焦点与 Esc）、LOC-01（无硬编码文字）、AI-02/APR-02（守卫与焦点），并运行 §15.1 的自动化检查。
- 每次发布前（4.0 起）：A11Y-04 键盘脚本、A11Y-07 高对比人工检查、A11Y-08 Narrator 脚本、A11Y-09 两种工作区检查、A11Y-15 AI4W FastPass；结果记入发布证据。
- 本规范的修改与偏差清单的状态（改正、有意偏差、延后）一起维护；有意偏差须写明理由与复审时间。

## 附录 A Token

### A.1 间距与尺寸

| Token | 值（epx） | 用途 |
|---|---|---|
| Space.4 | 4 | 图标与文字、紧凑组内 |
| Space.8 | 8 | 按钮之间、控件与上方标题 |
| Space.12 | 12 | 控件与旁边标签、卡片之间 |
| Space.16 | 16 | 卡片内边距、表面边缘到文字 |
| Space.24 | 24 | 页面左右边距、侧栏与主体之间 |
| Space.32 | 32 | 大分区之间 |
| Control.Height | 32 | 按钮、输入框、下拉框 |
| Control.Height.Compact | 24 | 行内文字按钮、筛选按钮（最小值） |
| List.RowHeight | 36 | 表格与列表行（最小值） |
| SidePane.Width | 280–320 | 右侧栏 |
| Drawer.Width | 400–520 | 抽屉（设置 400、待批 480、调用详情 520） |
| Dialog.Width | 420–548 | 对话框 |

### A.2 圆角

| Token | 值 | 用途 |
|---|---|---|
| Radius.Control | 4 | 按钮、输入框、复选框、进度条、ToolTip |
| Radius.Card | 4 或 8（统一选一个） | 页面卡片 |
| Radius.Overlay | 8 | 抽屉、对话框、toast、弹出框、窗口 |
| Radius.None | 0 | 相接的直边、最大化窗口 |

### A.3 字号

| Token | 字号 / 行高 | 字重 | 字体 |
|---|---|---|---|
| Type.Caption | 12 / 16 | Regular | Ui.Font |
| Type.Body | 14 / 20 | Regular | Ui.Font |
| Type.BodyStrong | 14 / 20 | SemiBold | Ui.Font |
| Type.BodyLarge | 18 / 24 | Regular | Ui.Font |
| Type.Subtitle | 20 / 28 | SemiBold | Ui.Font |
| Type.Title | 28 / 36 | SemiBold | Ui.Font |
| Type.Code | 12 / 16 | Regular | Ui.FontMono |

现有 `Ui.FontSize.*` 与 `Ui.Text.*`（`Themes/Primer.xaml:12-58`）可改名并改值后沿用。

## 附录 B 引用来源（2026-10-11 核对）

| 来源 | URL |
|---|---|
| Typography in Windows | https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography |
| Color in Windows | https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color |
| Geometry in Windows 11 | https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry |
| Content layout and spacing | https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics |
| Screen sizes and breakpoints | https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design |
| Navigation basics | https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics |
| NavigationView | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview |
| Commanding basics | https://learn.microsoft.com/en-us/windows/apps/design/basics/commanding-basics |
| Buttons | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons |
| Toggle switches | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggles |
| Collections and lists | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/lists |
| Nested UI in list items | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/nested-ui |
| Dialog controls | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs |
| InfoBar | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar |
| Progress controls | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls |
| Windows app title bar | https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design |
| Support snap layouts | https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu |
| Keyboard interactions | https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions |
| Visual feedback（焦点视觉） | https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback |
| Keyboard accessibility | https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility |
| Expose basic accessibility information | https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information |
| Accessible text requirements | https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements |
| Contrast themes | https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes |
| Landmarks and headings | https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings |
| Accessibility checklist | https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist |
| Globalization and localization | https://learn.microsoft.com/en-us/windows/apps/design/globalizing/globalizing-portal |
| Writing style | https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style |
| Error Messages（Win32 UX Guide） | https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-error |
| Confirmations（Win32 UX Guide） | https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm |
| High DPI desktop development | https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows |
| WPF：Styling for Focus / FocusVisualStyle | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle |
| .NET Accessibility Best Practices | https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices |
| What's new in WPF for .NET 9（Fluent、强调色） | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90 |
| What's new in accessibility in .NET Framework（LiveSetting 4.7.1、SizeOfSet 4.8、ToolTip） | https://learn.microsoft.com/en-us/dotnet/framework/whats-new/whats-new-in-accessibility |
| AutomationProperties.HeadingLevelProperty / IsDialogProperty | https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationproperties.headinglevelproperty ；https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationproperties.isdialogproperty |
| WCAG 2.2 | https://www.w3.org/TR/WCAG22/ |
| WCAG2ICT（2025-12-11 Group Note） | https://www.w3.org/TR/wcag2ict-22/ |
| 项目红线 | `docs/development/design/workbench-control-and-shared-session.md` |
