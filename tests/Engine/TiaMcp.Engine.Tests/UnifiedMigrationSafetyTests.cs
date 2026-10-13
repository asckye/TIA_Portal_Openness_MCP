using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class UnifiedMigrationSafetyTests
    {
        private sealed class DisposedGraph
        {
            public string A => "retained";
            public string B => throw new ObjectDisposedException("synthetic HMI handle");
            public int Reads;
            public string C { get { Reads++; return "must not be read"; } }
        }

        [Fact]
        public void A_disposed_handle_stops_the_graph_before_sibling_getters()
        {
            var target = new DisposedGraph();
            var rows = MigrationRead.Graph(target, "/selected").ToArray();
            Assert.Contains(rows, row => (string?)row["path"] == "/selected/A");
            Assert.Contains(rows, row => (bool?)row["remoteInspectionStopped"] == true);
            Assert.Equal(0, target.Reads);
        }

        [Fact]
        public void A_disposed_handle_invalidates_the_page_continuation()
        {
            using var pages = new MigrationPages();
            var target = new DisposedGraph();
            var result = pages.Read(new object(), "{}", "", 100, 5000, () => MigrationRead.Graph(target, "/selected"));
            Assert.False((bool)result["apiCallSuccess"]!);
            Assert.False((bool)result["traversalComplete"]!);
            Assert.True((bool)result["truncated"]!);
            Assert.Null(result["nextCursor"]);
            Assert.Equal(0, target.Reads);
        }
        private sealed class RemoteAttributes : Siemens.Engineering.IEngineeringObject
        {
            internal readonly AttributeInfos Infos = new AttributeInfos();
            public System.Collections.Generic.List<GraphicSelectionTests.Info> GetAttributeInfos() => Infos;
            public System.Collections.Generic.List<GraphicSelectionTests.Info> GetCompositionInfos() => new System.Collections.Generic.List<GraphicSelectionTests.Info>();
            public object GetAttribute(string name) => throw new ObjectDisposedException("remote attribute");
        }

        private sealed class AttributeInfos : System.Collections.Generic.List<GraphicSelectionTests.Info>, System.Collections.IEnumerable
        {
            internal int Disposals, Moves;
            internal bool FailDispose;
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new RemoteIterator(this);
        }

        private sealed class RemoteIterator(AttributeInfos owner) : System.Collections.IEnumerator, IDisposable
        {
            public object Current => new GraphicSelectionTests.Info { Name = "Remote" };
            public bool MoveNext() => ++owner.Moves == 1;
            public void Reset() => throw new NotSupportedException();
            public void Dispose()
            {
                owner.Disposals++;
                if (owner.FailDispose) throw new ObjectDisposedException("remote enumerator");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fatal_attribute_read_stops_traversal_and_guardedly_releases_its_enumerator(bool failDispose)
        {
            var target = new RemoteAttributes(); target.Infos.FailDispose = failDispose;
            var rows = MigrationRead.Graph(target, "/selected").ToArray();
            Assert.Contains(rows, row => (string?)row["code"] == "AttributeReadFailed" && (bool?)row["remoteInspectionStopped"] == true);
            Assert.Equal(1, target.Infos.Moves);
            Assert.Equal(1, target.Infos.Disposals);
            Assert.Equal(failDispose ? 1 : 0, rows.Count(row => (string?)row["code"] == "EnumeratorDisposeFailed"));
            if (failDispose)
                Assert.Contains(rows, row => (string?)row["code"] == "EnumeratorDisposeFailed" && (bool?)row["remoteInspectionStopped"] == true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Page_abort_keeps_the_failed_enumerator_release_evidence(bool failDispose)
        {
            var target = new RemoteAttributes(); target.Infos.FailDispose = failDispose;
            using var pages = new MigrationPages();
            var result = pages.Read(new object(), "{}", "", 100, 5000, () => MigrationRead.Graph(target, "/selected"));
            Assert.False((bool)result["apiCallSuccess"]!);
            Assert.Null(result["nextCursor"]);
            Assert.Equal(1, target.Infos.Disposals);
            Assert.Equal(1, target.Infos.Moves);
            var failed = result["records"]!.AsArray().Single(row => (string?)row?["code"] == "AttributeReadFailed")!;
            Assert.Equal(failDispose, failed["enumeratorDisposeFailure"] != null);
            if (failDispose) Assert.Equal("EnumeratorDisposeFailed", (string?)failed["enumeratorDisposeFailure"]!["code"]);
        }

    }
}
