using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TiaMcp.BuildCommon;
using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class PlcObjectPathResolverSources
{
    private static string Method(string path, string name)
    {
        string source = SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path));
        var method = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == name);
        return Regex.Replace(method.ToString(), @"\s+", " ");
    }

    [Fact]
    public void Identifiable_blocks_and_types_decode_object_paths_before_group_and_leaf_lookup()
    {
        string source = Method("src/Adapters/Native/Engineering/EngineeringServices.cs", "ExactIdentifiableObject");
        Assert.Contains("kind == \"plcBlock\" || kind == \"plcType\"", source, StringComparison.Ordinal);
        Assert.Contains("plcObject ? PlcObjectPath.Parts(objectPath, parameter: \"objectPath\")", source, StringComparison.Ordinal);
        Assert.Contains("PlcObjectPath.Format(parts.Take(parts.Length - 1))", source, StringComparison.Ordinal);
        Assert.Contains("pathParts: value => PlcObjectPath.Parts(value, true)", source, StringComparison.Ordinal);
        Assert.Contains("PlcGroupOperations.Find(collection, parts.Last())", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Adapters/Native/Plc/PlcSoftwareEditing.cs", "CreatePlcInstanceDb", "PLC_SOFTWARE_PROTECTION")]
    [InlineData("src/Adapters/Native/Plc/PlcB5Documents.cs", "ManagePlcBlockDocuments", "PLC_DOCUMENT_EXPORT")]
    public void Native_block_group_consumers_use_the_shared_decoded_group_parser(string path, string name, string feature)
    {
        string source = SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path));
        var method = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: new[] { feature }))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.ValueText == name);
        string body = Regex.Replace(method.ToString(), @"\s+", " ");
        Assert.Contains("groupPath, pathParts: value => PlcObjectPath.Parts(value, true)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Engineering_group_path_callback_uses_the_canonical_session_formatter()
    {
        string source = Method("src/Adapters/Native/Plc/PlcOrganisationWorker.cs", "GetPlcBlockGroupPath");
        Assert.Contains("return PlcObjectPath.Format(names);", source, StringComparison.Ordinal);
        string callbacks = SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory),
            "src/Adapters/Native/Engineering/WorkerFamilyCallbacks.cs"));
        Assert.Contains("EngineeringBlockGroupPath = group => session.GetPlcBlockGroupPath((PlcBlockGroup)group);", callbacks, StringComparison.Ordinal);
    }
}
