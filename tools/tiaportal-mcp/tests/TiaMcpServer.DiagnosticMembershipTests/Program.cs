using System;
using System.Threading.Tasks;
using Siemens.Collaboration.Net;

// Compile the actual production wrapper against a fake dependency. No Windows,
// Siemens runtime, process, membership, elevation, or mutation APIs are invoked.
internal static class Program
{
    private static int Main()
    {
        int reads = 0, repairs = 0;
        Api.Global.Fake.Repair = () => { repairs++; throw new Exception("Repair must never run"); };
        foreach (bool member in new[] { false, true })
        {
            Api.Global.Fake.Read = () => { reads++; return member; };
            if (TiaMcpServer.Siemens.Openness.IsUserInGroupNoFix() != member)
                throw new Exception("Membership result changed");
        }
        var unknown = new InvalidOperationException("Membership unavailable");
        Api.Global.Fake.Read = () => { reads++; throw unknown; };
        try
        {
            TiaMcpServer.Siemens.Openness.IsUserInGroupNoFix();
            throw new Exception("Unknown membership was swallowed");
        }
        catch (InvalidOperationException ex) when (ReferenceEquals(ex, unknown)) { }
        if (reads != 3 || repairs != 0) throw new Exception("Read-only contract failed");
        Console.WriteLine("PASS: nonmember=false, member=true, unknown=original exception; 3 reads, 0 repairs.");
        return 0;
    }
}

namespace Siemens.Collaboration.Net
{
    public static class Api
    {
        public static readonly FakeGlobal Global = new FakeGlobal();
    }
    public sealed class FakeGlobal
    {
        public readonly FakeOpenness Fake = new FakeOpenness();
        public FakeOpenness Openness() => Fake;
    }
    public sealed class FakeOpenness
    {
        public Func<bool> Read = () => throw new Exception("Read callback not configured");
        public Func<Task<bool>> Repair = () => throw new Exception("Repair callback not configured");
        public bool IsUserInGroup() => Read();
        public Task<bool> AddUserToGroupAsync() => Repair();
        public void Initialize(int? tiaMajorVersion) => throw new Exception("Initialization must never run");
    }
}
