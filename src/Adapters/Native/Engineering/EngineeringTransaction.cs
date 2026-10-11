using System;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private ExclusiveAccess? engineeringTransactionAccess;
        private Transaction? engineeringTransaction;
        private string? engineeringTransactionId;
        public bool HasEngineeringTransaction => engineeringTransactionAccess != null;

        public string BeginEngineeringTransaction(string text, bool dryRun = false)
        {
            Check();
            if (dryRun) throw new ArgumentException("A transaction scope cannot be opened in preview mode.");
            if (portal == null) throw new PlcSoftwareException("InvalidState", "TIA session unavailable.");
            if (engineeringTransactionAccess != null) throw new PlcSoftwareException("InvalidState", "A transaction is already open in this engine.");
            EngineeringPlcSession.VerifyBinding("Transaction");
            var persistence = project as ITransactionSupport ?? throw new NotSupportedException("The bound project does not implement ITransactionSupport.");
            var access = portal.ExclusiveAccess(text);
            try
            {
                var transaction = access.Transaction(persistence, text);
                engineeringTransaction = transaction;
                engineeringTransactionAccess = access;
                engineeringTransactionId = Guid.NewGuid().ToString("N");
                return engineeringTransactionId;
            }
            catch { access.Dispose(); throw; }
        }

        private Transaction RequireEngineeringTransaction(string id)
            => engineeringTransaction != null && id == engineeringTransactionId ? engineeringTransaction
                : throw new InvalidOperationException("The worker transaction scope is unavailable.");

        public bool ReadEngineeringTransaction(string id, string member)
        {
            var transaction = RequireEngineeringTransaction(id);
            switch (member)
            {
                case "CanCommit": return transaction.CanCommit;
                case "CommitRequested": return transaction.CommitRequested;
                case "IsCancellationRequested":
                    try { return engineeringTransactionAccess!.IsCancellationRequested; }
                    catch /* swallow(native-fallback): unavailable cancellation status conservatively cancels the transaction */ { return true; }
                default: throw new ArgumentException("Unknown transaction state member.");
            }
        }

        public void CommitEngineeringTransaction(string id, bool dryRun = false)
        {
            if (dryRun) throw new ArgumentException("A transaction commit cannot be requested in preview mode.");
            RequireEngineeringTransaction(id).CommitOnDispose();
        }

        public void DisposeEngineeringTransaction(string id)
        {
            var transaction = RequireEngineeringTransaction(id);
            var access = engineeringTransactionAccess!;
            try { transaction.Dispose(); }
            finally
            {
                engineeringTransaction = null; engineeringTransactionId = null; engineeringTransactionAccess = null;
                access.Dispose();
            }
        }

        private sealed class EngineeringAmbientAccess : IDisposable { public void Dispose() { } }
    }
}
