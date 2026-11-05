using System;

namespace TiaMcp.WorkerChannel;

public sealed class ChannelReadTimeoutException : TimeoutException
{
    public ChannelReadTimeoutException() : base("The worker read timed out and is still draining.") { }
}
