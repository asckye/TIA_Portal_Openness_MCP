using System;
using System.IO;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Optional;

internal static class OptionalAssemblyAdmission
{
    internal static HardwareAddressingReply Run(string release, string assembly, Func<HardwareAddressingReply> operation)
    {
        if (release == "21")
        {
            try { Assembly.Load(assembly); }
            catch (Exception error) when (error is FileNotFoundException || error is FileLoadException || error is BadImageFormatException)
            {
                /* swallow(probe-optional): expected resolution failures return a typed refusal before native calls. */
                return OptionalEngineeringCapabilities.Refusal("Official optional component is not installed/resolvable: " + assembly);
            }
        }
        return operation();
    }
}
