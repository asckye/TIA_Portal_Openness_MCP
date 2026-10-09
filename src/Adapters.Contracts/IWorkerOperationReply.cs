namespace TiaMcp.Adapters.Contracts
{
    // Implement explicitly on existing wire DTOs so adding the common contract
    // does not add JSON fields to an already released response.
    public interface IWorkerOperationReply
    {
        bool RequiresSessionReset { get; }
        bool MayHaveChanged { get; }
        bool BlockReadsAfterUncertain { get; }
    }
}
