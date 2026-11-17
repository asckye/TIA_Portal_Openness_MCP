using System;

namespace TiaMcp.Adapters.Contracts
{
    // Temporary export names also escape Windows file-name punctuation such as : * ? " < > |.
    public static class ObjectFileNameEncoding
    {
        public static string Encode(string value) => Uri.EscapeDataString(value);
    }
}
