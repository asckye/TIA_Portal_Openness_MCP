using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Hmi;

namespace TiaMcp.Logic.V4.Inputs
{
    // SDK-independent access to the very same contracts used by typed callers.
    // Tool-specific policies (allowed attributes/actions, release and target) remain
    // in the owning tool; this boundary checks the input type before invocation.
    internal sealed class TypedToolInput
    {
        internal JsonElement Schema { get; }
        private readonly Func<JsonElement, string, Error?> validate;
        private TypedToolInput(JsonElement schema, Func<JsonElement, string, Error?> validate)
        { Schema = schema; this.validate = validate; }
        internal Error? Validate(JsonElement value, string parameter) => validate(value, parameter);

        private static TypedToolInput From<T>(InputContract<T> contract) => new TypedToolInput(contract.Schema,
            (value, parameter) => contract.Read(value, parameter).Error);
        private static TypedToolInput Domain<T>() => From(DomainValidation.Contract<T>());
        private static TypedToolInput Hmi<T>() where T : HmiObject => From(HmiInputs.Contract<T>());
        private static TypedToolInput Construction<T>() where T : ConstructionNode
        {
            var contract = new ConstructionInput<T>();
            return new TypedToolInput(contract.Schema, (value, parameter) => contract.Read(value, parameter).Error);
        }
        private static TypedToolInput Generic(string method, Type type) => (TypedToolInput)typeof(TypedToolInput)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, null)!;

        internal static TypedToolInput? For(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (DomainSchemas.Has(type)) return Generic(nameof(Domain), type);
            if (typeof(ConstructionNode).IsAssignableFrom(type)) return Generic(nameof(Construction), type);
            if (typeof(HmiObject).IsAssignableFrom(type)) return Generic(nameof(Hmi), type);
            if (type == typeof(PropertyStep[])) return From(new InputContract<PropertyStep[]>(InputSchema.Array(
                new InputSchema(V4Json.ParseInput("{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"property\"],\"properties\":{\"property\":{\"type\":\"string\",\"minLength\":1},\"name\":{\"type\":\"string\",\"minLength\":1},\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}},\"not\":{\"required\":[\"name\",\"index\"]}}")), maximum: 24), new InputBudget(32768)));
            if (type == typeof(ParameterRef[])) return From(NumberListValidator.ParameterReferences());
            if (type == typeof(Scalar)) return From(new InputContract<Scalar>(InputSchema.Scalar(), new InputBudget()));
            if (type == typeof(ToolArguments)) return From(new InputContract<ToolArguments>(
                InputSchema.Map(new InputSchema(V4Json.ParseInput("{}"))), new InputBudget()));
            if (type == typeof(NativeValue)) return From(new InputContract<NativeValue>(NativeSchema(), new InputBudget()));
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AttributeMap<>))
                return Generic(nameof(Map), type.GetGenericArguments()[0]);
            if (type.IsArray && For(type.GetElementType()!) is TypedToolInput element)
                return Generic(nameof(Array), type.GetElementType()!);
            return null;
        }

        private static TypedToolInput Map<T>() => From(new InputContract<AttributeMap<T>>(CollectionSchema(typeof(T), false), new InputBudget()));
        private static TypedToolInput Array<T>() => From(new InputContract<T[]>(CollectionSchema(typeof(T), true), new InputBudget()));
        private static InputSchema CollectionSchema(Type type, bool array)
        {
            var value = JsonNode.Parse(ValueSchema(type).Json.GetRawText())!.AsObject();
            var definitions = value["$defs"]?.DeepClone();
            value.Remove("$defs");
            var schema = new JsonObject { ["type"] = array ? "array" : "object", [array ? "items" : "additionalProperties"] = value };
            if (definitions != null) schema["$defs"] = definitions;
            return new InputSchema(JsonSerializer.SerializeToElement(schema));
        }
        private static InputSchema ValueSchema(Type type)
        {
            if (For(type) is TypedToolInput input) return new InputSchema(input.Schema);
            if (type == typeof(string)) return InputSchema.String();
            if (type == typeof(bool)) return InputSchema.Boolean();
            if (type == typeof(int)) return InputSchema.Integer();
            throw new ArgumentException("No typed input contract for " + type.FullName);
        }
        private static InputSchema NativeSchema() => new InputSchema(V4Json.ParseInput(
            "{\"$ref\":\"#/$defs/value\",\"$defs\":{\"value\":{\"anyOf\":[{\"type\":[\"null\",\"boolean\",\"number\",\"string\"]},{\"type\":\"array\",\"items\":{\"$ref\":\"#/$defs/value\"}},{\"type\":\"object\",\"additionalProperties\":{\"$ref\":\"#/$defs/value\"}}]}}}"));
    }
}
