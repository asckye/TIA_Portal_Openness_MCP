using System;
using System.Globalization;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class ScalarDateFormattingTests
    {
        [Theory]
        [InlineData(DateTimeKind.Utc, "hmi")]
        [InlineData(DateTimeKind.Local, "hmi")]
        [InlineData(DateTimeKind.Unspecified, "hmi")]
        [InlineData(DateTimeKind.Utc, "hardware")]
        [InlineData(DateTimeKind.Local, "hardware")]
        [InlineData(DateTimeKind.Unspecified, "hardware")]
        public void Scalar_dates_preserve_fractional_seconds_and_kind(DateTimeKind kind, string family)
        {
            var date = new DateTime(2026, 11, 14, 12, 34, 56, kind).AddTicks(1234567);
            const string timestamp = "2026-11-14T12:34:56.1234567";
            string suffix = kind == DateTimeKind.Utc ? "Z" : kind == DateTimeKind.Unspecified ? ""
                : new DateTimeOffset(date).ToString("zzz", CultureInfo.InvariantCulture);
            object? rendered = family == "hmi" ? HmiScalar.Json(date)
                : ((System.Collections.Generic.Dictionary<string, object?>)HardwareScalarEvidence.Read(new DateProperty { Value = date })["values"]!)["Value"];

            string text = Assert.IsType<string>(rendered);
            Assert.Equal(timestamp + suffix, text);
            var parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(date.Ticks, parsed.Ticks);
            Assert.Equal(kind, parsed.Kind);
        }

        public sealed class DateProperty
        {
            public DateTime Value { get; set; }
        }
    }
}
