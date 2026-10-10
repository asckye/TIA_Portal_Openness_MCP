using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope = "")
            => new PlcOrganisationPortService().TraceTagCause(softwarePath, tag, blockScope);
        public ResponseJsonReport TraceTagCauseLive(string softwarePath, string tag, string ip, int rack = 0, int slot = 1, string blockScope = "", string expectModuleContains = "")
            => new PlcOrganisationPortService().TraceTagCauseLive(softwarePath, tag, ip, rack, slot, blockScope, expectModuleContains);
    }
}
