namespace TiaMcpServer.ModelContextProtocol
{
    internal static class DocumentImportGuidance
    {
        #region documents

        /// <summary>
        /// 扫描到了 .s7dcl 但一份都没导进去时附给调用方的自救步骤。
        /// 只引用本版本真实存在的工具，别指向不存在的东西再把人带偏一次。
        /// </summary>
        internal const string DocumentImportHelp =
            "\r\n按这个顺序处理，别改语法瞎猜：\r\n"
            + "1) GetAuthoringGuide(topic:'lad')（SCL 用 'scl'）—— 拿到本引擎验证过的语法与编码规则，"
            + "把你的文件逐条对齐；只改名字和操作数，别改结构。\r\n"
            + "2) .s7dcl 是**原子失败**：整份文档任何一处不合法都整份不导入，Openness 不给行号。"
            + "所以一次只放一个 NETWORK，导入→编译通过→再加下一段。整份写完再导，出错时没有任何定位信息。\r\n"
            + "3) 一个 NETWORK = 一个程序段；同一个 NETWORK 里的多条 RUNG 是同一段的并联分支。"
            + "要 12 段就写 12 个 NETWORK。\r\n"
            + "4) 编码必须 UTF-8 **带 BOM**。别把 BOM 的转义序列当成六个字符写进文件正文 —— "
            + "那是转义没生效，不是 BOM。\r\n"
            + "5) 只需要 DB / UDT / 变量表的话，改走 PlcBuildAndImport 的 JSON 路，完全绕开 .s7dcl。";


        #endregion
    }
}
