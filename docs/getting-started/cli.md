# CLI 与 AI spec 指南

交付包中的 **TiaMcpServer.exe** 同时支持 MCP 服务与命令行。
不需要装 MCP 客户端、不需要会编程：**任意 AI 写一份 spec，你跑一条命令。**

## 直接调用引擎

在完整包根目录打开终端，V21 使用 `runtime\v21\TiaMcpServer.exe`，V20 使用 `runtime\v20\TiaMcpServer.exe`。若希望在任意目录调用，可把所选 runtime 目录加入 PATH，然后使用 `TiaMcpServer.exe`。

下文命令以 V21 为例；V20 将路径中的 `v21` 改为 `v20`。旧 CMD 已被删除；AI 客户端与服务配置请双击 `TiaMcpConfigurator.exe`。

---

## 三种用法，门槛从低到高

### 1. 可选快捷脚本（默认 V21）
- 把一个 `spec.yaml` 或 `spec.json` **拖到 `scripts\operations\生成工程.bat` 上** → 自动建工程。
- 想让连接更快：先双击 `scripts\operations\预热.bat`（留着别关），之后每次建工程复用已启动实例。

### 2. 一条命令
```
runtime\v21\TiaMcpServer.exe gen  项目.yaml              # 从 spec 建完整工程
runtime\v21\TiaMcpServer.exe gen  项目.yaml --dry-run    # 只离线校验 spec，不连 TIA、不建任何东西
runtime\v21\TiaMcpServer.exe patch 改动.yaml             # 把 spec 增量合并进已有工程（spec 里写 projectPath）
runtime\v21\TiaMcpServer.exe compile  D:\proj\X.ap21 --plc PLC_1
runtime\v21\TiaMcpServer.exe describe D:\proj\X.ap21 --plc PLC_1
runtime\v21\TiaMcpServer.exe prewarm                     # 常驻 headless 实例，供后续命令复用
runtime\v21\TiaMcpServer.exe doctor                      # 一键体检：TIA 安装/exe 版本匹配/Openness 组/宿主注册（--fix 自动修用户组）
runtime\v21\TiaMcpServer.exe config                      # 一键把 MCP 注册进 Claude Desktop/Claude Code/Cursor/VS Code；日常配置优先使用 WPF 配置器
runtime\v21\TiaMcpServer.exe schema                      # 打印 spec 所有字段说明
```
退出码：**0=成功，1=有失败步骤，2=错误**（方便脚本/CI 判读）。
加 `--json` 输出机器可读结果，方便让 AI 读结果自我纠错。

### 3. 让 AI 生成 spec
把本页末尾的提示词 + `runtime\v21\TiaMcpServer.exe schema` 的输出贴给任意 AI，
描述你要的工程，AI 产出 `spec.yaml`，再走用法 1 或 2。

---

## spec 长什么样

最小例子（YAML）：
```yaml
projectName: MyLine
plcName: PLC_1
plcFamily: S7-1500
udt:
  - name: UDT_Status
    members:
      - { name: Active, datatype: Bool, commentZhCn: 运行 }
tagTable:
  - tableName: IO
    tags:
      - { name: Start, dataTypeName: Bool, logicalAddress: "%I0.0" }
compile: true
save: true
```
完整字段见 `runtime\v21\TiaMcpServer.exe schema`，现成模板见 `templates/project-blueprints/`（通用启停、电机示例，实际工程需独立编译验收）。
这两个模板**开箱即用**——里面引用 `.scl`/`.s7dcl` 的路径写成 `__BUNDLE__\...`，
引擎会自动把 `__BUNDLE__` 解析成交付包根目录，你直接 `runtime\v21\TiaMcpServer.exe gen templates\project-blueprints\scaffold_spec_motor.json` 即可，
无需手动替换路径。（拷到别处用同样有效，只要引擎仍位于交付包的 runtime 目录。）

提示：
- `runtime\v21\TiaMcpServer.exe gen` 从零建；`runtime\v21\TiaMcpServer.exe patch` 改已有工程（spec 里加 `projectPath: D:\...\X.ap21`）。
- HMI 画面 `width/height` 要按面板原生分辨率，否则被裁剪。
- `hmiTags` 用绝对地址（`%M..`）更容易通过回读校验。
- JSON 是首选格式（零歧义，AI 生成最稳）；YAML 是给人读写的便利。

