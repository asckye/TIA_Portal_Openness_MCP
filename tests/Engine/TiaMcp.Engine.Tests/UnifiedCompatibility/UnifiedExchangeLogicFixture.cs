using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedExchangeLogic
    {
        internal static string[] ExchangeActions => UnifiedPrimitiveCompatibility.Field<string[]>(null, "UnifiedExchangeLogic", "ExchangeActions");
        internal static string[] OpcUaAlarmActions => UnifiedPrimitiveCompatibility.Field<string[]>(null, "UnifiedExchangeLogic", "OpcUaAlarmActions");
        internal static string[] NativeExtensions => UnifiedPrimitiveCompatibility.Field<string[]>(null, "UnifiedExchangeLogic", "NativeExtensions");
        internal static DirectoryInfo ValidateDirectory(string directory, bool forExport)
            => UnifiedPrimitiveCompatibility.Call<DirectoryInfo>("UnifiedExchangeLogic", "ValidateDirectory", directory, forExport);
        internal static string ValidateFileName(string fileName)
            => UnifiedPrimitiveCompatibility.Call<string>("UnifiedExchangeLogic", "ValidateFileName", fileName);
        internal static string[] ParseExpectedNames(string json, int max = 500)
            => UnifiedPrimitiveCompatibility.Call<string[]>("UnifiedExchangeLogic", "ParseExpectedNames", json, max);
        internal static string ValidateXmlPath(string xmlPath)
            => UnifiedPrimitiveCompatibility.Call<string>("UnifiedExchangeLogic", "ValidateXmlPath", xmlPath);
        internal static JsonArray VerifyNativeFiles(object nativeResult, DirectoryInfo dir)
            => UnifiedPrimitiveCompatibility.Call<JsonArray>("UnifiedExchangeLogic", "VerifyNativeFiles", nativeResult, dir);
        internal static FileInfo? ResolveReportedFile(FileInfo reported, DirectoryInfo dir)
            => UnifiedPrimitiveCompatibility.Call<FileInfo?>("UnifiedExchangeLogic", "ResolveReportedFile", reported, dir);
        internal static JsonArray DirectoryListing(DirectoryInfo dir)
            => UnifiedPrimitiveCompatibility.Call<JsonArray>("UnifiedExchangeLogic", "DirectoryListing", dir);
        internal static JsonArray ListImportCandidates(DirectoryInfo dir, string[] extensions)
            => UnifiedPrimitiveCompatibility.Call<JsonArray>("UnifiedExchangeLogic", "ListImportCandidates", dir, extensions);
        internal static bool IsUnifiedTagCollection(object? value)
            => UnifiedPrimitiveCompatibility.Call<bool>("UnifiedExchangeLogic", "IsUnifiedTagCollection", value);
    }
}
