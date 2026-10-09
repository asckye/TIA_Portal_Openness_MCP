namespace TiaMcp.Adapters.Contracts
{
    public sealed class BlockDocumentExportReply
    {
        public string? TempDir { get; set; }
        public string? XmlPath { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
    }
}
