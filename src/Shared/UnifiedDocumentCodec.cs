using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;
namespace TiaMcpServer.Siemens.Services
{
    internal static class UnifiedDocumentCodec
    {
        private static string Segment(string value) => Uri.EscapeDataString(value);
        private static Dictionary<string, object?> Failure(string path, string code, Exception? ex = null, string? reason = null)
            => new Dictionary<string, object?> { ["path"] = path, ["kind"] = "gap", ["status"] = code == "Unsupported" ? "unsupported" : "failed", ["code"] = code,
                ["reason"] = reason ?? (ex == null ? code : ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message) };
        private static Dictionary<string, object?> Scalar(string path, object? value, string kind)
            => new Dictionary<string, object?> { ["path"] = path, ["kind"] = kind, ["status"] = "ok", ["type"] = value?.GetType().FullName, ["value"] = value, ["evidence"] = path };
        internal static IEnumerable<Dictionary<string, object?>> Document(string text, string path, string extension)
        {
            object? doc = null; Dictionary<string, object?>? error = null;
            try
            {
                if (extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
                { var stream = new YamlStream(); stream.Load(new StringReader(text)); doc = stream; }
                else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                { using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }); doc = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace); }
                else error = UnifiedDocumentCodec.Failure(path, "Unsupported", reason: "Native file format is retained as raw text but has no structured reader: " + extension);
            }
            catch (Exception ex) { error = UnifiedDocumentCodec.Failure(path, "NativeParseFailed", ex); }
            if (error != null) { yield return error; yield break; }
            if (doc is YamlStream yaml) for (int i = 0; i < yaml.Documents.Count; i++)
                foreach (var row in Yaml(yaml.Documents[i].RootNode, path + "/documents/" + i, new HashSet<YamlNode>())) yield return row;
            if (doc is XDocument xml && xml.Root != null) foreach (var row in Xml(xml.Root, path + "/" + xml.Root.Name)) yield return row;
        }
        private static IEnumerable<Dictionary<string, object?>> Yaml(YamlNode node, string path, HashSet<YamlNode> ancestors)
        {
            if (ancestors.Count >= 128 || !ancestors.Add(node)) { yield return UnifiedDocumentCodec.Failure(path, "NativeCycleOrDepthLimit"); yield break; }
            try
            {
                if (node is YamlScalarNode scalar)
                { var row = UnifiedDocumentCodec.Scalar(path, scalar.Value, "nativeValue"); row["line"] = node.Start.Line; row["column"] = node.Start.Column; row["nativeType"] = node.Tag.ToString(); yield return row; }
                else if (node is YamlMappingNode map) foreach (var pair in map.Children)
                    foreach (var row in Yaml(pair.Value, path + "/" + UnifiedDocumentCodec.Segment(((YamlScalarNode)pair.Key).Value ?? ""), ancestors)) yield return row;
                else if (node is YamlSequenceNode seq) for (int i = 0; i < seq.Children.Count; i++)
                    foreach (var row in Yaml(seq.Children[i], path + "/[" + i + "]", ancestors)) yield return row;
            }
            finally { ancestors.Remove(node); }
        }
        private static IEnumerable<Dictionary<string, object?>> Xml(XElement element, string path, int depth = 0)
        {
            if (depth >= 128) { yield return UnifiedDocumentCodec.Failure(path, "NativeDepthLimit"); yield break; }
            foreach (var a in element.Attributes()) yield return UnifiedDocumentCodec.Scalar(path + "/@" + a.Name, a.Value, "nativeValue");
            if (!element.HasElements) yield return UnifiedDocumentCodec.Scalar(path, element.Value, "nativeValue");
            int i = 0; foreach (var child in element.Elements()) foreach (var row in Xml(child, path + "/" + child.Name + "[" + i++ + "]", depth + 1)) yield return row;
        }
    }
}
