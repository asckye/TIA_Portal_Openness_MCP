#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TiaMcp.WorkerChannel;

public static class WorkerAssemblyMetadata
{
    // Inspection never loads the net48 worker or any Siemens dependency into a host.
    public static IReadOnlyDictionary<string, string?> Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new BadImageFormatException("Invalid worker assembly metadata.");
            string key = blob.ReadSerializedString() ?? throw new BadImageFormatException("Missing worker metadata key.");
            values.Add(key, blob.ReadSerializedString());
        }
        return values;
    }
}
#endif
