using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sharp7;

namespace TiaMcpServer.Runtime
{
    // Read-only live-value reader over the S7 (ISO-on-TCP / RFC1006) protocol.
    // This is a RUNTIME channel, independent of TIA Openness: it talks directly to
    // the CPU on port 102 and never writes, forces, or changes CPU mode.
    //
    // S7-1200/1500 preconditions for absolute reads of DB areas:
    //   - "Permit access with PUT/GET communication" must be enabled on the CPU.
    //   - The DB being read must be NON-optimized (standard access). Optimized DBs
    //     have no fixed absolute layout and cannot be read by DBx.DBd offset.
    // M / I / Q areas do not have the optimized-DB restriction.

    public sealed class S7CpuIdentity
    {
        public bool Connected;
        public string ModuleTypeName = "";
        public string AsName = "";
        public string ModuleName = "";
        public string SerialNumber = "";
        public int PduLength;
        public string? Error;       // connection-level failure
        public string? SzlError;    // CPU identification (SZL) unavailable — common on S7-1200, not fatal
    }

    public sealed class S7ReadItem
    {
        public string Spec = "";
        public string Area = "";   // DB/M/I/Q
        public int Db;
        public int ByteOffset;
        public int BitOffset;
        public string Type = "";   // BOOL/BYTE/SINT/USINT/INT/UINT/WORD/DINT/UDINT/DWORD/REAL
        public object? Value;      // decoded value (null on error)
        public string? Error;
    }

    public sealed class S7ReadResult
    {
        public bool Ok;
        public string? Error;
        public bool IdentityConfirmed;
        public S7CpuIdentity Identity = new S7CpuIdentity();
        public List<S7ReadItem> Items = new List<S7ReadItem>();
        public long ElapsedMs;
    }

    // One sampled signal over the whole trend window.
    public sealed class S7SampleSeries
    {
        public string Spec = "";
        public string Area = "";
        public int Db;
        public int ByteOffset;
        public int BitOffset;
        public string Type = "";
        public List<object?> Values = new List<object?>();   // one entry per sample; null = that sample failed
        public string? Error;                                 // parse error -> whole series invalid, not sampled
        public double? Min;
        public double? Max;
        public double? Avg;
    }

    public sealed class S7SampleResult
    {
        public bool Ok;
        public string? Error;
        public bool IdentityConfirmed;
        public S7CpuIdentity Identity = new S7CpuIdentity();
        public List<long> TimestampsMs = new List<long>();    // ms offset from first sample
        public int SampleCount;
        public int RequestedIntervalMs;
        public long ActualElapsedMs;
        public List<S7SampleSeries> Series = new List<S7SampleSeries>();
    }

    public sealed class S7DiagEntry
    {
        public int Index;
        public string EventId = "";   // first 2 bytes, hex (Siemens event id; text needs TIA's text DB)
        public string Raw = "";       // full record bytes, hex
    }

    public sealed class S7RunState
    {
        public bool Connected;
        public string Status = "UNKNOWN";   // RUN / STOP / UNKNOWN
        public S7CpuIdentity Identity = new S7CpuIdentity();
        public string? PlcDateTime;
        public int DiagRecordCount;
        public List<S7DiagEntry> DiagEntries = new List<S7DiagEntry>();
        public string? DiagNote;            // why the diagnostic buffer was not parsed (best-effort)
        public string? Error;
        public long ElapsedMs;
    }
}
