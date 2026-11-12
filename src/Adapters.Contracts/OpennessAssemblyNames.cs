using System.Reflection;

namespace TiaMcp.Adapters.Contracts;

public static class OpennessAssemblyNames
{
    public static AssemblyName V21Core => new AssemblyName(
        "Siemens.Engineering.Base, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b");

    public static AssemblyName Sibling(AssemblyName core, string simpleName)
    {
        var sibling = new AssemblyName {
            Name = simpleName,
            Version = core.Version,
            CultureInfo = core.CultureInfo
        };
        sibling.SetPublicKeyToken(core.GetPublicKeyToken());
        return sibling;
    }
}
