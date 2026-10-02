using System.Reflection;
using System.Text.Json;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Only self-authored fixture DLLs are loaded. No Siemens DLLs, TIA process,
// Windows identity API or engineering operation is involved.
var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");
var scratch = Path.Combine(Path.GetTempPath(), "tia-prompt-registration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    foreach (bool optionalPresent in new[] { true, false })
    {
        string directory = Path.Combine(scratch, optionalPresent ? "present" : "absent");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(fixtureDirectory, "PromptTest.Fixture.dll"), Path.Combine(directory, "PromptTest.Fixture.dll"));
        if (optionalPresent)
            File.Copy(Path.Combine(fixtureDirectory, "PromptTest.OptionalDependency.dll"), Path.Combine(directory, "PromptTest.OptionalDependency.dll"));
        var context = new FixtureContext(directory);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, "PromptTest.Fixture.dll"));
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
                    var baselineSchemas = baseline.GetServices<McpServerPrompt>().OrderBy(p => p.ProtocolPrompt.Name).Select(p => JsonSerializer.Serialize(p.ProtocolPrompt)).ToArray();
                    var actualSchemas = prompts.OrderBy(p => p.ProtocolPrompt.Name).Select(p => JsonSerializer.Serialize(p.ProtocolPrompt)).ToArray();
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
        finally { context.Unload(); }
    }
}
finally { Directory.Delete(scratch, recursive: true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class FixtureContext(string directory) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "PromptTest.OptionalDependency")
        {
            string file = Path.Combine(directory, name.Name + ".dll");
            if (!File.Exists(file)) throw new FileNotFoundException("Missing fake optional dependency: " + name.Name, file);
            return LoadFromAssemblyPath(file);
        }
        return null; // Share the actual SDK and framework with the test host.
    }
}
