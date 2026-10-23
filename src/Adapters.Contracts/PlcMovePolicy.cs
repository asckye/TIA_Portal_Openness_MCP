using System;
using System.Globalization;

namespace TiaMcp.Adapters.Contracts
{
    // MovePlcBlockToGroup round trip rules (API audit 2026-10-19 F22). Checked before the source block is deleted.
    public static class PlcMovePolicy
    {
        public static string? Refusal(bool knowHowProtected, bool consistent)
        {
            if (knowHowProtected)
                return "The block is know-how protected: its export holds only the public interface and cannot be imported again, so moving it would lose its code. Nothing was changed; move it in TIA Portal.";
            if (!consistent)
                return "The block is inconsistent: TIA exports only consistent blocks. Compile it first; nothing was changed.";
            return null;
        }

        public static int? Number(object? raw)
            => raw != null && int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : (int?)null;

        public static string Text(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "(unreadable)";

        // Null when the number is unchanged; otherwise the report text.
        public static string? NumberChange(int? before, int? after)
            => before.HasValue && after.HasValue && before.Value == after.Value ? null
                : "its block number changed or could not be read back (before " + Text(before) + ", after " + Text(after) + "); check references to the old number.";
    }
}
