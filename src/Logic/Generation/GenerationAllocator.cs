using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    public sealed class GenerationAllocator
    {
        private readonly HardwarePartAllocation? allocation;
        private readonly Dictionary<string, string> io = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> ips = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(long Start, long End, string Owner)>> occupied = new Dictionary<string, List<(long, long, string)>>(StringComparer.Ordinal);
        private readonly Dictionary<string, StructurePartNumberRangesItem> ranges;
        private readonly Dictionary<string, Dictionary<int, string>> numbers = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> assignedNumbers = new Dictionary<string, int>(StringComparer.Ordinal);

        public GenerationAllocator(HardwarePart hardware, StructurePart structure, AlarmsPartNumbering? alarmNumbering = null)
        {
            GenerationDocuments.Canonical(hardware);
            GenerationDocuments.Canonical(structure);
            allocation = hardware.Allocation;
            ranges = (structure.NumberRanges ?? new List<StructurePartNumberRangesItem>()).ToDictionary(r => r.Id, StringComparer.Ordinal);
            if (alarmNumbering != null && !ranges.ContainsKey("alarms")) ranges.Add("alarms", new StructurePartNumberRangesItem { Id = "alarms", Kind = "alarm", Start = alarmNumbering.Start, End = alarmNumbering.End });
            foreach (var range in ranges.Values)
            {
                if (range.Start > range.End) throw CanonicalJson.Failure("/numberRanges/" + range.Id, "range", "Number range is reversed.");
                foreach (var other in ranges.Values.Where(r => r.Id != range.Id && r.Kind == range.Kind))
                    if (range.Start <= other.End && other.Start <= range.End) throw CanonicalJson.Failure("/numberRanges", "collision", "Number ranges of the same kind overlap.");
            }
            var pools = allocation?.Io ?? new List<HardwarePartAllocationIoItem>();
            foreach (var pool in pools)
            {
                var start = IoAddress.Parse(pool.Start);
                var end = IoAddress.Parse(pool.End);
                RequireDirection(start, pool.Direction);
                RequireDirection(end, pool.Direction);
                if (start.Start > end.Start || start.Width != end.Width) throw CanonicalJson.Failure("/allocation/io/" + pool.Id, "range", "IO range endpoints are reversed or use different widths.");
                foreach (var other in pools.Where(p => p.Id != pool.Id))
                {
                    var first = IoAddress.Parse(other.Start); var last = IoAddress.Parse(other.End);
                    if (start.Area == first.Area && start.Start <= last.End && first.Start <= end.End)
                        throw CanonicalJson.Failure("/allocation/io", "collision", "IO allocation ranges overlap in the same memory area.");
                }
            }
        }

        internal void Prepare(MachineDescription machine, IReadOnlyDictionary<string, DeviceTypeRule> rules)
        {
            var requests = machine.Devices.OrderBy(d => d.Station, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal)
                .SelectMany(d => rules[d.Type].Signals.Where(s => s.Optional != true || d.Io.ContainsKey(s.Role))
                    .OrderBy(s => s.Role, StringComparer.Ordinal).Select(s => (Device: d, Signal: s))).ToArray();
            foreach (var request in requests)
            {
                var d = request.Device; var s = request.Signal;
                var key = SignalKey(d, s.Role);
                if (!d.Io.TryGetValue(s.Role, out var requested) || requested == "auto") continue;
                var address = IoAddress.Parse(requested);
                RequireDirection(address, s.Dir);
                RequireWidth(address, s.Type ?? DefaultType(s.Dir));
                CheckRange(address, s.Dir);
                Reserve(d.Station, address, key);
                io.Add(key, address.Text);
            }
            foreach (var request in requests)
            {
                var d = request.Device; var s = request.Signal; var key = SignalKey(d, s.Role);
                if (io.ContainsKey(key)) continue;
                var width = Width(s.Type ?? DefaultType(s.Dir));
                var candidates = (allocation?.Io ?? new List<HardwarePartAllocationIoItem>()).Where(p => p.Direction == s.Dir).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
                IoAddress? selected = null;
                foreach (var pool in candidates)
                {
                    var start = IoAddress.Parse(pool.Start); var end = IoAddress.Parse(pool.End);
                    for (var bit = start.Start; bit + width - 1 <= end.End; bit += width == 1 ? 1 : 8)
                    {
                        if (width != 1 && bit % 8 != 0) continue;
                        var address = new IoAddress(start.Area, bit, width);
                        if (!Collides(d.Station, address)) { selected = address; break; }
                    }
                    if (selected != null) break;
                }
                if (selected == null) throw CanonicalJson.Failure("/allocation/io/" + key, "range-exhausted", "No free IO address for " + s.Dir + ".");
                Reserve(d.Station, selected, key);
                io.Add(key, selected.Text);
            }
            PrepareIps(machine);
        }

        private void PrepareIps(MachineDescription machine)
        {
            uint first = 0, last = 0, network = 0, mask = 0;
            if (allocation?.Ip != null)
            {
                var parts = allocation.Ip.Subnet.Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix < 1 || prefix > 30)
                    throw CanonicalJson.Failure("/allocation/ip/subnet", "range", "Use an IPv4 CIDR subnet with prefix 1..30.");
                network = ParseIp(parts[0]); mask = uint.MaxValue << (32 - prefix);
                if ((network & mask) != network) throw CanonicalJson.Failure("/allocation/ip/subnet", "range", "Subnet must use its network address.");
                first = ParseIp(allocation.Ip.Start); last = ParseIp(allocation.Ip.End);
                if (first > last || !InSubnet(first) || !InSubnet(last)) throw CanonicalJson.Failure("/allocation/ip", "range", "IP range is reversed or includes non-host/out-of-subnet addresses.");
            }
            var used = new HashSet<uint>();
            foreach (var station in machine.Stations.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                if (station.Ip == null || station.Ip == "auto") continue;
                var ip = ParseIp(station.Ip);
                if (allocation?.Ip != null && (ip < first || ip > last || !InSubnet(ip))) throw CanonicalJson.Failure("/stations/" + station.Id + "/ip", "range", "Explicit IP is outside the allocation range.");
                if (!used.Add(ip)) throw CanonicalJson.Failure("/stations/" + station.Id + "/ip", "collision", "Duplicate IP address.");
                ips.Add(station.Id, FormatIp(ip));
            }
            foreach (var station in machine.Stations.Where(s => s.Ip == "auto").OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                if (allocation?.Ip == null) throw CanonicalJson.Failure("/allocation/ip", "range", "Automatic IP requires an allocation range.");
                var candidate = first;
                while (candidate <= last && used.Contains(candidate)) candidate++;
                if (candidate > last) throw CanonicalJson.Failure("/allocation/ip", "range-exhausted", "IP range is exhausted.");
                used.Add(candidate); ips.Add(station.Id, FormatIp(candidate));
            }
            bool InSubnet(uint value) => (value & mask) == network && value != network && value != (network | ~mask);
        }

        public string Io(string station, string device, string role)
        {
            var key = station + "/" + device + "/" + role;
            if (!io.TryGetValue(key, out var value)) throw CanonicalJson.Failure("/signals/" + key, "reference", "Signal was not allocated (optional signal may be absent).");
            return value;
        }

        public string Ip(string station) => ips.TryGetValue(station, out var value) ? value
            : throw CanonicalJson.Failure("/stations/" + station + "/ip", "reference", "Station has no allocated IP.");

        public int Number(string rangeId, string owner, int? explicitNumber = null)
        {
            if (!ranges.TryGetValue(rangeId, out var range)) throw CanonicalJson.Failure("/numberRanges/" + rangeId, "reference", "Unknown number range.");
            var key = rangeId + "/" + owner;
            if (assignedNumbers.TryGetValue(key, out var assigned))
            {
                if (explicitNumber.HasValue && explicitNumber != assigned) throw CanonicalJson.Failure("/numberRanges/" + key, "collision", "Owner requested two different numbers.");
                return assigned;
            }
            if (!numbers.TryGetValue(range.Kind, out var used)) numbers.Add(range.Kind, used = new Dictionary<int, string>());
            var number = explicitNumber ?? range.Start;
            if (explicitNumber == null) while (number < range.End && used.ContainsKey(number)) number++;
            if (number < range.Start || number > range.End) throw CanonicalJson.Failure("/numberRanges/" + key, "range", "Explicit number is outside its range.");
            if (used.ContainsKey(number)) throw CanonicalJson.Failure("/numberRanges/" + key, explicitNumber == null ? "range-exhausted" : "collision", "Block/sequence number is already allocated.");
            used.Add(number, key); assignedNumbers.Add(key, number); return number;
        }

        internal static string SignalKey(MachineDescriptionDevicesItem device, string role) => device.Station + "/" + device.Id + "/" + role;
        internal static string DefaultType(string direction) => direction == "DI" || direction == "DO" ? "Bool" : "Int";
        internal static int Width(string type) => type.ToUpperInvariant() switch
        {
            "BOOL" => 1, "BYTE" or "SINT" or "USINT" or "CHAR" => 8,
            "WORD" or "INT" or "UINT" => 16, "DWORD" or "DINT" or "UDINT" or "REAL" or "TIME" => 32,
            "LWORD" or "LINT" or "ULINT" or "LREAL" or "LTIME" => 64,
            _ => throw CanonicalJson.Failure("/signals/type", "io-type", "Unsupported IO scalar type: " + type)
        };

        internal static uint ParseIp(string text)
        {
            var parts = text.Split('.');
            if (parts.Length != 4) throw CanonicalJson.Failure("/ip", "ip", "IPv4 address requires four decimal octets.");
            uint value = 0;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 1 && part[0] == '0' || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet))
                    throw CanonicalJson.Failure("/ip", "ip", "Invalid IPv4 octet.");
                value = (value << 8) | octet;
            }
            return value;
        }

        internal static string FormatIp(uint ip) => string.Join(".", new[] { ip >> 24, ip >> 16 & 255, ip >> 8 & 255, ip & 255 }.Select(n => n.ToString(CultureInfo.InvariantCulture)));
        private static void RequireDirection(IoAddress address, string direction)
        {
            if (address.Area != (direction == "DI" || direction == "AI" ? "I" : "Q")) throw CanonicalJson.Failure("/allocation/io", "direction", "IO address uses the wrong memory area.");
        }
        private static void RequireWidth(IoAddress address, string type)
        {
            if (address.Width != Width(type)) throw CanonicalJson.Failure("/allocation/io", "io-type", "Address width differs from the signal data type.");
        }
        private void CheckRange(IoAddress address, string direction)
        {
            var pools = (allocation?.Io ?? new List<HardwarePartAllocationIoItem>()).Where(p => p.Direction == direction).ToArray();
            if (pools.Length > 0 && !pools.Any(p => address.Start >= IoAddress.Parse(p.Start).Start && address.End <= IoAddress.Parse(p.End).End))
                throw CanonicalJson.Failure("/allocation/io", "range", "Explicit IO address is outside its direction's ranges.");
        }
        private bool Collides(string station, IoAddress address) => occupied.TryGetValue(station + "/" + address.Area, out var used)
            && used.Any(r => address.Start <= r.End && r.Start <= address.End);
        private void Reserve(string station, IoAddress address, string owner)
        {
            if (Collides(station, address)) throw CanonicalJson.Failure("/allocation/io/" + owner, "collision", "IO addresses overlap.");
            var key = station + "/" + address.Area;
            if (!occupied.TryGetValue(key, out var used)) occupied.Add(key, used = new List<(long, long, string)>());
            used.Add((address.Start, address.End, owner));
        }
    }

    internal sealed class IoAddress
    {
        internal string Area { get; }
        internal long Start { get; }
        internal int Width { get; }
        internal long End => Start + Width - 1;
        internal string Text => "%" + Area + (Width == 1 ? "" : Width == 8 ? "B" : Width == 16 ? "W" : Width == 32 ? "D" : "L")
            + (Start / 8).ToString(CultureInfo.InvariantCulture) + (Width == 1 ? "." + (Start % 8).ToString(CultureInfo.InvariantCulture) : "");
        internal IoAddress(string area, long start, int width) { Area = area; Start = start; Width = width; }
        internal static bool IsIo(string text) => Regex.IsMatch(text, @"\A%?[IQ](?:[0-9]|[BWDL])", RegexOptions.CultureInvariant);
        internal static IoAddress Parse(string text)
        {
            var match = Regex.Match(text, @"\A%?([IQ])(?:(\d+)\.([0-7])|([BWDL])(\d+))\z", RegexOptions.CultureInvariant);
            if (!match.Success) throw CanonicalJson.Failure("/address", "io-address", "Use an absolute I/Q bit, byte, word, double word or long word address.");
            var numeric = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[5].Value;
            if (!int.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)) throw CanonicalJson.Failure("/address", "range", "IO byte address exceeds Int32.");
            var width = match.Groups[2].Success ? 1 : match.Groups[4].Value switch { "B" => 8, "W" => 16, "D" => 32, _ => 64 };
            return new IoAddress(match.Groups[1].Value, (long)bytes * 8 + (width == 1 ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0), width);
        }
    }
}
