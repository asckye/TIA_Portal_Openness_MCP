using System;
using System.IO;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Optional;

internal static class OptionalAssemblyAdmission
{
    internal static HardwareAddressingReply Run(string release, string assembly, Func<HardwareAddressingReply> operation,
        Func<AssemblyName, Assembly>? load = null)
    {
        if (release == "21")
        {
            try { (load ?? Assembly.Load)(OpennessAssemblyNames.Sibling(OpennessAssemblyNames.V21Core, assembly)); }
            catch (FileNotFoundException)
            {
                /* swallow(probe-optional): expected resolution failures return a typed refusal before native calls. */
                return OptionalEngineeringCapabilities.Refusal(
                    "Official optional component is not installed: " + assembly + " is not in the selected PublicAPI folder.");
            }
            catch (Exception error) when (error is FileLoadException || error is BadImageFormatException)
            {
                /* swallow(probe-optional): load failures retain the native reason in a typed refusal before native calls. */
                return OptionalEngineeringCapabilities.LoadFailure(assembly, error);
            }
        }
        return operation();
    }
}
