using System;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4
{
    public enum PagingMode { Offset, Cursor }
    public enum PagingUnit { Items, Utf16 }

    public sealed class Paging
    {
        [JsonPropertyOrder(0)] public PagingMode Mode { get; }
        [JsonPropertyOrder(1)] public int? Offset { get; }
        [JsonPropertyOrder(2)] public int Limit { get; }
        [JsonPropertyOrder(3)] public int? NextOffset { get; }
        [JsonPropertyOrder(4)] public string? Cursor { get; }
        [JsonPropertyOrder(5)] public string? NextCursor { get; }
        [JsonPropertyOrder(6)] public int? Total { get; }
        [JsonPropertyOrder(7)] public bool Complete { get; }
        [JsonPropertyOrder(8)] public PagingUnit Unit { get; }

        public Paging(PagingMode mode, int? offset, int limit, int? nextOffset, string? cursor,
            string? nextCursor, int? total, bool complete, PagingUnit unit = PagingUnit.Items)
        {
            Mode = mode;
            Offset = offset;
            Limit = limit;
            NextOffset = nextOffset;
            Cursor = cursor;
            NextCursor = nextCursor;
            Total = total;
            Complete = complete;
            Unit = unit;
            V4Validation.Paging(this);
        }
    }

    // Hosts associate an opaque cursor with this scope before looking up its snapshot.
    public sealed class CursorScope
    {
        [JsonPropertyOrder(0)] public string ReleaseKey { get; }
        [JsonPropertyOrder(1)] public string SessionId { get; }
        [JsonPropertyOrder(2)] public long BindingEpoch { get; }
        [JsonPropertyOrder(3)] public string QueryHash { get; }
        [JsonPropertyOrder(4)] public string SnapshotId { get; }

        public CursorScope(string releaseKey, string sessionId, long bindingEpoch, string queryHash, string snapshotId)
        {
            V4Validation.Release(releaseKey);
            V4Validation.Text(releaseKey, nameof(releaseKey));
            V4Validation.Text(sessionId, nameof(sessionId));
            V4Validation.Hash(queryHash);
            V4Validation.Text(snapshotId, nameof(snapshotId));
            V4Validation.Require(bindingEpoch >= 0, "Binding epoch must be nonnegative.");
            ReleaseKey = releaseKey;
            SessionId = sessionId;
            BindingEpoch = bindingEpoch;
            QueryHash = queryHash;
            SnapshotId = snapshotId;
        }

        public bool Matches(CursorScope other) => other != null && ReleaseKey == other.ReleaseKey
            && SessionId == other.SessionId && BindingEpoch == other.BindingEpoch
            && QueryHash == other.QueryHash && SnapshotId == other.SnapshotId;
    }

    public sealed class ExportHandle
    {
        [JsonPropertyOrder(0)] public string Id { get; }
        [JsonPropertyOrder(1)] public string MediaType { get; }
        [JsonPropertyOrder(2)] public long ByteLength { get; }
        [JsonPropertyOrder(3)] public string Sha256 { get; }
        [JsonPropertyOrder(4)] public DateTimeOffset? ExpiresUtc { get; }
        [JsonPropertyOrder(5)] public string? Tool { get; }
        [JsonPropertyOrder(6)] public string? Target { get; }
        [JsonPropertyOrder(7)] public DateTimeOffset? CreatedUtc { get; }
        [JsonPropertyOrder(8)] public int? TotalLength { get; }

        public ExportHandle(string id, string mediaType, long byteLength, string sha256, DateTimeOffset? expiresUtc,
            string? tool = null, string? target = null, DateTimeOffset? createdUtc = null, int? totalLength = null)
        {
            V4Validation.Text(id, nameof(id));
            V4Validation.Text(mediaType, nameof(mediaType));
            V4Validation.Require(byteLength >= 0, "Export byte length must be nonnegative.");
            V4Validation.Hash(sha256);
            Id = id;
            MediaType = mediaType;
            ByteLength = byteLength;
            Sha256 = sha256;
            ExpiresUtc = expiresUtc?.ToUniversalTime();
            Tool = tool;
            Target = target;
            CreatedUtc = createdUtc?.ToUniversalTime();
            V4Validation.Require(totalLength == null || totalLength >= 0, "Export text length must be nonnegative.");
            TotalLength = totalLength;
        }
    }
}
