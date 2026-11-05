# 0032 引用已发布的《工程开发规范》2.0，不保存副本；采纳 MCP 草案前 MCP 和厂商 API 规则仍按 1.1

- 状态：已采纳
- 日期：2026-11-05
- 决策者：维护者（提出者：Claude）
- 置信度：高
- 相关：[ADR 0030](0030-documentation-and-repository-structure.md)；[本项目采用的开发规范](../README.md)

## 背景

本仓库把工程开发手册 1.1 存成一份 HTML（`docs/standards/engineering-handbook.html`），并以它为依据。规范现在发布在 [asckye/engineering-standards](https://github.com/asckye/engineering-standards/tree/v2.0)，tag `v2.0`。已发布的文本和这份 HTML 不同：通用规则放在 `general/`，Windows、打包、诊断和桌面界面的取值移到 `desktop/`，领域 A、C 的正文已删除，由草稿 0.1 的 `mcp/` 取代。手工保留一份副本不符合规范的 P-09、P-26。

本项目还没有采纳 `mcp/`。部分规则在 `mcp/` 中与项目现行规则不同：`TL-06` 建议新服务用 `<产品>_<资源>_<动作>`，项目用 PascalCase；`WS-05` 把默认预览降为 SHOULD，项目仍按 MUST。v2.0 中已没有 1.1 的领域 A、C，也没有 v1.1 tag；这部分正文只在已公开的提交 `ec8e722` 中。

## 选项

引用 1.1 的 `MCP-xx`、`VND-xx` 规则：

1. 采纳前继续写 1.1 编号，链接固定到提交 `ec8e722`。不会把未采纳的规则写成依据；代价是同时引用两个版本，链接也不是 tag。
2. 现在改写新编号（`PRO`、`TL`、`WS`、`ENG`）。只剩一个版本；但部分引用与项目现行规则不一致，等于提前采纳草案。

本仓库的 HTML：

1. 删除。只保留一个来源，Git 历史仍可取回；离线阅读要自己克隆规范仓库。
2. 保留并标明是 1.1 副本、不是来源。可以离线阅读，但仓库里一直多一份手工副本。

## 决定

维护者 2026-11-05 选择两组的选项 1：

- 通用规则和桌面规则引用 `v2.0` 的 `general/`、`desktop/`。
- MCP 和厂商 API 规则继续写 1.1 编号，链接固定到提交 `ec8e722`。这项并存只限这部分规则，维护者采纳 `mcp/` 时结束。
- 删除 `docs/standards/engineering-handbook.html`。

## 后果

- 正面：规范只有一个来源；项目文档不会把未采纳的草案写成依据。
- 负面与取舍：通用规则和 MCP 规则引用不同版本；升级规范版本时要逐处改链接。
- 后续工作：采纳 `mcp/` 时改用新编号，并按[采用说明](../README.md#mcp-草案待采纳)的清单登记偏差。
- 验证方式：`check-repository -NoBinaries` 通过；除历史记录和测试夹具外，仓库中搜不到 `engineering-handbook.html`。
