using System;

namespace TiaOpenness.Core.Rpc
{
    /// <summary>
    /// The open project exposes no Version Control Interface; carries a distinct capability code.
    /// </summary>
    public class VersionControlUnsupportedException : Exception
    {
        public VersionControlUnsupportedException()
            : base("This project exposes no Version Control Interface in the current installation. " +
                   "Export blocks as Source for a text snapshot instead.")
        {
        }
    }
}
