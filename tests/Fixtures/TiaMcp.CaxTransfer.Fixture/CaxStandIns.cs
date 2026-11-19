using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;

namespace Siemens.Engineering.Cax
{
    public enum CaxImportOptions { RetainTiaDevice }
    public enum TransferResultState { Warning, Error }
    public sealed record TransferResultMessage(TransferResultState State, string Message, List<TransferResultMessage> Messages);
    public sealed record TransferResult(TransferResultState State, int ErrorCount, int WarningCount, List<TransferResultMessage> Messages);

    public sealed class CaxProvider(TransferResult result)
    {
        public int ImportCalls { get; private set; }
        public TransferResult Import(FileInfo source, CaxImportOptions option)
        {
            ImportCalls++;
            return result;
        }
        public TransferResult Export(Device device, FileInfo destination) => throw new NotSupportedException();
    }
}

namespace Siemens.Engineering.HW
{
    public sealed class Device { public string Name { get; init; } = ""; }
}

namespace Siemens.Engineering
{
    internal sealed class Project(CaxProvider provider)
    {
        public T? GetService<T>() where T : class => provider as T;
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine(string release, CaxProvider provider)
    {
        private readonly Project project = new(provider);
        private string ReleaseKey => release;
        private void Check() { }
        private bool HardwareProjectMissing() => false;
        private IDisposable? HardwareEditAccess() => null;
        private void HardwareWriteAdmission() { }
        private IEnumerable<Device> EnumerateAllDevices() => Array.Empty<Device>();
        private Device? HardwareLegacyDevice(string path) => throw new NotSupportedException();

        // Expose the adapter's returned metadata and let any thrown exception fail the test.
        private HardwareAddressingReply RunHardwareAddressStep(string tool, Func<Dictionary<string, object?>, string> run)
        {
            var reply = new HardwareAddressingReply();
            reply.Message = run(reply.Meta);
            return reply;
        }
    }
}
