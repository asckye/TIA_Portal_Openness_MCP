using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcFoundation;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class BatchReplacementPolicyTests
    {
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Only_overwrite_collisions_inspect_properties_and_unknown_consistency_blocks_before_callbacks(bool program)
        {
            string folder = Path.GetFullPath(Path.Combine("bin-build/P6-68/batch-properties", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "A.xml"), "<Document><Engineering version=\"V17\"/><SW.Blocks.FC><AttributeList><Name>A</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
                var target = new PlcBatchImportObject { Name = "A", Kind = "FC", Number = 1 };
                var unrelated = new PlcBatchImportObject { Name = "Unrelated", Kind = "FC", Number = 99 };
                var request = new PlcBatchImportRequest { Release = "17", Project = "C:/fixture.ap17", Software = "CPU/PLC", Directory = folder, Program = program, Overwrite = true };
                var inspected = new List<string>(); int mutations = 0;
                PlcBatchImportResult Run(IEnumerable<PlcBatchImportObject> inventory) => PlcBatchImportPolicy.Run(request, inventory,
                    () => mutations++, (_, item) => { mutations++; return new[] { item }; },
                    (_, _) => mutations++, (_, item) => { mutations++; return new[] { item }; }, () => { mutations++; return folder; }, () => mutations++,
                    item => { inspected.Add(item.Name); throw new IOException("Native property getter refused"); });
                var result = Run(new[] { target, unrelated });
                Assert.Equal(new[] { "A" }, inspected); Assert.Equal("replace-blocked: unknown-consistency", result.Items[0].Action); Assert.Equal(0, mutations);
                request.DryRun = false; request.Confirm = true; request.ExpectedProject = request.Project; request.ExpectedHash = result.PlanHash; request.Order = new[] { "A.xml" };
                Assert.False(Run(new[] { target, unrelated }).Executed); Assert.Equal(0, mutations);
                request.DryRun = true; request.Overwrite = false; inspected.Clear();
                Assert.Throws<AdapterPreconditionException>(() => Run(new[] { target, unrelated })); Assert.Empty(inspected);
                Assert.Equal("create", Run(new[] { unrelated }).Items[0].Action); Assert.Empty(inspected);
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
