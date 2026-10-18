using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Shared;
using TiaMcp.Logic.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class ImportStagingTests : IDisposable
    {
        internal static async Task VerifyApproval(PendingApproval pending)
        {
            pending.Validate();
            var display = JsonNode.Parse(pending.ParametersJson)!;
            Assert.Null(display["files"]![0]!["content"]);
            Assert.NotNull(display["files"]![0]!["sha256"]);
            Assert.NotNull(display["stagingLocation"]);
            using var wire = new MemoryStream();
            await ApprovalFrames.Write(wire, pending, CancellationToken.None);
            Assert.True(wire.Length < ApprovalFrames.MaximumBytes);
            wire.Position = 0;
            var received = await ApprovalFrames.Read<PendingApproval>(wire, CancellationToken.None);
            received.Validate(); Assert.Equal(pending.ArgumentDigest, received.ArgumentDigest);
            wire.SetLength(0); wire.Position = 0;
            await ApprovalFrames.Write(wire, new ApprovalDecision { RequestId = received.RequestId, PlanHash = received.PlanHash, ArgumentDigest = received.ArgumentDigest, Decision = "granted" }, CancellationToken.None);
            wire.Position = 0;
            Assert.True((await ApprovalFrames.Read<ApprovalDecision>(wire, CancellationToken.None)).Matches(pending));
        }
        private readonly string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private ImportStagingStore Store(string release = "17") { Directory.CreateDirectory(bundle); return new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N")); }
        private static StagedTextFile File(string name = "F.scl", string kind = "scl", string content = "FUNCTION F : Void\nBEGIN\nEND_FUNCTION\n") => new StagedTextFile { FileName = name, Kind = kind, Content = content };
        public void Dispose()
        {
            // The fixture bundle is a fresh short folder under the temp root (CI checkouts are deep).
            if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
        }
    }
}
