using System;
using System.Globalization;
using System.Text;

namespace TiaMcp.Adapters.Contracts
{
    // Split paths before decoding: an escaped separator belongs to a single object name.
    public static class ObjectPathEncoding
    {
        public static string EncodeSegment(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (character == '%' || character == '/' || character == '\\' || character <= '\u001F' || character == '\u007F')
                    result.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
                else
                    result.Append(character);
            }
            return result.ToString();
        }

        public static string DecodeSegment(string value) => Uri.UnescapeDataString(value);

        public static string NormalizePath(string path, string parameter = "path")
        {
            if (path == null) throw new AdapterPreconditionException("A path value is required.", parameter);
            if (path.IndexOf('\\') >= 0)
                throw new AdapterPreconditionException("Unencoded backslashes are refused; encode name backslashes as %5C.", parameter);
            return string.Join("/", Array.ConvertAll(path.Split('/'), segment => EncodeSegment(DecodeSegment(segment))));
        }
    }
}
