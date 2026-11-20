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
        private bool exclusiveHeld;
        public IDisposable ExclusiveAccess()
        {
            if (exclusiveHeld) throw new InvalidOperationException("Multiple instances of ExclusiveAccess are not supported.");
            exclusiveHeld = true;
            return new Access(this);
        }
        private sealed class Access(CaxProvider provider) : IDisposable
        {
            public void Dispose() => provider.exclusiveHeld = false;
        }
        public TransferResult Import(FileInfo source, CaxImportOptions option)
        {
            if (exclusiveHeld) throw new InvalidOperationException("Action is not supported within ExclusiveAccess.");
            ImportCalls++;
            return result;
        }
        public TransferResult Export(Device device, FileInfo destination) => throw new NotSupportedException();
#if !PLC_CAX_TRANSFER_RESULT
        public bool Import(FileInfo source, FileInfo log, CaxImportOptions option)
        {
            using var access = ExclusiveAccess();
            ImportCalls++;
            File.WriteAllText(log.FullName, "Device import failed.");
            return result.ErrorCount == 0;
        }
        public bool Export(Device device, FileInfo destination, FileInfo log) => throw new NotSupportedException();
#endif
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
        public int WriteAdmissions { get; private set; }
        private IDisposable HardwareEditAccess() => provider.ExclusiveAccess();
        private void HardwareWriteAdmission() => WriteAdmissions++;
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
