using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;

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
            if (includeSession && OpennessReadiness.Ready)
            {
                RegisterPortalServices(services);
            }
            foreach (var type in catalog.Methods.Where(pair => !pair.Value.IsStatic)
                .Select(pair => pair.Value.DeclaringType!).Distinct())
            {
                // The MCP SDK releases each target after a call; singleton tool classes must not be disposable.
                // InitializeEnvironment is available before readiness, but the only constructor's
                // session contract references unavailable Siemens types. Its no-TIA target never reads
                // the session field, so create the target with its default null field without asking the
                // runtime to resolve that constructor signature. Readiness refuses all other session calls.
                if (!OpennessReadiness.Ready
                    && string.Equals(type.FullName, "TiaMcpServer.ModelContextProtocol.SessionTools", StringComparison.Ordinal))
                {
                    if (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type))
                        throw new InvalidOperationException("MCP singleton tool type must not be disposable: " + type.FullName);
                    services.TryAdd(ServiceDescriptor.Singleton(type,
                        _ => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type)));
                    continue;
                }
                Register(services, type, "MCP singleton tool type must not be disposable: ");
            }
            return services;
        }

        // Keep every Portal/Siemens reference out of AddEngine's JIT-compiled body. A packaged
        // engine intentionally has no Siemens assemblies beside it; this method is reached only
        // after startup has proved Openness readiness.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterPortalServices(IServiceCollection services)
        {
            services.TryAddSingleton<TiaMcpServer.Siemens.Portal>();
            services.TryAddSingleton<TiaMcpServer.Siemens.IEngineeringSession>(provider =>
                provider.GetRequiredService<TiaMcpServer.Siemens.Portal>());
            services.TryAddSingleton<TiaMcpServer.Siemens.IHmiToolSession>(provider =>
                provider.GetRequiredService<TiaMcpServer.Siemens.Portal>());
            foreach (var type in ToolCatalog.LoadableTypes(typeof(TiaMcpServer.Siemens.Portal).Assembly, "EngineRegistration"))
            {
                if (!type.IsClass || type.IsAbstract || type.Namespace != "TiaMcpServer.Siemens.Services"
                    || !type.Name.EndsWith("Service", StringComparison.Ordinal)) continue;
                Register(services, type, "Engine singleton service must not be disposable: ");
            }
        }

        // Checking a type's interfaces loads its base types. Without TIA some of them reference Siemens assemblies that
        // cannot load; the readiness gate refuses those tools before dispatch, so the host skips them instead of failing
        // to start. With TIA available the same failure is a real error and is rethrown.
        private static void Register(IServiceCollection services, Type type, string disposableMessage)
        {
            try
            {
                if (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type))
                    throw new InvalidOperationException(disposableMessage + type.FullName);
                services.TryAdd(ServiceDescriptor.Singleton(type, type));
            }
            catch (Exception error) when (!OpennessReadiness.Ready && IsMissingAssembly(error))
            {
                Console.Error.WriteLine("EngineRegistration: skipped " + type.FullName + " (environment not ready): " + error.GetType().Name);
            }
        }

        private static bool IsMissingAssembly(Exception error)
            => error is FileNotFoundException || error is FileLoadException || error is TypeLoadException || error is BadImageFormatException;
    }
}
