using System;
using System.Collections.Generic;

namespace TiaMcp.Logic.V4.Inputs
{
    public static class DriveFunctionPolicy
    {
        // StartdriveLogic.cs:30-45,208-280. This is the action policy for family V;
        // defaults (dataSet=0, encoderNumber=1) remain the future tool adapter's job.
        public static NativeValuePolicy Create(string action)
        {
            var fields = new Dictionary<string, InputSchema>(StringComparer.Ordinal);
            var required = new List<string>();
            void Add(string name, InputSchema schema, bool needed = true)
            { fields.Add(name, schema); if (needed) required.Add(name); }
            void DataSet() => Add("dataSet", InputSchema.Integer(0, 65535), false);
            void EncoderNumber() => Add("encoderNumber", InputSchema.Integer(0, 65535), false);
            void Entries() => Add("entries", InputSchema.Map(InputSchema.Scalar(), 1, 200, InputSchema.String(minLength: 1)));
            var rotaryLinear = InputSchema.String(allowed: new[] { "Rotary", "Linear" });
            var absoluteIncremental = InputSchema.String(allowed: new[] { "Absolute", "Incremental" });
            switch (action)
            {
                case "read": case "updateCheckSums": break;
                case "changeDriveObjectType": Add("driveObjectType", InputSchema.String(minLength: 1)); break;
                case "changeActivationState": Add("activationState", InputSchema.String(allowed: new[] { "Deactivate", "Activate", "DeactivateAndNotPresent" })); break;
                case "activateFunction": case "deactivateFunction": Add("functionKey", InputSchema.String(allowed: new[] { "BasicPositioner", "TechnologyController" })); break;
                case "setSIAxisType": Add("rotaryLinear", rotaryLinear); break;
                case "setMotorCode": Add("motorCode", InputSchema.Integer(0)); Add("motorDataSet", InputSchema.Integer(0, 65535), false); break;
                case "setSimoGearMlfb": Add("mlfb", InputSchema.String(minLength: 1)); break;
                case "setMotorType":
                    Add("motorType", InputSchema.String(allowed: new[] { "NoMotor", "InductionMotor", "SynchronousMotor", "NoCodeNumber1LE1InductionMotor", "NoCodeNumber1LG6InductionMotor",
                        "NoCodeNumber1xx1SIMOTICSFDInductionMotor", "NoCodeNumber1LA7InductionMotorNoCodeNumber", "MotorSeriesNumber1LA81PQ8StandardInduction",
                        "InductionMotor1LE1", "InductionMotor1PC1", "InductionMotor1PH4", "InductionMotor1LE5", "InductionMotor1PH7", "InductionMotor1PH8",
                        "NoEncoder1FG1GearedSynchronousMotor", "NoEncoder1FK7SynchronousMotor", "DriveCliqMotor", "DriveCliqMotorDataSet" }));
                    DataSet(); break;
                case "readMotorConfiguration": DataSet(); break;
                case "setEquivalentCircuitDiagramData": DataSet(); Add("equivalentCircuitDiagram", InputSchema.Boolean()); break;
                case "projectMotorConfiguration": DataSet(); Entries(); break;
                case "setEncoder":
                    Add("encoderInterface", InputSchema.String(allowed: new[] { "None", "Terminal", "DSub", "DriveCliQ", "HTL", "SSI" }));
                    Add("encoderType", InputSchema.String(allowed: new[] { "NoEncoder", "Resolver", "HTLTTL", "SSIProtocoll", "SinCos", "EnDat", "HTL", "SSIProtocollAndHTLTTL", "SSIProtocollAndSinCos", "DriveCliQ" }));
                    Add("absoluteIncremental", absoluteIncremental); Add("rotaryLinear", rotaryLinear); EncoderNumber(); break;
                case "readEncoderConfiguration": EncoderNumber(); break;
                case "projectEncoderConfiguration": EncoderNumber(); Entries(); break;
                case "setEncoderType": EncoderNumber(); Add("rotaryLinear", rotaryLinear); Add("absoluteIncremental", absoluteIncremental, false); break;
                default: throw new ArgumentException("Unknown drive function action.");
            }
            return new NativeValuePolicy(InputSchema.Object(fields, required.ToArray()), new InputBudget(), value =>
            {
                foreach (var property in value.Json.EnumerateObject())
                {
                    if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString())) return false;
                    if (property.Name == "entries") foreach (var entry in property.Value.EnumerateObject()) if (string.IsNullOrWhiteSpace(entry.Name)) return false;
                }
                return true;
            });
        }
    }
}
