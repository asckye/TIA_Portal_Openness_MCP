using System;

namespace TiaMcp.Adapters.Contracts.Paging
{
    public static class PlcTypeExportPage
    {
        public static void Validate(int offset, int limit, bool dryRun)
        {
            if (offset < 0 || offset > 256) throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 32) throw new ArgumentOutOfRangeException(nameof(limit));
            if (!dryRun && offset != 0) throw new ArgumentException("Page offsets apply only to preview.", nameof(offset));
            if (!dryRun && limit != 32)
                throw new ArgumentException("Page limits apply only to preview. Apply exports the entire reviewed inventory.", nameof(limit));
        }
    }
}
