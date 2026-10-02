using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonLegacy;
using Legacy = TiaMcp.WorkerProtocol.JsonLegacy.StrictCodec;
using Modern = TiaMcp.WorkerProtocol.JsonV2.StrictCodec;

// Every codec call made by the mirrored reviewed corpus crosses both decoders.
static class DifferentialCodec
{
    public const int MaxFrameBytes = Legacy.MaxFrameBytes;
    public static V2Frame Decode(byte[] bytes)
    {
        V2Frame? legacy = null; TiaMcp.WorkerProtocol.JsonV2.V2Frame? modern = null;
        Exception? le = null, me = null;
        try { legacy = Legacy.Decode(bytes); } catch (IdentityViolation e) { le = e; }
        try { modern = Modern.Decode(bytes); } catch (IdentityViolation e) { me = e; }
        if ((le == null) != (me == null)) throw new Exception("Differential admission mismatch", le ?? me);
        if (le != null) { if (le.Message != me!.Message) throw new Exception("Differential error mismatch: " + le.Message + " / " + me.Message); throw le; }
        if (!Legacy.Encode(legacy!).SequenceEqual(Modern.Encode(modern!))) throw new Exception("Differential encoded-byte mismatch");
        return legacy!;
    }
    public static byte[] Encode(V2Frame frame) { var bytes = Legacy.Encode(frame); _ = Decode(bytes); return bytes; }
}
