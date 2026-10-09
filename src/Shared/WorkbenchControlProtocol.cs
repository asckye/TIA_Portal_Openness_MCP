using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpenness.Shared
{
    internal enum WorkbenchControlOperation { DisplayPage, DisplayBlock, DisplayCall, DisplayLadder, DisplayAtlas, ReadState, ReadSelection, PrefillForm }
    internal enum WorkbenchPage { Overview, Blocks, VersionControl, Calls, Audit, Environment, Log }
    internal enum WorkbenchControlStatus { Done, Refused, Failed }
    internal enum WorkbenchControlError { ResourceUnavailable, AccessDenied, UnsupportedCapability, PreconditionFailed, IdentityMismatch, NotFound, TargetAmbiguous, Timeout, InternalError, InvalidArgument }
    internal enum WorkbenchPrefillForm { InspectionRules, BlockFilter, BlockSelection }
    internal enum WorkbenchPrefillMode { Set, Clear }
    internal enum WorkbenchSessionSource { WorkbenchBridge, SharedMcpSession }
    internal enum WorkbenchControlNotificationKind { StateChanged }
    internal enum WorkbenchRenderKind { Ladder, Atlas }

    [JsonConverter(typeof(WorkbenchControlRequestConverter))]
    internal sealed class WorkbenchControlRequest
    {
        public string Protocol { get; set; } = WorkbenchControlProtocol.Name;
        public int Version { get; set; } = WorkbenchControlProtocol.Version;
        public string RequestId { get; set; } = "";
        public DateTimeOffset DeadlineUtc { get; set; }
        public WorkbenchControlOrigin Origin { get; set; } = new WorkbenchControlOrigin();
        public WorkbenchControlOperation Operation { get; set; }
        public WorkbenchControlArguments Arguments { get; set; } = new WorkbenchReadStateArguments();
        internal int TimeoutSeconds => Operation == WorkbenchControlOperation.ReadState || Operation == WorkbenchControlOperation.ReadSelection ? 2 : 5;
        internal WorkbenchControlError? CheckDeadline(DateTimeOffset now)
            => DeadlineUtc <= now ? WorkbenchControlError.Timeout
                : DeadlineUtc > now.AddSeconds(TimeoutSeconds) ? WorkbenchControlError.InvalidArgument : (WorkbenchControlError?)null;
    }

    internal sealed class WorkbenchControlOrigin
    {
        [JsonRequired] public string Host { get; set; } = "foundation";
        [JsonRequired] public string ReleaseKey { get; set; } = "";
        [JsonRequired] public int HostProcessId { get; set; }
        [JsonRequired] public string McpSession { get; set; } = "";
        [JsonRequired] public string ClientName { get; set; } = "";
        [JsonRequired] public string? BoundProjectFile { get; set; }
    }
    internal abstract class WorkbenchControlArguments { }
    internal sealed class WorkbenchDisplayPageArguments : WorkbenchControlArguments
    { [JsonRequired] public WorkbenchPage Page { get; set; } }
    internal sealed class WorkbenchDisplayBlockArguments : WorkbenchControlArguments
    {
        [JsonRequired] public string SoftwarePath { get; set; } = "";
        [JsonRequired] public string BlockPath { get; set; } = "";
    }
    internal sealed class WorkbenchDisplayCallArguments : WorkbenchControlArguments
    { [JsonRequired] public string RequestId { get; set; } = ""; }
    internal sealed class WorkbenchDisplayLadderArguments : WorkbenchControlArguments
    {
        [JsonRequired] public string SoftwarePath { get; set; } = "";
        [JsonRequired] public string BlockPath { get; set; } = "";
        public string? RenderRequestId { get; set; }
        public WorkbenchRenderArtifact? Artifact { get; set; }
    }
    internal sealed class WorkbenchDisplayAtlasArguments : WorkbenchControlArguments
    {
        public string? RenderRequestId { get; set; }
        public WorkbenchRenderArtifact? Artifact { get; set; }
    }
    internal sealed class WorkbenchRenderArtifact
    {
        [JsonRequired] public string RequestId { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public string Sha256 { get; set; } = "";
        [JsonRequired] public WorkbenchRenderKind Kind { get; set; }
    }
    internal sealed class WorkbenchReadStateArguments : WorkbenchControlArguments { }
    internal sealed class WorkbenchReadSelectionArguments : WorkbenchControlArguments
    {
        public int Offset { get; set; }
        public int Limit { get; set; } = 100;
    }
    [JsonConverter(typeof(WorkbenchPrefillArgumentsConverter))]
    internal sealed class WorkbenchPrefillArguments : WorkbenchControlArguments
    {
        public WorkbenchPrefillForm Form { get; set; }
        public WorkbenchPrefillMode Mode { get; set; }
        public WorkbenchPrefillFields Fields { get; set; } = new WorkbenchInspectionRulesFields();
    }
    internal abstract class WorkbenchPrefillFields { }
    internal sealed class WorkbenchInspectionRulesFields : WorkbenchPrefillFields
    { public string? NamePattern { get; set; } }
    internal sealed class WorkbenchBlockFilterFields : WorkbenchPrefillFields
    { public string? Filter { get; set; } }
    internal sealed class WorkbenchBlockSelectionFields : WorkbenchPrefillFields
    {
        public string? SoftwarePath { get; set; }
        public string[]? BlockPaths { get; set; }
    }

    internal sealed class WorkbenchControlResponse
    {
        [JsonRequired] public string Protocol { get; set; } = WorkbenchControlProtocol.Name;
        [JsonRequired] public int Version { get; set; } = WorkbenchControlProtocol.Version;
        [JsonRequired] public string RequestId { get; set; } = "";
        [JsonRequired] public WorkbenchControlStatus Status { get; set; }
        [JsonRequired] public WorkbenchControlData? Data { get; set; }
        [JsonRequired] public WorkbenchControlRefusal? Refusal { get; set; }
        [JsonRequired] public WorkbenchControlInfo Workbench { get; set; } = new WorkbenchControlInfo();
    }
    internal sealed class WorkbenchControlInfo
    {
        [JsonRequired] public string Version { get; set; } = "";
        [JsonRequired] public int[] Contracts { get; set; } = new[] { 1 };
        public bool? ControlEnabled { get; set; }
    }
    internal sealed class WorkbenchControlRefusal
    {
        [JsonRequired] public WorkbenchControlError Code { get; set; }
        [JsonRequired] public string Condition { get; set; } = "";
        [JsonRequired] public string Target { get; set; } = "";
        [JsonRequired] public string[] Candidates { get; set; } = Array.Empty<string>();
        public string? Capability { get; set; }
        public string? Expected { get; set; }
        public string? Actual { get; set; }
        public string? Stage { get; set; }
        public string? DiagnosticId { get; set; }
        public string? Resource { get; set; }
    }
    // A closed result shape covers the eight operations; UI snapshots contain no native proxies.
    internal sealed class WorkbenchControlData
    {
        public WorkbenchPage? Page { get; set; }
        public WorkbenchPage? PreviousPage { get; set; }
        public WorkbenchDevice? Device { get; set; }
        public WorkbenchBlock? Block { get; set; }
        public bool? Focused { get; set; }
        public bool? Opened { get; set; }
        public string? NextStep { get; set; }
        public string? RequestId { get; set; }
        public string? Tool { get; set; }
        public string? Result { get; set; }
        public WorkbenchControlInfo? Workbench { get; set; }
        public string? Release { get; set; }
        public WorkbenchSession? Session { get; set; }
        public bool? Busy { get; set; }
        public WorkbenchPrefillState? Prefill { get; set; }
        public WorkbenchBlock? FocusedBlock { get; set; }
        public WorkbenchBlock[]? CheckedBlocks { get; set; }
        public WorkbenchCall? SelectedCall { get; set; }
        public WorkbenchPaging? Paging { get; set; }
        public WorkbenchPrefillForm? Form { get; set; }
        public string[]? Applied { get; set; }
        public bool? AwaitingConfirmation { get; set; }
    }
    internal sealed class WorkbenchDevice
    {
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public string DisplayName { get; set; } = "";
    }
    internal sealed class WorkbenchBlock
    {
        [JsonRequired] public string Path { get; set; } = "";
        public string? Name { get; set; }
        [JsonRequired] public string Kind { get; set; } = "";
        [JsonRequired] public int Number { get; set; }
    }
    internal sealed class WorkbenchSession
    {
        [JsonRequired] public WorkbenchSessionSource Source { get; set; }
        [JsonRequired] public bool Connected { get; set; }
        [JsonRequired] public WorkbenchProject? Project { get; set; }
        public string? SessionKey { get; set; }
    }
    internal sealed class WorkbenchProject
    {
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
    }
    [JsonConverter(typeof(WorkbenchPrefillStateConverter))]
    internal sealed class WorkbenchPrefillState
    {
        public WorkbenchPrefillForm Form { get; set; }
        public WorkbenchPrefillFields Fields { get; set; } = new WorkbenchInspectionRulesFields();
        [JsonRequired] public WorkbenchControlOrigin Origin { get; set; } = new WorkbenchControlOrigin();
    }
    internal sealed class WorkbenchCall
    {
        [JsonRequired] public string RequestId { get; set; } = "";
        [JsonRequired] public string Tool { get; set; } = "";
        [JsonRequired] public string Result { get; set; } = "";
    }
    internal sealed class WorkbenchPaging
    {
        [JsonRequired] public int Offset { get; set; }
        [JsonRequired] public int Limit { get; set; }
        [JsonRequired] public int Total { get; set; }
    }
    // Snapshot notification DTO only; v1's per-call transport does not subscribe or push it.
    internal sealed class WorkbenchControlNotification
    {
        [JsonRequired] public string Protocol { get; set; } = WorkbenchControlProtocol.Name;
        [JsonRequired] public int Version { get; set; } = WorkbenchControlProtocol.Version;
        [JsonRequired] public WorkbenchControlNotificationKind Notification { get; set; }
        [JsonRequired] public WorkbenchControlData State { get; set; } = new WorkbenchControlData();
    }

    internal static class WorkbenchControlProtocol
    {
        internal const string Name = "tiamcp.workbench-control";
        internal const int Version = 1;
        internal static readonly JsonSerializerOptions Json = CreateOptions();
        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchControlOperation>("display.page", "display.block", "display.call", "display.ladder", "display.atlas", "read.state", "read.selection", "prefill.form"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchPage>("overview", "blocks", "versionControl", "calls", "audit", "environment", "log"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchControlStatus>("done", "refused", "failed"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchControlError>("RESOURCE_UNAVAILABLE", "ACCESS_DENIED", "UNSUPPORTED_CAPABILITY", "PRECONDITION_FAILED", "IDENTITY_MISMATCH", "NOT_FOUND", "TARGET_AMBIGUOUS", "TIMEOUT", "INTERNAL_ERROR", "INVALID_ARGUMENT"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchPrefillForm>("inspectionRules", "blockFilter", "blockSelection"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchPrefillMode>("set", "clear"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchSessionSource>("workbench-bridge", "shared-mcp-session"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchControlNotificationKind>("stateChanged"));
            options.Converters.Add(new WorkbenchClosedEnum<WorkbenchRenderKind>("ladder", "atlas"));
            return options;
        }
        internal static Type ArgumentType(WorkbenchControlOperation operation)
        {
            switch (operation)
            {
                case WorkbenchControlOperation.DisplayPage: return typeof(WorkbenchDisplayPageArguments);
                case WorkbenchControlOperation.DisplayBlock: return typeof(WorkbenchDisplayBlockArguments);
                case WorkbenchControlOperation.DisplayCall: return typeof(WorkbenchDisplayCallArguments);
                case WorkbenchControlOperation.DisplayLadder: return typeof(WorkbenchDisplayLadderArguments);
                case WorkbenchControlOperation.DisplayAtlas: return typeof(WorkbenchDisplayAtlasArguments);
                case WorkbenchControlOperation.ReadState: return typeof(WorkbenchReadStateArguments);
                case WorkbenchControlOperation.ReadSelection: return typeof(WorkbenchReadSelectionArguments);
                case WorkbenchControlOperation.PrefillForm: return typeof(WorkbenchPrefillArguments);
                default: throw new InvalidDataException("Invalid control operation.");
            }
        }
        internal static Type FieldType(WorkbenchPrefillForm form)
        {
            switch (form)
            {
                case WorkbenchPrefillForm.InspectionRules: return typeof(WorkbenchInspectionRulesFields);
                case WorkbenchPrefillForm.BlockFilter: return typeof(WorkbenchBlockFilterFields);
                case WorkbenchPrefillForm.BlockSelection: return typeof(WorkbenchBlockSelectionFields);
                default: throw new InvalidDataException("Invalid prefill form.");
            }
        }
        internal static bool IsHex(string? text, int length) => text != null && text.Length == length && text.All(c => "0123456789abcdef".Contains(c));
        internal static void Require(bool condition)
        { if (!condition) throw new InvalidDataException("Invalid workbench control frame."); }
        internal static void Validate(object frame)
        {
            if (frame is WorkbenchControlRequest request)
            {
                Require(request.Protocol == Name && request.Version > 0 && IsHex(request.RequestId, 32)
                    && request.DeadlineUtc.Offset == TimeSpan.Zero && request.DeadlineUtc != default
                    && request.Origin != null && request.Arguments != null && request.Arguments.GetType() == ArgumentType(request.Operation));
                ValidateOrigin(request.Origin!);
                if (request.Arguments is WorkbenchDisplayBlockArguments block) Require(Text(block.SoftwarePath) && Text(block.BlockPath));
                if (request.Arguments is WorkbenchDisplayCallArguments call) Require(IsHex(call.RequestId, 32));
                if (request.Arguments is WorkbenchDisplayLadderArguments ladder)
                {
                    Require(Text(ladder.SoftwarePath) && Text(ladder.BlockPath));
                    ValidateArtifact(ladder.RenderRequestId, ladder.Artifact, WorkbenchRenderKind.Ladder);
                }
                if (request.Arguments is WorkbenchDisplayAtlasArguments atlas) ValidateArtifact(atlas.RenderRequestId, atlas.Artifact, WorkbenchRenderKind.Atlas);
                if (request.Arguments is WorkbenchReadSelectionArguments selection) Require(selection.Offset >= 0 && selection.Limit >= 1 && selection.Limit <= 256);
                if (request.Arguments is WorkbenchPrefillArguments prefill) ValidatePrefill(prefill);
            }
            else if (frame is WorkbenchControlResponse response)
            {
                Require(response.Protocol == Name && response.Version == Version && IsHex(response.RequestId, 32) && response.Workbench != null);
                ValidateInfo(response.Workbench!);
                Require(response.Status == WorkbenchControlStatus.Done ? response.Data != null && response.Refusal == null : response.Data == null && response.Refusal != null);
                if (response.Refusal != null) Require(response.Refusal.Condition != null && response.Refusal.Target != null
                    && response.Refusal.Candidates != null && response.Refusal.Candidates.Length <= 16 && response.Refusal.Candidates.All(Text));
                if (response.Data != null) ValidateData(response.Data);
            }
            else if (frame is WorkbenchControlNotification notification)
            {
                Require(notification.Protocol == Name && notification.Version == Version && notification.State != null);
                ValidateData(notification.State!);
            }
            else throw new InvalidDataException("Unknown workbench control frame type.");
        }
        private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value!.Length <= 4096;
        private static void ValidateOrigin(WorkbenchControlOrigin origin)
            => Require(origin.Host == "foundation" && new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Contains(origin.ReleaseKey)
                && origin.HostProcessId > 0 && IsHex(origin.McpSession, 16) && origin.ClientName != null && origin.ClientName.Length <= 256);
        private static void ValidateArtifact(string? id, WorkbenchRenderArtifact? artifact, WorkbenchRenderKind kind)
        {
            Require(id == null || IsHex(id, 32));
            Require(artifact == null || artifact.RequestId == id && Text(artifact.Path) && IsHex(artifact.Sha256, 64) && artifact.Kind == kind);
        }
        private static void ValidatePrefill(WorkbenchPrefillArguments prefill)
        {
            Require(prefill.Fields != null && prefill.Fields.GetType() == FieldType(prefill.Form));
            if (prefill.Fields is WorkbenchInspectionRulesFields rules) Require(rules.NamePattern == null || rules.NamePattern.Length <= 256);
            if (prefill.Fields is WorkbenchBlockFilterFields filter) Require(filter.Filter == null || filter.Filter.Length <= 256);
            if (prefill.Fields is WorkbenchBlockSelectionFields selection) Require((selection.SoftwarePath == null || Text(selection.SoftwarePath))
                && (selection.BlockPaths == null || selection.BlockPaths.Length <= 256 && selection.BlockPaths.All(Text)));
            bool clear = prefill.Mode == WorkbenchPrefillMode.Clear;
            if (prefill.Fields is WorkbenchInspectionRulesFields inspection) Require(clear ? inspection.NamePattern == null : inspection.NamePattern != null);
            if (prefill.Fields is WorkbenchBlockFilterFields blockFilter) Require(clear ? blockFilter.Filter == null : blockFilter.Filter != null);
            if (prefill.Fields is WorkbenchBlockSelectionFields blockSelection) Require(clear
                ? blockSelection.SoftwarePath == null && blockSelection.BlockPaths == null
                : blockSelection.SoftwarePath != null && blockSelection.BlockPaths != null);
            // Regex/path semantics and human confirmation are the control surface's responsibility.
        }
        private static void ValidateInfo(WorkbenchControlInfo info)
            => Require(Text(info.Version) && info.Contracts != null && info.Contracts.Length >= 1 && info.Contracts.Length <= 16
                && info.Contracts.All(v => v > 0) && info.Contracts.Distinct().Count() == info.Contracts.Length);
        private static void ValidateBlock(WorkbenchBlock block) => Require(Text(block.Path) && Text(block.Kind) && block.Number >= 0);
        private static void ValidateData(WorkbenchControlData data)
        {
            if (data.Workbench != null) ValidateInfo(data.Workbench);
            if (data.Device != null) Require(Text(data.Device.Id) && Text(data.Device.DisplayName));
            if (data.Block != null) ValidateBlock(data.Block);
            if (data.FocusedBlock != null) ValidateBlock(data.FocusedBlock);
            if (data.CheckedBlocks != null)
            {
                Require(data.CheckedBlocks.Length <= 256 && data.CheckedBlocks.All(b => b != null));
                foreach (var block in data.CheckedBlocks) ValidateBlock(block);
            }
            if (data.SelectedCall != null) Require(IsHex(data.SelectedCall.RequestId, 32) && Text(data.SelectedCall.Tool) && Text(data.SelectedCall.Result));
            if (data.Paging != null) Require(data.Paging.Offset >= 0 && data.Paging.Limit >= 1 && data.Paging.Limit <= 256 && data.Paging.Total >= 0);
            if (data.Session?.Project != null) Require(Text(data.Session.Project.Name) && Text(data.Session.Project.Path));
            if (data.Prefill != null)
            {
                Require(data.Prefill.Fields != null && data.Prefill.Origin != null);
                ValidatePrefill(new WorkbenchPrefillArguments { Form = data.Prefill.Form, Fields = data.Prefill.Fields! });
                ValidateOrigin(data.Prefill.Origin!);
            }
        }
        internal static WorkbenchControlResponse? Negotiate(WorkbenchControlRequest request, string workbenchVersion)
        {
            Validate(request);
            if (request.Version == Version) return null;
            return new WorkbenchControlResponse { RequestId = request.RequestId, Status = WorkbenchControlStatus.Refused,
                Refusal = new WorkbenchControlRefusal { Code = WorkbenchControlError.UnsupportedCapability,
                    Condition = "workbench-contract-unsupported", Target = "workbench-control", Capability = "workbench-control.v" + request.Version },
                Workbench = new WorkbenchControlInfo { Version = workbenchVersion, Contracts = new[] { Version } } };
        }
    }
    internal sealed class WorkbenchClosedEnum<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly T[] values = (T[])Enum.GetValues(typeof(T));
        private readonly string[] names;
        internal WorkbenchClosedEnum(params string[] names) { this.names = names; }
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected a closed control enum.");
            int index = Array.IndexOf(names, reader.GetString());
            return index >= 0 ? values[index] : throw new JsonException("Unknown control enum value.");
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            int index = Array.IndexOf(values, value);
            if (index < 0) throw new JsonException("Unknown control enum value.");
            writer.WriteStringValue(names[index]);
        }
    }
    internal sealed class WorkbenchControlRequestConverter : JsonConverter<WorkbenchControlRequest>
    {
        internal sealed class Wire
        {
            [JsonRequired] public string Protocol { get; set; } = "";
            [JsonRequired] public int Version { get; set; }
            [JsonRequired] public string RequestId { get; set; } = "";
            [JsonRequired] public DateTimeOffset DeadlineUtc { get; set; }
            [JsonRequired] public WorkbenchControlOrigin Origin { get; set; } = new WorkbenchControlOrigin();
            [JsonRequired] public WorkbenchControlOperation Operation { get; set; }
            [JsonRequired] public JsonElement Arguments { get; set; }
        }
        public override WorkbenchControlRequest Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var wire = JsonSerializer.Deserialize<Wire>(ref reader, options)!;
            return new WorkbenchControlRequest { Protocol = wire.Protocol, Version = wire.Version, RequestId = wire.RequestId,
                DeadlineUtc = wire.DeadlineUtc, Origin = wire.Origin, Operation = wire.Operation,
                Arguments = (WorkbenchControlArguments)JsonSerializer.Deserialize(wire.Arguments.GetRawText(), WorkbenchControlProtocol.ArgumentType(wire.Operation), options)! };
        }
        public override void Write(Utf8JsonWriter writer, WorkbenchControlRequest value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteString("protocol", value.Protocol); writer.WriteNumber("version", value.Version);
            writer.WriteString("requestId", value.RequestId); writer.WriteString("deadlineUtc", value.DeadlineUtc);
            writer.WritePropertyName("origin"); JsonSerializer.Serialize(writer, value.Origin, options);
            writer.WritePropertyName("operation"); JsonSerializer.Serialize(writer, value.Operation, options);
            writer.WritePropertyName("arguments"); JsonSerializer.Serialize(writer, value.Arguments, WorkbenchControlProtocol.ArgumentType(value.Operation), options);
            writer.WriteEndObject();
        }
    }
    internal sealed class WorkbenchPrefillArgumentsConverter : JsonConverter<WorkbenchPrefillArguments>
    {
        internal sealed class Wire
        {
            [JsonRequired] public WorkbenchPrefillForm Form { get; set; }
            [JsonRequired] public WorkbenchPrefillMode Mode { get; set; }
            [JsonRequired] public JsonElement Fields { get; set; }
        }
        public override WorkbenchPrefillArguments Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var wire = JsonSerializer.Deserialize<Wire>(ref reader, options)!;
            return new WorkbenchPrefillArguments { Form = wire.Form, Mode = wire.Mode,
                Fields = (WorkbenchPrefillFields)JsonSerializer.Deserialize(wire.Fields.GetRawText(), WorkbenchControlProtocol.FieldType(wire.Form), options)! };
        }
        public override void Write(Utf8JsonWriter writer, WorkbenchPrefillArguments value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WritePropertyName("form"); JsonSerializer.Serialize(writer, value.Form, options);
            writer.WritePropertyName("mode"); JsonSerializer.Serialize(writer, value.Mode, options);
            writer.WritePropertyName("fields"); JsonSerializer.Serialize(writer, value.Fields, WorkbenchControlProtocol.FieldType(value.Form), options);
            writer.WriteEndObject();
        }
    }
    internal sealed class WorkbenchPrefillStateConverter : JsonConverter<WorkbenchPrefillState>
    {
        internal sealed class Wire
        {
            [JsonRequired] public WorkbenchPrefillForm Form { get; set; }
            [JsonRequired] public JsonElement Fields { get; set; }
            [JsonRequired] public WorkbenchControlOrigin Origin { get; set; } = new WorkbenchControlOrigin();
        }
        public override WorkbenchPrefillState Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var wire = JsonSerializer.Deserialize<Wire>(ref reader, options)!;
            return new WorkbenchPrefillState { Form = wire.Form, Origin = wire.Origin,
                Fields = (WorkbenchPrefillFields)JsonSerializer.Deserialize(wire.Fields.GetRawText(), WorkbenchControlProtocol.FieldType(wire.Form), options)! };
        }
        public override void Write(Utf8JsonWriter writer, WorkbenchPrefillState value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WritePropertyName("form"); JsonSerializer.Serialize(writer, value.Form, options);
            writer.WritePropertyName("fields"); JsonSerializer.Serialize(writer, value.Fields, WorkbenchControlProtocol.FieldType(value.Form), options);
            writer.WritePropertyName("origin"); JsonSerializer.Serialize(writer, value.Origin, options); writer.WriteEndObject();
        }
    }
}
