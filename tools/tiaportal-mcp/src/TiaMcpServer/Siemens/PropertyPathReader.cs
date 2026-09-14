using System;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class PropertyPathReader
    {
        internal sealed class Result
        {
            internal object? Value;
            internal string Status = "Value";
            internal string? Segment;
            internal string? ResolvedType;
            internal string? Error;
            internal bool Success => Status == "Value";
            internal JsonObject Metadata() => new JsonObject {
                ["success"] = Success, ["operationSuccess"] = Success, ["status"] = Status,
                ["failedSegment"] = Segment, ["resolvedType"] = ResolvedType, ["error"] = Error };
        }

        internal static Result Read(object? root, string path)
        {
            var result = new Result { Value = root, ResolvedType = root?.GetType().FullName };
            if (root == null) { result.Status = "ObjectNotFound"; return result; }
            var parts = (path ?? "").Split('.');
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part) || part.IndexOfAny(new[] { '[', ']', '/', '(', ')' }) >= 0)
                { result.Status = "UnsupportedPath"; result.Segment = part; result.Value = null; return result; }
            }
            foreach (var part in parts)
            {
                if (result.Value == null) { result.Status = "NullIntermediate"; result.Segment = part; return result; }
                var current = result.Value;
                result.ResolvedType = current.GetType().FullName;
                try
                {
                    var prop = current.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null) { result.Status = "PropertyNotFound"; result.Segment = part; result.Value = null; return result; }
                    if (prop.GetIndexParameters().Length != 0) { result.Status = "UnsupportedPath"; result.Segment = part; result.Value = null; return result; }
                    result.Value = prop.GetValue(current);
                }
                catch (Exception ex)
                {
                    result.Status = "ReadFailed"; result.Segment = part; result.Value = null;
                    result.Error = (ex.InnerException ?? ex).Message; return result;
                }
            }
            return result;
        }
    }
}
