using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbodiedNPC.Settings
{
    /// <summary>Exit intent only. The existing Portable supervisor owns all process operations.</summary>
    public static class PortableAIConfigRestart
    {
        public const int ExitCode = 73;
        private static bool requested;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => requested = false;

        public static string UnavailableReason
        {
            get
            {
                if (Application.isEditor) return SettingsUiLanguage.T(
                    "Unity Editor 不会自动重开；请结束 Play 后手动重启测试 Backend。",
                    "Unity Editor cannot reopen automatically. End Play and restart the test Backend manually.");
                if (Application.platform != RuntimePlatform.WindowsPlayer || SceneManager.GetActiveScene().name != "TitleScene")
                    return SettingsUiLanguage.T("请在主菜单应用；游戏中不会自动退出。", "Apply from Title; gameplay will not exit automatically.");
                if (Environment.GetEnvironmentVariable("EMBODIEDNPC_UNIFIED_RESTART") == "0")
                    return SettingsUiLanguage.T("本次启动已重启一次。请正常退出后使用 Start.cmd 再次启动。",
                        "This launch has already restarted once. Exit normally and launch Start.cmd again.");
                if (!SupervisorAlive()) return SettingsUiLanguage.T(
                    "未检测到支持重启的统一启动入口。请正常退出后使用 Start.cmd 启动。",
                    "No supported unified launcher detected. Exit normally and launch with Start.cmd.");
                return null;
            }
        }

        private static bool SupervisorAlive()
        {
            if (Environment.GetEnvironmentVariable("EMBODIEDNPC_UNIFIED_RESTART") != "1" ||
                !int.TryParse(Environment.GetEnvironmentVariable("EMBODIEDNPC_UNIFIED_PID"), out int pid) ||
                !long.TryParse(Environment.GetEnvironmentVariable("EMBODIEDNPC_UNIFIED_CREATED"), out long created)) return false;
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == created;
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException ||
                error is System.ComponentModel.Win32Exception || error is NotSupportedException) { return false; }
        }

        public static bool Request()
        {
            if (requested || UnavailableReason != null) return false;
            requested = true;
            Application.Quit(ExitCode);
            return true;
        }
    }
}
