using System;
using System.Text.Json;
using System.Xml;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Construction
{
    // Use this boundary before construction work; Schema is the contract's own schema.
    // The element contract preserves omission and the original wire field order.
    // Foundation's legacy character limit excludes raw whitespace/escape overhead;
    // its decoded limit is checked below. Read(string) still uses V4Json.ParseInput
    // through InputContract, including the shared reader-depth error mapping.
    public sealed class ConstructionInput<T> where T : ConstructionNode
    {
        private readonly InputContract<JsonElement> contract;
        public JsonElement Schema => contract.Schema;
        public InputBudget Budget => contract.Budget;

        public ConstructionInput(ConstructionProfile profile = ConstructionProfile.FullEngine)
        {
            V4Validation.Defined(profile);
            contract = new InputContract<JsonElement>(ConstructionSchemas.For(typeof(T)), ConstructionJson.InputBudget, json =>
            {
                try
                {
                    ConstructionJson.XmlCharacters(json);
                    var value = ConstructionJson.Read(typeof(T), json);
                    if (profile == ConstructionProfile.Foundation)
                    {
                        if (typeof(T) == typeof(PlcArtifactSpec)) ValidateFoundationUnion(json);
                        else if (value is ConstructionSpec spec) FoundationConstructionValidation.Validate(spec);
                    }
                    return json;
                }
                catch (ArgumentException error) when (error.InnerException is InputRejection rejection) { throw rejection; }
                catch (XmlException) /* swallow(privacy): invalid XML characters must not echo the supplied string */
                { throw new InputRejection(); }
            });
        }

        public InputResult<T> Read(string? json, string parameter, bool optional = false) => Map(contract.Read(json, parameter, optional));
        public InputResult<T> Read(JsonElement json, string parameter, bool optional = false) => Map(contract.Read(json, parameter, optional));
        public InputResult<T> Validate(T value, string parameter) => Read(value == null ? V4Json.ParseInput("null") : value.Json, parameter);

        private static void ValidateFoundationUnion(JsonElement json)
        {
            InputRejection? budgetFailure = null;
            foreach (var member in PlcArtifactSpec.MemberTypes)
            {
                try
                {
                    ConstructionSchemas.For(member).Check(json);
                    FoundationConstructionValidation.Validate((ConstructionSpec)ConstructionJson.Read(member, json));
                    return;
                }
                catch (InputRejection rejection) { if (rejection.IsLimit) budgetFailure = rejection; }
                catch (ArgumentException error) when (error.InnerException is InputRejection rejection)
                { if (rejection.IsLimit) budgetFailure = rejection; }
            }
            throw budgetFailure ?? new InputRejection();
        }

        private static InputResult<T> Map(InputResult<JsonElement> result) => new InputResult<T>(result.Presence,
            result.IsValid && result.Presence == InputPresence.Value ? (T)ConstructionJson.Read(typeof(T), result.Value) : null, result.Error);
    }
}
