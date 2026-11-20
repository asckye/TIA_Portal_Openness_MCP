using System;

namespace TiaOpenness.Shared
{
    internal static class HttpEndpoint
    {
        internal static Uri Parse(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("An HTTP(S) endpoint without credentials, query or fragment is required.");
            return uri;
        }
    }
}
