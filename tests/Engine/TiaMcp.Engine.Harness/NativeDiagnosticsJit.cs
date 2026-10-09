using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;

internal static partial class Program
{
    private static void NativeDiagnosticsJit(string apiDirectory)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
            string path = Path.Combine(Path.GetFullPath(apiDirectory), new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        SerializedCallGuardTests().GetAwaiter().GetResult();
        var type = Server.GetType("TiaMcpServer.Diagnostics.GeneratedNativeCalls", true)!;
        int prepared = 0, open = 0;
        foreach (var method in Server.GetTypes().Where(t => t.Name.StartsWith("__TiaMcpNativeCall_", StringComparison.Ordinal)).SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)))
        {
            if (method.ContainsGenericParameters) { open++; continue; }
            try { RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++; }
            catch (Exception ex) { throw new Exception("Invalid diagnostic wrapper " + method.Name, ex); }
        }
        Check(prepared > 1000, "Missing production native diagnostic wrappers");
        var pagesType = Server.GetType("TiaMcpServer.Siemens.MigrationPages", true)!;
        using (var pages = (IDisposable)Activator.CreateInstance(pagesType, All, null, new object?[] { null }, null)!)
        { pagesType.GetMethod("Expire", All)!.Invoke(pages, null); }
        Console.WriteLine("PASS native diagnostic wrappers preserve private-type access in timer cleanup");
        Console.WriteLine("COMPLETE: " + prepared + " native diagnostic wrappers JIT prepared; " + open + " open generic wrappers not prepared (generic patterns tested separately); no native call executed");
        int adapterChecks = 0;
        AdapterIntegrationChecks.Diagnostics(Server, EngineSurface.For(Server).Adapter!, (ok, message) => { Check(ok, message); adapterChecks++; });
        Console.WriteLine("COMPLETE: " + adapterChecks + " adapter integration diagnostic checks passed");
        EngineSurface.CheckHostRetirement(Server, Check);
    }
}
