using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public static class PromptRegistrationFixture
{
    public static async Task Run(bool optionalPresent, Action<bool, string> Check)
    {
        // SDK-owned JSON contracts avoid retaining collectible types in shared reflection caches.
        var assembly = typeof(PromptRegistrationFixture).Assembly;
        var scannedServices = new ServiceCollection();
        bool scanFailed = false;
        try { scannedServices.AddMcpServer().WithPromptsFromAssembly(assembly); }
        catch (ReflectionTypeLoadException ex)
        {
            scanFailed = true;
            Check(ex.LoaderExceptions.Any(e => e is FileNotFoundException && e.Message.Contains("PromptTest.OptionalDependency")), "unexpected loader failure");
        }
        Check(scanFailed == !optionalPresent, "assembly scan must fail only when fake optional DLL is absent");
        var promptType = assembly.GetType("TiaMcpServer.ModelContextProtocol.McpPrompts", throwOnError: true)!;
        var expected = promptType.GetMethods().Select(m => m.GetCustomAttribute<McpServerPromptAttribute>())
            .Where(a => a != null).Select(a => a!.Name!).OrderBy(n => n).ToArray();
        Check(expected.Length == 30 && expected.Distinct().Count() == 30, "30 unique production prompts must survive");
        foreach (var transportSite in new[] { "stdio", "HTTP stream" })
        {
            var services = new ServiceCollection();
            var builder = services.AddMcpServer();
            if (transportSite == "stdio") builder.WithStdioServerTransport();
            else builder.WithStreamServerTransport(Stream.Null, Stream.Null);
            var registration = assembly.GetType("TiaMcpServer.ModelContextProtocol.McpPromptRegistration", true)!;
            registration.GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { builder });
            await using var provider = services.BuildServiceProvider();
            var prompts = provider.GetServices<McpServerPrompt>().ToArray();
            Check(prompts.Select(p => p.ProtocolPrompt.Name).OrderBy(n => n).SequenceEqual(expected), "registered prompt inventory differs");
            if (optionalPresent)
            {
                await using var baseline = scannedServices.BuildServiceProvider();
                var baselineSchemas = baseline.GetServices<McpServerPrompt>().OrderBy(p => p.ProtocolPrompt.Name).Select(p => JsonSerializer.Serialize(p.ProtocolPrompt, ModelContextProtocol.McpJsonUtilities.DefaultOptions)).ToArray();
                var actualSchemas = prompts.OrderBy(p => p.ProtocolPrompt.Name).Select(p => JsonSerializer.Serialize(p.ProtocolPrompt, ModelContextProtocol.McpJsonUtilities.DefaultOptions)).ToArray();
                Check(actualSchemas.SequenceEqual(baselineSchemas), "explicit registration must preserve every original prompt schema");
            }
            var server = provider.GetRequiredService<IMcpServer>();
            foreach (var prompt in prompts)
            {
                var request = new RequestContext<GetPromptRequestParams>(server)
                {
                    Services = provider,
                    Params = new GetPromptRequestParams
                    {
                        Name = prompt.ProtocolPrompt.Name,
                        Arguments = promptType.GetMethods().Single(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name == prompt.ProtocolPrompt.Name)
                            .GetParameters().ToDictionary(p => p.Name!, p => JsonSerializer.SerializeToElement(
                                p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(string) ? "regression-example" : Activator.CreateInstance(p.ParameterType)))
                    }
                };
                var result = await prompt.GetAsync(request);
                Check(result.Messages.Count > 0, "prompt invocation returned no messages");
            }
            Console.WriteLine($"PASS {transportSite} registration helper: all 30 real prompts registered and rendered; optional DLL {(optionalPresent ? "present" : "absent")}");
        }
    }
}
