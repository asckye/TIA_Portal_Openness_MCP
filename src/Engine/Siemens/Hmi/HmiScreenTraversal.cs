using System.Collections.Generic;
namespace TiaMcpServer.Siemens
{
    internal static class HmiScreenTraversal
    {
        internal static List<string> ListNames(object root) => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.ListNames(root);
        internal static object? FindByName(object root, string name) => TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.FindByName(root, name);
    }
}
