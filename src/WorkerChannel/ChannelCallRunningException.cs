using System;
using System.Threading.Tasks;

namespace TiaMcp.WorkerChannel;

public sealed class ChannelCallRunningException : TimeoutException
{
    public Task<string> Reply { get; }

    public ChannelCallRunningException(Task<string> reply, string message) : base(message) => Reply = reply;
}
