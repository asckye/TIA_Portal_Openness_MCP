using System;
using System.Collections.Generic;
using Siemens.Engineering.Connection;

namespace TiaMcp.Adapters.Native.Online;

internal static class OnlineRoutePrimitives
{
#if !ONLINE_ADDRESSES
    internal static string Address(ConfigurationAddress value) => value.Name;
#endif
#if !ONLINE_TARGET_ADDRESSES
    internal static IEnumerable<ConfigurationAddress> TargetAddresses(ConfigurationTargetInterface value) => Array.Empty<ConfigurationAddress>();
#endif
#if !ONLINE_ADDRESSES
    internal static IEnumerable<ConfigurationAddress> PcAddresses(ConfigurationPcInterface value) => Array.Empty<ConfigurationAddress>();
#endif
}
