using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringExport
    {
        internal static JsonObject Export(object target, string path, bool overwrite = true)
            => JsonSerializer.SerializeToNode(EngineeringFileExport.Export(target, path, overwrite, TiaMcp.Logic.V4.CallerInputFiles.RecordExportFailure))!.AsObject();
        internal static string HashFile(string path) => EngineeringFileExport.HashFile(path);
    }
}
