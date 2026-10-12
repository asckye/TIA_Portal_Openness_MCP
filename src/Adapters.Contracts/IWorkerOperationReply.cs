namespace TiaMcp.Adapters.Contracts
{
    // Implement explicitly on existing wire DTOs so adding the common contract
    // does not add JSON fields to an already released response.
    public interface IWorkerOperationReply
    {
        public bool RequiresSessionReset { get; }
        public bool MayHaveChanged { get; }
        public bool BlockReadsAfterUncertain { get; }
    }
}
