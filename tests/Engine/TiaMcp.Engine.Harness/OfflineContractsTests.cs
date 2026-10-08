using System;
using System.Reflection;

namespace TiaMcp.Engine.Tests
{
    internal static class OfflineContractsTests
    {
        internal static void Run(Assembly engine, string repository, Action<bool, string> check)
            => new OfflineContractChecks(engine, check).Run(repository);
    }
}
