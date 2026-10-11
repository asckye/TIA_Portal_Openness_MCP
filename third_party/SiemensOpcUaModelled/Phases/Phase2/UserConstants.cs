// Adapted from Siemens OPC UA modelled interface, commit 317dfd06. See UPSTREAM.json and LICENSE.md.
#nullable disable
using System;
using Native = TiaMcp.Adapters.Native.Plc.PlcOpcUaModelAccess;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Siemens.Engineering.SW.Tags;
using System.Xml.Linq;

namespace AddInOpcUaInterface.Phases
{
    internal static class UserConstants
    {
        // Dictionary to map the name of a user constant with its value
        public static Dictionary<string, int> UserConstantValues = new Dictionary<string, int>();

        /// <summary>
        /// Stores all user constants in a dictionary with their respective values.
        /// Note: User constants are defined inside each tag table of the project.
        /// </summary>
        /// <param name="tagTableGroup"></param>
        public static void GetUserConstants(PlcTagTableGroup tagTableGroup)
        {
            // Start empty before entering the loop
            CollectConstants(tagTableGroup, true);
        }

        private static void CollectConstants(PlcTagTableGroup tagTableGroup, bool reset)
        {
            if (reset) UserConstantValues.Clear();

            foreach (PlcTagTable table in Native.Tables(tagTableGroup))
            {
                foreach (PlcUserConstant userConstant in Native.Constants(table))
                {
                    string userConstantName = Native.Name(userConstant);
                    // Try to parse the string into an int
                    if (int.TryParse(Native.Value(userConstant), out int value))
                    {
                        // Parsing was successful
                        UserConstantValues.Add(userConstantName, value);
                    }
                }
            }
            foreach (PlcTagTableGroup tableGroup in Native.Groups(tagTableGroup))
            {
                CollectConstants(tableGroup, false);
            }
        }
    }
}
