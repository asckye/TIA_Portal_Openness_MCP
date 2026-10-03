using System.Text;
using System.Text.Json.Nodes;

// Synthetic worker: no Siemens references, native loading or network operations.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var log = Environment.GetEnvironmentVariable("TIA_FIXTURE_LOG") ?? throw new ArgumentException("Fixture log required");
void Record(object value) => File.AppendAllText(log, System.Text.Json.JsonSerializer.Serialize(value) + "\n", new UTF8Encoding(false));
Record(new { stage = "start", pid = Environment.ProcessId, args });
int? attached = null;
while (Console.ReadLine() is { } line)
{
    var request = JsonNode.Parse(line)!;
    var operation = request["operation"]!.GetValue<string>();
    Record(new { stage = "call", pid = Environment.ProcessId, operation });
    JsonNode result;
    switch (operation)
    {
        case "Attach":
            attached = request["arguments"]!["processId"]!.GetValue<int>();
            result = new JsonObject { ["Stage"]="attached", ["OwnsPortal"]=false, ["AttemptedPids"]=new JsonArray(attached.Value), ["Strategy"]="non-owning-attachment-only", ["LaunchMode"]="never" };
            break;
        case "ReadProjectTree": result = JsonValue.Create("生产线 / PLC_测试 / 程序块"); break;
        case "Disconnect":
            result = new JsonObject { ["Stage"]="disconnected", ["SessionState"]="terminal", ["Strategy"]="non-owning-attachment-only", ["WorkerAcknowledged"]=true, ["Detached"]=true, ["ProcessId"]=attached, ["SavedProject"]=false, ["ClosedProject"]=false, ["LaunchMode"]="never" };
            break;
        default: throw new InvalidOperationException("Unexpected fixture operation: " + operation);
    }
    Console.WriteLine(new JsonObject { ["id"]=request["id"]!.DeepClone(), ["result"]=result }.ToJsonString());
}
Record(new { stage = "exit", pid = Environment.ProcessId });
