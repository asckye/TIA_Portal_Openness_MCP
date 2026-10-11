using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseExportFile : ResponseMessage
    {
        public string? ExportPath { get; set; }
    }

    public class ResponseBatchExport : ResponseMessage
    {
        public IEnumerable<string>? Exported { get; set; }
        public IEnumerable<string>? Failed { get; set; }
    }
}
