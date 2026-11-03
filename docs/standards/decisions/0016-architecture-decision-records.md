# 0016 设计决策用 ADR 记录，一页一个决定

- 状态：已采纳
- 日期：2026-10-11
- 决策者：维护者
- 置信度：高
- 相关：[文档写作规范](../design-spec/documentation-style.md) 第 14 节；[ADR 索引](README.md)；[ADR 模板](0000-template.md)；[ADR 0015](0015-microsoft-design-spec-baseline.md)

## 背景

设计决策分散在重构计划、路线图、交接、设计文档末尾的“维护者决定”小节和协调会话中，有些只有协调记录。新读者难以找到决定和理由；决定被修改后，原来的理由和变化的原因也看不出来。

## 选项

1. 继续在各文档中记录：没有额外流程；记录分散，修改时直接改写原文。
2. 采用架构决策记录（ADR）：一页一个决定、只追加；既往决定需要回溯补写。

## 决定

采用选项 2，参考 Microsoft Azure Well-Architected Framework 的 [Maintain an architecture decision record (ADR)](https://learn.microsoft.com/en-us/azure/well-architected/architect-role/architecture-decision-record) 与 Michael Nygard 的 [Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)：

- ADR 放在 `docs/development/decisions/NNNN-<短标题>.md`，按 [模板](0000-template.md) 编写，用中文；
- ADR 只追加：已采纳的 ADR 不改写正文，决定改变时写新的 ADR 取代旧的，两边互相链接；
- [索引](README.md) 列出全部 ADR 和待补写的决定；
- 设计文档链接 ADR，不再在文末维护“维护者决定”小节。

首批 0001–0016 于 2026-10-11 写入（0001–0014 为回溯记录）；其余待补写的决定列在索引中。

## 后果

- 正面：每个结构性决定有一页独立可读的记录，修改的历史可追溯。
- 负面与取舍：回溯记录依赖现有书面材料，个别只有协调记录的决定须经维护者确认后再写。
- 后续工作：索引中标为“4.0 前：是”的待补写决定在整合 P7-11b 文档时补写（P8-63）。
- 验证方式：索引与文件一致；仓库链接检查；设计文档按文档写作规范第 14.5 节链接 ADR。
