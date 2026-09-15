using System;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class PlcListingReadTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var read = new PlcListingRead();
            check(read.Required("+S1-K1/Name", () => "+S1-K1") == "+S1-K1", "listing preserves literal IEC name");
            check(read.Metadata("blocks")["dataComplete"]!.GetValue<bool>(), "empty successful diagnostics is complete");
            check(read.Optional<string?>("F_FB/Namespace", () => throw new NotSupportedException("not available"), null) == null,
                "unsupported safety block property is null, not a fabricated value");
            check(read.Optional<bool?>("F_FB/IsConsistent", () => throw new InvalidOperationException("cannot read"), null) == null,
                "failed boolean property remains unknown");
            check(read.Optional("Other/Name", () => "Other", "") == "Other", "later healthy block remains readable");
            var meta = read.Metadata("blocks");
            check(meta["apiCallSuccess"]!.GetValue<bool>() && !meta["dataComplete"]!.GetValue<bool>() && meta["failureCount"]!.GetValue<int>() == 2,
                "successful inventory does not imply complete attributes");
            check(meta.ToJsonString().Contains("F_FB/Namespace") && meta.ToJsonString().Contains("NotSupportedException"), "failure includes property and exception type");
            meta["failures"]!.AsArray().Clear();
            check(read.Metadata("blocks")["failureCount"]!.GetValue<int>() == 2, "returned diagnostics cannot mutate capture");
            bool stopped = false;
            try { read.Optional<string?>("F_FB/Namespace", () => throw new ObjectDisposedException("Portal"), null); }
            catch (PortalException ex) { stopped = ex.Message.Contains("connection unavailable"); }
            check(stopped, "disposed Portal fails instead of continuing attribute scan");
            bool staged = false;
            try { read.Required<object>("+S1-K1/BlockGroup", () => throw new Exception("denied")); }
            catch (PortalException ex) { staged = ex.Message.Contains("+S1-K1/BlockGroup") && ex.InnerException?.Message == "denied"; }
            check(staged, "root group access failure keeps stage and inner exception");
        }
    }
}
