using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Hmi
{
    // Family-local boundary; V4Json remains the only public serializer.
    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public abstract class HmiObject
    {
        internal HmiObject() { }
        internal virtual void Validate() { }
        public JsonObject ToBuilderInput() => HmiBuilderAdapter.Convert(this);
        public JsonObject GetSchema() => HmiSchemas.For(GetType());
    }

    public static class HmiInputs
    {
        public static InputContract<T> Contract<T>() where T : HmiObject => new InputContract<T>(
            HmiSchemas.Contract(typeof(T)), new InputBudget(), validateInput: input =>
            {
                if (typeof(T) == typeof(DeviceAmlSpec)) HmiRules.AmlBudget(input);
            });
    }

    internal sealed class HmiJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => typeof(HmiObject).IsAssignableFrom(type);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type), true)!;

        private sealed class Converter<T> : JsonConverter<T> where T : HmiObject
        {
            public override bool HandleNull => true;

            public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                InputGuard.Require(root.ValueKind == JsonValueKind.Object);
                var union = HmiContracts.Union(type);
                if (union != null)
                {
                    InputGuard.Require(root.TryGetProperty("type", out var tag) && tag.ValueKind == JsonValueKind.String
                        && union.ContainsKey(tag.GetString()!), union.Keys.ToArray());
                    type = union[tag.GetString()!];
                }
                var constructor = HmiContracts.Constructor(type);
                var parameters = constructor.GetParameters();
                string? control = HmiContracts.ControlName(type);
                var required = parameters.Where(p => !p.HasDefaultValue).Select(p => p.Name!).ToList();
                if (control != null) required.Add("type");
                InputGuard.Closed(root, required.ToArray(), parameters.Where(p => p.HasDefaultValue).Select(p => p.Name!).ToArray());
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name == "type" && control != null)
                        InputGuard.Require(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == control);
                    InputGuard.Require(property.Value.ValueKind != JsonValueKind.Null);
                }
                var arguments = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (root.TryGetProperty(parameter.Name!, out var value))
                        arguments[i] = value.Deserialize(HmiContracts.InputType(parameter.ParameterType), options);
                    else arguments[i] = parameter.DefaultValue;
                }
                try
                {
                    var result = (T)constructor.Invoke(arguments);
                    result.Validate();
                    return result;
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (value == null) throw new JsonException("An HMI input cannot be null.");
                value.Validate();
                writer.WriteStartObject();
                var type = value.GetType();
                string? control = HmiContracts.ControlName(type);
                if (control != null) writer.WriteString("type", control);
                foreach (var parameter in HmiContracts.Constructor(type).GetParameters())
                {
                    var field = HmiContracts.Property(type, parameter);
                    var fieldValue = field.GetValue(value);
                    if (fieldValue == null && parameter.HasDefaultValue) continue;
                    if (fieldValue == null) throw new JsonException("Missing HMI field: " + parameter.Name);
                    writer.WritePropertyName(parameter.Name!);
                    JsonSerializer.Serialize(writer, fieldValue, HmiContracts.InputType(parameter.ParameterType), options);
                }
                writer.WriteEndObject();
            }
        }
    }

    internal static class HmiContracts
    {
        internal static readonly IReadOnlyDictionary<string, Type> Classic = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Text"] = typeof(ClassicTextItem), ["Button"] = typeof(ClassicButtonItem),
            ["IOField"] = typeof(ClassicIoFieldItem), ["Rectangle"] = typeof(ClassicRectangleItem), ["Lamp"] = typeof(ClassicLampItem)
        };
        internal static readonly IReadOnlyDictionary<string, Type> Unified = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Text"] = typeof(UnifiedTextItem), ["Button"] = typeof(UnifiedButtonItem),
            ["IOField"] = typeof(UnifiedIoFieldItem), ["Rectangle"] = typeof(UnifiedRectangleItem)
        };
        internal static IReadOnlyDictionary<string, Type>? Union(Type type) => type == typeof(ClassicScreenItem) ? Classic
            : type == typeof(UnifiedScreenItem) ? Unified : null;
        internal static string? ControlName(Type type) => Classic.Concat(Unified).FirstOrDefault(p => p.Value == type).Key;
        internal static ConstructorInfo Constructor(Type type) => type.GetConstructors().Single();
        internal static PropertyInfo Property(Type type, ParameterInfo parameter) => type.GetProperty(
            char.ToUpperInvariant(parameter.Name![0]) + parameter.Name.Substring(1))!;

        // Public constructors accept ordinary dictionaries; wire maps use the shared
        // converter and immutable AttributeMap without changing the builder shape.
        internal static Type InputType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
            ? typeof(AttributeMap<>).MakeGenericType(type.GenericTypeArguments[1]) : type;
        internal static IReadOnlyList<T>? OptionalList<T>(IReadOnlyList<T>? values) => values == null ? null : V4Validation.List(values);
    }

    [JsonConverter(typeof(ClassicTextConverter))]
    public sealed class ClassicText
    {
        public string? Text { get; }
        public AttributeMap<string>? Languages { get; }
        public ClassicText(string text) { Text = text ?? throw new ArgumentNullException(nameof(text)); }
        public ClassicText(IReadOnlyDictionary<string, string> languages)
        {
            Languages = new AttributeMap<string>(languages);
            HmiRules.TextMap(Languages);
        }
    }

    internal sealed class ClassicTextConverter : JsonConverter<ClassicText>
    {
        public override ClassicText Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return new ClassicText(reader.GetString()!);
            return new ClassicText(JsonSerializer.Deserialize<AttributeMap<string>>(ref reader, options)
                ?? throw new JsonException("Classic text must be a string or a language dictionary."));
        }
        public override void Write(Utf8JsonWriter writer, ClassicText value, JsonSerializerOptions options)
        {
            if (value.Text != null) writer.WriteStringValue(value.Text);
            else JsonSerializer.Serialize(writer, value.Languages, options);
        }
    }
}
