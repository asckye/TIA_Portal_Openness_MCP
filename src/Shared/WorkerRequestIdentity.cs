namespace TiaOpenness.Shared
{
    internal static class WorkerRequestIdentity
    {
        internal const int MaximumLength = 128;
        // At maximum identity length the metadata occupies about half of the 1 MiB request frame.
        internal const int MaximumUnsettledRequests = 4096;

        internal static bool IsValid(string text)
        {
            if (text.Length == 0 || text.Length > MaximumLength) return false;
            foreach (char c in text)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9'
                    || c == '-' || c == '_' || c == '.' || c == ':')) return false;
            return true;
        }
    }
}
