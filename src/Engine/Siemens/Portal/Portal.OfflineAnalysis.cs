using System;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Exports one block to a fresh temp directory and returns the single SimaticML file; used only when
        // ComparePlcBlockDocuments is asked for a block path instead of a file. Requires an open project and
        // a consistent block (ExportBlock enforces both); nothing is saved, compiled or downloaded.
        public (string TempDir, string XmlPath) ExportBlockDocumentForAnalysis(string softwarePath, string blockPath)
        {
            if (string.IsNullOrWhiteSpace(softwarePath)) throw new ArgumentException("softwarePath is required for block-path mode.");
            if (string.IsNullOrWhiteSpace(blockPath)) throw new ArgumentException("blockPath is required for block-path mode.");
            var result = ExportBlockToTemp(softwarePath, blockPath)
                ?? throw new PortalException(PortalErrorCode.ExportFailed, "Block export returned nothing for '" + blockPath + "'.");
            var xml = result.Paths.FirstOrDefault(p => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new PortalException(PortalErrorCode.ExportFailed, "Block export produced no .xml for '" + blockPath + "' in " + result.TempDir);
            return (result.TempDir, xml);
        }
    }
}
