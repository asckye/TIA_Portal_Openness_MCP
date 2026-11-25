using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using TiaMcp.Adapters.Contracts;

namespace Siemens.Engineering.SW.ExternalSources
{
    internal enum GenerateBlockOption { None }
    internal sealed class Source : EngineeringObject
    {
        internal string Name => "Motor.scl";
        internal Exception? Failure;
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcDocumentPrimitives
    {
        internal static PlcSoftware ExternalSourceGroup(PlcSoftware software) => software;
        internal static Source[] Sources(PlcSoftware software) => new[] { software.Source };
        internal static Source? Find(Source[] sources, string name) => sources.SingleOrDefault(source => source.Name == name);
        internal static string Name(Source source) => source.Name;
        internal static Source CreateFromFile(Source[] sources, string name, string path) => throw new NotSupportedException();
        internal static IList<IEngineeringObject> Generate(Source source, GenerateBlockOption option) => throw source.Failure!;
        internal static PlcBlockGroup BlockGroup(PlcSoftware software) => software.BlockGroup;
        internal static PlcBlockGroup TypeGroup(PlcSoftware software) => software.BlockGroup;
        internal static IEnumerable<PlcBlock> Blocks(PlcBlockGroup group) => group.Blocks;
        internal static string Name(PlcBlock block) => block.Name;
        internal static string Language(PlcBlock block) => block.ProgrammingLanguage;
        internal static bool IsConsistent(PlcBlock block) => true;
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly (bool IsLocalSession, int? ProcessId) lifecycle = (false, 123);
        private readonly SourceIdentity externalDeleteIdentities = new SourceIdentity();
        private sealed class SourceIdentity { internal string Get(Source source) => source.Name; }
        private static string Child(string path, string name) => path + "/" + name;
        private static IEnumerable<(string Path, PlcBlockGroup Value)> BlockGroups(PlcBlockGroup group) => new[] { ("", group) };
        private static IEnumerable<(string Path, PlcBlockGroup Value)> TypeGroups(PlcBlockGroup group) => Array.Empty<(string, PlcBlockGroup)>();
    }
    internal static class PlcLifecyclePolicy
    {
        internal static void RequireLocalSessionExecution(bool local, bool allowed) { if (local) throw new InvalidOperationException(); }
    }
    internal static class PlcExternalSourceImportPolicy
    {
        internal const int MaximumFileBytes = 4 * 1024 * 1024;
        internal static string ValidateFile(string path) => throw new NotSupportedException();
    }
}

namespace TiaOpenness.Shared
{
    internal static class NativeInputPolicy
    {
        internal static string FullPath(string path) => Path.GetFullPath(path);
        internal static Stream OpenRead(string path, string parameter) => throw new NotSupportedException();
    }
}
