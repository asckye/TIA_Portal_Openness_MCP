using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OnlineToolPolicy
    {
        internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy()
        {
            return new[]
            {
                "在线监视只允许读取变量当前状态/当前值。",
                "在线模式不允许修改监控表、监视表或表内对象。",
                "不允许通过 MCP 暴露、调用或绕过任何强制表/强制相关操作。",
                "通用反射入口必须拦截强制相关服务，并拦截在线/监视/监控表面的写入、创建、删除、下载、启停和上下线切换动作。",
                "新增监视能力必须先探测 API 形状，再用最小实例读回验证；未验证前只能标记为探测能力。"
            };
        }

        internal static bool IsOnlineModeError(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? string.Empty;
                if (m.IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.IndexOf("online", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        internal static T WithAutoOffline<T>(Func<T> op)
        {
            try { return op(); }
            catch (Exception ex) when (IsOnlineModeError(ex))
            {
                try { ((OnlineDownloadService)EngineServices.Get(typeof(OnlineDownloadService))).GoOfflineAll(); } catch { /* swallow(native-fallback): preserve the one retry so the original operation reports its own failure */ }
                return op(); // retry once, now fully offline
            }
        }
    }
}
