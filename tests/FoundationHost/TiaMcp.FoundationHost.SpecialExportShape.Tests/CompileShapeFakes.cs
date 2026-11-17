// Native-shape doubles for the actual scoped compiler and software-unit resolver.
// SDK builds and native acceptance remain separate evidence.
namespace Siemens.Engineering
{
    public interface IEngineeringServiceProvider { public T? GetService<T>() where T : class; }
    public abstract class CompileServiceOwner : IEngineeringServiceProvider
    {
        public Dictionary<Type, object> Services { get; } = new();
        public List<Type> ServiceReads { get; } = new();
        public T? GetService<T>() where T : class
        { ServiceReads.Add(typeof(T)); return Services.TryGetValue(typeof(T), out var service) ? (T)service : null; }
    }
}
namespace Siemens.Engineering.Compiler
{
    public interface ICompilable { public CompilerResult Compile(); }
    public enum CompilerResultState { Success, Information, Warning, Error }
    public sealed class CompilerResult
    {
        public CompilerResultState State { get; set; } = CompilerResultState.Success;
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public CompilerResultMessageComposition Messages { get; } = new();
    }
    public sealed class CompilerResultMessageComposition : List<CompilerResultMessage> { }
    public sealed class CompilerResultMessage
    {
        public CompilerResultState State { get; set; }
        public string Description { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime DateTime { get; set; } = new(2026, 11, 17);
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public CompilerResultMessageComposition Messages { get; } = new();
    }
}
namespace Siemens.Engineering.HW
{
    public sealed class DeviceItem : Siemens.Engineering.CompileServiceOwner { }
}
namespace Siemens.Engineering.Safety
{
    public sealed class SafetyAdministration
    {
        public bool IsLoggedOnToSafetyOfflineProgram { get; set; }
        public Action<string>? Observing { get; set; }
        public bool FailLogin { get; set; }
        public bool FailLogoff { get; set; }
        public void LoginToSafetyOfflineProgram(System.Security.SecureString password)
        { Observing?.Invoke("login"); if (FailLogin) throw new InvalidOperationException("native secret"); IsLoggedOnToSafetyOfflineProgram = true; }
        public void LogoffFromSafetyOfflineProgram()
        { Observing?.Invoke("logoff"); if (FailLogoff) throw new InvalidOperationException("logoff"); IsLoggedOnToSafetyOfflineProgram = false; }
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    public abstract class PlcBlockGroup : Siemens.Engineering.CompileServiceOwner
    {
        public string Name { get; set; } = "";
        public PlcBlockComposition Blocks { get; } = new();
        public PlcBlockUserGroupComposition Groups { get; } = new();
    }
    public sealed class PlcBlockSystemGroup : PlcBlockGroup { }
    public sealed class PlcBlockUserGroup : PlcBlockGroup { }
    public sealed class PlcBlockUserGroupComposition : List<PlcBlockUserGroup>
    {
        public Func<string, PlcBlockUserGroup?>? Finding { get; set; }
        public PlcBlockUserGroup? Find(string name) => Finding == null ? this.SingleOrDefault(g => g.Name == name) : Finding(name);
    }
    public sealed class PlcBlock : Siemens.Engineering.CompileServiceOwner { public string Name { get; set; } = ""; }
    public sealed class PlcBlockComposition : List<PlcBlock>
    {
        public Func<string, PlcBlock?>? Finding { get; set; }
        public PlcBlock? Find(string name) => Finding == null ? this.SingleOrDefault(b => b.Name == name) : Finding(name);
        public IList<PlcBlock> Import(FileInfo file, Siemens.Engineering.ImportOptions options) => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Types
{
    public class PlcTypeGroup { }
    public sealed class PlcType { }
    public sealed class PlcTypeComposition
    {
        public IList<PlcType> Import(FileInfo file, Siemens.Engineering.ImportOptions options) => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Tags { public sealed class PlcTagTableGroup { } }
namespace Siemens.Engineering.SW.ExternalSources { public sealed class PlcExternalSourceGroup { } }
namespace Siemens.Engineering.SW.Units
{
    public sealed class PlcUnit : Siemens.Engineering.CompileServiceOwner
    {
        public string Name { get; set; } = "";
        public Siemens.Engineering.SW.Blocks.PlcBlockSystemGroup BlockGroup { get; } = new();
        public Siemens.Engineering.SW.Types.PlcTypeGroup TypeGroup { get; } = new();
        public Siemens.Engineering.SW.Tags.PlcTagTableGroup TagTableGroup { get; } = new();
        public Siemens.Engineering.SW.ExternalSources.PlcExternalSourceGroup ExternalSourceGroup { get; } = new();
    }
    public sealed class PlcUnitComposition : List<PlcUnit>
    {
        public Func<string, PlcUnit?>? Finding { get; set; }
        public PlcUnit? Find(string name) => Finding == null ? this.SingleOrDefault(u => u.Name == name) : Finding(name);
    }
    public sealed class PlcUnitSystemGroup { public PlcUnitComposition Units { get; } = new(); }
    public sealed class PlcUnitProvider { public PlcUnitSystemGroup UnitGroup { get; } = new(); }
}
namespace Siemens.Engineering.SW
{
    public sealed partial class PlcSoftware
    {
        public Blocks.PlcBlockSystemGroup BlockGroup { get; } = new();
        public Types.PlcTypeGroup TypeGroup { get; } = new();
        public Tags.PlcTagTableGroup TagTableGroup { get; } = new();
        public ExternalSources.PlcExternalSourceGroup ExternalSourceGroup { get; } = new();
    }
}
namespace TiaMcp.Adapters.Native.Plc
{
    internal static partial class PlcBlockPrimitives
    {
        public static Siemens.Engineering.Compiler.ICompilable? Compiler(Siemens.Engineering.IEngineeringServiceProvider owner)
            => owner.GetService<Siemens.Engineering.Compiler.ICompilable>();
        public static Siemens.Engineering.SW.Blocks.PlcBlock? Find(Siemens.Engineering.SW.Blocks.PlcBlockComposition blocks, string name)
            => blocks.Find(name);
    }
    // Unrelated import policy surface needed by the linked unit resolver; no compile route calls these methods.
    internal static class PlcImportOptionPolicy
    {
        internal const string UnsupportedCondition = "unsupported";
        internal static string? Unsupported(string release, string kind, string[]? sw, string[]? cultures, string unit) => null;
        internal static T Combine<T>(T value, string[]? cultures) => value;
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal List<string> CompileEvents { get; } = new();
        internal bool TargetOnline { get; set; }
        internal bool ProjectOnline { get; set; }
        internal Siemens.Engineering.HW.DeviceItem CompileDevice { get; } = new();
    }
}
