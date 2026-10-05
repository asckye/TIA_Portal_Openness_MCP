using System;
using System.Text.Json;

namespace TiaMcp.Logic.V4
{
    public interface IBehaviorCandidateContract
    {
        JsonElement InputSchema { get; }
    }

    public sealed class DeviceCreationContract : IBehaviorCandidateContract
    {
        public JsonElement InputSchema => JsonSerializer.Deserialize<JsonElement>(@"{
            ""type"":""object"",""additionalProperties"":false,
            ""required"":[""typeIdentifier"",""deviceName"",""family""],
            ""properties"":{
                ""typeIdentifier"":{""type"":""string"",""minLength"":1,""maxLength"":512,""description"":""Exact TypeIdentifier from the installed catalog; no normalization or candidate probing.""},
                ""deviceName"":{""type"":""string"",""minLength"":1,""maxLength"":128},
                ""family"":{""type"":""string"",""enum"":[""S7-1200"",""S7-1500"",""HMI"",""WinCCUnifiedPC"",""GSD"",""Hardware""]},
                ""mode"":{""type"":""string"",""enum"":[""preview"",""apply""],""default"":""preview""},
                ""confirm"":{""type"":""boolean"",""default"":false},
                ""expectedPlanHash"":{""type"":""string"",""default"":""""},
                ""expectedProjectFile"":{""type"":""string"",""default"":""""}
            }}");
    }
}
