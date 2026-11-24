using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

// SDK-free shapes for the linked native callers. Only the safety login surface is exercised.
namespace Siemens.Engineering
{
    public interface IEngineeringObject { public object GetAttribute(string name); public void SetAttribute(string name, object value); }
    public interface IEngineeringServiceProvider { public T? GetService<T>() where T : class; }
    public class EngineeringObject : IEngineeringObject, IEngineeringServiceProvider
    {
        public object? Parent { get; set; }
        public virtual T? GetService<T>() where T : class => throw new NotSupportedException();
        public object GetAttribute(string name) => throw new NotSupportedException();
        public void SetAttribute(string name, object value) => throw new NotSupportedException();
    }
    public class NonRecoverableException : Exception { }
}
namespace Siemens.Engineering.HW
{
    public sealed class DeviceItem : EngineeringObject
    {
        public string Name => "CPU";
        public string TypeIdentifier => "F-CPU";
        internal Safety.SafetyAdministration Administration = null!;
        public override T? GetService<T>() where T : class => Administration as T;
    }
}
namespace Siemens.Engineering.HW.Features
{
    public sealed class SoftwareContainer : EngineeringObject
    {
        public SW.PlcSoftware Software { get; } = new SW.PlcSoftware();
    }
}
namespace Siemens.Engineering.SW
{
    public sealed class PlcSoftware : EngineeringObject, Compiler.ICompilable
    {
        public string Name => "CPU";
        public Blocks.PlcBlockGroup BlockGroup { get; } = new Blocks.PlcBlockGroup();
        public override T? GetService<T>() where T : class => this as T;
        internal int CompileCalls;
        public Compiler.CompilerResult Compile() { CompileCalls++; throw new InvalidOperationException("fixture compiler failure"); }
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    public class PlcBlock : EngineeringObject
    {
        public string Name { get; set; } = "";
        public int Number => 0;
        public string ProgrammingLanguage => "";
        public bool IsKnowHowProtected => false;
        public override T? GetService<T>() where T : class => null;
    }
    public class FC : PlcBlock { }
    public class FB : PlcBlock { }
    public class InstanceDB : PlcBlock { public string InstanceOfName => ""; }
    public class PlcBlockUserGroup : EngineeringObject { public string Name => ""; }
    public class PlcBlockGroup
    {
        public PlcBlockGroups Groups { get; } = new PlcBlockGroups();
        public List<PlcBlock> Blocks { get; } = new List<PlcBlock>();
        public string Name { get; set; } = "";
    }
    public class PlcBlockGroups : List<PlcBlockGroup> { public PlcBlockGroup? Find(string name) => Find(group => group.Name == name); }
}
namespace Siemens.Engineering.Compiler
{
    public interface ICompilable { public CompilerResult Compile(); }
    public enum CompilerResultState { Success }
    public class CompilerResult
    {
        public CompilerResultState State => CompilerResultState.Success;
        public int ErrorCount => 0;
        public int WarningCount => 0;
        public CompilerResultMessageComposition Messages => throw new NotSupportedException();
    }
    public class CompilerResultMessage : CompilerResult
    {
        public string Description => ""; public string Path => ""; public DateTime DateTime => default;
    }
    public class CompilerResultMessageComposition : List<CompilerResultMessage> { }
}
namespace Siemens.Engineering.Safety
{
    public sealed class SafetyAdministration
    {
        internal readonly List<string> Calls = new List<string>();
        internal Exception? LoginError;
        internal Action? OnReadback;
        internal Exception? LogoffError;
        internal bool LoggedOnAfterLogin;
        private bool attempted;
        public bool IsSafetyOfflineProgramPasswordSet => true;
        public bool IsLoggedOnToSafetyOfflineProgram
        {
            get
            {
                Calls.Add(attempted ? "readback" : "precheck");
                if (attempted) OnReadback?.Invoke();
                return attempted && LoggedOnAfterLogin;
            }
        }
        public void LoginToSafetyOfflineProgram(SecureString password)
        {
            Calls.Add("login");
            attempted = true;
            if (LoginError != null) throw LoginError;
        }
        public void LogoffFromSafetyOfflineProgram()
        {
            Calls.Add("logoff");
            if (LogoffError != null) throw LogoffError;
            LoggedOnAfterLogin = false;
        }
        public RuntimeGroupComposition RuntimeGroups { get; } = new RuntimeGroupComposition();
        public SafetySettings Settings => throw new NotSupportedException();
        public void SetSafetyOfflineProgramPassword(SecureString password) => throw new NotSupportedException();
        public void RevokeSafetyOfflineProgramPassword(SecureString password) => throw new NotSupportedException();
    }
    public class RuntimeGroup : EngineeringObject { }
    public class RuntimeGroupComposition : List<RuntimeGroup> { public RuntimeGroup? Find(string name) => throw new NotSupportedException(); }
    public class SafetySignature { public string Type => ""; public uint Value => 0; }
    public class SafetySignatureComposition : List<SafetySignature> { }
    public class SafetySignatureProvider { public SafetySignatureComposition Signatures => throw new NotSupportedException(); }
    public class SafetySettings : EngineeringObject
    {
        public AssignmentOfBlockNumbers? AssignmentOfBlockNumbers => null;
        public SafetySystemVersion? SafetySystemVersion { get; set; }
        public IEnumerable<SafetySystemVersion> GetApplicableSafetySystemVersions() => throw new NotSupportedException();
        public void CleanSystemGeneratedObjects() => throw new NotSupportedException();
    }
    public class SafetySystemVersion { public string Value => ""; }
    public class AssignmentOfBlockNumbers { public int ManagementMode, FromFB, ToFB, FromFC, ToFC, FromDB, ToDB; }
    public class GlobalSettings
    {
        public void SafetyModificationsPossible(bool value) => throw new NotSupportedException();
        public void UsernameForFChangeHistory(string value) => throw new NotSupportedException();
    }
    public enum SafetyPrintoutFilePrinter { MicrosoftPrintToPdf }
    public enum SafetyPrintoutOption { All }
    public class SafetyPrintout
    {
        public bool Print(SafetyPrintoutFilePrinter printer, FileInfo file, string layout, SafetyPrintoutOption option) => throw new NotSupportedException();
    }
}
