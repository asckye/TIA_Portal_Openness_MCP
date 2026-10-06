using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer
{
    internal static class EngineRegistration
    {
        internal static IServiceCollection AddEngine(this IServiceCollection services, bool includeSession)
            => AddEngine(services, includeSession, ToolCatalog.Engine);

        internal static IServiceCollection AddEngine(this IServiceCollection services, bool includeSession, ToolCatalog catalog)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            services.TryAddSingleton(catalog);
            if (includeSession)
            {
                services.TryAddSingleton<Portal>();
                services.TryAddSingleton<IEngineeringSession>(provider => provider.GetRequiredService<Portal>());
                services.TryAddSingleton<IHmiToolSession>(provider => provider.GetRequiredService<Portal>());
                foreach (var type in ToolCatalog.LoadableTypes(typeof(Portal).Assembly, "EngineRegistration"))
                {
                    if (!type.IsClass || type.IsAbstract || type.Namespace != "TiaMcpServer.Siemens.Services"
                        || !type.Name.EndsWith("Service", StringComparison.Ordinal)) continue;
                    if (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type))
                        throw new InvalidOperationException("Engine singleton service must not be disposable: " + type.FullName);
                    services.TryAdd(ServiceDescriptor.Singleton(type, type));
                }
            }
            foreach (var type in catalog.Methods.Where(pair => !pair.Value.IsStatic)
                .Select(pair => pair.Value.DeclaringType!).Distinct())
            {
                // The MCP SDK releases each target after a call; singleton tool classes must not be disposable.
                if (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type))
                    throw new InvalidOperationException("MCP singleton tool type must not be disposable: " + type.FullName);
                services.TryAdd(ServiceDescriptor.Singleton(type, type));
            }
            return services;
        }
    }
}
