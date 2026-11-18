using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Exchange
{
    internal static class DataBlockStartValueLiterals
    {
        // Siemens STEP 7 online help: "Constants" and "Elementary data types" (TIA Portal V21).
        // SimaticML StartValue is text, not an XML number: SDK SW.InterfaceSections_v3/v5.xsd, StartValue_T.
        // Local export-layout examples: templates/plc/block-xml/DB_HMI_Interface.xml (false, 10, 100.0)
        // and tests/FoundationHost/TiaMcp.Engine.Tests/Fixtures/PlcRender/Catalog/Primer.xml (T#2s).
        // Keep the template's evidenced radix; never infer radix or silently round Excel's numeric text.
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly Dictionary<string, (decimal Min, decimal Max)> Integers =
            new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase) {
                ["SInt"] = (sbyte.MinValue, sbyte.MaxValue), ["Int"] = (short.MinValue, short.MaxValue),
                ["DInt"] = (int.MinValue, int.MaxValue), ["LInt"] = (long.MinValue, long.MaxValue),
                ["USInt"] = (0, byte.MaxValue), ["UInt"] = (0, ushort.MaxValue), ["UDInt"] = (0, uint.MaxValue),
                ["ULInt"] = (0, ulong.MaxValue), ["Byte"] = (0, byte.MaxValue), ["Word"] = (0, ushort.MaxValue),
                ["DWord"] = (0, uint.MaxValue), ["LWord"] = (0, ulong.MaxValue) };
        internal static readonly string[] SystemScalars = { "HW_IO", "HW_ANY", "HW_DEVICE", "HW_MODULE", "HW_SUBMODULE", "DB_ANY" };

        internal static bool AdditionalScalar(string type, string value)
        {
            if (value.Length == 0 || !(SystemScalars.Contains(type, StringComparer.OrdinalIgnoreCase)
                || Regex.IsMatch(type, "\\AW?String\\[(?:[A-Za-z_][A-Za-z0-9_]*|\"[^\"]+\")\\]\\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return false;
            try { Normalize(type, value, value); return true; }
            catch (ArgumentException) /* swallow(parse-fallback): a non-literal is excluded and reported through NotEditableReasons. */
            { return false; }
        }

        internal static (string Text, bool IsDefault) Normalize(string type, string value, string exported)
        {
            string name = type.ToUpperInvariant();
            if (name == "BOOL")
            {
                if (!bool.TryParse(value, out bool boolean)) throw new ArgumentException("Bool requires true or false.");
                return (boolean ? "true" : "false", !boolean);
            }
            if (Integers.TryGetValue(type, out var range)) return Integer(value, exported, range.Min, range.Max);
            if (SystemScalars.Contains(type, StringComparer.OrdinalIgnoreCase)) return Integer(value, exported, 0, ushort.MaxValue);
            if (name == "REAL" || name == "LREAL") return Real(name, value);
            if (name is "TIME" or "LTIME" or "S5TIME") return Duration(name, value);
            if (name == "DATE") return Date(value);
            if (name is "TIME_OF_DAY" or "LTIME_OF_DAY") return TimeOfDay(name, value);
            if (name is "DATE_AND_TIME" or "LDT") return DateTimeLiteral(name, value);
            if (name is "CHAR" or "WCHAR" || name.StartsWith("STRING", StringComparison.Ordinal)
                || name.StartsWith("WSTRING", StringComparison.Ordinal)) return Quoted(type, value);
            throw new ArgumentException("No supported scalar literal format for DataType " + type + ".");
        }

        private static (string, bool) Integer(string value, string exported, decimal min, decimal max)
        {
            var original = Regex.Match(exported, @"\A(16|2|8)#([0-9A-Fa-f]+)\z", RegexOptions.CultureInvariant);
            var radix = Regex.Match(value, @"\A(16|2|8)#([0-9A-Fa-f]+)\z", RegexOptions.CultureInvariant);
            decimal number;
            if (radix.Success)
            {
                if (!original.Success || original.Groups[1].Value != radix.Groups[1].Value)
                    throw new ArgumentException("Decimal integer required; radix notation needs the same form in this member's export.");
                try { number = Convert.ToUInt64(radix.Groups[2].Value, int.Parse(radix.Groups[1].Value, Invariant)); }
                catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException)
                { throw new ArgumentException("Invalid or out-of-range integer literal."); }
            }
            else if (!Regex.IsMatch(value, @"\A[+-]?[0-9]+\z", RegexOptions.CultureInvariant)
                || !decimal.TryParse(value, NumberStyles.AllowLeadingSign, Invariant, out number))
                throw new ArgumentException("Decimal integer required; Excel fractions, exponents and locale separators are refused.");
            if (number < min || number > max) throw new ArgumentException("Integer must be within " + min.ToString(Invariant) + ".."
                + max.ToString(Invariant) + ".");
            string text = number.ToString("0", Invariant);
            if (original.Success)
            {
                if (number < 0) throw new ArgumentException("A negative value cannot retain this unsigned export radix.");
                int basis = int.Parse(original.Groups[1].Value, Invariant);
                ulong remaining = (ulong)number; var digits = new StringBuilder();
                do { digits.Insert(0, "0123456789ABCDEF"[(int)(remaining % (ulong)basis)]); remaining /= (ulong)basis; } while (remaining != 0);
                text = basis.ToString(Invariant) + "#" + digits.ToString().PadLeft(original.Groups[2].Length, '0');
            }
            return (text, number == 0);
        }

        private static (string, bool) Real(string type, string value)
        {
            if (!Regex.IsMatch(value, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[Ee][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant)
                || !double.TryParse(value, NumberStyles.Float, Invariant, out double number) || double.IsInfinity(number))
                throw new ArgumentException(type + " requires a finite invariant decimal literal.");
            string shortest;
            if (type == "REAL")
            {
                float single = (float)number;
                if (float.IsInfinity(single)) throw new ArgumentException("Real is outside the finite 32-bit range.");
                shortest = single.ToString("R", Invariant);
            }
            else shortest = number.ToString("R", Invariant);
            string text = RealText(shortest);
            if (NumberKey(value) != NumberKey(shortest))
                throw new ArgumentException(type + " precision would change the supplied value; use suggested value " + text + ".");
            return (text, number == 0);
        }

        private static string RealText(string value)
        {
            var parts = value.ToUpperInvariant().Split('E');
            return (parts[0].Contains(".") ? parts[0] : parts[0] + ".0") + (parts.Length == 1 ? "" : "E" + parts[1]);
        }
        private static string NumberKey(string value)
        {
            var parts = value.ToUpperInvariant().Split('E');
            int exponent = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, Invariant, out exponent))
                throw new ArgumentException("Numeric exponent is outside the supported literal range.");
            string mantissa = parts[0].TrimStart('+'); bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
            mantissa = mantissa.TrimStart('-'); int point = mantissa.IndexOf('.');
            if (point >= 0) exponent -= mantissa.Length - point - 1;
            string digits = mantissa.Replace(".", "").TrimStart('0');
            if (digits.Length == 0) return "0";
            int length = digits.Length; digits = digits.TrimEnd('0'); exponent += length - digits.Length;
            return (negative ? "-" : "") + digits + "E" + exponent.ToString(Invariant);
        }

        private static (string, bool) Duration(string type, string value)
        {
            string prefix = type == "TIME" ? "T" : type == "LTIME" ? "LT" : "S5T";
            var literal = Regex.Match(value, @"\A(?:" + prefix + "|" + type + @")#(-?)(.+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!literal.Success) throw new ArgumentException(type + " requires its typed literal prefix " + prefix + "#.");
            string body = literal.Groups[2].Value;
            var units = new[] { "d", "h", "m", "s", "ms", "us", "ns" };
            var factors = new[] { 86400000000000m, 3600000000000m, 60000000000m, 1000000000m, 1000000m, 1000m, 1m };
            var components = Regex.Matches(body, @"([0-9]+)(ms|us|ns|d|h|m|s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!Regex.IsMatch(body, @"\A[0-9]+(?:ms|us|ns|d|h|m|s)(?:_?[0-9]+(?:ms|us|ns|d|h|m|s))*\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new ArgumentException("Duration requires whole typed units in d/h/m/s/ms/us/ns order.");
            decimal total = 0; int previous = -1;
            foreach (Match component in components)
            {
                int index = Array.IndexOf(units, component.Groups[2].Value.ToLowerInvariant());
                if (index <= previous || !decimal.TryParse(component.Groups[1].Value, NumberStyles.None, Invariant, out var count))
                    throw new ArgumentException("Invalid or repeated duration unit.");
                try { total = checked(total + count * factors[index]); }
                catch (OverflowException) { throw new ArgumentException("Duration is outside its type range."); }
                previous = index;
            }
            if (literal.Groups[1].Length != 0) total = -total;
            decimal minimum = type == "TIME" ? int.MinValue * 1000000m : type == "LTIME" ? long.MinValue : 0;
            decimal maximum = type == "TIME" ? int.MaxValue * 1000000m : type == "LTIME" ? long.MaxValue : 9990000000000m;
            if (total < minimum || total > maximum || type != "LTIME" && total % 1000000 != 0)
                throw new ArgumentException("Duration is outside its type range or resolution.");
            if (type == "S5TIME" && !new[] { 10000000m, 100000000m, 1000000000m, 10000000000m }
                .Any(basis => total % basis == 0 && total / basis <= 999))
                throw new ArgumentException("S5Time requires an exact 10/100/1000/10000 ms base with at most 999 units.");
            decimal remaining = Math.Abs(total); var result = new List<string>();
            for (int i = 0; i < units.Length; i++)
            {
                decimal count = decimal.Truncate(remaining / factors[i]); remaining %= factors[i];
                if (count != 0) result.Add(count.ToString("0", Invariant) + units[i]);
            }
            return (prefix + "#" + (total < 0 ? "-" : "") + (result.Count == 0 ? "0ms" : string.Join("_", result)), total == 0);
        }

        private static (string, bool) Date(string value)
        {
            var match = Regex.Match(value, @"\A(?:D|DATE)#([0-9]{4}-[0-9]{1,2}-[0-9]{1,2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, new[] { "yyyy-M-d", "yyyy-MM-dd", "yyyy-M-dd", "yyyy-MM-d" },
                Invariant, DateTimeStyles.None, out var date) || date < new DateTime(1990, 1, 1) || date > new DateTime(2168, 12, 31))
                throw new ArgumentException("Date requires D#yyyy-MM-dd within 1990-01-01..2168-12-31.");
            return ("D#" + date.ToString("yyyy-MM-dd", Invariant), date == new DateTime(1990, 1, 1));
        }

        private static (string, bool) TimeOfDay(string type, string value)
        {
            bool wide = type == "LTIME_OF_DAY"; string prefix = wide ? "LTOD" : "TOD";
            var match = Regex.Match(value, @"\A(?:" + prefix + "|" + type + @")#([0-9]{1,2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]+))?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) throw new ArgumentException(type + " requires " + prefix + "#HH:mm:ss with an optional fractional second.");
            int hour = int.Parse(match.Groups[1].Value, Invariant), minute = int.Parse(match.Groups[2].Value, Invariant),
                second = int.Parse(match.Groups[3].Value, Invariant), precision = wide ? 9 : 3;
            string fraction = match.Groups[4].Value;
            if (hour > 23 || minute > 59 || second > 59 || fraction.Length > precision)
                throw new ArgumentException(type + " is outside its clock range or resolution.");
            return (prefix + "#" + hour.ToString("00", Invariant) + ":" + minute.ToString("00", Invariant) + ":" + second.ToString("00", Invariant)
                + "." + fraction.PadRight(precision, '0'), hour == 0 && minute == 0 && second == 0 && fraction.Trim('0').Length == 0);
        }

        private static (string, bool) DateTimeLiteral(string type, string value)
        {
            bool wide = type == "LDT"; string prefix = wide ? "LDT" : "DT";
            var match = Regex.Match(value, @"\A(?:" + prefix + "|" + type + @")#([0-9]{4}-[0-9]{2}-[0-9]{2})-(.+)\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date)
                || date < new DateTime(wide ? 1970 : 1990, 1, 1) || date > new DateTime(wide ? 2262 : 2089, wide ? 4 : 12, wide ? 11 : 31))
                throw new ArgumentException(type + " requires " + prefix + "#yyyy-MM-dd-HH:mm:ss with a date in its type range.");
            var time = TimeOfDay(wide ? "LTIME_OF_DAY" : "TIME_OF_DAY", (wide ? "LTOD#" : "TOD#") + match.Groups[2].Value);
            string clock = time.Item1.Substring(time.Item1.IndexOf('#') + 1);
            if (wide)
            {
                decimal nanoseconds = (date - new DateTime(1970, 1, 1)).Ticks * 100m
                    + decimal.Parse(clock.Substring(0, 2), Invariant) * 3600000000000m
                    + decimal.Parse(clock.Substring(3, 2), Invariant) * 60000000000m
                    + decimal.Parse(clock.Substring(6, 2), Invariant) * 1000000000m + decimal.Parse(clock.Substring(9), Invariant);
                if (nanoseconds > long.MaxValue) throw new ArgumentException("LDT is outside its nanosecond range.");
            }
            return (prefix + "#" + date.ToString("yyyy-MM-dd", Invariant) + "-" + clock,
                date == new DateTime(wide ? 1970 : 1990, 1, 1) && time.Item2);
        }

        private static (string, bool) Quoted(string type, string value)
        {
            bool wide = type.StartsWith("W", StringComparison.OrdinalIgnoreCase);
            bool character = type.Equals(wide ? "WChar" : "Char", StringComparison.OrdinalIgnoreCase);
            string prefix = wide ? character ? "WCHAR#" : "WSTRING#" : "";
            var match = Regex.Match(value, @"\A(?:(?:W?CHAR|W?STRING)#)?'((?:[^'$]|\$(?:[0-9A-Fa-f]{2,4}|[$'NnRrTtLlPp]))*)'\z",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success) throw new ArgumentException("Char/String requires a single-quoted Siemens literal; escape quotes as $'.");
            int hash = value.IndexOf('#');
            if (hash >= 0 && !value.Substring(0, hash).Equals(wide ? character ? "WCHAR" : "WSTRING" : character ? "CHAR" : "STRING",
                StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Quoted literal prefix does not match DataType.");
            string body = match.Groups[1].Value; var decoded = new StringBuilder();
            if (body.Any(char.IsControl)) throw new ArgumentException("Control characters require Siemens dollar escapes in quoted literals.");
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c != '$') { decoded.Append(c); continue; }
                c = body[++i];
                if (Uri.IsHexDigit(c))
                {
                    int count = wide ? 4 : 2;
                    if (i + count > body.Length || !body.Substring(i, count).All(Uri.IsHexDigit))
                        throw new ArgumentException("Hex string escape must have " + count.ToString(Invariant) + " digits.");
                    decoded.Append((char)int.Parse(body.Substring(i, count), NumberStyles.HexNumber, Invariant)); i += count - 1;
                }
                else decoded.Append(char.ToUpperInvariant(c) switch { 'N' or 'L' => '\n', 'R' => '\r', 'T' => '\t', 'P' => '\f', _ => c });
            }
            int maximum = character ? 1 : wide ? 65534 : 254;
            var capacity = Regex.Match(type, @"\[([0-9]+)\]\z", RegexOptions.CultureInvariant);
            if (capacity.Success && int.TryParse(capacity.Groups[1].Value, NumberStyles.None, Invariant, out int declared)) maximum = declared;
            if (decoded.Length > maximum || character && decoded.Length != 1 || !wide && decoded.ToString().Any(c => c > 255)
                || decoded.ToString().Any(char.IsSurrogate)) throw new ArgumentException("Quoted value exceeds the character type or declared capacity.");
            return (prefix + "'" + body + "'", character ? decoded[0] == '\0' : decoded.Length == 0);
        }
    }
}
