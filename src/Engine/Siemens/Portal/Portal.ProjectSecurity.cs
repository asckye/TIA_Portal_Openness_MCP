using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Siemens.Engineering.Umac;
using TiaMcp.Adapters.Native.Security;
namespace TiaMcpServer.Siemens;
public partial class Portal
{
    private UmacConfigurator RequireUmac() => ProjectSecurityPrimitives.RequireUmac(_project!);
    private static object? FindOrdinal(object collection, string name, string kind) => ProjectSecurityPrimitives.FindOrdinal(collection, name, kind);
    private static Role ExactUmacRole(UmacConfigurator umac, string name)
        => ProjectSecurityPrimitives.ExactUmacRole(umac, name);
    private static JsonObject UmacRow(object item) => JsonSerializer.SerializeToNode(ProjectSecurityPrimitives.UmacRow(item))!.AsObject();
}
