using TiaMcp.WorkerProtocol;
namespace TiaMcp.WorkerProtocol.HostPreview;

// Local launch configuration only. An advertised version is not authentication.
// Production v1 remains selected by its existing launcher, never by probing v2 bytes.
public static class ExplicitProtocolSelection
{
    public static void RequireV2(int requestedVersion, int hostVersion, int workerVersion)
    {
        if (requestedVersion != 2 || hostVersion != 2 || workerVersion != 2)
            throw new IdentityViolation("MatchingExplicitV2EndpointsRequired");
    }
}
