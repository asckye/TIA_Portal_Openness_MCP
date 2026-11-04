using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Mock
{
    public sealed partial class MockTiaSession
    {

        // ---- fixture -------------------------------------------------------

        private void BuildFixture()
        {
            _devices.Clear();

            var plc = new MockDevice
            {
                Info = new DeviceInfo
                {
                    Id = "PLC_1",
                    Name = "PLC_1",
                    TypeIdentifier = "System:Device.S71500",
                    ArticleNumber = "6ES7 516-3FN02-0AB0",
                    FirmwareVersion = "V3.1",
                    Category = "Plc",
                    DisplayName = "PLC_1",
                    TypeName = "CPU 1516F-3 PN/DP",
                    GroupPath = string.Empty,
                    ItemNames = new List<string> { "PLC_1", "Rack_0" },
                },
            };

            plc.Blocks.AddRange(new[]
            {
                Block("Main", "OB", BlockKind.OB, 1, "LAD", true, "M.Chen"),
                Block("Motion/FB_Axis", "FB", BlockKind.FB, 100, "SCL", true, "M.Chen"),
                Block("Motion/FB_Axis_DB", "InstanceDB", BlockKind.InstanceDB, 900, null, true, "M.Chen"),
                Block("Motion/FC_Jog", "FC", BlockKind.FC, 101, "SCL", true, null),
                Block("Safety/FC_EStop", "FC", BlockKind.FC, 110, "LAD", true, "S.Wang"),
                Block("Recipe/DB_Recipe", "DB", BlockKind.DB, 500, null, true, "S.Wang"),
                Block("Types/UDT_Axis", "UDT", BlockKind.UDT, null, null, true, "M.Chen"),
                Block("Legacy/FC_Old_Unused", "FC", BlockKind.FC, 199, "LAD", true, null),
                Block("Draft/FC_Broken", "FC", BlockKind.FC, 200, "SCL", false, null),
                ProtectedBlock("Vendor/FB_Locked", 300),
            });

            plc.TagTables["Default tag table"] = new List<TagInfo>
            {
                Tag("StartButton", "Bool", "%I0.0", "Cycle start", "Default tag table"),
                Tag("StopButton", "Bool", "%I0.1", "Cycle stop", "Default tag table"),
                Tag("MotorRun", "Bool", "%Q0.0", "Motor contactor", "Default tag table"),
            };
            plc.TagTables["Axis"] = new List<TagInfo>
            {
                Tag("Axis_Position", "Real", "%ID100", "Encoder position", "Axis"),
                Tag("Axis_Speed", "Real", "%ID104", "Actual speed", "Axis"),
                Tag("Axis_Fault", "Bool", "%I2.0", null, "Axis"),
            };

            var hmi = new MockDevice
            {
                Info = new DeviceInfo
                {
                    Id = "HMI_1",
                    Name = "HMI_1",
                    TypeIdentifier = "System:Device.HmiUnified",
                    ArticleNumber = "6AV2 128-3GB36-0AX0",
                    FirmwareVersion = "V21",
                    Category = "Hmi",
                    DisplayName = "HMI_1",
                    TypeName = "TP1500 Comfort",
                    GroupPath = "Line 1",
                    ItemNames = new List<string> { "HMI_1" },
                },
            };

            // A panel has screens where a PLC has blocks; the mock carries both so the tree and the
            // export path are exercised for an HMI device too.
            hmi.Blocks.AddRange(new[]
            {
                Screen("Screens/Start"),
                Screen("Screens/Line/Overview"),
                Screen("Screens/Line/Alarms"),
                Screen("Templates/Header"),
            });

            hmi.TagTables["HMI tags"] = new List<TagInfo>
            {
                Tag("Motor_Run", "Bool", null, "From PLC_1", "HMI tags"),
                Tag("Setpoint", "Real", null, null, "HMI tags"),
            };

            _devices.Add(plc);
            _devices.Add(hmi);
        }

        private static BlockInfo Block(string path, string _, BlockKind kind, int? number,
            string language, bool consistent, string author)
        {
            return new BlockInfo
            {
                Path = path,
                FolderPath = path.LastIndexOf('/') < 0 ? string.Empty : path.Substring(0, path.LastIndexOf('/')),
                Name = path.Substring(path.LastIndexOf('/') + 1),
                Kind = kind,
                Number = number,
                ProgrammingLanguage = language,
                IsConsistent = consistent,
                IsKnowHowProtected = false,
                ModifiedDate = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
                HeaderAuthor = author,
                HeaderVersion = "0.1",
            };
        }

        /// <summary>An HMI screen: no number, no language, nothing to compile.</summary>
        private static BlockInfo Screen(string path)
        {
            return new BlockInfo
            {
                Path = path,
                FolderPath = path.LastIndexOf('/') < 0 ? string.Empty : path.Substring(0, path.LastIndexOf('/')),
                Name = path.Substring(path.LastIndexOf('/') + 1),
                Kind = BlockKind.HmiScreen,
                IsConsistent = true,
                ModifiedDate = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            };
        }

        private static TagInfo Tag(string name, string type, string address, string comment, string table)
        {
            return new TagInfo
            {
                Name = name,
                DataType = type,
                LogicalAddress = address,
                Comment = comment,
                TableName = table,
            };
        }
    }
}
