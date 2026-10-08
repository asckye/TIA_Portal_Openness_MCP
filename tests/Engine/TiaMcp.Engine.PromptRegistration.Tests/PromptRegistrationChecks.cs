using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Sdk;

[XunitTestCaseDiscoverer("TiaMcp.Engine.Tests.Shared.CheckTheoryDiscoverer", "TiaMcp.Engine.PromptRegistration.Tests")]
public sealed class CheckTheoryAttribute : TheoryAttribute { }

public sealed class PromptRegistrationChecks
{
    [CheckTheory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Check(CheckRow row) => row.Verify();

    public static IEnumerable<object[]> Rows() => CheckSuite.Run(nameof(PromptRegistrationChecks), Run);

    private static void Run(Action<bool, string> Check)
    {
        VerifyCurrentPromptText(Check);
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var scratch = Path.Combine(Path.GetTempPath(), "tia-prompt-registration-" + Guid.NewGuid().ToString("N"));
        var contexts = new List<WeakReference>();
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
                LoadAndRun(directory, optionalPresent, Check, contexts);
            }
        }
        finally
        {
            // Windows keeps fixture DLLs locked until the collectible contexts actually unload.
            // The non-inlined helper drops reflection/async locals before this bounded GC wait.
            foreach (var context in contexts)
            {
                for (int attempt = 0; context.IsAlive && attempt < 10; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(50);
                }
                Check(!context.IsAlive, "Fixture context unloaded before scratch cleanup");
            }
            if (contexts.All(context => !context.IsAlive))
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                    Check(!Directory.Exists(scratch), "Fixture scratch directory removed after unload");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Check(false, "Fixture scratch cleanup failed after unload: " + ex); }
            }
        }
    }

    private static void VerifyCurrentPromptText(Action<bool, string> Check)
    {
        var methods = typeof(TiaMcpServer.ModelContextProtocol.McpPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType.Name == "McpServerPromptAttribute"))
            .ToArray();
        Check(methods.Length == 30, $"Current MCP prompt inventory is 30; found {methods.Length}");
        foreach (var method in methods)
        {
            var args = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(string)
                ? (object)"sample" : parameter.ParameterType == typeof(bool) ? true : 1).ToArray();
            string prompt = (string)method.Invoke(null, args)!;
            bool currentRules = new[]
            {
                "schemaVersion 4 envelope", "ListPortalProcessProjects", "meta.outcome", "OUTCOME_UNKNOWN",
                "D1 native behavior remains current", "L5 is NOT RUN", "Do not save or close a project unless the user explicitly requested that action",
                "Workbench approval before dispatch", "data/logs/audit", "tia audit verify"
            }.All(prompt.Contains);
            Check(currentRules, $"{method.Name} prompt carries V4, selection, D1, approval and audit rules");
            bool stale = new[]
            {
                "EnsureOpennessUserGroup", "fbBlockJson", "designJson", "meta.success / meta.operationSuccess",
                "SaveProject — save any pending changes first"
            }.Any(prompt.Contains);
            Check(!stale, $"{method.Name} prompt has no legacy or JSON-string instructions");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAndRun(string directory, bool optionalPresent, Action<bool, string> Check,
        List<WeakReference> contexts)
    {
        var context = new FixtureContext(directory);
        contexts.Add(new WeakReference(context));
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, "PromptTest.Fixture.dll"));
            var run = (Task)assembly.GetType("PromptRegistrationFixture", true)!.GetMethod("Run")!
                .Invoke(null, new object[] { optionalPresent, Check })!;
            run.GetAwaiter().GetResult();
        }
        finally { context.Unload(); }
    }

}

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
        // The SDK caches reflected prompt methods under its default serializer options.
        // Load these real SDK assemblies here so their caches cannot root an unloaded fixture.
        if (name.Name!.StartsWith("ModelContextProtocol", StringComparison.Ordinal) ||
            name.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            return LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"));
        return null; // Share framework types with the test host.
    }
}
