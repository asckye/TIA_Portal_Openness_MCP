using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcpServer.Runtime;
using static TiaMcp.Logic.V4.Domain.DomainShape;

using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeRequest : DomainDto
    {
        internal OpenPipeRequest(JsonElement json) : base(json) { }
        public string Message => Required<string>("message");
        public string? ClientCookie => Optional<string?>("clientCookie", null);
        public bool HasParams => Json.TryGetProperty("params", out _);
        public OpenPipeParams Params => Message switch
        {
            "ReadTag" => Required<OpenPipeReadTagParams>("params"),
            "WriteTag" => Required<OpenPipeWriteTagParams>("params"),
            "ReadAlarm" => Required<OpenPipeReadAlarmParams>("params"),
            "BrowseTags" => Required<OpenPipeBrowseTagsParams>("params"),
            _ => HasParams ? Required<OpenPipeExpertParams>("params") : V4Json.Deserialize<OpenPipeExpertParams>("{}")
        };
        public bool IsReadOnly => RuntimeChannelsLogic.IsReadOnlyOpenPipeMessage(Message);

        public string ToWire(string generatedCookie)
        {
            DomainValidation.Text(generatedCookie);
            var parameters = new JsonObject();
            bool expert = Params is OpenPipeExpertParams;
            if (HasParams) foreach (var field in Json.GetProperty("params").EnumerateObject())
            {
                string key = expert ? field.Name : char.ToUpperInvariant(field.Name[0]) + field.Name.Substring(1);
                parameters[key] = JsonNode.Parse(field.Value.GetRawText());
            }
            if (Message == "WriteTag")
            {
                var tags = new JsonArray();
                foreach (var tag in ((OpenPipeWriteTagParams)Params).Tags)
                    tags.Add(new JsonObject { ["Name"] = tag.Name, ["Value"] = JsonNode.Parse(V4Json.Serialize(tag.Value)) });
                parameters["Tags"] = tags;
            }
            var request = new JsonObject { ["Message"] = Message };
            if (HasParams) request["Params"] = parameters;
            request["ClientCookie"] = string.IsNullOrWhiteSpace(ClientCookie) ? generatedCookie : ClientCookie;
            string wire = RuntimeChannelsLogic.PrepareRawRequest(RuntimeChannelsLogic.ToWireJson(request)).line;
            var validation = OpenPipeLimits.Wire.Read(wire, "request");
            if (validation.Error != null) throw new JsonException(V4Json.Serialize(validation.Error));
            return wire;
        }
    }
    internal abstract class OpenPipeParams : DomainDto { internal OpenPipeParams(JsonElement json) : base(json) { } }
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeExpertParams : OpenPipeParams
    {
        internal OpenPipeExpertParams(JsonElement json) : base(json) { }
        public AttributeMap<NativeValue> Values => V4Json.Deserialize<AttributeMap<NativeValue>>(Json.GetRawText());
    }
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeReadTagParams : OpenPipeParams
    {
        internal OpenPipeReadTagParams(JsonElement json) : base(json) { }
        public IReadOnlyList<string> Tags => Items<string>("tags");
    }
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeWriteTagParams : OpenPipeParams
    {
        internal OpenPipeWriteTagParams(JsonElement json) : base(json) { }
        public IReadOnlyList<OpenPipeTagWrite> Tags => Items<OpenPipeTagWrite>("tags");
    }
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeTagWrite : DomainDto
    {
        internal OpenPipeTagWrite(JsonElement json) : base(json) { }
        public string Name => Required<string>("name");
        public Scalar Value => Required<Scalar>("value");
    }
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeReadAlarmParams : OpenPipeParams
    {
        internal OpenPipeReadAlarmParams(JsonElement json) : base(json) { }
        public IReadOnlyList<string> SystemNames => Items<string>("systemNames");
        public string Filter => Required<string>("filter");
        public int LanguageId => Required<int>("languageId");
    }
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class OpenPipeBrowseTagsParams : OpenPipeParams
    {
        internal OpenPipeBrowseTagsParams(JsonElement json) : base(json) { }
        public string? Filter => Optional<string?>("filter", null);
        public int? PageSize => Optional<int?>("pageSize", null);
    }
    internal static partial class DomainSchemas
    {
        private static void AddOpenPipeShapes(Dictionary<Type, InputSchema> d)
        {
            // RuntimeChannelsLogic.cs:287-326 and RuntimeChannelTools.cs:568 evidence
            // the typed messages. Other names retain PrepareRawRequest's expert path.
            d[typeof(OpenPipeReadTagParams)] = Object(("tags", Array(String())));
            d[typeof(OpenPipeTagWrite)] = Object(("name", String()), ("value", Scalar));
            d[typeof(OpenPipeWriteTagParams)] = Object(("tags", Array(d[typeof(OpenPipeTagWrite)])));
            d[typeof(OpenPipeReadAlarmParams)] = Object(("systemNames", Array(String())), ("filter", String()), ("languageId", Integer()));
            d[typeof(OpenPipeBrowseTagsParams)] = Object(("filter?", String()), ("pageSize?", Integer()));
            var messages = new[] { ("ReadTag", typeof(OpenPipeReadTagParams)), ("WriteTag", typeof(OpenPipeWriteTagParams)),
                ("ReadAlarm", typeof(OpenPipeReadAlarmParams)), ("BrowseTags", typeof(OpenPipeBrowseTagsParams)) };
            d[typeof(NativeValue)] = new InputSchema(OpenPipeLimits.Values.Schema);
            var expertParams = new JsonObject { ["type"] = "object", ["additionalProperties"] = OpenPipeLimits.ValueSchema(), ["minProperties"] = 0 };
            d[typeof(OpenPipeExpertParams)] = OpenPipeLimits.Schema((JsonObject)expertParams.DeepClone(), OpenPipeLimits.MaxDepth - 1);
            var expertName = JsonNode.Parse(V4Json.Serialize(String(1).Json))!.AsObject();
            expertName["not"] = new JsonObject { ["enum"] = new JsonArray(messages.Select(m => (JsonNode)JsonValue.Create(m.Item1)!).ToArray()) };
            var alternatives = new JsonArray(messages.Select(m => JsonNode.Parse(V4Json.Serialize(Object(
                ("message", String(0, null, m.Item1)), ("params", d[m.Item2]), ("clientCookie?", String())).Json))).ToArray());
            var expertRequest = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("message"), ["properties"] = new JsonObject {
                    ["message"] = expertName.DeepClone(), ["params"] = expertParams, ["clientCookie"] = JsonNode.Parse(V4Json.Serialize(String().Json)) } };
            // Streaming names must reach the shared pre-construction callback even
            // with malformed Params; ordinary expert requests retain their closed shape.
            alternatives.Add(new JsonObject { ["type"] = "object", ["required"] = new JsonArray("message"),
                ["properties"] = new JsonObject { ["message"] = expertName,
                    ["params"] = new JsonObject { ["additionalProperties"] = OpenPipeLimits.ValueSchema() } },
                ["if"] = new JsonObject { ["properties"] = new JsonObject { ["message"] = new JsonObject {
                    ["pattern"] = "^([sS][uU][bB][sS][cC][rR][iI][bB][eE]|[uU][nN][sS][uU][bB][sS][cC][rR][iI][bB][eE])" } } },
                ["then"] = true, ["else"] = expertRequest });
            d[typeof(OpenPipeRequest)] = OpenPipeLimits.Schema(new JsonObject { ["oneOf"] = alternatives }, OpenPipeLimits.MaxDepth);
        }
    }
    internal static partial class DomainValidation
    {
        public static void OpenPipe(OpenPipeRequest request, string releaseKey, bool channelAvailable, bool readOnly)
        {
            Capability(releaseKey, channelAvailable, "OpenPipe", readOnly, !request.IsReadOnly);
            _ = request.Params;
        }
    }
}