---

## 常见问题

- **慢？** 冷启动取决于机器、工程与 Openness。可先运行 `runtime\v21\TiaMcpServer.exe prewarm` 保留实例；预热不保证固定耗时。
- **中文乱码？** 输出已强制 UTF-8；`.scl` 保存为 UTF-8 无 BOM；`.s7dcl` / Openness XML 使用 UTF-8 BOM。
- **V20 还是 V21？** 使用与已安装 TIA 大版本匹配的 EXE，仓库和 ZIP 均提供 `runtime/v20`、`runtime/v21`。非默认安装传 `--tia-portal-location "安装根目录"`（不含 Bin）及对应的 `--tia-major-version 20|21`。
- **要看 GUI？** 加 `--with-ui` 用完整界面启动（较慢）。
- **工程路径可以写相对的吗？** 可以——`runtime\v21\TiaMcpServer.exe describe/compile/export/import` 和 `runtime\v21\TiaMcpServer.exe patch` 的工程路径
  现在按你当前所在目录解析（v2.0 修复，之前只认 exe 目录会报 `Projects.Open failed`）。
- **`runtime\v21\TiaMcpServer.exe prewarm` 怎么停？** 在它运行的那个窗口按 `Ctrl+C` 即可优雅关闭。若是后台/双击启动的，
  `runtime\v21\TiaMcpServer.exe prewarm --stop` 只会关掉那个 headless TIA 实例，预热**进程本身**需手动结束（任务管理器里的 `TiaMcpServer.exe`）。


---



## 让任意 AI 生成 tia spec 的提示词

把下面这段（连同 `runtime\v21\TiaMcpServer.exe schema` 的输出）贴给任意 AI（Claude / GPT / Gemini / 国产模型均可），
再用自然语言描述你要的博途工程。AI 会产出一份 `spec.json`（或 `.yaml`），
你保存后运行 `runtime\v21\TiaMcpServer.exe gen <spec>` 即可。**这是通用契约——不需要 AI 支持 MCP。**

---

## 提示词（复制以下整段）

> 你是西门子 TIA Portal 工程生成助手。我会用自然语言描述一个 PLC/HMI 工程，
> 你只输出一份**严格合法的 JSON**（不要解释、不要 Markdown 代码围栏外的任何文字），
> 用于命令行工具 `runtime\v21\TiaMcpServer.exe gen`。规则：
>
> 1. 只用下面【字段说明】里列出的键，不要发明新键。
> 2. `projectName` 必填。从零建工程时不要写 `projectPath`。
> 3. `udt` / `globalDb` / `tagTable` 的对象形状严格照【字段说明】的示例。
> 4. PLC 逻辑：简单结构/数据放 `udt`/`globalDb`/`tagTable`；带表达式/算法的 FB/FC
>    用 `sclSourceFiles` 引用 `.scl` 外部源文件路径（不要试图用 JSON 表达 SCL 逻辑）。
> 5. HMI 画面 `width`/`height` 用目标面板的原生分辨率（如 800×480 / 1280×800）。
>    画面元素放在 `designJson.items`，文字用独立 `Text` 项（不要写在 Rectangle 上）。
> 6. `hmiTags` 尽量用绝对地址（`%M..` / `%DB..`）。
> 7. 不确定的可选项就省略（用默认值），不要瞎填。
>
> 【字段说明】
> <在这里粘贴 `runtime\v21\TiaMcpServer.exe schema` 的输出>
>
> 现在等待我的工程描述。

---

## 使用流程

1. 运行 `runtime\v21\TiaMcpServer.exe schema`，复制输出，替换提示词里的 `<...>`。
2. 把整段提示词发给 AI，然后描述工程（例：「S7-1500 + WinCC Unified，一个启停控制，
   带运行/故障状态，HMI 800×480 一个启动按钮一个停止按钮两个状态灯」）。
3. 保存 AI 输出为 `spec.json`。
4. 先 `runtime\v21\TiaMcpServer.exe gen spec.json --dry-run` 离线校验；通过后去掉 `--dry-run` 正式生成。
5. 失败时把 `--json` 输出贴回给 AI，让它按报错修订 spec。
