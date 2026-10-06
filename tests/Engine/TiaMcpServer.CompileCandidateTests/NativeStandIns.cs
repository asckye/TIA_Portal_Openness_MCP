using System.Security;

// Managed stand-ins only; the test project has no Siemens references or native session.
namespace Siemens.Engineering
{
    public interface IEngineeringServiceProvider { T? GetService<T>() where T : class; }
}
namespace Siemens.Engineering.HW
{
    public class HardwareObject
    {
        public List<DeviceItem> DeviceItems { get; } = new();
    }
    public sealed class DeviceItem : HardwareObject, IEngineeringServiceProvider
    {
        public string Name { get; set; } = "CPU";
        public Dictionary<Type, object> Services { get; } = new();
        public T? GetService<T>() where T : class => Services.TryGetValue(typeof(T), out var service) ? (T)service : null;
    }
}
namespace Siemens.Engineering.HW.Features
{
    public sealed class SoftwareContainer { public object? Software { get; set; } }
}
namespace Siemens.Engineering.Online
{
    public enum OnlineState { Offline, Online }
    public sealed class OnlineProvider { public OnlineState State { get; set; } }
}
namespace Siemens.Engineering.SW
{
    public sealed class PlcSoftware { }
}
namespace Siemens.Engineering.Compiler
{
    public interface ICompilable { CompilerResult Compile(); }
    public enum CompilerResultState { Success, Warning, Error }
    public sealed class CompilerResult
    {
        public CompilerResultState State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public CompilerResultMessageComposition Messages { get; } = new();
    }
    public sealed class CompilerResultMessage
    {
        public CompilerResultState State { get; set; }
        public string? Path { get; set; }
        public string? Description { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public CompilerResultMessageComposition Messages { get; } = new();
    }
    public sealed class CompilerResultMessageComposition : List<CompilerResultMessage> { }
}
namespace Siemens.Engineering.Safety
{
    public sealed class SafetyAdministration
    {
        public bool IsLoggedOnToSafetyOfflineProgram { get; set; }
        public bool IsSafetyOfflineProgramPasswordSet { get; set; } = true;
        public Action? LoginAction, LogoutAction;
        public int LoginCalls, LogoutCalls;
        public bool LeaveLoggedOn;
        public void LoginToSafetyOfflineProgram(SecureString password) { LoginCalls++; IsLoggedOnToSafetyOfflineProgram = true; LoginAction?.Invoke(); }
        public void LogoffFromSafetyOfflineProgram() { LogoutCalls++; LogoutAction?.Invoke(); if (!LeaveLoggedOn) IsLoggedOnToSafetyOfflineProgram = false; }
    }
}
