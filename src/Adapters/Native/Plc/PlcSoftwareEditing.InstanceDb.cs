using System;
using System.Collections.Generic;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
#if PLC_SOFTWARE_PROTECTION
        internal static InstanceDB CreateInstanceDb(PlcBlockGroup group, string name, bool autoNumber, int number, FB fb,
            IDictionary<string, object?> meta)
        {
            int? count = null;
            try { count = group.Blocks.Count; }
            catch (Exception) { /* swallow(probe-optional): an unavailable baseline cannot settle a failed creation. */ }
            try { return group.Blocks.CreateInstanceDB(name, autoNumber, autoNumber && number < 1 ? 1 : number, fb.Name); }
            catch (Exception error) when (count.HasValue && NativeRefusalProbe.ProvesUnchanged(error,
                () => group.Blocks.Count == count.Value && group.Blocks.Find(name) == null))
            {
                meta["mayHaveChanged"] = false;
                meta["apiCallSuccess"] = false;
                throw;
            }
        }
#endif
    }
}
