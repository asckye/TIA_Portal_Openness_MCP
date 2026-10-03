using Microsoft.Extensions.DependencyInjection;
using System;

namespace TiaMcpServer
{
    internal static class EngineServices
    {
        private static readonly Lazy<IServiceProvider> standalone = new Lazy<IServiceProvider>(() =>
            new ServiceCollection().AddEngine(includeSession: true).BuildServiceProvider());

        internal static IServiceProvider? Host { get; private set; }
        internal static IServiceProvider Provider => Host ?? standalone.Value;

        internal static void SetServiceProvider(IServiceProvider services)
            => Host = services ?? throw new ArgumentNullException(nameof(services));

        internal static void InitializeStandalone() { _ = Provider; }
        internal static object Get(Type type) => Provider.GetRequiredService(type);
    }
}
