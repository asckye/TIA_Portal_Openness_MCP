using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sharp7;

namespace TiaMcpServer.Runtime
{

    public static partial class S7LiveReader
    {
        // Sharp7 area codes
        private const int AreaDB = 0x84;
        private const int AreaMK = 0x83; // M (merker)
        private const int AreaPE = 0x81; // I (process inputs)
        private const int AreaPA = 0x82; // Q (process outputs)
        private const int WLByte = 0x02;

        public static S7CpuIdentity ProbeIdentity(string ip, int rack, int slot)
        {
            var id = new S7CpuIdentity();
            var client = new S7Client();
            try
            {
                int res = client.ConnectTo(ip, rack, slot);
                if (res != 0)
                {
                    id.Error = $"Connect failed: {client.ErrorText(res)}";
                    return id;
                }
                id.Connected = true;
                id.PduLength = client.NegotiatedPduLength();

                var info = new S7Client.S7CpuInfo();
                int r = client.GetCpuInfo(ref info);
                if (r == 0)
                {
                    id.ModuleTypeName = (info.ModuleTypeName ?? "").Trim();
                    id.AsName = (info.ASName ?? "").Trim();
                    id.ModuleName = (info.ModuleName ?? "").Trim();
                    id.SerialNumber = (info.SerialNumber ?? "").Trim();
                }
                else
                {
                    id.SzlError = $"GetCpuInfo unavailable: {client.ErrorText(r)} (common on S7-1200; not a connection failure)";
                }
                return id;
            }
            catch (Exception ex)
            {
                id.Error = ex.Message;
                return id;
            }
            finally
            {
                try { client.Disconnect(); } catch /* swallow(teardown): disconnect failure must not replace the CPU identity result */ { }
            }
        }

