using System;
using Microsoft.Win32;

namespace RimePPT.Core
{
    /// <summary>开机自启：HKCU Run 键（无需管理员权限）。</summary>
    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RimePPT";

        public static bool GetRunAtStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetRunAtStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key is null)
                {
                    return;
                }

                if (enable)
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        key.SetValue(ValueName, $"\"{exe}\"");
                    }
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // 注册表操作失败不影响其他功能
            }
        }
    }
}
