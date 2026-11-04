using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sharp7;

namespace TiaMcpServer.Runtime
{
    public static partial class S7LiveReader
    {

        public static S7ReadItem ParseSpec(string raw)
        {
            var item = new S7ReadItem { Spec = raw };
            if (string.IsNullOrWhiteSpace(raw)) { item.Error = "empty spec"; return item; }

            string s = raw.Trim();
            string? typeOverride = null;
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                typeOverride = s.Substring(colon + 1).Trim().ToUpperInvariant();
                s = s.Substring(0, colon).Trim();
            }

            var mDb = DbRx.Match(s);
            if (mDb.Success)
            {
                item.Area = "DB";
                item.Db = int.Parse(mDb.Groups["db"].Value, CultureInfo.InvariantCulture);
                item.ByteOffset = int.Parse(mDb.Groups["byte"].Value, CultureInfo.InvariantCulture);
                string sz = mDb.Groups["sz"].Value.ToUpperInvariant();
                if (sz == "X")
                {
                    item.BitOffset = mDb.Groups["bit"].Success ? int.Parse(mDb.Groups["bit"].Value, CultureInfo.InvariantCulture) : 0;
                    item.Type = "BOOL";
                }
                else item.Type = DefaultTypeForSize(sz);
                ApplyTypeOverride(item, typeOverride, sz);
                return item;
            }

            var mBit = BitRx.Match(s);
            if (mBit.Success)
            {
                item.Area = mBit.Groups["area"].Value.ToUpperInvariant();
                item.ByteOffset = int.Parse(mBit.Groups["byte"].Value, CultureInfo.InvariantCulture);
                item.BitOffset = int.Parse(mBit.Groups["bit"].Value, CultureInfo.InvariantCulture);
                item.Type = "BOOL";
                return item;
            }

            var mSz = SzRx.Match(s);
            if (mSz.Success)
            {
                item.Area = mSz.Groups["area"].Value.ToUpperInvariant();
                item.ByteOffset = int.Parse(mSz.Groups["byte"].Value, CultureInfo.InvariantCulture);
                string sz = mSz.Groups["sz"].Value.ToUpperInvariant();
                item.Type = DefaultTypeForSize(sz);
                ApplyTypeOverride(item, typeOverride, sz);
                return item;
            }

            item.Error = $"Unrecognized address '{raw}'. Use DB10.DBD0:REAL, DB1.DBX2.3, M0.0, MW12, etc.";
            return item;
        }

        // Convert a TIA watch-table absolute address to an S7 read spec.
        // "%DB1.DBX0.0" -> "DB1.DBX0.0", "%MW4" -> "MW4", "%DB1.DBD0" + float -> "DB1.DBD0:REAL".
        // Returns null for symbolic operands (e.g. "Crew_Data".X) which need no '%' and
        // cannot be read by absolute offset without symbol resolution.
        public static string? TiaAddressToSpec(string? tiaAddress, string? displayFormat)
        {
            if (string.IsNullOrWhiteSpace(tiaAddress)) return null;
            string a = tiaAddress!.Trim();
            if (!a.StartsWith("%", StringComparison.Ordinal)) return null;          // symbolic / non-absolute
            a = a.Substring(1).Trim();

            // Validate it parses as an absolute address before accepting.
            var probe = ParseSpec(a);
            if (probe.Error != null) return null;

            bool isFloat = !string.IsNullOrEmpty(displayFormat) &&
                (displayFormat!.IndexOf("float", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 displayFormat.IndexOf("real", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isFloat && probe.Type == "DWORD")        // 4-byte slot displayed as float -> REAL
                return a + ":REAL";

            // Honor TIA "DEC_signed" so negative values match what the watch table shows
            // (default decoding of B/W/D is unsigned). "DEC_unsigned" keeps the default.
            bool isSigned = !string.IsNullOrEmpty(displayFormat) &&
                displayFormat!.IndexOf("signed", StringComparison.OrdinalIgnoreCase) >= 0 &&
                displayFormat.IndexOf("unsigned", StringComparison.OrdinalIgnoreCase) < 0;
            if (isSigned)
            {
                if (probe.Type == "WORD") return a + ":INT";
                if (probe.Type == "DWORD") return a + ":DINT";
                if (probe.Type == "BYTE") return a + ":SINT";
            }
            return a;
        }

        // Convert a PLC tag's absolute LogicalAddress + TIA DataTypeName to an S7 read
        // spec. "%I0.0"+"Bool" -> "I0.0"; "%MW20"+"Int" -> "MW20:INT"; "%MD12"+"Real"
        // -> "MD12:REAL". Returns null for symbolic/non-absolute addresses or a
        // type/address mismatch (e.g. a numeric type on a bit address).
        public static string? TiaTagToSpec(string? logicalAddress, string? dataType)
        {
            if (string.IsNullOrWhiteSpace(logicalAddress)) return null;
            string a = logicalAddress!.Trim();
            if (!a.StartsWith("%", StringComparison.Ordinal)) return null;
            a = a.Substring(1).Trim();

            var probe = ParseSpec(a);
            if (probe.Error != null) return null;

            string? t = MapTiaDataType(dataType);
            if (t == null) return a;                                  // unknown type -> default decode
            if (t == "BOOL") return probe.Type == "BOOL" ? a : null;  // bool only on a bit address
            if (probe.Type == "BOOL") return null;                    // numeric type on a bit address -> invalid

            var withType = ParseSpec(a + ":" + t);
            return withType.Error == null ? a + ":" + t : a;          // fall back to default decode on size mismatch
        }

        private static string? MapTiaDataType(string? dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return null;
            switch (dataType!.Trim().ToUpperInvariant())
            {
                case "BOOL": return "BOOL";
                case "BYTE": return "BYTE";
                case "SINT": return "SINT";
                case "USINT": return "USINT";
                case "INT": return "INT";
                case "UINT": return "UINT";
                case "WORD": return "WORD";
                case "DINT": return "DINT";
                case "UDINT": return "UDINT";
                case "DWORD": return "DWORD";
                case "REAL": return "REAL";
                default: return null;                                 // TIME/STRING/struct/etc -> not an S7 scalar read
            }
        }

        private static string DefaultTypeForSize(string sz)
        {
            switch (sz)
            {
                case "B": return "BYTE";
                case "W": return "WORD";
                case "D": return "DWORD";
                default: return "BYTE";
            }
        }

        private static void ApplyTypeOverride(S7ReadItem item, string? type, string sz)
        {
            if (string.IsNullOrEmpty(type)) return;
            int want = TypeSize(type!);
            int have = sz == "B" ? 1 : sz == "W" ? 2 : sz == "D" ? 4 : 1;
            if (type == "BOOL")
            {
                item.Error = $"Type BOOL is only valid for bit addresses (e.g. DB1.DBX2.3), not '{item.Spec}'.";
                return;
            }
            if (want != have)
            {
                item.Error = $"Type '{type}' ({want} byte(s)) does not match address size '{sz}' ({have} byte(s)).";
                return;
            }
            item.Type = type!;
        }
    }
}
