using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class B3TransactionTests
    {
        [Fact]
        public void Ported_group_subcalls_complete_in_the_existing_transaction_scope()
        {
            var operations = new List<string>();
            var service = new PlcOrganisationPortService((operation, args) => {
                Assert.Equal("plc-organisation.ExecutePlcOrganisation", operation);
                Assert.False((bool)args["dryRun"]!); Assert.True((bool)args["confirm"]!);
                Assert.Equal("fixture.ap21", (string?)args["expectedProjectFile"]);
                string name = (string)args["request"]!["Operation"]!; operations.Add(name);
                return new JsonObject { ["Data"] = new JsonObject { ["success"] = true, ["operationSuccess"] = true, ["dryRun"] = false, ["mayHaveChanged"] = true, ["verifiedAbsent"] = name == "DeleteEmptyPlcBlockGroup" } };
            }, () => true, () => "fixture.ap21");
            var tools = new PlcOrganisationTools(service); var transaction = new Transaction(); var meta = new JsonObject();
            bool committed = TransactionExecution.Run(2, () => transaction, index => {
                string name = index == 0 ? "CreatePlcTypeGroup" : "DeleteEmptyPlcBlockGroup";
                TransactionExecution.RequireSupported(name);
                var response = index == 0 ? tools.CreatePlcTypeGroup("PLC", "Types", false) : tools.DeleteEmptyPlcBlockGroup("PLC", "Empty", false);
                return (bool?)response.Meta?["operationSuccess"] == true;
            }, meta);
            Assert.True(committed); Assert.True(transaction.Disposed);
            Assert.Equal(new[] { "CreatePlcTypeGroup", "DeleteEmptyPlcBlockGroup" }, operations);
        }
        private sealed class Transaction : IEngineeringTransaction
        {
            public bool CanCommit => true;
            public bool CommitRequested { get; private set; }
            public bool IsCancellationRequested => false;
            public bool Disposed { get; private set; }
            public void Commit() => CommitRequested = true;
            public void Dispose() => Disposed = true;
        }
    }
}
