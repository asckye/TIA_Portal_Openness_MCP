using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Environment;

namespace TiaOpenness.Core.Abstractions
{
    /// <summary>
    /// Stands in when the real Openness backend cannot be used and the caller did not ask for
    /// the mock. It reports <see cref="SessionMode.Openness"/> so nothing downstream mistakes it
    /// for synthetic data, and throws with the reason the moment a session is requested.
    /// </summary>
    public sealed class UnavailableSessionFactory : ITiaSessionFactory
    {
        private readonly string _reason;

        public UnavailableSessionFactory(string reason)
        {
            _reason = reason;
        }

        public SessionMode Mode { get { return SessionMode.Openness; } }

        public void Configure(string opennessVersion)
        {
            // Nothing to bind; the failure is reported when a session is actually needed.
        }

        public ITiaSession Create()
        {
            throw new OpennessUnavailableException(_reason);
        }
    }

    /// <summary>The real backend is not usable here. Carries the diagnosis, not just a symptom.</summary>
    public class OpennessUnavailableException : System.Exception
    {
        public OpennessUnavailableException(string reason)
            : base("TIA Portal Openness is not available on this machine. " + reason)
        {
        }
    }
}
