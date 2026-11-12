using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public static class MemberListCap
    {
        public static int Effective(int requested) => Math.Max(1, Math.Min(2000, requested));
        public static Dictionary<string, object?> Meta(int total, int returned)
            => new Dictionary<string, object?> { ["totalMembers"] = total, ["truncated"] = returned < total };
    }
}
