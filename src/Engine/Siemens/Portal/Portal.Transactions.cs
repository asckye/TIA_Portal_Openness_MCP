using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // ---- transactions ---------------------------------------------------------------------------------------------------------
        // One ExclusiveAccess + Transaction wraps several tool calls into a single TIA undo unit; the inner tools reuse the ambient
        // exclusive access (AcquireHmiEditAccess) instead of opening a second one.
        internal sealed class TransactionScope : IEngineeringTransaction
        {
            private readonly Portal _portal; private readonly ExclusiveAccess _access; private readonly Transaction _transaction; private bool _disposed;
            internal TransactionScope(Portal portal, ExclusiveAccess access, Transaction transaction) { _portal = portal; _access = access; _transaction = transaction; }
            public bool CanCommit => _transaction.CanCommit;
            public bool CommitRequested => _transaction.CommitRequested;
            public bool IsCancellationRequested { get { try { return _access.IsCancellationRequested; } catch /* swallow(native-fallback): unavailable cancellation status conservatively cancels the transaction */ { return true; } } }
            public void Commit() => _transaction.CommitOnDispose();
            public void Dispose()
            {
                if (_disposed) return; _disposed = true;
                try { _transaction.Dispose(); } finally { _portal._ambientExclusiveAccess = null; _access.Dispose(); }
            }
        }
        private ExclusiveAccess? _ambientExclusiveAccess;
        internal TransactionScope BeginTransaction(string text)
        {
            if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "TIA session unavailable.");
            if (_ambientExclusiveAccess != null) throw new PortalException(PortalErrorCode.InvalidState, "A transaction is already open in this engine.");
            EnsureBoundProjectUnchanged("Transaction");
            var persistence = _project as ITransactionSupport ?? throw new NotSupportedException("The bound project does not implement ITransactionSupport.");
            var access = _portal.ExclusiveAccess(text);
            try
            {
                var transaction = access.Transaction(persistence, text);
                _ambientExclusiveAccess = access;
                return new TransactionScope(this, access, transaction);
            }
            catch { access.Dispose(); throw; }
        }
    }
}
