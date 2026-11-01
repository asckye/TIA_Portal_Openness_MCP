# 0027 架构、流程和时序图用 archify 生成，JSON 为源，HTML 与来源记录一起提交

- 状态：已采纳
- 日期：2026-10-22（维护者批准文档重组方案的全部 15 项决定）；回溯记录（写于 2026-11-01）
- 决策者：维护者
- 置信度：中
- 相关：[ADR 0026](0026-documentation-structure-and-language.md)；[文档写作规范](../design-spec/documentation-style.md) 第 11.1 节；任务 DOCS（批次 D4a、D4b）

## 背景

现有架构说明用 Mermaid 写在 Markdown 中（文档写作规范第 11.1 节），README 用手写 SVG。EplanMCP 用 archify（Claude Code 技能 `tt-a1i/archify`）生成可交互的 HTML 图：支持深浅主题、聚焦和导出图片。EplanMCP 只跟踪技能锁文件，生成步骤是手动的，CI 不核对图。

约束：

- 构建和发布自动化只用 C#（`build-tools/release`），仓库拒绝新增 PowerShell、batch 和 cmd 文件，也不新增 `.mjs` 构建脚本。
- 2026-10-22 调查时开发机没有 Node.js，也没有 archify 副本；Codex 沙箱没有 Node 和网络。
- 生成的 HTML 内嵌 archify 查看器代码（需核实为 MIT）和 JetBrains Mono 的 WOFF2 子集（SIL OFL 1.1）。
- 图不能写入工具数量等随代码变化的数值（文档写作规范第 9 节）。

## 选项

1. 继续用 Mermaid：无新依赖，差异易审；没有交互和统一的视觉样式。
2. 照搬 EplanMCP 的手动步骤：与姊妹项目一致；仓库中的 HTML 无法离线核对是否与源一致。
3. 用 archify 生成，C# 命令负责生成和离线核对：图可交互；开发机需要 Node 和技能，仓库增加约每张 0.8 MB 的 HTML。

## 决定

采用选项 3。

- 位置：每张图一个目录 `docs/diagrams/<名称>/`，含 `<名称>.json`（archify JSON，`meta.locale` 为 `zh-CN`）、`<名称>.html`（生成结果）和 `<名称>.provenance.json`（archify 版本、技能锁哈希、打过字体补丁的模板 SHA-256、JSON 与 HTML 的 SHA-256、图类型、四道检查结果）。
- 生成：新增 `build-tools/release` 命令 `diagrams`。生成模式找到 Node，核对已安装技能与锁一致，用 C# 给模板打 Maple Mono 字体补丁，运行 archify `finalize`，要求 validate、deliver、check、browser-check 四道检查全部通过后写来源记录。archify 的回执写到已被忽略的 `bin-build/archify/`。
- 核对：`diagrams -Check` 不需要 Node，只核对 JSON、HTML 与来源记录的哈希一致、HTML 的生成器版本等于锁定版本、没有外部脚本或样式地址、仓库中没有回执文件。`check-repository` 调用该检查；CI、`check-all`、发布和打包都不运行 archify。
- 依赖：Node.js LTS 与 archify 技能只装在开发机上，安装时关闭遥测；根目录跟踪 `skills-lock.json`。技能版本升级按依赖变更审查。仓库中不放 `package.json` 或 `node_modules`。
- 文字为权威：图是对应文档章节的可视化，两者在同一批次一起改；图中不写工具数量和其他生成数值。
- 不随包：`docs/diagrams/` 不在交付允许列表中。README 继续用手写 SVG（英文、中文各一张），内容与系统架构图一致；archify 导出的图片不放进 `docs/assets/`。
- 许可证：锁定提交的 LICENSE 核实为 MIT 后才提交第一张图；许可证原文放入 `docs/licenses/`，`THIRD-PARTY-NOTICES.md` 把 archify 与内嵌的 JetBrains Mono 列为“仓库内文档资源，不随包”。
- 取代文档写作规范第 11.1 节中“架构图用 Mermaid”的写法。

## 后果

- 正面：架构、写调用审批、发布宿主、发布链和工作台页面有统一样式的交互图；仓库中的图能离线核对是否由锁定版本从当前源生成。
- 负面与取舍：
  - 每次重生成一张图约增加 0.8 MB 历史，只在源变化时重生成。
  - 生成依赖开发机上的 Node、技能，browser-check 很可能还需要本机浏览器；能否离线生成要在安装后核实。
  - GitHub 网页只显示 HTML 源码，读者要克隆后用浏览器打开。
- 后续工作：
  - D4a：`diagrams` 命令与测试（字体补丁的幂等与失败路径、来源记录被篡改时 `-Check` 拒绝、进程调用用替身）、`.gitattributes` 规则、`release-checks.json` 映射、`docs/diagrams/README.md`、许可证文件。
  - D4b：安装后生成首批两张图（系统架构、写调用审批时序），其余图在 4.0 之后。
  - 文档写作规范第 11.1 节在 D3b 中改写；现有 Mermaid 块在对应文档的重写批次中处理。
- 验证方式：`diagrams -Check` 在 `check-repository` 中通过；改动 JSON、HTML 或锁定版本任一项都使检查失败（由测试证明）。
