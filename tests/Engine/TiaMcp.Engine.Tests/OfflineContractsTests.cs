using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TiaMcpServer.ModelContextProtocol;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class OfflineContractsTests
    {
        [CheckTheory]
        [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
        public void Check(CheckRow row) => row.Verify();

        public static IEnumerable<object[]> Rows()
            => CheckSuite.Run(nameof(OfflineContractsTests), check =>
            {
                var assembly = typeof(ImportOrderTools).Assembly;
                var types = OfflineContractChecks.Groups.Select(name =>
                    assembly.GetType("TiaMcpServer.ModelContextProtocol." + name, true)!).ToArray();
                var services = new ServiceCollection();
                foreach (var type in types)
                {
                    var constructor = type.GetConstructors().Single();
                    services.AddSingleton(type, constructor.Invoke(constructor.GetParameters().Select(_ => (object?)null).ToArray()));
                }
                using var provider = services.BuildServiceProvider();
                try
                {
                    McpServer.ConfigureToolBridge(new ToolCatalog(types), () => false, new HashSet<string>());
                    EngineServices.SetServiceProvider(provider);
                    int count = 0;
                    new OfflineContractChecks(assembly, (ok, message) => { count++; check(ok, message); }).Run(Repository());
                    if (count < 453) throw new InvalidOperationException("Missing offline contract checks: " + count);
                }
                finally { ToolBridgeFixture.Configure(); }
            });

        private static string Repository([CallerFilePath] string source = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
    }
}