        // expectModuleContains: if non-empty, the CPU module type must contain this
        // (case-insensitive) substring or the read is aborted before touching any data.
        // This is the identity guard: e.g. pass "1211C" so we only ever read the
        // intended CPU and never an unrelated PLC that happens to answer on port 102.
        public static S7ReadResult ReadItems(string ip, int rack, int slot, IEnumerable<string> specs, string? expectModuleContains)
        {
            var result = new S7ReadResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = new S7Client();
            try
            {
                int res = client.ConnectTo(ip, rack, slot);
                if (res != 0)
                {
                    result.Error = $"Connect to {ip} (rack {rack}, slot {slot}) failed: {client.ErrorText(res)}";
                    return result;
                }
                result.Identity.Connected = true;
                result.Identity.PduLength = client.NegotiatedPduLength();

                var info = new S7Client.S7CpuInfo();
                int infoRes = client.GetCpuInfo(ref info);
                if (infoRes == 0)
                {
                    result.Identity.ModuleTypeName = (info.ModuleTypeName ?? "").Trim();
                    result.Identity.AsName = (info.ASName ?? "").Trim();
                    result.Identity.ModuleName = (info.ModuleName ?? "").Trim();
                    result.Identity.SerialNumber = (info.SerialNumber ?? "").Trim();
                }
                else
                {
                    result.Identity.SzlError = $"GetCpuInfo unavailable: {client.ErrorText(infoRes)} (common on S7-1200)";
                }

                // Identity guard: only ABORT on a positive mismatch. If SZL identity is
                // unavailable (S7-1200 commonly refuses GetCpuInfo), we cannot cross-check,
                // so we proceed on the caller-supplied IP and flag that it was unconfirmed.
                if (!string.IsNullOrWhiteSpace(expectModuleContains))
                {
                    if (!string.IsNullOrWhiteSpace(result.Identity.ModuleTypeName))
                    {
                        if (result.Identity.ModuleTypeName.IndexOf(expectModuleContains!, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            result.Error = $"Identity guard tripped: CPU at {ip} reports module '{result.Identity.ModuleTypeName}', " +
                                           $"which does not contain expected '{expectModuleContains}'. Aborted before reading any data.";
                            return result;
                        }
                        result.IdentityConfirmed = true;
                    }
                    // else: SZL unavailable -> IdentityConfirmed stays false, read proceeds.
                }

                foreach (var spec in specs)
                {
                    var item = ParseSpec(spec);
                    result.Items.Add(item);
                    if (item.Error != null) continue;
                    ReadOne(client, item);
                }

                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try { client.Disconnect(); } catch /* swallow(teardown): disconnect failure must not replace the S7 item read result */ { }
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
            }
        }

        // Hard bounds so a sampling request can never block forever or return an
        // unbounded payload. A call blocks for up to durationMs while it samples.
        public const int MinIntervalMs = 20;
        public const int MaxDurationMs = 120000;     // 2 minutes
        public const int MaxSamplesCap = 5000;

        // Sample a set of addresses on a single open S7 connection: at each interval,
        // read every address once and record the value, until durationMs elapses or
        // maxSamples is reached. Returns a time series per address plus min/max/avg of
        // the numeric ones (handy for PID step-response capture). Read-only.
        public static S7SampleResult SampleItems(string ip, int rack, int slot, IEnumerable<string> specs,
            int intervalMs, int durationMs, int maxSamples, string? expectModuleContains)
        {
            var result = new S7SampleResult();
            if (intervalMs < MinIntervalMs) intervalMs = MinIntervalMs;
            if (durationMs <= 0 || durationMs > MaxDurationMs) durationMs = MaxDurationMs;
            if (maxSamples <= 0) maxSamples = 600;
            if (maxSamples > MaxSamplesCap) maxSamples = MaxSamplesCap;
            result.RequestedIntervalMs = intervalMs;

            // Parse every spec once; keep one template item per series for the loop.
            var templates = new List<S7ReadItem>();
            foreach (var spec in specs)
            {
                var t = ParseSpec(spec);
                var series = new S7SampleSeries
                {
                    Spec = t.Spec, Area = t.Area, Db = t.Db,
                    ByteOffset = t.ByteOffset, BitOffset = t.BitOffset, Type = t.Type,
                    Error = t.Error
                };
                result.Series.Add(series);
                templates.Add(t);
            }
            if (result.Series.Count == 0) { result.Error = "No addresses supplied."; return result; }

            var client = new S7Client();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int res = client.ConnectTo(ip, rack, slot);
                if (res != 0)
                {
                    result.Error = $"Connect to {ip} (rack {rack}, slot {slot}) failed: {client.ErrorText(res)}";
                    return result;
                }
                result.Identity.Connected = true;
                result.Identity.PduLength = client.NegotiatedPduLength();

                var info = new S7Client.S7CpuInfo();
                if (client.GetCpuInfo(ref info) == 0)
                {
                    result.Identity.ModuleTypeName = (info.ModuleTypeName ?? "").Trim();
                    result.Identity.AsName = (info.ASName ?? "").Trim();
                    result.Identity.ModuleName = (info.ModuleName ?? "").Trim();
                    result.Identity.SerialNumber = (info.SerialNumber ?? "").Trim();
                }
                else result.Identity.SzlError = "GetCpuInfo unavailable (common on S7-1200)";

                if (!string.IsNullOrWhiteSpace(expectModuleContains))
                {
                    if (!string.IsNullOrWhiteSpace(result.Identity.ModuleTypeName))
                    {
                        if (result.Identity.ModuleTypeName.IndexOf(expectModuleContains!, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            result.Error = $"Identity guard tripped: CPU at {ip} reports module '{result.Identity.ModuleTypeName}', " +
                                           $"which does not contain expected '{expectModuleContains}'. Aborted before sampling.";
                            return result;
                        }
                        result.IdentityConfirmed = true;
                    }
                }

                long nextTick = 0;
                while (sw.ElapsedMilliseconds < durationMs && result.SampleCount < maxSamples)
                {
                    long t0 = sw.ElapsedMilliseconds;
                    result.TimestampsMs.Add(t0);
                    for (int i = 0; i < templates.Count; i++)
                    {
                        var tpl = templates[i];
                        if (tpl.Error != null) { result.Series[i].Values.Add(null); continue; }
                        ReadOne(client, tpl);                 // overwrites tpl.Value / tpl.Error
                        result.Series[i].Values.Add(tpl.Error == null ? tpl.Value : null);
                    }
                    result.SampleCount++;

                    nextTick += intervalMs;
                    long remaining = nextTick - sw.ElapsedMilliseconds;
                    if (remaining > 0) System.Threading.Thread.Sleep((int)remaining);
                }

                foreach (var s in result.Series) Aggregate(s);
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try { client.Disconnect(); } catch /* swallow(teardown): disconnect failure must not replace collected samples or elapsed-time reporting */ { }
                sw.Stop();
                result.ActualElapsedMs = sw.ElapsedMilliseconds;
            }
        }

        private static void Aggregate(S7SampleSeries s)
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0; int n = 0;
            foreach (var v in s.Values)
            {
                if (v == null) continue;
                double d;
                if (v is bool b) d = b ? 1 : 0;
                else if (v is IConvertible) { try { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch /* swallow(parse-fallback): non-numeric sample values are excluded from numeric aggregates */ { continue; } }
                else continue;
                if (d < min) min = d;
                if (d > max) max = d;
                sum += d; n++;
            }
            if (n > 0) { s.Min = min; s.Max = max; s.Avg = Math.Round(sum / n, 6); }
        }

        // Snap7/S7 operating-mode codes (not exposed as named constants by Sharp7).
        private const int S7StatusUnknown = 0x00;
        private const int S7StatusStop = 0x04;
        private const int S7StatusRun = 0x08;
        private const int SzlDiagnosticBuffer = 0x00A0;   // diagnostic buffer SZL id

        // Read CPU operating mode (RUN/STOP) over S7 — something TIA Openness cannot do.
        // Also best-effort reads the CPU clock and diagnostic buffer (raw entries only;
        // full event text needs TIA's text database). Read-only.
        public static S7RunState ReadRunState(string ip, int rack, int slot, int maxDiagEntries, string? expectModuleContains)
        {
            var state = new S7RunState();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = new S7Client();
            try
            {
                int res = client.ConnectTo(ip, rack, slot);
                if (res != 0) { state.Error = $"Connect to {ip} (rack {rack}, slot {slot}) failed: {client.ErrorText(res)}"; return state; }
                state.Connected = true;
                state.Identity.Connected = true;
                state.Identity.PduLength = client.NegotiatedPduLength();

                var info = new S7Client.S7CpuInfo();
                if (client.GetCpuInfo(ref info) == 0)
                {
                    state.Identity.ModuleTypeName = (info.ModuleTypeName ?? "").Trim();
                    state.Identity.AsName = (info.ASName ?? "").Trim();
                    state.Identity.ModuleName = (info.ModuleName ?? "").Trim();
                    state.Identity.SerialNumber = (info.SerialNumber ?? "").Trim();
                }
                else state.Identity.SzlError = "GetCpuInfo unavailable (common on S7-1200)";

                if (!string.IsNullOrWhiteSpace(expectModuleContains) && !string.IsNullOrWhiteSpace(state.Identity.ModuleTypeName)
                    && state.Identity.ModuleTypeName.IndexOf(expectModuleContains!, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    state.Error = $"Identity guard tripped: CPU at {ip} reports module '{state.Identity.ModuleTypeName}', " +
                                  $"which does not contain expected '{expectModuleContains}'.";
                    return state;
                }

                int status = 0;
                int rcStatus = client.PlcGetStatus(ref status);
                state.Status = rcStatus != 0 ? "UNKNOWN"
                    : status == S7StatusRun ? "RUN"
                    : status == S7StatusStop ? "STOP"
                    : status == S7StatusUnknown ? "UNKNOWN"
                    : $"OTHER(0x{status:X2})";

                try { var dt = new DateTime(); if (client.GetPlcDateTime(ref dt) == 0) state.PlcDateTime = dt.ToString("O"); } catch /* swallow(probe-optional): an unavailable PLC clock must not discard operating-state and diagnostic results */ { }

                // Best-effort diagnostic buffer (raw). Wrapped: any failure -> clean note.
                try
                {
                    var szl = new S7Client.S7SZL();
                    szl.Data = new byte[4096];
                    int size = szl.Data.Length;
                    int rcSzl = client.ReadSZL(SzlDiagnosticBuffer, 0x0000, ref szl, ref size);
                    if (rcSzl == 0)
                    {
                        state.DiagRecordCount = szl.Header.N_DR;
                        state.DiagEntries = ParseSzlDiagRecords(szl.Data, szl.Header.LENTHDR, szl.Header.N_DR, maxDiagEntries);
                        if (state.DiagEntries.Count > 0)
                            state.DiagNote =
                                "Raw diagnostic-buffer records (newest first as returned by the CPU). Event IDs are hex; full event text requires TIA's text " +
                                "database.";
                    }
                    else state.DiagNote = $"Diagnostic buffer not available over SZL on this CPU ({client.ErrorText(rcSzl)}). RUN/STOP above is still valid.";
                }
                catch (Exception ex) { state.DiagNote = "Diagnostic buffer read skipped: " + ex.Message; }

                return state;
            }
            catch (Exception ex) { state.Error = ex.Message; return state; }
            finally { try { client.Disconnect(); } catch /* swallow(teardown): disconnect failure must not replace the PLC state and diagnostic result */ { } sw.Stop(); state.ElapsedMs = sw.ElapsedMilliseconds; }
        }

        // Pure parser for SZL diagnostic records: split Data into fixed-length records,
        // extract the 2-byte event id (big-endian) + the raw bytes of each. Testable.
        public static List<S7DiagEntry> ParseSzlDiagRecords(byte[] data, int recordLen, int count, int maxEntries)
        {
            var list = new List<S7DiagEntry>();
            if (data == null || recordLen <= 0 || count <= 0) return list;
            int n = maxEntries > 0 ? Math.Min(count, maxEntries) : count;
            for (int i = 0; i < n; i++)
            {
                int off = i * recordLen;
                if (off + recordLen > data.Length) break;
                string eventId = recordLen >= 2 ? ((data[off] << 8) | data[off + 1]).ToString("X4") : "";
                var sb = new StringBuilder(recordLen * 3);
                for (int k = 0; k < recordLen; k++) { if (k > 0) sb.Append(' '); sb.Append(data[off + k].ToString("X2")); }
                list.Add(new S7DiagEntry { Index = i, EventId = eventId, Raw = sb.ToString() });
            }
            return list;
        }

        private static void ReadOne(S7Client client, S7ReadItem item)
        {
            int area;
            switch (item.Area)
            {
                case "DB": area = AreaDB; break;
                case "M": area = AreaMK; break;
                case "I": area = AreaPE; break;
                case "Q": area = AreaPA; break;
                default: item.Error = $"Unknown area '{item.Area}'"; return;
            }

            int size = TypeSize(item.Type);
            var buffer = new byte[size];
            int r = client.ReadArea(area, item.Db, item.ByteOffset, size, WLByte, buffer);
            if (r != 0)
            {
                item.Error = client.ErrorText(r) +
                    " (S7-1200/1500: needs PUT/GET enabled and a non-optimized DB for absolute reads)";
                return;
            }

            try
            {
                item.Value = Decode(item.Type, buffer, item.BitOffset);
            }
            catch (Exception ex)
            {
                item.Error = "decode failed: " + ex.Message;
            }
        }

        private static int TypeSize(string type)
        {
            switch (type)
            {
                case "BOOL": return 1;
                case "BYTE":
                case "SINT":
                case "USINT": return 1;
                case "INT":
                case "UINT":
                case "WORD": return 2;
                case "DINT":
                case "UDINT":
                case "DWORD":
                case "REAL": return 4;
                default: return 1;
            }
        }

        private static object Decode(string type, byte[] b, int bit)
        {
            switch (type)
            {
                case "BOOL": return S7.GetBitAt(b, 0, bit);
                case "BYTE": return (int)b[0];
                case "USINT": return (int)b[0];
                case "SINT": return (int)(sbyte)b[0];
                case "INT": return (int)S7.GetIntAt(b, 0);
                case "UINT": return (int)S7.GetUIntAt(b, 0);
                case "WORD": return (int)S7.GetWordAt(b, 0);
                case "DINT": return S7.GetDIntAt(b, 0);
                case "UDINT": return (long)S7.GetUDIntAt(b, 0);
                case "DWORD": return (long)S7.GetDWordAt(b, 0);
                case "REAL": return Math.Round((double)S7.GetRealAt(b, 0), 6);
                default: return (int)b[0];
            }
        }

        // Parse classic S7 absolute addresses, optional ":TYPE" suffix:
        //   DB10.DBX2.3 / DB10.DBB4 / DB10.DBW6 / DB10.DBD8
        //   M0.0 / MB10 / MW12 / MD14   (same for I and Q)
        private static readonly Regex DbRx = new Regex(
            @"^DB(?<db>\d+)\.DB(?<sz>[XBWD])(?<byte>\d+)(?:\.(?<bit>\d+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BitRx = new Regex(
            @"^(?<area>[MIQ])(?<byte>\d+)\.(?<bit>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SzRx = new Regex(
            @"^(?<area>[MIQ])(?<sz>[BWD])(?<byte>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
