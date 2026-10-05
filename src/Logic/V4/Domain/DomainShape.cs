using System.Linq;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    // Domain schema notation: '?' denotes omission and discriminated branches use
    // exactly one alternative. Construction and validation belong to InputSchema.
    internal static class DomainShape
    {
        internal static InputSchema String(int min = 0, int? max = null, params string[] choices) =>
            InputSchema.String(max, min, choices.Length == 0 ? null : choices);
        internal static InputSchema Pattern(string pattern) => InputSchema.String(pattern: pattern);
        internal static InputSchema Integer(long min = int.MinValue, long max = int.MaxValue) => InputSchema.Integer(min, max);
        internal static InputSchema Number() => InputSchema.Number();
        internal static InputSchema Boolean(bool? constant = null) => constant.HasValue
            ? new InputSchema(V4Json.Data(new { type = "boolean", @const = constant.Value })!.Value) : InputSchema.Boolean();
        internal static InputSchema Null() => InputSchema.Null();
        internal static InputSchema Union(params InputSchema[] choices) =>
            new InputSchema(V4Json.Data(new { oneOf = choices.Select(c => c.Json).ToArray() })!.Value);
        internal static InputSchema Array(InputSchema item, int min = 0, int? max = null, bool unique = false) => InputSchema.Array(item, min, max, unique);
        internal static InputSchema Map(InputSchema item, int min = 0, int? max = null) => InputSchema.Map(item, min, max);
        internal static InputSchema Object(params (string Name, InputSchema Shape)[] fields) => InputSchema.Object(
            fields.ToDictionary(f => f.Name.TrimEnd('?'), f => f.Shape),
            fields.Where(f => !f.Name.EndsWith("?", System.StringComparison.Ordinal)).Select(f => f.Name).ToArray());
    }
}
