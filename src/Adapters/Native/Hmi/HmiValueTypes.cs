using System;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Hmi
{
    public static class HmiValueTypes
    {
        public static object? Render(object value)
        {
#if !HMI_GRAPHICS_PROVIDER
            return null;
#else
            switch (value)
            {
                case global::Siemens.Engineering.Hmi.ConstValue constant:
                    return new HmiObject { ["valueClass"] = "ConstValue", ["value"] = HmiScalar.Json(constant.Value) };
                case global::Siemens.Engineering.Hmi.NullableDateTime dateTime:
                    return new HmiObject
                    {
                        ["valueClass"] = "NullableDateTime", ["granularity"] = dateTime.DateTimeValues.ToString(),
                        ["year"] = dateTime.Year, ["month"] = dateTime.Month, ["day"] = dateTime.Day, ["hour"] = dateTime.Hour, ["minute"] = dateTime.Minute, ["second"] = dateTime.Second,
                        ["text"] = dateTime.ToString()
                    };
                default: return null;
            }
#endif
        }
    }
}
