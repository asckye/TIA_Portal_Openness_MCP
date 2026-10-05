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
            "\r\nFollow this sequence; do not guess by changing the syntax:\r\n"
            + "1) GetToolUsage(language:'lad') (SCL: language:'scl') — read the language examples and encoding rules, "
            + "Align your file item by item; change only names and operands, not the structure.\r\n"
            + "2) .s7dcl import **fails atomically**: any invalid part prevents the entire document from being imported, and Openness provides no line number. "
            + "Include one NETWORK at a time: import, compile successfully, then add the next segment. Importing the whole document at once leaves no location information when it fails.\r\n"
            + "3) One NETWORK is one program segment; multiple RUNGs within the same NETWORK are parallel branches of that segment."
            + "For 12 segments, write 12 NETWORKs.\r\n"
            + "4) Encoding must be UTF-8 **with BOM**. Do not write the BOM escape sequence as six literal characters in the file body: "
            + "that is an unprocessed escape sequence, not a BOM.\r\n"
            + "5) For DBs, UDTs or tag tables, use BuildAndImportPlcArtifact with a structured spec to bypass .s7dcl.";


        #endregion
    }
}
