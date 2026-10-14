using System;
using System.Collections.Generic;

namespace TiaMcp.Shared
{
    internal static class UnifiedHmiPropertyAdmission
    {
        internal static Dictionary<string, object?>? Validate(Type type, string input)
            => UnifiedHmiPropertyRules.Validate(type, input);
    }
}
